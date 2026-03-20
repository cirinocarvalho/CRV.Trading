# Composable Engine Architecture

## Problem

`OrbStrategyEngine` is a 2,800+ line monolith with hardcoded Setup A/B/C/D fields (`_stA`, `_stB`, `_entA`, `_stopA`, etc.), one shared bar feed, one ORB calculator, and inline strategy logic for every setup type. Adding or modifying a strategy means editing hundreds of lines across entry, exit, tick, bar, snapshot, and reset code paths. Per-setup instrument override was added but the ORB/ATR/VWAP remain shared, limiting the architecture to same-price instruments.

## Goal

Refactor into a composable engine where:
- Setups are pluggable strategy instances behind `ISetupStrategy`
- Setups sharing the same instrument are grouped onto one thread with shared indicators
- Different instruments get independent feeds, indicators, and threads
- Adding a new strategy type means implementing one interface, not editing the engine
- The refactor is incremental — each stage produces a buildable, testable, deployable system

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Initial strategies | A (Pullback) + B (Retest) + C (ORB Fakeout) + D (Session Fakeout) + extensible | Active strategies plus clean interface for future types |
| Threading model | Smart pooling — group by ticker | Same-instrument setups share feed/thread; different instruments get isolation |
| Indicators per group | Shared — one ORB/ATR/VWAP per ticker group | ORB and indicators are properties of the instrument, not the strategy |
| Analytical modules | Per ticker group | PDH/PDL, sweeps, session levels are instrument-specific |
| Cross-setup risk | Global dollar-sum daily loss limit | One trading account, one capital pool |
| Config model | Split into EngineConfig (global) + SetupConfig (per instance) | Eliminates suffixed property explosion; each setup self-contained |
| DB migration | None — map at load time via existing SessionConfig layer | Keeps refactor focused on engine, not persistence |
| UI approach | Templated partials for A/B/C/D, not fully dynamic | DRY markup without dynamic UI complexity |
| Rollout | Incremental extraction from monolith | Each stage builds, tests, and runs live |
| Dropped | Setup F (VWAP Reversion), CompositeSetupEngine | F unused; CompositeSetupEngine only served composite signals for C/D/F |

## Architecture

### Core Abstractions

#### ISetupStrategy

The central interface. Each strategy type implements it. Strategies are stateful but self-contained — they own their own state machine, trade state, counts, and cutoff flags. No shared mutable state between strategies.

```csharp
public interface ISetupStrategy
{
    SetupId SetupId { get; }
    StrategyType StrategyType { get; }
    string Name { get; }              // "A", "B", "C", "D", or custom
    bool IsActive { get; }            // has open trade
    bool IsArmed { get; }             // waiting for entry trigger

    void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules);
    void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules);
    void Reconfigure(SetupConfig config);
    void Reset();                     // new session

    EntrySignal? PendingEntry { get; }   // consumed by engine after OnBar/OnTick
    ExitSignal? PendingExit { get; }     // consumed by engine after OnBar/OnTick
    PartialSignal? PendingPartial { get; }
    BESignal? PendingBE { get; }

    void ApplyFill(decimal actualFillPrice);       // adjust levels after broker fill
    void ClearPendingSignals();
    void ForceExit();                              // external exit request

    SetupStateSnapshot GetSnapshot();              // for dashboard
    ActiveTradeView? GetActiveTrade(decimal lastPrice); // unrealized PnL view
}
```

#### Readonly State Snapshots

Strategies receive readonly snapshots — they never touch indicators or modules directly.

