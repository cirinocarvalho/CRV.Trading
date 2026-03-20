# Composable Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Incrementally refactor the monolithic OrbStrategyEngine into a composable architecture with pluggable strategy instances, per-ticker indicator groups, and a thin coordinator engine.

**Architecture:** Extract each setup's entry/exit logic into classes implementing `ISetupStrategy`. Group setups sharing the same instrument into `TickerGroup` instances with shared indicators/modules. `ComposableEngine` coordinates groups, routes signals to brokers, and manages cross-setup risk. DB schema unchanged — mapping at load time.

**Tech Stack:** C# / .NET 8, xUnit, ASP.NET Core Razor Pages, EF Core (SQLite), SignalR

**Spec:** `docs/superpowers/specs/2026-03-20-composable-engine-design.md`

---

## File Structure

### New Files (by stage)

**Stage 1:**
- `CRV.Core/Strategy/ISetupStrategy.cs` — interface + `OrbState`, `IndicatorState`, `ModuleState` readonly record structs + `SetupStateSnapshot` + `StrategyType` enum
- `CRV.Core/Strategy/PullbackStrategy.cs` — Setup A implementation (~250 lines)
- `CRV.Core.Tests/Strategy/PullbackStrategyTests.cs` — isolated unit tests

**Stage 2:**
- `CRV.Core/Strategy/RetestStrategy.cs` — Setup B (~250 lines)
- `CRV.Core/Strategy/OrbFakeoutStrategy.cs` — Setup C (~200 lines)
- `CRV.Core/Strategy/SessionFakeoutStrategy.cs` — Setup D (~200 lines)
- `CRV.Core.Tests/Strategy/RetestStrategyTests.cs`
- `CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs`
- `CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs`

**Stage 3:**
- `CRV.Core/Models/StrategySetupConfig.cs` — per-setup config class
- `CRV.Core/Models/EngineConfig.cs` — global config class
- `CRV.Core/Strategy/StrategyFactory.cs` — creates `ISetupStrategy` from `StrategyType`
- `CRV.Core.Tests/Models/ConfigMappingTests.cs`

**Stage 4:**
- `CRV.Core/Strategy/TickerGroup.cs` — per-instrument group with bar/tick loops
- `CRV.Core/Strategy/ComposableEngine.cs` — thin coordinator
- `CRV.Core/Strategy/RiskManager.cs` — daily PnL + loss limit
- `CRV.Core/Strategy/SnapshotAggregator.cs` — builds `EngineSnapshot`
- `CRV.Core.Tests/Strategy/TickerGroupTests.cs`
- `CRV.Core.Tests/Strategy/ComposableEngineTests.cs`
- `CRV.Core.Tests/Strategy/RiskManagerTests.cs`
- `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs`

**Stage 5:**
- No new files — modifies `LiveEngineOrchestrator.cs` and `BacktestEngine.cs`

**Stage 6:**
- `CRV.Web/Pages/Shared/_SetupCard.cshtml` — dashboard setup card partial
- `CRV.Web/Pages/Shared/_SetupConfigSection.cshtml` — settings setup config partial

### Modified Files
- `CRV.Core/Strategy/OrbStrategyEngine.cs` — hollowed out (Stages 1-2), eventually replaced (Stage 4-5)
- `CRV.Core/Models/StrategyConfig.cs` — add `ToEngineConfig()`, `ToSetupConfigs()` (Stage 3)
- `CRV.Core/Models/Signals.cs` — add `ExpectancyC`/`ExpectancyD` to `EngineSnapshot` (Stage 4)
- `CRV.Core/Models/SessionConfig.cs` — no structural changes, mapping stays as-is
- `CRV.Core/Modules/FalseBreakoutDetector.cs` — accept `ModuleConfig` (Stage 4)
- `CRV.Core.Tests/Strategy/FalseBreakoutIntegrationTests.cs` — update constructor (Stage 4)
- `CRV.Backtest/Engine/BacktestEngine.cs` — switch to `ComposableEngine` (Stage 5)
- `CRV.Web/Services/LiveEngineOrchestrator.cs` — create `ComposableEngine` (Stage 5)
- `CRV.Web/Api/EngineController.cs` — generic force-exit + ForceOrb (Stage 5)
- `CRV.Web/wwwroot/js/crv-hub.js` — C/D snapshot fields (Stage 5)
- `CRV.Web/Pages/Dashboard/Index.cshtml` — use partials (Stage 6)
- `CRV.Web/Pages/Settings/Live.cshtml` — use partials (Stage 6)

### Deleted Files
- `CRV.Core/Modules/CompositeSetupEngine.cs` (Stage 2) — note: may already be deleted on working branch
- `CRV.Core.Tests/Modules/CompositeSetupEngineTests.cs` (Stage 2) — note: may already be deleted on working branch

---

## Stage 1: Extract ISetupStrategy + PullbackStrategy

### Task 1: Create StrategySetupConfig

**Files:**
- Create: `CRV.Core/Models/StrategySetupConfig.cs`

> **Why first:** The interface in Task 2 references `StrategySetupConfig`, so the config class must exist first for the project to compile.

- [ ] **Step 1: Create the config class**

```csharp
// CRV.Core/Models/StrategySetupConfig.cs
using CRV.Core.Strategy;

namespace CRV.Core.Models;

/// <summary>
/// Per-setup instance configuration. Named to avoid collision with
/// existing SetupConfigBase/SetupConfigA/B/C/D hierarchy in SessionConfig.cs.
/// </summary>
public class StrategySetupConfig
{
    public string Name { get; set; } = "";               // "A", "B", "C", "D"
    public SetupId SetupId { get; set; }
    public StrategyType StrategyType { get; set; }
    public bool Enabled { get; set; }

    // Instrument (resolved effective values — fallback already applied)
    public string Ticker { get; set; } = "";
    public decimal PointValue { get; set; } = 20m;
    public decimal TickSize { get; set; } = 0.25m;

    // Sizing
    public int Contracts { get; set; } = 2;
    public decimal HiVolMult { get; set; } = 1.0m;
    public int MaxContracts { get; set; } = 2;

    // Entry
    public decimal StopPct { get; set; } = 0.10m;
    public int TargetPct { get; set; } = 100;
    public int PartialPct { get; set; } = 50;
    public decimal NearPct { get; set; } = 0.15m;
    public decimal MinRr { get; set; } = 1.5m;
    public string Mode { get; set; } = "Conservative";
    public decimal PullbackPct { get; set; } = 0.50m;    // A only
    public decimal RetestPct { get; set; } = 0.05m;      // B only
    public int EntryTickOffset { get; set; }
    public string OrderType { get; set; } = "Market";

    // Filters
    public bool UseVwap { get; set; } = true;
    public bool UseOrbClose { get; set; }
    public int CutoffHour { get; set; } = 14;
    public int CutoffMinute { get; set; } = 30;
    public bool CloseAtRthClose { get; set; } = true;
    public int MaxTrades { get; set; } = 5;
    public int MaxAdverseMinutes { get; set; }

    // Exit
    public bool UsePartial { get; set; } = true;
    public bool UseBe { get; set; } = true;
    public int PartialCts { get; set; }
    public bool AllowRearmAfterBe { get; set; } = true;

    // Derived
    public bool IsAggressive => Mode == "Aggressive";
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build CRV.Core/CRV.Core.csproj --nologo -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Models/StrategySetupConfig.cs
git commit -m "feat: add StrategySetupConfig per-setup config class"
```

### Task 2: Define ISetupStrategy interface and state snapshots

**Files:**
- Create: `CRV.Core/Strategy/ISetupStrategy.cs`

- [ ] **Step 1: Create the interface file with all types**

