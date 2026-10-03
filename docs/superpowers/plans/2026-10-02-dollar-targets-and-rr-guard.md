# Dollar Targets and R:R Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Pullback, Retest, OrbFakeout and SessionFakeout set their target in dollars (per contract or for the whole position), and make the minimum reward / risk rule an explicit, visible guard: checked on save, applied per trade as Skip or Raise target, switchable off.

**Architecture:** `StrategySetupConfig` gains five fields. `LevelCalculator` gains one request record (`LevelRequest`) and one entry point (`Targets`) that every strategy calls **after** sizing, so a whole-position dollar target can spread over the sized contract count. A small `MinRrGuard` turns the result into "take it", "take it with a raised target" or "skip it"; a skip travels the existing size-refusal path (`SizeRefusal` gets a `Reason`) so it reaches the log, the alert feed, backtest results and the cockpit card. The setup page checks the target against `MinRr` × the strategy's typical stop (`TypicalStop`, the median of its last 30 backtest trades) through `MinRrSaveCheck`.

**Tech Stack:** .NET 10, C# (nullable, file-scoped namespaces), xUnit 2.9.3, EF Core 10 on SQLite (JSON1 functions), ASP.NET Core Razor Pages, Playwright + axe (CRV.Web.A11yTests).

**Spec:** `docs/superpowers/specs/2026-10-02-dollar-targets-and-rr-guard-design.md`

**Requires:** nothing. This plan is built against today's `master` (d61b53c + the docs commit a709e4d). It does not use any name produced by plans 1–2. If plan 1 (ema21-removal) is merged first, locate edits by the quoted code, not by line number: line numbers below are for today's `master`.

## Global Constraints

- Branch `feat/dollar-targets-rr-guard`; Conventional Commits (`feat(risk):`, `feat(strategy):`, `feat(ui):`, `test(a11y):`, `docs:`).
- New config fields and defaults exactly: `TargetMode TargetMode = RangePct`, `decimal TargetDollars = 0`, `TargetDollarsBasis TargetDollarsBasis = PerContract`, `bool EnforceMinRr = true`, `MinRrAction MinRrAction = Skip`. Enums `TargetMode { RangePct, Dollars, Atr, RiskMultiple }`, `TargetDollarsBasis { PerContract, WholePosition }`, `MinRrAction { Skip, RaiseTarget }`, declared beside `StrategySetupConfig`.
- Basket JSON stores enums as ints (no string converter on these fields).
- Distance in points: `PerContract` = `TargetDollars / PointValue`; `WholePosition` = `TargetDollars / (PointValue × TotalContracts)` with the count **after** auto-size; rounded to the tick. `PartialPct` is a percentage of that distance.
- Order in every strategy: entry (tick offset applied) → final stop (stop mode, capped) → size (`AutoSizeByRiskCalculator`, uses the stop only) → target + partial (`LevelCalculator.Targets`) → R from the final entry, stop and target → guard → signal.
- `RaiseTarget` moves the target out to `MinRr × risk` and recomputes the partial at `PartialPct` of the new distance.
- Guard off: no save check and no per-trade check; `MinRr` and `MinRrAction` stay saved and are shown greyed out as "Not enforced".
- Badge text "R:R not enforced": amber (`c-badge warn`) when the strategy is on, grey (`c-badge`) when it is off — on the setup page head, the Strategies row and the cockpit card.
- `StrategyText` adds "Takes trades below X R" when the guard is off; Strategies rows include the target ("target $400 / contract", "target $150 / position", "target 2R", "target 100% of range").
- No-history save warning, verbatim: "The reward / risk check runs once this strategy has a backtest."
- Tests never place, modify or cancel orders and never start the live engine: strategies are driven directly, backtests use `BacktestEngine`, browser tests use the A11y fixture (Mock broker, engine not started).
- Basket JSON is only written through `BasketCodec` / `StrategyBasketService`, except the migration, which uses SQLite `json_insert` (see Decision 6) and is pinned by a test.
- Comments say what and why, never history.
- Every new page state passes the WCAG gate (`dotnet test CRV.Web.A11yTests`).

## Decisions

Facts the spec leaves open, decided here. Flag any you disagree with before Task 4.

