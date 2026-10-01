using CRV.Core.Options;
using CRV.Live.Brokers.Schwab;
using Xunit;

namespace CRV.Core.Tests.Options;

/// <summary>
/// The opening order the Explorer previews and places. Preview and placement used to build
/// it separately; placement dropped the stop and both ignored the chosen duration.
/// </summary>
public class OptionEntryOrderTests
{
    private static OptionLeg Leg(OptionRight r, LegAction a, decimal k, decimal prem, int qty = 1)
        => new(r, a, k, prem, qty, 100, $"SPY   260828{(r == OptionRight.Call ? "C" : "P")}{(int)(k * 1000):D8}");

    private static OptionLeg[] LongCall() => [Leg(OptionRight.Call, LegAction.Buy, 766m, 2.00m)];

    // 2-wide call vertical: +764c @3.00, −766c @2.00 → 1.00 debit; worth at most 2.00.
    private static OptionLeg[] Vertical() =>
    [
        Leg(OptionRight.Call, LegAction.Buy,  764m, 3.00m),
        Leg(OptionRight.Call, LegAction.Sell, 766m, 2.00m),
    ];

    private static List<Dictionary<string, object>> Children(Dictionary<string, object> p)
        => (List<Dictionary<string, object>>)p["childOrderStrategies"];

    [Fact]
    public void ASingleLegOrderCarriesItsStop()
    {
        var p = OptionEntryOrder.Payload(LongCall(), 1, OrderDuration.Day, 2.00m, exitPrice: null, stopTrigger: 1.00m);

        Assert.Equal("TRIGGER", p["orderStrategyType"]);
        var stop = Assert.Single(Children(p));
        Assert.Equal("STOP_LIMIT", stop["orderType"]);
        Assert.Equal(1.00m, stop["stopPrice"]);
        Assert.Equal(0.90m, stop["price"]);   // limit 10% through the trigger
    }

    [Fact]
    public void AStopAndAnExitAreAlternatives()
    {
        var p = OptionEntryOrder.Payload(LongCall(), 1, OrderDuration.Day, 2.00m, exitPrice: 3.00m, stopTrigger: 1.00m);

        var oco = Assert.Single(Children(p));
        Assert.Equal("OCO", oco["orderStrategyType"]);
        Assert.Equal(2, Children(oco).Count);
    }

    [Fact]
    public void ASpreadGetsNoStop()
    {
        // Schwab has no net-stop order type; the preview says so and nothing is attached.
        Assert.Null(OptionEntryOrder.Stop(1.00m, legCount: 2));
        var p = OptionEntryOrder.Payload(Vertical(), 1, OrderDuration.Day, 1.00m, exitPrice: null, stopTrigger: 0.50m);
        Assert.Equal("SINGLE", p["orderStrategyType"]);
    }

    [Theory]
    [InlineData(OrderDuration.Day,            "DAY")]
    [InlineData(OrderDuration.GoodTillCancel, "GOOD_TILL_CANCEL")]
    [InlineData(OrderDuration.FillOrKill,     "FILL_OR_KILL")]
    public void TheEntryUsesTheChosenDuration(OrderDuration duration, string expected)
    {
        var p = OptionEntryOrder.Payload(LongCall(), 1, duration, 2.00m, exitPrice: null, stopTrigger: null);
        Assert.Equal(expected, p["duration"]);
    }

    [Fact]
    public void TheExitAndStopStayUntilCancelledWhateverTheEntrysDuration()
    {
        // A Day entry that fills must not leave its protection expiring at the close.
        var p = OptionEntryOrder.Payload(LongCall(), 1, OrderDuration.Day, 2.00m, exitPrice: 3.00m, stopTrigger: 1.00m);
        var oco = Assert.Single(Children(p));
        Assert.All(Children(oco), c => Assert.Equal("GOOD_TILL_CANCEL", c["duration"]));
    }

    [Theory]
    [InlineData(null,             true,  OrderDuration.Day)]
    [InlineData("",               true,  OrderDuration.Day)]
    [InlineData("GoodTillCancel", true,  OrderDuration.GoodTillCancel)]
    [InlineData("filLorKILL",     true,  OrderDuration.FillOrKill)]
    [InlineData("Week",           false, OrderDuration.Day)]
    [InlineData("7",              false, OrderDuration.Day)]
    public void DurationParsingRefusesWhatItDoesNotKnow(string? value, bool ok, OrderDuration expected)
    {
        Assert.Equal(ok, OptionEntryOrder.TryParseDuration(value, out var d));
        if (ok) Assert.Equal(expected, d);
    }

    [Fact]
    public void AnExitAboveWhatTheSpreadCanBeWorthIsUnreachable()
    {
        Assert.Equal(2.00m, OptionEntryOrder.MaxStructureValue(Vertical()));
        Assert.True(OptionEntryOrder.ExitUnreachable(Vertical(), 2.50m));
        Assert.False(OptionEntryOrder.ExitUnreachable(Vertical(), 1.80m));
        Assert.False(OptionEntryOrder.ExitUnreachable(Vertical(), null));
    }

    [Fact]
    public void ALongCallHasNoCeilingSoAnyExitIsReachable()
    {
        Assert.Null(OptionEntryOrder.MaxStructureValue(LongCall()));
        Assert.False(OptionEntryOrder.ExitUnreachable(LongCall(), 50m));
    }
}
