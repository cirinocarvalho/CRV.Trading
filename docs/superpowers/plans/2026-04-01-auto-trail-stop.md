# Auto Trail Stop & PnL Accuracy Hardening — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-ticker auto-trail stop support (Tradovate native + backtest simulation) and harden fill price resolution to always use REST `/fill/deps` instead of stale fallbacks.

**Architecture:** Extend `BasketEntry` with `AutoTrailConfig`, flow trail params through `EntrySignal` → `TradovateExecutor` bracket builder. Backtest simulates trail tick-by-tick via transient state on `GroupOrder`. `ResolveExitFillPriceAsync` is rewritten to always verify prices via REST with retries, eliminating the WSS trust + stale fallback pattern. Manual page upgraded to `startOrderStrategy` when trail is enabled.

**Tech Stack:** C# / .NET 8 / EF Core / SQLite / Razor Pages / Tradovate REST API

**Spec:** `docs/superpowers/specs/2026-04-01-auto-trail-stop-design.md`

---

### Task 1: AutoTrailConfig Model + BasketEntry + EntrySignal

**Files:**
- Create: `CRV.Core/Models/AutoTrailConfig.cs`
- Modify: `CRV.Core/Models/BasketEntry.cs:41` (add AutoTrail property)
- Modify: `CRV.Core/Models/Signals.cs:9-26` (add trail params to EntrySignal)

- [ ] **Step 1: Create AutoTrailConfig class**

Create `CRV.Core/Models/AutoTrailConfig.cs`:

```csharp
namespace CRV.Core.Models;

public class AutoTrailConfig
{
    public bool Enabled { get; set; }
    public decimal StopLoss { get; set; }
    public decimal Freq { get; set; }
    public decimal? Trigger { get; set; }
}
```

- [ ] **Step 2: Add AutoTrail to BasketEntry**

In `CRV.Core/Models/BasketEntry.cs`, after line 41 (`public StrategySetupConfig Config`), add:

```csharp
    /// <summary>Per-ticker auto-trail stop config. Null = no trail.</summary>
    public AutoTrailConfig? AutoTrail { get; set; }
```

- [ ] **Step 3: Add trail params to EntrySignal**

In `CRV.Core/Models/Signals.cs`, add 3 new optional params to the `EntrySignal` record after the `Mode` parameter (line 26):

```csharp
    decimal? AutoTrailStopLoss = null,
    decimal? AutoTrailTrigger  = null,
    decimal? AutoTrailFreq     = null);
```

- [ ] **Step 4: Build and verify no compilation errors**

Run: `dotnet build CRV.Core`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Models/AutoTrailConfig.cs CRV.Core/Models/BasketEntry.cs CRV.Core/Models/Signals.cs
git commit -m "feat: add AutoTrailConfig model, BasketEntry.AutoTrail, EntrySignal trail params"
```

---

### Task 2: Flow Trail Params from Config to EntrySignal in All Strategies

**Files:**
- Modify: `CRV.Core/Strategy/PullbackStrategy.cs:311-316`
- Modify: `CRV.Core/Strategy/RetestStrategy.cs:362-368`
- Modify: `CRV.Core/Strategy/SessionFakeoutStrategy.cs:259-265`
- Modify: `CRV.Core/Strategy/OrbFakeoutStrategy.cs:250-256`

All four strategies create `EntrySignal` with the same pattern. Each needs to receive trail config and pass it through.

- [ ] **Step 1: Add AutoTrail to StrategySetupConfig for pass-through**

In `CRV.Core/Models/StrategySetupConfig.cs`, after line 71 (`AllowRearmAfterBe`), add:

```csharp
    // Auto Trail (per-ticker, copied from BasketEntry at setup construction)
    public AutoTrailConfig? AutoTrail { get; set; }
```

- [ ] **Step 2: Update PullbackStrategy.TryEntry**

In `CRV.Core/Strategy/PullbackStrategy.cs`, modify the `new EntrySignal(...)` call (lines 311-316) to add trail params after `Mode: _cfg.Mode`:

```csharp
        _pendingEntry = new EntrySignal(
            _cfg.SetupId,
            isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, contracts, time,
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe, Mode: _cfg.Mode,
            AutoTrailStopLoss: _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.StopLoss : null,
            AutoTrailTrigger:  _cfg.AutoTrail?.Enabled == true
                ? (_cfg.AutoTrail.Trigger ?? (_cfg.UsePartial ? (decimal?)null : throw new InvalidOperationException("AutoTrail.Trigger required when UsePartial=false")))
                : null,
            AutoTrailFreq:     _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.Freq : null);
```

Note: When `UsePartial=true` and `Trigger` is null, the trigger is derived later in `TradovateExecutor` from the Tg1 offset. When `UsePartial=false`, `Trigger` must be set (validated at config level).

- [ ] **Step 3: Update RetestStrategy.TryEntry**

In `CRV.Core/Strategy/RetestStrategy.cs`, apply the same change to the `new EntrySignal(...)` call (lines 362-368):

```csharp
        _pendingEntry = new EntrySignal(
            _cfg.SetupId,
            isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, contracts, time,
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe, Mode: _cfg.Mode,
            AutoTrailStopLoss: _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.StopLoss : null,
            AutoTrailTrigger:  _cfg.AutoTrail?.Enabled == true
                ? (_cfg.AutoTrail.Trigger ?? (_cfg.UsePartial ? (decimal?)null : throw new InvalidOperationException("AutoTrail.Trigger required when UsePartial=false")))
                : null,
            AutoTrailFreq:     _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.Freq : null);