```csharp
// CRV.Core/Strategy/ISetupStrategy.cs
using CRV.Core.Models;

namespace CRV.Core.Strategy;

// ── Strategy type enum ──────────────────────────────────────────
public enum StrategyType { Pullback, Retest, OrbFakeout, SessionFakeout }

// ── Readonly state snapshots passed to strategies ───────────────
public readonly record struct OrbState(
    decimal High, decimal Low, decimal Mid, decimal Range,
    bool IsSet, bool BullClose, bool BearClose,
    decimal AtrRatio);

public readonly record struct IndicatorState(
    decimal Atr, decimal Vwap, decimal VwapUpper1, decimal VwapLower1,
    decimal VwapUpper2, decimal VwapLower2, decimal LastClose);

public readonly record struct ModuleState(
    // SessionEngine
    decimal SessionHigh, decimal SessionLow,
    decimal AsiaHigh, decimal AsiaLow, bool AsiaCompressed,
    decimal LondonHigh, decimal LondonLow,
    decimal PDH, decimal PDL, decimal PWH, decimal PWL,
    SessionType CurrentSession,
    bool LondonSweptAsiaHigh, bool LondonSweptAsiaLow,
    bool NYBullExpansion, bool NYBearExpansion,
    // SweepDetector
    IReadOnlyList<SweepEvent> ActiveSweeps,
    // VwapModel
    int VwapState,
    bool BullVwapReclaim, bool BearVwapReject,
    // OpeningDriveDetector
    bool IsBullDrive, bool IsBearDrive,
    // TrendDayFilter (directional pairs — mirrors BullScore/BearScore/TrendDayBull/TrendDayBear)
    int TrendDayBullScore, int TrendDayBearScore,
    bool TrendDayBull, bool TrendDayBear,
    // FalseBreakoutDetector
    bool OrbFakeoutBull, bool OrbFakeoutBear,
    decimal FakeoutPenetration);

// ── Per-setup snapshot for dashboard ────────────────────────────
public class SetupStateSnapshot
{
    public SetupId SetupId { get; set; }
    public string Name { get; set; } = "";
    public int State { get; set; }           // state machine value
    public bool IsActive { get; set; }
    public bool IsArmed { get; set; }
    public bool PastCutoff { get; set; }
    public int TradeCount { get; set; }
    public int MaxTrades { get; set; }
    public bool StickyTgt { get; set; }
    public bool StickyStp { get; set; }
    public decimal Expectancy { get; set; }  // avg PnL per trade
    public bool Enabled { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal WinPnl { get; set; }
    public decimal LossPnl { get; set; }
}

// ── Strategy interface ──────────────────────────────────────────
public interface ISetupStrategy
{
    SetupId SetupId { get; }
    StrategyType StrategyType { get; }
    string Name { get; }
    bool IsActive { get; }
    bool IsArmed { get; }

    /// <summary>Process a confirmed bar. May produce pending signals.</summary>
    void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules);

    /// <summary>Process a price tick. May produce pending signals.</summary>
    void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules);

    /// <summary>Reconfigure for new session or settings change.</summary>
    void Reconfigure(StrategySetupConfig config);

    /// <summary>Reset all state for new trading day/session.</summary>
    void Reset();

    // ── Pending signals (consumed by engine after OnBar/OnTick) ──
    EntrySignal? PendingEntry { get; }
    ExitSignal? PendingExit { get; }
    PartialSignal? PendingPartial { get; }
    BESignal? PendingBE { get; }

    /// <summary>Adjust levels after broker reports actual fill price.</summary>
    void ApplyFill(decimal actualFillPrice);

    /// <summary>Clear all pending signals after engine has processed them.</summary>
    void ClearPendingSignals();

    /// <summary>Request force exit of active trade.</summary>
    void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd);

    /// <summary>Snapshot for dashboard display.</summary>
    SetupStateSnapshot GetSnapshot();

    /// <summary>Active trade view with unrealized PnL, or null if no trade.</summary>
    ActiveTradeView? GetActiveTrade(decimal lastPrice);
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build CRV.Core/CRV.Core.csproj --nologo -v q`
Expected: Build succeeded, 0 errors

