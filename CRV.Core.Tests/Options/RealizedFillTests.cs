using CRV.Live.Brokers.Schwab;
using Xunit;

namespace CRV.Core.Tests.Options;

/// <summary>
/// Payloads here are shaped from Schwab's documented order schema but are SYNTHETIC — no
/// order placed by this app has filled yet, so the parser has never seen a real execution.
/// These pin the arithmetic; they do not prove the field names match production.
/// </summary>
public class RealizedFillTests
{
    private static string Order(string legs, string activity) => $$"""
        { "orderLegCollection": [ {{legs}} ],
          "orderActivityCollection": [ {{activity}} ] }
        """;

    private const string SingleBuyLeg =
        """{ "legId": 1, "instruction": "BUY_TO_OPEN", "quantity": 1.0 }""";

    [Fact]
    public void UnfilledOrder_ReturnsNull()
        => Assert.Null(SchwabOptionOrder.ParseRealizedFill(
            $$"""{ "orderLegCollection": [ {{SingleBuyLeg}} ], "orderActivityCollection": [] }"""));

    [Fact]
    public void SingleLeg_ReportsThePricePaid()
    {
        var f = SchwabOptionOrder.ParseRealizedFill(Order(SingleBuyLeg, """
            { "executionType": "FILL", "executionLegs": [
                { "legId": 1, "quantity": 1.0, "price": 3.55, "time": "2026-09-04T18:30:00+0000" } ] }
            """));
        Assert.NotNull(f);
        Assert.Equal(3.55m, f!.NetPerUnit);
        Assert.Equal(1, f.UnitsFilled);
        Assert.Equal(new DateTime(2026, 9, 4, 18, 30, 0, DateTimeKind.Utc), f.FilledAtUtc);
    }

    [Fact]
    public void SoldLeg_IsACreditAndCarriesTheOppositeSign()
    {
        var f = SchwabOptionOrder.ParseRealizedFill(Order(
            """{ "legId": 1, "instruction": "SELL_TO_OPEN", "quantity": 1.0 }""", """
            { "executionType": "FILL", "executionLegs": [
                { "legId": 1, "quantity": 1.0, "price": 2.10 } ] }
            """));
        Assert.Equal(-2.10m, f!.NetPerUnit);
    }

    [Fact]
    public void Butterfly_NetsTheLegsAtTheirRatios()
    {
        // +1 @7.00, -2 @3.50, +1 @1.50 → 7.00 - 7.00 + 1.50 = 1.50 per fly
        var f = SchwabOptionOrder.ParseRealizedFill(Order("""
            { "legId": 1, "instruction": "BUY_TO_OPEN",  "quantity": 1.0 },
            { "legId": 2, "instruction": "SELL_TO_OPEN", "quantity": 2.0 },
            { "legId": 3, "instruction": "BUY_TO_OPEN",  "quantity": 1.0 }
            """, """
            { "executionType": "FILL", "executionLegs": [
                { "legId": 1, "quantity": 1.0, "price": 7.00 },
                { "legId": 2, "quantity": 2.0, "price": 3.50 },
                { "legId": 3, "quantity": 1.0, "price": 1.50 } ] }
            """));
        Assert.Equal(1.50m, f!.NetPerUnit);
        Assert.Equal(1, f.UnitsFilled);
    }

    [Fact]
    public void MultipleContracts_ArePricedPerUnitNotInTotal()
    {
        // Three of the same put. Net per unit stays the per-contract price.
        var f = SchwabOptionOrder.ParseRealizedFill(Order(
            """{ "legId": 1, "instruction": "BUY_TO_OPEN", "quantity": 3.0 }""", """
            { "executionType": "FILL", "executionLegs": [
                { "legId": 1, "quantity": 3.0, "price": 0.11 } ] }
            """));
        Assert.Equal(0.11m, f!.NetPerUnit);
        Assert.Equal(3, f.UnitsFilled);
    }

    [Fact]
    public void PartialFillsOnOneLeg_AreAveragedByQuantity()
    {
        // 1 @ 3.00 then 3 @ 4.00 → weighted average 3.75, not the midpoint 3.50.
        var f = SchwabOptionOrder.ParseRealizedFill(Order(
            """{ "legId": 1, "instruction": "BUY_TO_OPEN", "quantity": 4.0 }""", """
            { "executionType": "FILL", "executionLegs": [
                { "legId": 1, "quantity": 1.0, "price": 3.00 } ] },
            { "executionType": "FILL", "executionLegs": [
                { "legId": 1, "quantity": 3.0, "price": 4.00 } ] }
            """));
        Assert.Equal(3.75m, f!.NetPerUnit);
    }

    [Fact]
    public void SpreadFilledOnOnlyOneLeg_IsNotReportedAsAFill()
    {
        // Reporting a net for a half-filled spread would invent a price that never traded.
        var f = SchwabOptionOrder.ParseRealizedFill(Order("""
            { "legId": 1, "instruction": "BUY_TO_OPEN",  "quantity": 1.0 },
            { "legId": 2, "instruction": "SELL_TO_OPEN", "quantity": 1.0 }
            """, """
            { "executionType": "FILL", "executionLegs": [
                { "legId": 1, "quantity": 1.0, "price": 5.00 } ] }
            """));
        Assert.Null(f);
    }

    [Fact]
    public void SlippageIsTheDifferenceFromWhatWasAsked()
    {
        // The whole point of recording this: asked 1.50, filled 1.62 → 0.12 worse per unit.
        var f = SchwabOptionOrder.ParseRealizedFill(Order(SingleBuyLeg, """
            { "executionType": "FILL", "executionLegs": [
                { "legId": 1, "quantity": 1.0, "price": 1.62 } ] }
            """));
        Assert.Equal(0.12m, f!.NetPerUnit - 1.50m);
    }
}