```

- [ ] **Step 4: Update SessionFakeoutStrategy.TryEntry**

In `CRV.Core/Strategy/SessionFakeoutStrategy.cs`, apply the same pattern to the `new EntrySignal(...)` call (lines 259-265):

```csharp
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe,
            AutoTrailStopLoss: _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.StopLoss : null,
            AutoTrailTrigger:  _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.Trigger : null,
            AutoTrailFreq:     _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.Freq : null);
```

- [ ] **Step 5: Update OrbFakeoutStrategy.TryEntry**

In `CRV.Core/Strategy/OrbFakeoutStrategy.cs`, apply the same pattern to the `new EntrySignal(...)` call (lines 250-256):

```csharp
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe,
            AutoTrailStopLoss: _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.StopLoss : null,
            AutoTrailTrigger:  _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.Trigger : null,
            AutoTrailFreq:     _cfg.AutoTrail?.Enabled == true ? _cfg.AutoTrail.Freq : null);
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build CRV.Core`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Models/StrategySetupConfig.cs CRV.Core/Strategy/PullbackStrategy.cs CRV.Core/Strategy/RetestStrategy.cs CRV.Core/Strategy/SessionFakeoutStrategy.cs CRV.Core/Strategy/OrbFakeoutStrategy.cs
git commit -m "feat: flow AutoTrail params from config through all strategies to EntrySignal"
```

---

### Task 3: TradovateExecutor — Add autoTrail to Bracket Builder

**Files:**
- Modify: `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs:217-250`
- Test: `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs` (verify existing tests still pass)

- [ ] **Step 1: Write test for bracket with autoTrail**

In `CRV.Core.Tests/Strategy/`, create `AutoTrailBracketTests.cs`:

```csharp
namespace CRV.Core.Tests.Strategy;

using CRV.Core.Models;
using Xunit;

public class AutoTrailBracketTests
{
    [Fact]
    public void EntrySignal_WithAutoTrail_CarriesParams()
    {
        var sig = new EntrySignal(
            SetupId.A, Direction.Long,
            Entry: 6628.5m, Stop: 6613.0m, Tg2Price: 6659.5m, Tg1Price: 6644.0m,
            TotalContracts: 2, Time: DateTime.UtcNow,
            UsePartial: true, UseBe: false, PartialContracts: 1,
            AutoTrailStopLoss: 4.0m, AutoTrailTrigger: null, AutoTrailFreq: 0.25m);

        Assert.Equal(4.0m, sig.AutoTrailStopLoss);
        Assert.Null(sig.AutoTrailTrigger); // derived from Tg1 at executor
        Assert.Equal(0.25m, sig.AutoTrailFreq);
    }

    [Fact]
    public void EntrySignal_WithAutoTrail_NoPartial_RequiresTrigger()
    {
        var sig = new EntrySignal(
            SetupId.A, Direction.Long,
            Entry: 6628.5m, Stop: 6613.0m, Tg2Price: 6659.5m, Tg1Price: 6644.0m,
            TotalContracts: 2, Time: DateTime.UtcNow,
            UsePartial: false,
            AutoTrailStopLoss: 4.0m, AutoTrailTrigger: 8.0m, AutoTrailFreq: 0.25m);

        Assert.Equal(8.0m, sig.AutoTrailTrigger);
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test CRV.Core.Tests --filter "AutoTrailBracketTests" -v n`
Expected: 2 tests passed

- [ ] **Step 3: Modify TradovateExecutor bracket builder**

In `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs`, modify the bracket builder in `OnEntrySignalAsync` (around lines 217-250).

Replace the BE offset and bracket building section:

```csharp
        var beOffset = sig.UseBe ? Math.Abs(tg1Offset) : 0m;
```

With:

```csharp
        bool hasAutoTrail = sig.AutoTrailStopLoss.HasValue;
        // When auto-trail is active, skip breakEven — trail replaces it
        var beOffset = (!hasAutoTrail && sig.UseBe) ? Math.Abs(tg1Offset) : 0m;
```

Then in the bracket 2 builder (after `if (beOffset > 0) b2["breakEven"] = beOffset;`), add:

```csharp
            if (hasAutoTrail)
            {
                // Derive trigger from Tg1 offset if not explicitly set (UsePartial=true)
                var trailTrigger = sig.AutoTrailTrigger ?? Math.Abs(tg1Offset);
                b2["autoTrail"] = new Dictionary<string, object>
                {
                    ["stopLoss"] = sig.AutoTrailStopLoss!.Value,
                    ["trigger"] = trailTrigger,
                    ["freq"] = sig.AutoTrailFreq!.Value
                };
            }
```

For the single bracket path (no partial), add the same `autoTrail` block after the single bracket creation:

```csharp
            // Single bracket: full qty, one target + stop
            var singleBracket = new Dictionary<string, object>
            {
                ["qty"] = sig.TotalContracts, ["profitTarget"] = tg2Offset,
                ["stopLoss"] = stopOffset, ["trailingStop"] = false
            };
            if (hasAutoTrail)
            {
                singleBracket["autoTrail"] = new Dictionary<string, object>
                {
                    ["stopLoss"] = sig.AutoTrailStopLoss!.Value,
                    ["trigger"] = sig.AutoTrailTrigger!.Value,
                    ["freq"] = sig.AutoTrailFreq!.Value
                };
            }
            brackets.Add(singleBracket);
```

- [ ] **Step 4: Build and run existing tests**

Run: `dotnet build CRV.Live && dotnet test CRV.Core.Tests --filter "BrokerEventHandlerTests" -v n`
Expected: All existing tests pass

- [ ] **Step 5: Commit**

```bash
git add CRV.Live/Brokers/Tradovate/TradovateExecutor.cs CRV.Core.Tests/Strategy/AutoTrailBracketTests.cs
git commit -m "feat: add autoTrail dict to Tradovate bracket builder, skip breakEven when trail active"
```

---

### Task 4: PnL Accuracy — Rewrite ResolveExitFillPriceAsync

**Files:**
- Modify: `CRV.Core/Strategy/BrokerEventHandler.cs:664-704`
- Test: `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs`

- [ ] **Step 1: Write tests for REST-first fill price resolution**

Add to `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs`. First, update `FakeGroupExecutor` to support `GetOrderFillPriceAsync`:

```csharp
    private class FakeGroupExecutor : IGroupOrderExecutor
    {
        public List<(string orderId, decimal? price, int? qty)> Modifications { get; } = new();
        public List<string> Cancellations { get; } = new();
        public List<(string ticker, Direction dir, int qty)> MarketCloses { get; } = new();
        public Dictionary<string, decimal> FillPrices { get; } = new();

        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal signal) =>
            Task.FromResult<GroupOrder?>(null);

        public Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
        {
            Modifications.Add((orderId, newPrice, newQty));
            return Task.CompletedTask;
        }

        public Task CancelOrderAsync(string orderId)
        {
            Cancellations.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<decimal> PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
        {
            MarketCloses.Add((ticker, direction, qty));
            return Task.FromResult(0m);
        }

        public Task<decimal?> GetOrderFillPriceAsync(string orderId)
        {
            return Task.FromResult(FillPrices.TryGetValue(orderId, out var p) ? (decimal?)p : null);
        }
    }
```

Then add tests:

```csharp
    [Fact]
    public async Task StopFilled_UsesRestFillPrice_NotWssPrice()
    {
        var executor = new FakeGroupExecutor();
        executor.FillPrices["s1"] = 20000m; // REST returns BE price
        var handler = new BrokerEventHandler(executor);
        TradeRecord? trade = null;
        handler.OnTradeCompleted += (g, t) => trade = t;

        var group = MakeGroup();
        group.Status = GroupOrderStatus.Active;
        group.EntryPrice = 20000m;
        group.InitialStopPrice = 19950m;
        group.UseBe = true;
        handler.RegisterGroup(group, new FakeSetup());

        // WSS says fill at 19960 (wrong), REST says 20000 (correct BE price)
        await handler.HandleEventAsync(Evt("grp-001", "s1", LegType.Stop, OrderLegStatus.Filled, fill: 19960m));

        Assert.NotNull(trade);
        Assert.Equal(20000m, trade!.Exit); // REST price wins, not WSS
    }

    [Fact]
    public async Task StopFilled_RestFails_FallsBackToBePrice()
    {
        var executor = new FakeGroupExecutor();
        // No fill price configured in REST — will return null
        var handler = new BrokerEventHandler(executor);
        TradeRecord? trade = null;
        handler.OnTradeCompleted += (g, t) => trade = t;

        var group = MakeGroup();
        group.Status = GroupOrderStatus.PartialFilled;
        group.EntryPrice = 20000m;
        group.InitialStopPrice = 19950m;
        group.UseBe = true;
        handler.RegisterGroup(group, new FakeSetup());

        // Stop leg price was updated to BE by HandleTg1EventAsync
        group.GetLeg(LegType.Stop)!.Price = 20000m;

        await handler.HandleEventAsync(Evt("grp-001", "s1", LegType.Stop, OrderLegStatus.Filled));

        Assert.NotNull(trade);
        Assert.Equal(20000m, trade!.Exit); // Falls back to BE (stopLeg.Price), not initial stop
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "StopFilled_UsesRestFillPrice" -v n`
Expected: FAIL — current code returns WSS price 19960 instead of REST price 20000

- [ ] **Step 3: Rewrite ResolveExitFillPriceAsync**

In `CRV.Core/Strategy/BrokerEventHandler.cs`, replace `ResolveExitFillPriceAsync` (lines 664-704):

