using CRV.Core.Models;
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

    // ── Targets: every mode through one request ───────────────────

    private static LevelRequest Dollars(decimal dollars, TargetDollarsBasis basis, decimal pointValue,
        int contracts = 1, bool isLong = true, decimal partialPct = 50m) => new(
        Entry: 20000m, IsLong: isLong, Stop: isLong ? 19990m : 20010m, Contracts: contracts,
        Mode: TargetMode.Dollars, RangeOrAtr: 0m, TargetPct: 0m, TargetDollars: dollars, Basis: basis,
        PointValue: pointValue, PartialPct: partialPct, TickSize: 0.25m);

    [Fact]
    public void Dollars_PerContract_Mnq_Long()
    {
        // $400 a contract at $2 a point = 200 pts; partial at 50% = 100 pts; risk 10 pts.
        var (target, partial, rr) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 2m));

        Assert.Equal(20200m, target);
        Assert.Equal(20100m, partial);
        Assert.Equal(20m, rr);
    }

    [Fact]
    public void Dollars_PerContract_Mnq_Short()
    {
        var (target, partial, _) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 2m, isLong: false));

        Assert.Equal(19800m, target);
        Assert.Equal(19900m, partial);
    }

    [Fact]
    public void Dollars_PerContract_Nq_IsAPointPerTwentyDollars()
    {
        var (target, _, _) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 20m));

        Assert.Equal(20020m, target);
    }

    [Theory]
    [InlineData(1, 20200)]
    [InlineData(2, 20100)]
    [InlineData(4, 20050)]
    public void Dollars_WholePosition_SpreadsOverTheContracts(int contracts, int expectedTarget)
    {
        var (target, _, _) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.WholePosition, 2m, contracts));

        Assert.Equal((decimal)expectedTarget, target);
    }

    [Fact]
    public void Dollars_RoundToTheTick()
    {
        // $400.30 / $2 = 200.15 pts -> 200.25; partial 100.075 pts -> 100.00.
        var (target, partial, _) = LevelCalculator.Targets(Dollars(400.30m, TargetDollarsBasis.PerContract, 2m));

        Assert.Equal(20200.25m, target);
        Assert.Equal(20100m, partial);
    }

    [Fact]
    public void Dollars_PartialIsPartialPctOfTheDistance()
    {
        var (_, partial, _) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 2m, partialPct: 25m));

        Assert.Equal(20050m, partial);
    }

    [Fact]
    public void Dollars_WithoutAPointValue_HaveNoDistance()
    {
        var (target, _, rr) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 0m));

        Assert.Equal(20000m, target);
        Assert.Equal(0m, rr);
    }

    [Fact]
    public void RangePct_MatchesCalcLevels()
    {
        var r = new LevelRequest(1000m, true, 990m, 1, TargetMode.RangePct, 100m, 100m, 0m,
            TargetDollarsBasis.PerContract, 0m, 50m, 0m);

        Assert.Equal((1100m, 1050m, 10m), LevelCalculator.Targets(r));
    }

    [Fact]
    public void RiskMultiple_UsesTheTpMultiplesOfTheRisk()
    {
        var r = new LevelRequest(100m, true, 98m, 1, TargetMode.RiskMultiple, 0m, 0m, 0m,
            TargetDollarsBasis.PerContract, 0m, 50m, 0.25m, Tp1Mult: 1m, Tp2Mult: 2m);

        Assert.Equal((104m, 102m, 2m), LevelCalculator.Targets(r));
    }

    [Fact]
    public void Atr_UsesTheTpMultiplesOfTheAtr()
    {
        var r = new LevelRequest(100m, true, 98m, 1, TargetMode.Atr, 4m, 0m, 0m,
            TargetDollarsBasis.PerContract, 0m, 50m, 0.25m, Tp1Mult: 0.5m, Tp2Mult: 1.5m);

        var (target, partial, _) = LevelCalculator.Targets(r);

        Assert.Equal(106m, target);
        Assert.Equal(102m, partial);
    }

    [Fact]
    public void RaiseToMinRr_Long_MovesTargetToMinRAndRecomputesPartial()
    {
        var r = new LevelRequest(18010m, true, 18000m, 1, TargetMode.RangePct, 20m, 100m, 0m,
            TargetDollarsBasis.PerContract, 2m, 50m, 0.25m);

        Assert.Equal((18040m, 18025m), LevelCalculator.RaiseToMinRr(r, 3m));
    }

    [Fact]
    public void RaiseToMinRr_Short_MovesTargetToMinRAndRecomputesPartial()
    {
        var r = new LevelRequest(100m, false, 102m, 1, TargetMode.RangePct, 20m, 100m, 0m,
            TargetDollarsBasis.PerContract, 2m, 50m, 0.25m);

        Assert.Equal((97m, 98.5m), LevelCalculator.RaiseToMinRr(r, 1.5m));
    }

    [Theory]
    [InlineData(true,  99.9,  100.25)]
    [InlineData(false, 100.1, 99.75)]
    public void RaiseToMinRr_RoundsAwayFromTheEntry_SoTheTradeIsNeverBelowTheMinimum(bool isLong, double stop, double expected)
    {
        // Risk 0.1 x 1.2 = 0.12 pts: the nearest tick is the entry itself (0R); the raise goes one tick out.
        var r = new LevelRequest(100m, isLong, (decimal)stop, 1, TargetMode.RangePct, 0m, 0m, 0m,
            TargetDollarsBasis.PerContract, 2m, 50m, 0.25m);

        Assert.Equal((decimal)expected, LevelCalculator.RaiseToMinRr(r, 1.2m).Target);
    }

    [Fact]
    public void RangeStop_IsStopPctOfTheRangeFromTheEntry()
    {
        Assert.Equal(990m,  LevelCalculator.RangeStop(1000m, true,  100m, 0.10m));
        Assert.Equal(1010m, LevelCalculator.RangeStop(1000m, false, 100m, 0.10m));
    }

    [Theory]
    [InlineData(true,  18237.75, 0.10, 100, 50, 37.5, 0.25)]
    [InlineData(false, 18237.75, 0.35, 135, 40, 37.5, 0.25)]
    [InlineData(true,  1000,     0.10, 100, 50, 100,  0)]
    [InlineData(false, 4512.33,  0.50, 250, 33, 12.7, 0.01)]
    public void CalcLevels_AndCalcLevelsB_MatchTheDirectFormula(
        bool isLong, double entryD, double stopPctD, int targetPct, int partialPct, double rangeD, double tickD)
    {
        decimal entry = (decimal)entryD, stopPct = (decimal)stopPctD, range = (decimal)rangeD, tick = (decimal)tickD;
        decimal sign = isLong ? 1m : -1m;
        decimal targetDist = range * (targetPct / 100m);
        decimal expStop    = LevelCalculator.RoundToTick(entry - sign * range * stopPct, tick);
        decimal expTarget  = LevelCalculator.RoundToTick(entry + sign * targetDist, tick);
        decimal expPartial = LevelCalculator.RoundToTick(entry + sign * targetDist * (partialPct / 100m), tick);
        decimal risk       = Math.Abs(entry - expStop);
        decimal expRr      = risk > 0 ? Math.Abs(expTarget - entry) / risk : 0m;

        var a = LevelCalculator.CalcLevels(entry, isLong, stopPct, targetPct, partialPct, range, tick);
        var b = LevelCalculator.CalcLevelsB(entry, isLong, targetPct, partialPct, range, stopPct, tick);

        Assert.Equal((expStop, expTarget, expPartial, expRr), a);
        Assert.Equal(a, b);
    }
}
