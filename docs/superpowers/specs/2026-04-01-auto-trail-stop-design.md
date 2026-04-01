# Auto Trail Stop & PnL Accuracy Hardening

**Date:** 2026-04-01
**Status:** Draft

## Problem

1. **Partial fill PnL shows $0:** When a trade with partial fill hits the stop (BE or otherwise), the gross PnL sometimes records as $0. Root cause: `ResolveExitFillPriceAsync` falls back to `stopLeg.Price` (the initial/stale stop price) when WSS and `/fill/deps` fail to return the actual fill price. When the stop was moved to BE, the fallback still uses the old price, so the runner loss cancels the partial profit.

2. **No trailing stop support:** The system currently supports only fixed stops and move-to-BE. There's no way to trail the stop on the runner (Tg2) leg, which leads to giving back profits on winning trades.

## Solution

Two changes delivered together:

1. **Auto Trail Stop** — per-ticker trailing stop config, sent natively to Tradovate's bracket API, simulated tick-by-tick in backtest.
2. **PnL accuracy hardening** — fix exit fill price resolution to always get the real fill price from `/fill/deps`, with retries instead of stale fallbacks.

---

## 1. Config Model

### AutoTrailConfig

New class added to `CRV.Core/Models/`:

```csharp
public class AutoTrailConfig
{
    public bool Enabled { get; set; }
    public decimal StopLoss { get; set; }    // trailing distance in raw price value
    public decimal Freq { get; set; }        // granularity of trail updates (raw price)
    public decimal? Trigger { get; set; }    // profit in price before trail activates
                                             // null = derive from Tg1 offset when UsePartial=true
                                             // required when UsePartial=false
}
```

All values are raw price differences (not ticks), divisible by the instrument's tick size.

### BasketEntry

Add per-ticker trail config:

```csharp
public AutoTrailConfig? AutoTrail { get; set; }
```

Lives on `BasketEntry` because trail distances are instrument-specific (MES vs MNQ vs MGC have different tick sizes and volatility).

### EntrySignal

Add 3 nullable fields to carry trail params from config to executor:

```csharp
public decimal? AutoTrailStopLoss { get; set; }
public decimal? AutoTrailTrigger { get; set; }
public decimal? AutoTrailFreq { get; set; }
```

### ManualOrder

Add to the form model:

```csharp
public bool UseAutoTrail { get; set; }
public decimal AutoTrailStopLoss { get; set; }
public decimal AutoTrailTrigger { get; set; }
public decimal AutoTrailFreq { get; set; }
```

### UseBe interaction

When `AutoTrail.Enabled = true`, `UseBe` is disabled/greyed out in the UI. The trail with `trigger = Tg1 offset` is strictly better than BE (it moves beyond BE as price advances). The backend skips the `breakEven` bracket field when `autoTrail` is present.

---

## 2. Live Execution (Tradovate)

### Engine path — TradovateExecutor.OnEntrySignalAsync

Currently builds brackets via `startOrderStrategy`. When `AutoTrail` params are present on the signal:

**With partial (2 brackets):**
- Bracket 1 (Tg1 leg): unchanged — fixed target + stop, no trail
- Bracket 2 (Tg2 leg): add `autoTrail` dict, skip `breakEven`

```json
{
    "qty": 1,
    "profitTarget": -31.0,
    "stopLoss": 15.5,
    "trailingStop": false,
    "autoTrail": {
        "stopLoss": 4.0,
        "trigger": 15.5,
        "freq": 0.25
    }
}
```

`trigger` is derived as `Math.Abs(tg1Offset)` when `AutoTrailTrigger` is null and `UsePartial` is true.

**Without partial (single bracket):**
- Single bracket gets `autoTrail` dict
- `Trigger` must be explicitly set (validated at config level)

### Manual path — ManualBrokerOps

When `UseAutoTrail` is true, upgrade from `placeOSO` to `startOrderStrategy` (same API the engine uses). Build the same bracket structure with `autoTrail` on the appropriate bracket(s). When trail is off, keep using `placeOSO` as today.

### Signal flow

```
BasketEntry.AutoTrail (per-ticker config)
    -> Strategy.TryEntry() copies to EntrySignal
        -> AutoTrailStopLoss, AutoTrailFreq, AutoTrailTrigger (derived from Tg1 if null + UsePartial)
    -> TradovateExecutor.OnEntrySignalAsync()
        -> Adds autoTrail dict to bracket 2 (or single bracket)
        -> Skips breakEven field when autoTrail present
    -> Tradovate API — broker manages trailing server-side
```

---

## 3. Backtest Simulation

### Trail state on GroupOrder

Add transient (non-persisted) fields for backtest trail simulation:

```csharp
public bool AutoTrailActivated { get; set; }
public decimal? AutoTrailHighWater { get; set; }
```

### Per-tick logic in BacktestEngine.EvaluateFillsAsync

After normal fill evaluation, for active groups with auto-trail:

1. If auto-trail not enabled or group completed → skip
2. Compute current profit distance from entry price
3. If `!AutoTrailActivated` and profit >= trigger → activate, set `HighWater = currentPrice`
4. If `AutoTrailActivated`:
   - Update high water if price moved favorably (up for long, down for short)
   - Compute raw new stop: `HighWater - StopLoss` (long) or `HighWater + StopLoss` (short)
   - Snap to `Freq` grid: only move stop in `Freq` increments from the activation point
   - Ratchet only: new stop must be better than current stop (never move against position)
   - Update the Stop leg's price via `ModifyOrderAsync` (same path as BE moves)

