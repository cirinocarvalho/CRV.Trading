# EMA Strategy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One EMA strategy (`StrategyType.Ema = 5`) — price vs EMA or EMA vs EMA, entered on a Touch, a Cross or a Cross + retest of closed higher-timeframe bars — with its stops, targets, confirmations, migration of the retired EMA21 entries, setup page, Strategies row and cockpit card.

**Architecture:** Pure detectors (`TouchDetector`, `CrossDetector`, `CrossRetestDetector` in `EmaSignals.cs`) read closed signal bars; `EmaStrategy` builds those bars from execution bars with plan 4's `SessionBarAggregator` (through a `TimeframeFeed` that also applies stored history first), arms on a signal bar's close after the confirmations vote, and enters on the first tick of the next execution bar (`EmaLevels` → `MinRrGuard` → `EntrySignal`). `ComposableEngine` hands every closed bar to strategies before its idle check and before `TickerGroup`'s session and cutoff filters, so signal bars build around the clock; `OnBar` builds them too, so a driver that only calls `OnTick`/`OnBar` (plan 6's trace) gets the same signals. The web pages add the EMA panels to the existing setup page, a live "In plain words" and History panel through a preview handler, the Strategies row and cockpit state cells.

**Tech Stack:** .NET 10, C# (nullable, file-scoped namespaces in new files), EF Core on SQLite, xUnit 2.9.3, ASP.NET Core Razor Pages, vanilla JS, Playwright + axe (`CRV.Web.A11yTests`).

**Spec:** `docs/superpowers/specs/2026-10-02-ema-strategy-design.md`

**Requires:** plans 1–4 merged — `2026-10-02-ema21-removal.md`, `2026-10-02-hold-and-close.md`, `2026-10-02-dollar-targets-and-rr-guard.md`, `2026-10-02-htf-bars-and-history.md`. Plan 6 (`2026-10-02-ema-parity-and-validation.md`) builds on this one.

Names consumed from earlier plans (exact):

| Plan | Names |
|---|---|
| 1 | `StrategyType.Ema` (5), `SetupValidation.Entry/SaveErrors/DisabledSetups/DisabledReason/BarMinutes/RetiredEma21/Sentences`, `ComposableEngine.AddSetups/DisabledSetups`, `StrategyConfig.EmaBasketJson/ToEmaSetupConfigs()/EnumerateBasketEntries()`, `BasketItem(Entry, IsEmaBasket)`, `TickerGroup.GetGroupKey/GroupLabel`, `StrategyModel.EditableTypes/ReadOnly/RootLabel`, the setup page's JS type lookup `names = { 0: …, 3: … }`, `SetupSnapshot.DisabledReason`, `CockpitSnapshot.Disabled(...)`, `A11ySeed.RetiredId`, `A11ySeed.RetiredBasketJson` |
| 2 | `bool ISetupStrategy.CloseAtRthClose { get; }` (no default; every strategy implements it); resets never clear `InTrade` |
| 3 | `TargetMode { RangePct, Dollars, Atr, RiskMultiple }`, `TargetDollarsBasis`, `MinRrAction`, `StrategySetupConfig.TargetMode/TargetDollars/TargetDollarsBasis/EnforceMinRr/MinRrAction`, `LevelRequest.From(cfg, entry, isLong, stop, contracts, rangeOrAtr)`, `MinRrGuard.Apply(cfg, request)` → `GuardedLevels(Target, Partial, Rr, Skip)`, `SizeRefusalGate.ReportMinRr(...)`/`LastMinRrSkip`, `SetupStateSnapshot.MinRrEnforced/MinRr/LastSkip`, `MinRrSaveCheck.Check/AtrPerTrade` (private `Pass`/`Fail`), `StrategyText.Target(c)`, the setup page's `Num(..., mode:)` helper, `data-target-mode` and `TargetMode` radios, `StrategyModel.TypicalStopPoints` |
| 4 | `SignalTimeframe` (`CRV.Core.Indicators`), `SignalTimeframes.Minutes()/IsWholeMultipleOf()`, `SessionBucket.For(tf, utc)`, `HtfBar`, `SessionBarAggregator(SignalTimeframe tf, int executionMinutes)` with `Forming` / `OnExecutionBar(Bar)`, `EmaIndicator(int)` with `Add/IsReady/Value/Period`, `EasternTime.ToEastern`, `HtfBarRow`, `HtfBarStore(TradingDbContext)` with `CountAsync/LatestAsync/LastFilledUtcAsync`, `HistoryStatus`, `HistoryRequirement.For/Check/Note/SourceLine` |

## Global Constraints