```csharp
public readonly record struct OrbState(
    decimal High, decimal Low, decimal Mid, decimal Range,
    bool IsSet, bool BullClose, bool BearClose,
    decimal AtrRatio);

public readonly record struct IndicatorState(
    decimal Atr, decimal Vwap, decimal VwapUpper1, decimal VwapLower1,
    decimal VwapUpper2, decimal VwapLower2, decimal LastClose);

public readonly record struct ModuleState(
    // SessionEngine outputs
    decimal SessionHigh, decimal SessionLow,
    decimal AsiaHigh, decimal AsiaLow, bool AsiaCompressed,
    decimal LondonHigh, decimal LondonLow,
    decimal PDH, decimal PDL, decimal PWH, decimal PWL,
    SessionType CurrentSession,
    bool LondonSweptAsiaHigh, bool LondonSweptAsiaLow,
    bool NYBullExpansion, bool NYBearExpansion,
    // SweepDetector outputs
    IReadOnlyList<SweepEvent> ActiveSweeps,
    // VwapModel outputs
    int VwapState,
    bool BullVwapReclaim, bool BearVwapReject,
    // OpeningDriveDetector outputs
    bool IsBullDrive, bool IsBearDrive,
    // TrendDayFilter outputs
    int TrendDayScore, bool IsTrendDay,
    // FalseBreakoutDetector outputs
    bool OrbFakeoutBull, bool OrbFakeoutBear,
    decimal FakeoutPenetration);
```

#### Four Strategy Implementations

| Class | Setup | Current Source | Key Module Dependency |
|-------|-------|---------------|----------------------|
| `PullbackStrategy` | A | `TryEntryA`, `EvalTickSetupA`, `BookExitA` in OrbStrategyEngine | None (pure ORB logic) |
| `RetestStrategy` | B | `TryEntryB`, `EvalTickSetupB`, `BookExitB` | None (pure ORB logic) |
| `OrbFakeoutStrategy` | C | `ProcessSetupC`, `EvalTickExitCDF` | `FalseBreakoutDetector` via ModuleState |
| `SessionFakeoutStrategy` | D | `ProcessSetupD`, `EvalTickExitCDF` | `OpeningDriveDetector` via ModuleState |

Each strategy contains:
- State machine (`_state`: idle → armed → active)
- Trade state (`_entry`, `_stop`, `_target`, `_partial`, `_contracts`, `_partialHit`)
- Arm state (`_armEntry`, `_bullTraded`, `_bearTraded`)
- Cutoff flag (`_pastCutoff`)
- Trade count (`_tradeCount`)
- Per-setup daily stats (`_wins`, `_losses`, `_winPnl`, `_lossPnl`)
- Sticky exit markers (`_stickyTgt`, `_stickyStop`, `_exitBarIdx`)

### SetupConfig

Per-instance configuration. Replaces the suffixed properties (`StopPctA`, `StopPctB`, etc.).

```csharp
public class SetupConfig
{
    public string Name { get; set; } = "";               // "A", "B", "C", "D"
    public SetupId SetupId { get; set; }
    public StrategyType StrategyType { get; set; }
    public bool Enabled { get; set; }

    // Instrument (resolved effective values — fallback already applied)
    public string Ticker { get; set; } = "";
    public decimal PointValue { get; set; }
    public decimal TickSize { get; set; }

    // Sizing
    public int Contracts { get; set; } = 1;
    public decimal HiVolMult { get; set; } = 1.0m;
    public int MaxContracts { get; set; } = 10;

    // Entry
    public decimal StopPct { get; set; }
    public decimal TargetPct { get; set; }
    public decimal PartialPct { get; set; }
    public decimal NearPct { get; set; }
    public decimal MinRr { get; set; }
    public decimal PullbackPct { get; set; }     // A only
    public decimal RetestPct { get; set; }        // B only
    public bool IsAggressive { get; set; }
    public int EntryTickOffset { get; set; }
    public string OrderType { get; set; } = "Market";

    // Filters
    public bool UseVwap { get; set; }
    public bool UseOrbClose { get; set; }
    public int CutoffHour { get; set; }
    public int CutoffMinute { get; set; }
    public bool CloseAtRthClose { get; set; }
    public int MaxTrades { get; set; } = 3;
    public int MaxAdverseMinutes { get; set; }

    // Exit
    public bool UsePartial { get; set; }
    public bool UseBe { get; set; }
    public int PartialCts { get; set; }
    public bool AllowRearmAfterBe { get; set; }
}

public enum StrategyType { Pullback, Retest, OrbFakeout, SessionFakeout }
```

### EngineConfig

Global settings shared across all setups and ticker groups.

