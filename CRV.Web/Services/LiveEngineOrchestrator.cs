using System.Threading.Channels;
using CRV.Backtest.DataLoaders;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Live;
using CRV.Live.Brokers;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;

namespace CRV.Web.Services;

/// <summary>
/// Singleton service that owns the live engine lifecycle.
/// Dashboard and Settings pages call Start/Stop methods here.
/// Rebuilds the engine when config changes.
/// </summary>
public class LiveEngineOrchestrator : BackgroundService
{
    private readonly IServiceProvider  _sp;
    private readonly IConfiguration    _config;
    private readonly ILogger           _log;

    private readonly object            _lifecycleLock = new();
    private CancellationTokenSource?   _cts;
    private Task?                      _engineTask;
    private EngineSnapshot?            _lastSnapshot;
    private OrbStrategyEngine?         _engine;   // non-null while engine is running

    public bool     IsRunning    { get; private set; }
    public string   Status       { get; private set; } = "Stopped";
    public EngineSnapshot? LastSnapshot => _lastSnapshot;

    /// <summary>Request an immediate market exit for the active Setup A trade (applied on next bar).</summary>
    public void ForceExitSetupA()
    {
        OrbStrategyEngine? eng;
        lock (_lifecycleLock) { eng = _engine; }
        eng?.RequestForceExitA();
    }

    /// <summary>Request an immediate market exit for the active Setup B trade (applied on next bar).</summary>
    public void ForceExitSetupB()
    {
        OrbStrategyEngine? eng;
        lock (_lifecycleLock) { eng = _engine; }
        eng?.RequestForceExitB();
    }

    public LiveEngineOrchestrator(IServiceProvider sp, IConfiguration config, ILogger<LiveEngineOrchestrator> log)
    {
        _sp     = sp;
        _config = config;
        _log    = log;
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        // Service is started on-demand via StartAsync()
        ct.Register(() => StopEngine());
        return Task.CompletedTask;
    }

