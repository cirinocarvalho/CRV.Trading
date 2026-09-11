# Risk Model

Three gates, where there used to be two.

| Gate | Scope | Setting |
|---|---|---|
| Per-trade cap | One entry | `MaxTradeRisk` + `AutoSizeByRisk`, per basket entry |
| **Portfolio ceiling** | **Everything open at once** | **`MaxPortfolioRisk`, global. 0 disables** |
| Daily loss limit | One session's realised P&L | `MaxDailyLoss`, `DailyLossMode` |

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
  slot. A portfolio block is temporary — it lifts when a position closes — unlike a
  daily-loss breach, which stops the engine outright.

Exposure is read from the live group orders, never from a ledger kept alongside them.
A parallel ledger drifts: an entry that never fills leaves risk booked forever and
quietly stops the book trading.

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

## Arming a setup

`BasketEntry.Enabled` defaults to **false**. An entry whose JSON omits the key is
disarmed.

It used to default to true. In the live config every entry carried `"Enabled": false`
except one that omitted the key — so the only setup trading was the one nobody had
switched on, and its record was a single trade for −$277.80.

`/settings/live` warns when nothing in the basket is armed, because a config with
twelve entries and none enabled looks busy and trades nothing.

## Still missing

- **No correlation model.** The ceiling treats $100 of MNQ risk and $100 of MES risk as
  $200 of exposure. They are more correlated than that, so the ceiling is a floor on
  the true figure, not the figure.
- **No intraday re-check.** Exposure is tested when a signal arrives, not continuously,
  so a position whose stop widens after entry is not re-gated.
- **Sizing suggestions are not applied automatically.** The page reports what would
  level the book; changing it is a decision, not a calculation.
