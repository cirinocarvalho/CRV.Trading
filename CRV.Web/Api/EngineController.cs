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

    public EngineController(LiveEngineOrchestrator engine, StrategyConfigService cfgSvc,
                            ILastPriceProvider prices, SnapshotBroadcastService broadcast)
    {
        _engine = engine;
        _cfgSvc = cfgSvc;
        _prices = prices;
        _broadcast = broadcast;
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
    public async Task Stream(CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        await foreach (var snap in _broadcast.SubscribeAsync(ct))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(snap, options);
            await Response.WriteAsync($"data: {json}\n\n", ct);
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
}
