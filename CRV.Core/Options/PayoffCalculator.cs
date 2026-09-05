namespace CRV.Core.Options;

public enum OptionRight { Call, Put }
public enum LegAction   { Buy, Sell }

/// <summary>
/// One leg of an option structure.
/// <para><paramref name="Premium"/> is per share (e.g. 2.50), not per contract.</para>
/// <para><paramref name="Symbol"/> is the opaque OSI string returned by the broker's
/// chain response. It is carried through verbatim and never constructed locally —
/// a hand-built symbol can silently address a different contract.</para>
/// </summary>
public sealed record OptionLeg(
    OptionRight Right,
    LegAction   Action,
    decimal     Strike,
    decimal     Premium,
    int         Quantity   = 1,
    int         Multiplier = 100,
    string      Symbol     = "",
    /// <summary>Implied volatility as a percentage, as the chain reports it. Only needed to
    /// value the leg before expiration.</summary>
    decimal     ImpliedVolatility = 0m,
    /// <summary>Expiration date. Only needed to value the leg before expiration.</summary>
    DateTime    Expiration = default);

/// <summary>
/// Expiration-payoff properties of a structure. All money values are in dollars
/// (already multiplied by contract multiplier and quantity).
/// </summary>
/// <param name="NetDebit">Positive = you pay (debit). Negative = you receive (credit). Includes commission.</param>
/// <param name="MaxProfit">Best case in dollars. <see cref="decimal.MaxValue"/> when <paramref name="ProfitUnbounded"/>.</param>
/// <param name="MaxLoss">Worst case as a POSITIVE dollar amount at risk. <see cref="decimal.MaxValue"/> when <paramref name="LossUnbounded"/>.</param>
/// <param name="Breakevens">Underlying prices where payoff crosses zero, ascending.</param>
/// <param name="MaxProfitAt">
/// Underlying price at which <paramref name="MaxProfit"/> occurs, or null when the profit
/// is unbounded. A long put's best case is at an underlying of zero — a true bound, but a
/// meaningless target unless the figure says where it lives.
/// </param>
/// <param name="MaxLossAt">
/// Underlying price at which <paramref name="MaxLoss"/> occurs, or null when unbounded.
/// </param>
public sealed record StructureAnalytics(
    decimal NetDebit,
    decimal MaxProfit,
    decimal MaxLoss,
    bool    ProfitUnbounded,
    bool    LossUnbounded,
    IReadOnlyList<decimal> Breakevens,
    decimal? MaxProfitAt = null,
    decimal? MaxLossAt   = null);

/// <summary>One point on a payoff curve.</summary>
public readonly record struct PayoffPoint(decimal Underlying, decimal Pnl);

/// <summary>
/// Expiration payoff math for multi-leg option structures. Pure functions — no
/// broker, no network, no state. Used by the order-preview gate to show max loss
/// before anything is placed.
/// </summary>
public static class PayoffCalculator
{
    /// <summary>Structure P&amp;L in dollars if the underlying settles at <paramref name="underlying"/>.</summary>
    public static decimal PayoffAt(
        IReadOnlyList<OptionLeg> legs, decimal underlying, decimal commissionPerContract = 0m)
    {
        decimal total = 0m;
        foreach (var leg in legs)
        {
            decimal intrinsic = leg.Right == OptionRight.Call
                ? Math.Max(underlying - leg.Strike, 0m)
                : Math.Max(leg.Strike - underlying, 0m);
            total += Sign(leg) * (intrinsic - leg.Premium) * leg.Quantity * leg.Multiplier;
        }
        return total - Commission(legs, commissionPerContract);
    }

    /// <summary>Net debit/credit, max profit/loss, and breakevens for the structure.</summary>
    public static StructureAnalytics Analyze(
        IReadOnlyList<OptionLeg> legs, decimal commissionPerContract = 0m)
    {
        decimal netDebit = legs.Sum(l => Sign(l) * l.Premium * l.Quantity * l.Multiplier)
                         + Commission(legs, commissionPerContract);

        // The payoff is piecewise linear with kinks at the strikes, so every extreme
        // and every zero-crossing lies at a strike, at underlying = 0, or on the ray
        // above the highest strike. Underlying cannot go below 0, so the downside is
        // always bounded; only the upside ray can run away.
        var points = legs.Select(l => l.Strike).Append(0m).Distinct().OrderBy(s => s).ToList();
        var pnl    = points.Select(s => PayoffAt(legs, s, commissionPerContract)).ToList();

        // Dollars gained per $1 of underlying above the highest strike: puts are dead
        // there, so only calls contribute.
        decimal slopeAbove = legs
            .Where(l => l.Right == OptionRight.Call)
            .Sum(l => Sign(l) * l.Quantity * l.Multiplier);

        bool profitUnbounded = slopeAbove > 0m;
        bool lossUnbounded   = slopeAbove < 0m;

        int best  = pnl.IndexOf(pnl.Max());
        int worst = pnl.IndexOf(pnl.Min());

        decimal maxProfit = profitUnbounded ? decimal.MaxValue : pnl[best];
        decimal maxLoss   = lossUnbounded   ? decimal.MaxValue : -pnl[worst];

        return new StructureAnalytics(
            netDebit, maxProfit, maxLoss, profitUnbounded, lossUnbounded,
            Breakevens(points, pnl, slopeAbove),
            MaxProfitAt: profitUnbounded ? null : points[best],
            MaxLossAt:   lossUnbounded   ? null : points[worst]);
    }