`StrategySetupConfig` now exists from Task 1, so this compiles cleanly.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Strategy/ISetupStrategy.cs
git commit -m "feat: define ISetupStrategy interface and state snapshot types"
```

### Task 3: Write PullbackStrategy failing tests

**Files:**
- Create: `CRV.Core.Tests/Strategy/PullbackStrategyTests.cs`

Key behaviors to test:
1. Arms long when bar near ORB high (with VWAP/OrbClose filters)
2. Arms short when bar near ORB low
3. Enters long on pullback (conservative mode)
4. Enters immediately (aggressive mode)
5. Produces exit signal on target hit
6. Produces exit signal on stop hit
7. Produces partial + BE signals
8. Respects trade count limit
9. Respects cutoff (pastCutoff suppresses arming)
10. Reset clears all state
11. ForceExit produces exit signal
12. Directional lock prevents re-arming same side

- [ ] **Step 1: Write test file with core test cases**

```csharp
// CRV.Core.Tests/Strategy/PullbackStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class PullbackStrategyTests
{
    private static StrategySetupConfig DefaultConfig() => new()
    {
        Name = "A", SetupId = SetupId.A,
        StrategyType = StrategyType.Pullback,
        Enabled = true,
        Ticker = "NQM26", PointValue = 20m, TickSize = 0.25m,
        Contracts = 2, MaxContracts = 4, HiVolMult = 1.0m,
        StopPct = 0.10m, TargetPct = 100, PartialPct = 50,
        NearPct = 0.15m, MinRr = 1.0m, Mode = "Conservative",
        PullbackPct = 0.50m, MaxTrades = 3,
        UsePartial = false, UseBe = false,
        UseVwap = false, UseOrbClose = false,
        CutoffHour = 14, CutoffMinute = 30,
    };

    private static OrbState MakeOrb(decimal high = 5200m, decimal low = 5180m) => new(
        High: high, Low: low, Mid: (high + low) / 2m, Range: high - low,
        IsSet: true, BullClose: true, BearClose: false, AtrRatio: 0.8m);

    private static IndicatorState MakeIndicators(decimal vwap = 5190m) => new(
        Atr: 25m, Vwap: vwap,
        VwapUpper1: vwap + 10, VwapLower1: vwap - 10,
        VwapUpper2: vwap + 20, VwapLower2: vwap - 20,
        LastClose: 5195m);

    private static ModuleState EmptyModules() => new(
        SessionHigh: 5210m, SessionLow: 5170m,
        AsiaHigh: 0, AsiaLow: 0, AsiaCompressed: false,
        LondonHigh: 0, LondonLow: 0,
        PDH: 5220m, PDL: 5160m, PWH: 5230m, PWL: 5150m,
        CurrentSession: SessionType.NYOpen,
        LondonSweptAsiaHigh: false, LondonSweptAsiaLow: false,
        NYBullExpansion: false, NYBearExpansion: false,
        ActiveSweeps: Array.Empty<SweepEvent>(),
        VwapState: 0, BullVwapReclaim: false, BearVwapReject: false,
        IsBullDrive: false, IsBearDrive: false,
        TrendDayScore: 0, IsTrendDay: false,
        OrbFakeoutBull: false, OrbFakeoutBear: false,
        FakeoutPenetration: 0m);

    private static Bar MakeBar(decimal open, decimal high, decimal low, decimal close,
        DateTime? time = null)
        => new(time ?? new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc),
               open, high, low, close, 100);

    // ── Arming ──────────────────────────────────────────────────

    [Fact]
    public void Arms_Long_WhenBarNearOrbHigh()
    {
        var strategy = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(5200m, 5180m); // range=20, nearDist=20*0.15=3
        // Bar high 5198 >= 5200-3=5197 → should arm
        var bar = MakeBar(5195m, 5198m, 5190m, 5196m);

        strategy.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.True(strategy.IsArmed);
        Assert.Null(strategy.PendingEntry); // armed, not entered
    }

    [Fact]
    public void Arms_Short_WhenBarNearOrbLow()
    {
        var strategy = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(5200m, 5180m);
        // Bar low 5182 <= 5180+3=5183 → should arm short
        var bar = MakeBar(5185m, 5188m, 5182m, 5184m);

        strategy.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.True(strategy.IsArmed);
    }

    [Fact]
    public void DoesNotArm_WhenOrbNotSet()
    {
        var strategy = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb() with { IsSet = false };
        var bar = MakeBar(5195m, 5200m, 5190m, 5196m);

        strategy.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.False(strategy.IsArmed);
    }

    // ── Conservative entry (pullback) ───────────────────────────

    [Fact]
    public void Enters_Long_OnPullback_Conservative()
    {
        var strategy = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(5200m, 5180m); // range=20, pbPts=20*0.50=10, longPb=5200-10=5190

        // Bar 1: arm long
        var bar1 = MakeBar(5195m, 5199m, 5193m, 5197m);
        strategy.OnBar(bar1, orb, MakeIndicators(), EmptyModules());
        Assert.True(strategy.IsArmed);
        strategy.ClearPendingSignals();

        // Bar 2: pullback — low 5189 <= 5190+0.50(tickTol)=5190.50 → enter
        var bar2 = MakeBar(5195m, 5196m, 5189m, 5191m,
            new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc));
        strategy.OnBar(bar2, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(strategy.PendingEntry);
        Assert.Equal(SetupId.A, strategy.PendingEntry!.Setup);
        Assert.Equal(Direction.Long, strategy.PendingEntry.Direction);
        Assert.True(strategy.IsActive);
    }

    // ── Aggressive entry ────────────────────────────────────────

    [Fact]
    public void Enters_Immediately_Aggressive()
    {
        var cfg = DefaultConfig();
        cfg.Mode = "Aggressive";
        var strategy = new PullbackStrategy(cfg);
        var orb = MakeOrb(5200m, 5180m);

        // Bar near ORB high → arm + immediate entry
        var bar = MakeBar(5195m, 5199m, 5193m, 5197m);
        strategy.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        // In aggressive mode, arming and entry happen on same bar
        Assert.NotNull(strategy.PendingEntry);
        Assert.True(strategy.IsActive);
    }

    // ── Exit on target ──────────────────────────────────────────

    [Fact]
    public void Exits_OnTarget()
    {
        var strategy = CreateWithActiveLong();

        // Bar hits target
        var bar = MakeBar(5195m, 5215m, 5190m, 5210m,
            new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc));
        strategy.OnBar(bar, MakeOrb(), MakeIndicators(), EmptyModules());

        Assert.NotNull(strategy.PendingExit);
        Assert.Equal(ExitReason.Target, strategy.PendingExit!.Reason);
        Assert.False(strategy.IsActive);
    }

    // ── Exit on stop ────────────────────────────────────────────

    [Fact]
    public void Exits_OnStop()
    {
        var strategy = CreateWithActiveLong();

        // Bar hits stop
        var bar = MakeBar(5195m, 5196m, 5170m, 5172m,
            new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc));
        strategy.OnBar(bar, MakeOrb(), MakeIndicators(), EmptyModules());

        Assert.NotNull(strategy.PendingExit);
        Assert.Equal(ExitReason.Stop, strategy.PendingExit!.Reason);
        Assert.False(strategy.IsActive);
    }

    // ── Tick-mode entry ─────────────────────────────────────────

    [Fact]
    public void OnTick_Enters_WhenArmed_AndPullbackHit()
    {
        var strategy = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(5200m, 5180m);

        // Arm via bar
        var bar = MakeBar(5195m, 5199m, 5193m, 5197m);
        strategy.OnBar(bar, orb, MakeIndicators(), EmptyModules());
        Assert.True(strategy.IsArmed);
        strategy.ClearPendingSignals();

        // Tick at pullback level: 5190 (orbHigh - range*0.50 = 5200-10)
        strategy.OnTick(5190m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
            orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(strategy.PendingEntry);
        Assert.True(strategy.IsActive);
    }

    // ── Tick-mode exit ──────────────────────────────────────────

    [Fact]
    public void OnTick_Exits_OnTarget()
    {
        var strategy = CreateWithActiveLong();
        strategy.ClearPendingSignals();

        // Tick at target level
        decimal target = strategy.PendingEntry?.Target ?? strategy.GetActiveTrade(5200m)!.Target;
        strategy.OnTick(target, new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc),
            MakeOrb(), MakeIndicators(), EmptyModules());

        Assert.NotNull(strategy.PendingExit);
        Assert.Equal(ExitReason.Target, strategy.PendingExit!.Reason);
    }

    // ── Trade count limit ───────────────────────────────────────

    [Fact]
    public void DoesNotArm_WhenMaxTradesReached()
    {
        var cfg = DefaultConfig();
        cfg.MaxTrades = 1;
        cfg.Mode = "Aggressive";
        var strategy = new PullbackStrategy(cfg);
        var orb = MakeOrb(5200m, 5180m);

        // Trade 1: arm + enter + exit
        var bar1 = MakeBar(5195m, 5199m, 5193m, 5197m);
        strategy.OnBar(bar1, orb, MakeIndicators(), EmptyModules());
        strategy.ClearPendingSignals();
        // Force exit to complete the trade
        strategy.ForceExit(5195m, DateTime.UtcNow);
        strategy.ClearPendingSignals();

        // Try to arm again — should not arm
        var bar2 = MakeBar(5195m, 5199m, 5193m, 5197m,
            new DateTime(2026, 3, 10, 14, 32, 0, DateTimeKind.Utc));
        strategy.OnBar(bar2, orb, MakeIndicators(), EmptyModules());

        Assert.False(strategy.IsArmed);
        Assert.Null(strategy.PendingEntry);
    }

    // ── Reset ───────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsAllState()
    {
        var strategy = CreateWithActiveLong();
        strategy.ClearPendingSignals();

        strategy.Reset();

        Assert.False(strategy.IsActive);
        Assert.False(strategy.IsArmed);
        Assert.Null(strategy.PendingEntry);
        Assert.Null(strategy.PendingExit);
        var snap = strategy.GetSnapshot();
        Assert.Equal(0, snap.State);
        Assert.Equal(0, snap.TradeCount);
    }

    // ── ForceExit ───────────────────────────────────────────────

    [Fact]
    public void ForceExit_ProducesExitSignal()
    {
        var strategy = CreateWithActiveLong();
        strategy.ClearPendingSignals();

        strategy.ForceExit(5195m, DateTime.UtcNow);

        Assert.NotNull(strategy.PendingExit);
        Assert.Equal(ExitReason.SessionEnd, strategy.PendingExit!.Reason);
        Assert.False(strategy.IsActive);
    }

    // ── VWAP filter ─────────────────────────────────────────────

    [Fact]
    public void DoesNotArmLong_WhenVwapFilterOn_AndBelowVwap()
    {
        var cfg = DefaultConfig();
        cfg.UseVwap = true;
        var strategy = new PullbackStrategy(cfg);
        var orb = MakeOrb(5200m, 5180m);
        // VWAP at 5210, close at 5196 → below VWAP → no long arm
        var bar = MakeBar(5195m, 5199m, 5193m, 5196m);

        strategy.OnBar(bar, orb, MakeIndicators(vwap: 5210m), EmptyModules());

        Assert.False(strategy.IsArmed);
    }

    // ── Partial + BE ────────────────────────────────────────────

    [Fact]
    public void Partial_And_BE_ProduceSignals()
    {
        var cfg = DefaultConfig();
        cfg.UsePartial = true;
        cfg.UseBe = true;
        cfg.PartialPct = 50; // partial at 50% of target
        var strategy = new PullbackStrategy(cfg);

        // Get into an active long position
        var orb = MakeOrb(5200m, 5180m);
        cfg.Mode = "Aggressive";
        strategy.Reconfigure(cfg);
        var bar1 = MakeBar(5195m, 5199m, 5193m, 5197m);
        strategy.OnBar(bar1, orb, MakeIndicators(), EmptyModules());
        strategy.ClearPendingSignals();

        // Verify we have an active trade to read the partial level
        Assert.True(strategy.IsActive);
        var trade = strategy.GetActiveTrade(5195m);
        Assert.NotNull(trade);

        // Bar hits partial but not target
        var bar2 = MakeBar(5195m, trade!.Partial + 1m, 5190m, trade.Partial,
            new DateTime(2026, 3, 10, 14, 32, 0, DateTimeKind.Utc));
        strategy.OnBar(bar2, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(strategy.PendingPartial);
        Assert.NotNull(strategy.PendingBE);
        Assert.True(strategy.IsActive); // still active after partial
    }

    // ── Snapshot ─────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ReturnsCorrectState()
    {
        var strategy = new PullbackStrategy(DefaultConfig());
        var snap = strategy.GetSnapshot();

        Assert.Equal(SetupId.A, snap.SetupId);
        Assert.Equal("A", snap.Name);
        Assert.False(snap.IsActive);
        Assert.Equal(0, snap.State);
        Assert.True(snap.Enabled);
    }

    // ── Helper: create strategy with active long position ───────
    private PullbackStrategy CreateWithActiveLong()
    {
        var cfg = DefaultConfig();
        cfg.Mode = "Aggressive";
        var strategy = new PullbackStrategy(cfg);
        var orb = MakeOrb(5200m, 5180m);

        var bar = MakeBar(5195m, 5199m, 5193m, 5197m);
        strategy.OnBar(bar, orb, MakeIndicators(), EmptyModules());
        Assert.True(strategy.IsActive);
        return strategy;
    }
}
```

- [ ] **Step 2: Verify tests fail (PullbackStrategy doesn't exist yet)**

Run: `dotnet build CRV.Core.Tests/CRV.Core.Tests.csproj --nologo -v q 2>&1 | head -10`
Expected: Build FAILS with "The type or namespace name 'PullbackStrategy' could not be found"

- [ ] **Step 3: Commit test file**

```bash
git add CRV.Core.Tests/Strategy/PullbackStrategyTests.cs
git commit -m "test: add PullbackStrategy unit tests (red — class not yet implemented)"
```

### Task 4: Implement PullbackStrategy

**Files:**
- Create: `CRV.Core/Strategy/PullbackStrategy.cs`

This class extracts all Setup A logic from `OrbStrategyEngine.cs`. The entry/exit logic is self-contained — the strategy produces pending signals that the engine consumes. It does NOT call `IOrderExecutor` or `IStrategyEventSink` directly.

Key differences from the monolith:
- No `_executor` or `_sink` — signals are returned via `PendingEntry`/`PendingExit`/etc.
- No `_todayPnl` — daily PnL aggregation moves to `RiskManager` (Stage 4)
- No `_enteredThisBar` — cross-setup coordination moves to `TickerGroup` (Stage 4)
- No `_orbAtrRatio` — received via `OrbState.AtrRatio`
- Uses `LevelCalculator` and `ExitProcessor` from `StrategyHelpers.cs` (shared, no change)

- [ ] **Step 1: Create PullbackStrategy implementation**

```csharp
// CRV.Core/Strategy/PullbackStrategy.cs
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Setup A — Pullback strategy. Arms near ORB edge, optionally waits for
/// pullback, enters, manages exit via stop/target/partial/BE.
/// Extracted from OrbStrategyEngine (TryEntryA, EvalTickSetupA, BookExitA, etc.).
/// </summary>
public class PullbackStrategy : ISetupStrategy
{
    private StrategySetupConfig _cfg;

