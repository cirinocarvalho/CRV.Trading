using CRV.Core.Options;
using CRV.Live.Brokers.Schwab;
using Xunit;

namespace CRV.Core.Tests.Options;

public class SchwabOptionOrderTests
{
    private static OptionLeg Leg(OptionRight r, LegAction a, decimal k, decimal prem, int qty = 1)
        => new(r, a, k, prem, qty, 100, $"SPY   260828{(r == OptionRight.Call ? "C" : "P")}{(int)(k * 1000):D8}");

    // Real SPY butterfly: +764c @3.31, −2× 766c @1.80, +768c @0.81 → net debit 0.52
    private static OptionLeg[] Butterfly() =>
    [
        Leg(OptionRight.Call, LegAction.Buy,  764m, 3.31m),
        Leg(OptionRight.Call, LegAction.Sell, 766m, 1.80m, qty: 2),
        Leg(OptionRight.Call, LegAction.Buy,  768m, 0.81m),
    ];

    private static List<Dictionary<string, object>> LegsOf(Dictionary<string, object> p)
        => (List<Dictionary<string, object>>)p["orderLegCollection"];

    [Fact]
    public void NetPrice_IsPerSpreadPremiumExcludingCommission()
        => Assert.Equal(0.52m, SchwabOptionOrder.NetPrice(Butterfly()));

    [Fact]
    public void NetPrice_IsNegativeForACreditStructure()
        => Assert.Equal(-1.00m, SchwabOptionOrder.NetPrice(
            [Leg(OptionRight.Put, LegAction.Sell, 95m, 1.50m),
             Leg(OptionRight.Put, LegAction.Buy,  90m, 0.50m)]));

    [Fact]
    public void BuildPayload_EmitsOneOrderContainingEveryLeg()
    {
        var p = SchwabOptionOrder.BuildPayload(Butterfly());
        Assert.Equal("SINGLE", p["orderStrategyType"]);
        Assert.Equal(3, LegsOf(p).Count);
    }

    [Fact]
    public void BuildPayload_PricesADebitStructureAsNetDebit()
    {
        var p = SchwabOptionOrder.BuildPayload(Butterfly());
        Assert.Equal("NET_DEBIT", p["orderType"]);
        Assert.Equal(0.52m, p["price"]);   // positive — Schwab takes the magnitude
    }

    [Fact]
    public void BuildPayload_PricesACreditStructureAsNetCredit()
    {
        var p = SchwabOptionOrder.BuildPayload(
            [Leg(OptionRight.Put, LegAction.Sell, 95m, 1.50m),
             Leg(OptionRight.Put, LegAction.Buy,  90m, 0.50m)]);
        Assert.Equal("NET_CREDIT", p["orderType"]);
        Assert.Equal(1.00m, p["price"]);
    }

    [Fact]
    public void BuildPayload_UsesPlainLimitForASingleLeg()
    {
        var p = SchwabOptionOrder.BuildPayload([Leg(OptionRight.Call, LegAction.Buy, 764m, 3.31m)]);
        Assert.Equal("LIMIT", p["orderType"]);
        Assert.Equal(3.31m, p["price"]);
    }

    [Fact]
    public void BuildPayload_NeverUsesAMarketOrder()
    {
        // Market orders on multi-leg spreads fill badly and the damage compounds per leg.
        var p = SchwabOptionOrder.BuildPayload(Butterfly());
        Assert.DoesNotContain("MARKET", p["orderType"].ToString());
    }

    [Fact]
    public void BuildPayload_MapsBuyAndSellToOpeningInstructions()
    {
        var legs = LegsOf(SchwabOptionOrder.BuildPayload(Butterfly()));
        Assert.Equal("BUY_TO_OPEN",  legs[0]["instruction"]);
        Assert.Equal("SELL_TO_OPEN", legs[1]["instruction"]);
        Assert.Equal("BUY_TO_OPEN",  legs[2]["instruction"]);
    }

    [Fact]
    public void BuildPayload_CarriesTheChainSymbolVerbatim()
    {
        var legs = LegsOf(SchwabOptionOrder.BuildPayload(Butterfly()));
        var instrument = (Dictionary<string, object>)legs[0]["instrument"];
        Assert.Equal("SPY   260828C00764000", instrument["symbol"]);
        Assert.Equal("OPTION", instrument["assetType"]);
    }

