namespace CRV.Web.Pages.Options;

using System.Text.Json;
using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Core.Options;
using Microsoft.EntityFrameworkCore;
using CRV.Live;
using CRV.Live.Brokers.Schwab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class ExplorerModel : PageModel
{
    private readonly SchwabAuthService    _schwab;
    private readonly IHttpClientFactory   _httpFactory;
    private readonly IConfiguration       _config;
    private readonly TradingDbContext     _db;
    private readonly ILogger<ExplorerModel> _log;

    public ExplorerModel(
        SchwabAuthService schwab,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        TradingDbContext db,
        ILogger<ExplorerModel> log)
    {
        _schwab      = schwab;
        _httpFactory = httpFactory;
        _config      = config;
        _db          = db;
        _log         = log;
    }

    /// <summary>
    /// Live placement is opt-in via configuration and off by default. Preview works
    /// regardless — the intent is that the ticket is exercised for real, repeatedly,
    /// before anything can reach the market.
    /// </summary>
    public bool LiveOrdersEnabled => _config.GetValue("Options:AllowLiveOrders", false);

    /// <summary>Per-trade ceiling in dollars. 0 disables the check.</summary>
    public decimal MaxTradeRisk => _config.GetValue("Options:MaxTradeRisk", 0m);

    private string SchwabAccountId => _config["Schwab:AccountId"] ?? "";

    /// <summary>Drives the auth banner. Schwab rotates the refresh token on every grant,
    /// so a page opened a few times a week goes stale often — say so up front rather
    /// than failing an opaque fetch.</summary>
    public bool SchwabAuthenticated => _schwab.IsAuthenticated;

    public void OnGet() { }

    // ── AJAX: expirations for a symbol ────────────────────────────
    // Deliberately requests a single strike. The full chain is megabytes; this is the
    // cheapest call that still enumerates every expiry.

    public async Task<IActionResult> OnGetExpirationsAsync(string symbol, CancellationToken ct)
    {
        symbol = (symbol ?? "").Trim().ToUpperInvariant();
        if (symbol.Length == 0) return BadRequest(new { error = "Symbol is required." });

        try
        {
            var chain = await SchwabOptionChain.FetchAsync(
                _schwab, symbol, strikeCount: 1, httpFactory: _httpFactory, ct: ct);

            var byDate = chain.Contracts
                .GroupBy(c => c.Expiration.Date)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    date = g.Key.ToString("yyyy-MM-dd"),
                    dte  = g.Min(c => c.DaysToExpiration),
                })
                .ToList();

            return new JsonResult(new
            {
                symbol          = chain.Underlying,
                underlyingPrice = chain.UnderlyingPrice,
                expirations     = byDate,
            });
        }
        catch (Exception ex) { return Fail(ex, symbol); }
    }

    // ── AJAX: one expiry's chain ──────────────────────────────────

    public async Task<IActionResult> OnGetChainAsync(
        string symbol, string expiry, int strikeCount, CancellationToken ct)
    {
        symbol = (symbol ?? "").Trim().ToUpperInvariant();
        if (symbol.Length == 0)            return BadRequest(new { error = "Symbol is required." });
        if (!DateOnly.TryParse(expiry, out var exp))
            return BadRequest(new { error = "A valid expiry is required." });

        if (strikeCount <= 0) strikeCount = 40;
        strikeCount = Math.Min(strikeCount, 200);   // keep the response bounded

        try
        {
            var chain = await SchwabOptionChain.FetchAsync(
                _schwab, symbol, fromDate: exp, toDate: exp,
                strikeCount: strikeCount, httpFactory: _httpFactory, ct: ct);

            var rows = chain.Contracts
                .OrderBy(c => c.Strike).ThenBy(c => c.Right)
                .Select(c => new
                {
                    symbol       = c.Symbol,          // OSI, verbatim — the order key
                    right        = c.Right.ToString(),
                    strike       = c.Strike,
                    bid          = c.Bid,
                    ask          = c.Ask,
                    mid          = c.Mid,
                    spreadPct    = c.SpreadPct == decimal.MaxValue ? (decimal?)null : Math.Round(c.SpreadPct, 1),
                    delta        = c.Delta,
                    theta        = c.Theta,
                    iv           = c.ImpliedVolatility,
                    volume       = c.Volume,
                    openInterest = c.OpenInterest,
                    extrinsic    = c.ExtrinsicValue,
                    itm          = c.InTheMoney,
                    nonStandard  = c.NonStandard,
                    multiplier   = c.Multiplier,
                    hasBid       = c.HasBid,
                })
                .ToList();

            return new JsonResult(new
            {
                symbol          = chain.Underlying,
                underlyingPrice = chain.UnderlyingPrice,
                expiry          = exp.ToString("yyyy-MM-dd"),
                count           = rows.Count,
                rows,
            });
        }
        catch (Exception ex) { return Fail(ex, symbol); }
    }

    // ── AJAX: analyse a structure ─────────────────────────────────
    // The math deliberately runs server-side in PayoffCalculator rather than being
    // reimplemented in JavaScript. Max loss shown here must be the same number the
    // order-preview gate enforces later — two implementations could disagree.

    public record LegInput(
        string  Symbol,
        string  Right,
        string  Action,
        decimal Strike,
        decimal Premium,
        int     Quantity,
        int     Multiplier);

    public record AnalyzeRequest(
        List<LegInput>? Legs,
        decimal         CommissionPerContract,
        decimal         UnderlyingPrice,
        decimal?        LimitPrice = null);

    public IActionResult OnPostAnalyze([FromBody] AnalyzeRequest req)
    {
        if (req?.Legs is not { Count: > 0 })
            return BadRequest(new { error = "Select at least one leg." });
        if (req.Legs.Count > 8)
            return BadRequest(new { error = "A structure is limited to 8 legs." });

        List<OptionLeg> legs;
        try
        {
            legs = req.Legs.Select(l => new OptionLeg(
                Right:      Enum.Parse<OptionRight>(l.Right, ignoreCase: true),
                Action:     Enum.Parse<LegAction>(l.Action, ignoreCase: true),
                Strike:     l.Strike,
                Premium:    l.Premium,
                Quantity:   Math.Max(1, l.Quantity),
                Multiplier: l.Multiplier > 0 ? l.Multiplier : 100,
                Symbol:     l.Symbol)).ToList();
        }
        catch (Exception ex) { return BadRequest(new { error = $"Malformed leg: {ex.Message}" }); }

        var a = AnalyseAtLimit(legs, req.CommissionPerContract, req.LimitPrice);

        // Plot window is scaled to the structure's own width, never to the underlying's
        // absolute price: a 4-point butterfly on a $766 stock would otherwise be drawn
        // inside a ~$90 window and collapse into an unreadable spike.
        decimal lo = legs.Min(l => l.Strike);
        decimal hi = legs.Max(l => l.Strike);

        decimal width = hi - lo;
        if (width <= 0m) width = hi * 0.05m;   // single-strike structures have no natural width

        decimal pad = width * 0.75m;
        decimal from = lo - pad, to = hi + pad;

        // Keep spot on the chart even when the structure sits well away from it.
        if (req.UnderlyingPrice > 0m)
        {
            from = Math.Min(from, req.UnderlyingPrice - width * 0.25m);
            to   = Math.Max(to,   req.UnderlyingPrice + width * 0.25m);
        }

        // Same basis as the analysis above, so the drawn curve and the stated max loss agree.
        decimal curveCommission = req.CommissionPerContract;
        if (req.LimitPrice is { } lim)
        {
            int contracts = legs.Sum(l => l.Quantity);
            int mult      = legs[0].Multiplier > 0 ? legs[0].Multiplier : 100;
            if (contracts > 0)
                curveCommission += (lim - SchwabOptionOrder.NetPrice(legs))
                                 * mult * SchwabOptionOrder.UnitFactor(legs) / contracts;
        }

        var curve = PayoffCalculator.Curve(
            legs, Math.Max(0m, from), to, steps: 120,
            commissionPerContract: curveCommission);

        return new JsonResult(new
        {
            netDebit        = a.NetDebit,
            maxProfit       = a.ProfitUnbounded ? (decimal?)null : a.MaxProfit,
            maxLoss         = a.LossUnbounded   ? (decimal?)null : a.MaxLoss,
            maxProfitAt     = a.MaxProfitAt,
            maxLossAt       = a.MaxLossAt,
            profitUnbounded = a.ProfitUnbounded,
            lossUnbounded   = a.LossUnbounded,
            breakevens      = a.Breakevens,
            curve           = curve.Select(p => new { x = p.Underlying, y = p.Pnl }),
        });
    }

    // ── AJAX: structures that fit a stated view ───────────────────

    public async Task<IActionResult> OnGetSuggestAsync(
        string symbol, string expiry, decimal target, decimal commission,
        int strikeCount, decimal maxSpreadPct, CancellationToken ct)
    {
        symbol = (symbol ?? "").Trim().ToUpperInvariant();
        if (symbol.Length == 0) return BadRequest(new { error = "Symbol is required." });
        if (!DateOnly.TryParse(expiry, out var exp))
            return BadRequest(new { error = "A valid expiry is required." });
        if (target <= 0m) return BadRequest(new { error = "Enter the price you expect the underlying to reach." });

        if (strikeCount <= 0) strikeCount = 60;
        strikeCount = Math.Min(strikeCount, 200);

        try
        {
            var chain = await SchwabOptionChain.FetchAsync(
                _schwab, symbol, fromDate: exp, toDate: exp,
                strikeCount: strikeCount, httpFactory: _httpFactory, ct: ct);

            var gate = new LiquidityGate(MaxSpreadPct: maxSpreadPct > 0m ? maxSpreadPct : 10m);
            var found = StructureFinder.Find(
                chain, exp.ToDateTime(TimeOnly.MinValue), target, commission, gate);

            return new JsonResult(new
            {
                underlyingPrice = chain.UnderlyingPrice,
                target,
                candidates = found.Select(c => new
                {
                    c.Name,
                    netDebit    = c.NetDebit,
                    maxLoss     = c.MaxLoss,
                    maxProfit   = c.MaxProfit,
                    pnlAtTarget = c.PnlAtTarget,
                    returnOnRisk= c.ReturnOnRisk,
                    breakevens  = c.Breakevens,
                    worstSpread = Math.Round(c.WorstSpreadPct, 1),
                    sensitivity = c.Sensitivity.Select(p => new { x = p.Underlying, y = p.Pnl }),
                    legs        = c.Legs.Select(l => new
                    {
                        symbol     = l.Symbol,
                        right      = l.Right.ToString(),
                        action     = l.Action.ToString(),
                        strike     = l.Strike,
                        premium    = l.Premium,
                        quantity   = l.Quantity,
                        multiplier = l.Multiplier,
                    }),
                }),
            });
        }
        catch (Exception ex) { return Fail(ex, symbol); }
    }

    // ── AJAX: re-quote a set of contracts ─────────────────────────
    // Legs are captured from the chain at click time, so their premiums age as soon as
    // the market moves — and survive an expiry change or a chain reload untouched. Every
    // path that turns a premium into a money figure re-quotes through here first.

    public record QuoteRequest(List<string>? Symbols);

    public async Task<IActionResult> OnPostQuoteLegsAsync([FromBody] QuoteRequest req, CancellationToken ct)
    {
        if (req?.Symbols is not { Count: > 0 })
            return BadRequest(new { error = "No contracts to quote." });
        if (req.Symbols.Count > 8)
            return BadRequest(new { error = "A structure is limited to 8 legs." });

        try
        {
            var quotes = await SchwabOptionChain.FetchQuotesAsync(
                _schwab, req.Symbols, _httpFactory, ct);

            return new JsonResult(new
            {
                quotes = quotes.ToDictionary(
                    kv => kv.Key,
                    kv => new { bid = kv.Value.Bid, ask = kv.Value.Ask }),
                missing = req.Symbols.Where(sym => !quotes.ContainsKey(sym)).ToList(),
            });
        }
        catch (Exception ex) { return Fail(ex, "quotes"); }
    }

    // ── AJAX: preview an order ────────────────────────────────────

    public record OrderRequest(
        List<LegInput>? Legs,
        int             Spreads,
        decimal         CommissionPerContract,
        string?         Underlying = null,
        string?         Structure  = null,
        decimal?        LimitPrice = null,   // net you will pay/receive to open
        decimal?        ExitPrice  = null);  // net you want to receive on the way out

    /// <summary>
    /// Payoff analysis that reflects the price actually being bid, not the screen market.
    /// <para>A limit shifts the whole payoff curve vertically by the difference from the
    /// market net — which is exactly what a per-contract commission does. Folding the
    /// difference into the commission therefore moves max loss, max profit AND the
    /// breakevens correctly, reusing the tested calculator instead of adjusting its
    /// outputs afterwards and getting the breakevens wrong.</para>
    /// </summary>
    private static StructureAnalytics AnalyseAtLimit(
        IReadOnlyList<OptionLeg> legs, decimal commissionPerContract, decimal? limitPrice)
    {
        if (limitPrice is not { } limit) return PayoffCalculator.Analyze(legs, commissionPerContract);

        decimal marketNet   = SchwabOptionOrder.NetPrice(legs);   // per unit
        int     contracts   = legs.Sum(l => l.Quantity);
        int     multiplier  = legs[0].Multiplier > 0 ? legs[0].Multiplier : 100;
        // These legs already carry UnitFactor units, so the per-unit delta scales by it.
        decimal extraCost   = (limit - marketNet) * multiplier * SchwabOptionOrder.UnitFactor(legs);

        return PayoffCalculator.Analyze(
            legs, commissionPerContract + (contracts > 0 ? extraCost / contracts : 0m));
    }

    public async Task<IActionResult> OnPostPreviewOrderAsync([FromBody] OrderRequest req, CancellationToken ct)
    {
        var (legs, error) = BuildLegs(req);
        if (error is not null) return BadRequest(new { error });

        int spreads = Math.Max(1, req!.Spreads);
        var analysis = AnalyseAtLimit(legs!, req.CommissionPerContract, req.LimitPrice);

        decimal? totalMaxLoss = analysis.LossUnbounded ? null : analysis.MaxLoss * spreads;
        if (MaxTradeRisk > 0m && (analysis.LossUnbounded || totalMaxLoss > MaxTradeRisk))
            return BadRequest(new
            {
                error = analysis.LossUnbounded
                    ? "Refused: this structure has unlimited downside and a per-trade risk ceiling is set."
                    : $"Refused: max loss {totalMaxLoss:C} exceeds the configured ceiling of {MaxTradeRisk:C}.",
            });

        var payload = SchwabOptionOrder.BuildPayload(
            legs!, spreads, OrderDuration.Day, req.LimitPrice,
            req.ExitPrice is { } xp ? new AttachedExit(xp) : null);

        string? brokerBody = null; bool brokerOk = false;
        if (!string.IsNullOrEmpty(SchwabAccountId))
        {
            try
            {
                var r = await SchwabOptionOrder.PreviewAsync(
                    _schwab, SchwabAccountId, payload, _httpFactory, ct);
                brokerOk = r.Ok; brokerBody = r.Body;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Schwab previewOrder failed");
                brokerBody = ex.Message;
            }
        }

        // ── Round trip, when both ends of the trade are specified ──────
        decimal  entryNet     = req.LimitPrice ?? SchwabOptionOrder.NetPrice(legs!);
        int      contracts    = legs!.Sum(l => l.Quantity) * spreads;
        int      multiplier   = legs[0].Multiplier > 0 ? legs[0].Multiplier : 100;
        decimal  commission   = contracts * req.CommissionPerContract;
        // Three identical puts is three units of a one-leg structure, so the per-unit
        // price is multiplied by three — not by the spread count, which is still 1.
        int      units        = SchwabOptionOrder.TotalUnits(legs!, spreads);

        decimal? targetProfit    = null;
        decimal? returnOnRisk    = null;
        decimal? maxStructureVal = null;
        bool     exitUnreachable = false;

        if (req.ExitPrice is { } exitNet)
        {
            // Commission is charged on the way in AND on the way out.
            decimal entryCost    = entryNet * multiplier * units + commission;
            decimal exitProceeds = exitNet  * multiplier * units - commission;
            targetProfit = exitProceeds - entryCost;

            if (!analysis.LossUnbounded && analysis.MaxLoss > 0m)
                returnOnRisk = targetProfit / (analysis.MaxLoss * spreads) * 100m;

            // A spread cannot be worth more than the distance between its strikes. Asking
            // to sell a 2-wide vertical for 3.00 books a profit that can never be filled —
            // the exit simply rests forever while the position expires around it.
            if (!analysis.ProfitUnbounded)
            {
                var free = PayoffCalculator.Analyze(legs!, 0m);   // no commission, market prices
                maxStructureVal = (free.MaxProfit + SchwabOptionOrder.NetPrice(legs!) * multiplier) / multiplier;
                exitUnreachable = exitNet > maxStructureVal;
            }
        }

        return new JsonResult(new
        {
            spreads,
            units,
            targetProfit,
            returnOnRisk,
            maxStructureValue = maxStructureVal,
            exitUnreachable,
            netPricePerSpread = req.LimitPrice ?? SchwabOptionOrder.NetPrice(legs!),
            marketNetPerSpread = SchwabOptionOrder.NetPrice(legs!),
            limitPrice        = req.LimitPrice,
            exitPrice         = req.ExitPrice,
            netDebitPerSpread = analysis.NetDebit,
            totalNet          = analysis.NetDebit * spreads,
            totalMaxLoss,
            totalMaxProfit    = analysis.ProfitUnbounded ? (decimal?)null : analysis.MaxProfit * spreads,
            lossUnbounded     = analysis.LossUnbounded,
            profitUnbounded   = analysis.ProfitUnbounded,
            breakevens        = analysis.Breakevens,
            contracts         = legs!.Sum(l => l.Quantity) * spreads,
            payload,
            brokerOk,
            brokerBody,
            liveEnabled       = LiveOrdersEnabled,
        });
    }

    // ── AJAX: place the order ─────────────────────────────────────

    public async Task<IActionResult> OnPostPlaceOrderAsync([FromBody] OrderRequest req, CancellationToken ct)
    {
        if (!LiveOrdersEnabled)
            return BadRequest(new
            {
                error = "Live orders are disabled. Set Options:AllowLiveOrders to true in configuration to enable placement.",
            });
        if (string.IsNullOrEmpty(SchwabAccountId))
            return BadRequest(new { error = "Schwab:AccountId is not configured." });

        var (legs, error) = BuildLegs(req);
        if (error is not null) return BadRequest(new { error });

        int spreads  = Math.Max(1, req!.Spreads);
        var analysis = AnalyseAtLimit(legs!, req.CommissionPerContract, req.LimitPrice);

        // The ceiling is re-checked here, not just at preview — the two calls are
        // separate requests and only this one reaches the market.
        decimal? totalMaxLoss = analysis.LossUnbounded ? null : analysis.MaxLoss * spreads;
        if (MaxTradeRisk > 0m && (analysis.LossUnbounded || totalMaxLoss > MaxTradeRisk))
            return BadRequest(new { error = $"Refused: max loss exceeds the configured ceiling of {MaxTradeRisk:C}." });

        var payload = SchwabOptionOrder.BuildPayload(
            legs!, spreads, OrderDuration.Day, req.LimitPrice,
            req.ExitPrice is { } xpp ? new AttachedExit(xpp) : null);
        try
        {
            var r = await SchwabOptionOrder.PlaceAsync(_schwab, SchwabAccountId, payload, _httpFactory, ct);
            _log.LogInformation("Option order {Status}: {OrderId} {Body}",
                r.Ok ? "placed" : "rejected", r.OrderId, r.Body);

            await RecordAsync(new OptionOrderRecord
            {
                OrderId    = r.OrderId,
                Underlying = req.Underlying ?? "",
                Structure  = req.Structure  ?? "",
                Intent     = "Open",
                Spreads    = spreads,
                OrderType  = payload["orderType"].ToString() ?? "",
                NetPrice   = req.LimitPrice ?? SchwabOptionOrder.NetPrice(legs!),
                TotalNet   = analysis.NetDebit * spreads,
                MaxLoss    = analysis.LossUnbounded   ? null : analysis.MaxLoss   * spreads,
                MaxProfit  = analysis.ProfitUnbounded ? null : analysis.MaxProfit * spreads,
                Breakevens = string.Join(",", analysis.Breakevens),
                LegsJson   = JsonSerializer.Serialize(payload["orderLegCollection"]),
                Accepted   = r.Ok,
                Error      = r.Ok ? null : Trim(r.Body, 1000),
            }, ct);

            return new JsonResult(new { ok = r.Ok, orderId = r.OrderId, body = r.Body });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Option order placement failed");
            return new JsonResult(new { error = ex.Message });
        }
    }

    // ── AJAX: open option positions ───────────────────────────────

    public async Task<IActionResult> OnGetPositionsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(SchwabAccountId))
            return new JsonResult(new { error = "Schwab:AccountId is not configured." });
        try
        {
            var all = await ManualBrokerOps.GetPositionsSchwabAsync(_schwab, SchwabAccountId, _httpFactory);
            var opts = all.Where(p => string.Equals(p.AssetType, "OPTION", StringComparison.OrdinalIgnoreCase))
                          .Select(p => new
                          {
                              symbol      = p.Symbol,
                              description = p.Description,
                              direction   = p.Direction,
                              quantity    = p.Quantity,
                              avgPrice    = p.AveragePrice,
                              costBasis   = p.CostBasis,
                              unrealized  = p.UnrealizedPnl,
                              dayPnl      = p.DayPnl,
                              multiplier  = p.Multiplier,
                          })
                          .ToList();
            return new JsonResult(new { positions = opts });
        }
        catch (Exception ex) { return Fail(ex, "positions"); }
    }

    // ── AJAX: preview / place a closing order ─────────────────────

    public record ClosePosition(string Symbol, string Direction, int Quantity);
    public record CloseRequest(List<ClosePosition>? Positions, decimal CommissionPerContract, bool Place);

    public async Task<IActionResult> OnPostCloseAsync([FromBody] CloseRequest req, CancellationToken ct)
    {
        if (req?.Positions is not { Count: > 0 })
            return BadRequest(new { error = "Select at least one position to close." });
        if (req.Positions.Count > 8)
            return BadRequest(new { error = "A closing order is limited to 8 legs." });
        if (req.Place && !LiveOrdersEnabled)
            return BadRequest(new { error = "Live orders are disabled. Set Options:AllowLiveOrders to true to enable placement." });

        Dictionary<string, SchwabOptionChain.OptionQuote> quotes;
        try
        {
            quotes = await SchwabOptionChain.FetchQuotesAsync(
                _schwab, req.Positions.Select(p => p.Symbol).ToList(), _httpFactory, ct);
        }
        catch (Exception ex) { return Fail(ex, "quotes"); }

        var legs = new List<OptionLeg>();
        foreach (var pos in req.Positions)
        {
            if (!quotes.TryGetValue(pos.Symbol, out var q))
                return BadRequest(new { error = $"No live quote for {pos.Symbol}; cannot price a close." });

            bool isLong = string.Equals(pos.Direction, "LONG", StringComparison.OrdinalIgnoreCase);

            // Price the side we will actually trade when closing: a long leg is sold
            // (hit the bid), a short leg is bought back (lift the ask).
            decimal premium = isLong ? q.Bid : q.Ask;
            if (premium <= 0m && isLong)
                return BadRequest(new { error = $"{pos.Symbol} has no bid — it cannot be sold at any price right now." });

            legs.Add(new OptionLeg(
                Right:    q.Right,
                Action:   isLong ? LegAction.Buy : LegAction.Sell,   // the position as opened
                Strike:   q.Strike,
                Premium:  premium,
                Quantity: Math.Max(1, pos.Quantity),
                Multiplier: 100,
                Symbol:   pos.Symbol));
        }

        var payload = SchwabOptionOrder.BuildClosePayload(legs);

        decimal proceeds = SchwabOptionOrder.CloseProceeds(legs, req.CommissionPerContract);

        if (!req.Place)
        {
            string? brokerBody = null; bool brokerOk = false;
            try
            {
                var pr = await SchwabOptionOrder.PreviewAsync(_schwab, SchwabAccountId, payload, _httpFactory, ct);
                brokerOk = pr.Ok; brokerBody = pr.Body;
            }
            catch (Exception ex) { brokerBody = ex.Message; }

            return new JsonResult(new
            {
                payload, proceeds, brokerOk, brokerBody, liveEnabled = LiveOrdersEnabled,
            });
        }

        try
        {
            var r = await SchwabOptionOrder.PlaceAsync(_schwab, SchwabAccountId, payload, _httpFactory, ct);
            _log.LogInformation("Option close {Status}: {OrderId} {Body}",
                r.Ok ? "placed" : "rejected", r.OrderId, r.Body);

            await RecordAsync(new OptionOrderRecord
            {
                OrderId    = r.OrderId,
                Underlying = legs.Count > 0 ? UnderlyingOf(legs[0].Symbol) : "",
                Structure  = $"{legs.Count}-leg close",
                Intent     = "Close",
                Spreads    = 1,
                OrderType  = payload["orderType"].ToString() ?? "",
                NetPrice   = SchwabOptionOrder.NetPrice(legs),
                TotalNet   = -proceeds,   // TotalNet is a cost; proceeds is cash received
                LegsJson   = JsonSerializer.Serialize(payload["orderLegCollection"]),
                Accepted   = r.Ok,
                Error      = r.Ok ? null : Trim(r.Body, 1000),
            }, ct);

            return new JsonResult(new { ok = r.Ok, orderId = r.OrderId, body = r.Body });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Option close placement failed");
            return new JsonResult(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Write the local record of a submitted structure. Never throws into the caller:
    /// the order has already reached the broker at this point, so a bookkeeping failure
    /// must not be reported to the user as a failed order.
    /// </summary>
    private async Task RecordAsync(OptionOrderRecord rec, CancellationToken ct)
    {
        try
        {
            _db.OptionOrders.Add(rec);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist option order record for {OrderId}", rec.OrderId);
        }
    }

    /// <summary>Root symbol from an OSI string: the first six characters, space padded.</summary>
    private static string UnderlyingOf(string osi)
        => osi.Length >= 6 ? osi[..6].Trim() : osi.Trim();

    private static string Trim(string? s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];

    // ── AJAX: recent locally recorded structures ──────────────────

    public async Task<IActionResult> OnGetRecentOrdersAsync(CancellationToken ct)
    {
        var rows = await _db.OptionOrders.AsNoTracking()
            .OrderByDescending(o => o.PlacedAt)
            .Take(20)
            .Select(o => new
            {
                o.PlacedAt, o.OrderId, o.Underlying, o.Structure, o.Intent,
                o.Spreads, o.OrderType, o.TotalNet, o.MaxLoss, o.Accepted, o.Error,
            })
            .ToListAsync(ct);
        return new JsonResult(new { orders = rows });
    }

    private (List<OptionLeg>? Legs, string? Error) BuildLegs(OrderRequest? req)
    {
        if (req?.Legs is not { Count: > 0 }) return (null, "Select at least one leg.");
        if (req.Legs.Count > 8)             return (null, "A structure is limited to 8 legs.");
        if (req.Legs.Any(l => string.IsNullOrWhiteSpace(l.Symbol)))
            return (null, "A leg is missing its contract symbol.");
        try
        {
            return (req.Legs.Select(l => new OptionLeg(
                Right:      Enum.Parse<OptionRight>(l.Right, ignoreCase: true),
                Action:     Enum.Parse<LegAction>(l.Action, ignoreCase: true),
                Strike:     l.Strike,
                Premium:    l.Premium,
                Quantity:   Math.Max(1, l.Quantity),
                Multiplier: l.Multiplier > 0 ? l.Multiplier : 100,
                Symbol:     l.Symbol)).ToList(), null);
        }
        catch (Exception ex) { return (null, $"Malformed leg: {ex.Message}"); }
    }

    private IActionResult Fail(Exception ex, string symbol)
    {
        _log.LogWarning(ex, "Option chain fetch failed for {Symbol}", symbol);

        bool authProblem = ex is InvalidOperationException
                        || ex.Message.Contains("401")
                        || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);

        return new JsonResult(new
        {
            error   = authProblem
                ? "Schwab authorization has expired. Reconnect, then try again."
                : ex.Message,
            reauth  = authProblem,
        });
    }
}