    // ── State machine ───────────────────────────────────────────
    // 0=idle, 1=armed LONG, -1=armed SHORT, 2=active LONG, -2=active SHORT
    private int _state;

    // ── Trade state ─────────────────────────────────────────────
    private decimal _entry, _stop, _initStop, _target, _partial;
    private bool _active, _partialHit;
    private int _contracts;
    private decimal _pnl;          // accumulated PnL (includes partial)
    private DateTime _entryTime;

    // ── Arm state ───────────────────────────────────────────────
    private decimal _armEntry;
    private decimal _lastAtrRatio;  // cached from OrbState.AtrRatio on each OnBar
    private bool _bullTraded, _bearTraded;

    // ── Counters & flags ────────────────────────────────────────
    private int _tradeCount;
    private bool _pastCutoff;
    private int _wins, _losses;
    private decimal _winPnl, _lossPnl;

    // ── Sticky exit markers ─────────────────────────────────────
    private bool _stickyTgt, _stickyStp;
    private int _exitBarIdx = -1;
    private int _barIndex;

    // ── Pending signals ─────────────────────────────────────────
    public EntrySignal? PendingEntry { get; private set; }
    public ExitSignal? PendingExit { get; private set; }
    public PartialSignal? PendingPartial { get; private set; }
    public BESignal? PendingBE { get; private set; }

    // ── Interface properties ────────────────────────────────────
    public SetupId SetupId => _cfg.SetupId;
    public StrategyType StrategyType => StrategyType.Pullback;
    public string Name => _cfg.Name;
    public bool IsActive => _active;
    public bool IsArmed => !_active && (_state == 1 || _state == -1);

    public PullbackStrategy(StrategySetupConfig config)
    {
        _cfg = config;
    }

    public void Reconfigure(StrategySetupConfig config) => _cfg = config;

    // ── OnBar ───────────────────────────────────────────────────

    public void OnBar(Bar bar, OrbState orb, IndicatorState ind, ModuleState mod)
    {
        _barIndex++;
        _lastAtrRatio = orb.AtrRatio;  // cache for CalcContracts

        // Clear sticky exit markers from prior bar
        if (_exitBarIdx != -1 && _barIndex > _exitBarIdx)
        { _stickyTgt = false; _stickyStp = false; _exitBarIdx = -1; }

        if (!_cfg.Enabled || !orb.IsSet || orb.Range <= 0) return;

        // ── Entry logic (when not active) ───────────────────────
        if (!_active)
            ProcessArm(bar, orb, ind);

        // ── Exit logic (when active, bar-level safety net) ──────
        if (_active)
            ProcessBarExit(bar, orb);
    }

    // ── OnTick ──────────────────────────────────────────────────

