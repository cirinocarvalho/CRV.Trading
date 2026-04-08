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
    private readonly EmailNotificationService _emailSvc;

    public EngineController(LiveEngineOrchestrator engine, StrategyConfigService cfgSvc,
                            ILastPriceProvider prices, SnapshotBroadcastService broadcast,
                            TradeRepository trades, IServiceProvider sp, ILoggerFactory loggerFactory,
                            EmailNotificationService emailSvc)
    {
        _engine = engine;
        _cfgSvc = cfgSvc;
        _prices = prices;
        _broadcast = broadcast;
        _trades = trades;
        _sp = sp;
        _loggerFactory = loggerFactory;
        _emailSvc = emailSvc;
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
        var barsWithIndicators = _engine.GetBarHistoryWithIndicators(groupKey);

        // If engine has bars in buffer, use them
        if (barsWithIndicators.Count > 0)
        {
            // Recompute EMA21 from bar closes so the full history gets the curve
            // (stored per-bar values may be 0 for bars that were backfilled before the indicator existed)
            var ema21 = new CRV.Core.Indicators.Ema21Indicator();
            var result = barsWithIndicators.Select(bi =>
            {
                ema21.Update(bi.Bar.Close);
                return new
                {
                    time  = new DateTimeOffset(bi.Bar.Time, TimeSpan.Zero).ToUnixTimeSeconds(),
                    open  = bi.Bar.Open, high = bi.Bar.High, low = bi.Bar.Low, close = bi.Bar.Close,
                    volume = bi.Bar.Volume,
                    vwap  = bi.Vwap,
                    ema21 = ema21.HasValue ? ema21.Value : 0m
                };
            }).ToList();
            return Ok(result);
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

            // Recompute EMA21 from the fetched bars
            var fallbackEma = new CRV.Core.Indicators.Ema21Indicator();
            var enriched = result.Cast<dynamic>().Select(b =>
            {
                fallbackEma.Update((decimal)b.close);
                return new
                {
                    time = (long)b.time, open = (decimal)b.open, high = (decimal)b.high,
                    low = (decimal)b.low, close = (decimal)b.close, volume = (long)b.volume,
                    vwap = 0m,
                    ema21 = fallbackEma.IsReady ? fallbackEma.Value : 0m
                };
            }).ToList();
            return Ok(enriched);
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

    /// <summary>Send a test email to verify SMTP and recipient configuration.</summary>
    [HttpPost("email/test")]
    public async Task<IActionResult> TestEmail()
    {
        var (ok, message) = await _emailSvc.SendTestEmailAsync();
        return ok ? Ok(new { status = "ok", message }) : BadRequest(new { status = "error", message });
    }

    /// <summary>Recover an orphaned Tradovate strategy and register it for live tracking.</summary>
    [HttpPost("recover-strategy")]
    public async Task<IActionResult> RecoverStrategy([FromBody] RecoverStrategyRequest req)
    {
        var cfg = _cfgSvc.Current;
        var pointValue = req.PointValue > 0 ? req.PointValue : cfg.PointValue;
        var direction = req.Direction?.ToLowerInvariant() == "short"
            ? CRV.Core.Models.Direction.Short
            : CRV.Core.Models.Direction.Long;

        var (ok, message, group) = await _engine.RecoverStrategyAsync(
            req.StrategyId, req.Ticker, direction,
            req.TotalContracts, req.PartialContracts, req.UseBe,
            req.SetupId ?? $"Recovered-{req.StrategyId}", pointValue);

        if (!ok) return BadRequest(new { status = "error", message });
        return Ok(new
        {
            status = "ok", message,
            groupOrderId = group?.GroupOrderId,
            entryPrice = group?.EntryPrice,
            legs = group?.Legs.Select(l => new { l.LegType, l.OrderId, l.Price, l.Status })
        });
    }

    public record RecoverStrategyRequest(
        long StrategyId, string Ticker, string Direction,
        int TotalContracts, int PartialContracts = 0, bool UseBe = false,
        string? SetupId = null, decimal PointValue = 0);

    // ── Webhook — external order entry ──────────────────────────

    /// <summary>
    /// POST /api/engine/webhook/order
    /// Accepts an external order and places it through the live engine.
    /// Use from TradingView alerts, scripts, or any HTTP client.
    /// </summary>
    [HttpPost("webhook/order")]
    public async Task<IActionResult> WebhookOrder([FromBody] WebhookOrderRequest req)
    {
        var cfg = _cfgSvc.Current;

        // Resolve direction
        var dirStr = (req.Direction ?? "").Trim().ToLowerInvariant();
        if (dirStr is not ("long" or "short" or "buy" or "sell"))
            return BadRequest(new { status = "error", message = "Direction must be 'long', 'short', 'buy', or 'sell'." });
        var direction = dirStr is "long" or "buy"
            ? CRV.Core.Models.Direction.Long
            : CRV.Core.Models.Direction.Short;

        // Resolve ticker — use request ticker or fall back to global config
        var ticker = !string.IsNullOrEmpty(req.Ticker) ? req.Ticker : cfg.Ticker;
        var pointValue = req.PointValue > 0 ? req.PointValue : cfg.PointValue;
        var tickSize = req.TickSize > 0 ? req.TickSize : cfg.TickSize;

        // Validate required fields
        if (req.Entry <= 0) return BadRequest(new { status = "error", message = "Entry price is required." });
        if (req.Stop <= 0) return BadRequest(new { status = "error", message = "Stop price is required." });
        if (req.Qty <= 0) return BadRequest(new { status = "error", message = "Qty must be > 0." });

        // Compute targets — use explicit tgt1/tgt2 or fall back to entry ± distance
        decimal tgt1 = req.Tgt1 > 0 ? req.Tgt1 : req.Entry; // no partial if not specified
        decimal tgt2 = req.Tgt2 > 0 ? req.Tgt2 : req.Entry;
        bool usePartial = req.WithPartial && tgt1 > 0 && tgt1 != req.Entry;
        int partialCts = usePartial
            ? (req.PartialQty > 0 ? Math.Min(req.PartialQty, req.Qty - 1) : Math.Max(1, req.Qty / 2))
            : 0;

        // Build auto-trail if requested
        decimal? trailSL = null, trailTrigger = null, trailFreq = null;
        if (req.AutoTrail && req.TrailStopLoss > 0)
        {
            trailSL = req.TrailStopLoss;
            trailTrigger = req.TrailTrigger > 0 ? req.TrailTrigger : null;
            trailFreq = req.TrailFreq > 0 ? req.TrailFreq : req.TrailStopLoss;
        }

        var label = !string.IsNullOrEmpty(req.Label) ? req.Label : $"webhook-{DateTime.UtcNow:HHmmss}";

        var signal = new CRV.Core.Models.EntrySignal(
            Setup: CRV.Core.Models.SetupId.F,
            Direction: direction,
            Entry: req.Entry,
            Stop: req.Stop,
            Tg2Price: tgt2,
            Tg1Price: tgt1,
            TotalContracts: req.Qty,
            Time: DateTime.UtcNow,
            OrderType: req.OrderType ?? "Market",
            Ticker: ticker,
            SetupLabel: label,
            PartialContracts: partialCts,
            PointValue: pointValue,
            UsePartial: usePartial,
            UseBe: req.MoveBe,
            AutoTrailStopLoss: trailSL,
            AutoTrailTrigger: trailTrigger,
            AutoTrailFreq: trailFreq);

        var (ok, message, group) = await _engine.PlaceManualEntryAsync(signal, pointValue);

        if (!ok)
            return BadRequest(new { status = "error", message });

        _loggerFactory.CreateLogger<EngineController>()
            .LogInformation("[WEBHOOK] Order placed: {Dir} {Qty}× {Ticker} @ {Entry} | Stop {Stop} | Tgt1 {Tgt1} | Tgt2 {Tgt2} | Label {Label}",
                direction, req.Qty, ticker, req.Entry, req.Stop, tgt1, tgt2, label);

        return Ok(new
        {
            status = "ok",
            message,
            groupOrderId = group?.GroupOrderId,
            direction = direction.ToString(),
            entry = req.Entry,
            stop = req.Stop,
            tgt1,
            tgt2,
            qty = req.Qty,
            usePartial,
            moveBe = req.MoveBe,
            autoTrail = req.AutoTrail,
            label,
        });
    }

    public record WebhookOrderRequest
    {
        /// <summary>"long", "short", "buy", or "sell"</summary>
        public string? Direction { get; init; }
        /// <summary>Entry price</summary>
        public decimal Entry { get; init; }
        /// <summary>Stop loss price</summary>
        public decimal Stop { get; init; }
        /// <summary>Number of contracts</summary>
        public int Qty { get; init; }
        /// <summary>Target 1 (partial exit) price. 0 = no partial.</summary>
        public decimal Tgt1 { get; init; }
        /// <summary>Target 2 (full exit) price</summary>
        public decimal Tgt2 { get; init; }
        /// <summary>Contracts to exit at Tgt1. 0 = auto (qty/2). Rest exits at Tgt2/stop.</summary>
        public int PartialQty { get; init; }
        /// <summary>Enable partial exit at Tgt1</summary>
        public bool WithPartial { get; init; } = true;
        /// <summary>Move stop to breakeven after partial fill</summary>
        public bool MoveBe { get; init; } = true;
        /// <summary>Enable auto-trail stop</summary>
        public bool AutoTrail { get; init; }
        /// <summary>Trail stop distance (points). Required when AutoTrail=true.</summary>
        public decimal TrailStopLoss { get; init; }
        /// <summary>Trail activation trigger (points from entry). 0 = activate immediately.</summary>
        public decimal TrailTrigger { get; init; }
        /// <summary>Trail ratchet frequency (points). 0 = use TrailStopLoss.</summary>
        public decimal TrailFreq { get; init; }
        /// <summary>Broker ticker (e.g. "/MNQM26"). Empty = use global config ticker.</summary>
        public string? Ticker { get; init; }
        /// <summary>Point value override. 0 = use global config.</summary>
        public decimal PointValue { get; init; }
        /// <summary>Tick size override. 0 = use global config.</summary>
        public decimal TickSize { get; init; }
        /// <summary>"Market" or "Limit". Default: Market.</summary>
        public string? OrderType { get; init; }
        /// <summary>Custom label for the order (appears in dashboard). Auto-generated if empty.</summary>
        public string? Label { get; init; }
    }
}
