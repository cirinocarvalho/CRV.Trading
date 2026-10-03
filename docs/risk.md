# Risk Model

Three gates, where there used to be two.

| Gate | Scope | Setting |
|---|---|---|
| Per-trade cap | One entry | `MaxTradeRisk` + `AutoSizeByRisk`, per basket entry |
| **Portfolio ceiling** | **Everything open at once** | **`MaxPortfolioRisk`, global. 0 disables** |
| Daily loss limit | Today's realised P&L plus the open loss of positions held in from an earlier day | `MaxDailyLoss`, `DailyLossMode` |

## Portfolio ceiling

The per-trade cap and the daily loss limit say nothing about how much is committed
*right now*. Five setups could be in the market together, each individually within
budget, with nothing looking at the total — and MNQ and MES are not independent
risks. One bad opening drive takes both.

`PortfolioExposure` sums `|entry − stop| × contracts × pointValue` across every group
that is open **or still able to fill**, and refuses an entry that would push the total
past `MaxPortfolioRisk`.

Two decisions worth knowing:

- **Working entries count.** Counting only filled positions is how several resting
  orders all fill on one move and breach a ceiling that was never checked against them
  — exactly the correlated case this exists to catch.
- **A refused signal calls `RevertEntry()`**, so it does not consume the setup's trade
  slot. A portfolio block is temporary — it lifts when a position closes. A daily-loss
  refusal reverts too, since held open loss is marked to market and the breach can clear.
  A daily-loss breach stops new entries; open (and held) positions keep their stops and
  targets.

Exposure is read from the live group orders, never from a ledger kept alongside them.
A parallel ledger drifts: an entry that never fills leaves risk booked forever and
quietly stops the book trading.

## Held positions and the daily limit

A setup with **Close at the end of the session** off holds an open trade past its cutoff and
into the next session. That trade's loss belongs to no earlier day that is still being
checked, so the daily limit counts it:

- realised P&L today, plus
- the open loss of every position opened on an **earlier trading day** (18:00 ET roll),
  marked at the last price.

A held position in profit counts as zero, so it cannot hide a realised loss. One with no
price yet (just after a restart) also counts as zero until the first tick. Positions opened
today are not counted while open, as before. When the limit trips, new entries stop; held
positions are not closed by it and keep their own stops. The dashboard's HALTED state
includes held loss; the loss gauge stays realised.

Pinned by `HeldLossDailyLimitTests` and `RiskManagerTests`.

## Position sizing across instruments

`/validation` shows what each setup actually risks, measured from its fills rather
than its settings — `|Entry − InitialStop| × PointValue × Contracts`, median per setup.

Configuration saying "3 contracts" for several setups reads as equal exposure. It is
not. Stop distance differs by setup, point value differs by instrument, and the micros
span two orders of magnitude — MYM is $0.50 a point, MCL is $100. Across the live book:

| Setup | $/contract | Contracts | $/trade |
|---|---|---|---|
| retest-mgc | $73.00 | 2 | **$146.00** |
| retest-mcl | $60.00 | 2 | $120.00 |
| retest-mnq | $50.50 | 2 | $101.00 |
| retest-mes | $29.38 | 2 | $58.75 |
| pullback-mym | $17.75 | 2 | **$35.50** |

A **4.11× spread**, with the heaviest weighting on retest-mgc — which averaged +0.04R
and still lost $1,249. The signal was breakeven; position size did the damage. No
strategy change fixes that.

The last column suggests contracts that would bring each setup to the book's **median**
risk. Using the book's own middle means levelling moves size between setups without
scaling the account up or shutting it down. A setup too expensive for one contract at
that budget shows `—` rather than rounding up to one, since rounding up is how a budget
gets quietly blown.

Two guards on the reading:

- Setups are grouped by **root symbol**, so a contract roll does not split one setup in
  two. retest-mcl traded MCLK26 and then MCLM26; that is one setup.
- Setups with fewer than three fills are listed with a `*` but do not set the headline
  spread. One trade is not a sizing policy.

## The sizing floor

With `AutoSizeByRisk` on, `Contracts` is a **starting size, not a floor**.

**Was:** `AutoSizeByRiskCalculator` refused any signal whose budget could not carry the
configured contract count — even when it could plainly carry one. That is a selection
rule wearing a sizing rule's clothes, and on the live basket it drew a dead band:

| Setup | Budget | Contracts | Refused when stop > | One contract fits up to | Dead band |
|---|---|---|---|---|---|
| retest-mnq | $210 | 2 | 52.5 pts ($105/ct) | 105 pts | **52.5–105 pts** |
| sessionfakeout-mnq | $280 | 2 | 70 pts ($140/ct) | 140 pts | **70–140 pts** |

A two-fold band of stop distances refused outright, entered on exactly the wide-range
days the setup is built for. Replaying the live retest-mnq record under that rule
skipped 11 wide-stop trades netting +$1,775, including every Target winner ≥ $320, and
turned +$2,406 into +$572 — 97% of the damage was the skip, not the resizing. And the
refusal wrote nothing anywhere: no log line, no counter, nothing on a page. A setup that
traded nothing on a wide-range day was indistinguishable from one that never fired.

**Now:**

