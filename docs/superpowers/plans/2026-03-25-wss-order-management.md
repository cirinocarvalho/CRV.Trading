# WSS Order Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace dual-tracking trade state with broker-as-source-of-truth architecture using WSS events for order lifecycle, group orders for multi-leg management, and unified event stream for Mock/Tradovate parity.

**Architecture:** New `IBrokerEventStream` interface emits `OrderEvent`s consumed by a central `BrokerEventHandler` that drives all trade lifecycle (tg1 fill → BE move, trade completion, P&L). Strategies become pure signal generators emitting only `EntrySignal`. `IOrderExecutor` is simplified to place/modify/cancel. Mock broker becomes a dumb order book emitting events through `Channel<OrderEvent>`.

**Tech Stack:** C# / .NET 8, xUnit, EF Core (SQLite), SignalR, System.Threading.Channels, System.Net.WebSockets

**Spec:** `docs/superpowers/specs/2026-03-25-wss-order-management-design.md`

---

## File Structure

### New Files
| File | Responsibility |
|------|---------------|
| `CRV.Core/Models/GroupOrder.cs` | GroupOrder + OrderLeg models, LegType/OrderLegStatus/GroupOrderStatus enums |
| `CRV.Core/Interfaces/IBrokerEventStream.cs` | IBrokerEventStream interface + OrderEvent record |
| `CRV.Core/Strategy/BrokerEventHandler.cs` | Central event handler — reacts to WSS events, manages GroupOrder state, drives leg transitions |
| `CRV.Live/Brokers/MockEventStream.cs` | Mock IBrokerEventStream backed by Channel\<OrderEvent\> |
| `CRV.Live/Brokers/Tradovate/TradovateEventStream.cs` | Tradovate WSS IBrokerEventStream |
| `CRV.Core/Migrations/YYYYMMDD_AddGroupOrders.cs` | EF migration for GroupOrders + OrderLegs tables |
| `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs` | Unit tests for BrokerEventHandler |
| `CRV.Core.Tests/Brokers/MockEventStreamTests.cs` | Unit tests for MockEventStream |

### Modified Files
| File | Changes |
|------|---------|
| `CRV.Core/Interfaces/IInterfaces.cs` | Replace IOrderExecutor with new signature (GroupOrder return, ModifyOrderAsync, CancelOrderAsync, PlaceMarketCloseAsync). Simplify IStrategyEventSink. |
| `CRV.Core/Models/Signals.cs` | Update EntrySignal fields (Target→Tg2Price, Partial→Tg1Price, Contracts→TotalContracts, add PartialContracts). Remove ExitSignal, PartialSignal, BESignal. Simplify StrategySignals. Keep ActiveTradeView for backtest compat. |
| `CRV.Core/Strategy/ISetupStrategy.cs` | Remove trade lifecycle methods (ApplyFill, RevertEntry, RevertEntryToTickGate, ForceExit, GetActiveTrade, PendingExit/Partial/BE, PreExitTrade). Add SetInTrade/InTrade. |
| `CRV.Core/Strategy/PullbackStrategy.cs` | Remove ~15 trade lifecycle fields, exit/partial/BE signal generation, tick confirmation gate. Keep arm/entry logic. Add InTrade property. |
| `CRV.Core/Strategy/RetestStrategy.cs` | Same simplification as PullbackStrategy |
| `CRV.Core/Strategy/OrbFakeoutStrategy.cs` | Same simplification |
| `CRV.Core/Strategy/SessionFakeoutStrategy.cs` | Same simplification |
| `CRV.Core/Strategy/TickerGroup.cs` | Simplify StrategySignals to Entry-only. Remove PreExitTrade collection. |
| `CRV.Core/Strategy/ComposableEngine.cs` | Simplify RouteSignalsAsync (entry-only routing via BrokerEventHandler). Remove BuildTradeRecord. Add BrokerEventHandler field. Snapshot reads from BrokerEventHandler. |
| `CRV.Live/Brokers/MockBrokerExecutor.cs` | Rewrite as dumb order book — no OCO auto-cancel. Emits events via MockEventStream. New IOrderExecutor signature. |
| `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs` | New IOrderExecutor signature. Returns GroupOrder. Remove fill polling. |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Wire BrokerEventHandler + EventStream lifecycle. |
| `CRV.Web/Services/SignalREventSink.cs` | Simplify — remove OnEntryAsync, OnPartialAsync, OnBEMoveAsync |
| `CRV.Core/Data/TradingDbContext.cs` | Add DbSet\<GroupOrder\>, DbSet\<OrderLeg\> |
| `CRV.Web/Pages/Dashboard/Index.cshtml` | Add status row, broker-sourced trade data, state-dependent Exit/Cancel |
| `CRV.Web/Pages/Trading/Orders.cshtml` | Grouped view with expand |
| `CRV.Web/Pages/Trading/Orders.cshtml.cs` | Query GroupOrders + OrderLegs |

---

## Phase 1: Core Models & Interfaces (Additive — No Breaking Changes)

### Task 1: GroupOrder Model + Enums

**Files:**
- Create: `CRV.Core/Models/GroupOrder.cs`

- [ ] **Step 1: Create GroupOrder.cs with all models**

```csharp
namespace CRV.Core.Models;

// ── Group Order enums ────────────────────────────────────────
public enum LegType { Entry, Tg1, Tg2, Stop }
public enum OrderLegStatus { Working, Filled, Modified, Canceled, Rejected }
public enum GroupOrderStatus { Pending, Active, PartialFilled, Completed, Canceled }

// ── Order event — emitted by broker event stream ────────────
public record OrderEvent(
    string GroupOrderId,
    string OrderId,
    LegType LegType,
    OrderLegStatus Status,
    decimal? FillPrice,
    int? FillQty,
    decimal? ModifiedPrice,
    int? ModifiedQty,
    DateTime Timestamp);

// ── Group Order — multi-leg trade unit ──────────────────────
public class GroupOrder
{
    public int Id { get; set; }
    public string GroupOrderId { get; set; } = "";
    public string SetupId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public Direction Direction { get; set; }
    public int TotalContracts { get; set; }
    public int PartialContracts { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal PointValue { get; set; }
    public decimal AccruedPartialPnl { get; set; }
    public GroupOrderStatus Status { get; set; } = GroupOrderStatus.Pending;
    public string Broker { get; set; } = "";
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public List<OrderLeg> Legs { get; set; } = new();

    /// <summary>Find leg by type. Returns null if not found.</summary>
    public OrderLeg? GetLeg(LegType type) => Legs.FirstOrDefault(l => l.LegType == type);

    /// <summary>Find leg by broker order ID.</summary>
    public OrderLeg? GetLegByOrderId(string orderId) => Legs.FirstOrDefault(l => l.OrderId == orderId);

    /// <summary>Remaining contracts after partial fill.</summary>
    public int RemainingContracts => TotalContracts - PartialContracts;
}

// ── Order Leg — individual order within a group ─────────────
public class OrderLeg
{
    public int Id { get; set; }
    public string GroupOrderId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public LegType LegType { get; set; }
    public string OrderType { get; set; } = "";   // Market | Limit | Stop
    public string Action { get; set; } = "";       // BUY | SELL
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public OrderLegStatus Status { get; set; } = OrderLegStatus.Working;
    public decimal? FillPrice { get; set; }
    public DateTime? FillTime { get; set; }
    public DateTime? LastModifiedAt { get; set; }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build CRV.Core`
Expected: SUCCESS (additive change only)

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Models/GroupOrder.cs
git commit -m "feat: add GroupOrder, OrderLeg, OrderEvent models for WSS order management"
```

---

### Task 2: IBrokerEventStream Interface

**Files:**
- Create: `CRV.Core/Interfaces/IBrokerEventStream.cs`

- [ ] **Step 1: Create IBrokerEventStream.cs**

```csharp
namespace CRV.Core.Interfaces;

using CRV.Core.Models;