```csharp
public class EngineConfig
{
    // Session
    public int SessionStartHour { get; set; } = 18;
    public TimeOnly OrbStart { get; set; }
    public TimeOnly OrbEnd { get; set; }
    public TimeOnly RthStart { get; set; }
    public TimeOnly RthEnd { get; set; }
    public int ExitMinutesBefore { get; set; }
    public string Timezone { get; set; } = "America/New_York";

    // Risk
    public bool UseDailyLossLimit { get; set; }
    public decimal MaxDailyLoss { get; set; }
    public decimal AtrFilterPct { get; set; }
    public decimal CommissionPerSide { get; set; }

    // Broker
    public string Broker { get; set; } = "";
    public string ExecBroker { get; set; } = "";

    // Module parameters
    public ModuleConfig Modules { get; set; } = new();
}
```

### TickerGroup

Groups setups sharing the same instrument. Owns one bar feed, one set of indicators, one set of analytical modules, and one thread.

```
TickerGroup
├── Key: string (data feed ticker, e.g. "NQM26")
├── BarFeed: IBarFeed
├── Indicators
│   ├── OrbCalculator
│   ├── AtrIndicator(14)
│   └── VwapIndicator
├── Modules
│   ├── SessionEngine
│   ├── SweepDetector
│   ├── VwapModel
│   ├── OpeningDriveDetector
│   ├── TrendDayFilter
│   └── FalseBreakoutDetector
├── Setups: List<ISetupStrategy>
├── SemaphoreSlim(1, 1) — bar/tick serialization
├── TickChannel: Channel<(decimal, DateTime)>
└── Thread: Task (bar loop + tick consumer)
```

**Grouping logic:**
- Instruments with the same price data share a group (NQ/MNQ → same group, keyed by data feed ticker)
- `TickerGroupKey(ticker)` extracts the grouping key — initially maps known micro/mini pairs, extensible
- If two setups specify different tickers that resolve to the same group, the group uses the first ticker as the data feed source

**Bar loop (per group):**
```
foreach bar in feed.StreamAsync():
    await semaphore.WaitAsync()
    try:
        if warmup: update indicators only
        else:
            update OrbCalculator
            if unconfirmed: throttle (2s), skip strategy eval
            update ATR, VWAP (confirmed bars only)
            update all modules (SessionEngine, Sweep, etc.)
            build OrbState, IndicatorState, ModuleState snapshots
            foreach setup in Setups:
                setup.OnBar(bar, orbState, indicatorState, moduleState)
                if setup.PendingEntry: yield to engine
                if setup.PendingExit: yield to engine
            report state to SnapshotAggregator
    finally:
        semaphore.Release()
```

**Tick loop (per group):**
```
foreach (price, time) in tickChannel.ReadAllAsync():
    await semaphore.WaitAsync()
    try:
        foreach setup in Setups:
            setup.OnTick(price, time, orbState, indicatorState, moduleState)
            if pending signals: yield to engine
    finally:
        semaphore.Release()
```

### ComposableEngine

The top-level coordinator. Replaces `OrbStrategyEngine`. Thin — no strategy logic.

```
ComposableEngine
├── EngineConfig
├── TickerGroups: Dictionary<string, TickerGroup>
├── RiskManager
│   ├── TodayPnl: decimal (sum across all setups)
│   ├── TodayPeak / TodayMaxDD
│   ├── DdBreached: bool
│   └── CheckDailyLossLimit()
├── IOrderExecutor
├── IStrategyEventSink
├── SnapshotAggregator
│   └── builds one EngineSnapshot from all groups + setups
└── Alerts: CircularBuffer
```

**Public API:**
```csharp
public class ComposableEngine
{
    // Setup management
    void AddSetup(SetupConfig config);
    void RemoveSetup(SetupId id);

    // Lifecycle
    Task StartAsync(CancellationToken ct);
    Task StopAsync();

    // Configuration
    void Reconfigure(EngineConfig config, List<SetupConfig> setups);

    // External actions
    void ForceExitSetup(SetupId id);
    void ForceExitAll();
    void EnableTickMode();

    // State
    EngineSnapshot GetSnapshot();
    bool IsRunning { get; }
}
```