    public Task StartAsync(StrategyConfig cfg)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                _log.LogWarning("Engine already running — stop it first.");
                return Task.CompletedTask;
            }

            _log.LogInformation("Starting live engine for {Ticker}", cfg.Ticker);
            Status    = "Starting...";
            _cts      = new CancellationTokenSource();
            IsRunning = true;

            _engineTask = Task.Run(() => RunEngineAsync(cfg, _cts.Token));
            Status = "Live";
        }
        return Task.CompletedTask;
    }

    public void StopEngine()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            IsRunning = false;
            Status    = "Stopped";
            _log.LogInformation("Live engine stopped.");
        }
    }

    private async Task RunEngineAsync(StrategyConfig cfg, CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var prices = scope.ServiceProvider.GetRequiredService<ILastPriceProvider>();
            var sink   = scope.ServiceProvider.GetRequiredService<IStrategyEventSink>();
            var hub    = scope.ServiceProvider.GetRequiredService<IHubContext<TradingHub>>();

            // Clone config and inject AccountId from appsettings for the DATA broker
            cfg = cfg.Clone();
            cfg.AccountId = cfg.Broker switch
            {
                "TradeStation" => _config["TradeStation:AccountId"] ?? cfg.AccountId,
                "Schwab"       => _config["Schwab:AccountId"]       ?? cfg.AccountId,
                "Tradovate"    => _config["Tradovate:AccountId"]    ?? cfg.AccountId,
                _              => cfg.AccountId
            };

            // Use ExecAccountId from appsettings for the EXEC broker (if different)
            var execBroker = cfg.EffectiveExecBroker;
            var execAccountId = execBroker switch
            {
                "TradeStation" => _config["TradeStation:AccountId"] ?? cfg.AccountId,
                "Schwab"       => _config["Schwab:AccountId"]       ?? cfg.AccountId,
                "Tradovate"    => _config["Tradovate:AccountId"]    ?? cfg.AccountId,
                _              => cfg.AccountId
            };
            if (!string.IsNullOrWhiteSpace(cfg.ExecAccountId))
                execAccountId = cfg.ExecAccountId;

            // Convert ticker to the format expected by the DATA broker
            cfg.Ticker = FuturesSymbol.ForBroker(cfg.Ticker, cfg.Broker);

            // When exec broker is Mock, wrap the sink to tag all completed trades with Source="mock"
            IStrategyEventSink activeSink = execBroker == "Mock"
                ? new SourceOverrideSink(sink, "mock")
                : sink;

            var broadcast = scope.ServiceProvider.GetRequiredService<SnapshotBroadcastService>();
            var wrappedSink = new SnapshotCachingSink(activeSink, snap =>
            {
                _lastSnapshot = snap;
                broadcast.Publish(snap);
            });

            // ── Order executor — selected by EffectiveExecBroker ────
            IOrderExecutor executor;
            if (execBroker == "Mock")
            {
                executor = scope.ServiceProvider.GetRequiredService<MockBrokerExecutor>();
            }
            else
            {
                try
                {
                    var execCfg = cfg.Clone();
                    execCfg.AccountId = execAccountId;

                    var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                    executor = execBroker switch
                    {
                        "TradeStation" => new TradeStationExecutor(
                            scope.ServiceProvider.GetRequiredService<TradeStationAuthService>(),
                            execCfg,
                            scope.ServiceProvider.GetRequiredService<ILogger<TradeStationExecutor>>(),
                            httpFactory),
                        "Tradovate" => new TradovateExecutor(
                            scope.ServiceProvider.GetRequiredService<TradovateAuthService>(),
                            execCfg,
                            scope.ServiceProvider.GetRequiredService<ILogger<TradovateExecutor>>(),
                            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                            httpFactory),
                        _ => (IOrderExecutor) new SchwabExecutor(
                            scope.ServiceProvider.GetRequiredService<SchwabAuthService>(),
                            execCfg,
                            scope.ServiceProvider.GetRequiredService<ILogger<SchwabExecutor>>(),
                            httpFactory),
                    };
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "ExecBroker {Broker} unavailable — falling back to MockBrokerExecutor.", execBroker);
                    await hub.Clients.All.SendAsync("BrokerFallback",
                        $"Warning: {execBroker} exec broker unavailable — running in mock mode (no live orders).", ct);
                    executor = scope.ServiceProvider.GetRequiredService<MockBrokerExecutor>();
                }
            }

            // ── Bar feed — selected by cfg.Broker (data source) ─────
            if (cfg.Broker == "Mock")
            {
                Status    = "Error: Mock cannot be the data broker — select Schwab, TradeStation, or Tradovate";
                IsRunning = false;
                await hub.Clients.All.SendAsync("EngineStatusChanged", Status, ct);
                return;
            }

            IBarFeed feed;
            try
            {
                var feedHttpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                feed = cfg.Broker switch
                {
                    "TradeStation" => (IBarFeed) new TradeStationBarFeed(
                        scope.ServiceProvider.GetRequiredService<TradeStationAuthService>(),
                        cfg, prices,
                        scope.ServiceProvider.GetRequiredService<ILogger<TradeStationBarFeed>>(),
                        feedHttpFactory),
                    "Tradovate" => new TradovateBarFeed(
                        scope.ServiceProvider.GetRequiredService<TradovateAuthService>(),
                        cfg, prices,
                        scope.ServiceProvider.GetRequiredService<ILogger<TradovateBarFeed>>()),
                    _ => new SchwabBarFeed(
                        scope.ServiceProvider.GetRequiredService<SchwabAuthService>(),
                        cfg, prices,
                        scope.ServiceProvider.GetRequiredService<ILogger<SchwabBarFeed>>()),
                };
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Bar feed for broker {Broker} could not be created.", cfg.Broker);
                Status    = $"Error: bar feed unavailable for {cfg.Broker}";
                IsRunning = false;
                await hub.Clients.All.SendAsync("EngineStatusChanged", Status, ct);
                return;
            }

            _log.LogInformation("Engine starting — Feed: {Feed}, Executor: {Exec}, Account: {Acct}",
                cfg.Broker, execBroker, execAccountId);

            // ── Run ─────────────────────────────────────────────────
            var newEngine = new OrbStrategyEngine(cfg, executor, wrappedSink, prices,
                scope.ServiceProvider.GetRequiredService<ILogger<OrbStrategyEngine>>());
            lock (_lifecycleLock) { _engine = newEngine; }

            // Serialize bar processing and tick evaluation — they share all engine state
            var engineLock = new SemaphoreSlim(1, 1);

            // Enable tick-based entry/exit and wire L1 ticks → ProcessPriceTickAsync
            // Use a bounded channel so ticks queue (up to 64 deep) instead of dropping.
            // Only the latest tick matters for price eval, so BoundedChannelFullMode.DropOldest
            // keeps the queue from growing unbounded while preserving the freshest price.
            _engine.EnableTickMode();
            var tickCh = Channel.CreateBounded<(decimal price, DateTime time)>(
                new BoundedChannelOptions(64)
                {
                    FullMode     = BoundedChannelFullMode.DropOldest,
                    SingleWriter = false,
                    SingleReader = true
                });
            long _ticksDropped = 0;

            feed.OnPriceTick += (price, time) =>
            {
                if (!tickCh.Writer.TryWrite((price, time)))
                    Interlocked.Increment(ref _ticksDropped);
            };

            // Single consumer drains the tick channel sequentially — no Task.Run per tick
            var tickTask = Task.Run(async () =>
            {
                await foreach (var (price, time) in tickCh.Reader.ReadAllAsync(ct))
                {
                    OrbStrategyEngine? engine;
                    lock (_lifecycleLock) { engine = _engine; }
                    if (engine == null) break;
                    await engineLock.WaitAsync(ct);
                    try   { await engine.ProcessPriceTickAsync(price, time); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _log.LogWarning(ex, "[tick] ProcessPriceTickAsync failed @ {P}", price); }
                    finally { engineLock.Release(); }
                }
            }, ct);

            // If the executor is MockBrokerExecutor, evaluate fills on every L1 tick
            // (MockBrokerExecutor is internally thread-safe — no engineLock needed here)
            if (executor is MockBrokerExecutor mockExec)
            {
                feed.OnPriceTick += (price, time) =>
                    mockExec.EvaluateFills(price, time);
            }

            await hub.Clients.All.SendAsync("EngineStatusChanged", "Live");

            // Schwab CHART_FUTURES has no barsback replay — we must REST-backfill historical
            // bars so ORB/ATR/VWAP are warm before the streaming loop starts.
            // TradeStation handles this via barsback=20 in its stream URL.
            if (cfg.Broker == "Schwab")
            {
                var n = await BackfillAsync(cfg, scope, _engine, hub, ct);
                _log.LogInformation("Schwab backfill complete: {Count} bars loaded for warmup.", n);
                // Publish initial snapshot immediately so dashboard shows warmed-up values
                if (n > 0) await _engine.PublishCurrentStateAsync();
            }

            // Any confirmed bar whose open-time is before this cutoff arrived via the stream's
            // built-in history replay (TS barsback=20) and must be treated as warmup — builds
            // ORB/ATR/VWAP without firing orders or consuming the trade-count budget.
            // Bars at or after the cutoff are live and run the full strategy.
            var warmupCutoffUtc = DateTime.UtcNow.AddMinutes(-cfg.ExecutionTFMinutes);

            await foreach (var bar in feed.StreamAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                await engineLock.WaitAsync(ct);
                try
                {
                    if (bar.IsConfirmed && bar.Time < warmupCutoffUtc)
                        await _engine.WarmupBarAsync(bar, ct);
                    else
                        await _engine.ProcessBarAsync(bar, ct);
                }
                finally { engineLock.Release(); }
            }
            lock (_lifecycleLock) { _engine = null; }
            tickCh.Writer.TryComplete();
            try { await tickTask; } catch { /* already cancelled */ }
            var dropped = Interlocked.Read(ref _ticksDropped);
            if (dropped > 0)
                _log.LogWarning("Tick channel dropped {Count} ticks (channel full) during session.", dropped);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Engine task cancelled cleanly.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Engine task faulted.");
            Status    = $"Error: {ex.Message}";
            IsRunning = false;

            using var scope = _sp.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<TradingHub>>();
            await hub.Clients.All.SendAsync("EngineStatusChanged", $"Error: {ex.Message}");
        }
        finally
        {
            lock (_lifecycleLock) { _engine = null; }
        }
    }

    /// <summary>
    /// Fetches today's closed bars from the broker REST API and feeds them to the engine
    /// so the ORB and ATR are fully warm when the engine starts mid-session.
    /// Returns the number of bars fed, or 0 if backfill was skipped / failed.
    /// </summary>
    private async Task<int> BackfillAsync(StrategyConfig cfg, IServiceScope scope,
        OrbStrategyEngine engine, IHubContext<TradingHub> hub, CancellationToken ct)
    {
        try
        {
            var etTz  = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
            var nowUtc = DateTime.UtcNow;
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, etTz);
            var today  = nowEt.Date;

            var orbStartEt = today.Add(cfg.OrbStart.ToTimeSpan());

            // Nothing to backfill if we start before the ORB window
            if (nowEt <= orbStartEt)
            {
                _log.LogInformation("Engine started before ORB window — no backfill needed.");
                return 0;
            }

            // Reach back far enough for ATR(14) warmup + ORB window coverage.
            // NQ futures trade ~23h/day so pre-RTH bars (e.g. 6 AM ET) are typically available.
            // If the API returns fewer bars than requested, ATR simply won't be ready yet
            // (non-fatal — the engine skips the ATR filter when !IsReady).
            var fromEt = orbStartEt.AddMinutes(-cfg.ExecutionTFMinutes * 20);
            // Stop 1 full bar before now so backfill doesn't overlap the live stream
            var toEt   = nowEt.AddMinutes(-cfg.ExecutionTFMinutes);
            if (toEt <= fromEt) return 0;

            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(fromEt, DateTimeKind.Unspecified), etTz);
            var toUtc   = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(toEt, DateTimeKind.Unspecified), etTz);

            _log.LogInformation(
                "Backfilling {Ticker} {From:HH:mm}–{To:HH:mm} ET ({Tf}-min bars)…",
                cfg.Ticker, fromEt, toEt, cfg.ExecutionTFMinutes);

            await hub.Clients.All.SendAsync("EngineStatusChanged", "Backfilling…", ct);

            var lf = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var hf = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            IAsyncEnumerable<Bar> bars;

            if (cfg.Broker == "TradeStation")
            {
                var auth   = scope.ServiceProvider.GetRequiredService<TradeStationAuthService>();
                var token  = await auth.GetAccessTokenAsync();
                var loader = new TradeStationHistoricalLoader(
                    token, lf.CreateLogger<TradeStationHistoricalLoader>(), auth.ApiBaseUrl, hf);
                bars = loader.LoadAsync(cfg.Ticker, cfg.ExecutionTFMinutes, fromUtc, toUtc, ct);
            }
            else if (cfg.Broker == "Schwab")
            {
                var auth   = scope.ServiceProvider.GetRequiredService<SchwabAuthService>();
                var token  = await auth.GetAccessTokenAsync();
                var loader = new SchwabHistoricalLoader(
                    token, lf.CreateLogger<SchwabHistoricalLoader>(), auth.ApiBaseUrl, hf);
                bars = loader.LoadAsync(cfg.Ticker, cfg.ExecutionTFMinutes, fromUtc, toUtc, ct);
            }
            // Tradovate historical loader: add in Task 14
            else if (cfg.Broker == "Tradovate") return 0; // warmup via stream's built-in history
            else return 0;

            int count = 0;
            await foreach (var bar in bars.WithCancellation(ct))
            {
                // WarmupBarAsync builds ORB/ATR/VWAP without running strategy logic,
                // so historical bars do not fire orders or consume the trade-count budget.
                await engine.WarmupBarAsync(bar, ct);
                count++;
            }
            return count;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Backfill failed — engine will start without historical data.");
            return 0;
        }
    }
}

