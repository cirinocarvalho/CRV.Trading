using CRV.Backtest.DataLoaders;
using CRV.Core.Interfaces;
using CRV.Core.Strategy;
using CRV.Live;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CRV.Web.Api;

[ApiController]
[Route("api/engine")]
[EnableRateLimiting("engine-api")]
public class EngineController : ControllerBase
{
    private readonly LiveEngineOrchestrator _engine;
    private readonly StrategyConfigService  _cfgSvc;
    private readonly ILastPriceProvider     _prices;
    private readonly SnapshotBroadcastService _broadcast;
    private readonly TradeRepository _trades;
    private readonly IServiceProvider _sp;
    private readonly ILoggerFactory _loggerFactory;

    public EngineController(LiveEngineOrchestrator engine, StrategyConfigService cfgSvc,
                            ILastPriceProvider prices, SnapshotBroadcastService broadcast,
                            TradeRepository trades, IServiceProvider sp, ILoggerFactory loggerFactory)
    {
        _engine = engine;
        _cfgSvc = cfgSvc;
        _prices = prices;
        _broadcast = broadcast;
        _trades = trades;
        _sp = sp;
        _loggerFactory = loggerFactory;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        if (_engine.IsRunning)
            return BadRequest(new { status = "already_running" });

        var cfg = _cfgSvc.Current;
        _ = _engine.StartAsync(cfg);
        return Ok(new { status = "started", ticker = cfg.Ticker });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _engine.StopEngine();
        return Ok(new { status = "stopped" });
    }

    [HttpGet("status")]
    public IActionResult Status() =>
        Ok(new
        {
            running  = _engine.IsRunning,
            status   = _engine.Status,
            snapshot = _engine.LastSnapshot
        });

