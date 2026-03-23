# Setup Basket Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fixed 4-slot setup system (A/B/C/D) with a dynamic basket where users pick N setup+instrument combinations, and the dashboard renders only the basket entries.

**Architecture:** A JSON column (`BasketJson`) on `StrategyConfig` stores `List<BasketEntry>`, each with a string ID (e.g. `"b-mnq-1"`), strategy type, instrument, and full config. The `SetupId` enum is replaced by string IDs throughout. `EngineSnapshot` switches from 160+ flat per-setup fields to a `List<SetupSnapshot>` array. Dashboard renders N cards dynamically from that array.

**Tech Stack:** C# / ASP.NET Core / EF Core (SQLite) / SignalR / Vanilla JS (no framework)

---

## Phasing Strategy

This is a 3-phase rollout. Each phase produces working software. Phase 1 is the highest-risk refactor (snapshot DTO) and should be done first to de-risk everything else.

- **Phase 1** — Snapshot array refactor (internal plumbing, no user-facing change)
- **Phase 2** — Basket config model + settings UI
- **Phase 3** — Dynamic dashboard cards

---

## File Map

### New Files
| File | Purpose |
|------|---------|
| `CRV.Core/Models/BasketEntry.cs` | Basket entry model (ID, type, instrument, config) |

### Modified Files (Phase 1 — Snapshot)
| File | Change |
|------|--------|
| `CRV.Core/Models/Signals.cs` | Replace `SetupId` enum with string IDs; replace 160+ flat per-setup fields on `EngineSnapshot` with `List<SetupSnapshot>` |
| `CRV.Core/Strategy/SnapshotAggregator.cs` | Replace switch(A/B/C/D) with loop over strategies |
| `CRV.Core/Strategy/ComposableEngine.cs` | `_strategies` keyed by string ID instead of `SetupId` enum |
| `CRV.Core/Strategy/ISetupStrategy.cs` | `SetupId` → string `Id` property |
| `CRV.Core/Strategy/PullbackStrategy.cs` | Use string ID |
| `CRV.Core/Strategy/RetestStrategy.cs` | Use string ID |
| `CRV.Core/Strategy/OrbFakeoutStrategy.cs` | Use string ID |
| `CRV.Core/Strategy/SessionFakeoutStrategy.cs` | Use string ID |
| `CRV.Core/Strategy/TickerGroup.cs` | Strategy list uses string IDs |
| `CRV.Core/Models/StrategySetupConfig.cs` | `SetupId` → string `Id` |
| `CRV.Web/Pages/Dashboard/Index.cshtml` | Consume `setups[]` array instead of flat fields |
| `CRV.Web/Pages/Shared/_SetupCard.cshtml` | Make template-able (no hardcoded IDs) |
| `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs` | Update for array snapshot |
| `CRV.Core.Tests/Strategy/ComposableEngineTests.cs` | Update for string IDs |
| `CRV.Core.Tests/Strategy/TickerGroupTests.cs` | Update for string IDs |

### Modified Files (Phase 2 — Basket Config)
| File | Change |
|------|--------|
| `CRV.Core/Models/StrategyConfig.cs` | Add `BasketJson` column; add `ToSetupConfigsFromBasket()` |
| `CRV.Core/Data/TradingDbContext.cs` | Map `BasketJson` |
| `CRV.Core/Migrations/` | New migration for `BasketJson` column |
| `CRV.Web/Pages/Settings/Live.cshtml` | Basket editor UI (add/remove entries) |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Use basket entries instead of fixed A/B/C/D |
| `CRV.Core/Models/Signals.cs` | `TradeRecord.Setup` → string (was enum) |

### Modified Files (Phase 3 — Dynamic Dashboard)
| File | Change |
|------|--------|
| `CRV.Web/Pages/Dashboard/Index.cshtml` | Dynamic card creation from basket; remove hardcoded 4-card layout |
| `CRV.Web/Pages/Shared/_SetupCard.cshtml` | Remove, replaced by JS template |

---

## Phase 1: Snapshot Array Refactor

> **Goal:** Replace flat per-setup fields (SetupA, TradeCountA, ...) with a `List<SetupSnapshot>` on `EngineSnapshot`. Keep 4 fixed setups (A/B/C/D) — just change the data shape. Dashboard consumes the array.

### Task 1.1: Define SetupSnapshot DTO

**Files:**
- Modify: `CRV.Core/Models/Signals.cs`

- [ ] **Step 1: Add SetupSnapshot class**

Add this class right after the existing `ActiveTradeView` class (~line 343):

