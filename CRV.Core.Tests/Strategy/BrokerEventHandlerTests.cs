namespace CRV.Core.Tests.Strategy;

using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

public class BrokerEventHandlerTests
{
    // ── Test doubles ────────────────────────────────────────────

    private class FakeGroupExecutor : IGroupOrderExecutor
    {
        public List<(string orderId, decimal? price, int? qty)> Modifications { get; } = new();
        public List<string> Cancellations { get; } = new();
        public List<(string ticker, Direction dir, int qty)> MarketCloses { get; } = new();

        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal signal) =>
            Task.FromResult<GroupOrder?>(null);

        public Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
        {
            Modifications.Add((orderId, newPrice, newQty));
            return Task.CompletedTask;
        }

        public Task CancelOrderAsync(string orderId)
        {
            Cancellations.Add(orderId);
            return Task.CompletedTask;
        }

        public Task PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
        {
            MarketCloses.Add((ticker, direction, qty));
            return Task.CompletedTask;
        }
    }

    private class FakeSetup : ISetupStrategy
    {
        public string Id { get; set; } = "A";
        public SetupId SetupId { get; set; } = SetupId.A;
        public StrategyType StrategyType => StrategyType.Pullback;
        public string Name => "Test";
        public bool IsActive => false;
        public bool IsArmed => false;
        public bool InTrade { get; set; }
        public int CutoffHour => 16;
        public int CutoffMinute => 0;
        public string Ticker => "/NQH2026";
        public decimal PointValue => 20m;
        public (int, int) GetCutoffForSession(string s) => (16, 0);
        public bool IsEnabledForSession(string s) => true;
        public void OnBar(Bar b, OrbState o, IndicatorState i, ModuleState m) { }
        public void OnTick(decimal p, DateTime u, OrbState o, IndicatorState i, ModuleState m) { }
        public void Reconfigure(StrategySetupConfig c) { }
        public void Reset() { }
        public void ResetSession() { }
        public void ResetTradeCounters() { }
        public EntrySignal? PendingEntry => null;
        public void ClearPendingSignals() { }
        public void RevertEntry() { }
        public void ForceExit(decimal p, DateTime t, ExitReason r = ExitReason.SessionEnd) { }
        public void Disarm() { }
        public void ResetCutoff() { }
        public SetupStateSnapshot GetSnapshot() => new();
        public void SetInTrade(bool active) => InTrade = active;
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static GroupOrder MakeGroup(string setupId = "A", int total = 4, int partial = 2)
    {
        var group = new GroupOrder
        {
            GroupOrderId = "grp-001",
            SetupId = setupId,
            Ticker = "/NQH2026",
            Direction = Direction.Long,
            TotalContracts = total,
            PartialContracts = partial,
            PointValue = 20m,
            Status = GroupOrderStatus.Pending,
            Broker = "Mock",
        };
        group.Legs.Add(new OrderLeg { GroupOrderId = "grp-001", OrderId = "e1", LegType = LegType.Entry, OrderType = "Market", Action = "BUY", Quantity = total, Price = 20000m });
        group.Legs.Add(new OrderLeg { GroupOrderId = "grp-001", OrderId = "t1", LegType = LegType.Tg1, OrderType = "Limit", Action = "SELL", Quantity = partial, Price = 20050m });
        group.Legs.Add(new OrderLeg { GroupOrderId = "grp-001", OrderId = "t2", LegType = LegType.Tg2, OrderType = "Limit", Action = "SELL", Quantity = total - partial, Price = 20100m });
        group.Legs.Add(new OrderLeg { GroupOrderId = "grp-001", OrderId = "s1", LegType = LegType.Stop, OrderType = "Stop", Action = "SELL", Quantity = total, Price = 19950m });
        return group;
    }

    private static OrderEvent Evt(string groupId, string orderId, LegType leg, OrderLegStatus status,
        decimal? fill = null, int? qty = null) =>
        new(groupId, orderId, leg, status, fill, qty, null, null, DateTime.UtcNow);

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task EntryFilled_SetsGroupActive_AndEntryPrice()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20001.25m, 4));

        Assert.Equal(GroupOrderStatus.Active, group.Status);
        Assert.Equal(20001.25m, group.EntryPrice);
        Assert.True(strategy.InTrade);
    }

    [Fact]
    public async Task Tg1Filled_MovesStopToBE_AndReducesQty()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        // Entry fills
        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));
        // Tg1 fills
        await handler.HandleEventAsync(Evt("grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 2));

        Assert.Equal(GroupOrderStatus.PartialFilled, group.Status);
        Assert.Single(exec.Modifications);
        var mod = exec.Modifications[0];
        Assert.Equal("s1", mod.orderId);
        Assert.Equal(20000m, mod.price);  // BE = entry price
        Assert.Equal(2, mod.qty);          // remaining contracts
    }

    [Fact]
    public async Task Tg1Filled_AccruesPartialPnl()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));
        await handler.HandleEventAsync(Evt("grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 2));

        // 50 pts * 20 PV * 2 cts = 2000
        Assert.Equal(2000m, group.AccruedPartialPnl);
    }

    [Fact]
    public async Task Tg2Filled_CancelsStop_CompletesGroup()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));
        await handler.HandleEventAsync(Evt("grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 2));
        await handler.HandleEventAsync(Evt("grp-001", "t2", LegType.Tg2, OrderLegStatus.Filled, 20100m, 2));

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        Assert.Contains("s1", exec.Cancellations);
        Assert.False(strategy.InTrade);
    }

    [Fact]
    public async Task StopFilled_CancelsTg1Tg2_CompletesGroup()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));
        await handler.HandleEventAsync(Evt("grp-001", "s1", LegType.Stop, OrderLegStatus.Filled, 19950m, 4));

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        Assert.Contains("t1", exec.Cancellations);
        Assert.Contains("t2", exec.Cancellations);
        Assert.False(strategy.InTrade);
    }

    [Fact]
    public async Task StopFilledAfterPartial_CancelsTg2Only()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));
        await handler.HandleEventAsync(Evt("grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 2));

        exec.Cancellations.Clear();
        await handler.HandleEventAsync(Evt("grp-001", "s1", LegType.Stop, OrderLegStatus.Filled, 20000m, 2));

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        // tg1 already filled, only tg2 should be canceled
        Assert.Contains("t2", exec.Cancellations);
        Assert.DoesNotContain("t1", exec.Cancellations);
    }

    [Fact]
    public async Task ExitGroup_Pending_CancelsAllLegs()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        await handler.ExitGroupAsync("A");

        Assert.Equal(GroupOrderStatus.Canceled, group.Status);
        Assert.Equal(4, exec.Cancellations.Count);
    }

    [Fact]
    public async Task ExitGroup_Active_MarketCloses_AndCancelsLegs()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));

        await handler.ExitGroupAsync("A");

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        Assert.Single(exec.MarketCloses);
        Assert.Equal(4, exec.MarketCloses[0].qty);
    }

    [Fact]
    public async Task ExitGroup_PartialFilled_MarketCloses_RemainingQty()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));
        await handler.HandleEventAsync(Evt("grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 2));

        exec.Modifications.Clear();
        exec.Cancellations.Clear();

        await handler.ExitGroupAsync("A");

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        Assert.Single(exec.MarketCloses);
        Assert.Equal(2, exec.MarketCloses[0].qty);
    }

    [Fact]
    public void GetUnrealizedPnl_Long_CalculatesCorrectly()
    {
        var exec = new FakeGroupExecutor();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        group.Status = GroupOrderStatus.Active;
        group.EntryPrice = 20000m;

        var strategy = new FakeSetup();
        handler.RegisterGroup(group, strategy);

        // 25 pts * 20 PV * 4 cts = $2000
        var pnl = handler.GetUnrealizedPnl("A", 20025m);
        Assert.Equal(2000m, pnl);
    }

    [Fact]
    public void GetUnrealizedPnl_Short_CalculatesCorrectly()
    {
        var exec = new FakeGroupExecutor();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        group.Direction = Direction.Short;
        group.Status = GroupOrderStatus.Active;
        group.EntryPrice = 20000m;

        var strategy = new FakeSetup();
        handler.RegisterGroup(group, strategy);

        // Short: entry - price = 20000 - 19975 = 25 pts * 20 PV * 4 = 2000
        var pnl = handler.GetUnrealizedPnl("A", 19975m);
        Assert.Equal(2000m, pnl);
    }

    [Fact]
    public void GetUnrealizedPnl_AfterPartial_IncludesAccrued()
    {
        var exec = new FakeGroupExecutor();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        group.Status = GroupOrderStatus.PartialFilled;
        group.EntryPrice = 20000m;
        group.AccruedPartialPnl = 2000m;

        var strategy = new FakeSetup();
        handler.RegisterGroup(group, strategy);

        // Remaining 2 cts: 25 * 20 * 2 = 1000 + 2000 accrued = 3000
        var pnl = handler.GetUnrealizedPnl("A", 20025m);
        Assert.Equal(3000m, pnl);
    }

    [Fact]
    public async Task EntryRejected_CancelsAllLegs_SetsGroupCanceled()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Rejected));

        Assert.Equal(GroupOrderStatus.Canceled, group.Status);
        Assert.False(strategy.InTrade);
        // Should cancel tg1, tg2, stop (3 working legs remaining)
        Assert.Equal(3, exec.Cancellations.Count);
    }

    [Fact]
    public async Task TradeCompleted_FiresEvent_WithTradeRecord()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        TradeRecord? completedTrade = null;
        handler.OnTradeCompleted += (g, t) => completedTrade = t;

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));
        await handler.HandleEventAsync(Evt("grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 2));
        await handler.HandleEventAsync(Evt("grp-001", "t2", LegType.Tg2, OrderLegStatus.Filled, 20100m, 2));

        Assert.NotNull(completedTrade);
        Assert.Equal(ExitReason.Target, completedTrade!.ExitReason);
        Assert.Equal(Direction.Long, completedTrade.Direction);
        Assert.Equal(20000m, completedTrade.Entry);
        Assert.Equal(20100m, completedTrade.Exit);
    }

    [Fact]
    public async Task ExitAll_ClosesAllActiveGroups()
    {
        var exec = new FakeGroupExecutor();
        var handler = new BrokerEventHandler(exec);

        var stratA = new FakeSetup { Id = "A" };
        var groupA = MakeGroup("A");
        handler.RegisterGroup(groupA, stratA);

        var groupB = new GroupOrder
        {
            GroupOrderId = "grp-002", SetupId = "B", Ticker = "/NQH2026",
            Direction = Direction.Short, TotalContracts = 2, PartialContracts = 1,
            PointValue = 20m, Status = GroupOrderStatus.Pending, Broker = "Mock",
        };
        groupB.Legs.Add(new OrderLeg { GroupOrderId = "grp-002", OrderId = "e2", LegType = LegType.Entry, Action = "SELL", Quantity = 2, Price = 20000m });
        var stratB = new FakeSetup { Id = "B", SetupId = SetupId.B };
        handler.RegisterGroup(groupB, stratB);

        await handler.ExitAllAsync();

        Assert.Equal(GroupOrderStatus.Canceled, groupA.Status);
        Assert.Equal(GroupOrderStatus.Canceled, groupB.Status);
        Assert.Null(handler.GetActiveGroup("A"));
        Assert.Null(handler.GetActiveGroup("B"));
    }

    [Fact]
    public void HasActiveGroup_ReturnsFalse_WhenEmpty()
    {
        var exec = new FakeGroupExecutor();
        var handler = new BrokerEventHandler(exec);

        Assert.False(handler.HasActiveGroup("A"));
    }

    [Fact]
    public async Task HasActiveGroup_ReturnsFalse_AfterCompletion()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        handler.RegisterGroup(group, strategy);

        Assert.True(handler.HasActiveGroup("A"));

        await handler.HandleEventAsync(Evt("grp-001", "e1", LegType.Entry, OrderLegStatus.Filled, 20000m, 4));
        await handler.HandleEventAsync(Evt("grp-001", "s1", LegType.Stop, OrderLegStatus.Filled, 19950m, 4));

        Assert.False(handler.HasActiveGroup("A"));
    }
}
