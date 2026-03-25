# Phase 2: Strategy Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove trade lifecycle from strategies (~1,400 lines), making them pure signal generators. BrokerEventHandler manages all fills, partials, BE moves, and exits. Backtest continues working via MockGroupOrderExecutor + MockEventStream + BrokerEventHandler.

**Architecture:** Strategies emit only `EntrySignal` (no Exit/Partial/BE). When `BrokerEventHandler` is active, ComposableEngine routes entries through a new `BrokerEventHandler.PlaceEntryAsync()` method, which calls `IGroupOrderExecutor.OnEntrySignalAsync()` to create a `GroupOrder` with stop/tg1/tg2 legs, then registers the group for event tracking. `MockGroupOrderExecutor.EvaluateFills(price, time)` checks active legs against the current price and pushes `OrderEvent`s to `MockEventStream`, which fires `OnOrderUpdate` → `BrokerEventHandler.HandleEventAsync` processes fills, partials, BE moves, and trade completion. `BuildTradeRecord` in ComposableEngine is removed; `BrokerEventHandler.CompleteGroup` builds `TradeRecord`.

**Two mock executor classes (important distinction):**
- `MockBrokerExecutor : IOrderExecutor` — the OLD flat OCO-based executor (creates `MockOrder` objects). Used by the legacy `IOrderExecutor` path. Will be simplified but kept for the Orders page.
- `MockGroupOrderExecutor : IGroupOrderExecutor` — the NEW WSS-based executor (creates `GroupOrder` + legs, pushes events to `MockEventStream`). Used by `BrokerEventHandler`. This is what the backtest will use.

**Key design decisions:**
- `IsActive` stays on `ISetupStrategy` but becomes `=> InTrade` (set by BrokerEventHandler). This avoids rewriting 14+ call sites in TickerGroup/ComposableEngine.
- `RevertEntry()` stays on `ISetupStrategy` — it is signal coordination (opposing position guard, same-bar suppression), not trade lifecycle.
- `ForceExit` stays but becomes lightweight: just `SetInTrade(false)` + `ResetSession()`. The actual broker order cancellation is done by BrokerEventHandler via a new `ForceExitRequested` list returned from TickerGroup.
- `GetActiveTrade` is removed from `ISetupStrategy`. Call sites delegate to `BrokerEventHandler.GetGroupState()` to build `ActiveTradeView` from `GroupOrder`.
- `OnLevelsAdjustedAsync` stays on `IOrderExecutor` (has default implementation).
- When `_brokerHandler != null`, entries route through `BrokerEventHandler.PlaceEntryAsync()` → `IGroupOrderExecutor` instead of `IOrderExecutor`. This is how MockGroupOrderExecutor creates GroupOrders with legs.

**OrbStrategyEngine note:** The legacy `OrbStrategyEngine.cs` uses `ExitSignal`, `PartialSignal`, `BESignal`, and other removed types. It is still compiled but only used for the old (non-composable) engine path. Phase 2 does NOT touch OrbStrategyEngine — it will be deleted in a future cleanup. To prevent compile errors, we keep the signal record types but mark them `[Obsolete]`, or we delete OrbStrategyEngine as part of this plan (preferred — it is dead code now that ComposableEngine is the active path).

**Tech Stack:** C# / .NET 9 / ASP.NET Core Razor Pages / xUnit

---

## File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `CRV.Core/Interfaces/ISetupStrategy.cs` | Modify | Remove 7 trade-lifecycle members, keep IsActive/ForceExit/RevertEntry simplified |
| `CRV.Core/Interfaces/IInterfaces.cs` | Modify | Remove `OnPartialAsync`, `OnBEMoveAsync` from `IStrategyEventSink`; simplify `OnExitAsync`; remove `OnPartialSignalAsync`, `OnBESignalAsync`, `OnExitSignalAsync` from `IOrderExecutor` |
| `CRV.Core/Models/Signals.cs` | Modify | Delete `ExitSignal`, `PartialSignal`, `BESignal`; rename EntrySignal fields; delete bridge extensions |
| `CRV.Core/Strategy/TickerGroup.cs` | Modify | Simplify `StrategySignals`; simplify `CollectAndClearSignals`; rewrite `HasOpposingPosition` to use BrokerEventHandler; add force-exit collection |
| `CRV.Core/Strategy/ComposableEngine.cs` | Modify | Strip `RouteSignalsAsync` to entry-only; remove `BuildTradeRecord`; simplify `ForceExitAllAsync` + `ForceExitSetupAsync`; rewrite `GetActiveTrade`; fix `ResetWarmupCounters` |
| `CRV.Core/Strategy/SnapshotAggregator.cs` | Modify | Replace `strategy.GetActiveTrade()` with BrokerEventHandler lookup |
| `CRV.Core/Strategy/PullbackStrategy.cs` | Modify | Remove ~300 lines of trade lifecycle |
| `CRV.Core/Strategy/RetestStrategy.cs` | Modify | Remove ~300 lines of trade lifecycle |
| `CRV.Core/Strategy/OrbFakeoutStrategy.cs` | Modify | Remove ~300 lines of trade lifecycle |
| `CRV.Core/Strategy/SessionFakeoutStrategy.cs` | Modify | Remove ~300 lines of trade lifecycle |
| `CRV.Core/Strategy/OrbStrategyEngine.cs` | Delete | Dead code — ComposableEngine is the active path |
| `CRV.Live/Engine/LiveTradingEngine.cs` | Modify | Remove OrbStrategyEngine references |
| `CRV.Live/LiveEngineService.cs` | Modify | Remove OrbStrategyEngine references |
| `CRV.Core/Strategy/BrokerEventHandler.cs` | Modify | Add `PlaceEntryAsync()` method for entry routing |
| `CRV.Backtest/Engine/BacktestEngine.cs` | Modify | Wire BrokerEventHandler; simplify BacktestExecutor + BacktestSink |
| `CRV.Web/Services/SignalREventSink.cs` | Modify | Remove `OnPartialAsync`, `OnBEMoveAsync`; update `OnExitAsync` signature |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Modify | Update `SourceOverrideSink`, `ReplayFilterSink`, `SnapshotCachingSink` |
| `CRV.Live/Brokers/MockBrokerExecutor.cs` | Modify | Remove `OnPartialSignalAsync`, `OnBESignalAsync`, `OnExitSignalAsync` |
| `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs` | Modify | Same cleanup |
| `CRV.Live/Brokers/Schwab/SchwabExecutor.cs` | Modify | Same cleanup |
| `CRV.Live/Brokers/TradeStation/TradeStationExecutor.cs` | Modify | Same cleanup |
| `CRV.Core.Tests/Strategy/ComposableEngineTests.cs` | Modify | Update FakeStrategy + FakeSink + FakeExecutor |
| `CRV.Core.Tests/Strategy/TickerGroupTests.cs` | Modify | Update FakeStrategy |
| `CRV.Core.Tests/Strategy/TickEvalTests.cs` | Modify | Update NullExecutor + TestSink |
| `CRV.Core.Tests/Strategy/FalseBreakoutIntegrationTests.cs` | Modify | Update NullExecutor + TestSink |
| `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs` | Modify | Update fake strategy (RevertEntry removal) |
| `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs` | Modify | Update fake strategy (GetActiveTrade removal) |
| `CRV.Core.Tests/Strategy/PullbackStrategyTests.cs` | Modify | Remove trade lifecycle tests |
| `CRV.Core.Tests/Strategy/RetestStrategyTests.cs` | Modify | Remove trade lifecycle tests |
| `CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs` | Modify | Remove trade lifecycle tests |
| `CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs` | Modify | Remove trade lifecycle tests |