- Branch `feat/ema-strategy`. Conventional Commits: `feat(ema):`, `test(ema):`, `fix(ema):`, `feat(ema-ui):`.
- `StrategyType.Ema = 5`; the strategy emits an `EntrySignal`, `BrokerEventHandler` owns the trade. The same executor interface serves live, paper, backtest and Tradovate Replay; no special cases.
- Signals are evaluated on **closed** bars only, higher-timeframe bars included. No lookahead: entry at the next execution bar's open (arm-then-enter, consumed on the first tick of the next bar).
- Sizing as today: `AutoSizeByRiskCalculator.Calc`, then targets (`MinRrGuard.Apply`), in plan 3's order.
- New `StrategySetupConfig` enums are stored as **strings** in basket JSON via `[JsonConverter(typeof(JsonStringEnumConverter))]` per property; existing int enums (`StrategyType`, `TargetMode`, …) are unchanged.
- Defaults from the spec table, verbatim: `EmaSource PriceVsEma`, `EmaEntry CrossRetest`, `EmaDirection Both`, `EmaPeriod 21`, `FastEma 8`, `SlowEma 21`, `RetestEma Fast`, `SignalTimeframe H4`, `CheckEveryExecutionBar false`, `TouchTicks 1`, `TouchAtr 0.10`, `RearmAtr 0.5`, `CloseBackOnSide true`, `MinCloseBeyondAtr 0`, `MinSeparationAtr 0`, `HoldBars 1`, `RetestMinAwayAtr 0.25`, `RetestWindowBars 10`, `MaxEntriesPerCross 1`, `StopBuffer 2 ticks / 0.5 ATR`, confirmations trend + time window on, `ConfirmationsNeeded` All (0).
- EMA periods only 8, 21, 50, 200. Trend confirmation: D1 or W1, EMA 50 or 200 (defaults D1, 200); time window 15 / 15; rejection candle 40 %; not stretched 1.5 × ATR, off.
- Inactive (not enough data) confirmation counts as a **fail**.
- Never edit basket JSON with string replacement or Python; use `BasketCodec`.
- Tests never start a live broker or place orders: only strategy objects, `ComposableEngine` with fakes, and `BacktestEngine`.
- Every new screen and state passes the WCAG gate (`dotnet test CRV.Web.A11yTests`).
- Comments say what or why, never history.
- Baseline before Task 1: run `dotnet test CRV.Core.Tests` and `dotnet test CRV.Web.A11yTests` and record both pass counts in the first commit message; every task ends with both suites green ("all pass" below means the recorded count plus this plan's additions).

## Review Focus

1. **NQ and MNQ both stream into one `TickerGroup`.** The same bar time arrives twice; an EMA strategy must not fold it into its signal bar twice. Expected: the second copy is ignored. Pinned by `ObserveBar_SameBarTwice_CountsOnce` (Task 9).
2. **The engine starts mid-way through a signal bar.** That first bucket has only part of its highs and lows. Expected: it moves the EMA and ATR but never produces a signal. Pinned by `FirstBucketJoinedMidway_NeverSignals` (Task 9).
3. **Tradovate Replay or a backtest seeds history.** Stored bars after the replay date or the backtest start would be lookahead. Expected: only bars opening before the anchor are loaded. Pinned by `SeedAsync_AsksForBarsBeforeTheAnchor_PerRootAndTimeframe` (Task 10).
4. **The live engine is idle between sessions.** Signal bars spanning the gap (H4 18:00–22:00, D1) would be built from part of the session. Expected: every closed bar still reaches the strategy. Pinned by `IdleEngine_StillHandsClosedBarsToEmaStrategies` (Task 10).
5. **An EMA stop that lands on the wrong side of entry** (a long whose EMA − k × ATR is above the next bar's open after a gap). Expected: no order. Pinned by `EmaAtrStopAboveALongEntry_IsNoOrder` (Task 8).

## Decisions

1. **Confirmation storage is plan 6's shape** (`CRV.Core.Models`): `enum ConfirmationKind { HtfTrend, TimeWindow, RejectionCandle, NotStretched }`, `class ConfirmationConfig { Kind, Enabled, TrendTimeframe, TrendEmaPeriod, SkipFirstMinutes, SkipLastMinutes, CloseInPct, MaxAtrFromEma }` (a new one carries the shipped defaults), `List<ConfirmationConfig> StrategySetupConfig.Confirmations` (default: trend and time window on, rejection and not-stretched off) and `int ConfirmationsNeeded` (0 = All). The coordinator's message named the first member `HigherTimeframeTrend`; plan 6's committed code reads `ConfirmationKind.HtfTrend`, so this plan uses `HtfTrend` to compile against plan 6 unchanged.
2. **Rules the spec leaves open follow plan 6 Decision 6**, so parity compares like with like: Touch compares the previous close with the previous bar's EMA, both sides start armed, an arm is used only by a signal its direction allows (and, here, that the confirmations accept). Cross is sign state; zero is no side; with `HoldBars` N the cross bar counts as 1, a zero or opposite side before the Nth bar cancels it, and `MinCloseBeyondAtr` / `MinSeparationAtr` are checked on the Nth bar — failing them drops the cross. Cross + retest: any confirmed cross starts `Crossed` (a side the strategy doesn't trade still runs the state machine and uses up its retest, as in Pine); "moved away" is measured from the cross bar on, after the touch check, never on a touch bar; the touch check runs before the cancel check; the window counts signal bars after the cross bar and bar W is the last that can signal. A bar becomes "ready" when the line EMA (the EMA, or the fast EMA) was ready on the previous bar, the slow EMA is ready and ATR(14) is ready (Pine's `not na(lineA[1])`).
3. **ATR is `AtrIndicator(14)` on signal bars** (`EmaStrategy.AtrPeriod`), as plan 6 assumes.
4. **Option B (`CheckEveryExecutionBar`)** applies to Touch and Cross + retest only. Each closed execution bar is checked **before** that bar is folded into its signal bar, against the EMA and ATR of the last closed signal bar. For a retest, the cross and the window still run on signal bars; moving away, the touch and the cancel run on execution bars.
5. **Signal shape is fixed at engine start.** Source, entry, periods, retest EMA, signal timeframe, option B and the trend confirmation's switch/timeframe/period (`EmaSignalShape`) are read when the strategy is built; thresholds, stops, targets, sizing and the other confirmations follow a running reconfigure. The setup page's restart note says so.
6. **History seeding.** `ISetupStrategy` gains defaulted `HistoryNeeds` / `SeedHistory(...)` / `ObserveBar(...)`. At engine start (live and backtest) `HtfHistorySeeder` loads, per root and timeframe, the newest closed `HtfBars` rows opening before the anchor (now, the Replay date, or the backtest's `From`): 3 × the period + 14. `TimeframeFeed` applies stored bars that open before the first live bucket, then builds from execution bars; a first live bucket joined part-way moves the EMA/ATR but never signals. Plan 4's `HtfBarStore` gains `ClosedBeforeAsync` and implements `IHtfHistorySource`.
7. **Short history at engine start is not a disabled entry.** Saving an enabled EMA entry is blocked while stored bars are below the period (signal EMA, and the trend EMA when it is on) — `EmaHistoryGate`. At engine start an entry with short history runs and waits ("Waiting for: History, n more H4 bars"), building from live bars.
8. **Stops.** `EmaStopMode.SignalBarExtreme` = past the touch / retest bar's low (long) or high (short) by `StopBuffer` ticks; `EmaAtr` = the EMA (Cross: the EMA, or the slow EMA; Retest: the retest EMA) ± `StopBuffer` × ATR; `EntryAtr` = entry ± `StopBuffer` × ATR. A Cross with `SignalBarExtreme` is a validation error. A stop on the wrong side of entry sends nothing.
9. **Targets.** `Atr` and `RiskMultiple` use `AtrTp1Mult` (partial) / `AtrTp2Mult` (target) — one pair of fields, as the mockup's single "First / Final target" inputs (plan 3 Decision 4). `RangePct` is a validation error for EMA entries. Plan 3 deferred the save check for `Atr`; `MinRrSaveCheck.CheckAtr` does it from the signal timeframe's ATR over the latest stored bars and the typical stop.
10. **Time window** uses RTH 09:30–16:00 ET; signals outside RTH pass. Skipped: `[09:30, 09:30 + first)` and `(16:00 − last, 16:00]`. **Not stretched** measures the signal bar's close against the EMA (the entry price is not known when the strategy arms). **Rejection candle** is not offered (nor counted) for a plain Cross.
11. **Cockpit card** cells come from the strategy as `(Label, Value)` pairs (`SetupSnapshot.EmaCells`), so the page draws them without knowing the state machine. Card states use the existing badges: `▶ ARMED LONG/SHORT` while a retest is pending or an entry is armed, `IDLE` otherwise.
12. **Migration `ConvertRetiredEma21Entries`** is an empty EF migration (a marker); `Program.cs` runs its C# `Apply` right after `Migrate()` when that migration was pending. The converted entry keeps sizing and sessions, gets `EmaStopMode.SignalBarExtreme` + 2 ticks (Touch has no EMA stop), `TargetMode.Atr` (EMA21 targets were `AtrTp1Mult` / `AtrTp2Mult`), a direction from its old `AllowLong` / `AllowShort`, and the shortest signal timeframe that is a whole number of its bar size.
13. **"In plain words" and the History panel update live** through a `Preview` page handler that binds the form like Save and returns `StrategyText.Describe` and the history rows as JSON, so the wording has one source (C#). The name is filled from the same response until the person types in it, and never after.
14. **Strategies row** for EMA: `EMA · {entry} · {ticker} · {session names} · target {target}`; entry is `touch`, `cross`, `cross + retest`, or for two EMAs `two EMAs, cross` / `two EMAs, cross + retest` (the mockup's wording).
15. **The pickers are native radios** (`input.btn-check` + `label` in a `fieldset`/`legend`), styled like the mockup's `.st-trigger` buttons: they post without JavaScript and give screen readers a group and a name.

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Modify | `CRV.Core/Models/StrategySetupConfig.cs` | EMA enums, fields, `ConfirmationKind`, `ConfirmationConfig`, `EmaDirectionSides` |
| Modify | `CRV.Core/Models/StrategyConfig.cs` | `ToSetupConfig` maps the EMA fields; direction drives the side switches |
| Create | `CRV.Core/Models/EmaValidation.cs` | Per-entry EMA problems |
| Modify | `CRV.Core/Models/SetupValidation.cs` | `Ema` runs `EmaValidation` instead of "can't run in this version" |
| Create | `CRV.Core/Strategy/EmaHistoryGate.cs` | EMAs an entry reads, short-history errors, ATR from stored bars |
| Create | `CRV.Core/Strategy/EmaSignals.cs` | `EmaBar`, `EmaSignal`, `EmaSignalOptions`, `TouchDetector`, `CrossDetector`, `CrossRetestDetector` |
| Create | `CRV.Core/Strategy/Confirmations/IEntryConfirmation.cs` | `ConfirmationVote`, `ConfirmationContext`, `IEntryConfirmation` |
| Create | `CRV.Core/Strategy/Confirmations/HigherTimeframeTrend.cs`, `TimeWindow.cs`, `RejectionCandle.cs`, `NotStretched.cs` | The four confirmations |
| Create | `CRV.Core/Strategy/Confirmations/EmaConfirmations.cs` | `ConfirmationGate`, `ConfirmationDecision`, `EmaConfirmations` (build, normalize, labels) |
| Create | `CRV.Core/Strategy/TimeframeFeed.cs` | Signal bars from execution bars, stored history first |
| Create | `CRV.Core/Strategy/HtfHistory.cs` | `HistoryNeed`, `IHtfHistorySource`, `HtfHistorySeeder` |
| Modify | `CRV.Core/Strategy/ISetupStrategy.cs` | Defaulted `ObserveBar`, `HistoryNeeds`, `SeedHistory`; `SetupStateSnapshot.EmaCells` |
| Create | `CRV.Core/Strategy/EmaLevels.cs` | Entry, stop, size, guarded targets for an armed signal |
| Create | `CRV.Core/Strategy/EmaStrategy.cs` | The strategy and `EmaSignalShape` |
| Modify | `CRV.Core/Strategy/StrategyFactory.cs` | `Ema` case |
| Modify | `CRV.Core/Strategy/TickerGroup.cs`, `ComposableEngine.cs` | Closed bars reach strategies before filters and idle |
| Modify | `CRV.Core/Data/HtfBarStore.cs` | `ClosedBeforeAsync`, implements `IHtfHistorySource` |
| Modify | `CRV.Backtest/Engine/BacktestEngine.cs`, `CRV.Web/Services/BacktestRunnerService.cs`, `CRV.Web/Services/LiveEngineOrchestrator.cs` | History seeding |
| Modify | `CRV.Core/Models/Signals.cs`, `CRV.Core/Strategy/SnapshotAggregator.cs` | `CardCell`, `SetupSnapshot.EmaCells` |
| Create | `CRV.Core/Strategy/TargetText.cs` | Target and partial in a few words (card, row) |
| Create | `CRV.Core/Strategy/EmaDescription.cs` | Plain words, Strategies row, auto name |
| Modify | `CRV.Core/Strategy/MinRrSaveCheck.cs` | `CheckAtr` |
| Create | `CRV.Core/Migrations/<ts>_ConvertRetiredEma21Entries.cs` (+ Designer) | Type-4 → disabled `Ema` |
| Modify | `CRV.Web/Program.cs` | Runs the conversion after `Migrate()` |
| Modify | `CRV.Web/Pages/Setup/StrategyText.cs`, `Strategies.cshtml`, `Strategy.cshtml`, `Strategy.cshtml.cs`, `CRV.Web/Services/StrategyBasketService.cs` | Setup page, row, add defaults |
| Create | `CRV.Web/wwwroot/js/crv-ema-setup.js` | Setup page behaviour |
| Modify | `CRV.Web/wwwroot/css/components.css` | `.st-trigger*`, `.st-conf*`, `.st-meter*`, `.crumb`, fieldset reset |
| Modify | `CRV.Web/Pages/Dashboard/Index.cshtml` | EMA card cells |
| Modify | `CRV.Web.A11yTests/A11ySeed.cs`, `A11yPages.cs`, `CockpitSnapshot.cs`, `CockpitCardTests.cs`; Create `EmaSetupPageTests.cs` | WCAG and behaviour |
| Test | `CRV.Core.Tests/Models/EmaConfigTests.cs`, `Models/EmaValidationTests.cs`, `Strategy/EmaSignalsTests.cs`, `Strategy/ConfirmationTests.cs`, `Strategy/TimeframeFeedTests.cs`, `Strategy/EmaLevelsTests.cs`, `Strategy/EmaFixture.cs`, `Strategy/EmaStrategyTests.cs`, `Strategy/EmaCardTests.cs`, `Strategy/EmaDescriptionTests.cs`, `Strategy/ComposableEngineTests.cs`, `Backtest/EmaNoLookaheadTests.cs`, `Data/ConvertRetiredEma21EntriesTests.cs` | |

---

## Strategy core

### Task 1: EMA settings in the basket

**Files:**
- Modify: `CRV.Core/Models/StrategySetupConfig.cs`
- Modify: `CRV.Core/Models/StrategyConfig.cs` (`ToSetupConfig`)
- Test: `CRV.Core.Tests/Models/EmaConfigTests.cs`

**Interfaces:**
- Consumes: `SignalTimeframe` (plan 4), `TargetMode` (plan 3), `StrategyType.Ema`, `StrategyConfig.EmaBasketJson` / `ToEmaSetupConfigs()` (plan 1).
- Produces (namespace `CRV.Core.Models`): `enum EmaSource { PriceVsEma, EmaVsEma }`, `enum EmaEntry { Touch, Cross, CrossRetest }`, `enum EmaDirection { Up, Down, Both }`, `enum RetestEmaChoice { Fast, Slow }`, `enum EmaStopMode { SignalBarExtreme, EmaAtr, EntryAtr }`, `enum ConfirmationKind { HtfTrend, TimeWindow, RejectionCandle, NotStretched }`; `class ConfirmationConfig` (`Kind`, `Enabled`, `TrendTimeframe`, `TrendEmaPeriod`, `SkipFirstMinutes`, `SkipLastMinutes`, `CloseInPct`, `MaxAtrFromEma`, `static List<ConfirmationConfig> Defaults()`); `StrategySetupConfig` fields `EmaSource, EmaEntry, EmaDirection, EmaPeriod, FastEma, SlowEma, RetestEma, SignalTimeframe, CheckEveryExecutionBar, TouchTicks, TouchAtr, RearmAtr, CloseBackOnSide, MinCloseBeyondAtr, MinSeparationAtr, HoldBars, RetestMinAwayAtr, RetestWindowBars, MaxEntriesPerCross, EmaStopMode, StopBuffer, Confirmations, ConfirmationsNeeded`; `static (bool AllowLong, bool AllowShort) EmaDirectionSides.Sides(this EmaDirection d)`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Models/EmaConfigTests.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Models;

public class EmaConfigTests
{
    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var c = new StrategySetupConfig();

        Assert.Equal((EmaSource.PriceVsEma, EmaEntry.CrossRetest, EmaDirection.Both), (c.EmaSource, c.EmaEntry, c.EmaDirection));
        Assert.Equal((21, 8, 21, RetestEmaChoice.Fast), (c.EmaPeriod, c.FastEma, c.SlowEma, c.RetestEma));
        Assert.Equal((SignalTimeframe.H4, false), (c.SignalTimeframe, c.CheckEveryExecutionBar));
        Assert.Equal((1, 0.10m, 0.5m, true), (c.TouchTicks, c.TouchAtr, c.RearmAtr, c.CloseBackOnSide));
        Assert.Equal((0m, 0m, 1), (c.MinCloseBeyondAtr, c.MinSeparationAtr, c.HoldBars));
        Assert.Equal((0.25m, 10, 1), (c.RetestMinAwayAtr, c.RetestWindowBars, c.MaxEntriesPerCross));
        Assert.Equal((EmaStopMode.SignalBarExtreme, 2m, 0), (c.EmaStopMode, c.StopBuffer, c.ConfirmationsNeeded));

        Assert.Equal(new[] { ConfirmationKind.HtfTrend, ConfirmationKind.TimeWindow, ConfirmationKind.RejectionCandle, ConfirmationKind.NotStretched },
            c.Confirmations.Select(k => k.Kind));
        Assert.Equal(new[] { true, true, false, false }, c.Confirmations.Select(k => k.Enabled));
        var k0 = new ConfirmationConfig();
        Assert.Equal((SignalTimeframe.D1, 200, 15, 15, 40, 1.5m),
            (k0.TrendTimeframe, k0.TrendEmaPeriod, k0.SkipFirstMinutes, k0.SkipLastMinutes, k0.CloseInPct, k0.MaxAtrFromEma));
    }

    [Fact]
    public void BasketJson_StoresEmaEnumsAsStrings_AndKeepsStrategyTypeANumber()
    {
        var entry = new BasketEntry
        {
            Id = "ema-mnq", StrategyType = StrategyType.Ema, Ticker = "/MNQZ26",
            Config = new StrategySetupConfig { EmaEntry = EmaEntry.Touch, SignalTimeframe = SignalTimeframe.D1, EmaStopMode = EmaStopMode.EntryAtr },
        };
        entry.Config.Confirmations[0].TrendTimeframe = SignalTimeframe.W1;

        var json = BasketCodec.Serialize(new[] { entry });

        Assert.Contains("\"StrategyType\":5", json);
        Assert.Contains("\"EmaEntry\":\"Touch\"", json);
        Assert.Contains("\"SignalTimeframe\":\"D1\"", json);
        Assert.Contains("\"EmaStopMode\":\"EntryAtr\"", json);
        Assert.Contains("\"Kind\":\"HtfTrend\"", json);
        Assert.Contains("\"TrendTimeframe\":\"W1\"", json);

        var back = Assert.Single(BasketCodec.Parse(json));
        Assert.Equal((EmaEntry.Touch, SignalTimeframe.D1, SignalTimeframe.W1),
            (back.Config.EmaEntry, back.Config.SignalTimeframe, back.Config.Confirmations[0].TrendTimeframe));
    }

    [Fact]
    public void BasketJson_ReadsEnumsWrittenAsNumbers()
    {
        var back = Assert.Single(BasketCodec.Parse("""[{"Id":"e","StrategyType":5,"Config":{"EmaEntry":1,"EmaSource":1}}]"""));

        Assert.Equal((EmaEntry.Cross, EmaSource.EmaVsEma), (back.Config.EmaEntry, back.Config.EmaSource));
    }

    [Fact]
    public void ToSetupConfigs_FromEmaBasket_MapsEveryEmaField()
    {
        var src = new StrategySetupConfig
        {
            EmaSource = EmaSource.EmaVsEma, EmaEntry = EmaEntry.Cross, EmaDirection = EmaDirection.Down,
            EmaPeriod = 50, FastEma = 21, SlowEma = 200, RetestEma = RetestEmaChoice.Slow,
            SignalTimeframe = SignalTimeframe.H1, CheckEveryExecutionBar = true,
            TouchTicks = 3, TouchAtr = 0.2m, RearmAtr = 0.7m, CloseBackOnSide = false,
            MinCloseBeyondAtr = 0.1m, MinSeparationAtr = 0.3m, HoldBars = 3,
            RetestMinAwayAtr = 0.4m, RetestWindowBars = 6, MaxEntriesPerCross = 2,
            EmaStopMode = EmaStopMode.EmaAtr, StopBuffer = 0.5m, ConfirmationsNeeded = 1,
        };
        src.Confirmations[3].Enabled = true;
        src.Confirmations[3].MaxAtrFromEma = 2m;
        var cfg = new StrategyConfig
        {
            EmaBasketJson = BasketCodec.Serialize(new[] { new BasketEntry { Id = "ema-1", Enabled = true, StrategyType = StrategyType.Ema, Ticker = "/MNQZ26", Config = src } }),
        };

        var c = Assert.Single(cfg.ToEmaSetupConfigs());

        Assert.Equal((EmaSource.EmaVsEma, EmaEntry.Cross, EmaDirection.Down), (c.EmaSource, c.EmaEntry, c.EmaDirection));
        Assert.Equal((50, 21, 200, RetestEmaChoice.Slow), (c.EmaPeriod, c.FastEma, c.SlowEma, c.RetestEma));
        Assert.Equal((SignalTimeframe.H1, true), (c.SignalTimeframe, c.CheckEveryExecutionBar));
        Assert.Equal((3, 0.2m, 0.7m, false), (c.TouchTicks, c.TouchAtr, c.RearmAtr, c.CloseBackOnSide));
        Assert.Equal((0.1m, 0.3m, 3), (c.MinCloseBeyondAtr, c.MinSeparationAtr, c.HoldBars));
        Assert.Equal((0.4m, 6, 2), (c.RetestMinAwayAtr, c.RetestWindowBars, c.MaxEntriesPerCross));
        Assert.Equal((EmaStopMode.EmaAtr, 0.5m, 1), (c.EmaStopMode, c.StopBuffer, c.ConfirmationsNeeded));
        Assert.True(c.Confirmations[3].Enabled);
        Assert.Equal(2m, c.Confirmations[3].MaxAtrFromEma);
    }

    [Theory]
    [InlineData(EmaDirection.Up,   true,  false)]
    [InlineData(EmaDirection.Down, false, true)]
    [InlineData(EmaDirection.Both, true,  true)]
    public void Direction_SetsTheSideSwitches(EmaDirection dir, bool allowLong, bool allowShort)
    {
        Assert.Equal((allowLong, allowShort), dir.Sides());

        // The stored switches are overridden for EMA entries, whatever they say.
        var cfg = new StrategyConfig
        {
            EmaBasketJson = BasketCodec.Serialize(new[] { new BasketEntry
            {
                Id = "ema-1", StrategyType = StrategyType.Ema, Ticker = "/MNQZ26",
                Config = new StrategySetupConfig { EmaDirection = dir, AllowLong = !allowLong, AllowShort = !allowShort },
            } }),
        };
        var c = Assert.Single(cfg.ToEmaSetupConfigs());
        Assert.Equal((allowLong, allowShort), (c.AllowLong, c.AllowShort));
    }

    [Fact]
    public void Direction_DoesNotTouchOtherStrategiesSwitches()
    {
        var cfg = new StrategyConfig
        {
            BasketJson = BasketCodec.Serialize(new[] { new BasketEntry
            {
                Id = "pb", Enabled = true, StrategyType = StrategyType.Pullback, Ticker = "/MNQZ26",
                Config = new StrategySetupConfig { EmaDirection = EmaDirection.Up, AllowLong = false, AllowShort = true },
            } }),
        };
        var c = cfg.ToSetupConfigs().Single(s => s.Id == "pb");
        Assert.Equal((false, true), (c.AllowLong, c.AllowShort));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaConfigTests"`
Expected: build FAILS with `'StrategySetupConfig' does not contain a definition for 'EmaSource'`.

- [ ] **Step 3: Add the fields and types**

In `CRV.Core/Models/StrategySetupConfig.cs` add at the top:

```csharp
using System.Text.Json.Serialization;
using CRV.Core.Indicators;
```

Inside `StrategySetupConfig`, after `public decimal AtrTp2Mult { get; set; } = 2.0m;` add:

```csharp

    // ── EMA strategy (StrategyType.Ema) ───────────────────────────
    /// <summary>What crosses: price against one EMA, or a fast EMA against a slow one.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EmaSource EmaSource { get; set; } = EmaSource.PriceVsEma;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EmaEntry EmaEntry { get; set; } = EmaEntry.CrossRetest;
    /// <summary>Up = long, Down = short. Sets <see cref="AllowLong"/> / <see cref="AllowShort"/> for EMA entries.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EmaDirection EmaDirection { get; set; } = EmaDirection.Both;
    /// <summary>The EMA price is compared with (price vs EMA): 8, 21, 50 or 200.</summary>
    public int EmaPeriod { get; set; } = 21;
    public int FastEma { get; set; } = 8;
    public int SlowEma { get; set; } = 21;
    /// <summary>With two EMAs, the one a cross + retest has to come back to.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RetestEmaChoice RetestEma { get; set; } = RetestEmaChoice.Fast;
    /// <summary>The closed bars signals are read on; a whole number of execution bars.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SignalTimeframe SignalTimeframe { get; set; } = SignalTimeframe.H4;
    /// <summary>Touch and Cross + retest: also check each closed execution bar against the last closed signal-bar EMA.</summary>
    public bool CheckEveryExecutionBar { get; set; }
    /// <summary>Touch tolerance = max(TouchTicks × tick, TouchAtr × ATR).</summary>
    public int TouchTicks { get; set; } = 1;
    public decimal TouchAtr { get; set; } = 0.10m;
    /// <summary>Touch: a side arms again once price clears the EMA by this many ATR.</summary>
    public decimal RearmAtr { get; set; } = 0.5m;
    /// <summary>Touch and retest: the signal bar must close back on the trade's side of the EMA.</summary>
    public bool CloseBackOnSide { get; set; } = true;
    /// <summary>Price vs EMA cross: the close must be this many ATR past the EMA (0 = off).</summary>
    public decimal MinCloseBeyondAtr { get; set; }
    /// <summary>EMA vs EMA cross: the EMAs must be this many ATR apart (0 = off).</summary>
    public decimal MinSeparationAtr { get; set; }
    /// <summary>The new side must hold for this many bars, the cross bar included (1 = the cross bar counts).</summary>
    public int HoldBars { get; set; } = 1;
    public decimal RetestMinAwayAtr { get; set; } = 0.25m;
    public int RetestWindowBars { get; set; } = 10;
    public int MaxEntriesPerCross { get; set; } = 1;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EmaStopMode EmaStopMode { get; set; } = EmaStopMode.SignalBarExtreme;
    /// <summary>Ticks past the signal bar (SignalBarExtreme), or a multiple of ATR (EmaAtr, EntryAtr).</summary>
    public decimal StopBuffer { get; set; } = 2m;
    public List<ConfirmationConfig> Confirmations { get; set; } = ConfirmationConfig.Defaults();
    /// <summary>How many switched-on confirmations must pass: 0 = all of them.</summary>
    public int ConfirmationsNeeded { get; set; }
```

After the closing brace of `StrategySetupConfig` (end of file) add:

```csharp

/// <summary>What the EMA strategy compares: price with one EMA, or a fast EMA with a slow one.</summary>
public enum EmaSource { PriceVsEma, EmaVsEma }

/// <summary>How the EMA strategy enters: a pullback that touches the EMA, the cross, or a retest after the cross.</summary>
public enum EmaEntry { Touch, Cross, CrossRetest }

/// <summary>Which side the EMA strategy trades. Up = long, Down = short.</summary>
public enum EmaDirection { Up, Down, Both }

/// <summary>With two EMAs, the one a cross + retest comes back to.</summary>
public enum RetestEmaChoice { Fast, Slow }

/// <summary>Where the EMA strategy puts its stop.</summary>
public enum EmaStopMode { SignalBarExtreme, EmaAtr, EntryAtr }

/// <summary>The EMA strategy's entry confirmations.</summary>
public enum ConfirmationKind { HtfTrend, TimeWindow, RejectionCandle, NotStretched }

/// <summary>
/// One entry confirmation of an EMA setup: its switch and its parameters. Each kind reads only
/// its own parameters; a new one carries the shipped defaults.
/// </summary>
public sealed class ConfirmationConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConfirmationKind Kind { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Higher-timeframe trend: D1 or W1.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SignalTimeframe TrendTimeframe { get; set; } = SignalTimeframe.D1;
    /// <summary>Higher-timeframe trend: EMA 50 or 200.</summary>
    public int TrendEmaPeriod { get; set; } = 200;
    /// <summary>Time window: minutes skipped after the RTH open.</summary>
    public int SkipFirstMinutes { get; set; } = 15;
    /// <summary>Time window: minutes skipped before the RTH close.</summary>
    public int SkipLastMinutes { get; set; } = 15;
    /// <summary>Rejection candle: the signal bar closes in the top (long) / bottom (short) this many % of its range.</summary>
    public int CloseInPct { get; set; } = 40;
    /// <summary>Not stretched: the signal close is at most this many ATR from the EMA.</summary>
    public decimal MaxAtrFromEma { get; set; } = 1.5m;

    /// <summary>The four confirmations a new EMA setup gets: trend and time window on.</summary>
    public static List<ConfirmationConfig> Defaults() => new()
    {
        new() { Kind = ConfirmationKind.HtfTrend, Enabled = true },
        new() { Kind = ConfirmationKind.TimeWindow, Enabled = true },
        new() { Kind = ConfirmationKind.RejectionCandle },
        new() { Kind = ConfirmationKind.NotStretched },
    };
}

public static class EmaDirectionSides
{
    /// <summary>The long / short switches a direction stands for: Up is long only, Down short only, Both both.</summary>
    public static (bool AllowLong, bool AllowShort) Sides(this EmaDirection d) => (d != EmaDirection.Down, d != EmaDirection.Up);
}
```

- [ ] **Step 4: Map them**

In `CRV.Core/Models/StrategyConfig.cs`, `ToSetupConfig(BasketEntry b)`: replace the two lines

```csharp
        AllowLong = b.Config.AllowLong,
        AllowShort = b.Config.AllowShort,
```

with

```csharp
        // An EMA entry's direction picker is its side switch.
        AllowLong  = b.StrategyType == CRV.Core.Strategy.StrategyType.Ema ? b.Config.EmaDirection.Sides().AllowLong  : b.Config.AllowLong,
        AllowShort = b.StrategyType == CRV.Core.Strategy.StrategyType.Ema ? b.Config.EmaDirection.Sides().AllowShort : b.Config.AllowShort,
```

and after `AtrTp2Mult = b.Config.AtrTp2Mult,` add:

```csharp
        // EMA strategy
        EmaSource = b.Config.EmaSource,
        EmaEntry = b.Config.EmaEntry,
        EmaDirection = b.Config.EmaDirection,
        EmaPeriod = b.Config.EmaPeriod,
        FastEma = b.Config.FastEma,
        SlowEma = b.Config.SlowEma,
        RetestEma = b.Config.RetestEma,
        SignalTimeframe = b.Config.SignalTimeframe,
        CheckEveryExecutionBar = b.Config.CheckEveryExecutionBar,
        TouchTicks = b.Config.TouchTicks,
        TouchAtr = b.Config.TouchAtr,
        RearmAtr = b.Config.RearmAtr,
        CloseBackOnSide = b.Config.CloseBackOnSide,
        MinCloseBeyondAtr = b.Config.MinCloseBeyondAtr,
        MinSeparationAtr = b.Config.MinSeparationAtr,
        HoldBars = b.Config.HoldBars,
        RetestMinAwayAtr = b.Config.RetestMinAwayAtr,
        RetestWindowBars = b.Config.RetestWindowBars,
        MaxEntriesPerCross = b.Config.MaxEntriesPerCross,
        EmaStopMode = b.Config.EmaStopMode,
        StopBuffer = b.Config.StopBuffer,
        Confirmations = b.Config.Confirmations,
        ConfirmationsNeeded = b.Config.ConfirmationsNeeded,
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaConfigTests|FullyQualifiedName~BasketCodecTests|FullyQualifiedName~ConfigMappingTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Models/StrategySetupConfig.cs CRV.Core/Models/StrategyConfig.cs CRV.Core.Tests/Models/EmaConfigTests.cs
git commit -m "feat(ema): EMA strategy settings in the basket, enums stored as strings"
```

---

### Task 2: EMA validation and the history gate

**Files:**
- Create: `CRV.Core/Models/EmaValidation.cs`
- Modify: `CRV.Core/Models/SetupValidation.cs` (`Entry`)
- Create: `CRV.Core/Strategy/EmaHistoryGate.cs`
- Test: `CRV.Core.Tests/Models/EmaValidationTests.cs`

**Interfaces:**
- Consumes: Task 1 fields; `SetupValidation.Entry/BarMinutes` (plan 1); `SignalTimeframes.IsWholeMultipleOf`, `HistoryRequirement.For/Check`, `HistoryStatus`, `HtfBar` (plan 4); `AtrIndicator`.
- Produces:
  - `static IReadOnlyList<string> EmaValidation.Entry(BasketEntry entry, int barMinutes)` — lower-case problem phrases (plan 1's style); `static readonly int[] EmaValidation.Periods = { 8, 21, 50, 200 }`.
  - `static IReadOnlyList<(SignalTimeframe Timeframe, int Period, string Use)> EmaHistoryGate.Needs(StrategySetupConfig c)`; `static IReadOnlyList<string> EmaHistoryGate.Errors(StrategySetupConfig c, Func<SignalTimeframe, int> stored)`; `static decimal? EmaHistoryGate.Atr(IReadOnlyList<HtfBar> bars)`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Models/EmaValidationTests.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Models;

public class EmaValidationTests
{
    private static BasketEntry Entry(Action<StrategySetupConfig>? tweak = null, int? bar = 5)
    {
        var c = new StrategySetupConfig { TargetMode = TargetMode.RiskMultiple };
        tweak?.Invoke(c);
        return new BasketEntry { Id = "ema-mnq", Enabled = true, StrategyType = StrategyType.Ema, Ticker = "/MNQZ26", ExecutionTFMinutes = bar, Config = c };
    }

    [Fact]
    public void ValidEntry_HasNoProblems()
    {
        Assert.Empty(EmaValidation.Entry(Entry(), 5));
        Assert.Empty(SetupValidation.Entry(Entry(), new StrategyConfig()));
    }

    [Theory]
    [InlineData("touch-two-emas",  "touch needs price and one EMA, not two EMAs")]
    [InlineData("fast-not-faster", "fast EMA (21) must be shorter than slow EMA (21)")]
    [InlineData("odd-period",      "EMA periods are 8, 21, 50 or 200")]
    [InlineData("tf-not-multiple", "M15 bars can't be built from 10-minute bars")]
    [InlineData("cross-bar-stop",  "a cross stops past the EMA or a multiple of ATR from entry")]
    [InlineData("entry-atr-zero",  "a stop from entry needs a buffer above 0 × ATR")]
    [InlineData("hold-zero",       "the cross must hold for at least 1 bar")]
    [InlineData("range-target",    "EMA targets are set in ATR, R or dollars")]
    [InlineData("trend-h4",        "the trend check uses EMA 50 or 200 on D1 or W1")]
    public void InvalidEntry_NamesTheProblem(string kase, string expected)
    {
        var (entry, bar) = kase switch
        {
            "touch-two-emas"  => (Entry(c => { c.EmaEntry = EmaEntry.Touch; c.EmaSource = EmaSource.EmaVsEma; }), 5),
            "fast-not-faster" => (Entry(c => { c.EmaSource = EmaSource.EmaVsEma; c.FastEma = 21; c.SlowEma = 21; }), 5),
            "odd-period"      => (Entry(c => c.EmaPeriod = 20), 5),
            "tf-not-multiple" => (Entry(c => c.SignalTimeframe = SignalTimeframe.M15, bar: 10), 10),
            "cross-bar-stop"  => (Entry(c => { c.EmaEntry = EmaEntry.Cross; c.EmaStopMode = EmaStopMode.SignalBarExtreme; }), 5),
            "entry-atr-zero"  => (Entry(c => { c.EmaStopMode = EmaStopMode.EntryAtr; c.StopBuffer = 0m; }), 5),
            "hold-zero"       => (Entry(c => c.HoldBars = 0), 5),
            "range-target"    => (Entry(c => c.TargetMode = TargetMode.RangePct), 5),
            _                 => (Entry(c => c.Confirmations[0].TrendTimeframe = SignalTimeframe.H4), 5),
        };

        Assert.Contains(expected, EmaValidation.Entry(entry, bar));
        // The same problem blocks the save and disables the entry at engine start (plan 1's paths).
        Assert.Contains(expected, SetupValidation.Entry(entry, new StrategyConfig { ExecutionTFMinutes = bar }));
    }

    [Fact]
    public void Validation_DisablesOnlyThatEntry_AtEngineStart()
    {
        var good = Entry();
        good.Id = "ema-good";
        var bad = Entry(c => { c.EmaSource = EmaSource.EmaVsEma; c.FastEma = 50; c.SlowEma = 21; });
        bad.Id = "ema-bad";
        var cfg = new StrategyConfig { ExecutionTFMinutes = 5, EmaBasketJson = BasketCodec.Serialize(new[] { good, bad }) };

        var disabled = Assert.Single(SetupValidation.DisabledSetups(cfg));
        Assert.Equal("ema-bad", disabled.Id);
        Assert.Equal("fast EMA (50) must be shorter than slow EMA (21)", disabled.Reason);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void HistoryGate_BlocksBelowThePeriod(int stored, bool blocked)
    {
        var c = new StrategySetupConfig();
        foreach (var k in c.Confirmations) k.Enabled = false;

        var errors = EmaHistoryGate.Errors(c, _ => stored);

        Assert.Equal(blocked, errors.Count == 1);
        if (blocked) Assert.Equal("the signal EMA needs 21 H4 bars and 20 are stored", errors[0]);
    }

    [Fact]
    public void HistoryGate_ChecksTheTrendEmaOnlyWhenItIsOn()
    {
        var c = new StrategySetupConfig { EmaSource = EmaSource.EmaVsEma, FastEma = 8, SlowEma = 50 };

        Assert.Equal(new[] { (SignalTimeframe.H4, 50, "signal EMA"), (SignalTimeframe.D1, 200, "trend check") }, EmaHistoryGate.Needs(c));
        Assert.Equal(new[] { "the trend check needs 200 D1 bars and 199 are stored" },
            EmaHistoryGate.Errors(c, tf => tf == SignalTimeframe.D1 ? 199 : 50));

        c.Confirmations[0].Enabled = false;
        Assert.Single(EmaHistoryGate.Needs(c));
    }

    [Fact]
    public void Atr_FromStoredBars_IsWilderOver14_OrNullWhenShort()
    {
        var t = new DateTime(2026, 3, 18, 0, 0, 0, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, 14).Select(i => new HtfBar(SignalTimeframe.D1, t.AddDays(i), 100m, 102m, 98m, 100m, 1)).ToList();

        Assert.Null(EmaHistoryGate.Atr(bars.Take(13).ToList()));
        Assert.Equal(4m, EmaHistoryGate.Atr(bars));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaValidationTests"`
Expected: build FAILS with `The name 'EmaValidation' does not exist in the current context`.

- [ ] **Step 3: Write `EmaValidation`**

`CRV.Core/Models/EmaValidation.cs`:

```csharp
using CRV.Core.Indicators;

namespace CRV.Core.Models;

/// <summary>
/// What's wrong with one EMA entry on its own. Lower-case phrases that read after "Disabled: ",
/// like the rest of <see cref="SetupValidation"/>; the save path turns them into sentences.
/// </summary>
public static class EmaValidation
{
    public static readonly int[] Periods = { 8, 21, 50, 200 };

    public static IReadOnlyList<string> Entry(BasketEntry entry, int barMinutes)
    {
        var c = entry.Config;
        var problems = new List<string>();

        if (c.EmaEntry == EmaEntry.Touch && c.EmaSource != EmaSource.PriceVsEma)
            problems.Add("touch needs price and one EMA, not two EMAs");
        if (c.EmaSource == EmaSource.PriceVsEma && !Periods.Contains(c.EmaPeriod))
            problems.Add("EMA periods are 8, 21, 50 or 200");
        if (c.EmaSource == EmaSource.EmaVsEma)
        {
            if (!Periods.Contains(c.FastEma) || !Periods.Contains(c.SlowEma))
                problems.Add("EMA periods are 8, 21, 50 or 200");
            else if (c.FastEma >= c.SlowEma)
                problems.Add($"fast EMA ({c.FastEma}) must be shorter than slow EMA ({c.SlowEma})");
        }
        if (!c.SignalTimeframe.IsWholeMultipleOf(barMinutes))
            problems.Add($"{c.SignalTimeframe} bars can't be built from {barMinutes}-minute bars");

        if (c.EmaEntry == EmaEntry.Cross && c.EmaStopMode == EmaStopMode.SignalBarExtreme)
            problems.Add("a cross stops past the EMA or a multiple of ATR from entry");
        if (c.StopBuffer < 0)
            problems.Add("the stop buffer can't be negative");
        else if (c.EmaStopMode == EmaStopMode.EntryAtr && c.StopBuffer == 0)
            problems.Add("a stop from entry needs a buffer above 0 × ATR");

        if (c.HoldBars < 1) problems.Add("the cross must hold for at least 1 bar");
        if (c.RetestWindowBars < 1) problems.Add("the retest window must be at least 1 bar");
        if (c.MaxEntriesPerCross < 1) problems.Add("entries per cross must be at least 1");
        if (c.TouchTicks < 0 || c.TouchAtr < 0 || c.RearmAtr < 0 || c.MinCloseBeyondAtr < 0 ||
            c.MinSeparationAtr < 0 || c.RetestMinAwayAtr < 0)
            problems.Add("touch and cross distances can't be negative");

        if (c.TargetMode == TargetMode.RangePct)
            problems.Add("EMA targets are set in ATR, R or dollars");

        foreach (var k in c.Confirmations.Where(k => k.Enabled))
        {
            switch (k.Kind)
            {
                case ConfirmationKind.HtfTrend when k.TrendTimeframe is not (SignalTimeframe.D1 or SignalTimeframe.W1) || k.TrendEmaPeriod is not (50 or 200):
                    problems.Add("the trend check uses EMA 50 or 200 on D1 or W1"); break;
                case ConfirmationKind.TimeWindow when k.SkipFirstMinutes < 0 || k.SkipLastMinutes < 0:
                    problems.Add("time window minutes can't be negative"); break;
                case ConfirmationKind.RejectionCandle when k.CloseInPct is < 1 or > 100:
                    problems.Add("the rejection candle needs a share from 1 to 100%"); break;
                case ConfirmationKind.NotStretched when k.MaxAtrFromEma <= 0:
                    problems.Add("not stretched needs a distance above 0 × ATR"); break;
            }
        }
        if (c.ConfirmationsNeeded < 0) problems.Add("confirmations needed can't be negative");
        return problems;
    }
}
```

- [ ] **Step 4: Run EMA entries through it**

In `CRV.Core/Models/SetupValidation.cs`, `Entry(...)`, replace

```csharp
        else if (entry.StrategyType == StrategyType.Ema)
            problems.Add("the EMA strategy can't run in this version");
```

with

```csharp
        else if (entry.StrategyType == StrategyType.Ema)
            problems.AddRange(EmaValidation.Entry(entry, BarMinutes(entry, cfg)));
```

and delete plan 1's test that asserts `"the EMA strategy can't run in this version"` (in `CRV.Core.Tests/Models/SetupValidationTests.cs`, the `Assert.Contains("the EMA strategy can't run in this version", …)` fact): that phrase no longer exists. `ValidEntry_HasNoProblems` above takes its place.

- [ ] **Step 5: Write `EmaHistoryGate`**

`CRV.Core/Strategy/EmaHistoryGate.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// The stored history an EMA entry needs before it can be switched on: the signal EMA (the slow
/// one for two EMAs) on the signal timeframe, and the trend check's EMA on its own timeframe.
/// </summary>
public static class EmaHistoryGate
{
    public static IReadOnlyList<(SignalTimeframe Timeframe, int Period, string Use)> Needs(StrategySetupConfig c)
    {
        var needs = new List<(SignalTimeframe, int, string)>
        {
            (c.SignalTimeframe, c.EmaSource == EmaSource.EmaVsEma ? c.SlowEma : c.EmaPeriod, "signal EMA"),
        };
        if (c.Confirmations.FirstOrDefault(k => k.Kind == ConfirmationKind.HtfTrend && k.Enabled) is { } trend)
            needs.Add((trend.TrendTimeframe, trend.TrendEmaPeriod, "trend check"));
        return needs;
    }

    /// <summary>One phrase per EMA whose stored bars are below its period.</summary>
    public static IReadOnlyList<string> Errors(StrategySetupConfig c, Func<SignalTimeframe, int> stored)
    {
        var errors = new List<string>();
        foreach (var (tf, period, use) in Needs(c))
        {
            int have = stored(tf);
            if (HistoryRequirement.Check(have, period) == HistoryStatus.Blocked)
                errors.Add($"the {use} needs {HistoryRequirement.For(period).Needed} {tf} bars and {have} are stored");
        }
        return errors;
    }

    /// <summary>ATR(14) over stored bars, oldest first; null when fewer than 14.</summary>
    public static decimal? Atr(IReadOnlyList<HtfBar> bars)
    {
        var atr = new AtrIndicator(EmaStrategy.AtrPeriod);
        foreach (var b in bars) atr.Update(new Bar(b.OpenUtc, b.Open, b.High, b.Low, b.Close, b.Volume));
        return atr.IsReady ? atr.Value : null;
    }
}
```

`EmaStrategy.AtrPeriod` arrives in Task 9. Until then, add this to the top of `EmaHistoryGate` and remove it in Task 9 Step 3:

```csharp
    private const int AtrPeriodUntilEmaStrategy = 14;
```

and use `AtrPeriodUntilEmaStrategy` in place of `EmaStrategy.AtrPeriod`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaValidationTests|FullyQualifiedName~SetupValidationTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Models/EmaValidation.cs CRV.Core/Models/SetupValidation.cs CRV.Core/Strategy/EmaHistoryGate.cs \
  CRV.Core.Tests/Models/EmaValidationTests.cs CRV.Core.Tests/Models/SetupValidationTests.cs
git commit -m "feat(ema): validate EMA entries and gate switching on by stored history"
```

---

### Task 3: Touch detector

**Files:**
- Create: `CRV.Core/Strategy/EmaSignals.cs`
- Test: `CRV.Core.Tests/Strategy/EmaSignalsTests.cs`

**Interfaces:**
- Consumes: Task 1 fields, `Bar`.
- Produces (namespace `CRV.Core.Strategy`):
  - `readonly record struct EmaBar(Bar Bar, decimal Line, decimal Reference, decimal RetestLine, decimal Atr)`
  - `sealed record EmaSignal(bool IsLong, Bar SignalBar, decimal Ema, decimal Atr)`
  - `sealed record EmaSignalOptions(decimal TickSize, int TouchTicks, decimal TouchAtr, decimal RearmAtr, bool CloseBackOnSide, decimal MinBeyondAtr, int HoldBars, decimal RetestMinAwayAtr, int RetestWindowBars, int MaxEntriesPerCross)` with `static From(StrategySetupConfig)` and `decimal Tolerance(decimal atr)`
  - `sealed class TouchDetector`: `bool LongArmed`, `bool ShortArmed`, `void Remember(decimal close, decimal ema)`, `EmaSignal? OnBar(EmaBar b, EmaSignalOptions o, bool allowLong, bool allowShort)`, `void Accept(EmaSignal s)`

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/EmaSignalsTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// The detectors on hand-set EMA and ATR values. Tick 0.25, ATR 2: the touch tolerance is
/// max(1 tick, 0.10 × 2) = 0.25 and re-arming needs 0.5 × 2 = 1.0 of clearance.
/// </summary>
public class EmaSignalsTests
{
    internal static readonly DateTime T0 = new(2026, 3, 18, 14, 0, 0, DateTimeKind.Utc);

    internal static EmaSignalOptions Opts(int hold = 1, decimal minBeyond = 0m, int window = 10, int maxPer = 1,
        bool closeBack = true, decimal touchAtr = 0.10m) =>
        new(0.25m, 1, touchAtr, 0.5m, closeBack, minBeyond, hold, 0.25m, window, maxPer);

    /// <summary>A bar for price vs EMA: the line is the close, the reference and retest line the EMA.</summary>
    internal static EmaBar B(int i, decimal o, decimal h, decimal l, decimal c, decimal ema = 100m, decimal atr = 2m) =>
        new(new Bar(T0.AddMinutes(5 * i), o, h, l, c, 100), c, ema, ema, atr);

    // ── Touch ───────────────────────────────────────────────────

    [Fact]
    public void Touch_LongPullbackFromAbove_ClosingBackAbove_Signals()
    {
        var d = new TouchDetector();
        Assert.Null(d.OnBar(B(0, 104, 105, 103, 104.5m), Opts(), true, true));

        var s = d.OnBar(B(1, 104.5m, 104.5m, 100.25m, 103), Opts(), true, true);

        Assert.NotNull(s);
        Assert.True(s.IsLong);
        Assert.Equal((100m, 2m, 100.25m), (s.Ema, s.Atr, s.SignalBar.Low));
    }

    [Fact]
    public void Touch_ShortRallyFromBelow_ClosingBackBelow_Signals()
    {
        var d = new TouchDetector();
        d.OnBar(B(0, 96, 97, 95, 95.5m), Opts(), true, true);

        var s = d.OnBar(B(1, 95.5m, 99.75m, 95, 97), Opts(), true, true);

        Assert.False(Assert.IsType<EmaSignal>(s).IsLong);
    }

    [Fact]
    public void Touch_LowJustOutsideTolerance_NoSignal()
    {
        var d = new TouchDetector();
        d.OnBar(B(0, 104, 105, 103, 104.5m), Opts(), true, true);
        Assert.Null(d.OnBar(B(1, 104.5m, 104.5m, 100.26m, 103), Opts(), true, true));
    }

    [Fact]
    public void Touch_ExactEquality_NeverCounts()
    {
        var closeOnEma = new TouchDetector();
        closeOnEma.OnBar(B(0, 104, 105, 103, 104.5m), Opts(), true, true);
        Assert.Null(closeOnEma.OnBar(B(1, 104.5m, 104.5m, 99.5m, 100m), Opts(), true, true));

        var prevOnEma = new TouchDetector();
        prevOnEma.OnBar(B(0, 101, 101, 99, 100m), Opts(), true, true);
        Assert.Null(prevOnEma.OnBar(B(1, 100, 103, 99.9m, 102), Opts(), true, true));
    }

    [Fact]
    public void Touch_GapThroughTheEma_SignalsOnlyWhenItClosesBackOnSide()
    {
        var back = new TouchDetector();
        back.OnBar(B(0, 104, 105, 103, 104.5m), Opts(), true, true);
        Assert.True(back.OnBar(B(1, 98, 101, 97, 101), Opts(), true, true)?.IsLong);

        var through = new TouchDetector();
        through.OnBar(B(0, 104, 105, 103, 104.5m), Opts(), true, true);
        Assert.Null(through.OnBar(B(1, 98, 99, 97, 98), Opts(), true, true));
    }

    [Fact]
    public void Touch_FirstBarAfterWarmup_NeverSignals_UnlessThePreviousBarWasRemembered()
    {
        Assert.Null(new TouchDetector().OnBar(B(1, 104.5m, 104.5m, 100.25m, 103), Opts(), true, true));

        var remembered = new TouchDetector();
        remembered.Remember(104.5m, 100m);
        Assert.NotNull(remembered.OnBar(B(1, 104.5m, 104.5m, 100.25m, 103), Opts(), true, true));
    }

    [Fact]
    public void Touch_ConsecutiveTouchingBars_OneSignalPerArm_ReArmsOnlyAfterClearing()
    {
        var d = new TouchDetector();
        d.OnBar(B(0, 104, 105, 103, 104.5m), Opts(), true, true);
        var first = d.OnBar(B(1, 104.5m, 104.5m, 100.1m, 103), Opts(), true, true)!;
        d.Accept(first);

        Assert.Null(d.OnBar(B(2, 103, 103.5m, 100.1m, 102.5m), Opts(), true, true));   // still used
        Assert.Null(d.OnBar(B(3, 102.5m, 103, 100.9m, 102), Opts(), true, true));      // low 100.9 ≤ 101.0: not cleared
        Assert.False(d.LongArmed);
        Assert.Null(d.OnBar(B(4, 102, 104, 101.1m, 103.5m), Opts(), true, true));      // low 101.1 > 101.0: armed again
        Assert.True(d.LongArmed);
        Assert.NotNull(d.OnBar(B(5, 103.5m, 104, 100.2m, 103), Opts(), true, true));
    }

    [Fact]
    public void Touch_NotAccepted_KeepsTheArm()
    {
        var d = new TouchDetector();
        d.OnBar(B(0, 104, 105, 103, 104.5m), Opts(), true, true);
        Assert.NotNull(d.OnBar(B(1, 104.5m, 104.5m, 100.1m, 103), Opts(), true, true));   // rejected: not accepted

        Assert.NotNull(d.OnBar(B(2, 103, 103.5m, 100.1m, 102.5m), Opts(), true, true));
    }

    [Fact]
    public void Touch_SideNotTraded_NeitherSignalsNorUsesTheArm()
    {
        var d = new TouchDetector();
        d.OnBar(B(0, 104, 105, 103, 104.5m), Opts(), true, true);
        Assert.Null(d.OnBar(B(1, 104.5m, 104.5m, 100.1m, 103), Opts(), allowLong: false, allowShort: true));
        Assert.True(d.LongArmed);
    }

    [Fact]
    public void Touch_ToleranceUsesAtrWhenLarger()
    {
        var d = new TouchDetector();
        d.OnBar(B(0, 104, 105, 103, 104.5m, atr: 5), Opts(), true, true);
        Assert.NotNull(d.OnBar(B(1, 104.5m, 104.5m, 100.45m, 103, atr: 5), Opts(), true, true));   // tol = 0.5
    }

    [Fact]
    public void Touch_CloseBackOff_SignalsEvenWhenTheBarClosesBelow()
    {
        var d = new TouchDetector();
        d.OnBar(B(0, 104, 105, 103, 104.5m), Opts(closeBack: false), true, true);
        Assert.True(d.OnBar(B(1, 104.5m, 104.5m, 99, 99.5m), Opts(closeBack: false), true, true)?.IsLong);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaSignalsTests"`
Expected: build FAILS with `The type or namespace name 'TouchDetector' could not be found`.

- [ ] **Step 3: Write the detector**

`CRV.Core/Strategy/EmaSignals.cs`:

```csharp
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// One closed bar as the EMA detectors see it. <see cref="Line"/> is what crosses (the close
/// for price vs EMA, the fast EMA for two EMAs), <see cref="Reference"/> what it crosses (the
/// EMA, or the slow EMA), <see cref="RetestLine"/> the EMA a retest comes back to.
/// </summary>
public readonly record struct EmaBar(Bar Bar, decimal Line, decimal Reference, decimal RetestLine, decimal Atr);

/// <summary>A signal on a closed bar: the side, the bar that made it (its high / low place a
/// signal-bar stop), and the EMA and ATR at that bar.</summary>
public sealed record EmaSignal(bool IsLong, Bar SignalBar, decimal Ema, decimal Atr);

/// <summary>The detector settings, read from the strategy's config on every bar.</summary>
public sealed record EmaSignalOptions(
    decimal TickSize, int TouchTicks, decimal TouchAtr, decimal RearmAtr, bool CloseBackOnSide,
    decimal MinBeyondAtr, int HoldBars, decimal RetestMinAwayAtr, int RetestWindowBars, int MaxEntriesPerCross)
{
    public static EmaSignalOptions From(StrategySetupConfig c) => new(
        c.TickSize, c.TouchTicks, c.TouchAtr, c.RearmAtr, c.CloseBackOnSide,
        c.EmaSource == EmaSource.PriceVsEma ? c.MinCloseBeyondAtr : c.MinSeparationAtr,
        Math.Max(1, c.HoldBars), c.RetestMinAwayAtr, Math.Max(1, c.RetestWindowBars), Math.Max(1, c.MaxEntriesPerCross));

    /// <summary>How close to the EMA counts as a touch: the larger of the ticks and the ATR share.</summary>
    public decimal Tolerance(decimal atr) => Math.Max(TouchTicks * TickSize, TouchAtr * atr);
}

/// <summary>
/// Touch on closed bars, price vs one EMA. Long: the previous close was above the previous
/// bar's EMA, this bar's low comes within the tolerance of the EMA, and (with CloseBackOnSide)
/// it closes strictly above it. Short is the mirror image. Each side starts armed, gives one
/// signal per arm, and arms again once price clears the EMA by RearmAtr × ATR. Only an
/// accepted signal of a side the strategy trades uses the arm.
/// </summary>
public sealed class TouchDetector
{
    private decimal? _prevClose;
    private decimal _prevEma;

    public bool LongArmed { get; private set; } = true;
    public bool ShortArmed { get; private set; } = true;

    /// <summary>Records a bar that came before the detector was ready, so the next bar has a previous close.</summary>
    public void Remember(decimal close, decimal ema) { _prevClose = close; _prevEma = ema; }

    public EmaSignal? OnBar(EmaBar b, EmaSignalOptions o, bool allowLong, bool allowShort)
    {
        var bar = b.Bar;
        decimal ema = b.Reference;
        if (!LongArmed && bar.Low > ema + o.RearmAtr * b.Atr) LongArmed = true;
        if (!ShortArmed && bar.High < ema - o.RearmAtr * b.Atr) ShortArmed = true;

        EmaSignal? signal = null;
        if (_prevClose is decimal prev)
        {
            decimal tol = o.Tolerance(b.Atr);
            bool longTouch  = prev > _prevEma && bar.Low  <= ema + tol && (!o.CloseBackOnSide || bar.Close > ema);
            bool shortTouch = prev < _prevEma && bar.High >= ema - tol && (!o.CloseBackOnSide || bar.Close < ema);
            if (longTouch && LongArmed && allowLong) signal = new EmaSignal(true, bar, ema, b.Atr);
            else if (shortTouch && ShortArmed && allowShort) signal = new EmaSignal(false, bar, ema, b.Atr);
        }
        Remember(bar.Close, ema);
        return signal;
    }

    /// <summary>The strategy took the signal: its side waits to re-arm.</summary>
    public void Accept(EmaSignal s)
    {
        if (s.IsLong) LongArmed = false;
        else ShortArmed = false;
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaSignalsTests"`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/EmaSignals.cs CRV.Core.Tests/Strategy/EmaSignalsTests.cs
git commit -m "feat(ema): touch detector on closed signal bars"
```

---

### Task 4: Cross detector

**Files:**
- Modify: `CRV.Core/Strategy/EmaSignals.cs`
- Test: `CRV.Core.Tests/Strategy/EmaSignalsTests.cs`

**Interfaces:**
- Consumes: Task 3 `EmaBar`, `EmaSignalOptions`.
- Produces: `sealed class CrossDetector`: `int Side` (last non-zero side), `(int Side, DateTime Time)? LastCross` (the bar where the side flipped), `int? OnBar(EmaBar b, EmaSignalOptions o)` (+1 / −1 on the confirming bar).

- [ ] **Step 1: Write the failing tests**

Append inside `EmaSignalsTests`:

```csharp
    // ── Cross ───────────────────────────────────────────────────

    /// <summary>A cross bar: only the line and the reference matter.</summary>
    private static EmaBar X(int i, decimal line, decimal reference = 100m, decimal atr = 2m) =>
        new(new Bar(T0.AddMinutes(5 * i), line, line, line, line, 100), line, reference, reference, atr);

    private static int?[] Feed(CrossDetector d, EmaSignalOptions o, params decimal[] lines) =>
        lines.Select((l, i) => d.OnBar(X(i, l), o)).ToArray();

    [Fact]
    public void Cross_SignStateFlip_IsACross()
        => Assert.Equal(new int?[] { null, 1, null, -1 }, Feed(new CrossDetector(), Opts(), 99, 101, 102, 98));

    [Fact]
    public void Cross_EqualValues_AreNeverACross()
    {
        Assert.Equal(new int?[] { null, null, 1 }, Feed(new CrossDetector(), Opts(), 99, 100, 101));
        Assert.Equal(new int?[] { null, null, null }, Feed(new CrossDetector(), Opts(), 99, 100, 99));
    }

    [Fact]
    public void Cross_FirstBarAfterWarmup_HasNoSideToCrossFrom()
        => Assert.Equal(new int?[] { null, null }, Feed(new CrossDetector(), Opts(), 101, 102));

    [Fact]
    public void Cross_HoldBars3_ConfirmsOnTheThirdBarOnTheNewSide_AndRemembersTheCrossBar()
    {
        var d = new CrossDetector();
        Assert.Equal(new int?[] { null, null, null, 1 }, Feed(d, Opts(hold: 3), 99, 101, 102, 103));
        Assert.Equal((1, T0.AddMinutes(5)), d.LastCross);

        Assert.Equal(new int?[] { null, 1 }, Feed(new CrossDetector(), Opts(hold: 1), 99, 101));
    }

    [Fact]
    public void Cross_HoldBroken_ByAnEqualOrOppositeBar_IsDropped()
    {
        // An equal bar cancels the pending cross; the side stays "above", so no later bar re-crosses.
        Assert.Equal(new int?[] { null, null, null, null, null }, Feed(new CrossDetector(), Opts(hold: 3), 99, 101, 100, 102, 103));
        // Back below after one bar above: the up cross is dropped and the flip back is a cross down of its own
        // (the side it left was "above"), confirmed on its third bar.
        Assert.Equal(new int?[] { null, null, null, null, -1 }, Feed(new CrossDetector(), Opts(hold: 3), 99, 101, 99, 99, 99));
    }

    [Fact]
    public void Cross_MinBeyond_IsCheckedOnTheConfirmingBar_AndAFailDropsTheCross()
    {
        // 0.5 × ATR 2 = 1.0 needed.
        Assert.Equal(new int?[] { null, 1 }, Feed(new CrossDetector(), Opts(minBeyond: 0.5m), 99, 101.2m));
        Assert.Equal(new int?[] { null, null, null }, Feed(new CrossDetector(), Opts(minBeyond: 0.5m), 99, 100.5m, 101.2m));
    }

    [Fact]
    public void Options_UseTheMinimumForTheSource()
    {
        var c = new StrategySetupConfig { MinCloseBeyondAtr = 0.2m, MinSeparationAtr = 0.3m };
        Assert.Equal(0.2m, EmaSignalOptions.From(c).MinBeyondAtr);
        c.EmaSource = EmaSource.EmaVsEma;
        Assert.Equal(0.3m, EmaSignalOptions.From(c).MinBeyondAtr);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaSignalsTests"`
Expected: build FAILS with `The type or namespace name 'CrossDetector' could not be found`.

- [ ] **Step 3: Write the detector**

Append to `CRV.Core/Strategy/EmaSignals.cs`:

```csharp

/// <summary>
/// Sign-state crossover of Line against Reference on closed bars. Zero is no side, so equal
/// values never cross; a cross needs an earlier side to cross from. With HoldBars N the cross
/// bar counts as 1 and the cross confirms on the Nth bar on the new side; a zero or opposite
/// side before then cancels it. The minimum distance is checked on that Nth bar, and failing it
/// drops the cross.
/// </summary>
public sealed class CrossDetector
{
    private int _pendingSide;
    private int _pendingCount;
    private DateTime _pendingTime;

    /// <summary>The last side that was not equal: +1 above, −1 below, 0 none seen yet.</summary>
    public int Side { get; private set; }

    /// <summary>The last confirmed cross: its side and the open time of the bar where it crossed.</summary>
    public (int Side, DateTime Time)? LastCross { get; private set; }

    /// <summary>+1 or −1 when this bar confirms a cross up or down; null otherwise.</summary>
    public int? OnBar(EmaBar b, EmaSignalOptions o)
    {
        decimal diff = b.Line - b.Reference;
        int side = Math.Sign(diff);
        int? crossed = null;

        if (side != 0 && Side != 0 && side != Side)
        {
            _pendingSide = side;
            _pendingCount = 0;
            _pendingTime = b.Bar.Time;
        }
        if (_pendingSide != 0)
        {
            if (side == _pendingSide)
            {
                if (++_pendingCount >= o.HoldBars)
                {
                    if (Math.Abs(diff) >= o.MinBeyondAtr * b.Atr)
                    {
                        crossed = _pendingSide;
                        LastCross = (_pendingSide, _pendingTime);
                    }
                    _pendingSide = 0;
                    _pendingCount = 0;
                }
            }
            else
            {
                _pendingSide = 0;
                _pendingCount = 0;
            }
        }
        if (side != 0) Side = side;
        return crossed;
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaSignalsTests"`
Expected: PASS (18 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/EmaSignals.cs CRV.Core.Tests/Strategy/EmaSignalsTests.cs
git commit -m "feat(ema): sign-state cross detector with hold bars and minimum distance"
```

---

### Task 5: Cross + retest detector

**Files:**
- Modify: `CRV.Core/Strategy/EmaSignals.cs`
- Test: `CRV.Core.Tests/Strategy/EmaSignalsTests.cs`

**Interfaces:**
- Consumes: Tasks 3–4.
- Produces: `enum RetestPhase { Idle, Crossed }`; `sealed class CrossRetestDetector`: `RetestPhase Phase`, `int CrossSide`, `DateTime CrossTime`, `int BarsSinceCross`, `bool MovedAway`, `decimal MovedAwayAtr`, `(int Side, DateTime Time)? LastCross`, `int BarsLeft(EmaSignalOptions o)`, `EmaSignal? OnSignalBar(EmaBar b, EmaSignalOptions o, bool allowLong, bool allowShort, bool checkRetest)`, `EmaSignal? OnExecutionBar(EmaBar b, EmaSignalOptions o, bool allowLong, bool allowShort)`, `void Accept(EmaSignalOptions o)`.

- [ ] **Step 1: Write the failing tests**

Append inside `EmaSignalsTests` (EMA 100, ATR 2: moving away needs 0.5, the touch tolerance is 0.25):

```csharp
    // ── Cross + retest ──────────────────────────────────────────

    private static EmaSignal?[] Retest(CrossRetestDetector d, EmaSignalOptions o, bool allowLong, bool allowShort,
        bool acceptSignals, params EmaBar[] bars) =>
        bars.Select(b =>
        {
            var s = d.OnSignalBar(b, o, allowLong, allowShort, checkRetest: true);
            if (s != null && acceptSignals) d.Accept(o);
            return s;
        }).ToArray();

    private static readonly EmaBar Above   = B(0, 100.5m, 101.5m, 100.2m, 101);   // side above
    private static readonly EmaBar CrossDn = B(1, 101, 101.5m, 98.5m, 99);         // crosses down; moves away 0.75 ATR; also "touches"
    private static readonly EmaBar Retouch = B(2, 99, 99.9m, 98, 98.5m);           // high 99.9 ≥ 99.75, closes below

    [Fact]
    public void Retest_OnTheCrossBar_IsRejected_ALaterBarSignals()
    {
        var s = Retest(new CrossRetestDetector(), Opts(), true, true, true, Above, CrossDn, Retouch);

        Assert.Null(s[1]);
        Assert.False(Assert.IsType<EmaSignal>(s[2]).IsLong);
        Assert.Equal(100m, s[2]!.Ema);
    }

    [Fact]
    public void Retest_NeedsAMoveAwayFirst_AndATouchBarDoesNotCountAsMovingAway()
    {
        var d = new CrossRetestDetector();
        var s = Retest(d, Opts(), true, true, true,
            Above,
            B(1, 101, 101.2m, 99.8m, 99.9m),    // crosses down, 0.1 ATR away
            B(2, 99.9m, 100, 99.7m, 99.8m),     // touches, not moved: nothing
            B(3, 99.8m, 99.9m, 98.5m, 99),      // touches before moving away: no signal; it moves away on this bar
            B(4, 99, 99.5m, 98.5m, 99.2m),      // no touch (99.5 < 99.75): moves away 0.75 ATR
            B(5, 99.2m, 99.8m, 98.8m, 99.3m));  // touches after moving away

        Assert.Equal(new bool[] { false, false, false, false, false, true }, s.Select(x => x != null));
    }

    [Fact]
    public void Retest_CloseBackAcross_CancelsTheSetup()
    {
        // Two EMAs, retest on the fast one: closing back above the fast EMA cancels without a new cross
        // (with price vs EMA a close back across is itself the opposite cross).
        static EmaBar R(int i, decimal o, decimal h, decimal l, decimal c, decimal fast) =>
            new(new Bar(T0.AddMinutes(5 * i), o, h, l, c, 100), fast, 100m, fast, 2m);
        var d = new CrossRetestDetector();
        Retest(d, Opts(), true, true, true,
            R(0, 101, 102, 100.5m, 101.5m, fast: 101),
            R(1, 99, 99.5m, 97, 97.5m, fast: 99));               // fast below slow: crossed down; 1 ATR below the fast EMA
        Assert.Equal((RetestPhase.Crossed, -1), (d.Phase, d.CrossSide));

        Retest(d, Opts(), true, true, true, R(2, 97.5m, 99.5m, 97.5m, 99.4m, fast: 99));   // touches, closes above the fast EMA
        Assert.Equal(RetestPhase.Idle, d.Phase);
        Assert.Null(Retest(d, Opts(), true, true, true, R(3, 99.4m, 99.5m, 98, 98.5m, fast: 99))[0]);
    }

    [Fact]
    public void Retest_AfterTheWindow_HasExpired()
    {
        static EmaBar Far(int i) => B(i, 98, 98.5m, 97, 97.5m);   // below, no touch

        var late = new CrossRetestDetector();
        var s = Retest(late, Opts(window: 3), true, true, true, Above, CrossDn, Far(2), Far(3), Far(4), B(5, 97.5m, 99.8m, 97.5m, 99));
        Assert.Null(s[5]);
        Assert.Equal(RetestPhase.Idle, late.Phase);

        var inTime = new CrossRetestDetector();
        s = Retest(inTime, Opts(window: 3), true, true, true, Above, CrossDn, Far(2), Far(3), B(4, 97.5m, 99.8m, 97.5m, 99));
        Assert.NotNull(s[4]);
    }

    [Fact]
    public void Retest_MaxEntriesPerCross_1Then2_EachNeedingAFreshMoveAway()
    {
        var bars = new[] { Above, CrossDn, Retouch, B(3, 98.5m, 99, 97.5m, 98), B(4, 98, 99.8m, 97.8m, 99.5m) };

        Assert.Single(Retest(new CrossRetestDetector(), Opts(maxPer: 1), true, true, true, bars).Where(x => x != null));
        var two = Retest(new CrossRetestDetector(), Opts(maxPer: 2), true, true, true, bars);
        Assert.Equal(new[] { 2, 4 }, two.Select((x, i) => (x, i)).Where(p => p.x != null).Select(p => p.i));
    }

    [Fact]
    public void Retest_SideNotTraded_RunsTheStateMachine_ButNeverSignals()
    {
        var d = new CrossRetestDetector();
        var s = Retest(d, Opts(), allowLong: true, allowShort: false, true, Above, CrossDn, Retouch);

        Assert.All(s, x => Assert.Null(x));
        Assert.Equal(RetestPhase.Idle, d.Phase);   // the retest was used up, as in the Pine reference
    }

    [Fact]
    public void Retest_NotAccepted_StaysCrossed()
    {
        var d = new CrossRetestDetector();
        var s = Retest(d, Opts(), true, true, acceptSignals: false, Above, CrossDn, Retouch);
        Assert.NotNull(s[2]);
        Assert.Equal(RetestPhase.Crossed, d.Phase);
    }

    [Fact]
    public void Retest_OnExecutionBars_ChecksAgainstTheLastSignalBarEma()
    {
        var d = new CrossRetestDetector();
        Retest(d, Opts(), true, true, true, Above, CrossDn);
        var s = d.OnExecutionBar(Retouch, Opts(), true, true);
        Assert.False(Assert.IsType<EmaSignal>(s).IsLong);
    }

    [Fact]
    public void Retest_Long_IsTheMirrorImage()
    {
        var below  = B(0, 99.5m, 99.8m, 98.5m, 99);
        var crossU = B(1, 99, 101.5m, 98.8m, 101);    // up, 0.75 ATR away
        var dip    = B(2, 101, 102, 100.2m, 101.5m);  // low 100.2 ≤ 100.25, closes above
        var s = Retest(new CrossRetestDetector(), Opts(), true, true, true, below, crossU, dip);
        Assert.True(s[2]?.IsLong);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaSignalsTests"`
Expected: build FAILS with `The type or namespace name 'CrossRetestDetector' could not be found`.

- [ ] **Step 3: Write the detector**

Append to `CRV.Core/Strategy/EmaSignals.cs`:

```csharp

public enum RetestPhase { Idle, Crossed }

/// <summary>
/// Cross + retest: Idle → Crossed on any confirmed cross (the Cross options apply), then a
/// signal when a later bar within RetestWindowBars comes back within the touch tolerance of
/// the retest EMA after price first moved RetestMinAwayAtr × ATR beyond it, and (with
/// CloseBackOnSide) closes back on the cross side. The touch is checked before a close back
/// across, which cancels the setup. Moving away is measured from the cross bar on, after the
/// touch check, and never on a touch bar. A side the strategy doesn't trade still uses up its
/// retest. MaxEntriesPerCross caps the signals one cross gives; each needs a fresh move away.
/// </summary>
public sealed class CrossRetestDetector
{
    private readonly CrossDetector _cross = new();
    private int _entries;

    public RetestPhase Phase { get; private set; }
    public int CrossSide { get; private set; }
    public DateTime CrossTime { get; private set; }
    public int BarsSinceCross { get; private set; }
    public bool MovedAway { get; private set; }
    /// <summary>The farthest price has moved beyond the retest EMA since the cross, in ATR.</summary>
    public decimal MovedAwayAtr { get; private set; }
    public (int Side, DateTime Time)? LastCross => _cross.LastCross;

    public int BarsLeft(EmaSignalOptions o) => Math.Max(0, o.RetestWindowBars - BarsSinceCross);

    /// <summary>A closed signal bar. With <paramref name="checkRetest"/> off (execution-bar checks), the
    /// bar still crosses, ages the window and measures the move away, but can't be the retest.</summary>
    public EmaSignal? OnSignalBar(EmaBar b, EmaSignalOptions o, bool allowLong, bool allowShort, bool checkRetest)
    {
        EmaSignal? signal = null;
        bool touchBar = false;

        if (_cross.OnBar(b, o) is int side)
        {
            Phase = RetestPhase.Crossed;
            CrossSide = side;
            CrossTime = _cross.LastCross!.Value.Time;
            BarsSinceCross = 0;
            _entries = 0;
            MovedAway = false;
            MovedAwayAtr = 0m;
        }
        else if (Phase == RetestPhase.Crossed)
        {
            if (++BarsSinceCross > o.RetestWindowBars) Phase = RetestPhase.Idle;
            else if (checkRetest) (signal, touchBar) = Retest(b, o, allowLong, allowShort);
        }

        if (Phase == RetestPhase.Crossed && !touchBar) Measure(b, o);
        return signal;
    }

    /// <summary>A closed execution bar checked against the last closed signal bar's retest EMA and ATR.</summary>
    public EmaSignal? OnExecutionBar(EmaBar b, EmaSignalOptions o, bool allowLong, bool allowShort)
    {
        if (Phase != RetestPhase.Crossed) return null;
        var (signal, touchBar) = Retest(b, o, allowLong, allowShort);
        if (Phase == RetestPhase.Crossed && !touchBar) Measure(b, o);
        return signal;
    }

    /// <summary>The strategy took the retest: it counts against MaxEntriesPerCross and the next one needs a fresh move away.</summary>
    public void Accept(EmaSignalOptions o)
    {
        MovedAway = false;
        if (++_entries >= o.MaxEntriesPerCross) Phase = RetestPhase.Idle;
    }

    private (EmaSignal? Signal, bool TouchBar) Retest(EmaBar b, EmaSignalOptions o, bool allowLong, bool allowShort)
    {
        var bar = b.Bar;
        decimal line = b.RetestLine;
        bool isLong = CrossSide > 0;
        decimal tol = o.Tolerance(b.Atr);
        bool touched = isLong ? bar.Low <= line + tol : bar.High >= line - tol;
        bool onSide  = isLong ? bar.Close > line : bar.Close < line;
        bool across  = isLong ? bar.Close < line : bar.Close > line;

        if (MovedAway && touched && (onSide || !o.CloseBackOnSide))
        {
            if (isLong ? allowLong : allowShort) return (new EmaSignal(isLong, bar, line, b.Atr), true);
            Accept(o);
            return (null, true);
        }
        if (across) Phase = RetestPhase.Idle;
        return (null, false);
    }

    private void Measure(EmaBar b, EmaSignalOptions o)
    {
        decimal away = CrossSide > 0 ? b.Bar.High - b.RetestLine : b.RetestLine - b.Bar.Low;
        if (b.Atr > 0 && away / b.Atr > MovedAwayAtr) MovedAwayAtr = away / b.Atr;
        if (away >= o.RetestMinAwayAtr * b.Atr) MovedAway = true;
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaSignalsTests"`
Expected: PASS (27 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/EmaSignals.cs CRV.Core.Tests/Strategy/EmaSignalsTests.cs
git commit -m "feat(ema): cross + retest state machine"
```

---

### Task 6: Confirmations

**Files:**
- Create: `CRV.Core/Strategy/Confirmations/IEntryConfirmation.cs`, `HigherTimeframeTrend.cs`, `TimeWindow.cs`, `RejectionCandle.cs`, `NotStretched.cs`, `EmaConfirmations.cs`
- Test: `CRV.Core.Tests/Strategy/ConfirmationTests.cs`

**Interfaces:**
- Consumes: `ConfirmationKind`, `ConfirmationConfig` (Task 1), `EasternTime.ToEastern` (plan 4).
- Produces (namespace `CRV.Core.Strategy.Confirmations`):
  - `sealed record ConfirmationVote(string Name, bool Active, bool Passed, decimal? Value, decimal? Threshold)`
  - `sealed record ConfirmationContext(bool IsLong, Bar SignalBar, DateTime SignalCloseUtc, decimal Ema, decimal Atr, decimal? TrendEma)`
  - `interface IEntryConfirmation { ConfirmationVote Vote(ConfirmationContext context); }`
  - `HigherTimeframeTrend(SignalTimeframe, int)`, `TimeWindow(int, int)` (+ `RthOpen`, `RthClose`), `RejectionCandle(int)`, `NotStretched(decimal)`
  - `sealed record ConfirmationDecision(bool Passed, int PassedCount, int Required, int SwitchedOn)`; `static class ConfirmationGate { int Required(int needed, int switchedOn); ConfirmationDecision Decide(IReadOnlyList<ConfirmationVote> votes, int needed); }`
  - `static class EmaConfirmations { IReadOnlyList<IEntryConfirmation> Build(StrategySetupConfig c); List<ConfirmationConfig> Normalized(IEnumerable<ConfirmationConfig>? list); string Label(ConfirmationKind k); }`

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/ConfirmationTests.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy.Confirmations;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class ConfirmationTests
{
    // Wed 2026-03-18 is EDT (ET = UTC−4).
    private static DateTime Et(int h, int m) => new(2026, 3, 18, h + 4, m, 0, DateTimeKind.Utc);

    private static ConfirmationContext Ctx(bool isLong = true, decimal o = 100, decimal h = 110, decimal l = 100, decimal c = 108,
        decimal ema = 104, decimal atr = 4, decimal? trend = 100, DateTime? at = null) =>
        new(isLong, new Bar(Et(10, 0), o, h, l, c, 1), at ?? Et(11, 0), ema, atr, trend);

    [Fact]
    public void Trend_LongsAboveShortsBelow_InactiveWithoutEnoughBars()
    {
        var t = new HigherTimeframeTrend(SignalTimeframe.D1, 200);
        Assert.Equal(new ConfirmationVote("D1 trend (EMA 200)", true, true, 108m, 100m), t.Vote(Ctx(trend: 100)));
        Assert.False(t.Vote(Ctx(isLong: false, trend: 100)).Passed);
        var inactive = t.Vote(Ctx(trend: null));
        Assert.False(inactive.Active);
        Assert.False(inactive.Passed);
    }

    [Theory]
    [InlineData(9, 40, false)]   // 10 min into RTH, first 15 skipped
    [InlineData(9, 45, true)]
    [InlineData(15, 45, true)]   // exactly 15 min before the close: not in (15:45, 16:00]
    [InlineData(15, 50, false)]
    [InlineData(16, 0, false)]
    [InlineData(3, 0, true)]     // outside RTH
    public void TimeWindow_SkipsTheFirstAndLastMinutesOfRth(int h, int m, bool passed)
        => Assert.Equal(passed, new TimeWindow(15, 15).Vote(Ctx(at: Et(h, m))).Passed);

    [Fact]
    public void TimeWindow_UsesEasternTime_InWinterToo()
    {
        var winter = new DateTime(2026, 1, 14, 14, 40, 0, DateTimeKind.Utc);   // 09:40 EST
        Assert.False(new TimeWindow(15, 15).Vote(Ctx(at: winter)).Passed);
    }

    [Fact]
    public void RejectionCandle_ClosesInTheTopShareForLongs_BottomForShorts()
    {
        var r = new RejectionCandle(40);
        Assert.True(r.Vote(Ctx(h: 110, l: 100, c: 106)).Passed);                  // 60 % up: top 40 %
        Assert.False(r.Vote(Ctx(h: 110, l: 100, c: 105.9m)).Passed);
        Assert.True(r.Vote(Ctx(isLong: false, h: 110, l: 100, c: 104)).Passed);   // 40 % up: bottom 40 %
        Assert.False(r.Vote(Ctx(h: 100, l: 100, c: 100)).Active);                 // no range
    }

    [Fact]
    public void NotStretched_SignalCloseWithinKAtrOfTheEma()
    {
        var n = new NotStretched(1.5m);
        Assert.True(n.Vote(Ctx(c: 110, ema: 104, atr: 4)).Passed);    // 1.5 ATR
        Assert.False(n.Vote(Ctx(c: 110.1m, ema: 104, atr: 4)).Passed);
        Assert.False(n.Vote(Ctx(atr: 0)).Active);
    }

    private static ConfirmationVote V(bool active, bool passed) => new("x", active, passed, null, null);

    [Fact]
    public void Gate_AllOrAtLeastN_InactiveCountsAsFail_NClamped()
    {
        var votes = new[] { V(true, true), V(true, false), V(false, false) };

        Assert.False(ConfirmationGate.Decide(votes, 0).Passed);                  // all three
        Assert.True(ConfirmationGate.Decide(votes, 1).Passed);
        Assert.False(ConfirmationGate.Decide(votes, 2).Passed);                  // the inactive one is a fail
        Assert.Equal(3, ConfirmationGate.Decide(votes, 9).Required);             // clamped to the switched-on count
        Assert.Equal(new ConfirmationDecision(true, 0, 0, 0), ConfirmationGate.Decide(Array.Empty<ConfirmationVote>(), 0));
    }

    [Fact]
    public void Build_OnlySwitchedOn_NoRejectionCandleForAPlainCross()
    {
        var c = new StrategySetupConfig { EmaEntry = EmaEntry.Cross };
        foreach (var k in c.Confirmations) k.Enabled = true;

        Assert.Equal(new[] { typeof(HigherTimeframeTrend), typeof(TimeWindow), typeof(NotStretched) },
            EmaConfirmations.Build(c).Select(x => x.GetType()));
        c.EmaEntry = EmaEntry.Touch;
        Assert.Equal(4, EmaConfirmations.Build(c).Count);
    }

    [Fact]
    public void Normalized_GivesTheFourKindsInOrder_KeepingStoredSettings()
    {
        var stored = new List<ConfirmationConfig> { new() { Kind = ConfirmationKind.NotStretched, Enabled = true, MaxAtrFromEma = 2m } };

        var n = EmaConfirmations.Normalized(stored);

        Assert.Equal(Enum.GetValues<ConfirmationKind>(), n.Select(k => k.Kind));
        Assert.True(n[3].Enabled);
        Assert.Equal(2m, n[3].MaxAtrFromEma);
        Assert.False(n[0].Enabled);   // a kind the setup never stored is off
        Assert.Equal("Higher-timeframe trend", EmaConfirmations.Label(ConfirmationKind.HtfTrend));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~ConfirmationTests"`
Expected: build FAILS with `The type or namespace name 'Confirmations' does not exist in the namespace 'CRV.Core.Strategy'`.

- [ ] **Step 3: Write the confirmations**

`CRV.Core/Strategy/Confirmations/IEntryConfirmation.cs`:

```csharp
using CRV.Core.Models;

namespace CRV.Core.Strategy.Confirmations;

/// <summary>
/// One confirmation's say on a signal, shaped like the chop filter's diagnostic. Active means it
/// had what it needs to judge; an inactive vote counts as a fail.
/// </summary>
public sealed record ConfirmationVote(string Name, bool Active, bool Passed, decimal? Value, decimal? Threshold);

/// <summary>What a confirmation sees when the strategy arms: the side, the signal bar and when it
/// closed, the EMA and ATR on the signal timeframe, and the trend EMA (null until it has enough bars).</summary>
public sealed record ConfirmationContext(bool IsLong, Bar SignalBar, DateTime SignalCloseUtc, decimal Ema, decimal Atr, decimal? TrendEma);

public interface IEntryConfirmation
{
    ConfirmationVote Vote(ConfirmationContext context);
}
```

`CRV.Core/Strategy/Confirmations/HigherTimeframeTrend.cs`:

```csharp
using CRV.Core.Indicators;

namespace CRV.Core.Strategy.Confirmations;

/// <summary>Longs only with the signal close above an EMA on D1 or W1, shorts only below it.</summary>
public sealed class HigherTimeframeTrend(SignalTimeframe timeframe, int period) : IEntryConfirmation
{
    public ConfirmationVote Vote(ConfirmationContext c)
    {
        string name = $"{timeframe} trend (EMA {period})";
        if (c.TrendEma is not decimal ema) return new(name, false, false, null, null);
        decimal close = c.SignalBar.Close;
        return new(name, true, c.IsLong ? close > ema : close < ema, close, ema);
    }
}
```

`CRV.Core/Strategy/Confirmations/TimeWindow.cs`:

```csharp
using CRV.Core.Indicators;

namespace CRV.Core.Strategy.Confirmations;

/// <summary>
/// Skips signals in the first and last minutes of the regular session (09:30–16:00 ET):
/// [09:30, 09:30 + first) and (16:00 − last, 16:00]. Signals outside RTH pass.
/// </summary>
public sealed class TimeWindow(int skipFirstMinutes, int skipLastMinutes) : IEntryConfirmation
{
    public static readonly TimeOnly RthOpen = new(9, 30);
    public static readonly TimeOnly RthClose = new(16, 0);

    public ConfirmationVote Vote(ConfirmationContext c)
    {
        var et = TimeOnly.FromDateTime(EasternTime.ToEastern(c.SignalCloseUtc));
        if (et < RthOpen || et > RthClose) return new("Time window", true, true, null, null);

        decimal sinceOpen = (decimal)(et - RthOpen).TotalMinutes;
        decimal toClose   = (decimal)(RthClose - et).TotalMinutes;
        bool passed = sinceOpen >= skipFirstMinutes && toClose >= skipLastMinutes;
        return sinceOpen <= toClose
            ? new("Time window", true, passed, sinceOpen, skipFirstMinutes)
            : new("Time window", true, passed, toClose, skipLastMinutes);
    }
}
```

`CRV.Core/Strategy/Confirmations/RejectionCandle.cs`:

```csharp
namespace CRV.Core.Strategy.Confirmations;

/// <summary>The signal bar closes in the top <c>closeInPct</c> % of its range (long) or the bottom (short).</summary>
public sealed class RejectionCandle(int closeInPct) : IEntryConfirmation
{
    public ConfirmationVote Vote(ConfirmationContext c)
    {
        var b = c.SignalBar;
        decimal range = b.High - b.Low;
        if (range <= 0) return new("Rejection candle", false, false, null, null);

        decimal fromLow = (b.Close - b.Low) / range * 100m;
        decimal towardSide = c.IsLong ? fromLow : 100m - fromLow;
        decimal threshold = 100m - closeInPct;
        return new("Rejection candle", true, towardSide >= threshold, towardSide, threshold);
    }
}
```

`CRV.Core/Strategy/Confirmations/NotStretched.cs`:

```csharp
namespace CRV.Core.Strategy.Confirmations;

/// <summary>The signal close is at most <c>maxAtr</c> × ATR from the EMA (the entry price isn't known when the strategy arms).</summary>
public sealed class NotStretched(decimal maxAtr) : IEntryConfirmation
{
    public ConfirmationVote Vote(ConfirmationContext c)
    {
        if (c.Atr <= 0) return new("Not stretched", false, false, null, null);
        decimal distance = Math.Abs(c.SignalBar.Close - c.Ema) / c.Atr;
        return new("Not stretched", true, distance <= maxAtr, distance, maxAtr);
    }
}
```

`CRV.Core/Strategy/Confirmations/EmaConfirmations.cs`:

```csharp
using CRV.Core.Models;

namespace CRV.Core.Strategy.Confirmations;

public sealed record ConfirmationDecision(bool Passed, int PassedCount, int Required, int SwitchedOn);

/// <summary>All switched-on confirmations, or at least N of them; N is clamped to 1 … the switched-on
/// count like <c>ChopRegimeConfig.Normalize()</c>. An inactive vote is a fail.</summary>
public static class ConfirmationGate
{
    public static int Required(int needed, int switchedOn) =>
        switchedOn == 0 ? 0 : needed <= 0 ? switchedOn : Math.Clamp(needed, 1, switchedOn);

    public static ConfirmationDecision Decide(IReadOnlyList<ConfirmationVote> votes, int needed)
    {
        int passed = votes.Count(v => v.Active && v.Passed);
        int required = Required(needed, votes.Count);
        return new(passed >= required, passed, required, votes.Count);
    }
}

/// <summary>The EMA setup's confirmations as objects that vote, in the order the setup page shows them.</summary>
public static class EmaConfirmations
{
    public static IReadOnlyList<IEntryConfirmation> Build(StrategySetupConfig c) =>
        c.Confirmations
            .Where(k => k.Enabled && (k.Kind != ConfirmationKind.RejectionCandle || c.EmaEntry != EmaEntry.Cross))
            .GroupBy(k => k.Kind).Select(g => g.First())
            .OrderBy(k => k.Kind)
            .Select<ConfirmationConfig, IEntryConfirmation>(k => k.Kind switch
            {
                ConfirmationKind.HtfTrend        => new HigherTimeframeTrend(k.TrendTimeframe, k.TrendEmaPeriod),
                ConfirmationKind.TimeWindow      => new TimeWindow(k.SkipFirstMinutes, k.SkipLastMinutes),
                ConfirmationKind.RejectionCandle => new RejectionCandle(k.CloseInPct),
                _                                => new NotStretched(k.MaxAtrFromEma),
            })
            .ToList();

    /// <summary>One entry per kind in display order: the stored one when there is one, else that kind switched off.</summary>
    public static List<ConfirmationConfig> Normalized(IEnumerable<ConfirmationConfig>? list)
    {
        var stored = (list ?? Enumerable.Empty<ConfirmationConfig>()).ToList();
        return Enum.GetValues<ConfirmationKind>()
            .Select(kind => stored.FirstOrDefault(k => k.Kind == kind) ?? new ConfirmationConfig { Kind = kind })
            .ToList();
    }

    public static string Label(ConfirmationKind kind) => kind switch
    {
        ConfirmationKind.HtfTrend        => "Higher-timeframe trend",
        ConfirmationKind.TimeWindow      => "Time window",
        ConfirmationKind.RejectionCandle => "Rejection candle",
        ConfirmationKind.NotStretched    => "Not stretched",
        _                                => kind.ToString(),
    };
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~ConfirmationTests"`
Expected: PASS (13 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/Confirmations CRV.Core.Tests/Strategy/ConfirmationTests.cs
git commit -m "feat(ema): entry confirmations and the all / at-least-N gate"
```

---

### Task 7: Signal bars from execution bars, stored history first

**Files:**
- Create: `CRV.Core/Strategy/TimeframeFeed.cs`
- Create: `CRV.Core/Strategy/HtfHistory.cs`
- Modify: `CRV.Core/Strategy/ISetupStrategy.cs`
- Test: `CRV.Core.Tests/Strategy/TimeframeFeedTests.cs`

**Interfaces:**
- Consumes: `SessionBarAggregator(tf, executionMinutes)`, `SessionBucket.For`, `HtfBar` (plan 4).
- Produces:
  - `readonly record struct FeedBar(HtfBar Bar, bool Historical, bool Partial)`; `sealed class TimeframeFeed(SignalTimeframe tf, int executionMinutes)`: `SignalTimeframe Timeframe`, `HtfBar? Forming`, `void Seed(IReadOnlyList<HtfBar> closedBars)`, `IReadOnlyList<FeedBar> OnExecutionBar(Bar bar)`.
  - `sealed record HistoryNeed(SignalTimeframe Timeframe, int Bars)`; `interface IHtfHistorySource { Task<IReadOnlyList<HtfBar>> ClosedBeforeAsync(string root, SignalTimeframe tf, DateTime beforeUtc, int count, CancellationToken ct = default); }` (namespace `CRV.Core.Strategy`).
  - On `ISetupStrategy`, defaulted: `void ObserveBar(Bar bar) { }`, `IReadOnlyList<HistoryNeed> HistoryNeeds => Array.Empty<HistoryNeed>();`, `void SeedHistory(SignalTimeframe tf, IReadOnlyList<HtfBar> closedBars) { }`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/TimeframeFeedTests.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>H1 bars from 5-minute bars on Wed 2026-03-18 (H1 buckets are clock-aligned, 10:00Z = 06:00 ET).</summary>
public class TimeframeFeedTests
{
    private static readonly DateTime Day = new(2026, 3, 18, 0, 0, 0, DateTimeKind.Utc);

    private static HtfBar Stored(int hour) => new(SignalTimeframe.H1, Day.AddHours(hour), 1, 2, 0.5m, 1.5m, 10);
    private static Bar Exec(int hour, int minute) => new(Day.AddHours(hour).AddMinutes(minute), 1, 2, 0.5m, 1.5m, 10);

    [Fact]
    public void StoredBarsBeforeTheFirstLiveBucket_ComeFirst_TheLiveBucketJoinedMidwayIsPartial()
    {
        var feed = new TimeframeFeed(SignalTimeframe.H1, 5);
        feed.Seed(new[] { Stored(8), Stored(9), Stored(10), Stored(11) });

        var first = feed.OnExecutionBar(Exec(10, 25));
        Assert.Equal(new[] { Day.AddHours(8), Day.AddHours(9) }, first.Select(f => f.Bar.OpenUtc));
        Assert.All(first, f => Assert.True(f.Historical));

        for (int m = 30; m < 55; m += 5) Assert.Empty(feed.OnExecutionBar(Exec(10, m)));
        var closed = Assert.Single(feed.OnExecutionBar(Exec(10, 55)));
        Assert.Equal((Day.AddHours(10), false, true), (closed.Bar.OpenUtc, closed.Historical, closed.Partial));

        for (int m = 0; m < 55; m += 5) feed.OnExecutionBar(Exec(11, m));
        var full = Assert.Single(feed.OnExecutionBar(Exec(11, 55)));
        Assert.False(full.Partial);
    }

    [Fact]
    public void FirstBarOnTheBucketOpen_IsNotPartial()
    {
        var feed = new TimeframeFeed(SignalTimeframe.H1, 5);
        for (int m = 0; m < 55; m += 5) feed.OnExecutionBar(Exec(10, m));
        Assert.False(Assert.Single(feed.OnExecutionBar(Exec(10, 55))).Partial);
    }

    [Fact]
    public void SeedAfterTheFirstBar_IsIgnored()
    {
        var feed = new TimeframeFeed(SignalTimeframe.H1, 5);
        feed.OnExecutionBar(Exec(10, 0));
        feed.Seed(new[] { Stored(8) });
        Assert.Empty(feed.OnExecutionBar(Exec(10, 5)));
    }

    [Fact]
    public void Forming_IsTheBucketBeingBuilt()
    {
        var feed = new TimeframeFeed(SignalTimeframe.H1, 5);
        feed.OnExecutionBar(Exec(10, 10));
        Assert.Equal(Day.AddHours(10), feed.Forming?.OpenUtc);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~TimeframeFeedTests"`
Expected: build FAILS with `The type or namespace name 'TimeframeFeed' could not be found`.

- [ ] **Step 3: Write the feed and the history types**

`CRV.Core/Strategy/TimeframeFeed.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>A closed signal bar as the feed hands it out: from storage (Historical), or the first
/// live bucket joined part-way through (Partial: its close is right, its high and low may not be).</summary>
public readonly record struct FeedBar(HtfBar Bar, bool Historical, bool Partial);

/// <summary>
/// One signal timeframe built from execution bars, with stored history applied first. On the
/// first execution bar, the stored bars that open before that bar's bucket are handed out in
/// order; after that, each bucket as it closes.
/// </summary>
public sealed class TimeframeFeed
{
    private readonly SessionBarAggregator _aggregator;
    private IReadOnlyList<HtfBar> _seed = Array.Empty<HtfBar>();
    private bool _started;
    private DateTime _firstOpen;
    private bool _firstPartial;

    public TimeframeFeed(SignalTimeframe tf, int executionMinutes)
    {
        Timeframe = tf;
        _aggregator = new SessionBarAggregator(tf, executionMinutes);
    }

    public SignalTimeframe Timeframe { get; }

    /// <summary>The bucket being built, or null between a close and the next bar.</summary>
    public HtfBar? Forming => _aggregator.Forming;

    /// <summary>Stored closed bars to apply before the first live bucket. Ignored once bars have started.</summary>
    public void Seed(IReadOnlyList<HtfBar> closedBars)
    {
        if (!_started) _seed = closedBars.OrderBy(b => b.OpenUtc).ToList();
    }

    public IReadOnlyList<FeedBar> OnExecutionBar(Bar bar)
    {
        var output = new List<FeedBar>();
        if (!_started)
        {
            _started = true;
            _firstOpen = SessionBucket.For(Timeframe, bar.Time).OpenUtc;
            _firstPartial = bar.Time != _firstOpen;
            foreach (var stored in _seed)
                if (stored.OpenUtc < _firstOpen) output.Add(new FeedBar(stored, Historical: true, Partial: false));
            _seed = Array.Empty<HtfBar>();
        }

        if (_aggregator.OnExecutionBar(bar) is { } closed)
            output.Add(new FeedBar(closed, Historical: false, Partial: _firstPartial && closed.OpenUtc == _firstOpen));
        return output;
    }
}
```

`CRV.Core/Strategy/HtfHistory.cs`:

```csharp
using CRV.Core.Indicators;

namespace CRV.Core.Strategy;

/// <summary>How many closed bars of a timeframe a strategy wants loaded at start.</summary>
public sealed record HistoryNeed(SignalTimeframe Timeframe, int Bars);

/// <summary>Stored higher-timeframe bars per root (NQ for NQ and MNQ).</summary>
public interface IHtfHistorySource
{
    /// <summary>The newest <paramref name="count"/> stored bars opening before <paramref name="beforeUtc"/>, oldest first.</summary>
    Task<IReadOnlyList<HtfBar>> ClosedBeforeAsync(string root, SignalTimeframe tf, DateTime beforeUtc, int count, CancellationToken ct = default);
}
```

In `CRV.Core/Strategy/ISetupStrategy.cs` add `using CRV.Core.Indicators;` and, inside `ISetupStrategy` after `void OnTick(...)`, add:

```csharp

    /// <summary>
    /// Every closed bar of the strategy's group, handed over before the session, cutoff and idle
    /// filters, so a strategy that builds higher-timeframe bars sees all of them. The same bar can
    /// arrive again through <see cref="OnBar"/>; a strategy that uses this ignores repeats.
    /// </summary>
    void ObserveBar(Bar bar) { }

    /// <summary>Stored bars this strategy wants at start, per timeframe; empty for strategies that build nothing above the execution bars.</summary>
    IReadOnlyList<HistoryNeed> HistoryNeeds => Array.Empty<HistoryNeed>();

    /// <summary>Stored closed bars of <paramref name="tf"/>, oldest first, applied before the first live bar.</summary>
    void SeedHistory(SignalTimeframe tf, IReadOnlyList<HtfBar> closedBars) { }
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~TimeframeFeedTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/TimeframeFeed.cs CRV.Core/Strategy/HtfHistory.cs CRV.Core/Strategy/ISetupStrategy.cs CRV.Core.Tests/Strategy/TimeframeFeedTests.cs
git commit -m "feat(ema): signal bars from execution bars with stored history applied first"
```

---

### Task 8: Levels for an armed signal

**Files:**
- Create: `CRV.Core/Strategy/EmaLevels.cs`
- Test: `CRV.Core.Tests/Strategy/EmaLevelsTests.cs`

**Interfaces:**
- Consumes: `EmaSignal` (Task 3); `AutoSizeByRiskCalculator.Calc`; `MinRrGuard.Apply`, `LevelRequest.From` (plan 3).
- Produces: `enum EmaSkip { None, StopOnWrongSide, Size, MinRr }`; `sealed record EmaOrder(decimal Entry, decimal Stop, decimal Target, decimal Partial, int Contracts, int PartialContracts)`; `sealed record EmaLevelsResult(EmaOrder? Order, EmaSkip Skip, decimal Entry, decimal Stop, decimal Rr)`; `static class EmaLevels { decimal Stop(EmaStopMode mode, bool isLong, decimal entry, EmaSignal sig, decimal buffer, decimal tickSize); EmaLevelsResult Build(EmaSignal sig, decimal price, StrategySetupConfig c); }`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/EmaLevelsTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>MNQ ($2 a point, tick 0.25). Signal bar 104 / 106 / 100 / 105, EMA 101, ATR 2; entry at 105.</summary>
public class EmaLevelsTests
{
    private static readonly Bar SignalBar = new(new DateTime(2026, 3, 18, 14, 0, 0, DateTimeKind.Utc), 104, 106, 100, 105, 1);

    private static EmaSignal Sig(bool isLong = true, decimal ema = 101m, decimal atr = 2m) => new(isLong, SignalBar, ema, atr);

    private static StrategySetupConfig Cfg(Action<StrategySetupConfig>? tweak = null)
    {
        var c = new StrategySetupConfig
        {
            Ticker = "/MNQZ26", PointValue = 2m, TickSize = 0.25m, Contracts = 1, MaxContracts = 4,
            EmaStopMode = EmaStopMode.EntryAtr, StopBuffer = 1m, TargetMode = TargetMode.RiskMultiple,
            AtrTp1Mult = 1m, AtrTp2Mult = 2m, MinRr = 1.5m, PartialPct = 50,
        };
        tweak?.Invoke(c);
        return c;
    }

    [Theory]
    [InlineData(EmaStopMode.SignalBarExtreme, true,  2,    99.50)]
    [InlineData(EmaStopMode.SignalBarExtreme, false, 2,   106.50)]
    [InlineData(EmaStopMode.EmaAtr,           true,  0.5, 100.00)]
    [InlineData(EmaStopMode.EmaAtr,           false, 0.5, 102.00)]
    [InlineData(EmaStopMode.EntryAtr,         true,  1.5, 102.00)]
    [InlineData(EmaStopMode.EntryAtr,         false, 1.5, 108.00)]
    public void Stop_PerMode(EmaStopMode mode, bool isLong, double buffer, double expected)
        => Assert.Equal((decimal)expected, EmaLevels.Stop(mode, isLong, 105m, Sig(isLong), (decimal)buffer, 0.25m));

    [Fact]
    public void Stop_IsRoundedToTheTick()
        => Assert.Equal(104.25m, EmaLevels.Stop(EmaStopMode.EntryAtr, true, 105m, Sig(atr: 2.1m), 0.3m, 0.25m));   // 104.37 → 104.25

    [Fact]
    public void EmaAtrStopAboveALongEntry_IsNoOrder()
    {
        var r = EmaLevels.Build(Sig(ema: 106m), 105m, Cfg(c => { c.EmaStopMode = EmaStopMode.EmaAtr; c.StopBuffer = 0m; }));
        Assert.Equal(EmaSkip.StopOnWrongSide, r.Skip);
        Assert.Null(r.Order);
    }

    [Fact]
    public void RiskMultiple_PartialAtTp1_TargetAtTp2()
    {
        var o = EmaLevels.Build(Sig(), 105m, Cfg()).Order!;
        Assert.Equal((105m, 103m, 107m, 109m, 1), (o.Entry, o.Stop, o.Partial, o.Target, o.Contracts));
    }

    [Fact]
    public void Atr_TargetsAreMultiplesOfTheSignalAtr()
    {
        var o = EmaLevels.Build(Sig(atr: 4m), 105m, Cfg(c => c.TargetMode = TargetMode.Atr)).Order!;
        Assert.Equal((101m, 109m, 113m), (o.Stop, o.Partial, o.Target));
    }

    [Fact]
    public void Dollars_PerContract()
    {
        var o = EmaLevels.Build(Sig(), 105m, Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 400m; })).Order!;
        Assert.Equal((205m, 305m), (o.Partial, o.Target));   // $400 / $2 = 200 pts, partial 50 %
    }

    [Fact]
    public void BelowMinRr_Skips_OrRaisesTheTarget_OrPassesWithTheGuardOff()
    {
        var skip = EmaLevels.Build(Sig(), 105m, Cfg(c => c.AtrTp2Mult = 1m));
        Assert.Equal((EmaSkip.MinRr, 1m), (skip.Skip, skip.Rr));

        var raised = EmaLevels.Build(Sig(), 105m, Cfg(c => { c.AtrTp2Mult = 1m; c.MinRrAction = MinRrAction.RaiseTarget; })).Order!;
        Assert.Equal((108m, 106.5m), (raised.Target, raised.Partial));   // 1.5 × 2 pts; partial 50 % of it

        var off = EmaLevels.Build(Sig(), 105m, Cfg(c => { c.AtrTp2Mult = 1m; c.EnforceMinRr = false; })).Order!;
        Assert.Equal(107m, off.Target);
    }

    [Fact]
    public void Size_ByRisk_AndRefusedWhenOneContractDoesNotFit()
    {
        var sized = EmaLevels.Build(Sig(), 105m, Cfg(c => { c.AutoSizeByRisk = true; c.MaxTradeRisk = 10m; })).Order!;
        Assert.Equal(2, sized.Contracts);   // $4 a contract → 2 within $10

        Assert.Equal(EmaSkip.Size, EmaLevels.Build(Sig(), 105m, Cfg(c => { c.AutoSizeByRisk = true; c.MaxTradeRisk = 3m; })).Skip);
    }

    [Fact]
    public void EntryTickOffset_MovesTheEntry_AndTheStopFromIt()
    {
        var o = EmaLevels.Build(Sig(), 105m, Cfg(c => c.EntryTickOffset = 1)).Order!;
        Assert.Equal((105.25m, 103.25m), (o.Entry, o.Stop));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaLevelsTests"`
Expected: build FAILS with `The name 'EmaLevels' does not exist in the current context`.

- [ ] **Step 3: Write the levels**

`CRV.Core/Strategy/EmaLevels.cs`:

```csharp
using CRV.Core.Models;

namespace CRV.Core.Strategy;

public enum EmaSkip { None, StopOnWrongSide, Size, MinRr }

public sealed record EmaOrder(decimal Entry, decimal Stop, decimal Target, decimal Partial, int Contracts, int PartialContracts);

/// <summary>The order for an armed signal, or why there is none (with the entry, stop and R it was judged on).</summary>
public sealed record EmaLevelsResult(EmaOrder? Order, EmaSkip Skip, decimal Entry, decimal Stop, decimal Rr);

/// <summary>
/// Entry, stop, size and targets for an armed EMA signal, in plan 3's order: entry with its
/// tick offset → stop → size (the stop only) → targets (they need the size) → R and the guard.
/// </summary>
public static class EmaLevels
{
    /// <summary>
    /// Past the signal bar's low / high by <paramref name="buffer"/> ticks; the signal's EMA ± buffer × ATR;
    /// or entry ± buffer × ATR. On the tick.
    /// </summary>
    public static decimal Stop(EmaStopMode mode, bool isLong, decimal entry, EmaSignal sig, decimal buffer, decimal tickSize)
    {
        decimal raw = mode switch
        {
            EmaStopMode.SignalBarExtreme => isLong ? sig.SignalBar.Low - buffer * tickSize : sig.SignalBar.High + buffer * tickSize,
            EmaStopMode.EmaAtr           => isLong ? sig.Ema - buffer * sig.Atr : sig.Ema + buffer * sig.Atr,
            _                            => isLong ? entry - buffer * sig.Atr : entry + buffer * sig.Atr,
        };
        return LevelCalculator.RoundToTick(raw, tickSize);
    }

    public static EmaLevelsResult Build(EmaSignal sig, decimal price, StrategySetupConfig c)
    {
        bool isLong = sig.IsLong;
        decimal tick = c.TickSize;
        decimal ep = price;
        if (c.EntryTickOffset != 0 && tick > 0)
            ep = LevelCalculator.RoundToTick(isLong ? ep + c.EntryTickOffset * tick : ep - c.EntryTickOffset * tick, tick);

        decimal sl = Stop(c.EmaStopMode, isLong, ep, sig, c.StopBuffer, tick);
        if (isLong ? sl >= ep : sl <= ep) return new(null, EmaSkip.StopOnWrongSide, ep, sl, 0m);

        var (contracts, partialCts) = AutoSizeByRiskCalculator.Calc(ep, sl, c, atrRatio: 0m);
        if (contracts <= 0) return new(null, EmaSkip.Size, ep, sl, 0m);

        var levels = MinRrGuard.Apply(c, LevelRequest.From(c, ep, isLong, sl, contracts, sig.Atr));
        if (levels.Skip) return new(null, EmaSkip.MinRr, ep, sl, levels.Rr);

        return new(new EmaOrder(ep, sl, levels.Target, levels.Partial, contracts, partialCts), EmaSkip.None, ep, sl, levels.Rr);
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaLevelsTests"`
Expected: PASS (14 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/EmaLevels.cs CRV.Core.Tests/Strategy/EmaLevelsTests.cs
git commit -m "feat(ema): stops, size and guarded targets for an armed EMA signal"
```

---

### Task 9: The strategy

**Files:**
- Create: `CRV.Core/Strategy/EmaStrategy.cs`
- Modify: `CRV.Core/Strategy/StrategyFactory.cs`
- Modify: `CRV.Core/Strategy/EmaHistoryGate.cs` (drop the placeholder constant)
- Create: `CRV.Core.Tests/Strategy/EmaFixture.cs`
- Test: `CRV.Core.Tests/Strategy/EmaStrategyTests.cs`

**Interfaces:**
- Consumes: Tasks 1–8; `ISetupStrategy` incl. `CloseAtRthClose` (plan 2); `SizeRefusalGate.Report/ReportMinRr/LastMinRrSkip/Reset` (plan 3); `EmaIndicator`, `AtrIndicator`, `HistoryRequirement.For` (plan 4).
- Produces:
  - `sealed record EmaSignalShape(EmaSource Source, EmaEntry Entry, int Period, int FastPeriod, int SlowPeriod, RetestEmaChoice RetestEma, SignalTimeframe Timeframe, bool EveryExecutionBar, bool TrendOn, SignalTimeframe TrendTimeframe, int TrendPeriod)` with `static From(StrategySetupConfig)`.
  - `sealed class EmaStrategy(StrategySetupConfig cfg) : ISetupStrategy` with `const int AtrPeriod = 14`, `int SignalBarsClosed`, and the `ISetupStrategy` members incl. `ObserveBar`, `HistoryNeeds`, `SeedHistory`.
  - `StrategyFactory.Create` returns `EmaStrategy` for `StrategyType.Ema`.
  - `EmaFixture` (test helper): `T0`, `Closes`, `Bars(decimal[] closes, DateTime t0)`, `Cfg(EmaSource, EmaEntry, EmaDirection)`.

- [ ] **Step 1: Write the fixture**

`CRV.Core.Tests/Strategy/EmaFixture.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// Sixty 5-minute bars from Wed 2026-03-18 10:00 ET: flat at 100, a fall, a rise with a pullback, a
/// fall with a bounce. Each bar opens at the previous close and reaches 0.5 past its open and close.
/// The signal bars listed in <see cref="EmaStrategyTests"/> were computed by hand-checked arithmetic
/// on these closes (EMA 8 / EMA 8 vs 21, ATR 14 Wilder) and pinned against the Pine reading the parity
/// plan uses; the flat start sits exactly on the EMA, so it never crosses.
/// </summary>
internal static class EmaFixture
{
    public static readonly DateTime T0 = new(2026, 3, 18, 14, 0, 0, DateTimeKind.Utc);

    public static readonly decimal[] Closes = Build();

    private static decimal[] Build()
    {
        var c = new List<decimal>(Enumerable.Repeat(100m, 16));
        for (int i = 0; i < 10; i++) c.Add(99 - i);           // 16–25: 99 … 90
        for (int i = 0; i < 8; i++) c.Add(92 + 2 * i);        // 26–33: 92 … 106
        c.AddRange(new[] { 105m, 104m, 103m });               // 34–36
        for (int i = 0; i < 6; i++) c.Add(105 + 2 * i);       // 37–42: 105 … 115
        for (int i = 1; i <= 8; i++) c.Add(115 - 2 * i);      // 43–50: 113 … 99
        for (int i = 1; i <= 3; i++) c.Add(99 + i);           // 51–53: 100 … 102
        for (int i = 1; i <= 6; i++) c.Add(102 - 2 * i);      // 54–59: 100 … 90
        return c.ToArray();
    }

    public static List<Bar> Bars(decimal[] closes, DateTime t0)
    {
        var bars = new List<Bar>();
        decimal open = 100m;
        for (int i = 0; i < closes.Length; i++)
        {
            decimal close = closes[i];
            bars.Add(new Bar(t0.AddMinutes(5 * i), open, Math.Max(open, close) + 0.5m, Math.Min(open, close) - 0.5m, close, 100));
            open = close;
        }
        return bars;
    }

    /// <summary>M5 signals on 5-minute bars, EMA 8 (or 8 / 21), ATR stop × 1, 1R / 2R, no confirmations.</summary>
    public static StrategySetupConfig Cfg(EmaSource source, EmaEntry entry, EmaDirection dir)
    {
        var c = new StrategySetupConfig
        {
            Id = "ema-test", Name = "EMA test", SetupId = SetupId.F, StrategyType = StrategyType.Ema, Enabled = true,
            Ticker = "/MNQZ26", PointValue = 2m, TickSize = 0.25m, ExecutionTFMinutes = 5,
            EmaSource = source, EmaEntry = entry, EmaDirection = dir,
            EmaPeriod = 8, FastEma = 8, SlowEma = 21, RetestEma = RetestEmaChoice.Fast, SignalTimeframe = SignalTimeframe.M5,
            EmaStopMode = EmaStopMode.EntryAtr, StopBuffer = 1m,
            TargetMode = TargetMode.RiskMultiple, AtrTp1Mult = 1m, AtrTp2Mult = 2m, MinRr = 1.5m,
            Contracts = 1, MaxContracts = 1, MaxTrades = 10, UsePartial = false, ConfirmationsNeeded = 0,
        };
        (c.AllowLong, c.AllowShort) = dir.Sides();
        foreach (var k in c.Confirmations) k.Enabled = false;
        return c;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`CRV.Core.Tests/Strategy/EmaStrategyTests.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class EmaStrategyTests
{
    /// <summary>
    /// Drives the strategy the way the engine does: the next bar's first tick, then the bar. Returns
    /// (signal bar index, long?) for every entry, and checks no entry is ever made on the bar that signalled.
    /// </summary>
    private static List<(int Index, bool IsLong)> Run(ISetupStrategy s, IReadOnlyList<Bar> bars, bool observeFirst = false)
    {
        var entries = new List<(int, bool)>();
        for (int i = 0; i < bars.Count; i++)
        {
            s.OnTick(bars[i].Open, bars[i].Time, default, default, default);
            if (s.PendingEntry is { } e)
            {
                Assert.Equal(bars[i].Time, e.Time);    // the next bar's open, never the signal bar's
                Assert.Equal(bars[i].Open, e.Entry);
                entries.Add((i - 1, e.Direction == Direction.Long));
                s.ClearPendingSignals();
            }
            if (observeFirst) s.ObserveBar(bars[i]);
            s.OnBar(bars[i], default, default, default);
            Assert.Null(s.PendingEntry);
        }
        return entries;
    }

    public static TheoryData<EmaSource, EmaEntry, EmaDirection, int[], int[]> Combinations()
    {
        var data = new TheoryData<EmaSource, EmaEntry, EmaDirection, int[], int[]>();
        void Add(EmaSource s, EmaEntry e, int[] longs, int[] shorts)
        {
            data.Add(s, e, EmaDirection.Up, longs, Array.Empty<int>());
            data.Add(s, e, EmaDirection.Down, Array.Empty<int>(), shorts);
            data.Add(s, e, EmaDirection.Both, longs, shorts);
        }
        Add(EmaSource.PriceVsEma, EmaEntry.Touch,       new[] { 28, 37, 44 }, new[] { 17, 46, 53 });
        Add(EmaSource.PriceVsEma, EmaEntry.Cross,       new[] { 27 },         new[] { 45 });
        Add(EmaSource.PriceVsEma, EmaEntry.CrossRetest, new[] { 28 },         new[] { 46 });
        Add(EmaSource.EmaVsEma,   EmaEntry.Cross,       new[] { 31 },         new[] { 50 });
        Add(EmaSource.EmaVsEma,   EmaEntry.CrossRetest, new[] { 37 },         new[] { 53 });
        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EverySourceEntryDirection_SignalsOnTheExpectedBars(EmaSource source, EmaEntry entry, EmaDirection dir, int[] longs, int[] shorts)
    {
        var bars = EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0);

        var entries = Run(new EmaStrategy(EmaFixture.Cfg(source, entry, dir)), bars);

        Assert.Equal(longs, entries.Where(e => e.IsLong).Select(e => e.Index));
        Assert.Equal(shorts, entries.Where(e => !e.IsLong).Select(e => e.Index));
    }

    [Fact]
    public void ObserveBarThenOnBar_GivesTheSameSignalsAsOnBarAlone()
    {
        var bars = EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0);
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Both);

        Assert.Equal(Run(new EmaStrategy(cfg), bars), Run(new EmaStrategy(cfg), bars, observeFirst: true));
    }

    [Fact]
    public void ObserveBar_SameBarTwice_CountsOnce()
    {
        var s = new EmaStrategy(EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Both));
        var bar = EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0)[0];
        s.ObserveBar(bar);
        s.ObserveBar(bar);
        s.OnBar(bar, default, default, default);
        Assert.Equal(1, s.SignalBarsClosed);
    }

    [Fact]
    public void ArmNotTakenOnTheNextBar_Expires()
    {
        var bars = EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0);
        var s = new EmaStrategy(EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up));
        for (int i = 0; i <= 27; i++) s.OnBar(bars[i], default, default, default);   // cross up on 27, armed
        Assert.True(s.IsArmed);

        s.OnBar(bars[28], default, default, default);   // no tick came during bar 28
        s.OnTick(bars[29].Open, bars[29].Time, default, default, default);

        Assert.Null(s.PendingEntry);
    }

    // Touch fixture: flat at 100, rising to 108, then two bars that dip to the EMA.
    // Bar 20: low 103.75 vs EMA 104.33, close 107: 1.57 ATR from the EMA. Bar 21: low 104.5 vs
    // EMA 104.81, close 106.5: 0.96 ATR. No re-arm between them.
    private static List<Bar> TwoTouches()
    {
        var bars = EmaFixture.Bars(Enumerable.Repeat(100m, 16).Concat(new[] { 102m, 104m, 106m, 108m }).ToArray(), EmaFixture.T0);
        bars.Add(new Bar(EmaFixture.T0.AddMinutes(100), 108, 108, 103.75m, 107, 100));
        bars.Add(new Bar(EmaFixture.T0.AddMinutes(105), 107, 107, 104.5m, 106.5m, 100));
        bars.Add(new Bar(EmaFixture.T0.AddMinutes(110), 106.5m, 107, 106, 106.5m, 100));
        return bars;
    }

    [Fact]
    public void Touch_AcceptedArm_IsUsed_SoTheNextTouchingBarDoesNotSignal()
        => Assert.Equal(new[] { (20, true) }, Run(new EmaStrategy(EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Up)), TwoTouches()));

    [Fact]
    public void Touch_RejectedByConfirmations_KeepsTheArm()
    {
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Up);
        var notStretched = cfg.Confirmations.Single(k => k.Kind == ConfirmationKind.NotStretched);
        notStretched.Enabled = true;
        notStretched.MaxAtrFromEma = 1.2m;    // bar 20 (1.57 ATR) fails, bar 21 (0.96 ATR) passes

        Assert.Equal(new[] { (21, true) }, Run(new EmaStrategy(cfg), TwoTouches()));
    }

    [Fact]
    public void InactiveConfirmation_IsAFail()
    {
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Up);
        cfg.Confirmations.Single(k => k.Kind == ConfirmationKind.HtfTrend).Enabled = true;   // no D1 bars at all

        Assert.Empty(Run(new EmaStrategy(cfg), TwoTouches()));
    }

    [Fact]
    public void CheckEveryExecutionBar_TouchOnAnExecutionBar_AgainstTheLastClosedSignalEma()
    {
        // H1 signal bars on 5-minute bars. Sixteen stored hours: fifteen at 100, the last closing at 102
        // (EMA 8 = 100.44, ATR 1.14). Inside the next hour a 5-minute bar dips to 100.6, within 0.25 of
        // that EMA, and closes above it: only the execution-bar check can see it before the hour closes.
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Up);
        cfg.SignalTimeframe = SignalTimeframe.H1;
        cfg.CheckEveryExecutionBar = true;
        var s = new EmaStrategy(cfg);
        var start = new DateTime(2026, 3, 18, 0, 0, 0, DateTimeKind.Utc);
        s.SeedHistory(SignalTimeframe.H1, Enumerable.Range(0, 16)
            .Select(i => i < 15
                ? new HtfBar(SignalTimeframe.H1, start.AddHours(i), 100, 100.5m, 99.5m, 100, 1)
                : new HtfBar(SignalTimeframe.H1, start.AddHours(i), 100, 102.5m, 99.5m, 102, 1)).ToList());

        var t = start.AddHours(16);
        s.OnBar(new Bar(t, 102, 103, 101.5m, 102.5m, 1), default, default, default);                       // stored hours applied
        s.OnBar(new Bar(t.AddMinutes(5), 102.5m, 103, 102, 102.5m, 1), default, default, default);          // above the EMA
        s.OnBar(new Bar(t.AddMinutes(10), 102.5m, 102.5m, 100.6m, 101.5m, 1), default, default, default);   // dips within 0.25

        s.OnTick(101.5m, t.AddMinutes(15), default, default, default);
        Assert.Equal(Direction.Long, Assert.IsType<EntrySignal>(s.PendingEntry).Direction);
    }

    [Fact]
    public void H8_BarEndingAt1700_SignalsOnItsClose_AndEntersOnTheFirstBarOfTheNextSession()
    {
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up);
        cfg.SignalTimeframe = SignalTimeframe.H8;
        var s = new EmaStrategy(cfg);
        var open = new DateTime(2026, 3, 18, 14, 0, 0, DateTimeKind.Utc);   // 10:00 ET: the 10:00–17:00 H8 bar
        s.SeedHistory(SignalTimeframe.H8, Enumerable.Range(1, 30).Reverse().Select(k =>
        {
            decimal close = k <= 5 ? 99m : 100m;   // the last five stored bars closed below the EMA
            return new HtfBar(SignalTimeframe.H8, open.AddHours(-8 * k), close, close + 0.5m, close - 0.5m, close, 100);
        }).ToList());

        for (int i = 0; i < 84; i++)   // 10:00 … 16:55 ET, rising from 99 to 110
        {
            decimal o = 99m + 11m * i / 84m, c = 99m + 11m * (i + 1) / 84m;
            var bar = new Bar(open.AddMinutes(5 * i), o, Math.Max(o, c) + 0.25m, Math.Min(o, c) - 0.25m, c, 100);
            s.OnTick(bar.Open, bar.Time, default, default, default);
            Assert.Null(s.PendingEntry);
            s.OnBar(bar, default, default, default);
        }

        var next = new DateTime(2026, 3, 18, 22, 0, 0, DateTimeKind.Utc);   // 18:00 ET
        s.OnTick(110.25m, next, default, default, default);
        var entry = Assert.IsType<EntrySignal>(s.PendingEntry);
        Assert.Equal((Direction.Long, next, 110.25m), (entry.Direction, entry.Time, entry.Entry));
    }

    [Fact]
    public void FirstBucketJoinedMidway_NeverSignals()
    {
        static EmaStrategy Seeded(out DateTime bucket)
        {
            var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up);
            cfg.SignalTimeframe = SignalTimeframe.M15;
            var s = new EmaStrategy(cfg);
            bucket = new DateTime(2026, 3, 18, 14, 0, 0, DateTimeKind.Utc);
            var b = bucket;
            s.SeedHistory(SignalTimeframe.M15, Enumerable.Range(1, 30).Reverse().Select(k =>
            {
                decimal close = k <= 5 ? 99m : 100m;
                return new HtfBar(SignalTimeframe.M15, b.AddMinutes(-15 * k), close, close + 0.5m, close - 0.5m, close, 100);
            }).ToList());
            return s;
        }

        // Joined at 14:05: the 14:00 bucket is partial; its close crosses up, but it may not signal.
        var midway = Seeded(out var bucket);
        midway.OnBar(new Bar(bucket.AddMinutes(5), 99, 105, 99, 105, 1), default, default, default);
        midway.OnBar(new Bar(bucket.AddMinutes(10), 105, 110, 105, 110, 1), default, default, default);
        midway.OnTick(110, bucket.AddMinutes(15), default, default, default);
        Assert.Null(midway.PendingEntry);

        // Joined at 14:00: the same bucket is whole and signals.
        var whole = Seeded(out bucket);
        whole.OnBar(new Bar(bucket, 99, 100, 99, 100, 1), default, default, default);
        whole.OnBar(new Bar(bucket.AddMinutes(5), 100, 105, 100, 105, 1), default, default, default);
        whole.OnBar(new Bar(bucket.AddMinutes(10), 105, 110, 105, 110, 1), default, default, default);
        whole.OnTick(110, bucket.AddMinutes(15), default, default, default);
        Assert.NotNull(whole.PendingEntry);
    }

    [Fact]
    public void Resets_KeepInTrade_AndTheSignalState()
    {
        var bars = EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0);
        var s = new EmaStrategy(EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.CrossRetest, EmaDirection.Down));
        for (int i = 0; i <= 45; i++) s.OnBar(bars[i], default, default, default);   // crossed down on 45
        s.SetInTrade(true);

        s.ResetSession();
        s.Reset();
        s.ResetTradeCounters();

        Assert.True(s.InTrade);
        Assert.Equal(-1, s.GetSnapshot().State);   // still waiting for the retest
    }

    [Fact]
    public void Reconfigure_KeepsTheSignalShape_AndTakesNewThresholds()
    {
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up);
        var s = new EmaStrategy(cfg);
        var changed = EmaFixture.Cfg(EmaSource.EmaVsEma, EmaEntry.Touch, EmaDirection.Up);
        s.Reconfigure(changed);

        var entries = Run(s, EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0));

        Assert.Equal(new[] { (27, true) }, entries);   // still a price vs EMA 8 cross
        Assert.False(EmaSignalShape.From(cfg) == EmaSignalShape.From(changed));
    }

    [Fact]
    public void Factory_CreatesTheEmaStrategy_AndItReportsItsSwitches()
    {
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up);
        cfg.CloseAtRthClose = false;
        var s = StrategyFactory.Create(cfg);

        Assert.IsType<EmaStrategy>(s);
        Assert.Equal(StrategyType.Ema, s.StrategyType);
        Assert.False(s.CloseAtRthClose);
    }

    [Fact]
    public void HistoryNeeds_ParityDepthPlusAtr_ForTheSignalAndTrendTimeframes()
    {
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up);
        cfg.SignalTimeframe = SignalTimeframe.H4;
        cfg.EmaPeriod = 21;
        cfg.Confirmations[0].Enabled = true;   // trend: D1 EMA 200

        Assert.Equal(new[] { new HistoryNeed(SignalTimeframe.H4, 63 + 14), new HistoryNeed(SignalTimeframe.D1, 600) },
            new EmaStrategy(cfg).HistoryNeeds);
    }
}
```

- [ ] **Step 3: Write the strategy**

`CRV.Core/Strategy/EmaStrategy.cs`:

```csharp
using System.Globalization;
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Modules;
using CRV.Core.Strategy.Confirmations;

namespace CRV.Core.Strategy;

/// <summary>
/// The settings that decide how the EMA strategy builds its bars and EMAs. Fixed when the
/// strategy is built; a change needs an engine restart.
/// </summary>
public sealed record EmaSignalShape(
    EmaSource Source, EmaEntry Entry, int Period, int FastPeriod, int SlowPeriod, RetestEmaChoice RetestEma,
    SignalTimeframe Timeframe, bool EveryExecutionBar, bool TrendOn, SignalTimeframe TrendTimeframe, int TrendPeriod)
{
    public static EmaSignalShape From(StrategySetupConfig c)
    {
        var trend = c.Confirmations.FirstOrDefault(k => k.Kind == ConfirmationKind.HtfTrend && k.Enabled);
        return new(c.EmaSource, c.EmaEntry, c.EmaPeriod, c.FastEma, c.SlowEma, c.RetestEma, c.SignalTimeframe,
            c.CheckEveryExecutionBar && c.EmaEntry != EmaEntry.Cross,
            trend != null, trend?.TrendTimeframe ?? SignalTimeframe.D1, trend?.TrendEmaPeriod ?? 200);
    }
}

/// <summary>
/// EMA touch, cross, or cross + retest on closed signal-timeframe bars built from the execution
/// bars. A signal arms on the signal bar's close once the confirmations pass, and enters on the
/// first tick of the next execution bar. Pure signal generator: BrokerEventHandler owns the trade.
/// </summary>
public sealed class EmaStrategy : ISetupStrategy
{
    public const int AtrPeriod = 14;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private StrategySetupConfig _cfg;
    private readonly EmaSignalShape _shape;
    private readonly TimeframeFeed _signalFeed;
    private readonly EmaIndicator _ema;       // the EMA, or the slow EMA
    private readonly EmaIndicator? _fast;     // two EMAs only
    private readonly AtrIndicator _atr = new(AtrPeriod);
    private readonly TimeframeFeed? _trendFeed;
    private readonly EmaIndicator? _trendEma;
    private readonly TouchDetector _touch = new();
    private readonly CrossDetector _cross = new();
    private readonly CrossRetestDetector _retest = new();

    private DateTime _lastObserved = DateTime.MinValue;
    private bool _lineWasReady;
    private EmaBar? _lastSignalBar;
    private EmaSignal? _candidate;   // this bar's signal, waiting for OnBar
    private EmaSignal? _armed;       // accepted, waiting for the next bar's first tick
    private ConfirmationDecision? _lastDecision;

    private bool _inTrade;
    private bool _pastCutoff;
    private int _tradeCount, _longCount, _shortCount;
    private int _wins, _losses;
    private decimal _winPnl, _lossPnl;
    private EntrySignal? _pendingEntry;
    private SizeRefusal? _pendingSizeRefusal;
    private readonly SizeRefusalGate _refusalGate = new();

    public EmaStrategy(StrategySetupConfig cfg)
    {
        _cfg = cfg;
        _shape = EmaSignalShape.From(cfg);
        int barMinutes = Math.Max(1, cfg.ExecutionTFMinutes);
        bool two = _shape.Source == EmaSource.EmaVsEma;
        _signalFeed = new TimeframeFeed(_shape.Timeframe, barMinutes);
        _ema = new EmaIndicator(two ? _shape.SlowPeriod : _shape.Period);
        _fast = two ? new EmaIndicator(_shape.FastPeriod) : null;
        if (_shape.TrendOn)
        {
            _trendFeed = new TimeframeFeed(_shape.TrendTimeframe, barMinutes);
            _trendEma = new EmaIndicator(_shape.TrendPeriod);
        }
    }

    // ── Identity ─────────────────────────────────────────────────
    public string       Id               => _cfg.Id;
    public SetupId      SetupId          => _cfg.SetupId;
    public StrategyType StrategyType     => _cfg.StrategyType;
    public string       Name             => _cfg.Name;
    public string       Ticker           => _cfg.Ticker;
    public decimal      PointValue       => _cfg.PointValue;
    public TimeOnly     OrbStart         => _cfg.OrbStart;
    public TimeOnly     OrbEnd           => _cfg.OrbEnd;
    public bool         UseEmaFilter     => _cfg.UseEmaFilter;
    public bool         BypassChopFilter => _cfg.BypassChopFilter;
    public bool         CloseAtRthClose  => _cfg.CloseAtRthClose;
    public int          CutoffHour       => _cfg.CutoffHour;
    public int          CutoffMinute     => _cfg.CutoffMinute;
    public (int Hour, int Minute) GetCutoffForSession(string s) => _cfg.GetCutoffForSession(s);
    public bool IsEnabledForSession(string s) => _cfg.IsEnabledForSession(s);

    public bool IsActive => _inTrade;
    public bool IsArmed  => _armed != null || _retest.Phase == RetestPhase.Crossed;
    public bool InTrade  => _inTrade;
    public void SetInTrade(bool active) => _inTrade = active;

    /// <summary>Signal-timeframe bars folded in so far, stored and live.</summary>
    public int SignalBarsClosed { get; private set; }

    public void SeedTradeCount(int longs, int shorts)
    {
        _longCount = longs; _shortCount = shorts; _tradeCount = longs + shorts;
    }

    // ── Pending signals ──────────────────────────────────────────
    public EntrySignal? PendingEntry => _pendingEntry;
    public SizeRefusal? PendingSizeRefusal => _pendingSizeRefusal;
    public void ClearPendingSignals() { _pendingEntry = null; _pendingSizeRefusal = null; }

    public void Reconfigure(StrategySetupConfig config) => _cfg = config;

    public void RevertEntry()
    {
        if (_pendingEntry == null) return;
        if (_pendingEntry.Direction == Direction.Long) { if (_longCount > 0) _longCount--; }
        else if (_shortCount > 0) _shortCount--;
        _tradeCount = _longCount + _shortCount;
        _pendingEntry = null;
    }

    // ── Resets: the bars, EMAs and signal state run across days and sessions ──
    public void Reset()
    {
        _tradeCount = 0; _longCount = 0; _shortCount = 0;
        _wins = 0; _losses = 0; _winPnl = 0; _lossPnl = 0;
        _pastCutoff = false; _armed = null; _candidate = null;
        _refusalGate.Reset();
        ClearPendingSignals();
    }

    public void ResetSession()
    {
        _tradeCount = 0; _longCount = 0; _shortCount = 0;
        _pastCutoff = false; _armed = null; _candidate = null;
        _refusalGate.Reset();
        ClearPendingSignals();
    }

    public void ResetTradeCounters()
    {
        _tradeCount = 0; _longCount = 0; _shortCount = 0;
        _wins = 0; _losses = 0; _winPnl = 0; _lossPnl = 0;
        _pastCutoff = false;
    }

    public void Disarm()
    {
        if (!_inTrade) { _armed = null; _candidate = null; _pastCutoff = true; }
    }

    public void ResetCutoff() => _pastCutoff = false;

    public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd)
    {
        _pendingEntry = null;
        _armed = null;
    }

    // ── History ──────────────────────────────────────────────────
    public IReadOnlyList<HistoryNeed> HistoryNeeds
    {
        get
        {
            int period = _fast is null ? _shape.Period : _shape.SlowPeriod;
            var needs = new List<HistoryNeed> { new(_shape.Timeframe, HistoryRequirement.For(period).ParityNeeded + AtrPeriod) };
            if (_shape.TrendOn) needs.Add(new(_shape.TrendTimeframe, HistoryRequirement.For(_shape.TrendPeriod).ParityNeeded));
            return needs;
        }
    }

    public void SeedHistory(SignalTimeframe tf, IReadOnlyList<HtfBar> closedBars)
    {
        if (tf == _shape.Timeframe) _signalFeed.Seed(closedBars);
        if (_trendFeed != null && tf == _shape.TrendTimeframe) _trendFeed.Seed(closedBars);
    }

    // ── Bars ─────────────────────────────────────────────────────
    public void ObserveBar(Bar bar)
    {
        if (!_cfg.Enabled || !bar.IsConfirmed || bar.Time <= _lastObserved) return;
        _lastObserved = bar.Time;
        _candidate = null;
        var o = EmaSignalOptions.From(_cfg);

        // Option B: this execution bar against the last closed signal bar, before it joins the next one.
        if (_shape.EveryExecutionBar && _lastSignalBar is { } last)
        {
            var exec = new EmaBar(bar, bar.Close, last.Reference, last.RetestLine, last.Atr);
            _candidate = _shape.Entry == EmaEntry.Touch
                ? _touch.OnBar(exec, o, _cfg.AllowLong, _cfg.AllowShort)
                : _retest.OnExecutionBar(exec, o, _cfg.AllowLong, _cfg.AllowShort);
        }

        if (_trendFeed != null)
            foreach (var t in _trendFeed.OnExecutionBar(bar)) _trendEma!.Add(t.Bar.Close);

        foreach (var s in _signalFeed.OnExecutionBar(bar))
        {
            var signal = OnSignalBar(s.Bar, o);
            if (!s.Historical && !s.Partial) _candidate ??= signal;
        }
    }

    public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        if (!_cfg.Enabled || !bar.IsConfirmed) return;
        ObserveBar(bar);

        _armed = null;   // an arm the next bar's first tick didn't take has expired
        var signal = _candidate;
        _candidate = null;
        if (signal is null || _inTrade || !SideOpen(signal.IsLong)) return;

        var trendEma = _trendEma is { IsReady: true } t ? t.Value : (decimal?)null;
        var context = new ConfirmationContext(signal.IsLong, signal.SignalBar,
            bar.Time.AddMinutes(_cfg.ExecutionTFMinutes), signal.Ema, signal.Atr, trendEma);
        var votes = EmaConfirmations.Build(_cfg).Select(c => c.Vote(context)).ToList();
        _lastDecision = ConfirmationGate.Decide(votes, _cfg.ConfirmationsNeeded);
        if (!_lastDecision.Passed) return;   // a rejected signal keeps its arm

        if (_shape.Entry == EmaEntry.Touch) _touch.Accept(signal);
        else if (_shape.Entry == EmaEntry.CrossRetest) _retest.Accept(EmaSignalOptions.From(_cfg));
        _armed = signal;
    }

    public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        if (!_cfg.Enabled || _armed is not { } signal) return;
        _armed = null;
        if (_inTrade || !SideOpen(signal.IsLong)) return;
        TryEntry(signal, price, utc);
    }

    private EmaSignal? OnSignalBar(HtfBar h, EmaSignalOptions o)
    {
        var bar = new Bar(h.OpenUtc, h.Open, h.High, h.Low, h.Close, h.Volume);
        bool lineWasReady = _lineWasReady;
        _ema.Add(h.Close);
        _fast?.Add(h.Close);
        _atr.Update(bar);
        SignalBarsClosed++;
        _lineWasReady = (_fast ?? _ema).IsReady;

        if (!(lineWasReady && _ema.IsReady && _atr.IsReady))
        {
            if (_ema.IsReady && _shape.Entry == EmaEntry.Touch && !_shape.EveryExecutionBar) _touch.Remember(h.Close, _ema.Value);
            return null;
        }

        decimal reference = _ema.Value;
        decimal retestLine = _fast != null && _shape.RetestEma == RetestEmaChoice.Fast ? _fast.Value : reference;
        var eb = new EmaBar(bar, _fast?.Value ?? h.Close, reference, retestLine, _atr.Value);
        _lastSignalBar = eb;

        return _shape.Entry switch
        {
            EmaEntry.Touch => _shape.EveryExecutionBar ? null : _touch.OnBar(eb, o, _cfg.AllowLong, _cfg.AllowShort),
            EmaEntry.Cross => _cross.OnBar(eb, o) is int side && (side > 0 ? _cfg.AllowLong : _cfg.AllowShort)
                ? new EmaSignal(side > 0, bar, reference, eb.Atr) : null,
            _ => _retest.OnSignalBar(eb, o, _cfg.AllowLong, _cfg.AllowShort, checkRetest: !_shape.EveryExecutionBar),
        };
    }

    private bool SideOpen(bool isLong) =>
        _tradeCount < _cfg.MaxTrades &&
        (isLong ? _cfg.AllowLong && _longCount < _cfg.EffectiveMaxLong : _cfg.AllowShort && _shortCount < _cfg.EffectiveMaxShort);

    private void TryEntry(EmaSignal signal, decimal price, DateTime time)
    {
        var r = EmaLevels.Build(signal, price, _cfg);
        switch (r.Skip)
        {
            case EmaSkip.Size:
                _pendingSizeRefusal = _refusalGate.Report(signal.IsLong, r.Entry, r.Stop, _cfg, time);
                return;
            case EmaSkip.MinRr:
                _pendingSizeRefusal = _refusalGate.ReportMinRr(signal.IsLong, r.Entry, r.Stop, r.Rr, _cfg, time);
                return;
            case EmaSkip.StopOnWrongSide:
                return;
        }

        var order = r.Order!;
        bool trail = _cfg.AutoTrail?.Enabled == true;
        _pendingEntry = new EntrySignal(
            _cfg.SetupId, signal.IsLong ? Direction.Long : Direction.Short,
            order.Entry, order.Stop, order.Target, order.Partial, order.Contracts, time,
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: order.PartialContracts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe,
            AutoTrailStopLoss: trail ? LevelCalculator.RoundToTick(_cfg.AutoTrail!.StopLoss * signal.Atr, _cfg.TickSize) : null,
            AutoTrailTrigger:  trail && _cfg.AutoTrail!.Trigger.HasValue ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Trigger.Value * signal.Atr, _cfg.TickSize) : null,
            AutoTrailFreq:     trail ? LevelCalculator.RoundToTick(_cfg.AutoTrail!.Freq * signal.Atr, _cfg.TickSize) : null);

        if (signal.IsLong) _longCount++;
        else _shortCount++;
        _tradeCount = _longCount + _shortCount;
    }

    // ── Snapshot ─────────────────────────────────────────────────
    public SetupStateSnapshot GetSnapshot() => new()
    {
        Id = _cfg.Id, SetupId = _cfg.SetupId, Name = _cfg.Name,
        State = _armed is { } a ? (a.IsLong ? 1 : -1) : _retest.Phase == RetestPhase.Crossed ? _retest.CrossSide : 0,
        IsActive = IsActive, IsArmed = IsArmed, PastCutoff = _pastCutoff,
        TradeCount = _tradeCount, MaxTrades = _cfg.MaxTrades,
        Enabled = _cfg.Enabled,
        Wins = _wins, Losses = _losses, WinPnl = _winPnl, LossPnl = _lossPnl,
        Expectancy = (_wins + _losses) > 0 ? (_winPnl + _lossPnl) / (_wins + _losses) : 0m,
        MinRrEnforced = _cfg.EnforceMinRr,
        MinRr = _cfg.MinRr,
        LastSkip = _refusalGate.LastMinRrSkip?.Describe(),
    };
}
```

`Inv` is used by the card cells in Task 11; leave it.

In `CRV.Core/Strategy/StrategyFactory.cs`, before the `_ => throw …` arm add:

```csharp
        StrategyType.Ema            => new EmaStrategy(config),
```

In `CRV.Core/Strategy/EmaHistoryGate.cs` delete `AtrPeriodUntilEmaStrategy` and use `EmaStrategy.AtrPeriod`.

- [ ] **Step 4: Run them**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaStrategyTests|FullyQualifiedName~EmaValidationTests"`
Expected: PASS. If a combination row fails, print each signal bar's close, EMA and ATR before changing anything: the fixture's expectations are the plan-6 Pine reading of the rules (Decision 2), and a difference is a rule read differently, not a fixture to edit.

- [ ] **Step 5: Run the suite**

Run: `dotnet test CRV.Core.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Strategy/EmaStrategy.cs CRV.Core/Strategy/StrategyFactory.cs CRV.Core/Strategy/EmaHistoryGate.cs \
  CRV.Core.Tests/Strategy/EmaFixture.cs CRV.Core.Tests/Strategy/EmaStrategyTests.cs
git commit -m "feat(ema): EmaStrategy — touch, cross and cross + retest on closed signal bars"
```

---

### Task 10: Every closed bar reaches the strategy; stored history at start

**Files:**
- Modify: `CRV.Core/Strategy/TickerGroup.cs` (new `ObserveBarAsync`)
- Modify: `CRV.Core/Strategy/ComposableEngine.cs` (`ProcessBarAsync`, `WarmupBarAsync`)
- Modify: `CRV.Core/Strategy/HtfHistory.cs` (`HtfHistorySeeder`)
- Modify: `CRV.Core/Data/HtfBarStore.cs` (`ClosedBeforeAsync`, implements `IHtfHistorySource`)
- Modify: `CRV.Backtest/Engine/BacktestEngine.cs`, `CRV.Web/Services/BacktestRunnerService.cs`, `CRV.Web/Services/LiveEngineOrchestrator.cs`
- Test: `CRV.Core.Tests/Strategy/ComposableEngineTests.cs`, `CRV.Core.Tests/Backtest/EmaNoLookaheadTests.cs`

**Interfaces:**
- Consumes: Task 7 (`IHtfHistorySource`, `ObserveBar`, `HistoryNeeds`, `SeedHistory`), Task 9; `ComposableEngine.AddSetups` (plan 1); `HtfBarStore` (plan 4).
- Produces: `Task TickerGroup.ObserveBarAsync(Bar bar)`; `static Task HtfHistorySeeder.SeedAsync(IEnumerable<ISetupStrategy> strategies, IHtfHistorySource source, DateTime beforeUtc, CancellationToken ct = default)`; `HtfBarStore.ClosedBeforeAsync(...)`; `IHtfHistorySource? BacktestEngine.History { get; init; }`.

- [ ] **Step 1: Write the failing engine tests**

Append inside `ComposableEngineTests` (`CRV.Core.Tests/Strategy/ComposableEngineTests.cs`), adding `using CRV.Core.Indicators;` at the top:

```csharp
    // ── EMA strategies see every closed bar ──

    private static StrategySetupConfig EmaSetup()
    {
        var c = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Both);
        c.Id = "ema-mnq";
        return c;
    }

    [Fact]
    public async Task IdleEngine_StillHandsClosedBarsToEmaStrategies()
    {
        var engine = CreateEngine();
        engine.AddSetup(EmaSetup());
        engine.SetIdle();

        foreach (var bar in EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0).Take(20))
            await engine.ProcessBarAsync(bar, "/MNQZ26");
        await engine.WarmupBarAsync(EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0)[20], "/MNQZ26");

        Assert.Equal(21, ((EmaStrategy)engine.GetStrategy("ema-mnq")!).SignalBarsClosed);
    }

    private sealed class RecordingHistory : IHtfHistorySource
    {
        public List<(string Root, SignalTimeframe Tf, DateTime Before, int Count)> Calls { get; } = new();
        public Task<IReadOnlyList<HtfBar>> ClosedBeforeAsync(string root, SignalTimeframe tf, DateTime beforeUtc, int count, CancellationToken ct = default)
        {
            Calls.Add((root, tf, beforeUtc, count));
            return Task.FromResult<IReadOnlyList<HtfBar>>(Enumerable.Range(0, 3)
                .Select(i => new HtfBar(tf, beforeUtc.AddHours(-4 * (3 - i)), 100, 101, 99, 100, 1)).ToList());
        }
    }

    [Fact]
    public async Task SeedAsync_AsksForBarsBeforeTheAnchor_PerRootAndTimeframe()
    {
        var engine = CreateEngine();
        var setup = EmaSetup();
        setup.SignalTimeframe = SignalTimeframe.H4;
        setup.Confirmations[0].Enabled = true;   // D1 EMA 200
        engine.AddSetup(setup);
        engine.AddSetup(MakeSetupConfig(SetupId.A, "/MNQZ26"));   // a strategy with no history needs
        var source = new RecordingHistory();
        var anchor = new DateTime(2026, 3, 18, 14, 0, 0, DateTimeKind.Utc);

        await HtfHistorySeeder.SeedAsync(engine.GetStrategies(), source, anchor);

        Assert.Equal(new[] { ("NQ", SignalTimeframe.H4, anchor, 24 + 14), ("NQ", SignalTimeframe.D1, anchor, 600) }, source.Calls);
        await engine.ProcessBarAsync(new Bar(anchor, 100, 101, 99, 100, 1), "/MNQZ26");
        Assert.Equal(3, ((EmaStrategy)engine.GetStrategy("ema-mnq")!).SignalBarsClosed);
    }
```

(`EmaFixture.Cfg` uses EMA 8: `HistoryRequirement.For(8).ParityNeeded` = 24.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~ComposableEngineTests"`
Expected: FAIL — `HtfHistorySeeder` does not exist (build error).

- [ ] **Step 3: Hand closed bars over before the filters**

In `CRV.Core/Strategy/TickerGroup.cs`, before `// ── Bar processing ──` add:

```csharp
    /// <summary>
    /// Hands a closed bar to every strategy before the session, cutoff and idle filters, so a
    /// strategy that builds higher-timeframe bars sees the whole trading day.
    /// </summary>
    public async Task ObserveBarAsync(Bar bar)
    {
        if (!bar.IsConfirmed) return;
        await _semaphore.WaitAsync();
        try
        {
            foreach (var strategy in _strategies) strategy.ObserveBar(bar);
        }
        finally
        {
            _semaphore.Release();
        }
    }
```

In `CRV.Core/Strategy/ComposableEngine.cs`, make the first statement of `ProcessBarAsync` (before `if (_idle)`) and of `WarmupBarAsync` (before `if (_idle) return;`):

```csharp
        await ObserveAsync(bar, ticker);
```

and add next to them:

```csharp
    /// <summary>Every closed bar reaches the strategies, idle or not; see <see cref="TickerGroup.ObserveBarAsync"/>.</summary>
    private async Task ObserveAsync(Bar bar, string ticker)
    {
        if (bar.IsConfirmed && _groups.TryGetValue(TickerGroup.GetGroupKey(ticker), out var group))
            await group.ObserveBarAsync(bar);
    }
```

- [ ] **Step 4: The seeder and the store**

Append to `CRV.Core/Strategy/HtfHistory.cs`:

```csharp

/// <summary>
/// Loads stored closed bars into strategies at engine start. Only bars opening before
/// <c>beforeUtc</c> (now, the Replay date, the backtest's start) are asked for, so nothing
/// after the anchor reaches a strategy.
/// </summary>
public static class HtfHistorySeeder
{
    public static async Task SeedAsync(IEnumerable<ISetupStrategy> strategies, IHtfHistorySource source, DateTime beforeUtc, CancellationToken ct = default)
    {
        foreach (var strategy in strategies)
        {
            string root = TickerGroup.GetGroupKey(strategy.Ticker);
            foreach (var need in strategy.HistoryNeeds)
                strategy.SeedHistory(need.Timeframe, await source.ClosedBeforeAsync(root, need.Timeframe, beforeUtc, need.Bars, ct));
        }
    }
}
```

In `CRV.Core/Data/HtfBarStore.cs`: add `using CRV.Core.Strategy;`, change the declaration to `public sealed class HtfBarStore : IHtfHistorySource`, and add after `LatestAsync`:

```csharp
    /// <summary>The newest <paramref name="count"/> bars opening before <paramref name="beforeUtc"/>, oldest first.</summary>
    public async Task<IReadOnlyList<HtfBar>> ClosedBeforeAsync(string root, SignalTimeframe tf, DateTime beforeUtc, int count, CancellationToken ct = default)
    {
        var rows = await _db.HtfBars.AsNoTracking()
            .Where(r => r.Root == root && r.Timeframe == tf && r.OpenTime < beforeUtc)
            .OrderByDescending(r => r.OpenTime)
            .Take(count)
            .ToListAsync(ct);
        return rows
            .OrderBy(r => r.OpenTime)
            .Select(r => new HtfBar(r.Timeframe, DateTime.SpecifyKind(r.OpenTime, DateTimeKind.Utc), r.Open, r.High, r.Low, r.Close, r.Volume))
            .ToList();
    }
```

(Plan 4 stores only closed bars, so "opening before the anchor" is "closed before it" for every bucket but the one the anchor falls in, which `TimeframeFeed` drops.)

- [ ] **Step 5: Seed in the backtest and live**

`CRV.Backtest/Engine/BacktestEngine.cs`: add to the class, after the constructor:

```csharp
    /// <summary>Stored higher-timeframe bars loaded into strategies before the first bar (those opening before <see cref="BacktestConfig.From"/>).</summary>
    public IHtfHistorySource? History { get; init; }
```

and right after plan 1's `engine.AddSetups(_cfg);` block:

```csharp
        if (History != null)
            await HtfHistorySeeder.SeedAsync(engine.GetStrategies(), History, _btCfg.From, ct);
```

`CRV.Web/Services/BacktestRunnerService.cs`: replace `var engine = new BacktestEngine(cfg, btCfg, log);` with

```csharp
            var engine = new BacktestEngine(cfg, btCfg, log)
            {
                History = new CRV.Core.Data.HtfBarStore(scope.ServiceProvider.GetRequiredService<CRV.Core.Data.TradingDbContext>()),
            };
```

`CRV.Web/Services/LiveEngineOrchestrator.cs`, in `RunEngineAsync`, after plan 1's `foreach (var d in newEngine.DisabledSetups) …` line:

```csharp
            await SeedHigherTimeframesAsync(newEngine, cfg, ct);
```

and add the method next to `BackfillAsync`:

```csharp
    /// <summary>
    /// Loads stored higher-timeframe bars into the EMA strategies. Replay takes only bars before its
    /// replay date. Without stored bars a strategy warms up from live bars instead.
    /// </summary>
    private async Task SeedHigherTimeframesAsync(ComposableEngine engine, StrategyConfig cfg, CancellationToken ct)
    {
        var beforeUtc = cfg.Broker == "TradovateReplay" && cfg.ReplayDate.HasValue
            ? cfg.ReplayDate.Value.ToUniversalTime()
            : DateTime.UtcNow;
        try
        {
            using var historyScope = _sp.CreateScope();
            var store = new CRV.Core.Data.HtfBarStore(historyScope.ServiceProvider.GetRequiredService<CRV.Core.Data.TradingDbContext>());
            await HtfHistorySeeder.SeedAsync(engine.GetStrategies(), store, beforeUtc, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HTF] Stored history could not be loaded; EMA strategies warm up from live bars");
        }
    }
```

- [ ] **Step 6: Write the backtest lookahead test**

`CRV.Core.Tests/Backtest/EmaNoLookaheadTests.cs`:

```csharp
using CRV.Backtest.Engine;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Tests.Strategy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The fixture's sixty M5 bars as 1-minute bars from 09:30 ET (13:30Z, Wed 2026-03-18). EMA 8 cross up,
/// longs only: the cross is on M5 bar 27 (11:45–11:50 ET), so the only entry is at bar 28's first minute.
/// </summary>
public class EmaNoLookaheadTests
{
    private const string Ticker = "MNQZ26";
    private static readonly DateTime Open = new(2026, 3, 18, 13, 30, 0, DateTimeKind.Utc);

    private static async IAsyncEnumerable<(string, Bar)> Minutes()
    {
        foreach (var b in EmaFixture.Bars(EmaFixture.Closes, Open))
        {
            yield return (Ticker, new Bar(b.Time, b.Open, b.High, b.Low, b.Open, 100));
            for (int m = 1; m < 4; m++) yield return (Ticker, new Bar(b.Time.AddMinutes(m), b.Open, b.Open, b.Open, b.Open, 100));
            yield return (Ticker, new Bar(b.Time.AddMinutes(4), b.Open, Math.Max(b.Open, b.Close), Math.Min(b.Open, b.Close), b.Close, 100));
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Backtest_EntersOnTheBarAfterTheSignal_NeverOnItsOwnBar()
    {
        var setup = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up);
        setup.BypassChopFilter = true;
        setup.CutoffHour = 15; setup.CutoffMinute = 0;
        var entry = new BasketEntry
        {
            Id = "ema-cross-mnq", Enabled = true, Label = "EMA cross", StrategyType = StrategyType.Ema,
            Ticker = Ticker, PointValue = 2m, TickSize = 0.25m, ExecutionTFMinutes = 5, Config = setup,
            Sessions = new()
            {
                new() { SessionId = "Asia",   Enabled = false, CutoffHour = 1,  CutoffMinute = 30 },
                new() { SessionId = "London", Enabled = false, CutoffHour = 8,  CutoffMinute = 0  },
                new() { SessionId = "NY",     Enabled = true,  CutoffHour = 15, CutoffMinute = 0  },
            },
        };
        var cfg = new StrategyConfig
        {
            Ticker = Ticker, PointValue = 2m, TickSize = 0.25m, CommissionPerSide = 0.90m, ExecutionTFMinutes = 5,
            BasketJson = "", EmaBasketJson = BasketCodec.Serialize(new[] { entry }),
            EnableA = false, EnableB = false, EnableC = false, EnableD = false,
        };
        var bt = new BacktestConfig
        {
            From = Open.Date, To = Open.Date.AddDays(1), FillMode = FillMode.WithSlippage,
            ExecutionTFMinutes = 5, BacktestSession = "NY", DataSource = "CSV",
        };

        var result = await new BacktestEngine(cfg, bt, NullLogger<BacktestEngine>.Instance).RunAsync(Minutes());

        var trade = Assert.Single(result.Trades);
        var nextBar = Open.AddMinutes(5 * 28);
        Assert.InRange(trade.EnteredAt, nextBar, nextBar.AddMinutes(1).AddTicks(-1));
        Assert.Equal(Direction.Long, trade.Direction);
    }
}
```

- [ ] **Step 7: Run the tests**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~ComposableEngineTests|FullyQualifiedName~EmaNoLookaheadTests|FullyQualifiedName~HtfBarStoreTests"`
Expected: PASS.

- [ ] **Step 8: Run both suites**

Run: `dotnet test CRV.Core.Tests && dotnet build CRV.Web`
Expected: all pass; build succeeds.

- [ ] **Step 9: Commit**

```bash
git add CRV.Core/Strategy/TickerGroup.cs CRV.Core/Strategy/ComposableEngine.cs CRV.Core/Strategy/HtfHistory.cs CRV.Core/Data/HtfBarStore.cs \
  CRV.Backtest/Engine/BacktestEngine.cs CRV.Web/Services/BacktestRunnerService.cs CRV.Web/Services/LiveEngineOrchestrator.cs \
  CRV.Core.Tests/Strategy/ComposableEngineTests.cs CRV.Core.Tests/Backtest/EmaNoLookaheadTests.cs
git commit -m "feat(ema): every closed bar reaches EMA strategies; stored history loaded at start"
```

---

### Task 11: State cells for the cockpit card

**Files:**
- Create: `CRV.Core/Strategy/TargetText.cs`
- Modify: `CRV.Core/Models/Signals.cs` (`CardCell`, `SetupSnapshot.EmaCells`)
- Modify: `CRV.Core/Strategy/ISetupStrategy.cs` (`SetupStateSnapshot.EmaCells`)
- Modify: `CRV.Core/Strategy/EmaStrategy.cs` (`GetSnapshot`, cells)
- Modify: `CRV.Core/Strategy/SnapshotAggregator.cs`
- Test: `CRV.Core.Tests/Strategy/EmaCardTests.cs`

**Interfaces:**
- Consumes: Tasks 5, 6, 9; `SessionBucket.For`, `EasternTime.ToEastern` (plan 4).
- Produces: `sealed record CardCell(string Label, string Value)` (`CRV.Core.Models`); `IReadOnlyList<CardCell>? SetupStateSnapshot.EmaCells` and `SetupSnapshot.EmaCells` (hub JSON `emaCells: [{ label, value }]`); `static class TargetText { string Describe(StrategySetupConfig c); string? Partial(StrategySetupConfig c); }`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/EmaCardTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class EmaCardTests
{
    private static Dictionary<string, string> Cells(ISetupStrategy s) =>
        s.GetSnapshot().EmaCells!.ToDictionary(c => c.Label, c => c.Value);

    [Fact]
    public void BeforeEnoughBars_SaysHowManyMore()
    {
        var s = new EmaStrategy(EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.CrossRetest, EmaDirection.Both));
        var cells = Cells(s);
        Assert.Equal("History: 14 more M5 bars", cells["Waiting for"]);
        Assert.Equal("—", cells["EMA 8 (M5)"]);
    }

    [Fact]
    public void CrossedDown_WaitingForTheRetest()
    {
        var bars = EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0);
        var s = new EmaStrategy(EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.CrossRetest, EmaDirection.Down));
        for (int i = 0; i <= 45; i++) s.OnBar(bars[i], default, default, default);   // cross down on 45 (13:45 ET)

        var snap = s.GetSnapshot();
        var cells = snap.EmaCells!;
        Assert.Equal(new CardCell("Crossed down", "13:45 M5"), cells[0]);
        Assert.Equal("Retest ≤ 10 bars left", cells.Single(c => c.Label == "Waiting for").Value);
        Assert.Matches(@"^\d+\.\d\d ATR( ✓)?$", cells.Single(c => c.Label == "Moved away").Value);
        Assert.Equal("2R", cells.Single(c => c.Label == "Target").Value);
        Assert.DoesNotContain(cells, c => c.Label == "Partial");          // no partial set
        Assert.DoesNotContain(cells, c => c.Label == "Confirmations");    // none switched on
        Assert.Equal(-1, snap.State);
    }

    [Fact]
    public void Cross_ShowsBothEmasAndTheLastCross()
    {
        var bars = EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0);
        var s = new EmaStrategy(EmaFixture.Cfg(EmaSource.EmaVsEma, EmaEntry.Cross, EmaDirection.Both));
        for (int i = 0; i <= 35; i++) s.OnBar(bars[i], default, default, default);   // crossed up on 31 (12:35 ET)

        var cells = Cells(s);
        Assert.True(cells.ContainsKey("EMA 8 (M5)"));
        Assert.True(cells.ContainsKey("EMA 21 (M5)"));
        Assert.Equal("Up · 12:35", cells["Last cross"]);
    }

    [Fact]
    public void Confirmations_NeedBeforeAVote_ThenPassedOfOn()
    {
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Up);
        cfg.Confirmations.Single(k => k.Kind == ConfirmationKind.NotStretched).Enabled = true;
        cfg.Confirmations.Single(k => k.Kind == ConfirmationKind.TimeWindow).Enabled = true;
        cfg.ConfirmationsNeeded = 1;
        var s = new EmaStrategy(cfg);
        Assert.Equal("need 1 of 2", Cells(s)["Confirmations"]);

        var bars = EmaFixture.Bars(EmaFixture.Closes, EmaFixture.T0);
        for (int i = 0; i <= 28; i++) s.OnBar(bars[i], default, default, default);   // touch on 28 votes
        Assert.Matches(@"^\d of 2$", Cells(s)["Confirmations"]);
    }

    [Fact]
    public void SignalBarClose_IsShownInEasternTime()
    {
        var cfg = EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up);
        cfg.SignalTimeframe = CRV.Core.Indicators.SignalTimeframe.H4;
        var s = new EmaStrategy(cfg);
        s.OnBar(new Bar(new DateTime(2026, 3, 18, 14, 5, 0, DateTimeKind.Utc), 1, 2, 0.5m, 1.5m, 1), default, default, default);   // 10:05 ET

        Assert.Equal("14:00 ET", Cells(s)["H4 bar closes"]);
    }

    [Theory]
    [InlineData(TargetMode.Dollars, TargetDollarsBasis.PerContract, "$400 / contract", "$200 · 100 pts")]
    [InlineData(TargetMode.Dollars, TargetDollarsBasis.WholePosition, "$400 / position", "$200 / position")]
    [InlineData(TargetMode.RiskMultiple, TargetDollarsBasis.PerContract, "2R", "1R")]
    [InlineData(TargetMode.Atr, TargetDollarsBasis.PerContract, "2 × ATR", "1 × ATR")]
    [InlineData(TargetMode.RangePct, TargetDollarsBasis.PerContract, "100% of range", "50% of the way")]
    public void TargetText_InTheTargetsOwnUnit(TargetMode mode, TargetDollarsBasis basis, string target, string partial)
    {
        var c = new StrategySetupConfig
        {
            TargetMode = mode, TargetDollarsBasis = basis, TargetDollars = 400m, PointValue = 2m,
            PartialPct = 50, TargetPct = 100, AtrTp1Mult = 1m, AtrTp2Mult = 2m,
        };
        Assert.Equal((target, partial), (TargetText.Describe(c), TargetText.Partial(c)));
    }

    [Fact]
    public void Aggregator_CarriesTheCellsToTheSnapshot()
    {
        var s = new EmaStrategy(EmaFixture.Cfg(EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up));
        var snap = SnapshotAggregator.Build(new SnapshotAggregator.Inputs
        {
            Strategies = new ISetupStrategy[] { s }, Risk = new RiskManager(), Ticker = "MNQZ26", IsLive = true,
        });
        Assert.NotEmpty(snap.Setups.Single().EmaCells!);
    }
}
```

(`SnapshotAggregator.Build(Inputs)` is the aggregator's static entry point, `SnapshotAggregator.cs:104`.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaCardTests"`
Expected: build FAILS with `'SetupStateSnapshot' does not contain a definition for 'EmaCells'`.

- [ ] **Step 3: Snapshot fields**

In `CRV.Core/Models/Signals.cs`, after the `SetupSnapshot` class add:

```csharp

/// <summary>One label / value cell on a cockpit card.</summary>
public sealed record CardCell(string Label, string Value);
```

and inside `SetupSnapshot`, after `public bool OrbFormed { get; set; }`:

```csharp

    /// <summary>EMA strategies: their signal state as cells, drawn while no trade is open; null for other strategies.</summary>
    public IReadOnlyList<CardCell>? EmaCells { get; set; }
```

In `CRV.Core/Strategy/ISetupStrategy.cs`, inside `SetupStateSnapshot` after plan 3's `LastSkip`:

```csharp
    /// <summary>EMA strategies: their signal state as cells for the cockpit card.</summary>
    public IReadOnlyList<CardCell>? EmaCells { get; set; }
```

In `CRV.Core/Strategy/SnapshotAggregator.cs`, in the `new SetupSnapshot { … }` initializer after `OrbFormed = …,` add:

```csharp
                EmaCells     = ss.EmaCells,
```

- [ ] **Step 4: `TargetText`**

`CRV.Core/Strategy/TargetText.cs`:

```csharp
using System.Globalization;
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>A strategy's target and partial in a few words, in the target's own unit.</summary>
public static class TargetText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(decimal v) => v.ToString("0.##", Inv);
    private static string Money(decimal v) => "$" + v.ToString("N0", Inv);

    public static string Describe(StrategySetupConfig c) => c.TargetMode switch
    {
        TargetMode.Dollars      => $"{Money(c.TargetDollars)} / {(c.TargetDollarsBasis == TargetDollarsBasis.WholePosition ? "position" : "contract")}",
        TargetMode.RiskMultiple => $"{N(c.AtrTp2Mult)}R",
        TargetMode.Atr          => $"{N(c.AtrTp2Mult)} × ATR",
        _                       => $"{c.TargetPct}% of range",
    };

    /// <summary>Where the partial is taken; null when the target mode has none to show.</summary>
    public static string? Partial(StrategySetupConfig c)
    {
        switch (c.TargetMode)
        {
            case TargetMode.Dollars:
                decimal dollars = c.TargetDollars * c.PartialPct / 100m;
                if (c.TargetDollarsBasis == TargetDollarsBasis.WholePosition) return $"{Money(dollars)} / position";
                return c.PointValue > 0 ? $"{Money(dollars)} · {N(dollars / c.PointValue)} pts" : Money(dollars);
            case TargetMode.RiskMultiple when c.AtrTp1Mult > 0:
                return $"{N(c.AtrTp1Mult)}R";
            case TargetMode.Atr when c.AtrTp1Mult > 0:
                return $"{N(c.AtrTp1Mult)} × ATR";
            default:
                return $"{c.PartialPct}% of the way";
        }
    }
}
```

- [ ] **Step 5: The strategy's cells**

In `CRV.Core/Strategy/EmaStrategy.cs`, in `GetSnapshot()` after `LastSkip = …,` add `EmaCells = Cells(),` and add these members:

```csharp
    private IReadOnlyList<CardCell> Cells()
    {
        var o = EmaSignalOptions.From(_cfg);
        string tf = _shape.Timeframe.ToString();
        var cells = new List<CardCell>();

        if (_fast is null) cells.Add(new($"EMA {_shape.Period} ({tf})", Price(_ema)));
        else
        {
            cells.Add(new($"EMA {_shape.FastPeriod} ({tf})", Price(_fast)));
            cells.Add(new($"EMA {_shape.SlowPeriod} ({tf})", Price(_ema)));
        }

        if (_lastSignalBar is null)
            cells.Add(new("Waiting for", $"History: {MissingBars()} more {tf} bars"));
        else if (_shape.Entry == EmaEntry.CrossRetest && _retest.Phase == RetestPhase.Crossed)
        {
            cells.Insert(0, new(_retest.CrossSide > 0 ? "Crossed up" : "Crossed down", $"{Et(_retest.CrossTime)} {tf}"));
            cells.Add(new("Waiting for", $"Retest ≤ {_retest.BarsLeft(o)} bars left"));
            cells.Add(new("Moved away", $"{_retest.MovedAwayAtr.ToString("0.00", Inv)} ATR{(_retest.MovedAway ? " ✓" : "")}"));
        }
        else if (_shape.Entry == EmaEntry.Touch) cells.Add(new("Waiting for", TouchWaiting()));
        else cells.Add(new("Last cross", LastCrossText()));

        cells.Add(new("Target", TargetText.Describe(_cfg)));
        if (_cfg.UsePartial && TargetText.Partial(_cfg) is { } partial) cells.Add(new("Partial", partial));

        int on = EmaConfirmations.Build(_cfg).Count;
        if (on > 0)
            cells.Add(new("Confirmations", _lastDecision is { } d
                ? $"{d.PassedCount} of {d.SwitchedOn}"
                : $"need {ConfirmationGate.Required(_cfg.ConfirmationsNeeded, on)} of {on}"));

        if (_signalFeed.Forming is { } forming)
            cells.Add(new($"{tf} bar closes", $"{Et(SessionBucket.For(_shape.Timeframe, forming.OpenUtc).EndUtc)} ET"));
        return cells;
    }

    private int MissingBars() => Math.Max(1, Math.Max(_ema.Period + 1, AtrPeriod) - SignalBarsClosed);

    private static string Price(EmaIndicator ema) => ema.IsReady ? ema.Value.ToString("N2", Inv) : "—";

    private static string Et(DateTime utc) => EasternTime.ToEastern(utc).ToString("HH:mm", Inv);

    private string TouchWaiting()
    {
        bool up = _cfg.AllowLong && _touch.LongArmed, down = _cfg.AllowShort && _touch.ShortArmed;
        return (up, down) switch
        {
            (true, true)  => "Touch from either side",
            (true, false) => "Touch from above",
            (false, true) => "Touch from below",
            _             => $"Price to move {_cfg.RearmAtr.ToString("0.##", Inv)} ATR away",
        };
    }

    private string LastCrossText()
    {
        var last = _shape.Entry == EmaEntry.Cross ? _cross.LastCross : _retest.LastCross;
        return last is { } x ? $"{(x.Side > 0 ? "Up" : "Down")} · {Et(x.Time)}" : "—";
    }
```

- [ ] **Step 6: Run them**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaCardTests|FullyQualifiedName~SnapshotAggregatorTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy/TargetText.cs CRV.Core/Models/Signals.cs CRV.Core/Strategy/ISetupStrategy.cs CRV.Core/Strategy/EmaStrategy.cs \
  CRV.Core/Strategy/SnapshotAggregator.cs CRV.Core.Tests/Strategy/EmaCardTests.cs
git commit -m "feat(ema): EMA state cells on the setup snapshot"
```

---

### Task 12: Plain words, Strategies row and name

**Files:**
- Create: `CRV.Core/Strategy/EmaDescription.cs`
- Modify: `CRV.Web/Pages/Setup/StrategyText.cs`
- Test: `CRV.Core.Tests/Strategy/EmaDescriptionTests.cs`

**Interfaces:**
- Consumes: Task 1 fields, `TargetText` (Task 11), `EmaConfirmations`, `ConfirmationGate` (Task 6).
- Produces: `static class EmaDescription { string Describe(BasketEntry e, int barMinutes); string Row(BasketEntry e); string Title(BasketEntry e); string BarName(SignalTimeframe tf); }`; `StrategyText.TypeName(StrategyType.Ema)` = `"EMA (touch or cross)"`, `StrategyText.Describe(BasketEntry e, int barMinutes = 0)`, `StrategyText.Row(BasketEntry e)`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/EmaDescriptionTests.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class EmaDescriptionTests
{
    private static BasketEntry E(Action<StrategySetupConfig>? tweak = null)
    {
        var c = new StrategySetupConfig { TargetMode = TargetMode.RiskMultiple, AtrTp1Mult = 1m, AtrTp2Mult = 2m, UsePartial = true };
        tweak?.Invoke(c);
        return new BasketEntry
        {
            Id = "ema-mnq", StrategyType = StrategyType.Ema, Ticker = "/MNQZ26", Config = c,
            Sessions = new() { new() { SessionId = "London", Enabled = true }, new() { SessionId = "NY", Enabled = true }, new() { SessionId = "Asia" } },
        };
    }

    public static TheoryData<EmaSource, EmaEntry, EmaDirection, string> Openings() => new()
    {
        { EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Up,   "Long when the H4 bar dips to within 1 tick (or 0.1 ATR) of the 21 EMA from above and closes back above it." },
        { EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Down, "Short when the H4 bar rallies to within 1 tick (or 0.1 ATR) of the 21 EMA from below and closes back below it." },
        { EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Both, "Long when the H4 bar dips to within 1 tick (or 0.1 ATR) of the 21 EMA from above and closes back above it; short when the H4 bar rallies to within 1 tick (or 0.1 ATR) of the 21 EMA from below and closes back below it." },
        { EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Up,   "Long when price crosses above the 21 EMA, checked when the H4 bar closes." },
        { EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Down, "Short when price crosses below the 21 EMA, checked when the H4 bar closes." },
        { EmaSource.PriceVsEma, EmaEntry.Cross, EmaDirection.Both, "Long when price crosses above the 21 EMA; short when price crosses below the 21 EMA, checked when the H4 bar closes." },
        { EmaSource.PriceVsEma, EmaEntry.CrossRetest, EmaDirection.Up,   "Long when price crosses above the 21 EMA, moves 0.25 ATR above the 21 EMA and, within 10 bars, dips back to it and the H4 bar closes back above it." },
        { EmaSource.PriceVsEma, EmaEntry.CrossRetest, EmaDirection.Down, "Short when price crosses below the 21 EMA, moves 0.25 ATR below the 21 EMA and, within 10 bars, rallies back to it and the H4 bar closes back below it." },
        { EmaSource.PriceVsEma, EmaEntry.CrossRetest, EmaDirection.Both, "Long when price crosses above the 21 EMA, moves 0.25 ATR above the 21 EMA and, within 10 bars, dips back to it and the H4 bar closes back above it; short when price crosses below the 21 EMA, moves 0.25 ATR below the 21 EMA and, within 10 bars, rallies back to it and the H4 bar closes back below it." },
        { EmaSource.EmaVsEma, EmaEntry.Cross, EmaDirection.Up,   "Long when the 8 EMA crosses above the 21 EMA, checked when the H4 bar closes." },
        { EmaSource.EmaVsEma, EmaEntry.Cross, EmaDirection.Down, "Short when the 8 EMA crosses below the 21 EMA, checked when the H4 bar closes." },
        { EmaSource.EmaVsEma, EmaEntry.Cross, EmaDirection.Both, "Long when the 8 EMA crosses above the 21 EMA; short when the 8 EMA crosses below the 21 EMA, checked when the H4 bar closes." },
        { EmaSource.EmaVsEma, EmaEntry.CrossRetest, EmaDirection.Up,   "Long when the 8 EMA crosses above the 21 EMA, moves 0.25 ATR above the 8 EMA and, within 10 bars, dips back to it and the H4 bar closes back above it." },
        { EmaSource.EmaVsEma, EmaEntry.CrossRetest, EmaDirection.Down, "Short when the 8 EMA crosses below the 21 EMA, moves 0.25 ATR below the 8 EMA and, within 10 bars, rallies back to it and the H4 bar closes back below it." },
        { EmaSource.EmaVsEma, EmaEntry.CrossRetest, EmaDirection.Both, "Long when the 8 EMA crosses above the 21 EMA, moves 0.25 ATR above the 8 EMA and, within 10 bars, dips back to it and the H4 bar closes back above it; short when the 8 EMA crosses below the 21 EMA, moves 0.25 ATR below the 8 EMA and, within 10 bars, rallies back to it and the H4 bar closes back below it." },
    };

    [Theory]
    [MemberData(nameof(Openings))]
    public void EveryCombination_HasItsOwnSentence(EmaSource source, EmaEntry entry, EmaDirection dir, string opening)
        => Assert.StartsWith(opening, EmaDescription.Describe(E(c => { c.EmaSource = source; c.EmaEntry = entry; c.EmaDirection = dir; }), 5));

    [Fact]
    public void Ticks_AreSingularOrPlural()
        => Assert.Contains("within 2 ticks (or 0.1 ATR)",
            EmaDescription.Describe(E(c => { c.EmaEntry = EmaEntry.Touch; c.TouchTicks = 2; }), 5));

    [Fact]
    public void FullSentence_EntryStopTargetsConfirmationsAndClose()
    {
        var text = EmaDescription.Describe(E(c => { c.EmaEntry = EmaEntry.Touch; c.EmaDirection = EmaDirection.Up; }), 5);

        Assert.Contains(" No new signal on that side until price moves 0.5 ATR away from the EMA.", text);
        Assert.Contains(" Enters on the next 5-minute bar.", text);
        Assert.Contains(" Stop 2 ticks past the touch bar's low (long) or high (short).", text);
        Assert.Contains(" Half off at 1R, the rest at 2R.", text);
        Assert.Contains(" All 2 confirmations must pass.", text);
        Assert.EndsWith(" Closes at the end of the session.", text);
    }

    [Fact]
    public void NoPartial_NoHalfOff_HoldAndGuardOffSentencesComeLast()
    {
        var text = EmaDescription.Describe(E(c => { c.UsePartial = false; c.CloseAtRthClose = false; c.EnforceMinRr = false; c.MinRr = 1.5m; c.ConfirmationsNeeded = 1; }), 5);

        Assert.DoesNotContain("off at", text);
        Assert.Contains(" Target 2R.", text);
        Assert.Contains(" Needs at least 1 of 2 confirmations.", text);
        Assert.EndsWith(" Holds an open trade past the cutoff. Takes trades below 1.5R: the reward / risk guard is off.", text);
    }

    [Fact]
    public void DollarTargets_AndNoConfirmations()
    {
        var text = EmaDescription.Describe(E(c =>
        {
            c.TargetMode = TargetMode.Dollars; c.TargetDollars = 400m;
            foreach (var k in c.Confirmations) k.Enabled = false;
        }), 15);

        Assert.Contains(" Enters on the next 15-minute bar.", text);
        Assert.Contains(" Target $400 a contract, with the partial 50% of the way.", text);
        Assert.Contains(" No confirmations.", text);
    }

    [Theory]
    [InlineData(EmaSource.PriceVsEma, EmaEntry.CrossRetest, "EMA · cross + retest · /MNQZ26 · London, NY · target $400 / contract")]
    [InlineData(EmaSource.EmaVsEma, EmaEntry.Cross, "EMA · two EMAs, cross · /MNQZ26 · London, NY · target $400 / contract")]
    public void Row_ForTheStrategiesList(EmaSource source, EmaEntry entry, string expected)
        => Assert.Equal(expected, EmaDescription.Row(E(c => { c.EmaSource = source; c.EmaEntry = entry; c.TargetMode = TargetMode.Dollars; c.TargetDollars = 400m; })));

    [Theory]
    [InlineData(EmaSource.PriceVsEma, EmaEntry.CrossRetest, EmaDirection.Both, "EMA 21 cross + retest · MNQ H4")]
    [InlineData(EmaSource.PriceVsEma, EmaEntry.Touch, EmaDirection.Up, "EMA 21 touch long · MNQ H4")]
    [InlineData(EmaSource.EmaVsEma, EmaEntry.Cross, EmaDirection.Down, "EMA 8/21 cross down · MNQ H4")]
    public void Title_NamesTheSetup(EmaSource source, EmaEntry entry, EmaDirection dir, string expected)
        => Assert.Equal(expected, EmaDescription.Title(E(c => { c.EmaSource = source; c.EmaEntry = entry; c.EmaDirection = dir; })));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaDescriptionTests"`
Expected: build FAILS with `The name 'EmaDescription' does not exist in the current context`.

- [ ] **Step 3: Write the description**

`CRV.Core/Strategy/EmaDescription.cs`:

```csharp
using System.Globalization;
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy.Confirmations;

namespace CRV.Core.Strategy;

/// <summary>An EMA setup in plain words: the setup page's sentence, the Strategies row and the default name.</summary>
public static class EmaDescription
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(decimal v) => v.ToString("0.##", Inv);
    private static string Ticks(decimal n) => n == 1 ? "1 tick" : $"{N(n)} ticks";

    public static string BarName(SignalTimeframe tf) => tf switch
    {
        SignalTimeframe.M5  => "the 5-minute bar",
        SignalTimeframe.M15 => "the 15-minute bar",
        SignalTimeframe.M30 => "the 30-minute bar",
        SignalTimeframe.H1  => "the hourly bar",
        SignalTimeframe.H4  => "the H4 bar",
        SignalTimeframe.H8  => "the H8 bar",
        SignalTimeframe.D1  => "the daily bar",
        SignalTimeframe.W1  => "the weekly bar",
        _                   => "the monthly bar",
    };

    /// <summary>The setup in sentences. <paramref name="barMinutes"/> is the root's bar size (0 = unknown).</summary>
    public static string Describe(BasketEntry e, int barMinutes)
    {
        var c = e.Config;
        string bar = BarName(c.SignalTimeframe);
        bool two = c.EmaSource == EmaSource.EmaVsEma;
        string line = two ? $"the {c.FastEma} EMA" : "price";
        string reference = two ? $"the {c.SlowEma} EMA" : $"the {c.EmaPeriod} EMA";
        string retestRef = two ? $"the {(c.RetestEma == RetestEmaChoice.Fast ? c.FastEma : c.SlowEma)} EMA" : reference;
        string hold = c.HoldBars > 1 ? $" and stays there for {c.HoldBars} bars" : "";
        bool up = c.EmaDirection != EmaDirection.Down, down = c.EmaDirection != EmaDirection.Up;
        string tol = $"{Ticks(c.TouchTicks)} (or {N(c.TouchAtr)} ATR)";

        string Sides(string longText, string shortText) =>
            up && down ? $"{longText}; {char.ToLowerInvariant(shortText[0])}{shortText[1..]}" : up ? longText : shortText;

        var parts = new List<string>();
        switch (c.EmaEntry)
        {
            case EmaEntry.Touch:
                parts.Add(Sides(
                    $"Long when {bar} dips to within {tol} of {reference} from above{(c.CloseBackOnSide ? " and closes back above it" : "")}",
                    $"Short when {bar} rallies to within {tol} of {reference} from below{(c.CloseBackOnSide ? " and closes back below it" : "")}") + ".");
                parts.Add($"No new signal on that side until price moves {N(c.RearmAtr)} ATR away from the EMA.");
                break;
            case EmaEntry.Cross:
                parts.Add(Sides($"Long when {line} crosses above {reference}{hold}", $"Short when {line} crosses below {reference}{hold}")
                          + $", checked when {bar} closes.");
                break;
            default:
                parts.Add(Sides(
                    $"Long when {line} crosses above {reference}{hold}, moves {N(c.RetestMinAwayAtr)} ATR above {retestRef} and, within {c.RetestWindowBars} bars, dips back to it" +
                    (c.CloseBackOnSide ? $" and {bar} closes back above it" : ""),
                    $"Short when {line} crosses below {reference}{hold}, moves {N(c.RetestMinAwayAtr)} ATR below {retestRef} and, within {c.RetestWindowBars} bars, rallies back to it" +
                    (c.CloseBackOnSide ? $" and {bar} closes back below it" : "")) + ".");
                parts.Add("A close back across the EMA cancels it." + (c.MaxEntriesPerCross > 1 ? $" Up to {c.MaxEntriesPerCross} entries per cross." : ""));
                break;
        }

        parts.Add(barMinutes > 0 ? $"Enters on the next {barMinutes}-minute bar." : "Enters on the next bar.");

        string signalBar = c.EmaEntry == EmaEntry.Touch ? "the touch bar" : "the retest bar";
        string stopRef = c.EmaEntry == EmaEntry.CrossRetest ? retestRef : reference;
        parts.Add(c.EmaStopMode switch
        {
            EmaStopMode.SignalBarExtreme => $"Stop {Ticks(c.StopBuffer)} past {signalBar}'s low (long) or high (short).",
            EmaStopMode.EmaAtr           => $"Stop {N(c.StopBuffer)} ATR past {stopRef}.",
            _                            => $"Stop {N(c.StopBuffer)} ATR from entry.",
        });

        parts.Add(Targets(c));
        parts.Add(Confirmations(c));
        parts.Add(c.CloseAtRthClose ? "Closes at the end of the session." : "Holds an open trade past the cutoff.");
        if (!c.EnforceMinRr) parts.Add($"Takes trades below {N(c.MinRr)}R: the reward / risk guard is off.");
        return string.Join(" ", parts);
    }

    private static string Targets(StrategySetupConfig c)
    {
        if (c.TargetMode == TargetMode.Dollars)
            return $"Target ${c.TargetDollars.ToString("N0", Inv)} {(c.TargetDollarsBasis == TargetDollarsBasis.WholePosition ? "for the whole position" : "a contract")}" +
                   (c.UsePartial ? $", with the partial {c.PartialPct}% of the way." : ".");

        string unit = c.TargetMode == TargetMode.RiskMultiple ? "R" : " ATR";
        string tp2 = N(c.AtrTp2Mult) + unit;
        if (!c.UsePartial || c.AtrTp1Mult <= 0) return $"Target {tp2}.";
        string who = c.PartialCts > 0 ? $"{c.PartialCts} contract{(c.PartialCts == 1 ? "" : "s")}" : "Half";
        return $"{who} off at {N(c.AtrTp1Mult)}{unit}, the rest at {tp2}.";
    }

    private static string Confirmations(StrategySetupConfig c)
    {
        int on = EmaConfirmations.Build(c).Count;
        if (on == 0) return "No confirmations.";
        int required = ConfirmationGate.Required(c.ConfirmationsNeeded, on);
        if (required == on) return on == 1 ? "The confirmation must pass." : $"All {on} confirmations must pass.";
        return $"Needs at least {required} of {on} confirmations.";
    }

    /// <summary>The Strategies list row: EMA · entry · ticker · sessions · target.</summary>
    public static string Row(BasketEntry e)
    {
        var c = e.Config;
        string entry = c.EmaEntry switch { EmaEntry.Touch => "touch", EmaEntry.Cross => "cross", _ => "cross + retest" };
        if (c.EmaSource == EmaSource.EmaVsEma) entry = "two EMAs, " + entry;
        string sessions = e.Sessions is not { Count: > 0 } ? "all sessions"
            : e.Sessions.Where(s => s.Enabled).Select(s => s.SessionId).DefaultIfEmpty("no session").Aggregate((a, b) => $"{a}, {b}");
        return $"EMA · {entry} · {e.Ticker} · {sessions} · target {TargetText.Describe(c)}";
    }

    /// <summary>A name for a new or untouched setup, e.g. "EMA 21 cross + retest · MNQ H4".</summary>
    public static string Title(BasketEntry e)
    {
        var c = e.Config;
        bool touch = c.EmaEntry == EmaEntry.Touch;
        string emas = c.EmaSource == EmaSource.EmaVsEma ? $"EMA {c.FastEma}/{c.SlowEma}" : $"EMA {c.EmaPeriod}";
        string entry = c.EmaEntry switch { EmaEntry.Touch => "touch", EmaEntry.Cross => "cross", _ => "cross + retest" };
        string dir = c.EmaDirection switch
        {
            EmaDirection.Up   => touch ? " long" : " up",
            EmaDirection.Down => touch ? " short" : " down",
            _                 => "",
        };
        string t = e.Ticker.TrimStart('/').Trim();
        string root = t.Length > 3 ? t[..^3] : t;
        return $"{emas} {entry}{dir} · {root} {c.SignalTimeframe}";
    }
}
```

- [ ] **Step 4: `StrategyText` delegates**

In `CRV.Web/Pages/Setup/StrategyText.cs`:

- In `TypeName`, add `StrategyType.Ema => "EMA (touch or cross)",` before `_ =>`.
- Change `public static string Describe(BasketEntry e)` to `public static string Describe(BasketEntry e, int barMinutes = 0)` and make its first line `if (e.StrategyType == StrategyType.Ema) return EmaDescription.Describe(e, barMinutes);`.
- Add after `Sessions`:

```csharp
    /// <summary>The Strategies list row under the name.</summary>
    public static string Row(BasketEntry e) => e.StrategyType == StrategyType.Ema
        ? EmaDescription.Row(e)
        : $"{TypeName(e.StrategyType)} · {e.Ticker} · {Sessions(e)} · {Target(e.Config)}";
```

(`Target(...)` is plan 3's `StrategyText.Target`; the non-EMA string is what plan 3 put in `Strategies.cshtml`.)

- [ ] **Step 5: Run the tests**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaDescriptionTests" && dotnet build CRV.Web`
Expected: PASS (24 tests); build succeeds.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Strategy/EmaDescription.cs CRV.Web/Pages/Setup/StrategyText.cs CRV.Core.Tests/Strategy/EmaDescriptionTests.cs
git commit -m "feat(ema): plain words for every source, entry and direction; Strategies row; default name"
```

---

### Task 13: Migration — retired EMA21 entries become disabled EMA entries

**Files:**
- Create: `CRV.Core/Migrations/<ts>_ConvertRetiredEma21Entries.cs` (+ Designer, snapshot unchanged)
- Modify: `CRV.Web/Program.cs`
- Test: `CRV.Core.Tests/Data/ConvertRetiredEma21EntriesTests.cs`

**Interfaces:**
- Consumes: `BasketCodec`, `SetupValidation.RetiredEma21` (plan 1), `SignalTimeframes.IsWholeMultipleOf` (plan 4).
- Produces: `ConvertRetiredEma21Entries` (`CRV.Core.Migrations`) with `const string LabelSuffix = " (migrated from EMA21)"`, `static string? Convert(string? emaBasketJson, int defaultBarMinutes)` (null = nothing to convert; throws `JsonException` on unreadable JSON), `static void Apply(TradingDbContext db, ILogger log)`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Data/ConvertRetiredEma21EntriesTests.cs`:

```csharp
using System.Text.Json;
using CRV.Core.Data;
using CRV.Core.Indicators;
using CRV.Core.Migrations;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Data;

public class ConvertRetiredEma21EntriesTests
{
    private const string Basket = """
    [
      {"Id":"ema21-mnq","Enabled":true,"Label":"EMA21 [MNQ]","StrategyType":4,"Ticker":"/MNQZ26","PointValue":2,"TickSize":0.25,
       "ExecutionTFMinutes":10,
       "Sessions":[{"SessionId":"NY","Enabled":true,"CutoffHour":14,"CutoffMinute":30}],
       "Config":{"Contracts":3,"MaxContracts":5,"MaxTradeRisk":300,"AutoSizeByRisk":true,"AllowLong":true,"AllowShort":false,
                 "AtrTp1Mult":1.5,"AtrTp2Mult":3,"CloseAtRthClose":false}},
      {"Id":"ema-keep","Enabled":true,"Label":"EMA cross","StrategyType":5,"Ticker":"/MESZ26","Config":{"EmaEntry":"Cross"}}
    ]
    """;

    [Fact]
    public void Convert_RetiredEntry_BecomesADisabledEmaTouch_KeepingSizingAndSessions()
    {
        var entries = BasketCodec.Parse(ConvertRetiredEma21Entries.Convert(Basket, defaultBarMinutes: 5));

        var e = entries.Single(x => x.Id == "ema21-mnq");
        Assert.Equal((StrategyType.Ema, false, "EMA21 [MNQ] (migrated from EMA21)"), (e.StrategyType, e.Enabled, e.Label));
        var c = e.Config;
        Assert.Equal((EmaSource.PriceVsEma, EmaEntry.Touch, 21), (c.EmaSource, c.EmaEntry, c.EmaPeriod));
        Assert.Equal(SignalTimeframe.M30, c.SignalTimeframe);   // the shortest timeframe made of whole 10-minute bars
        Assert.Equal(EmaDirection.Up, c.EmaDirection);          // it traded longs only
        Assert.True(c.CloseAtRthClose);
        Assert.Equal((TargetMode.Atr, 1.5m, 3m), (c.TargetMode, c.AtrTp1Mult, c.AtrTp2Mult));
        Assert.Equal((EmaStopMode.SignalBarExtreme, 2m), (c.EmaStopMode, c.StopBuffer));
        Assert.Equal((3, 5, 300m, true), (c.Contracts, c.MaxContracts, c.MaxTradeRisk, c.AutoSizeByRisk));
        Assert.Equal(("NY", true, 14, 30), (e.Sessions[0].SessionId, e.Sessions[0].Enabled, e.Sessions[0].CutoffHour, e.Sessions[0].CutoffMinute));
        Assert.Equal("/MNQZ26", e.Ticker);
        Assert.Empty(EmaValidation.Entry(e, 10));
    }

    [Fact]
    public void Convert_LeavesOtherEntriesAsTheyWere()
    {
        var before = BasketCodec.Parse(Basket).Single(x => x.Id == "ema-keep");
        var after = BasketCodec.Parse(ConvertRetiredEma21Entries.Convert(Basket, 5)).Single(x => x.Id == "ema-keep");
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    [Theory]
    [InlineData(1, SignalTimeframe.M5)]
    [InlineData(2, SignalTimeframe.M30)]
    [InlineData(15, SignalTimeframe.M15)]
    [InlineData(20, SignalTimeframe.H1)]
    [InlineData(60, SignalTimeframe.H1)]
    public void Convert_UsesTheBarSizeOrTheNearestValidTimeframe(int bar, SignalTimeframe expected)
    {
        var json = Basket.Replace("\"ExecutionTFMinutes\":10", $"\"ExecutionTFMinutes\":{bar}");
        Assert.Equal(expected, BasketCodec.Parse(ConvertRetiredEma21Entries.Convert(json, 5))[0].Config.SignalTimeframe);
    }

    [Fact]
    public void Convert_NothingRetired_ReturnsNull_UnreadableThrows()
    {
        Assert.Null(ConvertRetiredEma21Entries.Convert("""[{"Id":"x","StrategyType":5}]""", 5));
        Assert.Null(ConvertRetiredEma21Entries.Convert("", 5));
        Assert.ThrowsAny<JsonException>(() => ConvertRetiredEma21Entries.Convert("[{", 5));
    }

    [Fact]
    public void Apply_ConvertsEveryConfig_AndLeavesAnUnreadableBasketAsItIs()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        db.Configs.Add(new StrategyConfig { Id = 1, BasketJson = "", EmaBasketJson = Basket, EmailRecipients = "" });
        db.Configs.Add(new StrategyConfig { Id = 2, BasketJson = "", EmaBasketJson = "[{ not json", EmailRecipients = "" });
        db.SaveChanges();

        ConvertRetiredEma21Entries.Apply(db, NullLogger.Instance);

        db.ChangeTracker.Clear();
        Assert.All(BasketCodec.Parse(db.Configs.Single(c => c.Id == 1).EmaBasketJson), e => Assert.Equal(StrategyType.Ema, e.StrategyType));
        Assert.Equal("[{ not json", db.Configs.Single(c => c.Id == 2).EmaBasketJson);
    }
}
```

(The `Replace` in `Convert_UsesTheBarSize…` edits the test's own literal, not a stored basket.)

- [ ] **Step 2: Generate the migration**

Run: `dotnet ef migrations add ConvertRetiredEma21Entries --project CRV.Core --startup-project CRV.Web`
Expected: a new `CRV.Core/Migrations/<ts>_ConvertRetiredEma21Entries.cs` with empty `Up` / `Down` and its Designer file; `TradingDbContextModelSnapshot.cs` unchanged (no schema change). If the snapshot changed, stop: another plan left a pending model change.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~ConvertRetiredEma21EntriesTests"`
Expected: build FAILS with `'ConvertRetiredEma21Entries' does not contain a definition for 'Convert'`.

- [ ] **Step 4: Write the conversion**

Replace the generated `CRV.Core/Migrations/<ts>_ConvertRetiredEma21Entries.cs` body with (keep the generated namespace, class name and the `#nullable disable` line):

```csharp
using System.Text.Json;
using CRV.Core.Data;
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <summary>
    /// Each stored EMA21 entry (type 4) in the EMA list becomes a switched-off EMA touch of EMA 21 with
    /// the same ticker, sessions and sizing, so nothing trades until someone reviews it. The baskets are
    /// JSON, which a migration can't edit through <see cref="BasketCodec"/>, so <c>Up</c> only records that
    /// the step is due and <c>Program.cs</c> runs <see cref="Apply"/> right after <c>Migrate()</c> applies it.
    /// </summary>
    public partial class ConvertRetiredEma21Entries : Migration
    {
        public const string LabelSuffix = " (migrated from EMA21)";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only: see Apply.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way: type 4 no longer runs.
        }

        /// <summary>The EMA list with its retired entries converted; null when it has none. Throws on unreadable JSON.</summary>
        public static string Convert(string emaBasketJson, int defaultBarMinutes)
        {
            if (string.IsNullOrWhiteSpace(emaBasketJson)) return null;
            var entries = BasketCodec.Parse(emaBasketJson);
            bool changed = false;
            foreach (var e in entries.Where(e => e.StrategyType == SetupValidation.RetiredEma21))
            {
                var c = e.Config;
                int bar = e.ExecutionTFMinutes is int tf && tf > 0 ? tf : Math.Max(1, defaultBarMinutes);
                e.StrategyType = StrategyType.Ema;
                e.Enabled = false;
                e.Label = (e.Label ?? "") + LabelSuffix;
                c.EmaSource = EmaSource.PriceVsEma;
                c.EmaEntry = EmaEntry.Touch;
                c.EmaPeriod = 21;
                c.EmaDirection = (c.AllowLong, c.AllowShort) switch { (true, false) => EmaDirection.Up, (false, true) => EmaDirection.Down, _ => EmaDirection.Both };
                c.SignalTimeframe = Enum.GetValues<SignalTimeframe>().First(t => t.IsWholeMultipleOf(bar));
                c.CloseAtRthClose = true;
                c.TargetMode = TargetMode.Atr;
                c.EmaStopMode = EmaStopMode.SignalBarExtreme;
                c.StopBuffer = 2m;
                changed = true;
            }
            return changed ? BasketCodec.Serialize(entries) : null;
        }

        /// <summary>Converts every stored config's EMA list. An unreadable list is left as it is and logged.</summary>
        public static void Apply(TradingDbContext db, ILogger log)
        {
            foreach (var cfg in db.Configs.ToList())
            {
                try
                {
                    if (Convert(cfg.EmaBasketJson, cfg.ExecutionTFMinutes) is { } json)
                    {
                        cfg.EmaBasketJson = json;
                        log.LogWarning("Config {Id}: EMA21 strategies converted to switched-off EMA touch strategies", cfg.Id);
                    }
                }
                catch (JsonException ex)
                {
                    log.LogError(ex, "Config {Id}: the EMA strategy list can't be read; its EMA21 entries were left as they are", cfg.Id);
                }
            }
            db.SaveChanges();
        }
    }
}
```

- [ ] **Step 5: Run it after `Migrate()`**

In `CRV.Web/Program.cs`, replace `    db.Database.Migrate();` with:

```csharp
    var convertRetiredEma21 = db.Database.GetPendingMigrations()
        .Any(m => m.EndsWith("_" + nameof(CRV.Core.Migrations.ConvertRetiredEma21Entries), StringComparison.Ordinal));
    db.Database.Migrate();
    if (convertRetiredEma21) CRV.Core.Migrations.ConvertRetiredEma21Entries.Apply(db, app.Logger);
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~ConvertRetiredEma21EntriesTests" && dotnet build CRV.Web`
Expected: PASS (9 tests); build succeeds.

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Migrations CRV.Web/Program.cs CRV.Core.Tests/Data/ConvertRetiredEma21EntriesTests.cs
git commit -m "feat(ema): migration turns stored EMA21 entries into switched-off EMA touch entries"
```

---

## Web

### Task 14: Styles, Strategies list and new EMA entries

**Files:**
- Modify: `CRV.Web/wwwroot/css/components.css`
- Modify: `CRV.Web/Pages/Setup/Strategies.cshtml`
- Modify: `CRV.Web/Services/StrategyBasketService.cs` (`Add`)
- Test: `CRV.Web.A11yTests/A11ySeed.cs`, `CRV.Web.A11yTests/EmaSetupPageTests.cs` (create)

**Interfaces:**
- Consumes: `StrategyText.Row/TypeName` (Task 12); plan 1's EMA basket routing in `Add`.
- Produces: CSS `.st-trigger*`, `.st-conf*`, `.st-meter*`, `.crumb`, `fieldset.st-field`; `A11ySeed.EmaIds` (`string[]`), `A11ySeed.EmaBasketJson`; new EMA entries start with EMA defaults.

- [ ] **Step 1: Seed EMA entries**

In `CRV.Web.A11yTests/A11ySeed.cs` add:

```csharp
    /// <summary>One EMA entry per setup-page layout: price vs EMA with each entry, two EMAs with cross and retest.</summary>
    public static readonly string[] EmaIds = { "a11y-ema-touch", "a11y-ema-cross", "a11y-ema-retest", "a11y-ema-two-cross", "a11y-ema-two-retest" };

    private static BasketEntry Ema(string id, EmaSource source, EmaEntry entry, string label)
    {
        var e = Entry(id, StrategyType.Ema, label);
        e.ExecutionTFMinutes = 5;
        e.Config = new StrategySetupConfig
        {
            EmaSource = source, EmaEntry = entry, EmaPeriod = 21, FastEma = 8, SlowEma = 21,
            EmaStopMode = entry == EmaEntry.Cross ? EmaStopMode.EmaAtr : EmaStopMode.SignalBarExtreme,
            StopBuffer = entry == EmaEntry.Cross ? 0.5m : 2m,
            TargetMode = TargetMode.RiskMultiple, AtrTp1Mult = 1m, AtrTp2Mult = 2m, Contracts = 1, MaxContracts = 1, MaxTrades = 2,
        };
        e.Enabled = false;   // switched off: no stored history in the test host
        return e;
    }

    /// <summary>The EMA list: plan 1's retired entry plus one entry per layout.</summary>
    public static string EmaBasketJson { get; } = BasketCodec.Serialize(
        BasketCodec.Parse(RetiredBasketJson).Concat(new[]
        {
            Ema(EmaIds[0], EmaSource.PriceVsEma, EmaEntry.Touch,       "EMA 21 touch · MNQ H4"),
            Ema(EmaIds[1], EmaSource.PriceVsEma, EmaEntry.Cross,       "EMA 21 cross · MNQ H4"),
            Ema(EmaIds[2], EmaSource.PriceVsEma, EmaEntry.CrossRetest, "EMA 21 cross + retest · MNQ H4"),
            Ema(EmaIds[3], EmaSource.EmaVsEma,   EmaEntry.Cross,       "EMA 8/21 cross · MNQ H4"),
            Ema(EmaIds[4], EmaSource.EmaVsEma,   EmaEntry.CrossRetest, "EMA 8/21 cross + retest · MNQ H4"),
        }).ToList());
```

and in `Apply`, replace plan 1's `cfg.EmaBasketJson = RetiredBasketJson;` with `cfg.EmaBasketJson = EmaBasketJson;`. Move `RetiredBasketJson` above `EmaBasketJson` if needed so it is initialised first (static initialisers run in text order).

- [ ] **Step 2: Write the failing page tests**

`CRV.Web.A11yTests/EmaSetupPageTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace CRV.Web.A11yTests;

[Collection(A11yCollection.Name)]
public class EmaSetupPageTests(A11yAppFixture app)
{
    private async Task<IPage> Open(IBrowserContext ctx, string path)
    {
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, path).ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
        return page;
    }

    [Fact]
    public async Task StrategiesList_DescribesEmaRows_AndOffersEmaToAdd()
    {
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await Open(ctx, "/setup/strategies");

        Assert.Equal("EMA · cross + retest · /MNQZ26 · NY · target 2R",
            (await page.Locator($"a[href='/setup/strategies/{A11ySeed.EmaIds[2]}'] small").TextContentAsync())!.Trim());
        Assert.Contains("EMA (touch or cross)", await page.Locator("#st-add-type").InnerTextAsync());
    }

    [Fact]
    public void AddEma_StartsWithEmaSettings()
    {
        var basket = app.Services.GetRequiredService<StrategyBasketService>();
        var (change, id) = basket.Add(StrategyType.Ema, "/MNQZ26", 2m, 0.25m, "test");
        try
        {
            Assert.True(change.Ok, change.Error);
            var item = basket.Find(id!)!;
            Assert.True(item.IsEmaBasket);
            var c = item.Entry.Config;
            Assert.Equal((TargetMode.RiskMultiple, 1m, 2m, true), (c.TargetMode, c.AtrTp1Mult, c.AtrTp2Mult, c.CloseAtRthClose));
            Assert.Equal((EmaEntry.CrossRetest, EmaStopMode.SignalBarExtreme, 2m), (c.EmaEntry, c.EmaStopMode, c.StopBuffer));
            Assert.Empty(CRV.Core.Models.EmaValidation.Entry(item.Entry, 5));
        }
        finally
        {
            basket.Remove(id!, "test");
        }
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~EmaSetupPageTests"`
Expected: FAIL — the row reads plan 3's generic text and the add picker has no EMA option; `AddEma_StartsWithEmaSettings` fails on `TargetMode`.

- [ ] **Step 4: Strategies page and new-entry defaults**

`CRV.Web/Pages/Setup/Strategies.cshtml`:
- Replace the row's `<small class="c-mut">…</small>` (plan 3's `@StrategyText.TypeName(e.StrategyType) · @e.Ticker · @StrategyText.Sessions(e) · @StrategyText.Target(e.Config)`) with `<small class="c-mut">@StrategyText.Row(e)</small>`.
- In the add picker's type list, append `StrategyType.Ema` after `StrategyType.SessionFakeout`.

`CRV.Web/Services/StrategyBasketService.cs`, in `Add`, replace `Config = new StrategySetupConfig { … },` with:

```csharp
                Config = type == StrategyType.Ema ? NewEmaConfig() : new StrategySetupConfig
                {
                    MaxTrades = 5, CutoffHour = 14, CutoffMinute = 30, Contracts = 2, MaxContracts = 2, AutoSizeByRisk = false,
                    HiVolMult = 1, StopPct = 0.35m, TargetPct = 65, PartialPct = 50, MinRr = 1.5m, Mode = "Aggressive",
                    OrderType = "Market", UsePartial = true, UseBe = true, PartialCts = 1, MaxEntrySlippage = 0,
                    MaxTradeRisk = 0, StopMode = "OrbPct", StopVwapTicks = 4,
                },
```

(the ORB values as they are in the file today) and add:

```csharp
    /// <summary>A new EMA strategy: the spec's signal defaults, 1R / 2R targets, one contract, closing at session end.</summary>
    private static StrategySetupConfig NewEmaConfig() => new()
    {
        MaxTrades = 2, CutoffHour = 14, CutoffMinute = 30, Contracts = 1, MaxContracts = 1, AutoSizeByRisk = false,
        MinRr = 1.5m, OrderType = "Market", UsePartial = true, UseBe = true, PartialPct = 50, CloseAtRthClose = true,
        TargetMode = TargetMode.RiskMultiple, AtrTp1Mult = 1m, AtrTp2Mult = 2m,
        EmaStopMode = EmaStopMode.SignalBarExtreme, StopBuffer = 2m,
    };
```

- [ ] **Step 5: Styles**

Append to `CRV.Web/wwwroot/css/components.css` (the classes plan 3 added — `.st-row-title`, `.st-wide`, `.st-usd`, `.ck-rr` — are already there):

```css
/* ── EMA setup: pickers, confirmations, history ───────────── */
.crumb { text-decoration: none; }
.crumb:hover { text-decoration: underline; }
fieldset.st-field { border: 0; margin: 0; padding: 0; min-width: 0; }
fieldset.st-field > legend { float: none; width: auto; padding: 0; font-size: inherit; }
.st-trigger { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: .5rem; }
.st-trigger.st-two { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.st-trigger .btn-check + label { display: grid; gap: .15rem; align-content: start; text-align: left; padding: .6rem .7rem; min-height: 44px;
    border: 1px solid var(--c-line); border-radius: var(--radius-sm); background: var(--c-raise); color: var(--c-text);
    font: 600 .85rem var(--font-body); cursor: pointer; }
.st-trigger .btn-check + label small { font: 400 .72rem var(--font-body); color: var(--c-muted); }
.st-trigger .btn-check + label svg { width: 34px; height: 16px; color: var(--c-muted); }
.st-trigger .btn-check:checked + label { border-color: var(--c-accent); background: var(--c-accent-soft); }
.st-trigger .btn-check:checked + label svg { color: var(--c-accent); }
.st-trigger .btn-check:focus-visible + label { outline: 2px solid var(--c-accent); outline-offset: 1px; }
.st-trigger .btn-check:disabled + label { opacity: .45; cursor: not-allowed; }
@media (max-width: 767.98px) {
    .st-trigger, .st-trigger.st-two { grid-template-columns: minmax(0, 1fr); }
    .st-trigger .btn-check + label { grid-template-columns: auto 1fr; align-items: center; column-gap: .6rem; }
    .st-trigger .btn-check + label svg { grid-row: span 2; }
}
.st-conf { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: .25rem .75rem; align-items: start; padding: .7rem 0; border-top: 1px solid var(--c-line); }
.st-conf:first-child { border-top: 0; padding-top: 0; }
.st-conf > input { margin-top: .2rem; }
.st-conf b { font-weight: 600; font-size: .875rem; }
.st-conf small { display: block; color: var(--c-muted); font-size: .76rem; }
.st-conf-params { grid-column: 2; display: flex; flex-wrap: wrap; gap: .5rem .75rem; align-items: center; margin-top: .35rem; font-size: .8rem; color: var(--c-muted); }
.st-conf-params .form-control, .st-conf-params .form-select { width: auto; min-width: 0; padding-top: .25rem; padding-bottom: .25rem; font-size: .85rem; }
.st-conf-params .form-control { width: 4.5rem; }
.st-meter { height: 6px; border-radius: 3px; background: var(--c-raise); overflow: hidden; margin: .35rem 0 .2rem; }
.st-meter > i { display: block; height: 100%; background: var(--c-up); }
.st-meter.warn > i { background: var(--c-accent); }
```

- [ ] **Step 6: Run the tests**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~EmaSetupPageTests|FullyQualifiedName~PageScanTests"`
Expected: PASS (the Strategies page scans include the EMA rows).

- [ ] **Step 7: Commit**

```bash
git add CRV.Web/wwwroot/css/components.css CRV.Web/Pages/Setup/Strategies.cshtml CRV.Web/Services/StrategyBasketService.cs \
  CRV.Web.A11yTests/A11ySeed.cs CRV.Web.A11yTests/EmaSetupPageTests.cs
git commit -m "feat(ema-ui): EMA rows and add option on the Strategies page; EMA styles"
```

---

### Task 15: Setup page — EMA panels, binding and save

**Files:**
- Modify: `CRV.Web/Pages/Setup/Strategy.cshtml`
- Modify: `CRV.Web/Pages/Setup/Strategy.cshtml.cs`
- Modify: `CRV.Core/Strategy/MinRrSaveCheck.cs` (`CheckAtr`)
- Modify: `CRV.Web.A11yTests/A11yPages.cs`, `EmaSetupPageTests.cs`
- Test: `CRV.Core.Tests/Strategy/MinRrSaveCheckTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 6, 9, 12, 14; plan 1's `EditableTypes`, `RootLabel`, JS `names`; plan 3's `Num(..., mode:)`, `TargetMode` radios, `MinRrSaveCheck`, `TypicalStopPoints`; plan 4's `HtfBarStore`.
- Produces: `StrategyModel.EditableTypes` includes `Ema`; `StrategyModel.BarMinutes` (`int`); `MinRrSaveCheck.CheckAtr(StrategySetupConfig c, decimal? atr, decimal? typicalStop)`; the form posts every EMA field; save sets the side switches from the direction and refuses an enabled entry with short history.

- [ ] **Step 1: Write the failing tests**

Append to `CRV.Core.Tests/Strategy/MinRrSaveCheckTests.cs` (plan 3's file):

```csharp
    [Fact]
    public void CheckAtr_TargetBelowMinRrOfTheTypicalStop_NamesTheAtrMultiple()
    {
        var c = new StrategySetupConfig { TargetMode = TargetMode.Atr, AtrTp2Mult = 1m, MinRr = 1.5m };
        // typical stop 40 pts, ATR 30: needs 1.5 × 40 / 30 = 2 × ATR
        Assert.Equal("Raise the target to at least 2 × ATR, or lower the minimum reward / risk.", MinRrSaveCheck.CheckAtr(c, 30m, 40m).Error);
        c.AtrTp2Mult = 2m;
        Assert.Null(MinRrSaveCheck.CheckAtr(c, 30m, 40m).Error);
        Assert.Equal(MinRrSaveCheck.AtrPerTrade, MinRrSaveCheck.CheckAtr(c, null, 40m).Warning);
        c.EnforceMinRr = false;
        c.AtrTp2Mult = 0.5m;
        Assert.Equal(new MinRrSaveResult(null, null), MinRrSaveCheck.CheckAtr(c, 30m, 40m));
    }
```

Append to `CRV.Web.A11yTests/EmaSetupPageTests.cs`:

```csharp
    private async Task Save(IPage page)
    {
        await page.RunAndWaitForNavigationAsync(() => page.Locator("#st-form button[type=submit]").First.ClickAsync());
    }

    [Fact]
    public async Task Save_DirectionSetsTheSideSwitches_AndTheFieldsRoundTrip()
    {
        var id = A11ySeed.EmaIds[3];
        var basket = app.Services.GetRequiredService<StrategyBasketService>();
        var original = basket.Find(id)!.Entry;
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, $"/setup/strategies/{id}");
            await page.Locator("label[for='ema-dir-Down']").ClickAsync();
            await page.Locator("label[for='ema-slow-50']").ClickAsync();
            await page.Locator("label[for='ema-conf-NotStretched']").ClickAsync();
            await Save(page);

            var c = basket.Find(id)!.Entry.Config;
            Assert.Equal((EmaDirection.Down, false, true), (c.EmaDirection, c.AllowLong, c.AllowShort));
            Assert.Equal(50, c.SlowEma);
            Assert.True(c.Confirmations.Single(k => k.Kind == ConfirmationKind.NotStretched).Enabled);
            Assert.Equal(4, c.Confirmations.Count);
        }
        finally
        {
            basket.Replace(id, original, "test");
        }
    }

    [Fact]
    public async Task SwitchingOnWithoutHistory_IsRefused_WithTheBarsNeeded()
    {
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await Open(ctx, $"/setup/strategies/{A11ySeed.EmaIds[1]}");
        await page.Locator("input[name='Entry.Enabled'][type=checkbox]").CheckAsync();
        await page.Locator("#st-form button[type=submit]").First.ClickAsync();

        await Assertions.Expect(page.Locator(".c-note.bad")).ToContainTextAsync("needs 21 H4 bars and 0 are stored");
        Assert.False(app.Services.GetRequiredService<StrategyBasketService>().Find(A11ySeed.EmaIds[1])!.Entry.Enabled);
    }

    [Fact]
    public async Task TwoEmas_TouchCantBePicked()
    {
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await Open(ctx, $"/setup/strategies/{A11ySeed.EmaIds[3]}");
        Assert.True(await page.Locator("#ema-entry-Touch").IsDisabledAsync());
    }
```

In `CRV.Web.A11yTests/A11yPages.cs`, add the EMA setup pages to `Routes` after the retired entry's route:

```csharp
        "/setup/strategies/" + A11ySeed.EmaIds[0],
        "/setup/strategies/" + A11ySeed.EmaIds[1],
        "/setup/strategies/" + A11ySeed.EmaIds[2],
        "/setup/strategies/" + A11ySeed.EmaIds[3],
        "/setup/strategies/" + A11ySeed.EmaIds[4],
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~MinRrSaveCheckTests"` — Expected: build FAILS (`CheckAtr` missing).
Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~EmaSetupPageTests"` — Expected: FAIL (the EMA page is read-only: plan 1 left `Ema` out of `EditableTypes`).

- [ ] **Step 3: `CheckAtr`**

In `CRV.Core/Strategy/MinRrSaveCheck.cs`, inside `MinRrSaveCheck` after `Check`:

```csharp
    /// <summary>
    /// An ATR target against MinRr × the typical stop, using the signal timeframe's ATR over the latest
    /// stored bars. Without an ATR or a typical stop the save passes with <see cref="AtrPerTrade"/>.
    /// </summary>
    public static MinRrSaveResult CheckAtr(StrategySetupConfig c, decimal? atr, decimal? typicalStop)
    {
        if (!c.EnforceMinRr) return Pass;
        if (atr is not decimal a || a <= 0 || typicalStop is not decimal stop) return new(null, AtrPerTrade);
        decimal minMult = Math.Ceiling(c.MinRr * stop / a * 100m) / 100m;
        return c.AtrTp2Mult >= minMult ? Pass : Fail($"{minMult:0.##} × ATR");
    }
```

- [ ] **Step 4: Page model**

In `CRV.Web/Pages/Setup/Strategy.cshtml.cs`:

- Add `using CRV.Core.Data;`, `using CRV.Core.Indicators;` and `using CRV.Core.Strategy.Confirmations;`.
- `EditableTypes` gains `StrategyType.Ema` as its last element.
- Add after `RootLabel`:

```csharp
    /// <summary>The bar size the entry runs on (its own, or the config's).</summary>
    public int BarMinutes => SetupValidation.BarMinutes(Entry, _cfgSvc.Current);

    /// <summary>Signal-timeframe ATR(14) over the latest stored bars of the entry's root, for the ATR target check.</summary>
    private async Task<decimal?> SignalAtrAsync(BasketEntry e)
    {
        var bars = await new HtfBarStore(_db).LatestAsync(TickerGroup.GetGroupKey(e.Ticker), e.Config.SignalTimeframe, EmaStrategy.AtrPeriod + 1);
        return EmaHistoryGate.Atr(bars);
    }

    /// <summary>Why an enabled EMA entry can't be switched on yet: too few stored bars for an EMA it reads.</summary>
    private async Task<IReadOnlyList<string>> EmaHistoryErrorsAsync(BasketEntry e)
    {
        var store = new HtfBarStore(_db);
        string root = TickerGroup.GetGroupKey(e.Ticker);
        var counts = new Dictionary<SignalTimeframe, int>();
        foreach (var (tf, _, _) in EmaHistoryGate.Needs(e.Config))
            counts[tf] = await store.CountAsync(root, tf);
        return EmaHistoryGate.Errors(e.Config, tf => counts[tf]);
    }
```

- In `OnPostSaveAsync`, after the auto-trail line (`if (!hadTrail && edited.AutoTrail is { Enabled: false }) edited.AutoTrail = null;`) add:

```csharp
        if (edited.StrategyType == StrategyType.Ema)
        {
            (edited.Config.AllowLong, edited.Config.AllowShort) = edited.Config.EmaDirection.Sides();
            edited.Config.Confirmations = EmaConfirmations.Normalized(edited.Config.Confirmations);
            if (edited.Enabled)
                Errors.AddRange((await EmaHistoryErrorsAsync(edited)).Select(p => char.ToUpperInvariant(p[0]) + p[1..] + "."));
        }
```

- Replace plan 3's `var rr = MinRrSaveCheck.Check(edited.Config, edited.PointValue, TypicalStopPoints, TypicalPositionPoints);` with:

```csharp
        var rr = edited.Config.TargetMode == TargetMode.Atr
            ? MinRrSaveCheck.CheckAtr(edited.Config, await SignalAtrAsync(edited), TypicalStopPoints)
            : MinRrSaveCheck.Check(edited.Config, edited.PointValue, TypicalStopPoints, TypicalPositionPoints);
```

- In `Validate(BasketEntry e)`, make the ORB-only lines skip EMA entries: wrap `if (c.StopMode == "OrbPct" && c.StopPct <= 0) …` and plan 3's `RangePct` target line in `if (e.StrategyType != StrategyType.Ema) { … }`.
- In `RestartNote`, add to `structural`:

```csharp
                         || (after.StrategyType == StrategyType.Ema && EmaSignalShape.From(before.Config) != EmaSignalShape.From(after.Config))
```

and change the running-engine message to `"Saved. The engine is running: the instrument, type, bar size, EMA signal settings or switching it on take effect the next time it starts."`.

- [ ] **Step 5: Markup**

In `CRV.Web/Pages/Setup/Strategy.cshtml`:

(a) In the top `@{ … }` block, after the `Check` helper, add:

```cshtml
    const string OrbTypes = "Pullback Retest OrbFakeout SessionFakeout";
    var confs = EmaConfirmations.Normalized(c.Confirmations);
    var confsOn = EmaConfirmations.Build(new StrategySetupConfig { EmaEntry = c.EmaEntry, Confirmations = confs }).Count;

    // A picker card: native radio + label, styled by .st-trigger.
    void Pick(string name, string id, string value, bool on, string icon, string title, string sub)
    {
        <input class="btn-check" type="radio" name="@name" id="@id" value="@value" checked="@on" />
        <label for="@id"><svg aria-hidden="true"><use href="#@icon" /></svg>@title<small id="@(id)-sub">@sub</small></label>
    }
    // A segmented radio group with a legend.
    void Seg(string name, string idPrefix, string legend, (string Value, string Label)[] options, string current, string? show = null)
    {
        <fieldset class="st-field" data-show="@show">
            <legend class="form-label">@legend</legend>
            <div class="c-seg c-seg-fill num">
                @foreach (var (v, l) in options)
                {
                    <input type="radio" class="btn-check" name="@name" id="@idPrefix-@v" value="@v" checked="@(v == current)" />
                    <label class="btn btn-sm" for="@idPrefix-@v" id="@idPrefix-@(v)-l">@l</label>
                }
            </div>
        </fieldset>
    }
    var periods = new[] { ("8", "8"), ("21", "21"), ("50", "50"), ("200", "200") };
```

Add `@using CRV.Core.Indicators` and `@using CRV.Core.Strategy.Confirmations` to the page's usings.

(b) Give the form the entry id: `<form method="post" asp-page-handler="Save" id="st-form" class="c-form" novalidate data-entry-id="@e.Id">`.

(c) Right after the Basics panel's closing `</section>`, insert the trigger icons and the Signal and Confirmations panels:

```cshtml
        <svg width="0" height="0" class="position-absolute" aria-hidden="true">
            <defs>
                <symbol id="t-touch" viewBox="0 0 34 16"><path fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" d="M1 11c6-1 12-2 32-6" opacity=".55"/><path fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" d="M2 3c4 0 6 3 9 6 1.5 1.4 3 1.5 4.5 0C18 6 22 2 32 1"/></symbol>
                <symbol id="t-both" viewBox="0 0 34 16"><path fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" d="M1 13C8 12 14 4 21 3s8 3 12 6"/><path fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" d="M1 4c8 1 18 5 32 3" opacity=".55"/></symbol>
                <symbol id="t-up" viewBox="0 0 34 16"><path fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" d="M1 14C10 13 18 6 33 2"/><path fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" d="M1 6c10 1 22 3 32 2" opacity=".55"/></symbol>
                <symbol id="t-price" viewBox="0 0 34 16"><path fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" d="M1 9c10-1 22-2 32-3" opacity=".55"/><path fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" d="M1 13l4-2 3 1 4-5 3 1 4-5 3 2 4-4 3 1 4-1"/></symbol>
                <symbol id="t-retest" viewBox="0 0 34 16"><path fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" d="M1 6c10 1 22 2 32 3" opacity=".55"/><path fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" d="M1 2c5 1 8 4 11 9 2 3 4 3 6 1 2-3 3-4 5-3 3 2 6 5 10 6"/><circle cx="20.5" cy="8" r="1.8" fill="currentColor"/></symbol>
            </defs>
        </svg>

        <section class="c-panel" data-types="Ema">
            <div class="c-panel-h"><h2>Signal</h2><span class="c-right">closed bars only</span></div>
            <div class="c-panel-b d-grid gap-3">
                <fieldset class="st-field">
                    <legend class="form-label">What crosses</legend>
                    <div class="st-trigger st-two">
                        @{ Pick("Entry.Config.EmaSource", "ema-src-PriceVsEma", "PriceVsEma", c.EmaSource == EmaSource.PriceVsEma, "t-price", "Price and an EMA", "Price against one EMA"); }
                        @{ Pick("Entry.Config.EmaSource", "ema-src-EmaVsEma", "EmaVsEma", c.EmaSource == EmaSource.EmaVsEma, "t-up", "Two EMAs", "Fast EMA against slow EMA"); }
                    </div>
                </fieldset>
                <fieldset class="st-field">
                    <legend class="form-label">Entry</legend>
                    <div class="st-trigger">
                        @{ Pick("Entry.Config.EmaEntry", "ema-entry-Touch", "Touch", c.EmaEntry == EmaEntry.Touch, "t-touch", "Touch", "Pulls back to the EMA, holds"); }
                        @{ Pick("Entry.Config.EmaEntry", "ema-entry-Cross", "Cross", c.EmaEntry == EmaEntry.Cross, "t-both", "Cross", "Enter on the cross"); }
                        @{ Pick("Entry.Config.EmaEntry", "ema-entry-CrossRetest", "CrossRetest", c.EmaEntry == EmaEntry.CrossRetest, "t-retest", "Cross + retest", "Cross, then come back to the EMA"); }
                    </div>
                </fieldset>
                <div>
                    @{ Seg("Entry.Config.EmaDirection", "ema-dir", "Direction", new[] { ("Up", "Up · long"), ("Down", "Down · short"), ("Both", "Both") }, c.EmaDirection.ToString()); }
                    <div class="form-text" id="ema-dir-help"></div>
                </div>

                <div class="st-grid">
                    <div class="st-field">
                        <label class="form-label" for="Entry.Config.SignalTimeframe">Signal timeframe</label>
                        <select class="form-select num" id="Entry.Config.SignalTimeframe" name="Entry.Config.SignalTimeframe" aria-describedby="ema-tf-help">
                            @foreach (var tf in Enum.GetValues<SignalTimeframe>()) { <option value="@tf" selected="@(c.SignalTimeframe == tf)">@tf</option> }
                        </select>
                        <div class="form-text" id="ema-tf-help"></div>
                    </div>
                    <div class="st-field" data-show="touch retest">
                        <label class="form-label" for="Entry.Config.CheckEveryExecutionBar">Check for a touch</label>
                        <select class="form-select" id="Entry.Config.CheckEveryExecutionBar" name="Entry.Config.CheckEveryExecutionBar">
                            <option value="false" selected="@(!c.CheckEveryExecutionBar)">When @EmaDescription.BarName(c.SignalTimeframe) closes</option>
                            <option value="true" selected="@c.CheckEveryExecutionBar">Every @Model.BarMinutes-minute bar, against the last closed @c.SignalTimeframe EMA</option>
                        </select>
                    </div>
                    @{ Seg("Entry.Config.EmaPeriod", "ema-period", "EMA", periods, c.EmaPeriod.ToString(inv), "price"); }
                    @{ Seg("Entry.Config.FastEma", "ema-fast", "Fast EMA", periods, c.FastEma.ToString(inv), "ema"); }
                    @{ Seg("Entry.Config.SlowEma", "ema-slow", "Slow EMA", periods, c.SlowEma.ToString(inv), "ema"); }
                    @{ Seg("Entry.Config.RetestEma", "ema-retest", "Retest which EMA", new[] { ("Fast", $"Fast ({c.FastEma})"), ("Slow", $"Slow ({c.SlowEma})") }, c.RetestEma.ToString(), "ema-retest"); }
                </div>
                <div class="c-note bad" role="alert" id="ema-fast-slow" hidden><i class="bi bi-exclamation-octagon" aria-hidden="true"></i>
                    <span>The fast EMA has to be shorter than the slow EMA: pick a shorter fast period or a longer slow one.</span></div>

                <div class="st-grid" data-show="cross retest">
                    <div class="st-field" data-show="price-cross price-retest">
                        @{ Num("Entry.Config.MinCloseBeyondAtr", "Cross counts when the close is past the EMA by", N(c.MinCloseBeyondAtr), "0 = any close on the other side.", "0.05", unit: "× ATR"); }
                    </div>
                    <div class="st-field" data-show="ema">
                        @{ Num("Entry.Config.MinSeparationAtr", "EMAs at least this far apart", N(c.MinSeparationAtr), "0 = off. EMAs that only meet and move apart again don't count as a cross.", "0.05", unit: "× ATR"); }
                    </div>
                    @{ Num("Entry.Config.HoldBars", "Cross has to hold for", c.HoldBars.ToString(inv), "1 = counts on the bar that crosses.", "1", unit: "bars"); }
                </div>

                <div class="st-grid" data-show="retest">
                    @{ Num("Entry.Config.RetestMinAwayAtr", "Move away first, at least", N(c.RetestMinAwayAtr), "A wiggle right at the EMA isn't a retest.", "0.05", unit: "× ATR past the EMA"); }
                    @{ Num("Entry.Config.RetestWindowBars", "Retest has to come within", c.RetestWindowBars.ToString(inv), "Later than that, the setup expires.", "1", unit: "bars of the cross"); }
                    @{ Num("Entry.Config.MaxEntriesPerCross", "Entries per cross", c.MaxEntriesPerCross.ToString(inv), "A close back across the EMA cancels the setup.", "1"); }
                </div>

                <div class="st-grid" data-show="touch retest">
                    <div class="st-field">
                        <label class="form-label" for="Entry.Config.TouchTicks">Counts as a touch within</label>
                        <div class="st-input">
                            <input class="form-control num" id="Entry.Config.TouchTicks" name="Entry.Config.TouchTicks" type="number" step="1" min="0" value="@c.TouchTicks" />
                            <span class="c-mut">ticks, or</span>
                            <input class="form-control num" name="Entry.Config.TouchAtr" type="number" step="0.05" min="0" value="@N(c.TouchAtr)" aria-label="Touch tolerance in ATR" />
                            <span class="c-mut">× ATR</span>
                        </div>
                        <div class="form-text">Whichever is larger.</div>
                    </div>
                    <div class="st-field" data-show="touch">
                        @{ Num("Entry.Config.RearmAtr", "Arm again after moving", N(c.RearmAtr), "Stops a run of touching bars from firing over and over.", "0.1", unit: "× ATR away"); }
                    </div>
                    @{ Check("Entry.Config.CloseBackOnSide", "Close back on the trade's side", c.CloseBackOnSide, "A long needs the bar to close above the EMA, a short below it."); }
                </div>
            </div>
        </section>

        <section class="c-panel" data-types="Ema">
            <div class="c-panel-h"><h2>Confirmations</h2>
                <div class="st-input small">
                    <label class="c-mut" for="Entry.Config.ConfirmationsNeeded">Need</label>
                    <select class="form-select form-select-sm c-select" id="Entry.Config.ConfirmationsNeeded" name="Entry.Config.ConfirmationsNeeded">
                        <option value="0" selected="@(c.ConfirmationsNeeded <= 0)">All on</option>
                        @for (var n = 1; n < confs.Count; n++) { <option value="@n" selected="@(c.ConfirmationsNeeded == n)" hidden="@(n >= confsOn)">At least @n</option> }
                    </select>
                </div>
            </div>
            <div class="c-panel-b">
                @for (var i = 0; i < confs.Count; i++)
                {
                    var k = confs[i];
                    var p = $"Entry.Config.Confirmations[{i}]";
                    var kid = $"ema-conf-{k.Kind}";
                    <div class="st-conf @(k.Enabled ? "" : "off")" data-show="@(k.Kind == ConfirmationKind.RejectionCandle ? "touch retest" : null)">
                        <input type="hidden" name="@(p).Kind" value="@k.Kind" />
                        <input class="form-check-input" type="checkbox" role="switch" id="@kid" name="@(p).Enabled" value="true" checked="@k.Enabled" aria-describedby="@(kid)-d" />
                        <input type="hidden" name="@(p).Enabled" value="false" />
                        <label for="@kid"><b>@EmaConfirmations.Label(k.Kind)</b><small id="@(kid)-d">@(k.Kind switch
                        {
                            ConfirmationKind.HtfTrend        => "Longs only above, shorts only below, an EMA on a higher timeframe.",
                            ConfirmationKind.TimeWindow      => "Skip the start and end of the regular session.",
                            ConfirmationKind.RejectionCandle => "The signal bar closes in the top part of its range (long) or the bottom part (short).",
                            _                                => "Skip when the signal bar closes too far from the EMA.",
                        })</small></label>
                        <div class="st-conf-params">
                            @switch (k.Kind)
                            {
                                case ConfirmationKind.HtfTrend:
                                    <select class="form-select num" name="@(p).TrendTimeframe" aria-label="Trend timeframe">
                                        <option value="D1" selected="@(k.TrendTimeframe == SignalTimeframe.D1)">D1</option>
                                        <option value="W1" selected="@(k.TrendTimeframe == SignalTimeframe.W1)">W1</option>
                                    </select>
                                    <select class="form-select num" name="@(p).TrendEmaPeriod" aria-label="Trend EMA">
                                        <option value="50" selected="@(k.TrendEmaPeriod == 50)">EMA 50</option>
                                        <option value="200" selected="@(k.TrendEmaPeriod == 200)">EMA 200</option>
                                    </select>
                                    break;
                                case ConfirmationKind.TimeWindow:
                                    <span>first</span><input class="form-control num" type="number" min="0" name="@(p).SkipFirstMinutes" value="@k.SkipFirstMinutes" aria-label="Skip first minutes" />
                                    <span>last</span><input class="form-control num" type="number" min="0" name="@(p).SkipLastMinutes" value="@k.SkipLastMinutes" aria-label="Skip last minutes" /><span>min</span>
                                    break;
                                case ConfirmationKind.RejectionCandle:
                                    <span>closes in the top</span><input class="form-control num" type="number" min="1" max="100" name="@(p).CloseInPct" value="@k.CloseInPct" aria-label="Close in top percent" /><span>% of the bar</span>
                                    break;
                                default:
                                    <span>at most</span><input class="form-control num" type="number" step="0.1" min="0" name="@(p).MaxAtrFromEma" value="@N(k.MaxAtrFromEma)" aria-label="Most ATR from the EMA" /><span>× ATR from the EMA</span>
                                    break;
                            }
                        </div>
                    </div>
                }
                <p class="c-mut small mb-0 pt-2 border-top">The chop filter still applies from Sessions &amp; risk. To skip it for this strategy, use “Ignore the chop filter”.</p>
            </div>
        </section>
```

(d) ORB-only fields get `types: OrbTypes` (or `data-types="@OrbTypes"` on raw markup): Entry mode select's `st-field`, `pct.MaxEntrySlippage`, the `StopMode` select's `st-field`, `pct.StopPct`, `Entry.Config.StopVwapTicks`, `Entry.Config.HiVolMult`, `UseVwap`, `UseOrbClose`, `UseEmaFilter`, `UseTickConfirmation`, `UseCustomOrbWindow`, and the two `OrbStart` / `OrbEnd` fields.

(e) In "Stop and exits", before the `StopMode` field add:

```cshtml
                <div class="st-field" data-types="Ema">
                    <label class="form-label" for="Entry.Config.EmaStopMode">Stop at</label>
                    <select class="form-select" id="Entry.Config.EmaStopMode" name="Entry.Config.EmaStopMode">
                        <option value="SignalBarExtreme" data-show="touch retest" selected="@(c.EmaStopMode == EmaStopMode.SignalBarExtreme)">Past the signal bar's low / high</option>
                        <option value="EmaAtr" data-show="cross retest" selected="@(c.EmaStopMode == EmaStopMode.EmaAtr)">Past the EMA by a multiple of ATR</option>
                        <option value="EntryAtr" selected="@(c.EmaStopMode == EmaStopMode.EntryAtr)">A multiple of ATR from entry</option>
                    </select>
                </div>
                @{ Num("Entry.Config.StopBuffer", "Stop buffer", N(c.StopBuffer), types: "Ema", unit: c.EmaStopMode == EmaStopMode.SignalBarExtreme ? "ticks" : "× ATR"); }
```

In plan 3's "Targets measured in" radios, replace its option array with:

```cshtml
                        @foreach (var (v, label, types) in new[] { (TargetMode.RangePct, "% of range", OrbTypes), (TargetMode.Atr, "ATR", "Ema"), (TargetMode.RiskMultiple, "Risk (R)", "Ema"), (TargetMode.Dollars, "Dollars ($)", "") })
                        {
                            <input type="radio" class="btn-check" name="Entry.Config.TargetMode" id="st-tmode-@v" value="@v" checked="@(c.TargetMode == v)" data-types="@types" />
                            <label class="btn btn-sm" for="st-tmode-@v" data-types="@types">@label</label>
                        }
```

and after plan 3's `TargetPct` field add:

```cshtml
                @{ Num("Entry.Config.AtrTp1Mult", "First target", N(c.AtrTp1Mult), types: "Ema", unit: c.TargetMode == TargetMode.Atr ? "× ATR" : "R", mode: "Atr RiskMultiple"); }
                @{ Num("Entry.Config.AtrTp2Mult", "Final target", N(c.AtrTp2Mult), types: "Ema", unit: c.TargetMode == TargetMode.Atr ? "× ATR" : "R", mode: "Atr RiskMultiple"); }
```

In plan 3's preview script, the line that shows target-mode fields becomes a list match:

```js
            if (el.dataset.targetMode) el.hidden = !el.dataset.targetMode.split(' ').includes(mode);
```

(f) In the side column, change the plain-words panel to:

```cshtml
            <div class="c-panel-b"><p class="c-plain mb-0" id="st-plain">@StrategyText.Describe(e, Model.BarMinutes)</p>
                <p class="c-mut small mt-2 mb-0" data-types="@OrbTypes">Updates after you save.</p>
                <p class="c-mut small mt-2 mb-0" data-types="Ema">Updates as you change the settings.</p></div>
```

(g) In the script, plan 1's lookup gains EMA: `const names = { 0: 'Pullback', 1: 'Retest', 2: 'OrbFakeout', 3: 'SessionFakeout', 5: 'Ema' };`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~MinRrSaveCheckTests"`
Expected: PASS.

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~EmaSetupPageTests"`
Expected: `Save_DirectionSetsTheSideSwitches…`, `SwitchingOnWithoutHistory…` PASS; `TwoEmas_TouchCantBePicked` still FAILS (the page script comes in Task 16).

- [ ] **Step 7: Commit**

```bash
git add CRV.Web/Pages/Setup/Strategy.cshtml CRV.Web/Pages/Setup/Strategy.cshtml.cs CRV.Core/Strategy/MinRrSaveCheck.cs \
  CRV.Core.Tests/Strategy/MinRrSaveCheckTests.cs CRV.Web.A11yTests/A11yPages.cs CRV.Web.A11yTests/EmaSetupPageTests.cs
git commit -m "feat(ema-ui): EMA signal, confirmations, stop and target fields on the setup page"
```

---

### Task 16: Setup page — live plain words, name, History panel and page behaviour

**Files:**
- Create: `CRV.Web/wwwroot/js/crv-ema-setup.js`
- Modify: `CRV.Web/Pages/Setup/Strategy.cshtml`, `Strategy.cshtml.cs`
- Test: `CRV.Web.A11yTests/EmaSetupPageTests.cs`

**Interfaces:**
- Consumes: Tasks 12, 15; `HistoryRequirement.For/Check/Note/SourceLine`, `HtfBarStore.CountAsync/LastFilledUtcAsync` (plan 4).
- Produces: `POST /setup/strategies/{id}?handler=Preview` → JSON `{ plain: string, name: string, history: [{ label, have, need, note, warn, percent }], source: string }`; `sealed record HistoryRow(string Label, int Have, int Need, string Note, bool Warn, int Percent)`; `StrategyModel.History`, `StrategyModel.HistorySource`.

- [ ] **Step 1: Write the failing tests**

Append to `CRV.Web.A11yTests/EmaSetupPageTests.cs`:

```csharp
    [Fact]
    public async Task PlainWords_AndName_FollowTheSettings_UntilTheNameIsTyped()
    {
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await Open(ctx, $"/setup/strategies/{A11ySeed.EmaIds[2]}");
        var name = page.Locator("[id='Entry.Label']");
        await name.FillAsync("");
        await name.DispatchEventAsync("input");

        await page.Locator("label[for='ema-entry-Cross']").ClickAsync();
        await Assertions.Expect(page.Locator("#st-plain")).ToContainTextAsync("crosses above the 21 EMA, checked when the H4 bar closes");
        await Assertions.Expect(name).ToHaveValueAsync("EMA 21 cross · MNQ H4");

        await name.FillAsync("My cross");
        await page.Locator("label[for='ema-period-50']").ClickAsync();
        await Assertions.Expect(page.Locator("#st-plain")).ToContainTextAsync("the 50 EMA");
        await Assertions.Expect(name).ToHaveValueAsync("My cross");
    }

    [Fact]
    public async Task HistoryPanel_ShowsBarsStoredAgainstTheParityDepth()
    {
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await Open(ctx, $"/setup/strategies/{A11ySeed.EmaIds[0]}");

        await Assertions.Expect(page.Locator("#ema-history")).ToContainTextAsync("H4 bars stored");
        await Assertions.Expect(page.Locator("#ema-history")).ToContainTextAsync("0 / 63");
        await page.SelectOptionAsync("[id='Entry.Config.SignalTimeframe']", "D1");
        await Assertions.Expect(page.Locator("#ema-history")).ToContainTextAsync("D1 bars stored");
    }

    [Fact]
    public async Task ConditionalFields_FollowSourceAndEntry()
    {
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await Open(ctx, $"/setup/strategies/{A11ySeed.EmaIds[0]}");   // price vs EMA, touch

        Assert.True(await page.Locator("[id='Entry.Config.RearmAtr']").IsVisibleAsync());
        Assert.False(await page.Locator("[id='Entry.Config.RetestWindowBars']").IsVisibleAsync());
        await page.Locator("label[for='ema-entry-CrossRetest']").ClickAsync();
        Assert.True(await page.Locator("[id='Entry.Config.RetestWindowBars']").IsVisibleAsync());
        Assert.False(await page.Locator("[id='Entry.Config.RearmAtr']").IsVisibleAsync());
    }
```

In `CRV.Web.A11yTests/PageScanTests.cs` add an interaction scan of every EMA layout's live state:

```csharp
    [Theory]
    [MemberData(nameof(Variants))]
    public async Task EmaSetupPage_AfterChangingTheSignal_HasNoWcagViolations(string theme, int width, int height)
    {
        var report = await PageScanner.ScanAsync(app, "/setup/strategies/" + A11ySeed.EmaIds[4], theme, width, height, async page =>
        {
            await page.Locator("label[for='ema-fast-21']").ClickAsync();   // fast = slow: the error note shows
            await page.Locator("#ema-fast-slow").WaitForAsync(new() { State = WaitForSelectorState.Visible });
            await page.Locator("#ema-history .st-meter").First.WaitForAsync();
        }, output);

        Assert.True(report is null, report);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~EmaSetupPageTests|FullyQualifiedName~EmaSetupPage_AfterChangingTheSignal"`
Expected: FAIL — no `#ema-history`, no live text, Touch not disabled.

- [ ] **Step 3: Page model — bind once, preview, history**

In `CRV.Web/Pages/Setup/Strategy.cshtml.cs`:

- Extract from `OnPostSaveAsync` the lines from `var edited = BasketCodec.Parse(BasketCodec.Serialize(new[] { before }))[0];` through the auto-trail line and plan 5's EMA block (Task 15) into:

```csharp
    /// <summary>The saved entry with the posted form applied on top; anything the form doesn't post keeps its value.</summary>
    private async Task<BasketEntry> BindEditedAsync(BasketEntry before)
    {
        // (the extracted lines, unchanged, ending with the EMA block; `return edited;`)
    }
```

so `OnPostSaveAsync` starts `if (!Load()) return NotFound(); if (ReadOnly) …; var before = Entry; var edited = await BindEditedAsync(before);`. Errors added during binding stay in `Errors` as today.

- Add:

```csharp
    public sealed record HistoryRow(string Label, int Have, int Need, string Note, bool Warn, int Percent);

    /// <summary>EMA entries: bars stored against the parity depth for each EMA the entry reads.</summary>
    public IReadOnlyList<HistoryRow> History { get; private set; } = Array.Empty<HistoryRow>();
    public string HistorySource { get; private set; } = "";

    private async Task LoadHistoryAsync(BasketEntry e)
    {
        if (e.StrategyType != StrategyType.Ema) return;
        var store = new HtfBarStore(_db);
        string root = TickerGroup.GetGroupKey(e.Ticker);
        var rows = new List<HistoryRow>();
        foreach (var (tf, period, use) in EmaHistoryGate.Needs(e.Config))
        {
            int have = await store.CountAsync(root, tf);
            int need = HistoryRequirement.For(period).ParityNeeded;
            rows.Add(new HistoryRow(
                use == "signal EMA" ? $"{tf} bars stored" : $"{tf} bars ({use})",
                have, need, HistoryRequirement.Note(have, period),
                HistoryRequirement.Check(have, period) != HistoryStatus.Ready,
                need == 0 ? 100 : Math.Min(100, have * 100 / need)));
        }
        History = rows;
        HistorySource = HistoryRequirement.SourceLine(root, await store.LastFilledUtcAsync(root));
    }

    /// <summary>The plain words, a default name and the History rows for the settings on the form, without saving.</summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (!Load()) return NotFound();
        var edited = await BindEditedAsync(Entry);
        await LoadHistoryAsync(edited);
        return new JsonResult(new
        {
            plain = StrategyText.Describe(edited, SetupValidation.BarMinutes(edited, _cfgSvc.Current)),
            name = edited.StrategyType == StrategyType.Ema ? EmaDescription.Title(edited) : edited.Label,
            history = History,
            source = HistorySource,
        });
    }
```

- Call `await LoadHistoryAsync(Entry);` in `OnGetAsync` after `Load()`, and in `OnPostSaveAsync` before each `return Page();` (`await LoadHistoryAsync(edited);`).

- [ ] **Step 4: History panel markup and script include**

In `Strategy.cshtml`, in the side column after the plain-words panel:

```cshtml
        <section class="c-panel" data-types="Ema">
            <div class="c-panel-h"><h2>History</h2><span class="c-right">Schwab</span></div>
            <div class="c-panel-b" id="ema-history" aria-live="polite">
                <div id="ema-history-rows">
                    @foreach (var h in Model.History)
                    {
                        <div class="c-kv"><span>@h.Label</span><span>@h.Have.ToString("N0", inv) / @h.Need.ToString("N0", inv)</span></div>
                        <div class="st-meter @(h.Warn ? "warn" : "")" role="img" aria-label="@h.Percent% of the history the EMA needs to match TradingView"><i style="width:@(h.Percent)%"></i></div>
                        <p class="c-mut small mb-2">@h.Note</p>
                    }
                </div>
                <p class="c-mut small mb-0" id="ema-history-source">@Model.HistorySource</p>
            </div>
        </section>
```

In `@section Scripts`, after the existing `</script>`:

```cshtml
<script src="~/js/crv-ema-setup.js" asp-append-version="true"></script>
```

- [ ] **Step 5: Page script**

`CRV.Web/wwwroot/js/crv-ema-setup.js`:

```js
// EMA setup page: conditional fields, labels that follow the settings, and the live plain words,
// name and History panel (rendered by the page's Preview handler, so the wording lives in one place).
(function () {
    const form = document.getElementById('st-form');
    const type = document.getElementById('st-type');
    if (!form || !type) return;

    const $ = id => document.getElementById(id);
    const picked = name => (form.querySelector(`input[name="${name}"]:checked`) || {}).value;
    const isEma = () => type.value === '5';
    const tag = { PriceVsEma: 'price', EmaVsEma: 'ema', Touch: 'touch', Cross: 'cross', CrossRetest: 'retest' };
    const tfHelp = {
        M5: 'Every 5 minutes on the clock.', M15: 'Every 15 minutes on the clock.', M30: 'Every 30 minutes on the clock.',
        H1: 'On the hour.', H4: 'Bars open 18:00, 22:00, 02:00, 06:00, 10:00, 14:00 ET, the same as TradingView.',
        H8: 'Bars open 18:00, 02:00 and 10:00 ET; the last one ends at 17:00 (7 hours).',
        D1: 'One bar per session, 18:00 to 17:00 ET.', W1: 'Sunday 18:00 to Friday 17:00 ET.',
        MN1: 'By trading date. Few signals: results need many years to mean anything.',
    };
    const tfBar = {
        M5: 'the 5-minute bar', M15: 'the 15-minute bar', M30: 'the 30-minute bar', H1: 'the hourly bar',
        H4: 'the H4 bar', H8: 'the H8 bar', D1: 'the daily bar', W1: 'the weekly bar', MN1: 'the monthly bar',
    };

    const nameBox = $('Entry.Label');
    // The name follows the settings while it is empty or still the entry's id; once typed, never again.
    let autoName = nameBox && (nameBox.value === '' || nameBox.value === form.dataset.entryId);
    nameBox?.addEventListener('input', e => { if (e.isTrusted) autoName = false; });

    function render() {
        if (!isEma()) return;
        const touch = $('ema-entry-Touch');
        touch.disabled = picked('Entry.Config.EmaSource') === 'EmaVsEma';
        $('ema-entry-Touch-sub').textContent = touch.disabled ? 'Only with price and an EMA' : 'Pulls back to the EMA, holds';
        if (touch.disabled && touch.checked) $('ema-entry-CrossRetest').checked = true;

        const src = tag[picked('Entry.Config.EmaSource')], entry = tag[picked('Entry.Config.EmaEntry')];
        const on = [src, entry, src + '-' + entry];
        form.querySelectorAll('[data-show]').forEach(el => { el.hidden = !el.dataset.show.split(' ').some(t => on.includes(t)); });

        // A stop choice the entry doesn't offer can't stay selected.
        const stop = $('Entry.Config.EmaStopMode');
        if (stop.selectedOptions[0]?.hidden) stop.value = [...stop.options].find(o => !o.hidden).value;
        const atrStop = stop.value !== 'SignalBarExtreme';
        const buffer = $('Entry.Config.StopBuffer');
        buffer.closest('.st-input').querySelector('.c-mut').textContent = atrStop ? '× ATR' : 'ticks';
        if (atrStop && buffer.value === '2') buffer.value = '0.5';
        if (!atrStop && buffer.value === '0.5') buffer.value = '2';

        const dirUp = $('ema-dir-Up-l'), dirDown = $('ema-dir-Down-l'), help = $('ema-dir-help');
        if (entry === 'touch') {
            dirUp.textContent = 'From above · long'; dirDown.textContent = 'From below · short';
            help.textContent = 'Long: price is above the EMA and dips to it. Short: price is below and rallies to it.';
        } else {
            dirUp.textContent = 'Up · long'; dirDown.textContent = 'Down · short';
            help.textContent = entry === 'retest'
                ? 'Down: crosses below, comes back up to the EMA, enters short. Up is the mirror image.'
                : 'Up enters long on a cross up; down enters short on a cross down.';
        }

        const tf = $('Entry.Config.SignalTimeframe').value;
        const bar = $('Entry.ExecutionTFMinutes')?.value || '';
        $('ema-tf-help').textContent = tfHelp[tf] + (bar ? ` Built from the ${bar}-minute bars.` : '');
        const when = $('Entry.Config.CheckEveryExecutionBar');
        when.options[0].textContent = `When ${tfBar[tf]} closes`;
        when.options[1].textContent = `Every ${bar || 'execution'}-minute bar, against the last closed ${tf} EMA`;

        const fast = picked('Entry.Config.FastEma'), slow = picked('Entry.Config.SlowEma');
        $('ema-retest-Fast-l').textContent = `Fast (${fast})`;
        $('ema-retest-Slow-l').textContent = `Slow (${slow})`;
        $('ema-fast-slow').hidden = !(src === 'ema' && parseInt(fast, 10) >= parseInt(slow, 10));

        let confsOn = 0;
        form.querySelectorAll('.st-conf').forEach(row => {
            const box = row.querySelector('input[type=checkbox]');
            row.classList.toggle('off', !box.checked);
            if (box.checked && !row.hidden) confsOn++;
        });
        const need = $('Entry.Config.ConfirmationsNeeded');
        [...need.options].forEach(o => { o.hidden = o.value !== '0' && parseInt(o.value, 10) >= confsOn; });
        if (need.selectedOptions[0]?.hidden) need.value = '0';

        const mode = picked('Entry.Config.TargetMode');
        form.querySelectorAll("[id='Entry.Config.AtrTp1Mult'], [id='Entry.Config.AtrTp2Mult']").forEach(input =>
            input.closest('.st-input').querySelector('.c-mut').textContent = mode === 'Atr' ? '× ATR' : 'R');
    }

    function renderHistory(rows, source) {
        $('ema-history-rows').innerHTML = rows.map(r =>
            `<div class="c-kv"><span>${CRV.esc(r.label)}</span><span>${r.have.toLocaleString('en-US')} / ${r.need.toLocaleString('en-US')}</span></div>` +
            `<div class="st-meter${r.warn ? ' warn' : ''}" role="img" aria-label="${r.percent}% of the history the EMA needs to match TradingView"><i style="width:${r.percent}%"></i></div>` +
            `<p class="c-mut small mb-2">${CRV.esc(r.note)}</p>`).join('');
        $('ema-history-source').textContent = source;
    }

    let timer = 0;
    async function preview() {
        if (!isEma()) return;
        const res = await fetch('?handler=Preview', { method: 'POST', body: new FormData(form) });
        if (!res.ok) return;
        const d = await res.json();
        $('st-plain').textContent = d.plain;
        if (autoName && nameBox) nameBox.value = d.name;
        renderHistory(d.history, d.source);
    }
    function changed() {
        render();
        clearTimeout(timer);
        timer = setTimeout(preview, 250);
    }

    form.addEventListener('input', changed);
    form.addEventListener('change', changed);
    type.addEventListener('change', changed);
    render();
})();
```

- [ ] **Step 6: Run the tests**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~EmaSetupPageTests|FullyQualifiedName~PageScanTests"`
Expected: PASS (every EMA layout, both themes, both viewports, plus the interaction scan).

- [ ] **Step 7: Commit**

```bash
git add CRV.Web/wwwroot/js/crv-ema-setup.js CRV.Web/Pages/Setup/Strategy.cshtml CRV.Web/Pages/Setup/Strategy.cshtml.cs \
  CRV.Web.A11yTests/EmaSetupPageTests.cs CRV.Web.A11yTests/PageScanTests.cs
git commit -m "feat(ema-ui): live plain words, default name and History panel on the EMA setup page"
```

---

### Task 17: Cockpit card

**Files:**
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml` (`createSetupCard`, `updateSetup`, new `_renderEmaCells`)
- Modify: `CRV.Web.A11yTests/CockpitSnapshot.cs`, `CockpitCardTests.cs`

**Interfaces:**
- Consumes: `SetupSnapshot.EmaCells` (hub JSON `emaCells`, Task 11); plan 1's disabled card branch.
- Produces: an EMA card draws `#<id>-ema` with its cells while no trade is open (`#<id>-grid` hidden); in a trade, the trade cells as for every strategy. `CockpitSnapshot.Ema(string id, string label, int state, params (string Label, string Value)[] cells)`.

- [ ] **Step 1: Write the failing tests**

In `CRV.Web.A11yTests/CockpitSnapshot.cs`:

- Add after `Disabled(...)`:

```csharp
    /// <summary>An EMA strategy's card with its state cells and no trade.</summary>
    public static SetupSnapshot Ema(string id, string label, int state, params (string Label, string Value)[] cells)
    {
        var s = Setup(id, label, "Ema", state);
        s.EmaCells = cells.Select(c => new CardCell(c.Label, c.Value)).ToList();
        return s;
    }
```

- At the end of `EveryState()`'s list (after plan 1's disabled card) add:

```csharp
            Ema("a11y-ema-retest-card", "EMA 21 cross + retest · MNQ H4", -1,
                ("Crossed down", "06:00 H4"), ("EMA 21 (H4)", "21,248.50"), ("Waiting for", "Retest ≤ 4 bars left"),
                ("Moved away", "0.41 ATR ✓"), ("Target", "$400 / contract"), ("Partial", "$200 · 100 pts"),
                ("Confirmations", "2 of 3"), ("H4 bar closes", "14:00 ET")),
            Ema("a11y-ema-cross-card", "EMA 8/21 cross · MNQ H1", 0,
                ("EMA 8 (H1)", "21,229.75"), ("EMA 21 (H1)", "21,204.00"), ("Last cross", "Up · 09:00"), ("Target", "2R")),
```

- Append `"▶ ARMED SHORT", "IDLE"` to `ExpectedStatuses`.

Append to `CockpitCardTests.cs`:

```csharp
    [Fact]
    public async Task EmaCard_WithoutATrade_ShowsItsStateCells()
    {
        await using var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, "/dashboard").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
        var setup = CockpitSnapshot.Ema("a11y-ema-cells", "EMA 21 cross + retest · MNQ H4", -1,
            ("Crossed down", "06:00 H4"), ("Waiting for", "Retest ≤ 4 bars left"));
        await page.EvaluateAsync("""
            json => {
                CRV.engine.status('Live');
                document.dispatchEvent(new CustomEvent('crv:update', { detail: JSON.parse(json) }));
            }
            """, CockpitSnapshot.Json([setup]));

        var cells = page.Locator("#a11y-ema-cells-ema .ck-cell");
        await cells.First.WaitForAsync();
        var texts = new List<string>();
        foreach (var cell in await cells.AllAsync())
            texts.Add($"{(await cell.Locator("span").TextContentAsync())!.Trim()}|{(await cell.Locator("b").TextContentAsync())!.Trim()}");
        Assert.Equal(new[] { "Crossed down|06:00 H4", "Waiting for|Retest ≤ 4 bars left" }, texts);
        Assert.True(await page.Locator("#a11y-ema-cells-grid").IsHiddenAsync());
        Assert.Equal("▶ ARMED SHORT", (await page.Locator("#status-a11y-ema-cells").TextContentAsync())!.Trim());
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~CockpitCardTests|FullyQualifiedName~CockpitSetupCards"`
Expected: FAIL — `#a11y-ema-cells-ema` never appears.

- [ ] **Step 3: Draw the cells**

In `CRV.Web/Pages/Dashboard/Index.cshtml`:

- In `createSetupCard`, add `Ema: "◆"` to `typeLabels`, give the trade grid an id — `<div class="ck-grid" id="${id}-grid">` — and after its closing `</div>` add:

```js
        <div class="ck-grid" id="${id}-ema" hidden></div>
```

- After `createSetupCard` add:

```js
// EMA strategies send their signal state as label / value cells; they replace the trade cells while no trade is open.
function _renderEmaCells(id, cells) {
    const ema = document.getElementById(id + "-ema");
    const grid = document.getElementById(id + "-grid");
    if (!ema || !grid) return;
    const show = Array.isArray(cells) && cells.length > 0;
    ema.hidden = !show;
    grid.hidden = show;
    if (show) ema.innerHTML = cells.map(c => `<div class="ck-cell"><span>${_esc(c.label)}</span><b>${_esc(c.value)}</b></div>`).join("");
}
```

- In `updateSetup`: in the `if (!enabled) { … return; }` block add `_renderEmaCells(id, null);` before `return`; in the `if (!trade) { … return; }` block add `_renderEmaCells(id, setup.emaCells);` before `return`; after that block (the trade path) add `_renderEmaCells(id, null);`.

- [ ] **Step 4: Run them**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build`
Expected: all pass (the cockpit scans now include both EMA cards in both themes and viewports).

- [ ] **Step 5: Run everything**

Run: `dotnet test CRV.Core.Tests && dotnet test CRV.Web.A11yTests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add CRV.Web/Pages/Dashboard/Index.cshtml CRV.Web.A11yTests/CockpitSnapshot.cs CRV.Web.A11yTests/CockpitCardTests.cs
git commit -m "feat(ema-ui): EMA state cells on the cockpit card"
```

---

### Task 18: Branch review

- [ ] **Step 1:** Run `grep -rn "can't run in this version" CRV.Core CRV.Web CRV.Core.Tests CRV.Web.A11yTests` — Expected: no matches.
- [ ] **Step 2:** Run the `security-review` skill on the branch. Points to check: cockpit cells and History rows reach the page through `_esc` / `CRV.esc`; the Preview handler binds only `Entry.*` like Save and is antiforgery-protected (it posts the form's token); the migration logs ids, never basket contents.
- [ ] **Step 3:** Open the PR with the baseline and final test counts, the Decisions list above, and a note for Cirino that new EMA strategies stay unable to switch on until `HtfBars` holds the history (plan 4's backfill).

---

## Self-Review

**1. Spec coverage**

| Spec item | Task |
|---|---|
| Config table, string enums, defaults | 1 |
| Direction drives AllowLong / AllowShort | 1 (mapping), 15 (save) |
| Validation: Touch needs PriceVsEma, Fast < Slow, TF multiple, history | 2 (+ plan 1 paths: save blocked, engine disables only that entry), 15 (history on save) |
| Touch, Cross, Cross + retest rules | 3, 4, 5 |
| Signals on closed signal bars; option B | 7, 9 |
| Timing: next execution bar's open, arm-then-enter; no lookahead (live and backtest) | 9 (`Run` asserts, H8 test), 10 (`EmaNoLookaheadTests`) |
| Stops per entry, buffer ticks / ATR | 8, 2 (Cross + bar stop invalid) |
| Targets Atr / RiskMultiple / Dollars, min-R guard, partial / BE, CloseAtRthClose | 8, 9, 15 (`CheckAtr`), 14 (new entries close) |
| Confirmations, All / at least N, inactive = fail, N clamping, evaluated when arming | 6, 9 |
| Chop stays `TickerGroup` | unchanged (`BypassChopFilter` passed through) |
| Migration `ConvertRetiredEma21Entries` via `BasketCodec`, unparseable left and logged | 13 |
| Setup page panels, conditional fields, corrections (bar size label is plan 1's; real bar size in copy; name never overwritten; plain words fixes; All / At least N) | 15, 16, 12 |
| Missing CSS classes | 14 (plan 3 added `.st-row-title`, `.st-wide`, `.st-usd`, `.ck-rr`) |
| Strategies row | 12, 14 |
| Cockpit card state cells | 11, 17 |
| `StrategyText` sentence for every combination | 12 |
| History panel (plan 4 Decision 7 hands it here) | 16 |
| WCAG: each Source × Entry layout, EMA cards, disabled card (plan 1) | 15, 16, 17 |
| Tests 1–12 of the spec | 3–5 (1, 3, 4, 5), 9 (2, 6, 10), 10 (6), 2 (7), 1 (8), 6 (9), 13 (11), 15–17 (12); DST and W1 / MN1 rollovers are bucket rules owned and tested by plan 4's `SessionBucketTests`; this plan pins the H8 17:00 boundary through the strategy |

**2. Placeholder scan:** no TBD / "similar to". One deliberate check names the exact command and what to do with its result: the empty generated migration (Task 13 Step 2).

**3. Type consistency:** `EmaBar`, `EmaSignal`, `EmaSignalOptions`, `TouchDetector.OnBar(b, o, allowLong, allowShort)`, `CrossDetector.OnBar(b, o)`, `CrossRetestDetector.OnSignalBar(b, o, allowLong, allowShort, checkRetest)` / `OnExecutionBar(b, o, allowLong, allowShort)` / `Accept(o)`, `ConfirmationGate.Decide/Required`, `EmaConfirmations.Build/Normalized/Label`, `TimeframeFeed(tf, executionMinutes)`, `HistoryNeed`, `IHtfHistorySource.ClosedBeforeAsync`, `HtfHistorySeeder.SeedAsync`, `EmaLevels.Build/Stop`, `EmaSkip`, `EmaSignalShape.From`, `EmaStrategy.AtrPeriod/SignalBarsClosed`, `CardCell`, `EmaCells`, `TargetText.Describe/Partial`, `EmaDescription.Describe/Row/Title/BarName`, `ConvertRetiredEma21Entries.Convert/Apply/LabelSuffix`, `MinRrSaveCheck.CheckAtr`, `A11ySeed.EmaIds/EmaBasketJson`, `CockpitSnapshot.Ema` are spelled the same in every task. Plan 6's reads match: `ConfirmationKind.HtfTrend`, `ConfirmationConfig { Kind, Enabled }`, `Confirmations` (a `List`), `ConfirmationsNeeded`, the EMA field names, `StrategyFactory.Create` → `EmaStrategy`, and signals produced through `OnTick` + `OnBar` alone.

**4. Review Focus:** each of the five lines has its test in the owning task (Tasks 8, 9, 10). Other inputs checked and covered: equal values (Task 4), a gap through the EMA (Task 3), a rejected touch keeping its arm (Tasks 3, 9), an inactive confirmation (Tasks 6, 9), a side the strategy doesn't trade (Tasks 3, 5), a reconfigure changing the signal shape (Task 9), a Cross saved with a signal-bar stop (Task 2).
