# Architecture

## Strategy Engine (`CRV.Core`)

`OrbStrategyEngine` processes bars one at a time. It supports five setups:

**Core Setups (entry/exit managed by engine):**
- **Setup A — Pullback**: enters on a pullback to within `NearPct`% of the ORB high/low after an initial move of at least `PullbackPct`% of the opening range
- **Setup B — Breakout Retest**: enters on a retest of the ORB boundary within `RetestPct`% after a confirmed breakout

**Module-Driven Setups (signals from analytical modules):**
- **Setup C — Sweep Reversal**: liquidity sweep of key level (PDH/PDL/equal highs-lows) + rejection + VWAP alignment
- **Setup D — Opening Drive Pullback**: confirmed opening drive + trend day + shallow VWAP pullback
- **Setup F — Midday VWAP Reversion**: extended beyond 2-sigma VWAP band during midday session + rejection candle

Within each bar, entry is evaluated **before** exit. This means a bar whose high/low breaches the stop or target on the same bar as entry is processed immediately — same-bar entry+exit is possible in both backtest and live modes.

All computed price levels (stop, target, partial, pullback entry) are snapped to the nearest valid tick via `LevelCalculator.RoundToTick(price, tickSize)` using `StrategyConfig.TickSize` (default `0.25` for NQ).

The engine emits signals via `IStrategyEventSink`:

| Method | Signal type | Trigger |
|--------|-------------|---------|
| `OnEntryAsync` | `EntrySignal` | Position opened |
| `OnPartialAsync` | `PartialSignal` | Partial target hit — scale out |
| `OnBEMoveAsync` | `BESignal` | Stop moved to breakeven |
| `OnExitAsync` | `ExitSignal` + `TradeRecord` | Position closed (target, stop, session end, or manual) |
| `OnSnapshotAsync` | `EngineSnapshot` | Per-bar state update |

## Live Trading (`CRV.Live`)

```
SchwabBarFeed / TradeStationBarFeed / TradovateBarFeed
    |  IAsyncEnumerable<Bar>                |  IBarFeed.OnPriceTick (L1 ticks, live only)
LiveEngineOrchestrator --- SemaphoreSlim(1,1) + Channel<tick> ---
    -> OrbStrategyEngine.ProcessBarAsync()           (bar close: arm conditions)
    -> OrbStrategyEngine.ProcessPriceTickAsync()     (every L1 tick: entry/exit/stop eval)
    -> IOrderExecutor (SchwabExecutor / TradeStationExecutor / TradovateExecutor / MockBrokerExecutor)
    -> IStrategyEventSink -> SignalREventSink -> browser dashboard (+ alert ding audio)
```

`LiveEngineOrchestrator` is a singleton `BackgroundService`. It is started on-demand from the Settings/Live page. If the configured broker fails to resolve, it falls back to `MockBrokerExecutor` and broadcasts a warning to the dashboard.

### Tick Mode

Separates the evaluation pipeline into two passes:

- **`ProcessBarAsync`** — runs on confirmed execution-TF bar close; updates arm state for Setup A/B, ORB, ATR, VWAP.
- **`ProcessPriceTickAsync`** — runs on every L1 price tick; evaluates entry triggers, exits, partial fills, and stop moves without waiting for a bar close.

A `SemaphoreSlim(1,1)` serializes both paths so they never run concurrently. The tick handler uses a 500 ms timeout so it drops ticks rather than blocking if the engine is busy. Tick mode is gated by `engine.EnableTickMode()` which is called in live mode only — backtest uses bar-close evaluation.

The data broker (`cfg.Broker`) and the order execution broker (`cfg.EffectiveExecBroker`) are configured independently — e.g. stream bars from Tradovate while routing orders through TradeStation, or run with `ExecBroker = Mock` to paper trade.

### Analytical Modules (`CRV.Core/Modules/`)

The engine owns 6 analytical modules via composition. All implement `IEngineModule` (OnBar, OnTick, NewSession) and are called in sequence during bar/tick processing. Configuration lives in `ModuleConfig`.