---

### Task 1: Delete OrbStrategyEngine (dead code)

**Rationale:** OrbStrategyEngine is the legacy engine, replaced by ComposableEngine. It references `ExitSignal`, `PartialSignal`, `BESignal`, `GetActiveTrade`, `ForceExit`, etc. Deleting it now prevents compile errors when we remove those types, and cleans up ~1,300 lines of dead code.

**Files:**
- Delete: `CRV.Core/Strategy/OrbStrategyEngine.cs`
- Modify: `CRV.Live/Engine/LiveTradingEngine.cs` (references OrbStrategyEngine)
- Modify: `CRV.Live/LiveEngineService.cs` (takes OrbStrategyEngine as parameter)
- Modify: `CRV.Core/Strategy/RiskManager.cs` (if references exist)
- Delete: Any OrbStrategyEngine test files

- [ ] **Step 1: Verify OrbStrategyEngine is not used in any active code path**

Search for `OrbStrategyEngine` in all `.cs` files. Verify it is only referenced by:
- Its own file
- Test files (if any — delete those too)
- Legacy configuration that can be removed

If it IS still used in an active path (e.g., `LiveTradingEngine.cs` creates one), then instead of deleting, wrap it in `#if false` / `#endif` or move to a `Legacy/` folder excluded from compilation. The key is to remove it from the compile so it doesn't block our interface changes.

- [ ] **Step 2: Delete the file and fix references**

```bash
rm CRV.Core/Strategy/OrbStrategyEngine.cs
```

Remove any `using` statements or factory code that references `OrbStrategyEngine`. If `LiveTradingEngine.cs` or `LiveEngineService.cs` has a code path that instantiates it, remove that path (it should already be dead — the live engine uses ComposableEngine).

- [ ] **Step 3: Build check**

Run: `dotnet build CRV.Core/CRV.Core.csproj`
Expected: 0 errors (OrbStrategyEngine had no dependents in the active path).

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "phase2: delete OrbStrategyEngine — dead code replaced by ComposableEngine"
```

---

### Task 2: Simplify IStrategyEventSink and IOrderExecutor

**Rationale:** Start with the downstream interfaces. Simplifying these first means we can update all implementations before touching the strategy interface, allowing intermediate build checks.

**Files:**
- Modify: `CRV.Core/Interfaces/IInterfaces.cs`
- Modify: `CRV.Core/Models/Signals.cs` (delete ExitSignal, PartialSignal, BESignal)

- [ ] **Step 1: Simplify IStrategyEventSink**

Remove `OnPartialAsync` and `OnBEMoveAsync`. Change `OnExitAsync` signature:

```csharp
public interface IStrategyEventSink
{
    Task OnEntryAsync(EntrySignal signal);
    Task OnExitAsync(TradeRecord completed);
    Task OnSnapshotAsync(EngineSnapshot snapshot);
}
```

- [ ] **Step 2: Simplify IOrderExecutor**

Remove `OnPartialSignalAsync`, `OnBESignalAsync`, `OnExitSignalAsync`. Keep `OnLevelsAdjustedAsync` (has default impl):

```csharp
public interface IOrderExecutor
{
    Task<decimal?> OnEntrySignalAsync(EntrySignal signal);

    /// <summary>
    /// Called after fill price adjustment changes stop/target levels or quantity.
    /// Broker should cancel + replace the existing bracket legs with updated price/qty.
    /// </summary>
    Task OnLevelsAdjustedAsync(string setupId, decimal newStop, decimal newTarget, int contracts) => Task.CompletedTask;
}
```

- [ ] **Step 3: Delete ExitSignal, PartialSignal, BESignal from Signals.cs**

Delete lines 36-59 (`ExitSignal`, `PartialSignal`, `BESignal` records). Keep `EntrySignal` and its extension methods (renames happen in Task 10).

- [ ] **Step 4: Do NOT build yet** — continue to Task 3 to update all implementations.

---

### Task 3: Update All Sink and Executor Implementations

**Rationale:** With interfaces simplified, update every implementation so the project compiles.

**Files:**
- Modify: `CRV.Web/Services/SignalREventSink.cs`
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs` (SourceOverrideSink, ReplayFilterSink, SnapshotCachingSink + line 502 OnExitAsync call)
- Modify: `CRV.Backtest/Engine/BacktestEngine.cs` (BacktestExecutor + BacktestSink)
- Modify: `CRV.Live/Brokers/MockBrokerExecutor.cs`
- Modify: `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs`
- Modify: `CRV.Live/Brokers/Schwab/SchwabExecutor.cs`
- Modify: `CRV.Live/Brokers/TradeStation/TradeStationExecutor.cs`

