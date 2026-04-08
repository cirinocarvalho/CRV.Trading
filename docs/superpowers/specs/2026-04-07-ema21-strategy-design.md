# EMA21 Strategy Integration Design

## Overview

Add a new EMA21-based strategy to the CRV.Trading platform alongside existing ORB-based strategies. The EMA21 strategy detects EMA cross and touch signals, enters on the next bar, and uses ATR-based targets with the EMA value at entry as stop. It runs for the full trading session (no ORB window dependency) and owns its own indicators internally.

## Architecture

### Self-Contained Strategy

`Ema21Strategy` implements `ISetupStrategy`. It owns its indicators internally and does not depend on `OrbState`, `IndicatorState`, or `ModuleState` — it ignores them and computes everything from the `Bar` data passed to `OnBar`.

This keeps zero coupling with the ORB pipeline while reusing all existing infrastructure (ComposableEngine, TickerGroup, BrokerEventHandler, RiskManager, backtest).

### Internal Indicators

All incremental, updated bar-by-bar via `OnBar`:

- **EMA21** — seeded with SMA on first 21 bars, then standard EMA smoothing formula: `ema = (close - prevEma) * k + prevEma` where `k = 2 / (21 + 1)`. Stores a ring buffer of last `SlopeLen` EMA values for slope calculation.
- **ATR(14)** — Wilder smoothing. Same formula as existing `AtrIndicator`.
- **Volume SMA(20)** — rolling 20-bar simple moving average of volume.

### Indicator Lifecycle

- **Persist across `ResetSession()`** — indicators survive session transitions. A 1-bar session still has warmed indicators from backfill.
- **Cleared on `Reset()` (new trading day)** — full reset of all state including indicators.
- **Warmup** — existing `WarmupBarAsync` flow feeds historical bars through `OnBar`. At least 21 bars needed to seed EMA, 14 for ATR, 20 for volume SMA. Since warmup already fetches sufficient history, no special warmup path is needed.

## Signal Detection

All signal detection runs on bar close (`OnBar`, only when `bar.IsConfirmed`).

### Prerequisites

EMA21 ready (21+ bars), ATR ready (14+ bars), volume SMA ready if `UseVolumeFilter=true` (20+ bars).

### Tick Zone

```
tickZone = TickSize * OpenTicksToEma
```

Example: 0.25 * 4 = 1.0 point for NQ.

### Slope Calculation

```
rawSlope = currentEma - ema[SlopeLen bars ago]
slopePct = |rawSlope| / currentEma * 100
SlopeUp  = rawSlope > 0 AND slopePct >= MinSlopePct
SlopeDown = rawSlope < 0 AND slopePct >= MinSlopePct
```

### Cross Signals

| Signal | Condition |
|---|---|
| CrossBull | prevClose < prevEma AND close > ema AND close + tickZone > ema |
| CrossBear | prevClose > prevEma AND close < ema AND close - tickZone < ema |

### Touch Signals

| Signal | Condition |
|---|---|
| TouchBull | close > ema AND SlopeUp AND low <= ema + ATR*AtrTouchMult AND high >= ema - ATR*AtrTouchMult AND close > ema AND bullish candle (close > open AND close > prevHigh) AND open > ema - tickZone |
| TouchBear | close < ema AND SlopeDown AND high >= ema - ATR*AtrTouchMult AND low <= ema + ATR*AtrTouchMult AND close < ema AND bearish candle (close < open AND close < prevLow) AND open < ema + tickZone |

### Volume Gate

All signals require `VolumeOk` — volume > 20-bar SMA when `UseVolumeFilter=true`, otherwise always true.

### Confirmed Entry (Next Bar)

- Raw signal fires on bar N: strategy arms (state = 1 for long, -1 for short).
- Entry fires on bar N+1's `OnBar` (at open price) if state is armed and `TradeState == Flat`.
- No double-fire guard beyond the Flat requirement.

## Entry & Exit

### Entry

Fires on bar after signal:

- **Entry price** = bar.Open
- **Stop** = EMA21 value at entry bar (fixed, not dynamic), snapped to tick
- **Target (Tg2Price)** = entry +/- ATR * `AtrTp2Mult`, snapped to tick
- **Partial (Tg1Price)** = entry +/- ATR * `AtrTp1Mult`, snapped to tick
- **ATR** snapshotted at entry time
- **R:R check** against `MinRr` — skip entry if insufficient
- **Contract sizing** via existing pattern (HiVolMult when AtrRatio >= 1.0, MaxContracts cap)