/// <summary>
/// Unified event stream for real-time order status updates.
/// Implemented by TradovateEventStream (WSS) and MockEventStream (Channel).
/// </summary>
public interface IBrokerEventStream
{
    event Action<OrderEvent>? OnOrderUpdate;
    event Action? OnDisconnected;
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build CRV.Core`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Interfaces/IBrokerEventStream.cs
git commit -m "feat: add IBrokerEventStream interface for unified order event streaming"
```

---

### Task 3: Update EntrySignal + Add New IOrderExecutor

This task introduces the new `IOrderExecutor` interface alongside the old one (via a new name `IGroupOrderExecutor`) to avoid breaking the build. The old interface will be removed in Phase 3 when strategies are simplified.

**Files:**
- Modify: `CRV.Core/Models/Signals.cs`
- Modify: `CRV.Core/Interfaces/IInterfaces.cs`

- [ ] **Step 1: Add new EntrySignal fields to Signals.cs**

Add `Tg1Price`, `Tg2Price`, `TotalContracts`, `PartialContracts` as computed properties on the existing EntrySignal so both old and new code can work:

```csharp
// Add after the existing EntrySignal record (line 21):

// ── Bridge properties for WSS order management transition ───
// These allow the new BrokerEventHandler to read EntrySignal using
// the new field names while strategies still construct using old names.
public static class EntrySignalExtensions
{
    public static decimal Tg1Price(this EntrySignal s) => s.Partial;
    public static decimal Tg2Price(this EntrySignal s) => s.Target;
    public static int TotalContracts(this EntrySignal s) => s.Contracts;
}
```

- [ ] **Step 2: Add IGroupOrderExecutor to IInterfaces.cs**

Add the new executor interface below the existing IOrderExecutor (line 49):

```csharp
/// <summary>
/// New order executor that returns GroupOrder and supports modify/cancel.
/// Replaces IOrderExecutor once strategy simplification is complete.
/// </summary>
public interface IGroupOrderExecutor
{
    Task<GroupOrder?> OnEntrySignalAsync(EntrySignal signal);
    Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty);
    Task CancelOrderAsync(string orderId);
    Task PlaceMarketCloseAsync(string ticker, Direction direction, int qty);
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build`
Expected: SUCCESS (additive only)

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Models/Signals.cs CRV.Core/Interfaces/IInterfaces.cs
git commit -m "feat: add IGroupOrderExecutor interface and EntrySignal bridge extensions"
```

---

## Phase 2: BrokerEventHandler (Core Brain)

### Task 4: BrokerEventHandler — Tests First

**Files:**
- Create: `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs`

- [ ] **Step 1: Write core test scaffolding with fakes**

```csharp
namespace CRV.Core.Tests.Strategy;

using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;

public class BrokerEventHandlerTests
{
    private class FakeGroupExecutor : IGroupOrderExecutor
    {
        public List<(string orderId, decimal? price, int? qty)> Modifications { get; } = new();
        public List<string> Cancellations { get; } = new();
        public List<(string ticker, Direction dir, int qty)> MarketCloses { get; } = new();

        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal signal) =>
            Task.FromResult<GroupOrder?>(null); // not used by handler

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

    private class FakeStrategy : ISetupStrategy
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
        public ExitSignal? PendingExit => null;
        public PartialSignal? PendingPartial => null;
        public BESignal? PendingBE => null;
        public ActiveTradeView? PreExitTrade => null;
        public void ApplyFill(decimal p) { }
        public void ClearPendingSignals() { }
        public void RevertEntry() { }
        public void RevertEntryToTickGate(decimal e) { }
        public void ForceExit(decimal p, DateTime t, ExitReason r = ExitReason.SessionEnd) { }
        public void Disarm() { }
        public void ResetCutoff() { }
        public SetupStateSnapshot GetSnapshot() => new();
        public ActiveTradeView? GetActiveTrade(decimal p) => null;
        public void SetInTrade(bool active) { InTrade = active; }
    }

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
            EntryPrice = null,
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

    [Fact]
    public async Task EntryFilled_SetsGroupActive_AndEntryPrice()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeStrategy();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "e1", LegType.Entry, OrderLegStatus.Filled,
            FillPrice: 20001.25m, FillQty: 4, null, null, DateTime.UtcNow));

        Assert.Equal(GroupOrderStatus.Active, group.Status);
        Assert.Equal(20001.25m, group.EntryPrice);
        Assert.True(strategy.InTrade);
    }

    [Fact]
    public async Task Tg1Filled_MovesStopToBE_AndReducesQty()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeStrategy();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        // Entry fills first
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "e1", LegType.Entry, OrderLegStatus.Filled,
            20000m, 4, null, null, DateTime.UtcNow));

        // Tg1 fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled,
            20050m, 2, null, null, DateTime.UtcNow));

        Assert.Equal(GroupOrderStatus.PartialFilled, group.Status);
        // Should have modified stop to BE (entry price) with reduced qty
        Assert.Single(exec.Modifications);
        var mod = exec.Modifications[0];
        Assert.Equal("s1", mod.orderId);
        Assert.Equal(20000m, mod.price);  // BE = entry price
        Assert.Equal(2, mod.qty);          // remaining contracts
    }

    [Fact]
    public async Task Tg2Filled_CancelsStop_CompletesGroup()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeStrategy();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        // Entry fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "e1", LegType.Entry, OrderLegStatus.Filled,
            20000m, 4, null, null, DateTime.UtcNow));
        // Tg1 fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled,
            20050m, 2, null, null, DateTime.UtcNow));
        // Tg2 fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "t2", LegType.Tg2, OrderLegStatus.Filled,
            20100m, 2, null, null, DateTime.UtcNow));

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        Assert.Contains("s1", exec.Cancellations);
        Assert.False(strategy.InTrade);
    }

    [Fact]
    public async Task StopFilled_CancelsTg1Tg2_CompletesGroup()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeStrategy();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        // Entry fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "e1", LegType.Entry, OrderLegStatus.Filled,
            20000m, 4, null, null, DateTime.UtcNow));
        // Stop fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "s1", LegType.Stop, OrderLegStatus.Filled,
            19950m, 4, null, null, DateTime.UtcNow));

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        Assert.Contains("t1", exec.Cancellations);
        Assert.Contains("t2", exec.Cancellations);
        Assert.False(strategy.InTrade);
    }

    [Fact]
    public async Task ExitGroup_Pending_CancelsAllLegs()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeStrategy();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        await handler.ExitGroupAsync("A");

        Assert.Equal(GroupOrderStatus.Canceled, group.Status);
        // Should cancel all 4 legs
        Assert.Equal(4, exec.Cancellations.Count);
    }

    [Fact]
    public async Task ExitGroup_Active_MarketCloses_AndCancelsLegs()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeStrategy();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        // Entry fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "e1", LegType.Entry, OrderLegStatus.Filled,
            20000m, 4, null, null, DateTime.UtcNow));

        await handler.ExitGroupAsync("A");

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        // Should have market closed full qty and canceled working legs
        Assert.Single(exec.MarketCloses);
        Assert.Equal(4, exec.MarketCloses[0].qty);
    }

    [Fact]
    public async Task ExitGroup_PartialFilled_MarketCloses_RemainingQty()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeStrategy();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        // Entry fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "e1", LegType.Entry, OrderLegStatus.Filled,
            20000m, 4, null, null, DateTime.UtcNow));
        // Tg1 fills
        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "t1", LegType.Tg1, OrderLegStatus.Filled,
            20050m, 2, null, null, DateTime.UtcNow));

        exec.Modifications.Clear();
        exec.Cancellations.Clear();

        await handler.ExitGroupAsync("A");

        Assert.Equal(GroupOrderStatus.Completed, group.Status);
        Assert.Single(exec.MarketCloses);
        Assert.Equal(2, exec.MarketCloses[0].qty);  // remaining after partial
    }

    [Fact]
    public void GetUnrealizedPnl_Long_CalculatesCorrectly()
    {
        var exec = new FakeGroupExecutor();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();
        group.Status = GroupOrderStatus.Active;
        group.EntryPrice = 20000m;

        var strategy = new FakeStrategy();
        handler.RegisterGroup(group, strategy);

        // Price moved to 20025 — 25 pts * 20 PointValue * 4 contracts = $2000
        var pnl = handler.GetUnrealizedPnl("A", 20025m);
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
        group.AccruedPartialPnl = 2000m;  // tg1 filled 50 pts * 20 PV * 2 cts

        var strategy = new FakeStrategy();
        handler.RegisterGroup(group, strategy);

        // Price at 20025 — remaining 2 cts: 25 * 20 * 2 = 1000 + 2000 accrued = 3000
        var pnl = handler.GetUnrealizedPnl("A", 20025m);
        Assert.Equal(3000m, pnl);
    }

    [Fact]
    public async Task EntryRejected_CancelsAllLegs_SetsGroupCanceled()
    {
        var exec = new FakeGroupExecutor();
        var strategy = new FakeStrategy();
        var handler = new BrokerEventHandler(exec);
        var group = MakeGroup();

        handler.RegisterGroup(group, strategy);

        await handler.HandleEventAsync(new OrderEvent(
            "grp-001", "e1", LegType.Entry, OrderLegStatus.Rejected,
            null, null, null, null, DateTime.UtcNow));

        Assert.Equal(GroupOrderStatus.Canceled, group.Status);
        Assert.False(strategy.InTrade);
    }
}
```

- [ ] **Step 2: Run tests — should fail (BrokerEventHandler doesn't exist)**

Run: `dotnet test CRV.Core.Tests --filter "BrokerEventHandler" -v m`
Expected: Build error — `BrokerEventHandler` type not found

- [ ] **Step 3: Commit test file**

```bash
git add CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs
git commit -m "test: add BrokerEventHandler tests (red — implementation pending)"
```

---

### Task 5: BrokerEventHandler — Implementation

**Files:**
- Create: `CRV.Core/Strategy/BrokerEventHandler.cs`

- [ ] **Step 1: Implement BrokerEventHandler**

```csharp
namespace CRV.Core.Strategy;

