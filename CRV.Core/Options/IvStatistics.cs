namespace CRV.Core.Options;

/// <summary>Where today's implied volatility sits against its own history.</summary>
/// <param name="Rank">
/// Position between the window's low and high, 0–100. The common "IV rank".
/// </param>
/// <param name="Percentile">
/// Share of observations strictly below today's, 0–100. Less sensitive to a single spike
/// than <paramref name="Rank"/>, which one outlier can dominate for a year.
/// </param>
public sealed record IvStanding(decimal Rank, decimal Percentile, decimal Low, decimal High, int Observations);

public static class IvStatistics
{
    /// <summary>
    /// Rank and percentile of <paramref name="current"/> against <paramref name="history"/>.
    /// <para>Returns null below <paramref name="minObservations"/>: a rank computed from a
    /// handful of readings is noise wearing the costume of a statistic, and showing it would
    /// invite exactly the confidence it does not deserve.</para>
    /// </summary>
    public static IvStanding? Standing(
        decimal current, IReadOnlyList<decimal> history, int minObservations = 20)
    {
        if (history is null || history.Count < minObservations) return null;

        decimal low  = history.Min();
        decimal high = history.Max();

        // A flat window has no range to rank within; calling that "100" would be a lie.
        decimal rank = high > low ? (current - low) / (high - low) * 100m : 50m;
        rank = Math.Clamp(rank, 0m, 100m);

        decimal below = history.Count(v => v < current);
        decimal percentile = below / history.Count * 100m;

        return new IvStanding(
            Math.Round(rank, 1), Math.Round(percentile, 1), low, high, history.Count);
    }
}