Trail-moved stops can be hit on the same or subsequent tick through the normal fill evaluation.

### Trail params on GroupOrder

Add fields to carry trail config for backtest simulation:

```csharp
public decimal? AutoTrailStopLoss { get; set; }
public decimal? AutoTrailTrigger { get; set; }
public decimal? AutoTrailFreq { get; set; }
```

Set at group creation from `EntrySignal`.

---

## 4. PnL Accuracy Hardening

### Problem

`ResolveExitFillPriceAsync` in `BrokerEventHandler` has a fallback chain:

1. `evt.FillPrice` from WSS → often null for bracket stops
2. `GetOrderFillPriceAsync(evt.OrderId)` → calls `/fill/deps` then `/order/item`
3. **Fallback to `stopLeg.Price`** → stale (initial stop, not BE/trailed price)

Step 3 produces wrong PnL when the stop was moved (BE or trail) but WSS/REST didn't return the actual fill price.

### Architecture: WSS for events, REST for prices

**Never trust WSS fill prices.** WSS is fast but unreliable for bracket stop prices (Tradovate often sends null/0). REST `/fill/deps` is the source of truth.

The new flow for all fill events (Stop, Tg1, Tg2):

```
WSS event arrives (fast, price may be null or inaccurate)
  → Immediately update state (group status, cancel paired legs, etc.)
  → Always call /fill/deps?masterid={evt.OrderId} for actual price
  → Record trade with verified price
```

Key change: remove the `evt.FillPrice > 0` short-circuit in `ResolveExitFillPriceAsync` (current line 672). Always verify via REST, even when WSS provides a price. The extra 200-500ms is irrelevant — the fill already happened at the broker.

### Fix

**Replace `ResolveExitFillPriceAsync` with REST-first resolution:**

1. Call `/fill/deps?masterid={evt.OrderId}` — use the **event's order ID** (the actual filled order, which may differ from `stopLeg.OrderId` if broker replaced orders during trail)
2. If first attempt returns no data, retry up to 3 times with 500ms delay — bracket stop fills often appear in `/fill/deps` after a brief delay
3. Final fallback logic (only if all retries fail):
   - If auto-trail was active: use `AutoTrailHighWater - StopLoss` (computed expected trail stop)
   - If BE was active: use `group.EntryPrice` (known BE level)
   - Otherwise: use `stopLeg.Price` (original stop — only correct fallback when stop hasn't moved)
   - Log a warning for any fallback usage so we can monitor

### Apply to all fill types

Same REST-first pattern for Stop, Tg1, and Tg2 fills. The existing `HandleTg1EventAsync` already calls REST (line 564-566) but only when WSS price is null — change it to always verify via REST.

---

## 5. UI Changes

### Basket Config UI (_SetupConfigSection.cshtml)

In the exit section, below `UsePartial` / `UseBe`:

- "Auto Trail" toggle checkbox
- When enabled, show fields:
  - `StopLoss` — trailing distance (raw price)
  - `Trigger` — optional when `UsePartial` is on (label: "leave blank to use Tg1 distance"), required otherwise
  - `Freq` — trail granularity (raw price)
- Validation: all values must be divisible by the ticker's `TickSize`
- When Auto Trail is on, `UseBe` checkbox is disabled/greyed out

### Manual Trade page (Manual.cshtml)

Below the existing "Move Stop to BE" checkbox:

- "Auto Trail" checkbox
- When enabled, show `StopLoss`, `Trigger`, `Freq` fields (all required for manual trades)
- When `UsePartial` is also checked, `Trigger` becomes optional with hint "defaults to Tg1 distance"
- When Auto Trail is checked, `UseBe` is disabled/greyed out
- When Auto Trail is on, order placement upgrades from `placeOSO` to `startOrderStrategy`

### Preview panel update

The existing preview table (R:R calculation) should reflect trail params when set.

---

## Files Changed

| Area | Files |
|------|-------|
| Config model | `CRV.Core/Models/BasketEntry.cs`, `CRV.Core/Models/Signals.cs`, `CRV.Core/Models/StrategySetupConfig.cs` |
| Entry signal | `CRV.Core/Strategy/PullbackStrategy.cs`, `CRV.Core/Strategy/RetestStrategy.cs`, other strategies |
| Live executor | `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs` |
| Broker events | `CRV.Core/Strategy/BrokerEventHandler.cs` |
| Backtest | `CRV.Backtest/Engine/BacktestEngine.cs` |
| Manual trade | `CRV.Live/ManualBrokerOps.cs`, `CRV.Web/Pages/Trading/Manual.cshtml`, `CRV.Web/Pages/Trading/Manual.cshtml.cs` |
| Basket UI | `CRV.Web/Pages/Shared/_SetupConfigSection.cshtml` |
| Tests | `CRV.Core.Tests/Strategy/BrokerEventHandlerTests.cs`, `CRV.Core.Tests/Strategy/BacktestPartialFillTests.cs`, new trail tests |

## Not Changing

- `GroupOrder` / `OrderLeg` persisted schema (trail state is transient)
- `ExitProcessor.ProcessBar()` (legacy Pine port, untouched)
- `TradeRecord` schema (PnL fields already capture the result correctly — the issue is the input price)
