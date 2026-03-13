# CRV Trading Engine — Module Extension Design

**Date:** 2026-03-12
**Status:** Approved

## Goal

Extend OrbStrategyEngine with 5 new analytical modules and 3 composite trade setups (C, D, F), plus dashboard integration.

## Architecture

### Composition Pattern

Each module is a standalone class in `CRV.Core/Modules/`, owned by `OrbStrategyEngine` as fields:

```
OrbStrategyEngine
  ├── _sessionEngine    : SessionEngine
  ├── _sweepDetector    : SweepDetector
  ├── _vwapModel        : VwapModel
  ├── _openingDrive     : OpeningDriveDetector
  ├── _trendDay         : TrendDayFilter
  └── _setupEngine      : CompositeSetupEngine
```

### Common Interface

```csharp
public interface IEngineModule
{
    void OnBar(Bar bar, DateTime tradingDate);
    void OnTick(decimal price, DateTime utcTime);
    void NewSession(DateTime tradingDate);
}
```

### Data Flow

1. Engine calls `module.NewSession()` on session boundary (existing `_lastDate` check)
2. Engine calls `module.OnTick()` from `ProcessPriceTickAsync`
3. Engine calls `module.OnBar()` from `ProcessBarAsync`
4. After all modules updated, `CompositeSetupEngine` reads their outputs
5. Snapshot builder reads module state into `EngineSnapshot` fields

### Historical Levels

At engine start, `LiveEngineOrchestrator` fetches 30 daily bars from broker REST API, passes to `SessionEngine.SeedHistory(dailyBars)` to compute PDH/PDL/PWH/PWL/PMH/PML.

---

## Module 1: Session Engine

**File:** `CRV.Core/Modules/SessionEngine.cs`

### Sessions (Eastern Time)

| Session    | Window          |
|------------|-----------------|
| Asia       | 18:00–00:00     |
| London     | 03:00–08:30     |
| NY Open    | 09:30–11:30     |
| Midday     | 11:30–13:30     |
| Power Hour | 15:00–16:00     |

### State

- `CurrentSession` enum
- Per-session: `AsiaHigh/Low/Mid/Range`, `LondonHigh/Low`, `NYHigh/Low`
- Full day: `SessionHigh/Low`
- Historical: `PrevDayHigh/Low`, `PrevWeekHigh/Low`, `PrevMonthHigh/Low`
- Derived: `AsiaCompressed` (asiaRange < ATR × 1.2)
- Cross-session: `LondonSweptAsiaHigh/Low`, `NYBullExpansion/NYBearExpansion`
- `SessionBias` (-1/0/+1)

### Key Methods

- `SeedHistory(dailyBars)` — sets PDH/PDL/PWH/PWL/PMH/PML from broker REST data
- `NewSession()` — rolls current into previous, resets trackers
- `OnBar(bar, tradingDate)` — detect session transitions (bar time → ET), update per-session high/low, evaluate London sweep / NY expansion
- `OnTick(price, time)` — update intra-session high/low

### Dependencies

Needs ATR value from engine (passed as parameter or set before calling).

---

## Module 2: Sweep Detector

**File:** `CRV.Core/Modules/SweepDetector.cs`

### Levels Monitored

Fed from SessionEngine + OrbCalculator:
- PDH/PDL, PWH/PWL, PMH/PML
- Session High/Low, ORB High/Low
- Equal Highs/Lows (detected from recent bars)

### Configuration

```
MinTickPenetration: decimal (default: tickSize × 2)
MinBodyReject: decimal (default: tickSize × 4)
EqualLevelTolerance: decimal (default: tickSize × 8)
ConfirmationBars: int (default: 1)
```

### Logic (bar close)

- **Bearish sweep:** `bar.High > level + minTickPen && bar.Close < level && upperWick >= minBodyReject`
- **Bullish sweep:** `bar.Low < level - minTickPen && bar.Close > level && lowerWick >= minBodyReject`
- **Equal highs:** `|bar[n-1].High - bar[n-2].High| <= tolerance` → new sweep level
- **Post-sweep:** next bar breaks local low = reversal, holds = continuation

### Outputs

```
ActiveSweeps: List<SweepEvent>
AnyBullSweep / AnyBearSweep: bool
LastSweepLevel / LastSweepType: for dashboard
```

---

## Module 3: VWAP Model

**File:** `CRV.Core/Modules/VwapModel.cs`

Wraps existing `VwapIndicator`, adds deviation bands and state classification.

### Deviation Bands

±1σ, ±2σ from incremental variance of HLC/3.

### State Classification

```
+2 = ExtendedBull (close > Upper2)
+1 = AcceptBull   (close > VWAP, low >= VWAP)
 0 = Neutral
-1 = AcceptBear   (close < VWAP, high <= VWAP)
-2 = ExtendedBear (close < Lower2)
```

### Setup Signals

- `BullVWAPReclaim / BearVWAPReject` — cross events
- `VWAPReversionLong` — close < Lower2 + bullish close near high
- `VWAPReversionShort` — close > Upper2 + bearish close near low
- `BullVWAPPullback` — trend day + low in Upper1–VWAP zone + close > VWAP
- `BearVWAPPullback` — trend day + high in Lower1–VWAP zone + close < VWAP

### Outputs

