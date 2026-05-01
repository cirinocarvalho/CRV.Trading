# Auto-Size By Risk — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in per-setup `AutoSizeByRisk` flag that scales contracts up to fill the `MaxTradeRisk` budget — between `Contracts` (floor) and `MaxContracts` (ceiling) — and scales `PartialCts` proportionally. When enabled, `HiVolMult` is ignored. Trades that exceed budget at the floor still skip (preserving today's veto).

**Architecture:** Single source of truth lives in each strategy's `CalcContracts(decimal ep, decimal sl)`. New runtime field `StrategySetupConfig.AutoSizeByRisk`, persisted via `StrategyConfig.AutoSizeByRiskA/B/C/D` columns and surfaced through `SessionConfig.SetupConfigBase.AutoSizeByRisk`. UI toggle added to the shared `_SetupConfigSection.cshtml` partial and the dynamic basket-entry editors in `Settings/Live.cshtml`. Backtest and Prospectus inherit automatically because they share the engine.

**Tech Stack:** C# / .NET 9, EF Core 9 (SQLite), ASP.NET Core Razor Pages, xUnit.

**Sizing formula (when `AutoSizeByRisk == true && MaxTradeRisk > 0`):**
```
riskPerCt  = |ep − sl| × PointValue
budgetCts  = floor(MaxTradeRisk / riskPerCt)            (riskPerCt > 0)
if budgetCts < Contracts → return 0  (caller treats as skip)
contracts  = min(budgetCts, MaxContracts)
```

**PartialCts scaling** (only when `_cfg.PartialCts > 0` and `contracts > _cfg.Contracts`):
```
scaledPartial = round(_cfg.PartialCts × contracts / _cfg.Contracts)
scaledPartial = clamp(scaledPartial, 1, contracts − 1)
```
If `_cfg.PartialCts == 0` (auto = 50%) leave it at 0 — downstream trade manager handles auto.

---

## File Structure

**Core models (CRV.Core/Models)**
- `StrategySetupConfig.cs` — runtime per-setup type. Add `bool AutoSizeByRisk`.
- `StrategyConfig.cs` — persisted EF entity. Add `AutoSizeByRiskA/B/C/D`. Wire into `ToSetupConfigs()` mappers (4 places).
- `SessionConfig.cs` — UI binding type. Add `AutoSizeByRisk` to `SetupConfigBase`. Wire round-trip in `WriteToConfig` (4 places) and `FromConfig` (4 places).

**Migration (CRV.Core/Migrations)**
- New EF migration: `AddAutoSizeByRisk` adding 4 boolean columns with default `0`.

**Strategies (CRV.Core/Strategy)** — each one gets the same surgical change:
- `PullbackStrategy.cs` — `CalcContracts()` → `CalcContracts(decimal ep, decimal sl)`, scale partial.
- `RetestStrategy.cs` — same.
- `SessionFakeoutStrategy.cs` — same.
- `OrbFakeoutStrategy.cs` — same.
- `Ema21Strategy.cs` — inline sizing block (no `CalcContracts` helper today). Add helper for consistency, scale partial.

**UI (CRV.Web/Pages)**
- `Shared/_SetupConfigSection.cshtml` — checkbox next to `MaxContracts`.
- `Settings/Live.cshtml` — add field to:
  - Setup-config defaults object (`Config: { … }`) — line ~1056.
  - Backtest basket-entry builder (`_eField`) — line ~1546.
  - Live basket-entry builder (`_bField`) — line ~1237.
  - Default object for new entries — line ~1414.

**Tests (CRV.Core.Tests)**
- `Models/StrategyConfigTests.cs` — extend existing fixture builder.
- `Models/ConfigMappingTests.cs` — assert round-trip of new field.
- `Strategy/AutoSizeByRiskTests.cs` (new) — drive the strategy logic with TDD.

---

## Task 1: Add `AutoSizeByRisk` field to `StrategySetupConfig`

