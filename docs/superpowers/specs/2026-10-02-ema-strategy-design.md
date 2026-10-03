# EMA Strategy Design

## Overview

One EMA strategy (`StrategyType.Ema = 5`) covering price-vs-EMA and EMA-vs-EMA setups with three entries (Touch, Cross, Cross + retest), signals on closed higher-timeframe bars, configurable stops and targets, and optional confirmations. It replaces the retired EMA21 strategy.

Part of the EMA set (order: ema21-removal → hold-and-close → dollar-targets-and-rr-guard → htf-bars-and-history → **ema-strategy** → ema-parity-and-validation). Requires `ema21-removal` (type number, basket column, root bar size, invalid-config handling), `dollar-targets-and-rr-guard` (target modes, guard) and `htf-bars-and-history` (aggregator, `EmaIndicator`, history).

Source: kickoff spec 4. UI: the EMA Strategy Setup Mockup (setup page, Strategies list, Cockpit) and the signal-logic canvas.

## Ground rules

- The strategy emits an `EntrySignal`; `BrokerEventHandler` owns the trade lifecycle.
- The same executor interface serves live, paper, backtest and Tradovate Replay; no special cases.
- Signals are evaluated on **closed** bars only, higher-timeframe bars included. No lookahead.
- Sizing as today: the strategy calls `AutoSizeByRiskCalculator` and fills `TotalContracts`, in the order set by `dollar-targets-and-rr-guard`.

## Config

New fields on `StrategySetupConfig` (enums stored as **strings** in basket JSON via a per-property `JsonStringEnumConverter`; the existing int enums are unchanged):

| Field | Values | Default |
|---|---|---|
| `EmaSource` | `PriceVsEma`, `EmaVsEma` | `PriceVsEma` |
| `EmaEntry` | `Touch`, `Cross`, `CrossRetest` | `CrossRetest` |
| `EmaDirection` | `Up`, `Down`, `Both` | `Both` |
| `EmaPeriod` | 8, 21, 50, 200 (PriceVsEma) | 21 |
| `FastEma`, `SlowEma` | 8, 21, 50, 200 (EmaVsEma), Fast < Slow | 8, 21 |
| `RetestEma` | `Fast`, `Slow` (EmaVsEma) | `Fast` |
| `SignalTimeframe` | M5, M15, M30, H1, H4, H8, D1, W1, MN1 | H4 |
| `CheckEveryExecutionBar` | bool (Touch and CrossRetest only; "option B") | false |
| `TouchTicks`, `TouchAtr` | ints / decimal; tolerance = max(ticks × tick, k × ATR) | 1, 0.10 |
| `RearmAtr` | decimal (Touch) | 0.5 |
| `CloseBackOnSide` | bool (Touch, CrossRetest) | true |
| `MinCloseBeyondAtr` | decimal, 0 = off (PriceVsEma cross) | 0 |
| `MinSeparationAtr` | decimal, 0 = off (EmaVsEma cross) | 0 |
| `HoldBars` | int ≥ 1; 1 = the cross bar counts (off) | 1 |
| `RetestMinAwayAtr` | decimal | 0.25 |
| `RetestWindowBars` | int | 10 |
| `MaxEntriesPerCross` | int | 1 |
| `EmaStopMode` | `SignalBarExtreme`, `EmaAtr`, `EntryAtr` | per entry, below |
| `StopBuffer` | ticks (extreme modes) or × ATR (ATR modes) | 2 ticks / 0.5 ATR |
| `Confirmations` | list, below | trend + time window on |
| `ConfirmationsNeeded` | `All` or N (1 … number switched on) | All |

**Direction drives `AllowLong` / `AllowShort`:** Up → long only, Down → short only, Both → both. The long/short caps, the direction guards and the badges all read those two switches. Up means long; for Touch, Up is the long pullback from above.

**Validation (save blocked, engine start disables only this entry):** Touch requires `PriceVsEma`; `EmaVsEma` requires Fast < Slow; `SignalTimeframe` is a whole multiple of the root's bar size; history requirements from `htf-bars-and-history`.

## Signals

All on closed signal-timeframe bars (or, with `CheckEveryExecutionBar`, each closed execution bar against the last **closed** higher-timeframe EMA). ATR is the signal timeframe's ATR.

### Touch (PriceVsEma)

