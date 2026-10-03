# Higher-Timeframe Bars and History Design

## Overview

Build session-anchored higher-timeframe bars (H4, H8, D1, W1, MN1 and the minute timeframes) from a ticker's execution bars, a generic EMA that matches TradingView, and a stored bar history per root filled from Schwab, so the EMA strategy can evaluate signals on closed higher-timeframe bars from its first live bar.

Part of the EMA set (order: ema21-removal → hold-and-close → dollar-targets-and-rr-guard → **htf-bars-and-history** → ema-strategy → ema-parity-and-validation).

Source: kickoff spec 3, sized with the Schwab probe run on 2026-10-02.

## How bars are built today

- Live: `RealTimeBarBuilder` (`CRV.Live/BarBuilders/RealTimeBarBuilder.cs:65-69`) snaps bar starts to minutes since **UTC midnight**, one builder per ticker at `TfMinutesFor` (Schwab, Tradovate and TradeStation feeds).
- Backtest: `BacktestEngine` buckets inline by local date plus minutes-of-day (`:186`, `:324-329`).
- No session-anchored or higher-timeframe aggregation exists. `CRV.Live/BarBuilders/BarAggregator.cs` and `CRV.Backtest/DataLoaders/BarResampler.cs` are never called.
- `"America/New_York"` lookup (`FindTz`, with a Windows-ID fallback) is copied in `Indicators.cs:303`, `SessionEngine.cs:241`, `TickerGroup.cs:1024`.
- `Ema21Indicator` (`Indicators.cs:46-75`) is SMA-seeded; `AtrIndicator` (`:7`) uses Wilder smoothing.
- No bars table exists; backtest bar snapshots are CSV files (`BarSnapshotStore`).

## Execution timeframe and signal timeframe

- **Execution timeframe**: the bar size of the root's feed (one per root, per `ema21-removal`). Anchored to UTC, must divide 60: 1, 2, 5, 10, 15, 20, 30, 60 minutes.
- **Signal timeframe**: built inside the strategy from execution bars. One of M5, M15, M30, H1, H4, H8, D1, W1, MN1, and a whole multiple of the execution timeframe (validated on save).

## `SessionBarAggregator`

Config / `HtfBar` / component / tests, in `CRV.Core/Indicators/`.

Buckets, in Eastern time (the shared time-zone helper below):

| Timeframe | Buckets |
|---|---|
| M5 … H1 | Clock-aligned within the session |
| H4 | Opens 18:00, 22:00, 02:00, 06:00, 10:00, 14:00; the 14:00 bar ends at 17:00. Verified against TradingView CME_MINI:NQ1!. |
| H8 | 18:00, 02:00, 10:00–17:00 (7 hours). Verified on a TradingView chart before parity work. |
| D1 | Trading date, 18:00 to 17:00 (`StrategyConfig.TradingDate()` rule) |
| W1 | Sunday 18:00 to Friday 17:00 |
| MN1 | By trading-date month |

- A bar closes as soon as the execution bar ending on its boundary is processed.
- On a holiday or early close the last bucket closes on the first execution bar of the next bucket: late, never early, never with data from the future.
- DST changes over a weekend, so no bucket straddles it; it is tested anyway.

The aggregator replaces what `BarAggregator` and `BarResampler` were meant to do; both are deleted.

## `EmaIndicator(period)`

Generic, SMA-seeded (the first value is the SMA of the first `period` closes), matching Pine `ta.ema`. Until `period` values have arrived it reports not ready. `Ema21Indicator` is left as it is.

## Shared Eastern-time helper

`EasternTime` (one place) owns the zone lookup with its Windows-ID fallback. `Indicators.cs`, `SessionEngine.cs`, `TickerGroup.cs` and the aggregator use it; the three copies go.

## History

### Storage

New table `HtfBars`: `Root` (e.g. `NQ`), `Timeframe`, `OpenTime` (UTC), `Open`, `High`, `Low`, `Close`, `Volume`; unique on (`Root`, `Timeframe`, `OpenTime`); decimals stored as REAL like every other price column. Unadjusted and joined across contracts (TradingView NQ1! default). One history per root, matching the per-root bar size.

### Sources (from the probe)

