# EMA strategy — kickoff prompt

Hand this file to a Claude Code session with the Superpowers plugin enabled:

> Follow docs/superpowers/2026-10-02-ema-strategy-kickoff.md

Before starting, run the Schwab probe in the appendix and paste its output into the
"Schwab probe output" line at the end of the prompt.

---

## Prompt

Use superpowers:brainstorming, then superpowers:writing-plans, for CRV.Trading. Write **5 spec + plan pairs** in `docs/superpowers/specs/` and `docs/superpowers/plans/`, following the existing format (e.g. `2026-04-07-ema21-strategy-design.md` / `2026-04-07-ema21-strategy.md`). Do not write code until I approve the plans.

**Ground rules**
- Strategies emit `EntrySignal`; `BrokerEventHandler` owns the trade lifecycle.
- The same executor interface serves live, paper, backtest and Tradovate Replay. No special-casing.
- Config → Result → Component → Tests per component; interface-driven; nullable enabled; file-scoped namespaces; existing naming.
- Signals are evaluated on **closed** bars only, higher-timeframe bars included. No lookahead.
- Sizing stays where it is today: the strategy calls `AutoSizeByRiskCalculator` and fills `TotalContracts`.

**Target UI (interactive mockup, desktop + mobile, dark + light):** https://claude.ai/artifact/JYo4TCHwPPu8CvEPUgxDFj. Its screens are the strategy setup page, the Strategies list and the Cockpit. It uses the site's own `tokens.css` / `shell.css` / `components.css`; match it. Signal-logic illustrations: https://claude.ai/artifact/MrrKaEXBEtbBMMdgYH4ziB.

**Specs, in order 1 → 2 → 3 → 4 → 5** (spec 2 is independent and can ship on its own):

### 1. `ema21-removal-and-hold-fix`
- **Remove EMA21:** delete `CRV.Core/Strategy/Ema21Strategy.cs` and `CRV.Core.Tests/Strategy/Ema21StrategyTests.cs`.
  - Give `StrategyType` explicit numbers: Pullback=0, Retest=1, OrbFakeout=2, SessionFakeout=3, **4 reserved (retired Ema21)**, **Ema=5**. Basket JSON stores enums as ints.
  - Remove the factory case and the EMA21-only fields (`SlopeLen`, `AtrTouchMult`, `MinSlopePct`, `OpenTicksToEma`, `UseVolumeFilter`). Keep `AtrTp1Mult` / `AtrTp2Mult`.
  - Update every reference: `StrategyFactory`, `StrategyConfig` (`Ema21BasketJson`, `ToEma21SetupConfigs`, `EnumerateBasketEntries`, the mapping at ~776), `StrategyBasketService` (`IsEma21` → `IsEmaBasket`), `StrategyConfigService`, `Program.cs:254`, `LiveEngineOrchestrator:1464`, `TickerAutoRoller`, `Setup/Strategy(.cshtml/.cs)`, `Strategies(.cshtml/.cs)`, `StrategyText`.
  - Mark the EMA21 superpowers docs as superseded.
  - **Keep** the shared `Ema21Indicator`, `IndicatorState.Ema21`, the dashboard EMA21 overlay and `UseEmaFilter`.
- **Migration:** rename the column `Ema21BasketJson` to `EmaBasketJson`. Convert old type-4 entries to **disabled** `Ema` entries (Source=PriceVsEma, Entry=Touch, EMA 21, same bar size) with "(migrated from EMA21)" added to the label. Historical `TradeRecord`s stay as they are (only `SetupLabel` strings). Old `BacktestRuns` must load as read-only legacy and must not crash.
- **Make `CloseAtRthClose` work:**
  - Today it's stored and mapped but ignored: `TickerGroup.cs:276-297, 403-410` and `BacktestEngine.cs:240` always flatten.
  - Expose it on `ISetupStrategy`. When it's off, the cutoff blocks new entries but an open position is held across sessions. The backtest session-end force-exit skips strategies that hold. `ResetSession` / `Reset` must not clear `InTrade` for a held position. Daily P&L counts on the day the trade closes.
  - The **migration sets `CloseAtRthClose = true`** on every existing basket entry and legacy setup, so behaviour is unchanged until the user unticks it.