    public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState ind, ModuleState mod)
    {
        if (!_cfg.Enabled || !orb.IsSet || orb.Range <= 0) return;
        if (_pastCutoff) return;

        decimal tickTol = _cfg.TickSize * 2;

        // ── Entry: armed but not active ─────────────────────────
        if (!_active && (_state == 1 || _state == -1))
        {
            bool isLong = _state == 1;
            if (_cfg.IsAggressive)
            {
                TryEntry(_armEntry, isLong, orb.Range, utc);
            }
            else
            {
                decimal pbPts   = orb.Range * _cfg.PullbackPct;
                decimal longPb  = LevelCalculator.RoundToTick(orb.High - pbPts, _cfg.TickSize);
                decimal shortPb = LevelCalculator.RoundToTick(orb.Low  + pbPts, _cfg.TickSize);

                if (isLong && price <= longPb + tickTol)
                    TryEntry(longPb, true, orb.Range, utc);
                else if (!isLong && price >= shortPb - tickTol)
                    TryEntry(shortPb, false, orb.Range, utc);
            }
            return;
        }

        // ── Exit: active position ───────────────────────────────
        if (!_active) return;
        bool long_ = _state == 2;
        bool hitStop   = long_ ? price <= _stop   : price >= _stop;
        bool hitTarget = long_ ? price >= _target  : price <= _target;
        bool hitPartPx = _cfg.UsePartial && !_partialHit &&
                         (long_ ? price >= _partial : price <= _partial);
        if (!hitStop && !hitTarget && !hitPartPx) return;

        // Partial fill
        bool partJustHit = false;
        if (hitPartPx && !hitTarget)
        {
            int half = CalcPartialCts();
            if (half > 0)
            {
                partJustHit = true;
                _partialHit = true;
                int remaining = _contracts - half;
                _pnl += (long_ ? _partial - _entry : _entry - _partial) * _cfg.PointValue * half;

                PendingPartial = new PartialSignal(SetupId, long_ ? Direction.Long : Direction.Short,
                    _partial, half, remaining, _entry, utc);

                if (_cfg.UseBe)
                {
                    _stop = _entry;
                    PendingBE = new BESignal(SetupId, long_ ? Direction.Long : Direction.Short,
                        _entry, _entry, remaining, utc);
                }
            }
            if (!hitTarget && !hitStop) return;
        }

        // Full exit
        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal exitPx = hitTarget ? _target : _stop;
        BookExit(reason, exitPx, utc, long_, partJustHit);
    }

    // ── ForceExit ───────────────────────────────────────────────

    public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd)
    {
        if (!_active) return;
        bool isLong = _state == 2;
        // Calculate remaining PnL
        int remCts = (_partialHit && _cfg.UsePartial)
            ? _contracts - CalcPartialCts() : _contracts;
        _pnl += (isLong ? currentPrice - _entry : _entry - currentPrice) * _cfg.PointValue * remCts;
        BookExit(reason, currentPrice, utcTime, isLong, sameBarPartial: false);
    }

    // ── ApplyFill ───────────────────────────────────────────────

    public void ApplyFill(decimal actualFillPrice)
    {
        if (!_active) return;
        decimal delta = actualFillPrice - _entry;
        _entry    = actualFillPrice;
        _stop    += delta;
        _initStop += delta;
        _target  += delta;
        _partial += delta;

        // Update pending entry signal with adjusted prices
        if (PendingEntry != null)
        {
            PendingEntry = PendingEntry with
            {
                Entry = _entry, Stop = _stop, Target = _target, Partial = _partial
            };
        }
    }

    public void ClearPendingSignals()
    {
        PendingEntry = null;
        PendingExit = null;
        PendingPartial = null;
        PendingBE = null;
    }

    // ── Reset ───────────────────────────────────────────────────

    public void Reset()
    {
        _state = 0; _entry = 0; _stop = 0; _initStop = 0;
        _target = 0; _partial = 0;
        _active = false; _tradeCount = 0; _partialHit = false;
        _pnl = 0; _armEntry = 0; _entryTime = DateTime.MinValue;
        _stickyTgt = false; _stickyStp = false; _exitBarIdx = -1;
        _bullTraded = false; _bearTraded = false; _contracts = 0;
        _pastCutoff = false; _barIndex = 0;
        ClearPendingSignals();
    }

    // ── Snapshot ─────────────────────────────────────────────────

    public SetupStateSnapshot GetSnapshot() => new()
    {
        SetupId = SetupId, Name = _cfg.Name,
        State = _state, IsActive = _active,
        IsArmed = IsArmed, PastCutoff = _pastCutoff,
        TradeCount = _tradeCount, MaxTrades = _cfg.MaxTrades,
        StickyTgt = _stickyTgt, StickyStp = _stickyStp,
        Enabled = _cfg.Enabled,
        Wins = _wins, Losses = _losses,
        WinPnl = _winPnl, LossPnl = _lossPnl,
        Expectancy = (_wins + _losses) > 0
            ? (_winPnl + _lossPnl) / (_wins + _losses) : 0m,
    };

    public ActiveTradeView? GetActiveTrade(decimal lastPrice)
    {
        if (!_active) return null;
        bool isLong = _state == 2;
        int remCts = (_partialHit && _cfg.UsePartial)
            ? _contracts - CalcPartialCts() : _contracts;
        decimal unrealized = (isLong ? lastPrice - _entry : _entry - lastPrice)
                           * _cfg.PointValue * remCts + _pnl;
        return new ActiveTradeView
        {
            Setup = SetupId, Direction = isLong ? Direction.Long : Direction.Short,
            Entry = _entry, CurrentStop = _stop, Target = _target, Partial = _partial,
            Contracts = _contracts, RemainingContracts = remCts,
            PartialFilled = _partialHit, LastPrice = lastPrice,
            UnrealizedPnl = unrealized, EnteredAt = _entryTime,
        };
    }

    // ── Private: arm & entry ────────────────────────────────────

    private void ProcessArm(Bar bar, OrbState orb, IndicatorState ind)
    {
        decimal orbHigh = orb.High, orbLow = orb.Low, orbRange = orb.Range;
        decimal tickTol = _cfg.TickSize * 2;

        // Disarm if price crossed to other side
        if ((_state == 1 && bar.Close < orbLow) || (_state == -1 && bar.Close > orbHigh))
        { _state = 0; _armEntry = 0; return; }

        bool isReady = _tradeCount < _cfg.MaxTrades && !_pastCutoff;

        // Arm
        if (isReady && _state == 0)
        {
            decimal nearDist = orbRange * _cfg.NearPct;

            // Clear directional lock when price leaves arm zone
            if (_bullTraded && bar.High < orbHigh - nearDist) _bullTraded = false;
            if (_bearTraded && bar.Low  > orbLow  + nearDist) _bearTraded = false;

            bool aboveVwap = !_cfg.UseVwap || bar.Close > ind.Vwap;
            bool belowVwap = !_cfg.UseVwap || bar.Close < ind.Vwap;
            bool orbLongOk = !_cfg.UseOrbClose || orb.BullClose;
            bool orbShortOk = !_cfg.UseOrbClose || orb.BearClose;

            if (bar.High >= orbHigh - nearDist && orbLongOk && aboveVwap && !_bullTraded)
            { _state = 1; _armEntry = bar.Open; }
            else if (bar.Low <= orbLow + nearDist && orbShortOk && belowVwap && !_bearTraded)
            { _state = -1; _armEntry = bar.Open; }
        }

        // Bar-level entry
        if (_state == 1 || _state == -1)
        {
            decimal pbPts   = orbRange * _cfg.PullbackPct;
            decimal longPb  = LevelCalculator.RoundToTick(orbHigh - pbPts, _cfg.TickSize);
            decimal shortPb = LevelCalculator.RoundToTick(orbLow  + pbPts, _cfg.TickSize);

            if (_cfg.IsAggressive)
            {
                if (_state == 1) TryEntry(_armEntry, true, orbRange, bar.Time);
                else if (_state == -1) TryEntry(_armEntry, false, orbRange, bar.Time);
            }
            else
            {
                if (_state == 1 && bar.Low <= longPb + tickTol)
                    TryEntry(longPb, true, orbRange, bar.Time);
                else if (_state == -1 && bar.High >= shortPb - tickTol)
                    TryEntry(shortPb, false, orbRange, bar.Time);
            }
        }
    }

    private void TryEntry(decimal ep, bool isLong, decimal orbRange, DateTime time)
    {
        // Apply tick offset
        if (_cfg.EntryTickOffset != 0 && _cfg.TickSize > 0)
        {
            decimal off = _cfg.EntryTickOffset * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + off : ep - off, _cfg.TickSize);
        }

        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPct, _cfg.TargetPct, _cfg.PartialPct, orbRange, _cfg.TickSize);
        if (rr < _cfg.MinRr) return;

        _entry = ep; _stop = sl; _initStop = sl;
        _target = tp; _partial = pp;
        _pnl = 0; _active = true; _partialHit = false;
        _contracts = CalcContracts(_lastAtrRatio);
        _state = isLong ? 2 : -2;
        _entryTime = time;

        PendingEntry = new EntrySignal(SetupId,
            isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _contracts, time,
            _cfg.OrderType, Ticker: _cfg.Ticker);
    }

    // ── Private: bar-level exit ─────────────────────────────────

    private void ProcessBarExit(Bar bar, OrbState orb)
    {
        bool isLong = _state == 2;
        bool prevPart = _partialHit;

        var result = ExitProcessor.ProcessBar(
            true, isLong, _entry, _stop, _target, _partial,
            _contracts, _pnl, _partialHit,
            _cfg.UsePartial, _cfg.UseBe, _cfg.PointValue,
            bar.High, bar.Low, _cfg.PartialCts);

        _pnl = result.NewPnl;
        _active = result.StillActive;
        _partialHit = result.PartialHit;
        _stop = result.NewStop;

        bool partJustHit = _partialHit && !prevPart;
        bool closingNow = result.HitTarget || result.HitStop;

        // Partial/BE signals (only when trade stays open)
        if (partJustHit && !closingNow)
        {
            int half = CalcPartialCts();
            int remaining = _contracts - half;
            if (half > 0)
            {
                PendingPartial = new PartialSignal(SetupId,
                    isLong ? Direction.Long : Direction.Short,
                    _partial, half, remaining, _entry, bar.Time);

                if (_cfg.UseBe)
                {
                    PendingBE = new BESignal(SetupId,
                        isLong ? Direction.Long : Direction.Short,
                        _entry, _entry, remaining, bar.Time);
                }
            }
        }

        if (closingNow)
        {
            var exitPx = result.HitTarget ? _target : _stop;
            bool isBE = _cfg.AllowRearmAfterBe && !result.HitTarget && exitPx == _entry;
            if (!isBE) { if (isLong) _bullTraded = true; else _bearTraded = true; }
            if (result.HitTarget) { _stickyTgt = true; _exitBarIdx = _barIndex; }
            else                  { _stickyStp = true; _exitBarIdx = _barIndex; }
            BookExit(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                exitPx, bar.Time, isLong, partJustHit);
        }
    }

    // ── Private: book exit ──────────────────────────────────────

    private void BookExit(ExitReason reason, decimal exitPx, DateTime time,
        bool isLong, bool sameBarPartial = false)
    {
        decimal risk = Math.Abs(_entry - _initStop) * _cfg.PointValue * _contracts;
        decimal rMult = risk > 0 ? _pnl / risk : 0;
        decimal comm = _contracts * 2 * 0; // commission handled by engine, not strategy
        decimal net = _pnl; // engine adds commission

        _tradeCount++;
        if (net > 0) { _wins++; _winPnl += net; }
        else { _losses++; _lossPnl += net; }

        int remCts = (_partialHit && _cfg.UsePartial && !sameBarPartial)
            ? _contracts - CalcPartialCts() : _contracts;

        PendingExit = new ExitSignal(SetupId, reason, exitPx, remCts, time, _cfg.Ticker);

        // Reset trade state
        _active = false; _state = 0; _partialHit = false; _pnl = 0;
    }

    // ── Private: helpers ────────────────────────────────────────

    private int CalcContracts(decimal orbAtrRatio)
    {
        // orbAtrRatio is the ORB range / ATR ratio from OrbState.AtrRatio
        // Callers must pass OrbState.AtrRatio, not orbRange
        bool isHighVol = orbAtrRatio >= 1.0m;
        int cts = isHighVol ? (int)Math.Round(_cfg.Contracts * _cfg.HiVolMult) : _cfg.Contracts;
        return Math.Min(cts, _cfg.MaxContracts);
    }

    // Overload using stored orb ratio (for tick mode where orb isn't re-passed)
    private int CalcContracts() => _contracts > 0 ? _contracts : _cfg.Contracts;

    private int CalcPartialCts()
    {
        if (_cfg.PartialCts > 0) return Math.Min(_cfg.PartialCts, _contracts - 1);
        return (int)Math.Floor(_contracts * 0.5);
    }
}
```

- [ ] **Step 2: Verify tests pass**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~PullbackStrategyTests" --nologo -v q`
Expected: All tests pass

- [ ] **Step 3: Verify all existing tests still pass**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --nologo -v q`
Expected: All 168+ tests pass

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Strategy/PullbackStrategy.cs
git commit -m "feat: implement PullbackStrategy — Setup A extracted behind ISetupStrategy"
```

### Task 5: Wire PullbackStrategy into OrbStrategyEngine

The monolith creates a `PullbackStrategy` internally and delegates Setup A calls to it. This is the key integration step — the engine becomes a thin wrapper for Setup A while keeping B/C/D inline.

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

**Important:** This task does NOT remove the old Setup A code yet. It adds the strategy instance and delegates to it. The old code stays commented out as a safety net. A later cleanup task removes it.

- [ ] **Step 1: Add PullbackStrategy field to OrbStrategyEngine**

In the field declarations section (around line 78), add:

```csharp
private PullbackStrategy _setupA;
```

