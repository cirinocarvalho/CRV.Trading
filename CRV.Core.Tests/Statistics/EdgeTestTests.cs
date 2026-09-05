using CRV.Core.Statistics;
using Xunit;

namespace CRV.Core.Tests.Statistics;

/// <summary>
/// Reproduces the review's central arithmetic. 176 live trades netted +$228 at a
/// mean of -0.0029R with sd 1.177 — a confidence interval spanning +/-0.17R and a
/// t of -0.03. The point of this class is that a run cannot report "+$228" without
/// also reporting that the number is indistinguishable from zero.
/// </summary>
public class EdgeTestTests
{
    // The live book, as measured.
    private static EdgeTest LiveBook() => EdgeTest.FromSummary(n: 176, meanR: -0.0029m, sdR: 1.177m);

    [Fact]
    public void TheLiveBookHasNoMeasurableEdge()
    {
        var e = LiveBook();
        Assert.Equal(0.0887, (double)e.StandardError, 4);
        Assert.Equal(-0.033, (double)e.TStatistic,    3);
        Assert.Equal(-0.178, (double)e.LowerBound,    3);
        Assert.Equal( 0.172, (double)e.UpperBound,    3);
        Assert.Equal(EdgeVerdict.NoMeasurableEdge, e.Verdict);
    }

    [Fact]
    public void TheIntervalStraddlingZeroIsWhatMakesItNoEdge()
    {
        var e = LiveBook();
        Assert.True(e.LowerBound < 0 && e.UpperBound > 0);
        Assert.False(e.IsSignificant);
    }

    [Fact]
    public void ItSaysHowManyTradesTheClaimWouldActuallyNeed()
    {
        // (t x sd / |mean|)^2 — the "you would need ~674,000 trades" figure.
        Assert.InRange(LiveBook().TradesNeededForSignificance!.Value, 600_000, 750_000);
    }

    [Fact]
    public void AMeanOfExactlyZeroCanNeverReachSignificance()
        => Assert.Null(EdgeTest.FromSummary(n: 100, meanR: 0m, sdR: 1m).TradesNeededForSignificance);

    // ── The gate that stops a 14-trade cell being called an edge ──

    [Theory]
    [InlineData(5)]
    [InlineData(14)]   // retest-mnq NY, the best-looking cell in the book
    [InlineData(19)]
    public void BelowTheMinimumSampleTheVerdictIsInsufficientEvidence(int n)
    {
        var e = EdgeTest.FromSummary(n, meanR: 0.75m, sdR: 1.0m);
        Assert.Equal(EdgeVerdict.InsufficientEvidence, e.Verdict);
        Assert.False(e.IsSignificant);
    }

    [Fact]
    public void AStrongEdgeOnAnAdequateSampleIsReportedAsOne()
    {
        // +0.5R with sd 1.0 over 100 trades: t = 5.0, interval nowhere near zero.
        var e = EdgeTest.FromSummary(n: 100, meanR: 0.5m, sdR: 1.0m);
        Assert.Equal(EdgeVerdict.EdgePresent, e.Verdict);
        Assert.True(e.IsSignificant);
        Assert.True(e.LowerBound > 0);
    }

    [Fact]
    public void AStrongNegativeEdgeIsAlsoAFinding()
    {
        var e = EdgeTest.FromSummary(n: 100, meanR: -0.5m, sdR: 1.0m);
        Assert.Equal(EdgeVerdict.EdgePresent, e.Verdict);
        Assert.True(e.UpperBound < 0);
    }

    [Fact]
    public void OneTradeHasNoIntervalAtAll()
    {
        var e = EdgeTest.FromSummary(n: 1, meanR: 3m, sdR: 0m);
        Assert.Equal(EdgeVerdict.InsufficientEvidence, e.Verdict);
        Assert.False(e.IsSignificant);
    }

    [Fact]
    public void NoTradesIsNotAnError()
    {
        var e = EdgeTest.FromSamples(Array.Empty<decimal>());
        Assert.Equal(0, e.Count);
        Assert.Equal(EdgeVerdict.InsufficientEvidence, e.Verdict);
    }

    // ── Computed from raw R-multiples ─────────────────────────────

    [Fact]
    public void FromSamplesUsesTheSampleStandardDeviation()
    {
        // mean 2, sample sd (n-1 divisor) = 2.
        var e = EdgeTest.FromSamples(new[] { 0m, 2m, 4m, 2m, 0m, 4m });
        Assert.Equal(6, e.Count);
        Assert.Equal(2m,   e.MeanR);
        Assert.Equal(1.789, (double)e.StandardDeviation, 3);
    }

    [Fact]
    public void FromSamplesAndFromSummaryAgree()
    {
        var samples = new[] { 1.2m, -1m, -1m, 2.4m, -1m, 0.3m, -1m, 1.8m };
        var a = EdgeTest.FromSamples(samples);
        var b = EdgeTest.FromSummary(a.Count, a.MeanR, a.StandardDeviation);
        Assert.Equal(a.LowerBound, b.LowerBound, 6);
        Assert.Equal(a.UpperBound, b.UpperBound, 6);
    }

    // ── Comparing two variants, which is what a sweep needs ───────

    [Fact]
    public void TwoVariantsWhoseIntervalsOverlapAreNotDistinguishable()
    {
        var a = EdgeTest.FromSummary(n: 60, meanR: 0.20m, sdR: 1.1m);
        var b = EdgeTest.FromSummary(n: 60, meanR: 0.05m, sdR: 1.1m);
        Assert.False(EdgeTest.Differ(a, b));
    }

    [Fact]
    public void AWideSeparationOnGoodSamplesIsDistinguishable()
    {
        var a = EdgeTest.FromSummary(n: 200, meanR:  0.60m, sdR: 1.0m);
        var b = EdgeTest.FromSummary(n: 200, meanR: -0.40m, sdR: 1.0m);
        Assert.True(EdgeTest.Differ(a, b));
    }

    [Fact]
    public void VariantsCannotBeComparedWhenEitherSampleIsTooSmall()
    {
        var big   = EdgeTest.FromSummary(n: 200, meanR: 0.60m, sdR: 1.0m);
        var small = EdgeTest.FromSummary(n: 8,   meanR: -2.0m, sdR: 1.0m);
        Assert.False(EdgeTest.Differ(big, small));
    }
}
