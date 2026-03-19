# False Breakout Setups (Setup C & D) — Design Spec

## Overview

Two new setups integrated into `OrbStrategyEngine` using the same field pattern, config structure, entry/exit flow, and dashboard layout as existing setups A and B.

- **Setup C — ORB False Breakout:** Price breaks the session ORB, fails to sustain, closes back inside within N bars. Entry on tick recross back into range.
- **Setup D — Session Range False Breakout:** Next session sweeps the prior session's full high/low, fails to sustain, reverses back inside. Entry on tick recross.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Architecture | Integrated into OrbStrategyEngine (not separate engine) | Reuses broker execution, partial/BE, dashboard, backtest — no code duplication |
| SetupId slots | Reuse `SetupId.C` and `SetupId.D` | Simpler; old DB records tagged C/D were from removed setups with no overlap |
| ORB source | Same `OrbCalculator` as A/B per session config | No separate ORB window tracking needed |
| Exit levels | Same `StopPct`/`TargetPct`/`PartialPct` config pattern as A/B | Consistent, configurable per session |

## 1. FalseBreakoutDetector Module

**File:** `CRV.Core/Modules/FalseBreakoutDetector.cs`

Stateful module implementing `IEngineModule`. Contains two internal trackers:

### 1.1 RangeBreakoutTracker (internal class)

Shared logic for both range sources. Each tracker holds:

| Field | Type | Purpose |
|-------|------|---------|
| `BreakoutDirection` | `Direction?` | None, Long (broke above), Short (broke below) |
| `BarsInBreakout` | `int` | Bars since breakout detected |
| `MaxBarsAllowed` | `int` | Computed from `MaxTimeOutsideMinutes / ExecutionTFMinutes` |
| `SweepHigh` | `decimal` | Highest price during breakout (stop reference for short fakeout) |
| `SweepLow` | `decimal` | Lowest price during breakout (stop reference for long fakeout) |
| `RejectionBarHigh` | `decimal` | The rejection candle's high |
| `RejectionBarLow` | `decimal` | The rejection candle's low |
| `IsActivated` | `bool` | True when rejection confirmed and all filters pass |
| `PenetrationDepth` | `decimal` | How far past range level, as fraction of range |

### 1.2 OnBar Flow

1. Check range is locked (ORB formed for OrbTracker; prior session ended for SessionRangeTracker)
2. If no active breakout and bar closes outside range → set `BreakoutDirection`, record sweep extreme, `BarsInBreakout = 1`
3. If breakout active → increment `BarsInBreakout`, update sweep extreme
4. If `BarsInBreakout > MaxBarsAllowed` → expire, reset tracker (breakout was legitimate)
5. If bar closes back inside range → apply quality filters:
   - Rejection candle body ≥ `MinRejectionBodyPct` of candle's total range
   - `PenetrationDepth` ≤ `MaxPenetrationPct`
   - VWAP on opposite side of breakout direction (from `VwapModel`)
   - `TrendDayFilter` BullScore/BearScore below `MaxTrendDayScore` (opposing direction)
6. All filters pass → `IsActivated = true`, record rejection bar data

### 1.3 OnTick Flow

- Update sweep extreme if price extends further during active breakout
- No activation on tick — only confirmed bar close

### 1.4 Range Sources

**OrbTracker:** Reference levels = `OrbHigh`/`OrbLow` from `OrbCalculator`. Locked when ORB is formed.

**SessionRangeTracker:** Reference levels from `SessionEngine`:
- During London session: `AsiaHigh`/`AsiaLow` (locked at Asia end)
- During NY session: `LondonHigh`/`LondonLow` (locked at London end)

### 1.5 Compound Signal

When both trackers activate in the same direction simultaneously:
```csharp
public bool IsCompoundFakeout =>
    OrbTracker.IsActivated && SessionRangeTracker.IsActivated &&
    OrbTracker.BreakoutDirection == SessionRangeTracker.BreakoutDirection;
```

### 1.6 Config Parameters (module-level on StrategyConfig)

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `FBMaxTimeOutsideMinutesOrb` | `int` | 15 | Max minutes outside ORB before expiry |
| `FBMaxTimeOutsideMinutesSR` | `int` | 60 | Max minutes outside session range before expiry |
| `FBMaxPenetrationPctOrb` | `decimal` | 0.30 | Max penetration as fraction of ORB range |
| `FBMaxPenetrationPctSR` | `decimal` | 0.25 | Max penetration as fraction of session range |
| `FBMinRejectionBodyPct` | `decimal` | 0.50 | Min rejection candle body as fraction of its range |
| `FBMaxTrendDayScore` | `int` | 60 | Max TrendDay score for activation |

### 1.7 Reset

`NewSession()` clears both trackers. `Reconfigure()` updates config and recomputes `MaxBarsAllowed`.