**Files:**
- Modify: `CRV.Core/Models/StrategySetupConfig.cs:111-112`

- [ ] **Step 1: Add the property next to `MaxTradeRisk`**

In `CRV.Core/Models/StrategySetupConfig.cs`, immediately after the `MaxTradeRisk` property (line 112), insert:

```csharp
    /// <summary>When true and <see cref="MaxTradeRisk"/> &gt; 0, contracts auto-scale between
    /// <see cref="Contracts"/> (floor) and <see cref="MaxContracts"/> (ceiling) to consume the
    /// risk budget. <see cref="HiVolMult"/> is ignored in this mode. When the floor's risk
    /// already exceeds the budget, the trade is skipped (same as the legacy veto).</summary>
    public bool AutoSizeByRisk { get; set; }
```

- [ ] **Step 2: Build to confirm the model compiles**

Run: `dotnet build CRV.Core/CRV.Core.csproj -nologo`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Models/StrategySetupConfig.cs
git commit -m "feat(config): add AutoSizeByRisk per-setup field"
```

---

## Task 2: Add per-setup persisted columns to `StrategyConfig`

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs` (add 4 properties + wire into 4 `ToSetupConfigs` blocks)

- [ ] **Step 1: Add `AutoSizeByRiskA/B/C/D` properties**

For each setup A/B/C/D, add a property next to the matching `MaxTradeRisk{X}` in `StrategyConfig.cs`:

After line 203 (`public decimal MaxTradeRiskA { get; set; } = 0m;`):
```csharp
    public bool    AutoSizeByRiskA   { get; set; } = false;
```

After line 251 (`public decimal MaxTradeRiskB { get; set; } = 0m;`):
```csharp
    public bool    AutoSizeByRiskB   { get; set; } = false;
```

After line 293 (`public decimal MaxTradeRiskC { get; set; } = 0m;`):
```csharp
    public bool    AutoSizeByRiskC   { get; set; } = false;
```

After line 323 (`public decimal MaxTradeRiskD { get; set; } = 0m;`):
```csharp
    public bool    AutoSizeByRiskD   { get; set; } = false;
```

- [ ] **Step 2: Wire `AutoSizeByRisk` into the four `ToSetupConfigs` setup builders**

`StrategyConfig.cs` has four blocks that materialize a `StrategySetupConfig` (one per setup, around lines 637, 661, 685, 708). Each block sets `MaxTradeRisk = MaxTradeRisk{X}` — add the new field on the line below.

Setup A block (after `MaxTradeRisk = MaxTradeRiskA,` near line 648):
```csharp
        AutoSizeByRisk = AutoSizeByRiskA,
```

Setup B block (after `MaxTradeRisk = MaxTradeRiskB,` near line 672):
```csharp
        AutoSizeByRisk = AutoSizeByRiskB,
```

Setup C block (after `MaxTradeRisk = MaxTradeRiskC,` near line 695):
```csharp
        AutoSizeByRisk = AutoSizeByRiskC,
```

Setup D block (after `MaxTradeRisk = MaxTradeRiskD,` near line 718):
```csharp
        AutoSizeByRisk = AutoSizeByRiskD,
```

- [ ] **Step 3: Build**

Run: `dotnet build CRV.Core/CRV.Core.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Models/StrategyConfig.cs
git commit -m "feat(config): persist AutoSizeByRiskA/B/C/D columns"
```

---

## Task 3: Add EF migration for the 4 new columns

**Files:**
- Create: `CRV.Core/Migrations/<timestamp>_AddAutoSizeByRisk.cs` (generated)
- Generated update to `CRV.Core/Migrations/TradingDbContextModelSnapshot.cs`

- [ ] **Step 1: Generate the migration**

Run from repo root:
```bash
dotnet ef migrations add AddAutoSizeByRisk --project CRV.Core --startup-project CRV.Web --no-build
```
Expected: a new migration file appears under `CRV.Core/Migrations/` adding 4 `bool` columns with default `false` to the `Configs` table.

