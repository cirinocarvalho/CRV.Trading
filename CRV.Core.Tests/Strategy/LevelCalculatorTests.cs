using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class LevelCalculatorTests
{
    private const decimal OrbRange  = 100m; // simple round number
    private const decimal PointValue = 20m;

    // ── Setup A ───────────────────────────────────────────────

    [Fact]
    public void SetupA_Long_StopBelowEntry_TargetAboveEntry()
    {
        var (stop, target, partial, rr) = LevelCalculator.CalcLevels(
            entry: 1000m, isLong: true,
            stopPct: 0.10m, targetPct: 100, partialPct: 50,
            orbRange: OrbRange);

        Assert.True(stop   < 1000m, "Stop must be below entry for a long.");
        Assert.True(target > 1000m, "Target must be above entry for a long.");
        Assert.True(partial > 1000m && partial < target, "Partial must be between entry and target.");
        Assert.True(rr > 0, "R:R must be positive.");
    }

    [Fact]
    public void SetupA_Short_StopAboveEntry_TargetBelowEntry()
    {
        var (stop, target, partial, rr) = LevelCalculator.CalcLevels(
            entry: 1000m, isLong: false,
            stopPct: 0.10m, targetPct: 100, partialPct: 50,
            orbRange: OrbRange);

        Assert.True(stop   > 1000m, "Stop must be above entry for a short.");
        Assert.True(target < 1000m, "Target must be below entry for a short.");
        Assert.True(partial < 1000m && partial > target, "Partial must be between entry and target.");
        Assert.True(rr > 0);
    }

    [Fact]
    public void SetupA_Long_LevelsCalculatedCorrectly()
    {
        // entry=1000, stopPct=0.10 → stopDist=10 → stop=990
        // targetPct=100 → targetDist=100 → target=1100
        // partialPct=50 → partialDist=50 → partial=1050
        // RR = 100/10 = 10
        var (stop, target, partial, rr) = LevelCalculator.CalcLevels(
            1000m, true, 0.10m, 100, 50, OrbRange);

        Assert.Equal(990m,  stop);
        Assert.Equal(1100m, target);
        Assert.Equal(1050m, partial);
        Assert.Equal(10m,   rr);
    }

    [Fact]
    public void SetupA_Short_LevelsCalculatedCorrectly()
    {
        var (stop, target, partial, rr) = LevelCalculator.CalcLevels(
            1000m, false, 0.10m, 100, 50, OrbRange);

        Assert.Equal(1010m, stop);
        Assert.Equal(900m,  target);
        Assert.Equal(950m,  partial);
        Assert.Equal(10m,   rr);
    }

    [Fact]
    public void SetupA_ZeroRisk_ReturnsZeroRr()
    {
        // stopPct=0 means stop == entry → risk == 0
        var (_, _, _, rr) = LevelCalculator.CalcLevels(1000m, true, 0m, 100, 50, OrbRange);
        Assert.Equal(0m, rr);
    }

    // ── Setup B ───────────────────────────────────────────────

    [Fact]
    public void SetupB_Long_StopComputedFromEntryAndStopPct()
    {
        // entry = orbHigh = 1000, orbRange = 100, stopPct = 0.50
        // stop = 1000 - 100 * 0.50 = 950 (same as old orbMid for symmetric ORB)
        var (stop, target, partial, rr) = LevelCalculator.CalcLevelsB(
            entry: 1000m, isLong: true,
            targetPct: 100, partialPct: 50,
            orbRange: OrbRange, stopPct: 0.50m);

        Assert.Equal(950m, stop);
        Assert.True(target > 1000m);
        Assert.True(rr > 0);
    }

    [Fact]
    public void SetupB_Short_StopComputedFromEntryAndStopPct()
    {
        // entry = orbLow = 1000, orbRange = 100, stopPct = 0.50
        // stop = 1000 + 100 * 0.50 = 1050 (same as old orbMid for symmetric ORB)
        var (stop, target, partial, rr) = LevelCalculator.CalcLevelsB(
            entry: 1000m, isLong: false,
            targetPct: 100, partialPct: 50,
            orbRange: OrbRange, stopPct: 0.50m);

        Assert.Equal(1050m, stop);
        Assert.True(target < 1000m);
        Assert.True(rr > 0);
    }

    [Fact]
    public void SetupB_Long_CustomStopPct_StopNotAtOrbMid()
    {
        // stopPct = 0.25 → stop = 1000 - 100 * 0.25 = 975 (not 950)
        var (stop, target, _, _) = LevelCalculator.CalcLevelsB(
            entry: 1000m, isLong: true,
            targetPct: 100, partialPct: 50,
            orbRange: OrbRange, stopPct: 0.25m);

        Assert.Equal(975m, stop);
        Assert.True(target > 1000m);
    }
}
