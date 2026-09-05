# Architecture

## Strategy Engine (`CRV.Core`)

### Composable Engine

`ComposableEngine` is a thin coordinator that manages multiple instruments and setups simultaneously. It replaces the earlier monolithic `OrbStrategyEngine` with a modular design:

```
ComposableEngine
 ├─ TickerGroup("NQ")           ← shared ATR, VWAP, ORB, modules for NQ/MNQ
 │    ├─ PullbackStrategy       (Setup A)
 │    └─ RetestStrategy         (Setup B)
 ├─ TickerGroup("ES")           ← shared indicators for ES/MES
 │    └─ OrbFakeoutStrategy     (Setup C)
 ├─ RiskManager                 ← global daily P&L, loss limit
 └─ SnapshotAggregator          ← builds EngineSnapshot from all groups
```

**Key components:**

| Component | File | Role |
|-----------|------|------|
| `ComposableEngine` | `Strategy/ComposableEngine.cs` | Orchestrates bar/tick routing, signal dispatch, snapshot publishing |
| `TickerGroup` | `Strategy/TickerGroup.cs` | Per-instrument coordinator — owns shared indicators (ATR, VWAP, ORB) and modules (session, sweep, drive, trend); dispatches bars/ticks to its strategies; enforces cross-setup entry suppression |
| `ISetupStrategy` | `Strategy/ISetupStrategy.cs` | Interface all setups implement: `OnBar`, `OnTick`, `PendingEntry/Exit`, `GetSnapshot`, `GetActiveTrade` |
| `StrategyFactory` | `Strategy/StrategyFactory.cs` | Static factory — creates `PullbackStrategy`, `RetestStrategy`, `OrbFakeoutStrategy`, or `SessionFakeoutStrategy` from config |
| `RiskManager` | `Strategy/RiskManager.cs` | Tracks daily P&L, wins/losses, max drawdown; enforces daily loss limit |
| `SnapshotAggregator` | `Strategy/SnapshotAggregator.cs` | Builds `EngineSnapshot` from per-strategy snapshots, risk state, indicator state, module state, and per-setup last prices |
| `ILastPriceProvider` | `Interfaces/IInterfaces.cs` | Simple price cache — `GetLastPrice(ticker)` / `UpdatePrice(ticker, price)` — used by snapshot aggregator for per-setup last price display |
| `BarRingBuffer` | `Strategy/BarRingBuffer.cs` | Thread-safe ring buffer (200 bars) with parallel VWAP values; deduplicates bars by timestamp; feeds the dashboard chart via `GET /api/engine/bars/{groupKey}` |

### Setups

**Core Setups (entry/exit managed by strategy):**
- **Setup A — Pullback** (`PullbackStrategy`): enters on a pullback to within `NearPct`% of the ORB high/low after an initial move of at least `PullbackPct`% of the opening range
- **Setup B — Breakout Retest** (`RetestStrategy`): enters on a retest of the ORB boundary within `RetestPct`% after a confirmed breakout

**Module-Driven Setups (signals from analytical modules):**
- **Setup C — ORB False Breakout** (`OrbFakeoutStrategy`): detects failed breakouts above/below the ORB range; quality filters include rejection body %, penetration depth, VWAP side, and trend day score
- **Setup D — Session Range False Breakout** (`SessionFakeoutStrategy`): same pattern applied to prior session high/low (Asia H/L for London, London H/L for NY); range locked at session boundaries via `FalseBreakoutDetector.OnSessionStart()`

Each strategy implements `ISetupStrategy` and is self-contained: it receives `OrbState`, `IndicatorState`, and `ModuleState` from its `TickerGroup` and emits pending signals (`PendingEntry`, `PendingExit`, etc.) that `ComposableEngine` routes to the broker and event sink.

Within each bar, entry is evaluated **before** exit. This means a bar whose high/low breaches the stop or target on the same bar as entry is processed immediately — same-bar entry+exit is possible in both backtest and live modes.

All computed price levels (stop, target, partial, pullback entry) are snapped to the nearest valid tick via `LevelCalculator.RoundToTick(price, tickSize)` using `StrategyConfig.TickSize` (default `0.25` for NQ).

