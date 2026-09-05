using CRV.Core.Models;
using CRV.Core.Risk;
using Xunit;

namespace CRV.Core.Tests.Risk;

/// <summary>
/// Measured from the live book, the risk actually committed per trade ranged from
/// $50.75 on pullback-mym to $219.12 on retest-mgc — a 4.3x spread across setups
/// that were all treated as one contract's worth of exposure. retest-mgc averaged
/// +0.04R and still lost $1,249, because the book was four times heavier on it than
/// on anything else. That is arithmetic, not signal, and no strategy change fixes it.
/// </summary>
public class RiskProfileTests
{
    private static readonly Dictionary<string, decimal> PointValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MNQM26"] = 2m, ["MESM26"] = 5m, ["MGCM26"] = 10m, ["MCLK26"] = 100m, ["MYMM26"] = 0.5m,
    };

    private static decimal PointValue(string ticker) => PointValues.GetValueOrDefault(ticker, 1m);

    private static TradeRecord Trade(string setup, string ticker, decimal stopPoints, int contracts) => new()
    {
        SetupLabel = setup, Ticker = ticker, Contracts = contracts,
        Entry = 1000m, InitialStop = 1000m - stopPoints, Target = 1020m, Exit = 1005m,
        Source = "live", EnteredAt = DateTime.UtcNow,
    };

    private static RiskProfile Profile(params TradeRecord[] trades)
        => RiskProfile.FromTrades(trades, PointValue);

    // ── The measurement ───────────────────────────────────────────

    [Fact]
    public void RiskPerContractIsStopDistanceTimesPointValue()
    {
        // MGC: 9.5 points x $10 = $95 a contract, x2 contracts = $190 committed.
        var p = Profile(Trade("retest-mgc", "MGCM26", 9.5m, 2));
        var e = Assert.Single(p.Entries);

        Assert.Equal(95m,  e.RiskPerContract);
        Assert.Equal(190m, e.RiskPerTrade);
        Assert.Equal(2,    e.MedianContracts);
    }

    [Fact]
    public void TheMedianIsUsedSoOneWideStopDoesNotSetTheNumber()
    {
        var p = Profile(
            Trade("retest-mnq", "MNQM26", 40m, 2),
            Trade("retest-mnq", "MNQM26", 42m, 2),
            Trade("retest-mnq", "MNQM26", 400m, 2));   // one outlier

        Assert.Equal(84m, Assert.Single(p.Entries).RiskPerContract);   // 42 x $2
    }

    [Fact]
    public void EachSetupIsMeasuredSeparatelyEvenOnTheSameInstrument()
    {
        var p = Profile(
            Trade("retest-mgc",   "MGCM26", 9m, 3),
            Trade("pullback-mgc", "MGCM26", 7m, 2));

        Assert.Equal(2, p.Entries.Count);
        Assert.Equal(270m, p.Entries.Single(e => e.Setup == "retest-mgc").RiskPerTrade);
        Assert.Equal(140m, p.Entries.Single(e => e.Setup == "pullback-mgc").RiskPerTrade);
    }

    // ── The dispersion, which is the finding ──────────────────────

    [Fact]
    public void TheBookIsRankedHeaviestFirst()
    {
        var p = Profile(
            Trade("pullback-mym", "MYMM26", 100m, 2),   // $100
            Trade("retest-mgc",   "MGCM26", 11m,  2),   // $220
            Trade("retest-mes",   "MESM26", 9m,   2));  // $90

        Assert.Equal(new[] { "retest-mgc", "pullback-mym", "retest-mes" },
            p.Entries.Select(e => e.Setup));
        Assert.Equal("retest-mgc",   p.Heaviest!.Setup);
        Assert.Equal("retest-mes",   p.Lightest!.Setup);
    }

    [Fact]
    public void DispersionIsTheRatioOfHeaviestToLightest()
    {
        var p = Profile(
            Trade("retest-mgc",   "MGCM26", 11m,  2),   // $220
            Trade("pullback-mym", "MYMM26", 110m, 2));  // $110

        Assert.Equal(2.0m, p.Dispersion);
        Assert.False(p.IsNormalised);
    }

    [Fact]
    public void ABookWithinToleranceIsCalledNormalised()
    {
        var p = Profile(
            Trade("a", "MESM26", 10m, 2),    // $100
            Trade("b", "MESM26", 11m, 2),    // $110
            Trade("c", "MESM26", 12m, 2));   // $120

        Assert.Equal(1.2m, p.Dispersion);
        Assert.True(p.IsNormalised);
    }

    [Theory]
    [InlineData(1.4, false)]   // 1.5x spread is outside a 1.4x tolerance
    [InlineData(1.6, true)]
    public void TheToleranceIsAdjustable(double tolerance, bool expected)
    {
        var p = RiskProfile.FromTrades(new[]
        {
            Trade("a", "MESM26", 10m, 2),   // $100
            Trade("b", "MESM26", 15m, 2),   // $150
        }, PointValue, (decimal)tolerance);

        Assert.Equal(expected, p.IsNormalised);
    }

    // ── What to do about it ───────────────────────────────────────

    [Fact]
    public void TheSuggestedTargetIsTheMedianRiskAlreadyBeingRun()
    {
        // Not an invented number: the book's own middle, so normalising neither
        // scales the whole account up nor shuts it down.
        var p = Profile(
            Trade("a", "MESM26", 10m, 2),    // $100
            Trade("b", "MESM26", 15m, 2),    // $150
            Trade("c", "MESM26", 40m, 2));   // $400

        Assert.Equal(150m, p.SuggestedTarget);
    }

    [Fact]
    public void SuggestedContractsBringsEachSetupToTheSameDollarRisk()
    {
        var p = Profile(
            Trade("retest-mgc",   "MGCM26", 10m, 2),    // $100/contract
            Trade("pullback-mym", "MYMM26", 100m, 2));  // $50/contract

        // Target $200 a trade: MGC gets 2, MYM gets 4.
        Assert.Equal(2, p.SuggestedContracts(p.Entries.Single(e => e.Setup == "retest-mgc"),   200m));
        Assert.Equal(4, p.SuggestedContracts(p.Entries.Single(e => e.Setup == "pullback-mym"), 200m));
    }

    [Fact]
    public void ASetupTooExpensiveForOneContractIsReportedAsZeroNotOne()
    {
        // $500 a contract against a $200 budget cannot be traded at that budget.
        // Rounding up to one would silently blow it by 150%.
        var p = Profile(Trade("retest-mgc", "MGCM26", 50m, 1));
        Assert.Equal(0, p.SuggestedContracts(p.Entries[0], 200m));
    }

    [Fact]
    public void SuggestedContractsNeedsAPositiveTarget()
        => Assert.Equal(0, Profile(Trade("a", "MESM26", 10m, 2)).SuggestedContracts(
            Profile(Trade("a", "MESM26", 10m, 2)).Entries[0], 0m));

    // ── Guards ────────────────────────────────────────────────────

    [Fact]
    public void TradesWithNoStopRecordedAreExcludedRatherThanCountedAsZeroRisk()
    {
        var withStop = Trade("a", "MESM26", 10m, 2);
        var noStop   = Trade("a", "MESM26", 0m,  2);   // InitialStop == Entry — never set

        var p = Profile(withStop, noStop);
        Assert.Equal(1, Assert.Single(p.Entries).Trades);
        Assert.Equal(100m, p.Entries[0].RiskPerTrade);
    }

    // ── Rolls and thin samples, as the live book actually presents them ──

    [Fact]
    public void OneSetupIsNotSplitInTwoByAContractRoll()
    {
        // retest-mcl traded MCLK26 and then MCLM26. That is one setup, not two.
        var p = Profile(
            Trade("retest-mcl", "MCLK26", 0.5m, 2),
            Trade("retest-mcl", "MCLM26", 0.6m, 2));

        var e = Assert.Single(p.Entries);
        Assert.Equal(2, e.Trades);
        Assert.Equal("MCL", e.Ticker);   // reported by root once it spans expiries
    }

    [Fact]
    public void DifferentInstrumentsOnOneSetupStillSeparate()
    {
        var p = Profile(
            Trade("retest", "MCLK26", 0.5m, 2),
            Trade("retest", "MNQM26", 20m,  2));
        Assert.Equal(2, p.Entries.Count);
    }

    [Fact]
    public void ASingleTradeDoesNotSetTheHeadlineDispersion()
    {
        // sessionfakeout-mnq has exactly one live trade, at $684. One fill is not a
        // sizing policy, and letting it drive the verdict buries the real spread.
        var p = Profile(
            Trade("sessionfakeout-mnq", "MNQM26", 171m, 2),   // $684, n=1
            Trade("retest-mgc", "MGCM26", 7.3m, 2), Trade("retest-mgc", "MGCM26", 7.3m, 2),
            Trade("retest-mgc", "MGCM26", 7.3m, 2), Trade("retest-mgc", "MGCM26", 7.3m, 2),
            Trade("retest-mgc", "MGCM26", 7.3m, 2),           // $146, n=5
            Trade("retest-mes", "MESM26", 5.9m, 2), Trade("retest-mes", "MESM26", 5.9m, 2),
            Trade("retest-mes", "MESM26", 5.9m, 2), Trade("retest-mes", "MESM26", 5.9m, 2),
            Trade("retest-mes", "MESM26", 5.9m, 2));          // $59, n=5

        // Still listed, and still first — it is the heaviest position on the book.
        Assert.Equal("sessionfakeout-mnq", p.Entries[0].Setup);

        // But the dispersion compares only the setups with enough fills to mean something.
        Assert.Equal(2.4746m, p.Dispersion);
        Assert.Equal("retest-mgc", p.DispersionHeaviest!.Setup);
        Assert.Equal("retest-mes", p.DispersionLightest!.Setup);
    }

    [Fact]
    public void WhenNothingHasEnoughFillsTheWholeBookIsUsedRatherThanNothing()
    {
        var p = Profile(
            Trade("a", "MESM26", 20m, 2),    // $200
            Trade("b", "MESM26", 10m, 2));   // $100

        Assert.Equal(2m, p.Dispersion);
        Assert.Equal("a", p.DispersionHeaviest!.Setup);
    }

    [Fact]
    public void AnEmptyBookIsNotAnError()
    {
        var p = Profile();
        Assert.Empty(p.Entries);
        Assert.Null(p.Heaviest);
        Assert.Equal(0m, p.Dispersion);
        Assert.True(p.IsNormalised);   // nothing to normalise
    }

    [Fact]
    public void ASingleSetupIsTriviallyNormalised()
    {
        var p = Profile(Trade("a", "MESM26", 10m, 2));
        Assert.Equal(1m, p.Dispersion);
        Assert.True(p.IsNormalised);
    }
}