- [ ] **Step 2: Inspect the generated `Up` block**

Open the new migration file and confirm `Up()` contains four `AddColumn<bool>` calls for `AutoSizeByRiskA`, `AutoSizeByRiskB`, `AutoSizeByRiskC`, `AutoSizeByRiskD`, each with `defaultValue: false, nullable: false`. Confirm `Down()` drops them.

- [ ] **Step 3: Apply the migration to a throwaway DB**

Run from repo root:
```bash
dotnet ef database update --project CRV.Core --startup-project CRV.Web
```
Expected: "Done." with no errors.

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Migrations
git commit -m "feat(migration): AddAutoSizeByRisk for per-setup risk auto-sizing"
```

---

## Task 4: Add `AutoSizeByRisk` to `SetupConfigBase` and round-trip mappers

**Files:**
- Modify: `CRV.Core/Models/SessionConfig.cs:30` (base property), and 8 mapping spots (4 in `WriteToConfig`, 4 in `FromConfig`).

- [ ] **Step 1: Add the property to `SetupConfigBase`**

In `SessionConfig.cs`, immediately after line 30 (`public int MaxContracts { get; set; } = 2;`), insert:

```csharp
    public bool    AutoSizeByRisk     { get; set; } = false;
```

- [ ] **Step 2: Wire into `WriteToConfig` (4 places)**

Each block (around lines 160, 197, 230, 258) contains `c.MaxTradeRisk{X} = Setup{X}.MaxTradeRisk;`. Add a line below it:

```csharp
        c.AutoSizeByRiskA = SetupA.AutoSizeByRisk;
```
(repeat for B, C, D in their respective blocks)

- [ ] **Step 3: Wire into `FromConfig` (4 places)**

Each setup block (around lines 313, 350, 384, 414) initializes a `SetupConfig{X}` object literal that includes `MaxTradeRisk = cfg.MaxTradeRisk{X},`. Add a line below it:

```csharp
            AutoSizeByRisk    = cfg.AutoSizeByRiskA,
```
(repeat for B, C, D)

- [ ] **Step 4: Build**

Run: `dotnet build CRV.Core/CRV.Core.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Models/SessionConfig.cs
git commit -m "feat(config): round-trip AutoSizeByRisk through SetupConfigBase"
```

---

## Task 5: Round-trip mapping test

**Files:**
- Modify: `CRV.Core.Tests/Models/ConfigMappingTests.cs`

- [ ] **Step 1: Write the failing assertion**

Open `CRV.Core.Tests/Models/ConfigMappingTests.cs`. Find the fixture that builds a `StrategyConfig` with all per-setup overrides set (around lines 70–145) and add a non-default `AutoSizeByRisk{X} = true` for each setup A/B/C/D in that fixture.

Then in the assertion block (around line 345 — `Assert.Equal(cfg.MaxContractsA, a.MaxContracts);`) add for each setup:

```csharp
        Assert.Equal(cfg.AutoSizeByRiskA, a.AutoSizeByRisk);
        Assert.Equal(cfg.AutoSizeByRiskB, b.AutoSizeByRisk);
        Assert.Equal(cfg.AutoSizeByRiskC, c.AutoSizeByRisk);
        Assert.Equal(cfg.AutoSizeByRiskD, d.AutoSizeByRisk);
```

Also extend the equality helper at line ~456 (`Assert.Equal(expected.MaxContracts, actual.MaxContracts);`) with:

```csharp
        Assert.Equal(expected.AutoSizeByRisk, actual.AutoSizeByRisk);