### Signal Flow

The engine emits signals via `IStrategyEventSink`:

| Method | Signal type | Trigger |
|--------|-------------|---------|
| `OnEntryAsync` | `EntrySignal` | Position opened |
| `OnPartialAsync` | `PartialSignal` | Partial target hit — scale out |
| `OnBEMoveAsync` | `BESignal` | Stop moved to breakeven |
| `OnExitAsync` | `ExitSignal` + `TradeRecord` | Position closed (target, stop, session end, or manual) |
| `OnSnapshotAsync` | `EngineSnapshot` | Per-bar state update |

## Live Trading (`CRV.Live`)

### Multi-Ticker Pipeline

```
IMultiTickerBarFeed  ──  streams (Bar, ticker) tuples + OnPriceTick(price, utc, ticker)
    │
    ├─ SchwabMultiTickerBarFeed      ← single WSS, comma-separated keys
    ├─ TradovateMultiTickerBarFeed   ← single WSS, separate subscriptions per symbol
    ├─ MultiTickerFeedMux            ← wraps N single-ticker IBarFeed instances (TradeStation)
    │
LiveEngineOrchestrator --- SemaphoreSlim(1,1) + Channel<tick> ---
    -> ComposableEngine.ProcessBarAsync(bar, ticker)       (bar close: arm conditions)
    -> ComposableEngine.ProcessPriceTickAsync(price, utc, ticker)  (every L1 tick)
    -> IOrderExecutor (SchwabExecutor / TradeStationExecutor / TradovateExecutor / MockBrokerExecutor)
    -> IStrategyEventSink -> SignalREventSink -> browser dashboard (+ alert ding audio)
```

`LiveEngineOrchestrator` is a singleton `BackgroundService`. It is started on-demand from the Settings/Live page. When multiple setups trade different instruments, the orchestrator creates a broker-specific multi-ticker feed:

| Broker | Multi-Ticker Feed | Connection Model |
|--------|-------------------|------------------|
| Schwab | `SchwabMultiTickerBarFeed` | Single WSS — `CHART_FUTURES` + `LEVELONE_FUTURES` with comma-separated `keys` (e.g. `/NQH26,/ESH26`); routes bars/ticks by symbol field in content rows |
| Tradovate | `TradovateMultiTickerBarFeed` | Single WSS — sends separate `md/getchart` + `md/subscribeQuote` per symbol; routes by subscription ID → ticker mapping dictionaries |
| TradeStation | `MultiTickerFeedMux` | Wraps N independent HTTP chunked streams (one per symbol) — no connection sharing possible |
| Tradovate Replay | `TradovateMultiTickerBarFeed` | Same as Tradovate with `PostAuthAction` and `ChartStartOverride` for replay-specific handshake |

If the configured broker fails to resolve, the orchestrator falls back to `MockBrokerExecutor` and broadcasts a warning to the dashboard.

### Tick Mode

Separates the evaluation pipeline into two passes:

- **`ProcessBarAsync`** — runs on confirmed execution-TF bar close; updates arm state for Setup A/B, ORB, ATR, VWAP.
- **`ProcessPriceTickAsync`** — runs on every L1 price tick; evaluates entry triggers, exits, partial fills, and stop moves without waiting for a bar close.

A `SemaphoreSlim(1,1)` serializes both paths so they never run concurrently. The tick handler uses a 500 ms timeout so it drops ticks rather than blocking if the engine is busy. Tick mode is gated by `engine.EnableTickMode()` which is called in live mode only — backtest uses bar-close evaluation.

The data broker (`cfg.Broker`) and the order execution broker (`cfg.EffectiveExecBroker`) are configured independently — e.g. stream bars from Tradovate while routing orders through TradeStation, or run with `ExecBroker = Mock` to paper trade.

### Analytical Modules (`CRV.Core/Modules/`)

Each `TickerGroup` owns 5 analytical modules via composition. All implement `IEngineModule` (OnBar, OnTick, NewSession) and are called in sequence during bar/tick processing. Configuration lives in `ModuleConfig`. Modules provide market context shared across all strategies in the group.

