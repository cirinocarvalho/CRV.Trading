# Hold and Close Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `CloseAtRthClose` work: a setup either closes its position at the cutoff and at session end (the default, as today) or holds it into the next session, and a held position's open loss counts against the daily loss limit.

**Architecture:** `ISetupStrategy` gains `CloseAtRthClose`, read from config. `TickerGroup`'s cutoff and session-off exits skip a holding strategy's open position. `BrokerEventHandler.ExitAllAsync` takes a keep filter, and `ComposableEngine.ForceExitAllAsync` passes "filled position of a holding strategy" as kept, so live and backtest both inherit it. Strategy resets never clear `InTrade`. `RiskManager.DdBreached(heldUnrealized)` adds the open loss of positions opened on an earlier trading day, which `ComposableEngine` reads from `BrokerEventHandler.GetUnrealizedPnl`. A data migration sets every stored value to `true` first, so nothing starts holding until someone switches it off.

**Tech Stack:** .NET 10, xUnit 2.9.3, EF Core (SQLite, JSON1 functions in the migration), Razor Pages + vanilla JS, Playwright/axe (`CRV.Web.A11yTests`).

**Spec:** `docs/superpowers/specs/2026-10-02-hold-and-close-design.md`

**Requires:** nothing. Plan 2 of 6 in the EMA set; it works on today's master. Where plan 1 (`ema21-removal`) changes a name this plan touches, the step says what to do if plan 1 has already merged.

## Global Constraints

- Branch `feat/hold-and-close` from `master`. Conventional Commit subjects (`feat(strategy):`, `feat(risk):`, `fix(backtest):`, `feat(ui):`, `chore(db):`, `docs:`). End every commit message with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- `CloseAtRthClose = true` is the default everywhere: `StrategySetupConfig`, `SetupConfigBase`, `StrategyConfig.CloseAtRthCloseA–D`, `SetupSnapshot`, new basket entries, `ManualStrategy`. Holding is an explicit choice.
- **A reset never clears `InTrade`**; only the broker event handler's completion does (`SetInTrade(false)` in `CompleteGroup` / cancel paths).
- Holding keeps only a **filled** position (`GroupOrderStatus.Active` or `PartialFilled`). A pending entry is always cancelled at the cutoff and at session end.
- Daily-loss check = realized P&L today + **open loss** (`Math.Min(0, unrealized)` per position) of positions opened on an **earlier trading day**, marked at the last price. Trading day = `StrategyConfig.TradingDate` rule (local hour ≥ `SessionStartHour` → next day) in `Timezone`.
- A tripped daily-loss check stops new entries only. It never closes a held position.
- Copy, verbatim:
  - Setup page help: `Off: the cutoff only stops new entries, and an open trade is held into the next session.`
  - Cockpit card footer: `closes at session end` / `holds past cutoff`
  - `StrategyText`: `Closes at the end of the session.` / `Holds an open trade past the cutoff.`
- Tests never place live orders or open broker connections: fake `IGroupOrderExecutor`s, `BacktestEngine`, in-memory SQLite only.
- Basket JSON is read/written in C# only through `BasketCodec`. The migration edits it with SQLite `json_set` (a migration cannot run C# per row); its test proves the result through `BasketCodec`.
- Comments say what/why, never history ("used to", "new", "now").
- Baselines (measured 2026-10-02 on master): `dotnet test CRV.Core.Tests` → **959** pass (the brief said 956; three tests landed since). `dotnet test CRV.Web.A11yTests` → 93 pass (needs Chromium).

## Decisions

Facts the spec does not settle or gets wrong, decided here. Cirino can overturn any of them at review.