- Long: the bar's low ≤ EMA + tolerance; the previous close was above the EMA; this bar closes strictly above the EMA. Exact equality never counts.
- Short: the mirror image.
- One signal per arm. The side re-arms once the low clears EMA + `RearmAtr` × ATR (mirror for shorts).
- A touch rejected by confirmations does not use up the arm.

### Cross

- Sign-state crossover on closed bars: PriceVsEma compares close vs EMA; EmaVsEma compares fast vs slow. Equal values never count as a cross.
- Optional `MinCloseBeyondAtr` (PriceVsEma) / `MinSeparationAtr` (EmaVsEma) and `HoldBars` (the new side must hold for N bars, the cross bar included; the check happens on the Nth bar).

### Cross + retest

State machine `Idle → Crossed → Signal | Expired`:

1. A confirmed cross (with the Cross options) enters `Crossed`, remembering the side and bar.
2. Price must move at least `RetestMinAwayAtr` × ATR beyond the retest EMA (PriceVsEma: the EMA; EmaVsEma: `RetestEma`).
3. Within `RetestWindowBars`, a **later** bar touches that EMA (Touch tolerance) and, with `CloseBackOnSide`, closes back on the cross side. That bar is the signal; the cross bar never is.
4. A close back across the EMA, or the end of the window, cancels the setup. `MaxEntriesPerCross` (default 1) caps signals per cross.

### Timing

Signal on the signal bar's close; entry at the next execution bar's open, using Retest's arm-then-enter pattern (`_enterNextBarOpen`, consumed on the first tick of the next bar, `RetestStrategy.cs:34, 409-424, 443-460`). The retired EMA21 strategy entered at the open of the bar whose close it had already used; that lookahead is pinned out by a test.

## Stops and targets

| Entry | Default stop | Alternatives |
|---|---|---|
| Touch | Past the touch bar's extreme + buffer (ticks) | ATR × k from entry |
| Cross + retest | Past the retest bar's extreme + buffer | EMA ± ATR × k; ATR × k from entry |
| Cross | (Slow) EMA ± ATR × k | ATR × k from entry |

Targets: `TargetMode` `Atr` (`AtrTp1Mult` / `AtrTp2Mult`), `RiskMultiple` (TP1 / TP2 in R), or `Dollars` (from `dollar-targets-and-rr-guard`), with the min-R guard. TP1/TP2 use the existing partial and break-even settings. `CloseAtRthClose` from `hold-and-close` applies; new EMA strategies default to closing.

## Confirmations

`IEntryConfirmation.Vote(context) → ConfirmationVote(Name, Active, Passed, Value, Threshold)`, shaped like `ChopFilterDiagnostic` (whose own field is `Voted`; here `Passed` reads correctly for a confirmation). Aggregation: `All` or at least N, N clamped to 1 … switched-on count like `ChopRegimeConfig.Normalize()`. **Inactive (not enough data) counts as a fail.** Evaluated when the strategy arms.

Shipped:

| Confirmation | Rule | Params (defaults) |
|---|---|---|
| Higher-timeframe trend | Longs only above, shorts only below, an EMA on D1 or W1 | D1, EMA 200 |
| Time window | Skip the first / last N minutes of RTH | 15 / 15 |
| Rejection candle (Touch, Retest) | The signal bar closes in the top X % of its range (long) / bottom (short) | 40 % |
| Not stretched | Entry at most k × ATR from the EMA | 1.5, off |

The chop filter stays the existing `TickerGroup` filter (`BypassChopFilter`). Deferred: VWAP, relative volume, EMA stack, RSI/MACD, sweep.

## Migration

`ConvertRetiredEma21Entries`: each stored `"StrategyType":4` entry in `EmaBasketJson` becomes a **disabled** `Ema` entry — `EmaSource = PriceVsEma`, `EmaEntry = Touch`, `EmaPeriod = 21`, `SignalTimeframe` = its bar size (or the nearest valid one), same ticker, sessions and sizing, `CloseAtRthClose = true`, label + " (migrated from EMA21)". Done in C# with `BasketCodec`, never string replacement; an unparseable basket is left as it is and logged.

## Web

Matches the mockup, built on `tokens.css` / `shell.css` / `components.css`, and passes the WCAG gate (the setup page, an EMA cockpit card in each state, and the disabled card join the scan list).