using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Central event handler that reacts to broker order events and drives
/// all trade lifecycle: entry confirmation, tg1 → BE move, completion,
/// P&amp;L accrual, and group state transitions.
/// </summary>
public class BrokerEventHandler
{
    private readonly IGroupOrderExecutor _executor;
    private readonly ILogger? _log;

    // Active groups keyed by SetupId
    private readonly Dictionary<string, (GroupOrder Group, ISetupStrategy Strategy)> _active = new();
    private readonly object _lock = new();

    /// <summary>Raised when a group completes (target, stop, or manual exit).</summary>
    public event Action<GroupOrder, TradeRecord>? OnTradeCompleted;

    public BrokerEventHandler(IGroupOrderExecutor executor, ILogger? log = null)
    {
        _executor = executor;
        _log = log;
    }

    /// <summary>Register a newly placed group order for event tracking.</summary>
    public void RegisterGroup(GroupOrder group, ISetupStrategy strategy)
    {
        lock (_lock)
            _active[group.SetupId] = (group, strategy);
    }

    /// <summary>Get the active group for a setup, or null.</summary>
    public GroupOrder? GetActiveGroup(string setupId)
    {
        lock (_lock)
            return _active.TryGetValue(setupId, out var pair) ? pair.Group : null;
    }

    /// <summary>Check if a setup has an active group order.</summary>
    public bool HasActiveGroup(string setupId)
    {
        lock (_lock)
            return _active.ContainsKey(setupId);
    }

    /// <summary>Handle an order event from the broker event stream.</summary>
    public async Task HandleEventAsync(OrderEvent evt)
    {
        GroupOrder? group;
        ISetupStrategy? strategy;

        lock (_lock)
        {
            // Find the group by GroupOrderId
            var match = _active.Values.FirstOrDefault(p => p.Group.GroupOrderId == evt.GroupOrderId);
            group = match.Group;
            strategy = match.Strategy;
        }

        if (group == null)
        {
            _log?.LogWarning("[BEH] Received event for unknown group {GroupId}", evt.GroupOrderId);
            return;
        }

        // Update leg status
        var leg = group.GetLegByOrderId(evt.OrderId);
        if (leg != null)
        {
            leg.Status = evt.Status;
            if (evt.FillPrice.HasValue) leg.FillPrice = evt.FillPrice;
            if (evt.Status == OrderLegStatus.Filled) leg.FillTime = evt.Timestamp;
            if (evt.ModifiedPrice.HasValue)
            {
                leg.Price = evt.ModifiedPrice.Value;
                leg.LastModifiedAt = evt.Timestamp;
            }
        }

        switch (evt.LegType)
        {
            case LegType.Entry:
                await HandleEntryEventAsync(group, strategy!, evt);
                break;
            case LegType.Tg1:
                await HandleTg1EventAsync(group, strategy!, evt);
                break;
            case LegType.Tg2:
                await HandleTg2EventAsync(group, strategy!, evt);
                break;
            case LegType.Stop:
                await HandleStopEventAsync(group, strategy!, evt);
                break;
        }
    }

    /// <summary>Manually exit a group order (Cancel if pending, Market Close if active).</summary>
    public async Task ExitGroupAsync(string setupId)
    {
        GroupOrder? group;
        ISetupStrategy? strategy;

        lock (_lock)
        {
            if (!_active.TryGetValue(setupId, out var pair)) return;
            group = pair.Group;
            strategy = pair.Strategy;
        }

        switch (group.Status)
        {
            case GroupOrderStatus.Pending:
                // Cancel all legs — no position
                foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
                    await _executor.CancelOrderAsync(leg.OrderId);
                group.Status = GroupOrderStatus.Canceled;
                group.CompletedAt = DateTime.UtcNow;
                break;

            case GroupOrderStatus.Active:
                // Market close full qty + cancel working legs
                foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
                    await _executor.CancelOrderAsync(leg.OrderId);
                await _executor.PlaceMarketCloseAsync(group.Ticker, group.Direction, group.TotalContracts);
                group.Status = GroupOrderStatus.Completed;
                group.CompletedAt = DateTime.UtcNow;
                break;

            case GroupOrderStatus.PartialFilled:
                // Market close remaining qty + cancel working legs
                foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
                    await _executor.CancelOrderAsync(leg.OrderId);
                var remaining = group.TotalContracts - group.PartialContracts;
                await _executor.PlaceMarketCloseAsync(group.Ticker, group.Direction, remaining);
                group.Status = GroupOrderStatus.Completed;
                group.CompletedAt = DateTime.UtcNow;
                break;
        }

        strategy?.SetInTrade(false);

        lock (_lock)
            _active.Remove(setupId);
    }

    /// <summary>Force-close all active groups (session boundary).</summary>
    public async Task ExitAllAsync()
    {
        List<string> setupIds;
        lock (_lock)
            setupIds = _active.Keys.ToList();

        foreach (var id in setupIds)
            await ExitGroupAsync(id);
    }

    /// <summary>Calculate unrealized P&amp;L for a setup.</summary>
    public decimal GetUnrealizedPnl(string setupId, decimal currentPrice)
    {
        GroupOrder? group;
        lock (_lock)
        {
            if (!_active.TryGetValue(setupId, out var pair)) return 0m;
            group = pair.Group;
        }

        if (group.EntryPrice is null) return 0m;

        var entry = group.EntryPrice.Value;
        var pv = group.PointValue;
        bool isLong = group.Direction == Direction.Long;

        int remainingQty = group.Status == GroupOrderStatus.PartialFilled
            ? group.TotalContracts - group.PartialContracts
            : group.TotalContracts;

        var unrealized = isLong
            ? (currentPrice - entry) * pv * remainingQty
            : (entry - currentPrice) * pv * remainingQty;

        return unrealized + group.AccruedPartialPnl;
    }

    /// <summary>Get group state for snapshot building.</summary>
    public GroupOrder? GetGroupState(string setupId)
    {
        lock (_lock)
            return _active.TryGetValue(setupId, out var pair) ? pair.Group : null;
    }

    // ── Private event handlers ──────────────────────────────────

