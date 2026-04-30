namespace CRV.Core.Tests.Strategy;

using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

/// <summary>
/// Tests for the dynamic N-bracket feature (Tg1..Tg4).
/// Covers EntrySignal bracket resolution, GroupOrder target iteration,
/// and BrokerEventHandler's unified target handler for 3+ bracket orders.
/// </summary>
public class NBracketTests
{
    // ── Reuse the same fakes as BrokerEventHandlerTests via a mini copy ─

    private class FakeGroupExecutor : IGroupOrderExecutor
    {
        public List<(string orderId, decimal? price, int? qty)> Modifications { get; } = new();
        public List<string> Cancellations { get; } = new();

        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal signal) => Task.FromResult<GroupOrder?>(null);
        public Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
        { Modifications.Add((orderId, newPrice, newQty)); return Task.CompletedTask; }
        public Task CancelOrderAsync(string orderId) { Cancellations.Add(orderId); return Task.CompletedTask; }
        public Task<decimal> PlaceMarketCloseAsync(string t, Direction d, int q) => Task.FromResult(0m);
        public Dictionary<string, decimal> FillPrices { get; } = new();
        public Task<decimal?> GetOrderFillPriceAsync(string orderId) =>
            Task.FromResult(FillPrices.TryGetValue(orderId, out var p) ? (decimal?)p : null);
    }

    private class FakeSetup : ISetupStrategy
    {
        public string Id { get; set; } = "F";
        public SetupId SetupId { get; set; } = SetupId.F;
        public StrategyType StrategyType => StrategyType.Pullback;
        public string Name => "Test";
        public bool IsActive => false;
        public bool IsArmed => false;
        public bool InTrade { get; set; }
        public int CutoffHour => 16;
        public int CutoffMinute => 0;
        public string Ticker => "/NQH2026";
        public decimal PointValue => 20m;
        public TimeOnly OrbStart => new(9, 30);
        public TimeOnly OrbEnd   => new(10, 0);
        public bool UseEmaFilter => false;
        public bool BypassChopFilter => false;
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
        public void SeedTradeCount(int l, int s) { }
    }

    private static OrderEvent Evt(string groupId, string orderId, LegType leg, OrderLegStatus status,
        decimal? fill = null, int? qty = null) =>
        new(groupId, orderId, leg, status, fill, qty, null, null, DateTime.UtcNow);

    /// <summary>
    /// Build a group with N target legs at sequential prices. UseBe=true, entry=20000.
    /// Stops for brackets 1..N-1 are queued in PendingStopIds; bracket 0's stop is the primary.
    /// </summary>
    private static GroupOrder MakeNBracketGroup(int[] qtys, decimal[] targets)
    {
        Assert.Equal(qtys.Length, targets.Length);
        var total = qtys.Sum();
        var group = new GroupOrder
        {
            GroupOrderId = "grp-n",
            SetupId = "F",
            Ticker = "/NQH2026",
            Direction = Direction.Long,
            TotalContracts = total,
            PartialContracts = qtys[0],
            PointValue = 20m,
            UseBe = true,
            EntryPrice = 20000m,
            Status = GroupOrderStatus.Active,
            Broker = "Mock",
        };
        group.Legs.Add(new OrderLeg { GroupOrderId = "grp-n", OrderId = "e1", LegType = LegType.Entry, OrderType = "Market", Action = "BUY", Quantity = total, Price = 20000m, Status = OrderLegStatus.Filled, FillPrice = 20000m });
        var targetLegTypes = new[] { LegType.Tg1, LegType.Tg2, LegType.Tg3, LegType.Tg4 };
        for (int i = 0; i < qtys.Length; i++)
        {
            group.Legs.Add(new OrderLeg
            {
                GroupOrderId = "grp-n",
                OrderId = $"t{i + 1}",
                LegType = targetLegTypes[i],
                OrderType = "Limit",
                Action = "SELL",
                Quantity = qtys[i],
                Price = targets[i],
            });
        }
        // Single primary stop at 19950; additional stops for brackets 2..N-1 queued
        group.Legs.Add(new OrderLeg { GroupOrderId = "grp-n", OrderId = "s1", LegType = LegType.Stop, OrderType = "Stop", Action = "SELL", Quantity = total, Price = 19950m });
        // Brackets 1..N-1 have stops s2..sN queued (bracket 0's stop = s1 is the primary)
        if (qtys.Length >= 3)
        {
            var pending = new List<string>();
            for (int i = 2; i <= qtys.Length; i++) pending.Add($"s{i}");
            group.PendingStopIds = pending;
        }
        else if (qtys.Length == 2)
        {
            // Legacy 2-bracket path uses Stop2OrderId
            group.Stop2OrderId = "s2";
        }
        return group;
    }

    // ── EntrySignal.ResolveBrackets ─────────────────────────────

    [Fact]
    public void ResolveBrackets_LegacySignal_Returns2BracketsFromTg1Tg2()
    {
        var sig = new EntrySignal(SetupId.F, Direction.Long, Entry: 100m, Stop: 99m,
            Tg2Price: 105m, Tg1Price: 102m,
            TotalContracts: 4, Time: DateTime.UtcNow, PartialContracts: 2,
            UsePartial: true, UseBe: true);

        var brackets = sig.ResolveBrackets();

        Assert.Equal(2, brackets.Count);
        Assert.Equal(102m, brackets[0].TargetPrice);
        Assert.Equal(2, brackets[0].Qty);
        Assert.False(brackets[0].MoveBe);
        Assert.Equal(105m, brackets[1].TargetPrice);
        Assert.Equal(2, brackets[1].Qty);
        Assert.True(brackets[1].MoveBe); // Backcompat: UseBe flows into final bracket
    }

    [Fact]
    public void ResolveBrackets_LegacySignal_NoPartial_ReturnsSingleBracket()
    {
        var sig = new EntrySignal(SetupId.F, Direction.Long, Entry: 100m, Stop: 99m,
            Tg2Price: 105m, Tg1Price: 0m,
            TotalContracts: 2, Time: DateTime.UtcNow,
            UsePartial: false);

        var brackets = sig.ResolveBrackets();

        Assert.Single(brackets);
        Assert.Equal(105m, brackets[0].TargetPrice);
        Assert.Equal(2, brackets[0].Qty);
    }

    [Fact]
    public void ResolveBrackets_ExplicitList_ReturnsVerbatim()
    {
        var explicitList = new[]
        {
            new BracketLeg(102m, 1, MoveBe: false),
            new BracketLeg(104m, 1, MoveBe: true),
            new BracketLeg(108m, 2, MoveBe: true),
        };
        var sig = new EntrySignal(SetupId.F, Direction.Long, Entry: 100m, Stop: 99m,
            Tg2Price: 105m, Tg1Price: 102m,
            TotalContracts: 4, Time: DateTime.UtcNow,
            Brackets: explicitList);

        var brackets = sig.ResolveBrackets();

        Assert.Equal(3, brackets.Count);
        Assert.Equal(108m, brackets[2].TargetPrice);
        Assert.Equal(2, brackets[2].Qty);
    }

    // ── GroupOrder.TargetLegs ───────────────────────────────────

    [Fact]
    public void TargetLegs_ReturnsOnlyTargetsInOrder()
    {
        var group = MakeNBracketGroup(
            qtys:    new[] { 1, 1, 1, 1 },
            targets: new[] { 100m, 101m, 102m, 103m });

        var targets = group.TargetLegs;

        Assert.Equal(4, targets.Count);
        Assert.Equal(LegType.Tg1, targets[0].LegType);
        Assert.Equal(LegType.Tg2, targets[1].LegType);
        Assert.Equal(LegType.Tg3, targets[2].LegType);
        Assert.Equal(LegType.Tg4, targets[3].LegType);
    }

    [Fact]
    public void IsTargetLeg_TrueForTg1ThroughTg4()
    {
        Assert.True(GroupOrder.IsTargetLeg(LegType.Tg1));
        Assert.True(GroupOrder.IsTargetLeg(LegType.Tg2));
        Assert.True(GroupOrder.IsTargetLeg(LegType.Tg3));
        Assert.True(GroupOrder.IsTargetLeg(LegType.Tg4));
        Assert.False(GroupOrder.IsTargetLeg(LegType.Entry));
        Assert.False(GroupOrder.IsTargetLeg(LegType.Stop));
    }

    // ── BrokerEventHandler: 3-bracket sequential fills ─────────

    [Fact]
    public async Task ThreeBrackets_Tg1Fill_MovesStopToBE_AdvancesPendingStopQueue()
    {
        var exec = new FakeGroupExecutor();
        exec.FillPrices["t1"] = 20050m;
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);

        var group = MakeNBracketGroup(
            qtys:    new[] { 1, 1, 2 },
            targets: new[] { 20050m, 20100m, 20200m });
        handler.RegisterGroup(group, strategy);

        // Tg1 fills → should move stop to BE (20000) and advance to s2 from PendingStopIds
        await handler.HandleEventAsync(Evt("grp-n", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 1));

        Assert.Equal(GroupOrderStatus.PartialFilled, group.Status);
        // Primary stop leg should now track s2
        var stopLeg = group.GetLeg(LegType.Stop);
        Assert.NotNull(stopLeg);
        Assert.Equal("s2", stopLeg!.OrderId);
        Assert.Equal(3, stopLeg.Quantity); // 4 total − 1 partial
        Assert.Equal(20000m, stopLeg.Price); // moved to BE
        // PendingStopIds should have shrunk
        Assert.Single(group.PendingStopIds);
        Assert.Equal("s3", group.PendingStopIds[0]);
        // AccruedPartialPnl = (20050 - 20000) * 20 * 1 = 1000
        Assert.Equal(1000m, group.AccruedPartialPnl);
    }

    [Fact]
    public async Task ThreeBrackets_Tg2Fill_AdvancesQueueAgain_AccruesMorePnl()
    {
        var exec = new FakeGroupExecutor();
        exec.FillPrices["t1"] = 20050m;
        exec.FillPrices["t2"] = 20100m;
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec);

        var group = MakeNBracketGroup(
            qtys:    new[] { 1, 1, 2 },
            targets: new[] { 20050m, 20100m, 20200m });
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-n", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 1));
        await handler.HandleEventAsync(Evt("grp-n", "t2", LegType.Tg2, OrderLegStatus.Filled, 20100m, 1));

        // Stop should have advanced again from s2 to s3
        var stopLeg = group.GetLeg(LegType.Stop);
        Assert.Equal("s3", stopLeg!.OrderId);
        Assert.Equal(2, stopLeg.Quantity); // 4 − 1 − 1
        Assert.Empty(group.PendingStopIds);
        // AccruedPartialPnl = 1000 (from t1) + (20100 − 20000) * 20 * 1 = 3000
        Assert.Equal(3000m, group.AccruedPartialPnl);
        // Group still partial-filled (runner still in)
        Assert.Equal(GroupOrderStatus.PartialFilled, group.Status);
    }

    [Fact]
    public async Task ThreeBrackets_TerminalTg3Fill_CompletesGroup()
    {
        var exec = new FakeGroupExecutor();
        exec.FillPrices["t1"] = 20050m;
        exec.FillPrices["t2"] = 20100m;
        exec.FillPrices["t3"] = 20200m;
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec) { CommissionPerSide = 0m };

        var group = MakeNBracketGroup(
            qtys:    new[] { 1, 1, 2 },
            targets: new[] { 20050m, 20100m, 20200m });
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-n", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 1));
        await handler.HandleEventAsync(Evt("grp-n", "t2", LegType.Tg2, OrderLegStatus.Filled, 20100m, 1));
        await handler.HandleEventAsync(Evt("grp-n", "t3", LegType.Tg3, OrderLegStatus.Filled, 20200m, 2));

        // Terminal fill → group completed, stop canceled
        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        Assert.Contains("s3", exec.Cancellations);
    }

    [Fact]
    public async Task FourBrackets_AllFillsInSequence_CompletesCorrectly()
    {
        var exec = new FakeGroupExecutor();
        exec.FillPrices["t1"] = 20050m;
        exec.FillPrices["t2"] = 20100m;
        exec.FillPrices["t3"] = 20150m;
        exec.FillPrices["t4"] = 20250m;
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec) { CommissionPerSide = 0m };

        var group = MakeNBracketGroup(
            qtys:    new[] { 1, 1, 1, 1 },
            targets: new[] { 20050m, 20100m, 20150m, 20250m });
        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(Evt("grp-n", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 1));
        await handler.HandleEventAsync(Evt("grp-n", "t2", LegType.Tg2, OrderLegStatus.Filled, 20100m, 1));
        await handler.HandleEventAsync(Evt("grp-n", "t3", LegType.Tg3, OrderLegStatus.Filled, 20150m, 1));
        await handler.HandleEventAsync(Evt("grp-n", "t4", LegType.Tg4, OrderLegStatus.Filled, 20250m, 1));

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        // All non-terminal partial P&L accrued (sum of tg1+tg2+tg3):
        // (50 + 100 + 150) * 20 * 1 = 6000
        Assert.Equal(6000m, group.AccruedPartialPnl);
    }

    [Fact]
    public async Task ThreeBrackets_StopFillAfterTg1_CancelsAllRemainingTargets()
    {
        var exec = new FakeGroupExecutor();
        exec.FillPrices["t1"] = 20050m;
        var strategy = new FakeSetup();
        var handler = new BrokerEventHandler(exec) { CommissionPerSide = 0m };

        var group = MakeNBracketGroup(
            qtys:    new[] { 1, 1, 2 },
            targets: new[] { 20050m, 20100m, 20200m });
        handler.RegisterGroup(group, strategy);

        // Tg1 fills then stop fills (runner stopped out)
        await handler.HandleEventAsync(Evt("grp-n", "t1", LegType.Tg1, OrderLegStatus.Filled, 20050m, 1));
        await handler.HandleEventAsync(Evt("grp-n", "s2", LegType.Stop, OrderLegStatus.Filled, 20000m, 3));

        // Both Tg2 and Tg3 should be canceled
        Assert.Contains("t2", exec.Cancellations);
        Assert.Contains("t3", exec.Cancellations);
        Assert.Equal(GroupOrderStatus.Completed, group.Status);
    }

    // ── PendingStopIds CSV round-trip ───────────────────────────

    [Fact]
    public void PendingStopIds_Get_ParsesFromCsv()
    {
        var group = new GroupOrder { PendingStopIdsCsv = "a,b,c" };
        Assert.Equal(3, group.PendingStopIds.Count);
        Assert.Equal("a", group.PendingStopIds[0]);
    }

    [Fact]
    public void PendingStopIds_Set_WritesToCsv()
    {
        var group = new GroupOrder();
        group.PendingStopIds = new List<string> { "x", "y" };
        Assert.Equal("x,y", group.PendingStopIdsCsv);
    }

    [Fact]
    public void PendingStopIds_SetEmpty_NullsCsv()
    {
        var group = new GroupOrder { PendingStopIdsCsv = "z" };
        group.PendingStopIds = new List<string>();
        Assert.Null(group.PendingStopIdsCsv);
    }
}