| Module | Purpose | Key Outputs |
|--------|---------|-------------|
| `SessionEngine` | Detects Asia/London/NY/Midday/PowerHour sessions, tracks per-session high/low, historical levels (PDH/PDL/PWH/PWL/PMH/PML) | `CurrentSession`, `AsiaCompressed`, `LondonSweptAsiaHigh/Low`, `NYBullExpansion` |
| `SweepDetector` | Monitors key levels for sweep+rejection patterns | `AnyBullSweep`, `AnyBearSweep`, `ActiveSweeps` |
| `VwapModel` | Wraps `VwapIndicator` with deviation bands and state classification | `VwapState` (-2 to +2), `Upper1/2`, `Lower1/2`, reversion/pullback signals |
| `OpeningDriveDetector` | Accumulates bar stats during ORB window, classifies drive | `OpeningDriveBull/Bear`, `DriveRangePctATR` |
| `TrendDayFilter` | Score-based trend day classification (0-5) | `BullScore`, `BearScore`, `TrendDayBull/Bear` (score >= 4) |

Historical levels are seeded at engine start from broker REST daily bars via `IBarFeed.FetchDailyBarsAsync`.

### Bar Feeds

**Single-ticker feeds** (`IBarFeed` — one connection per symbol):

| Broker | Protocol | Subscription |
|--------|----------|-------------|
| Schwab | WebSocket (Schwab Streaming API) | `CHART_FUTURES` + `LEVELONE_FUTURES` |
| TradeStation | HTTP streaming (SSE-style JSON lines) | `/v3/marketdata/stream/barcharts/{symbol}` |
| Tradovate | WebSocket (Tradovate MD API) | `md/getchart` (1-min bars) + `md/subscribeQuote` (L1 ticks) |

**Multi-ticker feeds** (`IMultiTickerBarFeed` — shared connections where possible):

| Class | Broker | Connection Model |
|-------|--------|------------------|
| `SchwabMultiTickerBarFeed` | Schwab | Single WSS with comma-separated `keys` — routes by field `"0"` (chart) and `"key"` (L1) |
| `TradovateMultiTickerBarFeed` | Tradovate | Single WSS with per-symbol `md/getchart` + `md/subscribeQuote` — routes by subscription ID dictionaries (`_reqIdToTicker`, `_chartIdToTicker`, `_quoteIdToTicker`) |
| `MultiTickerFeedMux` | TradeStation / any | Wraps N single-ticker `IBarFeed` instances into one `IMultiTickerBarFeed` stream; also provides `Single()` static helper for wrapping one feed without channel overhead |

All feeds auto-reconnect on error with exponential backoff. Multi-ticker feeds stream `(Bar, string ticker)` tuples and fire `OnPriceTick(price, utc, ticker)` events with the ticker parameter for routing.

### Order Executors

| Executor | Entry | Partial | BE Move | Exit |
|----------|-------|---------|---------|------|
| `SchwabExecutor` | TRIGGER + child OCO bracket | Cancel target, place new LIMIT | Cancel stop, place new STOP | Cancel remaining legs, place MARKET close |
| `TradeStationExecutor` | Market + OSO BRK bracket | Cancel target, place new Limit | Cancel stop, place new StopMarket | Cancel remaining legs, place Market close |
| `TradovateExecutor` | Market + OSO bracket (`/order/placeOSO`) | Cancel target, place new Limit | Cancel stop, place new Stop | Cancel remaining legs, place Market close |
| `MockBrokerExecutor` | Creates 3-leg OCO in memory (entry FILLED immediately, stop + target WORKING) | Logs | Moves stop order price in memory | Cancels all WORKING orders |

`MockBrokerExecutor` maintains a full in-memory order book and simulates realistic fills when `EvaluateFills(price, utcNow)` is called on every L1 tick. Fill rules:

| Order type | Fills when |
|------------|-----------|
| BUY STOP | `price >= stopPrice` |
| SELL STOP | `price <= stopPrice` |
| BUY LIMIT | `price <= limitPrice` |
| SELL LIMIT | `price >= limitPrice` |

