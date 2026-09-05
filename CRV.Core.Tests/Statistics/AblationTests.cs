using CRV.Core.Statistics;
using Xunit;

namespace CRV.Core.Tests.Statistics;

/// <summary>
/// The engine stacks VWAP, ATR, chop and EMA filters on top of the raw opening-range
/// break, and not one of them has ever been measured against the break alone. A
/// filter that does not beat the baseline is costing trades for nothing; a filter
/// whose effect cannot be distinguished from noise has not been shown to do either.
/// </summary>
public class AblationTests
{
    private static EdgeTest E(decimal meanR, int n = 200, decimal sd = 1.2m)
        => EdgeTest.FromSummary(n, meanR, sd);

    [Fact]
    public void AFilterThatClearlyImprovesOnTheBaselineEarnsItsPlace()
    {
        var result = new Ablation(baseline: E(-0.10m), withFilter: E(0.45m), "vwap");

        Assert.Equal(0.55m, result.Contribution);
        Assert.Equal(AblationVerdict.Earns, result.Verdict);
    }

    [Fact]
    public void AFilterThatMakesThingsWorseIsCalledOut()
    {
        var result = new Ablation(baseline: E(0.40m), withFilter: E(-0.20m), "chop");

        Assert.Equal(-0.60m, result.Contribution);
        Assert.Equal(AblationVerdict.Harms, result.Verdict);
    }

    [Fact]
    public void AFilterWhoseEffectIsWithinTheNoiseHasNotEarnedItsPlace()
    {
        // +0.05R apart on 200 trades each: nowhere near separable. The filter is
        // costing trades and complexity for an effect nobody can measure.
        var result = new Ablation(baseline: E(0.20m), withFilter: E(0.25m), "atr");

        Assert.Equal(AblationVerdict.NoMeasurableEffect, result.Verdict);
        Assert.False(result.Verdict == AblationVerdict.Earns);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(19)]
    public void TooFewTradesEitherSideAndTheAblationSaysNothing(int n)
    {
        var result = new Ablation(baseline: E(-0.10m, n), withFilter: E(0.80m, n), "ema");
        Assert.Equal(AblationVerdict.InsufficientEvidence, result.Verdict);
    }

    [Fact]
    public void ThePassRateIsReportedBecauseAFilterThatBlocksEverythingIsNotFree()
    {
        var result = new Ablation(baseline: E(0.10m, n: 200), withFilter: E(0.30m, n: 40), "vwap");
        Assert.Equal(0.20, result.PassRate, 3);
    }

    [Fact]
    public void AFilterThatBlocksNearlyEverythingIsFlaggedEvenWhenItLooksGood()
    {
        // 6 of 200 trades survive. Whatever the surviving handful scored, there is
        // no sample left to judge it on.
        var result = new Ablation(baseline: E(0.10m, n: 200), withFilter: E(1.20m, n: 6), "chop");
        Assert.Equal(AblationVerdict.InsufficientEvidence, result.Verdict);
        Assert.True(result.PassRate < 0.05);
    }

    // ── A stack of filters, read together ─────────────────────────

    [Fact]
    public void TheReportRanksByContributionAndNamesWhatToDelete()
    {
        var study = new AblationStudy(E(0.05m), new[]
        {
            new Ablation(E(0.05m), E(0.42m),  "vwap"),   // earns
            new Ablation(E(0.05m), E(0.08m),  "atr"),    // no measurable effect
            new Ablation(E(0.05m), E(-0.35m), "chop"),   // harms
        });

        Assert.Equal(new[] { "vwap", "atr", "chop" }, study.Ranked.Select(a => a.Name));
        Assert.Equal(new[] { "vwap" },                study.Earning.Select(a => a.Name));
        Assert.Equal(new[] { "atr", "chop" },         study.Candidates.Select(a => a.Name));
    }

    [Fact]
    public void AStudyWhereNothingEarnsItsPlaceSaysSo()
    {
        var study = new AblationStudy(E(0.30m), new[]
        {
            new Ablation(E(0.30m), E(0.28m), "vwap"),
            new Ablation(E(0.30m), E(0.33m), "atr"),
        });

        Assert.Empty(study.Earning);
        Assert.Equal(2, study.Candidates.Count);
        Assert.Contains("nothing measurably improves", study.Describe());
    }

    [Fact]
    public void AnEmptyStudyIsNotAnError()
    {
        var study = new AblationStudy(E(0.1m), Array.Empty<Ablation>());
        Assert.Empty(study.Ranked);
        Assert.Empty(study.Earning);
    }
}