**Setup page** panels: Basics, Signal ("What crosses", Entry, Direction pickers; conditional fields per the mockup's `data-show` rules), Confirmations, Sessions, Stop and exits, Size and limits, side column with "In plain words", History, Save. Corrections to the mockup:

- Bar size offers 1, 2, 5, 10, 15, 20, 30, 60 and reads "Shared by every NQ / MNQ strategy"; no "Same as the engine".
- Copy that says "5-minute" uses the real bar size.
- The name field is never overwritten once typed.
- "In plain words": "tick"/"ticks"; the short-only Touch sentence includes its tolerance; "half off at…" only when a partial is set; Both describes both sides; it ends with the close-or-hold sentence and, with the guard off, "Takes trades below X R".
- Confirmations: "All on" or "At least N" (N up to the switched-on count).
- Missing classes from the mockup's "proposed additions" (`.st-trigger*`, `.st-conf*`, `.st-meter*`, `.st-usd`, `.st-wide`, `.st-row-title`, `.ck-rr`, `.crumb`) go into `components.css` using themed tokens.

**Strategies list:** row description `EMA · {entry} · /{symbol} · {sessions} · target {target}`, e.g. "EMA · cross + retest · /MNQZ26 · London, NY · target $400 / contract".

**Cockpit card:** state cells as the mockup — e.g. "Crossed down 06:00 H4", "EMA 21 (H4)", "Waiting for: Retest ≤ 4 bars left", "Moved away 0.41 ATR ✓", "Target", "Partial", "Confirmations 2 of 3", "H4 bar closes 14:00 ET"; in a trade, the existing trade cells. The setup snapshot carries the state machine's fields.

**`StrategyText`:** a sentence for every Source × Entry × Direction combination.

## File Structure

### New Files

| File | Responsibility |
|---|---|
| `CRV.Core/Strategy/EmaStrategy.cs` | The strategy |
| `CRV.Core/Strategy/EmaSignals.cs` | Touch, Cross and CrossRetest detectors (pure, testable) |
| `CRV.Core/Strategy/Confirmations/*.cs` | `IEntryConfirmation`, `ConfirmationVote`, the four confirmations, aggregation |
| `CRV.Core/Migrations/<ts>_ConvertRetiredEma21Entries.cs` | Type-4 → disabled `Ema` entries |

### Modified Files

`StrategySetupConfig.cs`, `StrategyConfig.cs` (mapping), `StrategyFactory.cs` (`Ema` case), `Signals.cs` (`SetupSnapshot` EMA state fields), `SnapshotAggregator.cs`, `Pages/Setup/Strategy.cshtml(.cs)`, `Strategies.cshtml(.cs)`, `StrategyText.cs`, `Pages/Dashboard/Index.cshtml`, `wwwroot/css/components.css`, `CRV.Web.A11yTests/A11yPages.cs`, `CockpitSnapshot.cs`, `A11ySeed.cs`.

## Tests

1. Hand-verified EMA fixtures for the signal detectors.
2. Every Source × Entry × Direction combination (Touch only with PriceVsEma) produces the expected signals on a fixture.
3. Touch: exact equality (no signal), gaps through the EMA, the first bar after warmup, consecutive touching bars, re-arm only after `RearmAtr`, a confirmation-rejected touch keeps the arm.
4. CrossRetest: a retest on the cross bar is rejected; a retest after the window expires the setup; a close back across cancels it; `MaxEntriesPerCross` 1 vs 2.
5. Cross: equal values are not a cross; `HoldBars` 1 vs 3; `MinCloseBeyondAtr` / `MinSeparationAtr`.
6. No lookahead: a signal never fills on its own bar, in live and in backtest.
7. Validation: Touch with EmaVsEma, Fast ≥ Slow, a signal timeframe that is not a multiple of the bar size — each blocks save and disables only that entry at engine start.
8. Direction sets `AllowLong` / `AllowShort` for Up, Down and Both.
9. Confirmations: each one's vote; All vs at least N; inactive counts as a fail; N clamping.
10. Signal timeframes at session and H8 boundaries, weekly and monthly rollovers, DST.
11. Migration: a type-4 entry becomes a disabled `Ema` entry with the mapped fields; other entries untouched.
12. WCAG: the setup page in each Source × Entry layout, the EMA cockpit card states and the disabled card pass the gate.