    [Fact]
    public void BuildPayload_MultipliesLegRatiosByTheSpreadCount()
    {
        // 3 butterflies → leg ratio 1:2:1 becomes 3:6:3, and the net price is unchanged
        // because it is quoted per spread.
        var p = SchwabOptionOrder.BuildPayload(Butterfly(), spreads: 3);
        var legs = LegsOf(p);
        Assert.Equal([3, 6, 3], legs.Select(l => (int)l["quantity"]));
        Assert.Equal(0.52m, p["price"]);
    }

    [Fact]
    public void BuildPayload_DefaultsToDayDuration()
        => Assert.Equal("DAY", SchwabOptionOrder.BuildPayload(Butterfly())["duration"]);

    [Fact]
    public void BuildPayload_RejectsNonPositiveSpreadCount()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => SchwabOptionOrder.BuildPayload(Butterfly(), spreads: 0));

    [Fact]
    public void BuildPayload_RejectsAnEmptyStructure()
        => Assert.Throws<ArgumentException>(() => SchwabOptionOrder.BuildPayload([]));

    // ── Closing an open structure ──────────────────────────────────

    [Fact]
    public void BuildClosePayload_SellsToCloseALongLeg()
    {
        var legs = LegsOf(SchwabOptionOrder.BuildClosePayload(
            [Leg(OptionRight.Call, LegAction.Buy, 764m, 4.10m)]));
        Assert.Equal("SELL_TO_CLOSE", Assert.Single(legs)["instruction"]);
    }

    [Fact]
    public void BuildClosePayload_BuysToCloseAShortLeg()
    {
        var legs = LegsOf(SchwabOptionOrder.BuildClosePayload(
            [Leg(OptionRight.Put, LegAction.Sell, 95m, 0.40m)]));
        Assert.Equal("BUY_TO_CLOSE", Assert.Single(legs)["instruction"]);
    }

    [Fact]
    public void BuildClosePayload_InvertsEveryLegOfAButterfly()
    {
        var legs = LegsOf(SchwabOptionOrder.BuildClosePayload(Butterfly()));
        Assert.Equal(["SELL_TO_CLOSE", "BUY_TO_CLOSE", "SELL_TO_CLOSE"],
                     legs.Select(l => (string)l["instruction"]));
    }

    [Fact]
    public void BuildClosePayload_TurnsADebitStructureIntoAClosingCredit()
    {
        // Opened for 0.52 debit; closing at the same prices returns that 0.52 as a credit.
        var p = SchwabOptionOrder.BuildClosePayload(Butterfly());
        Assert.Equal("NET_CREDIT", p["orderType"]);
        Assert.Equal(0.52m, p["price"]);
    }

    [Fact]
    public void BuildClosePayload_KeepsSymbolsAndScalesBySpreadCount()
    {
        var legs = LegsOf(SchwabOptionOrder.BuildClosePayload(Butterfly(), spreads: 3));
        Assert.Equal([3, 6, 3], legs.Select(l => (int)l["quantity"]));
        var instrument = (Dictionary<string, object>)legs[0]["instrument"];
        Assert.Equal("SPY   260828C00764000", instrument["symbol"]);
    }

    [Fact]
    public void BuildClosePayload_IsNeverAMarketOrder()
    {
        // Closing at market is how a defined-risk winner turns into a scratch.
        var p = SchwabOptionOrder.BuildClosePayload(Butterfly());
        Assert.DoesNotContain("MARKET", p["orderType"].ToString());
    }

    [Fact]
    public void OpenThenClose_AreEqualInMagnitudeAndOppositeInSign()
    {
        var open  = SchwabOptionOrder.BuildPayload(Butterfly());
        var close = SchwabOptionOrder.BuildClosePayload(Butterfly());
        Assert.Equal(open["price"], close["price"]);
        Assert.Equal("NET_DEBIT",  open["orderType"]);
        Assert.Equal("NET_CREDIT", close["orderType"]);
    }

    // ── Cash effect of closing ─────────────────────────────────────
    // Live SPY quotes: 775c bid 1.95, 770c ask 4.02.

    [Fact]
    public void CloseProceeds_IsPositiveWhenSellingALongLeg()
        => Assert.Equal(194.35m, SchwabOptionOrder.CloseProceeds(
            [Leg(OptionRight.Call, LegAction.Buy, 775m, 1.95m)], commissionPerContract: 0.65m));

    [Fact]
    public void CloseProceeds_IsNegativeWhenTheCloseCostsMoney()
    {
        // Long 775c worth 1.95, short 770c costs 4.02 to buy back → you pay 2.07 net,
        // plus 2 contracts of commission. Schwab's own preview of this order reports
        // orderValue 207.00 and projectedCommission 1.30.
        var proceeds = SchwabOptionOrder.CloseProceeds(
        [
            Leg(OptionRight.Call, LegAction.Buy,  775m, 1.95m),
            Leg(OptionRight.Call, LegAction.Sell, 770m, 4.02m),
        ], commissionPerContract: 0.65m);

        Assert.Equal(-208.30m, proceeds);
        Assert.True(proceeds < 0m, "closing this position costs money and must not read as a credit");
    }

    [Fact]
    public void CloseProceeds_AppliesTheContractMultiplier()
        => Assert.Equal(195m, SchwabOptionOrder.CloseProceeds(
            [Leg(OptionRight.Call, LegAction.Buy, 775m, 1.95m)]));

    // ── Custom limit price ─────────────────────────────────────────

    [Fact]
    public void LimitPrice_OverridesThePriceDerivedFromTheMarket()
    {
        // The butterfly is 0.52 at the market; we will only pay 0.30.
        var p = SchwabOptionOrder.BuildPayload(Butterfly(), limitPrice: 0.30m);
        Assert.Equal(0.30m, p["price"]);
        Assert.Equal("NET_DEBIT", p["orderType"]);
    }

    [Fact]
    public void LimitPrice_DecidesTheOrderTypeRatherThanTheMarket()
    {
        // Market says debit, but a limit asking to be PAID for it is a credit order.
        var p = SchwabOptionOrder.BuildPayload(Butterfly(), limitPrice: -0.10m);
        Assert.Equal("NET_CREDIT", p["orderType"]);
        Assert.Equal(0.10m, p["price"]);
    }

    [Fact]
    public void LimitPrice_AppliesToASingleLegToo()
        => Assert.Equal(1.00m, SchwabOptionOrder.BuildPayload(
            [Leg(OptionRight.Call, LegAction.Buy, 764m, 2.00m)], limitPrice: 1.00m)["price"]);

    // ── Attached exit (One-Triggers-Other) ─────────────────────────

    private static Dictionary<string, object> Child(Dictionary<string, object> p)
        => ((List<Dictionary<string, object>>)p["childOrderStrategies"]).Single();

    [Fact]
    public void WithoutAnExit_TheOrderStaysASingleStrategy()
    {
        var p = SchwabOptionOrder.BuildPayload(Butterfly());
        Assert.Equal("SINGLE", p["orderStrategyType"]);
        Assert.False(p.ContainsKey("childOrderStrategies"));
    }

    [Fact]
    public void WithAnExit_TheOrderBecomesATrigger()
    {
        var p = SchwabOptionOrder.BuildPayload(Butterfly(), exit: new AttachedExit(1.50m));
        Assert.Equal("TRIGGER", p["orderStrategyType"]);
        Assert.Single((List<Dictionary<string, object>>)p["childOrderStrategies"]);
    }

    [Fact]
    public void AttachedExit_ClosesEveryLegAtTheTargetPrice()
    {
        // Buy the fly for 1.00, take profit at 3.00.
        var p = SchwabOptionOrder.BuildPayload(
            Butterfly(), limitPrice: 1.00m, exit: new AttachedExit(3.00m));

        Assert.Equal(1.00m, p["price"]);

        var child = Child(p);
        Assert.Equal(3.00m, child["price"]);
        Assert.Equal("NET_CREDIT", child["orderType"]);
        Assert.Equal(["SELL_TO_CLOSE", "BUY_TO_CLOSE", "SELL_TO_CLOSE"],
            ((List<Dictionary<string, object>>)child["orderLegCollection"])
                .Select(l => (string)l["instruction"]));
    }

    [Fact]
    public void AttachedExit_ScalesWithTheSpreadCount()
    {
        var child = Child(SchwabOptionOrder.BuildPayload(
            Butterfly(), spreads: 3, exit: new AttachedExit(3.00m)));
        Assert.Equal([3, 6, 3],
            ((List<Dictionary<string, object>>)child["orderLegCollection"])
                .Select(l => (int)l["quantity"]));
    }

    [Fact]
    public void AttachedExit_DefaultsToGoodTillCancel()
    {
        // A day-only exit would quietly expire and leave the position unprotected.
        var child = Child(SchwabOptionOrder.BuildPayload(Butterfly(), exit: new AttachedExit(3.00m)));
        Assert.Equal("GOOD_TILL_CANCEL", child["duration"]);
    }
}
