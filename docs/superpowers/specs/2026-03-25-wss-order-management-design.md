# WSS-Driven Order Management Design

**Date:** 2026-03-25
**Status:** Approved
**Scope:** Tradovate WSS order/position subscription, group orders, broker-as-source-of-truth, mock broker parity, dashboard + orders page updates

---

## Problem

The current architecture has the strategy engine and the broker maintaining separate views of trade state. Strategies track `_isActive`, `_entry`, `_stop`, `_remainingContracts` internally, while the broker (Tradovate or Mock) has its own order state. The TradovateExecutor polls REST for fill prices (up to 15 retries at 200ms). This dual-tracking causes discrepancies — entry prices differ, partial/BE state drifts, and the mock broker doesn't behave like the real broker.

## Solution

Flip the ownership: **broker is the single source of truth** for all trade lifecycle state. Strategies become pure signal generators — they decide *when* to enter but don't track the trade. A unified event stream interface (`IBrokerEventStream`) makes Tradovate WSS and Mock broker emit identical events. A new `BrokerEventHandler` reacts to these events and drives all dashboard updates, leg management (tg1 fill → BE move), and trade recording.

---

## 1. Unified Event Stream Interface

### IBrokerEventStream

```csharp
public interface IBrokerEventStream
{
    event Action<OrderEvent>? OnOrderUpdate;
    event Action? OnDisconnected;
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
}
```

### OrderEvent

```csharp
public record OrderEvent(
    string GroupOrderId,
    string OrderId,
    LegType LegType,          // Entry | Tg1 | Tg2 | Stop
    OrderLegStatus Status,     // Working | Filled | Modified | Canceled | Rejected
    decimal? FillPrice,
    int? FillQty,
    decimal? ModifiedPrice,
    int? ModifiedQty,
    DateTime Timestamp
);

public enum LegType { Entry, Tg1, Tg2, Stop }
public enum OrderLegStatus { Working, Filled, Modified, Canceled, Rejected }
```

### Implementations

**TradovateEventStream:**
- Connects to `wss://live.tradovateapi.com/v1/websocket`
- Authenticates with `accessToken` from `TradovateAuthService`
- Subscribes to `user/syncrequest` for real-time order updates
- Parses `order/update` messages into `OrderEvent`
- Reconnection with exponential backoff (same pattern as `TradovateBarFeed`)
- **Reconnection reconciliation:** After reconnect + reauth, calls `GET /order/list` to fetch current status of all orders for active GroupOrders. Compares with in-memory state and emits synthetic `OrderEvent`s for any missed transitions (e.g., a fill that happened during the disconnection window). This prevents state drift from brief WSS dropouts.
- Raises `OnDisconnected` event so dashboard can show connection health
- Lifecycle: connects/disconnects with the engine

**MockEventStream:**
- Implements `IBrokerEventStream`
- Backed by `Channel<OrderEvent>` (unbounded)
- `MockBrokerExecutor` pushes events onto the channel when orders change state
- Background loop reads from channel and raises `OnOrderUpdate`

---

## 2. Group Order Model

### GroupOrder

```csharp
public class GroupOrder
{
    public string GroupOrderId { get; set; }   // GUID short (8 chars)
    public string SetupId { get; set; }        // "A", "B", "b-mnq-1"
    public string Ticker { get; set; }
    public Direction Direction { get; set; }
    public int TotalContracts { get; set; }
    public int PartialContracts { get; set; }
    public decimal? EntryPrice { get; set; }   // null until entry fills
    public decimal PointValue { get; set; }    // for P&L calculation
    public decimal AccruedPartialPnl { get; set; } // locked P&L from tg1 fill
    public GroupOrderStatus Status { get; set; }
    public string Broker { get; set; }
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<OrderLeg> Legs { get; set; }
}

public enum GroupOrderStatus { Pending, Active, PartialFilled, Completed, Canceled }
```

### OrderLeg

```csharp
public class OrderLeg
{
    public string GroupOrderId { get; set; }
    public string OrderId { get; set; }        // broker's order ID (stored as string for broker-agnostic compat; Tradovate uses long, converted via ToString()/long.Parse())
    public LegType LegType { get; set; }
    public string OrderType { get; set; }      // Market | Limit | Stop
    public string Action { get; set; }         // BUY | SELL
    public int Quantity { get; set; }
    public decimal Price { get; set; }         // limit or stop price
    public OrderLegStatus Status { get; set; }
    public decimal? FillPrice { get; set; }
    public DateTime? FillTime { get; set; }
    public DateTime? LastModifiedAt { get; set; }
}
```

