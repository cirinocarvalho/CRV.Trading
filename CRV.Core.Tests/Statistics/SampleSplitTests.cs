using CRV.Core.Models;
using CRV.Core.Statistics;
using Xunit;

namespace CRV.Core.Tests.Statistics;

/// <summary>
/// There is no in-sample/out-of-sample split anywhere in this system, so every
/// number it has ever produced was measured on the data it was chosen against.
/// The promising subset in the live book — retest, NY, MNQ/MCL, long — was found
/// by reading the results; that is a hypothesis, and it needs data it has not seen.
/// </summary>
public class SampleSplitTests
{
    private static readonly DateTime Start = new(2026, 3, 30, 0, 0, 0, DateTimeKind.Utc);

    private static List<TradeRecord> Trades(int n, int everyHours = 24) =>
        Enumerable.Range(0, n).Select(i => new TradeRecord
        {
            EnteredAt = Start.AddHours(i * everyHours),
            ExitedAt  = Start.AddHours(i * everyHours).AddMinutes(30),
            RMultiple = i % 2 == 0 ? 1m : -1m,
            Ticker    = "MNQ",
        }).ToList();

    // ── Splitting by fraction ─────────────────────────────────────

    [Fact]
    public void SeventyThirtyPutsTheEarlierTradesInSample()
    {
        var split = SampleSplit.ByFraction(Trades(100), 0.70);
        Assert.Equal(70, split.InSample.Count);
        Assert.Equal(30, split.OutOfSample.Count);
        Assert.True(split.InSample.Max(t => t.EnteredAt) <= split.OutOfSample.Min(t => t.EnteredAt));
    }

    [Fact]
    public void TheSplitIsChronologicalNotRandom()
    {
        // Shuffled input must still split by time: sampling at random from a time
        // series leaks the future into the training set.
        var shuffled = Trades(50).OrderBy(_ => Guid.NewGuid()).ToList();
        var split = SampleSplit.ByFraction(shuffled, 0.60);
        Assert.All(split.InSample, t => Assert.True(t.EnteredAt < split.OutOfSample.Min(o => o.EnteredAt)));
    }

    [Theory]
    [InlineData(0.5,  88, 88)]
    [InlineData(0.75, 132, 44)]
    public void TheFractionIsHonoured(double fraction, int expectedIn, int expectedOut)
    {
        var split = SampleSplit.ByFraction(Trades(176), fraction);
        Assert.Equal(expectedIn,  split.InSample.Count);
        Assert.Equal(expectedOut, split.OutOfSample.Count);
    }

    // ── Splitting by date ─────────────────────────────────────────

    [Fact]
    public void SplittingOnADateIsInclusiveOfTheBoundaryOnTheOutOfSampleSide()
    {
        var boundary = Start.AddDays(10);
        var split = SampleSplit.ByDate(Trades(20), boundary);
        Assert.All(split.InSample,    t => Assert.True(t.EnteredAt <  boundary));
        Assert.All(split.OutOfSample, t => Assert.True(t.EnteredAt >= boundary));
    }

    // ── The embargo ───────────────────────────────────────────────

    [Fact]
    public void AnEmbargoDropsTradesStraddlingTheBoundary()
    {
        // Two days of embargo removes trades within 2 days after the split point,
        // so a position opened in-sample cannot bleed its outcome into out-of-sample.
        var plain    = SampleSplit.ByFraction(Trades(100), 0.70);
        var embargoed = SampleSplit.ByFraction(Trades(100), 0.70, embargo: TimeSpan.FromDays(2));

        Assert.Equal(70, embargoed.InSample.Count);
        Assert.Equal(plain.OutOfSample.Count - 2, embargoed.OutOfSample.Count);
        Assert.Equal(2, embargoed.EmbargoedCount);
    }

    [Fact]
    public void WithoutAnEmbargoNothingIsDropped()
        => Assert.Equal(0, SampleSplit.ByFraction(Trades(100), 0.70).EmbargoedCount);

    // ── Guards ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void AFractionThatLeavesOneSideEmptyIsRejected(double fraction)
        => Assert.Throws<ArgumentOutOfRangeException>(() => SampleSplit.ByFraction(Trades(50), fraction));

    [Fact]
    public void AnEmptySetSplitsIntoTwoEmptySets()
    {
        var split = SampleSplit.ByFraction(new List<TradeRecord>(), 0.7);
        Assert.Empty(split.InSample);
        Assert.Empty(split.OutOfSample);
    }

    // ── What the split is for ─────────────────────────────────────

    [Fact]
    public void EachSideIsScoredSeparatelyAndTheGapIsReported()
    {
        // In-sample looks strong, out-of-sample does not — the signature of a fit.
        var trades = new List<TradeRecord>();
        for (int i = 0; i < 60; i++)   // mean +1.0
            trades.Add(new TradeRecord { EnteredAt = Start.AddHours(i), RMultiple = i % 2 == 0 ? 2.0m : 0.0m });
        for (int i = 60; i < 120; i++) // mean -0.5
            trades.Add(new TradeRecord { EnteredAt = Start.AddHours(i), RMultiple = i % 2 == 0 ? 0.5m : -1.5m });

        var split = SampleSplit.ByFraction(trades, 0.5);
        Assert.Equal( 1.0m, split.InSampleEdge.MeanR);
        Assert.Equal(-0.5m, split.OutOfSampleEdge.MeanR);
        Assert.Equal( 1.5m, split.Degradation);
        Assert.True(split.FailedOutOfSample);
    }

    [Fact]
    public void AResultThatHoldsUpIsNotFlaggedAsAFailure()
    {
        var trades = Enumerable.Range(0, 120)
            .Select(i => new TradeRecord { EnteredAt = Start.AddHours(i), RMultiple = i % 2 == 0 ? 1.4m : -0.6m })
            .ToList();

        var split = SampleSplit.ByFraction(trades, 0.5);
        Assert.Equal(0m, split.Degradation);
        Assert.False(split.FailedOutOfSample);
    }

    [Fact]
    public void AZeroVarianceSampleIsNotTreatedAsInfiniteConfidence()
    {
        // Every trade returning exactly the same R is not a perfect edge, it is a
        // modelling artefact — under the old frictionless fill model every stop-out
        // came back at precisely -1.000R. There is no interval to compute, and the
        // honest answer is that the sample supports nothing.
        var trades = Enumerable.Range(0, 120)
            .Select(i => new TradeRecord { EnteredAt = Start.AddHours(i), RMultiple = -1m })
            .ToList();

        var split = SampleSplit.ByFraction(trades, 0.5);
        Assert.Equal(EdgeVerdict.InsufficientEvidence, split.InSampleEdge.Verdict);
        Assert.Equal(EdgeVerdict.InsufficientEvidence, split.OutOfSampleEdge.Verdict);
        Assert.False(split.FailedOutOfSample);
    }

    [Fact]
    public void AnUnderSampledOutOfSampleSideIsNotCalledAFailure()
    {
        // 10 out-of-sample trades cannot fail a test they were never able to pass.
        var trades = Enumerable.Range(0, 50)
            .Select(i => new TradeRecord { EnteredAt = Start.AddHours(i), RMultiple = i < 40 ? 1m : -1m })
            .ToList();

        var split = SampleSplit.ByFraction(trades, 0.8);
        Assert.Equal(EdgeVerdict.InsufficientEvidence, split.OutOfSampleEdge.Verdict);
        Assert.False(split.FailedOutOfSample);
    }
}
