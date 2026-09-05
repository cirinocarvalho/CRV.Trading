using CRV.Core.Options;
using Xunit;

namespace CRV.Core.Tests.Options;

public class IvStatisticsTests
{
    private static List<decimal> Range(int n, decimal from, decimal to)
        => Enumerable.Range(0, n).Select(i => from + (to - from) * i / (n - 1)).ToList();

    [Fact]
    public void TooFewObservations_ReturnsNothing()
    {
        // A rank from a handful of readings is noise dressed as a statistic.
        Assert.Null(IvStatistics.Standing(20m, Range(5, 10m, 30m)));
    }

    [Fact]
    public void AtTheTopOfItsRange_RanksAtOneHundred()
        => Assert.Equal(100m, IvStatistics.Standing(30m, Range(50, 10m, 30m))!.Rank);

    [Fact]
    public void AtTheBottomOfItsRange_RanksAtZero()
        => Assert.Equal(0m, IvStatistics.Standing(10m, Range(50, 10m, 30m))!.Rank);

    [Fact]
    public void MidRange_RanksAtFifty()
        => Assert.Equal(50m, IvStatistics.Standing(20m, Range(50, 10m, 30m))!.Rank);

    [Fact]
    public void RankIsClampedWhenTodayExceedsEverySeenBefore()
    {
        // A new high should read 100, not 140.
        Assert.Equal(100m, IvStatistics.Standing(38m, Range(50, 10m, 30m))!.Rank);
    }

    [Fact]
    public void PercentileAndRankDivergeWhenOneSpikeDominatesTheWindow()
    {
        // Rank is a range measure, so a single 90 makes an otherwise ordinary 20 look cheap.
        // Percentile counts observations, so it still says 20 is high. Reporting both is
        // the point — either alone misleads in a different direction.
        var history = Range(49, 10m, 20m);
        history.Add(90m);

        var s = IvStatistics.Standing(20m, history)!;
        Assert.True(s.Rank < 15m,       "range-based rank is dragged down by the outlier");
        Assert.True(s.Percentile > 90m, "count-based percentile is not");
    }

    [Fact]
    public void FlatHistory_ReportsTheMiddleRatherThanAnExtreme()
    {
        // No range to rank within. Calling it 100 would claim information that is not there.
        var s = IvStatistics.Standing(15m, Enumerable.Repeat(15m, 40).ToList())!;
        Assert.Equal(50m, s.Rank);
    }

    [Fact]
    public void LowAndHighAreReportedSoTheRankCanBeChecked()
    {
        var s = IvStatistics.Standing(25m, Range(50, 10m, 30m))!;
        Assert.Equal(10m, s.Low);
        Assert.Equal(30m, s.High);
        Assert.Equal(50, s.Observations);
    }
}