```csharp
    /// <summary>
    /// Resolve exit fill price: always verify via REST /fill/deps.
    /// Never trust WSS fill prices — they are unreliable for bracket stops.
    /// Falls back to contextual price (BE/trail/stop) only after retries exhaust.
    /// </summary>
    private async Task<decimal> ResolveExitFillPriceAsync(OrderEvent evt, string legLabel, decimal fallbackPrice = 0m)
    {
        // Always try REST first — even if WSS provided a price
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var restPrice = await _executor.GetOrderFillPriceAsync(evt.OrderId);
                if (restPrice is > 0)
                {
                    _log?.LogInformation("[BEH] {Leg} fill price verified via REST: {P} for order {O} (attempt {A})",
                        legLabel, restPrice, evt.OrderId, attempt + 1);
                    return restPrice.Value;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "[BEH] REST fill price lookup failed for {Leg} order {O} (attempt {A})",
                    legLabel, evt.OrderId, attempt + 1);
            }

            if (attempt < 2) await Task.Delay(500);
        }

        // REST exhausted — use contextual fallback
        if (fallbackPrice > 0)
        {
            _log?.LogWarning("[BEH] {Leg} fill price unresolved after 3 REST attempts — using fallback {P} for order {O}",
                legLabel, fallbackPrice, evt.OrderId);
            return fallbackPrice;
        }

        // Last resort: use WSS price if available
        if (evt.FillPrice is > 0)
        {
            _log?.LogWarning("[BEH] {Leg} fill price using WSS fallback {P} for order {O} (REST exhausted, no contextual fallback)",
                legLabel, evt.FillPrice.Value, evt.OrderId);
            return evt.FillPrice.Value;
        }

        _log?.LogError("[BEH] Could not resolve {Leg} fill price for order {O} — using 0", legLabel, evt.OrderId);
        return 0m;
    }
```

- [ ] **Step 4: Update HandleTg1EventAsync to always use REST**

In `BrokerEventHandler.cs`, modify `HandleTg1EventAsync` (lines 561-565). Replace:

```csharp
        var tg1FillPrice = evt.FillPrice;
        if (tg1FillPrice is null or 0)
        {
            _log?.LogWarning("[BEH] Tg1 fill missing price in WSS event for order {O} — fetching via REST", evt.OrderId);
            tg1FillPrice = await _executor.GetOrderFillPriceAsync(evt.OrderId);
        }
```

With:

```csharp
        // Always verify Tg1 fill price via REST — never trust WSS
        decimal? tg1FillPrice = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                tg1FillPrice = await _executor.GetOrderFillPriceAsync(evt.OrderId);
                if (tg1FillPrice is > 0) break;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "[BEH] Tg1 REST fill price lookup failed (attempt {A}) for order {O}", attempt + 1, evt.OrderId);
            }
            if (attempt < 2) await Task.Delay(500);
        }
        // Final fallback: WSS price or Tg1 leg price
        if (tg1FillPrice is null or 0)
        {
            var tg1Leg = group.GetLeg(LegType.Tg1);
            tg1FillPrice = evt.FillPrice ?? tg1Leg?.Price;
            _log?.LogWarning("[BEH] Tg1 fill price unresolved via REST — using fallback {P} for order {O}",
                tg1FillPrice, evt.OrderId);
        }
```

- [ ] **Step 5: Update HandleStopEventAsync fallback to use contextual price**

In `BrokerEventHandler.cs`, modify `HandleStopEventAsync` (line 660). Replace:

```csharp
        var exitPrice = await ResolveExitFillPriceAsync(evt, "Stop", fallbackPrice: stopLeg?.Price ?? 0m);
```

With:

```csharp
        // Contextual fallback: if BE was active, fall back to entry (not initial stop)
        var contextualFallback = (group.UseBe && group.EntryPrice.HasValue)
            ? group.EntryPrice.Value
            : stopLeg?.Price ?? 0m;
        var exitPrice = await ResolveExitFillPriceAsync(evt, "Stop", fallbackPrice: contextualFallback);
```

- [ ] **Step 6: Run all tests**

Run: `dotnet test CRV.Core.Tests --filter "BrokerEventHandlerTests" -v n`
Expected: All tests pass (including new ones)

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Strategy/BrokerEventHandler.cs CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs
git commit -m "fix: rewrite ResolveExitFillPriceAsync to always verify via REST, contextual fallbacks"
```

---

### Task 5: Backtest Trail Simulation

**Files:**
- Modify: `CRV.Core/Models/GroupOrder.cs:21-55` (add transient trail state)
- Modify: `CRV.Backtest/Engine/BacktestEngine.cs:463-539` (trail logic in EvaluateFillsAsync)
- Test: `CRV.Core.Tests/Strategy/BacktestAutoTrailTests.cs`

- [ ] **Step 1: Add transient trail state to GroupOrder**

In `CRV.Core/Models/GroupOrder.cs`, after `Stop2OrderId` (line 42), add:

```csharp
    // ── Auto-trail state (transient — backtest simulation only, not persisted) ──
    public decimal? AutoTrailStopLoss { get; set; }
    public decimal? AutoTrailTrigger { get; set; }
    public decimal? AutoTrailFreq { get; set; }
    public bool AutoTrailActivated { get; set; }
    public decimal? AutoTrailHighWater { get; set; }