- **Bar size is per ticker (option A):** reject strategies on the same ticker with different `ExecutionTFMinutes`, and extend that validation to the EMA basket. Label the field "shared by every <root> strategy".
- **Invalid config:** web save is blocked. At engine start, only that strategy is disabled: log it, skip it, and show "Disabled: <reason>" on the cockpit card and setup page. The rest of the basket keeps trading.
- **Tests:** a full-grep check for zero leftover EMA21-strategy references outside migrations; migration round-trip; the hold-vs-close matrix in live and backtest; validation for mismatched bar sizes on one ticker.

### 2. `dollar-targets-and-rr-guard` (all strategies: Pullback, Retest, OrbFakeout, SessionFakeout, Ema)
- **Config fields** (in `StrategySetupConfig`, `BasketEntry.Config`, `StrategyConfig.ToSetupConfig` and the legacy A–D builders):
  - `TargetMode { RangePct (default, today's behaviour), Dollars }`; the EMA strategy also has Atr and RiskMultiple
  - `TargetDollars`
  - `TargetDollarsBasis { PerContract (default), WholePosition }`
- **Distance in points:**
  - PerContract: `TargetDollars / PointValue`
  - WholePosition: `TargetDollars / (PointValue × TotalContracts)`, using the contract count after auto-size. Sizing must run before the levels are final.
  - Round to the tick. Examples: MNQ $400 per contract = 200 pts; MNQ $400 whole position with 2 contracts = 100 pts; NQ $400 per contract = 20 pts.
- **Partial:** reuse `PartialPct` ("% of the way"): $400 with 50% puts the partial at $200 of the move.
- **Setup page:** show the points, the partial in $, "all contracts at target" and "partial, then the rest at target" (for WholePosition with 2 contracts: $400 and $300).
- **Code:** add one `LevelCalculator` overload and use it at every `CalcLevels` / `CalcLevelsB` call site (`PullbackStrategy:343`, `RetestStrategy:530`, `OrbFakeoutStrategy:262`, `SessionFakeoutStrategy:273`) and in the EMA strategy. Stops are unchanged.
- **Min-R guard:**
  - `EnforceMinRr` (bool, per strategy, default **true**, and the migration sets it true).
  - When on, **save is blocked** if the target is below `MinRr` × the strategy's **typical stop**. The typical stop is the median stop of its last 30 backtest signals; with no history, estimate it from the stop mode and current ATR. The error names the minimum $, %, or R needed.
  - Per trade: `MinRrAction { Skip (default, today's behaviour), RaiseTarget }`. RaiseTarget moves that trade's target out to MinRr × risk, with the partial recomputed at `PartialPct`.
  - When off: no save check and no per-trade check. MinRr and MinRrAction stay saved but are shown greyed out as "Not enforced".
- **Visibility:** an "R:R not enforced" badge (amber when the strategy is on, grey when off) on the setup page head, the Strategies row and the cockpit card. A warning note on the Strategies page counts the strategies that are on with the guard off. Backtest results record the guard state. `StrategyText` says "Takes trades below X R".
- **Strategies list:** row descriptions include the target, e.g. "target $400 / contract", "$150 / position", "2R", "100% of range".
- **Tests:** long/short; tick rounding; `PartialPct`; NQ vs MNQ; both bases; WholePosition with 1, 2 and 4 auto-sized contracts; a 1-contract trade with a partial (follow `ResolveBrackets`); the save guard at the boundary with and without backtest history; Skip vs RaiseTarget; guard off; migration defaults.

### 3. `htf-bars-and-history`
- **Execution TF vs signal TF:**
  - The execution TF is the ticker's bar feed. It anchors to UTC and must divide 60 (1, 2, 5, 10, 15, 20, 30, 60).
  - The signal TF is built **inside the strategy** from the execution bars and must be a whole multiple of the execution TF.
- **`SessionBarAggregator`** (Config / `HtfBar` / Component / Tests), anchored to ET sessions:
  - H4: 18/22/02/06/10/14, with the last bar cut short at 17:00. Verified against TradingView CME_MINI:NQ1!.
  - H8: 18:00 / 02:00 / 10:00–17:00 (7 hours). Verify on a TradingView chart before parity.
  - D1: trading date (18:00–17:00). W1: Sun 18:00 → Fri 17:00. MN1: by trading-date month.
  - A bar closes as soon as the execution bar ending on its boundary is processed. On a holiday or early close, it closes on the next new-bucket bar (late, but no lookahead).
  - DST is safe because the change happens over the weekend. Test it anyway.