| Module | Purpose | Key Outputs |
|--------|---------|-------------|
| `SessionEngine` | Detects Asia/London/NY/Midday/PowerHour sessions, tracks per-session high/low, historical levels (PDH/PDL/PWH/PWL/PMH/PML) | `CurrentSession`, `AsiaCompressed`, `LondonSweptAsiaHigh/Low`, `NYBullExpansion` |
| `SweepDetector` | Monitors key levels for sweep+rejection patterns | `AnyBullSweep`, `AnyBearSweep`, `ActiveSweeps` |
| `VwapModel` | Wraps `VwapIndicator` with deviation bands and state classification | `VwapState` (-2 to +2), `Upper1/2`, `Lower1/2`, reversion/pullback signals |
| `OpeningDriveDetector` | Accumulates bar stats during ORB window, classifies drive | `OpeningDriveBull/Bear`, `DriveRangePctATR` |
| `TrendDayFilter` | Score-based trend day classification (0-5) | `BullScore`, `BearScore`, `TrendDayBull/Bear` (score >= 4) |
| `CompositeSetupEngine` | Evaluates combined setups C/D/F from all module outputs | `SetupCBull/Bear`, `SetupDBull/Bear`, `SetupFBull/Bear` |

Historical levels are seeded at engine start from broker REST daily bars via `IBarFeed.FetchDailyBarsAsync`.

### Bar Feeds

| Broker | Protocol | Subscription |
|--------|----------|-------------|
| Schwab | WebSocket (Schwab Streaming API) | `CHART_FUTURES` + `LEVELONE_FUTURES` |
| TradeStation | HTTP streaming (SSE-style JSON lines) | `/v3/marketdata/stream/barcharts/{symbol}` |
| Tradovate | WebSocket (Tradovate MD API) | `md/getchart` (1-min bars) + `md/subscribeQuote` (L1 ticks) |

All feeds auto-reconnect on error with a 5 s backoff.

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

### Manual Broker Operations

`ManualBrokerOps` provides broker-agnostic static helpers used by the Manual Trading and Orders pages. Methods cover positions, orders, cancel, flat-at-market, and place-OCO across all three brokers plus mock equivalents.

## Web Layer (`CRV.Web`)

### Pages

| Route | Purpose |
|-------|---------|
| `/` | Home / status summary |
| `/Dashboard` | Live P&L, trades, session badge, stream health, alert feed, Exit Now buttons (SignalR) |
| `/Settings/Live` | Start/stop engine, broker + strategy config |
| `/Backtest` | Run backtest + view results |
| `/Settings/Backtest` | Backtest config + inline results (metric cards, equity curve, trade log) |
| `/trading/manual` | Manual OCO bracket orders, live positions table, Cancel All |
| `/trading/orders` | Order list with filters, Cancel button, 30 s auto-refresh |
| `/trading/mock` | Mock broker trade review (day selector, metrics, equity curve, trade log) |
| `/auth/schwab` | Schwab OAuth2 authorization |
| `/auth/tradestation` | TradeStation OAuth2 authorization |
| `/auth/tradovate` | Tradovate authentication |

### Background Services

| Service | Purpose |
|---------|---------|
| `LiveEngineOrchestrator` | Live engine lifecycle, broker fallback alerting |
| `BacktestRunnerService` | On-demand backtests |
| `DailyStatsService` | In-memory today's P&L/win stats |
| `StrategyConfigService` | Loads/saves `StrategyConfig` from SQLite |
| `SignalREventSink` | Forwards engine signals to browser clients |

### Database

SQLite via EF Core. `TradingDbContext` manages:

| Table | Model | Indexes |
|-------|-------|---------|
| `Trades` | `TradeRecord` | `EnteredAt`, `SessionId`, `Source`, `Ticker`, `ExitReason` |
| `Configs` | `StrategyConfig` | — |
| `BacktestRuns` | `BacktestRunRow` | — |