### Status Transitions

```
Pending → Active           (entry leg filled)
Active → PartialFilled     (tg1 leg filled, stop modified to BE)
PartialFilled → Completed  (tg2 or stop filled)
Active → Completed         (stop filled before tg1)
Any → Canceled             (user clicks Cancel/Exit)
```

### Order Placement

Entry signal produces 4 legs, placed via REST:

1. **Entry + Stop** — `placeOSO` with entry + stop bracket (full qty)
2. **Tg1** — separate limit order (partial qty)
3. **Tg2** — separate limit order (remaining qty)

All order IDs stored in `GroupOrder.Legs`. The `placeOSO` brackets the entry with the stop only. Tg1 and Tg2 are independent limit orders.

### Race Condition: Independent Exit Legs

Since tg1, tg2, and stop are not broker-linked via OCO, there is a race window between a fill event arriving via WSS and BrokerEventHandler's cancel command reaching the broker. For example:
- Tg2 fills → WSS event arrives → BrokerEventHandler sends cancel-stop → but stop may have already filled in the window

**Mitigations:**
1. **BrokerEventHandler tolerates double-fill**: If stop fills after tg2 already filled (or vice versa), the handler detects the unexpected fill (group already completing) and immediately places a counter-order to flatten the unintended position. This is logged as an alert.
2. **Tg1 + Stop race**: After tg1 fills, the stop modification (qty reduced, price → BE) has a similar window. If stop fills at original qty/price during this window, BrokerEventHandler detects over-exit and flattens. This race is inherent to any non-atomic multi-leg modification.
3. **Future optimization**: If Tradovate supports `placeOCO` for tg2+stop linking, this eliminates the tg2/stop race. Can be added later without architectural changes — just a different placement call in the executor.

---

## 3. IOrderExecutor Changes

### New Signature

```csharp
public interface IOrderExecutor
{
    Task<GroupOrder?> OnEntrySignalAsync(EntrySignal signal);
    Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty);
    Task CancelOrderAsync(string orderId);
    Task PlaceMarketCloseAsync(string ticker, Direction direction, int qty);
}
```

**Removed:** `OnPartialSignalAsync`, `OnBESignalAsync`, `OnExitSignalAsync`, `OnLevelsAdjustedAsync` — these are now driven by `BrokerEventHandler` reacting to WSS events and calling `ModifyOrderAsync`/`CancelOrderAsync`/`PlaceMarketCloseAsync`.

**OnEntrySignalAsync** returns a `GroupOrder` with all leg order IDs populated, or null on failure.

---

## 4. BrokerEventHandler

The central component replacing trade lifecycle logic from strategies and `ComposableEngine.RouteSignalsAsync`.

### Responsibilities

- Subscribes to `IBrokerEventStream.OnOrderUpdate`
- Maintains in-memory `Dictionary<string, GroupOrder>` of active groups (keyed by SetupId)
- Reacts to events with state transitions and broker commands
- Calculates unrealized P&L for dashboard snapshots
- Persists group/leg state changes to DB

### Event Reactions

| Event | Action |
|-------|--------|
| Entry FILLED | group.Status=Active, EntryPrice=fillPrice, strategy.SetInTrade(true), update dashboard |
| Tg1 FILLED | group.Status=PartialFilled, accrue partial P&L, call executor.ModifyOrderAsync(stop → BE, qty reduced) |
| Tg2 FILLED | call executor.CancelOrderAsync(stop), group.Status=Completed, record TradeRecord, strategy.SetInTrade(false) |
| Stop FILLED | call executor.CancelOrderAsync(tg1 and/or tg2), group.Status=Completed, record TradeRecord, strategy.SetInTrade(false) |
| Entry REJECTED | cancel tg1, tg2 (stop was OSO bracket — auto-canceled by broker), group.Status=Canceled, strategy.SetInTrade(false), alert |
| Other leg REJECTED | log alert, attempt to cancel remaining legs + market close if position open |

### ExitGroup(setupId) — Manual Cancel/Exit

State-dependent behavior:
- **Pending** (entry working): cancel all legs → group.Status=Canceled
- **Active** (entry filled, all legs working): cancel tg1, tg2, stop + market close full qty → group.Status=Completed
- **PartialFilled** (tg1 filled): cancel tg2, stop + market close remaining qty → group.Status=Completed