### EntrySignal Mapping

| EntrySignal field | EMA21 value |
|---|---|
| Entry | bar.Open |
| Stop | EMA21 at entry bar (snapped to tick) |
| Tg2Price | entry +/- ATR * AtrTp2Mult |
| Tg1Price | entry +/- ATR * AtrTp1Mult |
| UsePartial | from config |
| UseBe | from config |
| PartialContracts | from config |
| OrderType | from config |

### Exit

Handled entirely by existing `BrokerEventHandler` — no changes needed:
- Partial at TP1
- Breakeven after partial
- Full exit at TP2 or stop

## Configuration

### New StrategyType Enum Value

```csharp
StrategyType { Pullback, Retest, OrbFakeout, SessionFakeout, Ema21 }
```

### New Fields on StrategySetupConfig

EMA21-specific fields (ignored by ORB strategies):

| Field | Type | Default | Description |
|---|---|---|---|
| `SlopeLen` | int | 5 | Lookback bars for EMA slope |
| `AtrTouchMult` | decimal | 0.5 | ATR mult for EMA touch detection zone |
| `MinSlopePct` | decimal | 0.05 | Min slope % to filter flat EMA noise |
| `OpenTicksToEma` | int | 4 | Max ticks signal bar open may be from EMA |
| `UseVolumeFilter` | bool | false | Require volume > 20-bar SMA on signal bar |
| `AtrTp1Mult` | decimal | 1.0 | ATR mult for partial target (TP1) |
| `AtrTp2Mult` | decimal | 2.0 | ATR mult for full target (TP2) |

### Reused Existing Fields

`Contracts`, `HiVolMult`, `MaxContracts`, `MinRr`, `MaxTrades`, `MaxLongTrades`, `MaxShortTrades`, `UsePartial`, `UseBe`, `PartialCts`, `CutoffHour`, `CutoffMinute`, `EntryTickOffset`, `OrderType`, `TickSize`, `PointValue`, `AutoTrail`, `SessionSlots`, `Enabled`.

### Session Scope

Active for the full trading session (session start to cutoff). No ORB window dependency. Uses existing per-session cutoff config.

## Registration

### StrategyFactory

```csharp
StrategyType.Ema21 => new Ema21Strategy(config),
```

### UI

Separate "EMA21 Strategy" section in the Settings page. Not mixed into the ORB basket editor. Own enable/disable toggle, own setup configuration.

## File Structure

### New Files

- `CRV.Core/Strategy/Ema21Strategy.cs` — single file implementing `ISetupStrategy` with internal indicators, signal detection, entry generation (~300 lines)
- `CRV.Core.Tests/Strategy/Ema21StrategyTests.cs` — unit tests

### Modified Files

- `CRV.Core/Strategy/ISetupStrategy.cs` — add `Ema21` to `StrategyType` enum
- `CRV.Core/Strategy/StrategyFactory.cs` — add `Ema21` case
- `CRV.Core/Models/StrategySetupConfig.cs` — add EMA21-specific config fields
- `CRV.Web/Pages/Settings/Live.cshtml` + `.cs` — add EMA21 section

## Tests

1. **Indicator warmup** — feed 21+ bars, verify EMA/ATR are ready
2. **CrossBull signal** — prevClose below EMA, close above -> arms long -> next bar entry
3. **CrossBear signal** — prevClose above EMA, close below -> arms short -> next bar entry
4. **TouchBull signal** — uptrend pullback to EMA zone with bullish candle -> arms long
5. **TouchBear signal** — downtrend pullback to EMA zone with bearish candle -> arms short
6. **No signal when flat slope** — slopePct < MinSlopePct -> no touch signals
7. **Volume filter** — signal suppressed when volume < 20-bar SMA (UseVolumeFilter=true)
8. **Entry levels** — verify stop = EMA21 at entry, target = ATR * mult
9. **MaxTrades guard** — no entry after max trades reached
10. **ResetSession preserves indicators** — indicators still ready after session reset
11. **Reset clears indicators** — new day resets everything