- [ ] **Step 2: Initialize in constructor**

In the constructor (around line 217), after existing module initialization, add:

```csharp
_setupA = new PullbackStrategy(BuildSetupConfigA(cfg));
```

Add a private helper method to build the config:

```csharp
private StrategySetupConfig BuildSetupConfigA(StrategyConfig cfg) => new()
{
    Name = "A", SetupId = SetupId.A, StrategyType = StrategyType.Pullback,
    Enabled = cfg.EnableA,
    Ticker = cfg.EffectiveTickerA, PointValue = cfg.EffectivePointValueA,
    TickSize = cfg.EffectiveTickSizeA,
    Contracts = cfg.ContractsA, HiVolMult = cfg.HiVolMultA, MaxContracts = cfg.MaxContractsA,
    StopPct = cfg.StopPctA, TargetPct = cfg.TargetPctA, PartialPct = cfg.PartialPctA,
    NearPct = cfg.NearPctA, MinRr = cfg.MinRrA, Mode = cfg.ModeA,
    PullbackPct = cfg.PullbackPct, EntryTickOffset = cfg.EntryTickOffsetA,
    OrderType = cfg.OrderTypeA,
    UseVwap = cfg.UseVwapA, UseOrbClose = cfg.UseOrbCloseA,
    CutoffHour = cfg.CutoffHourA, CutoffMinute = cfg.CutoffMinuteA,
    CloseAtRthClose = cfg.CloseAtRthCloseA, MaxTrades = cfg.MaxTradesA,
    MaxAdverseMinutes = cfg.MaxAdverseMinutesA,
    UsePartial = cfg.UsePartialA, UseBe = cfg.UseBeA,
    PartialCts = cfg.PartialCtsA, AllowRearmAfterBe = cfg.AllowRearmAfterBeA,
};
```

- [ ] **Step 3: Update Reconfigure to update the strategy**

In `Reconfigure()` method, add after config swap:

```csharp
_setupA.Reconfigure(BuildSetupConfigA(_cfg));
```

And in the reset section of Reconfigure, replace `ResetSetupA()` with:

```csharp
_setupA.Reset();
```

- [ ] **Step 4: Verify all existing tests still pass**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --nologo -v q`
Expected: All tests pass (strategy is wired but not yet used for dispatch)

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: wire PullbackStrategy into OrbStrategyEngine (not yet dispatching)"
```

### Task 6: Delegate Setup A dispatch to PullbackStrategy

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

This is the big switch — replace inline Setup A calls with delegation to `_setupA`. The strategy produces pending signals; the engine consumes them and routes to broker/sink.

- [ ] **Step 1: Create a signal dispatch helper**

Add a private method to consume pending signals from any `ISetupStrategy`:

```csharp
private async Task DispatchSignals(ISetupStrategy strategy, DateTime time)
{
    if (strategy.PendingPartial is { } psig)
    {
        await _executor.OnPartialSignalAsync(psig);
        await _sink.OnPartialAsync(psig);
        AddAlert("PARTIAL", psig.Setup, $"Partial @ {psig.PartialPrice:F2}", "yellow");
    }
    if (strategy.PendingBE is { } besig)
    {
        await _executor.OnBESignalAsync(besig);
        await _sink.OnBEMoveAsync(besig);
        AddAlert("MOVE_BE", besig.Setup, $"Stop → BE {besig.NewStop:F2}", "yellow");
    }
    if (strategy.PendingEntry is { } esig)
    {
        _enteredThisBar = true;
        var fill = await _executor.OnEntrySignalAsync(esig);
        if (fill.HasValue && fill.Value != esig.Entry) strategy.ApplyFill(fill.Value);
        await _sink.OnEntryAsync(esig);
        AddAlert("ENTRY", esig.Setup,
            $"{esig.Direction} {esig.Contracts}ct @ {esig.Entry:F2} | Stop {esig.Stop:F2} | Tgt {esig.Target:F2}",
            esig.Direction == Direction.Long ? "green" : "red");
    }
    if (strategy.PendingExit is { } xsig)
    {
        // Get trade info BEFORE clearing signals (strategy resets on BookExit)
        var snap = strategy.GetSnapshot();
        bool isWin = xsig.Reason == ExitReason.Target;
        decimal comm = xsig.Contracts * _cfg.CommissionPerSide * 2;

        await _executor.OnExitSignalAsync(xsig);

        // Build TradeRecord — mirrors existing BookExitA/B pattern in the monolith
        var trade = new TradeRecord
        {
            Setup      = xsig.Setup,
            EnteredAt  = _entryTimeForSetup(xsig.Setup), // stored when entry signal dispatched
            ExitedAt   = xsig.Time,
            Entry      = _entryPriceForSetup(xsig.Setup),
            Exit       = xsig.ExitPrice,
            InitialStop = _initStopForSetup(xsig.Setup),
            Target     = _targetForSetup(xsig.Setup),
            Contracts  = xsig.Contracts,
            IsWin      = isWin,
            GrossPnl   = snap.WinPnl + snap.LossPnl, // net PnL from strategy (last trade)
            Commission = comm,
            NetPnl     = (snap.WinPnl + snap.LossPnl) - comm,
            ExitReason = xsig.Reason,
            Ticker     = xsig.Ticker,
            // RMultiple, PartialFilled, Duration computed by TradeRecord or caller
        };

        // Update daily stats
        _todayPnl += trade.NetPnl;
        if (_todayPnl > _todayPeak) _todayPeak = _todayPnl;
        var dd = _todayPeak - _todayPnl;
        if (dd > _todayMaxDD) _todayMaxDD = dd;
        _ddBreached = _cfg.UseDailyLossLimit && _todayPnl <= -_cfg.MaxDailyLoss;

        await _sink.OnExitAsync(trade, xsig);
        AddAlert("EXIT", xsig.Setup,
            $"{xsig.Reason} @ {xsig.ExitPrice:F2}",
            xsig.Reason == ExitReason.Target ? "green" : "red");
    }
    strategy.ClearPendingSignals();
}
```

Note: The `_entryTimeForSetup()`, `_entryPriceForSetup()`, `_initStopForSetup()`, `_targetForSetup()` helpers retrieve the entry data that was cached when the `PendingEntry` signal was dispatched (the engine stores these in a dictionary keyed by `SetupId` at entry time). The `TradeRecord.GrossPnl` calculation above is a sketch — the implementer must compute it from `(exitPx - entryPx) * pointValue * contracts` with direction sign, matching the existing `BookExitA` pattern exactly. For Stage 1, the safest approach is to keep the existing inline code paths and add a `_useStrategyA` flag to toggle between old and new. Once verified identical, remove the old code.

- [ ] **Step 2: Add toggle flag and conditional dispatch**

For safe rollout, add a flag:
```csharp
private bool _useStrategyA = false; // Toggle to true after verification
```

In ProcessBarInternalAsync, wrap the existing `ProcessSetupA` call:
```csharp
if (_cfg.EnableA && !_ddBreached && !_pastCutoffA)
{
    if (_useStrategyA)
    {
        var orbState = new OrbState(_orb.OrbHigh, _orb.OrbLow, _orb.OrbMid, _orb.OrbRange,
            _orb.IsSet, _orb.OrbBullClose, _orb.OrbBearClose, _orbAtrRatio);
        var indState = new IndicatorState(_atr.Value, _vwap.Value,
            _vwapModel.Upper1, _vwapModel.Lower1, _vwapModel.Upper2, _vwapModel.Lower2,
            _lastBarClose);
        var modState = BuildModuleState();
        _setupA.OnBar(bar, orbState, indState, modState);
        await DispatchSignals(_setupA, bar.Time);
    }
    else
    {
        await ProcessSetupA(bar, aboveVwapA, belowVwapA, orbLongOkA, orbShortOkA);
    }
}
```

Similarly wrap the tick eval in ProcessPriceTickAsync:
```csharp
if (_cfg.EnableA && !_pastCutoffA)
{
    if (_useStrategyA)
    {
        _setupA.OnTick(price, utcTime, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
        await DispatchSignals(_setupA, utcTime);
    }
    else
    {
        await EvalTickSetupA(price, utcTime);
    }
}
```

- [ ] **Step 3: Add BuildOrbState, BuildIndicatorState, BuildModuleState helpers**