### Partial Entry Fill Policy

Entry orders are placed as **Market** (immediate fill, all-or-nothing) or **Limit** (fill-or-kill semantics). Partial entry fills are not supported — if a limit entry partially fills, BrokerEventHandler treats it as a full fill for the filled quantity and adjusts all leg quantities accordingly (tg1, tg2, stop). This is a defensive fallback; in practice, futures market/limit orders fill atomically.

### Session Reset

On session boundary (engine session change or daily reset), BrokerEventHandler force-closes any active GroupOrders via `ExitGroup`. Active groups should not span sessions. The `_activeGroups` dictionary is cleared after force-close.

### Unrealized P&L

```csharp
decimal GetUnrealizedPnl(string setupId)
{
    var group = _activeGroups[setupId];
    var remaining = group.TotalContracts - filledPartialQty;
    var unrealized = (lastPrice - group.EntryPrice) * pointValue * remaining;  // direction-adjusted
    return unrealized + accruedPartialPnl;
}
```

---

## 5. Strategy Simplification

### Removed from Strategies

- `_isActive`, `_entry`, `_stop`, `_target`, `_partial` fields
- `_remainingContracts`, `_partialFilled`, `_pnl`
- `_awaitingTickConfirm` and tick confirmation gate
- `TryTickConfirmedEntry`, `ApplyFill`, `RevertEntryToTickGate`
- `GetActiveTrade()` — unrealized P&L moves to `BrokerEventHandler`
- All exit signal generation (`PendingExit`, `PendingPartial`, `PendingBE`)
- `ForceExit()` — becomes `BrokerEventHandler.ExitGroup(setupId)`

### Remains in Strategies

- `OnBar()` — arm/disarm logic, entry condition detection
- `OnTick()` — tick-based entry gate only
- `PendingEntry` — the only signal strategies emit
- `Reconfigure()`, `Reset()`, `Disarm()`
- `IsArmed` state

### New

- `SetInTrade(bool)` — called by `BrokerEventHandler` to block re-arming
- `InTrade` property — checked by arm logic to prevent duplicate entries

### ISetupStrategy Interface (Simplified)

```csharp
public interface ISetupStrategy
{
    string Id { get; }
    SetupId SetupId { get; }
    string Name { get; }
    string Ticker { get; }
    decimal PointValue { get; }
    bool IsArmed { get; }
    bool InTrade { get; }

    void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules);
    void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules);

    EntrySignal? PendingEntry { get; }
    void ClearPendingSignals();

    void SetInTrade(bool active);
    void Reconfigure(StrategySetupConfig config);
    void Reset();
    void ResetSession();
    void Disarm();
    void ResetCutoff();
    void ResetTradeCounters();
    (int Hour, int Minute) GetCutoffForSession(string sessionId);
    bool IsEnabledForSession(string sessionId);
}
```

### EntrySignal (Updated)

Field renames: `Partial` → `Tg1Price`, `Target` → `Tg2Price`, `Contracts` → `TotalContracts`. Since `EntrySignal` is shared with backtest code, the backtest constructors must be updated in Phase 1 (search-and-replace across strategies and backtest executor).

```csharp
public record EntrySignal(
    SetupId Setup,
    Direction Direction,
    decimal Entry,          // requested entry price
    decimal Stop,           // stop level
    decimal Tg1Price,       // partial target (was "Partial")
    decimal Tg2Price,       // final target (was "Target")
    int TotalContracts,
    int PartialContracts,   // contracts to exit at tg1
    DateTime Time,
    string OrderType = "Market",
    string SessionId = "",
    string Ticker = "",
    string SetupLabel = ""
);
```

---

## 6. ComposableEngine Changes

### RouteSignalsAsync (Simplified)

```
For each StrategySignals:
  if Entry signal:
    - Risk check via Risk.CanTrade()
    - Call brokerEventHandler.OnEntryRequested(signal)
    - (BrokerEventHandler places orders, WSS events drive everything after)
  // No more Partial/BE/Exit routing — handled by BrokerEventHandler
```

### IStrategyEventSink Fate

