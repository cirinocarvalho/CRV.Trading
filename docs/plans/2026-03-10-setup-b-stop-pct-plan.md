# Setup B Stop Distance (% ORB) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a configurable `StopPctB` (decimal fraction, e.g. 0.50 = 50% of ORB range) to Setup B, replacing the hardcoded `orbMid` stop.

**Architecture:** New `StrategyConfig.StopPctB` property (default 0.50 preserves current behavior). `LevelCalculator.CalcLevelsB` signature changes: remove `orbMid`, add `stopPct` — stop computed as `entry ± orbRange * stopPct`. Two `OrbStrategyEngine` call sites updated. One UI input added to `Live.cshtml`. EF migration adds the DB column.

**Tech Stack:** C# / ASP.NET Core / EF Core SQLite / Razor Pages / xUnit

---

### Task 1: Add `StopPctB` to `StrategyConfig` with validation

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs` (lines 82-99 Setup B section, lines 186-200 Validate)
- Test: `CRV.Core.Tests/Models/StrategyConfigValidationTests.cs`

**Step 1: Write the failing test**

Open `CRV.Core.Tests/Models/StrategyConfigValidationTests.cs` and add two tests at the end of the class (before the closing `}`):

```csharp
[Fact]
public void Validate_EnableB_StopPctBZero_ReturnsError()
{
    var cfg = ValidConfig();
    cfg.EnableB   = true;
    cfg.StopPctB  = 0m;      // invalid: must be > 0
    var errors = cfg.Validate();
    Assert.Contains(errors, e => e.Contains("StopPctB"));
}

[Fact]
public void Validate_EnableB_StopPctBPositive_NoError()
{
    var cfg = ValidConfig();
    cfg.EnableB   = true;
    cfg.StopPctB  = 0.50m;   // valid
    var errors = cfg.Validate();
    Assert.DoesNotContain(errors, e => e.Contains("StopPctB"));
}
```

**Step 2: Run tests to confirm they fail**

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -c Release --filter "StopPctB" 2>&1 | tail -10
```

Expected: compile error — `StopPctB` does not exist yet.

**Step 3: Add the property to `StrategyConfig`**

In `CRV.Core/Models/StrategyConfig.cs`, after `EntryTickOffsetB` (line 98), add:

```csharp
/// <summary>
/// Stop distance for Setup B as a fraction of ORB range (default 0.50 = 50%).
/// Long: stop = entry - orbRange * StopPctB.
/// Short: stop = entry + orbRange * StopPctB.
/// Default 0.50 is identical to the legacy orbMid stop for a symmetric ORB.
/// </summary>
public decimal StopPctB { get; set; } = 0.50m;
```

**Step 4: Add validation rule**

In `Validate()`, inside the `if (EnableB)` block (around line 186), after the `EntryTickOffsetB` validation, add:

```csharp
if (StopPctB <= 0)
    errors.Add("StopPctB must be positive.");
```

**Step 5: Run tests to confirm they pass**

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -c Release --filter "StopPctB" 2>&1 | tail -10
```

Expected: 2 tests PASS.

**Step 6: Run full suite to confirm nothing broken**

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -c Release 2>&1 | tail -5
```

Expected: all tests pass (67 total: 65 + 2 new).

**Step 7: Commit**

```bash
git add CRV.Core/Models/StrategyConfig.cs CRV.Core.Tests/Models/StrategyConfigValidationTests.cs
git commit -m "feat: add StopPctB to StrategyConfig with validation (default 0.50)"
```

---

### Task 2: Update `CalcLevelsB` — remove `orbMid`, add `stopPct`

**Files:**
- Modify: `CRV.Core/Strategy/StrategyHelpers.cs` (lines 37-55, the `CalcLevelsB` method)
- Modify: `CRV.Core.Tests/Strategy/LevelCalculatorTests.cs` (lines 77-105, two existing tests + one new)

**Step 1: Update the two existing `CalcLevelsB` tests**

The current tests pass `orbMid` and assert `stop == orbMid`. With the new signature they must pass `stopPct` instead. In `LevelCalculatorTests.cs`, replace the entire `// ── Setup B ───` block (lines 77-105):