| Timeframe | Built from | Depth available |
|---|---|---|
| D1 | Schwab `pricehistory` daily | 5,093 bars, 2006-10-03 → today |
| W1, MN1 | Our own D1 (not Schwab's weekly/monthly), so they follow our session anchoring | Same 20 years: ~1,040 weeks, ~239 months |
| M30, H1, H4, H8 | Schwab 30-minute | ~8.5 months (2026-01-15 → today) |
| M5, M15 | Schwab 1-minute | Each request caps at 40,000 bars (~5.5 weeks); the backfill pages through date windows, as `SchwabHistoricalLoader` already chunks by month |

- `/NQ` and `/NQZ26` return the same continuous series; expired contracts return no minute history (`DataLoaders.cs:88-93`, confirmed: `/NQU26` empty).
- **Top-ups:** every closed live execution bar extends the stored minute history and closes higher-timeframe bars into `HtfBars`.
- **CSV import:** a CSV (TradingView export columns) goes into the same table, for depth Schwab no longer serves.

### Two checks before the data is trusted

1. **Daily alignment.** Schwab stamps daily bars at 05:00 UTC (midnight ET). For ten days spread across a year, our D1 must match TradingView's NQ1! daily OHLC (the 18:00–17:00 session).
2. **Adjustment.** Whether Schwab's continuous series is back-adjusted at rolls is unknown. Cirino spot-checks a 2010 daily close against TradingView NQ1! unadjusted; `ema-parity-and-validation` depends on the answer.

### Enabling rules

From stored counts, not hard-coded cases:

- A strategy can be switched on once stored bars for its signal timeframe ≥ its EMA period (the slow EMA for two-EMA setups, and the trend confirmation's EMA on its own timeframe).
- Below 3 × the period the History panel warns that the EMA will not match TradingView yet.
- With the probe numbers: MN1 × EMA 200 (239 bars) can be switched on, with the parity warning; W1 × EMA 200 (1,040+ bars) needs no warning.

### History panel

On the setup page (as in the mockup): bars stored vs needed for the signal timeframe and for each confirmation's timeframe, a meter, and the note: "Ready." / "{n} more bars to load before this strategy can trade." / "The EMA matches TradingView once 3× its length is loaded." Source line: "Schwab price history (NQ, contracts joined, unadjusted) · last filled {time} ET".

## File Structure

### New Files

| File | Responsibility |
|---|---|
| `CRV.Core/Indicators/SessionBarAggregator.cs` | Config, `HtfBar`, aggregation |
| `CRV.Core/Indicators/EmaIndicator.cs` | Generic SMA-seeded EMA |
| `CRV.Core/Indicators/EasternTime.cs` | Shared ET zone helper |
| `CRV.Core/Models/HtfBarRow.cs` + migration | `HtfBars` table |
| `CRV.Backtest/DataLoaders/SchwabHistoryBackfill.cs` | Paged daily / 30-min / 1-min backfill into `HtfBars` |
| `CRV.Backtest/DataLoaders/HtfCsvImport.cs` | CSV import |
| `CRV.Core/Strategy/HistoryRequirement.cs` | Bars needed vs stored, enable and parity rules |

### Deleted Files

| File |
|---|
| `CRV.Live/BarBuilders/BarAggregator.cs` |
| `CRV.Backtest/DataLoaders/BarResampler.cs` |

### Modified Files

`Indicators.cs`, `SessionEngine.cs`, `TickerGroup.cs` (use `EasternTime`); the live bar path (top-ups); `TradingDbContext.cs` (`HtfBars`).

## Tests

1. Bucket boundaries for every timeframe on hand-made bars: H4 including the short 14:00 bar, H8 including the 7-hour last bar, D1 at 18:00, W1 Sunday → Friday, MN1 at a month change that falls on a Sunday evening.
2. A bucket closes on the execution bar ending at its boundary, never before; a holiday or early close closes it late on the next bucket's first bar.
3. The spring and autumn DST weekends.
4. `EmaIndicator` matches hand-verified values, including SMA seeding and not-ready before `period` bars.
5. W1 and MN1 built from D1 match a hand-checked fixture.
6. The 1-minute backfill pages through the 40,000-bar cap without gaps or duplicates; re-running it is idempotent (unique key).
7. Enabling rules at the boundaries: `period − 1` blocked, `period` allowed, below `3 × period` warns.
8. A search finds no remaining `FindTz` copies and no references to the deleted aggregators.
