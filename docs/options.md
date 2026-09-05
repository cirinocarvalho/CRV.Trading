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
the market) and an attached exit — Schwab's One-Triggers-Other, submitted only once the
entry fills.

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

## Expiration payoff is labelled as such

`PayoffCalculator` computes payoff **at expiration**. Max profit, max loss, breakevens, the
chart and the structure finder's price columns are all settlement values — what a structure
is worth if the underlying *settles* there.

Reaching that price earlier pays differently, because the contract still holds time value,
and most option positions are closed before expiry. Every such figure therefore carries an
`at expiry` tag, the note names the actual expiry date, and `Net` is tagged `now` because it
is the one price you can transact at today.

There is no pre-expiry valuation: no Black-Scholes revaluation, no time axis, no vega. A
question of the form "what if it hits X *on date D*" cannot be answered here yet.

## Structure finder

Given a target price, it builds every structure the chain supports for that view, prices
each at the side you would actually trade, and ranks by profit at the target.

Ranking on that number alone always flatters structures that pay only at a precise price,
so each candidate carries a sensitivity row — payoff below, at and above the target. On
live SPY with a +2% target the butterfly showed the best return on risk (211%) and lost
\$451 fifteen points higher, where the long call made \$2,507.

No probability figure is shown, and nothing is labelled "recommended". The ordering is a
function of a target you supply; the columns do the arguing.

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
