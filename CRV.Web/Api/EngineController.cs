using CRV.Backtest.DataLoaders;
using CRV.Core.Interfaces;
using CRV.Core.Strategy;
using CRV.Live;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

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

    // ── Trade reconciliation against broker fills ──────────────────

    /// <summary>
    /// POST /api/engine/trades/reconcile
    /// Fetches recent fills from Tradovate and updates trade records with actual fill prices,
    /// then recalculates GrossPnl, NetPnl, and RMultiple.
    /// </summary>
    [HttpPost("trades/reconcile")]
    public async Task<IActionResult> ReconcileTrades([FromBody] ReconcileTradesRequest req)
    {
        var cfg = _cfgSvc.Current;
        if (cfg.Broker != "Tradovate" && cfg.ExecBroker != "Tradovate")
            return BadRequest(new { status = "error", message = "Reconciliation currently only supports Tradovate." });

        // Fetch all fills from Tradovate
        var auth = _sp.GetRequiredService<TradovateAuthService>();
        var token = await auth.GetAccessTokenAsync();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var fillRes = await http.GetAsync($"{auth.ApiBaseUrl}/fill/list");
        if (!fillRes.IsSuccessStatusCode)
            return BadRequest(new { status = "error", message = $"Tradovate /fill/list failed: {fillRes.StatusCode}" });

        var fillBody = await fillRes.Content.ReadAsStringAsync();
        var fills = new List<FillRow>();
        using (var doc = System.Text.Json.JsonDocument.Parse(fillBody))
        {
            foreach (var f in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var action = f.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                    var qty    = f.TryGetProperty("qty",    out var q) && q.ValueKind == System.Text.Json.JsonValueKind.Number ? q.GetDecimal() : 0m;
                    var price  = f.TryGetProperty("price",  out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetDecimal() : 0m;
                    var ts     = f.TryGetProperty("timestamp", out var tProp) ? tProp.GetString() ?? "" : "";
                    var contractId = f.TryGetProperty("contractId", out var cid) && cid.ValueKind == System.Text.Json.JsonValueKind.Number ? cid.GetInt64() : 0L;
                    if (qty <= 0 || price <= 0 || !DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc)) continue;
                    fills.Add(new FillRow(action, qty, price, utc, contractId));
                }
                catch { /* skip malformed */ }
            }
        }

        // Filter fills to the requested window
        DateTime fromUtc = req.FromUtc ?? DateTime.UtcNow.AddDays(-7);
        DateTime toUtc   = req.ToUtc   ?? DateTime.UtcNow;
        fills = fills.Where(f => f.Time >= fromUtc && f.Time <= toUtc).OrderBy(f => f.Time).ToList();

        if (fills.Count == 0)
            return Ok(new { status = "ok", message = "No fills returned from Tradovate for the requested window.", updated = 0 });

        // Load trades in the same window
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CRV.Core.Data.TradingDbContext>();
        var trades = await db.Trades
            .Where(t => t.EnteredAt >= fromUtc && t.EnteredAt <= toUtc && t.Source == "live")
            .OrderBy(t => t.EnteredAt)
            .ToListAsync();

        var results = new List<object>();
        int updated = 0;

        foreach (var t in trades)
        {
            // Find entry fill: matches direction + qty, timestamp closest to (and >=) t.EnteredAt - 5 min window
            var wantedAction = t.Direction == CRV.Core.Models.Direction.Long ? "Buy" : "Sell";
            var exitAction   = t.Direction == CRV.Core.Models.Direction.Long ? "Sell" : "Buy";

            var entryWindow = fills.Where(f =>
                f.Action == wantedAction &&
                f.Qty == t.Contracts &&
                Math.Abs((f.Time - t.EnteredAt).TotalMinutes) <= 5)
                .OrderBy(f => Math.Abs((f.Time - t.EnteredAt).TotalMinutes))
                .FirstOrDefault();

            // Find exit fills: opposite direction, total qty == t.Contracts, after entry time, before exit+5 min
            var exitFills = fills.Where(f =>
                f.Action == exitAction &&
                f.Time >= t.EnteredAt &&
                f.Time <= t.ExitedAt.AddMinutes(5))
                .ToList();
            decimal exitQty = 0;
            decimal exitWeighted = 0;
            foreach (var f in exitFills)
            {
                if (exitQty + f.Qty > t.Contracts) break;
                exitQty += f.Qty;
                exitWeighted += f.Qty * f.Price;
            }

            var before = new { Entry = t.Entry, Exit = t.Exit, GrossPnl = t.GrossPnl, NetPnl = t.NetPnl };
            bool changed = false;

            if (entryWindow != null && Math.Abs(entryWindow.Price - t.Entry) > 0.01m)
            {
                t.Entry = entryWindow.Price;
                changed = true;
            }

            if (exitQty == t.Contracts && exitWeighted > 0)
            {
                var avgExit = exitWeighted / exitQty;
                if (Math.Abs(avgExit - t.Exit) > 0.01m)
                {
                    t.Exit = avgExit;
                    changed = true;
                }
            }

            if (changed)
            {
                // Recalculate P&L
                decimal pv = cfg.PointValue;
                decimal direction = t.Direction == CRV.Core.Models.Direction.Long ? 1m : -1m;
                t.GrossPnl = direction * (t.Exit - t.Entry) * t.Contracts * pv;
                t.NetPnl   = t.GrossPnl - t.Commission;
                decimal risk = Math.Abs(t.Entry - t.InitialStop) * t.Contracts * pv;
                t.RMultiple = risk > 0 ? t.NetPnl / risk : 0m;
                updated++;
                results.Add(new
                {
                    id = t.Id, setup = t.SetupLabel, direction = t.Direction.ToString(),
                    before, after = new { Entry = t.Entry, Exit = t.Exit, GrossPnl = t.GrossPnl, NetPnl = t.NetPnl }
                });
            }
        }

        if (updated > 0 && !req.DryRun)
            await db.SaveChangesAsync();

        return Ok(new
        {
            status = "ok",
            dryRun = req.DryRun,
            fromUtc, toUtc,
            totalFills = fills.Count,
            totalTrades = trades.Count,
            updated,
            changes = results
        });
    }

    public record ReconcileTradesRequest
    {
        /// <summary>Start of reconciliation window (UTC). Defaults to 7 days ago.</summary>
        public DateTime? FromUtc { get; init; }
        /// <summary>End of reconciliation window (UTC). Defaults to now.</summary>
        public DateTime? ToUtc { get; init; }
        /// <summary>If true, return proposed changes without saving. Default false.</summary>
        public bool DryRun { get; init; }
    }

    private record FillRow(string Action, decimal Qty, decimal Price, DateTime Time, long ContractId);

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

        // Build the bracket list. Two modes:
        //   1. Explicit N-bracket list via req.Brackets (new — up to 4 brackets)
        //   2. Legacy Tgt1/Tgt2/PartialQty fields (backcompat)
        IReadOnlyList<CRV.Core.Models.BracketLeg>? brackets = null;
        decimal tgt1 = 0, tgt2 = 0;
        bool usePartial;
        int partialCts = 0;

        if (req.Brackets is { Length: > 0 })
        {
            // Explicit N-bracket path. Validate and normalize.
            if (req.Brackets.Length > 4)
                return BadRequest(new { status = "error", message = "Maximum 4 brackets supported." });
            if (req.Brackets.Any(b => b.Target <= 0))
                return BadRequest(new { status = "error", message = "Every bracket must specify a positive Target price." });

            var sum = req.Brackets.Sum(b => b.Qty);
            if (sum != req.Qty)
                return BadRequest(new { status = "error", message =
                    $"Sum of bracket quantities ({sum}) must equal Qty ({req.Qty})." });

            brackets = req.Brackets
                .Select(b => new CRV.Core.Models.BracketLeg(b.Target, b.Qty, b.MoveBe))
                .ToArray();

            // Populate legacy fields so downstream (non-Tradovate) brokers still work
            tgt1 = brackets[0].TargetPrice;
            tgt2 = brackets[^1].TargetPrice;
            usePartial = brackets.Count >= 2;
            partialCts = usePartial ? brackets[0].Qty : 0;
        }
        else
        {
            // Legacy Tgt1/Tgt2 path
            tgt1 = req.Tgt1 > 0 ? req.Tgt1 : req.Entry;
            tgt2 = req.Tgt2 > 0 ? req.Tgt2 : req.Entry;
            usePartial = req.WithPartial && tgt1 > 0 && tgt1 != req.Entry;
            partialCts = usePartial
                ? (req.PartialQty > 0 ? Math.Min(req.PartialQty, req.Qty - 1) : Math.Max(1, req.Qty / 2))
                : 0;
        }

        // Warn when N>2 and the live broker can't honor it (Schwab/TradeStation only see Tg1/Tg2).
        if (brackets is { Count: > 2 }
            && cfg.Broker is "Schwab" or "TradeStation")
        {
            _loggerFactory.CreateLogger<EngineController>()
                .LogWarning("[WEBHOOK] {Broker} does not support N-bracket signals — only Tg1 and Tg{Last} will be used, brackets 2..{Last2} will be dropped",
                    cfg.Broker, brackets.Count, brackets.Count - 1);
        }

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
            AutoTrailFreq: trailFreq,
            Brackets: brackets);

        var (ok, message, group) = await _engine.PlaceManualEntryAsync(signal, pointValue);

        if (!ok)
            return BadRequest(new { status = "error", message });

        _loggerFactory.CreateLogger<EngineController>()
            .LogInformation("[WEBHOOK] Order placed: {Dir} {Qty}× {Ticker} @ {Entry} | Stop {Stop} | Brackets {N} | Label {Label}",
                direction, req.Qty, ticker, req.Entry, req.Stop,
                brackets?.Count ?? (usePartial ? 2 : 1), label);

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
            brackets = brackets?.Select(b => new { target = b.TargetPrice, qty = b.Qty, moveBe = b.MoveBe }).ToArray(),
            qty = req.Qty,
            usePartial,
            moveBe = req.MoveBe,
            autoTrail = req.AutoTrail,
            label,
        });
    }

    public record WebhookBracketSpec
    {
        /// <summary>Target price for this bracket leg.</summary>
        public decimal Target { get; init; }
        /// <summary>Quantity for this bracket leg. Sum across all brackets must equal Qty.</summary>
        public int Qty { get; init; }
        /// <summary>Move stop to breakeven after this bracket fills (only meaningful for non-terminal brackets).</summary>
        public bool MoveBe { get; init; }
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
        /// <summary>Target 1 (partial exit) price. 0 = no partial. Ignored if <see cref="Brackets"/> is supplied.</summary>
        public decimal Tgt1 { get; init; }
        /// <summary>Target 2 (full exit) price. Ignored if <see cref="Brackets"/> is supplied.</summary>
        public decimal Tgt2 { get; init; }
        /// <summary>Contracts to exit at Tgt1. 0 = auto (qty/2). Rest exits at Tgt2/stop. Ignored if <see cref="Brackets"/> is supplied.</summary>
        public int PartialQty { get; init; }
        /// <summary>Enable partial exit at Tgt1. Ignored if <see cref="Brackets"/> is supplied.</summary>
        public bool WithPartial { get; init; } = true;
        /// <summary>
        /// Optional N-bracket specification (up to 4). When supplied, overrides the
        /// legacy Tgt1/Tgt2/PartialQty/WithPartial fields. Quantities must sum to Qty.
        /// Brackets are filled in order: first = Tg1 (partial), last = final target.
        /// </summary>
        public WebhookBracketSpec[]? Brackets { get; init; }
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