- [ ] **Step 1: SignalREventSink**

Remove `OnPartialAsync` and `OnBEMoveAsync` (both were `=> Task.CompletedTask`).

Change `OnExitAsync(ExitSignal sig, TradeRecord trade)` to `OnExitAsync(TradeRecord trade)`:
```csharp
public async Task OnExitAsync(TradeRecord trade)
{
    await _hub.Clients.All.SendAsync("Trade", new
    {
        time  = TimeZoneInfo.ConvertTimeFromUtc(trade.ExitedAt, _et).ToString("HH:mm:ss"),
        setup = !string.IsNullOrEmpty(trade.SetupLabel) ? trade.SetupLabel : trade.Setup.ToString(),
        // ... rest uses trade fields (already available)
    });
}
```

Key: `sig.Time` → `trade.ExitedAt`, `sig.Setup` → `trade.Setup`.

- [ ] **Step 2: SourceOverrideSink (LiveEngineOrchestrator.cs ~line 1156)**

Remove `OnPartialAsync`, `OnBEMoveAsync`. Update `OnExitAsync`:
```csharp
public Task OnExitAsync(TradeRecord t)
{
    t.Source = _source;
    return _inner.OnExitAsync(t);
}
```

- [ ] **Step 3: ReplayFilterSink (LiveEngineOrchestrator.cs ~line 1183)**

Remove `OnPartialAsync`, `OnBEMoveAsync`. Update `OnExitAsync`:
```csharp
public Task OnExitAsync(TradeRecord t) => _inner.OnExitAsync(t);
```

- [ ] **Step 4: SnapshotCachingSink (LiveEngineOrchestrator.cs ~line 1210)**

Remove `OnPartialAsync`, `OnBEMoveAsync`. Update `OnExitAsync`:
```csharp
public Task OnExitAsync(TradeRecord t) => _inner.OnExitAsync(t);
```

- [ ] **Step 5: LiveEngineOrchestrator direct OnExitAsync call (~line 502)**

Find the `await wrappedSink.OnExitAsync(...)` call and update to pass only `TradeRecord`. This is in the `OnTradeCompleted` handler where BrokerEventHandler fires a completed trade.

- [ ] **Step 6: BacktestExecutor — remove signal methods**

```csharp
internal class BacktestExecutor : IOrderExecutor
{
    private readonly BacktestConfig _btCfg;
    private readonly StrategyConfig _cfg;

    public BacktestExecutor(BacktestConfig btCfg, StrategyConfig cfg)
    { _btCfg = btCfg; _cfg = cfg; }

    public Task<decimal?> OnEntrySignalAsync(EntrySignal sig)
    {
        var fillPrice = ApplySlip(sig.Entry, sig.Direction == Direction.Long);
        return Task.FromResult<decimal?>(fillPrice);
    }

    private decimal ApplySlip(decimal price, bool isBuy)
    {
        if (_btCfg.FillMode != FillMode.WithSlippage) return price;
        decimal slip = _btCfg.SlippageTicks * _cfg.TickSize;
        return isBuy ? price + slip : price - slip;
    }
}
```

Note: `OnEntrySignalAsync` now returns a fill price (not null). The old code returned null and relied on strategy tick-gate for fill — that's gone now.

- [ ] **Step 7: BacktestSink — simplify**

```csharp
internal class BacktestSink : IStrategyEventSink
{
    private readonly List<TradeRecord> _trades;
    public BacktestSink(List<TradeRecord> trades) => _trades = trades;
    public Task OnEntryAsync(EntrySignal s) => Task.CompletedTask;
    public Task OnExitAsync(TradeRecord t) { _trades.Add(t); return Task.CompletedTask; }
    public Task OnSnapshotAsync(EngineSnapshot snap) => Task.CompletedTask;
}
```

- [ ] **Step 8: MockBrokerExecutor — remove OnPartialSignalAsync, OnBESignalAsync, OnExitSignalAsync**

Keep `OnEntrySignalAsync` (places group orders), `GetOrders()`, `CancelOrder()`, `EvaluateFills()`.

- [ ] **Step 9: TradovateExecutor, SchwabExecutor, TradeStationExecutor — same cleanup**

Remove `OnPartialSignalAsync`, `OnBESignalAsync`, `OnExitSignalAsync` from each.

- [ ] **Step 10: Build check**

Run: `dotnet build`
Expected: Errors only in files referencing `strategy.GetActiveTrade`, `strategy.PendingExit`, etc. — fixed in Tasks 4-6.

---

### Task 4: Simplify ISetupStrategy Interface

**Files:**
- Modify: `CRV.Core/Interfaces/ISetupStrategy.cs`

- [ ] **Step 1: Remove these 7 members**

```csharp
// Remove:
// Line 127: ExitSignal? PendingExit { get; }
// Line 128: PartialSignal? PendingPartial { get; }
// Line 129: BESignal? PendingBE { get; }
// Line 137: ActiveTradeView? PreExitTrade { get; }
// Line 140: void ApplyFill(decimal actualFillPrice);
// Line 153: void RevertEntryToTickGate(decimal entryLevel);
// Line 171: ActiveTradeView? GetActiveTrade(decimal lastPrice);
```

- [ ] **Step 2: Keep these members (simplified semantics)**

```csharp
// KEEP — IsActive: strategies change to `=> InTrade` (no internal trade state)
bool IsActive { get; }

// KEEP — InTrade + SetInTrade: set by BrokerEventHandler for entry suppression
bool InTrade { get; }
void SetInTrade(bool active);

// KEEP — ForceExit: simplified to just reset state (no signal generation)
// TickerGroup calls this for cutoff/session-disabled; BrokerEventHandler handles actual order cancellation
void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd);

// KEEP — RevertEntry: signal coordination (opposing position guard, same-bar suppression)
void RevertEntry();

// KEEP — ClearPendingSignals: only clears PendingEntry now
void ClearPendingSignals();

// KEEP — PendingEntry
EntrySignal? PendingEntry { get; }
```

