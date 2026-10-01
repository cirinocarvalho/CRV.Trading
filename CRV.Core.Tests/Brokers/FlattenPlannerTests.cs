namespace CRV.Core.Tests.Brokers;

using CRV.Core.Models;
using CRV.Live;
using Xunit;

public class FlattenPlannerTests
{
    private static GroupOrder Group(string id, string ticker, GroupOrderStatus status, int cts = 2, int partial = 0) => new()
    {
        GroupOrderId = id, SetupId = "retest-mnq", Ticker = ticker, Direction = Direction.Long,
        TotalContracts = cts, PartialContracts = partial, Status = status,
    };

    private static PositionView Pos(string sym, string dir, decimal qty, string asset = "FUTURE") =>
        new(sym, dir, qty, 100m, null, null, asset);

    private static OrderView Order(string sym, bool canCancel = true) =>
        new("o-" + sym, sym, "WORKING", "Working", "LIMIT", "SELL", 1, 1m, null, "", canCancel);

    [Fact]
    public void TrackedInstrument_IsClosedByTheEngineOnly()
    {
        // The broker still reports the position while the engine's exit is in flight.
        var plan = FlattenPlanner.Build(
            new[] { Group("g1", "MNQZ26", GroupOrderStatus.Active) },
            new[] { Pos("/MNQZ26", "LONG", 2) },
            new[] { Order("/MNQZ26") });

        Assert.Single(plan.Groups);
        Assert.Empty(plan.Positions);
        Assert.Empty(plan.OrderSymbols);
    }

    [Fact]
    public void UntrackedFuturesPosition_IsClosedAtTheBroker()
    {
        var plan = FlattenPlanner.Build(
            Array.Empty<GroupOrder>(),
            new[] { Pos("/MGCZ26", "SHORT", 1) },
            Array.Empty<OrderView>());

        var p = Assert.Single(plan.Positions);
        Assert.Equal("/MGCZ26", p.Symbol);
        Assert.False(p.IsLong);
        Assert.Equal(1, p.Quantity);
    }

    [Fact]
    public void OptionsPositions_AreLeftAlone()
    {
        var plan = FlattenPlanner.Build(
            Array.Empty<GroupOrder>(),
            new[] { Pos("SPY   261017C00580000", "LONG", 2, "OPTION") },
            new[] { Order("SPY   261017C00585000") });

        Assert.Empty(plan.Positions);
        Assert.Empty(plan.OrderSymbols);
        Assert.Single(plan.LeftAlone);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void WorkingOrdersWithoutPosition_AreCancelledOncePerInstrument()
    {
        var plan = FlattenPlanner.Build(
            Array.Empty<GroupOrder>(),
            new[] { Pos("/MGCZ26", "LONG", 1) },
            new[] { Order("/MESZ26"), Order("MESZ6"), Order("/MGCZ26"), Order("/MYMZ26", canCancel: false) });

        Assert.Equal(new[] { "/MESZ26" }, plan.OrderSymbols);   // MGC is covered by its position; MYM isn't cancellable
    }

    [Fact]
    public void FinishedGroups_AreIgnored_AndPartialFillsCountTheRemainder()
    {
        var plan = FlattenPlanner.Build(
            new[]
            {
                Group("done", "MNQZ26", GroupOrderStatus.Completed),
                Group("part", "MESZ26", GroupOrderStatus.PartialFilled, cts: 3, partial: 1),
            },
            Array.Empty<PositionView>(),
            Array.Empty<OrderView>());

        var g = Assert.Single(plan.Groups);
        Assert.Equal("part", g.GroupOrderId);
        Assert.Equal(2, g.Contracts);
    }

    [Theory]
    [InlineData("/MNQZ26", true)]
    [InlineData("MNQZ6", true)]
    [InlineData("M2KH27", true)]
    [InlineData("SPY", false)]
    [InlineData("SPY   261017C00580000", false)]
    public void IsFutureSymbol(string sym, bool expected) => Assert.Equal(expected, FlattenPlanner.IsFutureSymbol(sym));
}