/// <summary>
/// Wraps IStrategyEventSink to override the Source field on all completed trades.
/// Used to tag mock-executor trades as Source="mock" without changing the engine.
/// </summary>
internal class SourceOverrideSink : IStrategyEventSink
{
    private readonly IStrategyEventSink _inner;
    private readonly string             _source;

    public SourceOverrideSink(IStrategyEventSink inner, string source)
    {
        _inner  = inner;
        _source = source;
    }

    public Task OnEntryAsync(EntrySignal s)              => _inner.OnEntryAsync(s);
    public Task OnPartialAsync(PartialSignal s)          => _inner.OnPartialAsync(s);
    public Task OnBEMoveAsync(BESignal s)                => _inner.OnBEMoveAsync(s);
    public Task OnSnapshotAsync(EngineSnapshot snap)     => _inner.OnSnapshotAsync(snap);

    public Task OnExitAsync(ExitSignal s, TradeRecord t)
    {
        t.Source = _source; // override before persistence
        return _inner.OnExitAsync(s, t);
    }
}

/// <summary>Wraps IStrategyEventSink to intercept snapshots for local caching.</summary>
internal class SnapshotCachingSink : IStrategyEventSink
{
    private readonly IStrategyEventSink    _inner;
    private readonly Action<EngineSnapshot> _onSnapshot;

    public SnapshotCachingSink(IStrategyEventSink inner, Action<EngineSnapshot> onSnapshot)
    {
        _inner      = inner;
        _onSnapshot = onSnapshot;
    }

    public Task OnEntryAsync(EntrySignal s)              => _inner.OnEntryAsync(s);
    public Task OnPartialAsync(PartialSignal s)          => _inner.OnPartialAsync(s);
    public Task OnBEMoveAsync(BESignal s)                => _inner.OnBEMoveAsync(s);
    public Task OnExitAsync(ExitSignal s, TradeRecord t) => _inner.OnExitAsync(s, t);

    public async Task OnSnapshotAsync(EngineSnapshot snap)
    {
        _onSnapshot(snap);
        await _inner.OnSnapshotAsync(snap);
    }
}