- The budget carries the trade at whatever count it affords, **down to one contract**.
  A refusal happens only when a single contract exceeds `MaxTradeRisk`. In the former
  dead band, an AutoSize setup with `PartialCts > 0` trades one contract with no partial
  — a runner only — which is the correct consequence of the floor and a shape of trade
  the book had not had before.
- The fixed-size veto (`AutoSizeByRisk` off, `MaxTradeRisk > 0`) lives in the same
  calculator instead of being duplicated after it in five strategies. One place decides
  whether a trade fits its budget.
- **A refusal is an event.** `SizeRefusal` carries the time, setup, ticker, stop
  distance, dollar risk per contract and the budget. It goes through the same `RISK`
  alert channel as the portfolio ceiling, reaches every event sink (the live sink logs it
  at Warning), and is counted per setup in backtest results and per variant in every
  `/validation` study — a cell that "won" while refusing a fifth of its signals measured
  a different sample from its neighbours.
- **One refusal per signal.** A refused strategy stays armed and asks again on the next
  tick, as it always did; with a stop mode whose stop moves bar to bar, a later ask can
  fit. `SizeRefusalGate` reports a signal once while its direction, entry and stop are
  unchanged, so one signal does not become twenty-two alerts and the count measures
  signals rather than tick frequency.

Pinned by `AutoSizeByRiskTests`, a `BudgetBelowOneContract` test on each of the five
strategies, `SizeRefusalReachesTheResultTests`, and the refusal studies in
`ValidationRunnerTests`.

## Targets and the reward / risk guard

Every strategy sets its target one of two ways on the setup page: a share of the range
(`TargetMode = RangePct`, `TargetPct`) or dollars (`Dollars`, `TargetDollars`), per contract
or for the whole position (`TargetDollarsBasis`). A dollar target becomes points by the
instrument's point value (MNQ $400 a contract is 200 pts, NQ $400 a contract is 20 pts),
and a whole-position target is also divided by the contract count **after** sizing, so
$400 over 2 MNQ contracts is 100 pts. `PartialPct` is a share of that distance.
`SetupValidation` rejects a `Dollars` target of $0 and negative targets.

Each strategy works in this order: entry (tick offset applied), final stop, size,
target and partial, reward / risk from the fill, guard, signal. Targets are measured
from the fill. An `OrbPct` stop is measured from the signal price. A `BarHL` stop sits one
tick beyond the previous bar's low (long) or high (short), and a `Vwap` stop sits
`StopVwapTicks` ticks beyond VWAP. Both fall back to the `OrbPct` stop when they land on
the wrong side of the entry or are wider than it.

**The guard** (`EnforceMinRr`, on by default):

- **On save**, the target must be at least `MinRr` x the strategy's typical stop. For a
  range target that is `MinRr x StopPct` of the range (the stop setting; a bar or VWAP stop
  saves with a warning). For a dollar target it is the median stop of the strategy's last 30
  backtest trades (contracts x stop for a whole-position target); the setup page blocks a
  target below it. Without a backtest the save goes through with "The reward / risk check
  runs once this strategy has a backtest."
- **Per trade**, below `MinRr`: `Skip` drops the trade and records it through the size-refusal
  path, as a distinct `SKIP` alert in the feed (not a `RISK` size refusal) reading
  "Skipped: 1.2R below 1.5R", a warning log line, a line on the cockpit card, and
  `MinRrSkips` in backtest results, counted separately from `SizeRefusals`. `RaiseTarget`
  moves the target out to `MinRr x risk` with the partial recomputed.
- **Off**: no save check and no per-trade check. The strategy shows "R:R not enforced" on
  its setup page, its Strategies row and its cockpit card, and the Strategies page counts
  the strategies that are on with the guard off. Backtest results record each setup's guard,
  shown as a guard row in backtest runs only.

A trade with no reward or no risk (a target or stop on the entry) is never sent, guard on
or off. It is skipped as "Skipped: no reward or no risk".

Pinned by `LevelCalculatorTests`, `MinRrGuardTests`, `MinRrSaveCheckTests`, `TypicalStopTests`,
`SetupValidationTests`, the `TickOffset_*`, `WholePositionDollars_*` and `BelowMinimum_RaiseTarget_*`
tests on each of the four ORB strategies, `MinRrGuardReachesTheResultTests` and
`AddTargetModeAndRrGuardTests`.

## Arming a setup

`BasketEntry.Enabled` defaults to **false**. An entry whose JSON omits the key is
disarmed.

It used to default to true. In the live config every entry carried `"Enabled": false`
except one that omitted the key — so the only setup trading was the one nobody had
switched on, and its record was a single trade for −$277.80.

The Strategies page (`/setup/strategies`) warns when nothing in the basket is armed, because a config with
twelve entries and none enabled looks busy and trades nothing.

## Still missing

- **No correlation model.** The ceiling treats $100 of MNQ risk and $100 of MES risk as
  $200 of exposure. They are more correlated than that, so the ceiling is a floor on
  the true figure, not the figure.
- **No intraday re-check.** Exposure is tested when a signal arrives, not continuously,
  so a position whose stop widens after entry is not re-gated.
- **Sizing suggestions are not applied automatically.** The page reports what would
  level the book; changing it is a decision, not a calculation.
