# CRV.Trading — Engine Evaluation & Mock Broker Design
Date: 2026-03-10

---

## Already Completed This Session

| Item | Status | Notes |
|------|--------|-------|
| 1. Dashboard Setup Status On/Off | ✅ Done | `SetupAEnabled`/`SetupBEnabled` added to `EngineSnapshot`; dashboard shows "DISABLED" + 45% opacity when off |
| 5. ORB mid-session startup fix | ✅ Done | TradeStation `barsback` now dynamic; Tradovate switched from `asMuchAsElements:20` to `asFarAsTimestamp` anchored to ORB start |
| 6. Tradovate orders on `/trading/orders` | ✅ Already done | `Orders.cshtml.cs` already has `GetOrdersTradovateAsync` / `CancelOrderTradovateAsync` wired up |

---

## Items 2-4: Execution TF Arm + Realtime Price Evaluation

### Problem Statement

Currently the engine evaluates **everything** (arm conditions, entries, exits, stop moves) on the
execution TF bar close. This means:

- **Live**: Entry signals lag by up to 1 execution-TF bar. A 5-min bar close at 9:35 fires an
  entry at 9:35:00, missing fills that happen at 9:33 or 9:34.
- **Backtest**: Same lag — unrealistic for momentum/breakout strategies.

### Desired Behavior

| Phase | Live | Backtest |
|-------|------|----------|
| **Arm** | Execution TF bar close (confirmed candle) | Execution TF bar close |
| **Entry / Exit / Stop evaluation** | Realtime last price (L1 WSS tick, ~every 250 ms) | 1-min bar OHLCV (intra-TF evaluation) |

### Approach A — Split ProcessBar into two passes (Recommended)

Separate the engine pipeline into:

1. **`ProcessArmAsync(bar)`** — only runs on confirmed execution-TF bars; sets `_stA`/`_stB` (arm state).
2. **`ProcessPriceAsync(lastPrice, time)`** — runs on every L1 tick (live) or every 1-min bar
   (backtest); checks entry, exit, partial fill, stop move.

**Live flow:**
```
L1 tick → ILastPriceProvider.UpdatePrice()
        → engine.ProcessPriceAsync(price, now)   // entry/exit checks
TF bar close → engine.ProcessArmAsync(bar)       // arm state update
```

**Backtest flow (1-min CSV):**
```
1-min bar → engine.ProcessPriceAsync(bar.Open/High/Low/Close, bar.Time)  // entry/exit on each 1-min
On execution-TF boundary (bar.Time % tfMinutes == 0):
         → engine.ProcessArmAsync(resampled bar)                         // arm check
```

**Trade-offs:**
- ✅ Correct fill timing in both live and backtest
- ✅ Keeps 52 existing tests passing (just refactor, not new logic)
- ⚠️ Intra-TF entry in backtest assumes market order fill at the exact price seen; may overfit
- ⚠️ Requires engine to track "last arm bar time" to avoid re-arming on every L1 tick

### Approach B — Tick-level simulation in backtest only

Keep live architecture as-is, but in backtest run 1-min bars through a
`IntraBarFillSimulator` that checks entry/exit at open, high, low, close of each 1-min bar
(standard assumption: fill at open on the next bar after signal).

**Trade-offs:**
- ✅ Simpler — existing `ProcessBarAsync` unchanged for live
- ✅ More realistic backtest fill model
- ⚠️ Live still lags by one execution-TF bar

### Approach C — Keep current bar-close model

No change. Works for low-frequency strategies (15-min+ TF).

**Trade-offs:**
- ✅ Zero refactoring risk
- ⚠️ Entry timing unrealistic for 1-min/5-min TF live trading

### Recommendation

**Approach A** for the best live + backtest accuracy, implemented incrementally:

1. First refactor `OrbStrategyEngine.ProcessBarAsync` into `ProcessArmAsync` + `ProcessPriceAsync`
   (passes all existing tests).
2. Wire `ProcessPriceAsync` to L1 ticks in `SchwabBarFeed`/`TradeStationBarFeed`/`TradovateBarFeed`
   (new `IBarFeed.OnTick(decimal price, DateTime time)` optional callback or dedicated interface).
3. In `BacktestEngine`, run the execution-TF resampler for arm, then pass each 1-min bar's
   high/low/close to `ProcessPriceAsync`.

---

## Items 7-8: Mock Broker Simulation Mode

### Problem Statement

The current `MockBrokerExecutor`:
- Accepts orders but never fills them (no realtime fill simulation)
- Has no Working/Filled/Canceled state
- No OCO bracket support

The user wants a **full paper-trading simulation** mode that:
- Fills orders based on realtime last price (same WSS L1 feed as live)
- Supports OCO brackets (entry + stop + target)
- Has orders listed on `/trading/orders` with proper status lifecycle
- Has a settings page for simulation mode (CSV data source, date range, etc.)
- Optionally runs a "mock dashboard" that mirrors the live dashboard

### Design

#### MockBrokerExecutor v2

**State model per order:**
```csharp
public class MockOrder
{
    public string    OrderId    { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string    Symbol     { get; set; } = "";
    public string    Action     { get; set; } = ""; // "BUY" | "SELL"
    public int       Quantity   { get; set; }
    public decimal?  LimitPrice { get; set; }
    public decimal?  StopPrice  { get; set; }
    public string    Status     { get; set; } = "WORKING"; // WORKING | FILLED | CANCELED
    public decimal?  FillPrice  { get; set; }
    public DateTime  PlacedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? FilledAt   { get; set; }
    public string?   OcoGroupId { get; set; }  // OCO partner order ID
}
```

