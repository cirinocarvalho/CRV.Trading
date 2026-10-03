# Higher-Timeframe Bars and History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build session-anchored higher-timeframe bars from execution bars, a TradingView-matching generic EMA, and a per-root stored bar history filled from Schwab (backfill, CSV import, live top-ups), with the enable/parity rules the EMA strategy needs.

**Architecture:** One shared `EasternTime` helper owns time-zone lookup. `SessionBucket` maps any UTC moment to its bucket for each `SignalTimeframe` (CME Globex session, 18:00–17:00 ET), and `SessionBarAggregator` closes buckets from execution bars without lookahead. A new `HtfBars` table, read and written only through `HtfBarStore`, holds one continuous history per root; `SchwabHistoryBackfill` (behind an `IPriceHistoryFetcher` so tests never call Schwab), `HtfCsvImport` and the live `HtfTopUp` all write through it. `HistoryRequirement` turns stored counts into Blocked / ParityWarning / Ready and the History panel's copy.

**Tech Stack:** .NET 10, EF Core 10 on SQLite, xUnit 2.9.3, ASP.NET Core controllers.

**Spec:** `docs/superpowers/specs/2026-10-02-htf-bars-and-history-design.md`

**Requires:** `docs/superpowers/plans/2026-10-02-ema21-removal.md` (plan 1: one bar size per root, `SetupValidation.BarMinutes`; roots come from the existing `TickerGroup.GetGroupKey`), plus plans 2 and 3 merged first per the build order (this plan uses nothing from them).

## Global Constraints

- Branch `feat/htf-bars-and-history` from `master` after plans 1–3 are merged. Conventional Commit subjects (`feat(history):`, `refactor:`, `test:`, `docs:`).
- .NET 10, xUnit 2.9.3 with `using Xunit;` in every test file, test names `Subject_Condition_Expectation`, nullable enabled, file-scoped namespaces in new files.
- Tests never construct `SchwabPriceHistoryFetcher`, `SchwabAuthService` or an `HttpClient`, and never touch the network: backfill logic is tested against fake `IPriceHistoryFetcher`s. Agents never call `POST /api/history/{root}/backfill` against Schwab — Cirino runs it (Task 12).
- Never place orders, start the engine, or run the web app (`dotnet run`). Builds and `dotnet test` only.
- Do not read or print `CRV.Web/*tokens*.json`; do not open the repo's `crv_trading.db` (migration checks use a temp DB file).
- Bucket rules, verbatim from the spec: H4 opens 18:00, 22:00, 02:00, 06:00, 10:00, 14:00 ET and the 14:00 bar ends at 17:00; H8 opens 18:00, 02:00, 10:00–17:00 (7 hours); D1 is the trading date, 18:00 to 17:00; W1 is Sunday 18:00 to Friday 17:00; MN1 is by trading-date month; M5 … H1 are clock-aligned.
- "A bar closes as soon as the execution bar ending on its boundary is processed." On a holiday or early close it closes on the first execution bar of a later bucket: "late, never early, never with data from the future."
- `HtfBars`: `Root`, `Timeframe`, `OpenTime` (UTC), `Open`, `High`, `Low`, `Close`, `Volume`; unique on (`Root`, `Timeframe`, `OpenTime`); decimals stored as REAL.
- `EmaIndicator` is SMA-seeded (first value = SMA of the first `period` closes), not ready before `period` values. `Ema21Indicator` is left as it is.
- History panel copy, exact: "Ready." / "{n} more bars to load before this strategy can trade." / "The EMA matches TradingView once 3× its length is loaded." / "Schwab price history (NQ, contracts joined, unadjusted) · last filled {time} ET".
- A history write must never break bar processing: the live top-up catches and logs its own failures.
- Line numbers below are from `master` at `a709e4d`; plans 1–3 may shift them. Find the edit by the quoted code, not the number.

## Review Focus

1. **Schwab serves the bar that is still forming** (today's daily bar, the current 30-minute bar). Storing it would freeze a partial bar into history. Expected: nothing whose bucket ends after `nowUtc` is stored. Pinned by `RunAsync_StoresEveryTimeframeAndNothingStillForming` (Task 9).
2. **The engine restarts mid-bucket.** The first live bucket is partial and would overwrite the complete backfilled bar with the same key. Expected: the partial leading bucket is dropped. Pinned by `OnExecutionBar_StartedMidBucket_DropsThatPartialBucket` (Task 10) and `CloseAll_LeavesOutTheLeadingPartialAndTheFormingLastBucket` (Task 4).
3. **A feed reconnect replays bars that are already aggregated.** A bar for a bucket that already closed would start a second bar with the same key and overwrite good history. Expected: ignored. Pinned by `OnExecutionBar_BarForAnAlreadyClosedBucket_IsIgnored` and `OnExecutionBar_BarOlderThanTheFormingBucket_IsIgnored` (Task 4).
4. **A CSV exported at the wrong timeframe** (hourly rows imported as H4) would silently merge into wrong bars. Expected: rejected with a message naming the timeframe. Pinned by `ReadAsync_TwoRowsInOneBucket_RejectsTheWrongTimeframe` (Task 7).
5. **NQ and MNQ stream together** (one root, two tickers). Two writers to root `NQ` would interleave. Expected: the first ticker seen feeds the root; the other is ignored. Pinned by `OnExecutionBar_SecondTickerOnTheSameRoot_IsIgnored` (Task 10).

## Decisions

Facts the spec leaves open, decided here:

1. **`SessionBarAggregator` takes the execution bar size: `new SessionBarAggregator(SignalTimeframe tf, int executionMinutes)`.** The shared contract lists `SessionBarAggregator(SignalTimeframe tf)`, but "closes on the execution bar ending on its boundary" needs the bar's length (`Bar` carries only its open time). Plan 5 must pass the root's bar size.
2. **One closed bar per call.** When a gap makes one execution bar both close the old bucket and complete its own, the old one is returned and the new one closes on the next execution bar (late by one bar, never early).
3. **Bucket keys are the scheduled opens**, so the backfill, CSV import and top-ups agree: D1 = 18:00 ET on the day before the trading date; W1 = Sunday 18:00 ET; MN1 = the 18:00 ET open of the month's first weekday session; H4/H8 count from 18:00 ET. Session hours are the fixed CME Globex 18:00/17:00 ET, not `StrategyConfig.SessionStartHour`.
4. **`EasternTime` also has `Find(string id)` and `ToUtc(DateTime eastern)`.** The three copies being removed resolve the *configured* `cfg.Timezone` (the Risk page offers Chicago, Los Angeles and UTC too), so they call `EasternTime.Find(cfg.Timezone)`, not `EasternTime.Zone`. Direct `FindSystemTimeZoneById` calls in CRV.Web and CRV.Live are not copies of `FindTz` and stay out of scope.
5. **`HtfBarRow` adds one column, `FilledAt` (UTC),** so "last filled {time}" is the last write, not the newest bar's open.
6. **Top-ups** run in the live engine's bar loop for every data broker except `TradovateReplay` (Mock cannot be a data broker). Each closed execution bar feeds every timeframe that is a whole multiple of the root's bar size, D1/W1/MN1 included. "Extends the stored minute history" means the M5/M15/M30 rows: no raw 1-minute bars are stored.
7. **Entry points are API endpoints** (`api/history`: status, bars, backfill, CSV import). This plan adds no screen. The History panel *markup* belongs to plan 5, which builds the EMA setup page and its WCAG scans; this plan gives it `HistoryRequirement.Note`, `HistoryRequirement.SourceLine`, `HtfBarStore.CountAsync` and `HtfBarStore.LastFilledUtcAsync`. Blocking the switch-on is plan 5's validation, using `HistoryRequirement.Check` and `SignalTimeframes.IsWholeMultipleOf` (both defined here).
8. **Schwab daily stamps are midnight *Central*** (probe: 05:00Z in summer, 06:00Z in winter), not midnight ET as the spec says. Mapping each stamp through the D1 bucket lands on the right trading date either way; it is recorded for the daily-alignment check.
9. **Request shapes copy the probe:** daily `periodType=year&period=20&frequencyType=daily&frequency=1` (window filtered client-side); 30-minute and 1-minute with `startDate`/`endDate` over the last year. The probe showed an over-cap 1-minute request returns the *most recent* 40,000 bars, so paging walks backwards: each next request ends 1 ms before the oldest bar received.
10. **"1 more bar to load…"** in the singular when exactly one is missing.
11. **Roots come from `TickerGroup.GetGroupKey(ticker)`** (`CRV.Core.Strategy`, public, existing at `TickerGroup.cs:826`): it already merges micro and mini ("/MNQZ26" and "/NQZ26" both give "NQ"). Plan 1 does not add a separate root helper.
12. **CSV rows are mapped through `SessionBucket.For`**, so TradingView's daily/weekly/monthly stamps (00:00Z, midnight Chicago or the 18:00 ET open) all land on our keys; two rows in one bucket reject the file.

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Create | `CRV.Core/Indicators/EasternTime.cs` | The one time-zone lookup (IANA with Windows fallback), ET ⇄ UTC |
| Create | `CRV.Core/Indicators/EmaIndicator.cs` | Generic SMA-seeded EMA |
| Create | `CRV.Core/Indicators/SignalTimeframe.cs` | `SignalTimeframe` enum, `Minutes()`, `IsWholeMultipleOf()` |
| Create | `CRV.Core/Indicators/SessionBucket.cs` | Bucket open/end for any UTC moment and timeframe |
| Create | `CRV.Core/Indicators/SessionBarAggregator.cs` | `HtfBar`, `SessionBarAggregator`, `CloseAll` |
| Create | `CRV.Core/Models/HtfBarRow.cs` | `HtfBars` entity |
| Create | `CRV.Core/Data/HtfBarStore.cs` | Upsert, count, latest, last-filled over `HtfBars` |
| Create | `CRV.Core/Data/HtfTopUp.cs` | Closed live execution bars → closed HTF bars per root |
| Create | `CRV.Core/Strategy/HistoryRequirement.cs` | Needed vs stored, `HistoryStatus`, panel copy |
| Create | `CRV.Core/Migrations/<ts>_AddHtfBars.cs` (+ Designer, snapshot) | `HtfBars` table (generated) |
| Create | `CRV.Backtest/DataLoaders/HtfCsvImport.cs` | TradingView CSV → `HtfBars` |
| Create | `CRV.Backtest/DataLoaders/SchwabHistoryBackfill.cs` | Fetcher contract, paging, backfill, Schwab HTTP fetcher |
| Create | `CRV.Web/Api/HistoryController.cs` | `api/history` status, bars, backfill, import |
| Create | `CRV.Core.Tests/Indicators/{EasternTime,EmaIndicator,SessionBucket,SessionBarAggregator,SourceTree}Tests.cs` | Tests |
| Create | `CRV.Core.Tests/Data/{HtfBarStore,HtfTopUp}Tests.cs`, `CRV.Core.Tests/Strategy/HistoryRequirementTests.cs`, `CRV.Core.Tests/Backtest/{HtfCsvImport,SchwabHistoryBackfill}Tests.cs` | Tests |
| Modify | `CRV.Core/Indicators/Indicators.cs:243,298,303-318` | Use `EasternTime.Find`; delete `GetTz` |
| Modify | `CRV.Core/Modules/SessionEngine.cs:1,63,240-256` | Use `EasternTime.Find`; delete `FindTz` |
| Modify | `CRV.Core/Strategy/TickerGroup.cs:91,1023-1039` | Use `EasternTime.Find`; delete `FindTimeZone` |
| Modify | `CRV.Core/Data/TradingDbContext.cs` | `DbSet<HtfBarRow> HtfBars` + mapping |
| Modify | `CRV.Web/Services/LiveEngineOrchestrator.cs:1366-1421` | Live top-ups |
| Modify | `README.md` | "Price history" section |
| Delete | `CRV.Live/BarBuilders/BarAggregator.cs`, `CRV.Backtest/DataLoaders/BarResampler.cs` | Dead code the aggregator replaces |

---

### Task 1: `EasternTime` replaces the three zone lookups

**Files:**
- Create: `CRV.Core/Indicators/EasternTime.cs`
- Create: `CRV.Core.Tests/Indicators/EasternTimeTests.cs`
- Create: `CRV.Core.Tests/Indicators/SourceTreeTests.cs`
- Modify: `CRV.Core/Indicators/Indicators.cs:243,298,303-318`
- Modify: `CRV.Core/Modules/SessionEngine.cs:1,63,240-256`
- Modify: `CRV.Core/Strategy/TickerGroup.cs:91,1023-1039`

**Interfaces:**
- Produces: `static class EasternTime` (namespace `CRV.Core.Indicators`) with `TimeZoneInfo Zone`, `DateTime ToEastern(DateTime utc)`, `DateTime ToUtc(DateTime eastern)`, `TimeZoneInfo Find(string id)`.

- [ ] **Step 0: Branch and baseline**

```bash
git checkout master && git pull
git checkout -b feat/htf-bars-and-history
dotnet test CRV.Core.Tests 2>&1 | tail -3
```

Expected: `Passed!  - Failed:     0, Passed:   <N>` — write `<N>` down (956 before plans 1–3; their tests change it). Every later "Expected" count is relative to it.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Indicators/EasternTimeTests.cs`:

```csharp
using CRV.Core.Indicators;
using Xunit;

namespace CRV.Core.Tests.Indicators;

public class EasternTimeTests
{
    [Fact]
    public void ToEastern_SummerUtc_IsFourHoursBehind()
        => Assert.Equal(new DateTime(2026, 9, 30, 9, 30, 0), EasternTime.ToEastern(new DateTime(2026, 9, 30, 13, 30, 0, DateTimeKind.Utc)));

    [Fact]
    public void ToEastern_WinterUtc_IsFiveHoursBehind()
        => Assert.Equal(new DateTime(2026, 12, 1, 9, 30, 0), EasternTime.ToEastern(new DateTime(2026, 12, 1, 14, 30, 0, DateTimeKind.Utc)));

    [Fact]
    public void ToUtc_RoundTripsToEastern()
    {
        var utc = new DateTime(2026, 3, 9, 6, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, EasternTime.ToUtc(EasternTime.ToEastern(utc)));
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Eastern Standard Time")]
    public void Find_IanaAndWindowsIds_ResolveToEastern(string id)
        => Assert.Equal(TimeSpan.FromHours(-5), EasternTime.Find(id).BaseUtcOffset);

    [Fact]
    public void Find_Chicago_ResolvesToCentral()
        => Assert.Equal(TimeSpan.FromHours(-6), EasternTime.Find("America/Chicago").BaseUtcOffset);
}
```

`CRV.Core.Tests/Indicators/SourceTreeTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace CRV.Core.Tests.Indicators;

/// <summary>Searches the repository's C# sources for code the higher-timeframe design removed.</summary>
public class SourceTreeTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CRV.Trading.sln"))) return dir.FullName;
        throw new InvalidOperationException("CRV.Trading.sln not found above the test output folder.");
    }

    /// <summary>Repo-relative paths of .cs files matching <paramref name="pattern"/>, skipping build output, dot-folders and this file.</summary>
    private static List<string> FilesMatching(string pattern)
    {
        var regex = new Regex(pattern);
        return Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Root, p))
            .Where(rel => !rel.Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith('.') || s is "bin" or "obj"))
            .Where(rel => Path.GetFileName(rel) != nameof(SourceTreeTests) + ".cs")
            .Where(rel => regex.IsMatch(File.ReadAllText(Path.Combine(Root, rel))))
            .OrderBy(rel => rel)
            .ToList();
    }

    [Fact]
    public void Sources_MapIanaToWindowsZoneIdsOnlyInEasternTime()
        => Assert.Equal(
            new[] { Path.Combine("CRV.Core", "Indicators", "EasternTime.cs") },
            FilesMatching("=>\\s*\"Eastern Standard Time\""));

    [Fact]
    public void Sources_HaveNoPrivateTimeZoneLookups()
        => Assert.Empty(FilesMatching("\\b(FindTz|GetTz|FindTimeZone)\\("));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EasternTimeTests|FullyQualifiedName~SourceTreeTests"`