The existing `IStrategyEventSink` interface is **kept but simplified**:
- **Kept:** `OnSnapshotAsync` — still used for SignalR broadcast to dashboard
- **Kept:** `OnExitAsync` — called by `BrokerEventHandler` (not engine) when a trade completes, for trade persistence via `SignalREventSink`
- **Removed from engine routing:** `OnEntryAsync`, `OnPartialAsync`, `OnBEMoveAsync` — these events are now internal to `BrokerEventHandler`, which updates dashboard state directly via snapshot updates
- `BrokerEventHandler` holds a reference to `IStrategyEventSink` for `OnExitAsync` and `OnSnapshotAsync`

### StrategySignals Simplified

`StrategySignals` record drops `Exit`, `Partial`, `BE`, and `PreExitTrade` fields — only `Entry` remains:

```csharp
record StrategySignals(ISetupStrategy Strategy, EntrySignal? Entry);
```

### Snapshot Building

`SetupSnapshot.Trade` populated from `BrokerEventHandler.GetGroupState(setupId)` instead of `strategy.GetActiveTrade()`. The `BrokerEventHandler` provides:
- Direction, entry price, stop price, tg1/tg2 status, contracts, unrealized P&L
- All derived from the live `GroupOrder` state

---

## 7. Dashboard Card Changes

### Trade Card Rows (When In Trade)

| Row | Source | Example |
|-----|--------|---------|
| Status | entry leg status | `WORKING` → `FILLED` |
| Direction | group.Direction | `LONG` |
| Entry | group.EntryPrice (null until filled) | `—` → `5001.25` |
| Stop | stop leg price | `4990.00` → `5001.25 (BE)` |
| Tg1 (Partial) | tg1 leg status + price | `5010.00 WORKING` → `5010.00 ✓` |
| Tg2 (Target) | tg2 leg status + price | `5030.00 WORKING` |
| Contracts | remaining qty | `4` → `2` |
| Unrealized P&L | BrokerEventHandler | `+$225.00` |

### Status Badge Colors

- WORKING → yellow
- FILLED → green
- CANCELED → grey
- REJECTED → red

### Exit Button (State-Dependent)

- Entry WORKING → button label: **"Cancel"** (cancels all, no position)
- Entry FILLED → button label: **"Exit"** (market close + cancel working legs)

---

## 8. Orders Page Redesign

### Collapsed View (One Row Per Group)

| Group ID | Setup | Symbol | Direction | Qty | Entry | Status | Time |
|----------|-------|--------|-----------|-----|-------|--------|------|
| a1b2c3 | B-Pullback | /NQ | LONG | 4 | 5001.25 | ACTIVE | 14:30 |

Status badges: PENDING (yellow), ACTIVE (blue), PARTIAL (orange), COMPLETED (green), CANCELED (grey)

### Expanded View (Click [+] to Show Legs)

| Leg | Action | Qty | Price | Status | Fill Price | Fill Time |
|-----|--------|-----|-------|--------|------------|-----------|
| Entry | BUY | 4 | Market | FILLED | 5001.25 | 14:30:02 |
| Tg1 | SELL | 2 | 5010.00 | FILLED | 5010.00 | 14:45:10 |
| Tg2 | SELL | 2 | 5030.00 | WORKING | — | — |
| Stop | SELL | 2 | 5001.25 | WORKING | — | — |

### Filters

Same as today (broker, status, date range) but status filters apply to group status, not individual legs. Cancel button on group row only, calls `ExitGroup`.

---

## 9. Mock Broker Redesign

### MockBrokerExecutor

- `OnEntrySignalAsync` → places all 4 legs, returns `GroupOrder`
- Market entry: fills immediately, emits `OrderEvent(Entry, FILLED)`
- Limit entry: stays WORKING, fills via `EvaluateFills`
- `EvaluateFills(price)` → evaluates all WORKING legs, emits `OrderEvent` for each fill
- `ModifyOrderAsync` → updates leg in-memory, emits `OrderEvent(leg, MODIFIED)`
- `CancelOrderAsync` → cancels leg, emits `OrderEvent(leg, CANCELED)`

### Key Design Decision

Mock broker is a **dumb order book** — it does NOT auto-cancel OCO partners on fill. That logic lives in `BrokerEventHandler`, which reacts to fill events and explicitly cancels other legs via `CancelOrderAsync`. This ensures the mock broker exercises the exact same `BrokerEventHandler` code path as Tradovate.

### MockEventStream