```

- [ ] **Step 2: Run the test (still passes if fixture defaults match)**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~ConfigMapping" -nologo`
Expected: PASS — verifies the round-trip wiring done in Tasks 2 and 4.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core.Tests/Models/ConfigMappingTests.cs
git commit -m "test(config): assert AutoSizeByRisk round-trips through mappers"
```

---

## Task 6: TDD the sizing logic for `PullbackStrategy`

**Files:**
- Create: `CRV.Core.Tests/Strategy/AutoSizeByRiskTests.cs`
- Modify: `CRV.Core/Strategy/PullbackStrategy.cs:382, 408-415`

- [ ] **Step 1: Write the failing tests**

Create `CRV.Core.Tests/Strategy/AutoSizeByRiskTests.cs`:

```csharp
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class AutoSizeByRiskTests
{
    // Helper: directly exercise the sizing math via a strategy's CalcContracts.
    // We use PullbackStrategy as the canonical implementation; identical logic
    // is duplicated across the other 4 strategies and is covered by their own tests.
    private static StrategySetupConfig BaseCfg() => new()
    {
        Id = "A", Name = "A", SetupId = SetupId.A,
        StrategyType = StrategyType.Pullback,
        Ticker = "NQH26", PointValue = 20m, TickSize = 0.25m,
        Contracts = 2, MaxContracts = 6, HiVolMult = 1.0m,
        MaxTradeRisk = 500m, AutoSizeByRisk = true,
        PartialCts = 1,
    };

    [Fact]
    public void AutoSize_WithinBudget_ScalesUpToBudgetCap()
    {
        // riskPerCt = |100 - 95| * 20 = 100. Budget = 500/100 = 5. Cap = MaxContracts (6).
        // Expect 5 contracts.
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(5, cts);
        // Partial scaled: round(1 * 5 / 2) = 3 (clamped to cts-1 = 4).
        Assert.Equal(3, partial);
    }

    [Fact]
    public void AutoSize_BudgetCtsBelowFloor_ReturnsZeroToSignalSkip()
    {
        // riskPerCt = |100 - 80| * 20 = 400. Budget = 500/400 = 1. Floor = Contracts (2).
        // budgetCts (1) < Contracts (2) ⇒ skip.
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 80m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(0, cts);
    }

    [Fact]
    public void AutoSize_BudgetExceedsMaxContracts_ClampsToMaxContracts()
    {
        // riskPerCt = |100 - 99.5| * 20 = 10. Budget = 500/10 = 50. Cap to MaxContracts (6).
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 99.5m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(6, cts);
        // Partial: round(1 * 6 / 2) = 3, clamped to cts-1 = 5 ⇒ 3.
        Assert.Equal(3, partial);
    }

    [Fact]
    public void AutoSizeOff_FallsBackToHiVolMultThenMaxContractsClamp()
    {
        var cfg = BaseCfg();
        cfg.AutoSizeByRisk = false;
        cfg.HiVolMult = 2.0m;
        // High vol: 2 * 2.0 = 4. Cap to MaxContracts (6) ⇒ 4. PartialCts unchanged (1).
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: cfg, atrRatio: 1.0m);
        Assert.Equal(4, cts);
        Assert.Equal(1, partial);
    }

    [Fact]
    public void AutoSizeOff_ClampsToMaxContracts()
    {
        var cfg = BaseCfg();
        cfg.AutoSizeByRisk = false;
        cfg.HiVolMult = 5.0m;
        cfg.MaxContracts = 3;
        // High vol: 2 * 5.0 = 10, clamped to MaxContracts (3).
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: cfg, atrRatio: 1.0m);
        Assert.Equal(3, cts);
    }

    [Fact]
    public void AutoSize_PartialCtsZero_StaysZeroForAutoMode()
    {
        var cfg = BaseCfg();
        cfg.PartialCts = 0; // 0 means auto/50% — handled downstream
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: cfg, atrRatio: 0m);
        Assert.Equal(5, cts);
        Assert.Equal(0, partial);
    }

    [Fact]
    public void AutoSize_DisabledWhenMaxTradeRiskZero()
    {
        var cfg = BaseCfg();
        cfg.MaxTradeRisk = 0m;       // disabled — autosize must no-op
        cfg.HiVolMult = 1.0m;
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: cfg, atrRatio: 0m);
        Assert.Equal(2, cts);        // falls back to plain Contracts
    }
}
```

- [ ] **Step 2: Run — expect compile failure**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~AutoSizeByRisk" -nologo`
Expected: build error — `AutoSizeByRiskCalculator` does not exist.

