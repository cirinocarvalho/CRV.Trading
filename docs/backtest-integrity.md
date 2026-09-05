# Backtest Integrity

Three properties a backtest has to have before its number means anything: it must
reproduce itself, it must report the tail honestly, and it must charge for execution.
This system had none of them. What follows is what was wrong and what now holds.

## 1. A run is reproducible

**Was:** every run refetched bars from the broker, and any chunk that failed to
arrive was skipped with a log line. Runs 920–922 shared a configuration and a date
window and disagreed by **24.6%** on net. A backtest that cannot reproduce itself
cannot validate anything, so nothing downstream of this was trustworthy either.

**Now:**

- `BarSnapshotStore` (`CRV.Backtest/DataLoaders/BarSnapshotStore.cs`) writes the
  merged, chronologically-ordered bar stream to
  `CRV.Web/Data/bar-snapshots/<key>.csv`, where `<key>` is a SHA-256 of the data
  source, date range, execution TF and the sorted ticker set. A later run with the
  same inputs replays that file instead of going back to the broker.
- The snapshot is written to a `.partial` path and moved into place only when the
  source stream completes, so a run killed halfway cannot leave a truncated file to
  be replayed later as though it were whole.
- The capture happens on the **raw** merged stream, before the anomaly filter, so
  the filter can be changed and re-run against fixed data.
- `SchwabHistoricalLoader` and `TradeStationHistoricalLoader` now throw
  `BarLoadException` when a chunk fails, rather than continuing with a hole.
- **A request that succeeds and returns nothing is also a failure.** Schwab serves no
  minute history for expired futures contracts, and says so with HTTP 200 and
  `{"empty":true,"candles":[]}` rather than an error. That sailed past the
  failed-chunk guard: the loader yielded zero bars without complaint, and a backtest
  over such a window would run on no data and report "0 trades" as a result. A range
  that yields no bars at all now throws. Individual empty chunks are still fine — a
  holiday week legitimately returns nothing.
- Bars dropped by the >10% intra-session jump filter are counted and reported as one
  warning per ticker — `N of M bars dropped as anomalous` — instead of only scrolling
  past as individual lines.

`BacktestRunnerService.LastSnapshotKey` and `.LastRunWasReplay` say which bars
produced the most recent result.

**To force a refetch:** delete the snapshot file. Its path is in the run's log line.

**Pinned by:** `BacktestDeterminismTests` — one config, one bar series, ten runs,
identical trade fingerprints — plus `BarSnapshotStoreTests` and
`LoaderFailsLoudTests`.

## 2. Tail figures are numeric

See *Numeric storage* in [architecture.md](architecture.md). `decimal` columns are
`REAL`, not `TEXT`. Applied by the `StoreDecimalsAsReal` migration, which rebuilds
the affected tables; SQLite's REAL affinity converts the existing text values in
place. Verified against the 176-trade live book: row count and total P&L unchanged,
`MIN(RMultiple)` corrected from `-0.07` to `-4.32`.

## 3. Execution is charged for

**Was:** `FillMode` defaulted to `AtTouch`. Exits filled at their exact order price,
so a stop at 18000 filled at 18000 and every stop-out cost precisely 1R. Live, **20
of 123 stop-outs (16%) exceeded 1R** and the worst reached **-4.32R** — a stop is a
market order once touched, and it is touched in a market already moving away.
Modelling that as free flatters exactly the strategies that stop out most.

**Now:** `ExecutionModel` (`CRV.Backtest/Engine/ExecutionModel.cs`) prices fills, and
`FillMode` defaults to `WithSlippage`.

| Fill | Treatment |
|------|-----------|
| Limit entry | At the limit — a limit never fills worse than its price |
| Market entry | Adverse by `SlippageTicks` (default 1) |
| Target (limit) | At the limit |
| **Stop** | **Adverse by `StopSlippageTicks` (default 4)** |
| Market exit | Adverse by `SlippageTicks` |

Slippage is priced in **that instrument's** tick via `StrategyConfig.TickSizeFor`.
MCL ticks at 0.01 and MNQ at 0.25; charging the global tick would have overstated
MCL's stop cost twenty-five-fold.

`AtTouch` and `AtClose` remain, and remain frictionless. Use them to compare one run
against another, not to judge whether a setup is tradeable.

**Commission** is `StrategyConfig.CommissionPerSide`, currently $0.95 — inside the
$0.83–0.95 actually charged. An older config row booked $0.59; only the latest
`Config` row survives, so old backtests cannot be re-costed.

**Pinned by:** `ExecutionModelTests`, `PerInstrumentSlippageTests`, and
`StopSlippageReachesTheEngineTests`, which runs a stop-out session under both modes
and asserts the shipped default books it $2 worse on a one-contract MNQ stop.

## What is still not modelled

Stated so it is not mistaken for solved:

- **Intrabar ordering is a heuristic.** `BacktestEngine` feeds O→L→H→C on bullish
  bars and O→H→L→C on bearish. When one bar spans both stop and target, the outcome
  is decided by the bar's direction rather than by what actually happened first —
  conservative for longs on up bars, optimistic on down bars.
- **Resting limits always fill.** A limit the backtest fills at its price may never
  have been reached live.
- **No queue position, no partial-fill risk, no gap-through-stop beyond the fixed
  tick charge.**
- **No out-of-sample split and no walk-forward.** Reproducibility is a precondition
  for those, not a substitute. (Since added — see [validation.md](validation.md).)

## Historical data has an expiry

Schwab serves minute bars only for **live** futures contracts. Once a contract expires
its history is gone from the API — not thinned, gone: a request for a window when that
contract was the active front month returns `empty: true`.

Verified 2026-09-05 against every contract the live book traded:

| Symbol | Window | Candles |
|---|---|---|
| `/MNQZ26` (current) | Sep 2-4 | 3,000 |
| `/MNQU26` | Aug 20-22 | 2,640 |
| `/MNQM26` | **May 20-22, when it was the front month** | **0** |
| `/MNQM26`, `/MESM26`, `/MGCM26`, `/MCLK26`, `/MYMM26` | Mar 30 - Apr 1 | **0** |

The consequence is structural: **a live trading window can only be re-examined if its
bars were captured while the contract was alive.** That is what the snapshot store is
for, and it is why capture happens on every API-sourced run rather than on request.
The March-April 2026 window predates the snapshot store, so those bars are
unrecoverable and that period can never be reproduced in backtest.

It also explains part of the historical variance: as a contract approached and passed
expiry, refetches of the same window returned progressively less data, silently.
