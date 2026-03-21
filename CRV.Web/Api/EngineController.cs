using CRV.Core.Interfaces;
using CRV.Live;
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

    public EngineController(LiveEngineOrchestrator engine, StrategyConfigService cfgSvc,
                            ILastPriceProvider prices, SnapshotBroadcastService broadcast,
                            TradeRepository trades)
    {
        _engine = engine;
        _cfgSvc = cfgSvc;
        _prices = prices;
        _broadcast = broadcast;
        _trades = trades;
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

    /// <summary>Returns bar history for a ticker group (for Lightweight Charts).</summary>
    [HttpGet("bars/{groupKey}")]
    public IActionResult Bars(string groupKey)
    {
        var bars = _engine.GetBarHistory(groupKey);
        var result = bars.Select(b => new
        {
            time  = new DateTimeOffset(b.Time, TimeSpan.Zero).ToUnixTimeSeconds(),
            open  = b.Open,
            high  = b.High,
            low   = b.Low,
            close = b.Close
        });
        return Ok(result);
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
            setup    = t.Setup.ToString(),
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