- [ ] **Step 3: Implement the calculator**

Create `CRV.Core/Strategy/AutoSizeByRiskCalculator.cs`:

```csharp
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Shared sizing helper used by every strategy. Computes (contracts, partialContracts)
/// honoring AutoSizeByRisk + MaxTradeRisk + HiVolMult + MaxContracts. Returning
/// contracts == 0 means "skip" (risk floor exceeds the budget).
/// </summary>
internal static class AutoSizeByRiskCalculator
{
    public static (int contracts, int partial) Calc(
        decimal ep, decimal sl, StrategySetupConfig cfg, decimal atrRatio)
    {
        int contracts;

        if (cfg.AutoSizeByRisk && cfg.MaxTradeRisk > 0)
        {
            decimal riskPerCt = System.Math.Abs(ep - sl) * cfg.PointValue;
            if (riskPerCt <= 0) return (0, 0);

            int budgetCts = (int)System.Math.Floor(cfg.MaxTradeRisk / riskPerCt);
            if (budgetCts < cfg.Contracts) return (0, 0);  // signals caller to skip

            contracts = System.Math.Min(budgetCts, cfg.MaxContracts);
        }
        else
        {
            bool isHighVol = atrRatio >= 1.0m;
            int cts = isHighVol
                ? (int)System.Math.Round(cfg.Contracts * cfg.HiVolMult)
                : cfg.Contracts;
            contracts = System.Math.Min(cts, cfg.MaxContracts);
        }

        int partial = cfg.PartialCts;
        if (partial > 0 && contracts > cfg.Contracts && cfg.Contracts > 0)
        {
            partial = (int)System.Math.Round((decimal)cfg.PartialCts * contracts / cfg.Contracts);
            if (partial < 1) partial = 1;
            if (partial > contracts - 1) partial = contracts - 1;
        }

        return (contracts, partial);
    }
}
```

- [ ] **Step 4: Run the tests — all should pass**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~AutoSizeByRisk" -nologo`
Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/AutoSizeByRiskCalculator.cs CRV.Core.Tests/Strategy/AutoSizeByRiskTests.cs
git commit -m "feat(strategy): AutoSizeByRiskCalculator with full TDD coverage"
```

---

## Task 7: Wire `AutoSizeByRiskCalculator` into `PullbackStrategy`

**Files:**
- Modify: `CRV.Core/Strategy/PullbackStrategy.cs:382-414`

- [ ] **Step 1: Replace the inline `CalcContracts()` call site and helper**

In `PullbackStrategy.cs`, replace **line 382**:
```csharp
        int contracts = CalcContracts();
```
with:
```csharp
        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0) return; // floor risk exceeds budget — skip
```

In the same file, **delete the entire `CalcContracts()` method** (lines 408–415 in the current file).

In **line 396** (the `EntrySignal(...)` constructor block), replace:
```csharp
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
```
with:
```csharp
            PartialContracts: scaledPartial, PointValue: _cfg.PointValue,
```

- [ ] **Step 2: Build**

Run: `dotnet build CRV.Core/CRV.Core.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 3: Run the existing PullbackStrategy tests — must still pass**

Run: `dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~PullbackStrategy" -nologo`
Expected: all green. (`AutoSizeByRisk` defaults to false so behavior is unchanged.)

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Strategy/PullbackStrategy.cs
git commit -m "feat(strategy): PullbackStrategy uses AutoSizeByRiskCalculator"
```

---

## Task 8: Wire into `RetestStrategy`

**Files:**
- Modify: `CRV.Core/Strategy/RetestStrategy.cs:587-622`

- [ ] **Step 1: Replace call site and helper**

Replace **line 587** (`int contracts = CalcContracts();`) with:
```csharp
        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0) return;