1. **Targets are measured from the fill (the entry after the tick offset).** The spec's order of operations puts the tick offset before `LevelCalculator`, and a dollar target is a P&L amount, so it has to be measured from the fill. Stops stay measured from the signal price, as today. **Behaviour change:** a `RangePct` setup with `EntryTickOffset ≠ 0` gets its target and partial `EntryTickOffset` ticks further out than today (the reward from the fill stays `TargetPct × range` instead of shrinking by the offset). Setups with offset 0 (the default) are unchanged. The Prospectus page is updated to match (Task 10).
2. **`RangePct` save check uses the stop setting, not history.** `TradeRecord` stores no range, so a history median can't be expressed in "% of the range". With `StopMode = OrbPct` the stop is exactly `StopPct × range`, so the minimum is `MinRr × StopPct × 100` %. With `BarHL` / `Vwap` the save passes with the warning "The save check can't compare a bar or VWAP stop with the range; each trade is still checked."
3. **`WholePosition` typical stop = median of `Contracts × stop points`** over the same 30 trades (`TypicalStop.MedianPosition`), so the save check follows real auto-sized counts. The "example contracts" input drives the preview only.
4. **`Atr` save check is deferred to plan 5** (it needs ATR history the EMA plan owns). Here an `Atr` target saves with the warning "The save check can't size an ATR target before the market sets the ATR; each trade is still checked." `RiskMultiple` is checked as `AtrTp2Mult ≥ MinRr` (no history needed). `Atr` / `RiskMultiple` use `AtrTp1Mult` / `AtrTp2Mult`; the partial uses `Tp1Mult` when it is above 0, else `PartialPct`.
5. **A min-R skip is a `SizeRefusal` with `Reason = MinRr`** (plus `Rr`, `MinRr`). `BacktestResult.SizeRefusals` and `PerformanceMetrics.SizeRefusals` stay size-only, so `/validation` refusal counts don't change meaning; skips go to new `MinRrSkips`. Each skip is reported once per signal (same `SizeRefusalGate` dedupe), and the gate keeps the last skip for the cockpit card until the next session reset.
6. **Migration `AddTargetModeAndRrGuard` writes the defaults with SQLite `json_insert`** into `Configs.BasketJson` entries whose `Config` is a JSON object, and only for keys that are absent. Entries without `Config`, lowercase `config` keys, non-object array items and malformed JSON are left untouched (C# defaults already give the same values). `json_set` was rejected: it creates a second `Config` key next to a lowercase `config`, which `BasketCodec`'s case-insensitive read would then take. `Ema21BasketJson` is not touched (plan 1 renames that column and plan 5 converts its entries; C# defaults apply meanwhile). Legacy A–D setups have no columns for these fields; `BuildSetupConfigA..D` get the property-initializer defaults, pinned by a test.
7. **Zero reward or zero risk always skips**, even with the guard off: a target at the entry (a $0 target, no point value, a whole-position target that rounds to 0 ticks) or a stop at the entry is never sent.
8. **History scan = the 20 most recent backtest runs** (`RunAt` descending), trades matched by `SetupLabel == setup id`, de-duplicated across overlapping runs by `(EnteredAt, Entry, InitialStop)`.
9. **`StrategyText` tests live in `CRV.Web.A11yTests`** as plain unit tests (no collection fixture, no browser): it is the only test project that references `CRV.Web`.
10. **The grey cockpit badge is not scanned.** Disabled cockpit cards are drawn at 45% opacity and are not in today's scanned snapshot; the grey badge is scanned on the Strategies page and the setup page instead.
11. **A trade that fails both the budget and the minimum R now reports a size refusal** (sizing runs first). Before, the min-R `return` came first and nothing was reported.
12. **Baseline:** `dotnet test CRV.Core.Tests` reports **959** passing on this branch today (the brief says 956).

## Review Focus

1. **Setups with `EntryTickOffset ≠ 0`** — their RangePct target moves (Decision 1). A person expects the target distance from the fill to equal what the page says. Pinned by `TickOffset_TargetAndPartialAreMeasuredFromTheFill` (Task 4).
2. **A trade with no reward or no risk** (`TargetDollars = 0`, `PointValue = 0`, a whole-position target that rounds to 0 ticks, a stop at entry) must never be sent with a target at the entry, guard on or off. Pinned by `MinRrGuardTests.NoReward_Skips_EvenWithGuardOff` and `NoRisk_Skips_EvenWithGuardOff` (Task 3).
3. **A skipped signal re-asked on every tick** must produce one alert, not one per tick. Pinned by `Gate_MinRrSkip_SameSignalAskedTwice_ReportsOnce` (Task 3).
4. **A stored basket the migration can't safely edit** (lowercase `config`, a non-object item, malformed JSON, an empty basket, an entry without `Config`) must keep every key and value it had, and never gain a second `Config`. Pinned by `AddTargetModeAndRrGuardTests` (Task 9).
5. **Saving with the guard off** posts no `MinRr` / `MinRrAction` (the inputs are disabled); the stored values must survive. Pinned by `SaveWithGuardOff_KeepsStoredMinimumAndAction` (Task 12).

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Modify | `CRV.Core/Models/StrategySetupConfig.cs` | Five fields; three enums |
| Modify | `CRV.Core/Models/StrategyConfig.cs:743` | `ToSetupConfig(BasketEntry)` maps the fields |
| Modify | `CRV.Core/Strategy/StrategyHelpers.cs:1-59` | `LevelRequest`, `LevelCalculator.Targets/RaiseToMinRr/RangeStop`, `CalcLevels(B)` as wrappers, `MinRrGuard`, `GuardedLevels` |
| Modify | `CRV.Core/Models/SizeRefusal.cs` | `RefusalReason`, `Rr`, `MinRr`, skip wording |
| Modify | `CRV.Core/Strategy/SizeRefusalGate.cs` | `ReportMinRr`, `LastMinRrSkip` |
| Modify | `CRV.Core/Strategy/ISetupStrategy.cs:48-67,144-149` | `SetupStateSnapshot.MinRrEnforced/MinRr/LastSkip`; doc |
| Modify | `CRV.Core/Strategy/{OrbFakeout,SessionFakeout,Pullback,Retest}Strategy.cs` | New order, guard, snapshot fields |
| Create | `CRV.Core/Strategy/TypicalStop.cs` | Median stop / position risk of the last 30 backtest trades; reads run JSON |
| Create | `CRV.Core/Strategy/MinRrSaveCheck.cs` | Save-time check and its wording |
| Modify | `CRV.Backtest/Results/BacktestResults.cs` | `MinRrSkips`, `RrGuardState` per setup |
| Create | `CRV.Core/Migrations/<ts>_AddTargetModeAndRrGuard.cs` (+ Designer) | Guard defaults on stored entries |
| Modify | `CRV.Web/Pages/Dashboard/Prospectus.cshtml.cs:206-232` | Target/partial distances through `LevelCalculator.Targets` |
| Modify | `CRV.Web/Pages/Setup/StrategyText.cs` | `Target`, `Guard`, `GuardOffNote`; `Describe` wording |
| Modify | `CRV.Web/Pages/Setup/Strategy.cshtml(.cs)` | Target mode, dollar preview, typical stop, guard fields, head badge, save check |
| Modify | `CRV.Web/Pages/Setup/Strategies.cshtml(.cs)` | Row badge, target in description, guard-off note |
| Modify | `CRV.Core/Models/Signals.cs:257-292` | `SetupSnapshot.MinRrEnforced/MinRr/LastSkip` |
| Modify | `CRV.Core/Strategy/SnapshotAggregator.cs:260-289` | Maps them |
| Modify | `CRV.Web/Pages/Dashboard/Index.cshtml:1033-1072,1468-1474` | Card badge and skip line |
| Modify | `CRV.Web/Pages/Shared/_ResultsView.cshtml:41-56,178` | Skips and guard state per setup |
| Modify | `CRV.Web/wwwroot/css/components.css` | `.st-row-title`, `.st-wide`, `.st-usd`, `.ck-rr`, `.ck-skip` |
| Modify | `CRV.Web.A11yTests/{A11ySeed,A11yPages,CockpitSnapshot,CockpitCardTests,FixtureTests}.cs` | New seeded states, routes, card test |
| Create | `CRV.Web.A11yTests/StrategyTextTests.cs`, `StrategyPageTests.cs` | Text and page behaviour |
| Modify | `docs/risk.md` | "Targets and the reward / risk guard" section |
| Test | `CRV.Core.Tests/Models/ConfigMappingTests.cs`, `Strategy/LevelCalculatorTests.cs`, `Strategy/MinRrGuardTests.cs` (new), `Strategy/{OrbFakeout,SessionFakeout,Pullback,Retest}StrategyTests.cs`, `Strategy/TypicalStopTests.cs` (new), `Strategy/MinRrSaveCheckTests.cs` (new), `Backtest/MinRrGuardReachesTheResultTests.cs` (new), `Data/AddTargetModeAndRrGuardTests.cs` (new), `Strategy/SnapshotAggregatorTests.cs` | |

---

### Task 0: Branch

- [ ] **Step 1: Create the branch and confirm the baseline**

```bash
git checkout master && git pull
git checkout -b feat/dollar-targets-rr-guard
dotnet test CRV.Core.Tests
```

Expected: `Passed!  - Failed: 0, Passed: 959`.

---

### Task 1: Target and guard fields on the setup config

**Files:**
- Modify: `CRV.Core/Models/StrategySetupConfig.cs:58` (after `StopVwapTicks`), end of file
- Modify: `CRV.Core/Models/StrategyConfig.cs:743`
- Test: `CRV.Core.Tests/Models/ConfigMappingTests.cs`

**Interfaces:**
- Produces: `enum TargetMode { RangePct, Dollars, Atr, RiskMultiple }`, `enum TargetDollarsBasis { PerContract, WholePosition }`, `enum MinRrAction { Skip, RaiseTarget }` (namespace `CRV.Core.Models`); `StrategySetupConfig.TargetMode`, `.TargetDollars`, `.TargetDollarsBasis`, `.EnforceMinRr`, `.MinRrAction`.

- [ ] **Step 1: Write the failing tests**

Append to `CRV.Core.Tests/Models/ConfigMappingTests.cs`, after `ToSetupConfigs_FromBasket_PropagatesBypassChopAndFakeoutSession`:

```csharp
    [Fact]
    public void ToSetupConfigs_FromBasket_PropagatesTargetModeAndRrGuard()
    {
        var basket = """
        [{
          "Id":"of-mnq","Enabled":true,"Label":"ORB fakeout","StrategyType":2,"Ticker":"/MNQZ26",
          "Config":{ "TargetMode":1, "TargetDollars":400, "TargetDollarsBasis":1, "EnforceMinRr":false, "MinRrAction":1 }
        }]
        """;

        var s = new StrategyConfig { BasketJson = basket }.ToSetupConfigs().Single(x => x.Id == "of-mnq");

        Assert.Equal(TargetMode.Dollars, s.TargetMode);
        Assert.Equal(400m, s.TargetDollars);
        Assert.Equal(TargetDollarsBasis.WholePosition, s.TargetDollarsBasis);
        Assert.False(s.EnforceMinRr);
        Assert.Equal(MinRrAction.RaiseTarget, s.MinRrAction);
    }

    [Fact]
    public void ToSetupConfigs_EntryWithoutTheNewKeys_IsRangePctWithTheGuardOnAndSkipping()
    {
        var basket = """[{ "Id":"pb-mnq","Enabled":true,"StrategyType":0,"Ticker":"/MNQZ26","Config":{ "TargetPct":80 } }]""";

        var s = new StrategyConfig { BasketJson = basket }.ToSetupConfigs().Single(x => x.Id == "pb-mnq");

        Assert.Equal(TargetMode.RangePct, s.TargetMode);
        Assert.Equal(0m, s.TargetDollars);
        Assert.Equal(TargetDollarsBasis.PerContract, s.TargetDollarsBasis);
        Assert.True(s.EnforceMinRr);
        Assert.Equal(MinRrAction.Skip, s.MinRrAction);
    }

    [Fact]
    public void LegacySetups_AreRangePctWithTheGuardOnAndSkipping()
    {
        foreach (var s in CustomConfig().ToSetupConfigs())
        {
            Assert.Equal(TargetMode.RangePct, s.TargetMode);
            Assert.True(s.EnforceMinRr);
            Assert.Equal(MinRrAction.Skip, s.MinRrAction);
        }
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~ConfigMappingTests"`
Expected: build FAILS with `The name 'TargetMode' does not exist in the current context`.

- [ ] **Step 3: Add the fields and enums**

In `CRV.Core/Models/StrategySetupConfig.cs`, after line 58 (`public int StopVwapTicks { get; set; } = 4;`):

```csharp

    // ── Target and the reward / risk guard ─────────────────────────
    /// <summary>How the target distance is set. RangePct = <see cref="TargetPct"/> of the range.
    /// Atr and RiskMultiple use <see cref="AtrTp1Mult"/> / <see cref="AtrTp2Mult"/>.</summary>
    public TargetMode TargetMode { get; set; } = TargetMode.RangePct;
    /// <summary>Target size in dollars when <see cref="TargetMode"/> is Dollars.</summary>
    public decimal TargetDollars { get; set; }
    /// <summary>Whether <see cref="TargetDollars"/> is per contract or for the whole position after sizing.</summary>
    public TargetDollarsBasis TargetDollarsBasis { get; set; } = TargetDollarsBasis.PerContract;
    /// <summary>When true, saving checks the target against <see cref="MinRr"/> × the typical stop,
    /// and every trade below <see cref="MinRr"/> is skipped or has its target raised.</summary>
    public bool EnforceMinRr { get; set; } = true;
    /// <summary>What a trade below <see cref="MinRr"/> does while <see cref="EnforceMinRr"/> is on.</summary>
    public MinRrAction MinRrAction { get; set; } = MinRrAction.Skip;
```

At the end of the file, after the class's closing brace:

```csharp

/// <summary>How a strategy measures its target.</summary>
public enum TargetMode { RangePct, Dollars, Atr, RiskMultiple }

/// <summary>What a dollar target is measured over.</summary>
public enum TargetDollarsBasis { PerContract, WholePosition }

/// <summary>What a trade whose reward / risk is below the minimum does.</summary>
public enum MinRrAction { Skip, RaiseTarget }
```

- [ ] **Step 4: Map them from a basket entry**

In `CRV.Core/Models/StrategyConfig.cs`, in `ToSetupConfig(BasketEntry b)`, after `MinRr = b.Config.MinRr,` (line 743):

```csharp
        TargetMode = b.Config.TargetMode,
        TargetDollars = b.Config.TargetDollars,
        TargetDollarsBasis = b.Config.TargetDollarsBasis,
        EnforceMinRr = b.Config.EnforceMinRr,
        MinRrAction = b.Config.MinRrAction,
```

`BuildSetupConfigA..D` are left as they are: the property initializers give them `RangePct`, guard on, `Skip` (pinned by `LegacySetups_AreRangePctWithTheGuardOnAndSkipping`).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~ConfigMappingTests"`
Expected: PASS, `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Models/StrategySetupConfig.cs CRV.Core/Models/StrategyConfig.cs CRV.Core.Tests/Models/ConfigMappingTests.cs
git commit -m "feat(risk): target mode, dollar target and min-R guard settings per strategy"
```

---

### Task 2: One level calculator for every target mode

**Files:**
- Modify: `CRV.Core/Strategy/StrategyHelpers.cs:1-59`
- Test: `CRV.Core.Tests/Strategy/LevelCalculatorTests.cs`

**Interfaces:**
- Consumes: Task 1 enums.
- Produces (namespace `CRV.Core.Strategy`):
  - `record LevelRequest(decimal Entry, bool IsLong, decimal Stop, int Contracts, TargetMode Mode, decimal RangeOrAtr, decimal TargetPct, decimal TargetDollars, TargetDollarsBasis Basis, decimal PointValue, decimal PartialPct, decimal TickSize, decimal Tp1Mult = 0, decimal Tp2Mult = 0)` with `static LevelRequest From(StrategySetupConfig cfg, decimal entry, bool isLong, decimal stop, int contracts, decimal rangeOrAtr)`.
  - `static (decimal Target, decimal Partial, decimal Rr) LevelCalculator.Targets(LevelRequest r)`
  - `static (decimal Target, decimal Partial) LevelCalculator.RaiseToMinRr(LevelRequest r, decimal minRr)` — target rounded **away** from the entry so R ≥ `minRr`.
  - `static decimal LevelCalculator.RangeStop(decimal entry, bool isLong, decimal range, decimal stopPct, decimal tickSize = 0m)`
  - `CalcLevels` / `CalcLevelsB` keep their signatures and results, now as wrappers.

- [ ] **Step 1: Write the failing tests**

Add `using CRV.Core.Models;` at the top of `CRV.Core.Tests/Strategy/LevelCalculatorTests.cs`, then append inside the class:

```csharp
    // ── Targets: every mode through one request ───────────────────

    private static LevelRequest Dollars(decimal dollars, TargetDollarsBasis basis, decimal pointValue,
        int contracts = 1, bool isLong = true, decimal partialPct = 50m) => new(
        Entry: 20000m, IsLong: isLong, Stop: isLong ? 19990m : 20010m, Contracts: contracts,
        Mode: TargetMode.Dollars, RangeOrAtr: 0m, TargetPct: 0m, TargetDollars: dollars, Basis: basis,
        PointValue: pointValue, PartialPct: partialPct, TickSize: 0.25m);

    [Fact]
    public void Dollars_PerContract_Mnq_Long()
    {
        // $400 a contract at $2 a point = 200 pts; partial at 50% = 100 pts; risk 10 pts.
        var (target, partial, rr) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 2m));

        Assert.Equal(20200m, target);
        Assert.Equal(20100m, partial);
        Assert.Equal(20m, rr);
    }

    [Fact]
    public void Dollars_PerContract_Mnq_Short()
    {
        var (target, partial, _) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 2m, isLong: false));

        Assert.Equal(19800m, target);
        Assert.Equal(19900m, partial);
    }

    [Fact]
    public void Dollars_PerContract_Nq_IsAPointPerTwentyDollars()
    {
        var (target, _, _) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 20m));

        Assert.Equal(20020m, target);
    }

    [Theory]
    [InlineData(1, 20200)]
    [InlineData(2, 20100)]
    [InlineData(4, 20050)]
    public void Dollars_WholePosition_SpreadsOverTheContracts(int contracts, int expectedTarget)
    {
        var (target, _, _) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.WholePosition, 2m, contracts));

        Assert.Equal((decimal)expectedTarget, target);
    }

    [Fact]
    public void Dollars_RoundToTheTick()
    {
        // $400.30 / $2 = 200.15 pts -> 200.25; partial 100.075 pts -> 100.00.
        var (target, partial, _) = LevelCalculator.Targets(Dollars(400.30m, TargetDollarsBasis.PerContract, 2m));

        Assert.Equal(20200.25m, target);
        Assert.Equal(20100m, partial);
    }

    [Fact]
    public void Dollars_PartialIsPartialPctOfTheDistance()
    {
        var (_, partial, _) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 2m, partialPct: 25m));

        Assert.Equal(20050m, partial);
    }

    [Fact]
    public void Dollars_WithoutAPointValue_HaveNoDistance()
    {
        var (target, _, rr) = LevelCalculator.Targets(Dollars(400m, TargetDollarsBasis.PerContract, 0m));

        Assert.Equal(20000m, target);
        Assert.Equal(0m, rr);
    }

    [Fact]
    public void RangePct_MatchesCalcLevels()
    {
        var r = new LevelRequest(1000m, true, 990m, 1, TargetMode.RangePct, 100m, 100m, 0m,
            TargetDollarsBasis.PerContract, 0m, 50m, 0m);

        Assert.Equal((1100m, 1050m, 10m), LevelCalculator.Targets(r));
    }

    [Fact]
    public void RiskMultiple_UsesTheTpMultiplesOfTheRisk()
    {
        var r = new LevelRequest(100m, true, 98m, 1, TargetMode.RiskMultiple, 0m, 0m, 0m,
            TargetDollarsBasis.PerContract, 0m, 50m, 0.25m, Tp1Mult: 1m, Tp2Mult: 2m);

        Assert.Equal((104m, 102m, 2m), LevelCalculator.Targets(r));
    }

    [Fact]
    public void Atr_UsesTheTpMultiplesOfTheAtr()
    {
        var r = new LevelRequest(100m, true, 98m, 1, TargetMode.Atr, 4m, 0m, 0m,
            TargetDollarsBasis.PerContract, 0m, 50m, 0.25m, Tp1Mult: 0.5m, Tp2Mult: 1.5m);

        var (target, partial, _) = LevelCalculator.Targets(r);

        Assert.Equal(106m, target);
        Assert.Equal(102m, partial);
    }

    [Fact]
    public void RaiseToMinRr_Long_MovesTargetToMinRAndRecomputesPartial()
    {
        var r = new LevelRequest(18010m, true, 18000m, 1, TargetMode.RangePct, 20m, 100m, 0m,
            TargetDollarsBasis.PerContract, 2m, 50m, 0.25m);

        Assert.Equal((18040m, 18025m), LevelCalculator.RaiseToMinRr(r, 3m));
    }

    [Fact]
    public void RaiseToMinRr_Short_MovesTargetToMinRAndRecomputesPartial()
    {
        var r = new LevelRequest(100m, false, 102m, 1, TargetMode.RangePct, 20m, 100m, 0m,
            TargetDollarsBasis.PerContract, 2m, 50m, 0.25m);

        Assert.Equal((97m, 98.5m), LevelCalculator.RaiseToMinRr(r, 1.5m));
    }

    [Theory]
    [InlineData(true,  99.9,  100.25)]
    [InlineData(false, 100.1, 99.75)]
    public void RaiseToMinRr_RoundsAwayFromTheEntry_SoTheTradeIsNeverBelowTheMinimum(bool isLong, double stop, double expected)
    {
        // Risk 0.1 x 1.2 = 0.12 pts: the nearest tick is the entry itself (0R); the raise goes one tick out.
        var r = new LevelRequest(100m, isLong, (decimal)stop, 1, TargetMode.RangePct, 0m, 0m, 0m,
            TargetDollarsBasis.PerContract, 2m, 50m, 0.25m);

        Assert.Equal((decimal)expected, LevelCalculator.RaiseToMinRr(r, 1.2m).Target);
    }

    [Fact]
    public void RangeStop_IsStopPctOfTheRangeFromTheEntry()
    {
        Assert.Equal(990m,  LevelCalculator.RangeStop(1000m, true,  100m, 0.10m));
        Assert.Equal(1010m, LevelCalculator.RangeStop(1000m, false, 100m, 0.10m));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~LevelCalculatorTests"`
Expected: build FAILS with `The type or namespace name 'LevelRequest' could not be found`.

- [ ] **Step 3: Replace `LevelCalculator`**

Replace lines 1–59 of `CRV.Core/Strategy/StrategyHelpers.cs` (from `namespace CRV.Core.Strategy;` through the closing brace of `LevelCalculator`) with:

```csharp
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// What a strategy knows when it sets its target: the fill, the final stop, the sized
/// contract count and the target settings. <see cref="RangeOrAtr"/> is the range for
/// RangePct and the ATR for Atr.
/// </summary>
public record LevelRequest(
    decimal Entry, bool IsLong, decimal Stop, int Contracts,
    TargetMode Mode, decimal RangeOrAtr, decimal TargetPct,
    decimal TargetDollars, TargetDollarsBasis Basis, decimal PointValue,
    decimal PartialPct, decimal TickSize,
    decimal Tp1Mult = 0, decimal Tp2Mult = 0)
{
    public static LevelRequest From(StrategySetupConfig cfg, decimal entry, bool isLong, decimal stop,
        int contracts, decimal rangeOrAtr) => new(
        entry, isLong, stop, contracts, cfg.TargetMode, rangeOrAtr, cfg.TargetPct,
        cfg.TargetDollars, cfg.TargetDollarsBasis, cfg.PointValue, cfg.PartialPct, cfg.TickSize,
        cfg.AtrTp1Mult, cfg.AtrTp2Mult);
}

/// <summary>Port of Pine f_calcLevels(), extended to every target mode.</summary>
public static class LevelCalculator
{
    /// <summary>
    /// Rounds <paramref name="price"/> to the nearest valid tick.
    /// tickSize = 0 means no rounding (used by existing tests without a tick parameter).
    /// </summary>
    public static decimal RoundToTick(decimal price, decimal tickSize)
        => tickSize > 0 ? Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize : price;

    /// <summary>The OrbPct stop: <paramref name="stopPct"/> of the range from <paramref name="entry"/>, on the tick.</summary>
    public static decimal RangeStop(decimal entry, bool isLong, decimal range, decimal stopPct, decimal tickSize = 0m)
        => RoundToTick(isLong ? entry - range * stopPct : entry + range * stopPct, tickSize);

    /// <summary>
    /// Target and partial on the tick, measured from <see cref="LevelRequest.Entry"/>, and the
    /// reward / risk they give against <see cref="LevelRequest.Stop"/> (0 when there is no risk).
    /// </summary>
    public static (decimal Target, decimal Partial, decimal Rr) Targets(LevelRequest r)
    {
        decimal risk        = Math.Abs(r.Entry - r.Stop);
        decimal dist        = TargetDistance(r, risk);
        decimal partialDist = PartialDistance(r, risk, dist);

        decimal target  = RoundToTick(r.IsLong ? r.Entry + dist        : r.Entry - dist,        r.TickSize);
        decimal partial = RoundToTick(r.IsLong ? r.Entry + partialDist : r.Entry - partialDist, r.TickSize);

        decimal reward = Math.Abs(target - r.Entry);
        return (target, partial, risk > 0 ? reward / risk : 0m);
    }

    /// <summary>
    /// The target moved out to <paramref name="minRr"/> × the risk, rounded away from the entry so
    /// the trade is never below the minimum, and the partial at PartialPct of the new distance.
    /// </summary>
    public static (decimal Target, decimal Partial) RaiseToMinRr(LevelRequest r, decimal minRr)
    {
        decimal dist   = Math.Abs(r.Entry - r.Stop) * minRr;
        decimal target = r.IsLong ? CeilToTick(r.Entry + dist, r.TickSize) : FloorToTick(r.Entry - dist, r.TickSize);

        decimal partialDist = Math.Abs(target - r.Entry) * (r.PartialPct / 100m);
        decimal partial     = RoundToTick(r.IsLong ? r.Entry + partialDist : r.Entry - partialDist, r.TickSize);
        return (target, partial);
    }

    /// <summary>Setup A levels: OrbPct stop and a RangePct target, both from <paramref name="entry"/>.</summary>
    public static (decimal stop, decimal target, decimal partial, decimal rr)
        CalcLevels(decimal entry, bool isLong, decimal stopPct,
                   int targetPct, int partialPct, decimal orbRange,
                   decimal tickSize = 0m)
    {
        decimal stop = RangeStop(entry, isLong, orbRange, stopPct, tickSize);
        var (target, partial, rr) = Targets(new LevelRequest(entry, isLong, stop, 1, TargetMode.RangePct,
            orbRange, targetPct, 0m, TargetDollarsBasis.PerContract, 0m, partialPct, tickSize));
        return (stop, target, partial, rr);
    }

    /// <summary>
    /// Setup B — stop is entry-anchored at entry ± orbRange * stopPct (tick-rounded).
    /// Default stopPct 0.50 gives the same stop as orbMid for a symmetric ORB.
    /// </summary>
    public static (decimal stop, decimal target, decimal partial, decimal rr)
        CalcLevelsB(decimal entry, bool isLong, int targetPct,
                    int partialPct, decimal orbRange, decimal stopPct,
                    decimal tickSize = 0m)
        => CalcLevels(entry, isLong, stopPct, targetPct, partialPct, orbRange, tickSize);

    private static decimal TargetDistance(LevelRequest r, decimal risk) => r.Mode switch
    {
        TargetMode.RangePct     => r.RangeOrAtr * (r.TargetPct / 100m),
        TargetMode.Dollars      => DollarDistance(r),
        TargetMode.Atr          => r.RangeOrAtr * r.Tp2Mult,
        TargetMode.RiskMultiple => risk * r.Tp2Mult,
        _                       => 0m,
    };

    /// <summary>Per contract: dollars / point value. Whole position: also spread over the contracts.</summary>
    private static decimal DollarDistance(LevelRequest r)
    {
        decimal perPoint = r.Basis == TargetDollarsBasis.WholePosition ? r.PointValue * r.Contracts : r.PointValue;
        return perPoint > 0 ? r.TargetDollars / perPoint : 0m;
    }

    private static decimal PartialDistance(LevelRequest r, decimal risk, decimal dist) => r.Mode switch
    {
        TargetMode.Atr          when r.Tp1Mult > 0 => r.RangeOrAtr * r.Tp1Mult,
        TargetMode.RiskMultiple when r.Tp1Mult > 0 => risk * r.Tp1Mult,
        _                                          => dist * (r.PartialPct / 100m),
    };

    private static decimal CeilToTick(decimal price, decimal tickSize)
        => tickSize > 0 ? Math.Ceiling(price / tickSize) * tickSize : price;

    private static decimal FloorToTick(decimal price, decimal tickSize)
        => tickSize > 0 ? Math.Floor(price / tickSize) * tickSize : price;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~LevelCalculatorTests|FullyQualifiedName~StrategyTests"`
Expected: PASS, `Failed: 0` (the existing `CalcLevels` / `CalcLevelsB` tests and every strategy test still pass: the wrappers give the same numbers).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/StrategyHelpers.cs CRV.Core.Tests/Strategy/LevelCalculatorTests.cs
git commit -m "feat(strategy): one level calculator for range, dollar, ATR and R targets"
```

---

### Task 3: The guard, and a min-R skip as a recorded refusal

**Files:**
- Modify: `CRV.Core/Strategy/StrategyHelpers.cs` (append after `LevelCalculator`)
- Modify: `CRV.Core/Models/SizeRefusal.cs`
- Modify: `CRV.Core/Strategy/SizeRefusalGate.cs`
- Modify: `CRV.Core/Strategy/ISetupStrategy.cs:48-67,144-149`
- Create test: `CRV.Core.Tests/Strategy/MinRrGuardTests.cs`

**Interfaces:**
- Consumes: Task 2 `LevelRequest`, `LevelCalculator.Targets`, `LevelCalculator.RaiseToMinRr`.
- Produces:
  - `sealed record GuardedLevels(decimal Target, decimal Partial, decimal Rr, bool Skip)`; `static GuardedLevels MinRrGuard.Apply(StrategySetupConfig cfg, LevelRequest r)`.
  - `enum RefusalReason { Size, MinRr }`; `SizeRefusal(..., RefusalReason Reason = Size, decimal Rr = 0, decimal MinRr = 0)`; `Describe()` for a skip = `"Skipped: {Rr:0.0#}R below {MinRr:0.0#}R"`.
  - `SizeRefusalGate.ReportMinRr(bool isLong, decimal ep, decimal sl, decimal rr, StrategySetupConfig cfg, DateTime time)` → `SizeRefusal?`; `SizeRefusal? SizeRefusalGate.LastMinRrSkip` (cleared by `Reset()`).
  - `SetupStateSnapshot.MinRrEnforced` (bool, default true), `.MinRr` (decimal), `.LastSkip` (string?).

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/MinRrGuardTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// The per-trade reward / risk guard. MNQ at $2 a point, long from 20000 with a 40-point stop,
/// minimum 1.5R: a dollar target under $120 a contract is below the minimum.
/// </summary>
public class MinRrGuardTests
{
    private static readonly DateTime T0 = new(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc);

    private static StrategySetupConfig Cfg(decimal dollars, bool enforce = true, MinRrAction action = MinRrAction.Skip) => new()
    {
        Id = "of-mnq", Ticker = "MNQZ26", PointValue = 2m, TickSize = 0.25m, PartialPct = 50,
        TargetMode = TargetMode.Dollars, TargetDollars = dollars, TargetDollarsBasis = TargetDollarsBasis.PerContract,
        MinRr = 1.5m, EnforceMinRr = enforce, MinRrAction = action,
    };

    private static LevelRequest Request(StrategySetupConfig cfg, decimal stop = 19960m)
        => LevelRequest.From(cfg, entry: 20000m, isLong: true, stop: stop, contracts: 1, rangeOrAtr: 0m);

    [Fact]
    public void BelowMinimum_Skip_SkipsAndSaysByHowMuch()
    {
        var cfg = Cfg(100m);                     // 50 pts / 40 = 1.25R

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.True(g.Skip);
        Assert.Equal(1.25m, g.Rr);
    }

    [Fact]
    public void AtMinimum_Takes()
    {
        var cfg = Cfg(120m);                     // 60 pts / 40 = 1.5R

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.False(g.Skip);
        Assert.Equal(20060m, g.Target);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_TakesAtTheMinimumWithThePartialRecomputed()
    {
        var cfg = Cfg(100m, action: MinRrAction.RaiseTarget);

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.False(g.Skip);
        Assert.Equal(20060m, g.Target);          // 1.5 x 40 pts
        Assert.Equal(20030m, g.Partial);         // 50% of the new 60 pts
        Assert.Equal(1.5m, g.Rr);
    }

    [Fact]
    public void GuardOff_TakesBelowMinimum()
    {
        var cfg = Cfg(100m, enforce: false);

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.False(g.Skip);
        Assert.Equal(20050m, g.Target);
        Assert.Equal(1.25m, g.Rr);
    }

    [Fact]
    public void NoReward_Skips_EvenWithGuardOff()
    {
        var cfg = Cfg(0m, enforce: false);

        Assert.True(MinRrGuard.Apply(cfg, Request(cfg)).Skip);
    }

    [Fact]
    public void NoRisk_Skips_EvenWithGuardOff()
    {
        var cfg = Cfg(400m, enforce: false);

        Assert.True(MinRrGuard.Apply(cfg, Request(cfg, stop: 20000m)).Skip);
    }

    [Fact]
    public void Gate_MinRrSkip_IsARefusalWithItsReason()
    {
        var gate = new SizeRefusalGate();

        var skip = gate.ReportMinRr(isLong: true, ep: 20000m, sl: 19960m, rr: 1.25m, Cfg(100m), T0);

        Assert.NotNull(skip);
        Assert.Equal(RefusalReason.MinRr, skip!.Reason);
        Assert.Equal(1.25m, skip.Rr);
        Assert.Equal(1.5m, skip.MinRr);
        Assert.Equal(40m, skip.StopDistance);
        Assert.Equal("Skipped: 1.25R below 1.5R", skip.Describe());
        Assert.Same(skip, gate.LastMinRrSkip);
    }

    [Fact]
    public void Gate_MinRrSkip_SameSignalAskedTwice_ReportsOnce()
    {
        var gate = new SizeRefusalGate();
        gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0);

        Assert.Null(gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0.AddSeconds(15)));
    }

    [Fact]
    public void Gate_Reset_ForgetsTheLastSkip()
    {
        var gate = new SizeRefusalGate();
        gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0);

        gate.Reset();

        Assert.Null(gate.LastMinRrSkip);
    }

    [Fact]
    public void SizeRefusal_KeepsItsWording()
    {
        var r = new SizeRefusal(T0, "of-mnq", "MNQZ26", 40m, 80m, 50m);

        Assert.Equal(RefusalReason.Size, r.Reason);
        Assert.StartsWith("refused for size", r.Describe());
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~MinRrGuardTests"`
Expected: build FAILS with `The name 'MinRrGuard' does not exist in the current context`.

- [ ] **Step 3: Add the guard**

Append to `CRV.Core/Strategy/StrategyHelpers.cs`, after the closing brace of `LevelCalculator` (before `public record ExitResult`):

```csharp

/// <summary>A trade's target and partial once the reward / risk guard has looked at it.</summary>
public sealed record GuardedLevels(decimal Target, decimal Partial, decimal Rr, bool Skip);

/// <summary>
/// The per-trade minimum reward / risk rule. A trade with no reward or no risk is always
/// skipped: its target would sit on the entry. Otherwise, with the guard on, a trade below
/// MinRr is skipped or has its target raised to MinRr; with it off, it is taken as it is.
/// </summary>
public static class MinRrGuard
{
    public static GuardedLevels Apply(StrategySetupConfig cfg, LevelRequest r)
    {
        var (target, partial, rr) = LevelCalculator.Targets(r);
        if (rr <= 0) return new(target, partial, rr, Skip: true);
        if (!cfg.EnforceMinRr || rr >= cfg.MinRr) return new(target, partial, rr, Skip: false);
        if (cfg.MinRrAction == MinRrAction.Skip) return new(target, partial, rr, Skip: true);

        var (raised, raisedPartial) = LevelCalculator.RaiseToMinRr(r, cfg.MinRr);
        decimal risk = Math.Abs(r.Entry - r.Stop);
        return new(raised, raisedPartial, Math.Abs(raised - r.Entry) / risk, Skip: false);
    }
}
```

- [ ] **Step 4: Give `SizeRefusal` a reason**

Replace `CRV.Core/Models/SizeRefusal.cs` with:

```csharp
using System.Globalization;

namespace CRV.Core.Models;

/// <summary>Why a signal the strategy wanted to take was not taken.</summary>
public enum RefusalReason
{
    /// <summary>The risk budget could not carry it at even one contract.</summary>
    Size,
    /// <summary>Its reward / risk was below the strategy's minimum and the guard skips such trades.</summary>
    MinRr,
}

/// <summary>
/// A signal the strategy wanted to take and did not: the risk budget could not carry it at
/// even one contract, or its reward / risk was below the minimum. Not a trade, so it never
/// reaches the trade record — which is why it is an event of its own: a study that measures
/// only the trades that were taken, while some signals were dropped before they could become
/// trades, is measuring a different sample and ought to say so.
/// </summary>
public sealed record SizeRefusal(
    DateTime Time,
    string   SetupLabel,
    string   Ticker,
    decimal  StopDistance,
    decimal  RiskPerContract,
    decimal  Budget,
    RefusalReason Reason = RefusalReason.Size,
    decimal  Rr    = 0m,
    decimal  MinRr = 0m)
{
    public string Describe() => Reason == RefusalReason.MinRr
        ? string.Create(CultureInfo.InvariantCulture, $"Skipped: {Rr:0.0#}R below {MinRr:0.0#}R")
        : $"refused for size — stop {StopDistance:F2} pts = ${RiskPerContract:F2}/ct, budget ${Budget:F0}";
}
```

- [ ] **Step 5: Report skips through the gate**

Replace the body of `SizeRefusalGate` in `CRV.Core/Strategy/SizeRefusalGate.cs` (keep the class doc comment) with:

```csharp
public sealed class SizeRefusalGate
{
    private (bool IsLong, decimal Ep, decimal Sl)? _last;

    /// <summary>The last signal skipped for reward / risk since the last reset; shown on the cockpit card.</summary>
    public SizeRefusal? LastMinRrSkip { get; private set; }

    /// <summary>The refusal to report, or null when this signal was already reported.</summary>
    public SizeRefusal? Report(bool isLong, decimal ep, decimal sl, StrategySetupConfig cfg, DateTime time)
    {
        if (!IsNew(isLong, ep, sl)) return null;
        return AutoSizeByRiskCalculator.Refusal(ep, sl, cfg, time);
    }

    /// <summary>The min-R skip to report, or null when this signal was already reported.</summary>
    public SizeRefusal? ReportMinRr(bool isLong, decimal ep, decimal sl, decimal rr, StrategySetupConfig cfg, DateTime time)
    {
        if (!IsNew(isLong, ep, sl)) return null;
        LastMinRrSkip = AutoSizeByRiskCalculator.Refusal(ep, sl, cfg, time)
            with { Reason = RefusalReason.MinRr, Rr = rr, MinRr = cfg.MinRr };
        return LastMinRrSkip;
    }

    /// <summary>Forget the last signal — a new day or session may legitimately refuse it again.</summary>
    public void Reset()
    {
        _last = null;
        LastMinRrSkip = null;
    }

    private bool IsNew(bool isLong, decimal ep, decimal sl)
    {
        var key = (isLong, ep, sl);
        if (_last == key) return false;
        _last = key;
        return true;
    }
}
```

- [ ] **Step 6: Snapshot fields and the interface doc**

In `CRV.Core/Strategy/ISetupStrategy.cs`, inside `SetupStateSnapshot`, after `public decimal LossPnl { get; set; }` (line 66):

```csharp
    /// <summary>False when the strategy takes trades below its minimum reward / risk.</summary>
    public bool MinRrEnforced { get; set; } = true;
    public decimal MinRr { get; set; }
    /// <summary>The last trade skipped for reward / risk this session, in words; null when none.</summary>
    public string? LastSkip { get; set; }
```

Replace the doc comment on `PendingSizeRefusal` (lines 144–148) with:

```csharp
    /// <summary>
    /// A signal the strategy wanted to take and did not: the risk budget could not carry it
    /// at even one contract, or the reward / risk guard skipped it (see
    /// <see cref="SizeRefusal.Reason"/>). Set instead of <see cref="PendingEntry"/>, consumed
    /// and cleared alongside it. Null for strategies that do neither.
    /// </summary>
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~MinRrGuardTests|FullyQualifiedName~AutoSizeByRiskTests|FullyQualifiedName~ComposableEngineTests"`
Expected: PASS, `Failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add CRV.Core/Strategy/StrategyHelpers.cs CRV.Core/Models/SizeRefusal.cs CRV.Core/Strategy/SizeRefusalGate.cs CRV.Core/Strategy/ISetupStrategy.cs CRV.Core.Tests/Strategy/MinRrGuardTests.cs
git commit -m "feat(risk): min-R guard with skip recorded as a refusal"
```

---

### Task 4: Fakeout strategies — size first, then target, then guard

**Files:**
- Modify: `CRV.Core/Strategy/OrbFakeoutStrategy.cs:233-253,264-322`
- Modify: `CRV.Core/Strategy/SessionFakeoutStrategy.cs:241-261,275-333`
- Test: `CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs`, `CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs`

**Interfaces:**
- Consumes: `LevelCalculator.RangeStop`, `LevelRequest.From`, `MinRrGuard.Apply`, `SizeRefusalGate.ReportMinRr`, `SizeRefusalGate.LastMinRrSkip` (Tasks 2–3).
- Produces: `GetSnapshot()` fills `MinRrEnforced`, `MinRr`, `LastSkip`.

- [ ] **Step 1: Write the failing OrbFakeout tests**

Append inside `OrbFakeoutStrategyTests` (entry long at 5180, stop 5178, range 20, $20 a point):

```csharp
    // ═══════════════════════════════════════════════════════════════
    // Targets after sizing, and the reward / risk guard
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TickOffset_TargetAndPartialAreMeasuredFromTheFill()
    {
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;                 // fill 5180.50
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        var e = s.PendingEntry!;
        Assert.Equal(5180.50m, e.Entry);
        Assert.Equal(5178m, e.Stop);             // stop stays measured from the signal price
        Assert.Equal(5200.50m, e.Tg2Price);      // 20 pts from the fill
        Assert.Equal(5190.50m, e.Tg1Price);
    }

    [Fact]
    public void TickOffset_RewardRiskIsMeasuredFromTheFill()
    {
        // From the fill: 20 / 2.5 = 8R. From the signal price it would have been 10R.
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;
        cfg.MinRr = 9m;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        var skip = s.PendingSizeRefusal!;
        Assert.Equal(RefusalReason.MinRr, skip.Reason);
        Assert.Equal(8m, skip.Rr);
        Assert.Equal("Skipped: 8.0R below 9.0R", skip.Describe());
        Assert.Equal(skip.Describe(), s.GetSnapshot().LastSkip);
        Assert.True(s.IsArmed);                  // a skipped trade leaves the setup armed, as before

        s.Reset();
        Assert.Null(s.GetSnapshot().LastSkip);
    }

    [Theory]
    [InlineData(40,  1, 5200)]
    [InlineData(80,  2, 5190)]
    [InlineData(160, 4, 5185)]
    public void WholePositionDollars_TargetIsSpreadOverTheSizedContracts(int budget, int contracts, int target)
    {
        // Stop 2 pts x $20 = $40 a contract, so the budget sizes 1, 2 or 4; $400 over the position.
        var cfg = DefaultConfig();
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk = budget;
        cfg.MaxContracts = 4;
        cfg.TargetMode = TargetMode.Dollars;
        cfg.TargetDollars = 400m;
        cfg.TargetDollarsBasis = TargetDollarsBasis.WholePosition;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        var e = s.PendingEntry!;
        Assert.Equal(contracts, e.TotalContracts);
        Assert.Equal((decimal)target, e.Tg2Price);
    }

    [Fact]
    public void OneContractWithPartial_DollarTarget_IsASingleTg2Bracket()
    {
        var cfg = DefaultConfig();
        cfg.Contracts = 1;
        cfg.MaxContracts = 1;
        cfg.UsePartial = true;
        cfg.TargetMode = TargetMode.Dollars;
        cfg.TargetDollars = 400m;                // $400 / $20 = 20 pts
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        var leg = Assert.Single(s.PendingEntry!.ResolveBrackets());
        Assert.Equal(5200m, leg.TargetPrice);
        Assert.Equal(1, leg.Qty);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_EntersAtTheMinimum()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 12m;                         // range target gives 10R
        cfg.MinRrAction = MinRrAction.RaiseTarget;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        var e = s.PendingEntry!;
        Assert.Equal(5204m, e.Tg2Price);         // 12 x 2 pts
        Assert.Equal(5192m, e.Tg1Price);         // 50% of 24 pts
        Assert.Null(s.PendingSizeRefusal);
    }

    [Fact]
    public void GuardOff_TakesTradeBelowMinimum()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99m;
        cfg.EnforceMinRr = false;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Equal(5200m, s.PendingEntry!.Tg2Price);
        Assert.False(s.GetSnapshot().MinRrEnforced);
        Assert.Equal(99m, s.GetSnapshot().MinRr);
    }
```

- [ ] **Step 2: Write the failing SessionFakeout tests**

Append inside `SessionFakeoutStrategyTests` (entry long at session low 5170, stop 5166, session range 40):

```csharp
    // ═════════════════════════════════════════════════════════════════
    // Targets after sizing, and the reward / risk guard
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void TickOffset_RewardRiskIsMeasuredFromTheFill()
    {
        // Fill 5170.50, stop 5166, target 5210.50: 40 / 4.5 = 8.89R. From the signal price: 10R.
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;
        cfg.MinRr = 9m;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        var skip = s.PendingSizeRefusal!;
        Assert.Equal(RefusalReason.MinRr, skip.Reason);
        Assert.Equal(40m / 4.5m, skip.Rr);
        Assert.Equal(skip.Describe(), s.GetSnapshot().LastSkip);
    }

    [Fact]
    public void TickOffset_TargetIsMeasuredFromTheFill()
    {
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Equal(5210.50m, s.PendingEntry!.Tg2Price);
    }

    [Fact]
    public void WholePositionDollars_TargetIsSpreadOverTheSizedContracts()
    {
        // Stop 4 pts x $20 = $80 a contract; $160 budget = 2 contracts; $400 / ($20 x 2) = 10 pts.
        var cfg = DefaultConfig();
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk = 160m;
        cfg.TargetMode = TargetMode.Dollars;
        cfg.TargetDollars = 400m;
        cfg.TargetDollarsBasis = TargetDollarsBasis.WholePosition;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Equal(2, s.PendingEntry!.TotalContracts);
        Assert.Equal(5180m, s.PendingEntry.Tg2Price);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_EntersAtTheMinimum()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 12m;
        cfg.MinRrAction = MinRrAction.RaiseTarget;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Equal(5218m, s.PendingEntry!.Tg2Price);   // 12 x 4 pts
        Assert.Equal(5194m, s.PendingEntry.Tg1Price);
    }
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~FakeoutStrategyTests"`
Expected: FAIL — e.g. `TickOffset_TargetAndPartialAreMeasuredFromTheFill` `Expected: 5200.50 Actual: 5200`, `TickOffset_RewardRiskIsMeasuredFromTheFill` `PendingSizeRefusal` null, `WholePositionDollars_*` target 5200.

- [ ] **Step 4: Rewire `OrbFakeoutStrategy.TryEntry`**

In `CRV.Core/Strategy/OrbFakeoutStrategy.cs`, replace:

```csharp
        // Calculate levels from ORIGINAL entry (offset applied to entry only, below)
        var (sl, tp, pp, _) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPct, _cfg.TargetPct, _cfg.PartialPct, orb.Range, _cfg.TickSize);

        // Apply entry tick offset to entry price only (levels already computed from true signal price)
```

with:

```csharp
        // OrbPct stop from the signal price; the tick offset below moves only the entry.
        decimal sl = LevelCalculator.RangeStop(ep, isLong, orb.Range, _cfg.StopPct, _cfg.TickSize);

        // Apply entry tick offset to the entry; target and partial are measured from it after sizing.
```

Then replace:

```csharp
        decimal risk   = Math.Abs(ep - sl);
        decimal reward = Math.Abs(tp - ep);
        decimal rr     = risk > 0 ? reward / risk : 0;
        if (rr < _cfg.MinRr) return;

        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0)
        {
            _pendingSizeRefusal = _refusalGate.Report(isLong, ep, sl, _cfg, time);
            return;
        }
```

with:

```csharp
        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0)
        {
            _pendingSizeRefusal = _refusalGate.Report(isLong, ep, sl, _cfg, time);
            return;
        }

        // Target and partial come after sizing: a whole-position dollar target spreads over the count.
        var levels = MinRrGuard.Apply(_cfg, LevelRequest.From(_cfg, ep, isLong, sl, contracts, orb.Range));
        if (levels.Skip)
        {
            _pendingSizeRefusal = _refusalGate.ReportMinRr(isLong, ep, sl, levels.Rr, _cfg, time);
            return;
        }
        decimal tp = levels.Target, pp = levels.Partial;
```

In `GetSnapshot()`, after `LossPnl     = _lossPnl,` add:

```csharp
        MinRrEnforced = _cfg.EnforceMinRr,
        MinRr       = _cfg.MinRr,
        LastSkip    = _refusalGate.LastMinRrSkip?.Describe(),
```

- [ ] **Step 5: Rewire `SessionFakeoutStrategy.TryEntry`**

In `CRV.Core/Strategy/SessionFakeoutStrategy.cs`, replace:

```csharp
        // Calculate levels from ORIGINAL entry (offset applied to entry only, below)
        var (sl, tp, pp, _) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPct, _cfg.TargetPct, _cfg.PartialPct, rangeSize, _cfg.TickSize);

        // Apply entry tick offset to entry price only (levels already computed from true signal price)
```

with:

```csharp
        // Session-range stop from the signal price; the tick offset below moves only the entry.
        decimal sl = LevelCalculator.RangeStop(ep, isLong, rangeSize, _cfg.StopPct, _cfg.TickSize);

        // Apply entry tick offset to the entry; target and partial are measured from it after sizing.
```

Then replace the same `decimal risk … if (rr < _cfg.MinRr) return; … refusal block` as in Step 4 with:

```csharp
        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0)
        {
            _pendingSizeRefusal = _refusalGate.Report(isLong, ep, sl, _cfg, time);
            return;
        }

        // Target and partial come after sizing: a whole-position dollar target spreads over the count.
        var levels = MinRrGuard.Apply(_cfg, LevelRequest.From(_cfg, ep, isLong, sl, contracts, rangeSize));
        if (levels.Skip)
        {
            _pendingSizeRefusal = _refusalGate.ReportMinRr(isLong, ep, sl, levels.Rr, _cfg, time);
            return;
        }
        decimal tp = levels.Target, pp = levels.Partial;
```

Add the same three lines to its `GetSnapshot()` after `LossPnl     = _lossPnl,`:

```csharp
        MinRrEnforced = _cfg.EnforceMinRr,
        MinRr       = _cfg.MinRr,
        LastSkip    = _refusalGate.LastMinRrSkip?.Describe(),
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~FakeoutStrategyTests"`
Expected: PASS, `Failed: 0` (including the existing `Entry_RespectsMinRr`, `ArmedSide_SwitchedOffWhileArmed_DoesNotEnter` and `BudgetBelowOneContract_RefusesAndSaysSo`).

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy/OrbFakeoutStrategy.cs CRV.Core/Strategy/SessionFakeoutStrategy.cs CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs
git commit -m "feat(risk): fakeout strategies size before the target and apply the min-R guard"
```

---

### Task 5: Pullback and Retest — size first, R from the fill

**Files:**
- Modify: `CRV.Core/Strategy/PullbackStrategy.cs:300-320,341-393`
- Modify: `CRV.Core/Strategy/RetestStrategy.cs:499-517,526-598`
- Test: `CRV.Core.Tests/Strategy/PullbackStrategyTests.cs`, `CRV.Core.Tests/Strategy/RetestStrategyTests.cs`

**Interfaces:**
- Consumes: same as Task 4.
- Produces: `GetSnapshot()` fills `MinRrEnforced`, `MinRr`, `LastSkip`.

- [ ] **Step 1: Write the failing Pullback tests**

Append inside `PullbackStrategyTests` (armed long, entry at 5190 inside the range, stop 5188, target 5210):

```csharp
    // ─── Targets after sizing, and the reward / risk guard ─────────────────

    private static PullbackStrategy EnterLongAt5190(StrategySetupConfig cfg)
    {
        var s = new PullbackStrategy(cfg);
        ArmLong(s);
        s.OnBar(MakeBar(5191m, 5192m, 5189m, 5191m), MakeOrb(), MakeIndicators(), EmptyModules());
        return s;
    }

    [Fact]
    public void TickOffset_RewardRiskIsMeasuredFromTheFill()
    {
        // Fill 5190.50, stop 5188, target 5210.50: 20 / 2.5 = 8R. Before, R used 5190: 10R.
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;
        cfg.MinRr = 9m;

        var s = EnterLongAt5190(cfg);

        Assert.Null(s.PendingEntry);
        var skip = s.PendingSizeRefusal!;
        Assert.Equal(RefusalReason.MinRr, skip.Reason);
        Assert.Equal(8m, skip.Rr);
        Assert.Equal(skip.Describe(), s.GetSnapshot().LastSkip);
    }

    [Fact]
    public void WholePositionDollars_TargetIsSpreadOverTheSizedContracts()
    {
        // Stop 2 pts x $20 = $40 a contract; $80 budget = 2 contracts; $400 / ($20 x 2) = 10 pts.
        var cfg = DefaultConfig();
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk = 80m;
        cfg.TargetMode = TargetMode.Dollars;
        cfg.TargetDollars = 400m;
        cfg.TargetDollarsBasis = TargetDollarsBasis.WholePosition;

        var s = EnterLongAt5190(cfg);

        Assert.Equal(2, s.PendingEntry!.TotalContracts);
        Assert.Equal(5200m, s.PendingEntry.Tg2Price);
        Assert.True(s.GetSnapshot().MinRrEnforced);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_EntersAtTheMinimum()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 12m;
        cfg.MinRrAction = MinRrAction.RaiseTarget;

        var s = EnterLongAt5190(cfg);

        Assert.Equal(5214m, s.PendingEntry!.Tg2Price);   // 12 x 2 pts
        Assert.Equal(5202m, s.PendingEntry.Tg1Price);
    }
```

- [ ] **Step 2: Write the failing Retest tests**

Append inside `RetestStrategyTests` (`EnterLong`: entry 5200, stop 5190, target 5220):

```csharp
    // ── Targets after sizing, and the reward / risk guard ───────────

    [Fact]
    public void TickOffset_RewardRiskIsMeasuredFromTheFill()
    {
        // Fill 5200.50, stop 5190, target 5220.50: 20 / 10.5 = 1.90R. Before, R used 5200: 2R.
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;
        cfg.MinRr = 1.95m;
        var s = new RetestStrategy(cfg);

        EnterLong(s);

        Assert.Null(s.PendingEntry);
        var skip = s.PendingSizeRefusal!;
        Assert.Equal(RefusalReason.MinRr, skip.Reason);
        Assert.Equal(20m / 10.5m, skip.Rr);
        Assert.Equal(skip.Describe(), s.GetSnapshot().LastSkip);
    }

    [Fact]
    public void WholePositionDollars_TargetIsSpreadOverTheSizedContracts()
    {
        // Stop 10 pts x $20 = $200 a contract; $400 budget = 2 contracts; $400 / ($20 x 2) = 10 pts = 1R.
        var cfg = DefaultConfig();
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk = 400m;
        cfg.TargetMode = TargetMode.Dollars;
        cfg.TargetDollars = 400m;
        cfg.TargetDollarsBasis = TargetDollarsBasis.WholePosition;
        var s = new RetestStrategy(cfg);

        EnterLong(s);

        Assert.Equal(2, s.PendingEntry!.TotalContracts);
        Assert.Equal(5210m, s.PendingEntry.Tg2Price);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_EntersAtTheMinimum()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 3m;
        cfg.MinRrAction = MinRrAction.RaiseTarget;
        var s = new RetestStrategy(cfg);

        EnterLong(s);

        Assert.Equal(5230m, s.PendingEntry!.Tg2Price);   // 3 x 10 pts
        Assert.Equal(5215m, s.PendingEntry.Tg1Price);
    }
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~PullbackStrategyTests|FullyQualifiedName~RetestStrategyTests"`
Expected: FAIL — `TickOffset_RewardRiskIsMeasuredFromTheFill` gets a `PendingEntry` (R was taken before the offset), the dollar and raise tests get the range target.

- [ ] **Step 4: Rewire `PullbackStrategy.TryEntry`**

In `CRV.Core/Strategy/PullbackStrategy.cs`, replace:

```csharp
        // Calculate levels from ORIGINAL entry (offset applied to entry only, below)
        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPct, _cfg.TargetPct, _cfg.PartialPct, orb.Range, _cfg.TickSize);

        // Apply entry tick offset to entry price only (levels already computed from true signal price)
```

with:

```csharp
        // OrbPct stop from the signal price; the tick offset below moves only the entry.
        decimal sl = LevelCalculator.RangeStop(ep, isLong, orb.Range, _cfg.StopPct, _cfg.TickSize);

        // Apply entry tick offset to the entry; target and partial are measured from it after sizing.
```

Delete the R recomputation inside **both** the `BarHL` and the `Vwap` blocks — these three lines, twice:

```csharp
            decimal risk   = Math.Abs(ep - sl);
            decimal reward = Math.Abs(tp - ep);
            rr = risk > 0 ? reward / risk : 0;
```

Then replace:

```csharp
        if (rr < _cfg.MinRr) return;

        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0)
        {
            _pendingSizeRefusal = _refusalGate.Report(isLong, ep, sl, _cfg, time);
            return;
        }
```

with:

```csharp
        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0)
        {
            _pendingSizeRefusal = _refusalGate.Report(isLong, ep, sl, _cfg, time);
            return;
        }

        // Target and partial come after sizing: a whole-position dollar target spreads over the count.
        var levels = MinRrGuard.Apply(_cfg, LevelRequest.From(_cfg, ep, isLong, sl, contracts, orb.Range));
        if (levels.Skip)
        {
            _pendingSizeRefusal = _refusalGate.ReportMinRr(isLong, ep, sl, levels.Rr, _cfg, time);
            return;
        }
        decimal tp = levels.Target, pp = levels.Partial;
```

In `GetSnapshot()`, after `LossPnl     = _lossPnl,`:

```csharp
        MinRrEnforced = _cfg.EnforceMinRr,
        MinRr       = _cfg.MinRr,
        LastSkip    = _refusalGate.LastMinRrSkip?.Describe(),
```

- [ ] **Step 5: Rewire `RetestStrategy.TryEntry`**

In `CRV.Core/Strategy/RetestStrategy.cs`, replace:

```csharp
        // Calculate levels from ORIGINAL entry (before offset) so partial/target are based
        // on the true signal price, not the artificially nudged entry.
        var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
            _cfg.TargetPct, _cfg.PartialPct, orb.Range, _cfg.StopPct, _cfg.TickSize);

        // Apply entry tick offset to entry price only
```

with:

```csharp
        // OrbPct stop from the signal price; the tick offset below moves only the entry.
        decimal sl = LevelCalculator.RangeStop(ep, isLong, orb.Range, _cfg.StopPct, _cfg.TickSize);

        // Apply entry tick offset to the entry; target and partial are measured from it after sizing.
```

Delete the same three R lines inside both the `BarHL` and `Vwap` blocks, and replace the `if (rr < _cfg.MinRr) return;` + sizing block exactly as in Step 4 (same replacement text, `orb.Range`). Add the same three lines to its `GetSnapshot()` after `LossPnl     = _lossPnl,`.

- [ ] **Step 6: Run the strategy and backtest tests**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~StrategyTests|FullyQualifiedName~Backtest"`
Expected: PASS, `Failed: 0` (existing `UsesCalcLevelsB_ForStopCalculation`, refusal and determinism tests included).

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy/PullbackStrategy.cs CRV.Core/Strategy/RetestStrategy.cs CRV.Core.Tests/Strategy/PullbackStrategyTests.cs CRV.Core.Tests/Strategy/RetestStrategyTests.cs
git commit -m "feat(risk): pullback and retest size before the target and take R from the fill"
```

---

### Task 6: Typical stop from backtest history

**Files:**
- Create: `CRV.Core/Strategy/TypicalStop.cs`
- Test: `CRV.Core.Tests/Strategy/TypicalStopTests.cs`

**Interfaces:**
- Produces (namespace `CRV.Core.Strategy`):
  - `static decimal? TypicalStop.Median(IEnumerable<TradeRecord> trades, int last = 30)` — median `|Entry − InitialStop|` in points; null = no history.
  - `static decimal? TypicalStop.MedianPosition(IEnumerable<TradeRecord> trades, int last = 30)` — median `Contracts × stop points`.
  - `static IReadOnlyList<TradeRecord> TypicalStop.Recent(IEnumerable<TradeRecord> trades, int last = 30)` — the trades the medians use.
  - `static List<TradeRecord> TypicalStop.FromRuns(IEnumerable<string?> resultJsons, string setupId)`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/TypicalStopTests.cs`:

```csharp
using System.Text.Json;
using CRV.Backtest.Results;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class TypicalStopTests
{
    private static readonly DateTime Day = new(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc);

    private static TradeRecord Trade(decimal stopPts, int daysAgo = 0, int contracts = 1, string label = "of-mnq") => new()
    {
        SetupLabel = label, Ticker = "MNQZ26", Direction = Direction.Long, Contracts = contracts,
        Entry = 20000m, InitialStop = 20000m - stopPts, Target = 20000m + 2 * stopPts,
        EnteredAt = Day.AddDays(-daysAgo), ExitedAt = Day.AddDays(-daysAgo).AddMinutes(30),
    };

    [Fact]
    public void Median_OddCount_IsTheMiddleStop()
        => Assert.Equal(20m, TypicalStop.Median([Trade(10m, 1), Trade(40m, 2), Trade(20m, 3)]));

    [Fact]
    public void Median_EvenCount_AveragesTheMiddleTwo()
        => Assert.Equal(25m, TypicalStop.Median([Trade(10m, 1), Trade(20m, 2), Trade(30m, 3), Trade(40m, 4)]));

    [Fact]
    public void Median_UsesOnlyTheLast30Trades()
    {
        var trades = Enumerable.Range(1, 30).Select(d => Trade(10m, d)).Append(Trade(1000m, daysAgo: 99));

        Assert.Equal(10m, TypicalStop.Median(trades));
    }

    [Fact]
    public void Median_CountsATradeSavedInTwoRunsOnce()
    {
        // The same trade from two overlapping runs, plus one other: [10, 40], not [10, 10, 40].
        Assert.Equal(25m, TypicalStop.Median([Trade(10m, 1), Trade(10m, 1), Trade(40m, 2)]));
    }

    [Fact]
    public void Median_NoUsableTrades_IsNull()
    {
        var noStop = Trade(10m);
        noStop.InitialStop = 0m;

        Assert.Null(TypicalStop.Median([]));
        Assert.Null(TypicalStop.Median([noStop]));
    }

    [Fact]
    public void MedianPosition_MultipliesByContracts()
        => Assert.Equal(40m, TypicalStop.MedianPosition([Trade(40m, 1, contracts: 2), Trade(40m, 2), Trade(10m, 3, contracts: 4)]));

    [Fact]
    public void FromRuns_KeepsThisSetupsTrades_AndSkipsUnreadableRuns()
    {
        string Run(params TradeRecord[] t) => JsonSerializer.Serialize(new BacktestResult { Trades = t.ToList() });

        var trades = TypicalStop.FromRuns(
            [Run(Trade(10m, 1), Trade(20m, 2, label: "other")), null, "{not json", Run(Trade(30m, 3))], "of-mnq");

        Assert.Equal(new[] { 10m, 30m }, trades.Select(t => t.Entry - t.InitialStop).OrderBy(x => x));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~TypicalStopTests"`
Expected: build FAILS with `The name 'TypicalStop' does not exist in the current context`.

- [ ] **Step 3: Write `TypicalStop`**

`CRV.Core/Strategy/TypicalStop.cs`:

```csharp
using System.Text.Json;
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// A strategy's typical stop: the median stop of its last 30 trades in saved backtest runs.
/// The save-time reward / risk check measures a target against it. No history gives null,
/// never an estimate.
/// </summary>
public static class TypicalStop
{
    public const int DefaultLast = 30;

    /// <summary>Median stop distance in points, or null with no usable trades.</summary>
    public static decimal? Median(IEnumerable<TradeRecord> trades, int last = DefaultLast)
        => MedianOf(Recent(trades, last).Select(StopPoints));

    /// <summary>Median of contracts × stop points: the whole position's risk in points.</summary>
    public static decimal? MedianPosition(IEnumerable<TradeRecord> trades, int last = DefaultLast)
        => MedianOf(Recent(trades, last).Select(t => StopPoints(t) * Math.Max(1, t.Contracts)));

    /// <summary>The newest <paramref name="last"/> trades with a stop, each counted once even when
    /// overlapping runs saved it twice.</summary>
    public static IReadOnlyList<TradeRecord> Recent(IEnumerable<TradeRecord> trades, int last = DefaultLast)
        => trades.Where(t => t.InitialStop != 0 && t.InitialStop != t.Entry)
                 .DistinctBy(t => (t.EnteredAt, t.Entry, t.InitialStop))
                 .OrderByDescending(t => t.EnteredAt)
                 .Take(last)
                 .ToList();

    /// <summary>The trades of <paramref name="setupId"/> in saved runs' result JSON. A run that
    /// can't be read is skipped: it says nothing about this setup's stops.</summary>
    public static List<TradeRecord> FromRuns(IEnumerable<string?> resultJsons, string setupId)
    {
        var found = new List<TradeRecord>();
        foreach (var json in resultJsons)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            RunTrades? run;
            try { run = JsonSerializer.Deserialize<RunTrades>(json); }
            catch (JsonException) { continue; }
            if (run?.Trades is null) continue;
            found.AddRange(run.Trades.Where(t => t.SetupLabel == setupId));
        }
        return found;
    }

    private static decimal StopPoints(TradeRecord t) => Math.Abs(t.Entry - t.InitialStop);

    private static decimal? MedianOf(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    /// <summary>The part of a saved backtest result this needs.</summary>
    private sealed class RunTrades
    {
        public List<TradeRecord>? Trades { get; set; }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~TypicalStopTests"`
Expected: PASS, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/TypicalStop.cs CRV.Core.Tests/Strategy/TypicalStopTests.cs
git commit -m "feat(risk): typical stop from a strategy's last 30 backtest trades"
```

---

### Task 7: The save-time reward / risk check

**Files:**
- Create: `CRV.Core/Strategy/MinRrSaveCheck.cs`
- Test: `CRV.Core.Tests/Strategy/MinRrSaveCheckTests.cs`

**Interfaces:**
- Consumes: Task 1 fields; Task 6 medians (passed in as numbers).
- Produces: `sealed record MinRrSaveResult(string? Error, string? Warning)`; `static MinRrSaveResult MinRrSaveCheck.Check(StrategySetupConfig c, decimal pointValue, decimal? typicalStop, decimal? typicalPosition)`; constants `MinRrSaveCheck.NoHistory`, `.StopMovesWithBar`, `.AtrPerTrade`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/MinRrSaveCheckTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>Saving is blocked when the target is below MinRr x the typical stop. MNQ ($2 a point),
/// minimum 1.5R, typical stop 40 pts, typical position risk 80 pts.</summary>
public class MinRrSaveCheckTests
{
    private static StrategySetupConfig Cfg(Action<StrategySetupConfig> set)
    {
        var c = new StrategySetupConfig { MinRr = 1.5m, StopPct = 0.10m, StopMode = "OrbPct" };
        set(c);
        return c;
    }

    private static MinRrSaveResult Check(StrategySetupConfig c, decimal? stop = 40m, decimal? position = 80m)
        => MinRrSaveCheck.Check(c, pointValue: 2m, stop, position);

    [Theory]
    [InlineData(119.99, "Raise the target to at least $120 a contract, or lower the minimum reward / risk.")]
    [InlineData(120,    null)]
    [InlineData(120.01, null)]
    public void Dollars_PerContract_AtTheBoundary(double dollars, string? error)
    {
        var r = Check(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = (decimal)dollars; }));

        Assert.Equal(error, r.Error);
        Assert.Null(r.Warning);
    }

    [Theory]
    [InlineData(239.99, "Raise the target to at least $240 for the whole position, or lower the minimum reward / risk.")]
    [InlineData(240,    null)]
    [InlineData(240.01, null)]
    public void Dollars_WholePosition_AtTheBoundary(double dollars, string? error)
    {
        var r = Check(Cfg(c =>
        {
            c.TargetMode = TargetMode.Dollars; c.TargetDollars = (decimal)dollars;
            c.TargetDollarsBasis = TargetDollarsBasis.WholePosition;
        }));

        Assert.Equal(error, r.Error);
    }

    [Theory]
    [InlineData(14, "Raise the target to at least 15% of the range, or lower the minimum reward / risk.")]
    [InlineData(15, null)]
    [InlineData(16, null)]
    public void RangePct_OrbPctStop_AtTheBoundary(int targetPct, string? error)
        => Assert.Equal(error, Check(Cfg(c => c.TargetPct = targetPct), stop: null).Error);

    [Fact]
    public void RangePct_RoundsTheMinimumUpToAWholePercent()
        => Assert.Equal("Raise the target to at least 53% of the range, or lower the minimum reward / risk.",
            Check(Cfg(c => { c.StopPct = 0.35m; c.TargetPct = 52; })).Error);

    [Theory]
    [InlineData(1.49, "Raise the target to at least 1.5R, or lower the minimum reward / risk.")]
    [InlineData(1.5,  null)]
    [InlineData(1.51, null)]
    public void RiskMultiple_AtTheBoundary(double tp2, string? error)
        => Assert.Equal(error, Check(Cfg(c => { c.TargetMode = TargetMode.RiskMultiple; c.AtrTp2Mult = (decimal)tp2; })).Error);

    [Fact]
    public void Dollars_WithoutHistory_SavesWithTheWarning()
    {
        var r = Check(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 1m; }), stop: null, position: null);

        Assert.Null(r.Error);
        Assert.Equal("The reward / risk check runs once this strategy has a backtest.", r.Warning);
    }

    [Fact]
    public void RangePct_BarStop_SavesWithAWarning()
    {
        var r = Check(Cfg(c => { c.StopMode = "BarHL"; c.TargetPct = 1; }));

        Assert.Null(r.Error);
        Assert.Equal(MinRrSaveCheck.StopMovesWithBar, r.Warning);
    }

    [Fact]
    public void Atr_SavesWithAWarning()
        => Assert.Equal(MinRrSaveCheck.AtrPerTrade, Check(Cfg(c => c.TargetMode = TargetMode.Atr)).Warning);

    [Fact]
    public void GuardOff_NoCheckAndNoWarning()
    {
        var r = Check(Cfg(c => { c.EnforceMinRr = false; c.TargetMode = TargetMode.Dollars; c.TargetDollars = 1m; }));

        Assert.Equal(new MinRrSaveResult(null, null), r);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~MinRrSaveCheckTests"`
Expected: build FAILS with `The type or namespace name 'MinRrSaveResult' could not be found`.

- [ ] **Step 3: Write the check**

`CRV.Core/Strategy/MinRrSaveCheck.cs`:

```csharp
using System.Globalization;
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>An error blocks the save; a warning lets it through and is shown with it.</summary>
public sealed record MinRrSaveResult(string? Error, string? Warning);

/// <summary>
/// Blocks saving a strategy whose target is below MinRr × its typical stop, naming the minimum
/// in the target's own unit. A range target is measured against the stop setting (the stop is
/// exactly StopPct of the range); a dollar target against the median stop of the last 30
/// backtest trades (per contract) or of contracts × stop (whole position).
/// </summary>
public static class MinRrSaveCheck
{
    public const string NoHistory        = "The reward / risk check runs once this strategy has a backtest.";
    public const string StopMovesWithBar = "The save check can't compare a bar or VWAP stop with the range; each trade is still checked.";
    public const string AtrPerTrade      = "The save check can't size an ATR target before the market sets the ATR; each trade is still checked.";

    private static readonly MinRrSaveResult Pass = new(null, null);

    public static MinRrSaveResult Check(StrategySetupConfig c, decimal pointValue, decimal? typicalStop, decimal? typicalPosition)
    {
        if (!c.EnforceMinRr) return Pass;

        switch (c.TargetMode)
        {
            case TargetMode.RangePct:
                if (c.StopMode != "OrbPct") return new(null, StopMovesWithBar);
                decimal minPct = c.MinRr * c.StopPct * 100m;
                return c.TargetPct >= minPct ? Pass : Fail($"{Math.Ceiling(minPct):0}% of the range");

            case TargetMode.Dollars:
                bool whole = c.TargetDollarsBasis == TargetDollarsBasis.WholePosition;
                if ((whole ? typicalPosition : typicalStop) is not decimal points) return new(null, NoHistory);
                decimal minDollars = c.MinRr * points * pointValue;
                return c.TargetDollars >= minDollars
                    ? Pass
                    : Fail($"${Math.Ceiling(minDollars):N0} {(whole ? "for the whole position" : "a contract")}");

            case TargetMode.RiskMultiple:
                return c.AtrTp2Mult >= c.MinRr ? Pass : Fail($"{c.MinRr:0.##}R");

            default:
                return new(null, AtrPerTrade);
        }
    }

    private static MinRrSaveResult Fail(FormattableString minimum)
        => new($"Raise the target to at least {minimum.ToString(CultureInfo.InvariantCulture)}, or lower the minimum reward / risk.", null);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~MinRrSaveCheckTests"`
Expected: PASS, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/MinRrSaveCheck.cs CRV.Core.Tests/Strategy/MinRrSaveCheckTests.cs
git commit -m "feat(risk): block saving a target below the minimum R of the typical stop"
```

---

### Task 8: Backtest results record skips and the guard

**Files:**
- Modify: `CRV.Backtest/Results/BacktestResults.cs`
- Modify: `CRV.Web/Pages/Shared/_ResultsView.cshtml:41-48,178`
- Test: `CRV.Core.Tests/Backtest/MinRrGuardReachesTheResultTests.cs`

**Interfaces:**
- Consumes: `RefusalReason`, `MinRrAction` (Tasks 1, 3).
- Produces: `BacktestResult.MinRrSkips` (`List<SizeRefusal>`); `PerformanceMetrics.MinRrSkips` (int), `PerformanceMetrics.RrGuard` (`RrGuardState?`); `sealed record RrGuardState(bool Enforced, decimal MinRr, MinRrAction Action)` in `CRV.Backtest.Results`. `SizeRefusals` stay size-only.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Backtest/MinRrGuardReachesTheResultTests.cs`:

```csharp
using CRV.Backtest.Results;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The fixture's trade: entry 18010, stop 18000 (10 pts), target 18030 (20 pts) = 2R, and price
/// then runs to 18100. A 3R minimum puts it below the guard.
/// </summary>
public class MinRrGuardReachesTheResultTests
{
    [Fact]
    public async Task BelowMinimum_Skip_RunReportsTheSkip_NotASizeRefusal()
    {
        var result = await PullbackSessionFixture.Run(PullbackSessionFixture.Config(s => s.MinRr = 3m));

        Assert.Empty(result.Trades);
        Assert.Empty(result.SizeRefusals);
        var skip = Assert.Single(result.MinRrSkips);
        Assert.Equal(RefusalReason.MinRr, skip.Reason);
        Assert.Equal(2m, skip.Rr);
        Assert.Equal(3m, skip.MinRr);
        Assert.Equal(1, result.PerSetup["pullback-mnq"].MinRrSkips);
        Assert.Equal(0, result.PerSetup["pullback-mnq"].SizeRefusals);
        Assert.Equal(new RrGuardState(true, 3m, MinRrAction.Skip), result.PerSetup["pullback-mnq"].RrGuard);
    }

    [Fact]
    public async Task BelowMinimum_RaiseTarget_TradesWithTheTargetAtTheMinimum()
    {
        var result = await PullbackSessionFixture.Run(PullbackSessionFixture.Config(s =>
        {
            s.MinRr = 3m;
            s.MinRrAction = MinRrAction.RaiseTarget;
        }));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(18040m, trade.Target);      // 3 x 10 pts
        Assert.Empty(result.MinRrSkips);
    }

    [Fact]
    public async Task GuardOff_TradesBelowMinimum_AndTheResultSaysSo()
    {
        var result = await PullbackSessionFixture.Run(PullbackSessionFixture.Config(s =>
        {
            s.MinRr = 3m;
            s.EnforceMinRr = false;
        }));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(18030m, trade.Target);
        Assert.False(result.PerSetup["pullback-mnq"].RrGuard!.Enforced);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~MinRrGuardReachesTheResultTests"`
Expected: build FAILS with `'BacktestResult' does not contain a definition for 'MinRrSkips'`.

- [ ] **Step 3: Split refusals and record the guard**

In `CRV.Backtest/Results/BacktestResults.cs`:

After the `SizeRefusals` property of `BacktestResult` (line 21) add:

```csharp

    /// <summary>Signals skipped because their reward / risk was below the strategy's minimum.</summary>
    public List<SizeRefusal> MinRrSkips { get; set; } = new();
```

Replace the `SizeRefusals` doc + property of `PerformanceMetrics` (lines 61–62) with:

```csharp
    /// <summary>Signals refused by the risk budget. Zero unless a budget is set and bit.</summary>
    public int      SizeRefusals    { get; set; }

    /// <summary>Signals skipped by the reward / risk guard.</summary>
    public int      MinRrSkips      { get; set; }

    /// <summary>The setup's reward / risk guard in this run; null for the totals.</summary>
    public RrGuardState? RrGuard    { get; set; }
```

After `public record EquityPoint(...)` (line 71) add:

```csharp

/// <summary>Whether a setup enforced its minimum reward / risk in a run, and how.</summary>
public sealed record RrGuardState(bool Enforced, decimal MinRr, MinRrAction Action);
```

Replace the body of `Calculate` (lines 76–103) with:

```csharp
    public static BacktestResult Calculate(List<TradeRecord> trades, StrategyConfig cfg, BacktestConfig btCfg,
        List<SizeRefusal>? refusals = null)
    {
        refusals ??= new();
        var sizeRefusals = refusals.Where(r => r.Reason == RefusalReason.Size).ToList();
        var minRrSkips   = refusals.Where(r => r.Reason == RefusalReason.MinRr).ToList();

        // Group by SetupLabel (string Id), falling back to Setup enum name for legacy trades
        static string LabelOf(TradeRecord t) =>
            !string.IsNullOrEmpty(t.SetupLabel) ? t.SetupLabel : t.Setup.ToString();

        var refusedBySetup = sizeRefusals.GroupBy(r => r.SetupLabel).ToDictionary(g => g.Key, g => g.Count());
        var skippedBySetup = minRrSkips.GroupBy(r => r.SetupLabel).ToDictionary(g => g.Key, g => g.Count());
        var guards = cfg.ToSetupConfigs().GroupBy(s => s.Id)
            .ToDictionary(g => g.Key, g => new RrGuardState(g.First().EnforceMinRr, g.First().MinRr, g.First().MinRrAction));

        // A setup that was refused or skipped every time it fired has no trades and still needs a row.
        var labels = trades.Select(LabelOf).Concat(refusedBySetup.Keys).Concat(skippedBySetup.Keys).Distinct();
        var perSetup = labels.ToDictionary(
            label => label,
            label =>
            {
                var m = Calc(trades.Where(t => LabelOf(t) == label).ToList(), cfg,
                             refusedBySetup.GetValueOrDefault(label), skippedBySetup.GetValueOrDefault(label));
                m.RrGuard = guards.GetValueOrDefault(label);
                return m;
            });

        return new BacktestResult
        {
            Config       = cfg,
            BtConfig     = btCfg,
            Trades       = trades,
            Total        = Calc(trades, cfg, sizeRefusals.Count, minRrSkips.Count),
            PerSetup     = perSetup,
            SizeRefusals = sizeRefusals,
            MinRrSkips   = minRrSkips,
            EquityCurve  = BuildCurve(trades)
        };
    }
```

Change the `Calc` signature and its two `SizeRefusals = sizeRefusals` sites:

```csharp
    private static PerformanceMetrics Calc(List<TradeRecord> trades, StrategyConfig cfg, int sizeRefusals = 0, int minRrSkips = 0)
    {
        if (trades.Count == 0) return new() { SizeRefusals = sizeRefusals, MinRrSkips = minRrSkips };
```

and in the returned initializer, after `SizeRefusals    = sizeRefusals,`:

```csharp
            MinRrSkips      = minRrSkips,
```

- [ ] **Step 4: Show them in the results view**

In `CRV.Web/Pages/Shared/_ResultsView.cshtml`, inside `Details(PerformanceMetrics m)`, after the `@if (m.SizeRefusals > 0) { … }` block (line 47):

```cshtml
        @if (m.MinRrSkips > 0)
        {
            <div class="c-kv" title="Signals whose reward / risk was below the strategy's minimum, skipped by the guard."><span>Skipped below minimum R</span><span class="c-acc">@m.MinRrSkips</span></div>
        }
        @if (m.RrGuard is { } g)
        {
            <div class="c-kv"><span>Minimum reward / risk</span><span>@(g.Enforced ? $"{g.MinRr:0.##}R, {(g.Action == MinRrAction.Skip ? "skip" : "raise the target")}" : "Not enforced")</span></div>
        }
```

and change the "By setup" filter (line 178) to:

```cshtml
                        @foreach (var (label, m) in r.PerSetup.Where(kv => kv.Value.TotalTrades > 0 || kv.Value.SizeRefusals > 0 || kv.Value.MinRrSkips > 0).OrderByDescending(kv => kv.Value.NetPnl))
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet build CRV.Web && dotnet test CRV.Core.Tests --filter "FullyQualifiedName~Backtest"`
Expected: build succeeds; PASS, `Failed: 0` (`SizeRefusalReachesTheResultTests` and `ValidationRunnerTests` unchanged).

- [ ] **Step 6: Commit**

```bash
git add CRV.Backtest/Results/BacktestResults.cs CRV.Web/Pages/Shared/_ResultsView.cshtml CRV.Core.Tests/Backtest/MinRrGuardReachesTheResultTests.cs
git commit -m "feat(backtest): results record min-R skips and each setup's guard"
```

---

### Task 9: Migration `AddTargetModeAndRrGuard`

**Files:**
- Create: `CRV.Core/Migrations/<timestamp>_AddTargetModeAndRrGuard.cs` and its `.Designer.cs` (generated)
- Test: `CRV.Core.Tests/Data/AddTargetModeAndRrGuardTests.cs`

**Interfaces:**
- Produces: `public partial class AddTargetModeAndRrGuard : Migration` with `public const string Sql`.

- [ ] **Step 1: Generate the empty migration**

Run: `dotnet ef migrations add AddTargetModeAndRrGuard --project CRV.Core --startup-project CRV.Web`
Expected: `Done.` — a new `CRV.Core/Migrations/<timestamp>_AddTargetModeAndRrGuard.cs` with empty `Up` / `Down` (the fields live in basket JSON, so the model doesn't change) and its `.Designer.cs`; `git diff CRV.Core/Migrations/TradingDbContextModelSnapshot.cs` is empty.

- [ ] **Step 2: Write the failing test**

`CRV.Core.Tests/Data/AddTargetModeAndRrGuardTests.cs`:

```csharp
using CRV.Core.Migrations;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// The migration writes TargetMode = RangePct, the guard on and Skip into every stored basket
/// entry that has a Config object and doesn't say otherwise, and leaves anything it can't
/// safely edit exactly as it was.
/// </summary>
public class AddTargetModeAndRrGuardTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");

    public AddTargetModeAndRrGuardTests()
    {
        _conn.Open();
        Exec("""CREATE TABLE "Configs" ("Id" INTEGER PRIMARY KEY, "BasketJson" TEXT NULL);""");
    }

    public void Dispose() => _conn.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Insert(int id, string? json)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """INSERT INTO "Configs" ("Id", "BasketJson") VALUES ($id, $json);""";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private string? Read(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """SELECT "BasketJson" FROM "Configs" WHERE "Id" = $id;""";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public void WritesTheDefaults_KeepsWhatWasSet_AndTouchesNothingElse()
    {
        Insert(1, """[{"Id":"pb","Enabled":true,"Config":{"StopPct":0.10,"MinRr":1.50,"OrbStart":"09:30:00"},"Sessions":[{"SessionId":"NY","Enabled":true}]},{"Id":"of","Config":{"EnforceMinRr":false,"MinRrAction":1}},{"Id":"bare"}]""");

        Exec(AddTargetModeAndRrGuard.Sql);

        var json = Read(1)!;
        Assert.Contains("\"StopPct\":0.10", json);           // numbers keep their text
        Assert.Contains("\"OrbStart\":\"09:30:00\"", json);
        Assert.DoesNotContain("\"Id\":\"bare\",\"Config\"", json);

        var entries = BasketCodec.Parse(json);
        var pb = entries.Single(e => e.Id == "pb").Config;
        Assert.Equal((TargetMode.RangePct, 0m, TargetDollarsBasis.PerContract, true, MinRrAction.Skip),
            (pb.TargetMode, pb.TargetDollars, pb.TargetDollarsBasis, pb.EnforceMinRr, pb.MinRrAction));
        Assert.Contains("\"EnforceMinRr\":true", json);

        var of = entries.Single(e => e.Id == "of").Config;
        Assert.False(of.EnforceMinRr);
        Assert.Equal(MinRrAction.RaiseTarget, of.MinRrAction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""[1,"x"]""")]
    [InlineData("""[{"Id":"lower","config":{"MinRr":2}}]""")]
    public void LeavesWhatItCantSafelyEditUnchanged(string json)
    {
        Insert(2, json);

        Exec(AddTargetModeAndRrGuard.Sql);

        Assert.Equal(json, Read(2));
    }

    [Fact]
    public void LeavesANullBasketNull()
    {
        Insert(3, null);

        Exec(AddTargetModeAndRrGuard.Sql);

        Assert.Null(Read(3));
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~AddTargetModeAndRrGuardTests"`
Expected: build FAILS with `'AddTargetModeAndRrGuard' does not contain a definition for 'Sql'`.

- [ ] **Step 4: Write the migration**

Replace the generated class in `CRV.Core/Migrations/<timestamp>_AddTargetModeAndRrGuard.cs` (keep the file's `using` and `namespace CRV.Core.Migrations` lines) with:

```csharp
    /// <summary>
    /// Writes the target and reward / risk guard settings into every stored basket entry:
    /// TargetMode = RangePct (0), TargetDollars = 0, TargetDollarsBasis = PerContract (0),
    /// EnforceMinRr = true, MinRrAction = Skip (0) — the behaviour every strategy had before
    /// these settings existed. json_insert adds only keys that are absent and only inside a
    /// Config object; a basket that isn't a JSON array of objects is left as it is. The legacy
    /// A–D setups have no columns for these settings and take the same values from the
    /// StrategySetupConfig defaults.
    /// </summary>
    public partial class AddTargetModeAndRrGuard : Migration
    {
        public const string Sql = """
            UPDATE "Configs"
               SET "BasketJson" = (
                   SELECT json_group_array(json(
                            CASE WHEN json_type(e.value, '$.Config') = 'object'
                                 THEN json_insert(e.value,
                                        '$.Config.TargetMode', 0,
                                        '$.Config.TargetDollars', 0,
                                        '$.Config.TargetDollarsBasis', 0,
                                        '$.Config.EnforceMinRr', json('true'),
                                        '$.Config.MinRrAction', 0)
                                 ELSE e.value END))
                     FROM (SELECT value FROM json_each("Configs"."BasketJson") ORDER BY key) AS e)
             WHERE json_valid("BasketJson")
               AND json_type("BasketJson") = 'array'
               AND json_array_length("BasketJson") > 0
               AND NOT EXISTS (SELECT 1 FROM json_each("Configs"."BasketJson") WHERE type <> 'object');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The values written are the defaults the code reads for an absent key, so leaving them is harmless.
        }
    }
```

(`json_array_length > 0` keeps `"[]"` byte-identical; `json_group_array` over no rows would also give `[]`, but the test pins it.)

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~AddTargetModeAndRrGuardTests"`
Expected: PASS, `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Migrations CRV.Core.Tests/Data/AddTargetModeAndRrGuardTests.cs
git commit -m "feat(risk): migration writes target mode and min-R guard defaults into stored baskets"
```

---

### Task 10: Prospectus measures targets like the strategies

**Files:**
- Modify: `CRV.Web/Pages/Dashboard/Prospectus.cshtml.cs:206-232`

**Interfaces:**
- Consumes: `LevelRequest.From`, `LevelCalculator.Targets` (Task 2).

- [ ] **Step 1: Replace the distance maths**

In `CRV.Web/Pages/Dashboard/Prospectus.cshtml.cs`, replace from the comment `// EntryTickOffset shifts the entry price after sl/tp/pp are computed` through `int contracts = sizedCts > 0 ? sizedCts : setup.Contracts;` with:

```csharp
                // EntryTickOffset moves the fill, not the stop: the stop is measured from the signal
                // price, so its distance from the fill grows by the offset (negative offsets reverse
                // it). Target and partial are measured from the fill, after sizing, like the strategies.
                var entryOffsetPts = setup.EntryTickOffset * tickSize;

                // Compute P&L for a given ORB range
                ProspectusRow MakeRow(decimal orbRange)
                {
                    // Skip the offset adjustment when we have no range — otherwise an
                    // empty row would still register a phantom risk = offset*pv*cts.
                    var offset = orbRange > 0 ? entryOffsetPts : 0m;
                    var stopDist = Math.Max(0m, orbRange * stopPct + offset);

                    // Route through AutoSizeByRiskCalculator so projections match runtime
                    // sizing. Synthetic (ep=stopDist, sl=0) — calculator only uses
                    // Math.Abs(ep-sl) × PointValue for riskPerCt.
                    // atrRatio=0 means "not high-vol regime" — projections show baseline
                    // sizing; live high-vol days will scale up via HiVolMult at runtime.
                    var (sizedCts, sizedPartial) = AutoSizeByRiskCalculator.Calc(
                        ep: stopDist, sl: 0m, cfg: setup, atrRatio: 0m);

                    // sizedCts == 0 signals "skip" (AutoSize ON + floor risk > budget).
                    // For projection display fall back to baseline so the row still shows
                    // what the trade WOULD look like absent the budget veto.
                    int contracts = sizedCts > 0 ? sizedCts : setup.Contracts;

                    // Distances from a fill at 0, so the returned prices are the distances.
                    var (tgt, part, _) = orbRange > 0
                        ? LevelCalculator.Targets(LevelRequest.From(setup, 0m, true, -stopDist, contracts, orbRange))
                        : (0m, 0m, 0m);
                    var targetDist  = Math.Max(0m, tgt);
                    var partialDist = Math.Max(0m, part);
```

The rest of `MakeRow` (from `int partialCts = !usePartial` onward) is unchanged.

- [ ] **Step 2: Build and scan the page**

Run: `dotnet build CRV.Web && dotnet test CRV.Web.A11yTests --filter "FullyQualifiedName~PageScanTests&DisplayName~prospectus"`
Expected: build succeeds; the four `/dashboard/prospectus` scans PASS.

- [ ] **Step 3: Commit**

```bash
git add CRV.Web/Pages/Dashboard/Prospectus.cshtml.cs
git commit -m "fix(ui): prospectus measures targets from the fill, dollar targets included"
```

---

### Task 11: Plain words for the target and the guard

**Files:**
- Modify: `CRV.Web/Pages/Setup/StrategyText.cs`
- Create test: `CRV.Web.A11yTests/StrategyTextTests.cs`

**Interfaces:**
- Produces: `StrategyText.Target(StrategySetupConfig c)` ("target $400 / contract" …), `StrategyText.Guard(StrategySetupConfig c)`, `StrategyText.GuardOffNote(int count)`; `Describe(e)` uses the target sentence and appends the guard sentence.

- [ ] **Step 1: Write the failing tests**

`CRV.Web.A11yTests/StrategyTextTests.cs` (plain unit tests: no collection, no browser):

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Web.Pages.Setup;
using Xunit;

namespace CRV.Web.A11yTests;

public class StrategyTextTests
{
    private static StrategySetupConfig Cfg(Action<StrategySetupConfig>? set = null)
    {
        var c = new StrategySetupConfig { TargetPct = 100, MinRr = 1.5m };
        set?.Invoke(c);
        return c;
    }

    private static BasketEntry Entry(StrategySetupConfig c) => new()
    {
        Id = "of-mnq", StrategyType = StrategyType.OrbFakeout, Ticker = "/MNQZ26", Config = c,
    };

    [Fact]
    public void Target_DescribesEveryMode()
    {
        Assert.Equal("target 100% of range", StrategyText.Target(Cfg()));
        Assert.Equal("target $400 / contract", StrategyText.Target(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 400m; })));
        Assert.Equal("target $150 / position", StrategyText.Target(Cfg(c =>
            { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 150m; c.TargetDollarsBasis = TargetDollarsBasis.WholePosition; })));
        Assert.Equal("target $1,500 / contract", StrategyText.Target(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 1500m; })));
        Assert.Equal("target 2R", StrategyText.Target(Cfg(c => { c.TargetMode = TargetMode.RiskMultiple; c.AtrTp2Mult = 2m; })));
    }

    [Fact]
    public void Describe_GuardOff_SaysItTakesTradesBelowTheMinimum()
        => Assert.Contains("Takes trades below 1.5R: the reward / risk guard is off.",
            StrategyText.Describe(Entry(Cfg(c => c.EnforceMinRr = false))));

    [Fact]
    public void Describe_GuardOn_SaysWhatHappensBelowTheMinimum()
    {
        Assert.Contains("Skips trades below 1.5R.", StrategyText.Describe(Entry(Cfg())));
        Assert.Contains("Moves the target out to 1.5R when a trade's stop would leave less.",
            StrategyText.Describe(Entry(Cfg(c => c.MinRrAction = MinRrAction.RaiseTarget))));
    }

    [Fact]
    public void Describe_DollarTarget_NamesTheAmount()
        => Assert.Contains("target is $400 a contract",
            StrategyText.Describe(Entry(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 400m; }))));

    [Fact]
    public void GuardOffNote_CountsStrategies()
    {
        Assert.Equal("1 strategy that's on doesn't enforce its minimum reward / risk, so it can take trades that risk more than they can win.",
            StrategyText.GuardOffNote(1));
        Assert.Equal("3 strategies that are on don't enforce their minimum reward / risk, so they can take trades that risk more than they can win.",
            StrategyText.GuardOffNote(3));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Web.A11yTests --filter "FullyQualifiedName~StrategyTextTests"`
Expected: build FAILS with `'StrategyText' does not contain a definition for 'Target'`.

- [ ] **Step 3: Add the wording**

In `CRV.Web/Pages/Setup/StrategyText.cs` add `using System.Globalization;` at the top, then add these members inside the class (after `Sessions`):

```csharp
    /// <summary>The target in a few words, for the Strategies list.</summary>
    public static string Target(StrategySetupConfig c) => c.TargetMode switch
    {
        TargetMode.Dollars      => $"target {Money(c.TargetDollars)} / {(c.TargetDollarsBasis == TargetDollarsBasis.WholePosition ? "position" : "contract")}",
        TargetMode.RiskMultiple => $"target {Num(c.AtrTp2Mult)}R",
        TargetMode.Atr          => $"target {Num(c.AtrTp2Mult)} × ATR",
        _                       => $"target {c.TargetPct}% of range",
    };

    /// <summary>What happens to a trade below the minimum reward / risk.</summary>
    public static string Guard(StrategySetupConfig c) => !c.EnforceMinRr
        ? $"Takes trades below {Num(c.MinRr)}R: the reward / risk guard is off."
        : c.MinRrAction == MinRrAction.RaiseTarget
            ? $"Moves the target out to {Num(c.MinRr)}R when a trade's stop would leave less."
            : $"Skips trades below {Num(c.MinRr)}R.";

    /// <summary>The Strategies page note for strategies that are on with the guard off.</summary>
    public static string GuardOffNote(int count) => count == 1
        ? "1 strategy that's on doesn't enforce its minimum reward / risk, so it can take trades that risk more than they can win."
        : $"{count} strategies that are on don't enforce their minimum reward / risk, so they can take trades that risk more than they can win.";

    private static string TargetSentence(StrategySetupConfig c) => c.TargetMode switch
    {
        TargetMode.Dollars      => c.TargetDollarsBasis == TargetDollarsBasis.WholePosition
                                       ? $"target is {Money(c.TargetDollars)} for the whole position"
                                       : $"target is {Money(c.TargetDollars)} a contract",
        TargetMode.RiskMultiple => $"target is {Num(c.AtrTp2Mult)}R",
        TargetMode.Atr          => $"target is {Num(c.AtrTp2Mult)} × ATR",
        _                       => $"target is {c.TargetPct}% of the range",
    };

    private static string Money(decimal v) => "$" + v.ToString("#,0.##", CultureInfo.InvariantCulture);
    private static string Num(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
```

Replace the `return` of `Describe` with:

```csharp
        return $"{what} on {e.Ticker}, and {mode}. Stop is {stop}; {TargetSentence(c)}. {exits} {size} {Guard(c)} " +
               $"Up to {c.MaxTrades} trade{(c.MaxTrades == 1 ? "" : "s")} per session, in {Sessions(e)}.";
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test CRV.Web.A11yTests --filter "FullyQualifiedName~StrategyTextTests"`
Expected: PASS, `Failed: 0` (no browser starts: the class has no collection fixture).

- [ ] **Step 5: Commit**

```bash
git add CRV.Web/Pages/Setup/StrategyText.cs CRV.Web.A11yTests/StrategyTextTests.cs
git commit -m "feat(ui): plain words for dollar targets and the reward / risk guard"
```

---

### Task 12: Setup page — target mode, dollar preview, typical stop, guard, save check

**Files:**
- Modify: `CRV.Web/Pages/Setup/Strategy.cshtml.cs`
- Modify: `CRV.Web/Pages/Setup/Strategy.cshtml:20-30,46,150,169-207,257-293`
- Modify: `CRV.Web/wwwroot/css/components.css` (after line 309, the `.st-cutoff` rule)
- Modify: `CRV.Web.A11yTests/A11ySeed.cs`, `A11yPages.cs`, `FixtureTests.cs:36-38`
- Create test: `CRV.Web.A11yTests/StrategyPageTests.cs`

**Interfaces:**
- Consumes: `TypicalStop.FromRuns/Median/MedianPosition/Recent` (Task 6), `MinRrSaveCheck.Check` (Task 7), Task 1 fields.
- Produces: `StrategyModel.TypicalStopPoints`, `.TypicalPositionPoints`, `.TypicalStopTrades`; constants `GuardOnHelp`, `GuardOffHelp`, `ActionSkipHelp`, `ActionRaiseHelp`, `ActionOffHelp`; `static string ActionHelp(StrategySetupConfig c)`. Element ids used by tests: `Entry.Config.TargetDollars`, `st-tmode-Dollars`, `st-basis-WholePosition`, `st-basis-PerContract`, `st-preview-cts`, `st-u-dist`, `st-u-all`, `st-u-mix`, `st-rr-badge`, `Entry.Config.MinRr`, `Entry.Config.MinRrAction`, class `st-rr-off`. Seed ids `A11ySeed.DollarsId = "a11y-dollars"`, `A11ySeed.GuardOffId = "a11y-guard-off"`.

- [ ] **Step 1: Seed the two new states**

In `CRV.Web.A11yTests/A11ySeed.cs`, add the constants after `Ema21Id`:

```csharp
    public const string DollarsId  = "a11y-dollars";
    public const string GuardOffId = "a11y-guard-off";
```

Add two entries to `OrbBasketJson` (after the session-fakeout entry):

```csharp
        DollarEntry(DollarsId,  StrategyType.OrbFakeout, "ORB fakeout $ [MES]", enabled: true,
                    TargetDollarsBasis.WholePosition, 150m, minRr: 1.5m, MinRrAction.Skip),
        DollarEntry(GuardOffId, StrategyType.Pullback,   "Pullback $ [MNQ]",    enabled: false,
                    TargetDollarsBasis.PerContract,   400m, minRr: 2.5m, MinRrAction.RaiseTarget),
```

and the builder after `Entry(...)`:

```csharp
    /// <summary>A dollar-target entry with the reward / risk guard off.</summary>
    private static BasketEntry DollarEntry(string id, StrategyType type, string label, bool enabled,
        TargetDollarsBasis basis, decimal dollars, decimal minRr, MinRrAction action)
    {
        var e = Entry(id, type, label);
        e.Enabled = enabled;
        e.Config = new StrategySetupConfig
        {
            TargetMode = TargetMode.Dollars, TargetDollars = dollars, TargetDollarsBasis = basis,
            EnforceMinRr = false, MinRr = minRr, MinRrAction = action,
        };
        return e;
    }
```

In `A11yPages.Routes` add after the `Ema21Id` route:

```csharp
        "/setup/strategies/" + A11ySeed.DollarsId,
        "/setup/strategies/" + A11ySeed.GuardOffId,
```

In `FixtureTests.SeededStrategyPage_ServesOk` add:

```csharp
    [InlineData("/setup/strategies/" + A11ySeed.DollarsId)]
    [InlineData("/setup/strategies/" + A11ySeed.GuardOffId)]
```

- [ ] **Step 2: Write the failing page tests**

`CRV.Web.A11yTests/StrategyPageTests.cs`:

```csharp
using System.Text.Json;
using CRV.Backtest.Results;
using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>
/// The setup page's dollar-target preview and reward / risk guard. Saving only rewrites the
/// seeded basket (restored afterwards); nothing is sent to a broker and the engine never runs.
/// </summary>
[Collection(A11yCollection.Name)]
public class StrategyPageTests(A11yAppFixture app)
{
    private async Task<IPage> Open(IBrowserContext ctx, string id)
    {
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, "/setup/strategies/" + id).ToString(),
            new() { WaitUntil = WaitUntilState.NetworkIdle });
        return page;
    }

    private static ILocator ById(IPage page, string id) => page.Locator($"[id='{id}']");

    private static async Task<string> Text(IPage page, string id) => (await ById(page, id).TextContentAsync())!.Trim();

    private static Task Save(IPage page) => page.RunAndWaitForNavigationAsync(
        () => page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync());

    [Theory]
    [InlineData(2, "40 pts · $400 over 2 contracts", "$400", "$300 (1 at partial, 1 at target)")]
    [InlineData(1, "80 pts · $400 over 1 contract",  "$400", "No partial with 1 contract")]
    public async Task DollarPreview_WholePosition_ShowsBothOutcomes(int contracts, string dist, string all, string mix)
    {
        // MES at $5 a point, $400 for the whole position, partial at 50%.
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await Open(ctx, A11ySeed.DollarsId);

        await ById(page, "Entry.Config.TargetDollars").FillAsync("400");
        await page.Locator("label[for='st-basis-WholePosition']").ClickAsync();
        await ById(page, "st-preview-cts").FillAsync(contracts.ToString());

        Assert.Equal(dist, await Text(page, "st-u-dist"));
        Assert.Equal(all,  await Text(page, "st-u-all"));
        Assert.Equal(mix,  await Text(page, "st-u-mix"));
    }

    [Fact]
    public async Task GuardOff_GreysItsFieldsAndShowsTheBadge()
    {
        await using var ctx = await app.Browser.NewContextAsync();

        var off = await Open(ctx, A11ySeed.GuardOffId);            // strategy off: grey badge
        Assert.True(await ById(off, "Entry.Config.MinRr").IsDisabledAsync());
        Assert.True(await ById(off, "Entry.Config.MinRrAction").IsDisabledAsync());
        Assert.Equal(2, await off.Locator(".st-rr-off:visible").CountAsync());
        Assert.DoesNotContain("warn", await ById(off, "st-rr-badge").GetAttributeAsync("class"));

        var on = await Open(ctx, A11ySeed.DollarsId);              // strategy on: amber badge
        Assert.Contains("warn", await ById(on, "st-rr-badge").GetAttributeAsync("class"));

        await on.Locator("input[type=checkbox][name='Entry.Config.EnforceMinRr']").CheckAsync();
        Assert.False(await ById(on, "Entry.Config.MinRr").IsDisabledAsync());
        Assert.Equal(0, await on.Locator(".st-rr-off:visible").CountAsync());
    }

    [Fact]
    public async Task SaveBelowMinimum_WithBacktestHistory_IsBlocked()
    {
        // Three backtest trades with a 40-point stop: 1.5R x 40 pts x $5 = $300 a contract minimum.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var trades = Enumerable.Range(1, 3).Select(d => new TradeRecord
        {
            SetupLabel = A11ySeed.DollarsId, Ticker = "/MESZ26", Direction = Direction.Long, Contracts = 1,
            Entry = 6000m, InitialStop = 5960m, Target = 6080m, Exit = 6080m, ExitReason = ExitReason.Target,
            EnteredAt = DateTime.UtcNow.Date.AddDays(-d), ExitedAt = DateTime.UtcNow.Date.AddDays(-d).AddMinutes(30),
        }).ToList();
        var run = new BacktestRunRow
        {
            Ticker = "/MESZ26", ConfigName = "a11y-history", RunAt = DateTime.UtcNow,
            ResultJson = JsonSerializer.Serialize(new BacktestResult { Trades = trades }),
        };
        db.BacktestRuns.Add(run);
        db.SaveChanges();
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, A11ySeed.DollarsId);
            Assert.Contains("40 pts", await page.Locator(".st-field", new() { HasText = "Typical stop" }).TextContentAsync());

            await page.Locator("input[type=checkbox][name='Entry.Config.EnforceMinRr']").CheckAsync();
            await page.Locator("label[for='st-basis-PerContract']").ClickAsync();
            await ById(page, "Entry.Config.TargetDollars").FillAsync("50");
            await Save(page);

            Assert.Contains("Raise the target to at least $300 a contract",
                await page.Locator(".c-note.bad[role=alert]").TextContentAsync());
        }
        finally
        {
            db.BacktestRuns.Remove(run);
            db.SaveChanges();
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }

    [Fact]
    public async Task SaveWithoutHistory_PassesWithTheWarning()
    {
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, A11ySeed.DollarsId);

            await page.Locator("input[type=checkbox][name='Entry.Config.EnforceMinRr']").CheckAsync();
            await ById(page, "Entry.Config.TargetDollars").FillAsync("50");
            await Save(page);

            Assert.Contains("The reward / risk check runs once this strategy has a backtest.",
                await page.Locator(".c-note.warn").First.TextContentAsync());
        }
        finally
        {
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }

    [Fact]
    public async Task SaveWithGuardOff_KeepsStoredMinimumAndAction()
    {
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, A11ySeed.GuardOffId);

            await Save(page);

            var cfg = app.Services.GetRequiredService<StrategyBasketService>().Find(A11ySeed.GuardOffId)!.Entry.Config;
            Assert.False(cfg.EnforceMinRr);
            Assert.Equal(2.5m, cfg.MinRr);
            Assert.Equal(MinRrAction.RaiseTarget, cfg.MinRrAction);
        }
        finally
        {
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~StrategyPageTests"`
Expected: FAIL — `Timeout … waiting for Locator("[id='Entry.Config.TargetDollars']")` (the field doesn't exist yet).

- [ ] **Step 4: Page model — typical stop, help text, save check**

In `CRV.Web/Pages/Setup/Strategy.cshtml.cs`:

Add usings:

```csharp
using CRV.Core.Data;
using Microsoft.EntityFrameworkCore;
```

Replace the fields/constructor (lines 17–23) with:

```csharp
    /// <summary>How many recent backtest runs the typical stop is read from.</summary>
    private const int RunsScanned = 20;

    public const string GuardOnHelp     = "Checks the target when you save, and every trade before it's sent.";
    public const string GuardOffHelp    = "Off: no check on save, and trades are taken whatever their reward / risk. Your minimum R and what to do below it are kept for when you turn it back on.";
    public const string ActionSkipHelp  = "Each trade is still checked: if its stop makes the target less than {r}R, the trade is skipped.";
    public const string ActionRaiseHelp = "Each trade is still checked: if its stop makes the target less than {r}R, the target moves out to {r}R for that trade.";
    public const string ActionOffHelp   = "Not enforced: turn on “Enforce minimum reward / risk” under Size and limits.";

    public static string ActionHelp(StrategySetupConfig c) =>
        (!c.EnforceMinRr ? ActionOffHelp : c.MinRrAction == MinRrAction.RaiseTarget ? ActionRaiseHelp : ActionSkipHelp)
        .Replace("{r}", c.MinRr.ToString("0.##", CultureInfo.InvariantCulture));

    private readonly StrategyBasketService _basket;
    private readonly LiveEngineOrchestrator _engine;
    private readonly TradingDbContext _db;

    public StrategyModel(StrategyBasketService basket, LiveEngineOrchestrator engine, TradingDbContext db)
    {
        _basket = basket; _engine = engine; _db = db;
    }
```

Add after `public string? RemoveError { get; private set; }`:

```csharp
    /// <summary>Median stop of the last 30 backtest trades, in points; null without a backtest.</summary>
    public decimal? TypicalStopPoints { get; private set; }
    /// <summary>Median of contracts × stop over the same trades, in points.</summary>
    public decimal? TypicalPositionPoints { get; private set; }
    public int TypicalStopTrades { get; private set; }
```

Replace `OnGet` with:

```csharp
    public async Task<IActionResult> OnGetAsync()
    {
        if (!Load()) return NotFound();
        await LoadTypicalStopAsync();
        Saved = TempData["strategy_saved"] as string;
        RemoveError = TempData["strategy_error"] as string;
        if (TempData["strategy_warnings"] is string w) Warnings.AddRange(w.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return Page();
    }
```

In `OnPostSaveAsync`, replace from `Validate(edited);` through `if (change.Warnings.Count > 0) TempData["strategy_warnings"] = string.Join("\n", change.Warnings);` with:

```csharp
        Validate(edited);
        await LoadTypicalStopAsync();
        var rr = MinRrSaveCheck.Check(edited.Config, edited.PointValue, TypicalStopPoints, TypicalPositionPoints);
        if (rr.Error != null) Errors.Add(rr.Error);
        if (Errors.Count > 0)
        {
            Entry = edited;
            return Page();
        }

        var change = _basket.Replace(Id, edited, Who);
        if (!change.Ok) { Errors.Add(change.Error!); Entry = edited; return Page(); }

        TempData["strategy_saved"] = RestartNote(before, edited);
        var warnings = rr.Warning is { } guardWarning ? change.Warnings.Append(guardWarning).ToList() : change.Warnings.ToList();
        if (warnings.Count > 0) TempData["strategy_warnings"] = string.Join("\n", warnings);
```

In `Validate`, replace `if (c.TargetPct <= 0) Errors.Add("Target must be more than 0% of the range.");` with:

```csharp
        if (c.TargetMode == TargetMode.RangePct && c.TargetPct <= 0) Errors.Add("Target must be more than 0% of the range.");
        if (c.TargetMode == TargetMode.Dollars && c.TargetDollars <= 0) Errors.Add("Target must be more than $0.");
```

Add the loader after `Load()`:

```csharp
    private async Task LoadTypicalStopAsync()
    {
        var runs = await _db.BacktestRuns.OrderByDescending(r => r.RunAt).Take(RunsScanned)
            .Select(r => r.ResultJson).ToListAsync();
        var trades = TypicalStop.FromRuns(runs, Id);
        TypicalStopTrades     = TypicalStop.Recent(trades).Count;
        TypicalStopPoints     = TypicalStop.Median(trades);
        TypicalPositionPoints = TypicalStop.MedianPosition(trades);
    }
```

- [ ] **Step 5: Page markup**

In `CRV.Web/Pages/Setup/Strategy.cshtml`:

(a) `Num` helper (lines 20–30): add a `mode` parameter and attribute:

```cshtml
    void Num(string name, string label, string value, string? help = null, string step = "any", string? types = null, string? unit = null, string? mode = null)
    {
        <div class="st-field" data-types="@types" data-target-mode="@mode">
```

(the rest of the helper is unchanged).

(b) Head badge — after `<span class="c-sub">…</span>` (line 46):

```cshtml
    <span class="c-badge st-rr-off @(e.Enabled ? "warn" : "")" id="st-rr-badge" title="Minimum reward / risk is not enforced for this strategy" hidden="@c.EnforceMinRr"><i class="bi bi-exclamation-triangle"></i>R:R not enforced</span>
```

(c) Delete line 150 from the Entry panel: `@{ Num("Entry.Config.MinRr", "Minimum reward / risk", N(c.MinRr), unit: "R"); }` (it moves to Size and limits).

(d) In "Stop and exits", replace the target line (line 182) `@{ Num("Entry.Config.TargetPct", "Target", c.TargetPct.ToString(inv), step: "1", unit: "% of range"); }` with:

```cshtml
                <div class="st-field st-wide">
                    <span class="form-label" id="st-tmode-l">Targets measured in</span>
                    <div><div class="c-seg" role="radiogroup" aria-labelledby="st-tmode-l">
                        @foreach (var (v, label) in new[] { (TargetMode.RangePct, "% of range"), (TargetMode.Dollars, "Dollars ($)") })
                        {
                            <input type="radio" class="btn-check" name="Entry.Config.TargetMode" id="st-tmode-@v" value="@v" checked="@(c.TargetMode == v)" />
                            <label class="btn btn-sm" for="st-tmode-@v">@label</label>
                        }
                    </div></div>
                </div>
                @{ Num("Entry.Config.TargetPct", "Target", c.TargetPct.ToString(inv), step: "1", unit: "% of range", mode: "RangePct"); }
                <div class="st-field" data-target-mode="Dollars">
                    <label class="form-label" for="Entry.Config.TargetDollars">Target</label>
                    <div class="st-input"><span class="c-mut">$</span><input class="form-control num" id="Entry.Config.TargetDollars" name="Entry.Config.TargetDollars" type="number" step="any" min="0" inputmode="decimal" value="@N(c.TargetDollars)" /></div>
                </div>
                <div class="st-field" data-target-mode="Dollars">
                    <span class="form-label" id="st-basis-l">Dollars are</span>
                    <div class="c-seg c-seg-fill" role="radiogroup" aria-labelledby="st-basis-l">
                        @foreach (var (v, label) in new[] { (TargetDollarsBasis.PerContract, "Per contract"), (TargetDollarsBasis.WholePosition, "Whole position") })
                        {
                            <input type="radio" class="btn-check" name="Entry.Config.TargetDollarsBasis" id="st-basis-@v" value="@v" checked="@(c.TargetDollarsBasis == v)" />
                            <label class="btn btn-sm" for="st-basis-@v">@label</label>
                        }
                    </div>
                </div>
                <div class="st-field" data-target-mode="Dollars">
                    <label class="form-label" for="st-preview-cts">Example: contracts after sizing</label>
                    <input class="form-control num" id="st-preview-cts" type="number" step="1" min="1" inputmode="numeric" value="@Math.Max(1, c.Contracts)" />
                    <div class="form-text">Only for the preview below. Size by risk picks the real number per trade.</div>
                </div>
                <div class="st-field" data-target-mode="Dollars">
                    <span class="form-label">Typical stop for this strategy</span>
                    <div class="form-control-plaintext num">@(Model.TypicalStopPoints is decimal ts
                        ? $"{N(ts)} pts · ${(ts * e.PointValue).ToString("N0", inv)} a contract" + (Model.TypicalPositionPoints is decimal tp ? $" · ${(tp * e.PointValue).ToString("N0", inv)} a typical position" : "")
                        : "No backtest yet")</div>
                    <div class="form-text">@(Model.TypicalStopPoints is null ? MinRrSaveCheck.NoHistory : $"Median of its last {Model.TypicalStopTrades} backtest trades. Used to check the target when you save.")</div>
                </div>
                <div class="st-field st-wide" data-target-mode="Dollars">
                    <div class="st-usd" aria-live="polite">
                        <div class="c-kv"><span>Target distance</span><span id="st-u-dist"></span></div>
                        <div class="c-kv"><span>Partial</span><span id="st-u-part"></span></div>
                        <div class="c-kv"><span>All contracts at target</span><span id="st-u-all"></span></div>
                        <div class="c-kv"><span>Partial, then the rest at target</span><span id="st-u-mix"></span></div>
                    </div>
                </div>
                <div class="st-field">
                    <label class="form-label" for="Entry.Config.MinRrAction">If one trade's stop is wider than usual</label>
                    <select class="form-select" id="Entry.Config.MinRrAction" name="Entry.Config.MinRrAction" aria-describedby="st-rr-action-help" disabled="@(!c.EnforceMinRr)">
                        <option value="Skip" selected="@(c.MinRrAction == MinRrAction.Skip)">Skip the trade</option>
                        <option value="RaiseTarget" selected="@(c.MinRrAction == MinRrAction.RaiseTarget)">Raise the target to the minimum R</option>
                    </select>
                    <div class="form-text" id="st-rr-action-help" data-skip="@StrategyModel.ActionSkipHelp" data-raise="@StrategyModel.ActionRaiseHelp" data-off="@StrategyModel.ActionOffHelp">@StrategyModel.ActionHelp(c)</div>
                </div>
```

(e) In "Size and limits", after the `MaxShortTrades` line (line 206) and before the panel body closes:

```cshtml
                <label class="c-switch">
                    <span>Enforce minimum reward / risk<small class="d-block c-mut" id="st-rr-help" data-on="@StrategyModel.GuardOnHelp" data-off="@StrategyModel.GuardOffHelp">@(c.EnforceMinRr ? StrategyModel.GuardOnHelp : StrategyModel.GuardOffHelp)</small></span>
                    <input class="form-check-input" type="checkbox" role="switch" name="Entry.Config.EnforceMinRr" value="true" @(c.EnforceMinRr ? "checked" : "") />
                    <input type="hidden" name="Entry.Config.EnforceMinRr" value="false" />
                </label>
                <div class="st-field">
                    <label class="form-label" for="Entry.Config.MinRr">Minimum reward / risk</label>
                    <div class="st-input">
                        <input class="form-control num" id="Entry.Config.MinRr" name="Entry.Config.MinRr" type="number" step="any" inputmode="decimal" value="@N(c.MinRr)" disabled="@(!c.EnforceMinRr)" />
                        <span class="c-mut">R</span>
                        <span class="c-badge st-rr-off" hidden="@c.EnforceMinRr">Not enforced</span>
                    </div>
                </div>
```

(f) In the `@section Scripts` `<script>`, add a second IIFE after the instrument-picker IIFE's `})();`:

```javascript
(function () {
    // Target mode fields, the dollar preview and the reward / risk guard, kept in step with the form.
    const form = document.getElementById('st-form');
    const $ = id => document.getElementById(id);
    const picked = name => (form.querySelector(`input[name="${name}"]:checked`) || {}).value;
    const box = name => form.querySelector(`input[type=checkbox][name="${name}"]`);
    const num = id => parseFloat($(id).value) || 0;
    const money = v => '$' + Math.round(v).toLocaleString('en-US');
    const pts = v => v.toLocaleString('en-US', { maximumFractionDigits: 2 }) + ' pts';

    function preview() {
        const mode = picked('Entry.Config.TargetMode');
        form.querySelectorAll('[data-target-mode]').forEach(el => {
            if (el.dataset.targetMode) el.hidden = el.dataset.targetMode !== mode;
        });
        const pv = num('st-pv'), tick = num('st-tick') || 0.25;
        if (mode !== 'Dollars' || pv <= 0) return;
        const perContract = picked('Entry.Config.TargetDollarsBasis') !== 'WholePosition';
        const dollars = num('Entry.Config.TargetDollars');
        const cts = Math.max(1, Math.floor(num('st-preview-cts')));
        const toTick = v => Math.round(v / tick) * tick;
        const dist = toTick(perContract ? dollars / pv : dollars / (pv * cts));
        const pDist = toTick(dist * num('Entry.Config.PartialPct') / 100);
        const usePartial = box('Entry.Config.UsePartial').checked;
        const pc = num('Entry.Config.PartialCts');
        // Same split as sizing and the bracket builder: runner-only with size-by-risk, else the set count, else half.
        const partCts = !usePartial || cts < 2 ? 0
            : box('Entry.Config.AutoSizeByRisk').checked && pc > 0 ? cts - 1
            : pc > 0 ? Math.min(pc, cts - 1)
            : Math.floor(cts / 2);
        $('st-u-dist').textContent = pts(dist) + ' · ' + money(dollars) +
            (perContract ? ' a contract' : ' over ' + cts + ' contract' + (cts === 1 ? '' : 's'));
        $('st-u-part').textContent = pts(pDist) + ' · ' + money(pDist * pv) + ' a contract';
        $('st-u-all').textContent = money(dist * pv * cts);
        $('st-u-mix').textContent = !usePartial ? 'No partial: taking a partial is off'
            : partCts > 0 ? money(pDist * pv * partCts + dist * pv * (cts - partCts)) + ' (' + partCts + ' at partial, ' + (cts - partCts) + ' at target)'
            : 'No partial with 1 contract';
    }

    function guard() {
        const on = box('Entry.Config.EnforceMinRr').checked;
        const r = $('Entry.Config.MinRr').value || '0';
        $('Entry.Config.MinRr').disabled = !on;
        $('Entry.Config.MinRrAction').disabled = !on;
        document.querySelectorAll('.st-rr-off').forEach(b => b.hidden = on);
        $('st-rr-badge').classList.toggle('warn', box('Entry.Enabled').checked);
        const help = $('st-rr-help');
        help.textContent = on ? help.dataset.on : help.dataset.off;
        const act = $('st-rr-action-help');
        act.textContent = (!on ? act.dataset.off
            : $('Entry.Config.MinRrAction').value === 'RaiseTarget' ? act.dataset.raise : act.dataset.skip).replaceAll('{r}', r);
    }

    const sync = () => { preview(); guard(); };
    form.addEventListener('input', sync);
    form.addEventListener('change', sync);
    sync();
})();
```

(`data-target-mode` with a null `mode` is omitted by Razor, so `el.dataset.targetMode` is only set on the target fields.)

- [ ] **Step 6: Styles**

In `CRV.Web/wwwroot/css/components.css`, after the `.st-cutoff` rule (line 309):

```css
.st-wide { grid-column: 1 / -1; }
.st-usd { border: 1px solid var(--c-line); border-radius: var(--radius-sm); padding: .1rem .75rem; background: var(--c-raise); }
```

- [ ] **Step 7: Run the page tests and the gate**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~StrategyPageTests|FullyQualifiedName~FixtureTests|DisplayName~setup/strategies"`
Expected: PASS, `Failed: 0` — the five `StrategyPageTests`, `SeededStrategyPage_ServesOk` for both new routes, and every `/setup/strategies*` scan (dark/light × desktop/phone), including `a11y-dollars` (amber badge, greyed guard) and `a11y-guard-off` (grey badge).

- [ ] **Step 8: Commit**

```bash
git add CRV.Web/Pages/Setup/Strategy.cshtml CRV.Web/Pages/Setup/Strategy.cshtml.cs CRV.Web/wwwroot/css/components.css CRV.Web.A11yTests
git commit -m "feat(ui): dollar targets, typical stop and the reward / risk guard on the setup page"
```

---

### Task 13: Strategies list and cockpit — badges, note, skip line

**Files:**
- Modify: `CRV.Web/Pages/Setup/Strategies.cshtml:21-26,52-55`, `Strategies.cshtml.cs`
- Modify: `CRV.Core/Models/Signals.cs:290` (`SetupSnapshot`)
- Modify: `CRV.Core/Strategy/SnapshotAggregator.cs:279`
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml:1043-1070,1468-1472`
- Modify: `CRV.Web/wwwroot/css/components.css`
- Test: `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs`, `CRV.Web.A11yTests/CockpitSnapshot.cs`, `CRV.Web.A11yTests/CockpitCardTests.cs`

**Interfaces:**
- Consumes: `SetupStateSnapshot.MinRrEnforced/MinRr/LastSkip` (Tasks 3–5), `StrategyText.Target/GuardOffNote` (Task 11).
- Produces: `SetupSnapshot.MinRrEnforced` (bool, default true), `.MinRr`, `.LastSkip` (camelCase on the hub: `minRrEnforced`, `minRr`, `lastSkip`); `StrategiesModel.GuardOffCount`; cockpit element ids `rr-{cardId}`, `{cardId}-skip`.

- [ ] **Step 1: Write the failing snapshot test**

Append inside `SnapshotAggregatorTests`:

```csharp
    [Fact]
    public void Setup_CarriesTheRewardRiskGuardAndLastSkip()
    {
        var ss = new SetupStateSnapshot
        {
            SetupId = SetupId.A, Enabled = true,
            MinRrEnforced = false, MinRr = 1.5m, LastSkip = "Skipped: 1.2R below 1.5R",
        };

        var a = FindSetup(SnapshotAggregator.Build(DefaultInputs(MakeStub(SetupId.A, ss))), "A");

        Assert.False(a.MinRrEnforced);
        Assert.Equal(1.5m, a.MinRr);
        Assert.Equal("Skipped: 1.2R below 1.5R", a.LastSkip);
    }
```

- [ ] **Step 2: Write the failing cockpit test**

In `CRV.Web.A11yTests/CockpitSnapshot.cs`, add a public helper after `Setup(...)`:

```csharp
    /// <summary>A card whose strategy takes trades below its minimum R and has just skipped one.</summary>
    public static SetupSnapshot GuardOff(SetupSnapshot s)
    {
        s.MinRrEnforced = false;
        s.MinRr = 1.5m;
        s.LastSkip = "Skipped: 1.2R below 1.5R";
        return s;
    }
```

Add it to `EveryState()` after the `a11y-short` entry:

```csharp
            GuardOff(Setup("a11y-rr-off", "Pullback $ [MNQ]", "Pullback", state: 0)),
```

and append `"IDLE"` to `ExpectedStatuses`.

Append inside `CockpitCardTests`:

```csharp
    [Fact]
    public async Task GuardOffCard_ShowsTheAmberBadgeAndTheLastSkip()
    {
        await using var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, "/dashboard").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.EvaluateAsync("""
            json => {
                CRV.engine.status('Live');
                document.dispatchEvent(new CustomEvent('crv:update', { detail: JSON.parse(json) }));
            }
            """, CockpitSnapshot.Json([CockpitSnapshot.GuardOff(CockpitSnapshot.Setup("a11y-rr", "Pullback $ [MNQ]", "Pullback", state: 0))]));

        var badge = page.Locator("#rr-a11y-rr");
        await badge.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal("R:R not enforced", (await badge.TextContentAsync())!.Trim());
        Assert.Contains("warn", await badge.GetAttributeAsync("class"));
        Assert.Equal("Skipped: 1.2R below 1.5R", (await page.Locator("#a11y-rr-skip").TextContentAsync())!.Trim());
    }
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SnapshotAggregatorTests"`
Expected: build FAILS with `'SetupSnapshot' does not contain a definition for 'MinRrEnforced'`.

- [ ] **Step 4: Carry the fields to the dashboard snapshot**

In `CRV.Core/Models/Signals.cs`, inside `SetupSnapshot`, after `public bool     OrbFormed      { get; set; }` (line 291):

```csharp

    // Reward / risk guard
    public bool     MinRrEnforced  { get; set; } = true;
    public decimal  MinRr          { get; set; }
    /// <summary>The last trade skipped for reward / risk this session, in words; null when none.</summary>
    public string?  LastSkip       { get; set; }
```

In `CRV.Core/Strategy/SnapshotAggregator.cs`, in the `new SetupSnapshot { … }` initializer, after `Expectancy   = CalcExpectancy(...),` (line 280):

```csharp
                MinRrEnforced = ss.MinRrEnforced,
                MinRr        = ss.MinRr,
                LastSkip     = ss.LastSkip,
```

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SnapshotAggregatorTests"`
Expected: PASS.

- [ ] **Step 5: Strategies list — badge, target, note**

In `CRV.Web/Pages/Setup/Strategies.cshtml.cs`, after `public bool LegacyInUse { get; private set; }`:

```csharp
    /// <summary>Strategies that are on and take trades below their minimum reward / risk.</summary>
    public int GuardOffCount => Items.Count(i => i.Entry.Enabled && !i.Entry.Config.EnforceMinRr);
```

In `CRV.Web/Pages/Setup/Strategies.cshtml`, after the "No strategy is on" note block (line 26):

```cshtml
@if (Model.GuardOffCount > 0)
{
    <div class="c-note warn mb-3"><i class="bi bi-exclamation-triangle"></i><span>@StrategyText.GuardOffNote(Model.GuardOffCount)</span></div>
}
```

Replace the row's main link contents (lines 53–54):

```cshtml
                <span class="st-row-title"><b>@name</b>@if (!e.Config.EnforceMinRr) {<span class="c-badge @(e.Enabled ? "warn" : "")" title="Minimum reward / risk is not enforced for this strategy">R:R not enforced</span>}</span>
                <small class="c-mut">@StrategyText.TypeName(e.StrategyType) · @e.Ticker · @StrategyText.Sessions(e) · @StrategyText.Target(e.Config)</small>
```

- [ ] **Step 6: Cockpit card — badge and skip line**

In `CRV.Web/Pages/Dashboard/Index.cshtml`, in `createSetupCard`, after `<b class="ck-name">${_esc(setup.label)}</b>`:

```javascript
            <span class="c-badge ck-rr" id="rr-${id}" hidden>R:R not enforced</span>
```

and after the closing `</div>` of `<div class="ck-grid">…</div>`:

```javascript
        <p class="ck-skip c-mut" id="${id}-skip" hidden></p>
```

In `updateSetup`, after the `// ON / OFF badge` block (before `// When disabled, show DISABLED…`):

```javascript
    // Reward / risk guard: amber while the setup is on, grey while it's off; the last skip in words.
    const rrEl = document.getElementById("rr-" + id);
    if (rrEl) {
        rrEl.hidden = setup.minRrEnforced !== false;
        rrEl.className = "c-badge ck-rr" + (enabled ? " warn" : "");
        rrEl.title = "Minimum reward / risk is not enforced: this setup can take trades below " + Number(setup.minRr ?? 0) + "R";
    }
    const skipEl = document.getElementById(id + "-skip");
    if (skipEl) { skipEl.textContent = setup.lastSkip || ""; skipEl.hidden = !setup.lastSkip; }
```

- [ ] **Step 7: Styles**

In `CRV.Web/wwwroot/css/components.css`, after `.st-row-main small { … }` (line 295):

```css
.st-row-title { display: flex; flex-wrap: wrap; align-items: center; gap: .4rem; }
```

and after `.ck-cell > b { … }` (line 170):

```css
.ck-rr { flex-basis: auto; }
.ck-skip { margin: 0; padding: 0 .9rem .7rem; font-size: .8rem; }
```

- [ ] **Step 8: Run the cockpit and Strategies tests and scans**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests --no-build --filter "FullyQualifiedName~CockpitCardTests|FullyQualifiedName~CockpitSetupCards|DisplayName~setup/strategies"`
Expected: PASS, `Failed: 0` — the new card test, the cockpit card scans with the guard-off card (8 statuses), and the Strategies page scans with the note, the amber row badge (`a11y-dollars`) and the grey one (`a11y-guard-off`).

- [ ] **Step 9: Commit**

```bash
git add CRV.Core/Models/Signals.cs CRV.Core/Strategy/SnapshotAggregator.cs CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs CRV.Web/Pages/Setup/Strategies.cshtml CRV.Web/Pages/Setup/Strategies.cshtml.cs CRV.Web/Pages/Dashboard/Index.cshtml CRV.Web/wwwroot/css/components.css CRV.Web.A11yTests/CockpitSnapshot.cs CRV.Web.A11yTests/CockpitCardTests.cs
git commit -m "feat(ui): R:R not enforced badges, guard-off note and the cockpit skip line"
```

---

### Task 14: Docs and full verification

**Files:**
- Modify: `docs/risk.md` (new section before `## Arming a setup`)

- [ ] **Step 1: Document the guard**

Insert before `## Arming a setup` in `docs/risk.md`:

```markdown
## Targets and the reward / risk guard

Every strategy sets its target one of two ways on the setup page: a share of the range
(`TargetMode = RangePct`, `TargetPct`) or dollars (`Dollars`, `TargetDollars`), per contract
or for the whole position (`TargetDollarsBasis`). A dollar target becomes points by the
instrument's point value — MNQ $400 a contract is 200 pts, NQ $400 a contract is 20 pts —
and a whole-position target is also divided by the contract count **after** sizing, so
$400 over 2 MNQ contracts is 100 pts. `PartialPct` is a share of that distance.

Each strategy works in this order: entry (tick offset applied) → final stop → size →
target and partial → reward / risk from the fill → guard → signal. Targets are measured
from the fill; stops from the signal price.

**The guard** (`EnforceMinRr`, on by default):

- **On save**, the target must be at least `MinRr` × the strategy's typical stop. For a
  range target that is `MinRr × StopPct` of the range (the stop setting). For a dollar
  target it is the median stop of the strategy's last 30 backtest trades (contracts × stop
  for a whole-position target). Without a backtest the save goes through with "The reward /
  risk check runs once this strategy has a backtest."
- **Per trade**, below `MinRr`: `Skip` drops the trade and records it like a size refusal —
  a `RISK` alert ("Skipped: 1.2R below 1.5R"), a warning log line, a line on the cockpit card,
  and `MinRrSkips` in backtest results — or `RaiseTarget` moves the target out to
  `MinRr × risk` with the partial recomputed.
- **Off**: no save check and no per-trade check. The strategy shows "R:R not enforced" on
  its setup page, its Strategies row and its cockpit card, and the Strategies page counts
  the strategies that are on with the guard off. Backtest results record each setup's guard.

A trade with no reward or no risk (a target or stop on the entry) is never sent, guard on or off.

Pinned by `LevelCalculatorTests`, `MinRrGuardTests`, `MinRrSaveCheckTests`, `TypicalStopTests`,
the `TickOffset_*`, `WholePositionDollars_*` and `BelowMinimum_RaiseTarget_*` tests on each of
the four ORB strategies, `MinRrGuardReachesTheResultTests` and `AddTargetModeAndRrGuardTests`.
```

- [ ] **Step 2: Full build and test**

Run: `dotnet build CRV.Trading.sln && dotnet test CRV.Core.Tests && dotnet test CRV.Web.A11yTests`
Expected: build succeeds with no new warnings; `CRV.Core.Tests` `Failed: 0` (959 + the new tests); `CRV.Web.A11yTests` `Failed: 0` (93 + 8 new page scans + 2 fixture routes + 6 strategy-page tests + 5 strategy-text tests + 1 cockpit card test = 115).

- [ ] **Step 3: Commit**

```bash
git add docs/risk.md
git commit -m "docs: targets and the reward / risk guard"
```

- [ ] **Step 4: Security review and PR**

Run the `security-review` skill on the branch, fix or report its findings, then open the PR against `master` with the Decisions section above in its description (Decision 1 is a behaviour change for setups with an entry tick offset).

---

## Self-Review

**Spec coverage**

| Spec requirement | Task |
|---|---|
| Five config fields, defaults, `BasketEntry.Config`, `ToSetupConfig`, legacy builders | 1 |
| Distance per contract / whole position, tick rounding, `PartialPct` | 2 |
| One `LevelCalculator` overload used at every call site; `CalcLevels(B)` as wrappers | 2, 4, 5 |
| New order: offset → stop → size → target → R from final entry → guard | 4, 5 |
| Pullback and Retest R from the final entry | 5 |
| Guard per trade: Skip recorded via the size-refusal path; RaiseTarget with partial recomputed | 3, 4, 5 |
| Skip reaches log, alert feed and cockpit card | 3 (refusal path), 13 |
| Typical stop = median of last 30 backtest trades by setup id; none → warning | 6, 12 |
| Save check per mode, boundary, error names the minimum in the field's unit | 7, 12 (RangePct per Decision 2, Atr per Decision 4) |
| Guard off: no checks, fields greyed "Not enforced" | 3, 7, 12 |
| Badge on head / row / card, amber vs grey | 12, 13 |
| Strategies note counting on-with-guard-off | 11, 13 |
| Backtest results record guard state | 8 |
| `StrategyText` "Takes trades below X R"; row target descriptions | 11, 13 |
| Setup page: points, partial $, both outcomes, example contracts, mockup layout, WCAG | 12 |
| Migration `AddTargetModeAndRrGuard` | 9 |
| Tests 1–7 of the spec | 2 (1), 4–5 (2), 4 (3), 7 + 12 (4), 3–5 + 8 (5), 3 + 7 + 12 (6), 1 + 9 (7) |

**Placeholder scan:** every code step has the code; the only generated name is the migration timestamp, produced by `dotnet ef` in Task 9 Step 1.

**Type consistency:** `LevelRequest` fields match the shared contract (`Entry, IsLong, Stop, Contracts, Mode, RangeOrAtr, TargetPct, TargetDollars, Basis, PointValue, PartialPct, TickSize, Tp1Mult = 0, Tp2Mult = 0`); `Targets` returns `(Target, Partial, Rr)`; `RaiseToMinRr(LevelRequest, decimal)` returns `(Target, Partial)`; `TypicalStop.Median(IEnumerable<TradeRecord>, int last = 30)` returns `decimal?`. `SizeRefusalGate.ReportMinRr` / `LastMinRrSkip`, `MinRrGuard.Apply`, `GuardedLevels.Skip`, `SetupStateSnapshot`/`SetupSnapshot` `MinRrEnforced/MinRr/LastSkip`, `MinRrSaveCheck.Check(cfg, pointValue, typicalStop, typicalPosition)` are spelled the same in every task that uses them.

**Review Focus:** the five lines above each have a named test in the owning task (Tasks 3, 4, 9, 12).