**Signal routing:**
When a `TickerGroup` detects a pending signal from a strategy:
1. `PendingEntry` → `RiskManager.CanTrade()` check → `IOrderExecutor.OnEntrySignalAsync()` → `strategy.ApplyFill(actualPrice)` → `IStrategyEventSink.OnEntryAsync()`
2. `PendingPartial` → `IOrderExecutor.OnPartialSignalAsync()` → `IStrategyEventSink.OnPartialAsync()`
3. `PendingBE` → `IOrderExecutor.OnBESignalAsync()` → `IStrategyEventSink.OnBEMoveAsync()`
4. `PendingExit` → PnL calculation using `setup.Config.PointValue` → `RiskManager.RecordTrade(pnl)` → `IOrderExecutor.OnExitSignalAsync()` → `IStrategyEventSink.OnExitAsync(signal, tradeRecord)` → `strategy.ClearPendingSignals()`

If `RiskManager.DdBreached`, all groups halt — no new entries, force-exit all active trades.

### Snapshot Aggregation

`SnapshotAggregator` builds one `EngineSnapshot` (same shape as today) from all ticker groups and setups, maintaining dashboard compatibility.

**Per-group contributions:**
- OrbHigh/Low/Mid/Range, OrbBullClose, OrbBearClose, OrbAtrRatio
- ATR, VWAP
- Session levels (PDH/PDL, Asia/London/NY highs-lows)
- Module outputs (sweeps, drive, trend day, fakeout state)

**Per-setup contributions:**
- SetupXState (state machine value)
- ActiveTradeView (entry, stop, target, unrealized PnL)
- TradeCountX, MaxTradesX
- ExpectancyX
- PastCutoffX
- StickyTgtX, StickyStpX

**Multi-ticker case:** When groups have different instruments, the snapshot uses the primary group's indicator values for the main display (OrbHigh, ATR, VWAP). Dashboard Market Context could show tabs per instrument in a future UI update.

### Config Mapping (No DB Migration)

The existing `SessionConfig` / `StrategyConfig` / `Configs` table stays unchanged. Mapping happens at load time:

```
DB (Configs table, flat suffixed columns)
  ↕ SessionConfig.ToLegacyConfig() / FromExistingConfig()
StrategyConfig (flat object with A/B/C/D suffixes)
  ↕ new mapping layer
EngineConfig + List<SetupConfig>
  → ComposableEngine
```

New mapping methods on `StrategyConfig`:
```csharp
public EngineConfig ToEngineConfig() { ... }
public List<SetupConfig> ToSetupConfigs() { ... }
```

These extract the global fields into `EngineConfig` and split the per-setup suffixed fields into individual `SetupConfig` objects. The `EffectiveTicker/PointValue/TickSize` resolution (custom vs global fallback) happens here.

### UI: Templated Partials

**Dashboard (`Index.cshtml`):**
- Extract setup card into `_SetupCard.cshtml` partial
- Render: `@await Html.PartialAsync("_SetupCard", snapshot.SetupA)` for each setup
- Market Context section rendered per ticker group (initially one group)

**Settings (`Live.cshtml`):**
- Extract setup config section into `_SetupConfigSection.cshtml` partial
- Each setup rendered from `SetupConfig` object
- Module parameters stay in "Market Context Modules" area (global)

**Hardcoded for A/B/C/D** — not fully dynamic. Adding Setup E means adding one more partial call.

## Incremental Extraction Plan

### Stage 1: Extract ISetupStrategy + PullbackStrategy
- Define `ISetupStrategy`, `OrbState`, `IndicatorState`, `ModuleState`, `SetupConfig`
- Extract Setup A fields and logic from `OrbStrategyEngine` into `PullbackStrategy`
- Engine creates a `PullbackStrategy` instance internally
- Engine delegates Setup A's `OnBar`/`OnTick` to the strategy instance
- Setups B, C, D remain inline in the engine
- **Tests:** existing tests pass + new unit tests for `PullbackStrategy` in isolation
- **Verification:** mock broker run produces identical trades to before

### Stage 2: Extract RetestStrategy + OrbFakeoutStrategy + SessionFakeoutStrategy
- Same extraction pattern for B, C, D
- Engine now has zero inline strategy logic — all four delegate to strategy instances
- The monolith is hollow: orchestration + indicators + modules, no entry/exit code
- **Tests:** all existing tests pass + new per-strategy unit tests