```csharp
// ── Setup B ───────────────────────────────────────────────

[Fact]
public void SetupB_Long_StopComputedFromEntryAndStopPct()
{
    // entry = orbHigh = 1000, orbRange = 100, stopPct = 0.50
    // stop = 1000 - 100 * 0.50 = 950 (same as old orbMid for symmetric ORB)
    var (stop, target, partial, rr) = LevelCalculator.CalcLevelsB(
        entry: 1000m, isLong: true,
        targetPct: 100, partialPct: 50,
        orbRange: OrbRange, stopPct: 0.50m);

    Assert.Equal(950m, stop);
    Assert.True(target > 1000m);
    Assert.True(rr > 0);
}

[Fact]
public void SetupB_Short_StopComputedFromEntryAndStopPct()
{
    // entry = orbLow = 1000, orbRange = 100, stopPct = 0.50
    // stop = 1000 + 100 * 0.50 = 1050 (same as old orbMid for symmetric ORB)
    var (stop, target, partial, rr) = LevelCalculator.CalcLevelsB(
        entry: 1000m, isLong: false,
        targetPct: 100, partialPct: 50,
        orbRange: OrbRange, stopPct: 0.50m);

    Assert.Equal(1050m, stop);
    Assert.True(target < 1000m);
    Assert.True(rr > 0);
}

[Fact]
public void SetupB_Long_CustomStopPct_StopNotAtOrbMid()
{
    // stopPct = 0.25 → stop = 1000 - 100 * 0.25 = 975 (not 950)
    var (stop, target, _, _) = LevelCalculator.CalcLevelsB(
        entry: 1000m, isLong: true,
        targetPct: 100, partialPct: 50,
        orbRange: OrbRange, stopPct: 0.25m);

    Assert.Equal(975m, stop);
    Assert.True(target > 1000m);
}
```

Note: `OrbRange` is a const defined near the top of the test class — check its value (it is `100m`).

**Step 2: Run tests to confirm they fail**

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -c Release --filter "SetupB" 2>&1 | tail -10
```

Expected: compile error — `CalcLevelsB` signature mismatch.

**Step 3: Rewrite `CalcLevelsB` in `StrategyHelpers.cs`**

Replace the entire `CalcLevelsB` method (lines 37-54) with:

```csharp
/// <summary>
/// Setup B — stop is entry-anchored at entry ± orbRange * stopPct (tick-rounded).
/// Default stopPct 0.50 gives the same stop as orbMid for a symmetric ORB.
/// </summary>
public static (decimal stop, decimal target, decimal partial, decimal rr)
    CalcLevelsB(decimal entry, bool isLong, int targetPct,
                int partialPct, decimal orbRange, decimal stopPct,
                decimal tickSize = 0m)
{
    decimal stopDist    = orbRange * stopPct;
    decimal targetDist  = orbRange * (targetPct  / 100m);
    decimal partialDist = targetDist * (partialPct / 100m);

    decimal stop    = RoundToTick(isLong ? entry - stopDist  : entry + stopDist,  tickSize);
    decimal target  = RoundToTick(isLong ? entry + targetDist : entry - targetDist, tickSize);
    decimal partial = RoundToTick(isLong ? entry + partialDist : entry - partialDist, tickSize);

    decimal risk   = Math.Abs(entry - stop);
    decimal reward = Math.Abs(target - entry);
    decimal rr     = risk > 0 ? reward / risk : 0;
    return (stop, target, partial, rr);
}
```

**Step 4: Run the Setup B tests**

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -c Release --filter "SetupB" 2>&1 | tail -10
```

Expected: 3 tests PASS (but full suite will fail because `OrbStrategyEngine` still passes `orbMid`).

**Step 5: Commit the calculator change only (tests + impl together)**

Do NOT commit yet — wait until `OrbStrategyEngine` compiles (Task 3). Skip to Task 3 now.

---