```

Delete the `private int CalcContracts()` method around line 614.

In the `EntrySignal(...)` block at line 601, replace `PartialContracts: _cfg.PartialCts` with `PartialContracts: scaledPartial`.

- [ ] **Step 2: Build + test**

Run: `dotnet build CRV.Core/CRV.Core.csproj -nologo && dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~RetestStrategy" -nologo`
Expected: 0 build errors, all tests green.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Strategy/RetestStrategy.cs
git commit -m "feat(strategy): RetestStrategy uses AutoSizeByRiskCalculator"
```

---

## Task 9: Wire into `SessionFakeoutStrategy`

**Files:**
- Modify: `CRV.Core/Strategy/SessionFakeoutStrategy.cs:318-352`

- [ ] **Step 1: Replace call site and helper**

Replace **line 318** with:
```csharp
        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0) return;
```

Delete `private int CalcContracts()` around line 343.

In the `EntrySignal(...)` block at line 332, replace `PartialContracts: _cfg.PartialCts` with `PartialContracts: scaledPartial`.

- [ ] **Step 2: Build + test**

Run: `dotnet build CRV.Core/CRV.Core.csproj -nologo && dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~SessionFakeout" -nologo`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Strategy/SessionFakeoutStrategy.cs
git commit -m "feat(strategy): SessionFakeoutStrategy uses AutoSizeByRiskCalculator"
```

---

## Task 10: Wire into `OrbFakeoutStrategy`

**Files:**
- Modify: `CRV.Core/Strategy/OrbFakeoutStrategy.cs:307-341`

- [ ] **Step 1: Replace call site and helper**

Replace **line 307** with:
```csharp
        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, _lastAtrRatio);
        if (contracts <= 0) return;
```

Delete `private int CalcContracts()` around line 332.

In the `EntrySignal(...)` block at line 321, replace `PartialContracts: _cfg.PartialCts` with `PartialContracts: scaledPartial`.

- [ ] **Step 2: Build + test**

Run: `dotnet build CRV.Core/CRV.Core.csproj -nologo && dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~OrbFakeout" -nologo`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Strategy/OrbFakeoutStrategy.cs
git commit -m "feat(strategy): OrbFakeoutStrategy uses AutoSizeByRiskCalculator"
```

---

## Task 11: Wire into `Ema21Strategy` (no helper today — inline block)

**Files:**
- Modify: `CRV.Core/Strategy/Ema21Strategy.cs:417-441`

- [ ] **Step 1: Replace the contract-sizing + risk-veto block**

In `Ema21Strategy.cs`, replace **lines 426–434** (the `// Contract sizing` + `// Max trade risk filter` block) with:

```csharp
        // Contract sizing (AutoSizeByRisk-aware)
        var (contracts, scaledPartial) = AutoSizeByRiskCalculator.Calc(ep, sl, _cfg, atrRatio: 0m);
        if (contracts <= 0) { _state = 0; return; }
```

Then in the `EntrySignal(...)` block at line 441, replace `PartialContracts: _cfg.PartialCts` with `PartialContracts: scaledPartial`.

> **Note:** EMA21 strategy does not track ATR-ratio sizing today (ignores HiVolMult). Passing `atrRatio: 0m` keeps the no-AutoSize fallback at plain `Contracts` clamped by `MaxContracts` — exactly the current behavior.

- [ ] **Step 2: Build + test**

Run: `dotnet build CRV.Core/CRV.Core.csproj -nologo && dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj --filter "FullyQualifiedName~Ema21" -nologo`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Strategy/Ema21Strategy.cs
git commit -m "feat(strategy): Ema21Strategy uses AutoSizeByRiskCalculator"
```

---

## Task 12: Full test sweep before touching UI

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test -nologo`
Expected: all green. If anything red is unrelated, capture the failure name and continue. If red is related, stop and fix.

- [ ] **Step 2: Commit (no-op if nothing changed)**

