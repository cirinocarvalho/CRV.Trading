using System.Threading.Channels;
using CRV.Backtest.DataLoaders;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Live;
using CRV.Live.BarBuilders;
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
    private ComposableEngine?          _engine;   // non-null while engine is running

    public bool     IsRunning    { get; private set; }
    public string   Status       { get; private set; } = "Stopped";
    public EngineSnapshot? LastSnapshot => _lastSnapshot;

    /// <summary>Get bar history for a specific ticker group (for dashboard chart).</summary>
    public List<CRV.Core.Models.Bar> GetBarHistory(string groupKey)
    {
        ComposableEngine? eng;
        lock (_lifecycleLock) { eng = _engine; }
        return eng?.GetBarHistory(groupKey) ?? new List<CRV.Core.Models.Bar>();
    }

    /// <summary>Get bar history with VWAP for a specific ticker group.</summary>
    public List<(CRV.Core.Models.Bar Bar, decimal Vwap)> GetBarHistoryWithVwap(string groupKey)
    {
        ComposableEngine? eng;
        lock (_lifecycleLock) { eng = _engine; }
        return eng?.GetBarHistoryWithVwap(groupKey) ?? new List<(CRV.Core.Models.Bar, decimal)>();
    }

    /// <summary>Force-exit a specific setup immediately at current market price.</summary>
    public async Task ForceExitSetup(string setupId)
    {
        ComposableEngine? eng;
        lock (_lifecycleLock) { eng = _engine; }
        if (eng != null) await eng.ForceExitSetupAsync(setupId);
    }

    /// <summary>
    /// Force-set the ORB by fetching historical bars from the broker and scanning the configured window.
    /// Called from the dashboard "Force ORB" button when the ORB didn't form correctly after restart.
    /// </summary>
    public async Task<(bool ok, string message)> ForceOrbAsync(StrategyConfig cfg)
    {
        ComposableEngine? eng;
        lock (_lifecycleLock) { eng = _engine; }
        if (eng == null) return (false, "Engine not running");
        try
        {
            using var scope = _sp.CreateScope();
            var tz = TimeZoneInfo.FindSystemTimeZoneById(cfg.Timezone);
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);

            // Use active session's ORB window
            var orbStart = cfg.OrbStart;
            var orbEnd = cfg.OrbEnd;
            {
                var sessions = cfg.Sessions ?? SessionConfig.CreateDefaults(cfg);
                var sessionMgr = new SessionManager(sessions);
                var activeSession = sessionMgr.GetActiveSession(TimeOnly.FromDateTime(nowLocal));
                if (activeSession != null)
                {
                    orbStart = activeSession.OrbStart;
                    orbEnd = activeSession.OrbEnd;
                    _log.LogInformation("ForceORB using session {Session} ORB window {Start}–{End}",
                        activeSession.SessionId, orbStart, orbEnd);
                }
            }

            // Reach back 20 TF bars before ORB start for ATR/VWAP warmup, up to now
            var orbStartLocal = nowLocal.Date.Add(orbStart.ToTimeSpan());
            if (orbStartLocal > nowLocal) orbStartLocal = orbStartLocal.AddDays(-1);
            var fromLocal = orbStartLocal.AddMinutes(-cfg.ExecutionTFMinutes * 20);
            var toLocal = nowLocal.AddMinutes(-cfg.ExecutionTFMinutes); // don't overlap live stream

            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(fromLocal, DateTimeKind.Unspecified), tz);
            var toUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(toLocal, DateTimeKind.Unspecified), tz);

            // Collect all distinct tickers from basket (or fallback to global)
            var setupConfigs = cfg.ToSetupConfigs();
            var allTickers = setupConfigs
                .Select(s => s.Ticker)
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => FuturesSymbol.ForBroker(t, cfg.Broker))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
            if (allTickers.Count == 0) allTickers.Add(FuturesSymbol.ForBroker(cfg.Ticker, cfg.Broker));

            var lf = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
            var hf = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var tradingDate = cfg.TradingDate(nowLocal);
            var nowTime = TimeOnly.FromDateTime(nowLocal);
            var results = new List<string>();

            foreach (var ticker in allTickers)
            {
                // Fetch bars from broker for this ticker
                IAsyncEnumerable<Bar> bars;
                if (cfg.Broker == "Schwab")
                {
                    var auth = scope.ServiceProvider.GetRequiredService<SchwabAuthService>();
                    var token = await auth.GetAccessTokenAsync();
                    var loader = new SchwabHistoricalLoader(token, lf.CreateLogger<SchwabHistoricalLoader>(), auth.ApiBaseUrl, hf);
                    bars = loader.LoadAsync(ticker, cfg.ExecutionTFMinutes, fromUtc, toUtc);
                }
                else if (cfg.Broker == "TradeStation")
                {
                    var auth = scope.ServiceProvider.GetRequiredService<TradeStationAuthService>();
                    var token = await auth.GetAccessTokenAsync();
                    var loader = new TradeStationHistoricalLoader(token, lf.CreateLogger<TradeStationHistoricalLoader>(), auth.ApiBaseUrl, hf);
                    bars = loader.LoadAsync(ticker, cfg.ExecutionTFMinutes, fromUtc, toUtc);
                }
                else
                {
                    return (false, $"ForceORB not supported for broker {cfg.Broker} — use Schwab or TradeStation");
                }

                // Resolve the raw ticker for engine routing (strip broker prefix)
                var rawTicker = setupConfigs
                    .Select(s => s.Ticker)
                    .FirstOrDefault(t => FuturesSymbol.ForBroker(t, cfg.Broker) == ticker) ?? cfg.Ticker;

                // Feed ALL bars as warmup — builds ATR, VWAP, and all indicators.
                decimal orbHigh = 0, orbLow = decimal.MaxValue;
                decimal lastClose = 0;
                int barCount = 0, orbBarCount = 0;
                await foreach (var bar in bars)
                {
                    await eng.WarmupBarAsync(bar, rawTicker);
                    barCount++;

                    var barLocal = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, tz);
                    var barTime = TimeOnly.FromDateTime(barLocal);
                    if (barTime >= orbStart && barTime < orbEnd)
                    {
                        if (bar.High > orbHigh) orbHigh = bar.High;
                        if (bar.Low < orbLow) orbLow = bar.Low;
                        lastClose = bar.Close;
                        orbBarCount++;
                    }
                }

                if (orbBarCount == 0 || orbHigh == 0 || orbLow == decimal.MaxValue)
                {
                    results.Add($"{ticker}: no ORB bars ({barCount} warmup)");
                    continue;
                }

                decimal range = orbHigh - orbLow;
                decimal closeRelPct = range > 0 ? (lastClose - orbLow) / range : 0.5m;

                var cache = new OrbStateCache
                {
                    TradingDate = tradingDate,
                    Symbol = rawTicker?.TrimStart('/') ?? "",
                    SessionId = eng.ActiveSessionId,
                    OrbHigh = orbHigh,
                    OrbLow = orbLow,
                    CloseRelPct = closeRelPct,
                    OrbAtrRatio = 0
                };

                if (nowTime >= orbStart && nowTime < orbEnd)
                {
                    eng.SeedOrbFloor(cache);
                    _log.LogInformation("ForceORB {Ticker} (floor — ORB open): H={H:F2} L={L:F2} R={R:F2}",
                        ticker, orbHigh, orbLow, range);
                }
                else
                {
                    eng.RestoreOrb(cache);
                    _log.LogInformation("ForceORB {Ticker}: H={H:F2} L={L:F2} R={R:F2} ({OrbBars} ORB + {Total} warmup)",
                        ticker, orbHigh, orbLow, range, orbBarCount, barCount);
                }
                OrbStateCacheService.Save(cache);
                results.Add($"{ticker}: H={orbHigh:F2} L={orbLow:F2} R={range:F2} ({barCount} bars)");
            }

            await eng.PublishCurrentStateAsync();
            return (true, string.Join(" | ", results));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ForceORB failed");
            return (false, $"Error: {ex.Message}");
        }
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
                "Tradovate" or "TradovateReplay" => _config["Tradovate:AccountId"] ?? cfg.AccountId,
                _              => cfg.AccountId
            };

            // Use ExecAccountId from appsettings for the EXEC broker (if different)
            var execBroker = cfg.EffectiveExecBroker;
            var execAccountId = execBroker switch
            {
                "TradeStation" => _config["TradeStation:AccountId"] ?? cfg.AccountId,
                "Schwab"       => _config["Schwab:AccountId"]       ?? cfg.AccountId,
                "Tradovate" or "TradovateReplay" => _config["Tradovate:AccountId"] ?? cfg.AccountId,
                _              => cfg.AccountId
            };
            if (!string.IsNullOrWhiteSpace(cfg.ExecAccountId))
                execAccountId = cfg.ExecAccountId;

            // Convert primary ticker to the format expected by the DATA broker
            cfg.Ticker = FuturesSymbol.ForBroker(cfg.Ticker, cfg.Broker);

            // Compute distinct tickers across all enabled setups (broker-format)
            var setupConfigs = cfg.ToSetupConfigs().Where(s => s.Enabled).ToList();
            var distinctTickers = setupConfigs
                .Select(s => FuturesSymbol.ForBroker(s.Ticker, cfg.Broker))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
            if (distinctTickers.Count == 0) distinctTickers.Add(cfg.Ticker);

            // When exec broker is Mock, wrap the sink to tag all completed trades with Source="mock"
            IStrategyEventSink activeSink = execBroker == "Mock"
                ? new SourceOverrideSink(sink, "mock")
                : execBroker == "TradovateReplay"
                    ? (cfg.SaveReplayTrades
                        ? new SourceOverrideSink(sink, "replay")
                        : (IStrategyEventSink) new ReplayFilterSink(new SourceOverrideSink(sink, "replay"), _log))
                    : sink;

            var wrappedSink = new SnapshotCachingSink(activeSink, snap =>
            {
                _lastSnapshot = snap;
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
                        "TradovateReplay" => new TradovateExecutor(
                            CreateReplayAuthService(scope),
                            execCfg,
                            scope.ServiceProvider.GetRequiredService<ILogger<TradovateExecutor>>(),
                            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
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

            IMultiTickerBarFeed mux;
            try
            {
                if (distinctTickers.Count == 1)
                {
                    var feed = CreateBarFeed(scope, cfg, prices, distinctTickers[0]);
                    mux = MultiTickerFeedMux.Single(distinctTickers[0], feed);
                }
                else
                {
                    // Schwab and Tradovate support multi-symbol on a single WSS connection —
                    // use shared-connection feeds instead of separate IBarFeed per ticker.
                    mux = cfg.Broker switch
                    {
                        "Schwab" => new SchwabMultiTickerBarFeed(
                            scope.ServiceProvider.GetRequiredService<SchwabAuthService>(),
                            cfg, prices, distinctTickers,
                            scope.ServiceProvider.GetRequiredService<ILogger<SchwabMultiTickerBarFeed>>()),

                        "Tradovate" => new TradovateMultiTickerBarFeed(
                            scope.ServiceProvider.GetRequiredService<TradovateAuthService>(),
                            cfg, prices, distinctTickers,
                            scope.ServiceProvider.GetRequiredService<ILogger<TradovateMultiTickerBarFeed>>()),

                        "TradovateReplay" => CreateReplayMultiTickerBarFeed(scope, cfg, prices, distinctTickers),

                        // TradeStation uses per-symbol HTTP streams — cannot share connection
                        _ => CreateMultiTickerFeedMux(scope, cfg, prices, distinctTickers),
                    };
                    _log.LogInformation("Multi-ticker feed created ({Type}) for {Count} tickers: {Tickers}",
                        mux.GetType().Name, distinctTickers.Count, string.Join(", ", distinctTickers));
                }
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
            var engineConfig = cfg.ToEngineConfig();
            var newEngine = new ComposableEngine(executor, wrappedSink, prices, engineConfig);
            foreach (var setupCfg in cfg.ToSetupConfigs())
            {
                if (setupCfg.Enabled)
                    newEngine.AddSetup(setupCfg);
            }
            lock (_lifecycleLock) { _engine = newEngine; }

            // Seed module historical levels from daily bars (per ticker)
            foreach (var ticker in distinctTickers)
            {
                try
                {
                    var dailyBars = await mux.FetchDailyBarsAsync(ticker, 30, ct);
                    if (dailyBars.Count > 0)
                    {
                        _log.LogInformation("Seeded {Count} daily bars for {Ticker} module levels", dailyBars.Count, ticker);
                        newEngine.SeedModuleHistory(dailyBars);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Failed to fetch daily bars for {Ticker}: {Err}", ticker, ex.Message);
                }
            }

            // Multi-session support
            var sessions = cfg.Sessions ?? SessionConfig.CreateDefaults(cfg);
            var sessionMgr = new SessionManager(sessions);

            DateTime lastDailyReset = DateTime.MinValue;

            async Task CheckSessionTransitionAsync(DateTime barTimeUtc)
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(cfg.Timezone);
                var local = TimeZoneInfo.ConvertTimeFromUtc(barTimeUtc, tz);
                var localTime = TimeOnly.FromDateTime(local);

                // Daily reset at TradingDate boundary (before first session of the day)
                var tradingDate = cfg.TradingDate(local);
                if (tradingDate != lastDailyReset)
                {
                    lastDailyReset = tradingDate;
                    newEngine.ResetDaily();
                }

                var (transition, session) = sessionMgr.CheckTransition(localTime);
                switch (transition)
                {
                    case TransitionType.SessionStarted:
                        var flat = session!.ToLegacyConfig(cfg);
                        newEngine.Reconfigure(flat, session.SessionId);

                        // Restore ORB from cache for each ticker group after Reconfigure()+Reset().
                        // Skip for mock/replay — they replay from scratch, cached ORB would be stale.
                        // Past OrbEnd: restore fully (ORB won't change).
                        // Inside ORB window: restore as floor so warmup bars can only expand.
                        if (execBroker != "Mock" && execBroker != "TradovateReplay")
                        try
                        {
                            var td = cfg.TradingDate(local);
                            var sessionKey = session.SessionId.ToString();
                            foreach (var ticker in distinctTickers)
                            {
                                var cached = OrbStateCacheService.Load(ticker.TrimStart('/'), td, sessionKey);
                                if (cached == null) continue;
                                if (localTime >= flat.OrbEnd)
                                {
                                    newEngine.RestoreOrb(cached);
                                    _log.LogInformation("ORB restored from cache for {Ticker} session {Session} (past OrbEnd)",
                                        ticker, sessionKey);
                                }
                                else
                                {
                                    newEngine.SeedOrbFloor(cached);
                                    _log.LogInformation("ORB seeded from cache for {Ticker} session {Session} (inside ORB window)",
                                        ticker, sessionKey);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to restore ORB from cache for session {Session}", session.SessionId);
                        }

                        await newEngine.PublishCurrentStateAsync();
                        break;
                    case TransitionType.SessionEnded:
                        await newEngine.ForceExitAllAsync();
                        newEngine.SetIdle();
                        await newEngine.PublishCurrentStateAsync();
                        await hub.Clients.All.SendAsync("EngineStatusChanged", "Session Ended");
                        break;
                }
            }

            // Serialize bar processing and tick evaluation — they share all engine state
            var engineLock = new SemaphoreSlim(1, 1);

            // Enable tick-based entry/exit and wire L1 ticks → ProcessPriceTickAsync
            // Use a bounded channel so ticks queue (up to 64 deep) instead of dropping.
            // Only the latest tick matters for price eval, so BoundedChannelFullMode.DropOldest
            // keeps the queue from growing unbounded while preserving the freshest price.
            _engine.EnableTickMode();
            var tickCh = Channel.CreateBounded<(decimal price, DateTime time, string ticker)>(
                new BoundedChannelOptions(64)
                {
                    FullMode     = BoundedChannelFullMode.DropOldest,
                    SingleWriter = false,
                    SingleReader = true
                });
            long _ticksDropped = 0;

            mux.OnPriceTick += (price, time, ticker) =>
            {
                if (!tickCh.Writer.TryWrite((price, time, ticker)))
                    Interlocked.Increment(ref _ticksDropped);
            };

            // Single consumer drains the tick channel sequentially — no Task.Run per tick
            var tickTask = Task.Run(async () =>
            {
                await foreach (var (price, time, tickTicker) in tickCh.Reader.ReadAllAsync(ct))
                {
                    ComposableEngine? engine;
                    lock (_lifecycleLock) { engine = _engine; }
                    if (engine == null) break;
                    await engineLock.WaitAsync(ct);
                    try
                    {
                        // Use the later of tick time and wall-clock for session transition
                        // (ticks may queue in the channel and arrive slightly stale)
                        var tickTransitionTime = time > DateTime.UtcNow ? time : DateTime.UtcNow;
                        await CheckSessionTransitionAsync(tickTransitionTime);
                        await engine.ProcessPriceTickAsync(price, time, tickTicker);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _log.LogWarning(ex, "[tick] ProcessPriceTickAsync failed @ {P}", price); }
                    finally { engineLock.Release(); }
                }
            }, ct);

            // If the executor is MockBrokerExecutor, evaluate fills on every L1 tick
            // (MockBrokerExecutor is internally thread-safe — no engineLock needed here)
            if (executor is MockBrokerExecutor mockExec)
            {
                mux.OnPriceTick += (price, time, _) =>
                    mockExec.EvaluateFills(price, time);
            }

            await hub.Clients.All.SendAsync("EngineStatusChanged", "Live");

            // ── Pre-initialize session state ─────────────────────────────────
            // Surgically set up the engine's session context BEFORE backfill so:
            //   1. lastDailyReset is seeded (first streaming bar won't ResetDaily)
            //   2. If a session is active: Reconfigure + ORB cache restore happen now,
            //      so backfill bars build ORB into clean (post-reset) groups.
            SessionConfig? preInitSession = null; // saved for post-backfill ReseedSessionRange
            //   3. SessionManager._activeSession is seeded so the first streaming bar's
            //      CheckTransition returns None (no redundant reset).
            // We deliberately do NOT call CheckSessionTransitionAsync() here because
            // that can trigger SessionEnded → SetIdle() if the engine starts after hours.
            {
                var initTz = TimeZoneInfo.FindSystemTimeZoneById(cfg.Timezone);
                var initLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, initTz);
                var initLocalTime = TimeOnly.FromDateTime(initLocal);

                // Seed daily reset so first bar doesn't wipe state
                lastDailyReset = cfg.TradingDate(initLocal);
                newEngine.ResetDaily();

                // Seed SessionManager + engine with the currently active session (if any)
                var initSession = sessionMgr.GetActiveSession(initLocalTime);
                preInitSession = initSession;
                if (initSession != null)
                {
                    // Advance SessionManager internal state so CheckTransition returns None
                    sessionMgr.CheckTransition(initLocalTime);

                    var flat = initSession.ToLegacyConfig(cfg);
                    newEngine.Reconfigure(flat, initSession.SessionId);

                    // Restore ORB from cache for each ticker group
                    // Skip for mock/replay — they replay from scratch, cached ORB would be stale.
                    if (execBroker != "Mock" && execBroker != "TradovateReplay")
                    {
                        var td = cfg.TradingDate(initLocal);
                        var sessionKey = initSession.SessionId.ToString();
                        foreach (var ticker in distinctTickers)
                        {
                            var cached = OrbStateCacheService.Load(ticker.TrimStart('/'), td, sessionKey);
                            if (cached == null) continue;
                            if (initLocalTime >= flat.OrbEnd)
                            {
                                newEngine.RestoreOrb(cached);
                                _log.LogInformation("ORB restored from cache for {Ticker} session {Session} (past OrbEnd)",
                                    ticker.TrimStart('/'), sessionKey);
                            }
                            else
                            {
                                newEngine.SeedOrbFloor(cached);
                                _log.LogInformation("ORB seeded from cache for {Ticker} session {Session} (inside ORB window)",
                                    ticker.TrimStart('/'), sessionKey);
                            }
                        }
                    }

                    _log.LogInformation("Pre-initialized session {Session} (RTH {Start}–{End})",
                        initSession.SessionId, initSession.RthStart, initSession.RthEnd);
                }
                else
                {
                    // No active session right now.  Check if we're past ALL enabled
                    // sessions' RTH end today (i.e. the trading day is over).
                    // The old check used Any(), which incorrectly fired when Asia/London
                    // had ended but we were in the gap BETWEEN London end and NY start
                    // (e.g. 08:30–09:30 ET), setting the engine idle and wiping ORB state.
                    bool pastAllSessions = sessions.Where(s => s.Enabled).All(s => initLocalTime > s.RthEnd);
                    if (pastAllSessions && sessions.Any(s => s.Enabled))
                    {
                        newEngine.SetIdle();
                        await hub.Clients.All.SendAsync("EngineStatusChanged", "Session Ended");
                        _log.LogInformation("No active session at {Time} — past session end, engine idle",
                            initLocalTime);
                    }
                    else
                    {
                        _log.LogInformation("No active session at {Time} — engine will start when next session begins",
                            initLocalTime);
                    }
                }
            }

            // Schwab CHART_FUTURES has no barsback replay — we must REST-backfill historical
            // bars so ORB/ATR/VWAP are warm before the streaming loop starts.
            // TradeStation handles this via barsback=20 in its stream URL.
            if (cfg.Broker == "Schwab")
            {
                int totalBackfill = 0;
                foreach (var ticker in distinctTickers)
                {
                    var n = await BackfillAsync(cfg, scope, _engine, hub, ticker, ct);
                    totalBackfill += n;
                }
                _log.LogInformation("Schwab backfill complete: {Count} bars loaded for warmup.", totalBackfill);
                // Reset trade counters accumulated during backfill — warmup replays the
                // entire day (Asia→London→NY) so counters include prior-session trades.
                // This zeros them so only live trades count. Minor cosmetic issue: trades
                // that already happened in the current session during backfill show 0.
                if (totalBackfill > 0) newEngine.ResetWarmupCounters();
                // Re-seed session range reference after backfill. During pre-init,
                // OnSessionBoundary ran before backfill when session-engine levels
                // (London H/L, Asia H/L) were still 0. Now that backfill has populated
                // them, re-seed so SessionRangeTracker has a valid reference range.
                if (totalBackfill > 0 && preInitSession != null)
                    newEngine.ReseedSessionRange(preInitSession.SessionId);
                // Publish initial snapshot immediately so dashboard shows warmed-up values
                if (totalBackfill > 0) await _engine.PublishCurrentStateAsync();
            }

            // Any confirmed bar whose open-time is before this cutoff arrived via the stream's
            // built-in history replay (TS barsback=20) and must be treated as warmup — builds
            // ORB/ATR/VWAP without firing orders or consuming the trade-count budget.
            // Bars at or after the cutoff are live and run the full strategy.
            var warmupCutoffUtc = (cfg.Broker == "TradovateReplay" && cfg.ReplayDate.HasValue
                ? cfg.ReplayDate.Value.ToUniversalTime()
                : DateTime.UtcNow)
                .AddMinutes(-cfg.ExecutionTFMinutes);

            await foreach (var (bar, barTicker) in mux.StreamAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                await engineLock.WaitAsync(ct);
                try
                {
                    var isWarmup = bar.IsConfirmed && bar.Time < warmupCutoffUtc;
                    // For live bars use wall-clock time for session transition — bar.Time is the
                    // bar's OPEN timestamp which can be up to one bar-period stale (e.g. 08:25 for
                    // an 08:25-08:30 bar), causing entries after the session actually ended.
                    // For warmup bars use bar.Time so historical replay uses correct timestamps.
                    var transitionTime = isWarmup ? bar.Time : DateTime.UtcNow;
                    await CheckSessionTransitionAsync(transitionTime);
                    if (isWarmup)
                        await _engine.WarmupBarAsync(bar, barTicker, ct);
                    else
                        await _engine.ProcessBarAsync(bar, barTicker, ct);
                }
                finally { engineLock.Release(); }
            }
            lock (_lifecycleLock) { _engine = null; }
            tickCh.Writer.TryComplete();
            try { await tickTask; } catch { /* already cancelled */ }
            await mux.DisposeAsync();
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
    /// Creates a single <see cref="IBarFeed"/> for the given ticker by cloning the config
    /// and setting the Ticker property. Keeps feed constructors unchanged.
    /// </summary>
    private IBarFeed CreateBarFeed(IServiceScope scope, StrategyConfig cfg, ILastPriceProvider prices, string ticker)
    {
        var feedCfg = cfg.Clone();
        feedCfg.Ticker = ticker;

        return cfg.Broker switch
        {
            "TradeStation" => (IBarFeed) new TradeStationBarFeed(
                scope.ServiceProvider.GetRequiredService<TradeStationAuthService>(),
                feedCfg, prices,
                scope.ServiceProvider.GetRequiredService<ILogger<TradeStationBarFeed>>(),
                scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()),
            "TradovateReplay" => CreateReplayBarFeed(scope, feedCfg, prices),
            "Tradovate" => new TradovateBarFeed(
                scope.ServiceProvider.GetRequiredService<TradovateAuthService>(),
                feedCfg, prices,
                scope.ServiceProvider.GetRequiredService<ILogger<TradovateBarFeed>>()),
            _ => new SchwabBarFeed(
                scope.ServiceProvider.GetRequiredService<SchwabAuthService>(),
                feedCfg, prices,
                scope.ServiceProvider.GetRequiredService<ILogger<SchwabBarFeed>>()),
        };
    }

    /// <summary>
    /// Creates a <see cref="MultiTickerFeedMux"/> with separate <see cref="IBarFeed"/> per ticker.
    /// Used for brokers that cannot share a connection (e.g. TradeStation HTTP streams).
    /// </summary>
    private IMultiTickerBarFeed CreateMultiTickerFeedMux(
        IServiceScope scope, StrategyConfig cfg, ILastPriceProvider prices,
        IReadOnlyList<string> tickers)
    {
        var feeds = new Dictionary<string, IBarFeed>();
        foreach (var ticker in tickers)
            feeds[ticker] = CreateBarFeed(scope, cfg, prices, ticker);
        return new MultiTickerFeedMux(feeds);
    }

    /// <summary>
    /// Creates a <see cref="TradovateMultiTickerBarFeed"/> configured for replay mode
    /// with clock init and chart start override.
    /// </summary>
    private IMultiTickerBarFeed CreateReplayMultiTickerBarFeed(
        IServiceScope scope, StrategyConfig cfg, ILastPriceProvider prices,
        IReadOnlyList<string> tickers)
    {
        var replayAuth = CreateReplayAuthService(scope);
        var feed = new TradovateMultiTickerBarFeed(replayAuth, cfg, prices, tickers,
            scope.ServiceProvider.GetRequiredService<ILogger<TradovateMultiTickerBarFeed>>());

        var replayDate = cfg.ReplayDate ?? DateTime.UtcNow.AddDays(-1);
        feed.ChartStartOverride = replayDate.ToUniversalTime();
        feed.PostAuthAction = async (ws, ct) =>
        {
            var clockReq = System.Text.Json.JsonSerializer.Serialize(new
            {
                startTimestamp = replayDate.ToUniversalTime().ToString("O"),
                speed          = cfg.ReplaySpeed,
                initialBalance = cfg.ReplayBalance
            });
            var frame = $"replay/initializeclock\n2\n\n{clockReq}";
            await ws.SendAsync(
                System.Text.Encoding.UTF8.GetBytes(frame),
                System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
            _log.LogInformation("Replay clock initialized (multi-ticker): {Date} speed={Speed}% balance=${Bal}",
                replayDate, cfg.ReplaySpeed, cfg.ReplayBalance);
        };

        return feed;
    }

    /// <summary>
    /// Creates a TradovateAuthService configured for the replay endpoint.
    /// </summary>
    private TradovateAuthService CreateReplayAuthService(IServiceScope scope)
    {
        return new TradovateAuthService(
            _config["Tradovate:Username"] ?? "",
            _config["Tradovate:Password"] ?? "",
            int.TryParse(_config["Tradovate:Cid"], out var cid) ? cid : 0,
            _config["Tradovate:Secret"] ?? "",
            _config["Tradovate:DeviceId"] ?? "",
            _config["Tradovate:AppId"] ?? "CRVBot",
            Path.Combine(Path.GetTempPath(), "crv-tradovate-replay-tokens.json"),
            apiBaseUrl: "https://replay.tradovateapi.com/v1",
            mdWssUrl: "wss://replay.tradovateapi.com/v1/websocket",
            httpFactory: scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(),
            log: scope.ServiceProvider.GetRequiredService<ILogger<TradovateAuthService>>());
    }

    /// <summary>
    /// Creates a TradovateBarFeed configured for replay with clock init and chart start override.
    /// </summary>
    private IBarFeed CreateReplayBarFeed(IServiceScope scope, StrategyConfig cfg, ILastPriceProvider prices)
    {
        var replayAuth = CreateReplayAuthService(scope);
        var feed = new TradovateBarFeed(replayAuth, cfg, prices,
            scope.ServiceProvider.GetRequiredService<ILogger<TradovateBarFeed>>());

        var replayDate = cfg.ReplayDate ?? DateTime.UtcNow.AddDays(-1);
        feed.ChartStartOverride = replayDate.ToUniversalTime();
        feed.PostAuthAction = async (ws, ct) =>
        {
            var clockReq = System.Text.Json.JsonSerializer.Serialize(new
            {
                startTimestamp = replayDate.ToUniversalTime().ToString("O"),
                speed          = cfg.ReplaySpeed,
                initialBalance = cfg.ReplayBalance
            });
            var frame = $"replay/initializeclock\n2\n\n{clockReq}";
            await ws.SendAsync(
                System.Text.Encoding.UTF8.GetBytes(frame),
                System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
            _log.LogInformation("Replay clock initialized: {Date} speed={Speed}% balance=${Bal}",
                replayDate, cfg.ReplaySpeed, cfg.ReplayBalance);
        };

        return feed;
    }

    /// <summary>
    /// Fetches today's closed bars from the broker REST API and feeds them to the engine
    /// so the ORB and ATR are fully warm when the engine starts mid-session.
    /// Returns the number of bars fed, or 0 if backfill was skipped / failed.
    /// </summary>
    private async Task<int> BackfillAsync(StrategyConfig cfg, IServiceScope scope,
        ComposableEngine engine, IHubContext<TradingHub> hub, string ticker, CancellationToken ct)
    {
        try
        {
            var etTz  = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
            var nowUtc = DateTime.UtcNow;
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, etTz);
            var today  = nowEt.Date;

            // Try to restore ORB from cache (streaming-derived, not REST).
            // At backfill time we're in the initial (pre-session-manager) phase,
            // so determine which session is currently active to match the cache key.
            var sessionsMgr = new SessionManager(cfg.Sessions ?? SessionConfig.CreateDefaults(cfg));
            var activeSession = sessionsMgr.GetActiveSession(TimeOnly.FromDateTime(nowEt));
            var sessionIdStr = activeSession?.SessionId.ToString() ?? "";
            // Normalize ticker for cache lookup (cache saves canonical, backfill receives broker format)
            var canonicalTicker = ticker.TrimStart('/');
            // Pre-set session ID so any ORB formed during backfill is cached with the correct key
            engine.SetActiveSessionId(sessionIdStr);

            var cached = OrbStateCacheService.Load(canonicalTicker, today, sessionIdStr);
            if (cached != null)
            {
                engine.RestoreOrb(cached);
                _log.LogInformation("ORB restored from cache for {Ticker} session {Session}", canonicalTicker, sessionIdStr);
            }

            // Use earliest ORB start across all enabled sessions for backfill window
            var earliestOrbStart = cfg.OrbStart;
            if (cfg.Sessions != null)
            {
                foreach (var s in cfg.Sessions)
                {
                    if (s.Enabled && s.OrbStart < earliestOrbStart)
                        earliestOrbStart = s.OrbStart;
                }
            }
            var orbStartEt = today.Add(earliestOrbStart.ToTimeSpan());
            // If earliest ORB start is in the evening (e.g. Asia 19:00) and it's past midnight,
            // the ORB start was yesterday evening
            if (orbStartEt > nowEt)
                orbStartEt = orbStartEt.AddDays(-1);

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
                ticker, fromEt, toEt, cfg.ExecutionTFMinutes);

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
                bars = loader.LoadAsync(ticker, cfg.ExecutionTFMinutes, fromUtc, toUtc, ct);
            }
            else if (cfg.Broker == "Schwab")
            {
                // Validate the configured ticker is the current active contract.
                // Only warn for quarterly equity indices (NQ/ES/YM/RTY) where the roll calendar
                // is accurate. Commodity contracts (GC, CL) use different expiration conventions
                // (e.g. CL expires ~3 business days before the 25th of the prior month).
                var root = FuturesSymbol.RootSymbol(ticker);
                if (ContractRollCalendar.IsQuarterly(root))
                {
                    var active = ContractRollCalendar.ActiveContract(root);
                    if (!string.Equals(FuturesSymbol.Normalize(ticker), active, StringComparison.OrdinalIgnoreCase))
                        _log.LogWarning(
                            "Ticker mismatch: config uses {Ticker} but active contract is {Active} — backfill may return stale data from broker",
                            ticker, active);
                }

                var auth   = scope.ServiceProvider.GetRequiredService<SchwabAuthService>();
                var token  = await auth.GetAccessTokenAsync();
                var loader = new SchwabHistoricalLoader(
                    token, lf.CreateLogger<SchwabHistoricalLoader>(), auth.ApiBaseUrl, hf);
                bars = loader.LoadAsync(ticker, cfg.ExecutionTFMinutes, fromUtc, toUtc, ct);
            }
            // Tradovate historical loader: add in Task 14
            else if (cfg.Broker is "Tradovate" or "TradovateReplay") return 0; // warmup via stream's built-in history
            else return 0;

            int count = 0;
            int skipped = 0;
            Bar? prevBar = null;
            await foreach (var bar in bars.WithCancellation(ct))
            {
                // Detect intra-session price jumps >10% between consecutive bars (stale/wrong contract data).
                // Only check same-day bars — cross-session gaps are normal.
                if (prevBar != null && prevBar.Close > 0 && bar.Open > 0 && prevBar.Time.Date == bar.Time.Date)
                {
                    decimal pctChange = Math.Abs(bar.Open - prevBar.Close) / prevBar.Close;
                    if (pctChange > 0.10m)
                    {
                        _log.LogWarning(
                            "Backfill anomaly: intra-session price jump {Pct:P1} between {T1:u} C={C1:F2} and {T2:u} O={O2:F2} — skipping bar (possible stale contract data from broker)",
                            pctChange, prevBar.Time, prevBar.Close, bar.Time, bar.Open);
                        skipped++;
                        continue;
                    }
                }

                // WarmupBarAsync builds ORB/ATR/VWAP without running strategy logic,
                // so historical bars do not fire orders or consume the trade-count budget.
                await engine.WarmupBarAsync(bar, ticker, ct);
                prevBar = bar;
                count++;
            }
            if (skipped > 0)
                _log.LogWarning("Backfill: skipped {Skipped} anomalous bars (possible wrong contract data)", skipped);
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

/// <summary>
/// Wraps IStrategyEventSink to suppress trade persistence while keeping
/// dashboard snapshots and engine signals flowing. Used when SaveReplayTrades=false.
/// </summary>
internal class ReplayFilterSink : IStrategyEventSink
{
    private readonly IStrategyEventSink _inner;
    private readonly ILogger _log;

    public ReplayFilterSink(IStrategyEventSink inner, ILogger log)
    {
        _inner = inner;
        _log   = log;
    }

    public Task OnEntryAsync(EntrySignal s)          => _inner.OnEntryAsync(s);
    public Task OnPartialAsync(PartialSignal s)      => _inner.OnPartialAsync(s);
    public Task OnBEMoveAsync(BESignal s)            => _inner.OnBEMoveAsync(s);
    public Task OnSnapshotAsync(EngineSnapshot snap) => _inner.OnSnapshotAsync(snap);

    public Task OnExitAsync(ExitSignal s, TradeRecord t)
    {
        _log.LogInformation("[REPLAY] Trade completed but NOT persisted: {Setup} {Dir} {Entry}→{Exit} PnL={Pnl:F2}",
            t.Setup, t.Direction, t.Entry, t.Exit, t.NetPnl);
        return Task.CompletedTask;
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