### Task 3: Update `OrbStrategyEngine` call sites

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`
  - `TryEntryB` (line 829): remove `orbMid` param, pass `_cfg.StopPctB`
  - `TryEntryBFromTick` (line 346): remove `_orb.OrbMid`, pass `_cfg.StopPctB`

**Step 1: Update `TryEntryB` signature and call**

At line 829, change the method signature from:
```csharp
private async Task TryEntryB(Bar bar, decimal ep, bool isLong, decimal orbRange, decimal orbMid)
```
to:
```csharp
private async Task TryEntryB(Bar bar, decimal ep, bool isLong, decimal orbRange)
```

At line 839-840, change:
```csharp
var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
    _cfg.TargetPctB, _cfg.PartialPctB, orbRange, orbMid, _cfg.TickSize);
```
to:
```csharp
var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
    _cfg.TargetPctB, _cfg.PartialPctB, orbRange, _cfg.StopPctB, _cfg.TickSize);
```

**Step 2: Update the four `TryEntryB` call sites**

At lines 745, 750, 765, 767 (inside `ProcessSetupB`), remove the `orbMid` argument:

```csharp
// Before:
await TryEntryB(bar, ep, true, orbRange, orbMid);
await TryEntryB(bar, ep, false, orbRange, orbMid);
await TryEntryB(bar, orbHigh, true, orbRange, orbMid);
await TryEntryB(bar, orbLow, false, orbRange, orbMid);

// After:
await TryEntryB(bar, ep, true, orbRange);
await TryEntryB(bar, ep, false, orbRange);
await TryEntryB(bar, orbHigh, true, orbRange);
await TryEntryB(bar, orbLow, false, orbRange);
```

Also remove the `decimal orbMid = _orb.OrbMid;` local at line 713 IF it is no longer used elsewhere in `ProcessSetupB`. Check first — `orbMid` is also used on lines 770-771 for the exit condition (`if (_stB == 2 && bar.Close < orbMid)`) — do NOT remove those references; they still reference `_orb.OrbMid` for the arm-state reset logic. Change line 713 from a local variable assignment to an inline reference on lines 770-771:

```csharp
// Before (line 713):
decimal orbMid   = _orb.OrbMid;
// ...
if (_stB == 2  && bar.Close < orbMid) _stB = 0;  // line 770
if (_stB == -2 && bar.Close > orbMid) _stB = 0;  // line 771

// After — remove local, use property directly:
if (_stB == 2  && bar.Close < _orb.OrbMid) _stB = 0;
if (_stB == -2 && bar.Close > _orb.OrbMid) _stB = 0;
```

Also check lines 983-987 (in `EvaluateArmState`): same pattern — replace `decimal orbMid = _orb.OrbMid;` + usages with `_orb.OrbMid` inline if `orbMid` has no other use in that block.

**Step 3: Update `TryEntryBFromTick`**

At line 353-354, change:
```csharp
var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
    _cfg.TargetPctB, _cfg.PartialPctB, _orb.OrbRange, _orb.OrbMid, _cfg.TickSize);
```
to:
```csharp
var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
    _cfg.TargetPctB, _cfg.PartialPctB, _orb.OrbRange, _cfg.StopPctB, _cfg.TickSize);
```

Also remove the inline comment on line 285 that says `Stop = orbMid (from CalcLevelsB)` — update it to reflect the new behavior:
```csharp
// Stop = entry ± orbRange * StopPctB (from CalcLevelsB).  Default 0.50 → orbMid equivalent.
```

**Step 4: Build to confirm no compile errors**

```bash
dotnet build CRV.Core/CRV.Core.csproj -c Release 2>&1 | tail -5
```

Expected: Build succeeded, 0 warnings.

**Step 5: Run full test suite**

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -c Release 2>&1 | tail -5
```

Expected: all 67 tests PASS.

**Step 6: Commit**

```bash
git add CRV.Core/Strategy/StrategyHelpers.cs \
        CRV.Core/Strategy/OrbStrategyEngine.cs \
        CRV.Core.Tests/Strategy/LevelCalculatorTests.cs
git commit -m "feat: replace CalcLevelsB orbMid stop with configurable StopPctB (default 0.50)"
```

---

### Task 4: Add `StopPctB` input to Live Settings UI