If you needed any fix to keep tests green, commit it as `fix(strategy): <description>`.

---

## Task 13: UI — shared `_SetupConfigSection.cshtml`

**Files:**
- Modify: `CRV.Web/Pages/Shared/_SetupConfigSection.cshtml` (around the `MaxContracts` input at line 132)

- [ ] **Step 1: Add the checkbox right after the `MaxContracts` input**

Open `_SetupConfigSection.cshtml`. Find the `<input … data-field="@(fieldPfx).MaxContracts" …>` near line 132 and add **immediately after** that input element:

```cshtml
        <label class="ms-2 small">
            <input type="checkbox"
                   class="form-check-input live-config-input"
                   data-session="@i" data-field="@(fieldPfx).AutoSizeByRisk"
                   @(setup.AutoSizeByRisk ? "checked" : "") />
            Auto-size by risk
        </label>
```

> **Note:** Keep the same `live-config-input` class and `data-session`/`data-field` pattern that surrounding inputs use — that's what the page-level JavaScript binds to. No additional JS changes needed.

- [ ] **Step 2: Smoke-build the web project**

Run: `dotnet build CRV.Web/CRV.Web.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add CRV.Web/Pages/Shared/_SetupConfigSection.cshtml
git commit -m "feat(ui): expose AutoSizeByRisk in shared setup config partial"
```

---

## Task 14: UI — `Settings/Live.cshtml` defaults & basket builders

**Files:**
- Modify: `CRV.Web/Pages/Settings/Live.cshtml` lines ~1056, ~1237, ~1414, ~1546

- [ ] **Step 1: Add `AutoSizeByRisk: false` to the default Setup-Config object**

In `Settings/Live.cshtml` line 1056, the literal:
```js
Config: { MaxTrades: 5, CutoffHour: 14, CutoffMinute: 30, Contracts: 2, MaxContracts: 2, HiVolMult: 1, …
```
becomes (insert immediately after `MaxContracts: 2,`):
```js
Config: { MaxTrades: 5, CutoffHour: 14, CutoffMinute: 30, Contracts: 2, MaxContracts: 2, AutoSizeByRisk: false, HiVolMult: 1, …
```

- [ ] **Step 2: Add a checkbox row to the live basket-entry builder (`_bField`)**

At line 1237 the live editor renders fields like:
```js
_bField(entry.Id, 'MaxContracts', 'number', cfg.MaxContracts || cfg.Contracts || 2, 80, 'Max Cts', 'int') +
```
Add this line **immediately below**:
```js
_bField(entry.Id, 'AutoSizeByRisk', 'checkbox', cfg.AutoSizeByRisk === true, 0, 'Auto-size by risk', 'bool') +
```

- [ ] **Step 3: Add to the new-entry default object**

At line 1414 the literal:
```js
Contracts: 2, MaxContracts: 2, MaxTrades: 5, MinRr: 1.0,
```
becomes:
```js
Contracts: 2, MaxContracts: 2, AutoSizeByRisk: false, MaxTrades: 5, MinRr: 1.0,
```

- [ ] **Step 4: Add a checkbox row to the backtest entry builder (`_eField`)**

At line 1546:
```js
_eField(entry.Id, 'MaxContracts', 'number', cfg.MaxContracts || 2, 80, 'Max Cts', 'int') +
```
Add **immediately below**:
```js
_eField(entry.Id, 'AutoSizeByRisk', 'checkbox', cfg.AutoSizeByRisk === true, 0, 'Auto-size by risk', 'bool') +
```

- [ ] **Step 5: Verify `_bField`/`_eField` already support `'checkbox'`**

In the same file, search for `function _bField` and `function _eField`. Confirm both have a branch handling `kind === 'checkbox'` that emits `<input type="checkbox" …>` and binds the value as boolean. If a checkbox branch is missing, add it inline using the same DOM pattern those helpers already use for boolean fields like `UseVwap` (search for `'UseVwap'` to find a known-good example).