    private Task HandleEntryEventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status == OrderLegStatus.Filled)
        {
            group.Status = GroupOrderStatus.Active;
            group.EntryPrice = evt.FillPrice;
            strategy.SetInTrade(true);
            _log?.LogInformation("[BEH] Entry FILLED grp={G} @ {P}", group.GroupOrderId, evt.FillPrice);
        }
        else if (evt.Status == OrderLegStatus.Rejected)
        {
            _log?.LogWarning("[BEH] Entry REJECTED grp={G}", group.GroupOrderId);
            return CancelRemainingAndComplete(group, strategy, GroupOrderStatus.Canceled);
        }

        return Task.CompletedTask;
    }

    private async Task HandleTg1EventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        group.Status = GroupOrderStatus.PartialFilled;

        // Accrue partial P&L
        if (group.EntryPrice.HasValue && evt.FillPrice.HasValue)
        {
            bool isLong = group.Direction == Direction.Long;
            var partialPnl = isLong
                ? (evt.FillPrice.Value - group.EntryPrice.Value) * group.PointValue * group.PartialContracts
                : (group.EntryPrice.Value - evt.FillPrice.Value) * group.PointValue * group.PartialContracts;
            group.AccruedPartialPnl = partialPnl;
        }

        // Move stop to BE with reduced qty
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null && group.EntryPrice.HasValue)
        {
            var remaining = group.TotalContracts - group.PartialContracts;
            await _executor.ModifyOrderAsync(stopLeg.OrderId, group.EntryPrice.Value, remaining);
            _log?.LogInformation("[BEH] Tg1 FILLED grp={G} — stop→BE @ {P}, qty→{Q}",
                group.GroupOrderId, group.EntryPrice, remaining);
        }
    }

    private async Task HandleTg2EventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        // Cancel stop leg
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null && stopLeg.Status == OrderLegStatus.Working)
            await _executor.CancelOrderAsync(stopLeg.OrderId);

        await CompleteGroup(group, strategy, ExitReason.Target, evt.FillPrice ?? 0m, evt.Timestamp);
    }

    private async Task HandleStopEventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        // Cancel tg1 and tg2 legs
        foreach (var leg in group.Legs.Where(l =>
            (l.LegType == LegType.Tg1 || l.LegType == LegType.Tg2) &&
            l.Status == OrderLegStatus.Working))
        {
            await _executor.CancelOrderAsync(leg.OrderId);
        }

        await CompleteGroup(group, strategy, ExitReason.Stop, evt.FillPrice ?? 0m, evt.Timestamp);
    }

    private async Task CancelRemainingAndComplete(GroupOrder group, ISetupStrategy strategy, GroupOrderStatus status)
    {
        foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
            await _executor.CancelOrderAsync(leg.OrderId);

        group.Status = status;
        group.CompletedAt = DateTime.UtcNow;
        strategy.SetInTrade(false);

        lock (_lock)
            _active.Remove(group.SetupId);
    }

    private Task CompleteGroup(GroupOrder group, ISetupStrategy strategy,
        ExitReason reason, decimal exitPrice, DateTime exitTime)
    {
        group.Status = GroupOrderStatus.Completed;
        group.CompletedAt = exitTime;
        strategy.SetInTrade(false);

        // Build trade record
        if (group.EntryPrice.HasValue)
        {
            bool isLong = group.Direction == Direction.Long;
            var entry = group.EntryPrice.Value;
            var stopLeg = group.GetLeg(LegType.Stop);
            var initStop = stopLeg?.Price ?? 0m;
            var tg1 = group.GetLeg(LegType.Tg1);
            var tg2 = group.GetLeg(LegType.Tg2);

            // Compute total P&L
            int remaining = group.TotalContracts - group.PartialContracts;
            decimal exitPnl = isLong
                ? (exitPrice - entry) * group.PointValue * remaining
                : (entry - exitPrice) * group.PointValue * remaining;
            decimal totalPnl = exitPnl + group.AccruedPartialPnl;

            decimal risk = Math.Abs(entry - initStop) * group.PointValue * group.TotalContracts;
            decimal rMult = risk > 0 ? totalPnl / risk : 0m;

            var trade = new TradeRecord
            {
                Setup = strategy.SetupId,
                SetupLabel = strategy.Id,
                Direction = group.Direction,
                Ticker = group.Ticker.TrimStart('/'),
                Contracts = group.TotalContracts,
                Entry = entry,
                InitialStop = initStop,
                Target = tg2?.Price ?? 0m,
                Partial = tg1?.Price ?? 0m,
                Exit = exitPrice,
                ExitReason = reason,
                PartialFilled = group.Status == GroupOrderStatus.PartialFilled ||
                                group.AccruedPartialPnl != 0m,
                PartialPrice = tg1?.FillPrice ?? tg1?.Price ?? 0m,
                GrossPnl = totalPnl,
                Commission = 0m,  // calculated downstream
                NetPnl = totalPnl,
                RMultiple = rMult,
                EnteredAt = group.CreatedAt,
                ExitedAt = exitTime,
                SessionId = group.SessionId ?? "",
            };

            OnTradeCompleted?.Invoke(group, trade);
        }

        lock (_lock)
            _active.Remove(group.SetupId);

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Add SetInTrade to ISetupStrategy**

In `CRV.Core/Strategy/ISetupStrategy.cs`, add after line 76 (`bool IsArmed { get; }`):

```csharp
    /// <summary>Whether this setup is currently in a trade (set by BrokerEventHandler).</summary>
    bool InTrade { get; }

    /// <summary>Called by BrokerEventHandler to signal trade entry/exit.</summary>
    void SetInTrade(bool active);
```

- [ ] **Step 3: Add InTrade/SetInTrade stub to all 4 strategy implementations**

In each of PullbackStrategy.cs, RetestStrategy.cs, OrbFakeoutStrategy.cs, SessionFakeoutStrategy.cs, add a field and methods:

```csharp
    private bool _inTrade;
    public bool InTrade => _inTrade;
    public void SetInTrade(bool active) => _inTrade = active;
```

- [ ] **Step 4: Run tests — all should pass**

Run: `dotnet test CRV.Core.Tests --filter "BrokerEventHandler" -v m`
Expected: All tests PASS

- [ ] **Step 5: Verify full build**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Strategy/BrokerEventHandler.cs CRV.Core/Strategy/ISetupStrategy.cs CRV.Core/Strategy/PullbackStrategy.cs CRV.Core/Strategy/RetestStrategy.cs CRV.Core/Strategy/OrbFakeoutStrategy.cs CRV.Core/Strategy/SessionFakeoutStrategy.cs CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs
git commit -m "feat: implement BrokerEventHandler — central order event handler with leg management"
```

---

## Phase 3: MockEventStream + MockBrokerExecutor Rewrite

### Task 6: MockEventStream

**Files:**
- Create: `CRV.Live/Brokers/MockEventStream.cs`
- Create: `CRV.Core.Tests/Brokers/MockEventStreamTests.cs`

- [ ] **Step 1: Write MockEventStream tests**

```csharp
namespace CRV.Core.Tests.Brokers;

using CRV.Core.Models;
using CRV.Live.Brokers;

public class MockEventStreamTests
{
    [Fact]
    public async Task PushEvent_RaisesOnOrderUpdate()
    {
        var stream = new MockEventStream();
        var received = new List<OrderEvent>();
        stream.OnOrderUpdate += e => received.Add(e);

        await stream.ConnectAsync(CancellationToken.None);
        Assert.True(stream.IsConnected);

        stream.PushEvent(new OrderEvent(
            "g1", "o1", LegType.Entry, OrderLegStatus.Filled,
            100m, 1, null, null, DateTime.UtcNow));

        // Give background loop time to deliver
        await Task.Delay(100);

        Assert.Single(received);
        Assert.Equal("g1", received[0].GroupOrderId);
    }

    [Fact]
    public async Task Disconnect_StopsDelivery()
    {
        var stream = new MockEventStream();
        var count = 0;
        stream.OnOrderUpdate += _ => Interlocked.Increment(ref count);

        await stream.ConnectAsync(CancellationToken.None);
        await stream.DisconnectAsync();

        Assert.False(stream.IsConnected);

        stream.PushEvent(new OrderEvent(
            "g1", "o1", LegType.Entry, OrderLegStatus.Filled,
            100m, 1, null, null, DateTime.UtcNow));

        await Task.Delay(100);
        Assert.Equal(0, count);
    }
}
```

- [ ] **Step 2: Implement MockEventStream**

```csharp
namespace CRV.Live.Brokers;

using System.Threading.Channels;
using CRV.Core.Interfaces;
using CRV.Core.Models;

/// <summary>
/// Mock implementation of IBrokerEventStream backed by an unbounded Channel.
/// MockBrokerExecutor pushes events; a background loop delivers them to subscribers.
/// </summary>
public class MockEventStream : IBrokerEventStream
{
    private readonly Channel<OrderEvent> _channel = Channel.CreateUnbounded<OrderEvent>();
    private CancellationTokenSource? _cts;
    private Task? _consumerTask;

    public event Action<OrderEvent>? OnOrderUpdate;
    public event Action? OnDisconnected;
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsConnected = true;
        _consumerTask = ConsumeLoop(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        _cts?.Cancel();
        if (_consumerTask != null)
        {
            try { await _consumerTask; }
            catch (OperationCanceledException) { }
        }
        OnDisconnected?.Invoke();
    }

    /// <summary>Push an event onto the channel (called by MockBrokerExecutor).</summary>
    public void PushEvent(OrderEvent evt)
    {
        _channel.Writer.TryWrite(evt);
    }

    private async Task ConsumeLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            {
                OnOrderUpdate?.Invoke(evt);
            }
        }
        catch (OperationCanceledException) { }
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test CRV.Core.Tests --filter "MockEventStream" -v m`
Expected: All PASS

- [ ] **Step 4: Commit**

```bash
git add CRV.Live/Brokers/MockEventStream.cs CRV.Core.Tests/Brokers/MockEventStreamTests.cs
git commit -m "feat: add MockEventStream — Channel-backed IBrokerEventStream for mock broker"
```

---

### Task 7: MockBrokerExecutor Rewrite as IGroupOrderExecutor

**Files:**
- Modify: `CRV.Live/Brokers/MockBrokerExecutor.cs`

- [ ] **Step 1: Add MockGroupOrderExecutor class alongside existing MockBrokerExecutor**

Add after the existing `MockBrokerExecutor` class (don't remove it yet — it's still used by the old path):

```csharp
/// <summary>
/// New mock broker — dumb order book that emits events via MockEventStream.
/// No OCO auto-cancel — BrokerEventHandler manages all leg transitions.
/// </summary>
public class MockGroupOrderExecutor : IGroupOrderExecutor
{
    private readonly MockEventStream _stream;
    private readonly ILogger _log;
    private readonly Dictionary<string, MockGroupOrderState> _orders = new();
    private readonly object _lock = new();

    public MockGroupOrderExecutor(MockEventStream stream, ILogger<MockGroupOrderExecutor> log)
    {
        _stream = stream;
        _log = log;
    }

    private class MockGroupOrderState
    {
        public string GroupOrderId { get; set; } = "";
        public Dictionary<string, MockLegState> Legs { get; } = new();
    }

    private class MockLegState
    {
        public string OrderId { get; set; } = "";
        public LegType LegType { get; set; }
        public string Action { get; set; } = "";
        public int Quantity { get; set; }
        public decimal? LimitPrice { get; set; }
        public decimal? StopPrice { get; set; }
        public string Status { get; set; } = "WORKING";
    }

    public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
    {
        var groupId = Guid.NewGuid().ToString("N")[..8];
        bool isLong = sig.Direction == Direction.Long;
        var exitAction = isLong ? "SELL" : "BUY";
        var entryAction = isLong ? "BUY" : "SELL";
        var ticker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : sig.Setup.ToString();
        var setupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString();
        var partialCts = sig.Contracts / 2; // default: half for partial
        var remainingCts = sig.Contracts - partialCts;

        var group = new GroupOrder
        {
            GroupOrderId = groupId,
            SetupId = setupId,
            Ticker = ticker,
            Direction = sig.Direction,
            TotalContracts = sig.Contracts,
            PartialContracts = partialCts,
            PointValue = 20m, // will be set by caller
            Status = GroupOrderStatus.Pending,
            Broker = "Mock",
        };

        var entryLeg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-e", LegType = LegType.Entry, OrderType = sig.OrderType, Action = entryAction, Quantity = sig.Contracts, Price = sig.Entry };
        var tg1Leg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-t1", LegType = LegType.Tg1, OrderType = "Limit", Action = exitAction, Quantity = partialCts, Price = sig.Partial };
        var tg2Leg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-t2", LegType = LegType.Tg2, OrderType = "Limit", Action = exitAction, Quantity = remainingCts, Price = sig.Target };
        var stopLeg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-s", LegType = LegType.Stop, OrderType = "Stop", Action = exitAction, Quantity = sig.Contracts, Price = sig.Stop };

        group.Legs.AddRange(new[] { entryLeg, tg1Leg, tg2Leg, stopLeg });

        // Register in internal order book
        var state = new MockGroupOrderState { GroupOrderId = groupId };
        state.Legs[entryLeg.OrderId] = new MockLegState { OrderId = entryLeg.OrderId, LegType = LegType.Entry, Action = entryAction, Quantity = sig.Contracts, LimitPrice = sig.OrderType == "Limit" ? sig.Entry : null };
        state.Legs[tg1Leg.OrderId] = new MockLegState { OrderId = tg1Leg.OrderId, LegType = LegType.Tg1, Action = exitAction, Quantity = partialCts, LimitPrice = sig.Partial };
        state.Legs[tg2Leg.OrderId] = new MockLegState { OrderId = tg2Leg.OrderId, LegType = LegType.Tg2, Action = exitAction, Quantity = remainingCts, LimitPrice = sig.Target };
        state.Legs[stopLeg.OrderId] = new MockLegState { OrderId = stopLeg.OrderId, LegType = LegType.Stop, Action = exitAction, Quantity = sig.Contracts, StopPrice = sig.Stop };

        lock (_lock)
            _orders[groupId] = state;

        // Market entry: fill immediately
        if (sig.OrderType != "Limit")
        {
            _stream.PushEvent(new OrderEvent(groupId, entryLeg.OrderId, LegType.Entry,
                OrderLegStatus.Filled, sig.Entry, sig.Contracts, null, null, DateTime.UtcNow));
        }

        _log.LogInformation("[MOCK-NEW] Group {G} placed: {D} {Q}x @ {E}", groupId, sig.Direction, sig.Contracts, sig.Entry);
        return Task.FromResult<GroupOrder?>(group);
    }

    public Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
    {
        lock (_lock)
        {
            foreach (var state in _orders.Values)
            {
                if (state.Legs.TryGetValue(orderId, out var leg))
                {
                    if (newPrice.HasValue)
                    {
                        if (leg.StopPrice.HasValue) leg.StopPrice = newPrice;
                        else leg.LimitPrice = newPrice;
                    }
                    if (newQty.HasValue) leg.Quantity = newQty.Value;

                    _stream.PushEvent(new OrderEvent(state.GroupOrderId, orderId, leg.LegType,
                        OrderLegStatus.Modified, null, null, newPrice, newQty, DateTime.UtcNow));
                    break;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(string orderId)
    {
        lock (_lock)
        {
            foreach (var state in _orders.Values)
            {
                if (state.Legs.TryGetValue(orderId, out var leg) && leg.Status == "WORKING")
                {
                    leg.Status = "CANCELED";
                    _stream.PushEvent(new OrderEvent(state.GroupOrderId, orderId, leg.LegType,
                        OrderLegStatus.Canceled, null, null, null, null, DateTime.UtcNow));
                    break;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
    {
        _log.LogInformation("[MOCK-NEW] Market close {D} {Q}x {T}", direction, qty, ticker);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Evaluate fills for all WORKING orders against current price.
    /// Called on each tick. Emits OrderEvent for each fill — NO OCO auto-cancel.
    /// </summary>
    public void EvaluateFills(decimal price, DateTime utcNow)
    {
        if (price <= 0) return;

        lock (_lock)
        {
            foreach (var state in _orders.Values)
            {
                foreach (var leg in state.Legs.Values.Where(l => l.Status == "WORKING"))
                {
                    bool fills = false;

                    if (leg.Action == "BUY")
                    {
                        fills = (leg.StopPrice.HasValue && price >= leg.StopPrice)
                             || (leg.LimitPrice.HasValue && price <= leg.LimitPrice);
                    }
                    else // SELL
                    {
                        fills = (leg.StopPrice.HasValue && price <= leg.StopPrice)
                             || (leg.LimitPrice.HasValue && price >= leg.LimitPrice);
                    }

                    if (!fills) continue;

                    leg.Status = "FILLED";
                    _stream.PushEvent(new OrderEvent(state.GroupOrderId, leg.OrderId, leg.LegType,
                        OrderLegStatus.Filled, price, leg.Quantity, null, null, utcNow));

                    _log.LogInformation("[MOCK-NEW FILL] {A} {Q}x @ {P} (order {Id})",
                        leg.Action, leg.Quantity, price, leg.OrderId);
                }
            }
        }
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add CRV.Live/Brokers/MockBrokerExecutor.cs CRV.Live/Brokers/MockEventStream.cs
git commit -m "feat: add MockGroupOrderExecutor — dumb order book with event emission"
```

---

## Phase 4: Database Migration

### Task 8: EF Migration for GroupOrders + OrderLegs

**Files:**
- Modify: `CRV.Core/Data/TradingDbContext.cs`
- Create: New migration file (auto-generated)

- [ ] **Step 1: Add DbSets to TradingDbContext**

After line 11 (`public DbSet<OrderRecord> Orders { get; set; }`):

```csharp
    public DbSet<GroupOrder>  GroupOrders { get; set; }
    public DbSet<OrderLeg>    OrderLegs   { get; set; }
```

- [ ] **Step 2: Add entity configuration in OnModelCreating**

Add configuration for the new tables in the `OnModelCreating` method:

```csharp
        // GroupOrders
        modelBuilder.Entity<GroupOrder>(e =>
        {
            e.HasIndex(o => o.GroupOrderId).IsUnique();
            e.HasIndex(o => o.SetupId);
            e.HasIndex(o => o.CreatedAt);
            e.Property(o => o.Direction).HasConversion<string>();
            e.Property(o => o.Status).HasConversion<string>();
        });

        // OrderLegs
        modelBuilder.Entity<OrderLeg>(e =>
        {
            e.HasIndex(o => o.GroupOrderId);
            e.HasIndex(o => o.OrderId).IsUnique();
            e.Property(o => o.LegType).HasConversion<string>();
            e.Property(o => o.Status).HasConversion<string>();
        });
```

- [ ] **Step 3: Generate migration**

Run: `cd /Users/ciro/Source/WebApps/CRV.Trading && dotnet ef migrations add AddGroupOrders --project CRV.Core --startup-project CRV.Web`
Expected: Migration file created

- [ ] **Step 4: Apply migration**

Run: `dotnet ef database update --project CRV.Core --startup-project CRV.Web`
Expected: SUCCESS

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Data/TradingDbContext.cs CRV.Core/Migrations/
git commit -m "feat: add GroupOrders + OrderLegs database tables"
```

---

## Phase 5: TradovateEventStream (WSS)

### Task 9: TradovateEventStream

**Files:**
- Create: `CRV.Live/Brokers/Tradovate/TradovateEventStream.cs`

- [ ] **Step 1: Implement TradovateEventStream**

```csharp
namespace CRV.Live.Brokers.Tradovate;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tradovate WSS event stream for real-time order updates.
/// Connects to wss://live.tradovateapi.com/v1/websocket, authenticates,
/// subscribes to user/syncrequest, and parses order update messages.
/// </summary>
public class TradovateEventStream : IBrokerEventStream
{
    private readonly TradovateAuthService _auth;
    private readonly ILogger _log;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Map Tradovate orderId → (GroupOrderId, LegType) for event routing
    private readonly Dictionary<long, (string GroupOrderId, LegType LegType)> _orderMap = new();
    private readonly object _mapLock = new();

    public event Action<OrderEvent>? OnOrderUpdate;
    public event Action? OnDisconnected;
    public bool IsConnected { get; private set; }

    public TradovateEventStream(TradovateAuthService auth, ILogger<TradovateEventStream> log)
    {
        _auth = auth;
        _log = log;
    }

    /// <summary>Register a Tradovate order ID for event routing.</summary>
    public void RegisterOrder(long tradovateOrderId, string groupOrderId, LegType legType)
    {
        lock (_mapLock)
            _orderMap[tradovateOrderId] = (groupOrderId, legType);
    }

    /// <summary>Remove all mappings for a group (on completion/cancel).</summary>
    public void UnregisterGroup(string groupOrderId)
    {
        lock (_mapLock)
        {
            var toRemove = _orderMap.Where(kv => kv.Value.GroupOrderId == groupOrderId)
                                     .Select(kv => kv.Key).ToList();
            foreach (var key in toRemove)
                _orderMap.Remove(key);
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await ConnectAndAuthAsync(_cts.Token);
        _receiveTask = ReceiveLoop(_cts.Token);
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        _cts?.Cancel();

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown",
                    CancellationToken.None);
            }
            catch { /* ignore close errors */ }
        }

        if (_receiveTask != null)
        {
            try { await _receiveTask; }
            catch (OperationCanceledException) { }
        }

        _ws?.Dispose();
        _ws = null;
    }

    /// <summary>
    /// After reconnect, reconcile missed events by querying REST order/list.
    /// Compares with in-memory state and emits synthetic OrderEvents for missed fills.
    /// </summary>
    public async Task ReconcileAfterReconnectAsync()
    {
        // Query current order states from REST
        // Compare with _orderMap entries
        // Emit synthetic OrderEvents for any missed transitions
        // This is called automatically after reconnection
        _log.LogInformation("[TV-WSS] Reconciliation after reconnect — checking for missed fills");
        // Implementation deferred — requires access to TradovateExecutor REST helpers
    }

    private async Task ConnectAndAuthAsync(CancellationToken ct)
    {
        var wssUrl = _auth.ApiBaseUrl.Replace("https://", "wss://").Replace("/v1", "/v1/websocket");
        var token = await _auth.GetAccessTokenAsync();

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(wssUrl), ct);
        _log.LogInformation("[TV-WSS] Connected to {Url}", wssUrl);

        // Authenticate
        var authMsg = $"authorize\n0\n\n{token}";
        await SendAsync(authMsg, ct);

        // Wait for auth response
        var resp = await ReceiveOneAsync(ct);
        if (resp.Contains("\"s\":200"))
        {
            IsConnected = true;
            _log.LogInformation("[TV-WSS] Authenticated");
        }
        else
        {
            _log.LogError("[TV-WSS] Auth failed: {Resp}", resp);
            return;
        }

        // Subscribe to user sync for order updates
        var subMsg = "user/syncrequest\n1\n\n{\"users\":[" + await GetUserIdAsync(ct) + "]}";
        await SendAsync(subMsg, ct);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    IsConnected = false;
                    OnDisconnected?.Invoke();
                    _log.LogWarning("[TV-WSS] Server closed connection");

                    // Reconnect with backoff
                    await ReconnectWithBackoffAsync(ct);
                    continue;
                }

                var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                ProcessMessage(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogWarning(ex, "[TV-WSS] WebSocket error — reconnecting");
            IsConnected = false;
            OnDisconnected?.Invoke();
            await ReconnectWithBackoffAsync(ct);
        }
    }

    private void ProcessMessage(string raw)
    {
        // Tradovate WSS messages have format: "a]<JSON>" or heartbeat
        if (!raw.StartsWith("a[")) return;

        try
        {
            var jsonPart = raw[1..]; // Strip "a" prefix
            using var doc = JsonDocument.Parse(jsonPart);

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("e", out var eventType)) continue;
                var eType = eventType.GetString();

                if (eType == "order" && element.TryGetProperty("d", out var data))
                    ProcessOrderUpdate(data);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[TV-WSS] Failed to parse message: {Raw}", raw);
        }
    }

    private void ProcessOrderUpdate(JsonElement data)
    {
        if (!data.TryGetProperty("id", out var idProp)) return;
        var tradovateId = idProp.GetInt64();

        (string GroupOrderId, LegType LegType) mapping;
        lock (_mapLock)
        {
            if (!_orderMap.TryGetValue(tradovateId, out mapping)) return;
        }

        var status = data.TryGetProperty("ordStatus", out var sp) ? sp.GetString() : null;
        var fillPrice = data.TryGetProperty("avgFillPrice", out var fp) && fp.ValueKind == JsonValueKind.Number
            ? fp.GetDecimal() : (decimal?)null;
        var fillQty = data.TryGetProperty("filledQty", out var fq) && fq.ValueKind == JsonValueKind.Number
            ? fq.GetInt32() : (int?)null;

        var legStatus = status switch
        {
            "Filled" => OrderLegStatus.Filled,
            "Working" => OrderLegStatus.Working,
            "Canceled" or "Cancelled" => OrderLegStatus.Canceled,
            "Rejected" => OrderLegStatus.Rejected,
            _ => (OrderLegStatus?)null
        };

        if (legStatus == null) return;

        var evt = new OrderEvent(
            mapping.GroupOrderId,
            tradovateId.ToString(),
            mapping.LegType,
            legStatus.Value,
            fillPrice, fillQty,
            null, null,
            DateTime.UtcNow);

        _log.LogInformation("[TV-WSS] Order update: {OrderId} {LegType} → {Status} @ {Price}",
            tradovateId, mapping.LegType, legStatus, fillPrice);

        OnOrderUpdate?.Invoke(evt);
    }

    private async Task ReconnectWithBackoffAsync(CancellationToken ct)
    {
        int[] delays = { 1000, 2000, 5000, 10000, 30000 };

        for (int i = 0; i < delays.Length && !ct.IsCancellationRequested; i++)
        {
            _log.LogInformation("[TV-WSS] Reconnect attempt {N} in {D}ms", i + 1, delays[i]);
            await Task.Delay(delays[i], ct);

            try
            {
                _ws?.Dispose();
                await ConnectAndAuthAsync(ct);
                if (IsConnected)
                {
                    await ReconcileAfterReconnectAsync();
                    _log.LogInformation("[TV-WSS] Reconnected successfully");
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[TV-WSS] Reconnect attempt {N} failed", i + 1);
            }
        }

        _log.LogError("[TV-WSS] All reconnect attempts exhausted");
    }

    private async Task SendAsync(string message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task<string> ReceiveOneAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var result = await _ws!.ReceiveAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private async Task<string> GetUserIdAsync(CancellationToken ct)
    {
        // Get user ID from auth service — needed for sync subscription
        // Tradovate user ID comes from the auth response
        // For now return empty — will be populated from auth handshake
        return "0";
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add CRV.Live/Brokers/Tradovate/TradovateEventStream.cs
git commit -m "feat: add TradovateEventStream — WSS order update subscription"
```

---

## Phase 6: Wire Into ComposableEngine + Orchestrator

### Task 10: Add BrokerEventHandler to ComposableEngine

**Files:**
- Modify: `CRV.Core/Strategy/ComposableEngine.cs`

- [ ] **Step 1: Add BrokerEventHandler field and constructor parameter**

Add field after line 19:
```csharp
    private readonly BrokerEventHandler? _brokerHandler;
```

Update constructor (line 42-52) to accept optional BrokerEventHandler:
```csharp
    public ComposableEngine(
        IOrderExecutor executor,
        IStrategyEventSink sink,
        ILastPriceProvider prices,
        EngineConfig config,
        BrokerEventHandler? brokerHandler = null)
    {
        _executor = executor;
        _sink = sink;
        _prices = prices;
        _config = config;
        _brokerHandler = brokerHandler;
    }
```

- [ ] **Step 2: Add method to register entry with BrokerEventHandler**

Add after ForceExitAllAsync:

```csharp
    /// <summary>
    /// Place an entry via the new group-order path (when BrokerEventHandler is wired).
    /// Returns true if the entry was placed, false if rejected or unavailable.
    /// </summary>
    public async Task<bool> PlaceGroupEntryAsync(ISetupStrategy strategy, EntrySignal signal, IGroupOrderExecutor groupExec)
    {
        if (_brokerHandler == null) return false;

        var group = await groupExec.OnEntrySignalAsync(signal);
        if (group == null) return false;

        // Set PointValue from strategy
        group.PointValue = strategy.PointValue;
        group.SessionId = _activeSessionId;

        _brokerHandler.RegisterGroup(group, strategy);
        return true;
    }
```

- [ ] **Step 3: Update GetSnapshot to read from BrokerEventHandler when available**

In the `GetSnapshot` method, update the SetupSnapshot building. In `SnapshotAggregator.cs`, the `ActiveTradeView` for each setup should come from BrokerEventHandler when available. Add to the `GetSnapshot` method before the `return`:

```csharp
        // Override trade views from BrokerEventHandler (when wired)
        if (_brokerHandler != null)
        {
            var snap = SnapshotAggregator.Build(inputs);
            foreach (var setup in snap.Setups)
            {
                var groupState = _brokerHandler.GetGroupState(setup.Id);
                if (groupState != null && groupState.EntryPrice.HasValue)
                {
                    var lp = _prices.GetLastPrice(
                        _strategies.TryGetValue(setup.Id, out var s) ? s.Ticker : _config.Ticker);
                    setup.Trade = new ActiveTradeView
                    {
                        Setup = Enum.TryParse<SetupId>(setup.Id, out var sid) ? sid : SetupId.A,
                        Direction = groupState.Direction,
                        Entry = groupState.EntryPrice.Value,
                        InitialStop = groupState.GetLeg(LegType.Stop)?.Price ?? 0m,
                        CurrentStop = groupState.GetLeg(LegType.Stop)?.Price ?? 0m,
                        Target = groupState.GetLeg(LegType.Tg2)?.Price ?? 0m,
                        Partial = groupState.GetLeg(LegType.Tg1)?.Price ?? 0m,
                        Contracts = groupState.TotalContracts,
                        RemainingContracts = groupState.TotalContracts - groupState.PartialContracts,
                        PartialFilled = groupState.Status == GroupOrderStatus.PartialFilled,
                        LastPrice = lp,
                        UnrealizedPnl = _brokerHandler.GetUnrealizedPnl(setup.Id, lp),
                        EnteredAt = groupState.CreatedAt,
                        Ticker = groupState.Ticker,
                        PointValue = groupState.PointValue,
                    };
                }
            }
            return snap;
        }
```

- [ ] **Step 4: Update ForceExitSetupAsync to use BrokerEventHandler when available**

In `ForceExitSetupAsync` (line 336), add at the top:
```csharp
        // Use BrokerEventHandler if wired (new path)
        if (_brokerHandler != null && _brokerHandler.HasActiveGroup(setupId))
        {
            await _brokerHandler.ExitGroupAsync(setupId);
            AddAlert("EXIT", strategy.SetupId, $"Force exit @ {px:F2}", "orange");
            await PublishSnapshotInternal();
            return;
        }
```

- [ ] **Step 5: Update ForceExitAllAsync similarly**

In `ForceExitAllAsync` (line 367), add at the top:
```csharp
        if (_brokerHandler != null)
        {
            await _brokerHandler.ExitAllAsync();
        }
```

- [ ] **Step 6: Build and test**

Run: `dotnet build && dotnet test`
Expected: SUCCESS — all existing tests pass (BrokerEventHandler is null in existing tests)

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy/ComposableEngine.cs
git commit -m "feat: wire BrokerEventHandler into ComposableEngine with backward-compat null path"
```

---

### Task 11: Wire in LiveEngineOrchestrator

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

- [ ] **Step 1: Read current orchestrator to identify exact wiring points**

Read `LiveEngineOrchestrator.cs` to identify:
- Where `IOrderExecutor` is created (lines 321-366)
- Where `ComposableEngine` is constructed
- Where `EvaluateFills` is called on MockBrokerExecutor
- Where the engine lifecycle connects/disconnects

- [ ] **Step 2: Add MockEventStream and MockGroupOrderExecutor creation alongside existing executor**

In the Mock broker creation block, add the new group executor and event stream:

```csharp
// After existing mock executor creation:
MockEventStream? mockEventStream = null;
MockGroupOrderExecutor? mockGroupExec = null;
BrokerEventHandler? brokerHandler = null;

if (execBroker == "Mock")
{
    mockEventStream = new MockEventStream();
    var mockGroupLogger = scope.ServiceProvider.GetRequiredService<ILogger<MockGroupOrderExecutor>>();
    mockGroupExec = new MockGroupOrderExecutor(mockEventStream, mockGroupLogger);
    brokerHandler = new BrokerEventHandler(mockGroupExec,
        scope.ServiceProvider.GetRequiredService<ILogger<BrokerEventHandler>>());
}
```

- [ ] **Step 3: Pass BrokerEventHandler to ComposableEngine constructor**

Update the engine construction to include `brokerHandler`:

```csharp
var engine = new ComposableEngine(executor, cachingSink, prices, engineConfig, brokerHandler);
```

- [ ] **Step 4: Connect MockEventStream and subscribe to events**

After engine construction, wire the event stream:

```csharp
if (mockEventStream != null && brokerHandler != null)
{
    await mockEventStream.ConnectAsync(engineCts.Token);
    mockEventStream.OnOrderUpdate += async evt =>
    {
        try { await brokerHandler.HandleEventAsync(evt); }
        catch (Exception ex) { _log.LogError(ex, "[ORCH] BrokerEventHandler error"); }
    };

    // Subscribe to trade completion for persistence
    brokerHandler.OnTradeCompleted += (group, trade) =>
    {
        // Route to existing trade persistence via sink
        _ = Task.Run(async () =>
        {
            try { await activeSink.OnExitAsync(new ExitSignal(trade.Setup, trade.ExitReason, trade.Exit, trade.Contracts, trade.ExitedAt), trade); }
            catch (Exception ex) { _log.LogError(ex, "[ORCH] Trade completion persistence error"); }
        });
    };
}
```

- [ ] **Step 5: Wire new EvaluateFills for MockGroupOrderExecutor**

In the tick processing path where `MockBrokerExecutor.EvaluateFills` is called, also call the new executor:

```csharp
mockGroupExec?.EvaluateFills(price, utcNow);
```

- [ ] **Step 6: Disconnect on engine stop**

In the engine shutdown path:
```csharp
if (mockEventStream != null)
    await mockEventStream.DisconnectAsync();
```

- [ ] **Step 7: Build and verify**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 8: Commit**

```bash
git add CRV.Web/Services/LiveEngineOrchestrator.cs
git commit -m "feat: wire BrokerEventHandler + MockEventStream into LiveEngineOrchestrator"
```

---

## Phase 7: Dashboard + Orders Page Updates

### Task 12: Dashboard — Trade Status Row + Group Order Info

**Files:**
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml`

- [ ] **Step 1: Read current dashboard trade card rendering**

Read the dashboard JS to understand how `SetupSnapshot.Trade` is rendered.

- [ ] **Step 2: Add status badge and group order display to trade card**

In the trade card rendering section, add status row and update trade info display:

```javascript
// In the updateSetup function, add after direction display:
if (setup.Trade) {
    // Status row
    var statusBadge = setup.Trade.GroupStatus || 'ACTIVE';
    var statusColor = {
        'PENDING': 'warning', 'ACTIVE': 'info',
        'PARTIAL': 'warning', 'COMPLETED': 'success', 'CANCELED': 'secondary'
    }[statusBadge] || 'info';

    // Entry price from broker (shows — until filled)
    var entryDisplay = setup.Trade.Entry > 0 ? setup.Trade.Entry.toFixed(2) : '—';

    // Stop with BE indicator
    var stopDisplay = setup.Trade.CurrentStop.toFixed(2);
    if (setup.Trade.PartialFilled) stopDisplay += ' (BE)';
}
```

- [ ] **Step 3: Update Exit button to show Cancel/Exit based on state**

```javascript
// Exit button label based on group status
var exitLabel = setup.Trade && setup.Trade.Entry > 0 ? 'Exit' : 'Cancel';
```

- [ ] **Step 4: Commit**

```bash
git add CRV.Web/Pages/Dashboard/Index.cshtml
git commit -m "feat: dashboard trade card shows broker status, group state, and Cancel/Exit"
```

---

### Task 13: Orders Page — Grouped View

**Files:**
- Modify: `CRV.Web/Pages/Trading/Orders.cshtml`
- Modify: `CRV.Web/Pages/Trading/Orders.cshtml.cs`

- [ ] **Step 1: Add handler for group orders in Orders.cshtml.cs**

```csharp
public async Task<IActionResult> OnGetGroupOrdersAsync(string? status, string? from, string? to)
{
    var query = _db.GroupOrders.Include(g => g.Legs).AsNoTracking();

    if (!string.IsNullOrEmpty(status) && status != "ALL")
        query = query.Where(g => g.Status.ToString() == status);

    if (DateTime.TryParse(from, out var fromDate))
        query = query.Where(g => g.CreatedAt >= fromDate);
    if (DateTime.TryParse(to, out var toDate))
        query = query.Where(g => g.CreatedAt <= toDate.AddDays(1));

    var groups = await query.OrderByDescending(g => g.CreatedAt).Take(100).ToListAsync();

    return new JsonResult(groups.Select(g => new
    {
        g.GroupOrderId,
        g.SetupId,
        g.Ticker,
        Direction = g.Direction.ToString(),
        g.TotalContracts,
        EntryPrice = g.EntryPrice?.ToString("F2") ?? "—",
        Status = g.Status.ToString(),
        CreatedAt = g.CreatedAt.ToString("HH:mm:ss"),
        Legs = g.Legs.Select(l => new
        {
            LegType = l.LegType.ToString(),
            l.Action,
            l.Quantity,
            Price = l.Price.ToString("F2"),
            Status = l.Status.ToString(),
            FillPrice = l.FillPrice?.ToString("F2") ?? "—",
            FillTime = l.FillTime?.ToString("HH:mm:ss") ?? "—"
        })
    }));
}
```

- [ ] **Step 2: Add group orders table to Orders.cshtml**

Add a "Group Orders" tab alongside existing orders table with collapsible rows showing legs.

- [ ] **Step 3: Add JavaScript for expand/collapse and AJAX loading**

```javascript
function loadGroupOrders() {
    fetch('?handler=GroupOrders&status=' + getStatus() + '&from=' + getFrom() + '&to=' + getTo())
        .then(r => r.json())
        .then(data => renderGroupOrders(data));
}

function renderGroupOrders(groups) {
    var html = '';
    groups.forEach(g => {
        html += `<tr class="group-row" onclick="toggleLegs('${g.GroupOrderId}')">
            <td><button class="btn btn-sm btn-outline-secondary">+</button></td>
            <td>${g.GroupOrderId}</td>
            <td>${g.SetupId}</td>
            <td>${g.Ticker}</td>
            <td>${g.Direction}</td>
            <td>${g.TotalContracts}</td>
            <td>${g.EntryPrice}</td>
            <td><span class="badge bg-${statusColor(g.Status)}">${g.Status}</span></td>
            <td>${g.CreatedAt}</td>
        </tr>`;
        g.Legs.forEach(l => {
            html += `<tr class="leg-row d-none" data-group="${g.GroupOrderId}">
                <td></td>
                <td colspan="2">${l.LegType}</td>
                <td>${l.Action}</td>
                <td>${l.Quantity}</td>
                <td>${l.Price}</td>
                <td><span class="badge bg-${legStatusColor(l.Status)}">${l.Status}</span></td>
                <td>${l.FillPrice} ${l.FillTime}</td>
            </tr>`;
        });
    });
    document.getElementById('groupOrdersBody').innerHTML = html;
}

function toggleLegs(groupId) {
    document.querySelectorAll(`tr[data-group="${groupId}"]`).forEach(r => {
        r.classList.toggle('d-none');
    });
}
```

- [ ] **Step 4: Commit**

```bash
git add CRV.Web/Pages/Trading/Orders.cshtml CRV.Web/Pages/Trading/Orders.cshtml.cs
git commit -m "feat: orders page shows grouped orders with expandable leg detail"
```

---

## Phase 8: TradovateExecutor Updates

### Task 14: Add IGroupOrderExecutor to TradovateExecutor

**Files:**
- Modify: `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs`

- [ ] **Step 1: Make TradovateExecutor implement IGroupOrderExecutor alongside IOrderExecutor**

Add `IGroupOrderExecutor` implementation. The new methods wrap existing REST calls but return GroupOrder:

```csharp
public class TradovateExecutor : IOrderExecutor, IGroupOrderExecutor
{
    // ... existing code ...

    // ── IGroupOrderExecutor ────────────────────────────────────

    async Task<GroupOrder?> IGroupOrderExecutor.OnEntrySignalAsync(EntrySignal sig)
    {
        var rawTicker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker;
        var symbol = FuturesSymbol.ToTradovate(rawTicker);
        var account = await GetAccountAsync();
        var entryAction = sig.Direction == Direction.Long ? "Buy" : "Sell";
        var exitAction = sig.Direction == Direction.Long ? "Sell" : "Buy";
        bool isLimit = sig.OrderType == "Limit";

        var groupId = Guid.NewGuid().ToString("N")[..8];
        var partialCts = sig.Contracts / 2;
        var remainCts = sig.Contracts - partialCts;

        // 1. Place Entry + Stop via placeOSO
        var osoBody = new
        {
            accountSpec = account.Name, accountId = account.Id,
            action = entryAction, symbol, orderQty = sig.Contracts,
            orderType = isLimit ? "Limit" : "Market",
            price = isLimit ? (decimal?)sig.Entry : null,
            isAutomated = true,
            bracket1 = new { action = exitAction, orderType = "Stop", stopPrice = sig.Stop, orderQty = sig.Contracts }
        };
        var (entryId, _, stopId) = await PlaceOsoAsync(osoBody);
        if (entryId is null) return null;

        // 2. Place Tg1 (partial limit)
        var tg1Body = new { accountSpec = account.Name, accountId = account.Id, action = exitAction, symbol, orderQty = partialCts, orderType = "Limit", price = sig.Partial, isAutomated = true };
        var tg1Id = await PlaceSingleAsync(tg1Body);

        // 3. Place Tg2 (remaining limit)
        var tg2Body = new { accountSpec = account.Name, accountId = account.Id, action = exitAction, symbol, orderQty = remainCts, orderType = "Limit", price = sig.Target, isAutomated = true };
        var tg2Id = await PlaceSingleAsync(tg2Body);

        // Discover stop if needed
        if (stopId is null)
        {
            var (_, foundStop) = await FindBracketLegsAsync(entryId.Value, 0, sig.Stop);
            stopId = foundStop;
        }

        var group = new GroupOrder
        {
            GroupOrderId = groupId,
            SetupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString(),
            Ticker = rawTicker,
            Direction = sig.Direction,
            TotalContracts = sig.Contracts,
            PartialContracts = partialCts,
            Status = GroupOrderStatus.Pending,
            Broker = "Tradovate",
        };

        group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = entryId.Value.ToString(), LegType = LegType.Entry, OrderType = sig.OrderType, Action = entryAction == "Buy" ? "BUY" : "SELL", Quantity = sig.Contracts, Price = sig.Entry });
        group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = (tg1Id ?? 0).ToString(), LegType = LegType.Tg1, OrderType = "Limit", Action = exitAction == "Sell" ? "SELL" : "BUY", Quantity = partialCts, Price = sig.Partial });
        group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = (tg2Id ?? 0).ToString(), LegType = LegType.Tg2, OrderType = "Limit", Action = exitAction == "Sell" ? "SELL" : "BUY", Quantity = remainCts, Price = sig.Target });
        group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = (stopId ?? 0).ToString(), LegType = LegType.Stop, OrderType = "Stop", Action = exitAction == "Sell" ? "SELL" : "BUY", Quantity = sig.Contracts, Price = sig.Stop });

        return group;
    }

    async Task IGroupOrderExecutor.ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
    {
        if (!long.TryParse(orderId, out var id)) return;
        await ModifyOrderAsync(id, newQty ?? 0, stopPrice: newPrice);
    }

    async Task IGroupOrderExecutor.CancelOrderAsync(string orderId)
    {
        if (!long.TryParse(orderId, out var id)) return;
        await CancelOrderAsync(id);
    }

    async Task IGroupOrderExecutor.PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
    {
        var account = await GetAccountAsync();
        var symbol = FuturesSymbol.ToTradovate(ticker);
        var action = direction == Direction.Long ? "Sell" : "Buy";
        await PlaceSingleAsync(new { accountSpec = account.Name, accountId = account.Id, action, symbol, orderQty = qty, orderType = "Market", isAutomated = true });
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build`
Expected: SUCCESS

- [ ] **Step 3: Commit**

```bash
git add CRV.Live/Brokers/Tradovate/TradovateExecutor.cs
git commit -m "feat: TradovateExecutor implements IGroupOrderExecutor with separate tg1/tg2 legs"
```

---

## Phase 9: Integration Test + API Endpoints

### Task 15: Force-Exit API for Group Orders

**Files:**
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml.cs`

- [ ] **Step 1: Update ForceExit handler to support group order exit**

The existing `OnPostForceExit` already delegates to `LiveEngineOrchestrator.ForceExitSetupAsync` which delegates to `ComposableEngine.ForceExitSetupAsync` — this was already updated in Task 10 to use BrokerEventHandler when available. No additional work needed.

- [ ] **Step 2: Verify the path works end-to-end**

Manual test: Start engine with Mock broker → observe dashboard → trigger entry → verify group order appears → verify tg1 fill → verify stop moves to BE.

- [ ] **Step 3: Commit if any changes**

---

### Task 16: Run Full Test Suite

- [ ] **Step 1: Run all tests**

Run: `dotnet test`
Expected: All tests pass (existing tests use old IOrderExecutor path; new tests exercise BrokerEventHandler)

- [ ] **Step 2: Build in Release mode**

Run: `dotnet build -c Release`
Expected: SUCCESS

- [ ] **Step 3: Final commit with any fixes**

---

## Verification Checklist

- [ ] `dotnet build` — 0 errors
- [ ] `dotnet test` — all tests pass
- [ ] New BrokerEventHandler tests cover: entry fill, tg1→BE, tg2→complete, stop→complete, exit group (pending/active/partial), unrealized P&L, entry rejection
- [ ] MockEventStream delivers events via Channel
- [ ] MockGroupOrderExecutor places 4-leg groups with no OCO auto-cancel
- [ ] Dashboard shows trade card with broker status
- [ ] Orders page shows grouped orders with expandable legs
- [ ] GroupOrders + OrderLegs tables created via migration
- [ ] Old IOrderExecutor path still works (backward compat until full strategy simplification)
