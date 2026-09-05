# Options

Stock, ETF and index options research and trading, at `/options/explorer`. Independent of
the futures strategy engine — it shares no code with `ComposableEngine`, `TickerGroup` or
`BrokerEventHandler`, and changes nothing about how futures trade.

Schwab only. The chain, order payloads and preview flow are all verified against the live
Schwab API.

## Layout

The page is four stacked panels, each usable on its own:

| Panel | Purpose |
|-------|---------|
| Open option positions | Current option holdings with cost basis and unrealised P&L; multi-select close |
| Working at the broker | Every non-terminal option order at Schwab, **including ones this app never placed** |
| Submitted structures | Local record of what this app submitted, with live status joined from the broker |
| Chain → Structure → Ticket | Browse the chain, build a structure, price it, preview and place |

## Core types (`CRV.Core/Options`)

Pure, broker-free and unit tested.

| Type | Purpose |
|------|---------|
| `PayoffCalculator` | Expiration payoff, max profit/loss and the underlying prices they occur at, breakevens, per-contract commission, and `Curve()` for plotting |
| `OptionContract` | One contract, with `Mid`, `SpreadPct`, `HasBid`, `CommissionPctOfPremium` |
| `LiquidityGate` | Admission test for tradeability |
| `OptionChainParser` | Parses Schwab's nested `callExpDateMap` → `"2026-08-28:2"` → strike → array |
| `StructureFinder` | Builds and ranks the structures that fit a stated view |

`PayoffCalculator.Curve()` injects every strike and breakeven as an explicit sample point.
A uniform grid steps over a butterfly's peak and draws a blunted tent that understates max
profit.

## Liquidity: open interest is not a filter

Measured on a live SPY chain of 3,610 contracts: 610 were quoted at or below \$0.02. Every
one of them had a spread of 200% of mid, 62% had **no bid at all**, and their open interest
ran as high as 10,717.

An open-interest floor admits all of them. `LiquidityGate` therefore gates on **`bid > 0`
plus spread as a percentage of mid**; open interest is shown as a column but is not the
primary test.

## Order construction (`CRV.Live/Brokers/Schwab/SchwabOptionOrder`)

**A spread is always one net-priced order.** There is no per-leg submission path, because a
partial fill on a legged-in spread leaves a naked short option in place of a defined-risk
structure.

**Leg quantity is a count, not part of the price.** Schwab multiplies the quoted price by
the leg quantities it is sent, so folding quantity into the net price bills the order twice
over — three puts at 3.17 quoted as 9.51 previews at \$2,853 against a real cost of \$951.
`UnitFactor` divides the leg quantities by their GCD, so three identical puts is one put
traded three times while a 1:2:1 butterfly stays one structure priced as a whole.

**Symbols are carried verbatim.** The OSI string from the chain response is passed through
untouched into the order. A rebuilt symbol can address a valid but entirely different
contract.

**Never a market order.** Single legs price as `LIMIT`, multi-leg as `NET_DEBIT` /
`NET_CREDIT` / `NET_ZERO`. A test asserts no payload can contain `MARKET`.

Supported on entry: a custom limit price (the order type follows the sign of the limit, not
the market), an attached take-profit, and an attached stop.

### Brackets

| Attached | Structure | Schwab reports |
|----------|-----------|----------------|
| Exit only | entry triggers the closing limit | `OTO` |
| Stop only | entry triggers the stop-limit | `OTO` |
| Both | entry triggers an **OCO** pair | `OTOCO` |

Both together must be an OCO. Otherwise each can fill: the position closes twice, the second
time opening a new one in the opposite direction.

**Stops are single-leg only.** Schwab prices a spread as a net debit or credit and neither is
a stop order type — there is no net-stop for a multi-leg structure, and attempting one is
rejected with *"Stop price must be populated only for stop orders."* Rather than silently
omitting the stop, the ticket disables the field and the dialog says why. A defined-risk
spread already has a bounded worst case; that bound is the stop.

**Stops are emitted as `STOP_LIMIT`, never `STOP`.** A plain stop becomes a market order the
instant it triggers, and on an options book that is precisely when the spread is widest. The
limit is placed 10% through the trigger so a triggered stop can actually fill — a stop-limit
priced at the trigger frequently does not, which is the failure mode that leaves someone
believing they were protected. This is a real trade-off, not a free improvement: a stop-limit
can miss entirely in a fast move, and the dialog says so.

## Safety model

| Guard | Behaviour |
|-------|-----------|
| Live placement | Off unless `Options:AllowLiveOrders` is true |
| Risk ceiling | `Options:MaxTradeRisk` re-checked on the **place** call, not only on preview |
| Preview, not Place | The page button previews; Place exists only inside the dialog, after Schwab's `previewOrder` returns |
| Size | Resets to 1 on every structure change; never remembered |
| Stale quotes | Preview always re-quotes every leg first; the dialog locks after 30 seconds |
| Unreachable exits | An exit above the structure's maximum possible value is refused |
| Closing | Multi-select, closed as one order; long legs priced at the bid, short legs at the ask |

