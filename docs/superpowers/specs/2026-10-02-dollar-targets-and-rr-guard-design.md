# Dollar Targets and R:R Guard Design

## Overview

Let every strategy (Pullback, Retest, OrbFakeout, SessionFakeout, and Ema when it arrives) set its target in dollars, per contract or for the whole position, and make the minimum reward/risk rule explicit: checked when a strategy is saved, applied per trade as Skip or Raise target, switchable off, and visible when it is off.

Part of the EMA set (order: ema21-removal → hold-and-close → **dollar-targets-and-rr-guard** → htf-bars-and-history → ema-strategy → ema-parity-and-validation). It does not depend on the other specs and can ship on its own; `ema-strategy` builds on it.

Source: kickoff spec 2.

## How levels are computed today

- `LevelCalculator` is in `CRV.Core/Strategy/StrategyHelpers.cs:4`. `CalcLevels` (`:13-35`) and `CalcLevelsB` (`:41-58`) are the same maths with a different parameter order and return `(stop, target, partial, rr)`, rounded to the tick (`RoundToTick`, half away from zero).
- There is **one** target mode: `targetDist = orbRange × TargetPct / 100`. `PartialPct` is a percentage of the target distance (`partialDist = targetDist × PartialPct / 100`).
- Call sites (each the second line of a two-line call): `PullbackStrategy.cs:342-343`, `RetestStrategy.cs:529-530` (`CalcLevelsB`), `OrbFakeoutStrategy.cs:261-262`, `SessionFakeoutStrategy.cs:272-273` (session range, falling back to the ORB range).
- Order in every strategy: levels → entry tick offset → stop-mode override (`BarHL`, `Vwap`, capped at the `OrbPct` risk) → `if (rr < MinRr) return;` → `AutoSizeByRiskCalculator.Calc` (Pullback `:388`, Retest `:593`, OrbFakeout `:313`, SessionFakeout `:324`).
- In Pullback and Retest with `OrbPct` stops, `rr` is computed from the entry **before** the tick offset; the fakeouts recompute it after.
- A trade below `MinRr` (default 1.5, `StrategySetupConfig.cs:47`) is dropped by a bare `return`: no log, no refusal record.

## Config

New fields in `StrategySetupConfig`, carried through `BasketEntry.Config`, `StrategyConfig.ToSetupConfig` and the legacy A–D builders (`BuildSetupConfigA..D`, which get the defaults below and no new UI):

| Field | Type | Default | Meaning |
|---|---|---|---|
| `TargetMode` | enum `{ RangePct, Dollars, Atr, RiskMultiple }` | `RangePct` | `RangePct` is today's behaviour. `Atr` and `RiskMultiple` are used by the EMA strategy only; the setup page offers them only there. |
| `TargetDollars` | decimal | 0 | Target size in $ when `TargetMode = Dollars` |
| `TargetDollarsBasis` | enum `{ PerContract, WholePosition }` | `PerContract` | What `TargetDollars` is measured over |
| `EnforceMinRr` | bool | `true` | The guard switch |
| `MinRrAction` | enum `{ Skip, RaiseTarget }` | `Skip` | What a trade below `MinRr` does |

`MinRr` and `PartialPct` keep their meaning. The migration writes `EnforceMinRr = true` and `MinRrAction = Skip` on every stored entry and legacy setup, so nothing changes until the user changes it.

## Distance in points

- `PerContract`: `TargetDollars / PointValue`.
- `WholePosition`: `TargetDollars / (PointValue × TotalContracts)`, using the contract count **after** auto-size.
- Rounded to the tick. Examples: MNQ $400 per contract = 200 pts; MNQ $400 whole position with 2 contracts = 100 pts; NQ $400 per contract = 20 pts.
- Partial: `PartialPct` of that distance, so $400 with 50% puts the partial at $200 of the move.

## New order of operations

```
entry (with tick offset) → final stop (stop mode, capped) → size (AutoSizeByRiskCalculator, uses the stop only)
  → target + partial (LevelCalculator, needs the contract count for WholePosition)
  → R from the final entry, stop and target → guard → signal
```

One `LevelCalculator` overload takes the target mode inputs and the contract count, and every call site uses it; `CalcLevels` and `CalcLevelsB` become thin wrappers over it (or are removed if nothing else calls them). Stops are computed as today. Pullback and Retest compute R from the final entry, like the fakeouts.

## The guard

**On (`EnforceMinRr = true`):**