- [ ] **Step 3: Do NOT build yet** — continue to Task 5.

---

### Task 5: Strip Trade Lifecycle from All 4 Strategies

**Rationale:** Each strategy has ~300+ lines of trade management. Remove it, keeping only signal generation (OnBar/OnTick emit PendingEntry).

**Files:**
- Modify: `CRV.Core/Strategy/PullbackStrategy.cs`
- Modify: `CRV.Core/Strategy/RetestStrategy.cs`
- Modify: `CRV.Core/Strategy/OrbFakeoutStrategy.cs`
- Modify: `CRV.Core/Strategy/SessionFakeoutStrategy.cs`

- [ ] **Step 1: PullbackStrategy — remove trade lifecycle**

**Remove these private fields** (lines 23-31, 34-35, 38-39, 60-63):
```
_entry, _stop, _target, _partial, _initStop, _contracts, _partHit, _pnl, _entryTime
_stickyTgt, _stickyStop
_awaitingTickConfirm, _theoreticalEntry
_pendingExit, _pendingPartial, _pendingBe, _preExitTrade
```

**Change `IsActive`** (line 77):
```csharp
// Before: public bool IsActive => _state == 2 || _state == -2;
// After:
public bool IsActive => _inTrade;
```

**Keep `InTrade` / `SetInTrade`** (lines 79-81) — unchanged.

**Remove properties**: `PendingExit`, `PendingPartial`, `PendingBE`, `PreExitTrade` (lines 89-92).

**Simplify `ClearPendingSignals`** (lines 94-103):
```csharp
public void ClearPendingSignals() => _pendingEntry = null;
```

**Keep `RevertEntry`** (lines 115-124) but simplify:
```csharp
public void RevertEntry()
{
    _pendingEntry = null;
    // Reset state back to armed so strategy can re-trigger
    if (_state == 2) _state = 1;
    else if (_state == -2) _state = -1;
}
```

**Simplify `ForceExit`** (lines 403-410):
```csharp
public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd)
{
    _pendingEntry = null;
    _state = 0;
    // Trade state (InTrade) is managed by BrokerEventHandler — don't touch _inTrade here
}
```

**Remove**: `RevertEntryToTickGate` (126-131), `ApplyFill` (413-425), `GetActiveTrade` (451-477).

**Remove private helpers**: `StageOrEnter` (481-487), `TryTickConfirmedEntry` (489-509), `BookExit` (541-560).

**Simplify `TryEntry`** (lines 511-539) — keep signal creation, remove trade state:
```csharp
private void TryEntry(decimal ep, bool isLong, OrbState orb, DateTime time)
{
    int cts = CalcContracts();
    if (cts <= 0) return;

    // Keep existing stop/target/partial calculations (they use ORB levels, ATR, etc.)
    decimal stop = /* existing calculation */;
    decimal target = /* existing calculation */;
    decimal partial = /* existing calculation */;
    int partialCts = CalcPartialCts(cts, _cfg.PartialCts);

    _pendingEntry = new EntrySignal(
        Setup: _cfg.SetupId, Direction: isLong ? Direction.Long : Direction.Short,
        Entry: ep, Stop: stop, Target: target, Partial: partial,
        Contracts: cts, Time: time,
        OrderType: _cfg.OrderType, SessionId: _sessionId,
        Ticker: _cfg.Ticker, PartialContracts: partialCts);

    _tradeCount++;
    _state = 0;  // consumed — go idle
}
```

**Keep**: `CalcContracts` (562-569), `CalcPartialCts` (571-576).

**Clean `OnTick`**: Remove all tick-mode exit/partial/BE logic. Keep tick-mode entry logic (checking if price reaches armed level).

**Clean `OnBar`**: Remove exit evaluation on bar close. Keep arm/de-arm logic and entry signal generation.

- [ ] **Step 2: RetestStrategy — same pattern**

Same removals. `IsActive` was `=> _state == 3 || _state == -3`; change to `=> _inTrade`.

- [ ] **Step 3: OrbFakeoutStrategy — same pattern**

Same removals. Also delete `TryEntryFromTick` (483-519).

- [ ] **Step 4: SessionFakeoutStrategy — same pattern**

Same removals. Also delete `TryEntryFromTick` (493-534).

- [ ] **Step 5: Build check**

Run: `dotnet build CRV.Core/CRV.Core.csproj`
Expected: Errors in ComposableEngine, TickerGroup, SnapshotAggregator, tests — fixed in Tasks 6-8.

---

### Task 6: Simplify TickerGroup and ComposableEngine

**Rationale:** These files use `GetActiveTrade`, `PendingExit`, `BuildTradeRecord`, and the full signal routing pipeline. Simplify to entry-only routing with BrokerEventHandler handling exits.

**Files:**
- Modify: `CRV.Core/Strategy/TickerGroup.cs`
- Modify: `CRV.Core/Strategy/ComposableEngine.cs`
- Modify: `CRV.Core/Strategy/SnapshotAggregator.cs`

#### TickerGroup changes:

- [ ] **Step 1: Simplify StrategySignals record** (line 11-17)

```csharp
public record StrategySignals(
    ISetupStrategy Strategy,
    EntrySignal? Entry);
```

- [ ] **Step 2: Simplify CollectAndClearSignals** (lines 372-381)

```csharp
public List<StrategySignals> CollectAndClearSignals()
{
    var result = new List<StrategySignals>(_strategies.Count);
    foreach (var s in _strategies)
    {
        result.Add(new StrategySignals(s, s.PendingEntry));
        s.ClearPendingSignals();
    }
    return result;
}
```

- [ ] **Step 3: Rewrite HasOpposingPosition** (lines 654-666)

The current implementation calls `strategy.GetActiveTrade(0)` to check direction. Replace with `InTrade` + a direction query. Add a `_brokerHandler` reference to TickerGroup:

Option A — Pass BrokerEventHandler to TickerGroup:
```csharp
// Add to TickerGroup constructor/field:
private readonly BrokerEventHandler? _brokerHandler;

private bool HasOpposingPosition(ISetupStrategy entering, bool isLong)
{
    if (_brokerHandler == null) return false;
    foreach (var s in _strategies)
    {
        if (s == entering || !s.InTrade) continue;
        var group = _brokerHandler.GetGroupState(s.SetupId);
        if (group != null && (group.Direction == Direction.Long) != isLong)
            return true;
    }
    return false;
}
```

Option B — Simpler: track direction on the strategy itself. Add `Direction? TradeDirection { get; }` to ISetupStrategy. BrokerEventHandler sets it on entry fill. **Prefer Option A** since it keeps the interface minimal.

- [ ] **Step 4: Add BrokerEventHandler to TickerGroup**

TickerGroup needs a reference for `HasOpposingPosition` and for force-exits. Add to constructor:
```csharp
public TickerGroup(TickerGroupConfig cfg, TimeZoneInfo tz, BrokerEventHandler? brokerHandler = null)
{
    // ... existing init ...
    _brokerHandler = brokerHandler;
}
```

ComposableEngine passes its `_brokerHandler` when creating TickerGroups.

- [ ] **Step 5: Wire TickerGroup force-exits to BrokerEventHandler**

In `ProcessBarAsync` and `ProcessTickAsync`, when `strategy.IsActive` + cutoff/session-disabled triggers `strategy.ForceExit()`, also call BrokerEventHandler:

```csharp
// Replace in ProcessBarAsync (lines 243-247):
if (strategy.IsActive)
{
    strategy.ForceExit(px, bar.Time, ExitReason.SessionEnd);
    if (_brokerHandler != null)
        _ = _brokerHandler.ExitGroupAsync(strategy.SetupId);
}
```

Same pattern for tick path (lines 332-334, 338-341).

#### ComposableEngine changes:

- [ ] **Step 6: Simplify RouteSignalsAsync** (lines 162-239)

Strip to entry-only. **Critical:** When `_brokerHandler` is active, route entries through `BrokerEventHandler.PlaceEntryAsync()` which calls `IGroupOrderExecutor.OnEntrySignalAsync()` to create GroupOrders with stop/tg1/tg2 legs:

```csharp
public async Task RouteSignalsAsync(List<StrategySignals> signals)
{
    foreach (var sig in signals)
    {
        if (sig.Entry is not { } esig) continue;

        var setup = sig.Strategy.SetupId;
        var label = sig.Strategy.Id;

        if (string.IsNullOrEmpty(esig.SetupLabel) && !string.IsNullOrEmpty(label))
            esig = esig with { SetupLabel = label };

        if (!Risk.CanTrade(_config.UseDailyLossLimit, _config.MaxDailyLoss))
            continue;

        // Route through BrokerEventHandler (WSS path) or legacy IOrderExecutor
        if (_brokerHandler != null)
            await _brokerHandler.PlaceEntryAsync(esig, sig.Strategy);
        else
            await _executor.OnEntrySignalAsync(esig);

        await _sink.OnEntryAsync(esig);

        bool isLong = esig.Direction == Direction.Long;
        AddAlert("ENTRY", setup,
            $"{(isLong ? "LONG" : "SHORT")} {esig.Contracts}ct @ {esig.Entry:F2} | Stop {esig.Stop:F2} | Tgt {esig.Target:F2}",
            isLong ? "green" : "red", label);
    }
}
```

- [ ] **Step 6b: Add PlaceEntryAsync to BrokerEventHandler**

Add a new method to `CRV.Core/Strategy/BrokerEventHandler.cs`:
```csharp
/// <summary>Place an entry order and register the group for event tracking.</summary>
public async Task PlaceEntryAsync(EntrySignal signal, ISetupStrategy strategy)
{
    var group = await _executor.OnEntrySignalAsync(signal);
    if (group != null)
        RegisterGroup(group, strategy);
}
```

This calls `IGroupOrderExecutor.OnEntrySignalAsync()` (implemented by `MockGroupOrderExecutor` or `TradovateExecutor`) which creates the `GroupOrder` with all legs, then registers it for event tracking.

- [ ] **Step 7: Delete BuildTradeRecord** (lines 691-742)

Delete the entire method. Trade records are now built by `BrokerEventHandler.CompleteGroup`.

- [ ] **Step 8: Simplify ForceExitSetupAsync** (lines 344-381)

Remove the `strategy.IsActive` / `strategy.GetActiveTrade` / `BuildTradeRecord` path:
```csharp
public async Task ForceExitSetupAsync(string setupId)
{
    if (!_strategies.TryGetValue(setupId, out var strategy)) return;

    // Delegate to BrokerEventHandler for actual order cancellation + trade completion
    if (_brokerHandler != null && _brokerHandler.HasActiveGroup(setupId))
        await _brokerHandler.ExitGroupAsync(setupId);

    // Reset strategy state
    strategy.ForceExit(_prices.GetLastPrice(strategy.Ticker), DateTime.UtcNow, ExitReason.Manual);

    AddAlert("EXIT", strategy.SetupId, $"Force exit", "orange");
    await PublishSnapshotInternal();
}
```

- [ ] **Step 9: Simplify ForceExitAllAsync** (lines 384-420)

```csharp
public async Task ForceExitAllAsync(DateTime? utcTime = null)
{
    // BrokerEventHandler cancels all broker orders + market-closes active positions
    if (_brokerHandler != null)
        await _brokerHandler.ExitAllAsync();

    // Reset all strategy state
    foreach (var (_, strategy) in _strategies)
        strategy.ResetSession();
}
```

- [ ] **Step 10: Rewrite GetActiveTrade** (lines 602-608)

Delegate to BrokerEventHandler using the shared helper (from Step 12):
```csharp
public ActiveTradeView? GetActiveTrade(string setupId)
{
    if (_brokerHandler == null) return null;
    var group = _brokerHandler.GetGroupState(setupId);
    return group != null ? GroupOrder.BuildTradeView(group) : null;
}
```

- [ ] **Step 11: Fix ResetWarmupCounters** (lines 264-282)