### Stage 3: Extract SetupConfig + EngineConfig
- Define `SetupConfig` and `EngineConfig` classes
- Add `ToEngineConfig()` and `ToSetupConfigs()` mapping methods to `StrategyConfig`
- Update `SessionConfig.ToLegacyConfig()` / `FromExistingConfig()` for round-trip
- Engine and strategies read from new config objects
- DB stays unchanged — mapping layer handles it
- **Tests:** config mapping round-trip tests

### Stage 4: Extract TickerGroup
- Move indicators + modules + bar/tick loop into `TickerGroup` class
- Engine becomes `ComposableEngine` — creates groups, routes signals, manages risk
- Single-ticker case works identically to today
- Multi-ticker support comes for free (multiple groups, multiple threads)
- **Tests:** ticker grouping logic tests + integration tests

### Stage 5: LiveEngineOrchestrator integration
- Orchestrator creates `ComposableEngine` instead of `OrbStrategyEngine`
- Bar feed creation moves into `TickerGroup`
- Thread-per-group replaces single bar loop
- Warmup flow adapts per group
- **Tests:** full live engine startup/shutdown tests

### Stage 6: Dashboard + Settings templates
- Extract `_SetupCard.cshtml` and `_SetupConfigSection.cshtml` partials
- Dashboard renders setup cards from snapshot data
- Settings renders setup config from `SetupConfig` list
- Market Context section stays full-width
- **Tests:** manual UI verification

## Supersedes

This design supersedes the plan in `jolly-dancing-reef.md` (Remove Setup C, D, F from Engine). Instead of deleting C and D, they are extracted into clean strategy classes. Setup F (VWAP Reversion) and `CompositeSetupEngine` are still dropped.

## Files Affected

### New Files
- `CRV.Core/Strategy/ISetupStrategy.cs` — interface + state snapshots
- `CRV.Core/Strategy/PullbackStrategy.cs` — Setup A
- `CRV.Core/Strategy/RetestStrategy.cs` — Setup B
- `CRV.Core/Strategy/OrbFakeoutStrategy.cs` — Setup C
- `CRV.Core/Strategy/SessionFakeoutStrategy.cs` — Setup D
- `CRV.Core/Strategy/ComposableEngine.cs` — orchestrator
- `CRV.Core/Strategy/TickerGroup.cs` — per-instrument group
- `CRV.Core/Strategy/RiskManager.cs` — daily PnL + loss limit
- `CRV.Core/Strategy/SnapshotAggregator.cs` — builds EngineSnapshot
- `CRV.Core/Models/SetupConfig.cs` — per-setup config
- `CRV.Core/Models/EngineConfig.cs` — global config
- `CRV.Core.Tests/Strategy/PullbackStrategyTests.cs`
- `CRV.Core.Tests/Strategy/RetestStrategyTests.cs`
- `CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs`
- `CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs`
- `CRV.Core.Tests/Strategy/ComposableEngineTests.cs`
- `CRV.Core.Tests/Strategy/TickerGroupTests.cs`
- `CRV.Web/Pages/Shared/_SetupCard.cshtml`
- `CRV.Web/Pages/Shared/_SetupConfigSection.cshtml`

### Modified Files
- `CRV.Core/Strategy/OrbStrategyEngine.cs` — hollowed out incrementally, eventually replaced
- `CRV.Core/Models/StrategyConfig.cs` — add `ToEngineConfig()`, `ToSetupConfigs()`
- `CRV.Core/Models/SessionConfig.cs` — mapping updates
- `CRV.Web/Services/LiveEngineOrchestrator.cs` — creates ComposableEngine
- `CRV.Web/Pages/Dashboard/Index.cshtml` — use partials
- `CRV.Web/Pages/Settings/Live.cshtml` — use partials

### Deleted Files
- `CRV.Core/Strategy/CompositeSetupEngine.cs` — replaced by individual strategies
- `CRV.Core.Tests/Modules/CompositeSetupEngineTests.cs`