- **`EmaIndicator(period)`:** generic, SMA-seeded (matches Pine `ta.ema`). Leave `Ema21Indicator` untouched.
- **History:**
  - A SQLite table of higher-timeframe bars per **root** (NQ), unadjusted and joined across contracts (TradingView NQ1! default).
  - Filled from **Schwab only**: a REST `pricehistory` backfill plus top-ups from live closed bars. A CSV import goes into the same table.
  - Schwab returns no minute history for expired contracts (`DataLoaders.cs:89-93`). Size the backfill to the probe results below.
- **Validation:** a strategy can't be enabled until stored bars ≥ period. TradingView parity needs ≥ 3 × period. **MN1 × EMA200 is blocked**; W1 × EMA200 shows a warning. A "History" panel on the setup page shows bars stored vs needed.

### 4. `ema-strategy` (one strategy, `StrategyType.Ema = 5`)
- **Config:**
  - `Source { PriceVsEma, EmaVsEma }`, `Entry { Touch, Cross, CrossRetest }`, `Direction { Up, Down, Both }`. Up = long, Down = short. For Touch, Up = long pullback from above.
  - Touch is only valid with PriceVsEma.
  - EMA periods come from {8, 21, 50, 200}. EmaVsEma requires Fast < Slow and adds `RetestEma { Fast, Slow }`.
  - `SignalTimeframe` is one of {M5, M15, M30, H1, H4, H8, D1, W1, MN1}.
  - Store the enums as strings in JSON.
- **Touch:**
  - Low ≤ EMA + max(1 tick, 0.10 × ATR).
  - The previous close is on the trend side, and this bar closes strictly back on that side. Exact equality doesn't count.
  - Re-arm after the low clears EMA + 0.5 × ATR (and the mirror image for shorts).
  - A touch rejected by confirmations doesn't use up the arm.
- **Cross:**
  - Sign-state crossover on closed bars; equal values never count as a cross.
  - PriceVsEma: close vs EMA, with optional `MinCloseBeyondAtr`. EmaVsEma: fast vs slow, with optional `MinSeparationAtr`.
  - Optional `HoldBars`. All three are off by default.
- **CrossRetest** (e.g. price crosses down through EMA 21, retests it, short):
  - A state machine: Idle → Crossed → Signal / Expired.
  - After a confirmed cross, price must move ≥ `RetestMinAwayAtr` (0.25) beyond the retest EMA.
  - Within `RetestWindowBars` (10), a **later** bar must touch the EMA (Touch tolerance) and close back on the cross side. That bar is the signal; the cross bar never counts.
  - A close back across the EMA, or the end of the window, cancels the setup. `MaxEntriesPerCross` defaults to 1.
- **Timing:** signal on the signal-bar close; enter at the next execution bar's open (the existing arm-then-enter pattern). Option B is a flag for Touch and Retest, off by default: check every execution bar against the last **closed** higher-TF EMA.
- **Stops:**
  - Touch: past the touch bar's extreme + buffer (ticks).
  - Retest: past the retest bar's extreme + buffer (default), or the EMA ± ATR × k.
  - Cross: the (slow) EMA ± ATR × k.
  - Any of them can instead use ATR × k from entry.
- **Targets:** `TargetMode { Atr, RiskMultiple, Dollars }`. Dollars and the min-R guard come from spec 2. TP1/TP2 use the existing partial and break-even settings.
- **Confirmations:**
  - `IEntryConfirmation` returns a `ConfirmationVote` (Name, Active, Passed, Value, Threshold), mirroring `ChopFilterDiagnostic`. Aggregation is All or MinVotes(N), clamped like `ChopRegimeConfig.Normalize()`. Inactive (not enough data) counts as **fail**.
  - Evaluate them inside the strategy when it arms.
  - Ship: HTF trend alignment (price vs EMA 50/200 on D1/W1), time window (skip the first/last N minutes of RTH), rejection-candle quality (Touch and Retest), and the extension gate (max distance from the EMA in ATR).
  - Chop stays the existing `TickerGroup` filter (`BypassChopFilter`).
  - Defer VWAP, relative volume, EMA stack, RSI/MACD and sweep.