```csharp
/// <summary>
/// Per-setup snapshot for the dashboard. Replaces the flat SetupA/B/C/D fields.
/// </summary>
public class SetupSnapshot
{
    public string   Id             { get; set; } = "";    // "A", "B", "C", "D" (later: "b-mnq-1")
    public string   Label          { get; set; } = "";    // "B — Breakout [BONGA]"
    public string   StrategyType   { get; set; } = "";    // "Pullback", "Retest", etc.
    public string   Ticker         { get; set; } = "";
    public decimal  PointValue     { get; set; }
    public decimal  LastPrice      { get; set; }
    public bool     Enabled        { get; set; }
    public int      State          { get; set; }          // state machine value
    public bool     PastCutoff     { get; set; }

    // Trade
    public ActiveTradeView? Trade  { get; set; }
    public int      TradeCount     { get; set; }
    public int      MaxTrades      { get; set; }
    public bool     StickyTgt      { get; set; }
    public bool     StickyStp      { get; set; }

    // Daily stats
    public int      Wins           { get; set; }
    public int      Losses         { get; set; }
    public decimal  WinPnl         { get; set; }
    public decimal  LossPnl        { get; set; }
    public decimal  Expectancy     { get; set; }

    // Per-setup ORB
    public decimal  OrbHigh        { get; set; }
    public decimal  OrbLow         { get; set; }
    public decimal  OrbMid         { get; set; }
    public decimal  OrbRange       { get; set; }
    public bool     OrbBullClose   { get; set; }
    public bool     OrbBearClose   { get; set; }
    public decimal  OrbAtrRatio    { get; set; }
    public bool     OrbFormed      { get; set; }
}
```

- [ ] **Step 2: Add `Setups` list to EngineSnapshot**

Add this property to `EngineSnapshot` (after the existing flat fields, ~line 315):

```csharp
    /// <summary>Dynamic setup snapshots (replaces flat SetupA/B/C/D fields).</summary>
    public List<SetupSnapshot> Setups { get; set; } = new();
```

- [ ] **Step 3: Build succeeds**