    /// <summary>
    /// Structure P&amp;L in dollars if the underlying is at <paramref name="underlying"/> on
    /// <paramref name="asOf"/> — before expiration, where remaining time value still counts.
    /// <para>Deliberately parallel to <see cref="PayoffAt"/>: the only difference is that a
    /// leg is worth its Black-Scholes value rather than its intrinsic value. As
    /// <paramref name="asOf"/> approaches expiry the two converge, which is what makes the
    /// expiration figure a special case of this one rather than a different quantity.</para>
    /// <para>Returns null when any leg lacks the volatility or expiry needed to value it —
    /// never a number that looks computed but is not.</para>
    /// </summary>
    /// <param name="rate">Risk-free rate as a decimal (0.04 = 4%).</param>
    /// <param name="ivShiftPoints">
    /// Volatility points added to every leg. Implied volatility does not stay put — it
    /// typically falls as equities rise and collapses after events — so the honest use of
    /// this model is to look at a range, not a point.
    /// </param>
    public static decimal? ValueAt(
        IReadOnlyList<OptionLeg> legs, decimal underlying, DateTime asOf,
        decimal rate = 0.04m, decimal commissionPerContract = 0m, decimal ivShiftPoints = 0m)
    {
        decimal total = 0m;
        foreach (var leg in legs)
        {
            if (leg.Expiration == default) return null;

            double vol = (double)(leg.ImpliedVolatility + ivShiftPoints) / 100d;
            if (vol <= 0d) return null;

            double years = (leg.Expiration - asOf).TotalDays / 365d;

            double value = BlackScholes.Price(
                leg.Right, (double)underlying, (double)leg.Strike, years, (double)rate, vol);

            total += Sign(leg) * ((decimal)value - leg.Premium) * leg.Quantity * leg.Multiplier;
        }
        return total - Commission(legs, commissionPerContract);
    }

    /// <summary>
    /// Payoff sampled across a range of underlying prices, for plotting.
    /// <para>Uniform samples alone round off the corners: a butterfly's peak sits exactly
    /// on a strike and a uniform grid will usually step over it, drawing a blunted tent
    /// that understates the best case. Every strike and every breakeven inside the range
    /// is therefore added as an explicit point.</para>
    /// </summary>
    public static IReadOnlyList<PayoffPoint> Curve(
        IReadOnlyList<OptionLeg> legs, decimal from, decimal to,
        int steps = 100, decimal commissionPerContract = 0m)
    {
        if (to < from) (from, to) = (to, from);
        if (steps < 1) steps = 1;

        var xs = new SortedSet<decimal> { from, to };

        decimal span = to - from;
        for (int i = 1; i < steps; i++)
            xs.Add(from + span * i / steps);

        // The corners of the payoff and its zero-crossings must be sampled exactly.
        foreach (var strike in legs.Select(l => l.Strike))
            if (strike > from && strike < to) xs.Add(strike);

        foreach (var be in Analyze(legs, commissionPerContract).Breakevens)
            if (be > from && be < to) xs.Add(be);

        return xs.Select(x => new PayoffPoint(x, PayoffAt(legs, x, commissionPerContract))).ToList();
    }

    // ── helpers ──────────────────────────────────────────────────

    private static decimal Sign(OptionLeg leg) => leg.Action == LegAction.Buy ? 1m : -1m;

    private static decimal Commission(IReadOnlyList<OptionLeg> legs, decimal perContract)
        => perContract == 0m ? 0m : legs.Sum(l => l.Quantity * perContract);

    private static List<decimal> Breakevens(
        List<decimal> points, List<decimal> pnl, decimal slopeAbove)
    {
        var result = new List<decimal>();

        // Zero-crossings on the finite segments between consecutive kinks.
        for (int i = 0; i < points.Count - 1; i++)
        {
            decimal a = pnl[i], b = pnl[i + 1];
            if (a == 0m) { Add(result, points[i]); continue; }
            if (a < 0m == b < 0m) continue;   // same side of zero — no crossing
            Add(result, points[i] + (points[i + 1] - points[i]) * (-a) / (b - a));
        }
        if (pnl[^1] == 0m) Add(result, points[^1]);

        // The ray above the highest strike, where the slope is constant.
        if (slopeAbove != 0m)
        {
            decimal cross = points[^1] - pnl[^1] / slopeAbove;
            if (cross > points[^1]) Add(result, cross);
        }

        result.Sort();
        return result;
    }

    private static void Add(List<decimal> xs, decimal x)
    {
        if (!xs.Contains(x)) xs.Add(x);
    }
}