`strategy.IsActive` now returns `strategy.InTrade` (set by BrokerEventHandler). During warmup, BrokerEventHandler isn't active, so `InTrade` will be false. The existing logic should still work:
```csharp
// IsActive (=> InTrade) will be false during warmup since
// BrokerEventHandler doesn't process warmup entries.
// No code change needed — just verify.
```

#### SnapshotAggregator changes:

- [ ] **Step 12: Extract shared BuildTradeViewFromGroup helper**

Add a static helper on `GroupOrder` (or a separate static class) so both ComposableEngine.GetActiveTrade and SnapshotAggregator use the same logic:

```csharp
// Add to GroupOrder (CRV.Core/Models/GroupOrder.cs) or a static helper:
public static ActiveTradeView? BuildTradeView(GroupOrder group)
{
    if (group.EntryPrice == null) return null;

    var stopLeg = group.GetLeg(LegType.Stop);
    var tg1Leg = group.GetLeg(LegType.Tg1);
    var tg2Leg = group.GetLeg(LegType.Tg2);

    return new ActiveTradeView
    {
        Direction = group.Direction,
        Contracts = group.TotalContracts,
        Entry = group.EntryPrice.Value,
        CurrentStop = stopLeg?.Price ?? 0m,
        InitialStop = stopLeg?.Price ?? 0m,
        Target = tg2Leg?.Price ?? 0m,
        Partial = tg1Leg?.Price ?? 0m,
        PartialFilled = group.Status == GroupOrderStatus.PartialFilled,
        EnteredAt = group.CreatedAt,
    };
}
```

- [ ] **Step 13: Rewrite GetActiveTrade call in SnapshotAggregator** (~line 191)

Replace `strategy.GetActiveTrade(setupLastPrice)` with BrokerEventHandler lookup. SnapshotAggregator needs a reference to BrokerEventHandler (pass via the snapshot inputs object):

```csharp
// In the snapshot building loop, replace:
// var trade = strategy.GetActiveTrade(setupLastPrice);
// With:
ActiveTradeView? trade = null;
if (brokerHandler != null)
{
    var group = brokerHandler.GetGroupState(strategy.SetupId);
    if (group != null)
        trade = GroupOrder.BuildTradeView(group);
}
```

Use the same helper in `ComposableEngine.GetActiveTrade` (Step 10).

- [ ] **Step 14: Build check**

Run: `dotnet build`
Expected: Errors only in test files — fixed in Task 7.

- [ ] **Step 15: Commit**

```bash
git add -A && git commit -m "phase2: strip trade lifecycle from strategies and engine

Strategies are now pure signal generators (EntrySignal only).
TickerGroup/ComposableEngine delegate trade management to BrokerEventHandler.
BuildTradeRecord removed; trade records built by BrokerEventHandler.CompleteGroup."
```

---

### Task 7: Wire Backtest Through BrokerEventHandler

**Rationale:** The backtest previously relied on strategy-owned trade state for fills. Now it must use `MockGroupOrderExecutor` + `MockEventStream` + `BrokerEventHandler` — the same pipeline as live mock trading.

**Key classes:**
- `MockGroupOrderExecutor : IGroupOrderExecutor` (creates GroupOrders with legs, pushes events to MockEventStream)
- `MockEventStream : IBrokerEventStream` (channel-based, fires `OnOrderUpdate`)
- `BrokerEventHandler` (subscribes to `OnOrderUpdate`, manages trade lifecycle)

**Files:**
- Modify: `CRV.Backtest/Engine/BacktestEngine.cs`

- [ ] **Step 1: Understand the backtest flow after Phase 2**

New flow:
1. `BacktestEngine` creates `MockGroupOrderExecutor` + `MockEventStream` + `BrokerEventHandler` + `ComposableEngine`
2. Strategy emits `EntrySignal` → `ComposableEngine.RouteSignalsAsync` → `BrokerEventHandler.PlaceEntryAsync`
3. `BrokerEventHandler` calls `MockGroupOrderExecutor.OnEntrySignalAsync` → creates GroupOrder with entry/stop/tg1/tg2 legs
4. Entry is auto-filled (Market order) via `MockEventStream.PushEvent` → `BrokerEventHandler.HandleEventAsync`
5. On each OHLC tick, `MockGroupOrderExecutor.EvaluateFills(price, time)` checks stop/target levels
6. When a level is hit, pushes `OrderEvent` to `MockEventStream` → `BrokerEventHandler.HandleEventAsync` processes fill
7. `BrokerEventHandler.OnTradeCompleted` fires → `BacktestSink.OnExitAsync(trade)` collects trade

- [ ] **Step 2: Wire MockGroupOrderExecutor + BrokerEventHandler into BacktestEngine.RunAsync**

ComposableEngine's constructor already takes optional `BrokerEventHandler` (line 53). Update BacktestEngine:

```csharp
public async Task<BacktestResult> RunAsync(...)
{
    var trades = new List<TradeRecord>();
    var sink = new BacktestSink(trades);
    var prices = new InMemoryPriceProvider();

    // WSS-style fill simulation for backtest
    var mockStream = new MockEventStream();
    var groupExec = new MockGroupOrderExecutor(mockStream, NullLogger<MockGroupOrderExecutor>.Instance);
    var handler = new BrokerEventHandler(groupExec, NullLogger.Instance);

    // Subscribe MockEventStream → BrokerEventHandler
    mockStream.OnOrderUpdate += async evt => await handler.HandleEventAsync(evt);

    // Capture completed trades
    handler.OnTradeCompleted += (group, trade) =>
    {
        trade.Commission = trade.Contracts * 2 * _cfg.CommissionPerSide;
        trade.NetPnl = trade.GrossPnl - trade.Commission;
        sink.OnExitAsync(trade);
    };

    // BacktestExecutor is no longer needed — entries route through BrokerEventHandler.PlaceEntryAsync
    // But we still need a no-op IOrderExecutor for the legacy path (ComposableEngine constructor requires it)
    var noopExecutor = new NoopExecutor();

    var engineConfig = _cfg.ToEngineConfig();
    var engine = new ComposableEngine(noopExecutor, sink, prices, engineConfig, handler);

    // ... rest of setup (AddSetup, EnableTickMode, etc.) ...
```

