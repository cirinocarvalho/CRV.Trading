namespace CRV.Core.Options;

/// <summary>One open option leg, marked to the current market.</summary>
public sealed record OptionPositionLeg(
    string      Symbol,
    string      Underlying,
    OptionRight Right,
    decimal     Strike,
    DateTime    Expiration,
    bool        IsLong,
    int         Quantity,
    int         Multiplier,
    decimal     Mid,
    decimal     Delta,
    decimal     Gamma,
    decimal     Theta,
    decimal     Vega);

/// <summary>
/// Exposure across every open option position.
/// </summary>
/// <param name="LongPremiumAtRisk">
/// What the long legs are currently worth — the most they can lose, since an option cannot
/// go below zero. Short legs are excluded because their loss is not bounded by any figure
/// derivable from a leg in isolation.
/// </param>
/// <param name="UnpairedShorts">
/// Short legs with no long leg of the same right and expiry to cap them. A long option caps
/// a short one of the same right and expiry whatever the strikes, because beyond the outer
/// strike the two move one-for-one; without one, the loss is open-ended.
/// </param>
public sealed record PortfolioRisk(
    decimal NetDeltaDollars,
    decimal NetGammaDollars,
    decimal NetThetaDollars,
    decimal NetVegaDollars,
    decimal LongPremiumAtRisk,
    IReadOnlyList<string> UnpairedShorts,
    int     LegCount)
{
    /// <summary>True when at least one short leg has nothing capping it.</summary>
    public bool HasUnboundedRisk => UnpairedShorts.Count > 0;
}

/// <summary>
/// Aggregates open option legs into portfolio exposure.
/// <para>Per-trade limits do not catch the case that actually hurts: many individually
/// compliant positions pointing the same way. These figures are what a portfolio limit
/// can be applied to.</para>
/// </summary>
public static class PortfolioRiskCalculator
{
    public static PortfolioRisk Aggregate(IReadOnlyList<OptionPositionLeg> legs)
    {
        if (legs.Count == 0)
            return new PortfolioRisk(0m, 0m, 0m, 0m, 0m, [], 0);

        decimal Sum(Func<OptionPositionLeg, decimal> greek)
            => legs.Sum(l => (l.IsLong ? 1m : -1m) * greek(l) * l.Quantity * l.Multiplier);

        decimal longPremium = legs
            .Where(l => l.IsLong)
            .Sum(l => l.Mid * l.Quantity * l.Multiplier);

        // A long option of the same right and expiry caps a short one regardless of strike,
        // because past the outer strike both move one-for-one. Grouping by expiry matters:
        // a long put in a later cycle does not protect a short put through this one.
        var unpaired = legs
            .GroupBy(l => (l.Underlying, l.Expiration.Date, l.Right))
            .Where(g => g.Where(l => !l.IsLong).Sum(l => l.Quantity)
                      > g.Where(l =>  l.IsLong).Sum(l => l.Quantity))
            .SelectMany(g => g.Where(l => !l.IsLong).Select(l => l.Symbol))
            .Distinct()
            .ToList();

        return new PortfolioRisk(
            NetDeltaDollars:   Sum(l => l.Delta),
            NetGammaDollars:   Sum(l => l.Gamma),
            NetThetaDollars:   Sum(l => l.Theta),
            NetVegaDollars:    Sum(l => l.Vega),
            LongPremiumAtRisk: longPremium,
            UnpairedShorts:    unpaired,
            LegCount:          legs.Count);
    }
}