- **Save check.** Saving is blocked when the target is below `MinRr` × the strategy's **typical stop**. Every target mode except `Atr` is checked; `Atr` saves with a warning (see below). The error names the minimum in the field's own unit, e.g. "Raise the target to at least $240 a contract", "… at least 120% of the range", "… at least 1.5R".
- **Range target.** `RangePct` is checked against the stop setting, not backtest history: with an `OrbPct` stop the minimum is `MinRr × StopPct` of the range; a bar or VWAP stop saves with a warning. `Atr` saves with a warning, since its check needs the ATR the EMA strategy owns (plan 5); `RiskMultiple` is checked as `AtrTp2Mult ≥ MinRr`.
- **Typical stop (dollar targets)** = the median stop distance of the strategy's last 30 trades in its saved backtest runs (`BacktestRuns.ResultJson`, matched by setup id). With no backtest yet the save is allowed with the warning "The reward / risk check runs once this strategy has a backtest" — no estimate is invented. The setup page shows the typical stop and where it came from.
- **Per trade.** `Skip`: the trade is not taken and the skip is recorded through the same path as a sizing refusal (`SizeRefusalGate` / `PendingSizeRefusal`), so it reaches the log and the cockpit card ("Skipped: 1.2R below 1.5R"). `RaiseTarget`: the trade's target moves out to `MinRr × risk`, the partial is recomputed at `PartialPct` of the new distance, and the trade is taken.

**Off:** no save check and no per-trade check. `MinRr` and `MinRrAction` stay saved and are shown greyed out as "Not enforced".

## Visibility

- An "R:R not enforced" badge on the setup page head, the Strategies row and the cockpit card: amber when the strategy is on, grey when it is off.
- A note on the Strategies page counts the strategies that are on with the guard off.
- Backtest results record the guard state per strategy.
- `StrategyText` adds "Takes trades below X R" when the guard is off.
- Strategies list descriptions include the target: "target $400 / contract", "$150 / position", "2R", "100% of range".

## Setup page

For `Dollars`: the target in points, the partial in $, and two outcomes: "all contracts at target" and "partial, then the rest at target" (e.g. whole position, $400, 2 contracts: $400 and $300; with 1 contract: "No partial with 1 contract"). An "example contracts after sizing" input drives the preview only. Copy and layout follow the EMA setup mockup's "Stop and exits" and "Size and limits" panels. The page passes the WCAG gate.

## File Structure

### New Files

| File | Responsibility |
|---|---|
| `CRV.Core/Strategy/TypicalStop.cs` | Median stop of the last 30 backtest trades for a setup |
| `CRV.Core/Migrations/<ts>_AddTargetModeAndRrGuard.cs` | Guard defaults on stored entries |

### Modified Files

| File | Change |
|---|---|
| `CRV.Core/Models/StrategySetupConfig.cs`, `StrategyConfig.cs` | New fields and mapping |
| `CRV.Core/Strategy/StrategyHelpers.cs` | The `LevelCalculator` overload |
| `CRV.Core/Strategy/{Pullback,Retest,OrbFakeout,SessionFakeout}Strategy.cs` | New order; guard |
| `CRV.Core/Strategy/SizeRefusalGate.cs` (and refusal model) | Min-R skip as a recorded refusal |
| `CRV.Web/Pages/Setup/Strategy.cshtml(.cs)`, `Strategies.cshtml(.cs)`, `StrategyText.cs` | Fields, preview, save check, badges, descriptions |
| `CRV.Web/Pages/Dashboard/Index.cshtml` | Card badge and skip line |
| `CRV.Backtest/Results/BacktestResults.cs` | Guard state per strategy |

## Tests

1. Distance: long and short; tick rounding; `PartialPct`; NQ vs MNQ; both bases; `WholePosition` with 1, 2 and 4 auto-sized contracts.
2. Order: sizing runs before a `WholePosition` target is set; R uses the final entry (tick offset applied) in all four strategies.
3. A 1-contract trade with a partial resolves to a single Tg2 bracket (already true since #55/#57; pinned here for dollar targets).
4. Save guard at the boundary (just below, at, just above) with backtest history, in each target mode, with the error naming the minimum in that unit; without history the save passes with the warning.
5. Per trade: `Skip` records a refusal and places nothing; `RaiseTarget` places the trade at `MinRr × risk` with the partial recomputed.
6. Guard off: no save check, no per-trade check, fields greyed, badge shown.
7. Migration defaults: `RangePct`, `EnforceMinRr = true`, `Skip` on every stored entry and legacy setup.
