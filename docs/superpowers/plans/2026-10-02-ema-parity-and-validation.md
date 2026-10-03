# EMA Parity and Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the C# EMA strategy computes the same EMAs and fires on the same bars as a Pine v6 reference on TradingView's NQ1! bars, report how far Schwab's stored bars sit from TradingView's, and measure each EMA setup and each confirmation out of sample on `/validation`.

**Architecture:** A Pine v6 script (`docs/pine/ema-strategy-parity.pine`) runs every Source × Entry × Direction rule on closed signal bars through `request.security(..., lookahead_off)` and plots the EMA values, the signal and its own inputs, so one TradingView "Export chart data" CSV carries both the bars and Pine's answers. `TradingViewExport` reads that file; `EmaSignalTrace` drives the real `EmaStrategy` over the exported bars; `EmaParity` compares the two traces bar by bar; `BarSourceComparison` compares the exported bars with the `HtfBars` rows Schwab filled. `ValidationRunner` gains EMA studies (each setup alone, no confirmations, then one confirmation at a time, split by date with an embargo) built on `SampleSplit`, `EdgeTest` and `Ablation`. `/validation` shows both.

**Tech Stack:** .NET 10, xUnit 2.9.3, ASP.NET Core Razor Pages, EF Core (SQLite), Playwright + axe (existing `CRV.Web.A11yTests`), Pine Script v6.

**Spec:** `docs/superpowers/specs/2026-10-02-ema-parity-and-validation-design.md`

**Requires:** plans 1–5 merged — `2026-10-02-ema21-removal.md`, `2026-10-02-hold-and-close.md`, `2026-10-02-dollar-targets-and-rr-guard.md`, `2026-10-02-htf-bars-and-history.md`, `2026-10-02-ema-strategy.md`. Uses from them: `StrategyType.Ema`, `StrategyConfig.EmaBasketJson`, `StrategyConfig.ToEmaSetupConfigs()`, `TargetMode`, `TargetDollarsBasis`, `EnforceMinRr`, `SignalTimeframe`, `EasternTime`, `HtfBar`, `SessionBarAggregator`, `EmaIndicator`, `HtfBarRow` / `HtfBars`, `HistoryRequirement`, `EmaStrategy`, `EmaSource`, `EmaEntry`, `EmaDirection`, `RetestEmaChoice`, `EmaStopMode` and the EMA setup fields named in the ema-strategy spec's Config table.

## Global Constraints

- Branch `feat/ema-parity-and-validation` off `master`. Conventional Commit subjects (`fix(stats):`, `feat(ema):`, `test(a11y):`, `docs:`). Every commit message ends with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- Logic parity pass rule, verbatim from the spec: "after warmup, every signal timestamp matches and every EMA value is within ±1 tick". Warmup = `HistoryRequirement.For(slowest EMA period).ParityNeeded` signal bars. ±1 tick means `|Pine − C#| ≤ tick size`; never loosen it to make a run pass.
- Data parity "is reported, not pass/fail".
- "Fewer than 20 trades is `InsufficientEvidence` (expected for W1 and MN1) and shown as such, not hidden." (`EdgeTest.MinimumSample = 20`.)
- "Every result states the date range, instrument, bar source (Schwab or TradingView export), and fill and commission assumptions (`BacktestConfig.FillMode`, commission per side). No single backtest is presented as proof of profitability."
- Results appear on the existing `/validation` page — "no separate report file".
- Pine: v6, `barstate.isconfirmed`, `request.security(..., lookahead = barmerge.lookahead_off)`, session-anchored `"240"` / `"480"`.
- Tests never place orders or open broker connections: synthetic bars, `BarSnapshotStore` replays and the backtest executor only.
- Basket JSON is only changed through `BasketCodec` (never string replacement, never Python).
- New `/validation` markup reuses `c-panel`, `c-panel-h`, `c-panel-b`, `c-table-wrap`, `c-table`, `c-badge`, `c-note`, `c-plain`, `c-mut` and passes the WCAG gate (`dotnet test CRV.Web.A11yTests`).
- Comments say what or why, never history. File-scoped namespaces, nullable on, per-file `using Xunit;`, test names `Subject_Condition_Expectation`.
- Baselines before starting (after plans 1–5): record `dotnet test CRV.Core.Tests` and `dotnet test CRV.Web.A11yTests` pass counts; every task ends with the unit suite green.

## Review Focus

1. **A parity run that compared nothing** (export shorter than the warmup, or no signal bars after the parity start) must not read as a pass. Pinned by Task 4 `Compare_WithNothingAfterWarmup_DoesNotPass`.
2. **TradingView titles the export's columns with the indicator name** (`CRV EMA parity: EMA A`) on some versions; the reader must still find them. Pinned by Task 5 `Read_ColumnsTitledWithTheIndicatorName_AreFound`.
3. **An export taken at a bar size Schwab's stored history doesn't have** (e.g. 10 minutes) must say so, not report every bar as missing. Pinned by Task 8 `StoredTimeframeFor_ABarSizeWithNoStoredHistory_Throws`.
4. **A study whose setup traded exactly once** (likely on W1 / MN1) must not crash: `SampleSplit.ByFraction` throws today on one trade (`Math.Clamp(cut, 1, 0)`). Pinned by Task 1.
5. **Isolating one EMA setup changes the enabled tickers**, and with them the snapshot key; the study must still replay the basket's snapshot. Pinned by Task 9 `EmaStudies_OnOneSession_ReportInsufficientEvidenceEverywhere` (its basket holds an enabled ORB entry on a second ticker).

## Decisions

Facts the spec and the shared contract leave open, decided here:

1. **Where the C# signals come from.** `EmaSignalTrace` drives the real `EmaStrategy` (created by `StrategyFactory.Create`) through `ISetupStrategy` in the backtest's order (the bar's open tick, then the bar). Every entry is treated as a trade that closes at once (`SetInTrade(true)` then `SetInTrade(false)`) and `ResetTradeCounters()` runs after it, because the Pine reference has no position: a signal the strategy skips only because it is in a trade would be a lifecycle difference, not a logic one. Confirmations are off, the min-R guard is off, the trade cap is 1,000,000, one contract, no auto-size. The EMA values come from `SessionBarAggregator` + `EmaIndicator`, the classes the strategy builds its signal bars and EMAs with.
2. **One export, not an import.** The spec says the C# backtest runs on the TradingView bars "imported through htf-bars-and-history's CSV path". This plan reads the bars straight from the same "Export chart data" file that carries Pine's columns instead: `HtfBars` holds the root's live history (unique on Root, Timeframe, OpenTime), and importing TradingView's bars there would overwrite or mix with Schwab's — the very data the data-parity comparison measures. `HtfCsvImport` is left for its own purpose (depth Schwab no longer serves).
3. **Common seed point.** TradingView requests higher-timeframe history far beyond the chart's first bar, so a plain `ta.ema` would start years earlier than anything the C# side can see. The Pine script computes its EMAs and ATR by hand from the input **Parity start** (a signal-bar open; a Sunday 18:00 ET works for every timeframe), and the C# trace starts from the same bar. Both are SMA-seeded (Pine `ta.ema`'s seed, `EmaIndicator`'s seed); ATR is Wilder with an SMA seed and a first true range of high − low (`AtrIndicator`).
4. **Signal-bar keys.** Rows are matched on a key, never on the chart row: intraday signal bars by their open time, D1 by trading date at 00:00 UTC (Pine `time_tradingday`; C# `EasternTime.ToEastern(OpenUtc).AddHours(6).Date`). Keys travel as minutes since the Unix epoch so the CSV never prints them in exponent form. Logic parity covers M5, M15, M30, H1, H4, H8 and D1. W1 and MN1 are not covered: an intraday export cannot reach their warmup, and they are built from our D1 (htf-bars-and-history), which D1 parity already checks. The validation studies cover every timeframe.
5. **ATR length 14.** The ema-strategy spec has no ATR-length field; this plan takes the strategy's signal-bar ATR to be 14 (`AtrIndicator`'s default) as `EmaParity.StrategyAtrLength`, and `EmaParity.Run` refuses an export made with another value.
6. **Pine readings of rules the ema-strategy spec leaves open** (if plan 5's C# reads one differently, parity reports it; the spec decides which side is wrong, and where the spec is silent, ask Cirino):
   - Touch: "the previous close was above the EMA" compares the previous close with the previous bar's EMA. Both sides start armed. An arm is used only by a signal its direction allows.
   - Cross: the side is the sign of close − EMA (price) or fast − slow (two EMAs); zero is no side and never a cross; a cross needs a previous non-zero side. With `HoldBars` = N the cross bar counts as 1 and the check runs on the Nth bar; a zero or opposite side before then cancels it. `MinCloseBeyondAtr` / `MinSeparationAtr` are checked on that confirming bar; failing them drops the cross.
   - Cross + retest: any confirmed cross (either side) starts `Crossed`; only allowed sides signal. "Moved away" is measured from the cross bar onwards at each bar's extreme (high for longs, low for shorts) against that bar's retest EMA and ATR, updated after the bar's touch check, so the touch must come on a later bar; a signal bar does not count as moving away, and a second entry (`MaxEntriesPerCross` 2) needs a fresh move away. The touch check runs before the cancel check, so with `CloseBackOnSide` off a touch bar that closes across still signals. The window counts signal bars from the cross bar; bar W after the cross is the last that can signal.
7. **Plan 5's confirmation storage is not in the shared contract.** This plan assumes, in `CRV.Core.Models` beside `StrategySetupConfig`: `enum ConfirmationKind { HtfTrend, TimeWindow, RejectionCandle, NotStretched }`, `class ConfirmationConfig { ConfirmationKind Kind; bool Enabled; … }`, `List<ConfirmationConfig> StrategySetupConfig.Confirmations` (defaults: trend and time window on) and `int StrategySetupConfig.ConfirmationsNeeded` (0 = All). Only `EmaConfirmationSwitches.cs` knows this shape; Task 2's surface test fails to compile if plan 5 merged something else, and that one file plus the test are what to adapt.
8. **Where exports live.** `Data/parity/*.csv` under the working directory — `DATA_DIR` when set (Azure, the a11y host), otherwise wherever `dotnet run` was started — beside the database whose stored bars they are compared with. `/validation` lists the folder and opens only a listed name; there is no upload. `.gitignore` gains `Data/parity/`.
9. **Data parity source.** `HtfBars` rows for root `NQ` (TradingView's reference is CME_MINI:NQ1!) at the export's bar size: 5 → M5, 15 → M15, 30 → M30, 60 → H1.
10. **Study shape.** Each enabled EMA setup runs alone (every ORB entry, legacy setup and other EMA entry off) but replays the whole basket's snapshot. The split is 70/30 by date; the embargo is the larger of the page's one day and two signal bars (`ValidationRunner.EmbargoFor`). Each confirmation's run is split at the baseline's boundary and compared with the baseline on both sides (`Ablation` in sample and out of sample).
11. **Parity and fixture levels.** The parity setup and the test fixtures use `EmaStopMode.EntryAtr` (× 1.0) and a `TargetMode.Dollars` target: both are valid for every entry type, and they exist only because the strategy needs levels to emit an entry.
12. **Mockup gate.** The approved EMA mockup does not include `/validation`; per Cirino's rule, Task 10 Step 1 shows an HTML mockup of the new panels and waits for approval before any page markup is written.
13. **Bug fixed on the way:** `SampleSplit.ByFraction` throws on a single trade (Task 1).

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Modify | `CRV.Core/Statistics/SampleSplit.cs:82-89` | A single trade is in-sample instead of throwing |
| Modify | `CRV.Core/Models/StrategyConfig.cs` (after `MapBasketEntries`) | `MapEmaBasketEntries` through `BasketCodec` |
| Create | `CRV.Backtest/Experiments/EmaConfirmationSwitches.cs` | All confirmations off, or exactly one on (the only code that knows plan 5's confirmation shape) |
| Create | `docs/pine/ema-strategy-parity.pine` | Pine v6 reference |
| Create | `CRV.Backtest/Experiments/EmaParity.cs` | `EmaTraceRow`, `EmaParityMismatch`, `EmaParityReport`, `EmaParity` (keys, compare, parity setup, run) |
| Create | `CRV.Backtest/DataLoaders/TradingViewExport.cs` | Reads an "Export chart data" CSV: bars, Pine rows, `PineParityParameters` |
| Create | `CRV.Backtest/Experiments/EmaSignalTrace.cs` | Drives `EmaStrategy` over bars, records EMA and signal per closed signal bar |
| Create | `CRV.Backtest/Experiments/BarSourceComparison.cs` | Schwab vs TradingView bar differences |
| Modify | `CRV.Backtest/Experiments/ValidationRunner.cs` | EMA studies, `IsolateEmaSetup`, `EmbargoFor`, snapshot key from the full basket |
| Modify | `CRV.Web/Pages/Validation/Index.cshtml(.cs)` | EMA study panels, parity panels, export picker |
| Modify | `CRV.Web.A11yTests/A11yAppFixture.cs`, `A11ySeed.cs`, `PageScanTests.cs` | Snapshot store in the temp dir, seeds, two new scans |
| Modify | `docs/validation.md`, `README.md:177`, `.gitignore` | Docs; ignore `Data/parity/` |
| Create | `CRV.Core.Tests/Statistics/SampleSplitTests.cs` (one test added) | Single-trade split |
| Create | `CRV.Core.Tests/Models/EmaBasketMappingTests.cs` | `MapEmaBasketEntries` |
| Create | `CRV.Core.Tests/Backtest/EmaPlanSurfaceTests.cs` | Pins the plan 4/5 names this plan consumes |
| Create | `CRV.Core.Tests/Backtest/EmaConfirmationSwitchesTests.cs` | Switches |
| Create | `CRV.Core.Tests/Backtest/EmaParityFixture.cs` | 36 hand-worked bars + Pine export text |
| Create | `CRV.Core.Tests/Backtest/EmaParityCompareTests.cs` | Spec test 1 |
| Create | `CRV.Core.Tests/Backtest/TradingViewExportTests.cs` | Reader |
| Create | `CRV.Core.Tests/Backtest/EmaSignalTraceTests.cs` | Trace + parity setup |
| Create | `CRV.Core.Tests/Backtest/EmaParityRunTests.cs` | End to end on a synthetic export |
| Create | `CRV.Core.Tests/Backtest/BarSourceComparisonTests.cs` | Spec test 2 |
| Create | `CRV.Core.Tests/Backtest/EmaStudyTests.cs` | Spec test 3 |
| Create | `CRV.Core.Tests/Backtest/EmaParityRealExportTests.cs` + `CRV.Core.Tests/Backtest/Fixtures/tv-nq1-60-h4-retest-both-8.csv` | Spec test 4 (after Cirino's export) |
| Modify | `CRV.Core.Tests/CRV.Core.Tests.csproj` | Copy the CSV fixture to output |

---

### Task 1: A single trade splits instead of throwing

**Files:**
- Modify: `CRV.Core/Statistics/SampleSplit.cs:82-89`
- Test: `CRV.Core.Tests/Statistics/SampleSplitTests.cs` (add after `AnEmptySetSplitsIntoTwoEmptySets`, ~line 105)

**Interfaces:**
- Consumes: nothing new.
- Produces: `SampleSplit.ByFraction` accepts one trade: it is in-sample, out-of-sample is empty. Task 9 relies on this.

- [ ] **Step 1: Write the failing test**

Add to `SampleSplitTests`:

```csharp
    [Fact]
    public void ByFraction_ASingleTrade_IsInSampleAndLeavesOutOfSampleEmpty()
    {
        // A weekly or monthly setup can trade once in a whole window. One trade cannot be
        // split, and the study must say "too few" rather than crash.
        var split = SampleSplit.ByFraction(Trades(1), 0.70, TimeSpan.FromDays(1));

        Assert.Single(split.InSample);
        Assert.Empty(split.OutOfSample);
        Assert.Equal(EdgeVerdict.InsufficientEvidence, split.OutOfSampleEdge.Verdict);
        Assert.False(split.FailedOutOfSample);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SampleSplitTests.ByFraction_ASingleTrade"`
Expected: FAIL with `System.ArgumentException` ("'1' cannot be greater than 0") from `Math.Clamp`.

- [ ] **Step 3: Write minimal implementation**

In `SampleSplit.ByFraction`, replace lines 82–89:

```csharp
        var ordered = trades.OrderBy(t => t.EnteredAt).ToList();
        if (ordered.Count == 0) return Empty(refusals);

        // One trade cannot be split: it is the in-sample side, and the empty out-of-sample
        // side reports insufficient evidence instead of a verdict.
        if (ordered.Count == 1)
            return Build(ordered, ordered[0].EnteredAt.AddTicks(1), embargo, refusals);

        int cut = (int)Math.Round(ordered.Count * inSampleFraction, MidpointRounding.AwayFromZero);
        cut = Math.Clamp(cut, 1, ordered.Count - 1);

        var boundary = ordered[cut].EnteredAt;
        return Build(ordered, boundary, embargo, refusals);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SampleSplitTests"`
Expected: PASS (all `SampleSplitTests`).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Statistics/SampleSplit.cs CRV.Core.Tests/Statistics/SampleSplitTests.cs
git commit -m "fix(stats): a single trade splits into in-sample instead of throwing"
```

---

### Task 2: EMA basket mapping, confirmation switches, and the names this plan consumes

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs` — add `MapEmaBasketEntries` directly after `MapBasketEntries` (currently lines 585–597; plan 1 may have moved it)
- Create: `CRV.Backtest/Experiments/EmaConfirmationSwitches.cs`
- Test: `CRV.Core.Tests/Models/EmaBasketMappingTests.cs`, `CRV.Core.Tests/Backtest/EmaConfirmationSwitchesTests.cs`, `CRV.Core.Tests/Backtest/EmaPlanSurfaceTests.cs`

**Interfaces:**
- Consumes (plans 1, 3, 4, 5): `StrategyConfig.EmaBasketJson`, `StrategyConfig.ToEmaSetupConfigs()`, `StrategyType.Ema`, the EMA fields on `StrategySetupConfig`, `ConfirmationKind`, `ConfirmationConfig`, `Confirmations`, `ConfirmationsNeeded` (Decision 7), `SessionBarAggregator(SignalTimeframe, int executionMinutes)`, `HtfBar? OnExecutionBar(Bar)`, `EmaIndicator(int)` with `Add`, `IsReady`, `Value`, `HistoryRequirement.For(int)`, `EasternTime.ToEastern(DateTime)`, `HtfBarRow`.
- Produces:
  - `void StrategyConfig.MapEmaBasketEntries(Action<BasketEntry> change)`
  - `static class EmaConfirmationSwitches` (namespace `CRV.Backtest.Experiments`): `void ClearAll(StrategySetupConfig setup)`, `IReadOnlyList<(string Name, Action<StrategySetupConfig> Enable)> For(EmaEntry entry)`, `string Label(ConfirmationKind kind)`.

- [ ] **Step 1: Write the surface test (pins plan 4/5 names)**

`CRV.Core.Tests/Backtest/EmaPlanSurfaceTests.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The names the parity and validation work reads from the htf-bars-and-history and
/// ema-strategy plans. If this file stops compiling, those plans merged different names:
/// adapt this file and EmaConfirmationSwitches.cs, nothing else.
/// </summary>
public class EmaPlanSurfaceTests
{
    [Fact]
    public void EmaSetupFields_AreTheOnesParityAndStudiesSet()
    {
        var setup = new StrategySetupConfig
        {
            StrategyType = StrategyType.Ema,
            EmaSource = EmaSource.EmaVsEma, EmaEntry = EmaEntry.CrossRetest, EmaDirection = EmaDirection.Down,
            EmaPeriod = 50, FastEma = 8, SlowEma = 21, RetestEma = RetestEmaChoice.Slow,
            SignalTimeframe = SignalTimeframe.H4, CheckEveryExecutionBar = false,
            TouchTicks = 2, TouchAtr = 0.2m, RearmAtr = 0.6m, CloseBackOnSide = false,
            MinCloseBeyondAtr = 0.1m, MinSeparationAtr = 0.2m, HoldBars = 2,
            RetestMinAwayAtr = 0.3m, RetestWindowBars = 12, MaxEntriesPerCross = 2,
            EmaStopMode = EmaStopMode.EntryAtr, StopBuffer = 1.5m,
            TargetMode = TargetMode.Dollars, TargetDollars = 400m, TargetDollarsBasis = TargetDollarsBasis.PerContract,
            EnforceMinRr = false, ConfirmationsNeeded = 0,
        };
        setup.Confirmations.Add(new ConfirmationConfig { Kind = ConfirmationKind.NotStretched, Enabled = true });

        Assert.Equal(StrategyType.Ema, StrategyFactory.Create(setup).StrategyType);
    }

    [Fact]
    public void HigherTimeframeSurface_IsWhatTheTraceUses()
    {
        var aggregator = new SessionBarAggregator(SignalTimeframe.M5, executionMinutes: 1);
        HtfBar? closed = aggregator.OnExecutionBar(
            new Bar(new DateTime(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc), 1m, 2m, 0.5m, 1.5m, 10));
        var ema = new EmaIndicator(3);
        ema.Add(1m);
        var row = new HtfBarRow
        {
            Root = "NQ", Timeframe = SignalTimeframe.H1, OpenTime = new DateTime(2026, 4, 15, 13, 0, 0, DateTimeKind.Utc),
            Open = 1m, High = 2m, Low = 0.5m, Close = 1.5m, Volume = 10,
        };

        Assert.False(ema.IsReady);
        Assert.Equal(63, HistoryRequirement.For(21).ParityNeeded);
        Assert.Equal(new DateTime(2026, 4, 15, 9, 30, 0),
            EasternTime.ToEastern(new DateTime(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("NQ", row.Root);
        Assert.True(closed is null || closed.Timeframe == SignalTimeframe.M5);
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaPlanSurfaceTests"`
Expected: PASS. A compile error means plans 4/5 merged other names: stop, adapt this file (and, once written, `EmaConfirmationSwitches.cs`) to the merged names, and note the difference in the PR.

- [ ] **Step 3: Write the failing basket-mapping tests**

`CRV.Core.Tests/Models/EmaBasketMappingTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Models;

/// <summary>
/// A study varies one EMA entry at a time. The EMA basket is JSON on the config, so a change
/// has to be written back through BasketCodec, the same as MapBasketEntries does for ORB.
/// </summary>
public class EmaBasketMappingTests
{
    private static BasketEntry Ema(string id, bool enabled) => new()
    {
        Id = id, Enabled = enabled, Label = id, StrategyType = StrategyType.Ema,
        Ticker = "/MNQZ26", PointValue = 2m, TickSize = 0.25m,
        Config = new StrategySetupConfig { StrategyType = StrategyType.Ema, EmaPeriod = 21 },
    };

    [Fact]
    public void MapEmaBasketEntries_AChange_ReachesTheEmaSetups()
    {
        var cfg = new StrategyConfig { EmaBasketJson = BasketCodec.Serialize(new[] { Ema("a", true), Ema("b", false) }) };

        cfg.MapEmaBasketEntries(e => { e.Enabled = e.Id == "b"; e.Config.EmaPeriod = 50; });

        var setups = cfg.ToEmaSetupConfigs();
        Assert.False(setups.Single(s => s.Id == "a").Enabled);
        Assert.True(setups.Single(s => s.Id == "b").Enabled);
        Assert.All(setups, s => Assert.Equal(50, s.EmaPeriod));
    }

    [Fact]
    public void MapEmaBasketEntries_TheOrbBasket_IsLeftAlone()
    {
        var orb = BasketCodec.Serialize(new[] { new BasketEntry { Id = "p", Enabled = true, StrategyType = StrategyType.Pullback } });
        var cfg = new StrategyConfig { BasketJson = orb, EmaBasketJson = BasketCodec.Serialize(new[] { Ema("a", true) }) };

        cfg.MapEmaBasketEntries(e => e.Enabled = false);

        Assert.Equal(orb, cfg.BasketJson);
    }

    [Fact]
    public void MapEmaBasketEntries_AnUnreadableBasket_IsLeftAsItIs()
    {
        var cfg = new StrategyConfig { EmaBasketJson = "{not json" };

        cfg.MapEmaBasketEntries(e => e.Enabled = true);

        Assert.Equal("{not json", cfg.EmaBasketJson);
    }
}
```

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaBasketMappingTests"`
Expected: FAIL to compile — `'StrategyConfig' does not contain a definition for 'MapEmaBasketEntries'`.

- [ ] **Step 5: Implement `MapEmaBasketEntries`**

In `CRV.Core/Models/StrategyConfig.cs`, directly after `MapBasketEntries`:

```csharp
    /// <summary>
    /// Applies a change to every EMA basket entry and writes that basket back through
    /// <see cref="BasketCodec"/>. A basket that does not parse is left untouched, so a
    /// study can never overwrite a basket it could not read.
    /// </summary>
    public void MapEmaBasketEntries(Action<BasketEntry> change)
    {
        if (string.IsNullOrEmpty(EmaBasketJson)) return;

        List<BasketEntry> basket;
        try { basket = BasketCodec.Parse(EmaBasketJson); }
        catch (System.Text.Json.JsonException) { return; }
        if (basket.Count == 0) return;

        foreach (var e in basket) change(e);
        EmaBasketJson = BasketCodec.Serialize(basket);
    }
```

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaBasketMappingTests"`
Expected: PASS (3 tests).

- [ ] **Step 7: Write the failing switch tests**

`CRV.Core.Tests/Backtest/EmaConfirmationSwitchesTests.cs`:

```csharp
using CRV.Backtest.Experiments;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Backtest;

public class EmaConfirmationSwitchesTests
{
    [Fact]
    public void ClearAll_ADefaultSetup_LeavesNoConfirmationOn()
    {
        var setup = new StrategySetupConfig();   // ships with trend and time window on

        EmaConfirmationSwitches.ClearAll(setup);

        Assert.DoesNotContain(setup.Confirmations, c => c.Enabled);
    }

    [Theory]
    [InlineData(EmaEntry.Touch, 4)]
    [InlineData(EmaEntry.CrossRetest, 4)]
    [InlineData(EmaEntry.Cross, 3)]
    public void For_RejectionCandle_IsOfferedOnlyToTouchAndRetest(EmaEntry entry, int count)
    {
        var switches = EmaConfirmationSwitches.For(entry);

        Assert.Equal(count, switches.Count);
        Assert.Equal(entry != EmaEntry.Cross, switches.Any(s => s.Name == "Rejection candle"));
    }

    [Fact]
    public void For_EachSwitch_LeavesExactlyItsOwnConfirmationOn()
    {
        foreach (var (name, enable) in EmaConfirmationSwitches.For(EmaEntry.Touch))
        {
            var setup = new StrategySetupConfig();

            enable(setup);

            var on = Assert.Single(setup.Confirmations, c => c.Enabled);
            Assert.Equal(name, EmaConfirmationSwitches.Label(on.Kind));
            Assert.Equal(0, setup.ConfirmationsNeeded);
        }
    }
}
```

- [ ] **Step 8: Run to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaConfirmationSwitchesTests"`
Expected: FAIL to compile — `The name 'EmaConfirmationSwitches' does not exist`.

- [ ] **Step 9: Implement the switches**

`CRV.Backtest/Experiments/EmaConfirmationSwitches.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;

namespace CRV.Backtest.Experiments;

/// <summary>
/// The EMA strategy's confirmations as switches a study can flip: all off for the bare
/// signal, or exactly one on. The only code in the parity and validation work that knows
/// how a setup stores its confirmations.
/// </summary>
public static class EmaConfirmationSwitches
{
    /// <summary>Switches every confirmation off, leaving the bare signal.</summary>
    public static void ClearAll(StrategySetupConfig setup)
    {
        foreach (var c in setup.Confirmations) c.Enabled = false;
    }

    /// <summary>
    /// One switch per confirmation that applies to <paramref name="entry"/>; each leaves only
    /// its own confirmation on. The rejection candle reads a touch or retest bar, so a plain
    /// cross does not get it.
    /// </summary>
    public static IReadOnlyList<(string Name, Action<StrategySetupConfig> Enable)> For(EmaEntry entry) =>
        Enum.GetValues<ConfirmationKind>()
            .Where(k => k != ConfirmationKind.RejectionCandle || entry != EmaEntry.Cross)
            .Select(k => (Label(k), (Action<StrategySetupConfig>)(s => EnableOnly(s, k))))
            .ToList();

    public static string Label(ConfirmationKind kind) => kind switch
    {
        ConfirmationKind.HtfTrend        => "Higher-timeframe trend",
        ConfirmationKind.TimeWindow      => "Time window",
        ConfirmationKind.RejectionCandle => "Rejection candle",
        ConfirmationKind.NotStretched    => "Not stretched",
        _                                => kind.ToString(),
    };

    private static void EnableOnly(StrategySetupConfig setup, ConfirmationKind kind)
    {
        ClearAll(setup);
        var own = setup.Confirmations.FirstOrDefault(c => c.Kind == kind);
        if (own is null)
        {
            // A setup saved without this confirmation gets it with its shipped defaults.
            own = new ConfirmationConfig { Kind = kind };
            setup.Confirmations.Add(own);
        }
        own.Enabled = true;
        setup.ConfirmationsNeeded = 0;
    }
}
```

- [ ] **Step 10: Run the task's tests and the suite**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaConfirmationSwitchesTests|FullyQualifiedName~EmaBasketMappingTests|FullyQualifiedName~EmaPlanSurfaceTests"`
Expected: PASS (9 tests).
Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failed.

- [ ] **Step 11: Commit**

```bash
git add CRV.Core/Models/StrategyConfig.cs CRV.Backtest/Experiments/EmaConfirmationSwitches.cs \
  CRV.Core.Tests/Models/EmaBasketMappingTests.cs CRV.Core.Tests/Backtest/EmaConfirmationSwitchesTests.cs \
  CRV.Core.Tests/Backtest/EmaPlanSurfaceTests.cs
git commit -m "feat(ema): map EMA basket entries and switch confirmations for studies"
```

---

### Task 3: Pine v6 reference script

**Files:**
- Create: `docs/pine/ema-strategy-parity.pine`

**Interfaces:**
- Produces the export column contract that Task 5 reads (titles exact, matched case-insensitively, optionally prefixed `<indicator name>: `):
  - `time` (UNIX seconds or ISO 8601), `open`, `high`, `low`, `close`, optional `Volume`
  - `HTF Key` — minutes since 1970-01-01 UTC: the signal bar's open, or for D the trading day at 00:00 UTC; set only on the chart bar where that signal bar closes, empty (`NaN`) elsewhere
  - `EMA A` — price-vs-EMA EMA or fast EMA; `EMA B` — slow EMA or empty; `Signal` — 1, −1, 0
  - `p_src` (0 PriceVsEma, 1 EmaVsEma), `p_entry` (0 Touch, 1 Cross, 2 CrossRetest), `p_dir` (0 Up, 1 Down, 2 Both), `p_len`, `p_fast`, `p_slow`, `p_retest` (0 Fast, 1 Slow), `p_tf` (signal timeframe in minutes, D = 1440), `p_touch_ticks`, `p_touch_atr`, `p_rearm_atr`, `p_close_back` (0/1), `p_min_beyond_atr`, `p_min_sep_atr`, `p_hold`, `p_away_atr`, `p_window`, `p_max_entries`, `p_atr_len`, `p_start` (minutes since epoch), `p_tick`

There is no automated Pine compiler here; the script is checked by Cirino in manual step M2.

- [ ] **Step 1: Write the script**

`docs/pine/ema-strategy-parity.pine`:

```pine
//@version=6
// CRV.Trading — EMA strategy parity reference.
//
// Runs the EmaStrategy signal rules (docs/superpowers/specs/2026-10-02-ema-strategy-design.md)
// on closed signal-timeframe bars, with no confirmations, so the C# strategy can be compared bar
// for bar with TradingView (docs/superpowers/specs/2026-10-02-ema-parity-and-validation-design.md).
//
// Use: a CME_MINI:NQ1! chart at the execution bar size (5 or 60 minutes), back-adjustment off,
// electronic trading hours; then "Export chart data…" with UNIX time. The export carries the bars
// and these columns (read by CRV.Backtest TradingViewExport):
//   HTF Key  minutes since 1970-01-01 UTC: the signal bar's open (time), or for D the trading day
//            at 00:00 UTC (time_tradingday). Set only on the chart bar where that signal bar closes.
//   EMA A    the EMA (price vs EMA) or the fast EMA (EMA vs EMA) on that signal bar
//   EMA B    the slow EMA (EMA vs EMA), otherwise empty
//   Signal   1 long, -1 short, 0 none
//   p_*      every input, so the C# side rebuilds the same setup from the file
// The EMAs and ATR start at "Parity start" rather than at the start of TradingView's history, so
// both sides seed from the same first signal bar.
indicator("CRV EMA parity", overlay = true)

// ── Inputs ──────────────────────────────────────────────────────────
string srcIn      = input.string("PriceVsEma", "What crosses", options = ["PriceVsEma", "EmaVsEma"], group = "Signal")
string entryIn    = input.string("CrossRetest", "Entry", options = ["Touch", "Cross", "CrossRetest"], group = "Signal")
string dirIn      = input.string("Both", "Direction", options = ["Up", "Down", "Both"], group = "Signal")
int    emaLen     = input.int(21, "EMA (price vs EMA)", options = [8, 21, 50, 200], group = "Signal")
int    fastLen    = input.int(8, "Fast EMA", options = [8, 21, 50, 200], group = "Signal")
int    slowLen    = input.int(21, "Slow EMA", options = [8, 21, 50, 200], group = "Signal")
string retestIn   = input.string("Fast", "Retest EMA (EMA vs EMA)", options = ["Fast", "Slow"], group = "Signal")
string sigTf      = input.timeframe("240", "Signal timeframe", options = ["5", "15", "30", "60", "240", "480", "D"], group = "Signal")
int    touchTicks = input.int(1, "Touch tolerance, ticks", minval = 0, group = "Touch")
float  touchAtr   = input.float(0.10, "Touch tolerance, × ATR", minval = 0, step = 0.05, group = "Touch")
float  rearmAtr   = input.float(0.5, "Re-arm after, × ATR", minval = 0, step = 0.05, group = "Touch")
bool   closeBack  = input.bool(true, "Close back on the side", group = "Touch")
float  minBeyond  = input.float(0.0, "Cross: close beyond EMA, × ATR (0 = off)", minval = 0, step = 0.05, group = "Cross")
float  minSep     = input.float(0.0, "Cross: EMA separation, × ATR (0 = off)", minval = 0, step = 0.05, group = "Cross")
int    holdBars   = input.int(1, "Cross: hold bars (1 = the cross bar)", minval = 1, group = "Cross")
float  awayAtr    = input.float(0.25, "Retest: moved away, × ATR", minval = 0, step = 0.05, group = "Retest")
int    windowBars = input.int(10, "Retest: window, bars", minval = 1, group = "Retest")
int    maxEntries = input.int(1, "Retest: entries per cross", minval = 1, group = "Retest")
int    atrLen     = input.int(14, "ATR length (the strategy uses 14)", minval = 1, group = "Parity")
int    startTime  = input.time(timestamp("2026-01-04T18:00:00-05:00"), "Parity start (a signal-bar open)", group = "Parity")

// ── Checks the setup page also makes ────────────────────────────────
bool isPrice    = srcIn == "PriceVsEma"
bool allowLong  = dirIn != "Down"
bool allowShort = dirIn != "Up"
bool sigIsDaily = timeframe.in_seconds(sigTf) >= 86400

if not isPrice and fastLen >= slowLen
    runtime.error("The fast EMA must be shorter than the slow EMA.")
if entryIn == "Touch" and not isPrice
    runtime.error("Touch needs What crosses = PriceVsEma.")
if timeframe.in_seconds(sigTf) < timeframe.in_seconds()
    runtime.error("The signal timeframe must not be shorter than the chart's bars.")
if timeframe.in_seconds(sigTf) % timeframe.in_seconds() != 0
    runtime.error("The signal timeframe must be a whole multiple of the chart's bar size.")

// ── Indicators from the parity start ────────────────────────────────

// SMA-seeded EMA: the first value is the mean of the first `len` closes, then each close moves
// it by 2 / (len + 1) of the gap (Pine ta.ema, EmaIndicator).
f_ema(float x, int len, bool active) =>
    var float sum = 0.0
    var int   n   = 0
    var float e   = na
    if active
        if n < len
            sum += x
            n   += 1
            if n == len
                e := sum / len
        else
            e += (x - e) * 2.0 / (len + 1)
    e

// Wilder ATR: the first true range is high - low, the first value is the mean of the first `len`
// true ranges, then Wilder smoothing (AtrIndicator).
f_atr(int len, bool active) =>
    var float prevClose = na
    var float sum = 0.0
    var int   n   = 0
    var float a   = na
    if active
        float tr = na(prevClose) ? high - low : math.max(high - low, math.abs(high - prevClose), math.abs(low - prevClose))
        prevClose := close
        n += 1
        if n <= len
            sum += tr
            if n == len
                a := sum / len
        else
            a := (a * (len - 1) + tr) / len
    a

// ── Every rule, on the signal timeframe's own bars (called through request.security) ──
f_logic() =>
    bool  active = time >= startTime
    float ePrice = f_ema(close, emaLen, active)
    float eFast  = f_ema(close, fastLen, active)
    float eSlow  = f_ema(close, slowLen, active)
    float atrV   = f_atr(atrLen, active)
    float lineA  = isPrice ? ePrice : eFast
    float lineB  = isPrice ? na : eSlow
    float rLine  = isPrice ? ePrice : retestIn == "Fast" ? eFast : eSlow
    bool  ready  = active and not na(lineA) and not na(lineA[1]) and (isPrice or not na(lineB)) and not na(atrV)
    float tol    = ready ? math.max(touchTicks * syminfo.mintick, touchAtr * atrV) : na

    // Cross: sign state on closed bars. Zero is no side, so equal values never cross.
    float diff = isPrice ? close - ePrice : eFast - eSlow
    int   side = na(diff) ? 0 : diff > 0 ? 1 : diff < 0 ? -1 : 0
    var int lastSide  = 0
    var int pendSide  = 0
    var int pendCount = 0
    int crossed = 0
    if ready
        if side != 0 and lastSide != 0 and side != lastSide
            pendSide  := side
            pendCount := 0
        if pendSide != 0
            if side == pendSide
                pendCount += 1
                if pendCount >= holdBars
                    float need = (isPrice ? minBeyond : minSep) * atrV
                    if need <= 0 or math.abs(diff) >= need
                        crossed := pendSide
                    pendSide  := 0
                    pendCount := 0
            else
                pendSide  := 0
                pendCount := 0
        if side != 0
            lastSide := side

    // Touch (price vs EMA): one signal per arm; a side re-arms once price clears the EMA by RearmAtr × ATR.
    var bool armLong  = true
    var bool armShort = true
    int touch = 0
    if ready and isPrice
        if not armLong and low > ePrice + rearmAtr * atrV
            armLong := true
        if not armShort and high < ePrice - rearmAtr * atrV
            armShort := true
        bool longTouch  = low <= ePrice + tol and close[1] > ePrice[1] and close > ePrice
        bool shortTouch = high >= ePrice - tol and close[1] < ePrice[1] and close < ePrice
        if longTouch and armLong and allowLong
            touch   := 1
            armLong := false
        else if shortTouch and armShort and allowShort
            touch    := -1
            armShort := false

    // Cross + retest: Idle -> Crossed -> Signal | Expired. The cross bar never signals.
    var int  rtSide    = 0
    var int  rtBar     = 0
    var bool rtAway    = false
    var int  rtEntries = 0
    int  retest = 0
    bool fired  = false
    if ready
        if crossed != 0
            rtSide    := crossed
            rtBar     := bar_index
            rtAway    := false
            rtEntries := 0
        else if rtSide != 0
            if bar_index - rtBar > windowBars
                rtSide := 0
            else
                bool touched = rtSide > 0 ? low <= rLine + tol : high >= rLine - tol
                bool onSide  = rtSide > 0 ? close > rLine : close < rLine
                bool across  = rtSide > 0 ? close < rLine : close > rLine
                if rtAway and touched and (onSide or not closeBack)
                    fired     := true
                    retest    := (rtSide > 0 and allowLong) or (rtSide < 0 and allowShort) ? rtSide : 0
                    rtEntries += 1
                    rtAway    := false
                    if rtEntries >= maxEntries
                        rtSide := 0
                else if across
                    rtSide := 0
        if rtSide != 0 and not fired
            float away = rtSide > 0 ? high - rLine : rLine - low
            if away >= awayAtr * atrV
                rtAway := true

    int crossSig = crossed > 0 and allowLong ? 1 : crossed < 0 and allowShort ? -1 : 0
    int outSig   = entryIn == "Touch" ? touch : entryIn == "Cross" ? crossSig : retest
    int key      = active ? (sigIsDaily ? math.floor(time_tradingday / 60000.0) : math.floor(time / 60000.0)) : na
    [key, lineA, lineB, outSig]

[htfKey, emaA, emaB, htfSig] = request.security(syminfo.tickerid, sigTf, f_logic(), lookahead = barmerge.lookahead_off)

// With lookahead off, history shows a signal bar's values only once that bar is complete, so a new
// key arrives on the chart bar where it closes: one export row per signal bar.
bool htfClosed = barstate.isconfirmed and not na(htfKey) and htfKey != nz(htfKey[1], -1)

// ── On the chart ────────────────────────────────────────────────────
plot(emaA, "EMA A line", color.new(color.orange, 0), 2)
plot(emaB, "EMA B line", color.new(color.purple, 0), 2)
plotshape(htfClosed and htfSig > 0, "Long",  shape.triangleup,   location.belowbar, color.new(color.green, 0), size = size.small)
plotshape(htfClosed and htfSig < 0, "Short", shape.triangledown, location.abovebar, color.new(color.red, 0),   size = size.small)

// ── Columns for the export (Data Window only, so they do not draw) ──
plot(htfClosed ? htfKey : na, "HTF Key", display = display.data_window)
plot(htfClosed ? emaA   : na, "EMA A",   display = display.data_window)
plot(htfClosed ? emaB   : na, "EMA B",   display = display.data_window)
plot(htfClosed ? htfSig : na, "Signal",  display = display.data_window)
plot(isPrice ? 0 : 1, "p_src", display = display.data_window)
plot(entryIn == "Touch" ? 0 : entryIn == "Cross" ? 1 : 2, "p_entry", display = display.data_window)
plot(dirIn == "Up" ? 0 : dirIn == "Down" ? 1 : 2, "p_dir", display = display.data_window)
plot(emaLen, "p_len", display = display.data_window)
plot(fastLen, "p_fast", display = display.data_window)
plot(slowLen, "p_slow", display = display.data_window)
plot(retestIn == "Fast" ? 0 : 1, "p_retest", display = display.data_window)
plot(timeframe.in_seconds(sigTf) / 60, "p_tf", display = display.data_window)
plot(touchTicks, "p_touch_ticks", display = display.data_window)
plot(touchAtr, "p_touch_atr", display = display.data_window)
plot(rearmAtr, "p_rearm_atr", display = display.data_window)
plot(closeBack ? 1 : 0, "p_close_back", display = display.data_window)
plot(minBeyond, "p_min_beyond_atr", display = display.data_window)
plot(minSep, "p_min_sep_atr", display = display.data_window)
plot(holdBars, "p_hold", display = display.data_window)
plot(awayAtr, "p_away_atr", display = display.data_window)
plot(windowBars, "p_window", display = display.data_window)
plot(maxEntries, "p_max_entries", display = display.data_window)
plot(atrLen, "p_atr_len", display = display.data_window)
plot(math.floor(startTime / 60000.0), "p_start", display = display.data_window)
plot(syminfo.mintick, "p_tick", display = display.data_window)

// ── The last ten signals, for a quick look ──────────────────────────
var array<int>   sigKeys  = array.new<int>()
var array<int>   sigSides = array.new<int>()
var array<float> sigEmas  = array.new<float>()
var int          sigCount = 0
if htfClosed and htfSig != 0
    sigCount += 1
    array.push(sigKeys, htfKey)
    array.push(sigSides, htfSig)
    array.push(sigEmas, emaA)
    if array.size(sigKeys) > 10
        array.shift(sigKeys)
        array.shift(sigSides)
        array.shift(sigEmas)

var table board = table.new(position.top_right, 3, 12, bgcolor = color.new(color.black, 15), border_width = 1)
if barstate.islast
    table.cell(board, 0, 0, "Signals: " + str.tostring(sigCount), text_color = color.white)
    table.cell(board, 0, 1, sigIsDaily ? "Trading day (UTC)" : "Signal bar open (UTC)", text_color = color.white)
    table.cell(board, 1, 1, "Side", text_color = color.white)
    table.cell(board, 2, 1, "EMA A", text_color = color.white)
    int rows = array.size(sigKeys)
    if rows > 0
        for i = 0 to rows - 1
            int j = rows - 1 - i
            table.cell(board, 0, i + 2, str.format_time(array.get(sigKeys, j) * 60000, sigIsDaily ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", "UTC"), text_color = color.white)
            table.cell(board, 1, i + 2, array.get(sigSides, j) > 0 ? "Long" : "Short", text_color = color.white)
            table.cell(board, 2, i + 2, str.tostring(array.get(sigEmas, j), format.mintick), text_color = color.white)
```

- [ ] **Step 2: Check the script against the rules it mirrors**

Read the script beside the ema-strategy spec's "Signals" section and Decision 6 of this plan. Each bullet of Decision 6 must be visible in the code; the Touch and Cross equality rules must use strict comparisons (`>`, `<`) for the close against the EMA.

- [ ] **Step 3: Commit**

```bash
git add docs/pine/ema-strategy-parity.pine
git commit -m "feat(ema): Pine v6 parity reference for every source, entry and direction"
```

---

### Task 4: Comparing two signal traces

**Files:**
- Create: `CRV.Backtest/Experiments/EmaParity.cs`
- Create: `CRV.Core.Tests/Backtest/EmaParityFixture.cs`
- Test: `CRV.Core.Tests/Backtest/EmaParityCompareTests.cs`

**Interfaces:**
- Consumes: `SignalTimeframe`, `HtfBar`, `EasternTime.ToEastern`.
- Produces (namespace `CRV.Backtest.Experiments`):
  - `sealed record EmaTraceRow(DateTime Key, decimal? EmaA, decimal? EmaB, int Signal)`
  - `sealed record EmaParityMismatch(DateTime Key, string Bar, string Problem)`
  - `sealed class EmaParityReport` — `SignalTimeframe Timeframe`, `int Compared`, `int WarmupRows`, `int PineSignals`, `int MatchedSignals`, `decimal MaxEmaDiffTicks`, `IReadOnlyList<EmaParityMismatch> Mismatches`, `bool Passed`, `string Describe()`
  - `static class EmaParity` — `DateTime KeyOf(SignalTimeframe tf, HtfBar bar)`, `string Label(SignalTimeframe tf, DateTime key)`, `EmaParityReport Compare(IReadOnlyList<EmaTraceRow> pine, IReadOnlyList<EmaTraceRow> csharp, SignalTimeframe timeframe, decimal tickSize, int warmupRows)`
  - Test fixture `EmaParityFixture` (internal, tests): `Start`, `Count`, `TimeOf(int)`, `Bars()`, `BarAt(int)`, `EmaA(int)`, `Signal(int)`, `ParamNames`, `ExportCsv(...)`.

- [ ] **Step 1: Write the fixture**

`CRV.Core.Tests/Backtest/EmaParityFixture.cs`:

```csharp
using System.Globalization;
using System.Text;
using CRV.Core.Models;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// Thirty-six five-minute bars from 09:30 ET on 2026-04-15 and the Pine parity columns that go
/// with them. Bars 1–32 sit flat at 100, so EMA(8) seeds at 100 on bar 8 and stays there; closes
/// of 98, 103, 95 and 96 then give a cross up on bar 34 and a cross down on bar 35.
/// EMA(8), alpha 2/9, worked by hand:
///   bar 33: 100       + (98  − 100)       · 2/9 =  99.555556
///   bar 34: 99.555556 + (103 − 99.555556) · 2/9 = 100.320988
///   bar 35: 100.320988 + (95 − 100.320988) · 2/9 =  99.138546
///   bar 36: 99.138546 + (96  − 99.138546) · 2/9 =  98.441091
/// </summary>
internal static class EmaParityFixture
{
    public static readonly DateTime Start = new(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc);
    public const int Count = 36;

    public static DateTime TimeOf(int bar) => Start.AddMinutes((bar - 1) * 5);

    public static IReadOnlyList<Bar> Bars() => Enumerable.Range(1, Count).Select(BarAt).ToList();

    public static Bar BarAt(int n) => n switch
    {
        <= 32 => new Bar(TimeOf(n), 100m, 101m,   99m,    100m, 1000),
        33    => new Bar(TimeOf(n), 100m, 100.5m, 97.5m,  98m,  1000),
        34    => new Bar(TimeOf(n), 98m,  103.5m, 97.75m, 103m, 1000),
        35    => new Bar(TimeOf(n), 103m, 104m,   94.5m,  95m,  1000),
        _     => new Bar(TimeOf(n), 95m,  96.5m,  94m,    96m,  1000),
    };

    public static decimal? EmaA(int n) => n switch
    {
        < 8   => null,
        <= 32 => 100m,
        33    => 99.555556m,
        34    => 100.320988m,
        35    => 99.138546m,
        _     => 98.441091m,
    };

    /// <summary>What Pine writes for a price-vs-EMA cross in both directions.</summary>
    public static int Signal(int n) => n == 34 ? 1 : n == 35 ? -1 : 0;

    public static readonly string[] ParamNames =
    {
        "p_src", "p_entry", "p_dir", "p_len", "p_fast", "p_slow", "p_retest", "p_tf",
        "p_touch_ticks", "p_touch_atr", "p_rearm_atr", "p_close_back", "p_min_beyond_atr", "p_min_sep_atr",
        "p_hold", "p_away_atr", "p_window", "p_max_entries", "p_atr_len", "p_start", "p_tick",
    };

    /// <summary>
    /// The CSV TradingView writes for these bars with the parity script on a 5-minute chart and
    /// signal timeframe 5: price vs EMA 8, Cross, <paramref name="direction"/> (0 Up, 1 Down, 2 Both).
    /// </summary>
    public static string ExportCsv(Func<int, decimal?>? emaA = null, Func<int, int>? signal = null,
        int direction = 2, int atrLength = 14, string titlePrefix = "", bool withPine = true)
    {
        emaA   ??= EmaA;
        signal ??= Signal;
        long startMinutes = new DateTimeOffset(Start).ToUnixTimeSeconds() / 60;

        var csv = new StringBuilder("time,open,high,low,close");
        if (withPine)
            csv.Append(',').Append(string.Join(',',
                new[] { "HTF Key", "EMA A", "EMA B", "Signal" }.Concat(ParamNames).Select(t => titlePrefix + t)));
        csv.Append('\n');

        for (int n = 1; n <= Count; n++)
        {
            var b = BarAt(n);
            long seconds = new DateTimeOffset(b.Time).ToUnixTimeSeconds();
            csv.Append(seconds).Append(',').Append(Inv(b.Open)).Append(',').Append(Inv(b.High))
               .Append(',').Append(Inv(b.Low)).Append(',').Append(Inv(b.Close));
            if (withPine)
            {
                csv.Append(',').Append(seconds / 60)
                   .Append(',').Append(emaA(n) is { } e ? Inv(e) : "NaN")
                   .Append(",NaN,").Append(signal(n))
                   .Append($",0,1,{direction},8,8,21,0,5,1,0.1,0.5,1,0,0,1,0.25,10,1,{atrLength},{startMinutes},0.25");
            }
            csv.Append('\n');
        }
        return csv.ToString();
    }

    private static string Inv(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
```

- [ ] **Step 2: Write the failing tests**

`CRV.Core.Tests/Backtest/EmaParityCompareTests.cs`:

```csharp
using CRV.Backtest.Experiments;
using CRV.Core.Indicators;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// Spec test 1: matching tables pass; a shifted timestamp, a missing signal and an EMA off by
/// two ticks each fail with a message naming the bar.
/// </summary>
public class EmaParityCompareTests
{
    private const decimal Tick = 0.25m;

    private static List<EmaTraceRow> Rows(Func<int, int>? signal = null, Func<int, decimal?>? ema = null) =>
        Enumerable.Range(1, EmaParityFixture.Count)
            .Select(n => new EmaTraceRow(EmaParityFixture.TimeOf(n),
                (ema ?? EmaParityFixture.EmaA)(n), null, (signal ?? EmaParityFixture.Signal)(n)))
            .ToList();

    private static EmaParityReport Compare(List<EmaTraceRow> pine, List<EmaTraceRow> csharp, int warmup = 0) =>
        EmaParity.Compare(pine, csharp, SignalTimeframe.M5, Tick, warmup);

    [Fact]
    public void Compare_MatchingTables_Pass()
    {
        var report = Compare(Rows(), Rows());

        Assert.True(report.Passed, report.Describe());
        Assert.Equal(EmaParityFixture.Count, report.Compared);
        Assert.Equal(2, report.PineSignals);
        Assert.Equal(2, report.MatchedSignals);
    }

    [Fact]
    public void Compare_AShiftedSignal_FailsNamingBothBars()
    {
        var report = Compare(Rows(n => n == 34 ? 1 : 0), Rows(n => n == 35 ? 1 : 0));

        Assert.False(report.Passed);
        Assert.Equal(2, report.Mismatches.Count);
        Assert.Contains(report.Mismatches, m => m.Bar == "2026-04-15 12:15 ET" && m.Problem == "signal differs: Pine long, C# none");
        Assert.Contains(report.Mismatches, m => m.Bar == "2026-04-15 12:20 ET" && m.Problem == "signal differs: Pine none, C# long");
    }

    [Fact]
    public void Compare_AMissingSignal_FailsNamingTheBar()
    {
        var report = Compare(Rows(), Rows(n => n == 35 ? -1 : 0));

        var miss = Assert.Single(report.Mismatches);
        Assert.Equal("2026-04-15 12:15 ET", miss.Bar);
        Assert.Contains("Pine long, C# none", miss.Problem);
    }

    [Fact]
    public void Compare_AnEmaTwoTicksOff_FailsNamingTheBar()
    {
        var report = Compare(Rows(), Rows(ema: n => n == 30 ? 100.5m : EmaParityFixture.EmaA(n)));

        var miss = Assert.Single(report.Mismatches);
        Assert.Equal("2026-04-15 11:55 ET", miss.Bar);
        Assert.StartsWith("EMA A off by 2 ticks", miss.Problem);
    }

    [Fact]
    public void Compare_AnEmaOneTickOff_Passes()
    {
        var report = Compare(Rows(), Rows(ema: n => n == 30 ? 100.25m : EmaParityFixture.EmaA(n)));

        Assert.True(report.Passed, report.Describe());
        Assert.Equal(1m, report.MaxEmaDiffTicks);
    }

    [Fact]
    public void Compare_ADifferenceInsideTheWarmup_IsIgnored()
    {
        var report = Compare(Rows(), Rows(ema: n => n == 9 ? 101m : EmaParityFixture.EmaA(n)), warmup: 24);

        Assert.True(report.Passed, report.Describe());
        Assert.Equal(EmaParityFixture.Count - 24, report.Compared);
    }

    [Fact]
    public void Compare_WithNothingAfterWarmup_DoesNotPass()
    {
        var report = Compare(Rows(), Rows(), warmup: 63);

        Assert.Equal(0, report.Compared);
        Assert.False(report.Passed);
        Assert.Contains("nothing compared", report.Describe());
    }

    [Fact]
    public void Compare_ABarMissingFromTheCSharpRun_FailsNamingIt()
    {
        var report = Compare(Rows(), Rows().Where(r => r.Key != EmaParityFixture.TimeOf(20)).ToList());

        var miss = Assert.Single(report.Mismatches);
        Assert.Equal("2026-04-15 11:05 ET", miss.Bar);
        Assert.StartsWith("bar missing from the C# run", miss.Problem);
    }

    [Fact]
    public void KeyOf_ADailyBar_IsItsTradingDateAtMidnightUtc()
    {
        // Sunday 2026-04-12 18:00 ET opens Monday's trading day.
        var bar = new HtfBar(SignalTimeframe.D1, new DateTime(2026, 4, 12, 22, 0, 0, DateTimeKind.Utc), 1m, 2m, 0.5m, 1.5m, 10);

        var key = EmaParity.KeyOf(SignalTimeframe.D1, bar);

        Assert.Equal(new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc), key);
        Assert.Equal("2026-04-13 (trading date)", EmaParity.Label(SignalTimeframe.D1, key));
    }

    [Fact]
    public void KeyOf_AnIntradayBar_IsItsOpen()
    {
        var open = new DateTime(2026, 4, 13, 14, 0, 0, DateTimeKind.Utc);
        var bar  = new HtfBar(SignalTimeframe.H4, open, 1m, 2m, 0.5m, 1.5m, 10);

        Assert.Equal(open, EmaParity.KeyOf(SignalTimeframe.H4, bar));
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaParityCompareTests"`
Expected: FAIL to compile — `The type or namespace name 'EmaTraceRow' could not be found`.

- [ ] **Step 4: Implement**

`CRV.Backtest/Experiments/EmaParity.cs`:

```csharp
using CRV.Core.Indicators;

namespace CRV.Backtest.Experiments;

/// <summary>One closed signal bar: its key, its EMA value(s), and the signal on it (1 long, −1 short, 0 none).</summary>
public sealed record EmaTraceRow(DateTime Key, decimal? EmaA, decimal? EmaB, int Signal);

/// <summary>One signal bar where the two sides disagree, named the way a person reads the chart.</summary>
public sealed record EmaParityMismatch(DateTime Key, string Bar, string Problem)
{
    public override string ToString() => $"{Bar}: {Problem}";
}

/// <summary>The C# strategy against the Pine reference on the same bars.</summary>
public sealed class EmaParityReport
{
    public SignalTimeframe Timeframe { get; init; }

    /// <summary>Pine signal bars compared, after the warmup.</summary>
    public int Compared { get; init; }
    public int WarmupRows { get; init; }
    public int PineSignals { get; init; }
    public int MatchedSignals { get; init; }
    public decimal MaxEmaDiffTicks { get; init; }
    public IReadOnlyList<EmaParityMismatch> Mismatches { get; init; } = [];

    /// <summary>Every signal and every EMA within a tick — and something was compared. A run that compared nothing proves nothing.</summary>
    public bool Passed => Compared > 0 && Mismatches.Count == 0;

    public string Describe()
    {
        if (Compared == 0)
            return $"nothing compared: no signal bars after the {WarmupRows}-bar warmup";

        string head = Passed ? "matches" : $"{Mismatches.Count} difference(s)";
        string body = $"{head} over {Compared} {Timeframe} bars after a {WarmupRows}-bar warmup; " +
                      $"Pine signals {PineSignals}, matched {MatchedSignals}; largest EMA gap {MaxEmaDiffTicks:0.##} ticks";
        return Passed ? body : body + "\n" + string.Join('\n', Mismatches.Take(20));
    }
}

/// <summary>
/// Logic parity: the EMA strategy and the Pine reference read the same bars, so any difference
/// in a signal or an EMA is a difference in logic. Pass: after the warmup every signal matches
/// and every EMA is within one tick.
/// </summary>
public static class EmaParity
{
    /// <summary>
    /// How a signal bar is named on both sides: its open time, or for D1 its trading date at
    /// 00:00 UTC, which is how TradingView names a daily bar (time_tradingday).
    /// </summary>
    public static DateTime KeyOf(SignalTimeframe tf, HtfBar bar) => tf == SignalTimeframe.D1
        ? DateTime.SpecifyKind(EasternTime.ToEastern(bar.OpenUtc).AddHours(6).Date, DateTimeKind.Utc)
        : bar.OpenUtc;

    public static string Label(SignalTimeframe tf, DateTime key) => tf == SignalTimeframe.D1
        ? $"{key:yyyy-MM-dd} (trading date)"
        : $"{EasternTime.ToEastern(key):yyyy-MM-dd HH:mm} ET";

    /// <summary>
    /// Compares the rows after the first <paramref name="warmupRows"/> Pine rows. A bar on one
    /// side only, a signal that differs, or an EMA more than one tick apart is a mismatch.
    /// </summary>
    public static EmaParityReport Compare(IReadOnlyList<EmaTraceRow> pine, IReadOnlyList<EmaTraceRow> csharp,
        SignalTimeframe timeframe, decimal tickSize, int warmupRows)
    {
        if (tickSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "Tick size must be positive.");

        var expected = pine.OrderBy(r => r.Key).ToList();
        if (expected.Count <= warmupRows)
            return new EmaParityReport { Timeframe = timeframe, WarmupRows = warmupRows };

        var from = expected[warmupRows].Key;
        var p = ByKey(expected.Skip(warmupRows), "Pine export");
        var c = ByKey(csharp.Where(r => r.Key >= from), "C# run");

        var mismatches = new List<EmaParityMismatch>();
        int pineSignals = 0, matched = 0;
        decimal maxTicks = 0m;

        foreach (var key in p.Keys.Union(c.Keys).OrderBy(k => k))
        {
            string bar = Label(timeframe, key);
            bool inPine = p.TryGetValue(key, out var pr);
            bool inCs   = c.TryGetValue(key, out var cr);
            if (inPine && pr!.Signal != 0) pineSignals++;

            if (!inCs)
            {
                mismatches.Add(new(key, bar, $"bar missing from the C# run (Pine: {Side(pr!.Signal)})"));
                continue;
            }
            if (!inPine)
            {
                mismatches.Add(new(key, bar, $"bar missing from the Pine export (C#: {Side(cr!.Signal)})"));
                continue;
            }

            if (pr!.Signal != cr!.Signal)
                mismatches.Add(new(key, bar, $"signal differs: Pine {Side(pr.Signal)}, C# {Side(cr.Signal)}"));
            else if (pr.Signal != 0)
                matched++;

            CheckEma("EMA A", pr.EmaA, cr.EmaA);
            CheckEma("EMA B", pr.EmaB, cr.EmaB);

            void CheckEma(string name, decimal? pv, decimal? cv)
            {
                if (pv is null && cv is null) return;
                if (pv is null || cv is null)
                {
                    mismatches.Add(new(key, bar, $"{name} only on the {(pv is null ? "C#" : "Pine")} side"));
                    return;
                }
                decimal ticks = Math.Abs(pv.Value - cv.Value) / tickSize;
                maxTicks = Math.Max(maxTicks, ticks);
                if (ticks > 1m)
                    mismatches.Add(new(key, bar, $"{name} off by {ticks:0.##} ticks (Pine {pv:0.00####}, C# {cv:0.00####})"));
            }
        }

        return new EmaParityReport
        {
            Timeframe = timeframe, WarmupRows = warmupRows, Compared = p.Count,
            PineSignals = pineSignals, MatchedSignals = matched, MaxEmaDiffTicks = maxTicks,
            Mismatches = mismatches,
        };
    }

    private static Dictionary<DateTime, EmaTraceRow> ByKey(IEnumerable<EmaTraceRow> rows, string side)
    {
        var map = new Dictionary<DateTime, EmaTraceRow>();
        foreach (var r in rows)
            if (!map.TryAdd(r.Key, r))
                throw new InvalidOperationException(
                    $"The {side} has two rows for {r.Key:yyyy-MM-dd HH:mm} UTC; each signal bar must appear once.");
        return map;
    }

    private static string Side(int signal) => signal > 0 ? "long" : signal < 0 ? "short" : "none";
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaParityCompareTests"`
Expected: PASS (10 tests).

- [ ] **Step 6: Commit**

```bash
git add CRV.Backtest/Experiments/EmaParity.cs CRV.Core.Tests/Backtest/EmaParityFixture.cs CRV.Core.Tests/Backtest/EmaParityCompareTests.cs
git commit -m "feat(ema): compare EMA signal traces bar by bar within a tick"
```

---

### Task 5: Reading a TradingView export

**Files:**
- Create: `CRV.Backtest/DataLoaders/TradingViewExport.cs`
- Test: `CRV.Core.Tests/Backtest/TradingViewExportTests.cs`

**Interfaces:**
- Consumes: `EmaTraceRow` (Task 4); `EmaSource`, `EmaEntry`, `EmaDirection`, `RetestEmaChoice`, `SignalTimeframe`; Task 3's column contract.
- Produces (namespace `CRV.Backtest.DataLoaders`):
  - `sealed record PineParityParameters(EmaSource Source, EmaEntry Entry, EmaDirection Direction, int EmaPeriod, int FastEma, int SlowEma, RetestEmaChoice RetestEma, SignalTimeframe Timeframe, int TouchTicks, decimal TouchAtr, decimal RearmAtr, bool CloseBackOnSide, decimal MinCloseBeyondAtr, decimal MinSeparationAtr, int HoldBars, decimal RetestMinAwayAtr, int RetestWindowBars, int MaxEntriesPerCross, int AtrLength, DateTime StartUtc, decimal TickSize)` with `int SlowestPeriod`
  - `sealed class TradingViewExport` — `IReadOnlyList<Bar> Bars`, `IReadOnlyList<EmaTraceRow> PineRows`, `PineParityParameters? Parameters`, `int BarMinutes`, `static TradingViewExport Read(TextReader reader)`, `static SignalTimeframe TimeframeOf(int minutes)`

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Backtest/TradingViewExportTests.cs`:

```csharp
using CRV.Backtest.DataLoaders;
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Backtest;

public class TradingViewExportTests
{
    private static TradingViewExport Read(string csv) => TradingViewExport.Read(new StringReader(csv));

    [Fact]
    public void Read_AParityExport_TakesBarsPineRowsAndInputs()
    {
        var export = Read(EmaParityFixture.ExportCsv());

        Assert.Equal(EmaParityFixture.Count, export.Bars.Count);
        var bar = export.Bars[33];
        Assert.Equal(EmaParityFixture.TimeOf(34), bar.Time);
        Assert.Equal((98m, 103.5m, 97.75m, 103m), (bar.Open, bar.High, bar.Low, bar.Close));
        Assert.Equal(5, export.BarMinutes);

        Assert.Equal(EmaParityFixture.Count, export.PineRows.Count);
        var row = export.PineRows.Single(r => r.Key == EmaParityFixture.TimeOf(34));
        Assert.Equal(100.320988m, row.EmaA);
        Assert.Null(row.EmaB);
        Assert.Equal(1, row.Signal);
        Assert.Null(export.PineRows[0].EmaA);

        var p = Assert.IsType<PineParityParameters>(export.Parameters);
        Assert.Equal(EmaSource.PriceVsEma, p.Source);
        Assert.Equal(EmaEntry.Cross, p.Entry);
        Assert.Equal(EmaDirection.Both, p.Direction);
        Assert.Equal(8, p.EmaPeriod);
        Assert.Equal(SignalTimeframe.M5, p.Timeframe);
        Assert.True(p.CloseBackOnSide);
        Assert.Equal(14, p.AtrLength);
        Assert.Equal(EmaParityFixture.Start, p.StartUtc);
        Assert.Equal(0.25m, p.TickSize);
        Assert.Equal(8, p.SlowestPeriod);
    }

    [Fact]
    public void Read_ColumnsTitledWithTheIndicatorName_AreFound()
    {
        var export = Read(EmaParityFixture.ExportCsv(titlePrefix: "CRV EMA parity: "));

        Assert.Equal(EmaParityFixture.Count, export.PineRows.Count);
        Assert.Equal(EmaEntry.Cross, export.Parameters!.Entry);
    }

    [Fact]
    public void Read_ABarsOnlyExportWithIsoTimes_HasNoPineRowsOrInputs()
    {
        var export = Read("time,open,high,low,close,Volume\n" +
                          "2026-04-15T09:30:00-04:00,100,101,99,100.5,12\n" +
                          "2026-04-15T09:35:00-04:00,100.5,102,100,101,8\n");

        Assert.Equal(EmaParityFixture.Start, export.Bars[0].Time);
        Assert.Equal(12, export.Bars[0].Volume);
        Assert.Empty(export.PineRows);
        Assert.Null(export.Parameters);
        Assert.Equal(5, export.BarMinutes);
    }

    [Fact]
    public void Read_WithoutACloseColumn_SaysWhichIsMissing()
    {
        var ex = Assert.Throws<FormatException>(() => Read("time,open,high,low\n1776259800,1,2,0.5\n"));

        Assert.Contains("close", ex.Message);
    }

    [Fact]
    public void Read_ACellThatIsNotANumber_NamesTheLine()
    {
        var ex = Assert.Throws<FormatException>(() => Read(
            "time,open,high,low,close\n1776259800,1,2,0.5,1.5\n1776260100,1,abc,0.5,1.5\n"));

        Assert.Contains("Line 3", ex.Message);
    }

    [Theory]
    [InlineData(10080)]
    [InlineData(43200)]
    public void TimeframeOf_WeeklyOrMonthly_IsOutsideLogicParity(int minutes)
    {
        var ex = Assert.Throws<FormatException>(() => TradingViewExport.TimeframeOf(minutes));

        Assert.Contains("outside logic parity", ex.Message);
    }

    [Theory]
    [InlineData(240, SignalTimeframe.H4)]
    [InlineData(480, SignalTimeframe.H8)]
    [InlineData(1440, SignalTimeframe.D1)]
    public void TimeframeOf_PineMinutes_MapToSignalTimeframes(int minutes, SignalTimeframe expected)
        => Assert.Equal(expected, TradingViewExport.TimeframeOf(minutes));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~TradingViewExportTests"`
Expected: FAIL to compile — `The type or namespace name 'TradingViewExport' could not be found`.

- [ ] **Step 3: Implement**

`CRV.Backtest/DataLoaders/TradingViewExport.cs`:

```csharp
using System.Globalization;
using CRV.Backtest.Experiments;
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;

namespace CRV.Backtest.DataLoaders;

/// <summary>The Pine parity script's inputs, read back from its p_* columns.</summary>
public sealed record PineParityParameters(
    EmaSource Source, EmaEntry Entry, EmaDirection Direction,
    int EmaPeriod, int FastEma, int SlowEma, RetestEmaChoice RetestEma,
    SignalTimeframe Timeframe,
    int TouchTicks, decimal TouchAtr, decimal RearmAtr, bool CloseBackOnSide,
    decimal MinCloseBeyondAtr, decimal MinSeparationAtr, int HoldBars,
    decimal RetestMinAwayAtr, int RetestWindowBars, int MaxEntriesPerCross,
    int AtrLength, DateTime StartUtc, decimal TickSize)
{
    /// <summary>The longest EMA the setup reads; it sets the warmup.</summary>
    public int SlowestPeriod => Source == EmaSource.PriceVsEma ? EmaPeriod : Math.Max(FastEma, SlowEma);
}

/// <summary>
/// A TradingView "Export chart data" CSV: the chart's bars and, when the CRV EMA parity script
/// was on the chart, its signal-bar rows and its inputs. Columns are found by title, also when
/// TradingView prefixes them with the indicator name.
/// </summary>
public sealed class TradingViewExport
{
    public IReadOnlyList<Bar> Bars { get; }
    public IReadOnlyList<EmaTraceRow> PineRows { get; }

    /// <summary>The script's inputs; null for an export taken without the script.</summary>
    public PineParityParameters? Parameters { get; }

    /// <summary>The chart's bar size, from the shortest gap between bars; 0 with fewer than two bars.</summary>
    public int BarMinutes { get; }

    private TradingViewExport(IReadOnlyList<Bar> bars, IReadOnlyList<EmaTraceRow> pineRows,
        PineParityParameters? parameters, int barMinutes)
    {
        Bars       = bars;
        PineRows   = pineRows;
        Parameters = parameters;
        BarMinutes = barMinutes;
    }

    public static TradingViewExport Read(TextReader reader)
    {
        var header = reader.ReadLine() ?? throw new FormatException("The export is empty.");
        var columns = header.Split(',').Select(c => c.Trim().Trim('"')).ToArray();

        int Find(string title) => Array.FindIndex(columns, c =>
            c.Equals(title, StringComparison.OrdinalIgnoreCase) ||
            c.EndsWith(": " + title, StringComparison.OrdinalIgnoreCase));

        var missing = new[] { "time", "open", "high", "low", "close" }.Where(t => Find(t) < 0).ToList();
        if (missing.Count > 0)
            throw new FormatException(
                $"The export has no {string.Join(", ", missing)} column. Export the chart from TradingView with \"Export chart data\".");

        int time = Find("time"), open = Find("open"), high = Find("high"), low = Find("low"), close = Find("close");
        int volume = Find("Volume");
        int key = Find("HTF Key"), emaA = Find("EMA A"), emaB = Find("EMA B"), signal = Find("Signal");
        bool hasParams = Find("p_src") >= 0;

        var bars = new List<Bar>();
        var rows = new List<EmaTraceRow>();
        string[]? paramRow = null;
        int lineNo = 1;

        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Length == 0) continue;
            var cells = line.Split(',');
            if (cells.Length < columns.Length)
                throw new FormatException($"Line {lineNo} has {cells.Length} cells; the header has {columns.Length}.");

            decimal Price(int i) => Number(cells[i], lineNo)
                ?? throw new FormatException($"Line {lineNo}: {columns[i]} is empty.");

            bars.Add(new Bar(ParseTime(cells[time], lineNo), Price(open), Price(high), Price(low), Price(close),
                volume >= 0 ? (long)(Number(cells[volume], lineNo) ?? 0m) : 0L));

            if (key >= 0 && Number(cells[key], lineNo) is { } k)
                rows.Add(new EmaTraceRow(
                    DateTime.UnixEpoch.AddMinutes((double)k),
                    emaA >= 0 ? Number(cells[emaA], lineNo) : null,
                    emaB >= 0 ? Number(cells[emaB], lineNo) : null,
                    signal >= 0 ? (int)Math.Round(Number(cells[signal], lineNo) ?? 0m) : 0));

            if (hasParams && paramRow is null) paramRow = cells;
        }

        var parameters = paramRow is null ? null : ReadParameters(paramRow, Find);
        return new TradingViewExport(bars, rows, parameters, BarMinutesOf(bars));
    }

    /// <summary>The signal timeframe for a Pine timeframe in minutes. W1 and MN1 are outside logic parity.</summary>
    public static SignalTimeframe TimeframeOf(int minutes) => minutes switch
    {
        5    => SignalTimeframe.M5,
        15   => SignalTimeframe.M15,
        30   => SignalTimeframe.M30,
        60   => SignalTimeframe.H1,
        240  => SignalTimeframe.H4,
        480  => SignalTimeframe.H8,
        1440 => SignalTimeframe.D1,
        _    => throw new FormatException(
            $"A {minutes}-minute signal timeframe is outside logic parity, which covers 5, 15, 30, 60, 240 and 480 minutes and D."),
    };

    private static PineParityParameters ReadParameters(string[] cells, Func<string, int> find)
    {
        decimal P(string name)
        {
            int i = find(name);
            if (i < 0)
                throw new FormatException(
                    $"The export has p_src but no {name} column; it came from a different version of the parity script.");
            return Number(cells[i], 2) ?? throw new FormatException($"{name} is empty on the first row.");
        }
        int I(string name) => (int)P(name);

        return new PineParityParameters(
            Source:             I("p_src") == 0 ? EmaSource.PriceVsEma : EmaSource.EmaVsEma,
            Entry:              I("p_entry") switch { 0 => EmaEntry.Touch, 1 => EmaEntry.Cross, _ => EmaEntry.CrossRetest },
            Direction:          I("p_dir") switch { 0 => EmaDirection.Up, 1 => EmaDirection.Down, _ => EmaDirection.Both },
            EmaPeriod:          I("p_len"),
            FastEma:            I("p_fast"),
            SlowEma:            I("p_slow"),
            RetestEma:          I("p_retest") == 0 ? RetestEmaChoice.Fast : RetestEmaChoice.Slow,
            Timeframe:          TimeframeOf(I("p_tf")),
            TouchTicks:         I("p_touch_ticks"),
            TouchAtr:           P("p_touch_atr"),
            RearmAtr:           P("p_rearm_atr"),
            CloseBackOnSide:    I("p_close_back") != 0,
            MinCloseBeyondAtr:  P("p_min_beyond_atr"),
            MinSeparationAtr:   P("p_min_sep_atr"),
            HoldBars:           I("p_hold"),
            RetestMinAwayAtr:   P("p_away_atr"),
            RetestWindowBars:   I("p_window"),
            MaxEntriesPerCross: I("p_max_entries"),
            AtrLength:          I("p_atr_len"),
            StartUtc:           DateTime.UnixEpoch.AddMinutes((double)P("p_start")),
            TickSize:           P("p_tick"));
    }

    private static DateTime ParseTime(string cell, int lineNo)
    {
        var s = cell.Trim().Trim('"');
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso))
            return iso.UtcDateTime;
        throw new FormatException($"Line {lineNo}: '{s}' is not a UNIX or ISO 8601 time.");
    }

    // TradingView writes NaN for a plot with no value on that bar.
    private static decimal? Number(string cell, int lineNo)
    {
        var s = cell.Trim().Trim('"');
        if (s.Length == 0 || s.Equals("NaN", StringComparison.OrdinalIgnoreCase) || s.Equals("na", StringComparison.OrdinalIgnoreCase))
            return null;
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        throw new FormatException($"Line {lineNo}: '{s}' is not a number.");
    }

    private static int BarMinutesOf(IReadOnlyList<Bar> bars)
    {
        int best = 0;
        for (int i = 1; i < bars.Count; i++)
        {
            int gap = (int)(bars[i].Time - bars[i - 1].Time).TotalMinutes;
            if (gap > 0 && (best == 0 || gap < best)) best = gap;
        }
        return best;
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~TradingViewExportTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Backtest/DataLoaders/TradingViewExport.cs CRV.Core.Tests/Backtest/TradingViewExportTests.cs
git commit -m "feat(ema): read TradingView chart exports with the Pine parity columns"
```

---

### Task 6: The C# trace — the strategy over the exported bars

**Files:**
- Create: `CRV.Backtest/Experiments/EmaSignalTrace.cs`
- Modify: `CRV.Backtest/Experiments/EmaParity.cs` (add `ParitySetup`)
- Test: `CRV.Core.Tests/Backtest/EmaSignalTraceTests.cs`

**Interfaces:**
- Consumes: `StrategyFactory.Create`, `ISetupStrategy` (`OnTick`, `OnBar`, `PendingEntry`, `ClearPendingSignals`, `SetInTrade`, `ResetTradeCounters`, `ResetCutoff`), `SessionBarAggregator`, `EmaIndicator`, `AtrIndicator`, `EmaParity.KeyOf` (Task 4), `PineParityParameters` (Task 5), `EmaConfirmationSwitches.ClearAll` (Task 2).
- Produces:
  - `static IReadOnlyList<EmaTraceRow> EmaSignalTrace.Run(StrategySetupConfig setup, IEnumerable<Bar> executionBars)`
  - `static StrategySetupConfig EmaParity.ParitySetup(PineParityParameters p, int barMinutes)`

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Backtest/EmaSignalTraceTests.cs`:

```csharp
using CRV.Backtest.DataLoaders;
using CRV.Backtest.Experiments;
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The real EMA strategy over the hand-worked fixture: price vs EMA 8, Cross, five-minute
/// signal bars on five-minute bars. Bar 34 closes above the EMA and bar 35 below it.
/// </summary>
public class EmaSignalTraceTests
{
    private static PineParityParameters Params(EmaDirection direction = EmaDirection.Both) => new(
        EmaSource.PriceVsEma, EmaEntry.Cross, direction, 8, 8, 21, RetestEmaChoice.Fast, SignalTimeframe.M5,
        1, 0.1m, 0.5m, true, 0m, 0m, 1, 0.25m, 10, 1, 14, EmaParityFixture.Start, 0.25m);

    private static IReadOnlyList<EmaTraceRow> Trace(EmaDirection direction = EmaDirection.Both) =>
        EmaSignalTrace.Run(EmaParity.ParitySetup(Params(direction), barMinutes: 5), EmaParityFixture.Bars());

    private static EmaTraceRow At(IReadOnlyList<EmaTraceRow> rows, int bar) =>
        rows.Single(r => r.Key == EmaParityFixture.TimeOf(bar));

    [Fact]
    public void Run_EveryClosedSignalBar_HasARowWithItsEma()
    {
        var rows = Trace();

        Assert.Equal(EmaParityFixture.Count, rows.Count);
        Assert.Null(At(rows, 7).EmaA);
        Assert.Equal(100m, At(rows, 8).EmaA);
        Assert.InRange(At(rows, 33).EmaA!.Value, 99.555555m, 99.555556m);
        Assert.InRange(At(rows, 34).EmaA!.Value, 100.320987m, 100.320988m);
        Assert.All(rows, r => Assert.Null(r.EmaB));
    }

    [Fact]
    public void Run_BothDirections_SignalOnTheBarsWhoseCloseCrossed()
    {
        // Entries happen on the next bar's open; the signal belongs to the bar that crossed.
        // A strategy that entered on the crossing bar's own open would put these one bar early.
        var rows = Trace();

        Assert.Equal(1, At(rows, 34).Signal);
        Assert.Equal(-1, At(rows, 35).Signal);
        Assert.Equal(2, rows.Count(r => r.Signal != 0));
    }

    [Fact]
    public void Run_DirectionUp_KeepsOnlyTheLong()
    {
        var rows = Trace(EmaDirection.Up);

        Assert.Equal(1, At(rows, 34).Signal);
        Assert.Equal(0, At(rows, 35).Signal);
    }

    [Theory]
    [InlineData(EmaDirection.Up, true, false)]
    [InlineData(EmaDirection.Down, false, true)]
    [InlineData(EmaDirection.Both, true, true)]
    public void ParitySetup_Sides_FollowTheDirection(EmaDirection direction, bool allowLong, bool allowShort)
    {
        var setup = EmaParity.ParitySetup(Params(direction), barMinutes: 5);

        Assert.Equal(allowLong, setup.AllowLong);
        Assert.Equal(allowShort, setup.AllowShort);
    }

    [Fact]
    public void ParitySetup_LeavesOnlyTheSignalRulesDeciding()
    {
        var setup = EmaParity.ParitySetup(Params(), barMinutes: 5);

        Assert.Equal(StrategyType.Ema, setup.StrategyType);
        Assert.Equal(SignalTimeframe.M5, setup.SignalTimeframe);
        Assert.Equal(5, setup.ExecutionTFMinutes);
        Assert.False(setup.CheckEveryExecutionBar);
        Assert.False(setup.EnforceMinRr);
        Assert.False(setup.AutoSizeByRisk);
        Assert.DoesNotContain(setup.Confirmations, c => c.Enabled);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaSignalTraceTests"`
Expected: FAIL to compile — `The name 'EmaSignalTrace' does not exist` and `'EmaParity' does not contain a definition for 'ParitySetup'`.

- [ ] **Step 3: Add `ParitySetup` to `EmaParity`**

In `CRV.Backtest/Experiments/EmaParity.cs`, add `using CRV.Backtest.DataLoaders;`, `using CRV.Core.Models;` and `using CRV.Core.Strategy;` to the usings, and inside `EmaParity`:

```csharp
    /// <summary>
    /// The strategy configured as the Pine script ran: the same signal inputs, no confirmations,
    /// no min-R guard, no trade cap and one contract, so nothing but the signal rules decides
    /// whether it signals. The stop (ATR from entry) and the dollar target are valid for every
    /// entry and are there only because the strategy needs levels to emit an entry.
    /// </summary>
    public static StrategySetupConfig ParitySetup(PineParityParameters p, int barMinutes)
    {
        var setup = new StrategySetupConfig
        {
            Id = "parity", Name = "EMA parity", SetupId = SetupId.F, StrategyType = StrategyType.Ema, Enabled = true,
            Ticker = "/NQ", PointValue = 20m, TickSize = p.TickSize, ExecutionTFMinutes = barMinutes,
            EmaSource = p.Source, EmaEntry = p.Entry, EmaDirection = p.Direction,
            EmaPeriod = p.EmaPeriod, FastEma = p.FastEma, SlowEma = p.SlowEma, RetestEma = p.RetestEma,
            SignalTimeframe = p.Timeframe, CheckEveryExecutionBar = false,
            TouchTicks = p.TouchTicks, TouchAtr = p.TouchAtr, RearmAtr = p.RearmAtr, CloseBackOnSide = p.CloseBackOnSide,
            MinCloseBeyondAtr = p.MinCloseBeyondAtr, MinSeparationAtr = p.MinSeparationAtr, HoldBars = p.HoldBars,
            RetestMinAwayAtr = p.RetestMinAwayAtr, RetestWindowBars = p.RetestWindowBars,
            MaxEntriesPerCross = p.MaxEntriesPerCross,
            AllowLong = p.Direction != EmaDirection.Down, AllowShort = p.Direction != EmaDirection.Up,
            EmaStopMode = EmaStopMode.EntryAtr, StopBuffer = 1.0m,
            TargetMode = TargetMode.Dollars, TargetDollars = 400m, TargetDollarsBasis = TargetDollarsBasis.PerContract,
            EnforceMinRr = false, MinRr = 0m,
            Contracts = 1, MaxContracts = 1, AutoSizeByRisk = false, UsePartial = false, UseBe = false,
            MaxTrades = 1_000_000, CutoffHour = 23, CutoffMinute = 59, CloseAtRthClose = false,
            UseVwap = false, UseEmaFilter = false, BypassChopFilter = true, OrderType = "Market",
        };
        EmaConfirmationSwitches.ClearAll(setup);
        return setup;
    }
```

- [ ] **Step 4: Implement the trace**

`CRV.Backtest/Experiments/EmaSignalTrace.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;

namespace CRV.Backtest.Experiments;

/// <summary>
/// Runs the EMA strategy over execution bars and records, for every closed signal bar, its EMA
/// value(s) and whether the strategy signalled on it: the C# side of a parity comparison.
/// <para>
/// The strategy is driven through <see cref="ISetupStrategy"/> in the backtest's order (the
/// bar's open tick, then the bar), but every entry is treated as a trade that closes at once and
/// the trade counters are cleared after it. The Pine reference has no position, so a signal the
/// strategy skipped only because it was in a trade, or out of trades, would be a lifecycle
/// difference rather than a logic one.
/// </para>
/// <para>
/// The EMA values come from <see cref="SessionBarAggregator"/> and <see cref="EmaIndicator"/>,
/// the classes the strategy builds its signal bars and EMAs with.
/// </para>
/// </summary>
public static class EmaSignalTrace
{
    public static IReadOnlyList<EmaTraceRow> Run(StrategySetupConfig setup, IEnumerable<Bar> executionBars)
    {
        var tf         = setup.SignalTimeframe;
        bool twoEmas   = setup.EmaSource == EmaSource.EmaVsEma;
        var aggregator = new SessionBarAggregator(tf, setup.ExecutionTFMinutes);
        var emaA       = new EmaIndicator(twoEmas ? setup.FastEma : setup.EmaPeriod);
        var emaB       = twoEmas ? new EmaIndicator(setup.SlowEma) : null;
        var atr        = new AtrIndicator();
        var strategy   = StrategyFactory.Create(setup);

        var rows  = new List<EmaTraceRow>();
        var byKey = new Dictionary<DateTime, int>();
        HtfBar? lastClosed = null;
        Bar? previous = null;

        foreach (var bar in executionBars)
        {
            strategy.ResetCutoff();
            strategy.OnTick(bar.Open, bar.Time, default, State(atr, bar.Open), default);
            Record(strategy, lastClosed, tf, rows, byKey);

            atr.Update(bar);
            if (aggregator.OnExecutionBar(bar) is { } closed)
            {
                emaA.Add(closed.Close);
                emaB?.Add(closed.Close);
                var key = EmaParity.KeyOf(tf, closed);
                byKey[key] = rows.Count;
                rows.Add(new EmaTraceRow(key,
                    emaA.IsReady ? emaA.Value : null,
                    emaB is { IsReady: true } ? emaB.Value : null,
                    0));
                lastClosed = closed;
            }

            strategy.OnBar(bar, default, State(atr, bar.Close), default);
            Record(strategy, lastClosed, tf, rows, byKey);
            previous = bar;
        }

        // A signal on the last signal bar enters on the next bar's open, which the export does not hold.
        if (previous is not null)
        {
            strategy.OnTick(previous.Close, previous.Time.AddMinutes(setup.ExecutionTFMinutes),
                default, State(atr, previous.Close), default);
            Record(strategy, lastClosed, tf, rows, byKey);
        }
        return rows;
    }

    private static IndicatorState State(AtrIndicator atr, decimal lastClose) =>
        new(atr.Value, 0m, 0m, 0m, 0m, 0m, lastClose);

    // An entry belongs to the signal bar that closed most recently: the strategy signals on that
    // bar's close and enters on the next bar's open.
    private static void Record(ISetupStrategy strategy, HtfBar? signalBar, SignalTimeframe tf,
        List<EmaTraceRow> rows, Dictionary<DateTime, int> byKey)
    {
        var entry = strategy.PendingEntry;
        if (entry is null && strategy.PendingSizeRefusal is null) return;

        if (entry is not null && signalBar is not null &&
            byKey.TryGetValue(EmaParity.KeyOf(tf, signalBar), out int i))
            rows[i] = rows[i] with { Signal = entry.Direction == Direction.Long ? 1 : -1 };

        strategy.ClearPendingSignals();
        if (entry is not null)
        {
            strategy.SetInTrade(true);
            strategy.SetInTrade(false);
        }
        strategy.ResetTradeCounters();
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaSignalTraceTests"`
Expected: PASS (7 tests). If `Run_BothDirections_SignalOnTheBarsWhoseCloseCrossed` fails, read `EmaStrategy` before changing anything here: a signal on bar 33 means the strategy enters on the crossing bar's own open (lookahead, plan 5 bug); no signal at all means something other than the signal rules gates it (record what, and ask).

- [ ] **Step 6: Commit**

```bash
git add CRV.Backtest/Experiments/EmaSignalTrace.cs CRV.Backtest/Experiments/EmaParity.cs CRV.Core.Tests/Backtest/EmaSignalTraceTests.cs
git commit -m "feat(ema): trace the strategy's EMA and signal on every closed signal bar"
```

---

### Task 7: Logic parity end to end

**Files:**
- Modify: `CRV.Backtest/Experiments/EmaParity.cs` (add `StrategyAtrLength`, `Run`)
- Test: `CRV.Core.Tests/Backtest/EmaParityRunTests.cs`

**Interfaces:**
- Consumes: `TradingViewExport` (Task 5), `EmaSignalTrace.Run`, `ParitySetup` (Task 6), `Compare` (Task 4), `HistoryRequirement.For`.
- Produces: `const int EmaParity.StrategyAtrLength = 14`; `static EmaParityReport EmaParity.Run(TradingViewExport export)` — throws `InvalidOperationException` with a plain message for an export without Pine columns, another ATR length, or a bar size that cannot build the signal bars.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Backtest/EmaParityRunTests.cs`:

```csharp
using CRV.Backtest.DataLoaders;
using CRV.Backtest.Experiments;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The whole logic-parity path on a synthetic export: read the file, rebuild the setup from the
/// Pine inputs, run the strategy on the file's bars, compare after a 24-bar warmup (3 × EMA 8).
/// </summary>
public class EmaParityRunTests
{
    private static EmaParityReport Run(string csv) => EmaParity.Run(TradingViewExport.Read(new StringReader(csv)));

    [Fact]
    public void Run_AMatchingExport_Passes()
    {
        var report = Run(EmaParityFixture.ExportCsv());

        Assert.True(report.Passed, report.Describe());
        Assert.Equal(24, report.WarmupRows);
        Assert.Equal(EmaParityFixture.Count - 24, report.Compared);
        Assert.Equal(2, report.MatchedSignals);
    }

    [Fact]
    public void Run_PinesEmaTwoTicksOff_FailsNamingTheBar()
    {
        var report = Run(EmaParityFixture.ExportCsv(emaA: n => n == 33 ? 100.055556m : EmaParityFixture.EmaA(n)));

        var miss = Assert.Single(report.Mismatches);
        Assert.Equal("2026-04-15 12:10 ET", miss.Bar);
        Assert.StartsWith("EMA A off by 2 ticks", miss.Problem);
    }

    [Fact]
    public void Run_APineSignalTheStrategyDidNotGive_FailsNamingTheBar()
    {
        var report = Run(EmaParityFixture.ExportCsv(signal: n => n == 30 ? 1 : EmaParityFixture.Signal(n)));

        var miss = Assert.Single(report.Mismatches);
        Assert.Equal("2026-04-15 11:55 ET", miss.Bar);
        Assert.Contains("Pine long, C# none", miss.Problem);
    }

    [Fact]
    public void Run_AnExportWithoutPineColumns_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Run(EmaParityFixture.ExportCsv(withPine: false)));

        Assert.Contains("Pine parity columns", ex.Message);
    }

    [Fact]
    public void Run_AnotherAtrLength_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Run(EmaParityFixture.ExportCsv(atrLength: 20)));

        Assert.Contains("ATR 20", ex.Message);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaParityRunTests"`
Expected: FAIL to compile — `'EmaParity' does not contain a definition for 'Run'`.

- [ ] **Step 3: Implement**

In `EmaParity`:

```csharp
    /// <summary>
    /// The ATR length behind the strategy's ATR-scaled tolerances and thresholds on signal bars.
    /// The ema-strategy spec has no field for it, so the Pine script must be run with this value.
    /// </summary>
    public const int StrategyAtrLength = 14;

    /// <summary>
    /// Logic parity for one export: the strategy, set up from the script's own inputs, on the
    /// export's bars from the parity start, against the script's rows. The warmup is the
    /// history the EMA needs before it matches TradingView (3 × the slowest period).
    /// </summary>
    public static EmaParityReport Run(TradingViewExport export)
    {
        var p = export.Parameters ?? throw new InvalidOperationException(
            "This export has no Pine parity columns. Put the CRV EMA parity script on the chart before exporting.");

        if (p.AtrLength != StrategyAtrLength)
            throw new InvalidOperationException(
                $"The Pine script ran with ATR {p.AtrLength}; the strategy uses ATR {StrategyAtrLength} on signal bars. " +
                $"Set the script's ATR length to {StrategyAtrLength} and export again.");

        int signalMinutes = MinutesOf(p.Timeframe);
        if (export.BarMinutes <= 0 || 60 % export.BarMinutes != 0 || signalMinutes % export.BarMinutes != 0)
            throw new InvalidOperationException(
                $"The export's {export.BarMinutes}-minute bars cannot build {p.Timeframe} bars. " +
                "Export a chart at 1, 2, 5, 10, 15, 20, 30 or 60 minutes that divides the signal timeframe.");

        var bars   = export.Bars.Where(b => b.Time >= p.StartUtc).ToList();
        var csharp = EmaSignalTrace.Run(ParitySetup(p, export.BarMinutes), bars);
        int warmup = HistoryRequirement.For(p.SlowestPeriod).ParityNeeded;

        return Compare(export.PineRows, csharp, p.Timeframe, p.TickSize, warmup);
    }

    private static int MinutesOf(SignalTimeframe tf) => tf switch
    {
        SignalTimeframe.M5  => 5,
        SignalTimeframe.M15 => 15,
        SignalTimeframe.M30 => 30,
        SignalTimeframe.H1  => 60,
        SignalTimeframe.H4  => 240,
        SignalTimeframe.H8  => 480,
        SignalTimeframe.D1  => 1440,
        _ => throw new InvalidOperationException($"{tf} is outside logic parity."),
    };
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaParity"`
Expected: PASS (all `EmaParityCompareTests`, `EmaParityRunTests`).

- [ ] **Step 5: Commit**

```bash
git add CRV.Backtest/Experiments/EmaParity.cs CRV.Core.Tests/Backtest/EmaParityRunTests.cs
git commit -m "feat(ema): run logic parity from a TradingView export"
```

---

### Task 8: Schwab against TradingView bars

**Files:**
- Create: `CRV.Backtest/Experiments/BarSourceComparison.cs`
- Test: `CRV.Core.Tests/Backtest/BarSourceComparisonTests.cs`

**Interfaces:**
- Consumes: `Bar`, `SignalTimeframe`.
- Produces (namespace `CRV.Backtest.Experiments`):
  - `sealed record BarDifference(DateTime OpenUtc, decimal OpenTicks, decimal HighTicks, decimal LowTicks, decimal CloseTicks)` with `decimal LargestTicks` (signed differences are other − reference)
  - `sealed class BarSourceReport` — `DateTime From`, `DateTime To`, `int ReferenceBars`, `int Matched`, `IReadOnlyList<DateTime> MissingInOther`, `IReadOnlyList<DateTime> ExtraInOther`, `IReadOnlyList<BarDifference> Differences`, `decimal LargestTicks`, `decimal MeanCloseTicks`, `string Describe()`
  - `static class BarSourceComparison` — `BarSourceReport Compare(IReadOnlyList<Bar> reference, IReadOnlyList<Bar> other, decimal tickSize)`, `SignalTimeframe StoredTimeframeFor(int minutes)`

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Backtest/BarSourceComparisonTests.cs`:

```csharp
using CRV.Backtest.Experiments;
using CRV.Core.Indicators;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>Spec test 2: per-bar differences and missing bars on fixtures. TradingView is the reference.</summary>
public class BarSourceComparisonTests
{
    private const decimal Tick = 0.25m;
    private static readonly DateTime T0 = new(2026, 4, 15, 13, 0, 0, DateTimeKind.Utc);

    private static Bar B(int hour, decimal close, decimal highExtra = 0m) =>
        new(T0.AddHours(hour), close - 1m, close + 2m + highExtra, close - 2m, close, 100);

    private static List<Bar> Reference() => new() { B(0, 100m), B(1, 101m), B(2, 102m) };

    [Fact]
    public void Compare_IdenticalFeeds_ShowNoDifference()
    {
        var report = BarSourceComparison.Compare(Reference(), Reference(), Tick);

        Assert.Equal(3, report.Matched);
        Assert.Empty(report.Differences);
        Assert.Empty(report.MissingInOther);
        Assert.Empty(report.ExtraInOther);
        Assert.Equal(0m, report.MeanCloseTicks);
    }

    [Fact]
    public void Compare_AHighOneTickApart_IsReportedOnItsBar()
    {
        var other = new List<Bar> { B(0, 100m), B(1, 101m, highExtra: 0.25m), B(2, 102m) };

        var report = BarSourceComparison.Compare(Reference(), other, Tick);

        var d = Assert.Single(report.Differences);
        Assert.Equal(T0.AddHours(1), d.OpenUtc);
        Assert.Equal(1m, d.HighTicks);
        Assert.Equal(0m, d.CloseTicks);
        Assert.Equal(1m, report.LargestTicks);
    }

    [Fact]
    public void Compare_ABarSchwabLacks_IsMissing()
    {
        var report = BarSourceComparison.Compare(Reference(), new List<Bar> { B(0, 100m), B(2, 102m) }, Tick);

        Assert.Equal(new[] { T0.AddHours(1) }, report.MissingInOther);
        Assert.Equal(2, report.Matched);
    }

    [Fact]
    public void Compare_AnExtraBarInsideTheWindow_IsCounted_AndOneOutsideIsNot()
    {
        var reference = new List<Bar> { B(0, 100m), B(2, 102m) };
        var other     = new List<Bar> { B(0, 100m), B(1, 101m), B(2, 102m), B(5, 105m) };

        var report = BarSourceComparison.Compare(reference, other, Tick);

        Assert.Equal(new[] { T0.AddHours(1) }, report.ExtraInOther);
    }

    [Fact]
    public void Compare_ASteadyOffset_ShowsInTheMeanClose()
    {
        // Every Schwab bar a point (4 ticks) higher: what a back-adjusted series looks like.
        var other = Reference().Select(b => b with { Open = b.Open + 1m, High = b.High + 1m, Low = b.Low + 1m, Close = b.Close + 1m }).ToList();

        var report = BarSourceComparison.Compare(Reference(), other, Tick);

        Assert.Equal(4m, report.MeanCloseTicks);
        Assert.Equal(3, report.Differences.Count);
    }

    [Fact]
    public void Compare_NoReferenceBars_SaysSo()
    {
        var report = BarSourceComparison.Compare(new List<Bar>(), Reference(), Tick);

        Assert.Equal(0, report.ReferenceBars);
        Assert.Contains("no reference bars", report.Describe());
    }

    [Fact]
    public void StoredTimeframeFor_ABarSizeWithNoStoredHistory_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BarSourceComparison.StoredTimeframeFor(10));

        Assert.Contains("10 minutes", ex.Message);
    }

    [Theory]
    [InlineData(5, SignalTimeframe.M5)]
    [InlineData(60, SignalTimeframe.H1)]
    public void StoredTimeframeFor_StoredSizes_Map(int minutes, SignalTimeframe expected)
        => Assert.Equal(expected, BarSourceComparison.StoredTimeframeFor(minutes));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~BarSourceComparisonTests"`
Expected: FAIL to compile — `The name 'BarSourceComparison' does not exist`.

- [ ] **Step 3: Implement**

`CRV.Backtest/Experiments/BarSourceComparison.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;

namespace CRV.Backtest.Experiments;

/// <summary>A bar both feeds hold whose prices differ, in ticks, other minus reference.</summary>
public sealed record BarDifference(DateTime OpenUtc, decimal OpenTicks, decimal HighTicks, decimal LowTicks, decimal CloseTicks)
{
    public decimal LargestTicks => new[] { OpenTicks, HighTicks, LowTicks, CloseTicks }.Max(t => Math.Abs(t));
}

public sealed class BarSourceReport
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int ReferenceBars { get; init; }
    public int Matched { get; init; }

    /// <summary>Reference bars the other feed does not have.</summary>
    public IReadOnlyList<DateTime> MissingInOther { get; init; } = [];

    /// <summary>Bars inside the reference's window that only the other feed has.</summary>
    public IReadOnlyList<DateTime> ExtraInOther { get; init; } = [];

    public IReadOnlyList<BarDifference> Differences { get; init; } = [];
    public decimal LargestTicks { get; init; }

    /// <summary>Mean signed close difference over matched bars. A steady non-zero value points at a back-adjusted series.</summary>
    public decimal MeanCloseTicks { get; init; }

    public string Describe() => ReferenceBars == 0
        ? "no reference bars to compare"
        : $"{Matched} of {ReferenceBars} bars in both; {MissingInOther.Count} missing, {ExtraInOther.Count} extra; " +
          $"{Differences.Count} differ, largest {LargestTicks:0.##} ticks; closes {MeanCloseTicks:+0.##;-0.##;0} ticks apart on average";
}

/// <summary>
/// How far one bar feed sits from another over the same window: Schwab's stored bars against a
/// TradingView export. Reported, never passed or failed. It tells how far live signals, built
/// on Schwab's bars, can drift from TradingView's.
/// </summary>
public static class BarSourceComparison
{
    public static BarSourceReport Compare(IReadOnlyList<Bar> reference, IReadOnlyList<Bar> other, decimal tickSize)
    {
        if (tickSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "Tick size must be positive.");
        if (reference.Count == 0) return new BarSourceReport();

        var from = reference.Min(b => b.Time);
        var to   = reference.Max(b => b.Time);
        var refByTime   = reference.GroupBy(b => b.Time).ToDictionary(g => g.Key, g => g.First());
        var otherByTime = other.Where(b => b.Time >= from && b.Time <= to)
                               .GroupBy(b => b.Time).ToDictionary(g => g.Key, g => g.First());

        var differences = new List<BarDifference>();
        decimal closeSum = 0m;
        int matched = 0;
        foreach (var (time, r) in refByTime.OrderBy(kv => kv.Key))
        {
            if (!otherByTime.TryGetValue(time, out var o)) continue;
            matched++;
            var d = new BarDifference(time,
                (o.Open - r.Open) / tickSize, (o.High - r.High) / tickSize,
                (o.Low - r.Low) / tickSize, (o.Close - r.Close) / tickSize);
            closeSum += d.CloseTicks;
            if (d.LargestTicks != 0m) differences.Add(d);
        }

        return new BarSourceReport
        {
            From = from, To = to, ReferenceBars = refByTime.Count, Matched = matched,
            MissingInOther = refByTime.Keys.Where(t => !otherByTime.ContainsKey(t)).OrderBy(t => t).ToList(),
            ExtraInOther   = otherByTime.Keys.Where(t => !refByTime.ContainsKey(t)).OrderBy(t => t).ToList(),
            Differences    = differences,
            LargestTicks   = differences.Count == 0 ? 0m : differences.Max(d => d.LargestTicks),
            MeanCloseTicks = matched == 0 ? 0m : closeSum / matched,
        };
    }

    /// <summary>
    /// The stored timeframe holding bars of <paramref name="minutes"/>. The history is filled at
    /// 5, 15, 30 and 60 minutes, so a TradingView export has to be taken at one of those sizes.
    /// </summary>
    public static SignalTimeframe StoredTimeframeFor(int minutes) => minutes switch
    {
        5  => SignalTimeframe.M5,
        15 => SignalTimeframe.M15,
        30 => SignalTimeframe.M30,
        60 => SignalTimeframe.H1,
        _  => throw new InvalidOperationException(
            $"Stored Schwab bars come in 5, 15, 30 and 60 minutes; this export's bars are {minutes} minutes. " +
            "Export the chart at one of those sizes."),
    };
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~BarSourceComparisonTests"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Backtest/Experiments/BarSourceComparison.cs CRV.Core.Tests/Backtest/BarSourceComparisonTests.cs
git commit -m "feat(ema): report per-bar differences between Schwab and TradingView bars"
```

---

### Task 9: EMA studies in `ValidationRunner`

**Files:**
- Modify: `CRV.Backtest/Experiments/ValidationRunner.cs` (usings; new EMA section after the ablation section, ~line 144; `RunAsync` at lines 151–165)
- Test: `CRV.Core.Tests/Backtest/EmaStudyTests.cs`

**Interfaces:**
- Consumes: `StrategyConfig.ToEmaSetupConfigs()`, `MapEmaBasketEntries` and `EmaConfirmationSwitches` (Task 2), `SampleSplit`, `EdgeTest`, `Ablation`, `BacktestResultCalculator.Calculate`, `PerformanceMetrics`.
- Produces (nested in `ValidationRunner`'s namespace `CRV.Backtest.Experiments`):
  - `sealed record EmaStudySide(EdgeTest Edge, PerformanceMetrics Metrics, int Refused)`
  - `sealed record EmaConfirmationResult(string Name, Ablation InSample, Ablation OutOfSample)`
  - `sealed record EmaSetupStudy(string SetupId, string Label, string Ticker, SignalTimeframe Timeframe, TimeSpan Embargo, SampleSplit Split, EmaStudySide InSample, EmaStudySide OutOfSample, IReadOnlyList<EmaConfirmationResult> Confirmations)`
  - `sealed record EmaStudyReport(DateTime From, DateTime To, string BarSource, FillMode FillMode, int SlippageTicks, int StopSlippageTicks, decimal CommissionPerSide, IReadOnlyList<EmaSetupStudy> Setups)`
  - `Task<EmaStudyReport> ValidationRunner.EmaStudiesAsync(StrategyConfig cfg, BacktestConfig btCfg, double inSampleFraction = 0.70, TimeSpan? embargo = null, CancellationToken ct = default)`
  - `static StrategyConfig ValidationRunner.IsolateEmaSetup(StrategyConfig cfg, string setupId, Action<StrategySetupConfig> change)`
  - `static TimeSpan ValidationRunner.EmbargoFor(SignalTimeframe tf, TimeSpan floor)`

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Backtest/EmaStudyTests.cs`:

```csharp
using CRV.Backtest.DataLoaders;
using CRV.Backtest.Engine;
using CRV.Backtest.Experiments;
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Statistics;
using CRV.Core.Strategy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// Spec test 3: each enabled EMA setup alone, with no confirmations and then with each
/// confirmation on its own, split by date. One session of bars is far below twenty trades, so
/// every verdict must come back as insufficient evidence rather than a ranking.
/// </summary>
public class EmaStudyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crv-ema-study-" + Guid.NewGuid().ToString("N"));
    private const string Ticker      = "MNQM26";
    private const string OtherTicker = "MESM26";
    private static readonly DateTime Open = new(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private BarSnapshotStore Store() => new(_dir);
    private ValidationRunner Runner() => new(Store(), NullLogger<ValidationRunner>.Instance);

    private static BasketEntry EmaCross(string id, bool enabled) => new()
    {
        Id = id, Enabled = enabled, Label = $"EMA cross [{id}]", StrategyType = StrategyType.Ema,
        Ticker = Ticker, PointValue = 2m, TickSize = 0.25m, ExecutionTFMinutes = 5,
        Sessions = new()
        {
            new() { SessionId = "Asia",   Enabled = false, CutoffHour = 1,  CutoffMinute = 30 },
            new() { SessionId = "London", Enabled = false, CutoffHour = 8,  CutoffMinute = 0  },
            new() { SessionId = "NY",     Enabled = true,  CutoffHour = 15, CutoffMinute = 30 },
        },
        Config = new StrategySetupConfig
        {
            StrategyType = StrategyType.Ema, Enabled = enabled, Ticker = Ticker, PointValue = 2m, TickSize = 0.25m,
            EmaSource = EmaSource.PriceVsEma, EmaEntry = EmaEntry.Cross, EmaDirection = EmaDirection.Both,
            EmaPeriod = 8, SignalTimeframe = SignalTimeframe.M5,
            EmaStopMode = EmaStopMode.EntryAtr, StopBuffer = 1.0m,
            TargetMode = TargetMode.Dollars, TargetDollars = 20m, TargetDollarsBasis = TargetDollarsBasis.PerContract,
            EnforceMinRr = false, MinRr = 0m,
            Contracts = 1, MaxContracts = 1, MaxTrades = 20, UsePartial = false, UseBe = false,
            BypassChopFilter = true, UseVwap = false, CloseAtRthClose = true,
        },
    };

    // An enabled ORB entry on a second ticker: isolating the EMA setup switches it off, which
    // changes the enabled tickers, yet the study must still replay the basket's snapshot.
    private static BasketEntry OrbOnOtherTicker() => new()
    {
        Id = "pullback-mes", Enabled = true, Label = "Pullback [MES]", StrategyType = StrategyType.Pullback,
        Ticker = OtherTicker, PointValue = 5m, TickSize = 0.25m,
        Config = new StrategySetupConfig { StrategyType = StrategyType.Pullback, Enabled = true, Ticker = OtherTicker },
    };

    private static StrategyConfig Config(bool emaEnabled = true) => new()
    {
        Ticker = Ticker, PointValue = 2m, TickSize = 0.25m, CommissionPerSide = 0.90m, ExecutionTFMinutes = 5,
        BasketJson    = BasketCodec.Serialize(new[] { OrbOnOtherTicker() }),
        EmaBasketJson = BasketCodec.Serialize(new[] { EmaCross("ema-cross", emaEnabled), EmaCross("ema-off", false) }),
    };

    private static BacktestConfig BtConfig() => new()
    {
        From = Open.Date, To = Open.Date.AddDays(1),
        FillMode = FillMode.WithSlippage, ExecutionTFMinutes = 5,
        BacktestSession = "NY", DataSource = "CSV",
    };

    // A 50-minute swing of ±30 points: the 8-EMA on five-minute bars is crossed every half swing.
    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> Session()
    {
        decimal prev = 18000m;
        for (int m = 0; m < 390; m++)
        {
            decimal next = 18000m + Math.Round((decimal)(30 * Math.Sin(2 * Math.PI * (m + 1) / 50.0)) * 4m) / 4m;
            yield return (Ticker, new Bar(Open.AddMinutes(m), prev, Math.Max(prev, next) + 0.5m, Math.Min(prev, next) - 0.5m, next, 500));
            prev = next;
        }
        await Task.CompletedTask;
    }

    private async Task CaptureBars()
    {
        var key = BarSnapshotStore.KeyFor(BtConfig(), new[] { Ticker, OtherTicker });
        await foreach (var _ in Store().Capture(key, Session())) { }
    }

    [Fact]
    public async Task EmaStudies_OnOneSession_ReportInsufficientEvidenceEverywhere()
    {
        await CaptureBars();

        var report = await Runner().EmaStudiesAsync(Config(), BtConfig(), embargo: TimeSpan.Zero);

        var study = Assert.Single(report.Setups);
        Assert.Equal("ema-cross", study.SetupId);
        Assert.True(study.Split.InSample.Count + study.Split.OutOfSample.Count + study.Split.EmbargoedCount > 0,
            "The swings cross the 8-EMA many times; a study with no trades measured nothing.");
        Assert.Equal(EdgeVerdict.InsufficientEvidence, study.InSample.Edge.Verdict);
        Assert.Equal(EdgeVerdict.InsufficientEvidence, study.OutOfSample.Edge.Verdict);
        Assert.Equal(EmaConfirmationSwitches.For(EmaEntry.Cross).Select(s => s.Name),
                     study.Confirmations.Select(c => c.Name));
        Assert.All(study.Confirmations, c =>
        {
            Assert.Equal(AblationVerdict.InsufficientEvidence, c.InSample.Verdict);
            Assert.Equal(AblationVerdict.InsufficientEvidence, c.OutOfSample.Verdict);
        });
    }

    [Fact]
    public async Task EmaStudies_Results_StateTheirAssumptions()
    {
        await CaptureBars();

        var report = await Runner().EmaStudiesAsync(Config(), BtConfig(), embargo: TimeSpan.Zero);

        Assert.Equal(Open.Date, report.From);
        Assert.Equal(Open.Date.AddDays(1), report.To);
        Assert.Equal("CSV", report.BarSource);
        Assert.Equal(FillMode.WithSlippage, report.FillMode);
        Assert.Equal(1, report.SlippageTicks);
        Assert.Equal(4, report.StopSlippageTicks);
        Assert.Equal(0.90m, report.CommissionPerSide);
        var study = report.Setups.Single();
        Assert.Equal(Ticker, study.Ticker);
        Assert.Equal(SignalTimeframe.M5, study.Timeframe);
        Assert.Equal(study.InSample.Edge.Count, study.InSample.Metrics.TotalTrades);
    }

    [Fact]
    public async Task EmaStudies_WithNoEmaSetupOn_RunNothing()
    {
        // No snapshot is captured: a study with nothing to run must not need one.
        var report = await Runner().EmaStudiesAsync(Config(emaEnabled: false), BtConfig());

        Assert.Empty(report.Setups);
    }

    [Fact]
    public void IsolateEmaSetup_LeavesOnlyThatSetupOn_OnACopy()
    {
        var original = Config();

        var isolated = ValidationRunner.IsolateEmaSetup(original, "ema-cross", EmaConfirmationSwitches.ClearAll);

        var on = Assert.Single(isolated.ToSetupConfigs(), s => s.Enabled);
        Assert.Equal("ema-cross", on.Id);
        Assert.DoesNotContain(on.Confirmations, c => c.Enabled);
        Assert.True(original.ToSetupConfigs().Single(s => s.Id == "pullback-mes").Enabled);
    }

    [Theory]
    [InlineData(SignalTimeframe.M5, 1, 1.0)]
    [InlineData(SignalTimeframe.D1, 1, 2.0)]
    [InlineData(SignalTimeframe.MN1, 1, 62.0)]
    public void EmbargoFor_IsTwoSignalBarsOrTheFloor(SignalTimeframe tf, int floorDays, double expectedDays)
        => Assert.Equal(TimeSpan.FromDays(expectedDays), ValidationRunner.EmbargoFor(tf, TimeSpan.FromDays(floorDays)));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaStudyTests"`
Expected: FAIL to compile — `'ValidationRunner' does not contain a definition for 'EmaStudiesAsync'`.

- [ ] **Step 3: Let `RunAsync` replay the basket's snapshot**

Replace `RunAsync` (lines 146–165) with:

```csharp
    /// <summary>
    /// Runs one configuration over the snapshotted bars. The snapshot must already
    /// exist — a validation study is not the place to discover the broker is down
    /// halfway through variant seven. <paramref name="barsFor"/> is the configuration the
    /// snapshot was keyed on, when <paramref name="cfg"/> switches entries off: a study of
    /// one setup still replays the whole basket's bars.
    /// </summary>
    private async Task<BacktestResult> RunAsync(StrategyConfig cfg, BacktestConfig btCfg,
        CancellationToken ct, StrategyConfig? barsFor = null)
    {
        var tickers = (barsFor ?? cfg).ToSetupConfigs().Where(s => s.Enabled)
            .Select(s => s.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var key = BarSnapshotStore.KeyFor(btCfg, tickers);
        if (!_snapshots.Has(key))
            throw new BarLoadException(
                $"No bar snapshot for key {key}. Run the backtest once to capture it before validating — " +
                "every variant must see identical bars, or the sweep measures the data rather than the parameter.");

        var engine = new BacktestEngine(cfg, btCfg, NullLogger<BacktestEngine>.Instance);
        return await engine.RunAsync(_snapshots.Replay(key, ct), ct);
    }
```

- [ ] **Step 4: Add the EMA studies**

Add `using CRV.Core.Indicators;` to the usings. Insert after `DisableAllFilters` (before `RunAsync`):

```csharp
    // ── EMA studies ───────────────────────────────────────────────

    /// <summary>One side of a split as the EMA study reports it.</summary>
    public sealed record EmaStudySide(EdgeTest Edge, PerformanceMetrics Metrics, int Refused);

    /// <summary>One confirmation on its own, against the bare signal on each side of the same boundary.</summary>
    public sealed record EmaConfirmationResult(string Name, Ablation InSample, Ablation OutOfSample);

    public sealed record EmaSetupStudy(
        string SetupId, string Label, string Ticker, SignalTimeframe Timeframe, TimeSpan Embargo,
        SampleSplit Split, EmaStudySide InSample, EmaStudySide OutOfSample,
        IReadOnlyList<EmaConfirmationResult> Confirmations);

    /// <summary>Every EMA setup's study, with the assumptions every result is read under.</summary>
    public sealed record EmaStudyReport(
        DateTime From, DateTime To, string BarSource, FillMode FillMode,
        int SlippageTicks, int StopSlippageTicks, decimal CommissionPerSide,
        IReadOnlyList<EmaSetupStudy> Setups);

    /// <summary>
    /// Each enabled EMA setup on its own: first with no confirmations, split by date with an
    /// embargo, then with each confirmation switched on alone and measured against that bare
    /// signal on both sides of the same boundary. Nothing is chosen here; a setup with fewer
    /// than twenty trades on a side is reported as insufficient evidence, not ranked.
    /// </summary>
    public async Task<EmaStudyReport> EmaStudiesAsync(StrategyConfig cfg, BacktestConfig btCfg,
        double inSampleFraction = 0.70, TimeSpan? embargo = null, CancellationToken ct = default)
    {
        var studies = new List<EmaSetupStudy>();

        foreach (var setup in cfg.ToEmaSetupConfigs().Where(s => s.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            var gap = EmbargoFor(setup.SignalTimeframe, embargo ?? TimeSpan.Zero);

            var bare  = await RunAsync(IsolateEmaSetup(cfg, setup.Id, EmaConfirmationSwitches.ClearAll), btCfg, ct, barsFor: cfg);
            var split = SampleSplit.ByFraction(bare.Trades, inSampleFraction, gap, bare.SizeRefusals);

            var confirmations = new List<EmaConfirmationResult>();
            foreach (var (name, enable) in EmaConfirmationSwitches.For(setup.EmaEntry))
            {
                ct.ThrowIfCancellationRequested();
                var run    = await RunAsync(IsolateEmaSetup(cfg, setup.Id, enable), btCfg, ct, barsFor: cfg);
                var withIt = SampleSplit.ByDate(run.Trades, split.Boundary, gap, run.SizeRefusals);
                confirmations.Add(new EmaConfirmationResult(name,
                    new Ablation(split.InSampleEdge,    withIt.InSampleEdge,    name, withIt.InSampleRefused),
                    new Ablation(split.OutOfSampleEdge, withIt.OutOfSampleEdge, name, withIt.OutOfSampleRefused)));
            }

            _log.LogInformation("EMA study {Setup}:\n{Report}", setup.Name, split.Describe());
            studies.Add(new EmaSetupStudy(
                setup.Id, setup.Name, setup.Ticker, setup.SignalTimeframe, gap, split,
                Side(split.InSample,    split.InSampleEdge,    split.InSampleRefused),
                Side(split.OutOfSample, split.OutOfSampleEdge, split.OutOfSampleRefused),
                confirmations));
        }

        return new EmaStudyReport(btCfg.From, btCfg.To, btCfg.DataSource, btCfg.FillMode,
            btCfg.SlippageTicks, btCfg.StopSlippageTicks, cfg.CommissionPerSide, studies);

        EmaStudySide Side(IReadOnlyList<TradeRecord> trades, EdgeTest edge, int refused) =>
            new(edge, BacktestResultCalculator.Calculate(trades.ToList(), cfg, btCfg).Total, refused);
    }

    /// <summary>
    /// A copy of <paramref name="cfg"/> trading only the EMA entry <paramref name="setupId"/>,
    /// with <paramref name="change"/> applied to it. Every ORB entry, legacy setup and other
    /// EMA entry is off, so nothing else competes for the bars or the risk budget.
    /// </summary>
    public static StrategyConfig IsolateEmaSetup(StrategyConfig cfg, string setupId, Action<StrategySetupConfig> change)
    {
        var c = cfg.Clone();
        c.EnableA = c.EnableB = c.EnableC = c.EnableD = false;
        c.MapBasketEntries(e => e.Enabled = false);
        c.MapEmaBasketEntries(e =>
        {
            e.Enabled = e.Id == setupId;
            if (e.Enabled) change(e.Config);
        });
        return c;
    }

    /// <summary>
    /// The gap after the boundary for a setup on <paramref name="tf"/>: at least
    /// <paramref name="floor"/>, and at least two signal bars, because a position opened on the
    /// last in-sample signal bar is still open at least that long.
    /// </summary>
    public static TimeSpan EmbargoFor(SignalTimeframe tf, TimeSpan floor)
    {
        var bar = tf switch
        {
            SignalTimeframe.M5  => TimeSpan.FromMinutes(5),
            SignalTimeframe.M15 => TimeSpan.FromMinutes(15),
            SignalTimeframe.M30 => TimeSpan.FromMinutes(30),
            SignalTimeframe.H1  => TimeSpan.FromHours(1),
            SignalTimeframe.H4  => TimeSpan.FromHours(4),
            SignalTimeframe.H8  => TimeSpan.FromHours(8),
            SignalTimeframe.D1  => TimeSpan.FromDays(1),
            SignalTimeframe.W1  => TimeSpan.FromDays(7),
            SignalTimeframe.MN1 => TimeSpan.FromDays(31),
            _                   => TimeSpan.Zero,
        };
        var twoBars = bar * 2;
        return twoBars > floor ? twoBars : floor;
    }
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaStudyTests|FullyQualifiedName~ValidationRunnerTests"`
Expected: PASS (all `EmaStudyTests` and the existing `ValidationRunnerTests`).

- [ ] **Step 6: Run the suite**

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add CRV.Backtest/Experiments/ValidationRunner.cs CRV.Core.Tests/Backtest/EmaStudyTests.cs
git commit -m "feat(ema): validation studies for each EMA setup and each confirmation"
```

---

### Task 10: EMA studies on `/validation`

**Files:**
- Modify: `CRV.Web/Pages/Validation/Index.cshtml.cs`
- Modify: `CRV.Web/Pages/Validation/Index.cshtml` (lines 10, 18–25, 38–42, 214 onwards, 276–283)
- Modify: `CRV.Web.A11yTests/A11yAppFixture.cs`, `CRV.Web.A11yTests/A11ySeed.cs`, `CRV.Web.A11yTests/PageScanTests.cs`

**Interfaces:**
- Consumes: `ValidationRunner.EmaStudiesAsync` and its records (Task 9).
- Produces: `IndexModel.EmaStudies` (`ValidationRunner.EmaStudyReport?`), `IndexModel.Gap(TimeSpan)`, the `Study=ema` button; `A11ySeed.StudyDay`, `A11ySeed.EmaStudyBasketJson`, `A11ySeed.EmaBasket(IServiceProvider)`, `A11ySeed.SetEmaBasket(IServiceProvider, string)`, `A11ySeed.SeedValidationSnapshotAsync(IServiceProvider)`.

- [ ] **Step 1: MANUAL GATE — mockup approval (Cirino)**

Build a static HTML mockup of the new `/validation` content — the EMA study panel from Step 4 and the two parity panels from Task 11 Step 3 — linking the site's `tokens.css`, `shell.css` and `components.css`, filled with sample numbers (one setup with "TOO FEW TRADES", one parity run with three differences). Publish it as an Artifact and give Cirino the link. **Do not write page markup until Cirino approves.** Apply his changes to the markup in this task and Task 11 before continuing.

- [ ] **Step 2: Write the failing accessibility scan**

In `CRV.Web.A11yTests/A11yAppFixture.cs`, add `using CRV.Backtest.DataLoaders;` and `using Microsoft.AspNetCore.TestHost;`, and inside `WithWebHostBuilder(b => { … })` after the `UseSetting` loop:

```csharp
                // Bar snapshots go to the temp data dir, so a seeded validation study never writes into the repo.
                b.ConfigureTestServices(s => s.AddSingleton(new BarSnapshotStore(Path.Combine(DataDir, "bar-snapshots"))));
```

In `CRV.Web.A11yTests/A11ySeed.cs`, add `using CRV.Backtest.DataLoaders;` and `using CRV.Core.Indicators;`, and these members:

```csharp
    public static readonly DateTime StudyDay = new(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One EMA cross on five-minute signal bars, switched on, so the EMA study has a setup to run.</summary>
    public static string EmaStudyBasketJson { get; } = BasketCodec.Serialize(new[]
    {
        new BasketEntry
        {
            Id = "a11y-ema-study", Enabled = true, Label = "EMA cross [a11y]", StrategyType = StrategyType.Ema,
            Ticker = "/MNQZ26", PointValue = 2m, TickSize = 0.25m,
            Config = new StrategySetupConfig
            {
                StrategyType = StrategyType.Ema, EmaSource = EmaSource.PriceVsEma, EmaEntry = EmaEntry.Cross,
                EmaDirection = EmaDirection.Both, EmaPeriod = 8, SignalTimeframe = SignalTimeframe.M5,
                EmaStopMode = EmaStopMode.EntryAtr, StopBuffer = 1.0m,
                TargetMode = TargetMode.Dollars, TargetDollars = 20m, EnforceMinRr = false, MinRr = 0m,
                Contracts = 1, MaxContracts = 1, UsePartial = false, UseBe = false, BypassChopFilter = true,
            },
        },
    });

    public static string EmaBasket(IServiceProvider services) =>
        services.GetRequiredService<StrategyConfigService>().Current.EmaBasketJson ?? "";

    public static void SetEmaBasket(IServiceProvider services, string json)
    {
        var configs = services.GetRequiredService<StrategyConfigService>();
        var cfg = configs.Current;
        cfg.EmaBasketJson = json;
        configs.Update(cfg);
    }

    /// <summary>
    /// Saves one New York session of one-minute bars, for every ticker the basket trades, as the
    /// snapshot /validation looks for on <see cref="StudyDay"/>, keyed exactly as the page keys it.
    /// </summary>
    public static async Task SeedValidationSnapshotAsync(IServiceProvider services)
    {
        var cfg = services.GetRequiredService<StrategyConfigService>().Current;
        var btCfg = new BacktestConfig
        {
            From = StudyDay, To = StudyDay.AddDays(1),
            DataSource = cfg.Broker, ExecutionTFMinutes = cfg.ExecutionTFMinutes, BacktestSession = "All",
        };
        var tickers = cfg.ToSetupConfigs().Where(s => s.Enabled)
            .Select(s => s.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var store = services.GetRequiredService<BarSnapshotStore>();
        await foreach (var _ in store.Capture(BarSnapshotStore.KeyFor(btCfg, tickers), SessionBars(tickers))) { }
    }

    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> SessionBars(IReadOnlyList<string> tickers)
    {
        var open = StudyDay.AddHours(13).AddMinutes(30);
        decimal prev = 21000m;
        for (int m = 0; m < 390; m++)
        {
            decimal next = 21000m + Math.Round((decimal)(30 * Math.Sin(2 * Math.PI * (m + 1) / 50.0)) * 4m) / 4m;
            var bar = new Bar(open.AddMinutes(m), prev, Math.Max(prev, next) + 0.5m, Math.Min(prev, next) - 0.5m, next, 500);
            foreach (var t in tickers) yield return (t, bar);
            prev = next;
        }
        await Task.CompletedTask;
    }
```

In `CRV.Web.A11yTests/PageScanTests.cs`, add:

```csharp
    [Theory]
    [MemberData(nameof(Variants))]
    public async Task ValidationEmaStudy_HasNoWcagViolations(string theme, int width, int height)
    {
        // Runs the EMA study over one seeded session: one setup, every verdict "too few trades".
        var previous = A11ySeed.EmaBasket(app.Services);
        A11ySeed.SetEmaBasket(app.Services, A11ySeed.EmaStudyBasketJson);
        try
        {
            await A11ySeed.SeedValidationSnapshotAsync(app.Services);
            var report = await PageScanner.ScanAsync(app, "/validation?Study=ema&FromStr=2026-04-15&ToStr=2026-04-16",
                theme, width, height, async page =>
                {
                    await page.GetByRole(AriaRole.Heading, new() { Name = "EMA cross [a11y]" })
                        .WaitForAsync(new() { State = WaitForSelectorState.Visible });
                }, output);

            Assert.True(report is null, report);
        }
        finally
        {
            A11ySeed.SetEmaBasket(app.Services, previous);
        }
    }
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test CRV.Web.A11yTests --filter "FullyQualifiedName~ValidationEmaStudy"`
Expected: FAIL — the heading "EMA cross [a11y]" never appears (timeout), because the page has no `ema` study.

- [ ] **Step 4: Page model**

In `CRV.Web/Pages/Validation/Index.cshtml.cs`, add after `public AblationStudy?    Ablation { get; private set; }`:

```csharp
    public ValidationRunner.EmaStudyReport? EmaStudies { get; private set; }
```

In the `switch (Study)` of `OnGetAsync`, after the `"ablation"` case:

```csharp
                case "ema":
                    EmaStudies = await _runner.EmaStudiesAsync(cfg, btCfg,
                        inSampleFraction: 0.70, embargo: TimeSpan.FromDays(1), ct: ct);
                    break;
```

And after `VerdictLabel`:

```csharp
    /// <summary>An embargo as people say it: "2-day", "10-minute".</summary>
    public static string Gap(TimeSpan t) => t.TotalDays >= 1
        ? $"{t.TotalDays:0.#}-day"
        : $"{t.TotalMinutes:0}-minute";
```

Update the class summary's first sentence to: `The quant-validation studies: does the result survive out of sample, is the ORB duration a stable choice, does any filter beat the bare break, and does each EMA setup and confirmation hold up.`

- [ ] **Step 5: Page markup**

In `CRV.Web/Pages/Validation/Index.cshtml`:

Line 10, add a helper below `R3`:

```cshtml
    string R3(decimal v) => v.ToString("+0.000;−0.000;0.000") + "R";
    string Tone(decimal v) => v > 0 ? "c-up" : v < 0 ? "c-down" : "";
```

Line 24, after the "Filters" button:

```cshtml
    <button name="Study" value="ema" class="btn btn-sm @(Model.Study == "ema" ? "btn-warning" : "btn-outline-secondary")">EMA strategies</button>
```

Before the closing `</div>` of `c-stack` (after the filter-ablation block, line 274), add:

```cshtml
@* ── EMA strategies: the bare signal, then each confirmation on its own ── *@
@if (Model.EmaStudies is { } ema)
{
    @if (ema.Setups.Count == 0)
    {
        <section class="c-panel">
            <div class="c-panel-h"><h2>EMA strategies</h2></div>
            <div class="c-panel-b"><p class="c-plain">No EMA strategy is switched on, so there is nothing to study.</p></div>
        </section>
    }
    @foreach (var s in ema.Setups)
    {
        var oosThin = s.Split.OutOfSampleEdge.Verdict == EdgeVerdict.InsufficientEvidence;
        <section class="c-panel">
            <div class="c-panel-h">
                <h2>@s.Label</h2>
                @if (s.Split.FailedOutOfSample) { <span class="c-badge bad">FAILED</span> }
                else if (oosThin) { <span class="c-badge warn">TOO FEW TRADES</span> }
                else { <span class="c-badge ok">HELD UP</span> }
            </div>
            <div class="c-panel-b">
                <p class="c-plain">@s.Ticker · @s.Timeframe signal bars · first with no confirmations, then with each confirmation on its own.</p>
                <p class="c-mut small">
                    @ema.From.ToString("yyyy-MM-dd") to @ema.To.ToString("yyyy-MM-dd") · bars from @ema.BarSource ·
                    fills @ema.FillMode (@ema.SlippageTicks tick on entries, @ema.StopSlippageTicks on stops) ·
                    commission $@ema.CommissionPerSide.ToString("0.00") a side ·
                    70/30 split by date with a @V.Gap(s.Embargo) gap.
                    One backtest is a measurement, not proof the strategy makes money.
                </p>
            </div>
            <div class="c-table-wrap">
                <table class="c-table">
                    <thead><tr>
                        <th>No confirmations</th><th class="r">Trades</th>
                        <th class="r" title="Signals the risk budget refused at even one contract.">Refused</th>
                        <th class="r">Win rate</th><th class="r">Mean R</th><th class="r">95% range</th>
                        <th class="r">Profit factor</th><th class="r">Max drawdown</th><th>Verdict</th>
                    </tr></thead>
                    <tbody>
                    @foreach (var (label, side) in new[] { ("In sample", s.InSample), ("Out of sample", s.OutOfSample) })
                    {
                        <tr>
                            <td class="t">@label</td>
                            <td class="r">@side.Edge.Count</td>
                            <td class="r @(side.Refused > 0 ? "c-acc" : "c-mut")">@side.Refused</td>
                            <td class="r">@(side.Edge.Count > 0 ? side.Metrics.WinRate.ToString("0") + "%" : "—")</td>
                            <td class="r">@R3(side.Edge.MeanR)</td>
                            <td class="r">@(side.Edge.Count > 1 ? $"{R3(side.Edge.LowerBound)} to {R3(side.Edge.UpperBound)}" : "—")</td>
                            <td class="r">@(side.Metrics.ProfitFactor > 0 ? side.Metrics.ProfitFactor.ToString("0.00") : "—")</td>
                            <td class="r">$@side.Metrics.MaxDrawdown.ToString("N0")</td>
                            <td><span class="c-badge @V.VerdictClass(side.Edge.Verdict)">@V.VerdictLabel(side.Edge.Verdict)</span></td>
                        </tr>
                    }
                    </tbody>
                </table>
            </div>
            @if (s.Confirmations.Count > 0)
            {
                <div class="c-table-wrap">
                    <table class="c-table">
                        <thead><tr>
                            <th>One confirmation on</th>
                            <th class="r">In-sample trades</th><th class="r">vs none</th><th>In sample</th>
                            <th class="r">Out-of-sample trades</th><th class="r">vs none</th><th>Out of sample</th>
                        </tr></thead>
                        <tbody>
                        @foreach (var c in s.Confirmations)
                        {
                            <tr>
                                <td class="t">@c.Name</td>
                                <td class="r">@c.InSample.WithFilter.Count</td>
                                <td class="r @Tone(c.InSample.Contribution)">@R3(c.InSample.Contribution)</td>
                                <td><span class="c-badge @V.VerdictClass(c.InSample.Verdict)">@V.AblationLabel(c.InSample.Verdict)</span></td>
                                <td class="r">@c.OutOfSample.WithFilter.Count</td>
                                <td class="r @Tone(c.OutOfSample.Contribution)">@R3(c.OutOfSample.Contribution)</td>
                                <td><span class="c-badge @V.VerdictClass(c.OutOfSample.Verdict)">@V.AblationLabel(c.OutOfSample.Verdict)</span></td>
                            </tr>
                        }
                        </tbody>
                    </table>
                </div>
                <div class="c-panel-b c-mut small">Each confirmation alone against no confirmations, split at the same date. Fewer than 20 trades on either side is "too few", not a verdict.</div>
            }
        </section>
    }
}
```

Lines 276–283 (empty state): change the condition and copy to

```cshtml
@if (Model.Split == null && Model.Surface == null && Model.Ablation == null && Model.EmaStudies == null && (Model.Error == null || !Model.HasSnapshot))
{
    <div class="c-panel c-empty">
        <i class="bi bi-clipboard-data"></i>
        <b>Pick a study</b>
        <span>Choose a date range and one of the studies above. Each replays saved bars, so run a backtest over the same dates first.</span>
    </div>
}
```

- [ ] **Step 6: Build and run the scan**

Run: `dotnet build CRV.Web`
Expected: Build succeeded, 0 errors.
Run: `dotnet test CRV.Web.A11yTests --filter "FullyQualifiedName~ValidationEmaStudy"`
Expected: PASS (4 tests: 2 themes × 2 viewports). Fix any violation in the markup (labels, contrast tokens) — never by disabling a rule.

- [ ] **Step 7: Run both suites**

Run: `dotnet test CRV.Core.Tests` — Expected: PASS, 0 failed.
Run: `dotnet test CRV.Web.A11yTests` — Expected: PASS, 0 failed (4 more than the baseline).

- [ ] **Step 8: Commit**

```bash
git add CRV.Web/Pages/Validation/Index.cshtml CRV.Web/Pages/Validation/Index.cshtml.cs \
  CRV.Web.A11yTests/A11yAppFixture.cs CRV.Web.A11yTests/A11ySeed.cs CRV.Web.A11yTests/PageScanTests.cs
git commit -m "feat(ema): EMA studies on the validation page"
```

---

### Task 11: Parity with TradingView on `/validation`, and docs

**Files:**
- Modify: `CRV.Web/Pages/Validation/Index.cshtml.cs`, `CRV.Web/Pages/Validation/Index.cshtml`
- Modify: `CRV.Web.A11yTests/A11ySeed.cs`, `CRV.Web.A11yTests/PageScanTests.cs`
- Modify: `docs/validation.md`, `README.md:177`, `.gitignore`

**Interfaces:**
- Consumes: `TradingViewExport.Read` (Task 5), `EmaParity.Run` (Task 7), `BarSourceComparison` (Task 8), `TradingDbContext.HtfBars` (plan 4).
- Produces: `IndexModel.Export`, `Parity`, `Sources`, `ParityNote`, `ParityExports`, `ExportFrom`, `ExportTo`, `ExportBarMinutes`, `ParityExportDirectory`; the `Study=parity` button and export picker; `A11ySeed.ParityExportName`, `A11ySeed.SeedParityExport(IServiceProvider, string)`.

- [ ] **Step 1: Write the failing accessibility scan**

In `CRV.Web.A11yTests/A11ySeed.cs`, add `using System.Text;` and:

```csharp
    public const string ParityExportName = "a11y-parity.csv";

    /// <summary>
    /// Writes a small TradingView export into the parity folder, with its EMA column three ticks
    /// above the strategy's, and stored NQ bars a tick higher than its bars, so both parity
    /// tables render.
    /// </summary>
    public static void SeedParityExport(IServiceProvider services, string dataDir)
    {
        var dir = Path.Combine(dataDir, "Data", "parity");
        Directory.CreateDirectory(dir);
        var start = new DateTime(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc);
        long startMinutes = new DateTimeOffset(start).ToUnixTimeSeconds() / 60;

        var csv = new StringBuilder("time,open,high,low,close,HTF Key,EMA A,EMA B,Signal,p_src,p_entry,p_dir,p_len,p_fast,p_slow," +
            "p_retest,p_tf,p_touch_ticks,p_touch_atr,p_rearm_atr,p_close_back,p_min_beyond_atr,p_min_sep_atr,p_hold," +
            "p_away_atr,p_window,p_max_entries,p_atr_len,p_start,p_tick\n");
        for (int i = 0; i < 36; i++)
        {
            long seconds = new DateTimeOffset(start.AddMinutes(5 * i)).ToUnixTimeSeconds();
            csv.Append($"{seconds},100,101,99,100,{seconds / 60},100.75,NaN,0,0,1,2,8,8,21,0,5,1,0.1,0.5,1,0,0,1,0.25,10,1,14,{startMinutes},0.25\n");
        }
        File.WriteAllText(Path.Combine(dir, ParityExportName), csv.ToString());

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        if (db.HtfBars.Any(r => r.Root == "NQ" && r.Timeframe == SignalTimeframe.M5)) return;
        db.HtfBars.AddRange(Enumerable.Range(0, 36).Select(i => new HtfBarRow
        {
            Root = "NQ", Timeframe = SignalTimeframe.M5, OpenTime = start.AddMinutes(5 * i),
            Open = 100m, High = 101.25m, Low = 99m, Close = 100m, Volume = 1000,
        }));
        db.SaveChanges();
    }
```

In `CRV.Web.A11yTests/PageScanTests.cs`:

```csharp
    [Theory]
    [MemberData(nameof(Variants))]
    public async Task ValidationParity_HasNoWcagViolations(string theme, int width, int height)
    {
        A11ySeed.SeedParityExport(app.Services, app.DataDir);

        var report = await PageScanner.ScanAsync(app, $"/validation?Study=parity&Export={A11ySeed.ParityExportName}",
            theme, width, height, async page =>
            {
                await page.GetByRole(AriaRole.Heading, new() { Name = "Signal parity" })
                    .WaitForAsync(new() { State = WaitForSelectorState.Visible });
            }, output);

        Assert.True(report is null, report);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test CRV.Web.A11yTests --filter "FullyQualifiedName~ValidationParity"`
Expected: FAIL — the "Signal parity" heading never appears (timeout).

- [ ] **Step 3: Page model**

In `CRV.Web/Pages/Validation/Index.cshtml.cs` add `using Microsoft.EntityFrameworkCore;`, then these members after `EmaStudies`:

```csharp
    [BindProperty(SupportsGet = true)] public string Export { get; set; } = "";

    public EmaParityReport?      Parity           { get; private set; }
    public BarSourceReport?      Sources          { get; private set; }
    public string?               ParityNote       { get; private set; }
    public IReadOnlyList<string> ParityExports    { get; private set; } = [];
    public DateTime              ExportFrom       { get; private set; }
    public DateTime              ExportTo         { get; private set; }
    public int                   ExportBarMinutes { get; private set; }

    /// <summary>
    /// Where TradingView exports for the parity study go: <c>Data/parity</c> under the working
    /// directory, which is DATA_DIR when set, beside the database whose stored bars they are
    /// compared with.
    /// </summary>
    public static string ParityExportDirectory => Path.GetFullPath(Path.Combine("Data", "parity"));

    // The TradingView reference is CME_MINI:NQ1!, so its bars are compared with the NQ root's history.
    private const string ParityRoot = "NQ";
```

At the start of `OnGetAsync`:

```csharp
        ParityExports = ListParityExports();
```

Directly after `if (string.IsNullOrEmpty(Study)) return;`:

```csharp
        // Parity reads a TradingView export, not a bar snapshot.
        if (Study == "parity")
        {
            await RunParityAsync(ct);
            return;
        }
```

New private methods:

```csharp
    private static IReadOnlyList<string> ListParityExports() =>
        Directory.Exists(ParityExportDirectory)
            ? Directory.EnumerateFiles(ParityExportDirectory, "*.csv")
                .Select(Path.GetFileName).OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// Logic parity (the strategy against the Pine script on the same exported bars) and data
    /// parity (those bars against Schwab's stored ones). Only a file listed in the parity folder
    /// is opened, so the query string cannot point the page anywhere else on disk.
    /// </summary>
    private async Task RunParityAsync(CancellationToken ct)
    {
        if (!ParityExports.Contains(Export, StringComparer.Ordinal))
        {
            Error = ParityExports.Count == 0
                ? $"No TradingView exports in {ParityExportDirectory}. Export the chart with the CRV EMA parity script on it and save the CSV there."
                : "Pick a TradingView export first.";
            return;
        }

        try
        {
            TradingViewExport export;
            using (var reader = new StreamReader(Path.Combine(ParityExportDirectory, Export)))
                export = TradingViewExport.Read(reader);

            if (export.Bars.Count == 0)
            {
                Error = "The export has no bars.";
                return;
            }
            ExportFrom       = export.Bars[0].Time;
            ExportTo         = export.Bars[^1].Time;
            ExportBarMinutes = export.BarMinutes;

            if (export.Parameters is null)
                ParityNote = "This export has no Pine parity columns, so only the bar comparison ran.";
            else
                Parity = EmaParity.Run(export);

            var timeframe = BarSourceComparison.StoredTimeframeFor(export.BarMinutes);
            var rows = await _db.HtfBars
                .Where(r => r.Root == ParityRoot && r.Timeframe == timeframe && r.OpenTime >= ExportFrom && r.OpenTime <= ExportTo)
                .OrderBy(r => r.OpenTime)
                .ToListAsync(ct);
            var schwab = rows.Select(r => new Bar(DateTime.SpecifyKind(r.OpenTime, DateTimeKind.Utc),
                r.Open, r.High, r.Low, r.Close, r.Volume)).ToList();
            Sources = BarSourceComparison.Compare(export.Bars, schwab, export.Parameters?.TickSize ?? 0.25m);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException)
        {
            _log.LogWarning(ex, "Parity study on {Export} failed", Export);
            Error = ex.Message;
        }
    }
```

- [ ] **Step 4: Page markup**

In `CRV.Web/Pages/Validation/Index.cshtml`:

After the "EMA strategies" button (Task 10), inside the same form:

```cshtml
    <select name="Export" class="form-select form-select-sm c-select" aria-label="TradingView export">
        @if (Model.ParityExports.Count == 0)
        {
            <option value="">No exports in Data/parity</option>
        }
        @foreach (var name in Model.ParityExports)
        {
            <option value="@name" selected="@(name == Model.Export)">@name</option>
        }
    </select>
    <button name="Study" value="parity" class="btn btn-sm @(Model.Study == "parity" ? "btn-warning" : "btn-outline-secondary")">Parity with TradingView</button>
```

Line 39, so a parity error shows without a bar snapshot:

```cshtml
@if (Model.Error != null && (Model.HasSnapshot || Model.Study == "parity"))
```

After the EMA studies block (Task 10), still inside `c-stack`:

```cshtml
@* ── Parity with TradingView ─────────────────────────────────────── *@
@if (Model.ExportBarMinutes > 0)
{
    <p class="c-mut small">@Model.Export · CME_MINI:NQ1! (TradingView export) · @Model.ExportBarMinutes-minute bars · @Model.ExportFrom.ToString("yyyy-MM-dd HH:mm") to @Model.ExportTo.ToString("yyyy-MM-dd HH:mm") UTC</p>
}
@if (Model.ParityNote is { } parityNote)
{
    <div class="c-note"><i class="bi bi-info-circle"></i><span>@parityNote</span></div>
}
@if (Model.Parity is { } parity)
{
    <section class="c-panel">
        <div class="c-panel-h">
            <h2>Signal parity</h2>
            @if (parity.Passed) { <span class="c-badge ok">MATCHES</span> }
            else if (parity.Compared == 0) { <span class="c-badge warn">NOTHING COMPARED</span> }
            else { <span class="c-badge bad">@parity.Mismatches.Count DIFFERENCES</span> }
        </div>
        <div class="c-panel-b">
            <p class="c-plain">
                @if (parity.Compared == 0)
                {
                    <text>No signal bars were left after the @parity.WarmupRows-bar warmup. Export a longer window.</text>
                }
                else
                {
                    <text>The strategy on the exported bars against the Pine script on the same bars: @parity.Compared @parity.Timeframe bars after a @parity.WarmupRows-bar warmup. Pine signalled @parity.PineSignals times and @parity.MatchedSignals matched. The largest EMA gap was @parity.MaxEmaDiffTicks.ToString("0.##") ticks; a match allows 1.</text>
                }
            </p>
            <p class="c-mut small">Both sides read the same bars, so a difference here is in the logic, not the data.</p>
        </div>
        @if (parity.Mismatches.Count > 0)
        {
            <div class="c-table-wrap">
                <table class="c-table">
                    <thead><tr><th>Signal bar</th><th>Difference</th></tr></thead>
                    <tbody>
                    @foreach (var m in parity.Mismatches.Take(50))
                    {
                        <tr><td class="t">@m.Bar</td><td>@m.Problem</td></tr>
                    }
                    </tbody>
                </table>
            </div>
            @if (parity.Mismatches.Count > 50)
            {
                <div class="c-panel-b c-mut small">Showing the first 50 of @parity.Mismatches.Count.</div>
            }
        }
    </section>
}
@if (Model.Sources is { } src)
{
    <section class="c-panel">
        <div class="c-panel-h"><h2>TradingView bars against Schwab's</h2></div>
        <div class="c-panel-b">
            <p class="c-plain">
                @src.Matched of @src.ReferenceBars bars are in both feeds; @src.MissingInOther.Count are missing from Schwab's stored bars and @src.ExtraInOther.Count are only in Schwab's.
                @src.Differences.Count differ, by at most @src.LargestTicks.ToString("0.##") ticks, and Schwab's close sits @src.MeanCloseTicks.ToString("+0.##;−0.##;0") ticks from TradingView's on average.
            </p>
            <p class="c-mut small">Not a pass or a fail: this is how far live signals, built on Schwab's bars, can drift from TradingView's. A steady offset in the close points at a back-adjusted series.</p>
        </div>
        @if (src.Differences.Count > 0)
        {
            <div class="c-table-wrap">
                <table class="c-table">
                    <thead><tr>
                        <th>Bar (UTC)</th><th class="r">Open</th><th class="r">High</th><th class="r">Low</th><th class="r">Close</th>
                    </tr></thead>
                    <tbody>
                    @foreach (var d in src.Differences.Take(50))
                    {
                        <tr>
                            <td class="t">@d.OpenUtc.ToString("yyyy-MM-dd HH:mm")</td>
                            <td class="r">@d.OpenTicks.ToString("+0.##;−0.##;0")</td>
                            <td class="r">@d.HighTicks.ToString("+0.##;−0.##;0")</td>
                            <td class="r">@d.LowTicks.ToString("+0.##;−0.##;0")</td>
                            <td class="r">@d.CloseTicks.ToString("+0.##;−0.##;0")</td>
                        </tr>
                    }
                    </tbody>
                </table>
            </div>
            <div class="c-panel-b c-mut small">Ticks, Schwab minus TradingView.@(src.Differences.Count > 50 ? $" Showing the first 50 of {src.Differences.Count}." : "")</div>
        }
        @if (src.MissingInOther.Count > 0)
        {
            <div class="c-panel-b small">
                <b>Missing from Schwab:</b>
                @string.Join(", ", src.MissingInOther.Take(20).Select(t => t.ToString("yyyy-MM-dd HH:mm")))@(src.MissingInOther.Count > 20 ? $" and {src.MissingInOther.Count - 20} more" : "") (UTC)
            </div>
        }
    </section>
}
```

Empty-state condition: add `&& Model.Parity == null && Model.Sources == null && Model.ParityNote == null` to the condition from Task 10, and change `(Model.Error == null || !Model.HasSnapshot)` to `(Model.Error == null || (!Model.HasSnapshot && Model.Study != "parity"))`.

- [ ] **Step 5: Build and run the scan**

Run: `dotnet build CRV.Web`
Expected: Build succeeded, 0 errors.
Run: `dotnet test CRV.Web.A11yTests --filter "FullyQualifiedName~ValidationParity"`
Expected: PASS (4 tests).

- [ ] **Step 6: Ignore local exports, document the studies**

`.gitignore`, under `# Data files`:

```
Data/parity/
```

`README.md:177`, the Validation row:

```markdown
| [Validation](docs/validation.md) | Confidence intervals and edge verdicts, in/out-of-sample splits, parameter-stability sweeps, filter ablation, EMA studies and TradingView parity |
```

`docs/validation.md`, insert before `## Reading a study honestly`:

```markdown
## EMA strategies

`ValidationRunner.EmaStudiesAsync` takes each enabled EMA setup on its own (every other
entry switched off, the basket's bar snapshot replayed) and runs it twice over:

1. **No confirmations.** Split 70/30 by date. The embargo is the larger of one day and two
   signal bars, so a position opened on the last in-sample signal bar cannot leak across.
2. **One confirmation at a time.** Each confirmation that applies to the entry is switched on
   alone, split at the same date, and compared with step 1 on both sides as an ablation.

Each side reports trades, refusals, win rate, mean R with its 95 % interval, profit factor
and max drawdown. Fewer than 20 trades is "too few trades" — expected for W1 and MN1 — and
is shown, not hidden. Every panel states the date range, instrument, bar source and the fill
and commission assumptions. One backtest is a measurement, not proof a setup makes money.

## Parity with TradingView

Two separate comparisons, because Schwab's and TradingView's NQ feeds can differ by a tick
here and there:

- **Logic parity.** `docs/pine/ema-strategy-parity.pine` runs every EMA rule on a
  CME_MINI:NQ1! chart. Its "Export chart data" CSV carries the chart's bars, Pine's EMA and
  signal for every signal bar, and the script's inputs. `EmaParity.Run` rebuilds the setup
  from those inputs, runs the real `EmaStrategy` on the same bars (every entry closed at once,
  so position state never hides a signal) and compares. Pass: after the warmup (3 × the
  slowest EMA, in signal bars), every signal matches and every EMA is within one tick. Both
  sides seed their EMAs at the script's "Parity start". Covers M5 to D1; W1 and MN1 are built
  from D1 and are not compared directly.
- **Data parity.** The same export's bars against the `HtfBars` rows Schwab filled for NQ at
  that bar size (5, 15, 30 or 60 minutes): per-bar differences in ticks, missing and extra
  bars, and the mean close offset (a steady offset means a back-adjusted series). Reported,
  never passed or failed.

Save exports to `Data/parity/` under the app's data directory and pick one on `/validation`
under **Parity with TradingView**.
```

- [ ] **Step 7: Run both suites**

Run: `dotnet test CRV.Core.Tests` — Expected: PASS, 0 failed.
Run: `dotnet test CRV.Web.A11yTests` — Expected: PASS, 0 failed (8 more than the baseline).

- [ ] **Step 8: Commit**

```bash
git add CRV.Web/Pages/Validation/Index.cshtml CRV.Web/Pages/Validation/Index.cshtml.cs \
  CRV.Web.A11yTests/A11ySeed.cs CRV.Web.A11yTests/PageScanTests.cs \
  docs/validation.md README.md .gitignore
git commit -m "feat(ema): logic and data parity with TradingView on the validation page"
```

---

## MANUAL STEPS — Cirino, in TradingView

Claude cannot do these. Each blocks only what it names.

- [ ] **M1 (blocks reading data parity): prerequisites from htf-bars-and-history.** The daily-alignment check and the adjustment spot-check (a 2010 daily close, Schwab against TradingView NQ1! unadjusted) are done and recorded. The Schwab backfill has filled NQ H1 and M5 history across the windows below.

- [ ] **M2 (blocks M3–M6): load the script.**
  1. Open a CME_MINI:NQ1! chart. Back-adjustment **off** (the chart's "B-ADJ" / "Back-adjustment" toggle for continuous futures). Session: electronic trading hours. Timeframe **60**.
  2. Pine Editor → new indicator → replace everything with `docs/pine/ema-strategy-parity.pine` → Save as "CRV EMA parity" → Add to chart.
  3. Expected: it compiles with no errors; an orange EMA line and green/red triangles appear. If it doesn't compile, send Claude the compiler message word for word.

- [ ] **M3 (blocks M4): spot-check where the rows land.** Inputs: Signal timeframe 240, Parity start 2026-01-04 18:00 (New York), ATR length 14. Open the Data Window and hover: `HTF Key`, `EMA A` and `Signal` should have values only on the **last 60-minute bar of each 4-hour bar** (for the 10:00 H4 bar, the 13:00 bar) and be empty on the others. If they appear on the **first** bar of the next 4-hour bar instead, or change from bar to bar inside one 4-hour bar, stop and tell Claude: Decision 4's reading of `lookahead_off` is wrong for this chart.

- [ ] **M4: export the parity windows.** Scroll left until the chart shows bars back to the parity start (an export only includes loaded bars). For each row, set the inputs (everything not listed stays at its default; ATR length 14), then chart menu → **Export chart data…** → Time format **UNIX timestamp** → save into `Data/parity/` under the folder the app runs from (with `dotnet run --project CRV.Web/CRV.Web.csproj` from the repo root, that is `<repo>/Data/parity/`) with the file name shown. Check the first export has columns `HTF Key … p_tick`; if they are missing, tell Claude (the script's Data Window plots would need `display.all`).

  | # | Chart | File | What crosses | Entry | Direction | EMA | Signal TF | Parity start (New York) |
  |---|---|---|---|---|---|---|---|---|
  | 1 | 60 | `nq1-60-h4-touch-both-21.csv` | PriceVsEma | Touch | Both | 21 | 240 | 2026-01-04 18:00 |
  | 2 | 60 | `nq1-60-h4-cross-both-21.csv` | PriceVsEma | Cross | Both | 21 | 240 | 2026-01-04 18:00 |
  | 3 | 60 | `nq1-60-h4-retest-both-21.csv` | PriceVsEma | CrossRetest | Both | 21 | 240 | 2026-01-04 18:00 |
  | 4 | 60 | `nq1-60-h4-emacross-both-8-21.csv` | EmaVsEma | Cross | Both | 8 / 21 | 240 | 2026-01-04 18:00 |
  | 5 | 60 | `nq1-60-h4-emaretest-both-8-21.csv` | EmaVsEma | CrossRetest (retest Fast) | Both | 8 / 21 | 240 | 2026-01-04 18:00 |
  | 6 | 60 | `nq1-60-h4-touch-up-21.csv` | PriceVsEma | Touch | Up | 21 | 240 | 2026-01-04 18:00 |
  | 7 | 60 | `nq1-60-h4-retest-down-21.csv` | PriceVsEma | CrossRetest | Down | 21 | 240 | 2026-01-04 18:00 |
  | 8 | 60 | `nq1-60-h4-emacross-both-50-200.csv` | EmaVsEma | Cross | Both | 50 / 200 | 240 | 2026-01-04 18:00 |
  | 9 | 60 | `nq1-60-h8-cross-both-21.csv` | PriceVsEma | Cross | Both | 21 | 480 | 2026-01-04 18:00 |
  | 10 | 60 | `nq1-60-d-cross-both-21.csv` | PriceVsEma | Cross | Both | 21 | D | 2026-01-04 18:00 |
  | 11 | 5 | `nq1-5-m15-retest-both-21.csv` | PriceVsEma | CrossRetest | Both | 21 | 15 | the earliest Sunday 18:00 the 5-minute chart loads |

- [ ] **M5 (blocks Task 12): the short fixture.** One more export for the test suite: chart 60, PriceVsEma, CrossRetest, Both, EMA 8, signal timeframe 240, Parity start **2026-08-02 18:00**, bars from then to **2026-09-25** (about 1,000 rows). Save it as `tv-nq1-60-h4-retest-both-8.csv` and hand it to Claude.

- [ ] **M6 (after Task 11 runs locally): read the results.** Start the app yourself, open `/validation`, pick each export, press **Parity with TradingView**, and note per file: MATCHES / differences, and the bar comparison line. Paste them into the PR. Differences are investigated in Task 12 Step 4's way; do not change the tolerance.

---

### Task 12: Parity on a real TradingView export (after M5)

**Files:**
- Create: `CRV.Core.Tests/Backtest/Fixtures/tv-nq1-60-h4-retest-both-8.csv` (Cirino's file from M5, unchanged)
- Modify: `CRV.Core.Tests/CRV.Core.Tests.csproj`
- Test: `CRV.Core.Tests/Backtest/EmaParityRealExportTests.cs`

**Interfaces:**
- Consumes: `TradingViewExport.Read`, `EmaParity.Run`.
- Produces: spec test 4.

- [ ] **Step 1: Check in the fixture and copy it to the test output**

Copy Cirino's file to `CRV.Core.Tests/Backtest/Fixtures/tv-nq1-60-h4-retest-both-8.csv` byte for byte. Read it once: it holds only prices, the Pine columns and the script's inputs — no account data. In `CRV.Core.Tests/CRV.Core.Tests.csproj`, in the `ItemGroup` with `spy_chain_fixture.json`:

```xml
    <None Update="Backtest\Fixtures\*.csv" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 2: Write the test**

`CRV.Core.Tests/Backtest/EmaParityRealExportTests.cs`:

```csharp
using CRV.Backtest.DataLoaders;
using CRV.Backtest.Experiments;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// Spec test 4: the strategy against the Pine reference on a real CME_MINI:NQ1! export
/// (60-minute bars, H4, price vs EMA 8, cross + retest, both directions, 2026-08-02 → 2026-09-25).
/// </summary>
public class EmaParityRealExportTests
{
    private static readonly string Fixture =
        Path.Combine(AppContext.BaseDirectory, "Backtest", "Fixtures", "tv-nq1-60-h4-retest-both-8.csv");

    [Fact]
    public void Run_ARealTradingViewExport_MatchesPine()
    {
        using var reader = new StreamReader(Fixture);

        var report = EmaParity.Run(TradingViewExport.Read(reader));

        Assert.True(report.Passed, report.Describe());
        Assert.True(report.PineSignals > 0, "A window with no signals proves only that the EMAs match.");
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaParityRealExportTests"`
Expected: PASS.

- [ ] **Step 4: If it fails — find the root cause, one change at a time**

Use superpowers:systematic-debugging. Read `report.Describe()`:
- **EMA off on every bar from the start:** the seeds differ — check the first C# signal bar is the one opening at the Parity start (`EmaSignalTrace` gets bars from `StartUtc`) and that `SessionBarAggregator` buckets that bar as TradingView does.
- **EMA off from one bar onwards:** a signal bar was built differently (holiday, early close, DST). Name the bar, compare its OHLC in the export with the C# `HtfBar`; the fix belongs in `SessionBarAggregator` (plan 4) with a failing test there first.
- **EMAs match, signals differ:** a rule is read differently. Find the bullet in Decision 6 or the ema-strategy spec that covers it; the side that departs from the spec is wrong. Fix C# with a failing `EmaStrategy` test first, or fix the Pine script and have Cirino re-export. If the spec is silent, stop and ask Cirino.

Never widen the one-tick tolerance or shorten the warmup to make it pass.

- [ ] **Step 5: Commit**

```bash
git add CRV.Core.Tests/Backtest/Fixtures/tv-nq1-60-h4-retest-both-8.csv CRV.Core.Tests/CRV.Core.Tests.csproj \
  CRV.Core.Tests/Backtest/EmaParityRealExportTests.cs
git commit -m "test(ema): logic parity on a real TradingView NQ1! export"
```

---

### Task 13: Verify, review, open the PR

- [ ] **Step 1: Full suites**

Run: `dotnet build` — Expected: Build succeeded, 0 errors.
Run: `dotnet test CRV.Core.Tests` — Expected: PASS, 0 failed.
Run: `dotnet test CRV.Web.A11yTests` — Expected: PASS, 0 failed.

- [ ] **Step 2: Security review**

Run the `security-review` skill on the branch. Pay attention to `RunParityAsync` (only listed file names are opened) and to the checked-in CSV fixture (prices only). Fix findings, or record in the PR why one is left.

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin feat/ema-parity-and-validation
gh pr create --base master --title "feat(ema): parity with TradingView and EMA validation studies" --body-file <(cat <<'EOF'
## What
- `docs/pine/ema-strategy-parity.pine`: Pine v6 reference for every source × entry × direction, exporting EMA, signal and inputs per signal bar.
- `TradingViewExport`, `EmaSignalTrace`, `EmaParity`: logic parity of the real `EmaStrategy` against Pine on the same exported bars (±1 tick after a 3× warmup).
- `BarSourceComparison`: Schwab's stored NQ bars against the export, reported not judged.
- `ValidationRunner.EmaStudiesAsync`: each EMA setup alone, no confirmations then one at a time, 70/30 by date with an embargo.
- `/validation`: "EMA strategies" and "Parity with TradingView".
- Fix: `SampleSplit.ByFraction` threw on a single trade.

## Parity results (M6)
| Export | Logic parity | Bars vs Schwab |
|---|---|---|
| one row per file from M6 |

## Not covered
W1 and MN1 are not compared with Pine directly (built from D1, which is).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)
```

- [ ] **Step 4: Confirm CI is green**

Run: `gh pr checks --watch`
Expected: `Build & Test (.NET 10)` and `Accessibility (WCAG 2.2 A/AA)` both pass.

---

## Self-Review

**Spec coverage**

| Spec requirement | Task |
|---|---|
| Pine v6 script, every Source × Entry × Direction, `barstate.isconfirmed`, `request.security(lookahead_off)`, "240"/"480", markers, exportable values | 3 |
| Logic parity on a TradingView NQ1! export: signals match, EMA ±1 tick after warmup | 4, 5, 6, 7 (Decision 2 on the import path) |
| Data parity: per-bar OHLC differences, missing / extra bars, reported not pass/fail | 8, 11 |
| Prerequisites from htf-bars-and-history | M1 |
| `SampleSplit` with embargo, `EdgeTest` (20), `Ablation` | 9 |
| Baseline (no confirmations) and one confirmation at a time | 9 |
| Win rate, expectancy R with 95 % interval, profit factor, max drawdown, trade count; InsufficientEvidence shown | 9, 10 |
| Date range, instrument, bar source, fill and commission stated; no single backtest as proof | 9, 10 |
| Results on `/validation`, no report file | 10, 11 |
| What Cirino does: export bars, run Pine and export, adjustment spot-check | M1–M6 |
| Tests 1–4 | 4, 8, 9, 12 |

**Placeholder scan:** no TBD/TODO; every code step has full code. The PR body's results table is filled from M6 by design.

**Type consistency:** `EmaTraceRow(Key, EmaA, EmaB, Signal)` used identically in Tasks 4–7; `PineParityParameters` positional order matches in Task 5 (definition), Task 6 (`Params`) and the reader; `EmaParity.ParitySetup(p, barMinutes)`, `EmaSignalTrace.Run(setup, bars)`, `EmaParity.Run(export)`, `BarSourceComparison.Compare(reference, other, tickSize)` / `StoredTimeframeFor(int)`, `ValidationRunner.EmaStudiesAsync` / `IsolateEmaSetup` / `EmbargoFor` and the `EmaStudyReport` record members match their uses in Tasks 9–11. `EmaConfirmationSwitches.For(EmaEntry)` returns `(Name, Enable)` everywhere.

**Review Focus:** each of the five lines has its test in the owning task (4, 5, 8, 1, 9).