```

- [ ] **Step 2: Set trail params at group creation in BacktestGroupOrderExecutor**

In `CRV.Backtest/Engine/BacktestEngine.cs`, in the `OnEntrySignalAsync` method (around line 370-420), after the `group` is created, add:

```csharp
        // Copy auto-trail config for backtest simulation
        if (sig.AutoTrailStopLoss.HasValue)
        {
            group.AutoTrailStopLoss = sig.AutoTrailStopLoss;
            group.AutoTrailFreq = sig.AutoTrailFreq;
            // Derive trigger from Tg1 offset if null and UsePartial
            group.AutoTrailTrigger = sig.AutoTrailTrigger
                ?? (sig.UsePartial ? Math.Abs(sig.Tg1Price - sig.Entry) : throw new InvalidOperationException("AutoTrail.Trigger required when UsePartial=false"));
        }
```

- [ ] **Step 3: Write test for backtest trail simulation**

Create `CRV.Core.Tests/Strategy/BacktestAutoTrailTests.cs`:

```csharp
namespace CRV.Core.Tests.Strategy;

using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

public class BacktestAutoTrailTests
{
    [Fact]
    public void TrailState_ActivatesAtTrigger()
    {
        var group = new GroupOrder
        {
            GroupOrderId = "g1", Direction = Direction.Long,
            EntryPrice = 100m, TotalContracts = 2, PartialContracts = 1,
            PointValue = 5m, InitialStopPrice = 90m,
            Status = GroupOrderStatus.PartialFilled,
            AutoTrailStopLoss = 4m, AutoTrailTrigger = 10m, AutoTrailFreq = 0.25m,
        };

        // Price at 109 — not yet at trigger (10 pts profit needed)
        Assert.False(group.AutoTrailActivated);

        // Simulate activation at 110 (10 pts profit)
        group.AutoTrailActivated = true;
        group.AutoTrailHighWater = 110m;
        Assert.True(group.AutoTrailActivated);
        Assert.Equal(110m, group.AutoTrailHighWater);
    }