## 2. Engine State Machines

### 2.1 Setup C (ORB False Breakout)

```
_stC = 0   Idle — OrbTracker not activated
_stC = ±1  Armed — OrbTracker.IsActivated, waiting for tick entry
_stC = ±2  Active — entry filled, managing trade
```

### 2.2 Setup D (Session Range False Breakout)

```
_stD = 0   Idle — SessionRangeTracker not activated
_stD = ±1  Armed — SessionRangeTracker.IsActivated, waiting for tick entry
_stD = ±2  Active — entry filled, managing trade
```

### 2.3 Arm Logic (ProcessSetupC/D on bar close)

1. Guard: `_stC == 0`, `EnableC`, `_tradeCountC < MaxTradesC`, `!_pastCutoffC`
2. Check `_falseBreakout.OrbTracker.IsActivated` (C) or `.SessionRangeTracker.IsActivated` (D)
3. Direction: opposite of breakout (short breakout → long arm)
4. Check directional lock (`_bullTradedC`/`_bearTradedC`) unless `AllowRearmAfterBeC`
5. Set `_stC = +1` (long) or `-1` (short), record `_armEntryC`

### 2.4 Entry Logic (EvalTickSetupC/D on tick)

When `_stC == ±1`:
- **Long entry:** tick price crosses above ORB low (was false break below)
- **Short entry:** tick price crosses below ORB high (was false break above)
- Apply `NearPctC` guard — reject if price too far from arm level
- Calculate levels: `StopPctC`/`TargetPctC`/`PartialPctC` of ORB range (C) or session range (D)
- Stop reference: tracker's `SweepLow` (long) or `SweepHigh` (short), offset by `StopPctC`
- Fire `EntrySignal(Setup: SetupId.C, ...)` → `IOrderExecutor.OnEntrySignalAsync`
- Apply fill price, fire `IStrategyEventSink.OnEntryAsync`
- Set `_stC = ±2`, record entry fields

### 2.5 Exit Logic

Identical to A/B — no new exit mechanics:
- Partial hit → `PartialSignal` → `OnPartialSignalAsync`
- BE move (if `UseBeC`) → `BESignal` → `OnBESignalAsync`
- Target/Stop hit → `ExitSignal` → `OnExitSignalAsync` → `TradeRecord`
- Adverse timeout (`MaxAdverseMinutesC`)
- Cutoff force-exit (`CutoffHourC`/`CutoffMinuteC`)
- RTH close force-exit (`CloseAtRthCloseC`)
- Manual force-exit via dashboard button

### 2.6 Engine Fields (per setup, same pattern as A/B)

```
_stC, _entC, _stopC, _tgtC, _partialC, _initStopC, _activeC,
_tradeCountC, _partHitC, _pnlC, _armEntryC, _entryTimeC,
_stickyTgtC, _stickyStpC, _exitBarIdxC, _bullTradedC, _bearTradedC, _ctsC
```

Same set duplicated for D.

## 3. Config & SessionConfig

### 3.1 StrategyConfig — Per-Setup Properties

Following A/B pattern, for both C and D:
```
EnableC, ContractsC, MaxContractsC, HiVolMultC, MaxTradesC,
StopPctC, TargetPctC, NearPctC, PartialPctC, PartialCtsC,
MinRrC, UsePartialC, UseBeC, AllowRearmAfterBeC,
CutoffHourC, CutoffMinuteC, CloseAtRthCloseC, OrderTypeC,
MaxAdverseMinutesC
```

### 3.2 SessionConfig

New classes `SetupConfigC` and `SetupConfigD` extending `SetupConfigBase`. No extra fields beyond base — false breakout detection params are module-level, not per-session.

```csharp
public SetupConfigC SetupC { get; set; } = new();
public SetupConfigD SetupD { get; set; } = new();
```

`ToLegacyConfig()`, `FromExistingConfig()`, and `CreateDefaults()` updated to map C/D properties.

### 3.3 EF Migration

Add all per-setup columns (`EnableC`, `ContractsC`, ..., `EnableD`, `ContractsD`, ...) and module params (`FBMaxTimeOutsideMinutesOrb`, etc.) to the Configs table.

## 4. EngineSnapshot

### 4.1 New Fields

```csharp
// Setup C/D trade views
ActiveTradeView? SetupC { get; init; }
ActiveTradeView? SetupD { get; init; }
int TradeCountC, TradeCountD, MaxTradesC, MaxTradesD
int SetupCState, SetupDState
bool SetupCEnabled, SetupDEnabled
bool PastCutoffC, PastCutoffD
bool StickyTgtC, StickyStpC, StickyTgtD, StickyStpD

// FalseBreakout module context
bool FBOrbBreakoutActive { get; init; }
bool FBSessionBreakoutActive { get; init; }
int FBOrbBarsInBreakout { get; init; }
int FBSessionBarsInBreakout { get; init; }
decimal FBOrbPenetrationDepth { get; init; }
decimal FBSessionPenetrationDepth { get; init; }
bool FBOrbActivated { get; init; }
bool FBSessionActivated { get; init; }
bool IsCompoundFakeout { get; init; }

// Per-setup daily stats
int TodayWinsC, TodayLossesC, TodayWinsD, TodayLossesD
decimal TodayWinPnlC, TodayLossPnlC, TodayWinPnlD, TodayLossPnlD
```

