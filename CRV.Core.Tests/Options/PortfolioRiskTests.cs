using CRV.Core.Options;
using Xunit;

namespace CRV.Core.Tests.Options;

public class PortfolioRiskTests
{
    private static readonly DateTime Sep = new(2026, 9, 18);
    private static readonly DateTime Oct = new(2026, 10, 16);

    private static OptionPositionLeg Leg(
        OptionRight right, bool isLong, decimal strike, int qty = 1,
        decimal mid = 2m, decimal delta = 0.5m, decimal vega = 0.10m,
        decimal theta = -0.05m, DateTime? exp = null, string under = "SPY")
        => new($"{under} {(exp ?? Sep):yyMMdd}{(right == OptionRight.Call ? 'C' : 'P')}{strike}",
               under, right, strike, exp ?? Sep, isLong, qty, 100, mid,
               delta, 0.02m, theta, vega);

    [Fact]
    public void EmptyPortfolio_IsAllZero()
    {
        var r = PortfolioRiskCalculator.Aggregate([]);
        Assert.Equal(0m, r.LongPremiumAtRisk);
        Assert.Equal(0, r.LegCount);
        Assert.False(r.HasUnboundedRisk);
    }

    [Fact]
    public void GreeksAreReportedInDollars()
    {
        // delta 0.5 on 2 contracts of 100 = $100 per point of underlying
        var r = PortfolioRiskCalculator.Aggregate([Leg(OptionRight.Call, true, 700m, qty: 2)]);
        Assert.Equal(100m, r.NetDeltaDollars);
        Assert.Equal(20m,  r.NetVegaDollars);
        Assert.Equal(-10m, r.NetThetaDollars);
    }

    [Fact]
    public void ShortLegsCarryTheOppositeSign()
    {
        var r = PortfolioRiskCalculator.Aggregate([
            Leg(OptionRight.Call, true,  700m),
            Leg(OptionRight.Call, false, 710m),
        ]);
        Assert.Equal(0m, r.NetDeltaDollars);   // +50 and −50
    }

    [Fact]
    public void LongPremiumAtRisk_ExcludesShortLegs()
    {
        // A short leg's loss is not bounded by anything derivable from the leg alone,
        // so it must not be netted against long premium and quietly reduce the figure.
        var r = PortfolioRiskCalculator.Aggregate([
            Leg(OptionRight.Call, true,  700m, mid: 3m),
            Leg(OptionRight.Call, false, 710m, mid: 1m),
        ]);
        Assert.Equal(300m, r.LongPremiumAtRisk);
    }

    [Fact]
    public void CorrelatedLongs_Accumulate()
    {
        // The case a per-trade limit misses entirely: several compliant positions,
        // all pointing the same way.
        var r = PortfolioRiskCalculator.Aggregate([
            Leg(OptionRight.Call, true, 700m, mid: 4m, under: "SPY"),
            Leg(OptionRight.Call, true, 500m, mid: 3m, under: "QQQ"),
            Leg(OptionRight.Call, true, 200m, mid: 2m, under: "IWM"),
        ]);
        Assert.Equal(900m, r.LongPremiumAtRisk);
        Assert.Equal(150m, r.NetDeltaDollars);
    }

    [Fact]
    public void NakedShort_IsFlaggedAsUnbounded()
    {
        var r = PortfolioRiskCalculator.Aggregate([Leg(OptionRight.Call, false, 700m)]);
        Assert.True(r.HasUnboundedRisk);
        Assert.Single(r.UnpairedShorts);
    }

    [Fact]
    public void ShortCappedByALongOfTheSameRightAndExpiry_IsNotFlagged()
    {
        // Strikes do not matter for boundedness — past the outer strike the two move
        // one-for-one, so any long call caps any short call in the same cycle.
        var r = PortfolioRiskCalculator.Aggregate([
            Leg(OptionRight.Call, false, 700m),
            Leg(OptionRight.Call, true,  900m),
        ]);
        Assert.False(r.HasUnboundedRisk);
    }

    [Fact]
    public void LongInADifferentExpiry_DoesNotCapAShort()
    {
        // A later long does not protect through this cycle's expiration.
        var r = PortfolioRiskCalculator.Aggregate([
            Leg(OptionRight.Put, false, 700m, exp: Sep),
            Leg(OptionRight.Put, true,  690m, exp: Oct),
        ]);
        Assert.True(r.HasUnboundedRisk);
    }

    [Fact]
    public void LongOfTheOtherRight_DoesNotCapAShort()
    {
        // A long put does nothing about a short call running away upward.
        var r = PortfolioRiskCalculator.Aggregate([
            Leg(OptionRight.Call, false, 700m),
            Leg(OptionRight.Put,  true,  690m),
        ]);
        Assert.True(r.HasUnboundedRisk);
    }

    [Fact]
    public void MoreShortsThanLongs_LeavesTheExcessUncovered()
    {
        var r = PortfolioRiskCalculator.Aggregate([
            Leg(OptionRight.Call, false, 700m, qty: 3),
            Leg(OptionRight.Call, true,  710m, qty: 1),
        ]);
        Assert.True(r.HasUnboundedRisk);
    }
}