Run: `dotnet build --verbosity quiet`
Expected: 0 errors (we added new fields, didn't remove old ones yet)

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Models/Signals.cs
git commit -m "feat: add SetupSnapshot DTO and Setups list to EngineSnapshot"
```

---

### Task 1.2: Populate Setups[] in SnapshotAggregator

**Files:**
- Modify: `CRV.Core/Strategy/SnapshotAggregator.cs`
- Test: `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs`

- [ ] **Step 1: Write failing test**

Add a test that verifies `Setups` list is populated:

```csharp
[Fact]
public void Build_PopulatesSetupsList()
{
    var inputs = CreateDefaultInputs(); // existing helper
    var snap = SnapshotAggregator.Build(inputs);

    Assert.NotNull(snap.Setups);
    Assert.Equal(inputs.Strategies.Count, snap.Setups.Count);
    // First strategy should have matching Id
    Assert.Equal("A", snap.Setups[0].Id);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "Build_PopulatesSetupsList" --verbosity quiet`
Expected: FAIL — `Setups` list is empty

- [ ] **Step 3: Implement — add Setups[] population to Build()**

In `SnapshotAggregator.Build()`, at the end of the strategy loop (after the existing switch block, ~line 300), add:

```csharp
snap.Setups.Add(new SetupSnapshot
{
    Id           = strategy.SetupId.ToString(),
    Label        = $"{strategy.SetupId} — {strategy.Name}",
    StrategyType = strategy.StrategyType.ToString(),
    Ticker       = strategy.Ticker?.TrimStart('/') ?? "",
    PointValue   = strategy.PointValue,
    LastPrice    = setupLastPrice,
    Enabled      = ss.Enabled,
    State        = ss.State,
    PastCutoff   = ss.PastCutoff,
    Trade        = trade,
    TradeCount   = ss.TradeCount,
    MaxTrades    = ss.MaxTrades,
    StickyTgt    = ss.StickyTgt,
    StickyStp    = ss.StickyStp,
    Wins         = ss.Wins,
    Losses       = ss.Losses,
    WinPnl       = ss.WinPnl,
    LossPnl      = ss.LossPnl,
    Expectancy   = CalcExpectancy(ss.Wins, ss.Losses, ss.WinPnl, ss.LossPnl),
    OrbHigh      = perSetupOrb?.High ?? 0,
    OrbLow       = perSetupOrb?.Low ?? 0,
    OrbMid       = perSetupOrb?.Mid ?? 0,
    OrbRange     = perSetupOrb?.Range ?? 0,
    OrbBullClose = perSetupOrb?.BullClose ?? false,
    OrbBearClose = perSetupOrb?.BearClose ?? false,
    OrbAtrRatio  = perSetupOrb?.AtrRatio ?? 0,
    OrbFormed    = perSetupOrb?.IsSet ?? false,
});
```

Where `perSetupOrb` is resolved by adding before the `switch`:

```csharp
OrbState? perSetupOrb = null;
inputs.PerSetupOrb?.TryGetValue(strategy.SetupId, out perSetupOrb);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "Build_PopulatesSetupsList" --verbosity quiet`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Strategy/SnapshotAggregator.cs CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs
git commit -m "feat: populate Setups[] array in SnapshotAggregator alongside flat fields"
```

---

### Task 1.3: Dashboard consumes Setups[] array

**Files:**
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml`

> **Important:** During this transition, the old flat fields (`setupA`, `tradeCountA`, etc.) are still populated. The dashboard switches to reading from `setups[]` but the flat fields remain as fallback. This makes the transition safe.

- [ ] **Step 1: Add JS helper to find setup by ID**

Add near the top of the `<script>` section:

```javascript
function findSetup(snap, id) {
    if (snap.setups && Array.isArray(snap.setups)) {
        return snap.setups.find(s => s.id === id) || null;
    }
    return null;
}
```

- [ ] **Step 2: Update `updateSetup()` to accept a SetupSnapshot object**

Change the `updateSetup()` function signature and body to read from the `SetupSnapshot` object instead of scattered flat fields. The function currently receives individual parameters — change it to receive the full setup snapshot:

```javascript
// OLD: updateSetup("a", d.setupA, d.setupAState, d.setupAEnabled, d.pastCutoffA, ...)
// NEW: updateSetup("a", findSetup(d, "A"), d.sessionEnded)
function updateSetup(id, setup, sessionEnded) {
    if (!setup) return;
    var trade = setup.trade;
    var state = setup.state;
    var enabled = setup.enabled !== false;
    var pastCutoff = setup.pastCutoff;
    var maxTradesReached = setup.tradeCount >= setup.maxTrades;
    var lastPrice = setup.lastPrice;
    var ticker = setup.ticker;
    var pointValue = setup.pointValue;
    // ... rest of existing logic using these local vars
}
```

- [ ] **Step 3: Update the call sites**

Replace the 4 hardcoded `updateSetup()` calls:

```javascript
// OLD:
// updateSetup("a", d.setupA, d.setupAState, d.setupAEnabled !== false, d.pastCutoffA, d.sessionEnded, ...)
// NEW:
["A","B","C","D"].forEach(id => {
    updateSetup(id.toLowerCase(), findSetup(d, id), d.sessionEnded);
});
```

- [ ] **Step 4: Update Active Setup / Signals logic**

Replace the existing `["a","b","c","d"].forEach(...)` block that builds `activeSetups[]` and `signalsList[]` to read from `d.setups`:

```javascript
var activeSetups = [];
var signalsList = [];
if (d.setups) {
    d.setups.forEach(s => {
        if (_selectedGroup && _tickerToGroup(s.ticker) !== _selectedGroup) return;
        var ID = s.id;
        if (s.trade && s.trade.entry > 0) {
            activeSetups.push(ID + (s.trade.direction === 0 ? " Long" : " Short"));
        } else if (s.state !== 0) {
            var dir = s.state > 0 ? "▲" : "▼";
            signalsList.push(ID + " Armed " + dir);
        }
    });
}
```

- [ ] **Step 5: Update header count/PnL display**

The header trade counts and P&L currently read `d.tradeCountA + d.tradeCountB + ...`. Update to sum from `d.setups`:

```javascript
// Per-setup counts for header (keep the existing d.todayTrades for DB-backed total)
var engineTradeCount = d.setups ? d.setups.reduce((sum, s) => sum + s.tradeCount, 0) : 0;
```

- [ ] **Step 6: Test manually — verify dashboard renders identically**

Run the app, open Dashboard. All 4 setup cards should render exactly as before. The data now flows from `d.setups[]` but produces the same DOM output.

- [ ] **Step 7: Commit**

```bash
git add CRV.Web/Pages/Dashboard/Index.cshtml
git commit -m "refactor: dashboard reads from setups[] array instead of flat per-setup fields"
```

---

### Task 1.4: Remove flat per-setup fields from EngineSnapshot

**Files:**
- Modify: `CRV.Core/Models/Signals.cs`
- Modify: `CRV.Core/Strategy/SnapshotAggregator.cs`
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml`
- Test: `CRV.Core.Tests/Strategy/SnapshotAggregatorTests.cs`

> **Important:** Only do this after Task 1.3 is verified working. This removes the old flat fields.

- [ ] **Step 1: Remove flat per-setup properties from EngineSnapshot**

In `Signals.cs`, remove all properties suffixed with A/B/C/D:
- `SetupA`, `SetupB`, `SetupC`, `SetupD` (ActiveTradeView)
- `TradeCountA/B/C/D`, `MaxTradesA/B/C/D`
- `SetupAState/B/C/D`, `SetupAEnabled/B/C/D`
- `PastCutoffA/B/C/D`
- `StickyTgtA/B/C/D`, `StickyStpA/B/C/D`
- `ExpectancyA/B/C/D`
- `TodayWinsA/B/C/D`, `TodayLossesA/B/C/D`, `TodayWinPnlA/B/C/D`, `TodayLossPnlA/B/C/D`
- `LastPriceA/B/C/D`, `TickerA/B/C/D`, `PointValueA/B/C/D`
- `OrbHighA/B/C/D`, `OrbLowA/B/C/D`, `OrbMidA/B/C/D`, `OrbRangeA/B/C/D`
- `OrbBullCloseA/B/C/D`, `OrbBearCloseA/B/C/D`, `OrbAtrRatioA/B/C/D`, `OrbFormedA/B/C/D`

Keep global fields (PastCutoff, Vwap, Atr, etc.)

- [ ] **Step 2: Remove switch(SetupId) block from SnapshotAggregator**

Remove the entire `switch (strategy.SetupId)` block (~lines 199–300). The `Setups.Add(...)` from Task 1.2 replaces it.

- [ ] **Step 3: Fix compilation errors**

Build and fix any remaining references to removed fields. Common locations:
- `ComposableEngine.cs` — any direct snapshot field access
- Dashboard JS — any lingering `d.setupAState` references (should all be migrated in Task 1.3)
- Test files — update assertions that check flat fields

- [ ] **Step 4: Update tests**

Update `SnapshotAggregatorTests` to assert against `snap.Setups[0].TradeCount` instead of `snap.TradeCountA`.

- [ ] **Step 5: Run all tests**

Run: `dotnet test --verbosity quiet`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: remove flat per-setup fields from EngineSnapshot, use Setups[] only"
```

---

## Phase 2: Basket Config Model

> **Goal:** Add `BasketJson` column to `StrategyConfig`. Users can define N setup+instrument entries. The old per-setup columns remain as defaults/fallback but `ToSetupConfigs()` reads from basket when present.

### Task 2.1: Define BasketEntry model

**Files:**
- Create: `CRV.Core/Models/BasketEntry.cs`

- [ ] **Step 1: Create the model**

```csharp
using CRV.Core.Strategy;

namespace CRV.Core.Models;

/// <summary>
/// A single entry in the user's setup basket. Each entry defines a strategy type,
/// instrument, and full per-setup configuration.
/// </summary>
public class BasketEntry
{
    /// <summary>Unique ID within the basket (e.g. "b-mnq-1", "a-mcl-1").</summary>
    public string Id { get; set; } = "";

    /// <summary>Display label (e.g. "B — Retest [MNQ]").</summary>
    public string Label { get; set; } = "";

    /// <summary>Strategy type to instantiate.</summary>
    public StrategyType StrategyType { get; set; }

    /// <summary>Broker ticker symbol (e.g. "/MNQM26").</summary>
    public string Ticker { get; set; } = "";

    /// <summary>Point value for this instrument.</summary>
    public decimal PointValue { get; set; } = 20m;

    /// <summary>Tick size for this instrument.</summary>
    public decimal TickSize { get; set; } = 0.25m;

    /// <summary>Full per-setup configuration.</summary>
    public StrategySetupConfig Config { get; set; } = new();
}
```

- [ ] **Step 2: Build succeeds**

Run: `dotnet build --verbosity quiet`

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Models/BasketEntry.cs
git commit -m "feat: add BasketEntry model for dynamic setup basket"
```

---

### Task 2.2: Add BasketJson to StrategyConfig + migration

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs`
- Modify: `CRV.Core/Data/TradingDbContext.cs`
- Create: new EF migration

- [ ] **Step 1: Add BasketJson property**

In `StrategyConfig.cs`, add near the top (after `Name`):

```csharp
    /// <summary>
    /// JSON-serialized List&lt;BasketEntry&gt; defining the active setup basket.
    /// When null/empty, falls back to the legacy per-setup columns (A/B/C/D).
    /// </summary>
    public string? BasketJson { get; set; }
```

- [ ] **Step 2: Configure in DbContext**

In `TradingDbContext.cs`, add column mapping:

```csharp
e.Property(s => s.BasketJson).HasColumnType("TEXT");
```

- [ ] **Step 3: Create migration**

Run: `dotnet ef migrations add AddBasketJson --project CRV.Core --startup-project CRV.Web`

- [ ] **Step 4: Apply migration**

Run: `dotnet ef database update --project CRV.Core --startup-project CRV.Web`

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Models/StrategyConfig.cs CRV.Core/Data/TradingDbContext.cs CRV.Core/Migrations/
git commit -m "feat: add BasketJson column to StrategyConfig"
```

---

### Task 2.3: Change SetupId from enum to string

**Files:**
- Modify: `CRV.Core/Models/Signals.cs` — `SetupId` enum, `EntrySignal`, `ExitSignal`, `TradeRecord`
- Modify: `CRV.Core/Models/StrategySetupConfig.cs` — `SetupId` → string `Id`
- Modify: `CRV.Core/Strategy/ISetupStrategy.cs` — `SetupId` → string `Id`
- Modify: All 4 strategy files
- Modify: `CRV.Core/Strategy/ComposableEngine.cs` — `Dictionary<SetupId, ...>` → `Dictionary<string, ...>`
- Modify: `CRV.Core/Strategy/SnapshotAggregator.cs`
- Modify: `CRV.Core/Strategy/TickerGroup.cs`
- Modify: `CRV.Core/Data/TradingDbContext.cs` — TradeRecord.Setup mapping
- Modify: All test files
- Create: new EF migration (TradeRecord.Setup column type change)

> **This is the most invasive change.** It touches nearly every file. The approach is:
> 1. Keep `SetupId` enum for backward compatibility in DB
> 2. Add `string Id` property to `ISetupStrategy` and `StrategySetupConfig`
> 3. Change `ComposableEngine._strategies` key from `SetupId` to `string`
> 4. `TradeRecord.Setup` stays `SetupId` enum for now (old trades use A/B/C/D); add `string SetupLabel` for new-style IDs

- [ ] **Step 1: Add string `Id` to StrategySetupConfig**

```csharp
// In StrategySetupConfig.cs
public string Id { get; set; } = "";  // "A", "B", or basket entry ID like "b-mnq-1"
```

- [ ] **Step 2: Add string `Id` to ISetupStrategy interface**

```csharp
// In ISetupStrategy.cs
string Id { get; }  // unique basket entry ID
```

- [ ] **Step 3: Implement in all 4 strategies**

Each strategy constructor already receives `StrategySetupConfig`. Store `_id = config.Id`:

```csharp
private readonly string _id;
public string Id => _id;
// In constructor: _id = config.Id;
```

- [ ] **Step 4: Change ComposableEngine dictionaries**

```csharp
// OLD:
private readonly Dictionary<SetupId, ISetupStrategy> _strategies = new();
private readonly Dictionary<SetupId, string> _setupToGroupKey = new();

// NEW:
private readonly Dictionary<string, ISetupStrategy> _strategies = new();
private readonly Dictionary<string, string> _setupToGroupKey = new();
```

Update `AddSetup`:
```csharp
_strategies[config.Id] = strategy;
_setupToGroupKey[config.Id] = groupKey;
```

- [ ] **Step 5: Update SnapshotAggregator**

Change `PerSetupOrb` key from `SetupId` to `string`:
```csharp
public Dictionary<string, OrbState> PerSetupOrb { get; init; }
```

In `Build()`, use `strategy.Id` instead of `strategy.SetupId.ToString()`:
```csharp
Id = strategy.Id,
```

- [ ] **Step 6: Update ToSetupConfigs() to set Id**

In `StrategyConfig.BuildSetupConfigA()`:
```csharp
Id = "A", Name = "A", SetupId = SetupId.A,
```
Same for B, C, D.

- [ ] **Step 7: Fix all compilation errors and tests**

Build, fix remaining references. Update test fakes to implement `string Id`.

- [ ] **Step 8: Run all tests**

Run: `dotnet test --verbosity quiet`
Expected: All pass

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor: add string Id to strategies, key engine dictionaries by string"
```

---

### Task 2.4: ToSetupConfigs reads from BasketJson

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs`

- [ ] **Step 1: Write test**

```csharp
[Fact]
public void ToSetupConfigs_WithBasketJson_ReturnsBasketEntries()
{
    var basket = new List<BasketEntry>
    {
        new() { Id = "b-mnq-1", StrategyType = StrategyType.Retest,
                Ticker = "/MNQM26", PointValue = 2, TickSize = 0.25m,
                Config = new() { MaxTrades = 3, CutoffHour = 14 } },
        new() { Id = "b-mgc-1", StrategyType = StrategyType.Retest,
                Ticker = "/MGCJ26", PointValue = 10, TickSize = 0.10m,
                Config = new() { MaxTrades = 5, CutoffHour = 13 } },
    };
    var cfg = new StrategyConfig { BasketJson = JsonSerializer.Serialize(basket) };
    var setups = cfg.ToSetupConfigs();

    Assert.Equal(2, setups.Count);
    Assert.Equal("b-mnq-1", setups[0].Id);
    Assert.Equal("/MNQM26", setups[0].Ticker);
    Assert.Equal(3, setups[0].MaxTrades);
}
```

- [ ] **Step 2: Implement**

```csharp
public List<StrategySetupConfig> ToSetupConfigs()
{
    if (!string.IsNullOrEmpty(BasketJson))
    {
        var basket = JsonSerializer.Deserialize<List<BasketEntry>>(BasketJson);
        if (basket?.Count > 0)
            return basket.Select(b => ToSetupConfig(b)).ToList();
    }
    // Legacy fallback: fixed A/B/C/D
    return new() { BuildSetupConfigA(), BuildSetupConfigB(), BuildSetupConfigC(), BuildSetupConfigD() };
}

private StrategySetupConfig ToSetupConfig(BasketEntry b) => new()
{
    Id = b.Id,
    Name = b.Label,
    SetupId = SetupId.F, // generic for basket entries
    StrategyType = b.StrategyType,
    Enabled = true,
    Ticker = b.Ticker,
    PointValue = b.PointValue,
    TickSize = b.TickSize,
    // Copy all config fields from b.Config
    Contracts = b.Config.Contracts,
    HiVolMult = b.Config.HiVolMult,
    MaxContracts = b.Config.MaxContracts,
    StopPct = b.Config.StopPct,
    TargetPct = b.Config.TargetPct,
    PartialPct = b.Config.PartialPct,
    NearPct = b.Config.NearPct,
    MinRr = b.Config.MinRr,
    Mode = b.Config.Mode,
    PullbackPct = b.Config.PullbackPct,
    RetestPct = b.Config.RetestPct,
    EntryTickOffset = b.Config.EntryTickOffset,
    OrderType = b.Config.OrderType,
    UseVwap = b.Config.UseVwap,
    UseOrbClose = b.Config.UseOrbClose,
    CutoffHour = b.Config.CutoffHour,
    CutoffMinute = b.Config.CutoffMinute,
    CloseAtRthClose = b.Config.CloseAtRthClose,
    MaxTrades = b.Config.MaxTrades,
    MaxAdverseMinutes = b.Config.MaxAdverseMinutes,
    UsePartial = b.Config.UsePartial,
    UseBe = b.Config.UseBe,
    PartialCts = b.Config.PartialCts,
    AllowRearmAfterBe = b.Config.AllowRearmAfterBe,
};
```

- [ ] **Step 3: Run tests**

Run: `dotnet test --verbosity quiet`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Models/StrategyConfig.cs CRV.Core.Tests/
git commit -m "feat: ToSetupConfigs reads from BasketJson with legacy fallback"
```

---

## Phase 3: Dynamic Dashboard & Settings UI

> **Goal:** Dashboard renders N cards from `setups[]`. Settings page provides basket editor.

### Task 3.1: Dynamic setup card rendering

**Files:**
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml`

- [ ] **Step 1: Replace static 4-card HTML with a container**

Replace the two `<div class="row g-3 mb-3">` blocks that render `_SetupCard` partials with:

```html
<div id="setup-cards-container" class="row g-3 mb-3">
    <!-- Setup cards rendered dynamically from setups[] -->
</div>
```

- [ ] **Step 2: Add JS card template function**

```javascript
function createSetupCard(setup) {
    var id = setup.id.toLowerCase().replace(/[^a-z0-9]/g, '-');
    var icons = { Pullback: "bi-triangle-fill", Retest: "bi-arrow-up-right-circle-fill",
                  OrbFakeout: "bi-arrow-left-right", SessionFakeout: "bi-arrow-return-right" };
    var icon = icons[setup.strategyType] || "bi-circle";

    var col = document.createElement("div");
    col.className = "col-md-6";
    col.id = "card-" + id;
    col.innerHTML = `
        <div class="card crv-card h-100">
            <div class="card-body p-3">
                <div class="d-flex justify-content-between align-items-center mb-2">
                    <h6 class="mb-0">
                        <i class="bi ${icon} me-1"></i>
                        ${setup.label}
                        <span class="badge bg-success ms-1" id="enabled-${id}">ON</span>
                        <span class="badge bg-secondary ms-1" id="status-${id}">IDLE</span>
                        <span class="small text-muted ms-1" id="count-${id}">[0/5]</span>
                    </h6>
                </div>
                <table class="table table-sm crv-detail-table mb-0">
                    <tr><td class="text-muted">Last Price</td><td id="${id}-price" class="font-monospace">—</td></tr>
                    <tr><td class="text-muted">Direction</td><td id="${id}-dir" class="font-monospace">—</td></tr>
                    <tr><td class="text-muted">Entry</td><td id="${id}-entry" class="font-monospace">—</td></tr>
                    <tr><td class="text-muted">Stop</td><td id="${id}-stop" class="font-monospace">—</td></tr>
                    <tr><td class="text-muted">Partial (50%)</td><td id="${id}-partial" class="font-monospace">—</td></tr>
                    <tr><td class="text-muted">Target</td><td id="${id}-target" class="font-monospace">—</td></tr>
                    <tr><td class="text-muted">Contracts</td><td id="${id}-cts" class="font-monospace">—</td></tr>
                    <tr><td class="text-muted">Expectancy</td><td id="${id}-expectancy" class="font-monospace">—</td></tr>
                    <tr><td class="text-muted">Unrealized P&L</td><td id="${id}-unreal" class="font-monospace">—</td></tr>
                </table>
            </div>
        </div>
    `;
    return col;
}
```

- [ ] **Step 3: Auto-create cards on first snapshot**

```javascript
var _cardsCreated = false;
// Inside the crv:update handler, before updateSetup calls:
if (!_cardsCreated && d.setups && d.setups.length > 0) {
    var container = document.getElementById("setup-cards-container");
    container.innerHTML = "";
    d.setups.forEach(s => container.appendChild(createSetupCard(s)));
    _cardsCreated = true;
}
```

- [ ] **Step 4: Update `updateSetup()` to use dynamic IDs**

The `id` parameter now uses the sanitized basket entry ID (e.g. `"b-mnq-1"` → `"b-mnq-1"`):

```javascript
d.setups.forEach(s => {
    var id = s.id.toLowerCase().replace(/[^a-z0-9]/g, '-');
    updateSetup(id, s, d.sessionEnded);
});
```

- [ ] **Step 5: Test manually**

Start engine with default 4 setups. Dashboard should create 4 cards dynamically.

- [ ] **Step 6: Commit**

```bash
git add CRV.Web/Pages/Dashboard/Index.cshtml
git commit -m "feat: dashboard renders setup cards dynamically from setups[] array"
```

---

### Task 3.2: Basket editor on Settings page

**Files:**
- Modify: `CRV.Web/Pages/Settings/Live.cshtml`

- [ ] **Step 1: Add Basket section to settings page**

Add a new tab/section "Setup Basket" with:
- A table showing current basket entries (ID, Type, Instrument, Actions)
- "Add Setup" button that opens a form:
  - Strategy Type dropdown (Pullback, Retest, OrbFakeout, SessionFakeout)
  - Instrument picker (presets: MNQ, MES, MGC, MCL + custom)
  - Per-setup config fields (reuse existing `_SetupConfigSection` partial)
- Remove button per entry
- Save button serializes to `BasketJson` and POSTs

- [ ] **Step 2: Add API endpoint for basket CRUD**

Add to settings page model or a new API controller:

```csharp
[HttpPost("api/settings/basket")]
public async Task<IActionResult> SaveBasket([FromBody] List<BasketEntry> basket)
{
    var cfg = await _db.StrategyConfigs.FirstAsync();
    cfg.BasketJson = JsonSerializer.Serialize(basket);
    await _db.SaveChangesAsync();
    return Ok();
}

[HttpGet("api/settings/basket")]
public async Task<IActionResult> GetBasket()
{
    var cfg = await _db.StrategyConfigs.FirstAsync();
    if (string.IsNullOrEmpty(cfg.BasketJson))
        return Ok(new List<BasketEntry>());
    return Ok(JsonSerializer.Deserialize<List<BasketEntry>>(cfg.BasketJson));
}
```

- [ ] **Step 3: Test — add 2 Retest setups on different instruments**

1. Open Settings → Setup Basket
2. Add: Retest on MNQ, label "B — MNQ"
3. Add: Retest on MGC, label "B — MGC"
4. Save
5. Start engine
6. Dashboard should show 2 cards instead of 4

- [ ] **Step 4: Commit**

```bash
git add CRV.Web/Pages/Settings/Live.cshtml CRV.Web/Api/
git commit -m "feat: basket editor UI on settings page with add/remove/save"
```

---

### Task 3.3: Backward compatibility — empty basket uses legacy A/B/C/D

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs` (already done in Task 2.4)

- [ ] **Step 1: Verify fallback**

When `BasketJson` is null/empty, `ToSetupConfigs()` falls back to `BuildSetupConfigA/B/C/D()`.

- [ ] **Step 2: Write test confirming legacy behavior**

```csharp
[Fact]
public void ToSetupConfigs_WithoutBasket_ReturnsLegacyABCD()
{
    var cfg = new StrategyConfig { BasketJson = null, EnableA = true, EnableB = true };
    var setups = cfg.ToSetupConfigs();
    Assert.Equal(4, setups.Count);
    Assert.Equal("A", setups[0].Id);
    Assert.Equal("D", setups[3].Id);
}
```

- [ ] **Step 3: Commit**

```bash
git add CRV.Core.Tests/
git commit -m "test: verify legacy A/B/C/D fallback when basket is empty"
```

---

## Migration / Compatibility Notes

### TradeRecord.Setup
- Existing trades in DB use `SetupId` enum (A/B/C/D stored as strings via EF conversion).
- Basket entries use custom string IDs (e.g. `"b-mnq-1"`).
- **Decision:** Change `TradeRecord.Setup` from `SetupId` enum to `string`. Add a migration that converts existing enum values to their string names ("A", "B", "C", "D"). This keeps `DailyStatsService` grouping working and avoids the `SetupId.F` collision problem (where all basket entries would share the same enum value).

### Broker State Dictionaries — CRITICAL
- `SchwabBroker`, `TradeStationBroker`, `TradovateExecutor` all use `Dictionary<SetupId, SetupState>` with hardcoded `[SetupId.A]`, `[SetupId.B]` keys.
- **Decision:** Change to `Dictionary<string, SetupState>` keyed by string setup ID. Broker state entries are created dynamically when `AddSetup` is called (or on first signal for that setup).

### IOrderExecutor Interface
- `OnLevelsAdjustedAsync(SetupId setup, ...)` uses enum.
- **Decision:** Change to `string setupId`. All broker implementations update accordingly.

### ForceExitSetup
- Currently 4 hardcoded `OnPostForceExitA/B/C/D` handlers in `Index.cshtml.cs`.
- `ComposableEngine.ForceExitSetupAsync(SetupId)` and `LiveEngineOrchestrator.ForceExitSetup(SetupId)`.
- **Decision:** Change to single `ForceExitSetupAsync(string setupId)`. Dashboard sends setup ID as parameter.

### Backtest CSV
- Current CSV has `SETUP` column with values `A`, `B`, `C`, `D`.
- Basket entries will produce IDs like `b-mnq-1`.
- **Decision:** The CSV `SETUP` column will contain the basket entry ID. Old backtests remain readable.

### OrbStateCacheService
- Already keyed by ticker symbol (via `TickerGroup.GetGroupKey()`), NOT by SetupId.
- **No change needed.**

### Additional Files Impacted by Phase 2 (SetupId → string)
These files reference `SetupId` enum and must be updated in Task 2.3:
- `CRV.Core/Interfaces/IInterfaces.cs` — `IOrderExecutor` interface
- `CRV.Live/Brokers/Schwab/SchwabBroker.cs` — state dictionary
- `CRV.Live/Brokers/TradeStation/TradeStationBroker.cs` — state dictionary
- `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs` — state dictionary + `RecoverDirectionAsync`
- `CRV.Live/Brokers/MockBrokerExecutor.cs` — signal.Setup.ToString()
- `CRV.Web/Pages/Dashboard/Index.cshtml.cs` — ForceExit handlers
- `CRV.Web/Services/LiveEngineOrchestrator.cs` — ForceExitSetup signature
- `CRV.Web/Services/DailyStatsService.cs` — per-setup stats routing
- `CRV.Backtest/Results/BacktestResults.cs` — filters by SetupId
- `CRV.Backtest/Engine/BacktestEngine.cs` — `Dictionary<SetupId, EntrySignal>`