**Files:**
- Modify: `CRV.Web/Pages/Settings/Live.cshtml` (around line 437-445 — the Setup B `row g-2` with Retest Zone + Entry Tick Offset)

**Step 1: Add the input**

The current layout has a `row g-2` with two `col-6` cells: "Retest Zone (% ORB)" and "Entry Tick Offset". Change it to a `row g-2` with three `col-4` cells by adding `StopPctB` as the first field:

Replace (lines 437-446):
```html
<div class="row g-2 mt-1">
    <div class="col-6">
        <label class="form-label small">Retest Zone (% ORB)</label>
        <input name="Config.RetestPct" type="number" step="any" value="@Model.Config.RetestPct" class="form-control form-control-sm" />
    </div>
    <div class="col-6">
        <label class="form-label small">Entry Tick Offset</label>
        <input name="Config.EntryTickOffsetB" type="number" min="0" max="20" value="@Model.Config.EntryTickOffsetB" class="form-control form-control-sm" />
    </div>
</div>
```

With:
```html
<div class="row g-2 mt-1">
    <div class="col-4">
        <label class="form-label small">Stop Dist (% ORB)</label>
        <input name="Config.StopPctB" type="number" step="any" min="0.01" value="@Model.Config.StopPctB" class="form-control form-control-sm" />
    </div>
    <div class="col-4">
        <label class="form-label small">Retest Zone (% ORB)</label>
        <input name="Config.RetestPct" type="number" step="any" value="@Model.Config.RetestPct" class="form-control form-control-sm" />
    </div>
    <div class="col-4">
        <label class="form-label small">Entry Tick Offset</label>
        <input name="Config.EntryTickOffsetB" type="number" min="0" max="20" value="@Model.Config.EntryTickOffsetB" class="form-control form-control-sm" />
    </div>
</div>
```

**Step 2: Build the web project**

```bash
dotnet build CRV.Web/CRV.Web.csproj -c Release 2>&1 | tail -5
```

Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add CRV.Web/Pages/Settings/Live.cshtml
git commit -m "feat: add Stop Dist (% ORB) input to Setup B in Live Settings"
```

---

### Task 5: EF Core migration for `StopPctB` column

**Files:**
- Generate: `CRV.Core/Migrations/` (new migration files — auto-generated)
- Auto-update: `CRV.Core/Migrations/TradingDbContextModelSnapshot.cs`

**Step 1: Create the migration**

```bash
dotnet ef migrations add AddStopPctB \
    --project CRV.Core \
    --startup-project CRV.Web \
    2>&1 | tail -10
```

Expected output: `Build succeeded.` and `Done. To undo this action, use 'ef migrations remove'`

**Step 2: Verify the generated migration**

Check the generated file in `CRV.Core/Migrations/`. It should contain exactly:

```csharp
migrationBuilder.AddColumn<decimal>(
    name: "StopPctB",
    table: "Configs",
    type: "TEXT",
    nullable: false,
    defaultValue: 0.5m);
```

If the `defaultValue` is `0m` instead of `0.5m`, edit the generated migration file manually to set `defaultValue: 0.5m` — this ensures existing rows get the correct default.

**Step 3: Apply migration to local DB**

```bash
dotnet ef database update \
    --project CRV.Core \
    --startup-project CRV.Web \
    2>&1 | tail -5
```

Expected: `Done.`

**Step 4: Run full test suite one more time**

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -c Release 2>&1 | tail -5
```

Expected: all 67 tests PASS.

**Step 5: Commit migration**

```bash
git add CRV.Core/Migrations/
git commit -m "feat: EF migration AddStopPctB — Configs.StopPctB column (default 0.5)"
```

---

## Verification Checklist

After all tasks are done:

- [ ] `dotnet test` → 67 tests PASS
- [ ] `dotnet build CRV.Web` → 0 errors
- [ ] Navigate to `/settings/live` → "Stop Dist (% ORB)" input appears in Setup B section
- [ ] Save with value `0.25` → saved and reloaded correctly
- [ ] Run a backtest with Setup B enabled → stop levels are at 25% of ORB range from entry
- [ ] Run a backtest with default `0.50` → stop levels match previous orbMid behavior