### Futures Symbol Conversion

Brokers use different symbol formats. `StrategyConfig.Ticker` stores the **canonical** format (no slash, 2-digit year, e.g. `NQH26`). `FuturesSymbol.ForBroker()` converts at runtime.

| Format | Example | Used by |
|--------|---------|---------|
| Canonical (stored) | `NQH26` | DB, UI, JS instrument selector |
| Schwab | `/NQH26` | Schwab Streaming API, order URLs |
| TradeStation | `NQH26` | TradeStation bar-feed URL, order body |
| Tradovate | `NQH6` | Tradovate MD WebSocket, order body (1-digit year) |

### Options (`CRV.Core/Options`, `CRV.Live/Brokers/Schwab`)

Independent of the strategy engine — no shared code with `ComposableEngine` or
`BrokerEventHandler`. `PayoffCalculator`, `OptionChainParser`, `LiquidityGate` and
`StructureFinder` are pure and unit tested; `SchwabOptionChain` and `SchwabOptionOrder`
handle transport only. See [Options](options.md).

### Manual Broker Operations

`ManualBrokerOps` provides broker-agnostic static helpers used by the Manual Trading and Orders pages. Methods cover positions, orders, cancel, flat-at-market, and place-OCO across all three brokers plus mock equivalents.

## Web Layer (`CRV.Web`)

### Pages

| Route | Purpose |
|-------|---------|
| `/` | Home / status summary |
| `/Dashboard` | Live P&L, trades, Lightweight Charts (candlestick + volume + VWAP + ORB levels), per-group Market Context, per-setup last price with blinking dot, USD values on Stop/Partial/Target, session badge, stream health, alert feed, Exit Now buttons (SignalR) |
| `/Settings/Live` | Start/stop engine, broker + strategy config |
| `/Backtest` | Run backtest + view results |
| `/Settings/Backtest` | Backtest config + inline results (metric cards, equity curve, trade log) |
| `/trading/manual` | Manual OCO bracket orders, live positions table, Cancel All |
| `/trading/orders` | Order list with filters, Cancel button, 30 s auto-refresh; includes options orders |
| `/options/explorer` | Options chain, structure builder, order ticket, positions and working orders — see [Options](options.md) |
| `/trading/mock` | Mock broker trade review (day selector, metrics, equity curve, trade log) |
| `/auth/schwab` | Schwab OAuth2 authorization |
| `/auth/tradestation` | TradeStation OAuth2 authorization |
| `/auth/tradovate` | Tradovate authentication |

### Background Services

| Service | Purpose |
|---------|---------|
| `LiveEngineOrchestrator` | Live engine lifecycle, multi-ticker feed creation, broker fallback alerting |
| `BacktestRunnerService` | On-demand backtests |
| `DailyStatsService` | In-memory today's P&L/win stats |
| `StrategyConfigService` | Loads/saves `StrategyConfig` from SQLite |
| `SignalREventSink` | Forwards engine signals to browser clients |
| `BrokerTokenKeepAlive` | Touches Schwab/TradeStation tokens every 12 h so the 7-day refresh window cannot lapse while the app runs |

### Database

SQLite via EF Core. `TradingDbContext` manages:

| Table | Model | Indexes |
|-------|-------|---------|
| `Trades` | `TradeRecord` | `EnteredAt`, `SessionId`, `Source`, `Ticker`, `ExitReason` |
| `Configs` | `StrategyConfig` | — |
| `BacktestRuns` | `BacktestRunRow` | — |

#### Numeric storage

`ConfigureConventions` maps every `decimal` to SQLite `REAL`. EF Core's default is
`TEXT`, which sorts lexicographically: `MIN(RMultiple)` over the live book returned
`-0.07` when the true minimum was `-4.32`, and the worst trade read `-$103` when it
was `-$740.70`. Every SQL-side tail figure was understated roughly sevenfold, always
in the flattering direction. C# keeps `decimal`; only the column type changes.

Anything added to a model as `decimal` inherits this automatically — do not
reintroduce a `HasColumnType("TEXT")` on a numeric column.