Expected: build FAILS with `The name 'EasternTime' does not exist in the current context`.

- [ ] **Step 3: Write `EasternTime`**

`CRV.Core/Indicators/EasternTime.cs`:

```csharp
namespace CRV.Core.Indicators;

/// <summary>
/// The one place that resolves time zones. CME futures sessions are defined in US Eastern
/// time, and the configured zone ids are IANA names ("America/New_York"); hosts without ICU
/// only know Windows ids, so the lookup falls back to the Windows name.
/// </summary>
public static class EasternTime
{
    public static TimeZoneInfo Zone { get; } = Find("America/New_York");

    public static DateTime ToEastern(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime ToUtc(DateTime eastern)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(eastern, DateTimeKind.Unspecified), Zone);

    /// <summary>Resolves an IANA or Windows zone id, falling back to the Windows id for the zones the app offers.</summary>
    public static TimeZoneInfo Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            var windowsId = id switch
            {
                "America/New_York"    => "Eastern Standard Time",
                "America/Chicago"     => "Central Standard Time",
                "America/Los_Angeles" => "Pacific Standard Time",
                "Europe/London"       => "GMT Standard Time",
                _                     => id
            };
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }
}
```

- [ ] **Step 4: Run `EasternTimeTests` to verify they pass, `SourceTreeTests` still fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EasternTimeTests|FullyQualifiedName~SourceTreeTests"`
Expected: 6 EasternTime tests pass; `Sources_MapIanaToWindowsZoneIdsOnlyInEasternTime` and `Sources_HaveNoPrivateTimeZoneLookups` FAIL listing `CRV.Core/Indicators/Indicators.cs`, `CRV.Core/Modules/SessionEngine.cs`, `CRV.Core/Strategy/TickerGroup.cs`.

- [ ] **Step 5: Replace the three copies**

`CRV.Core/Indicators/Indicators.cs` (`OrbCalculator`), both occurrences (lines 243 and 298):

```csharp
        var tz    = GetTz(_timezone);
```

become

```csharp
        var tz    = EasternTime.Find(_timezone);
```

and delete the whole `GetTz` method (lines 302–318: the blank line, `private static TimeZoneInfo GetTz(string tz)` and its body), leaving the class's closing `}`.

`CRV.Core/Modules/SessionEngine.cs`: add `using CRV.Core.Indicators;` above `using CRV.Core.Models;` (line 1); in the constructor (line 63)

```csharp
        _tz  = FindTz(cfg.Timezone);
```

becomes

```csharp
        _tz  = EasternTime.Find(cfg.Timezone);
```

and delete the `FindTz` method (lines 240–256: the blank line, `private static TimeZoneInfo FindTz(string tz)` and its body), leaving the class's closing `}`.

`CRV.Core/Strategy/TickerGroup.cs` (already `using CRV.Core.Indicators;`): in the constructor (line 91)

```csharp
        _tz = FindTimeZone(cfg.Timezone);
```

becomes

```csharp
        _tz = EasternTime.Find(cfg.Timezone);
```

and delete the `FindTimeZone` method (lines 1023–1039: the blank line, `private static TimeZoneInfo FindTimeZone(string tz)` and its body), leaving the class's closing `}`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EasternTimeTests|FullyQualifiedName~SourceTreeTests"`
Expected: PASS (8 tests).

Run: `dotnet test CRV.Core.Tests 2>&1 | tail -3`
Expected: `Failed: 0, Passed: <N + 8>`.

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Indicators/EasternTime.cs CRV.Core/Indicators/Indicators.cs CRV.Core/Modules/SessionEngine.cs CRV.Core/Strategy/TickerGroup.cs CRV.Core.Tests/Indicators/EasternTimeTests.cs CRV.Core.Tests/Indicators/SourceTreeTests.cs
git commit -m "refactor: one EasternTime helper resolves time zones"
```

---

### Task 2: `EmaIndicator`

**Files:**
- Create: `CRV.Core/Indicators/EmaIndicator.cs`
- Create: `CRV.Core.Tests/Indicators/EmaIndicatorTests.cs`

**Interfaces:**
- Produces: `sealed class EmaIndicator(int period)` with `void Add(decimal close)`, `bool IsReady`, `decimal Value` (0 until ready), `int Period`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Indicators/EmaIndicatorTests.cs`:

```csharp
using CRV.Core.Indicators;
using Xunit;

namespace CRV.Core.Tests.Indicators;

public class EmaIndicatorTests
{
    [Fact]
    public void Add_BeforePeriodCloses_IsNotReadyAndReportsZero()
    {
        var ema = new EmaIndicator(3);
        ema.Add(10m);
        ema.Add(11m);

        Assert.False(ema.IsReady);
        Assert.Equal(0m, ema.Value);
    }

    [Fact]
    public void Add_PeriodCloses_SeedsWithTheirSimpleAverage()
    {
        var ema = new EmaIndicator(3);
        foreach (var c in new[] { 10m, 11m, 12m }) ema.Add(c);

        Assert.True(ema.IsReady);
        Assert.Equal(11m, ema.Value);
    }

    [Fact]
    public void Add_AfterSeed_SmoothsWithTwoOverPeriodPlusOne()
    {
        // k = 2 / (3 + 1) = 0.5. Seed 11; then 0.5·13 + 0.5·11 = 12; then 0.5·9 + 0.5·12 = 10.5.
        var ema = new EmaIndicator(3);
        foreach (var c in new[] { 10m, 11m, 12m, 13m }) ema.Add(c);
        Assert.Equal(12m, ema.Value);

        ema.Add(9m);
        Assert.Equal(10.5m, ema.Value);
    }

    [Fact]
    public void Add_Period21_MatchesEma21Indicator()
    {
        var ema = new EmaIndicator(21);
        var ema21 = new Ema21Indicator();
        for (int i = 0; i < 60; i++)
        {
            var close = 21000m + (i % 7) * 3.25m - (i % 5) * 1.5m;
            ema.Add(close);
            ema21.Update(close);
            if (ema.IsReady) Assert.Equal(ema21.Value, ema.Value);
        }
        Assert.True(ema.IsReady);
    }

    [Fact]
    public void Constructor_PeriodBelowOne_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new EmaIndicator(0));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaIndicatorTests"`
Expected: build FAILS with `The type or namespace name 'EmaIndicator' could not be found`.

- [ ] **Step 3: Write `EmaIndicator`**

`CRV.Core/Indicators/EmaIndicator.cs`:

```csharp
namespace CRV.Core.Indicators;

/// <summary>
/// Exponential moving average seeded with the simple average of the first <c>period</c> closes,
/// the way Pine's <c>ta.ema</c> is, so values match TradingView once enough history is loaded.
/// </summary>
public sealed class EmaIndicator
{
    private readonly int     _period;
    private readonly decimal _k;
    private decimal _sum;
    private int     _count;

    public EmaIndicator(int period)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _k      = 2m / (period + 1);
    }

    public int Period => _period;

    /// <summary>True once <c>period</c> closes have been added.</summary>
    public bool IsReady => _count >= _period;

    /// <summary>The EMA; 0 until <see cref="IsReady"/>.</summary>
    public decimal Value { get; private set; }

    public void Add(decimal close)
    {
        _count++;
        if (_count < _period) { _sum += close; return; }
        if (_count == _period) { Value = (_sum + close) / _period; return; }
        Value = (close - Value) * _k + Value;
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~EmaIndicatorTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Indicators/EmaIndicator.cs CRV.Core.Tests/Indicators/EmaIndicatorTests.cs
git commit -m "feat(history): generic SMA-seeded EmaIndicator matching Pine ta.ema"
```

---

### Task 3: `SignalTimeframe` and `SessionBucket`

**Files:**
- Create: `CRV.Core/Indicators/SignalTimeframe.cs`
- Create: `CRV.Core/Indicators/SessionBucket.cs`
- Create: `CRV.Core.Tests/Indicators/SessionBucketTests.cs`

**Interfaces:**
- Consumes: `EasternTime.ToEastern`, `EasternTime.ToUtc` (Task 1).
- Produces:
  - `enum SignalTimeframe { M5, M15, M30, H1, H4, H8, D1, W1, MN1 }` (namespace `CRV.Core.Indicators`).
  - `static class SignalTimeframes`: `int? Minutes(this SignalTimeframe tf)` (null for D1/W1/MN1), `bool IsWholeMultipleOf(this SignalTimeframe tf, int executionMinutes)` — plan 5's save validation uses it.
  - `static class SessionBucket`: `const int SessionOpenHour = 18`, `const int SessionCloseHour = 17`, `const int SessionMinutes = 1380`, `(DateTime OpenUtc, DateTime EndUtc) For(SignalTimeframe tf, DateTime utc)` — both results `DateTimeKind.Utc`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Indicators/SessionBucketTests.cs`:

```csharp
using System.Globalization;
using CRV.Core.Indicators;
using Xunit;

namespace CRV.Core.Tests.Indicators;

/// <summary>
/// Bucket boundaries in UTC. Late September 2026 is EDT (ET = UTC−4): 18:00 ET is 22:00Z,
/// 17:00 ET is 21:00Z. Trading date Wed 2026-09-30 opens Tue 2026-09-29 22:00Z.
/// </summary>
public class SessionBucketTests
{
    private static DateTime U(string iso)
        => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static void AssertBucket(SignalTimeframe tf, string at, string open, string end)
    {
        var (o, e) = SessionBucket.For(tf, U(at));
        Assert.Equal(U(open), o);
        Assert.Equal(U(end), e);
        Assert.Equal(DateTimeKind.Utc, o.Kind);
    }

    [Theory]
    [InlineData("2026-09-29T22:00:00Z", "2026-09-29T22:00:00Z", "2026-09-30T02:00:00Z")] // 18:00 ET
    [InlineData("2026-09-30T02:30:00Z", "2026-09-30T02:00:00Z", "2026-09-30T06:00:00Z")] // 22:00 ET
    [InlineData("2026-09-30T06:00:00Z", "2026-09-30T06:00:00Z", "2026-09-30T10:00:00Z")] // 02:00 ET
    [InlineData("2026-09-30T13:30:00Z", "2026-09-30T10:00:00Z", "2026-09-30T14:00:00Z")] // 06:00 ET
    [InlineData("2026-09-30T14:00:00Z", "2026-09-30T14:00:00Z", "2026-09-30T18:00:00Z")] // 10:00 ET
    [InlineData("2026-09-30T20:59:00Z", "2026-09-30T18:00:00Z", "2026-09-30T21:00:00Z")] // 14:00 ET, ends 17:00
    public void For_H4_OpensEveryFourHoursFrom18AndCutsTheLastBarAt17(string at, string open, string end)
        => AssertBucket(SignalTimeframe.H4, at, open, end);

    [Theory]
    [InlineData("2026-09-29T22:00:00Z", "2026-09-29T22:00:00Z", "2026-09-30T06:00:00Z")] // 18:00 ET
    [InlineData("2026-09-30T06:00:00Z", "2026-09-30T06:00:00Z", "2026-09-30T14:00:00Z")] // 02:00 ET
    [InlineData("2026-09-30T20:00:00Z", "2026-09-30T14:00:00Z", "2026-09-30T21:00:00Z")] // 10:00–17:00 ET, 7 hours
    public void For_H8_OpensAt18And02And10WithASevenHourLastBar(string at, string open, string end)
        => AssertBucket(SignalTimeframe.H8, at, open, end);

    [Theory]
    [InlineData("2026-09-29T22:00:00Z", "2026-09-29T22:00:00Z", "2026-09-30T21:00:00Z")] // 18:00 ET starts Wednesday's session
    [InlineData("2026-09-29T20:59:00Z", "2026-09-28T22:00:00Z", "2026-09-29T21:00:00Z")] // 16:59 ET is still Tuesday's
    public void For_D1_RunsFrom18To17(string at, string open, string end)
        => AssertBucket(SignalTimeframe.D1, at, open, end);

    [Theory]
    [InlineData("2026-09-27T22:00:00Z", "2026-09-27T22:00:00Z", "2026-10-02T21:00:00Z")] // Sunday 18:00 ET
    [InlineData("2026-10-02T20:59:00Z", "2026-09-27T22:00:00Z", "2026-10-02T21:00:00Z")] // Friday 16:59 ET
    [InlineData("2026-10-04T22:00:00Z", "2026-10-04T22:00:00Z", "2026-10-09T21:00:00Z")] // next Sunday 18:00 ET
    public void For_W1_RunsFromSunday18ToFriday17(string at, string open, string end)
        => AssertBucket(SignalTimeframe.W1, at, open, end);

    [Theory]
    // Sunday 2026-05-31 18:00 ET starts Monday June 1's session, so it is June.
    [InlineData("2026-05-31T22:00:00Z", "2026-05-31T22:00:00Z", "2026-06-30T21:00:00Z")]
    // Friday May 29 16:59 ET is May's last session; May 1 2026 is a Friday, opened Thursday April 30 18:00 ET.
    [InlineData("2026-05-29T20:59:00Z", "2026-04-30T22:00:00Z", "2026-05-29T21:00:00Z")]
    public void For_MN1_GoesByTradingDateMonth(string at, string open, string end)
        => AssertBucket(SignalTimeframe.MN1, at, open, end);

    [Theory]
    [InlineData(SignalTimeframe.M5,  "2026-09-30T13:37:00Z", "2026-09-30T13:35:00Z", "2026-09-30T13:40:00Z")]
    [InlineData(SignalTimeframe.M15, "2026-09-30T13:37:00Z", "2026-09-30T13:30:00Z", "2026-09-30T13:45:00Z")]
    [InlineData(SignalTimeframe.M30, "2026-09-30T13:37:00Z", "2026-09-30T13:30:00Z", "2026-09-30T14:00:00Z")]
    [InlineData(SignalTimeframe.H1,  "2026-09-29T22:10:00Z", "2026-09-29T22:00:00Z", "2026-09-29T23:00:00Z")]
    public void For_MinuteTimeframes_AreClockAligned(SignalTimeframe tf, string at, string open, string end)
        => AssertBucket(tf, at, open, end);

    // Spring forward: Sunday 2026-03-08 02:00 ET. The Friday before closes at 17:00 EST (22:00Z);
    // Sunday opens at 18:00 EDT (22:00Z).
    [Fact]
    public void For_SpringDstWeekend_KeepsSessionAnchors()
    {
        AssertBucket(SignalTimeframe.W1, "2026-03-06T21:30:00Z", "2026-03-01T23:00:00Z", "2026-03-06T22:00:00Z");
        AssertBucket(SignalTimeframe.D1, "2026-03-08T22:00:00Z", "2026-03-08T22:00:00Z", "2026-03-09T21:00:00Z");
        AssertBucket(SignalTimeframe.H4, "2026-03-09T06:00:00Z", "2026-03-09T06:00:00Z", "2026-03-09T10:00:00Z");
    }

    // Fall back: Sunday 2026-11-01 02:00 ET, which is also a month change on a Sunday evening.
    [Fact]
    public void For_AutumnDstWeekend_KeepsSessionAnchors()
    {
        AssertBucket(SignalTimeframe.H8,  "2026-10-30T20:00:00Z", "2026-10-30T14:00:00Z", "2026-10-30T21:00:00Z");
        AssertBucket(SignalTimeframe.D1,  "2026-11-01T23:00:00Z", "2026-11-01T23:00:00Z", "2026-11-02T22:00:00Z");
        AssertBucket(SignalTimeframe.W1,  "2026-11-01T23:00:00Z", "2026-11-01T23:00:00Z", "2026-11-06T22:00:00Z");
        AssertBucket(SignalTimeframe.MN1, "2026-11-01T23:00:00Z", "2026-11-01T23:00:00Z", "2026-11-30T22:00:00Z");
    }