Add a minimal `NoopExecutor`:
```csharp
internal class NoopExecutor : IOrderExecutor
{
    public Task<decimal?> OnEntrySignalAsync(EntrySignal sig) => Task.FromResult<decimal?>(null);
}
```

Note: `NullLogger<T>` comes from `Microsoft.Extensions.Logging.Abstractions` — add the package to `CRV.Backtest.csproj` if not already referenced.

- [ ] **Step 3: Add EvaluateFills + event drain after each tick in EmitBucket**

In the `EmitBucket` method, after each `engine.ProcessPriceTickAsync` call, evaluate fills. Since `MockEventStream` uses a `Channel` and the `OnOrderUpdate` event fires asynchronously, we need to ensure events are processed synchronously in the backtest:

**Option A (simple — use synchronous event handler):** The `mockStream.OnOrderUpdate` handler already calls `handler.HandleEventAsync` which is awaited. Since backtest is single-threaded, the event fires synchronously from `PushEvent` through the channel consumer. Verify this works.

**Option B (explicit drain):** If Option A has timing issues, make `MockEventStream` expose a synchronous `PushEventSync` method that directly invokes `OnOrderUpdate` without the channel:
```csharp
// Add to MockEventStream:
public void PushEventSync(OrderEvent evt) => OnOrderUpdate?.Invoke(evt);
```
And have `MockGroupOrderExecutor.EvaluateFills` use `PushEventSync` in a backtest-mode flag.

After each tick in EmitBucket:
```csharp
prices.UpdatePrice(ticker, price);
await engine.ProcessPriceTickAsync(price, t, ticker);
groupExec.EvaluateFills(price, t);  // Check stop/target fills → fires events → BrokerEventHandler processes
```

Note: `groupExec` and `handler` need to be accessible from `EmitBucket`. Either pass them as parameters or store as fields in `BacktestEngine`.

- [ ] **Step 4: Connect MockEventStream before starting**

`MockEventStream.ConnectAsync` starts the consumer loop. Call it before the bar loop:
```csharp
await mockStream.ConnectAsync(ct);
```

And disconnect after:
```csharp
await mockStream.DisconnectAsync();
```

- [ ] **Step 5: Verify MockGroupOrderExecutor handles Market entries correctly**

`MockGroupOrderExecutor.OnEntrySignalAsync` creates a GroupOrder and pushes an entry fill event to `MockEventStream`. Verify:
1. Market entries push immediate `OrderEvent(LegType.Entry, Status.Filled, fillPrice)`
2. Stop/tg1/tg2 legs are created as WORKING
3. `EvaluateFills` checks WORKING legs against price

Check `MockGroupOrderExecutor.OnEntrySignalAsync` (line 456+) — it should already do this for the live mock path.

- [ ] **Step 6: Build and run backtest tests**

Run: `dotnet build && dotnet test --filter "Backtest"`

Compare trade results with a known baseline. Key things to verify:
- Same number of trades
- Entry/exit prices match (may differ slightly due to fill simulation)
- P&L and R-multiples are consistent
- Commission calculation works

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "phase2: wire backtest through MockGroupOrderExecutor + BrokerEventHandler

Backtest uses same WSS-style fill simulation as live mock trading.
MockGroupOrderExecutor.EvaluateFills drives stop/target fills on OHLC ticks."
```

---

### Task 8: Update All Test Files

**Files:**
- Modify: `CRV.Core.Tests/Strategy/ComposableEngineTests.cs`
- Modify: `CRV.Core.Tests/Strategy/TickerGroupTests.cs`
- Modify: `CRV.Core.Tests/Strategy/TickEvalTests.cs`
- Modify: `CRV.Core.Tests/Strategy/FalseBreakoutIntegrationTests.cs`
- Modify: `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs`
- Modify: `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs`
- Modify: `CRV.Core.Tests/Strategy/PullbackStrategyTests.cs`
- Modify: `CRV.Core.Tests/Strategy/RetestStrategyTests.cs`
- Modify: `CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs`
- Modify: `CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs`
- Delete: Any OrbStrategyEngine test file

- [ ] **Step 1: Update FakeStrategy in ComposableEngineTests.cs**

Remove: `PendingExit`, `PendingPartial`, `PendingBE`, `PreExitTrade`, `ApplyFill`, `RevertEntryToTickGate`, `GetActiveTrade`.
Keep: `IsActive` (=> `InTrade`), `ForceExit` (simplified), `RevertEntry`, `PendingEntry`, `ClearPendingSignals`, `SetInTrade`, `InTrade`.

Update FakeExecutor: remove `OnPartialSignalAsync`, `OnBESignalAsync`, `OnExitSignalAsync`.
Update FakeSink: remove `OnPartialAsync`, `OnBEMoveAsync`; change `OnExitAsync` to `Task OnExitAsync(TradeRecord t)`.

Remove tests that assert on exit/partial/BE signal routing through `RouteSignalsAsync`.

- [ ] **Step 2: Update FakeStrategy in TickerGroupTests.cs**

Same member removals as Step 1. Update `CollectAndClearSignals` assertions to check only `(Strategy, Entry)`.

- [ ] **Step 3: Update TickEvalTests.cs**

Update `NullExecutor`: remove `OnPartialSignalAsync`, `OnBESignalAsync`, `OnExitSignalAsync`.
Update `TestSink`: remove `OnPartialAsync`, `OnBEMoveAsync`; change `OnExitAsync` signature.

- [ ] **Step 4: Update FalseBreakoutIntegrationTests.cs**

Same updates as Step 3.

- [ ] **Step 5: Update BrokerEventHandlerTests.cs**

If fake strategies reference removed members, update them. `BrokerEventHandler` itself is unchanged.

- [ ] **Step 6: Update SnapshotAggregatorTests.cs**

If fake strategies reference `GetActiveTrade`, replace with mock that returns null (SnapshotAggregator now uses BrokerEventHandler).

- [ ] **Step 7: Update strategy-specific test files**

For each of PullbackStrategyTests, RetestStrategyTests, OrbFakeoutStrategyTests, SessionFakeoutStrategyTests:
- **Remove** tests that exercise: `ApplyFill`, `GetActiveTrade`, `BookExit`, exit-on-tick, partial-on-tick, BE-on-tick, `RevertEntryToTickGate`
- **Keep** tests that exercise: arm conditions, entry signal emission, disarm conditions, session reset, state machine transitions, `RevertEntry`

- [ ] **Step 8: Full test run**

Run: `dotnet test --verbosity normal`
Expected: All tests pass.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "phase2: update all test files for simplified strategy interface

Remove trade lifecycle test assertions. Update fake strategies,
executors, and sinks to match simplified interfaces."
```

