using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CRV.Core.Options;

namespace CRV.Live.Brokers.Schwab;

/// <summary>How long the order stays working.</summary>
public enum OrderDuration { Day, GoodTillCancel, FillOrKill }

/// <summary>
/// A closing order submitted automatically once the entry fills — Schwab's
/// One-Triggers-Other.
/// <para><paramref name="NetPrice"/> is the per-spread cash you want to RECEIVE on the way
/// out: "bought it for 1, sell it at 3" is <c>new AttachedExit(3m)</c>. Note this is the
/// opposite sign convention to an entry limit, where positive means cash paid — an exit is
/// stated the way a trader states it, not the way the entry is.</para>
/// </summary>
public record AttachedExit(decimal NetPrice, OrderDuration Duration = OrderDuration.GoodTillCancel);

/// <summary>
/// Builds Schwab option order payloads.
/// <para>A spread is always ONE order priced as a net debit or credit. Legging in
/// separately risks a partial fill that leaves a naked short option in place of a
/// defined-risk structure — the difference between a capped loss and an open-ended
/// one — so this type has no per-leg submission path at all.</para>
/// </summary>
public static class SchwabOptionOrder
{
    /// <summary>
    /// Greatest common divisor of the leg quantities — the factor by which the structure
    /// is already being traded more than once.
    /// <para>Three identical puts is one put traded three times, not a three-legged
    /// structure. A 1:2:1 butterfly is genuinely one structure. Telling these apart is
    /// what keeps the quoted price per unit.</para>
    /// </summary>
    public static int UnitFactor(IReadOnlyList<OptionLeg> legs)
    {
        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
        return legs.Count == 0 ? 1 : legs.Select(l => Math.Max(1, l.Quantity)).Aggregate(Gcd);
    }

    /// <summary>Leg ratios with the common factor divided out: 3 puts becomes 1, 3:6:3 becomes 1:2:1.</summary>
    private static List<OptionLeg> Reduce(IReadOnlyList<OptionLeg> legs)
    {
        int f = UnitFactor(legs);
        return f <= 1 ? legs.ToList()
                      : legs.Select(l => l with { Quantity = l.Quantity / f }).ToList();
    }

