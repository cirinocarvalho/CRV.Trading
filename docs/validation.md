# Validation

Three questions the system could not previously answer about itself: does a result
survive out of sample, is a parameter a stable choice, and does any filter beat the
strategy without it. `/validation` answers them.

All three replay the bar snapshot a backtest already captured. Sweeping across bars
that differ per variant measures the data, not the parameter — so the page refuses
to run until a snapshot for that window exists rather than fetching one per variant.
See [Backtest Integrity](backtest-integrity.md).

## The inference layer

`PerformanceMetrics` reports `Expectancy` with no standard error. Over the live book
that reads "+$228, expectancy +$1" — a small win. The same 176 trades are -0.0029R
with a 95% interval of [-0.178, +0.172] and t = -0.03: the edge, if any, is smaller
than the measurement. Every study on this page runs on `EdgeTest` instead.

`CRV.Core/Statistics/EdgeTest.cs` reports mean R, sample standard deviation, standard
error, t, a 95% interval from `StudentT`, and one of three verdicts:

| Verdict | Meaning |
|---|---|
| `InsufficientEvidence` | Under 20 trades. **Not** the same as no edge |
| `NoMeasurableEdge` | Enough trades to look; the interval straddles zero |
| `EdgePresent` | The interval excludes zero. The sign says which way |

The interval uses Student's t, not 1.96. The cells that looked promising in the live
book hold 13–16 trades, and at that size the normal value understates the interval by
about a fifth — the difference between "no evidence" and "edge".

`TradesNeededForSignificance` gives the sample the claim would actually require. For
the live book that is roughly 674,000.

Two deliberate refusals:

- **Zero variance is not infinite confidence.** Every trade returning exactly the same
  R is a modelling artefact, not a perfect edge — under the pre-P0 frictionless fill
  model every stop-out came back at precisely -1.000R. There is no interval to compute
  and the verdict is `InsufficientEvidence`.
- **Two variants cannot be compared when either is under-sampled.** `EdgeTest.Differ`
  returns false rather than confidently ranking 14 trades above 200.

## In-sample / out-of-sample

Nothing in this system had ever been validated on data it was not chosen against. The
promising subset of the live book — retest, NY, MNQ/MCL, long — was identified by
reading the results of those same trades. That is a hypothesis, not evidence.

`SampleSplit` splits **chronologically**, 70/30 by default with a one-day embargo.
Random sampling from a time series lets the future inform the past; the embargo drops
trades straddling the boundary, so a position opened in-sample cannot carry its
outcome across.

`FailedOutOfSample` is true only when the in-sample side showed an edge and the
out-of-sample side, on an adequate sample, kept less than half of it. An under-sampled
out-of-sample side reports insufficient evidence — a test that could not have been
passed was not failed.

> Choosing a parameter against the out-of-sample side spends it. Those trades are
> in-sample from that moment on, and the next honest test needs data neither side has
> seen.

## Parameter sweeps

The 30-minute opening range, and the per-instrument variants (MGC 08:20, MCL 09:00),
were chosen rather than validated — no neighbouring duration was ever tested.

`ParameterSurface` reads a sweep for **stability, not for a winner**. The best cell in
a table is positive by construction: give it five cells of noise and one will look
excellent. A `StableRegion` is the longest run of at least three adjacent cells that
each show a positive edge in their own right and none of which is statistically
distinguishable from its neighbour. `Recommended` is the *centre* of that region — the
value furthest from where the result falls away — not its peak.

No region means no recommendation, however good the best cell looks. A peak whose
neighbours disagree is what an artefact looks like.

`IsInert` catches the opposite failure: when every cell scores identically the
parameter never reached the strategy, and six identical rows would otherwise read as
strong agreement. Sweeping ORB duration against a setup that fades the previous
session's range is inert — that setup does not read the opening range at all.

## Filter ablation

The engine stacks VWAP, ATR, chop, EMA and ORB-close filters on the raw opening-range
break, and not one had been measured against that break alone.

`AblateAsync` runs the baseline with **every** filter off, then the baseline plus each
filter on its own — so what is measured is that filter's own contribution, not its
contribution given whatever else happened to be enabled.

| Verdict | Meaning |
|---|---|
| `Earns` | Measurably better than the baseline |
| `Harms` | Measurably worse |
| `NoMeasurableEffect` | Inside the noise. Complexity with nothing behind it |
| `InsufficientEvidence` | Too few trades either side, often because the filter blocks nearly everything |

`PassRate` is reported because a filter that blocks most trades is not free: a smaller
sample is a worse measurement, so a filter has to pay for the trades it removes.

`Candidates` — the filters worth deleting — includes those that harm and those that do
nothing measurable, but never the under-sampled ones. Those are unmeasured, not useless.

## Reading a study honestly

- `InsufficientEvidence` is the most common verdict on this data, and it is a real
  answer. It is not a softer way of saying no.
- A verdict of `EdgePresent` with a negative mean is a finding, not a bug.
- Running the same study repeatedly on the same snapshot until one comes out well is
  the same overfitting the split exists to prevent, done by hand.

## Still missing

- **No walk-forward.** A single split tests one boundary; a rolling re-fit tests
  whether the choice survives being made repeatedly.
- **No Monte Carlo / bootstrap** on the trade sequence, so drawdown has no interval.
- **Sweeps are one-dimensional.** Interactions between parameters are not explored,
  and multiple-comparison error is not corrected for — sweep enough parameters and
  something will look significant.