---

### Task 9: Rename EntrySignal Fields and Remove Bridge Extensions

**Files:**
- Modify: `CRV.Core/Models/Signals.cs`
- Modify: All files referencing `EntrySignal.Target`, `.Partial`, `.Contracts`

- [ ] **Step 1: Rename fields in EntrySignal record**

```csharp
public record EntrySignal(
    SetupId   Setup,
    Direction Direction,
    decimal   Entry,
    decimal   Stop,
    decimal   Tg2Price,           // was: Target
    decimal   Tg1Price,           // was: Partial
    int       TotalContracts,     // was: Contracts
    DateTime  Time,
    string    OrderType  = "Market",
    string    SessionId  = "NY",
    string    Ticker     = "",
    string    SetupLabel = "",
    int       PartialContracts = 0);
```

- [ ] **Step 2: Delete bridge extension methods**

Delete the entire `EntrySignalExtensions` static class (lines 24-34).

- [ ] **Step 3: Find-and-replace all call sites**

Systematically update:
- `sig.Target` → `sig.Tg2Price` (named constructor args: `Target:` → `Tg2Price:`)
- `sig.Partial` → `sig.Tg1Price` (named constructor args: `Partial:` → `Tg1Price:`)
- `sig.Contracts` → `sig.TotalContracts` (named constructor args: `Contracts:` → `TotalContracts:`)
- `.Tg1Price()` method call → `.Tg1Price` property
- `.Tg2Price()` method call → `.Tg2Price` property
- `.TotalContracts()` method call → `.TotalContracts` property
- `.EffectivePartialContracts()` → inline: `s.PartialContracts > 0 ? s.PartialContracts : s.TotalContracts / 2`

Key files: all 4 strategies, MockBrokerExecutor, TradovateExecutor, SchwabExecutor, TradeStationExecutor, ComposableEngine alert, BacktestEngine, all test files.

- [ ] **Step 4: Build and test**

Run: `dotnet build && dotnet test`
Expected: 0 errors, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "phase2: rename EntrySignal fields Target→Tg2Price, Partial→Tg1Price, Contracts→TotalContracts

Remove bridge extension methods. All call sites updated."
```

---

### Task 10: Final Cleanup and Verification

- [ ] **Step 1: Search for dead code**

```bash
rg "ExitSignal|PartialSignal|BESignal|BuildTradeRecord|ApplyFill|RevertEntryToTickGate|PendingExit|PendingPartial|PendingBE|PreExitTrade|OnPartialSignalAsync|OnBESignalAsync|OnExitSignalAsync" --type cs .
```

Remove any remaining references (comments mentioning old types are OK to keep).

- [ ] **Step 2: Full build and test**

```bash
dotnet build
dotnet test --verbosity normal
```

- [ ] **Step 3: Line count verification**

```bash
# Should show significant reduction
git diff --stat HEAD~5..HEAD  # across all phase2 commits
```

Expected: ~1,400+ lines removed from strategies, ~200 from ComposableEngine, ~1,300 from OrbStrategyEngine = ~3,000+ lines net removal.

- [ ] **Step 4: Manual smoke test**

1. Start the app
2. Dashboard shows Setup A + B with live signals
3. Mock broker: trigger a trade, verify fill events + P&L display
4. Orders > Group Orders tab shows legs with correct status
5. Backtest: run a backtest, verify trade results
6. Force-exit from dashboard works (button triggers BrokerEventHandler)

- [ ] **Step 5: Final commit**

```bash
git add -A && git commit -m "phase2: final cleanup — remove dead references"
```

---

## Verification Checklist

- [ ] `dotnet build` — 0 errors
- [ ] `dotnet test` — all tests pass
- [ ] Strategies only emit `EntrySignal` (no Exit/Partial/BE)
- [ ] `StrategySignals` has only `(Strategy, Entry)` fields
- [ ] `ISetupStrategy` has no: `PendingExit`, `PendingPartial`, `PendingBE`, `PreExitTrade`, `ApplyFill`, `RevertEntryToTickGate`, `GetActiveTrade`
- [ ] `ISetupStrategy` still has: `IsActive` (=> InTrade), `InTrade`, `SetInTrade`, `ForceExit` (simplified), `RevertEntry`, `PendingEntry`
- [ ] `IStrategyEventSink` has only: `OnEntryAsync`, `OnExitAsync(TradeRecord)`, `OnSnapshotAsync`
- [ ] `IOrderExecutor` has only: `OnEntrySignalAsync`, `OnLevelsAdjustedAsync` (default impl)
- [ ] `ExitSignal`, `PartialSignal`, `BESignal` records deleted
- [ ] `OrbStrategyEngine` deleted
- [ ] `EntrySignal` fields renamed: `Tg2Price`, `Tg1Price`, `TotalContracts`
- [ ] Bridge extension methods deleted
- [ ] Backtest produces trade results through BrokerEventHandler + MockBrokerExecutor
- [ ] TickerGroup force-exits delegate to BrokerEventHandler
- [ ] SnapshotAggregator uses BrokerEventHandler for ActiveTradeView
- [ ] All 7 IStrategyEventSink implementations updated
- [ ] All test fake strategies/executors/sinks updated
- [ ] ~3,000+ lines net removal