- Implements `IBrokerEventStream`
- `Channel<OrderEvent>` (unbounded)
- Background loop reads channel, raises `OnOrderUpdate`
- Same consumer code (`BrokerEventHandler`) processes both mock and Tradovate events

---

## 10. Database Migration

### New Tables

```sql
CREATE TABLE GroupOrders (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    GroupOrderId TEXT NOT NULL UNIQUE,
    SetupId TEXT NOT NULL,
    Ticker TEXT NOT NULL,
    Direction TEXT NOT NULL,
    TotalContracts INTEGER NOT NULL,
    PartialContracts INTEGER NOT NULL,
    EntryPrice REAL,
    PointValue REAL NOT NULL,
    AccruedPartialPnl REAL NOT NULL DEFAULT 0,
    Status TEXT NOT NULL DEFAULT 'Pending',
    Broker TEXT NOT NULL,
    SessionId TEXT,
    CreatedAt TEXT NOT NULL,
    CompletedAt TEXT
);

CREATE TABLE OrderLegs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    GroupOrderId TEXT NOT NULL REFERENCES GroupOrders(GroupOrderId),
    OrderId TEXT NOT NULL UNIQUE,
    LegType TEXT NOT NULL,
    OrderType TEXT NOT NULL,
    Action TEXT NOT NULL,
    Quantity INTEGER NOT NULL,
    Price REAL NOT NULL,
    Status TEXT NOT NULL DEFAULT 'Working',
    FillPrice REAL,
    FillTime TEXT,
    LastModifiedAt TEXT
);

CREATE INDEX IX_OrderLegs_GroupOrderId ON OrderLegs(GroupOrderId);
```

### Migration Strategy

- Add new tables alongside existing `Orders` table
- Old `OrderRecord` table stays for historical data (no row migration)
- New code writes exclusively to `GroupOrders` + `OrderLegs`
- Orders page queries new tables; legacy orders accessible via date cutoff

---

## 11. Backtest Implications

The backtest executor currently uses a simpler path — strategies emit all signals and the backtest records trades directly. With this redesign:

- **Phase 1 (this spec):** Live + Mock use the new architecture
- **Phase 2 (follow-up):** Backtest executor emits events through `MockEventStream` so `BrokerEventHandler` works identically. Until then, backtest continues with current flow.

---

## File Impact Summary

### New Files
- `CRV.Core/Interfaces/IBrokerEventStream.cs` — event stream interface + OrderEvent
- `CRV.Core/Models/GroupOrder.cs` — GroupOrder + OrderLeg models
- `CRV.Core/Strategy/BrokerEventHandler.cs` — central event handler
- `CRV.Live/Brokers/Tradovate/TradovateEventStream.cs` — WSS implementation
- `CRV.Live/Brokers/MockEventStream.cs` — mock implementation
- EF migration for new tables

### Modified Files
- `CRV.Core/Interfaces/IInterfaces.cs` — IOrderExecutor simplified, ISetupStrategy slimmed
- `CRV.Core/Models/Signals.cs` — EntrySignal updated (Tg1Price, Tg2Price, PartialContracts), StrategySignals reduced to Entry-only, ActiveTradeView removed (replaced by GroupOrder state)
- `CRV.Core/Strategy/TickerGroup.cs` — StrategySignals simplified (only Entry field)
- `CRV.Core/Strategy/ComposableEngine.cs` — RouteSignalsAsync simplified, snapshot reads from BrokerEventHandler
- `CRV.Core/Strategy/PullbackStrategy.cs` — remove trade lifecycle fields/methods
- `CRV.Core/Strategy/RetestStrategy.cs` — same
- `CRV.Core/Strategy/OrbFakeoutStrategy.cs` — same
- `CRV.Core/Strategy/SessionFakeoutStrategy.cs` — same
- `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs` — returns GroupOrder, exposes ModifyOrder/Cancel/MarketClose
- `CRV.Live/Brokers/MockBrokerExecutor.cs` — dumb order book + event emitter
- `CRV.Web/Pages/Dashboard/Index.cshtml` — status row, broker-sourced trade data, state-dependent Exit/Cancel
- `CRV.Web/Pages/Trading/Orders.cshtml` — grouped view with expand
- `CRV.Web/Pages/Trading/Orders.cshtml.cs` — query GroupOrders + OrderLegs
- `CRV.Web/Services/LiveEngineOrchestrator.cs` — wire BrokerEventHandler + EventStream lifecycle
- Test files — update for new interfaces