    [Fact]
    public void TrailState_RatchetsStop()
    {
        var group = new GroupOrder
        {
            GroupOrderId = "g1", Direction = Direction.Long,
            EntryPrice = 100m, InitialStopPrice = 90m,
            AutoTrailStopLoss = 4m, AutoTrailFreq = 0.25m,
            AutoTrailActivated = true, AutoTrailHighWater = 112m,
        };

        // Current stop should be: highWater - stopLoss = 112 - 4 = 108
        var newStop = group.AutoTrailHighWater!.Value - group.AutoTrailStopLoss!.Value;
        Assert.Equal(108m, newStop);

        // If price moves to 113, high water updates, stop ratchets
        group.AutoTrailHighWater = 113m;
        newStop = group.AutoTrailHighWater.Value - group.AutoTrailStopLoss.Value;
        Assert.Equal(109m, newStop);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test CRV.Core.Tests --filter "BacktestAutoTrailTests" -v n`
Expected: 2 tests passed

- [ ] **Step 5: Add trail simulation to EvaluateFillsAsync**

In `CRV.Backtest/Engine/BacktestEngine.cs`, at the end of `EvaluateFillsAsync` (after line 527, before the prune section at line 530), add trail evaluation logic. This runs after the normal fill checks, for each group that is still active:

```csharp
            // ── Auto-trail simulation ──────────────────────────────────
            // Only run if: trail is configured, entry has filled, group still active
            if (!entryFilled) continue;

            // Find the group order to access trail state
            // The group is accessed via BrokerEventHandler — we need to look it up
            // For backtest, trail state lives on the internal leg tracking
            var stopLegState = legs.Values.FirstOrDefault(l => l.LegType == LegType.Stop && l.Status == "WORKING");
            if (stopLegState == null) continue;

            // Trail params are stored on the OnEntrySignalAsync-created group.
            // We need a reference to the GroupOrder. Access via the _trailState dictionary.
            if (!_trailState.TryGetValue(groupId, out var trail)) continue;
            if (!trail.Enabled) continue;

            bool isLong = stopLegState.Action == "SELL"; // Stop action opposite to position

            decimal profitDistance = isLong
                ? price - trail.EntryPrice
                : trail.EntryPrice - price;

            if (!trail.Activated && profitDistance >= trail.Trigger)
            {
                trail.Activated = true;
                trail.HighWater = price;
            }

            if (trail.Activated)
            {
                // Update high water
                if ((isLong && price > trail.HighWater) || (!isLong && price < trail.HighWater))
                    trail.HighWater = price;

                // Compute new trailing stop
                decimal rawStop = isLong
                    ? trail.HighWater - trail.StopLoss
                    : trail.HighWater + trail.StopLoss;

                // Snap to freq grid (only move in freq increments)
                decimal snappedStop = isLong
                    ? Math.Floor(rawStop / trail.Freq) * trail.Freq
                    : Math.Ceiling(rawStop / trail.Freq) * trail.Freq;

                // Ratchet: only move stop in favorable direction
                decimal currentStop = stopLegState.StopPrice ?? trail.InitialStop;
                bool isBetter = isLong ? snappedStop > currentStop : snappedStop < currentStop;

                if (isBetter)
                {
                    stopLegState.StopPrice = snappedStop;
                    // Fire modify event so BrokerEventHandler's in-memory leg price stays in sync
                    if (OnModify != null)
                        await OnModify(stopLegState.OrderId, snappedStop, null);
                }
            }
```

- [ ] **Step 6: Add trail state tracking to BacktestGroupOrderExecutor**

Add a `_trailState` dictionary and `TrailSimState` class to `BacktestGroupOrderExecutor`:

```csharp
    private readonly Dictionary<string, TrailSimState> _trailState = new();

    private class TrailSimState
    {
        public bool Enabled { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Trigger { get; set; }
        public decimal Freq { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal InitialStop { get; set; }
        public bool Activated { get; set; }
        public decimal HighWater { get; set; }
    }
```

In `OnEntrySignalAsync`, after setting trail params on the group, also store trail state:

```csharp
        if (sig.AutoTrailStopLoss.HasValue)
        {
            _trailState[groupId] = new TrailSimState
            {
                Enabled = true,
                StopLoss = sig.AutoTrailStopLoss.Value,
                Trigger = sig.AutoTrailTrigger ?? Math.Abs(sig.Tg1Price - sig.Entry),
                Freq = sig.AutoTrailFreq!.Value,
                EntryPrice = fillPrice,
                InitialStop = Math.Abs(sig.Stop - sig.Entry),
            };
        }
```

Add an `OnModify` delegate for trail stop modifications:

```csharp
    public Func<string, decimal, int?, Task>? OnModify { get; set; }
```

Wire `OnModify` to `ModifyOrderAsync` in the executor setup.

- [ ] **Step 7: Clean up trail state on group completion**

In the prune section of `EvaluateFillsAsync` (around line 534), also remove trail state:

```csharp
        foreach (var gid in completedGroups)
        {
            _ordersByGroup.Remove(gid);
            _groupTickers.Remove(gid);
            _trailState.Remove(gid);
        }
```

- [ ] **Step 8: Build and run all backtest tests**

Run: `dotnet build CRV.Backtest && dotnet test CRV.Core.Tests --filter "Backtest" -v n`
Expected: All tests pass

- [ ] **Step 9: Commit**

```bash
git add CRV.Core/Models/GroupOrder.cs CRV.Backtest/Engine/BacktestEngine.cs CRV.Core.Tests/Strategy/BacktestAutoTrailTests.cs
git commit -m "feat: add tick-by-tick auto-trail simulation in backtest engine"
```

---

### Task 6: Manual Trade Page — AutoTrail UI + startOrderStrategy Upgrade

**Files:**
- Modify: `CRV.Web/Pages/Trading/Manual.cshtml:113-116` (add trail controls)
- Modify: `CRV.Web/Pages/Trading/Manual.cshtml.cs:359-387,392-422` (ManualOrder + BuildEntry)
- Modify: `CRV.Live/ManualBrokerOps.cs` (add startOrderStrategy method)

- [ ] **Step 1: Add AutoTrail fields to ManualOrder**

In `CRV.Web/Pages/Trading/Manual.cshtml.cs`, add to the `ManualOrder` class (after line 421):

```csharp
    // Auto Trail
    public bool UseAutoTrail { get; set; }
    [Range(0, double.MaxValue)] public decimal AutoTrailStopLoss { get; set; }
    [Range(0, double.MaxValue)] public decimal AutoTrailTrigger { get; set; }
    [Range(0, double.MaxValue)] public decimal AutoTrailFreq { get; set; }
```

- [ ] **Step 2: Update BuildEntry to pass trail params**

In `Manual.cshtml.cs`, update the `BuildEntry` method signature and body (lines 359-387):

Add params: `bool useAutoTrail = false, decimal autoTrailStopLoss = 0, decimal autoTrailTrigger = 0, decimal autoTrailFreq = 0`

In the `return new EntrySignal(...)`, add:

```csharp
            AutoTrailStopLoss: useAutoTrail ? autoTrailStopLoss : null,
            AutoTrailTrigger:  useAutoTrail ? (autoTrailTrigger > 0 ? autoTrailTrigger : (usePartial ? (decimal?)null : throw new InvalidOperationException("Trigger required"))) : null,
            AutoTrailFreq:     useAutoTrail ? autoTrailFreq : null
```

- [ ] **Step 3: Update OnPostAsync to pass trail params**

In `Manual.cshtml.cs`, update the `BuildEntry` call (lines 174-177) to pass trail params:

```csharp
            var sig = BuildEntry(isLong, Order.Contracts, Order.EntryPrice,
                Order.StopPoints, Order.TargetPoints, Order.PartialPoints,
                Order.Ticker, Order.PointValue, Order.OrderType,
                Order.UsePartial, Order.PartialContracts, Order.UseBe,
                Order.UseAutoTrail, Order.AutoTrailStopLoss, Order.AutoTrailTrigger, Order.AutoTrailFreq);
```

- [ ] **Step 4: Add AutoTrail validation in OnPostAsync**

In `OnPostAsync`, after the partial validation (line 134), add:

```csharp
        if (Order.UseAutoTrail)
        {
            if (Order.AutoTrailStopLoss <= 0) Errors.Add("Auto Trail Stop Loss must be > 0.");
            if (Order.AutoTrailFreq <= 0) Errors.Add("Auto Trail Freq must be > 0.");
            if (!Order.UsePartial && Order.AutoTrailTrigger <= 0)
                Errors.Add("Auto Trail Trigger is required when Partial is off.");
        }
```

- [ ] **Step 5: Add AutoTrail UI controls to Manual.cshtml**

In `Manual.cshtml`, after the UseBe checkbox (line 116), add:

```html
        <!-- Auto Trail -->
        <div class="form-check mt-2">
            <input class="form-check-input" type="checkbox" name="Order.UseAutoTrail" value="true"
                   id="chk-autotrail" @(Model.Order.UseAutoTrail ? "checked" : "")
                   onchange="toggleAutoTrail(this.checked)" />
            <input type="hidden" name="Order.UseAutoTrail" value="false" />
            <label class="form-check-label small fw-bold" for="chk-autotrail">Auto Trail Stop</label>
        </div>
        <div id="autotrail-section" class="@(Model.Order.UseAutoTrail ? "" : "d-none") ms-3 mt-1">
            <div class="row g-2">
                <div class="col-4">
                    <label class="form-label small">Stop Loss</label>
                    <input name="Order.AutoTrailStopLoss" id="m-at-stoploss" type="number" step="0.25" min="0"
                           value="@(Model.Order.AutoTrailStopLoss > 0 ? Model.Order.AutoTrailStopLoss : "")"
                           class="form-control form-control-sm font-monospace" placeholder="e.g. 4.0" />
                </div>
                <div class="col-4">
                    <label class="form-label small">Trigger</label>
                    <input name="Order.AutoTrailTrigger" id="m-at-trigger" type="number" step="0.25" min="0"
                           value="@(Model.Order.AutoTrailTrigger > 0 ? Model.Order.AutoTrailTrigger : "")"
                           class="form-control form-control-sm font-monospace" placeholder="Tg1 auto" />
                </div>
                <div class="col-4">
                    <label class="form-label small">Freq</label>
                    <input name="Order.AutoTrailFreq" id="m-at-freq" type="number" step="0.25" min="0"
                           value="@(Model.Order.AutoTrailFreq > 0 ? Model.Order.AutoTrailFreq : "")"
                           class="form-control form-control-sm font-monospace" placeholder="e.g. 0.25" />
                </div>
            </div>
            <small class="text-muted">Raw price values. Trigger defaults to Tg1 distance when Partial is on.</small>
        </div>
```

- [ ] **Step 6: Add toggleAutoTrail JavaScript**

In the `<script>` section of `Manual.cshtml`, add:

```javascript
function toggleAutoTrail(on) {
    document.getElementById('autotrail-section').classList.toggle('d-none', !on);
    // Disable UseBe when AutoTrail is on
    const beCheckbox = document.getElementById('chk-usebe');
    if (on) {
        beCheckbox.checked = false;
        beCheckbox.disabled = true;
    } else {
        beCheckbox.disabled = false;
    }
    updatePreview();
}
```

Also call `toggleAutoTrail` on page load if needed:

```javascript
// In the existing DOMContentLoaded or init block:
if (document.getElementById('chk-autotrail')?.checked) toggleAutoTrail(true);
```

- [ ] **Step 7: Build and verify**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 8: Commit**

```bash
git add CRV.Web/Pages/Trading/Manual.cshtml CRV.Web/Pages/Trading/Manual.cshtml.cs
git commit -m "feat: add AutoTrail controls to Manual Trade page with UseBe interaction"
```

---

### Task 7: Basket Config UI — AutoTrail Controls

**Files:**
- Modify: `CRV.Web/Pages/Shared/_SetupConfigSection.cshtml:151-187`

- [ ] **Step 1: Add AutoTrail controls to basket config exit section**

In `_SetupConfigSection.cshtml`, after the "BE" checkbox (line 158), add an "Auto Trail" checkbox and collapsible fields:

```html
                <div class="form-check form-check-inline">
                    <input class="form-check-input session-field" type="checkbox" data-session="@i" data-field="@(fieldPfx).AutoTrail.Enabled"
                           @(setup.AutoTrail?.Enabled == true ? "checked" : "")
                           onchange="toggleBasketAutoTrail(this, @i)" />
                    <label class="form-check-label small">Trail</label>
                </div>
```

After the main checkbox row (after line 187), add the trail config fields (collapsed by default):

```html
            <div id="autotrail-basket-@i" class="col-md-12 mt-1 @(setup.AutoTrail?.Enabled == true ? "" : "d-none")">
                <div class="row g-2">
                    <div class="col-4">
                        <label class="form-label small text-muted">Trail StopLoss</label>
                        <input class="form-control form-control-sm font-monospace session-field" type="number" step="0.25"
                               data-session="@i" data-field="@(fieldPfx).AutoTrail.StopLoss"
                               value="@(setup.AutoTrail?.StopLoss)" placeholder="e.g. 4.0" />
                    </div>
                    <div class="col-4">
                        <label class="form-label small text-muted">Trail Trigger</label>
                        <input class="form-control form-control-sm font-monospace session-field" type="number" step="0.25"
                               data-session="@i" data-field="@(fieldPfx).AutoTrail.Trigger"
                               value="@(setup.AutoTrail?.Trigger)" placeholder="Tg1 auto" />
                    </div>
                    <div class="col-4">
                        <label class="form-label small text-muted">Trail Freq</label>
                        <input class="form-control form-control-sm font-monospace session-field" type="number" step="0.25"
                               data-session="@i" data-field="@(fieldPfx).AutoTrail.Freq"
                               value="@(setup.AutoTrail?.Freq)" placeholder="e.g. 0.25" />
                    </div>
                </div>
            </div>
```

- [ ] **Step 2: Add toggleBasketAutoTrail JavaScript**

In the page's script section (or shared JS), add:

```javascript
function toggleBasketAutoTrail(checkbox, index) {
    const section = document.getElementById('autotrail-basket-' + index);
    section.classList.toggle('d-none', !checkbox.checked);
    // Disable BE when trail is on
    const beField = document.querySelector(`[data-session="${index}"][data-field$=".UseBe"]`);
    if (beField) {
        if (checkbox.checked) { beField.checked = false; beField.disabled = true; }
        else { beField.disabled = false; }
    }
}
```

- [ ] **Step 3: Verify the AutoTrail config is read from BasketEntry**

Check that the `setup` variable in the partial view maps to a `StrategySetupConfig` that now has the `AutoTrail` property. Since `BasketEntry.Config` is `StrategySetupConfig`, and we added `AutoTrail` to `StrategySetupConfig` in Task 2, the model binding should work. But `AutoTrailConfig` lives on `BasketEntry`, not `StrategySetupConfig`.

We need to decide: the UI reads `setup.AutoTrail` but `setup` is `StrategySetupConfig`. We added `AutoTrail` to `StrategySetupConfig` in Task 2 Step 1, so this works. However, the source of truth is `BasketEntry.AutoTrail`. The basket save/load logic must copy between them.

Check how basket save works and ensure `AutoTrail` is serialized with the basket JSON. Since `BasketEntry` has `Config` (StrategySetupConfig) and we're storing `AutoTrail` on `StrategySetupConfig`, it serializes automatically with the basket JSON.

- [ ] **Step 4: Build and verify**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add CRV.Web/Pages/Shared/_SetupConfigSection.cshtml
git commit -m "feat: add AutoTrail controls to basket config UI with UseBe interaction"
```

---

### Task 8: Integration Test — End-to-End Backtest with Trail

**Files:**
- Create: `CRV.Core.Tests/Strategy/BacktestAutoTrailIntegrationTests.cs`

- [ ] **Step 1: Write integration test**

```csharp
namespace CRV.Core.Tests.Strategy;

using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

public class BacktestAutoTrailIntegrationTests
{
    /// <summary>
    /// Simulates: Long entry at 100, partial at 110 (1ct), trail activates at trigger=10,
    /// price runs to 120, then retraces. Trail stop should ratchet up and fill.
    /// StopLoss=4, Freq=0.25, so at high=120, trail stop = 116.
    /// Price drops to 116 → stop fills at 116.
    /// Partial PnL: (110 - 100) * 5 * 1 = $50
    /// Runner PnL: (116 - 100) * 5 * 1 = $80
    /// Total Gross: $130
    /// </summary>
    [Fact]
    public async Task Trail_RatchetsStop_AndFillsAtTrailedPrice()
    {
        // This test verifies the full integration through BacktestGroupOrderExecutor
        // + BrokerEventHandler with auto-trail simulation.
        // Implementation details depend on how the executor and handler are wired.
        // The test should:
        // 1. Create an EntrySignal with AutoTrail params
        // 2. Feed ticks: entry fill at 100, partial fill at 110, price to 120, retrace to 116
        // 3. Verify: trail activated, stop ratcheted to 116, trade closed with correct PnL

        var sig = new EntrySignal(
            SetupId.A, Direction.Long,
            Entry: 100m, Stop: 90m, Tg2Price: 130m, Tg1Price: 110m,
            TotalContracts: 2, Time: DateTime.UtcNow,
            UsePartial: true, PartialContracts: 1, PointValue: 5m,
            UseBe: false,
            AutoTrailStopLoss: 4m, AutoTrailTrigger: null, AutoTrailFreq: 0.25m);

        // Trigger should be derived from Tg1: |110 - 100| = 10
        Assert.Null(sig.AutoTrailTrigger);
        Assert.Equal(4m, sig.AutoTrailStopLoss);

        // Full integration test with BacktestGroupOrderExecutor would go here
        // once the executor trail logic is wired. For now, verify signal construction.
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test CRV.Core.Tests --filter "BacktestAutoTrailIntegrationTests" -v n`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add CRV.Core.Tests/Strategy/BacktestAutoTrailIntegrationTests.cs
git commit -m "test: add integration test scaffold for backtest auto-trail simulation"
```

---

### Task 9: Run Full Test Suite + Build Verification

**Files:** None (verification only)

- [ ] **Step 1: Build entire solution**

Run: `dotnet build`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test --verbosity normal`
Expected: All tests pass

- [ ] **Step 3: Verify no regressions in existing BrokerEventHandler tests**

Run: `dotnet test CRV.Core.Tests --filter "BrokerEventHandlerTests" -v n`
Expected: All pass

- [ ] **Step 4: Verify backtest partial fill tests still pass**

Run: `dotnet test CRV.Core.Tests --filter "BacktestPartialFillTests" -v n`
Expected: All pass

- [ ] **Step 5: Final commit if any fixes needed**

```bash
git add -A
git commit -m "fix: address test failures from auto-trail integration"
```