## Conditional orders — read, cancel, but not create

Schwab supports triggering an order on the underlying's price; thinkorswim's *Order Rule*
builds one. **The REST API does not expose the condition.**

Reading a thinkorswim-created rule back returns a plain `SINGLE` / `MARKET` order tagged
`API_TOS:SGW:TOSWeb`, whose only trace of conditionality is `status: AWAITING_CONDITION`.
There is no trigger symbol, price or comparator in the payload. `activationPrice` is
accepted by `previewOrder` but reported as `advancedOrderType: NONE` — the same
silently-ignored signature as an invalid stop.

**Build such rules in thinkorswim and manage them here.** They appear in *Working at the
broker* as *Awaiting Condition* and can be cancelled. The condition lives at Schwab, so it
fires whether or not this app is running — better than any in-app watcher, which would
miss the move whenever the process is down.

## Portfolio exposure

A per-trade ceiling does not catch the case that hurts: several individually compliant
positions all pointing the same way. `PortfolioRiskCalculator` marks every open leg to the
market and reports net delta, gamma, theta and vega in dollars, plus premium at risk.

Two judgements are built in:

- **Short legs are excluded from "premium at risk"**, not netted into it. Their loss is not
  bounded by anything derivable from a leg alone, and netting would understate exposure.
- **Unpaired shorts are detected per underlying, expiry and right.** A long option caps a
  short one of the same right and cycle whatever the strikes, because past the outer strike
  the two move one-for-one — but a long put does nothing about a short call, and a later
  expiry does not protect through this one.

`Options:MaxPortfolioRisk` is checked on both preview and place, and **fails closed**: if
exposure cannot be established the order is refused, because a risk gate that opens on
error is not a gate.

## Early assignment

Short **American** legs can be assigned at any time, which turns the leg into stock and the
structure's defined risk with it. The confirm dialog names how many such legs an order
carries. Index options are European and cannot do this — one of the few places where the
index products are structurally safer.

> **Not covered: ex-dividend.** The chain reports a dividend yield but not ex-dividend
> dates, which is when assignment on a short call is most likely. That gap is real and
> unaddressed.

## Expected move

`OptionChain.ExpectedMove` returns the at-the-money straddle price — what the options are
charging for the move by that expiry. It is shown beside the chain and used to qualify a
stated target: `1.40× expected move` means the target sits beyond what the market is
pricing, `0.34×` means it is already paid for.

The at-the-money pair is chosen by `OptionChain.AtTheMoneyPair`: nearest spot, quoted on
both sides, **and from the same series**. Index expirations list more than one series at a
strike — SPX settles in the morning, SPXW in the afternoon — so pairing on strike alone
both collides and risks quoting a straddle across two different products. Ties on distance
break to the tighter combined spread. Null when no such pair exists, since zero would read
as "the market expects nothing to happen".

## Implied volatility history

`OptionChainSnapshotService` records one at-the-money IV reading per underlying per expiry
per session, into `OptionChainSnapshots`. It exists because **IV history cannot be bought
back**: the chain reports what IV is now and nothing about what it has been, so "is this
expensive?" stays unanswerable until readings accumulate. Starting costs a few kilobytes a
day; not starting costs however long you wait.

Configure with `Options:SnapshotSymbols` and `Options:SnapshotHourEastern`. Deliberately
small — the at-the-money IV and straddle per expiry, not the chain. Full chains would be
the only route to backtesting structures later, but at ~4 MB per symbol per capture that
is a separate decision with a real storage cost.