```csharp
private OrbState BuildOrbState() => new(
    _orb.OrbHigh, _orb.OrbLow, _orb.OrbMid, _orb.OrbRange,
    _orb.IsSet, _orb.OrbBullClose, _orb.OrbBearClose, _orbAtrRatio);

private IndicatorState BuildIndicatorState() => new(
    _atr.Value, _vwap.Value,
    _vwapModel.Upper1, _vwapModel.Lower1,
    _vwapModel.Upper2, _vwapModel.Lower2,
    _lastBarClose);

private ModuleState BuildModuleState() => new(
    _sessionEngine.SessionHigh, _sessionEngine.SessionLow,
    _sessionEngine.AsiaHigh, _sessionEngine.AsiaLow, _sessionEngine.AsiaCompressed,
    _sessionEngine.LondonHigh, _sessionEngine.LondonLow,
    _sessionEngine.PDH, _sessionEngine.PDL, _sessionEngine.PWH, _sessionEngine.PWL,
    _sessionEngine.CurrentSession,
    _sessionEngine.LondonSweptAsiaHigh, _sessionEngine.LondonSweptAsiaLow,
    _sessionEngine.NYBullExpansion, _sessionEngine.NYBearExpansion,
    _sweepDetector.ActiveSweeps,
    _vwapModel.State, _vwapModel.BullReclaim, _vwapModel.BearReject,
    _openingDrive.IsBullDrive, _openingDrive.IsBearDrive,
    _trendDay.BullScore, _trendDay.BearScore, _trendDay.TrendDayBull, _trendDay.TrendDayBear,
    _falseBreakout.OrbFakeoutBull, _falseBreakout.OrbFakeoutBear,
    _falseBreakout.PenetrationDepth);
```

Note: The exact property names on modules need to be verified against the actual code — the implementer should check each module's public properties and adjust. The pattern is clear: snapshot all module state into readonly records.

- [ ] **Step 4: Verify all tests pass with flag OFF (default)**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --nologo -v q`
Expected: All tests pass (old code path still active)

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: add Setup A strategy dispatch with toggle flag (default: old path)"
```

- [ ] **Step 6: Set flag to true and run tests**

Change `_useStrategyA = true;`

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --nologo -v q`

If tests fail, debug the PullbackStrategy to match exact monolith behavior. The `DispatchSignals` method will need refinement for `TradeRecord` construction and daily stats. Iterate until all tests pass.

- [ ] **Step 7: Commit with flag on**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: enable PullbackStrategy dispatch for Setup A"
```

### Task 7: Clean up old Setup A inline code

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

- [ ] **Step 1: Remove old Setup A methods**

Delete these methods (they are now in `PullbackStrategy`):
- `ProcessSetupA`
- `TryEntryA`
- `TryEntryAFromTick`
- `EvalTickSetupA`
- `BookExitA`
- `ForceExitA`
- `ResetSetupA`

Remove old Setup A fields that are now in the strategy:
- `_stA`, `_entA`, `_stopA`, `_tgtA`, `_partialA`, `_initStopA`
- `_activeA`, `_tradeCountA`, `_partHitA`, `_pnlA`, `_armEntryA`
- `_entryTimeA`, `_stickyTgtA`, `_stickyStpA`, `_exitBarIdxA`
- `_bullTradedA`, `_bearTradedA`, `_ctsA`
- `_todayWinsA`, `_todayLossesA`, `_todayWinPnlA`, `_todayLossPnlA`
- `_forceExitA`, `_pastCutoffA`

Remove the `_useStrategyA` toggle flag.

Update `PublishSnapshot` to read from `_setupA.GetSnapshot()` and `_setupA.GetActiveTrade()` instead of inline fields.

Update `ForceExitAllAsync` to call `_setupA.ForceExit(...)` instead of inline.

- [ ] **Step 2: Verify all tests pass**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --nologo -v q`
Expected: All tests pass

- [ ] **Step 3: Verify build passes**

Run: `dotnet build --nologo -v q`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "refactor: remove inline Setup A code — fully delegated to PullbackStrategy"
```

---

## Stage 2: Extract RetestStrategy + OrbFakeoutStrategy + SessionFakeoutStrategy

### Task 8: Extract RetestStrategy (Setup B)

Follow the same pattern as Tasks 3-7 for Setup A, but for Setup B.

**Files:**
- Create: `CRV.Core/Strategy/RetestStrategy.cs`
- Create: `CRV.Core.Tests/Strategy/RetestStrategyTests.cs`
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

Key differences from PullbackStrategy:
- State machine has 6 states: 0, ±1 (armed), ±2 (retest), ±3 (active)
- Retest zone logic: armed → retest when price returns to ORB level
- De-arm if price closes through OrbMid
- Entry at `orbHigh` (long) or `orbLow` (short), not pullback level
- Uses `CalcLevelsB` instead of `CalcLevels` for stop calculation

- [ ] **Step 1: Write RetestStrategy tests (red)**
- [ ] **Step 2: Implement RetestStrategy**
- [ ] **Step 3: Run tests — all pass**
- [ ] **Step 4: Wire into engine with toggle, verify, enable, clean up**
- [ ] **Step 5: Commit**

### Task 9: Extract OrbFakeoutStrategy (Setup C)

**Files:**
- Create: `CRV.Core/Strategy/OrbFakeoutStrategy.cs`
- Create: `CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs`
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

Key differences:
- Reads `ModuleState.OrbFakeoutBull/Bear` and `FakeoutPenetration` from `FalseBreakoutDetector`
- Different arm/entry conditions based on fakeout detection
- Source methods: `ProcessSetupC`, `EvalTickSetupC`

- [ ] **Step 1: Write OrbFakeoutStrategy tests (red)**
- [ ] **Step 2: Implement OrbFakeoutStrategy**
- [ ] **Step 3: Run tests — all pass**
- [ ] **Step 4: Wire into engine, verify, enable, clean up**
- [ ] **Step 5: Commit**

### Task 10: Extract SessionFakeoutStrategy (Setup D)

**Files:**
- Create: `CRV.Core/Strategy/SessionFakeoutStrategy.cs`
- Create: `CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs`
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

Key differences:
- Uses session range (not ORB) for entry/exit levels
- Reads `ModuleState.IsBullDrive/IsBearDrive` from `OpeningDriveDetector`
- Source methods: `ProcessSetupD`, `EvalTickSetupD`

- [ ] **Step 1: Write SessionFakeoutStrategy tests (red)**
- [ ] **Step 2: Implement SessionFakeoutStrategy**
- [ ] **Step 3: Run tests — all pass**
- [ ] **Step 4: Wire into engine, verify, enable, clean up**
- [ ] **Step 5: Commit**

### Task 11: Delete CompositeSetupEngine

**Files:**
- Delete: `CRV.Core/Strategy/CompositeSetupEngine.cs`
- Delete: `CRV.Core.Tests/Modules/CompositeSetupEngineTests.cs`
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs` — remove `_compositeSetups` field and all references

- [ ] **Step 1: Remove references from engine**
- [ ] **Step 2: Delete files**
- [ ] **Step 3: Verify build and tests pass**
- [ ] **Step 4: Commit**

```bash
git commit -m "refactor: remove CompositeSetupEngine — replaced by individual strategy classes"
```

---

## Stage 3: Extract StrategySetupConfig + EngineConfig mapping

### Task 12: Create EngineConfig class

**Files:**
- Create: `CRV.Core/Models/EngineConfig.cs`

- [ ] **Step 1: Write EngineConfig with all global fields**

See spec for full field list. Include: SessionStartHour, OrbStart/End, RthStart/End, ExitMinutesBefore, Timezone, ExecutionTFMinutes, Ticker, PointValue, TickSize, UseDailyLossLimit, MaxDailyLoss, AtrFilterPct, CommissionPerSide, AllowBothSameBar, Broker, ExecBroker, AccountId, ExecAccountId, ReplayDate, ReplaySpeed, ModuleConfig Modules.

- [ ] **Step 2: Verify build**
- [ ] **Step 3: Commit**

### Task 13: Add config mapping methods

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs`
- Create: `CRV.Core.Tests/Models/ConfigMappingTests.cs`

- [ ] **Step 1: Write round-trip tests**

Test that `cfg.ToEngineConfig()` extracts global fields correctly.
Test that `cfg.ToSetupConfigs()` produces 4 `StrategySetupConfig` objects with correct values.
Test round-trip: `ToSetupConfigs()` values match what `BuildSetupConfigA()` produces.

- [ ] **Step 2: Implement `ToEngineConfig()` and `ToSetupConfigs()` on StrategyConfig**

`ToEngineConfig()` must populate `EngineConfig.Modules` from existing `StrategyConfig` module fields (SweepMinPenetration, DriveRangeAtrMult, TrendDayThreshold, FBMinPenetrationPts, etc.) — currently this mapping happens in the engine constructor; move it to the config mapping.
- [ ] **Step 3: Run tests**
- [ ] **Step 4: Commit**

### Task 14: Create StrategyFactory

**Files:**
- Create: `CRV.Core/Strategy/StrategyFactory.cs`

- [ ] **Step 1: Implement factory**