    /// <summary>SSE stream of engine snapshots for the dashboard.</summary>
    [HttpGet("stream")]
    [DisableRateLimiting]   // long-lived connection — must not consume rate-limit slots
    public async Task Stream(CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        await foreach (var snap in _broadcast.SubscribeWithHeartbeatAsync(ct))
        {
            if (snap is null)
            {
                // Heartbeat — keep connection alive through proxies/Kestrel
                await Response.WriteAsync(": heartbeat\n\n", ct);
            }
            else
            {
                var json = System.Text.Json.JsonSerializer.Serialize(snap, options);
                await Response.WriteAsync($"data: {json}\n\n", ct);
            }
            await Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>Returns the last known price for a ticker (tries both canonical and broker-formatted).</summary>
    [HttpGet("price/{ticker}")]
    public IActionResult Price(string ticker)
    {
        // Try canonical first, then broker-formatted
        var price = _prices.GetLastPrice(ticker);
        if (price == 0)
        {
            var broker = _cfgSvc.Current.Broker;
            var brokerTicker = FuturesSymbol.ForBroker(ticker, broker);
            price = _prices.GetLastPrice(brokerTicker);
        }
        return Ok(new { ticker, price });
    }

    /// <summary>Force-set the ORB by fetching historical bars from the broker.</summary>
    [HttpPost("force-orb")]
    public async Task<IActionResult> ForceOrb()
    {
        var (ok, message) = await _engine.ForceOrbAsync(_cfgSvc.Current);
        return ok ? Ok(new { status = "ok", message }) : BadRequest(new { status = "error", message });
    }

    /// <summary>Returns bar history for a ticker group (for Lightweight Charts).
    /// Falls back to Schwab REST API when the in-memory buffer is empty (e.g. market closed).</summary>
    [HttpGet("bars/{groupKey}")]
    public async Task<IActionResult> Bars(string groupKey, CancellationToken ct)
    {
        var barsWithVwap = _engine.GetBarHistoryWithVwap(groupKey);

        // If engine has bars in buffer, use them
        if (barsWithVwap.Count > 0)
        {
            return Ok(barsWithVwap.Select(bv => new
            {
                time  = new DateTimeOffset(bv.Bar.Time, TimeSpan.Zero).ToUnixTimeSeconds(),
                open  = bv.Bar.Open, high = bv.Bar.High, low = bv.Bar.Low, close = bv.Bar.Close,
                volume = bv.Bar.Volume,
                vwap  = bv.Vwap
            }));
        }

        // Fallback: fetch historical bars from broker REST/WS API
        var cfg = _cfgSvc.Current;
        var ticker = ResolveTickerForGroup(groupKey, cfg);
        if (string.IsNullOrEmpty(ticker)) return Ok(Array.Empty<object>());

        var tf    = cfg.ExecutionTFMinutes;
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddMinutes(-tf * 200); // ~200 bars back

        // 15-second timeout so the chart doesn't hang if the broker API is slow/down
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            var result = new List<object>();

            if (cfg.Broker is "Tradovate" or "TradovateReplay")
            {
                var auth   = _sp.GetRequiredService<TradovateAuthService>();
                var mdToken = await auth.GetMdAccessTokenAsync();
                var log    = _loggerFactory.CreateLogger<TradovateHistoricalLoader>();
                var loader = new TradovateHistoricalLoader(mdToken, log, auth.MdWssUrl);
                var tvSymbol = FuturesSymbol.ToTradovate(ticker);

                await foreach (var b in loader.LoadAsync(tvSymbol, tf, fromUtc, toUtc, ct: cts.Token))
                {
                    result.Add(new
                    {
                        time  = new DateTimeOffset(b.Time, TimeSpan.Zero).ToUnixTimeSeconds(),
                        open  = b.Open, high = b.High, low = b.Low, close = b.Close,
                        volume = b.Volume
                    });
                }
            }
            else if (cfg.Broker == "Schwab")
            {
                var auth  = _sp.GetRequiredService<SchwabAuthService>();
                var token = await auth.GetAccessTokenAsync();
                var hf    = _sp.GetRequiredService<IHttpClientFactory>();
                var loader = new SchwabHistoricalLoader(
                    token, _loggerFactory.CreateLogger<SchwabHistoricalLoader>(), auth.ApiBaseUrl, hf);

                await foreach (var b in loader.LoadAsync(ticker, tf, fromUtc, toUtc, cts.Token))
                {
                    result.Add(new
                    {
                        time  = new DateTimeOffset(b.Time, TimeSpan.Zero).ToUnixTimeSeconds(),
                        open  = b.Open, high = b.High, low = b.Low, close = b.Close,
                        volume = b.Volume
                    });
                }
            }

            return Ok(result);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _loggerFactory.CreateLogger<EngineController>()
                .LogWarning("Chart bars request timed out for {Broker}/{Group}", cfg.Broker, groupKey);
            return Ok(Array.Empty<object>());
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<EngineController>()
                .LogWarning(ex, "Failed to fetch historical bars from {Broker} for chart", cfg.Broker);
            return Ok(Array.Empty<object>());
        }
    }

    /// <summary>Resolve the broker ticker for a group key (NQ, ES, GC, CL) from config.</summary>
    private static string? ResolveTickerForGroup(string groupKey, CRV.Core.Models.StrategyConfig cfg)
    {
        // Check each setup's effective ticker to find one matching this group
        var tickers = new[] { cfg.EffectiveTickerA, cfg.EffectiveTickerB, cfg.EffectiveTickerC, cfg.EffectiveTickerD };
        foreach (var t in tickers)
        {
            if (!string.IsNullOrEmpty(t) && TickerGroup.GetGroupKey(t) == groupKey)
                return t;
        }
        // Fallback: if the main ticker matches
        if (TickerGroup.GetGroupKey(cfg.Ticker) == groupKey)
            return cfg.Ticker;
        return null;
    }

    /// <summary>Returns today's completed trades for the dashboard table.</summary>
    [HttpGet("trades/today")]
    public async Task<IActionResult> TodayTrades()
    {
        var trades = await _trades.GetTodayAsync();
        var est = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var rows = trades.Select(t => new
        {
            time     = TimeZoneInfo.ConvertTimeFromUtc(t.EnteredAt, est).ToString("HH:mm:ss"),
            setup    = !string.IsNullOrEmpty(t.SetupLabel) ? t.SetupLabel : t.Setup.ToString(),
            ticker   = t.Ticker?.TrimStart('/') ?? "",
            dir      = t.Direction.ToString(),
            entry    = t.Entry,
            exit     = t.Exit,
            reason   = t.ExitReason.ToString(),
            netPnl   = t.NetPnl,
            r        = t.RMultiple
        });
        return Ok(rows);
    }
}