1. **Spec inaccuracy — today's open P&L is not in the limit.** The spec says a position opened today "already counts through its normal open P&L". It does not: `RiskManager` is realized-only and `GetUnrealizedPnl` feeds only the snapshot. Following the shared contract, this plan counts only positions opened on an **earlier** trading day. Intraday open P&L stays out of the limit, as today.
2. **A held winner counts as zero**, per position (`Math.Min(0, u)`), so it can never hide a realized loss (spec test 4). `RiskManager` clamps again defensively.
3. **No last price → the position counts as zero.** After a restart the price is 0 until the first tick; marking at 0 would book a fake full loss. The tick gate re-checks on every tick.
4. **`RiskManager.DdBreached` becomes a method** `DdBreached(decimal heldUnrealized = 0m)` (C# cannot have a property and method of one name). Every caller is updated (`ComposableEngine`, `SnapshotAggregator`, tests). `CanTrade` gains the same optional parameter.
5. **Backtest evaluates fills between sessions.** Brokers keep a held position's stop and target working overnight; the backtest only evaluated fills while a session ran. For non-holding configs nothing is working between sessions (session end cancels it all), so their results do not change.
6. **Not fixed: gap-through-stop fill price.** The backtest fills a stop at its level plus slippage even when price gaps through it. With bars between sessions now evaluated, the gap only matters across the daily 17:00–18:00 ET halt and weekends. Holding backtests are optimistic there. Worth a bead; out of scope.
7. **Restart recovery covers held positions.** `LiveBrokerPersistence.RecoverAsync` only recovers `StrategyLog` rows created since UTC midnight, so a restart during an overnight hold would orphan the position: no stop-exit record, no daily-loss count, and the engine could enter the same ticker again. The window becomes 7 days (covers a holiday weekend). Rows the broker reports completed are already marked completed by recovery, so the wider window self-cleans.
8. **`DailyStatsService` had no producer.** `OnTradeClosed` has no caller (verified by grep), so the "daily loss breached" email never fires. This plan rolls it on the trading date as the spec asks, feeds it from `EmailNotificationService.OnExitAsync` (already on every live exit), and makes the breach email also fire on `snap.TradingHalted`, which includes held open loss. **Flag for Cirino:** breach emails will start arriving when the limit trips.
9. **`sessions.json` is not migrated.** Legacy A–D per-session values live in a file in `DATA_DIR`, not the DB. They are used only when the basket is empty or malformed (`ToSetupConfigs` fallback). The migration covers the DB columns `CloseAtRthCloseA–D` and both baskets. The `live_settings.json` backup is likewise untouched.
10. **Web-only tests go in `CRV.Web.A11yTests`** as plain `[Fact]`s with no collection fixture (no browser). It is the only test project that references `CRV.Web`. Affects `StrategyText` and `DailyStatsService`.
11. **No default implementation on the interface member.** Every strategy decides explicitly. The nine test fakes get `public bool CloseAtRthClose { get; set; } = true;`.
12. **`LiveEngineOrchestrator` needs no change.** Its session-end exit is `ComposableEngine.ForceExitAllAsync` (`LiveEngineOrchestrator.cs:1090`), which gains the keep filter. Mock fills run on every tick regardless of the engine being idle (`:1128-1145`), so held mock positions still exit.
13. **Snapshot gauge stays realized.** `DailyLossUsed` (the dashboard gauge) is unchanged. `TradingHalted` includes held open loss, so the HALTED state agrees with the gate.
14. **The migration uses `json_set` on `$.Config.CloseAtRthClose`.** Every basket writer (`BasketCodec`, `MapBasketEntries`, the old editor) serializes PascalCase with default `System.Text.Json`, so that path matches. Verified against sqlite 3.54: numbers keep their text, an entry with no `Config` gets one holding only the flag (it deserializes identically), `''` / `NULL` / invalid JSON rows are skipped.

## Review Focus

1. **Restart during an overnight hold.** The engine restarts the morning after a held entry. The position must be recovered and tracked, or it is orphaned and could be doubled. Task 10 pins the recovery window, including a Friday entry recovered on Monday.
2. **A held stop hit while no session is running.** Price runs through a held long's stop at 19:00 ET with only NY enabled. The backtest must fill it then, not at the next session's open or never. Task 6, `HeldStop_FillsOvernight_WhenNoSessionIsRunning`.
3. **A position opened in the evening session.** Opened after 18:00 ET, it belongs to the next trading day. It must not count as "held from an earlier day" against the limit. Task 9, `PositionOpenedInTheEveningSession_IsTodays`.
4. **A held position with no price yet.** It must not book a phantom full-notional loss that halts trading. Task 9, `HeldPositionWithNoPriceYet_CountsAsZero`.
5. **A holding strategy's unfilled entry at the cutoff or session end.** It must be cancelled, not carried overnight as a resting order. Task 4, `PastCutoff_HoldingSetup_StillCancelsAnUnfilledEntry`. Task 5, `SessionEnd_CancelsAHoldingSetupsUnfilledEntry`.

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Create | `CRV.Core/Migrations/<ts>_DefaultCloseAtRthClose.cs` (+ `.Designer.cs`, generated) | Sets `CloseAtRthClose = true` on every basket entry (both baskets) and legacy setup A–D |
| Create | `CRV.Core/Models/TradingDay.cs` | The trading-day rule (local hour ≥ session start → next day), from local or UTC time |
| Create | `CRV.Core/Data/StrategyLogRecovery.cs` | Which `StrategyLog` rows restart recovery looks at |
| Modify | `CRV.Core/Strategy/ISetupStrategy.cs` | `CloseAtRthClose` member; reset docs state the `InTrade` rule |
| Modify | `CRV.Core/Strategy/{Pullback,Retest,OrbFakeout,SessionFakeout,Ema21,Manual}Strategy.cs` | Implement it; `Ema21Strategy` resets keep `InTrade` |
| Modify | `CRV.Core/Strategy/TickerGroup.cs:275-309, 392-413` | Cutoff and session-off exits skip a holding strategy's position |
| Modify | `CRV.Core/Strategy/BrokerEventHandler.cs:577-591` | `ExitAllAsync` keep filter |
| Modify | `CRV.Core/Strategy/ComposableEngine.cs` | `ForceExitAllAsync` keeps held positions; `HeldOpenLoss`; both daily-loss gates and the snapshot use it; warmup comment |
| Modify | `CRV.Core/Strategy/RiskManager.cs` | `DdBreached(decimal heldUnrealized = 0m)`, `CanTrade(..., heldUnrealized)` |
| Modify | `CRV.Core/Strategy/SnapshotAggregator.cs` | `Inputs.HeldOpenLoss`; `SetupSnapshot.CloseAtRthClose` |
| Modify | `CRV.Core/Models/Signals.cs:257` | `SetupSnapshot.CloseAtRthClose` |
| Modify | `CRV.Core/Models/StrategyConfig.cs:446-451` | `TradingDate` delegates to `TradingDay`; `TradingDateOfUtc` |
| Modify | `CRV.Backtest/Engine/BacktestEngine.cs:267-321` | Fills evaluated between sessions; one tick-path helper |
| Modify | `CRV.Web/Services/LiveBrokerPersistence.cs:91-95` | Uses `StrategyLogRecovery` |
| Modify | `CRV.Web/Services/DailyStatsService.cs:82-123` | Trading-date rollover |
| Modify | `CRV.Web/Services/EmailNotificationService.cs:75-92, 172-192` | Feeds `DailyStatsService`; breach email includes held loss |
| Modify | `CRV.Web/Pages/Setup/Strategy.cshtml:188`, `CRV.Web/Pages/Setup/StrategyText.cs:59-60` | Help text, plain words |
| Modify | `CRV.Web/Pages/Dashboard/Index.cshtml:1033-1072, 1442-1466`, `CRV.Web/wwwroot/css/components.css:171` | Card footer |
| Modify | `docs/risk.md` | Daily loss counts held open loss |
| Modify (tests) | 9 test fakes, `RiskManagerTests`, `ComposableEngineTests`, `TickerGroupTests`, `BrokerEventHandlerTests`, `SnapshotAggregatorTests`, `CockpitSnapshot`, `CockpitCardTests` | See tasks |
| Create (tests) | `CRV.Core.Tests/Data/DefaultCloseAtRthCloseTests.cs`, `.../Data/StrategyLogRecoveryTests.cs`, `.../Models/TradingDayTests.cs`, `.../Strategy/StrategyCloseAtRthCloseTests.cs`, `.../Strategy/StrategyResetKeepsTradeTests.cs`, `.../Strategy/HoldPastSessionEndTests.cs`, `.../Backtest/HoldPastSessionEndBacktestTests.cs`, `.../Risk/HeldLossDailyLimitTests.cs`, `CRV.Web.A11yTests/DailyStatsServiceTests.cs`, `CRV.Web.A11yTests/StrategyTextTests.cs` | See tasks |

---

### Task 1: Migration — every stored setup closes at session end

Lands first, so no commit on this branch has holding switched on by a stale `false` value.

**Files:**
- Create: `CRV.Core/Migrations/<ts>_DefaultCloseAtRthClose.cs` (generated, then filled in)
- Test: `CRV.Core.Tests/Data/DefaultCloseAtRthCloseTests.cs`

**Interfaces:**
- Produces: `CRV.Core.Migrations.DefaultCloseAtRthClose` with `static string BasketSql(string column)`, `const string LegacySql`, `const string EmaBasketColumn`.

- [ ] **Step 0: Branch**

```bash
git switch master && git pull --ff-only && git switch -c feat/hold-and-close
```

- [ ] **Step 1: Check which EMA basket column is live**

Run: `grep -c '"EmaBasketJson"' CRV.Core/Migrations/TradingDbContextModelSnapshot.cs`
Expected on today's master: `0`, so the column is `Ema21BasketJson` and the C# property is `StrategyConfig.Ema21BasketJson`. If it prints `1` (plan 1 merged), use `EmaBasketJson` / `StrategyConfig.EmaBasketJson` in Steps 2 and 4.

- [ ] **Step 2: Write the failing test**

`CRV.Core.Tests/Data/DefaultCloseAtRthCloseTests.cs`:

```csharp
using CRV.Core.Data;
using CRV.Core.Migrations;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// Holding is opt-in. The data migration sets CloseAtRthClose to true on every stored basket
/// entry and legacy setup, so no setup starts holding positions until someone switches it off.
/// </summary>
public class DefaultCloseAtRthCloseTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public DefaultCloseAtRthCloseTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static BasketEntry Entry(string id, bool close, decimal stopPct, int maxTrades) => new()
    {
        Id = id, Enabled = true, Label = id, StrategyType = StrategyType.Retest, Ticker = "MNQZ26",
        PointValue = 2m, TickSize = 0.25m,
        Config = new StrategySetupConfig
        {
            CloseAtRthClose = close, StopPct = stopPct, MaxTrades = maxTrades, CutoffHour = 15, CutoffMinute = 45,
        },
    };

    private void Migrate()
    {
        _db.Database.ExecuteSqlRaw(DefaultCloseAtRthClose.BasketSql("BasketJson"));
        _db.Database.ExecuteSqlRaw(DefaultCloseAtRthClose.BasketSql(DefaultCloseAtRthClose.EmaBasketColumn));
        _db.Database.ExecuteSqlRaw(DefaultCloseAtRthClose.LegacySql);
    }

    private StrategyConfig Reload()
    {
        _db.ChangeTracker.Clear();
        return _db.Configs.Single();
    }

    [Fact]
    public void Migrate_EveryBasketEntryAndLegacySetup_ClosesAtSessionEnd()
    {
        _db.Configs.Add(new StrategyConfig
        {
            Name = "live",
            BasketJson = BasketCodec.Serialize(new[] { Entry("retest-mnq", false, 0.35m, 3), Entry("pullback-mes", true, 0.10m, 5) }),
            Ema21BasketJson = BasketCodec.Serialize(new[] { Entry("ema21-mnq", false, 0.20m, 2) }),
            CloseAtRthCloseA = false, CloseAtRthCloseB = false, CloseAtRthCloseC = true, CloseAtRthCloseD = false,
        });
        _db.SaveChanges();

        Migrate();

        var cfg = Reload();
        var entries = BasketCodec.Parse(cfg.BasketJson).Concat(BasketCodec.Parse(cfg.Ema21BasketJson)).ToList();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.True(e.Config.CloseAtRthClose));
        Assert.True(cfg.CloseAtRthCloseA && cfg.CloseAtRthCloseB && cfg.CloseAtRthCloseC && cfg.CloseAtRthCloseD);
    }

    [Fact]
    public void Migrate_ChangesNothingButCloseAtRthClose()
    {
        var before = new[] { Entry("retest-mnq", false, 0.35m, 3), Entry("pullback-mes", true, 0.10m, 5) };
        _db.Configs.Add(new StrategyConfig { Name = "live", MaxDailyLoss = 750m, BasketJson = BasketCodec.Serialize(before) });
        _db.SaveChanges();

        Migrate();

        var expected = BasketCodec.Parse(BasketCodec.Serialize(before));
        foreach (var e in expected) e.Config.CloseAtRthClose = true;
        var cfg = Reload();
        Assert.Equal(BasketCodec.Serialize(expected), BasketCodec.Serialize(BasketCodec.Parse(cfg.BasketJson)));
        Assert.Equal(new[] { "retest-mnq", "pullback-mes" }, BasketCodec.Parse(cfg.BasketJson).Select(e => e.Id));
        Assert.Equal(("live", 750m), (cfg.Name, cfg.MaxDailyLoss));
    }

    [Fact]
    public void Migrate_EmptyOrMissingBaskets_AreLeftAlone()
    {
        _db.Configs.Add(new StrategyConfig { Name = "legacy", BasketJson = "", Ema21BasketJson = null });
        _db.SaveChanges();

        Migrate();

        var cfg = Reload();
        Assert.Equal("", cfg.BasketJson);
        Assert.Null(cfg.Ema21BasketJson);
    }

    [Fact]
    public void ANewEntry_ClosesAtSessionEnd()
    {
        Assert.True(new StrategySetupConfig().CloseAtRthClose);
        Assert.True(new BasketEntry().Config.CloseAtRthClose);
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~DefaultCloseAtRthCloseTests`
Expected: build FAILS with `The type or namespace name 'DefaultCloseAtRthClose' could not be found`.

- [ ] **Step 4: Generate the migration and fill it in**

Run: `dotnet ef migrations add DefaultCloseAtRthClose --project CRV.Core --startup-project CRV.Web`
Expected: `Done.` and new `CRV.Core/Migrations/<ts>_DefaultCloseAtRthClose.cs` + `.Designer.cs`. `git diff --stat CRV.Core/Migrations/TradingDbContextModelSnapshot.cs` shows no change, or only the `ProductVersion` line.

Replace the whole of `CRV.Core/Migrations/<ts>_DefaultCloseAtRthClose.cs` with:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRV.Core.Migrations
{
    /// <summary>
    /// Sets CloseAtRthClose to true on every basket entry (ORB and EMA baskets) and on legacy
    /// setups A–D, so no stored setup starts holding positions past the session until someone
    /// switches it off. Basket JSON is edited in SQL with json_set because a migration cannot
    /// run C# per row; every basket writer serializes PascalCase, so "$.Config.CloseAtRthClose"
    /// is the key BasketCodec reads.
    /// </summary>
    public partial class DefaultCloseAtRthClose : Migration
    {
        /// <summary>The EMA basket column when this migration runs.</summary>
        public const string EmaBasketColumn = "Ema21BasketJson";

        /// <summary>Sets Config.CloseAtRthClose on every entry of the basket JSON in <paramref name="column"/>.
        /// Empty, NULL and non-array values are left alone.</summary>
        public static string BasketSql(string column) => $"""
            UPDATE "Configs"
               SET "{column}" = (
                   SELECT json_group_array(json_set(e.value, '$.Config.CloseAtRthClose', json('true')))
                     FROM json_each("Configs"."{column}") AS e)
             WHERE json_valid("{column}") AND json_type("{column}") = 'array';
            """;

        public const string LegacySql = """
            UPDATE "Configs"
               SET "CloseAtRthCloseA" = 1, "CloseAtRthCloseB" = 1, "CloseAtRthCloseC" = 1, "CloseAtRthCloseD" = 1;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BasketSql("BasketJson"));
            migrationBuilder.Sql(BasketSql(EmaBasketColumn));
            migrationBuilder.Sql(LegacySql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way: nothing records which rows held false before.
        }
    }
}
```

If Step 1 printed `1`, set `EmaBasketColumn = "EmaBasketJson"` and use `EmaBasketJson` for the property in the test.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~DefaultCloseAtRthCloseTests`
Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Migrations CRV.Core.Tests/Data/DefaultCloseAtRthCloseTests.cs
git commit -m "chore(db): every stored setup closes at session end before holding is honoured

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `CloseAtRthClose` on every strategy

**Files:**
- Modify: `CRV.Core/Strategy/ISetupStrategy.cs:113`
- Modify: `CRV.Core/Strategy/PullbackStrategy.cs:63`, `RetestStrategy.cs:74`, `OrbFakeoutStrategy.cs:63`, `SessionFakeoutStrategy.cs:66`, `Ema21Strategy.cs:85`, `ManualStrategy.cs:41`
- Modify (fakes): `CRV.Core.Tests/Strategy/ChopFilterIntegrationTests.cs:80`, `BacktestPartialFillTests.cs:35`, `BrokerEventHandlerTests.cs:63`, `SnapshotAggregatorTests.cs:23`, `TickerGroupTests.cs:41`, `SimulatedFillPriceTests.cs:48`, `NBracketTests.cs:48`, `ComposableEngineTests.cs:59`, `CRV.Core.Tests/Risk/PortfolioGateTests.cs:55`
- Test: `CRV.Core.Tests/Strategy/StrategyCloseAtRthCloseTests.cs`

**Interfaces:**
- Produces: `bool ISetupStrategy.CloseAtRthClose { get; }`. Each strategy returns `_cfg.CloseAtRthClose`; `ManualStrategy` returns `true`. Every test fake has `public bool CloseAtRthClose { get; set; } = true;`.

- [ ] **Step 1: Write the failing test**

`CRV.Core.Tests/Strategy/StrategyCloseAtRthCloseTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>Every strategy reports whether it closes at the session or holds, straight from its config.</summary>
public class StrategyCloseAtRthCloseTests
{
    public static TheoryData<StrategyType> OrbTypes => new()
    {
        StrategyType.Pullback, StrategyType.Retest, StrategyType.OrbFakeout, StrategyType.SessionFakeout,
    };

    private static StrategySetupConfig Config(StrategyType type, bool close) => new()
    {
        Id = "s", Name = "s", StrategyType = type, Enabled = true,
        Ticker = "MNQZ26", PointValue = 2m, TickSize = 0.25m, CloseAtRthClose = close,
    };

    [Theory, MemberData(nameof(OrbTypes))]
    public void CloseAtRthClose_ComesFromTheConfig(StrategyType type)
    {
        Assert.True(StrategyFactory.Create(Config(type, true)).CloseAtRthClose);
        Assert.False(StrategyFactory.Create(Config(type, false)).CloseAtRthClose);
    }

    [Theory, MemberData(nameof(OrbTypes))]
    public void CloseAtRthClose_FollowsAReconfigure(StrategyType type)
    {
        var s = StrategyFactory.Create(Config(type, true));
        s.Reconfigure(Config(type, false));
        Assert.False(s.CloseAtRthClose);
    }

    // Delete this test if CRV.Core/Strategy/Ema21Strategy.cs no longer exists (plan 1 merged).
    [Fact]
    public void Ema21_CloseAtRthClose_ComesFromTheConfig()
    {
        Assert.True(StrategyFactory.Create(Config(StrategyType.Ema21, true)).CloseAtRthClose);
        Assert.False(StrategyFactory.Create(Config(StrategyType.Ema21, false)).CloseAtRthClose);
    }

    [Fact]
    public void ManualTrades_CloseAtSessionEnd()
    {
        var signal = new EntrySignal(SetupId.F, Direction.Long, 100m, 95m, 110m, 0m, 1, DateTime.UtcNow,
            Ticker: "MNQZ26", SetupLabel: "manual-1");
        Assert.True(new ManualStrategy(signal).CloseAtRthClose);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~StrategyCloseAtRthCloseTests`
Expected: build FAILS with `'ISetupStrategy' does not contain a definition for 'CloseAtRthClose'`.

- [ ] **Step 3: Add the interface member**

In `CRV.Core/Strategy/ISetupStrategy.cs`, after line 113 (`bool BypassChopFilter { get; }`), insert:

```csharp

    /// <summary>
    /// True: the open position is closed at the setup's cutoff, when its session slot is off,
    /// and at session end. False: those only stop new entries; an open position is held into
    /// the next session and leaves through its own stop, targets, trail or a manual exit.
    /// </summary>
    bool CloseAtRthClose { get; }
```

- [ ] **Step 4: Implement it in every strategy**

After the `BypassChopFilter` line in each of `PullbackStrategy.cs:63`, `RetestStrategy.cs:74`, `OrbFakeoutStrategy.cs:63`, `SessionFakeoutStrategy.cs:66` and `Ema21Strategy.cs:85` (skip `Ema21Strategy.cs` if the file no longer exists), insert, matching the surrounding alignment:

```csharp
    public bool         CloseAtRthClose => _cfg.CloseAtRthClose;
```

In `CRV.Core/Strategy/ManualStrategy.cs`, after line 41 (`public bool    BypassChopFilter => true; ...`), insert:

```csharp
    public bool    CloseAtRthClose => true;    // Manual trades close at session end like any closing setup.
```

- [ ] **Step 5: Add the member to the nine test fakes**

After the `BypassChopFilter` line of each fake listed under **Files**, insert:

```csharp
        public bool CloseAtRthClose { get; set; } = true;
```

(`ChopFilterIntegrationTests.cs:80`, `BacktestPartialFillTests.cs:35`, `BrokerEventHandlerTests.cs:63`, `SnapshotAggregatorTests.cs:23`, `TickerGroupTests.cs:41`, `SimulatedFillPriceTests.cs:48`, `NBracketTests.cs:48`, `ComposableEngineTests.cs:59`, `Risk/PortfolioGateTests.cs:55`.)

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failures (959 + 4 from Task 1 + 10 here).

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy CRV.Core.Tests
git commit -m "feat(strategy): every strategy reports whether it closes at session end or holds

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: A reset never clears `InTrade`

**Files:**
- Modify: `CRV.Core/Strategy/Ema21Strategy.cs:146, 159` (remove `_inTrade = false;`)
- Modify: `CRV.Core/Strategy/ISetupStrategy.cs:124-139` (docs)
- Modify: `CRV.Core/Strategy/ComposableEngine.cs:265-273` (comment that becomes false)
- Test: `CRV.Core.Tests/Strategy/StrategyResetKeepsTradeTests.cs`

**Interfaces:**
- Consumes: `ISetupStrategy.CloseAtRthClose` (Task 2).
- Produces: the rule "`Reset()`, `ResetSession()`, `ResetTradeCounters()` leave `InTrade` unchanged" for every strategy.

- [ ] **Step 1: Write the failing tests**

`CRV.Core.Tests/Strategy/StrategyResetKeepsTradeTests.cs`:

```csharp
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// A held position outlives session and day resets. Only the broker event handler's
/// completion clears InTrade.
/// </summary>
public class StrategyResetKeepsTradeTests
{
    public static TheoryData<StrategyType> OrbTypes => new()
    {
        StrategyType.Pullback, StrategyType.Retest, StrategyType.OrbFakeout, StrategyType.SessionFakeout,
    };

    private static StrategySetupConfig Config(StrategyType type) => new()
    {
        Id = "s", Name = "s", StrategyType = type, Enabled = true,
        Ticker = "MNQZ26", PointValue = 2m, TickSize = 0.25m, CloseAtRthClose = false,
    };

    private static void AssertResetsKeepTheTrade(ISetupStrategy s)
    {
        s.SetInTrade(true);
        s.ResetSession();
        Assert.True(s.InTrade);
        s.Reset();
        Assert.True(s.InTrade);
        s.ResetTradeCounters();
        Assert.True(s.InTrade);
        Assert.True(s.IsActive);
    }

    [Theory, MemberData(nameof(OrbTypes))]
    public void Resets_KeepAnOpenTrade(StrategyType type)
        => AssertResetsKeepTheTrade(StrategyFactory.Create(Config(type)));

    // Delete this test if CRV.Core/Strategy/Ema21Strategy.cs no longer exists (plan 1 merged).
    [Fact]
    public void Ema21_Resets_KeepAnOpenTrade()
        => AssertResetsKeepTheTrade(StrategyFactory.Create(Config(StrategyType.Ema21)));

    [Fact]
    public void ResetWarmupCounters_KeepsARecoveredPosition()
    {
        var engine = new ComposableEngine(new NoopExec(), new NullSink(), new NoPrices(), new EngineConfig());
        engine.AddSetup(Config(StrategyType.Retest));
        var s = engine.GetStrategy("s")!;
        s.SetInTrade(true);   // what restart recovery does for a position still open at the broker

        engine.ResetWarmupCounters();

        Assert.True(s.InTrade);
    }

    [Fact]
    public async Task StopFill_ClearsInTrade()
    {
        var handler = new BrokerEventHandler(new SimulatedExec());
        var s = StrategyFactory.Create(Config(StrategyType.Pullback));
        var group = new GroupOrder
        {
            GroupOrderId = "g1", SetupId = "s", Ticker = "MNQZ26", Direction = Direction.Long,
            TotalContracts = 1, PointValue = 2m, EntryPrice = 18010m, InitialStopPrice = 18000m,
            Status = GroupOrderStatus.Active,
        };
        group.Legs.Add(new OrderLeg { GroupOrderId = "g1", OrderId = "g1-s", LegType = LegType.Stop, Price = 18000m, Quantity = 1 });
        handler.RegisterGroup(group, s);
        s.SetInTrade(true);
        s.ResetSession();

        await handler.HandleEventAsync(new OrderEvent("g1", "g1-s", LegType.Stop, OrderLegStatus.Filled,
            18000m, 1, null, null, DateTime.UtcNow));

        Assert.False(s.InTrade);
    }

    private sealed class SimulatedExec : IGroupOrderExecutor
    {
        public bool IsSimulated => true;
        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<GroupOrder?>(null);
        public Task ModifyOrderAsync(string id, decimal? p, int? q) => Task.CompletedTask;
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> PlaceMarketCloseAsync(string t, Direction d, int q) => Task.FromResult(0m);
    }

    private sealed class NoopExec : IOrderExecutor
    { public Task<decimal?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<decimal?>(null); }

    private sealed class NullSink : IStrategyEventSink
    {
        public Task OnEntryAsync(EntrySignal s) => Task.CompletedTask;
        public Task OnExitAsync(TradeRecord t) => Task.CompletedTask;
        public Task OnSnapshotAsync(EngineSnapshot s) => Task.CompletedTask;
    }

    private sealed class NoPrices : ILastPriceProvider
    {
        public decimal GetLastPrice(string t) => 0m;
        public void UpdatePrice(string t, decimal p) { }
    }
}
```

- [ ] **Step 2: Run them to verify the Ema21 test fails**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~StrategyResetKeepsTradeTests`
Expected: `Ema21_Resets_KeepAnOpenTrade` FAILS (`Assert.True() Failure` after `ResetSession`). The others PASS: they pin the rule for the strategies that already follow it. If plan 1 merged and the Ema21 test was deleted, every test passes here; go on to Step 3 for the docs.

- [ ] **Step 3: Make Ema21's resets keep the trade**

Skip if `Ema21Strategy.cs` no longer exists. In `CRV.Core/Strategy/Ema21Strategy.cs`, delete the line `        _inTrade = false;` in `Reset()` (line 146) and the same line in `ResetSession()` (line 159).

- [ ] **Step 4: State the rule in the interface docs**

In `CRV.Core/Strategy/ISetupStrategy.cs`, replace:

```csharp
    /// <summary>Reset all state for new trading day (clears trade counts, P&amp;L stats).</summary>
    void Reset();

    /// <summary>
    /// Reset for intra-day session transition.
    /// Clears trade state (arm, entry, stops) and per-session counters (trade count,
    /// direction-traded flags) but preserves daily P&amp;L stats (wins, losses, winPnl, lossPnl).
    /// </summary>
    void ResetSession();
```

with:

```csharp
    /// <summary>
    /// Reset all state for new trading day (clears trade counts, P&amp;L stats).
    /// Never clears <see cref="InTrade"/>: a position held into the new day stays tracked.
    /// </summary>
    void Reset();

    /// <summary>
    /// Reset for intra-day session transition.
    /// Clears trade state (arm, entry, stops) and per-session counters (trade count,
    /// direction-traded flags) but preserves daily P&amp;L stats (wins, losses, winPnl, lossPnl).
    /// Never clears <see cref="InTrade"/>; only the broker event handler's completion does.
    /// </summary>
    void ResetSession();
```

- [ ] **Step 5: Correct the warmup comment that is now false**

In `CRV.Core/Strategy/ComposableEngine.cs`, replace:

```csharp
            // Silently clear any active trades from warmup — those entries were
            // discarded (no broker order placed), so the strategy must not think
            // it has a real position. Disarm() resets _state to 0 (idle) without
            // producing exit signals or recording phantom trades.
            if (strategy.IsActive)
            {
                strategy.ClearPendingSignals();
                strategy.ResetSession();  // full reset: clears entry/stop/target/state
            }
```

with:

```csharp
            // Warmup entries are discarded before any order is placed, so a strategy
            // in a trade here holds a real position recovered from the broker.
            // ResetSession clears its arm state and counters and keeps InTrade.
            if (strategy.IsActive)
            {
                strategy.ClearPendingSignals();
                strategy.ResetSession();
            }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failures.

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy CRV.Core.Tests/Strategy/StrategyResetKeepsTradeTests.cs
git commit -m "fix(strategy): a reset never clears InTrade, so a held position stays tracked

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Cutoff and session-off exits keep a holding strategy's position

**Files:**
- Modify: `CRV.Core/Strategy/TickerGroup.cs:275-309` (bar path), `:392-413` (tick path)
- Test: `CRV.Core.Tests/Strategy/TickerGroupTests.cs` (append; add `using CRV.Core.Interfaces;`)

**Interfaces:**
- Consumes: `ISetupStrategy.CloseAtRthClose` (Task 2); `TickerGroupTests.FakeStrategy.CloseAtRthClose` settable (Task 2).
- Produces: past the cutoff or with its session slot off, a holding strategy keeps its filled position, is disarmed and not evaluated, and still has an unfilled entry cancelled.

- [ ] **Step 1: Write the failing tests**

Add `using CRV.Core.Interfaces;` to the top of `CRV.Core.Tests/Strategy/TickerGroupTests.cs`. Append inside the class, before its closing `}`:

```csharp
    // ── Hold vs close at the cutoff and when the session slot is off ──

    private sealed class CloseRecorder : IGroupOrderExecutor
    {
        public int MarketCloses { get; private set; }
        public bool IsSimulated => true;
        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<GroupOrder?>(null);
        public Task ModifyOrderAsync(string id, decimal? p, int? q) => Task.CompletedTask;
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> PlaceMarketCloseAsync(string t, Direction d, int q) { MarketCloses++; return Task.FromResult(0m); }
    }

    private static GroupOrder Group(string setupId, bool filled)
    {
        var id = "g-" + setupId;
        var g = new GroupOrder
        {
            GroupOrderId = id, SetupId = setupId, Ticker = "/NQH2026", Direction = Direction.Long,
            TotalContracts = 1, PointValue = 20m, InitialStopPrice = 19950m,
            EntryPrice = filled ? 20000m : null,
            Status = filled ? GroupOrderStatus.Active : GroupOrderStatus.Pending,
        };
        g.Legs.Add(new OrderLeg { GroupOrderId = id, OrderId = id + "-e", LegType = LegType.Entry, Price = 20000m, Quantity = 1,
            Status = filled ? OrderLegStatus.Filled : OrderLegStatus.Working });
        g.Legs.Add(new OrderLeg { GroupOrderId = id, OrderId = id + "-s", LegType = LegType.Stop, Price = 19950m, Quantity = 1 });
        return g;
    }

    private static (TickerGroup Group, BrokerEventHandler Handler, CloseRecorder Exec) WithGroup(FakeStrategy s, bool filled = true)
    {
        var exec = new CloseRecorder();
        var handler = new BrokerEventHandler(exec);
        var group = new TickerGroup("NQ", DefaultConfig(), handler);
        group.AddStrategy(s);
        handler.RegisterGroup(Group(s.Id, filled), s);
        s.IsActive = filled;
        s.InTrade = filled;
        return (group, handler, exec);
    }

    // 2026-03-20 is in EDT: 18:45 UTC is 14:45 ET, past a 14:30 cutoff; 14:00 UTC is 10:00 ET.
    private static readonly DateTime PastCutoffUtc = new(2026, 3, 20, 18, 45, 0, DateTimeKind.Utc);
    private static readonly DateTime MorningUtc    = new(2026, 3, 20, 14, 0, 0, DateTimeKind.Utc);

    private static FakeStrategy CutoffAt1430(bool close) =>
        new() { Id = "A", CutoffHour = 14, CutoffMinute = 30, CloseAtRthClose = close };

    [Fact]
    public async Task PastCutoff_ClosingSetup_FlattensItsPosition()
    {
        var (group, handler, exec) = WithGroup(CutoffAt1430(close: true));

        await group.ProcessBarAsync(MakeBar(PastCutoffUtc, 20010m, 20012m, 20008m, 20010m));

        Assert.False(handler.HasActiveGroup("A"));
        Assert.Equal(1, exec.MarketCloses);
    }

    [Fact]
    public async Task PastCutoff_HoldingSetup_KeepsItsPosition_AndTakesNoNewEntry()
    {
        var s = CutoffAt1430(close: false);
        var (group, handler, exec) = WithGroup(s);

        await group.ProcessBarAsync(MakeBar(PastCutoffUtc, 20010m, 20012m, 20008m, 20010m));

        Assert.True(handler.HasActiveGroup("A"));
        Assert.Equal(0, exec.MarketCloses);
        Assert.True(s.InTrade);
        Assert.Equal(0, s.OnBarCallCount);   // not evaluated past its cutoff, so it cannot arm or enter
    }

    [Fact]
    public async Task PastCutoffTick_ClosingSetup_FlattensItsPosition()
    {
        var (group, handler, exec) = WithGroup(CutoffAt1430(close: true));

        await group.ProcessTickAsync(20010m, PastCutoffUtc);

        Assert.False(handler.HasActiveGroup("A"));
        Assert.Equal(1, exec.MarketCloses);
    }

    [Fact]
    public async Task PastCutoffTick_HoldingSetup_KeepsItsPosition()
    {
        var s = CutoffAt1430(close: false);
        var (group, handler, exec) = WithGroup(s);

        await group.ProcessTickAsync(20010m, PastCutoffUtc);

        Assert.True(handler.HasActiveGroup("A"));
        Assert.Equal(0, exec.MarketCloses);
        Assert.Equal(0, s.OnTickCallCount);
    }

    [Fact]
    public async Task PastCutoff_HoldingSetup_StillCancelsAnUnfilledEntry()
    {
        var (group, handler, _) = WithGroup(CutoffAt1430(close: false), filled: false);

        await group.ProcessBarAsync(MakeBar(PastCutoffUtc, 20010m, 20012m, 20008m, 20010m));

        Assert.False(handler.HasActiveGroup("A"));
    }

    [Theory]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    public async Task SessionSlotOff_ClosingSetupFlattens_HoldingSetupKeeps(bool close, bool kept)
    {
        var s = new SessionGatedFakeStrategy { Id = "A", AllowedSession = "London", CloseAtRthClose = close };
        var (group, handler, _) = WithGroup(s);
        group.SetActiveSessionId("NY");

        await group.ProcessBarAsync(MakeBar(MorningUtc, 20010m, 20012m, 20008m, 20010m));
        await group.ProcessTickAsync(20010m, MorningUtc.AddSeconds(30));

        Assert.Equal(kept, handler.HasActiveGroup("A"));
    }
```

- [ ] **Step 2: Run them to verify the holding ones fail**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~TickerGroupTests`
Expected: `PastCutoff_HoldingSetup_KeepsItsPosition_AndTakesNoNewEntry`, `PastCutoffTick_HoldingSetup_KeepsItsPosition` and `SessionSlotOff_...(close: False, kept: True)` FAIL (`Assert.True() Failure` on `HasActiveGroup`). The closing and pending-cancel tests PASS.

- [ ] **Step 3: Bar path — skip a holding strategy's position**

In `CRV.Core/Strategy/TickerGroup.cs`, `ProcessBarAsync`, replace:

```csharp
                if (!IsEnabledForCurrentSession(strategy))
                {
                    if (strategy.IsActive)
                    {
                        var px = bar.Close > 0 ? bar.Close : _lastBarClose;
```

with:

```csharp
                if (!IsEnabledForCurrentSession(strategy))
                {
                    // A holding strategy keeps its open position when its session slot is off.
                    if (strategy.IsActive && strategy.CloseAtRthClose)
                    {
                        var px = bar.Close > 0 ? bar.Close : _lastBarClose;
```

Then replace:

```csharp
                    // Force-exit active trades past cutoff (CloseAtRthClose behavior)
                    if (strategy.IsActive)
                    {
                        var px = bar.Close > 0 ? bar.Close : _lastBarClose;
                        strategy.ForceExit(px, DateTime.UtcNow, ExitReason.SessionEnd);
                        if (_brokerHandler != null)
                            await _brokerHandler.ExitGroupAsync(strategy.Id, px, ExitReason.SessionEnd);
                    }
```

with:

```csharp
                    // Force-exit active trades past cutoff (CloseAtRthClose behavior).
                    // A holding strategy keeps its position; Disarm() below still stops new entries.
                    if (strategy.IsActive)
                    {
                        if (strategy.CloseAtRthClose)
                        {
                            var px = bar.Close > 0 ? bar.Close : _lastBarClose;
                            strategy.ForceExit(px, DateTime.UtcNow, ExitReason.SessionEnd);
                            if (_brokerHandler != null)
                                await _brokerHandler.ExitGroupAsync(strategy.Id, px, ExitReason.SessionEnd);
                        }
                    }
```

The `else if (_brokerHandler != null)` pending-cancel branch that follows is unchanged and still pairs with `if (strategy.IsActive)`.

- [ ] **Step 4: Tick path — same rule**

In `ProcessTickAsync`, replace:

```csharp
                if (!IsEnabledForCurrentSession(strategy))
                {
                    if (strategy.IsActive)
                    {
                        strategy.ForceExit(price, utc, ExitReason.SessionEnd);
```

with:

```csharp
                if (!IsEnabledForCurrentSession(strategy))
                {
                    if (strategy.IsActive && strategy.CloseAtRthClose)
                    {
                        strategy.ForceExit(price, utc, ExitReason.SessionEnd);
```

and replace:

```csharp
                if (IsPastCutoff(strategy, tickLocalTime))
                {
                    if (strategy.IsActive)
                    {
                        strategy.ForceExit(price, utc, ExitReason.SessionEnd);
```

with:

```csharp
                if (IsPastCutoff(strategy, tickLocalTime))
                {
                    if (strategy.IsActive && strategy.CloseAtRthClose)
                    {
                        strategy.ForceExit(price, utc, ExitReason.SessionEnd);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Strategy/TickerGroup.cs CRV.Core.Tests/Strategy/TickerGroupTests.cs
git commit -m "feat(strategy): past its cutoff a holding setup keeps its position and takes no new entry

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Session end keeps held positions

**Files:**
- Modify: `CRV.Core/Strategy/BrokerEventHandler.cs:577-591` (`ExitAllAsync`)
- Modify: `CRV.Core/Strategy/ComposableEngine.cs:401-409` (`ForceExitAllAsync`)
- Test: `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs` (append), `CRV.Core.Tests/Strategy/HoldPastSessionEndTests.cs` (create)

**Interfaces:**
- Consumes: `ISetupStrategy.CloseAtRthClose` (Task 2), resets keep `InTrade` (Task 3).
- Produces: `Task BrokerEventHandler.ExitAllAsync(Func<string, decimal>? priceResolver = null, DateTime? exitTime = null, Func<GroupOrder, ISetupStrategy, bool>? keep = null)`; `internal static bool ComposableEngine.HoldsPastSessionEnd(GroupOrder group, ISetupStrategy strategy)`.

- [ ] **Step 1: Write the failing handler test**

Append inside `BrokerEventHandlerTests`:

```csharp
    [Fact]
    public async Task ExitAll_LeavesTheGroupsTheFilterKeeps()
    {
        var exec = new FakeGroupExecutor();
        var handler = new BrokerEventHandler(exec);

        var held = MakeGroup("hold");
        held.Status = GroupOrderStatus.Active;
        held.EntryPrice = 20000m;
        handler.RegisterGroup(held, new FakeSetup { Id = "hold" });

        var closed = new GroupOrder
        {
            GroupOrderId = "grp-002", SetupId = "close", Ticker = "/NQH2026", Direction = Direction.Long,
            TotalContracts = 2, PointValue = 20m, EntryPrice = 20000m, Status = GroupOrderStatus.Active, Broker = "Mock",
        };
        handler.RegisterGroup(closed, new FakeSetup { Id = "close", SetupId = SetupId.B });

        await handler.ExitAllAsync(_ => 20010m, keep: (g, s) => s.Id == "hold");

        Assert.NotNull(handler.GetActiveGroup("hold"));
        Assert.Null(handler.GetActiveGroup("close"));
        Assert.Single(exec.MarketCloses);
    }
```

- [ ] **Step 2: Write the failing engine tests (live path)**

`CRV.Core.Tests/Strategy/HoldPastSessionEndTests.cs`:

```csharp
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// The session-end exit the live orchestrator and the backtest both call
/// (ComposableEngine.ForceExitAllAsync), with a mock executor.
/// </summary>
public class HoldPastSessionEndTests
{
    private static readonly DateTime EnteredAt = new(2026, 4, 15, 18, 0, 0, DateTimeKind.Utc);   // 14:00 ET
    private static readonly DateTime SessionEnd = new(2026, 4, 15, 20, 0, 0, DateTimeKind.Utc);  // 16:00 ET
    private static readonly DateTime NextMorning = new(2026, 4, 16, 14, 0, 0, DateTimeKind.Utc); // 10:00 ET

    /// <summary>Fills every entry at once and hands back a group with a working stop and target.</summary>
    private sealed class FillingExecutor : IGroupOrderExecutor
    {
        public Dictionary<string, GroupOrder> Groups { get; } = new();
        public bool IsSimulated => true;

        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
        {
            var id = "g-" + sig.SetupLabel;
            var g = new GroupOrder
            {
                GroupOrderId = id, SetupId = sig.SetupLabel, Ticker = sig.Ticker, Direction = sig.Direction,
                TotalContracts = sig.TotalContracts, PointValue = sig.PointValue, EntryPrice = sig.Entry,
                InitialStopPrice = sig.Stop, Status = GroupOrderStatus.Active, CreatedAt = sig.Time,
            };
            g.Legs.Add(new OrderLeg { GroupOrderId = id, OrderId = id + "-e", LegType = LegType.Entry, Status = OrderLegStatus.Filled, Price = sig.Entry, Quantity = sig.TotalContracts });
            g.Legs.Add(new OrderLeg { GroupOrderId = id, OrderId = id + "-t", LegType = LegType.Tg2, Price = sig.Tg2Price, Quantity = sig.TotalContracts });
            g.Legs.Add(new OrderLeg { GroupOrderId = id, OrderId = id + "-s", LegType = LegType.Stop, Price = sig.Stop, Quantity = sig.TotalContracts });
            Groups[sig.SetupLabel] = g;
            return Task.FromResult<GroupOrder?>(g);
        }

        public Task ModifyOrderAsync(string id, decimal? p, int? q) => Task.CompletedTask;
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> PlaceMarketCloseAsync(string t, Direction d, int q) => Task.FromResult(0m);
    }

    private sealed class Prices : ILastPriceProvider
    {
        private readonly Dictionary<string, decimal> _p = new();
        public decimal GetLastPrice(string t) => _p.TryGetValue(t, out var v) ? v : 0m;
        public void UpdatePrice(string t, decimal p) => _p[t] = p;
    }

    private sealed class NoopExec : IOrderExecutor
    { public Task<decimal?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<decimal?>(null); }

    private sealed class NullSink : IStrategyEventSink
    {
        public Task OnEntryAsync(EntrySignal s) => Task.CompletedTask;
        public Task OnExitAsync(TradeRecord t) => Task.CompletedTask;
        public Task OnSnapshotAsync(EngineSnapshot s) => Task.CompletedTask;
    }

    private sealed record Rig(ComposableEngine Engine, BrokerEventHandler Handler, FillingExecutor Exec, List<TradeRecord> Trades);

    private static Rig Build()
    {
        var exec = new FillingExecutor();
        var handler = new BrokerEventHandler(exec) { IsBacktest = true };
        var prices = new Prices();
        prices.UpdatePrice("MNQM26", 18050m);
        prices.UpdatePrice("MESM26", 5010m);
        var cfg = new StrategyConfig { Ticker = "MNQM26", PointValue = 2m, TickSize = 0.25m, UseDailyLossLimit = false }.ToEngineConfig();
        var engine = new ComposableEngine(new NoopExec(), new NullSink(), prices, cfg, handler);
        engine.AddSetup(Setup("hold-mnq", "MNQM26", 2m, close: false));
        engine.AddSetup(Setup("close-mes", "MESM26", 5m, close: true));
        var trades = new List<TradeRecord>();
        handler.OnTradeCompleted += (_, t) => trades.Add(t);
        return new Rig(engine, handler, exec, trades);
    }

    private static StrategySetupConfig Setup(string id, string ticker, decimal pv, bool close) => new()
    {
        Id = id, Name = id, StrategyType = StrategyType.Pullback, Enabled = true,
        Ticker = ticker, PointValue = pv, TickSize = 0.25m, CloseAtRthClose = close,
    };

    private static Task Enter(Rig rig, string id, string ticker, decimal entry, decimal stop, decimal pv) =>
        rig.Engine.RouteSignalsAsync(new List<StrategySignals>
        {
            new(rig.Engine.GetStrategy(id)!, new EntrySignal(SetupId.F, Direction.Long, entry, stop, entry + 40m, 0m, 1, EnteredAt,
                OrderType: "Market", Ticker: ticker, SetupLabel: id, PointValue: pv, UsePartial: false, UseBe: false)),
        });

    [Fact]
    public async Task SessionEnd_ClosesTheClosingSetup_AndKeepsTheHoldingOne()
    {
        var rig = Build();
        await Enter(rig, "hold-mnq", "MNQM26", 18010m, 18000m, 2m);
        await Enter(rig, "close-mes", "MESM26", 5000m, 4990m, 5m);

        await rig.Engine.ForceExitAllAsync(SessionEnd);

        Assert.True(rig.Handler.HasActiveGroup("hold-mnq"));
        Assert.True(rig.Engine.GetStrategy("hold-mnq")!.InTrade);
        Assert.False(rig.Handler.HasActiveGroup("close-mes"));
        Assert.False(rig.Engine.GetStrategy("close-mes")!.InTrade);
        var t = Assert.Single(rig.Trades);
        Assert.Equal(("close-mes", ExitReason.SessionEnd), (t.SetupLabel, t.ExitReason));
    }

    [Fact]
    public async Task HeldPosition_StillExitsOnItsStop_NextSession()
    {
        var rig = Build();
        await Enter(rig, "hold-mnq", "MNQM26", 18010m, 18000m, 2m);
        await rig.Engine.ForceExitAllAsync(SessionEnd);

        // Next trading day: daily reset, then the next session starts.
        rig.Engine.ResetDaily();
        rig.Engine.Reconfigure(new StrategyConfig { Ticker = "MNQM26", UseDailyLossLimit = false }, SessionId.NY);
        Assert.True(rig.Engine.GetStrategy("hold-mnq")!.InTrade);

        var g = rig.Exec.Groups["hold-mnq"];
        await rig.Handler.HandleEventAsync(new OrderEvent(g.GroupOrderId, g.GroupOrderId + "-s", LegType.Stop,
            OrderLegStatus.Filled, 18000m, 1, null, null, NextMorning));

        Assert.False(rig.Handler.HasActiveGroup("hold-mnq"));
        Assert.False(rig.Engine.GetStrategy("hold-mnq")!.InTrade);
        var t = Assert.Single(rig.Trades);
        Assert.Equal((ExitReason.Stop, 18000m, NextMorning), (t.ExitReason, t.Exit, t.ExitedAt));
    }

    [Fact]
    public async Task SessionEnd_CancelsAHoldingSetupsUnfilledEntry()
    {
        var rig = Build();
        var pending = new GroupOrder
        {
            GroupOrderId = "g-pending", SetupId = "hold-mnq", Ticker = "MNQM26", Direction = Direction.Long,
            TotalContracts = 1, PointValue = 2m, Status = GroupOrderStatus.Pending,
        };
        pending.Legs.Add(new OrderLeg { GroupOrderId = "g-pending", OrderId = "g-pending-e", LegType = LegType.Entry, Price = 18010m, Quantity = 1 });
        rig.Handler.RegisterGroup(pending, rig.Engine.GetStrategy("hold-mnq")!);

        await rig.Engine.ForceExitAllAsync(SessionEnd);

        Assert.False(rig.Handler.HasActiveGroup("hold-mnq"));
        Assert.Equal(GroupOrderStatus.Canceled, pending.Status);
    }

    [Fact]
    public async Task SessionEnd_ClosesAManualTrade()
    {
        var rig = Build();
        var signal = new EntrySignal(SetupId.F, Direction.Long, 18010m, 18000m, 18050m, 0m, 1, EnteredAt,
            Ticker: "MNQM26", SetupLabel: "manual-1", PointValue: 2m);
        var g = (await rig.Exec.OnEntrySignalAsync(signal))!;
        rig.Handler.RegisterGroup(g, new ManualStrategy(signal) { PointValue = 2m });

        await rig.Engine.ForceExitAllAsync(SessionEnd);

        Assert.False(rig.Handler.HasActiveGroup("manual-1"));
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HoldPastSessionEndTests|FullyQualifiedName~BrokerEventHandlerTests"`
Expected: build FAILS with `No overload for method 'ExitAllAsync' takes ... 'keep'`. After Step 4 alone: the engine tests that need the hold to survive session end (`SessionEnd_ClosesTheClosingSetup_AndKeepsTheHoldingOne`, `HeldPosition_StillExitsOnItsStop_NextSession`) FAIL until Step 5.

- [ ] **Step 4: Add the keep filter to `ExitAllAsync`**

In `CRV.Core/Strategy/BrokerEventHandler.cs`, replace lines 577-591:

```csharp
    /// <summary>Force-close all active groups (session boundary).</summary>
    /// <param name="priceResolver">Resolves current price for a ticker. Null = no trade records.</param>
    /// <param name="exitTime">Simulated time for backtest; null = DateTime.UtcNow.</param>
    public async Task ExitAllAsync(Func<string, decimal>? priceResolver = null, DateTime? exitTime = null)
    {
        List<(string Id, string Ticker)> setupInfo;
        lock (_lock)
            setupInfo = _active.Select(kv => (kv.Key, kv.Value.Group.Ticker)).ToList();
```

with:

```csharp
    /// <summary>Force-close active groups at a session boundary, except those <paramref name="keep"/> returns true for.</summary>
    /// <param name="priceResolver">Resolves current price for a ticker. Null = no trade records.</param>
    /// <param name="exitTime">Simulated time for backtest; null = DateTime.UtcNow.</param>
    /// <param name="keep">Groups to leave open (a held position); null closes every group.</param>
    public async Task ExitAllAsync(Func<string, decimal>? priceResolver = null, DateTime? exitTime = null,
        Func<GroupOrder, ISetupStrategy, bool>? keep = null)
    {
        List<(string Id, string Ticker)> setupInfo;
        lock (_lock)
            setupInfo = _active
                .Where(kv => keep == null || !keep(kv.Value.Group, kv.Value.Strategy))
                .Select(kv => (kv.Key, kv.Value.Group.Ticker)).ToList();
```

The rest of the method is unchanged.

- [ ] **Step 5: Keep held positions in `ForceExitAllAsync`**

In `CRV.Core/Strategy/ComposableEngine.cs`, replace:

```csharp
    /// <summary>Force-exit all active trades.</summary>
    public async Task ForceExitAllAsync(DateTime? utcTime = null)
    {
        if (_brokerHandler != null)
            await _brokerHandler.ExitAllAsync(ticker => _prices.GetLastPrice(ticker), utcTime);
```

with:

```csharp
    /// <summary>
    /// Session-end exit: closes every active group except the filled positions of strategies
    /// that hold past the session, then resets session state. Resets keep InTrade, so a held
    /// position stays tracked into the next session.
    /// </summary>
    public async Task ForceExitAllAsync(DateTime? utcTime = null)
    {
        if (_brokerHandler != null)
            await _brokerHandler.ExitAllAsync(ticker => _prices.GetLastPrice(ticker), utcTime, HoldsPastSessionEnd);
```

and add, directly after that method:

```csharp
    /// <summary>
    /// A filled position whose strategy holds past the session
    /// (<see cref="ISetupStrategy.CloseAtRthClose"/> false). An unfilled entry is never held.
    /// </summary>
    internal static bool HoldsPastSessionEnd(GroupOrder group, ISetupStrategy strategy)
        => !strategy.CloseAtRthClose
           && group.Status is GroupOrderStatus.Active or GroupOrderStatus.PartialFilled;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failures.

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy/BrokerEventHandler.cs CRV.Core/Strategy/ComposableEngine.cs CRV.Core.Tests/Strategy
git commit -m "feat(strategy): session end keeps a holding setup's filled position

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Backtest — held orders fill between sessions

**Files:**
- Modify: `CRV.Backtest/Engine/BacktestEngine.cs:266-321` (`EmitBucket`) + new private `TickPath`
- Test: `CRV.Core.Tests/Backtest/HoldPastSessionEndBacktestTests.cs`

**Interfaces:**
- Consumes: Tasks 2–5 (hold at cutoff and session end).
- Produces: between sessions (not warmup), every one-minute bar's O/H/L/C ticks go to `BacktestGroupOrderExecutor.EvaluateFillsAsync`, so a held stop or target fills when hit.

- [ ] **Step 1: Write the tests**

`CRV.Core.Tests/Backtest/HoldPastSessionEndBacktestTests.cs`:

```csharp
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// Hold vs close through the backtest. The fixture's session fills a long at 18010 with its
/// stop at 18000. The target is moved out of reach and the day runs flat at 18100 to 16:30 ET,
/// past the 15:00 cutoff and the 16:00 session end.
/// </summary>
public class HoldPastSessionEndBacktestTests
{
    private static readonly DateTime DayOneOpen = PullbackSessionFixture.Open;   // 09:30 ET
    private static readonly DateTime DayTwoOpen = DayOneOpen.AddDays(1);

    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> DayOneThen(IEnumerable<(string, Bar)> after)
    {
        await foreach (var b in PullbackSessionFixture.Session()) yield return b;
        for (int m = 120; m < 420; m++)
            yield return (PullbackSessionFixture.Ticker, new Bar(DayOneOpen.AddMinutes(m), 18100m, 18101m, 18099m, 18100m, 500));
        foreach (var b in after) yield return b;
    }

    /// <summary>18100 falling to 17990 over 30 minutes, then flat: through the 18000 stop.</summary>
    private static IEnumerable<(string, Bar)> FallThroughStop(DateTime start)
    {
        for (int i = 0; i < 30; i++)
        {
            decimal a = 18100m - 110m * i / 30, b = 18100m - 110m * (i + 1) / 30;
            yield return (PullbackSessionFixture.Ticker, new Bar(start.AddMinutes(i), a, a, b, b, 500));
        }
        for (int i = 30; i < 40; i++)
            yield return (PullbackSessionFixture.Ticker, new Bar(start.AddMinutes(i), 17990m, 17991m, 17989m, 17990m, 500));
    }

    private static Task<BacktestResult> Run(bool closeAtRthClose, IAsyncEnumerable<(string, Bar)> bars)
    {
        var cfg = PullbackSessionFixture.Config(s => { s.TargetPct = 1000; s.CloseAtRthClose = closeAtRthClose; });
        var bt = PullbackSessionFixture.BtConfig();
        bt.To = bt.From.AddDays(3);
        return new BacktestEngine(cfg, bt, NullLogger<BacktestEngine>.Instance).RunAsync(bars);
    }

    [Fact]
    public async Task ClosingSetup_IsFlattenedAtItsCutoff()
    {
        var r = await Run(true, DayOneThen(FallThroughStop(DayTwoOpen)));

        var first = r.Trades[0];
        Assert.Equal(ExitReason.SessionEnd, first.ExitReason);
        Assert.InRange(first.ExitedAt, DayOneOpen.AddMinutes(330), DayOneOpen.AddMinutes(390));   // 15:00–16:00 ET
        Assert.InRange(first.Exit, 18099m, 18101m);
    }

    [Fact]
    public async Task HoldingSetup_KeepsItsTradeThroughSessionEnd_AndExitsOnItsStopNextSession()
    {
        var r = await Run(false, DayOneThen(FallThroughStop(DayTwoOpen)));

        var first = r.Trades[0];
        Assert.Equal(ExitReason.Stop, first.ExitReason);
        Assert.Equal(DayTwoOpen.Date, first.ExitedAt.Date);
        Assert.True(first.Exit <= 18000m, $"stop fill {first.Exit} should be at or below 18000");
        // Nothing else entered while the trade was held.
        Assert.All(r.Trades.Skip(1), t => Assert.True(t.EnteredAt >= first.ExitedAt));
    }

    [Fact]
    public async Task HeldStop_FillsOvernight_WhenNoSessionIsRunning()
    {
        var evening = DayOneOpen.Date.AddHours(23);   // 19:00 ET; only NY is backtested
        var r = await Run(false, DayOneThen(FallThroughStop(evening)));

        var t = Assert.Single(r.Trades);
        Assert.Equal(ExitReason.Stop, t.ExitReason);
        Assert.InRange(t.ExitedAt, evening, evening.AddMinutes(40));
    }
}
```

- [ ] **Step 2: Run them to verify the overnight one fails**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~HoldPastSessionEndBacktestTests`
Expected: `HeldStop_FillsOvernight_WhenNoSessionIsRunning` FAILS (`Assert.Single() Failure: The collection was empty`: the stop is never evaluated between sessions). The other two PASS: Tasks 4–5 already carry the hold through the backtest.

- [ ] **Step 3: Evaluate fills between sessions, through one tick-path helper**

In `CRV.Backtest/Engine/BacktestEngine.cs`, `EmitBucket`, replace from `if (isWarmup || betweenSessions)` through the end of the live branch's tick loop:

```csharp
        if (isWarmup || betweenSessions)
        {
            if (betweenSessions) engine.ClearIdle();
            await engine.WarmupBarAsync(tfBar, ticker, ct);
            if (betweenSessions) engine.SetIdle();
        }
        else
        {
            // 1. Fire accumulated 1-min OHLC ticks for entry/exit evaluation.
            foreach (var (o, h, l, c, t) in bkt.Pending)
            {
                prices.UpdatePrice(ticker, o);
                await engine.ProcessPriceTickAsync(o, t, ticker);
                await groupExec.EvaluateFillsAsync(o, t, ticker);
                if (c >= o)
                {   // Bullish: O → L → H → C
                    prices.UpdatePrice(ticker, l);
                    await engine.ProcessPriceTickAsync(l, t.AddSeconds(15), ticker);
                    await groupExec.EvaluateFillsAsync(l, t.AddSeconds(15), ticker);
                    prices.UpdatePrice(ticker, h);
                    await engine.ProcessPriceTickAsync(h, t.AddSeconds(30), ticker);
                    await groupExec.EvaluateFillsAsync(h, t.AddSeconds(30), ticker);
                }
                else
                {   // Bearish: O → H → L → C
                    prices.UpdatePrice(ticker, h);
                    await engine.ProcessPriceTickAsync(h, t.AddSeconds(15), ticker);
                    await groupExec.EvaluateFillsAsync(h, t.AddSeconds(15), ticker);
                    prices.UpdatePrice(ticker, l);
                    await engine.ProcessPriceTickAsync(l, t.AddSeconds(30), ticker);
                    await groupExec.EvaluateFillsAsync(l, t.AddSeconds(30), ticker);
                }
                prices.UpdatePrice(ticker, c);
                await engine.ProcessPriceTickAsync(c, t.AddSeconds(45), ticker);
                await groupExec.EvaluateFillsAsync(c, t.AddSeconds(45), ticker);
            }
```

with:

```csharp
        if (isWarmup || betweenSessions)
        {
            if (betweenSessions) engine.ClearIdle();
            await engine.WarmupBarAsync(tfBar, ticker, ct);
            if (betweenSessions) engine.SetIdle();

            // Orders stay working at the broker between sessions, so a position held past
            // session end can still hit its stop or target overnight. Session end cancels
            // everything else, so this finds nothing to fill unless a position is held.
            if (!isWarmup)
            {
                foreach (var p in bkt.Pending)
                foreach (var (price, time) in TickPath(p.O, p.H, p.L, p.C, p.T))
                {
                    prices.UpdatePrice(ticker, price);
                    await groupExec.EvaluateFillsAsync(price, time, ticker);
                }
            }
        }
        else
        {
            // 1. Fire accumulated 1-min OHLC ticks for entry/exit evaluation.
            foreach (var p in bkt.Pending)
            foreach (var (price, time) in TickPath(p.O, p.H, p.L, p.C, p.T))
            {
                prices.UpdatePrice(ticker, price);
                await engine.ProcessPriceTickAsync(price, time, ticker);
                await groupExec.EvaluateFillsAsync(price, time, ticker);
            }
```

Leave step 2 (`prices.UpdatePrice(ticker, bkt.Close); await engine.ProcessBarAsync(...)`) and the closing braces as they are. Then add, directly after `EmitBucket`:

```csharp
    /// <summary>
    /// The four prices a one-minute bar is replayed as, 15 s apart:
    /// bullish O → L → H → C, bearish O → H → L → C.
    /// </summary>
    private static IEnumerable<(decimal Price, DateTime Time)> TickPath(decimal o, decimal h, decimal l, decimal c, DateTime t)
    {
        yield return (o, t);
        if (c >= o)
        {
            yield return (l, t.AddSeconds(15));
            yield return (h, t.AddSeconds(30));
        }
        else
        {
            yield return (h, t.AddSeconds(15));
            yield return (l, t.AddSeconds(30));
        }
        yield return (c, t.AddSeconds(45));
    }
```

- [ ] **Step 4: Run all tests, including the determinism and fill suites**

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failures. `BacktestDeterminismTests`, `BacktestPartialFillTests`, `ExecutionModelTests` and `BacktestAutoTrailTests` passing shows the tick-path refactor changed nothing for closing configs.

- [ ] **Step 5: Commit**

```bash
git add CRV.Backtest/Engine/BacktestEngine.cs CRV.Core.Tests/Backtest/HoldPastSessionEndBacktestTests.cs
git commit -m "fix(backtest): a held position's stop and target fill between sessions

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: The trading-day rule in one place

**Files:**
- Create: `CRV.Core/Models/TradingDay.cs`
- Modify: `CRV.Core/Models/StrategyConfig.cs:446-451`
- Test: `CRV.Core.Tests/Models/TradingDayTests.cs`

**Interfaces:**
- Produces: `static DateTime TradingDay.Of(DateTime localTime, int sessionStartHour)`; `static DateTime TradingDay.OfUtc(DateTime utc, TimeZoneInfo zone, int sessionStartHour)`; `DateTime StrategyConfig.TradingDateOfUtc(DateTime utc)`.

- [ ] **Step 1: Write the failing test**

`CRV.Core.Tests/Models/TradingDayTests.cs`:

```csharp
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Models;

/// <summary>Futures sessions span midnight: from the session start hour (18:00 ET) on, it is the next trading day.</summary>
public class TradingDayTests
{
    private static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Theory]
    [InlineData(17, 59, 15)]
    [InlineData(18, 0, 16)]
    [InlineData(9, 30, 15)]
    public void Of_RollsAtTheSessionStartHour(int hour, int minute, int expectedDay)
        => Assert.Equal(new DateTime(2026, 4, expectedDay), TradingDay.Of(new DateTime(2026, 4, 15, hour, minute, 0), 18));

    [Theory]
    [InlineData(2026, 4, 15, 21, 59, 15)]   // 17:59 EDT
    [InlineData(2026, 4, 15, 22, 0, 16)]    // 18:00 EDT
    [InlineData(2026, 4, 16, 1, 0, 16)]     // 21:00 EDT the evening before; UTC already says the 16th
    public void OfUtc_UsesTheExchangeClock(int y, int mo, int d, int h, int mi, int expectedDay)
        => Assert.Equal(new DateTime(2026, 4, expectedDay),
            TradingDay.OfUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc), Ny, 18));

    [Fact]
    public void StrategyConfig_AgreesWithTradingDay()
    {
        var cfg = new StrategyConfig { Timezone = "America/New_York", SessionStartHour = 18 };
        var utc = new DateTime(2026, 4, 15, 22, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 4, 16), cfg.TradingDateOfUtc(utc));
        Assert.Equal(new DateTime(2026, 4, 16), cfg.TradingDate(new DateTime(2026, 4, 15, 18, 30, 0)));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~TradingDayTests`
Expected: build FAILS with `The name 'TradingDay' does not exist in the current context`.

- [ ] **Step 3: Write `TradingDay`**

`CRV.Core/Models/TradingDay.cs`:

```csharp
namespace CRV.Core.Models;

/// <summary>
/// The trading day a moment belongs to. Futures sessions span midnight: from the session
/// start hour (18:00 ET for CME) on, it is already the next calendar day's session.
/// </summary>
public static class TradingDay
{
    public static DateTime Of(DateTime localTime, int sessionStartHour)
        => localTime.Hour >= sessionStartHour ? localTime.Date.AddDays(1) : localTime.Date;

    public static DateTime OfUtc(DateTime utc, TimeZoneInfo zone, int sessionStartHour)
        => Of(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone), sessionStartHour);
}
```

- [ ] **Step 4: Make `StrategyConfig` use it**

In `CRV.Core/Models/StrategyConfig.cs`, replace the body of `TradingDate` (keep its XML doc):

```csharp
    public DateTime TradingDate(DateTime localTime)
    {
        if (localTime.Hour >= SessionStartHour)
            return localTime.Date.AddDays(1);
        return localTime.Date;
    }
```

with:

```csharp
    public DateTime TradingDate(DateTime localTime) => TradingDay.Of(localTime, SessionStartHour);

    /// <summary>The trading date of a UTC moment, read on this config's <see cref="Timezone"/> clock.</summary>
    public DateTime TradingDateOfUtc(DateTime utc)
        => TradingDay.OfUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(Timezone), SessionStartHour);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Models/TradingDay.cs CRV.Core/Models/StrategyConfig.cs CRV.Core.Tests/Models/TradingDayTests.cs
git commit -m "refactor: one trading-day rule for local and UTC times

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: `RiskManager` counts held open loss

**Files:**
- Modify: `CRV.Core/Strategy/RiskManager.cs:20-35, 72-85`
- Modify (callers): `CRV.Core/Strategy/ComposableEngine.cs:152`, `CRV.Core/Strategy/SnapshotAggregator.cs:122`, `CRV.Core.Tests/Strategy/RiskManagerTests.cs`, `CRV.Core.Tests/Strategy/ComposableEngineTests.cs:325, 387`
- Test: `CRV.Core.Tests/Strategy/RiskManagerTests.cs` (append)

**Interfaces:**
- Produces: `bool RiskManager.DdBreached(decimal heldUnrealized = 0m)`; `bool RiskManager.CanTrade(bool useDailyLossLimit, decimal maxDailyLoss, DailyLossMode mode = DailyLossMode.Floor, decimal heldUnrealized = 0m)`. Only the negative part of `heldUnrealized` counts.

- [ ] **Step 1: Write the failing tests**

Append inside `RiskManagerTests`:

```csharp
    // ──────────────────────────────────────────────
    // Held positions: their open loss counts, their open profit does not
    // ──────────────────────────────────────────────

    [Fact]
    public void DdBreached_FloorMode_CountsHeldOpenLoss()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-200m);
        Assert.True(rm.CanTrade(true, 500m));

        Assert.True(rm.DdBreached(heldUnrealized: -300m));    // -200 - 300 = -500
        Assert.False(rm.DdBreached(heldUnrealized: -299m));
    }

    [Fact]
    public void CanTrade_HeldWinner_DoesNotHideARealizedLoss()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-600m);

        Assert.False(rm.CanTrade(true, 500m, DailyLossMode.Floor, heldUnrealized: 900m));
    }

    [Fact]
    public void DdBreached_PeakMode_CountsHeldOpenLossFromThePeak()
    {
        var rm = new RiskManager();
        rm.RecordTrade(300m);                                  // peak 300
        Assert.True(rm.CanTrade(true, 200m, DailyLossMode.Peak));

        Assert.True(rm.DdBreached(heldUnrealized: -200m));     // 300 - (300 - 200) = 200
    }

    [Fact]
    public void CanTrade_LimitOff_IgnoresHeldLoss()
    {
        var rm = new RiskManager();
        Assert.True(rm.CanTrade(false, 0m, DailyLossMode.Floor, heldUnrealized: -10_000m));
    }

    [Fact]
    public void HeldLoss_LeavesTheRealizedFiguresAlone()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-100m);
        rm.CanTrade(true, 500m, DailyLossMode.Floor, heldUnrealized: -450m);

        Assert.Equal(-100m, rm.TodayPnl);
        Assert.Equal(100m, rm.DailyLossUsed);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~RiskManagerTests`
Expected: build FAILS with `Non-invocable member 'RiskManager.DdBreached' cannot be used like a method`.

- [ ] **Step 3: Make `DdBreached` a method with the held input**

In `CRV.Core/Strategy/RiskManager.cs`, replace:

```csharp
    // Cached limit config for the DdBreached property
    private bool          _useDailyLossLimit;
    private decimal       _maxDailyLoss;
    private DailyLossMode _mode = DailyLossMode.Floor;

    /// <summary>
    /// True when the daily loss limit is breached.
    /// Floor mode: TodayPnl &lt;= -MaxDailyLoss (absolute floor).
    /// Peak mode:  (TodayPeak - TodayPnl) &gt;= MaxDailyLoss (drawdown from high-water mark).
    /// Dynamic in both modes: recovers when PnL improves.
    /// </summary>
    public bool DdBreached => _useDailyLossLimit && _mode switch
    {
        DailyLossMode.Peak  => (TodayPeak - TodayPnl) >= _maxDailyLoss,
        _                   => TodayPnl <= -_maxDailyLoss,
    };
```

with:

```csharp
    // Cached limit config for DdBreached
    private bool          _useDailyLossLimit;
    private decimal       _maxDailyLoss;
    private DailyLossMode _mode = DailyLossMode.Floor;

    /// <summary>
    /// True when the daily loss limit is breached, counting realized P&amp;L plus the open loss of
    /// positions held in from an earlier trading day (<paramref name="heldUnrealized"/>). Only a loss
    /// counts: a held winner never offsets a realized loss.
    /// Floor mode: TodayPnl + held loss &lt;= -MaxDailyLoss (absolute floor).
    /// Peak mode:  (TodayPeak - (TodayPnl + held loss)) &gt;= MaxDailyLoss (drawdown from high-water mark).
    /// Dynamic in both modes: recovers when PnL improves.
    /// </summary>
    public bool DdBreached(decimal heldUnrealized = 0m)
    {
        if (!_useDailyLossLimit) return false;
        var pnl = TodayPnl + Math.Min(0m, heldUnrealized);
        return _mode switch
        {
            DailyLossMode.Peak => (TodayPeak - pnl) >= _maxDailyLoss,
            _                  => pnl <= -_maxDailyLoss,
        };
    }
```

and replace:

```csharp
    public bool CanTrade(bool useDailyLossLimit, decimal maxDailyLoss,
                         DailyLossMode mode = DailyLossMode.Floor)
    {
        // Cache for DdBreached property (used by ProcessPriceTickAsync)
        _useDailyLossLimit = useDailyLossLimit;
        _maxDailyLoss = maxDailyLoss;
        _mode = mode;

        return !DdBreached;
    }
```

with:

```csharp
    public bool CanTrade(bool useDailyLossLimit, decimal maxDailyLoss,
                         DailyLossMode mode = DailyLossMode.Floor, decimal heldUnrealized = 0m)
    {
        // Cache for DdBreached (used by ProcessPriceTickAsync and the snapshot)
        _useDailyLossLimit = useDailyLossLimit;
        _maxDailyLoss = maxDailyLoss;
        _mode = mode;

        return !DdBreached(heldUnrealized);
    }
```

- [ ] **Step 4: Update the callers**

- `CRV.Core/Strategy/ComposableEngine.cs:152`: `if (Risk.DdBreached) return;` → `if (Risk.DdBreached()) return;` (Task 9 adds the held input).
- `CRV.Core/Strategy/SnapshotAggregator.cs:122`: `TradingHalted  = inputs.Risk.DdBreached,` → `TradingHalted  = inputs.Risk.DdBreached(),` (Task 9 adds the held input).
- `CRV.Core.Tests/Strategy/RiskManagerTests.cs`: replace every `rm.DdBreached)` with `rm.DdBreached())` (Edit with replace_all; 14 occurrences).
- `CRV.Core.Tests/Strategy/ComposableEngineTests.cs`: replace every `engine.Risk.DdBreached)` with `engine.Risk.DdBreached())` (2 occurrences, lines 325 and 387).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet build CRV.Trading.sln && dotnet test CRV.Core.Tests`
Expected: build succeeds with no `DdBreached` errors in any project; tests PASS, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Strategy CRV.Core.Tests/Strategy
git commit -m "feat(risk): daily loss check takes the open loss of held positions

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: The engine feeds held open loss to the daily-loss gates

**Files:**
- Modify: `CRV.Core/Strategy/ComposableEngine.cs:152, 194, 472-519` + new `HeldOpenLoss`
- Modify: `CRV.Core/Strategy/SnapshotAggregator.cs:48-49, 122`
- Modify: `docs/risk.md:9` + new section
- Test: `CRV.Core.Tests/Risk/HeldLossDailyLimitTests.cs`, `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs` (append)

**Interfaces:**
- Consumes: `TradingDay.OfUtc` (Task 7), `RiskManager.DdBreached(decimal)` / `CanTrade(..., decimal)` (Task 8), `BrokerEventHandler.GetAllActiveGroups()` / `GetUnrealizedPnl(string, decimal)`.
- Produces: `internal decimal ComposableEngine.HeldOpenLoss(DateTime utcNow)` (≤ 0); `decimal SnapshotAggregator.Inputs.HeldOpenLoss { get; init; }`.

- [ ] **Step 1: Write the failing engine tests**

`CRV.Core.Tests/Risk/HeldLossDailyLimitTests.cs`:

```csharp
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Risk;

/// <summary>
/// A position held in from an earlier trading day counts its open loss against today's daily
/// loss limit. Trading day rolls at 18:00 ET; 2026-04-16 14:00 UTC is 10:00 ET on the 16th.
/// </summary>
public class HeldLossDailyLimitTests
{
    private static readonly DateTime Now       = new(2026, 4, 16, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Yesterday = new(2026, 4, 15, 18, 0, 0, DateTimeKind.Utc);   // 14:00 ET on the 15th

    private sealed class RecordingExecutor : IGroupOrderExecutor
    {
        public List<EntrySignal> Placed { get; } = new();
        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
        {
            Placed.Add(sig);
            return Task.FromResult<GroupOrder?>(new GroupOrder
            {
                GroupOrderId = "g-" + sig.SetupLabel, SetupId = sig.SetupLabel, Ticker = sig.Ticker,
                Direction = sig.Direction, TotalContracts = sig.TotalContracts, EntryPrice = sig.Entry,
                InitialStopPrice = sig.Stop, PointValue = sig.PointValue, Status = GroupOrderStatus.Active, CreatedAt = sig.Time,
            });
        }
        public Task ModifyOrderAsync(string id, decimal? p, int? q) => Task.CompletedTask;
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> PlaceMarketCloseAsync(string t, Direction d, int q) => Task.FromResult(0m);
    }

    private sealed class Prices : ILastPriceProvider
    {
        private readonly Dictionary<string, decimal> _p = new();
        public decimal GetLastPrice(string t) => _p.TryGetValue(t, out var v) ? v : 0m;
        public void UpdatePrice(string t, decimal p) => _p[t] = p;
    }

    private sealed class NoopExec : IOrderExecutor
    { public Task<decimal?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<decimal?>(null); }

    private sealed class NullSink : IStrategyEventSink
    {
        public Task OnEntryAsync(EntrySignal s) => Task.CompletedTask;
        public Task OnExitAsync(TradeRecord t) => Task.CompletedTask;
        public Task OnSnapshotAsync(EngineSnapshot s) => Task.CompletedTask;
    }

    private sealed record Rig(ComposableEngine Engine, BrokerEventHandler Handler, RecordingExecutor Exec, Prices Prices);

    /// <summary>A $500 floor, and a 2-lot MNQ long ($2/pt) from 18000 held since <paramref name="openedAt"/>.</summary>
    private static Rig Build(DateTime openedAt, decimal? mnqPrice)
    {
        var exec = new RecordingExecutor();
        var handler = new BrokerEventHandler(exec) { IsBacktest = true };
        var prices = new Prices();
        if (mnqPrice is decimal p) prices.UpdatePrice("MNQM26", p);
        prices.UpdatePrice("MESM26", 5000m);
        var cfg = new StrategyConfig
        {
            Ticker = "MNQM26", Timezone = "America/New_York", SessionStartHour = 18,
            UseDailyLossLimit = true, MaxDailyLoss = 500m, DailyLossMode = DailyLossMode.Floor,
        }.ToEngineConfig();
        var engine = new ComposableEngine(new NoopExec(), new NullSink(), prices, cfg, handler);
        engine.AddSetup(new StrategySetupConfig { Id = "hold-mnq", Name = "hold-mnq", StrategyType = StrategyType.Pullback, Enabled = true, Ticker = "MNQM26", PointValue = 2m, CloseAtRthClose = false });
        engine.AddSetup(new StrategySetupConfig { Id = "enter-mes", Name = "enter-mes", StrategyType = StrategyType.Pullback, Enabled = true, Ticker = "MESM26", PointValue = 5m });

        var held = new GroupOrder
        {
            GroupOrderId = "held", SetupId = "hold-mnq", Ticker = "MNQM26", Direction = Direction.Long,
            TotalContracts = 2, PointValue = 2m, EntryPrice = 18000m, InitialStopPrice = 17700m,
            Status = GroupOrderStatus.Active, CreatedAt = openedAt,
        };
        var strategy = engine.GetStrategy("hold-mnq")!;
        handler.RegisterGroup(held, strategy);
        strategy.SetInTrade(true);
        return new Rig(engine, handler, exec, prices);
    }

    private static Task TrySignal(Rig rig) => rig.Engine.RouteSignalsAsync(new List<StrategySignals>
    {
        new(rig.Engine.GetStrategy("enter-mes")!, new EntrySignal(SetupId.F, Direction.Long, 5000m, 4990m, 5020m, 0m, 1, Now,
            OrderType: "Market", Ticker: "MESM26", SetupLabel: "enter-mes", PointValue: 5m, UsePartial: false, UseBe: false)),
    });

    [Fact]
    public async Task HeldLossFromAnEarlierDay_PastTheLimit_StopsNewEntries()
    {
        var rig = Build(Yesterday, mnqPrice: 17800m);   // -200 pts × 2 × $2 = -$800

        await TrySignal(rig);

        Assert.Empty(rig.Exec.Placed);
    }

    [Fact]
    public async Task HeldLossWithinTheLimit_LetsEntriesThrough()
    {
        var rig = Build(Yesterday, mnqPrice: 17900m);   // -$400

        await TrySignal(rig);

        Assert.Single(rig.Exec.Placed);
    }

    [Fact]
    public async Task HeldLossAndRealizedLoss_TripTheLimitTogether()
    {
        var rig = Build(Yesterday, mnqPrice: 17900m);   // -$400 held
        rig.Engine.Risk.RecordTrade(-200m);             // -$200 realized today

        await TrySignal(rig);

        Assert.Empty(rig.Exec.Placed);
    }

    [Fact]
    public async Task HeldWinner_DoesNotHideARealizedLoss()
    {
        var rig = Build(Yesterday, mnqPrice: 18500m);   // +$2,000 held
        rig.Engine.Risk.RecordTrade(-600m);

        await TrySignal(rig);

        Assert.Empty(rig.Exec.Placed);
    }

    [Fact]
    public async Task PositionOpenedToday_IsNotHeld()
    {
        var rig = Build(new DateTime(2026, 4, 16, 13, 45, 0, DateTimeKind.Utc), mnqPrice: 17800m);

        await TrySignal(rig);

        Assert.Single(rig.Exec.Placed);
    }

    [Fact]
    public async Task PositionOpenedInTheEveningSession_IsTodays()
    {
        // 22:30 UTC on the 15th is 18:30 ET: already the 16th's session.
        var rig = Build(new DateTime(2026, 4, 15, 22, 30, 0, DateTimeKind.Utc), mnqPrice: 17800m);

        await TrySignal(rig);

        Assert.Single(rig.Exec.Placed);
    }

    [Fact]
    public async Task HeldPositionWithNoPriceYet_CountsAsZero()
    {
        var rig = Build(Yesterday, mnqPrice: null);

        await TrySignal(rig);

        Assert.Single(rig.Exec.Placed);
    }

    [Fact]
    public async Task ATrippedLimit_DoesNotCloseTheHeldPosition()
    {
        var rig = Build(Yesterday, mnqPrice: 17800m);

        await TrySignal(rig);

        Assert.True(rig.Handler.HasActiveGroup("hold-mnq"));
        Assert.True(rig.Engine.GetStrategy("hold-mnq")!.InTrade);
    }

    [Fact]
    public void HeldOpenLoss_SumsTheLossOfPositionsFromEarlierDays()
    {
        var rig = Build(Yesterday, mnqPrice: 17800m);
        Assert.Equal(-800m, rig.Engine.HeldOpenLoss(Now));
    }
}
```

Append inside `SnapshotAggregatorTests`:

```csharp
    [Fact]
    public void TradingHalted_CountsHeldOpenLoss()
    {
        var risk = new RiskManager();
        risk.RecordTrade(-200m);
        risk.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m);

        var snap = SnapshotAggregator.Build(new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(), Risk = risk, LastPrice = 5000m, HeldOpenLoss = -300m,
        });

        Assert.True(snap.TradingHalted);
        Assert.Equal(200m, snap.DailyLossUsed);   // the gauge stays realized
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~HeldLossDailyLimitTests|FullyQualifiedName~SnapshotAggregatorTests"`
Expected: build FAILS with `'ComposableEngine' does not contain a definition for 'HeldOpenLoss'` and `'SnapshotAggregator.Inputs' does not contain a definition for 'HeldOpenLoss'`.

- [ ] **Step 3: Add `HeldOpenLoss` to the engine**

In `CRV.Core/Strategy/ComposableEngine.cs`, add directly after `HoldsPastSessionEnd` (Task 5):

```csharp
    /// <summary>
    /// Open loss (≤ 0) of positions opened on an earlier trading day, marked at the last price.
    /// It counts against today's daily loss limit. A position in profit counts as zero, so a held
    /// winner never hides a realized loss. One with no price yet (0 after a restart) also counts
    /// as zero rather than as a loss of its whole notional.
    /// </summary>
    internal decimal HeldOpenLoss(DateTime utcNow)
    {
        if (_brokerHandler == null) return 0m;

        var zone  = TimeZoneInfo.FindSystemTimeZoneById(_config.Timezone);
        var today = TradingDay.OfUtc(utcNow, zone, _config.SessionStartHour);
        decimal loss = 0m;
        foreach (var g in _brokerHandler.GetAllActiveGroups())
        {
            if (g.EntryPrice is null || g.Status is not (GroupOrderStatus.Active or GroupOrderStatus.PartialFilled)) continue;
            if (TradingDay.OfUtc(g.CreatedAt, zone, _config.SessionStartHour) >= today) continue;
            var price = _prices.GetLastPrice(g.Ticker);
            if (price <= 0) continue;
            loss += Math.Min(0m, _brokerHandler.GetUnrealizedPnl(g.SetupId, price));
        }
        return loss;
    }
```

- [ ] **Step 4: Feed it to both gates and the snapshot**

In `ComposableEngine.cs`:
- line 152: `if (Risk.DdBreached()) return;` → `if (Risk.DdBreached(HeldOpenLoss(utcTime))) return;`
- line 194: `if (!Risk.CanTrade(_config.UseDailyLossLimit, _config.MaxDailyLoss, _config.DailyLossMode))` → `if (!Risk.CanTrade(_config.UseDailyLossLimit, _config.MaxDailyLoss, _config.DailyLossMode, HeldOpenLoss(esig.Time)))`
- in `GetSnapshot`, inside `new SnapshotAggregator.Inputs { ... }`, after `DailyLossLimit = _config.MaxDailyLoss,` add `HeldOpenLoss = HeldOpenLoss(DateTime.UtcNow),`

In `CRV.Core/Strategy/SnapshotAggregator.cs`, after `public decimal DailyLossLimit { get; init; }` (line 49) add:

```csharp

        /// <summary>Open loss (≤ 0) of positions held in from an earlier trading day; counts toward TradingHalted.</summary>
        public decimal HeldOpenLoss { get; init; }
```

and change line 122 to `TradingHalted  = inputs.Risk.DdBreached(inputs.HeldOpenLoss),`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failures.

- [ ] **Step 6: Document it**

In `docs/risk.md`, change the table row on line 9 to:

```markdown
| Daily loss limit | Today's realised P&L plus the open loss of positions held in from an earlier day | `MaxDailyLoss`, `DailyLossMode` |
```

and insert before `## Position sizing across instruments`:

```markdown
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
```

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy CRV.Core.Tests docs/risk.md
git commit -m "feat(risk): a held position's open loss counts against the daily loss limit

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Restart recovery finds positions held overnight

**Files:**
- Create: `CRV.Core/Data/StrategyLogRecovery.cs`
- Modify: `CRV.Web/Services/LiveBrokerPersistence.cs:91-95`
- Test: `CRV.Core.Tests/Data/StrategyLogRecoveryTests.cs`

**Interfaces:**
- Produces: `static class StrategyLogRecovery { const int LookbackDays = 7; static IQueryable<StrategyLog> Recoverable(IQueryable<StrategyLog> logs, DateTime utcNow); }`.

- [ ] **Step 1: Write the failing test**

`CRV.Core.Tests/Data/StrategyLogRecoveryTests.cs`:

```csharp
using CRV.Core.Data;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// On restart, live recovery re-discovers every broker strategy that may still be open.
/// A position held over a weekend was placed days ago and must still be found.
/// </summary>
public class StrategyLogRecoveryTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public StrategyLogRecoveryTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static StrategyLog Log(string id, DateTime createdAt, bool completed) => new()
    {
        BrokerStrategyId = id, SetupId = "retest-mnq", Ticker = "MNQZ26", TotalContracts = 1,
        PointValue = 2m, CreatedAt = createdAt, IsCompleted = completed,
    };

    [Fact]
    public void Recoverable_IncludesAnOpenPositionHeldOverTheWeekend()
    {
        var monday = new DateTime(2026, 4, 20, 14, 0, 0, DateTimeKind.Utc);
        _db.StrategyLogs.AddRange(
            Log("1001", monday.AddHours(-1), completed: false),                                     // this morning
            Log("1002", new DateTime(2026, 4, 17, 19, 0, 0, DateTimeKind.Utc), completed: false),   // Friday, held
            Log("1003", new DateTime(2026, 4, 17, 15, 0, 0, DateTimeKind.Utc), completed: true),    // Friday, closed
            Log("1004", monday.AddDays(-30), completed: false));                                    // long gone
        _db.SaveChanges();

        var ids = StrategyLogRecovery.Recoverable(_db.StrategyLogs, monday)
            .Select(s => s.BrokerStrategyId).OrderBy(s => s).ToList();

        Assert.Equal(new[] { "1001", "1002" }, ids);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~StrategyLogRecoveryTests`
Expected: build FAILS with `The name 'StrategyLogRecovery' does not exist in the current context`.

- [ ] **Step 3: Write `StrategyLogRecovery`**

`CRV.Core/Data/StrategyLogRecovery.cs`:

```csharp
using CRV.Core.Models;

namespace CRV.Core.Data;

/// <summary>
/// Which broker strategies a restart tries to recover. A position held past session end can
/// stay open over a weekend or holiday, so recovery looks back a week rather than to midnight.
/// A row the broker reports finished is marked completed by recovery, so older rows drop out.
/// </summary>
public static class StrategyLogRecovery
{
    public const int LookbackDays = 7;

    public static IQueryable<StrategyLog> Recoverable(IQueryable<StrategyLog> logs, DateTime utcNow)
    {
        var since = utcNow.Date.AddDays(-LookbackDays);
        return logs.Where(s => !s.IsCompleted && s.CreatedAt >= since);
    }
}
```

- [ ] **Step 4: Use it in live recovery**

In `CRV.Web/Services/LiveBrokerPersistence.cs`, replace:

```csharp
            // Find uncompleted strategies from today
            var today = DateTime.UtcNow.Date;
            var logs = await db.StrategyLogs
                .Where(s => !s.IsCompleted && s.CreatedAt >= today)
                .ToListAsync(ct);
```

with:

```csharp
            // Uncompleted strategies recent enough to still be open, including positions held overnight
            var logs = await StrategyLogRecovery.Recoverable(db.StrategyLogs, DateTime.UtcNow)
                .ToListAsync(ct);
```

(`using CRV.Core.Data;` is already at the top of the file.)

- [ ] **Step 5: Run the tests and build the web project**

Run: `dotnet build CRV.Web && dotnet test CRV.Core.Tests`
Expected: build succeeds; tests PASS, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Data/StrategyLogRecovery.cs CRV.Web/Services/LiveBrokerPersistence.cs CRV.Core.Tests/Data/StrategyLogRecoveryTests.cs
git commit -m "fix(live): restart recovery finds a position held overnight

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: Daily stats roll on the trading date and see every exit

**Files:**
- Modify: `CRV.Web/Services/DailyStatsService.cs:82-123`
- Modify: `CRV.Web/Services/EmailNotificationService.cs:75-79, 172-192`
- Test: `CRV.Web.A11yTests/DailyStatsServiceTests.cs`

**Interfaces:**
- Consumes: `StrategyConfig.TradingDateOfUtc(DateTime)` (Task 7); `EngineSnapshot.TradingHalted` with held loss (Task 9).
- Produces: `void DailyStatsService.OnTradeClosed(TradeRecord r, StrategyConfig cfg)`.

- [ ] **Step 1: Write the failing test**

`CRV.Web.A11yTests/DailyStatsServiceTests.cs` (a plain unit test: no collection, no browser):

```csharp
using CRV.Core.Models;
using CRV.Web.Services;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>Today's stats follow the engine's trading day (18:00 ET roll), not the UTC date.</summary>
public class DailyStatsServiceTests
{
    private static readonly StrategyConfig Cfg = new() { Timezone = "America/New_York", SessionStartHour = 18, MaxDailyLoss = 500m };

    private static TradeRecord Closed(DateTime exitUtc, decimal net) => new()
    {
        SetupLabel = "retest-mnq", GrossPnl = net, NetPnl = net, EnteredAt = exitUtc.AddMinutes(-30), ExitedAt = exitUtc,
    };

    [Fact]
    public void TradesEitherSideOfUtcMidnight_InOneTradingDay_CountTogether()
    {
        var svc = new DailyStatsService();
        svc.OnTradeClosed(Closed(new DateTime(2026, 4, 15, 23, 0, 0, DateTimeKind.Utc), -300m), Cfg);   // 19:00 ET → the 16th
        svc.OnTradeClosed(Closed(new DateTime(2026, 4, 16, 1, 0, 0, DateTimeKind.Utc), -250m), Cfg);    // 21:00 ET → the 16th

        var s = svc.Get();
        Assert.Equal(new DateTime(2026, 4, 16), s.Date);
        Assert.Equal(2, s.TodayTrades);
        Assert.Equal(-550m, s.TodayNetPnL);
        Assert.True(s.DDBreached);
    }

    [Fact]
    public void ATradeAfterTheSessionRoll_StartsANewDay()
    {
        var svc = new DailyStatsService();
        svc.OnTradeClosed(Closed(new DateTime(2026, 4, 15, 21, 30, 0, DateTimeKind.Utc), -300m), Cfg);  // 17:30 ET → the 15th
        svc.OnTradeClosed(Closed(new DateTime(2026, 4, 15, 22, 30, 0, DateTimeKind.Utc), -100m), Cfg);  // 18:30 ET → the 16th

        var s = svc.Get();
        Assert.Equal(new DateTime(2026, 4, 16), s.Date);
        Assert.Equal(1, s.TodayTrades);
        Assert.Equal(-100m, s.TodayNetPnL);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test CRV.Web.A11yTests --filter FullyQualifiedName~DailyStatsServiceTests`
Expected: build FAILS with `Argument 2: cannot convert from 'CRV.Core.Models.StrategyConfig' to 'decimal'`.

- [ ] **Step 3: Roll on the trading date of the exit**

In `CRV.Web/Services/DailyStatsService.cs`, replace:

```csharp
    public void OnTradeClosed(TradeRecord r, decimal maxDailyLoss)
    {
        lock (_lock)
        {
            if (_stats.Date != DateTime.UtcNow.Date)
            {
                _stats = new DailyStats { Date = DateTime.UtcNow.Date };
            }
```

with:

```csharp
    /// <summary>Adds a closed trade to the trading day it closed on (the engine's day, rolling at the session start hour).</summary>
    public void OnTradeClosed(TradeRecord r, StrategyConfig cfg)
    {
        lock (_lock)
        {
            var day = cfg.TradingDateOfUtc(r.ExitedAt);
            if (_stats.Date != day)
            {
                _stats = new DailyStats { Date = day };
            }
```

and change line 121 to `_stats.DDBreached = _stats.TodayNetPnL <= -Math.Abs(cfg.MaxDailyLoss);`.

- [ ] **Step 4: Feed it every exit, and include held loss in the breach email**

In `CRV.Web/Services/EmailNotificationService.cs`, replace:

```csharp
    public Task OnExitAsync(TradeRecord trade)
    {
        var cfg = _cfgSvc.Current;
        if (!cfg.EmailEnabled || !cfg.EmailOnExit) return Task.CompletedTask;
```

with:

```csharp
    public Task OnExitAsync(TradeRecord trade)
    {
        var cfg = _cfgSvc.Current;
        _statsSvc.OnTradeClosed(trade, cfg);
        if (!cfg.EmailEnabled || !cfg.EmailOnExit) return Task.CompletedTask;
```

and in `OnSnapshotAsync`, replace:

```csharp
            var stats = _statsSvc.Get();
            if (stats.DDBreached && !_dailyLossBreachSent)
```

with:

```csharp
            var stats = _statsSvc.Get();
            // The engine's halt also counts the open loss of positions held in from an earlier day.
            bool breached = stats.DDBreached || snap.TradingHalted;
            if (breached && !_dailyLossBreachSent)
```

and `else if (!stats.DDBreached)` with `else if (!breached)`.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test CRV.Web.A11yTests --filter FullyQualifiedName~DailyStatsServiceTests`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add CRV.Web/Services/DailyStatsService.cs CRV.Web/Services/EmailNotificationService.cs CRV.Web.A11yTests/DailyStatsServiceTests.cs
git commit -m "fix(alerts): daily stats roll on the trading date and see every exit

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 12: Say it on the screens

Screens follow the approved EMA mockup; no new mockup is needed (brief). The footer is new content on an existing card, so it goes through the WCAG scan.

**Files:**
- Modify: `CRV.Core/Models/Signals.cs:265` (`SetupSnapshot`), `CRV.Core/Strategy/SnapshotAggregator.cs:269`
- Modify: `CRV.Web/Pages/Setup/Strategy.cshtml:188`, `CRV.Web/Pages/Setup/StrategyText.cs:59-60`
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml:1033-1072, 1466`, `CRV.Web/wwwroot/css/components.css:171`
- Modify (tests): `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs`, `CRV.Web.A11yTests/CockpitSnapshot.cs`, `CRV.Web.A11yTests/CockpitCardTests.cs`
- Test: `CRV.Web.A11yTests/StrategyTextTests.cs`

**Interfaces:**
- Consumes: `ISetupStrategy.CloseAtRthClose` (Task 2).
- Produces: `bool SetupSnapshot.CloseAtRthClose { get; set; } = true` (JSON `closeAtRthClose`); card element `#{cardId}-hold`; `CockpitSnapshot.Setup(..., bool closeAtRthClose = true)`.

- [ ] **Step 1: Write the failing tests**

Append inside `SnapshotAggregatorTests` (the stub's `CloseAtRthClose` setter is from Task 2):

```csharp
    [Fact]
    public void SetupSnapshot_SaysWhetherTheSetupHolds()
    {
        var holds  = new StubStrategy { Id = "H", SetupId = SetupId.F, CloseAtRthClose = false };
        var closes = new StubStrategy { Id = "C", SetupId = SetupId.F };

        var snap = SnapshotAggregator.Build(DefaultInputs(holds, closes));

        Assert.False(FindSetup(snap, "H").CloseAtRthClose);
        Assert.True(FindSetup(snap, "C").CloseAtRthClose);
    }
```

`CRV.Web.A11yTests/StrategyTextTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Web.Pages.Setup;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>The plain-words description says whether a setup closes at session end or holds.</summary>
public class StrategyTextTests
{
    private static BasketEntry Entry(bool close) => new()
    {
        Id = "retest-mnq", Label = "Retest [MNQ]", StrategyType = StrategyType.Retest, Ticker = "/MNQZ26",
        Config = new StrategySetupConfig { CloseAtRthClose = close },
    };

    [Fact]
    public void Describe_ClosingSetup_SaysItClosesAtSessionEnd()
        => Assert.EndsWith("Closes at the end of the session.", StrategyText.Describe(Entry(true)));

    [Fact]
    public void Describe_HoldingSetup_SaysItHoldsPastTheCutoff()
        => Assert.EndsWith("Holds an open trade past the cutoff.", StrategyText.Describe(Entry(false)));
}
```

In `CRV.Web.A11yTests/CockpitSnapshot.cs`, give `Setup` a `closeAtRthClose` parameter and make the short-trade card a holding one so the WCAG scan draws both footers:

```csharp
    public static SetupSnapshot Setup(string id, string label, string type, int state,
        bool pastCutoff = false, int tradeCount = 0, ActiveTradeView? trade = null, bool closeAtRthClose = true) => new()
    {
        Id = id, Label = label, StrategyType = type, Ticker = "/MNQZ26", PointValue = 2m, LastPrice = 21236.25m,
        Enabled = true, State = state, PastCutoff = pastCutoff, Trade = trade, TradeCount = trade == null ? tradeCount : 1,
        MaxTrades = 2, Wins = 1, Losses = 1, WinPnl = 140m, LossPnl = -40m, Expectancy = 50m,
        OrbHigh = 21250m, OrbLow = 21180m, OrbMid = 21215m, OrbRange = 70m, OrbFormed = true,
        CloseAtRthClose = closeAtRthClose,
    };
```

and in `EveryState()` change `Setup("a11y-short", "Session fakeout [MES]", "SessionFakeout", state: 0, trade: new ActiveTradeView` to `Setup("a11y-short", "Session fakeout [MES]", "SessionFakeout", state: 0, closeAtRthClose: false, trade: new ActiveTradeView`.

Append inside `CockpitCardTests`:

```csharp
    [Fact]
    public async Task Footer_SaysWhetherTheSetupClosesOrHolds()
    {
        await using var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, "/dashboard").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.EvaluateAsync("""
            json => {
                CRV.engine.status('Live');
                document.dispatchEvent(new CustomEvent('crv:update', { detail: JSON.parse(json) }));
            }
            """, CockpitSnapshot.Json([
                CockpitSnapshot.Setup("a11y-closes", "Pullback [MNQ]", "Pullback", state: 0),
                CockpitSnapshot.Setup("a11y-holds", "Retest [MNQ]", "Retest", state: 0, closeAtRthClose: false),
            ]));

        var closes = page.Locator("#a11y-closes-hold");
        await closes.WaitForAsync();
        Assert.Equal("closes at session end", (await closes.TextContentAsync())!.Trim());
        Assert.Equal("holds past cutoff", (await page.Locator("#a11y-holds-hold").TextContentAsync())!.Trim());
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter FullyQualifiedName~SnapshotAggregatorTests`
Expected: build FAILS with `'SetupSnapshot' does not contain a definition for 'CloseAtRthClose'`.

- [ ] **Step 3: Carry the flag on the snapshot**

In `CRV.Core/Models/Signals.cs`, `SetupSnapshot`, after `public bool     PastCutoff     { get; set; }` add:

```csharp
    /// <summary>True: closes at session end. False: holds an open trade past the cutoff.</summary>
    public bool     CloseAtRthClose { get; set; } = true;
```

In `CRV.Core/Strategy/SnapshotAggregator.cs`, in `snap.Setups.Add(new SetupSnapshot { ... })`, after `PastCutoff   = ss.PastCutoff,` add:

```csharp
                CloseAtRthClose = strategy.CloseAtRthClose,
```

Run: `dotnet test CRV.Core.Tests`
Expected: PASS, 0 failures.

- [ ] **Step 4: Setup page help and plain words**

`CRV.Web/Pages/Setup/Strategy.cshtml:188`, replace:

```cshtml
                @{ Check("Entry.Config.CloseAtRthClose", "Close at the end of the session", c.CloseAtRthClose); }
```

with:

```cshtml
                @{ Check("Entry.Config.CloseAtRthClose", "Close at the end of the session", c.CloseAtRthClose, "Off: the cutoff only stops new entries, and an open trade is held into the next session."); }
```

`CRV.Web/Pages/Setup/StrategyText.cs`, replace:

```csharp
        return $"{what} on {e.Ticker}, and {mode}. Stop is {stop}; target is {c.TargetPct}% of the range. {exits} {size} " +
               $"Up to {c.MaxTrades} trade{(c.MaxTrades == 1 ? "" : "s")} per session, in {Sessions(e)}.";
```

with:

```csharp
        var close = c.CloseAtRthClose ? "Closes at the end of the session." : "Holds an open trade past the cutoff.";

        return $"{what} on {e.Ticker}, and {mode}. Stop is {stop}; target is {c.TargetPct}% of the range. {exits} {size} " +
               $"Up to {c.MaxTrades} trade{(c.MaxTrades == 1 ? "" : "s")} per session, in {Sessions(e)}. {close}";
```

- [ ] **Step 5: Cockpit card footer**

In `CRV.Web/Pages/Dashboard/Index.cshtml`, directly above `function createSetupCard(setup) {` add:

```js
// What happens to an open trade at the cutoff and session end.
function _holdText(setup) { return setup.closeAtRthClose === false ? "holds past cutoff" : "closes at session end"; }

```

In `createSetupCard`, after the `ck-grid` block's closing `</div>` and before the closing backtick, add:

```js
        <footer class="ck-setup-f c-mut small" id="${id}-hold">${_holdText(setup)}</footer>
```

In `updateSetup`, after `setText(id + "-ticker", ticker || "");` add:

```js
    setText(id + "-hold", _holdText(setup));
```

In `CRV.Web/wwwroot/css/components.css`, after line 171 (`@media (max-width: 420px) { .ck-grid ... }`) add:

```css
.ck-setup-f { padding: 0 .9rem .6rem; }
```

- [ ] **Step 6: Run the web tests**

Run: `dotnet build CRV.Web.A11yTests && dotnet test CRV.Web.A11yTests`
Expected: PASS, 0 failures: 93 + 2 (DailyStats) + 2 (StrategyText) + 1 (footer) = **98**. The page scans now include both footers in both themes and widths.

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Models/Signals.cs CRV.Core/Strategy/SnapshotAggregator.cs CRV.Core.Tests CRV.Web CRV.Web.A11yTests
git commit -m "feat(ui): setup page, plain words and cockpit card say whether a setup closes or holds

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 13: Verify, review, PR

- [ ] **Step 1: Everything green**

Run: `dotnet build CRV.Trading.sln && dotnet test CRV.Core.Tests --no-build && dotnet test CRV.Web.A11yTests --no-build`
Expected: build with no errors. CRV.Core.Tests PASS, 0 failures (959 + this plan's tests). CRV.Web.A11yTests 98 PASS.

- [ ] **Step 2: Migration applies cleanly to a copy of a real DB**

Run, against a **copy** (never the live file):

```bash
cp "$DATA_DIR/crv_trading.db" /tmp/crv-hold-check.db
dotnet ef database update --project CRV.Core --startup-project CRV.Web --connection "Data Source=/tmp/crv-hold-check.db"
sqlite3 /tmp/crv-hold-check.db "select count(*) from Configs, json_each(Configs.BasketJson) e where json_extract(e.value,'$.Config.CloseAtRthClose') is not 1;"
```

Expected: `Done.`, then `0`. Skip this step if no local copy of a real DB exists, and say so in the PR.

- [ ] **Step 3: Security review**

Run the `security-review` skill on the branch. Fix findings, or record in the PR why each is left.

- [ ] **Step 4: Push and open the PR**

```bash
git push -u origin feat/hold-and-close
gh pr create --base master --title "feat(strategy): CloseAtRthClose — close at session end or hold into the next session" --body-file <(cat <<'EOF'
## What
- `CloseAtRthClose` works. On (default): close at the cutoff, when the session slot is off, and at session end, as before. Off: those only stop new entries; a filled position is held into the next session and exits on its own stop/targets/trail or Exit now. Unfilled entries are always cancelled.
- Strategy resets never clear `InTrade`.
- Daily loss limit counts the open loss of positions held in from an earlier trading day. A held winner counts as zero; a trip stops entries and closes nothing.
- Migration `DefaultCloseAtRthClose` sets every stored basket entry and legacy setup to close, so nothing holds until switched off.
- Backtest evaluates fills between sessions (no change for closing configs).
- Restart recovery looks back 7 days, so a held position is re-tracked.
- `DailyStatsService` rolls on the trading date and is fed every exit. **The daily-loss-breached email can now fire**; it never could before.
- Setup help text, plain-words description and cockpit card footer.

## Not done (see plan Decisions)
- Gap-through-stop fills at the stop level in backtest: optimistic across the daily halt and weekends for holding setups.
- `sessions.json` legacy A–D values are not migrated (file, not DB; used only with an empty basket).

## Tests
- Unit: CRV.Core.Tests all pass. A11y: 98 pass.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)
```

- [ ] **Step 5: Confirm CI is green**

Run: `gh pr checks --watch`
Expected: build/test and Accessibility jobs pass.

---

## Self-Review

**Spec coverage**

| Spec requirement | Task |
|---|---|
| `ISetupStrategy.CloseAtRthClose`, every strategy, Manual → true | 2 |
| Cutoff: cancel pending, disarm, block entries, keep position | 4 (+ pending cancel test) |
| Session end skips holding groups | 5 (engine, live path), 6 (backtest path) |
| Session reset keeps `InTrade`, group tracked | 3, 5 (`HeldPosition_StillExitsOnItsStop_NextSession`) |
| Next session: exits keep working, no new entry until closed | 5 (stop), 6 (backtest stop + no entry while held); Exit now is unchanged `ForceExitSetupAsync` → `ExitGroupAsync(Manual)` |
| Session-disabled slot follows the same rule | 4 |
| `InTrade` rule explicit and tested for every strategy; `CompleteGroup` clears | 3 |
| Defaults: migration (both baskets + legacy A–D); new strategies default true | 1 (sessions.json: Decision 9) |
| Daily loss = realized + held unrealized (earlier day in full) | 8, 9 (today's-position claim: Decision 1) |
| Realized books on the close day | unchanged `RecordTrade` on `OnTradeCompleted`; 11 for `DailyStatsService` |
| Trip stops entries, never force-closes held | 9 `ATrippedLimit_DoesNotCloseTheHeldPosition` |
| `DailyStatsService` rolls on trading date | 11 |
| UI: setup help, card footer, `StrategyText` | 12 |
| Files: `LiveEngineOrchestrator` | no change needed (Decision 12) |
| Tests 1–6 | 4/5/6 (1), 4 (2), 3 (3), 8/9 (4), 11 (5), 1 (6) |

**Placeholder scan:** No TBD/TODO; every code step has full code. The only variable is the generated migration timestamp `<ts>`, produced by the `dotnet ef` command in Task 1 Step 4.

**Type consistency:**
- `ExitAllAsync(..., Func<GroupOrder, ISetupStrategy, bool>? keep)` is defined in Task 5 and passed `HoldsPastSessionEnd(GroupOrder, ISetupStrategy)`.
- `DdBreached(decimal heldUnrealized = 0m)` and `CanTrade(..., decimal heldUnrealized = 0m)` (Task 8) are used in Task 9.
- `HeldOpenLoss(DateTime)` returns `decimal`; `Inputs.HeldOpenLoss` is `decimal`.
- `TradingDay.OfUtc(DateTime, TimeZoneInfo, int)` and `StrategyConfig.TradingDateOfUtc(DateTime)` (Task 7) are used in Tasks 9 and 11.
- `SetupSnapshot.CloseAtRthClose` and `CockpitSnapshot.Setup(..., closeAtRthClose)` are both defined in Task 12.
- The fake members `CloseAtRthClose { get; set; }` (Task 2) are used with object initializers in Tasks 4 and 12.

**Review Focus:** Each of the five lines has a named test in its owning task (Tasks 10, 6, 9, 9, 4/5).