**Fill logic (called on every L1 tick):**
- `BUY STOP` → fills when `lastPrice >= stopPrice`
- `SELL STOP` → fills when `lastPrice <= stopPrice`
- `BUY LIMIT` → fills when `lastPrice <= limitPrice`
- `SELL LIMIT` → fills when `lastPrice >= limitPrice`
- When one OCO leg fills → cancel the other leg immediately

**OCO bracket:** `PlaceOsoAsync(entry, stop, target)` creates 3 `MockOrder`s linked by `OcoGroupId`.

#### `/trading/orders` for Mock broker

`GetOrdersMockAsync()` in `ManualBrokerOps.cs` currently returns an empty list. Update it to
return the in-memory `MockOrder` list from `MockBrokerExecutor` (inject via DI).

#### Simulation Settings (item 8)

A new `/Settings/Simulation` page with:
- Data source: CSV file path (existing `CsvBarLoader`)
- Symbol / date range
- Execution TF minutes
- A "Run Simulation" button that starts the engine in backtest mode against the CSV but shows the
  result in a **live-style dashboard** rather than the static backtest results table
- "Stop Simulation" button

Implementation strategy:
- `SimulationService` (BackgroundService, like `LiveEngineOrchestrator`) reads the CSV bar-by-bar
  at configurable playback speed (e.g., real-time 1-min = 1s delay per bar)
- Uses `MockBrokerExecutor` as executor
- Feeds bars through `OrbStrategyEngine` and pushes `EngineSnapshot` via SignalR
- Dashboard page can display simulation data exactly like live

This is a large feature (~3 days of work). Suggest implementing in this order:
1. MockBrokerExecutor fill simulation + OCO (1 day)
2. `/trading/orders` showing mock orders (0.5 days)
3. `/Settings/Simulation` + `SimulationService` (1.5 days)

---

## Item 9: Architecture Recommendations

### High Priority

**1. Extract price evaluation into a `PriceEvaluator` service**
The engine currently mixes arm logic (bar-close) with price evaluation (should be tick-based).
Splitting into `ProcessArmAsync` + `ProcessPriceAsync` (see items 2-4 above) is the single most
impactful change for live accuracy.

**2. Deduplicate historical loaders**
`SchwabHistoricalLoader`, `TradeStationHistoricalLoader`, and `TradovateHistoricalLoader` each
implement their own HTTP/WS connection and bar parsing. A shared `IHistoricalLoader` interface
(same as `IBarFeed` but for finite date ranges) would enable:
- Consistent error handling
- Unified `BackfillAsync` in the orchestrator (remove the broker-specific switch)
- Easier testing

**3. Replace `StrategyConfigService` singleton with event-sourcing**
Currently `StrategyConfig` is loaded once at startup and changes require a page-reload to take
effect. An `IOptionsMonitor<StrategyConfig>` pattern (or a simple `ConfigChanged` event) would
allow the engine to pick up changes to non-active-trade settings (e.g., ATR filter, max trades)
without a restart.

**4. Add cancellation + timeout to all broker HTTP calls**
Several calls use `new HttpClient()` without a timeout. A shared `IHttpClientFactory` instance
with named clients ("schwab", "tradestation", "tradovate") would enforce timeouts and enable
connection pooling.

### Medium Priority

**5. Promote `MEMORY.md` to structured docs**
MEMORY.md is at 201 lines (over the 200-line limit). Break into topic files:
- `docs/architecture.md` — project structure + key classes
- `docs/brokers.md` — Schwab, TradeStation, Tradovate auth + API notes
- `docs/engine.md` — ORB engine internals, state machine, bar lifecycle
- Keep MEMORY.md as a short index with links

**6. Add integration tests for bar feed warmup**
52 unit tests cover indicators and engine logic but not the warmup flow. Add 2-3 xUnit tests
that feed a mock bar stream (with historical flags) and verify ORB/ATR/VWAP values after warmup.

**7. Harden `BacktestEngine` concurrency**
`BacktestRunnerService` runs one backtest at a time via a `CancellationTokenSource` but doesn't
prevent a second click from starting a race. Add a `SemaphoreSlim(1)` guard.

### Low Priority

**8. Consider moving to EventSource/SSE for dashboard**
SignalR (WebSocket) is excellent but requires server-side hub state. For a read-only dashboard,
Server-Sent Events (SSE) via `IAsyncEnumerable<EngineSnapshot>` would be simpler to debug and
doesn't require sticky sessions.

**9. Futures symbol resolution via contract roll calendar**
`NQH26` will expire in March 2026. Hardcoding the expiry in `StrategyConfig.Ticker` requires
manual updates each quarter. A `FuturesContractRoller` service that reads the next active
contract from the broker's instrument list would eliminate this manual step.

**10. Structured logging → Seq or OpenTelemetry**
Add a `OTLP` exporter (or Seq sink) to capture engine decisions in a queryable log store.
This is invaluable for debugging "why didn't it enter/exit at X price" questions.

---

## Implementation Order (Suggested)

| Priority | Item | Effort | Impact |
|----------|------|--------|--------|
| 1 | Items 2-4: Split engine into ProcessArmAsync + ProcessPriceAsync | 3 days | High |
| 2 | Item 7: MockBrokerExecutor fill simulation + OCO | 1 day | High |
| 3 | Item 6 Mock orders on `/trading/orders` | 0.5 days | Medium |
| 4 | Item 8: Simulation settings + SimulationService | 1.5 days | Medium |
| 5 | Rec #2: IHistoricalLoader interface | 1 day | Medium |
| 6 | Rec #3: Config live-reload | 0.5 days | Medium |
| 7 | Rec #5: Structured docs | 0.5 days | Low |