- [ ] **Step 6: Manually launch the dev server and verify the toggle round-trips**

Run: `dotnet run --project CRV.Web -nologo` (background)
Then:
- Browse to `/Settings/Live`, expand any setup, toggle "Auto-size by risk" on, save.
- Restart the page, confirm the toggle persists.
- Repeat the same for a basket entry in the dynamic editor.

If `mcp__Claude_Preview__preview_*` tools are available, automate this step instead.

- [ ] **Step 7: Commit**

```bash
git add CRV.Web/Pages/Settings/Live.cshtml
git commit -m "feat(ui): expose AutoSizeByRisk in live + backtest config editors"
```

---

## Task 15: UI — Backtest settings page (if separate form)

**Files:**
- Modify: `CRV.Web/Pages/Settings/Backtest.cshtml` (if it has its own per-setup form)

- [ ] **Step 1: Confirm whether Backtest settings has a separate per-setup editor**

Run: `grep -n "MaxContracts\|MaxTradeRisk" CRV.Web/Pages/Settings/Backtest.cshtml 2>/dev/null`

- [ ] **Step 2a: If grep returns hits**

Add the same checkbox pattern from Task 13 next to the `MaxContracts` input on that page.

- [ ] **Step 2b: If grep returns nothing**

Backtest reuses the shared `_SetupConfigSection.cshtml` partial — no further work needed. Skip to step 3.

- [ ] **Step 3: Commit (only if a change was made)**

```bash
git add CRV.Web/Pages/Settings/Backtest.cshtml
git commit -m "feat(ui): expose AutoSizeByRisk in backtest settings"
```

---

## Task 16: Final verification

- [ ] **Step 1: Build everything**

Run: `dotnet build -nologo`
Expected: 0 errors, 0 warnings related to new code.

- [ ] **Step 2: Full test suite**

Run: `dotnet test -nologo`
Expected: all green.

- [ ] **Step 3: End-to-end smoke**

- Start CRV.Web, configure one setup with `Contracts=2, MaxContracts=6, MaxTradeRisk=500, AutoSizeByRisk=true, HiVolMult=99` (deliberately absurd to prove HiVolMult is ignored).
- Run a backtest over a known day; in the trade log, verify the `Contracts` column shows values between 2 and 6 governed by stop distance, never the 99×2 = 198 that HiVolMult would have produced.
- Repeat with `MaxTradeRisk=10` (deliberately tight) and confirm: trades with `|ep - sl| × 20 × 2 > 10` are skipped (no rows in trade log for those signals).

- [ ] **Step 4: Final commit (if any UI tweaks were needed)**

```bash
git status
# commit any remaining files with a descriptive message
```

---

## Self-Review

- **Spec coverage:** AutoSizeByRisk flag (Task 1) ✓; per-setup persistence (Task 2 + migration in 3) ✓; round-trip through `SetupConfigBase` (Task 4) ✓; sizing math (Task 6) ✓; applied to all 5 strategies (Tasks 7–11) ✓; partial-cts proportional scaling (Task 6 + each strategy task) ✓; UI in Live + Backtest (Tasks 13–15) ✓; backtest + prospectus inherit via shared engine path (no extra work, called out in Architecture) ✓; HiVolMult ignored when AutoSize on (Task 6 — `if/else` ensures this branch is never reached) ✓; "skip when budget < floor" (Task 6 test `AutoSize_BudgetCtsBelowFloor_ReturnsZeroToSignalSkip`) ✓.
- **Placeholder scan:** No "TBD"/"similar to"/"add validation" strings. Each step has the exact code or command.
- **Type consistency:** `AutoSizeByRisk` (bool) used everywhere. `AutoSizeByRiskCalculator.Calc(decimal ep, decimal sl, StrategySetupConfig cfg, decimal atrRatio)` signature is identical at every call site (Tasks 7–11). `PartialContracts: scaledPartial` consistent across strategies.