- **Web:** a setup page matching the mockup (What crosses / Entry / Direction pickers, conditional fields, live "In plain words", History panel); cockpit card state lines (e.g. "Crossed down 06:00 · waiting for retest, 4 bars left · moved away 0.41 ATR ✓"); `StrategyText` for every combination.
- **Tests:**
  - Hand-verified EMA fixtures; every Source × Entry × Direction combination.
  - Touch edge cases: exact equality, gaps, the first bar after warmup, consecutive touches, re-arm.
  - Retest edge cases: a retest on the cross bar (rejected), a retest after the window (expired), a cross reversal (cancelled), `MaxEntriesPerCross` 1 vs 2.
  - Touch with EmaVsEma and Fast ≥ Slow are both rejected by validation.
  - Confirmations, including inactive = fail.
  - Session and 8H boundaries, weekly/monthly rollovers, DST.

### 5. `ema-parity-and-validation`
- A Pine v6 script covering every Source × Entry × Direction combination, using `barstate.isconfirmed`, `request.security(..., lookahead_off)` and session-anchored `"240"` / `"480"`. Compare signal timestamps with the C# backtest after warmup, EMA within ±1 tick, on the same data (NQ1! unadjusted).
- A baseline (no confirmations), then one confirmation added at a time, on a time-ordered in/out-of-sample split (`SampleSplit` with an embargo), using `EdgeTest` and `Ablation`.
- Report win rate, expectancy (R, with a 95% interval), profit factor, max drawdown and trade count. Flag fewer than 20 trades as InsufficientEvidence (expected for W1/MN1).

**Environment notes**
- Install .NET 10 if missing (`global.json` = 10.0.100) and record the baseline build and test counts before any change.
- **Schwab probe output** (from the appendix script): <paste here>

---

## Appendix — Schwab history probe

Run where CRV.Web is logged in to Schwab (the folder that holds `schwab_tokens.json`; needs `curl` and `jq`). It prints bar counts and date ranges only. Do not share the token file.

```bash
#!/usr/bin/env bash
# Schwab futures history probe: prints bar count + first/last bar date per query.
TOKEN=$(jq -r .access_token schwab_tokens.json)
API="https://api.schwabapi.com/marketdata/v1/pricehistory"
NOW=$(($(date +%s)*1000))
YEAR_AGO=$(( NOW - 365*24*3600*1000 ))

probe() {  # $1 = label, $2 = query string
  local r; r=$(curl -s -H "Authorization: Bearer $TOKEN" "$API?$2")
  echo "$r" | jq -r --arg l "$1" '
    if .candles then
      "\($l): \(.candles|length) bars, " +
      (if (.candles|length)>0 then
        "\(.candles[0].datetime/1000|strftime("%Y-%m-%d %H:%M")) → \(.candles[-1].datetime/1000|strftime("%Y-%m-%d %H:%M")) UTC"
       else "empty=\(.empty)" end)
    else "\($l): ERROR \(.)" end'
}

for S in /NQ /NQZ26 /MNQZ26; do
  E=$(printf %s "$S" | jq -sRr @uri)
  probe "$S daily 20y"   "symbol=$E&periodType=year&period=20&frequencyType=daily&frequency=1"
  probe "$S weekly 20y"  "symbol=$E&periodType=year&period=20&frequencyType=weekly&frequency=1"
  probe "$S monthly 20y" "symbol=$E&periodType=year&period=20&frequencyType=monthly&frequency=1"
  probe "$S 30m 1y"      "symbol=$E&periodType=day&frequencyType=minute&frequency=30&startDate=$YEAR_AGO&endDate=$NOW"
  probe "$S 1m 1y"       "symbol=$E&periodType=day&frequencyType=minute&frequency=1&startDate=$YEAR_AGO&endDate=$NOW"
done
probe "/NQU26 (expired) 1m" "symbol=%2FNQU26&periodType=day&frequencyType=minute&frequency=1&startDate=$YEAR_AGO&endDate=$NOW"
```

A `401` means the access token expired (about 30 minutes): let the app refresh it, then run again.