```
Vwap, Upper1, Upper2, Lower1, Lower2
VwapState: int (-2 to +2)
BullVWAPReclaim, BearVWAPReject
VWAPReversionLong, VWAPReversionShort
BullVWAPPullback, BearVWAPPullback
```

---

## Module 4: Opening Drive Detector

**File:** `CRV.Core/Modules/OpeningDriveDetector.cs`

### During ORB Window (OnBar)

Accumulate: bull/bear bar count, drive high/low, close quality (close-near-high ratio).

### At ORB Close (freeze)

- OR range > ATR × 0.80
- Bull drive: bullCount > bearCount × 2, close > VWAP, close in upper 30% of drive range
- Bear drive: bearCount > bullCount × 2, close < VWAP, close in lower 30% of drive range
- No deep pullback: retracement < 35% of drive range

### Outputs

```
OpeningDriveBull, OpeningDriveBear: bool
OpeningDriveConfirmed: bool
DriveRangePctATR: decimal
DrivePullbackPct: decimal
```

---

## Module 5: Trend Day Filter

**File:** `CRV.Core/Modules/TrendDayFilter.cs`

Score-based model (0–5), updated every bar after ORB forms.

### Scoring (1 point each)

| # | Bull                          | Bear                          |
|---|-------------------------------|-------------------------------|
| 1 | Opening drive bull            | Opening drive bear            |
| 2 | Accepted above ORB            | Accepted below ORB            |
| 3 | Close > VWAP                  | Close < VWAP                  |
| 4 | Shallow pullback from high <35% | Shallow pullback from low <35% |
| 5 | Session high > ORB high       | Session low < ORB low         |

Trend day = score ≥ 4.

### Inputs (from other modules)

OpeningDrive state, ORB levels, VWAP value, SessionEngine high/low.

### Outputs

```
BullScore, BearScore: int (0–5)
TrendDayBull, TrendDayBear: bool
```

---

## Composite Setup Engine

**File:** `CRV.Core/Modules/CompositeSetupEngine.cs`

Reads all module outputs, evaluates setups C/D/F on each bar close.

### Setup C — Sweep Reversal

- `AnyBullSweep && close > VWAP && bullScore >= 2` → long
- `AnyBearSweep && close < VWAP && bearScore >= 2` → short
- Target: VWAP or opposite liquidity level

### Setup D — Opening Drive Pullback

- `OpeningDriveBull && TrendDayBull && BullVWAPPullback` → long
- `OpeningDriveBear && TrendDayBear && BearVWAPPullback` → short
- Target: session high extension

### Setup F — Midday VWAP Reversion

- `InMidday && !TrendDayBull && VWAPReversionLong` → long
- `InMidday && !TrendDayBear && VWAPReversionShort` → short
- Target: VWAP

### Session Expansion

- `LondonSweptAsiaLow && NYBullExpansion` → long
- `LondonSweptAsiaHigh && NYBearExpansion` → short

Each setup arms on bar close, executes on tick (same pattern as Setup A/B).

---

## EngineSnapshot Extensions

New fields:

```csharp
// Session
string CurrentSession
decimal SessionHigh, SessionLow
decimal PrevDayHigh, PrevDayLow
bool AsiaCompressed

// Sweep
string LastSweep  // e.g. "PDH Bear" or "None"

// VWAP Model
decimal VwapUpper1, VwapUpper2, VwapLower1, VwapLower2
int VwapState

// Opening Drive
bool OpeningDriveBull, OpeningDriveBear

// Trend Day
int TrendScoreBull, TrendScoreBear

// Composite Setups
int SetupCState, SetupDState, SetupFState
ActiveTradeView? SetupC, SetupD, SetupF
```

---

## Implementation Order

**Phase 1:** IEngineModule interface → SessionEngine → VwapModel → SweepDetector
**Phase 2:** OpeningDriveDetector → TrendDayFilter
**Phase 3:** CompositeSetupEngine → EngineSnapshot extensions → Dashboard UI

Each module gets unit tests in `CRV.Core.Tests/Modules/`.

---

## File Inventory

### New Files

- `CRV.Core/Modules/IEngineModule.cs`
- `CRV.Core/Modules/SessionEngine.cs`
- `CRV.Core/Modules/SweepDetector.cs`
- `CRV.Core/Modules/VwapModel.cs`
- `CRV.Core/Modules/OpeningDriveDetector.cs`
- `CRV.Core/Modules/TrendDayFilter.cs`
- `CRV.Core/Modules/CompositeSetupEngine.cs`
- `CRV.Core.Tests/Modules/SessionEngineTests.cs`
- `CRV.Core.Tests/Modules/SweepDetectorTests.cs`
- `CRV.Core.Tests/Modules/VwapModelTests.cs`
- `CRV.Core.Tests/Modules/OpeningDriveDetectorTests.cs`
- `CRV.Core.Tests/Modules/TrendDayFilterTests.cs`
- `CRV.Core.Tests/Modules/CompositeSetupEngineTests.cs`

### Modified Files

- `CRV.Core/Models/Signals.cs` — EngineSnapshot new fields
- `CRV.Core/Strategy/OrbStrategyEngine.cs` — instantiate + call modules
- `CRV.Web/Services/LiveEngineOrchestrator.cs` — daily bar fetch + SeedHistory
- `CRV.Web/Pages/Dashboard/Index.cshtml` — display new fields
- `CRV.Web/wwwroot/js/crv-hub.js` — render new snapshot fields