## 5. Dashboard & Settings UI

### 5.1 Dashboard

**Setup cards grid:** Row 2 = A, B, C, D (four columns). Each card has identical layout:
- Header with setup name: "Setup C — ORB Fakeout" / "Setup D — Session Fakeout"
- State badge (Idle / Armed / Active)
- Active trade levels + unrealized PnL
- Trade count / max trades
- Sticky exit markers
- Force exit button

**Market Context area:** New "False Breakout" mini-card:
- ORB breakout status: idle / "Tracking (3/5 bars)" / "Activated"
- Session range breakout status: same
- Compound fakeout badge

### 5.2 Settings/Live.cshtml

Per-session config tabs: Setup C and D form sections (same fields as A/B).

Global module params in "Market Context Modules" area:
- FB Max Time Outside (ORB) / (Session Range)
- FB Max Penetration % (ORB) / (Session Range)
- FB Min Rejection Body %
- FB Max Trend Day Score

Setups summary table: two new rows for C and D.

### 5.3 API Endpoints

- `POST /api/engine/force-exit-c`
- `POST /api/engine/force-exit-d`

### 5.4 SignalR (crv-hub.js)

- `updateSetup('C', ...)` and `updateSetup('D', ...)` from snapshot
- False breakout context in market context section

## 6. Tests

### 6.1 Module Tests — `Modules/FalseBreakoutDetectorTests.cs`

- Breakout detected on bar close outside ORB range
- Breakout expires after MaxBarsAllowed bars
- Rejection activates on close back inside within limit
- Rejection rejected: body < MinRejectionBodyPct
- Rejection rejected: penetration > MaxPenetrationPct
- Rejection rejected: VWAP on wrong side
- Rejection rejected: TrendDay score too high
- Session range tracker: Asia H/L for London, London H/L for NY
- Compound flag: both trackers activate same direction
- NewSession resets all state

### 6.2 Integration Tests — `Strategy/FalseBreakoutIntegrationTests.cs`

- Setup C arms on OrbTracker activation
- Setup C entry fires on tick recross of ORB level
- Setup C respects NearPct guard
- Setup C partial/BE/target/stop exit flow
- Setup D arms on SessionRangeTracker activation
- Setup D entry on tick recross of prior session level
- Direction lock prevents re-arm (unless AllowRearmAfterBe)
- Cutoff and MaxTrades caps respected

### 6.3 Backtest

No changes to `BacktestEngine`. Setup C/D trades flow through existing `EntrySignal` → `BacktestExecutor` → `BacktestSink` path automatically.

Backtest UI: add C/D to setup filter dropdown on results page.

## 7. Files Modified/Created

### New Files
- `CRV.Core/Modules/FalseBreakoutDetector.cs`
- `CRV.Core.Tests/Modules/FalseBreakoutDetectorTests.cs`
- `CRV.Core.Tests/Strategy/FalseBreakoutIntegrationTests.cs`
- EF migration file

### Modified Files
- `CRV.Core/Strategy/OrbStrategyEngine.cs` — fields, ProcessSetupC/D, EvalTickSetupC/D, snapshot, Reconfigure, ResetDaily, ForceExitAll
- `CRV.Core/Models/StrategyConfig.cs` — per-setup C/D properties + FB module params
- `CRV.Core/Models/SessionConfig.cs` — SetupConfigC/D classes, ToLegacyConfig, FromExistingConfig, CreateDefaults
- `CRV.Core/Models/Signals.cs` — EngineSnapshot fields
- `CRV.Web/Services/LiveEngineOrchestrator.cs` — ForceExitSetupC/D methods
- `CRV.Web/Api/EngineController.cs` — force-exit-c/d endpoints
- `CRV.Web/Pages/Dashboard/Index.cshtml` — Setup C/D cards + FB context
- `CRV.Web/Pages/Dashboard/Index.cshtml.cs` — ForceExitC/D handlers
- `CRV.Web/Pages/Settings/Live.cshtml` — C/D form sections + FB module params
- `CRV.Web/Pages/Settings/Live.cshtml.cs` — C/D session config handling
- `CRV.Web/wwwroot/js/crv-hub.js` — C/D snapshot handling
- `CRV.Web/Pages/Backtest/*.cshtml` — setup filter dropdown