`IvStatistics.Standing` reports both **rank** (position between the window's low and high)
and **percentile** (share of sessions below today). Reporting both matters: one volatility
spike dominates a range for a year, so rank alone can call an elevated reading cheap while
percentile still calls it high.

Two things that are easy to get wrong and are handled:

- **Ranked against a constant-maturity series** — one reading per session, the expiry
  nearest 30 days — not against every expiry recorded. A single session records dozens of
  expiries, and ranking today's number against today's *term structure* yields a
  confident-looking figure that means nothing.
- **Silent below 20 sessions.** A rank from a handful of readings is noise wearing the
  costume of a statistic. The strip says how many sessions exist instead.

## Expiration payoff is labelled as such

`PayoffCalculator` computes payoff **at expiration**. Max profit, max loss, breakevens, the
chart and the structure finder's price columns are all settlement values — what a structure
is worth if the underlying *settles* there.

Reaching that price earlier pays differently, because the contract still holds time value,
and most option positions are closed before expiry. Every such figure therefore carries an
`at expiry` tag, the note names the actual expiry date, and `Net` is tagged `now` because it
is the one price you can transact at today.

## Value before expiration

`PayoffCalculator.ValueAt` answers "what if it reaches X **on date D**" — the question the
expiration figure cannot. It is deliberately parallel to `PayoffAt`; the only difference is
that a leg is worth its Black-Scholes value rather than its intrinsic value, so as the date
approaches expiry the two converge. A test pins that convergence to the cent, which is what
makes the settlement number a special case rather than a separate quantity.

The gap is not small. A long SPY 770 call bought at 6.43 with the underlying unchanged at
770.19:

| | P&L |
|---|---|
| 7 days before expiry | −$97.15 |
| At expiry, same price | −$624.65 |

Six times worse for an identical underlying price, purely from time value.

**Three volatility scenarios are shown, never one.** Implied volatility does not hold still
— it typically falls as equities rise and collapses after events — so a single figure would
be the same false precision the expiration payoff already invites. The same position above
spans −$309 to +$115 across ±5 volatility points, which is a larger swing than the price
move being contemplated.

The risk-free rate comes from the chain's own `interestRate` rather than an assumed figure.
Valuation assumes European exercise; equity and ETF options are American, so the model
understates a deep in-the-money put and a call before a dividend, and is otherwise the
standard approximation.

## Structure finder

Given a target price, it builds every structure the chain supports for that view, prices
each at the side you would actually trade, and ranks by profit at the target.

Ranking on that number alone always flatters structures that pay only at a precise price,
so each candidate carries a sensitivity row — payoff below, at and above the target. On
live SPY with a +2% target the butterfly showed the best return on risk (211%) and lost
\$451 fifteen points higher, where the long call made \$2,507.

No probability figure is shown, and nothing is labelled "recommended". The ordering is a
function of a target you supply; the columns do the arguing.

## Execution measurement

Slippage is the largest and least visible cost in options, and it cannot be reconstructed
after the fact — the market at the moment you submitted is gone. So it is captured then:

| Recorded | When |
|----------|------|
| `MarketAtSubmitJson` | Two-sided market per leg, snapshotted immediately before submitting |
| `MidNetPrice` | Net per unit at the mid — the execution benchmark |
| `NetPrice` | What was actually asked for |
| `FilledNetPrice` | Realized net per unit, backfilled once the broker reports a fill |

`SlippageVsMid` is signed so positive is always worse: a debit filled above the mid, or a
credit filled below it. `SlippageVsAsked` shows how far the fill landed from the limit.

Without the market snapshot a fill price says nothing — $3.60 is excellent against a
3.55/3.75 market and poor against 3.50/3.60.

The backfill runs only for orders the broker reports as filled that carry no fill yet, so
it is bounded and idempotent. Failing to snapshot the market never blocks an order, and
failing to read a fill never hides the local record.

> **Unverified against a live fill.** No order placed by this app has executed. The parser
> is tested against payloads shaped from Schwab's documented order schema, which pins the
> arithmetic — weighted averaging across partial fills, leg ratios, sign by side, and
> refusing to report a net for a spread filled on only one leg — but does not prove the
> field names match production. The first real fill is also the first test of that.

## Local record

`OptionOrderRecord` (table `OptionOrders`, migration `AddOptionOrders`) keeps what the
broker does not: that these legs were one structure, its name, and its max loss at the
moment it was confirmed. Schwab's order history returns legs; it does not return the fact
that they formed a butterfly with a \$54.60 worst case.

Rows are immutable. **Order status is never mirrored into them** — it is joined from the
broker at read time, so a row cannot claim "working" for an order that has since filled.

## Index options

`$SPX`, `$NDX` and `$RUT` work (bare `SPX` is rejected). They report `isIndex: true`,
`assetMainType: INDEX`, roots `SPXW` / `NDXP` / `RUTW`, and **European exercise** — so
there is no early assignment on short legs.

## Practical limits

- **Chain requests must be bounded.** SPY across three weeks of expiries returns 3,610
  contracts and 4.3 MB. The explorer requests one expiry with a capped strike count.
- **Auth goes stale.** Schwab rotates the refresh token on every grant and expires it after
  seven idle days. `BrokerTokenKeepAlive` touches it every 12 hours while the app runs; a
  banner offers reconnection when it has lapsed.
- **Options cannot be flattened from the Manual page.** Its Flat button posts a `MARKET`
  order tagged `assetType: FUTURE`; options are closed from the explorer instead.
- **Expired expiries are hidden.** Schwab keeps listing an expiry after its contracts stop
  trading, so today's 0DTE is still offered all evening; selecting it only earns a
  "Symbol is expired" rejection. The picker drops anything past the contract's own
  `expirationDate`, which keeps index options right (SPXW trades past the equity close)
  without a hard-coded 4pm rule.
- **No options backtesting.** There is no chain history, so structures can be researched
  and forward-tested but not backtested.