    /// <summary>
    /// Net premium for ONE unit of the structure: positive is a debit you pay, negative a
    /// credit you receive. Excludes commission — the broker prices the order on premium alone.
    /// <para>This is per unit, not per position. Schwab multiplies it by the leg quantities
    /// it is sent, so folding the quantity in here would bill the order twice over: three
    /// puts at 3.17 quoted as 9.51 previews at $2,853 instead of $951.</para>
    /// </summary>
    public static decimal NetPrice(IReadOnlyList<OptionLeg> legs)
        => Reduce(legs).Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.Premium * l.Quantity);

    /// <summary>Total units traded: the spread count times the factor already in the leg quantities.</summary>
    public static int TotalUnits(IReadOnlyList<OptionLeg> legs, int spreads)
        => Math.Max(1, spreads) * UnitFactor(legs);

    /// <summary>
    /// Signed cash effect of closing an open structure, in dollars.
    /// <para>POSITIVE means the close pays you; NEGATIVE means you pay to get out. A long
    /// leg is sold (cash in) and a short leg is bought back (cash out), so a spread whose
    /// short leg is worth more than its long one costs money to close. Reporting that as
    /// a credit is the failure this exists to prevent.</para>
    /// <para><paramref name="openLegs"/> carries CURRENT market premiums.</para>
    /// </summary>
    public static decimal CloseProceeds(
        IReadOnlyList<OptionLeg> openLegs, decimal commissionPerContract = 0m)
        => openLegs.Sum(l => (l.Action == LegAction.Buy ? 1m : -1m) * l.Premium * l.Quantity * l.Multiplier)
         - openLegs.Sum(l => l.Quantity) * commissionPerContract;

    /// <summary>
    /// Build a CLOSING order for an already-open structure.
    /// <para><paramref name="openLegs"/> describes the position as it was opened, but
    /// carrying CURRENT market premiums — the closing price comes from today's market,
    /// not from what you paid. Each leg's side is inverted and marked to-close, so a
    /// structure opened for a debit closes for a credit.</para>
    /// </summary>
    public static Dictionary<string, object> BuildClosePayload(
        IReadOnlyList<OptionLeg> openLegs,
        int spreads = 1,
        OrderDuration duration = OrderDuration.Day)
    {
        if (openLegs is null || openLegs.Count == 0)
            throw new ArgumentException("A structure needs at least one leg.", nameof(openLegs));

        var inverted = openLegs
            .Select(l => l with { Action = l.Action == LegAction.Buy ? LegAction.Sell : LegAction.Buy })
            .ToList();

        return Build(inverted, spreads, duration, closing: true);
    }

    /// <summary>
    /// Build the order body. <paramref name="spreads"/> is how many times the whole
    /// structure is traded: each leg's quantity is its ratio multiplied by that count.
    /// </summary>
    /// <param name="limitPrice">
    /// Overrides the price derived from the current market. This is how you say "the ask
    /// is 2.00 but I will only pay 1.00" — the order rests until the market comes to you.
    /// Sign is taken from this value, not from the market, so a limit can turn a
    /// would-be debit into a credit order and be labelled correctly.
    /// </param>
    /// <param name="exit">
    /// Optional take-profit submitted only once the entry fills.
    /// </param>
    public static Dictionary<string, object> BuildPayload(
        IReadOnlyList<OptionLeg> legs,
        int spreads = 1,
        OrderDuration duration = OrderDuration.Day,
        decimal? limitPrice = null,
        AttachedExit? exit = null)
    {
        var order = Build(legs, spreads, duration, closing: false, limitPrice);
        if (exit is null) return order;

        // One-Triggers-Other: the child is the closing structure, and Schwab only
        // releases it when the parent fills. Attaching the exit at entry time is the
        // point — an exit you have to remember to place is one you can forget.
        var child = Build(
            legs.Select(l => l with { Action = l.Action == LegAction.Buy ? LegAction.Sell : LegAction.Buy })
                .ToList(),
            spreads, exit.Duration, closing: true,
            // Negated: the exit is stated as cash received, while Build reads a positive
            // limit as cash paid. Without this, "sell at 3" submits as a 3.00 DEBIT.
            limitPrice: -exit.NetPrice);

        order["orderStrategyType"]    = "TRIGGER";
        order["childOrderStrategies"] = new List<Dictionary<string, object>> { child };
        return order;
    }

    private static Dictionary<string, object> Build(
        IReadOnlyList<OptionLeg> legs, int spreads, OrderDuration duration, bool closing,
        decimal? limitPrice = null)
    {
        if (legs is null || legs.Count == 0)
            throw new ArgumentException("A structure needs at least one leg.", nameof(legs));
        if (spreads <= 0)
            throw new ArgumentOutOfRangeException(nameof(spreads), spreads, "Spread count must be positive.");

        int factor  = UnitFactor(legs);
        var ratios  = Reduce(legs);
        int units   = spreads * factor;
        decimal net = limitPrice ?? NetPrice(legs);

        legs    = ratios;
        spreads = units;

        // Single legs price as a plain limit; anything multi-leg is a net order so the
        // structure fills as a unit or not at all. Market is never emitted.
        string orderType = legs.Count == 1
            ? "LIMIT"
            : net > 0m ? "NET_DEBIT"
            : net < 0m ? "NET_CREDIT"
            : "NET_ZERO";

        return new Dictionary<string, object>
        {
            ["orderStrategyType"]  = "SINGLE",
            ["orderType"]          = orderType,
            ["session"]            = "NORMAL",
            ["duration"]           = duration switch
            {
                OrderDuration.GoodTillCancel => "GOOD_TILL_CANCEL",
                OrderDuration.FillOrKill     => "FILL_OR_KILL",
                _                            => "DAY",
            },
            ["price"]              = Math.Abs(net),   // Schwab takes the magnitude; orderType carries the sign
            ["orderLegCollection"] = legs.Select(l => new Dictionary<string, object>
            {
                ["instruction"] = (l.Action, closing) switch
                {
                    (LegAction.Buy,  false) => "BUY_TO_OPEN",
                    (LegAction.Sell, false) => "SELL_TO_OPEN",
                    (LegAction.Buy,  true)  => "BUY_TO_CLOSE",
                    _                       => "SELL_TO_CLOSE",
                },
                ["quantity"]    = l.Quantity * spreads,
                ["instrument"]  = new Dictionary<string, object>
                {
                    // Verbatim from the chain response. Never rebuilt — a malformed OSI
                    // string can address a valid but entirely different contract.
                    ["symbol"]    = l.Symbol,
                    ["assetType"] = "OPTION",
                },
            }).ToList(),
        };
    }

    // ── transport ────────────────────────────────────────────────

    /// <summary>Result of a preview or placement call.</summary>
    public record OrderResult(bool Ok, string Body, string? OrderId = null);

    /// <summary>
    /// Ask Schwab to validate and cost the order without submitting it.
    /// </summary>
    public static Task<OrderResult> PreviewAsync(
        SchwabAuthService auth, string accountId, Dictionary<string, object> payload,
        IHttpClientFactory? httpFactory = null, CancellationToken ct = default)
        => PostAsync(auth, $"/trader/v1/accounts/{accountId}/previewOrder", payload, httpFactory, ct);

    /// <summary>
    /// Submit the order for real. Callers are responsible for gating this behind an
    /// explicit confirmation — nothing in this method asks whether you meant it.
    /// </summary>
    public static async Task<OrderResult> PlaceAsync(
        SchwabAuthService auth, string accountId, Dictionary<string, object> payload,
        IHttpClientFactory? httpFactory = null, CancellationToken ct = default)
    {
        var r = await PostAsync(auth, $"/trader/v1/accounts/{accountId}/orders", payload, httpFactory, ct, wantLocation: true);
        return r;
    }

    private static async Task<OrderResult> PostAsync(
        SchwabAuthService auth, string path, Dictionary<string, object> payload,
        IHttpClientFactory? httpFactory, CancellationToken ct, bool wantLocation = false)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = httpFactory?.CreateClient("Schwab") ?? new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var res  = await http.PostAsync(auth.ApiBaseUrl + path, content, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        string? orderId = null;
        if (wantLocation && res.Headers.Location is { } loc)
            orderId = loc.Segments.LastOrDefault()?.Trim('/');

        return new OrderResult(res.IsSuccessStatusCode, body, orderId);
    }

    /// <summary>Fetch one order's full document, for reading back what it executed at.</summary>
    public static async Task<string?> FetchOrderAsync(
        SchwabAuthService auth, string accountId, string orderId,
        IHttpClientFactory? httpFactory = null, CancellationToken ct = default)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = httpFactory?.CreateClient("Schwab") ?? new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await http.GetAsync($"{auth.ApiBaseUrl}/trader/v1/accounts/{accountId}/orders/{orderId}", ct);
        return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(ct) : null;
    }

    // ── realized execution ───────────────────────────────────────

    /// <summary>What an order actually filled at, as opposed to what it asked for.</summary>
    /// <param name="NetPerUnit">
    /// Signed net premium per unit of the structure: positive is a debit paid, negative a
    /// credit received — the same convention as <see cref="NetPrice"/>, so subtracting the
    /// two gives slippage directly.
    /// </param>
    public record RealizedFill(decimal NetPerUnit, int UnitsFilled, DateTime? FilledAtUtc);

    /// <summary>
    /// Extract the realized fill from a Schwab order document, or null if it has not filled.
    /// <para>Execution legs report a price per contract keyed by <c>legId</c>. The side and
    /// the ratio have to come from the order's own leg collection — an execution price alone
    /// does not say whether it was paid or received, nor how many of it make one spread.</para>
    /// </summary>
    public static RealizedFill? ParseRealizedFill(string orderJson)
    {
        using var doc = JsonDocument.Parse(orderJson);
        var root = doc.RootElement;

        static decimal Num(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
               ? v.GetDecimal() : 0m;

        // legId → side and ordered quantity.
        var legs = new Dictionary<int, (decimal Sign, decimal Qty)>();
        if (root.TryGetProperty("orderLegCollection", out var legCol) &&
            legCol.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in legCol.EnumerateArray())
            {
                int id = (int)Num(l, "legId");
                var instr = l.TryGetProperty("instruction", out var i) ? i.GetString() ?? "" : "";
                legs[id] = (instr.StartsWith("BUY", StringComparison.Ordinal) ? 1m : -1m, Num(l, "quantity"));
            }
        }
        if (legs.Count == 0) return null;

        // Ratios: leg quantities divided by their common factor, so results are per unit.
        int gcd = legs.Values.Select(v => (int)Math.Abs(v.Qty)).Where(q => q > 0).DefaultIfEmpty(1).Aggregate(Gcd);
        if (gcd <= 0) gcd = 1;

        // Quantity-weighted average execution price per leg — a leg can fill in pieces.
        var filledQty   = new Dictionary<int, decimal>();
        var weightedSum = new Dictionary<int, decimal>();
        DateTime? filledAt = null;

        if (root.TryGetProperty("orderActivityCollection", out var acts) &&
            acts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in acts.EnumerateArray())
            {
                if (a.TryGetProperty("executionType", out var et) &&
                    !string.Equals(et.GetString(), "FILL", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!a.TryGetProperty("executionLegs", out var xl) || xl.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var x in xl.EnumerateArray())
                {
                    int id = (int)Num(x, "legId");
                    if (!legs.ContainsKey(id)) continue;

                    decimal qty = Num(x, "quantity"), price = Num(x, "price");
                    if (qty <= 0m) continue;

                    filledQty[id]   = filledQty.GetValueOrDefault(id) + qty;
                    weightedSum[id] = weightedSum.GetValueOrDefault(id) + qty * price;

                    if (x.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                                                DateTimeStyles.RoundtripKind, out var dto) &&
                        (filledAt is null || dto.UtcDateTime > filledAt))
                        filledAt = dto.UtcDateTime;
                }
            }
        }

        if (filledQty.Count == 0) return null;

        decimal net = 0m;
        decimal units = decimal.MaxValue;
        foreach (var (id, leg) in legs)
        {
            if (!filledQty.TryGetValue(id, out var qty) || qty <= 0m) return null;   // partial across legs

            decimal ratio = leg.Qty / gcd;
            net   += leg.Sign * (weightedSum[id] / qty) * ratio;
            units  = Math.Min(units, ratio > 0m ? qty / ratio : 0m);
        }

        return new RealizedFill(net, (int)Math.Round(units), filledAt);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