    [Theory]
    [InlineData(SignalTimeframe.M5, 1, true)]
    [InlineData(SignalTimeframe.M5, 5, true)]
    [InlineData(SignalTimeframe.M5, 10, false)]
    [InlineData(SignalTimeframe.M15, 10, false)]
    [InlineData(SignalTimeframe.M30, 20, false)]
    [InlineData(SignalTimeframe.M30, 15, true)]
    [InlineData(SignalTimeframe.H1, 20, true)]
    [InlineData(SignalTimeframe.H4, 60, true)]
    [InlineData(SignalTimeframe.D1, 60, true)]
    [InlineData(SignalTimeframe.MN1, 30, true)]
    [InlineData(SignalTimeframe.H1, 0, false)]
    public void IsWholeMultipleOf_ChecksTheTimeframeIsMadeOfWholeExecutionBars(SignalTimeframe tf, int executionMinutes, bool expected)
        => Assert.Equal(expected, tf.IsWholeMultipleOf(executionMinutes));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SessionBucketTests"`
Expected: build FAILS with `The type or namespace name 'SignalTimeframe' could not be found`.

- [ ] **Step 3: Write `SignalTimeframe`**

`CRV.Core/Indicators/SignalTimeframe.cs`:

```csharp
namespace CRV.Core.Indicators;

/// <summary>The bar size a strategy evaluates its signals on, built from the root's execution bars.</summary>
public enum SignalTimeframe { M5, M15, M30, H1, H4, H8, D1, W1, MN1 }

public static class SignalTimeframes
{
    /// <summary>Nominal length in minutes; null for D1, W1 and MN1, whose length follows the session calendar.</summary>
    public static int? Minutes(this SignalTimeframe tf) => tf switch
    {
        SignalTimeframe.M5  => 5,
        SignalTimeframe.M15 => 15,
        SignalTimeframe.M30 => 30,
        SignalTimeframe.H1  => 60,
        SignalTimeframe.H4  => 240,
        SignalTimeframe.H8  => 480,
        _                   => null,
    };

    /// <summary>
    /// True when every bucket of <paramref name="tf"/> is made of whole execution bars.
    /// Execution bars divide 60, and the session (18:00–17:00 ET) is a whole number of hours,
    /// so H1 and longer always qualify; the minute timeframes need their length to divide evenly.
    /// </summary>
    public static bool IsWholeMultipleOf(this SignalTimeframe tf, int executionMinutes)
    {
        if (executionMinutes <= 0) return false;
        return tf.Minutes() is not int m || m % executionMinutes == 0;
    }
}
```

- [ ] **Step 4: Write `SessionBucket`**

`CRV.Core/Indicators/SessionBucket.cs`:

```csharp
namespace CRV.Core.Indicators;

/// <summary>
/// Where a moment falls in each signal timeframe, anchored to the CME Globex session:
/// the trading day runs 18:00 ET to 17:00 ET the next day (the <c>StrategyConfig.TradingDate</c> rule),
/// the week from Sunday 18:00 to Friday 17:00, the month by trading date.
/// </summary>
public static class SessionBucket
{
    public const int SessionOpenHour  = 18;
    public const int SessionCloseHour = 17;

    /// <summary>Length of one full session, 18:00 to 17:00.</summary>
    public const int SessionMinutes = 23 * 60;

    /// <summary>The bucket of <paramref name="tf"/> that contains <paramref name="utc"/>: its open and its scheduled end, in UTC.</summary>
    public static (DateTime OpenUtc, DateTime EndUtc) For(SignalTimeframe tf, DateTime utc)
    {
        var et           = EasternTime.ToEastern(utc);
        var tradingDate  = et.Hour >= SessionOpenHour ? et.Date.AddDays(1) : et.Date;
        var sessionOpen  = tradingDate.AddDays(-1).AddHours(SessionOpenHour);
        var sessionClose = tradingDate.AddHours(SessionCloseHour);

        var (open, end) = tf switch
        {
            SignalTimeframe.H4 or SignalTimeframe.H8 => FromSessionOpen(et, sessionOpen, sessionClose, tf.Minutes()!.Value),
            SignalTimeframe.D1  => (sessionOpen, sessionClose),
            SignalTimeframe.W1  => Week(tradingDate),
            SignalTimeframe.MN1 => Month(tradingDate),
            _                   => Clock(et, tf.Minutes()!.Value),
        };
        return (EasternTime.ToUtc(open), EasternTime.ToUtc(end));
    }

    private static (DateTime, DateTime) Clock(DateTime et, int minutes)
    {
        var open = et.Date.AddMinutes((et.Hour * 60 + et.Minute) / minutes * minutes);
        return (open, open.AddMinutes(minutes));
    }

    // H4 and H8 count from the 18:00 open; the last bucket is cut short at the 17:00 close.
    private static (DateTime, DateTime) FromSessionOpen(DateTime et, DateTime sessionOpen, DateTime sessionClose, int minutes)
    {
        var since = (int)(et - sessionOpen).TotalMinutes;
        var open  = sessionOpen.AddMinutes(since / minutes * minutes);
        var end   = open.AddMinutes(minutes);
        return (open, end < sessionClose ? end : sessionClose);
    }

    private static (DateTime, DateTime) Week(DateTime tradingDate)
    {
        var monday = tradingDate.AddDays(-(((int)tradingDate.DayOfWeek + 6) % 7));
        return (monday.AddDays(-1).AddHours(SessionOpenHour), monday.AddDays(4).AddHours(SessionCloseHour));
    }

    // Opens with the session of the month's first weekday, ends with the close of its last weekday.
    private static (DateTime, DateTime) Month(DateTime tradingDate)
    {
        var first = new DateTime(tradingDate.Year, tradingDate.Month, 1);
        while (IsWeekend(first)) first = first.AddDays(1);
        var last = new DateTime(tradingDate.Year, tradingDate.Month, 1).AddMonths(1).AddDays(-1);
        while (IsWeekend(last)) last = last.AddDays(-1);
        return (first.AddDays(-1).AddHours(SessionOpenHour), last.AddHours(SessionCloseHour));
    }

    private static bool IsWeekend(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
```

- [ ] **Step 5: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SessionBucketTests"`
Expected: PASS (33 tests).

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Indicators/SignalTimeframe.cs CRV.Core/Indicators/SessionBucket.cs CRV.Core.Tests/Indicators/SessionBucketTests.cs
git commit -m "feat(history): session-anchored buckets for every signal timeframe"
```

---

### Task 4: `SessionBarAggregator`; delete the dead aggregators

**Files:**
- Create: `CRV.Core/Indicators/SessionBarAggregator.cs`
- Create: `CRV.Core.Tests/Indicators/SessionBarAggregatorTests.cs`
- Modify: `CRV.Core.Tests/Indicators/SourceTreeTests.cs`
- Delete: `CRV.Live/BarBuilders/BarAggregator.cs`, `CRV.Backtest/DataLoaders/BarResampler.cs`

**Interfaces:**
- Consumes: `SessionBucket.For`, `SessionBucket.SessionMinutes`, `SignalTimeframe` (Task 3); `CRV.Core.Models.Bar`.
- Produces:
  - `record HtfBar(SignalTimeframe Timeframe, DateTime OpenUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume)`.
  - `sealed class SessionBarAggregator(SignalTimeframe tf, int executionMinutes)` with `SignalTimeframe Timeframe`, `HtfBar? Forming`, `HtfBar? OnExecutionBar(Bar bar)` (returns the bar it closed, else null), and `static IReadOnlyList<HtfBar> CloseAll(IEnumerable<Bar> bars, SignalTimeframe tf, int executionMinutes)` (complete buckets only).

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Indicators/SessionBarAggregatorTests.cs`:

```csharp
using System.Globalization;
using CRV.Core.Indicators;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Indicators;

/// <summary>Times are UTC; late September 2026 is EDT, so 18:00 ET = 22:00Z and 17:00 ET = 21:00Z.</summary>
public class SessionBarAggregatorTests
{
    private static DateTime U(string iso)
        => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static Bar B(string iso, decimal open, decimal high, decimal low, decimal close, long volume = 10)
        => new(U(iso), open, high, low, close, volume);

    /// <summary>Consecutive bars of <paramref name="minutes"/> from <paramref name="firstIso"/>; bar i is O=100+i H=110+i L=90+i C=105+i.</summary>
    private static List<Bar> Run(string firstIso, int minutes, int count)
        => Enumerable.Range(0, count)
            .Select(i => new Bar(U(firstIso).AddMinutes(i * minutes), 100 + i, 110 + i, 90 + i, 105 + i, 10))
            .ToList();

    [Fact]
    public void OnExecutionBar_H4_ClosesOnTheBarEndingOnItsBoundary()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.H4, 30);
        var bars = Run("2026-09-29T22:00:00Z", 30, 8); // 18:00–22:00 ET

        for (int i = 0; i < 7; i++)
            Assert.Null(agg.OnExecutionBar(bars[i]));
        Assert.NotNull(agg.Forming);

        var closed = agg.OnExecutionBar(bars[7]);

        Assert.Equal(new HtfBar(SignalTimeframe.H4, U("2026-09-29T22:00:00Z"), 100, 117, 90, 112, 80), closed);
        Assert.Null(agg.Forming);
    }

    [Fact]
    public void OnExecutionBar_H4_LastBarOfTheSessionIsThreeHours()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.H4, 30);
        var bars = Run("2026-09-30T18:00:00Z", 30, 6); // 14:00–17:00 ET

        var results = bars.Select(agg.OnExecutionBar).ToList();

        Assert.All(results.Take(5), Assert.Null);
        Assert.Equal(U("2026-09-30T18:00:00Z"), results[5]!.OpenUtc);
        Assert.Equal(110m, results[5]!.Close);
    }

    [Fact]
    public void OnExecutionBar_H8_LastBarOfTheSessionIsSevenHours()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.H8, 60);
        var bars = Run("2026-09-30T14:00:00Z", 60, 7); // 10:00–17:00 ET

        var results = bars.Select(agg.OnExecutionBar).ToList();

        Assert.All(results.Take(6), Assert.Null);
        Assert.Equal(new HtfBar(SignalTimeframe.H8, U("2026-09-30T14:00:00Z"), 100, 116, 90, 111, 70), results[6]);
    }

    [Fact]
    public void OnExecutionBar_M5FromOneMinute_ClosesEveryFifthBar()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.M5, 1);
        var results = Run("2026-09-30T13:30:00Z", 1, 10).Select(agg.OnExecutionBar).ToList();

        Assert.Equal(new[] { 4, 9 }, results.Select((r, i) => (r, i)).Where(x => x.r != null).Select(x => x.i));
    }

    [Fact]
    public void OnExecutionBar_EarlyClose_ClosesTheDayLateOnTheNextSessionsFirstBar()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.D1, 30);
        // Session opens 18:00 ET; the exchange closes early at 13:00 ET (last bar 12:30 ET).
        Assert.Null(agg.OnExecutionBar(B("2026-11-25T23:00:00Z", 100, 101, 99, 100)));  // Wed 18:00 EST
        Assert.Null(agg.OnExecutionBar(B("2026-11-26T17:30:00Z", 100, 104, 98, 103)));  // Thu 12:30 EST

        var closed = agg.OnExecutionBar(B("2026-11-26T23:00:00Z", 103, 105, 102, 104)); // Thu 18:00 EST, next session

        Assert.Equal(new HtfBar(SignalTimeframe.D1, U("2026-11-25T23:00:00Z"), 100, 104, 98, 103, 20), closed);
        Assert.Equal(U("2026-11-26T23:00:00Z"), agg.Forming!.OpenUtc);
    }

    [Fact]
    public void OnExecutionBar_GapLeavesTheNewBucketForTheNextBar()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.H1, 30);
        Assert.Null(agg.OnExecutionBar(B("2026-09-30T14:00:00Z", 1, 2, 0, 1)));  // 10:00 ET, its 10:30 bar never comes

        // The 11:30 ET bar ends the 11:00 bucket, but this call already closes the 10:00 one.
        var first = agg.OnExecutionBar(B("2026-09-30T15:30:00Z", 5, 6, 4, 5));
        Assert.Equal(U("2026-09-30T14:00:00Z"), first!.OpenUtc);

        var second = agg.OnExecutionBar(B("2026-09-30T16:00:00Z", 7, 8, 6, 7));
        Assert.Equal(new HtfBar(SignalTimeframe.H1, U("2026-09-30T15:00:00Z"), 5, 6, 4, 5, 10), second);
    }

    [Fact]
    public void OnExecutionBar_BarForAnAlreadyClosedBucket_IsIgnored()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.H1, 30);
        agg.OnExecutionBar(B("2026-09-30T14:00:00Z", 1, 2, 0, 1));
        Assert.NotNull(agg.OnExecutionBar(B("2026-09-30T14:30:00Z", 1, 3, 0, 2)));

        Assert.Null(agg.OnExecutionBar(B("2026-09-30T14:30:00Z", 9, 9, 9, 9)));
        Assert.Null(agg.Forming);
    }

    [Fact]
    public void OnExecutionBar_BarOlderThanTheFormingBucket_IsIgnored()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.H1, 30);
        agg.OnExecutionBar(B("2026-09-30T15:00:00Z", 1, 2, 0, 1));

        Assert.Null(agg.OnExecutionBar(B("2026-09-30T14:30:00Z", 9, 9, 9, 9)));
        Assert.Equal(1m, agg.Forming!.Open);
        Assert.Equal(2m, agg.Forming.High);
    }

    [Fact]
    public void Constructor_ExecutionMinutesBelowOne_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SessionBarAggregator(SignalTimeframe.H1, 0));

    [Fact]
    public void CloseAll_LeavesOutTheLeadingPartialAndTheFormingLastBucket()
    {
        // 19:00 ET to 23:30 ET: the 18:00 H4 bucket is joined late, the 22:00 one is still forming.
        var bars = Run("2026-09-29T23:00:00Z", 30, 10);

        Assert.Empty(SessionBarAggregator.CloseAll(bars, SignalTimeframe.H4, 30));

        var h1 = SessionBarAggregator.CloseAll(bars, SignalTimeframe.H1, 30);
        Assert.Equal(5, h1.Count);
        Assert.Equal(U("2026-09-29T23:00:00Z"), h1[0].OpenUtc);
    }

    // W1 and MN1 built from D1: every weekday's D1 from Thu 2026-10-01 to Fri 2026-11-06.
    // D1 for trading date T opens at 18:00 ET on T−1: 22:00Z while EDT, 23:00Z from Nov 2 (EST).
    // Day i (0-based) has O=100+i H=110+i L=90+i C=105+i V=1000.
    private static List<Bar> DailyFixture()
    {
        var bars = new List<Bar>();
        int i = 0;
        for (var t = new DateTime(2026, 10, 1); t <= new DateTime(2026, 11, 6); t = t.AddDays(1))
        {
            if (t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var openUtc = DateTime.SpecifyKind(t.AddDays(-1).AddHours(t < new DateTime(2026, 11, 2) ? 22 : 23), DateTimeKind.Utc);
            bars.Add(new Bar(openUtc, 100 + i, 110 + i, 90 + i, 105 + i, 1000));
            i++;
        }
        return bars;
    }

    [Fact]
    public void CloseAll_W1FromD1_MatchesHandCheckedWeeks()
    {
        var weeks = SessionBarAggregator.CloseAll(DailyFixture(), SignalTimeframe.W1, SessionBucket.SessionMinutes);

        // Oct 1–2 is a partial week (left out); Oct 5–9, 12–16, 19–23, 26–30 and Nov 2–6 close.
        Assert.Equal(5, weeks.Count);
        Assert.Equal(new HtfBar(SignalTimeframe.W1, U("2026-10-04T22:00:00Z"), 102, 116, 92, 111, 5000), weeks[0]);
        Assert.Equal(new HtfBar(SignalTimeframe.W1, U("2026-10-25T22:00:00Z"), 117, 131, 107, 126, 5000), weeks[3]);
        Assert.Equal(new HtfBar(SignalTimeframe.W1, U("2026-11-01T23:00:00Z"), 122, 136, 112, 131, 5000), weeks[4]);
    }

    [Fact]
    public void CloseAll_MN1FromD1_MatchesHandCheckedMonth()
    {
        var months = SessionBarAggregator.CloseAll(DailyFixture(), SignalTimeframe.MN1, SessionBucket.SessionMinutes);

        // October (22 sessions) closes on Friday Oct 30; November is still forming.
        Assert.Equal(new HtfBar(SignalTimeframe.MN1, U("2026-09-30T22:00:00Z"), 100, 131, 90, 126, 22000), Assert.Single(months));
    }

    [Fact]
    public void OnExecutionBar_W1AcrossTheSpringDstWeekend_ClosesFridayAndReopensSunday()
    {
        var agg = new SessionBarAggregator(SignalTimeframe.W1, SessionBucket.SessionMinutes);
        HtfBar? closed = null;
        // Trading dates Mon Mar 2 – Fri Mar 6 (EST, open 23:00Z the day before).
        for (int d = 2; d <= 6; d++)
            closed = agg.OnExecutionBar(new Bar(new DateTime(2026, 3, d - 1, 23, 0, 0, DateTimeKind.Utc), 1, 2, 0, 1, 1));
        Assert.Equal(U("2026-03-01T23:00:00Z"), closed!.OpenUtc);

        // Mon Mar 9 opens Sunday Mar 8 at 18:00 EDT = 22:00Z.
        Assert.Null(agg.OnExecutionBar(new Bar(U("2026-03-08T22:00:00Z"), 1, 2, 0, 1, 1)));
        Assert.Equal(U("2026-03-08T22:00:00Z"), agg.Forming!.OpenUtc);
    }
}
```

Append to `CRV.Core.Tests/Indicators/SourceTreeTests.cs`, inside the class after `Sources_HaveNoPrivateTimeZoneLookups`:

```csharp

    [Fact]
    public void Sources_HaveNoReferencesToTheRetiredBarAggregators()
        => Assert.Empty(FilesMatching("\\bBarAggregator\\b|\\bBarResampler\\b"));
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SessionBarAggregatorTests|FullyQualifiedName~SourceTreeTests"`
Expected: build FAILS with `The type or namespace name 'SessionBarAggregator' could not be found`.

- [ ] **Step 3: Write the aggregator**

`CRV.Core/Indicators/SessionBarAggregator.cs`:

```csharp
using CRV.Core.Models;

namespace CRV.Core.Indicators;

/// <summary>A closed (or forming) higher-timeframe bar. <see cref="OpenUtc"/> is the bucket's scheduled open.</summary>
public record HtfBar(SignalTimeframe Timeframe, DateTime OpenUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>
/// Builds session-anchored bars of one <see cref="SignalTimeframe"/> from a root's execution bars.
/// A bucket closes on the execution bar that ends on its boundary. When that bar never comes
/// (holiday, early close, a gap in the feed) the bucket closes on the first bar of a later
/// bucket: late, never early, and never with data from the future.
/// </summary>
public sealed class SessionBarAggregator
{
    private readonly TimeSpan _execution;
    private DateTime  _formingEnd;
    private DateTime? _lastClosedOpen;

    public SessionBarAggregator(SignalTimeframe tf, int executionMinutes)
    {
        if (executionMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(executionMinutes));
        Timeframe  = tf;
        _execution = TimeSpan.FromMinutes(executionMinutes);
    }

    public SignalTimeframe Timeframe { get; }

    /// <summary>The bucket being built, or null between a close and the next bar.</summary>
    public HtfBar? Forming { get; private set; }

    /// <summary>Adds one closed execution bar; returns the higher-timeframe bar it closed, if any.</summary>
    public HtfBar? OnExecutionBar(Bar bar)
    {
        var (open, end) = SessionBucket.For(Timeframe, bar.Time);

        // A bar for a bucket that already closed, or older than the forming one, would rewrite history.
        if (open <= _lastClosedOpen || (Forming is { } f0 && open < f0.OpenUtc)) return null;

        if (Forming is { } previous && previous.OpenUtc != open)
        {
            // The previous bucket missed its boundary bar, so it closes now. The new bucket is
            // only checked on its next bar, which keeps this call to one closed bar.
            Start(bar, open, end);
            return Close(previous);
        }

        if (Forming is null) Start(bar, open, end);
        else Forming = Forming with
        {
            High   = Math.Max(Forming.High, bar.High),
            Low    = Math.Min(Forming.Low, bar.Low),
            Close  = bar.Close,
            Volume = Forming.Volume + bar.Volume,
        };

        if (bar.Time + _execution < _formingEnd) return null;
        var closed = Forming!;
        Forming = null;
        return Close(closed);
    }

    /// <summary>
    /// Every bucket the bars cover from its open, closed. A leading bucket the bars join part-way
    /// through and the still-forming last bucket are left out, so nothing partial is returned.
    /// </summary>
    public static IReadOnlyList<HtfBar> CloseAll(IEnumerable<Bar> bars, SignalTimeframe tf, int executionMinutes)
    {
        var aggregator = new SessionBarAggregator(tf, executionMinutes);
        var closed = new List<HtfBar>();
        bool? skipFirst = null;
        foreach (var bar in bars)
        {
            skipFirst ??= SessionBucket.For(tf, bar.Time).OpenUtc != bar.Time;
            if (aggregator.OnExecutionBar(bar) is not { } done) continue;
            if (skipFirst == true) { skipFirst = false; continue; }
            closed.Add(done);
        }
        return closed;
    }

    private void Start(Bar bar, DateTime open, DateTime end)
    {
        Forming     = new HtfBar(Timeframe, open, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        _formingEnd = end;
    }

    private HtfBar Close(HtfBar bar)
    {
        _lastClosedOpen = bar.OpenUtc;
        return bar;
    }
}
```

- [ ] **Step 4: Delete the dead aggregators**

```bash
git rm CRV.Live/BarBuilders/BarAggregator.cs CRV.Backtest/DataLoaders/BarResampler.cs
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SessionBarAggregatorTests|FullyQualifiedName~SourceTreeTests"`
Expected: PASS (13 + 3 tests).

Run: `dotnet build CRV.Trading.sln 2>&1 | tail -3`
Expected: `Build succeeded.` with `0 Error(s)` (nothing referenced the deleted files).

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Indicators/SessionBarAggregator.cs CRV.Core.Tests/Indicators/SessionBarAggregatorTests.cs CRV.Core.Tests/Indicators/SourceTreeTests.cs
git commit -m "feat(history): SessionBarAggregator closes HTF bars without lookahead"
```

---

### Task 5: `HtfBars` table and `HtfBarStore`

**Files:**
- Create: `CRV.Core/Models/HtfBarRow.cs`
- Create: `CRV.Core/Data/HtfBarStore.cs`
- Create: `CRV.Core/Migrations/<timestamp>_AddHtfBars.cs`, `.Designer.cs` (generated); Modify: `CRV.Core/Migrations/TradingDbContextModelSnapshot.cs` (generated)
- Modify: `CRV.Core/Data/TradingDbContext.cs`
- Create: `CRV.Core.Tests/Data/HtfBarStoreTests.cs`

**Interfaces:**
- Consumes: `HtfBar`, `SignalTimeframe` (Tasks 3–4).
- Produces:
  - `class HtfBarRow` (`CRV.Core.Models`): `int Id`, `string Root`, `SignalTimeframe Timeframe`, `DateTime OpenTime`, `decimal Open/High/Low/Close`, `long Volume`, `DateTime FilledAt`.
  - `DbSet<HtfBarRow> TradingDbContext.HtfBars`.
  - `sealed class HtfBarStore(TradingDbContext db)` (`CRV.Core.Data`): `Task<int> UpsertAsync(string root, IReadOnlyCollection<HtfBar> bars, CancellationToken ct = default)` (returns new rows), `Task<int> CountAsync(string root, SignalTimeframe tf, CancellationToken ct = default)`, `Task<IReadOnlyList<HtfBar>> LatestAsync(string root, SignalTimeframe tf, int count, CancellationToken ct = default)` (oldest first, `OpenUtc` Kind Utc), `Task<DateTime?> LastFilledUtcAsync(string root, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Data/HtfBarStoreTests.cs`:

```csharp
using CRV.Core.Data;
using CRV.Core.Indicators;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Data;

public class HtfBarStoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;
    private readonly HtfBarStore _store;

    public HtfBarStoreTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _store = new HtfBarStore(_db);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static HtfBar H4(int hour, decimal close)
        => new(SignalTimeframe.H4, new DateTime(2026, 9, 30, hour, 0, 0, DateTimeKind.Utc), close - 1, close + 1, close - 2, close, 100);

    private string ColumnType(string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT type FROM pragma_table_info('HtfBars') WHERE name = '{column}';";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task UpsertAsync_NewBars_InsertsAndCounts()
    {
        var inserted = await _store.UpsertAsync("NQ", [H4(2, 21000m), H4(6, 21010m)]);

        Assert.Equal(2, inserted);
        Assert.Equal(2, await _store.CountAsync("NQ", SignalTimeframe.H4));
        Assert.Equal(0, await _store.CountAsync("NQ", SignalTimeframe.D1));
        Assert.Equal(0, await _store.CountAsync("ES", SignalTimeframe.H4));
    }

    [Fact]
    public async Task UpsertAsync_SameKeyAgain_OverwritesWithoutDuplicating()
    {
        await _store.UpsertAsync("NQ", [H4(2, 21000m)]);

        var inserted = await _store.UpsertAsync("NQ", [H4(2, 21005m)]);

        Assert.Equal(0, inserted);
        var bar = Assert.Single(await _store.LatestAsync("NQ", SignalTimeframe.H4, 10));
        Assert.Equal(21005m, bar.Close);
    }

    [Fact]
    public async Task LatestAsync_ReturnsTheNewestOldestFirstInUtc()
    {
        await _store.UpsertAsync("NQ", [H4(10, 3m), H4(2, 1m), H4(6, 2m)]);

        var bars = await _store.LatestAsync("NQ", SignalTimeframe.H4, 2);

        Assert.Equal(new[] { 2m, 3m }, bars.Select(b => b.Close));
        Assert.Equal(DateTimeKind.Utc, bars[0].OpenUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 30, 6, 0, 0), bars[0].OpenUtc);
    }

    [Fact]
    public async Task LastFilledUtcAsync_IsNullUntilAWriteThenTheWriteTime()
    {
        Assert.Null(await _store.LastFilledUtcAsync("NQ"));
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _store.UpsertAsync("NQ", [H4(2, 1m)]);

        var filled = await _store.LastFilledUtcAsync("NQ");
        Assert.NotNull(filled);
        Assert.True(filled >= before);
        Assert.Equal(DateTimeKind.Utc, filled!.Value.Kind);
    }

    [Fact]
    public void HtfBars_Key_IsUniqueOnRootTimeframeAndOpenTime()
    {
        _db.HtfBars.Add(new() { Root = "NQ", Timeframe = SignalTimeframe.H4, OpenTime = new DateTime(2026, 9, 30, 2, 0, 0) });
        _db.HtfBars.Add(new() { Root = "NQ", Timeframe = SignalTimeframe.H4, OpenTime = new DateTime(2026, 9, 30, 2, 0, 0) });

        Assert.Throws<DbUpdateException>(() => _db.SaveChanges());
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("High")]
    [InlineData("Low")]
    [InlineData("Close")]
    public void HtfBars_Prices_AreStoredAsReal(string column)
        => Assert.Equal("REAL", ColumnType(column));

    [Fact]
    public void HtfBars_Timeframe_IsStoredAsItsName()
    {
        _db.HtfBars.Add(new() { Root = "NQ", Timeframe = SignalTimeframe.MN1, OpenTime = new DateTime(2026, 9, 30) });
        _db.SaveChanges();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT Timeframe FROM HtfBars";
        Assert.Equal("MN1", cmd.ExecuteScalar());
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HtfBarStoreTests"`
Expected: build FAILS with `The type or namespace name 'HtfBarStore' could not be found`.

- [ ] **Step 3: Write the entity and map it**

`CRV.Core/Models/HtfBarRow.cs`:

```csharp
using CRV.Core.Indicators;

namespace CRV.Core.Models;

/// <summary>
/// One stored higher-timeframe bar of a root's continuous history (contracts joined, unadjusted).
/// <see cref="OpenTime"/> is the bucket's scheduled open in UTC; <see cref="FilledAt"/> is when
/// the row was last written.
/// </summary>
public class HtfBarRow
{
    public int             Id        { get; set; }
    public string          Root      { get; set; } = "";
    public SignalTimeframe Timeframe { get; set; }
    public DateTime        OpenTime  { get; set; }
    public decimal         Open      { get; set; }
    public decimal         High      { get; set; }
    public decimal         Low       { get; set; }
    public decimal         Close     { get; set; }
    public long            Volume    { get; set; }
    public DateTime        FilledAt  { get; set; }
}
```

`CRV.Core/Data/TradingDbContext.cs` — after the `OptionChainSnapshots` DbSet line add:

```csharp
    public DbSet<HtfBarRow> HtfBars { get; set; } = null!;
```

and at the end of `OnModelCreating`, after the `StrategyLog` block, add:

```csharp

        // ── Higher-timeframe history (one continuous series per root) ──
        b.Entity<HtfBarRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Timeframe).HasConversion<string>();
            // One bar per root, timeframe and open: re-running a backfill or import updates rather than duplicates.
            e.HasIndex(r => new { r.Root, r.Timeframe, r.OpenTime }).IsUnique();
        });
```

- [ ] **Step 4: Write the store**

`CRV.Core/Data/HtfBarStore.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CRV.Core.Data;

/// <summary>Reads and writes a root's stored higher-timeframe history (the HtfBars table).</summary>
public sealed class HtfBarStore
{
    private readonly TradingDbContext _db;

    public HtfBarStore(TradingDbContext db) => _db = db;

    /// <summary>
    /// Inserts bars that are not stored yet and overwrites the ones that are, keyed on
    /// (root, timeframe, open time). Returns how many rows were new.
    /// </summary>
    public async Task<int> UpsertAsync(string root, IReadOnlyCollection<HtfBar> bars, CancellationToken ct = default)
    {
        if (bars.Count == 0) return 0;
        var now = DateTime.UtcNow;
        int inserted = 0;

        foreach (var group in bars.GroupBy(b => b.Timeframe))
        {
            var tf  = group.Key;
            var min = group.Min(b => b.OpenUtc);
            var max = group.Max(b => b.OpenUtc);
            var existing = await _db.HtfBars
                .Where(r => r.Root == root && r.Timeframe == tf && r.OpenTime >= min && r.OpenTime <= max)
                .ToDictionaryAsync(r => r.OpenTime, ct);

            foreach (var bar in group)
            {
                if (!existing.TryGetValue(bar.OpenUtc, out var row))
                {
                    row = new HtfBarRow { Root = root, Timeframe = tf, OpenTime = bar.OpenUtc };
                    _db.HtfBars.Add(row);
                    existing[bar.OpenUtc] = row;
                    inserted++;
                }
                row.Open     = bar.Open;
                row.High     = bar.High;
                row.Low      = bar.Low;
                row.Close    = bar.Close;
                row.Volume   = bar.Volume;
                row.FilledAt = now;
            }
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return inserted;
    }

    public Task<int> CountAsync(string root, SignalTimeframe tf, CancellationToken ct = default)
        => _db.HtfBars.CountAsync(r => r.Root == root && r.Timeframe == tf, ct);

    /// <summary>The newest <paramref name="count"/> bars, oldest first.</summary>
    public async Task<IReadOnlyList<HtfBar>> LatestAsync(string root, SignalTimeframe tf, int count, CancellationToken ct = default)
    {
        var rows = await _db.HtfBars.AsNoTracking()
            .Where(r => r.Root == root && r.Timeframe == tf)
            .OrderByDescending(r => r.OpenTime)
            .Take(count)
            .ToListAsync(ct);
        return rows
            .OrderBy(r => r.OpenTime)
            .Select(r => new HtfBar(r.Timeframe, DateTime.SpecifyKind(r.OpenTime, DateTimeKind.Utc),
                r.Open, r.High, r.Low, r.Close, r.Volume))
            .ToList();
    }

    /// <summary>When any of the root's bars was last written, or null when it has none.</summary>
    public async Task<DateTime?> LastFilledUtcAsync(string root, CancellationToken ct = default)
    {
        var last = await _db.HtfBars.Where(r => r.Root == root).MaxAsync(r => (DateTime?)r.FilledAt, ct);
        return last is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HtfBarStoreTests"`
Expected: PASS (10 tests).

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddHtfBars --project CRV.Core --startup-project CRV.Web`
Expected: `Done.` and three changed files. Open `CRV.Core/Migrations/<timestamp>_AddHtfBars.cs` and check `Up` creates exactly this (and `Down` drops `HtfBars`); if anything else appears (another table altered), stop — the model drifted from an earlier plan's migration.

```csharp
            migrationBuilder.CreateTable(
                name: "HtfBars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Root = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", nullable: false),
                    OpenTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Open = table.Column<double>(type: "REAL", nullable: false),
                    High = table.Column<double>(type: "REAL", nullable: false),
                    Low = table.Column<double>(type: "REAL", nullable: false),
                    Close = table.Column<double>(type: "REAL", nullable: false),
                    Volume = table.Column<long>(type: "INTEGER", nullable: false),
                    FilledAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HtfBars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HtfBars_Root_Timeframe_OpenTime",
                table: "HtfBars",
                columns: new[] { "Root", "Timeframe", "OpenTime" },
                unique: true);
```

- [ ] **Step 7: Apply every migration to a throwaway DB**

```bash
rm -f "$TMPDIR/htf-migrate.db"
dotnet ef database update --project CRV.Core --startup-project CRV.Web --connection "Data Source=$TMPDIR/htf-migrate.db"
sqlite3 "$TMPDIR/htf-migrate.db" "SELECT name, type FROM pragma_table_info('HtfBars');" && rm -f "$TMPDIR/htf-migrate.db"
```

Expected: `Done.`, then the ten columns with `Open|REAL`, `High|REAL`, `Low|REAL`, `Close|REAL`.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test CRV.Core.Tests 2>&1 | tail -3`
Expected: `Failed: 0, Passed: <N + 72>`.

- [ ] **Step 9: Commit**

```bash
git add CRV.Core/Models/HtfBarRow.cs CRV.Core/Data/HtfBarStore.cs CRV.Core/Data/TradingDbContext.cs CRV.Core/Migrations CRV.Core.Tests/Data/HtfBarStoreTests.cs
git commit -m "feat(history): HtfBars table and HtfBarStore"
```

---

### Task 6: `HistoryRequirement`

**Files:**
- Create: `CRV.Core/Strategy/HistoryRequirement.cs`
- Create: `CRV.Core.Tests/Strategy/HistoryRequirementTests.cs`

**Interfaces:**
- Consumes: `EasternTime.ToEastern` (Task 1).
- Produces (namespace `CRV.Core.Strategy`): `enum HistoryStatus { Blocked, ParityWarning, Ready }`; `static class HistoryRequirement` with `const int ParityMultiple = 3`, `(int Needed, int ParityNeeded) For(int period)`, `HistoryStatus Check(int stored, int period)`, `string Note(int stored, int period)`, `string SourceLine(string root, DateTime? lastFilledUtc)`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/HistoryRequirementTests.cs`:

```csharp
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class HistoryRequirementTests
{
    [Fact]
    public void For_Period_NeedsPeriodAndThreeTimesForParity()
        => Assert.Equal((200, 600), HistoryRequirement.For(200));

    [Theory]
    [InlineData(199, 200, HistoryStatus.Blocked)]
    [InlineData(200, 200, HistoryStatus.ParityWarning)]
    [InlineData(599, 200, HistoryStatus.ParityWarning)]
    [InlineData(600, 200, HistoryStatus.Ready)]
    [InlineData(0, 21, HistoryStatus.Blocked)]
    public void Check_AtTheBoundaries(int stored, int period, HistoryStatus expected)
        => Assert.Equal(expected, HistoryRequirement.Check(stored, period));

    [Theory]
    [InlineData(239, 200, HistoryStatus.ParityWarning)]  // MN1 × EMA 200 from the probe: allowed, warned
    [InlineData(1040, 200, HistoryStatus.Ready)]         // W1 × EMA 200: no warning
    public void Check_ProbeDepths(int stored, int period, HistoryStatus expected)
        => Assert.Equal(expected, HistoryRequirement.Check(stored, period));

    [Theory]
    [InlineData(150, 200, "50 more bars to load before this strategy can trade.")]
    [InlineData(199, 200, "1 more bar to load before this strategy can trade.")]
    [InlineData(239, 200, "The EMA matches TradingView once 3× its length is loaded.")]
    [InlineData(600, 200, "Ready.")]
    public void Note_ReadsAsThePanelShowsIt(int stored, int period, string expected)
        => Assert.Equal(expected, HistoryRequirement.Note(stored, period));

    [Fact]
    public void SourceLine_ShowsRootAndLastFillInEastern()
        => Assert.Equal(
            "Schwab price history (NQ, contracts joined, unadjusted) · last filled 2026-10-02 12:59 ET",
            HistoryRequirement.SourceLine("NQ", new DateTime(2026, 10, 2, 16, 59, 0, DateTimeKind.Utc)));

    [Fact]
    public void SourceLine_NeverFilled_SaysSo()
        => Assert.Equal(
            "Schwab price history (NQ, contracts joined, unadjusted) · not filled yet",
            HistoryRequirement.SourceLine("NQ", null));

    [Fact]
    public void For_PeriodBelowOne_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => HistoryRequirement.For(0));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HistoryRequirementTests"`
Expected: build FAILS with `The name 'HistoryRequirement' does not exist in the current context`.

- [ ] **Step 3: Write `HistoryRequirement`**

`CRV.Core/Strategy/HistoryRequirement.cs`:

```csharp
using CRV.Core.Indicators;

namespace CRV.Core.Strategy;

public enum HistoryStatus { Blocked, ParityWarning, Ready }

/// <summary>
/// How much stored history an EMA needs. A strategy can be switched on once the stored bars
/// for its timeframe reach the EMA period; the SMA seed only washes out, and the value
/// matches TradingView, once three times the period is loaded.
/// </summary>
public static class HistoryRequirement
{
    public const int ParityMultiple = 3;

    public static (int Needed, int ParityNeeded) For(int period)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        return (period, period * ParityMultiple);
    }

    public static HistoryStatus Check(int stored, int period)
    {
        var (needed, parity) = For(period);
        if (stored < needed) return HistoryStatus.Blocked;
        return stored < parity ? HistoryStatus.ParityWarning : HistoryStatus.Ready;
    }

    /// <summary>The History panel's note for one timeframe.</summary>
    public static string Note(int stored, int period)
    {
        var (needed, _) = For(period);
        return Check(stored, period) switch
        {
            HistoryStatus.Blocked when needed - stored == 1 => "1 more bar to load before this strategy can trade.",
            HistoryStatus.Blocked       => $"{needed - stored} more bars to load before this strategy can trade.",
            HistoryStatus.ParityWarning => "The EMA matches TradingView once 3× its length is loaded.",
            _                           => "Ready.",
        };
    }

    /// <summary>The History panel's source line, with the last fill shown in Eastern time.</summary>
    public static string SourceLine(string root, DateTime? lastFilledUtc)
    {
        var filled = lastFilledUtc is { } t
            ? $"last filled {EasternTime.ToEastern(t):yyyy-MM-dd HH:mm} ET"
            : "not filled yet";
        return $"Schwab price history ({root}, contracts joined, unadjusted) · {filled}";
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HistoryRequirementTests"`
Expected: PASS (15 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/HistoryRequirement.cs CRV.Core.Tests/Strategy/HistoryRequirementTests.cs
git commit -m "feat(history): enable and TradingView-parity rules from stored bar counts"
```

---

### Task 7: `HtfCsvImport`

**Files:**
- Create: `CRV.Backtest/DataLoaders/HtfCsvImport.cs`
- Create: `CRV.Core.Tests/Backtest/HtfCsvImportTests.cs`

**Interfaces:**
- Consumes: `SessionBucket.For` (Task 3), `HtfBar` (Task 4), `HtfBarStore.UpsertAsync`, `HtfBarStore.CountAsync` (Task 5).
- Produces (namespace `CRV.Backtest.DataLoaders`): `static class HtfCsvImport` with `Task<IReadOnlyList<HtfBar>> ReadAsync(TextReader reader, SignalTimeframe tf, CancellationToken ct = default)` (throws `FormatException` with the column or line) and `Task<int> ImportAsync(TextReader reader, string root, SignalTimeframe tf, HtfBarStore store, CancellationToken ct = default)` (returns new rows).

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Backtest/HtfCsvImportTests.cs`:

```csharp
using CRV.Backtest.DataLoaders;
using CRV.Core.Data;
using CRV.Core.Indicators;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Backtest;

public class HtfCsvImportTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public HtfCsvImportTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static DateTime Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    // TradingView's default export: UNIX seconds, plus whatever indicator columns were on the chart.
    // 1790719200 = 2026-09-29 22:00Z (18:00 ET), 1790733600 = 02:00Z, 1790748000 = 06:00Z.
    private const string H4Unix = """
        time,open,high,low,close,EMA,Volume
        1790719200,21000,21050,20990,21040,20980.5,1200
        1790733600,21040,21060,21010,21020,20985.1,800
        1790748000,21020,21100,21015,21090,20991.2,950
        """;

    [Fact]
    public async Task ReadAsync_UnixSecondsExport_ReadsEachBarOnItsBucket()
    {
        var bars = await HtfCsvImport.ReadAsync(new StringReader(H4Unix), SignalTimeframe.H4);

        Assert.Equal(3, bars.Count);
        Assert.Equal(new HtfBar(SignalTimeframe.H4, Utc(9, 29, 22), 21000m, 21050m, 20990m, 21040m, 1200), bars[0]);
        Assert.Equal(Utc(9, 30, 6), bars[2].OpenUtc);
    }

    [Fact]
    public async Task ReadAsync_IsoExportWithoutVolume_ReadsOffsetTimes()
    {
        const string csv = """
            time,open,high,low,close
            2026-09-29T18:00:00-04:00,21000,21050,20990,21040
            """;

        var bar = Assert.Single(await HtfCsvImport.ReadAsync(new StringReader(csv), SignalTimeframe.H4));

        Assert.Equal(Utc(9, 29, 22), bar.OpenUtc);
        Assert.Equal(0, bar.Volume);
    }

    [Theory]
    [InlineData("1790726400")]                 // 2026-09-30 00:00Z, TradingView's daily stamp
    [InlineData("2026-09-30T00:00:00-05:00")]  // midnight Chicago
    [InlineData("2026-09-29T18:00:00-04:00")]  // the session open itself
    public async Task ReadAsync_DailyStamps_MapToTheTradingDatesSessionOpen(string time)
    {
        var csv = $"time,open,high,low,close\n{time},1,2,0,1\n";

        var bar = Assert.Single(await HtfCsvImport.ReadAsync(new StringReader(csv), SignalTimeframe.D1));

        Assert.Equal(Utc(9, 29, 22), bar.OpenUtc);
    }

    [Fact]
    public async Task ReadAsync_MissingCloseColumn_NamesIt()
    {
        var ex = await Assert.ThrowsAsync<FormatException>(() =>
            HtfCsvImport.ReadAsync(new StringReader("time,open,high,low\n1790719200,1,2,0\n"), SignalTimeframe.H4));
        Assert.Contains("'close'", ex.Message);
    }

    [Fact]
    public async Task ReadAsync_BadNumber_NamesTheLine()
    {
        var ex = await Assert.ThrowsAsync<FormatException>(() =>
            HtfCsvImport.ReadAsync(new StringReader("time,open,high,low,close\n1790719200,1,2,0,abc\n"), SignalTimeframe.H4));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public async Task ReadAsync_TwoRowsInOneBucket_RejectsTheWrongTimeframe()
    {
        // Hourly rows imported as H4: 18:00 and 19:00 ET share one H4 bucket.
        const string csv = "time,open,high,low,close\n1790719200,1,2,0,1\n1790722800,1,2,0,1\n";

        var ex = await Assert.ThrowsAsync<FormatException>(() =>
            HtfCsvImport.ReadAsync(new StringReader(csv), SignalTimeframe.H4));
        Assert.Contains("exported at H4", ex.Message);
    }

    [Fact]
    public async Task ImportAsync_Twice_StoresEachBarOnce()
    {
        var store = new HtfBarStore(_db);

        Assert.Equal(3, await HtfCsvImport.ImportAsync(new StringReader(H4Unix), "NQ", SignalTimeframe.H4, store));
        Assert.Equal(0, await HtfCsvImport.ImportAsync(new StringReader(H4Unix), "NQ", SignalTimeframe.H4, store));
        Assert.Equal(3, await store.CountAsync("NQ", SignalTimeframe.H4));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HtfCsvImportTests"`
Expected: build FAILS with `The name 'HtfCsvImport' does not exist in the current context`.

- [ ] **Step 3: Write the importer**

`CRV.Backtest/DataLoaders/HtfCsvImport.cs`:

```csharp
using System.Globalization;
using CRV.Core.Data;
using CRV.Core.Indicators;

namespace CRV.Backtest.DataLoaders;

/// <summary>
/// Imports a TradingView chart export (columns <c>time,open,high,low,close[,Volume]</c>, time as
/// UNIX seconds or ISO 8601) into the stored history, for depth Schwab no longer serves.
/// Each row's time is mapped to the session bucket that contains it, so TradingView's own
/// daily, weekly and monthly stamps land on the same keys as our bars.
/// </summary>
public static class HtfCsvImport
{
    public static async Task<IReadOnlyList<HtfBar>> ReadAsync(TextReader reader, SignalTimeframe tf, CancellationToken ct = default)
    {
        var header = await reader.ReadLineAsync(ct) ?? throw new FormatException("The CSV is empty.");
        var cols = header.Split(',').Select(c => c.Trim().Trim('"').ToLowerInvariant()).ToArray();
        int Col(string name, bool required = true)
        {
            var i = Array.IndexOf(cols, name);
            if (i < 0 && required) throw new FormatException($"The CSV has no '{name}' column.");
            return i;
        }
        int iTime = Col("time"), iOpen = Col("open"), iHigh = Col("high"), iLow = Col("low"), iClose = Col("close");
        int iVolume = Col("volume", required: false);

        var bars = new Dictionary<DateTime, HtfBar>();
        int line = 1;
        while (await reader.ReadLineAsync(ct) is { } row)
        {
            line++;
            if (string.IsNullOrWhiteSpace(row)) continue;
            var p = row.Split(',');
            var time = ParseTime(Field(p, iTime, line), line);
            var open = SessionBucket.For(tf, time).OpenUtc;
            if (bars.ContainsKey(open))
                throw new FormatException(
                    $"CSV line {line}: a second row falls in the {tf} bar opening {open:u}. Was the chart exported at {tf}?");
            bars[open] = new HtfBar(tf, open,
                Price(p, iOpen, line), Price(p, iHigh, line), Price(p, iLow, line), Price(p, iClose, line),
                iVolume >= 0 && iVolume < p.Length && decimal.TryParse(p[iVolume], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? (long)v : 0);
        }
        return bars.Values.OrderBy(b => b.OpenUtc).ToList();
    }

    /// <summary>Reads the CSV and stores its bars under <paramref name="root"/>; returns how many rows were new.</summary>
    public static async Task<int> ImportAsync(TextReader reader, string root, SignalTimeframe tf, HtfBarStore store, CancellationToken ct = default)
        => await store.UpsertAsync(root, await ReadAsync(reader, tf, ct), ct);

    private static string Field(string[] p, int i, int line)
        => i < p.Length ? p[i].Trim().Trim('"') : throw new FormatException($"CSV line {line}: too few columns.");

    private static DateTime ParseTime(string s, int line)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcDateTime;
        throw new FormatException($"CSV line {line}: '{s}' is not a time.");
    }

    private static decimal Price(string[] p, int i, int line)
        => decimal.TryParse(Field(p, i, line), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : throw new FormatException($"CSV line {line}: '{p[i]}' is not a number.");
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HtfCsvImportTests"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Backtest/DataLoaders/HtfCsvImport.cs CRV.Core.Tests/Backtest/HtfCsvImportTests.cs
git commit -m "feat(history): import TradingView CSV exports into HtfBars"
```

---

### Task 8: Price-history fetcher contract and paging past the 40,000-bar cap

**Files:**
- Create: `CRV.Backtest/DataLoaders/SchwabHistoryBackfill.cs`
- Create: `CRV.Core.Tests/Backtest/SchwabHistoryBackfillTests.cs`

**Interfaces:**
- Consumes: `BarLoadException` (`CRV.Backtest/DataLoaders/BarSnapshotStore.cs:19`), `Bar`.
- Produces (namespace `CRV.Backtest.DataLoaders`):
  - `enum PriceHistoryFrequency { Daily, Minute30, Minute1 }`
  - `record PriceHistoryQuery(string Symbol, PriceHistoryFrequency Frequency, DateTime FromUtc, DateTime ToUtc)`
  - `interface IPriceHistoryFetcher { Task<IReadOnlyList<Bar>> FetchAsync(PriceHistoryQuery query, CancellationToken ct); }`
  - `SchwabHistoryBackfill.RequestBarCap` (`const int` 40,000) and `internal static Task<IReadOnlyList<Bar>> SchwabHistoryBackfill.FetchPagedAsync(IPriceHistoryFetcher fetcher, PriceHistoryQuery query, int cap, CancellationToken ct)` (visible to tests through the existing `InternalsVisibleTo`).

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Backtest/SchwabHistoryBackfillTests.cs`:

```csharp
using CRV.Backtest.DataLoaders;
using CRV.Core.Data;
using CRV.Core.Indicators;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The backfill against a fake fetcher that behaves like Schwab's pricehistory: bars inside the
/// window, and only the most recent <c>cap</c> of them when the window holds more. The real
/// Schwab client is never constructed here.
/// </summary>
public class SchwabHistoryBackfillTests : IDisposable
{
    private sealed class FakeFetcher(IReadOnlyList<Bar> daily, IReadOnlyList<Bar> m30, IReadOnlyList<Bar> m1,
        int cap = SchwabHistoryBackfill.RequestBarCap) : IPriceHistoryFetcher
    {
        public List<PriceHistoryQuery> Queries { get; } = [];

        public Task<IReadOnlyList<Bar>> FetchAsync(PriceHistoryQuery q, CancellationToken ct)
        {
            Queries.Add(q);
            var source = q.Frequency switch
            {
                PriceHistoryFrequency.Daily    => daily,
                PriceHistoryFrequency.Minute30 => m30,
                _                              => m1,
            };
            var window = source.Where(b => b.Time >= q.FromUtc && b.Time <= q.ToUtc).ToList();
            IReadOnlyList<Bar> page = window.Count > cap ? window.Skip(window.Count - cap).ToList() : window;
            return Task.FromResult(page);
        }
    }

    /// <summary>Returns the same full page whatever window is asked for.</summary>
    private sealed class StuckFetcher(IReadOnlyList<Bar> page) : IPriceHistoryFetcher
    {
        public Task<IReadOnlyList<Bar>> FetchAsync(PriceHistoryQuery q, CancellationToken ct) => Task.FromResult(page);
    }

    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public SchwabHistoryBackfillTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static readonly DateTime Start = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    private static List<Bar> Minutes(int count)
        => Enumerable.Range(0, count).Select(i => new Bar(Start.AddMinutes(i), 1, 2, 0, 1, 1)).ToList();

    private static PriceHistoryQuery MinuteQuery(DateTime toUtc)
        => new("/NQ", PriceHistoryFrequency.Minute1, Start.AddDays(-1), toUtc);

    [Theory]
    [InlineData(100_000, 3)] // 40,000 + 40,000 + 20,000
    [InlineData(80_000, 3)]  // two full pages, then an empty one
    [InlineData(10, 1)]
    public async Task FetchPagedAsync_PagesPastTheCapWithoutGapsOrDuplicates(int total, int expectedRequests)
    {
        var series = Minutes(total);
        var fetcher = new FakeFetcher([], [], series);

        var bars = await SchwabHistoryBackfill.FetchPagedAsync(
            fetcher, MinuteQuery(series[^1].Time), SchwabHistoryBackfill.RequestBarCap, CancellationToken.None);

        Assert.Equal(series.Select(b => b.Time), bars.Select(b => b.Time));
        Assert.Equal(expectedRequests, fetcher.Queries.Count);
        for (int i = 1; i < fetcher.Queries.Count; i++)
            Assert.True(fetcher.Queries[i].ToUtc < fetcher.Queries[i - 1].ToUtc);
    }

    [Fact]
    public async Task FetchPagedAsync_FetcherThatIgnoresTheWindow_FailsInsteadOfLooping()
    {
        var page = Minutes(SchwabHistoryBackfill.RequestBarCap);

        await Assert.ThrowsAsync<BarLoadException>(() => SchwabHistoryBackfill.FetchPagedAsync(
            new StuckFetcher(page), MinuteQuery(page[^1].Time.AddDays(1)), SchwabHistoryBackfill.RequestBarCap, CancellationToken.None));
    }
}
```

(The `CRV.Core.Data`, `CRV.Core.Indicators` and logging usings are used by Task 9's tests in this file.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SchwabHistoryBackfillTests"`
Expected: build FAILS with `The type or namespace name 'IPriceHistoryFetcher' could not be found`.

- [ ] **Step 3: Write the contract and the pager**

`CRV.Backtest/DataLoaders/SchwabHistoryBackfill.cs`:

```csharp
using CRV.Core.Models;

namespace CRV.Backtest.DataLoaders;

public enum PriceHistoryFrequency { Daily, Minute30, Minute1 }

/// <summary>One price-history request: a continuous symbol such as "/NQ", a bar size, an inclusive UTC window.</summary>
public record PriceHistoryQuery(string Symbol, PriceHistoryFrequency Frequency, DateTime FromUtc, DateTime ToUtc);

/// <summary>Fetches one page of price history.</summary>
public interface IPriceHistoryFetcher
{
    /// <summary>
    /// Bars inside the query window, oldest first. A window holding more than
    /// <see cref="SchwabHistoryBackfill.RequestBarCap"/> bars returns only the most recent ones.
    /// </summary>
    Task<IReadOnlyList<Bar>> FetchAsync(PriceHistoryQuery query, CancellationToken ct);
}

public sealed class SchwabHistoryBackfill
{
    /// <summary>Schwab returns at most this many bars per request, keeping the most recent.</summary>
    public const int RequestBarCap = 40_000;

    private const int MaxPages = 500;

    /// <summary>
    /// Every bar in the query window. Schwab keeps the most recent bars when a request is over
    /// its cap, so a full page means older bars remain: the next request ends just before the
    /// oldest bar received. Stops on a short or empty page.
    /// </summary>
    internal static async Task<IReadOnlyList<Bar>> FetchPagedAsync(
        IPriceHistoryFetcher fetcher, PriceHistoryQuery query, int cap, CancellationToken ct)
    {
        var bars = new List<Bar>();
        var to = query.ToUtc;
        for (int page = 0; ; page++)
        {
            if (page == MaxPages)
                throw new BarLoadException($"{query.Symbol} {query.Frequency} history needed more than {MaxPages} requests.");
            var batch = await fetcher.FetchAsync(query with { ToUtc = to }, ct);
            if (batch.Count == 0) break;
            bars.AddRange(batch);
            if (batch.Count < cap) break;

            var oldest = batch.Min(b => b.Time);
            if (oldest <= query.FromUtc) break;
            if (oldest > to)
                throw new BarLoadException($"{query.Symbol} {query.Frequency} history returned bars after the requested end {to:u}.");
            to = oldest.AddMilliseconds(-1);
        }
        return bars
            .Where(b => b.Time >= query.FromUtc && b.Time <= query.ToUtc)
            .DistinctBy(b => b.Time)
            .OrderBy(b => b.Time)
            .ToList();
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SchwabHistoryBackfillTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Backtest/DataLoaders/SchwabHistoryBackfill.cs CRV.Core.Tests/Backtest/SchwabHistoryBackfillTests.cs
git commit -m "feat(history): page price history past Schwab's 40,000-bar cap"
```

---

### Task 9: `SchwabHistoryBackfill.RunAsync` and the Schwab HTTP fetcher

**Files:**
- Modify: `CRV.Backtest/DataLoaders/SchwabHistoryBackfill.cs` (replace the whole file)
- Modify: `CRV.Core.Tests/Backtest/SchwabHistoryBackfillTests.cs`

**Interfaces:**
- Consumes: `IPriceHistoryFetcher`, `PriceHistoryQuery`, `FetchPagedAsync` (Task 8); `SessionBucket.For`, `SessionBucket.SessionMinutes` (Task 3); `SessionBarAggregator.CloseAll`, `HtfBar` (Task 4); `HtfBarStore.UpsertAsync` (Task 5).
- Produces:
  - `SchwabHistoryBackfill(IPriceHistoryFetcher fetcher, HtfBarStore store, ILogger<SchwabHistoryBackfill> log)` with `Task<IReadOnlyDictionary<SignalTimeframe, int>> RunAsync(string root, DateTime nowUtc, CancellationToken ct = default)` (new rows per timeframe; all nine keys present).
  - `sealed class SchwabPriceHistoryFetcher(HttpClient http, string accessToken, string apiBaseUrl) : IPriceHistoryFetcher` — used only by `HistoryController` (Task 11).

- [ ] **Step 1: Write the failing tests**

Append inside `SchwabHistoryBackfillTests` (after `FetchPagedAsync_FetcherThatIgnoresTheWindow_FailsInsteadOfLooping`):

```csharp

    // "Now" is Friday 2026-10-02 12:59 ET (16:59Z), when the probe ran: Friday's session is still open.
    private static readonly DateTime Now = new(2026, 10, 2, 16, 59, 0, DateTimeKind.Utc);

    // Schwab daily bars stamped 05:00Z (midnight Central) for Mon Sep 21 – Fri Oct 2.
    private static List<Bar> DailyBars()
    {
        var bars = new List<Bar>();
        for (var d = new DateTime(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc); d <= new DateTime(2026, 10, 2, 5, 0, 0, DateTimeKind.Utc); d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                bars.Add(new Bar(d, 100 + d.Day, 110 + d.Day, 90 + d.Day, 105 + d.Day, 1000));
        return bars;
    }

    // Session-hours bars (no 17:00–18:00 ET break, 21:00–22:00Z) from `first` to `last`.
    private static List<Bar> SessionBars(int minutes, DateTime first, DateTime last)
    {
        var bars = new List<Bar>();
        for (var t = first; t <= last; t = t.AddMinutes(minutes))
            if (t.Hour != 21)
                bars.Add(new Bar(t, 1, 2, 0, 1, 1));
        return bars;
    }

    // Schwab also serves the bar still forming at Now: 30-min 16:30Z and today's daily bar.
    private FakeFetcher ProbeShapedFetcher() => new(
        DailyBars(),
        SessionBars(30, new DateTime(2026, 9, 29, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 16, 30, 0, DateTimeKind.Utc)),
        SessionBars(1, new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 16, 58, 0, DateTimeKind.Utc)));

    [Fact]
    public async Task RunAsync_StoresEveryTimeframeAndNothingStillForming()
    {
        var store = new HtfBarStore(_db);
        var backfill = new SchwabHistoryBackfill(ProbeShapedFetcher(), store, NullLogger<SchwabHistoryBackfill>.Instance);

        var inserted = await backfill.RunAsync("NQ", Now);

        // D1: Sep 21 – Oct 1 (Friday's session is open). W1: Sep 21–25 (the next week is forming).
        // MN1: September was joined part-way, October is forming.
        // 30-min from Wed's session: two full sessions plus Friday up to 12:00 ET (the 12:30 ET bar is unfinished).
        // 1-min from Friday's session to 12:58 ET.
        var expected = new Dictionary<SignalTimeframe, int>
        {
            [SignalTimeframe.D1] = 9, [SignalTimeframe.W1] = 1, [SignalTimeframe.MN1] = 0,
            [SignalTimeframe.M30] = 46 + 46 + 37, [SignalTimeframe.H1] = 23 + 23 + 18,
            [SignalTimeframe.H4] = 6 + 6 + 4, [SignalTimeframe.H8] = 3 + 3 + 2,
            [SignalTimeframe.M5] = 227, [SignalTimeframe.M15] = 75,
        };
        Assert.Equal(expected, inserted);
        foreach (var (tf, n) in expected)
            Assert.Equal(n, await store.CountAsync("NQ", tf));
    }

    [Fact]
    public async Task RunAsync_MapsSchwabDailyStampsToTheSessionOpen()
    {
        var store = new HtfBarStore(_db);
        await new SchwabHistoryBackfill(ProbeShapedFetcher(), store, NullLogger<SchwabHistoryBackfill>.Instance).RunAsync("NQ", Now);

        var last = (await store.LatestAsync("NQ", SignalTimeframe.D1, 1))[0];

        // Thursday Oct 1's Schwab bar (stamped 2026-10-01 05:00Z) is the session that opened Wed 18:00 ET.
        Assert.Equal(new HtfBar(SignalTimeframe.D1, new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc), 101, 111, 91, 106, 1000), last);
    }

    [Fact]
    public async Task RunAsync_Twice_InsertsNothingTheSecondTime()
    {
        var store = new HtfBarStore(_db);
        var backfill = new SchwabHistoryBackfill(ProbeShapedFetcher(), store, NullLogger<SchwabHistoryBackfill>.Instance);
        await backfill.RunAsync("NQ", Now);
        var before = _db.HtfBars.Count();

        var second = await backfill.RunAsync("NQ", Now);

        Assert.All(second.Values, n => Assert.Equal(0, n));
        Assert.Equal(before, _db.HtfBars.Count());
    }

    [Fact]
    public async Task RunAsync_AsksForTheContinuousRootSymbol()
    {
        var fetcher = ProbeShapedFetcher();
        await new SchwabHistoryBackfill(fetcher, new HtfBarStore(_db), NullLogger<SchwabHistoryBackfill>.Instance).RunAsync("NQ", Now);

        Assert.All(fetcher.Queries, q => Assert.Equal("/NQ", q.Symbol));
        Assert.Contains(fetcher.Queries, q => q.Frequency == PriceHistoryFrequency.Daily && q.FromUtc == Now.AddYears(-20));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SchwabHistoryBackfillTests"`
Expected: build FAILS with `'SchwabHistoryBackfill' does not contain a constructor that takes 3 arguments`.

- [ ] **Step 3: Replace `CRV.Backtest/DataLoaders/SchwabHistoryBackfill.cs`**

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using CRV.Core.Data;
using CRV.Core.Indicators;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

namespace CRV.Backtest.DataLoaders;

public enum PriceHistoryFrequency { Daily, Minute30, Minute1 }

/// <summary>One price-history request: a continuous symbol such as "/NQ", a bar size, an inclusive UTC window.</summary>
public record PriceHistoryQuery(string Symbol, PriceHistoryFrequency Frequency, DateTime FromUtc, DateTime ToUtc);

/// <summary>Fetches one page of price history. Schwab's implementation is <see cref="SchwabPriceHistoryFetcher"/>.</summary>
public interface IPriceHistoryFetcher
{
    /// <summary>
    /// Bars inside the query window, oldest first. A window holding more than
    /// <see cref="SchwabHistoryBackfill.RequestBarCap"/> bars returns only the most recent ones.
    /// </summary>
    Task<IReadOnlyList<Bar>> FetchAsync(PriceHistoryQuery query, CancellationToken ct);
}

/// <summary>
/// Fills a root's stored history from Schwab price history, sized to what Schwab serves
/// (2026-10-02 probe): D1 from 20 years of daily bars, W1 and MN1 built from that D1 so they
/// follow our session anchoring; M30, H1, H4 and H8 from 30-minute bars; M5 and M15 from
/// 1-minute bars, paged past the per-request cap. Buckets still forming at
/// <c>nowUtc</c> are never stored. Re-running only overwrites rows with the same key.
/// </summary>
public sealed class SchwabHistoryBackfill
{
    /// <summary>Schwab returns at most this many bars per request, keeping the most recent.</summary>
    public const int RequestBarCap = 40_000;

    private const int MaxPages = 500;

    private static readonly SignalTimeframe[] From30Minute = [SignalTimeframe.M30, SignalTimeframe.H1, SignalTimeframe.H4, SignalTimeframe.H8];
    private static readonly SignalTimeframe[] From1Minute  = [SignalTimeframe.M5, SignalTimeframe.M15];

    private readonly IPriceHistoryFetcher _fetcher;
    private readonly HtfBarStore _store;
    private readonly ILogger _log;

    public SchwabHistoryBackfill(IPriceHistoryFetcher fetcher, HtfBarStore store, ILogger<SchwabHistoryBackfill> log)
    {
        _fetcher = fetcher;
        _store   = store;
        _log     = log;
    }

    /// <summary>Backfills every signal timeframe for <paramref name="root"/> (e.g. "NQ"); returns the new rows per timeframe.</summary>
    public async Task<IReadOnlyDictionary<SignalTimeframe, int>> RunAsync(string root, DateTime nowUtc, CancellationToken ct = default)
    {
        var symbol   = "/" + root;
        var inserted = new Dictionary<SignalTimeframe, int>();

        var daily = await _fetcher.FetchAsync(
            new PriceHistoryQuery(symbol, PriceHistoryFrequency.Daily, nowUtc.AddYears(-20), nowUtc), ct);
        var d1 = DailyBars(daily, nowUtc);
        await StoreAsync(root, SignalTimeframe.D1, d1, inserted, ct);

        var d1AsExecution = d1.Select(b => new Bar(b.OpenUtc, b.Open, b.High, b.Low, b.Close, b.Volume)).ToList();
        foreach (var tf in new[] { SignalTimeframe.W1, SignalTimeframe.MN1 })
            await StoreAsync(root, tf, SessionBarAggregator.CloseAll(d1AsExecution, tf, SessionBucket.SessionMinutes), inserted, ct);

        var m30 = Ended(await FetchPagedAsync(_fetcher,
            new PriceHistoryQuery(symbol, PriceHistoryFrequency.Minute30, nowUtc.AddYears(-1), nowUtc), RequestBarCap, ct), 30, nowUtc);
        foreach (var tf in From30Minute)
            await StoreAsync(root, tf, SessionBarAggregator.CloseAll(m30, tf, 30), inserted, ct);

        var m1 = Ended(await FetchPagedAsync(_fetcher,
            new PriceHistoryQuery(symbol, PriceHistoryFrequency.Minute1, nowUtc.AddYears(-1), nowUtc), RequestBarCap, ct), 1, nowUtc);
        foreach (var tf in From1Minute)
            await StoreAsync(root, tf, SessionBarAggregator.CloseAll(m1, tf, 1), inserted, ct);

        return inserted;
    }

    /// <summary>
    /// Every bar in the query window. Schwab keeps the most recent bars when a request is over
    /// its cap, so a full page means older bars remain: the next request ends just before the
    /// oldest bar received. Stops on a short or empty page.
    /// </summary>
    internal static async Task<IReadOnlyList<Bar>> FetchPagedAsync(
        IPriceHistoryFetcher fetcher, PriceHistoryQuery query, int cap, CancellationToken ct)
    {
        var bars = new List<Bar>();
        var to = query.ToUtc;
        for (int page = 0; ; page++)
        {
            if (page == MaxPages)
                throw new BarLoadException($"{query.Symbol} {query.Frequency} history needed more than {MaxPages} requests.");
            var batch = await fetcher.FetchAsync(query with { ToUtc = to }, ct);
            if (batch.Count == 0) break;
            bars.AddRange(batch);
            if (batch.Count < cap) break;

            var oldest = batch.Min(b => b.Time);
            if (oldest <= query.FromUtc) break;
            if (oldest > to)
                throw new BarLoadException($"{query.Symbol} {query.Frequency} history returned bars after the requested end {to:u}.");
            to = oldest.AddMilliseconds(-1);
        }
        return bars
            .Where(b => b.Time >= query.FromUtc && b.Time <= query.ToUtc)
            .DistinctBy(b => b.Time)
            .OrderBy(b => b.Time)
            .ToList();
    }

    /// <summary>
    /// Schwab daily bars as D1 bars on our session keys. Schwab stamps each daily bar at
    /// midnight Central of its trading date, which the D1 bucket maps to that date's 18:00 ET open.
    /// </summary>
    internal static IReadOnlyList<HtfBar> DailyBars(IEnumerable<Bar> daily, DateTime nowUtc)
        => daily
            .Select(b => (Bucket: SessionBucket.For(SignalTimeframe.D1, b.Time), Bar: b))
            .Where(x => x.Bucket.EndUtc <= nowUtc)
            .DistinctBy(x => x.Bucket.OpenUtc)
            .OrderBy(x => x.Bucket.OpenUtc)
            .Select(x => new HtfBar(SignalTimeframe.D1, x.Bucket.OpenUtc, x.Bar.Open, x.Bar.High, x.Bar.Low, x.Bar.Close, x.Bar.Volume))
            .ToList();

    // The bar still forming at nowUtc is partial: Schwab serves it as if it were complete.
    private static List<Bar> Ended(IEnumerable<Bar> bars, int minutes, DateTime nowUtc)
        => bars.Where(b => b.Time.AddMinutes(minutes) <= nowUtc).ToList();

    private async Task StoreAsync(string root, SignalTimeframe tf, IReadOnlyList<HtfBar> bars,
        Dictionary<SignalTimeframe, int> inserted, CancellationToken ct)
    {
        inserted[tf] = await _store.UpsertAsync(root, bars, ct);
        _log.LogInformation("[HISTORY] {Root} {Tf}: {Total} bars, {New} new", root, tf, bars.Count, inserted[tf]);
    }
}

/// <summary>
/// Schwab <c>pricehistory</c> over HTTP, with the request shapes the 2026-10-02 probe verified.
/// Never used by tests: they drive <see cref="SchwabHistoryBackfill"/> through a fake fetcher.
/// </summary>
public sealed class SchwabPriceHistoryFetcher : IPriceHistoryFetcher
{
    private readonly HttpClient _http;
    private readonly string _accessToken;
    private readonly string _apiBaseUrl;

    public SchwabPriceHistoryFetcher(HttpClient http, string accessToken, string apiBaseUrl)
    {
        _http        = http;
        _accessToken = accessToken;
        _apiBaseUrl  = apiBaseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<Bar>> FetchAsync(PriceHistoryQuery query, CancellationToken ct)
    {
        var symbol = Uri.EscapeDataString(query.Symbol);
        var range  = query.Frequency == PriceHistoryFrequency.Daily
            ? "periodType=year&period=20&frequencyType=daily&frequency=1"
            : $"periodType=day&frequencyType=minute&frequency={(query.Frequency == PriceHistoryFrequency.Minute30 ? 30 : 1)}" +
              $"&startDate={new DateTimeOffset(query.FromUtc).ToUnixTimeMilliseconds()}" +
              $"&endDate={new DateTimeOffset(query.ToUtc).ToUnixTimeMilliseconds()}";

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_apiBaseUrl}/marketdata/v1/pricehistory?symbol={symbol}&{range}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new BarLoadException($"Schwab price history request failed for {query.Symbol} {query.Frequency}.", ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new BarLoadException(
                    $"Schwab price history returned HTTP {(int)response.StatusCode} for {query.Symbol} {query.Frequency}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("candles", out var candles)) return [];

            var bars = new List<Bar>();
            foreach (var c in candles.EnumerateArray())
            {
                if (!c.TryGetProperty("datetime", out var ts)) continue;
                var time = DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64()).UtcDateTime;
                if (time < query.FromUtc || time > query.ToUtc) continue;
                bars.Add(new Bar(time,
                    c.GetProperty("open").GetDecimal(), c.GetProperty("high").GetDecimal(),
                    c.GetProperty("low").GetDecimal(),  c.GetProperty("close").GetDecimal(),
                    c.TryGetProperty("volume", out var v) ? v.GetInt64() : 0));
            }
            return bars.OrderBy(b => b.Time).ToList();
        }
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SchwabHistoryBackfillTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add CRV.Backtest/DataLoaders/SchwabHistoryBackfill.cs CRV.Core.Tests/Backtest/SchwabHistoryBackfillTests.cs
git commit -m "feat(history): Schwab backfill of D1/W1/MN1, 30-minute and 1-minute history"
```

---

### Task 10: Live top-ups

**Files:**
- Create: `CRV.Core/Data/HtfTopUp.cs`
- Create: `CRV.Core.Tests/Data/HtfTopUpTests.cs`
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs:1366-1421` and a new private method after `CreateReplayMultiTickerBarFeed` / before `BackfillAsync` (line 1634)

**Interfaces:**
- Consumes: `TickerGroup.GetGroupKey(string ticker)` (existing, `CRV.Core/Strategy/TickerGroup.cs:826`), `SignalTimeframes.IsWholeMultipleOf`, `SessionBucket.For` (Task 3), `SessionBarAggregator` (Task 4), `HtfBarStore.UpsertAsync` (Task 5), `StrategyConfig.TfMinutesFor(string ticker, int? fallbackMinutes = null)` (existing, `StrategyConfig.cs:627`).
- Produces: `sealed class HtfTopUp` (`CRV.Core.Data`) with `IReadOnlyList<(string Root, HtfBar Bar)> OnExecutionBar(string ticker, int executionMinutes, Bar bar)`.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Data/HtfTopUpTests.cs`:

```csharp
using CRV.Core.Data;
using CRV.Core.Indicators;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Data;

public class HtfTopUpTests
{
    private static Bar At(int day, int hour, int minute, bool confirmed = true)
        => new(new DateTime(2026, 9, day, hour, minute, 0, DateTimeKind.Utc), 1, 2, 0, 1, 1, confirmed);

    [Fact]
    public void OnExecutionBar_ClosesEveryTimeframeMadeOfWholeExecutionBars()
    {
        var topUp = new HtfTopUp();

        // 18:00 ET open, 30-minute bars to 22:00 ET.
        var closed = Enumerable.Range(0, 8)
            .SelectMany(i => topUp.OnExecutionBar("/NQZ26", 30, new Bar(new DateTime(2026, 9, 29, 22, 0, 0, DateTimeKind.Utc).AddMinutes(30 * i), 1, 2, 0, 1, 1)))
            .ToList();

        Assert.All(closed, c => Assert.Equal("NQ", c.Root));
        Assert.Equal(8, closed.Count(c => c.Bar.Timeframe == SignalTimeframe.M30));
        Assert.Equal(4, closed.Count(c => c.Bar.Timeframe == SignalTimeframe.H1));
        Assert.Single(closed, c => c.Bar.Timeframe == SignalTimeframe.H4);
        Assert.DoesNotContain(closed, c => c.Bar.Timeframe is SignalTimeframe.M5 or SignalTimeframe.M15);
    }

    [Fact]
    public void OnExecutionBar_StartedMidBucket_DropsThatPartialBucket()
    {
        var topUp = new HtfTopUp();

        // Starts 19:00 ET: H4 18:00 was under way, H1 19:00 starts clean.
        var closed = Enumerable.Range(0, 6)
            .SelectMany(i => topUp.OnExecutionBar("/NQZ26", 30, new Bar(new DateTime(2026, 9, 29, 23, 0, 0, DateTimeKind.Utc).AddMinutes(30 * i), 1, 2, 0, 1, 1)))
            .ToList();

        Assert.DoesNotContain(closed, c => c.Bar.Timeframe == SignalTimeframe.H4);
        Assert.Equal(3, closed.Count(c => c.Bar.Timeframe == SignalTimeframe.H1));
    }

    [Fact]
    public void OnExecutionBar_SecondTickerOnTheSameRoot_IsIgnored()
    {
        var topUp = new HtfTopUp();
        topUp.OnExecutionBar("/NQZ26", 30, At(29, 22, 0));

        Assert.Empty(topUp.OnExecutionBar("/MNQZ26", 30, At(29, 22, 30)));
        Assert.NotEmpty(topUp.OnExecutionBar("/NQZ26", 30, At(29, 22, 30)));
    }

    [Fact]
    public void OnExecutionBar_UnconfirmedBar_IsIgnored()
    {
        var topUp = new HtfTopUp();

        Assert.Empty(topUp.OnExecutionBar("/NQZ26", 30, At(29, 22, 0, confirmed: false)));

        // Had the unconfirmed bar been taken, its M30 bucket would already be closed.
        Assert.Contains(topUp.OnExecutionBar("/NQZ26", 30, At(29, 22, 0)), c => c.Bar.Timeframe == SignalTimeframe.M30);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HtfTopUpTests"`
Expected: build FAILS with `The type or namespace name 'HtfTopUp' could not be found`.

- [ ] **Step 3: Write `HtfTopUp`**

`CRV.Core/Data/HtfTopUp.cs`:

```csharp
using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Strategy;

namespace CRV.Core.Data;

/// <summary>
/// Turns closed live execution bars into closed higher-timeframe bars for the stored history.
/// One ticker feeds each root (the first one seen, so NQ and MNQ on one feed don't write the
/// same root twice). Each timeframe made of whole execution bars gets an aggregator; the
/// bucket that was already under way when the feed started is partial and is not returned.
/// </summary>
public sealed class HtfTopUp
{
    private sealed class Track(SessionBarAggregator aggregator, bool skipFirst)
    {
        public SessionBarAggregator Aggregator { get; } = aggregator;
        public bool SkipFirst { get; set; } = skipFirst;
    }

    private readonly Dictionary<string, string> _tickerOfRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Root, SignalTimeframe Tf), Track> _tracks = new();

    public IReadOnlyList<(string Root, HtfBar Bar)> OnExecutionBar(string ticker, int executionMinutes, Bar bar)
    {
        if (!bar.IsConfirmed) return [];
        var root = TickerGroup.GetGroupKey(ticker);
        if (!_tickerOfRoot.TryAdd(root, ticker) &&
            !string.Equals(_tickerOfRoot[root], ticker, StringComparison.OrdinalIgnoreCase))
            return [];

        var closed = new List<(string, HtfBar)>();
        foreach (var tf in Enum.GetValues<SignalTimeframe>())
        {
            if (!tf.IsWholeMultipleOf(executionMinutes)) continue;
            if (!_tracks.TryGetValue((root, tf), out var track))
            {
                track = new Track(new SessionBarAggregator(tf, executionMinutes),
                    skipFirst: SessionBucket.For(tf, bar.Time).OpenUtc != bar.Time);
                _tracks[(root, tf)] = track;
            }
            if (track.Aggregator.OnExecutionBar(bar) is not { } done) continue;
            if (track.SkipFirst) { track.SkipFirst = false; continue; }
            closed.Add((root, done));
        }
        return closed;
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HtfTopUpTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Wire it into the live bar loop**

`CRV.Web/Services/LiveEngineOrchestrator.cs` — before the bar loop (line 1366):

```csharp
            bool streamWarmupDone = false;
            await foreach (var (bar, barTicker) in mux.StreamAsync(ct))
```

becomes

```csharp
            bool streamWarmupDone = false;
            // Closed bars from a real market feed extend the stored higher-timeframe history.
            // Replay sessions re-run old data and must not overwrite it.
            var htfTopUp = new CRV.Core.Data.HtfTopUp();
            var recordHistory = cfg.Broker != "TradovateReplay";
            await foreach (var (bar, barTicker) in mux.StreamAsync(ct))
```

and at the end of the loop body (line 1420):

```csharp
                finally { engineLock.Release(); }
            }
```

becomes

```csharp
                finally { engineLock.Release(); }

                if (recordHistory && bar.IsConfirmed)
                    await TopUpHistoryAsync(htfTopUp, cfg, bar, barTicker, ct);
            }
```

Add this method directly above the `/// <summary>` of `BackfillAsync` (line 1634, "Fetches today's closed bars…"):

```csharp
    /// <summary>
    /// Writes the higher-timeframe bars a closed execution bar completes into HtfBars.
    /// A failed write is logged and dropped: history must never stop the bar loop.
    /// </summary>
    private async Task TopUpHistoryAsync(CRV.Core.Data.HtfTopUp topUp, StrategyConfig cfg, Bar bar, string ticker, CancellationToken ct)
    {
        try
        {
            var closed = topUp.OnExecutionBar(ticker, cfg.TfMinutesFor(ticker), bar);
            if (closed.Count == 0) return;
            using var scope = _sp.CreateScope();
            var store = new CRV.Core.Data.HtfBarStore(scope.ServiceProvider.GetRequiredService<CRV.Core.Data.TradingDbContext>());
            foreach (var root in closed.GroupBy(c => c.Root))
                await store.UpsertAsync(root.Key, root.Select(c => c.Bar).ToList(), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[HISTORY] Top-up failed for {Ticker} bar {Time:u}", ticker, bar.Time);
        }
    }

```

- [ ] **Step 6: Build and run the suite**

Run: `dotnet build CRV.Trading.sln 2>&1 | tail -3`
Expected: `Build succeeded.` with `0 Error(s)`.

Run: `dotnet test CRV.Core.Tests 2>&1 | tail -3`
Expected: `Failed: 0, Passed: <N + 106>` (Tasks 1–10 add 8 + 5 + 33 + 14 + 10 + 15 + 9 + 4 + 4 + 4 = 106).

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Data/HtfTopUp.cs CRV.Core.Tests/Data/HtfTopUpTests.cs CRV.Web/Services/LiveEngineOrchestrator.cs
git commit -m "feat(history): closed live bars top up the stored HTF history"
```

---

### Task 11: `api/history` endpoints

**Files:**
- Create: `CRV.Web/Api/HistoryController.cs`

**Interfaces:**
- Consumes: `HtfBarStore` (Task 5), `HistoryRequirement.SourceLine` (Task 6), `HtfCsvImport.ImportAsync` (Task 7), `SchwabHistoryBackfill`, `SchwabPriceHistoryFetcher`, `BarLoadException` (Task 9), `TickerGroup.GetGroupKey` (existing), `SchwabAuthService.GetAccessTokenAsync()` / `.ApiBaseUrl` (existing, `CRV.Live/Brokers/Schwab/SchwabBroker.cs:28,84`).
- Produces: `GET api/history/{root}` (counts per timeframe, last fill, source line), `GET api/history/{root}/{timeframe}?count=` (latest bars, oldest first, for the daily-alignment check), `POST api/history/{root}/backfill`, `POST api/history/{root}/import/{timeframe}` (multipart field `file`).

There is no web test project for controllers; the logic behind each endpoint is covered by Tasks 5–9. This task is verified by the build.

- [ ] **Step 1: Write the controller**

`CRV.Web/Api/HistoryController.cs`:

```csharp
using System.Text.RegularExpressions;
using CRV.Backtest.DataLoaders;
using CRV.Core.Data;
using CRV.Core.Indicators;
using CRV.Core.Strategy;
using CRV.Live.Brokers.Schwab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CRV.Web.Api;

/// <summary>
/// The stored higher-timeframe history per root: what is stored, a Schwab backfill, and
/// TradingView CSV imports. Roots are normalised so "MNQ" and "/NQZ26" both mean NQ.
/// </summary>
[ApiController]
[Route("api/history")]
[EnableRateLimiting("engine-api")]
public partial class HistoryController : ControllerBase
{
    private readonly TradingDbContext   _db;
    private readonly SchwabAuthService  _schwab;
    private readonly IHttpClientFactory _http;
    private readonly ILoggerFactory     _logs;

    public HistoryController(TradingDbContext db, SchwabAuthService schwab, IHttpClientFactory http, ILoggerFactory logs)
    {
        _db     = db;
        _schwab = schwab;
        _http   = http;
        _logs   = logs;
    }

    [GeneratedRegex("^/?[A-Za-z0-9]{1,8}$")]
    private static partial Regex TickerShape();

    private static string? RootOf(string root)
        => TickerShape().IsMatch(root) ? TickerGroup.GetGroupKey(root.ToUpperInvariant()) : null;

    [HttpGet("{root}")]
    public async Task<IActionResult> Status(string root, CancellationToken ct)
    {
        if (RootOf(root) is not { } r) return BadRequest(new { status = "error", message = "Unknown root." });
        var store  = new HtfBarStore(_db);
        var counts = new Dictionary<string, int>();
        foreach (var tf in Enum.GetValues<SignalTimeframe>())
            counts[tf.ToString()] = await store.CountAsync(r, tf, ct);
        var last = await store.LastFilledUtcAsync(r, ct);
        return Ok(new { root = r, counts, lastFilledUtc = last, source = HistoryRequirement.SourceLine(r, last) });
    }

    [HttpGet("{root}/{timeframe}")]
    public async Task<IActionResult> Bars(string root, SignalTimeframe timeframe, [FromQuery] int count = 20, CancellationToken ct = default)
    {
        if (RootOf(root) is not { } r) return BadRequest(new { status = "error", message = "Unknown root." });
        return Ok(await new HtfBarStore(_db).LatestAsync(r, timeframe, Math.Clamp(count, 1, 1000), ct));
    }

    [HttpPost("{root}/backfill")]
    public async Task<IActionResult> Backfill(string root, CancellationToken ct)
    {
        if (RootOf(root) is not { } r) return BadRequest(new { status = "error", message = "Unknown root." });
        var fetcher  = new SchwabPriceHistoryFetcher(_http.CreateClient("Schwab"), await _schwab.GetAccessTokenAsync(), _schwab.ApiBaseUrl);
        var backfill = new SchwabHistoryBackfill(fetcher, new HtfBarStore(_db), _logs.CreateLogger<SchwabHistoryBackfill>());
        try
        {
            var inserted = await backfill.RunAsync(r, DateTime.UtcNow, ct);
            return Ok(new { root = r, inserted = inserted.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) });
        }
        catch (BarLoadException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { status = "error", message = ex.Message });
        }
    }

    [HttpPost("{root}/import/{timeframe}")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Import(string root, SignalTimeframe timeframe, IFormFile file, CancellationToken ct)
    {
        if (RootOf(root) is not { } r) return BadRequest(new { status = "error", message = "Unknown root." });
        using var reader = new StreamReader(file.OpenReadStream());
        try
        {
            var inserted = await HtfCsvImport.ImportAsync(reader, r, timeframe, new HtfBarStore(_db), ct);
            return Ok(new { root = r, timeframe = timeframe.ToString(), inserted });
        }
        catch (FormatException ex)
        {
            return BadRequest(new { status = "error", message = ex.Message });
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build CRV.Trading.sln 2>&1 | tail -3`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add CRV.Web/Api/HistoryController.cs
git commit -m "feat(history): api/history status, bars, Schwab backfill and CSV import"
```

---

### Task 12: README, full verification, hand-off checks

**Files:**
- Modify: `README.md` (new section after "Running Tests", before "## Accessibility", line 112)

- [ ] **Step 1: Document the history**

Insert before `## Accessibility`:

````markdown
## Price history (EMA strategies)

EMA strategies read their signals from higher-timeframe bars (M5 … MN1) built on the CME
session (18:00–17:00 ET). Each root (NQ, ES, …) has one stored history in the `HtfBars`
table: Schwab's continuous series, contracts joined, unadjusted.

```bash
# Fill from Schwab (Schwab must be authorised). D1: 20 years; W1/MN1: built from D1;
# M30/H1/H4/H8: ~8.5 months of 30-minute bars; M5/M15: 1-minute bars, paged.
curl -k -X POST https://localhost:5001/api/history/NQ/backfill

# Import a TradingView export (chart → Export data) for depth Schwab no longer serves.
curl -k -X POST -F file=@NQ1_240.csv https://localhost:5001/api/history/NQ/import/H4

# What is stored, and the latest bars of one timeframe.
curl -k https://localhost:5001/api/history/NQ
curl -k "https://localhost:5001/api/history/NQ/D1?count=260"
```

While the engine runs on a live feed, every closed bar tops the history up. A strategy can be
switched on once the stored bars for its timeframe reach its EMA period; its EMA matches
TradingView once three times the period is stored.

Before trusting the data: compare our D1 with TradingView NQ1! daily OHLC on ten days spread
across a year (Schwab stamps daily bars at midnight Central), and spot-check a 2010 daily close
against NQ1! unadjusted to learn whether Schwab's series is back-adjusted.
````

- [ ] **Step 2: Full verification**

Run: `dotnet build CRV.Trading.sln 2>&1 | tail -3`
Expected: `Build succeeded.` `0 Error(s)`.

Run: `dotnet test CRV.Core.Tests 2>&1 | tail -3`
Expected: `Failed: 0, Passed: <N + 106>`.

Run: `dotnet test CRV.Web.A11yTests 2>&1 | tail -3`
Expected: `Failed: 0` with the same count as on `master` (no screen changed; this confirms the host still boots with the new table).

Run: `grep -rnE "FindTz|GetTz\(|FindTimeZone\(|\bBarAggregator\b|\bBarResampler\b" --include='*.cs' CRV.* | grep -v '/bin/' | grep -v '/obj/' | grep -v SourceTreeTests`
Expected: no output.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: price history backfill, import and trust checks"
```

- [ ] **Step 4: Hand the data checks to Cirino**

Stop here and give Cirino this list (agents do not run it — it calls Schwab with his tokens):

1. Start CRV.Web, authorise Schwab, run `POST /api/history/NQ/backfill`; note the `inserted` counts (expect roughly D1 ≈ 5,090, W1 ≈ 1,040, MN1 ≈ 239, M30 ≈ 8,500).
2. **Daily alignment:** `GET /api/history/NQ/D1?count=260`; for ten dates spread across the year, compare O/H/L/C with TradingView CME_MINI:NQ1! daily. Mismatched opens point at a session cut other than 18:00 ET.
3. **Adjustment:** read a 2010 D1 close from the `HtfBars` table (DataGrip on a copy of the DB, or `GET /api/history/NQ/D1?count=5000` and pick a 2010 row) and compare it with TradingView NQ1! unadjusted on the same date.
4. **H8:** confirm on a TradingView NQ1! 8-hour chart that bars open 18:00, 02:00, 10:00 ET with a 7-hour last bar.

Record the answers in the PR description; `ema-parity-and-validation` depends on 2 and 3.

- [ ] **Step 5: Security review and PR**

Run the `security-review` skill on the branch; fix findings or list them with the reason in the PR. Then:

```bash
git push -u origin feat/htf-bars-and-history
gh pr create --title "feat(history): higher-timeframe bars and stored history" --body "$(cat <<'EOF'
Session-anchored HTF bars (SessionBarAggregator), generic EmaIndicator, HtfBars table with Schwab backfill, TradingView CSV import and live top-ups, and the enable/parity rules (HistoryRequirement).

Spec: docs/superpowers/specs/2026-10-02-htf-bars-and-history-design.md
Plan: docs/superpowers/plans/2026-10-02-htf-bars-and-history.md

Data checks (Cirino):
- Daily alignment (10 days vs NQ1!): <result>
- Adjustment (2010 close): <result>
- H8 boundaries on TradingView: <result>

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-Review

**Spec coverage**

| Spec requirement | Task |
|---|---|
| Execution TF vs signal TF; signal TF a whole multiple (validated on save) | 3 (`IsWholeMultipleOf`; plan 5 wires the save check) |
| `SessionBarAggregator`: H4/H8/D1/W1/MN1/minute buckets | 3 (buckets), 4 (aggregation) |
| Closes on the boundary bar; holiday/early close closes late | 4 |
| DST weekends | 3, 4 |
| Delete `BarAggregator`, `BarResampler` | 4 |
| `EmaIndicator` SMA-seeded, not ready before period; `Ema21Indicator` untouched | 2 |
| `EasternTime`; three copies go | 1 |
| `HtfBars` table, unique key, REAL decimals | 5 |
| Sources: D1 Schwab daily; W1/MN1 from our D1; M30–H8 from 30-min; M5/M15 from 1-min paged | 9 (8 for paging) |
| Top-ups from live closed bars | 10 |
| CSV import | 7 |
| Two checks before trusting the data | 12 (hand-off), README |
| Enabling rules from stored counts, parity warning, probe examples | 6 |
| History panel copy and source line | 6 (strings); markup in plan 5 (Decision 7) |
| Tests 1–8 of the spec | 3/4 (1–3), 2 (4), 4 (5), 8/9 (6), 6 (7), 1/4 (8) |

**Placeholder scan:** no TBD/TODO; every code step has full code. The PR body's `<result>` fields are for Cirino's answers, not code.

**Type consistency:** `SessionBarAggregator(SignalTimeframe, int)` is used the same in Tasks 4, 9, 10; `HtfBarStore` methods match between Tasks 5, 7, 9, 10, 11; `RunAsync` returns `IReadOnlyDictionary<SignalTimeframe, int>` in Tasks 9 and 11; `HtfTopUp.OnExecutionBar(string, int, Bar)` matches Task 10's wiring.

**Review Focus:** each of the five lines has its test in the owning task (9, 10/4, 4, 7, 10).
