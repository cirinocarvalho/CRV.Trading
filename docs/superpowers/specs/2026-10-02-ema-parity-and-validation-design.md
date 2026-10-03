# EMA Parity and Validation Design

## Overview

Prove the EMA strategy computes the same EMAs and fires on the same bars as a TradingView reference, then measure whether its signals and each confirmation carry an edge, out of sample, with the existing statistics tooling.

Part of the EMA set (order: ema21-removal → hold-and-close → dollar-targets-and-rr-guard → htf-bars-and-history → ema-strategy → **ema-parity-and-validation**).

Source: kickoff spec 5.

## Parity

### Reference script

A Pine v6 script (`docs/pine/ema-strategy-parity.pine`) that implements every Source × Entry × Direction combination with the same parameters as the C# strategy, using `barstate.isconfirmed`, `request.security(..., lookahead = barmerge.lookahead_off)` and the session-anchored `"240"` / `"480"` timeframes. It plots signal markers and writes the EMA values and signal timestamps to a table or alert log that can be exported.

### Two separate comparisons

Schwab's and TradingView's NQ feeds can differ by a tick here and there; comparing the C# strategy on Schwab bars against Pine on TradingView bars would mix data differences with logic differences. So:

1. **Logic parity.** The C# backtest runs on a **CSV export of TradingView's NQ1! bars** (unadjusted, the same window), imported through `htf-bars-and-history`'s CSV path. Pass: after warmup, every signal timestamp matches and every EMA value is within ±1 tick.
2. **Data parity.** Stored Schwab bars for the same window are compared with that export: per-bar OHLC differences, missing or extra bars. The result is reported, not pass/fail; it tells how far live signals can drift from TradingView's.

Prerequisites from `htf-bars-and-history`: the daily-alignment check and Cirino's adjustment spot-check.

## Validation

Reuse what exists in `CRV.Core/Statistics` and `CRV.Backtest/Experiments`:

- `SampleSplit.ByDate` / `ByFraction` with an embargo (trades between the boundary and boundary + gap are dropped and counted).
- `EdgeTest` (`MinimumSample = 20`, `InsufficientEvidence` below it).
- `Ablation` / `AblationStudy`.
- `ValidationRunner` gains the EMA studies; results appear on the existing `/validation` page (no separate report file).

Studies:

1. **Baseline:** each configured EMA setup with no confirmations, on a time-ordered in-sample / out-of-sample split with an embargo.
2. **One confirmation at a time:** baseline + each confirmation alone, as an ablation against the baseline.

Reported per study: win rate, expectancy in R with a 95 % interval, profit factor, max drawdown, trade count. Fewer than 20 trades is `InsufficientEvidence` (expected for W1 and MN1) and shown as such, not hidden.

Every result states the date range, instrument, bar source (Schwab or TradingView export), and fill and commission assumptions (`BacktestConfig.FillMode`, commission per side). No single backtest is presented as proof of profitability.

## What Cirino does

- Export NQ1! bars from TradingView for the parity window (each timeframe the parity run covers).
- Run the Pine script on a chart and export its signal table.
- The adjustment spot-check (from `htf-bars-and-history`).

## File Structure

### New Files

| File | Responsibility |
|---|---|
| `docs/pine/ema-strategy-parity.pine` | Pine v6 reference |
| `CRV.Backtest/Experiments/EmaParity.cs` | Signal and EMA comparison against an exported signal table |
| `CRV.Backtest/Experiments/BarSourceComparison.cs` | Schwab vs TradingView bar differences |

### Modified Files

`CRV.Backtest/Experiments/ValidationRunner.cs` (EMA studies), `CRV.Web/Pages/Validation/Index.cshtml(.cs)` (EMA results).

## Tests

1. `EmaParity` on a fabricated pair of signal tables: matching tables pass; a shifted timestamp, a missing signal and an EMA off by 2 ticks each fail with a message naming the bar.
2. `BarSourceComparison` reports per-bar differences and missing bars correctly on fixtures.
3. The EMA studies run through `ValidationRunner` on a fixture and report `InsufficientEvidence` below 20 trades.
4. The parity run on a short real TradingView export (checked in as a fixture once Cirino provides it) matches the Pine signals.