```csharp
public static class StrategyFactory
{
    public static ISetupStrategy Create(StrategySetupConfig config) => config.StrategyType switch
    {
        StrategyType.Pullback => new PullbackStrategy(config),
        StrategyType.Retest => new RetestStrategy(config),
        StrategyType.OrbFakeout => new OrbFakeoutStrategy(config),
        StrategyType.SessionFakeout => new SessionFakeoutStrategy(config),
        _ => throw new ArgumentException($"Unknown strategy type: {config.StrategyType}")
    };
}
```

- [ ] **Step 2: Update engine to use factory**
- [ ] **Step 3: Verify tests pass**
- [ ] **Step 4: Commit**

---

## Stage 4: Extract TickerGroup + ComposableEngine

### Task 15: Implement RiskManager

**Files:**
- Create: `CRV.Core/Strategy/RiskManager.cs`
- Create: `CRV.Core.Tests/Strategy/RiskManagerTests.cs`

- [ ] **Step 1: Write tests for daily PnL tracking, loss limit, breach detection**
- [ ] **Step 2: Implement RiskManager**

```csharp
public class RiskManager
{
    public decimal TodayPnl { get; private set; }
    public decimal TodayPeak { get; private set; }
    public decimal TodayMaxDD { get; private set; }
    public bool DdBreached { get; private set; }
    public int TodayWins { get; private set; }
    public int TodayLosses { get; private set; }
    public decimal TodayWinPnl { get; private set; }
    public decimal TodayLossPnl { get; private set; }

    public void RecordTrade(decimal netPnl) { ... }
    public bool CanTrade(bool useDailyLossLimit, decimal maxDailyLoss) { ... }
    public void ResetDay() { ... }
}
```

- [ ] **Step 3: Run tests**
- [ ] **Step 4: Commit**

### Task 16: Implement SnapshotAggregator

**Files:**
- Create: `CRV.Core/Strategy/SnapshotAggregator.cs`
- Create: `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs`

Note: The existing `EngineSnapshot` only has `ExpectancyA`/`ExpectancyB`. For C/D support, add `ExpectancyC`/`ExpectancyD` fields to `EngineSnapshot` in `Signals.cs` (these are just extra properties — backward compatible, default 0).

- [ ] **Step 1: Add ExpectancyC/ExpectancyD to EngineSnapshot**
- [ ] **Step 2: Write tests for snapshot aggregation** — verify each setup's snapshot maps to the correct EngineSnapshot fields, verify OrbState/IndicatorState/ModuleState mapping
- [ ] **Step 3: Implement aggregator that builds EngineSnapshot from strategy snapshots + indicator state**
- [ ] **Step 4: Run tests**
- [ ] **Step 5: Commit**

### Task 17: Implement TickerGroup

**Files:**
- Create: `CRV.Core/Strategy/TickerGroup.cs`
- Create: `CRV.Core.Tests/Strategy/TickerGroupTests.cs`

Key responsibilities:
- Owns bar feed, OrbCalculator, ATR, VWAP, all modules
- Bar loop: update indicators → update modules → dispatch to strategies → collect signals
- Tick loop: dispatch to strategies → collect signals
- `_enteredThisBar` cross-setup coordination
- ORB cache per group
- SemaphoreSlim for serialization

- [ ] **Step 1: Write tests for grouping logic and bar dispatch**
- [ ] **Step 2: Implement TickerGroup**
- [ ] **Step 3: Run tests**
- [ ] **Step 4: Commit**

### Task 18: Implement ComposableEngine

**Files:**
- Create: `CRV.Core/Strategy/ComposableEngine.cs`
- Create: `CRV.Core.Tests/Strategy/ComposableEngineTests.cs`

Public API must include:
- `AddSetup(StrategySetupConfig)` — creates strategy via StrategyFactory, assigns to TickerGroup
- `StartAsync()` — starts all TickerGroup loops
- `Reconfigure(EngineConfig, List<StrategySetupConfig>)` — session transition
- `ForceExitSetup(SetupId)` — delegates to strategy, needs `ILastPriceProvider` dependency for current price
- `ForceOrbAsync()` — delegates to appropriate TickerGroup's OrbCalculator (spec requirement)
- `EnableTickMode()` — propagates to all TickerGroups (spec requirement)
- `GetSnapshot()` — delegates to SnapshotAggregator
- `TickerGroupKey(string ticker)` — static helper mapping NQ/MNQ → same key, ES → own key

- [ ] **Step 1: Write tests for AddSetup, signal routing, risk management, EnableTickMode, ForceOrb**
- [ ] **Step 2: Implement ComposableEngine with TickerGroup management**

Constructor takes `IOrderExecutor`, `IStrategyEventSink`, `ILastPriceProvider`, `EngineConfig`.

- [ ] **Step 3: Run tests**
- [ ] **Step 4: Commit**

### Task 19: Refactor FalseBreakoutDetector to accept ModuleConfig

**Files:**
- Modify: `CRV.Core/Modules/FalseBreakoutDetector.cs`
- Modify: `CRV.Core.Tests/Strategy/FalseBreakoutIntegrationTests.cs` — update to construct with `ModuleConfig`

- [ ] **Step 1: Change constructor to accept ModuleConfig instead of StrategyConfig**
- [ ] **Step 2: Update OrbStrategyEngine to pass ModuleConfig**
- [ ] **Step 3: Update FalseBreakoutIntegrationTests to construct detector with ModuleConfig** (existing tests use `new StrategyConfig {...}` — these will break without this step)
- [ ] **Step 4: Verify all tests pass**
- [ ] **Step 5: Commit**

---

## Stage 5: LiveEngineOrchestrator + BacktestEngine integration

### Task 20: Update LiveEngineOrchestrator

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

- [ ] **Step 1: Replace OrbStrategyEngine creation with ComposableEngine**
- [ ] **Step 2: Move bar feed creation into TickerGroup**
- [ ] **Step 3: Update warmup flow per group**
- [ ] **Step 4: Update session transition to call ComposableEngine.Reconfigure()**
- [ ] **Step 5: Verify build and manual test with Mock broker**
- [ ] **Step 6: Commit**

### Task 21: Update EngineController and SignalR hub

**Files:**
- Modify: `CRV.Web/Api/EngineController.cs` — replace per-setup `ForceExitSetupA/B/C/D()` calls with generic `ForceExitSetup(SetupId)`
- Modify: `CRV.Web/wwwroot/js/crv-hub.js` — add handling for C/D snapshot fields if new fields added

- [ ] **Step 1: Update EngineController force-exit endpoints**

Replace individual `_orchestrator.ForceExitSetupA()` etc. with `_orchestrator.ForceExitSetup(SetupId.A)` pattern. The orchestrator delegates to `ComposableEngine.ForceExitSetup(SetupId)`.

- [ ] **Step 2: Update ForceOrb endpoint to call `ComposableEngine.ForceOrbAsync()`**
- [ ] **Step 3: Verify crv-hub.js handles any new snapshot fields (ExpectancyC/D)**
- [ ] **Step 4: Build and manual test**
- [ ] **Step 5: Commit**

### Task 22: Update BacktestEngine

**Files:**
- Modify: `CRV.Backtest/Engine/BacktestEngine.cs`

- [ ] **Step 1: Switch from OrbStrategyEngine to ComposableEngine**
- [ ] **Step 2: Run backtest on test dataset, verify identical trades**
- [ ] **Step 3: Commit**

---

## Stage 6: Dashboard + Settings templates

### Task 23: Extract dashboard setup card partial

**Files:**
- Create: `CRV.Web/Pages/Shared/_SetupCard.cshtml`
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml`

- [ ] **Step 1: Create partial with setup card markup**
- [ ] **Step 2: Replace inline A/B/C/D cards with partial calls**
- [ ] **Step 3: Manual verification — dashboard renders correctly**
- [ ] **Step 4: Commit**

### Task 24: Extract settings setup config partial

**Files:**
- Create: `CRV.Web/Pages/Shared/_SetupConfigSection.cshtml`
- Modify: `CRV.Web/Pages/Settings/Live.cshtml`

- [ ] **Step 1: Create partial with setup config section markup**
- [ ] **Step 2: Replace inline A/B/C/D form sections with partial calls**
- [ ] **Step 3: Manual verification — settings page renders and saves correctly**
- [ ] **Step 4: Commit**

---

## Verification Checklist

After all stages:
- [ ] `dotnet build` — 0 errors
- [ ] `dotnet test` — all tests pass
- [ ] Dashboard shows setup cards for A/B/C/D + Market Context
- [ ] Settings shows setup config for A/B/C/D in each session tab
- [ ] Mock broker run produces trades for enabled setups
- [ ] Backtest produces identical results to pre-refactor
- [ ] Per-setup instrument override still works (NQ on A, MNQ on B)
- [ ] Force exit buttons work for each setup
- [ ] Session transitions (Asia → London → NY) reconfigure correctly
- [ ] Daily loss limit halts all setups across all ticker groups
