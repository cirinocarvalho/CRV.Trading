# Design: Setup B — Configurable Stop Distance (% ORB)

**Date:** 2026-03-10
**Status:** Approved

## Problem

Setup B's stop is hardcoded at `orbMid` in `CalcLevelsB`. There is no way to adjust the stop distance — it is always 50% of ORB range from the entry (`orbHigh`/`orbLow`). Setup A has `StopPctA` for exactly this control; Setup B has no equivalent.

## Solution

Add `StopPctB` (decimal fraction, default `0.50`) to `StrategyConfig`. Replace the hardcoded `orbMid` reference in `CalcLevelsB` with an entry-anchored percentage calculation. Default 0.50 is mathematically identical to `orbMid`, so existing behavior is preserved.

## Changes

### 1. `StrategyConfig` (CRV.Core/Models/StrategyConfig.cs)
- Add property: `public decimal StopPctB { get; set; } = 0.50m;`
- Add validation in `Validate()` (inside `if (EnableB)`): `StopPctB > 0`

### 2. `LevelCalculator.CalcLevelsB` (CRV.Core/Strategy/StrategyHelpers.cs)
- Replace `decimal orbMid` parameter with `decimal stopPct`
- Compute stop as: `entry - orbRange * stopPct` (Long) / `entry + orbRange * stopPct` (Short), then tick-round
- `orbMid` parameter is removed entirely — callers no longer need to pass it

### 3. `OrbStrategyEngine` (CRV.Core/Strategy/OrbStrategyEngine.cs)
- Update `TryEntryB` call to pass `_cfg.StopPctB` instead of `orbMid`
- Update `TryEntryBFromTick` call to pass `_cfg.StopPctB` instead of `_orb.OrbMid`

### 4. `Live.cshtml` (CRV.Web/Pages/Settings/Live.cshtml)
- Add `<input name="Config.StopPctB" type="number" step="any" ...>` in the Setup B section
- Label: "Stop Dist (% ORB)"
- Place in the existing `row g-2` alongside "Retest Zone (% ORB)"

### 5. `Backtest.cshtml` (CRV.Web/Pages/Settings/Backtest.cshtml)
- Add matching `StopPctB` input in the backtest config form

### 6. EF Migration
- Run `dotnet ef migrations add AddStopPctB --project CRV.Core --startup-project CRV.Web`

### 7. Tests (CRV.Core.Tests/Strategy/LevelCalculatorTests.cs)
- Update existing `CalcLevelsB` tests for new signature (swap `orbMid` arg for `stopPct`)
- Add test: `stopPct=0.50` with symmetric ORB gives same stop price as old `orbMid`

## Backward Compatibility

Default `StopPctB = 0.50` produces `stop = entry - orbRange * 0.50`. For a Long entry at `orbHigh`:
`orbHigh - (orbHigh - orbLow) * 0.50 = (orbHigh + orbLow) / 2 = orbMid` ✓

No behavioral change for users who don't touch the setting.
