using CRV.Core.Models;

namespace CRV.Core.Risk;

/// <summary>
/// The ceiling between a per-trade risk cap and a daily loss limit.
/// <para>
/// The system had both of those and nothing in between, so five setups could be in
/// the market at once, each individually within budget, with no view of the total.
/// MNQ and MES are not independent risks — one bad opening drive takes both — and
/// treating concurrent positions as unrelated is how a book sized for $200 a trade
/// finds itself risking $1,000 on a single move.
/// </para>
/// <para>
/// Exposure is read from the live group orders rather than from a ledger kept
/// alongside them. A parallel ledger drifts: an entry that never fills leaves risk
/// booked forever and quietly stops the book trading.
/// </para>
/// </summary>
public static class PortfolioExposure
{
    /// <summary>
    /// Dollar risk currently committed across every group that is open or could
    /// still fill.
    /// <para>
    /// Working entries count. Counting only filled positions is how several resting
    /// orders all fill on one move and breach a limit that was never checked against
    /// them — which is precisely the correlated case this exists to catch.
    /// </para>
    /// </summary>
    public static decimal OpenRisk(IEnumerable<GroupOrder> groups) =>
        groups.Where(IsLive).Sum(RiskOf);

    /// <summary>Risk the signal would commit if it filled in full.</summary>
    public static decimal CandidateRisk(EntrySignal signal) =>
        Math.Abs(signal.Entry - signal.Stop) * signal.TotalContracts * signal.PointValue;

    /// <summary>
    /// Whether <paramref name="candidateRisk"/> can be added without exceeding
    /// <paramref name="maxPortfolioRisk"/>. A limit of zero or less disables the gate.
    /// <para>
    /// A candidate with no measurable risk is refused rather than admitted as free:
    /// a signal whose stop equals its entry is malformed, and letting it through
    /// unmeasured defeats the point of the gate.
    /// </para>
    /// </summary>
    public static bool Admits(IEnumerable<GroupOrder> openGroups, decimal candidateRisk,
        decimal maxPortfolioRisk)
    {
        if (maxPortfolioRisk <= 0) return true;      // no limit configured
        if (candidateRisk <= 0)    return false;     // unmeasurable — do not wave through
        return OpenRisk(openGroups) + candidateRisk <= maxPortfolioRisk;
    }

    /// <summary>One line for the log when a signal is turned away.</summary>
    public static string Describe(IEnumerable<GroupOrder> openGroups, decimal candidateRisk,
        decimal maxPortfolioRisk)
    {
        var live = openGroups.Where(IsLive).ToList();
        decimal open = live.Sum(RiskOf);
        string positions = live.Count == 0
            ? "nothing open"
            : string.Join(", ", live.Select(g => $"{g.SetupId} ${RiskOf(g):F0}"));

        return $"portfolio risk ${open:F0} + ${candidateRisk:F0} candidate " +
               $"exceeds ${maxPortfolioRisk:F0} ceiling ({positions})";
    }

    /// <summary>Filled, or still able to fill.</summary>
    private static bool IsLive(GroupOrder g) =>
        g.Status is GroupOrderStatus.Active or GroupOrderStatus.Pending;

    private static decimal RiskOf(GroupOrder g)
    {
        // A filled group knows its entry; a working one only has it on the entry leg.
        decimal entry = g.EntryPrice
            ?? g.Legs.FirstOrDefault(l => l.LegType == LegType.Entry)?.Price
            ?? 0m;

        // No stop recorded means the risk is real but not measurable here. Counting
        // it as zero would understate the book, so it is left out and the per-trade
        // cap remains its only guard.
        if (entry <= 0 || g.InitialStopPrice <= 0) return 0m;

        return Math.Abs(entry - g.InitialStopPrice) * g.TotalContracts * g.PointValue;
    }
}
