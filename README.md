# CRV.Trading

ASP.NET Core web application for live and backtested ORB (Opening Range Breakout) futures trading. Supports Schwab, TradeStation, and Tradovate brokers with real-time dashboard via SignalR.

## Projects

| Project | Target | Purpose |
|---------|--------|---------|
| `CRV.Core` | net10.0 | Strategy engine, models, indicators, interfaces, DB context |
| `CRV.Backtest` | net10.0 | Backtesting engine, bar loaders (CSV, Schwab, TradeStation), result calculation |
| `CRV.Live` | net10.0 | Broker integrations (bar feeds + order executors), real-time bar builder |
| `CRV.Web` | net10.0 | ASP.NET Core Razor Pages UI, SignalR hub, background services, REST API |
| `CRV.Core.Tests` | net10.0 | xUnit tests (64 tests) covering indicators, strategy components, broker simulation, validation |

## Prerequisites

- .NET 10 SDK
- SQLite (bundled via EF Core)
- Schwab Developer account (for live Schwab trading/data)
- TradeStation API credentials (for live TradeStation trading/data)
- Tradovate account + API credentials (for live Tradovate trading/data)

## Quick Start

```bash
# 1. Restore and build
dotnet build CRV.Trading.sln

# 2. Set broker credentials via user-secrets (never commit these)
cd CRV.Web
dotnet user-secrets set "Schwab:AppKey"             "YOUR_SCHWAB_APP_KEY"
dotnet user-secrets set "Schwab:AppSecret"          "YOUR_SCHWAB_APP_SECRET"
dotnet user-secrets set "TradeStation:ClientId"     "YOUR_TS_CLIENT_ID"
dotnet user-secrets set "TradeStation:ClientSecret" "YOUR_TS_CLIENT_SECRET"
dotnet user-secrets set "Tradovate:Username"         "YOUR_TV_USERNAME"
dotnet user-secrets set "Tradovate:Password"         "YOUR_TV_PASSWORD"
dotnet user-secrets set "Tradovate:Cid"              "YOUR_TV_CID"
dotnet user-secrets set "Tradovate:Secret"           "YOUR_TV_SECRET"

# 3. Set account IDs and Schwab redirect URI in appsettings.json
#    (see Configuration section below — these are non-sensitive)

# 4. Run with HTTPS (required for OAuth2 broker authentication)
dotnet run --launch-profile https

# 5. Open browser
#    App:     https://localhost:5001
#    Swagger: https://localhost:5001/swagger  (Development only)

# 6. Authorize Schwab (one-time, retail accounts only)
#    Navigate to https://localhost:5001/auth/schwab → click "Connect with Schwab"
#    After logging in on Schwab's site you are redirected back automatically.
#    Tokens are saved to schwab_tokens.json and refreshed automatically.

# 7. Authorize TradeStation (one-time, if using TradeStation broker)
#    Navigate to https://localhost:5001/auth/tradestation → click "Connect with TradeStation"
#    After logging in on TradeStation's site you are redirected back automatically.
#    Tokens are saved to tradestation_tokens.json and refreshed automatically.

# 8. Authorize Tradovate (one-time, if using Tradovate broker)
#    Navigate to https://localhost:5001/auth/tradovate → click "Connect"
#    Credentials are read from user-secrets; no OAuth2 redirect needed.
#    Tokens are saved to tradovate_tokens.json and auto-renewed every 90 min.
```

## Running Tests

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj
# Expected: 64 tests passing
```

## Configuration

### Launch Profiles

The application has two launch profiles defined in `CRV.Web/Properties/launchSettings.json`:

| Profile | Ports | Command |
|---------|-------|---------|
| `http` | HTTP only on port 5000 | `dotnet run --launch-profile http` |
| `https` | HTTPS on 5001 + HTTP on 5000 | `dotnet run --launch-profile https` |

**⚠️ Important:** OAuth2 redirect URIs for both Schwab and TradeStation are configured for **`https://127.0.0.1:5001`**. If you run with the `http` profile (or `dotnet run` without specifying a profile, which defaults to the first profile), broker authentication will fail because the redirect URI won't match.

**Always use:**
```bash
dotnet run --launch-profile https
```

Or explicitly configure Kestrel to listen on HTTPS via `appsettings.Development.json` (already configured):
```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://localhost:5000" },
      "Https": { "Url": "https://localhost:5001" }
    }
  }
}
```

The HTTPS development certificate is automatically generated and trusted when you install .NET SDK. Verify with:
```bash
dotnet dev-certs https --check --trust
```

If your certificate is missing or untrusted, regenerate it:
```bash
dotnet dev-certs https --clean
dotnet dev-certs https --trust
```

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=crv_trading.db"
  },
  "Schwab": {
    "ApiBaseUrl":  "https://api.schwabapi.com",
    "WssBaseUrl":  "wss://streamer-api.schwab.com/ws",
    "AccountId":   "YOUR_SCHWAB_ACCOUNT_HASH",
    "RedirectUri": "https://127.0.0.1:5001/auth/schwab",
    "TokenFile":   "schwab_tokens.json"
  },
  "TradeStation": {
    "ApiBaseUrl":  "https://api.tradestation.com",
    "AuthBaseUrl": "https://signin.tradestation.com",
    "AccountId":   "YOUR_TS_ACCOUNT_ID",
    "RedirectUri": "https://127.0.0.1:5001/auth/tradestation",
    "TokenFile":   "tradestation_tokens.json"
  },
  "Tradovate": {
    "ApiBaseUrl": "https://live.tradovateapi.com/v1",
    "MdWssUrl":   "wss://md.tradovateapi.com/v1/websocket",
    "AccountId":  "YOUR_TV_ACCOUNT_ID",
    "TokenFile":  "tradovate_tokens.json"
  }
}
```

| Key | Sensitive? | Notes |
|-----|------------|-------|
| `Schwab:AppKey` / `AppSecret` | ✅ user-secrets | OAuth2 client credentials from [developer.schwab.com](https://developer.schwab.com) |
| `Schwab:ApiBaseUrl` | No | Schwab REST API base URL; defaults to `https://api.schwabapi.com` |
| `Schwab:WssBaseUrl` | No | Schwab WebSocket streaming URL; overrides API-returned `streamerSocketUrl` if set |
| `Schwab:AccountId` | No | Encrypted account hash from the Schwab API |
| `Schwab:RedirectUri` | No | Must exactly match the URI registered in your Schwab developer app |
| `Schwab:TokenFile` | No | Path where OAuth2 tokens are persisted; **keep this file out of source control** |
| `TradeStation:ClientId` / `ClientSecret` | ✅ user-secrets | From TradeStation developer portal |
| `TradeStation:ApiBaseUrl` | No | TradeStation REST API base URL; defaults to `https://api.tradestation.com` |
| `TradeStation:AuthBaseUrl` | No | TradeStation OAuth2 authorization server; defaults to `https://signin.tradestation.com` |
| `TradeStation:AccountId` | No | TradeStation account ID |
| `TradeStation:RedirectUri` | No | Must exactly match the URI registered in your TradeStation developer app |
| `TradeStation:TokenFile` | No | Path where TradeStation OAuth2 tokens are persisted; **keep this file out of source control** |
| `Tradovate:Username` / `Password` | ✅ user-secrets | Tradovate account login credentials |
| `Tradovate:Cid` / `Secret` | ✅ user-secrets | Tradovate API application credentials (from Tradovate developer portal) |
| `Tradovate:ApiBaseUrl` | No | Tradovate REST API base URL; use `https://demo.tradovateapi.com/v1` for paper trading |
| `Tradovate:MdWssUrl` | No | Tradovate market-data WebSocket URL |
| `Tradovate:AccountId` | No | Tradovate account ID |
| `Tradovate:TokenFile` | No | Path where Tradovate tokens are persisted; **keep this file out of source control** |

> **Note:** API URLs, `AccountId`, and `RedirectUri` are non-sensitive and live in `appsettings.json`. Credentials (`AppKey`, `AppSecret`, `ClientId`, `ClientSecret`, Tradovate `Username`/`Password`/`Cid`/`Secret`) must always be in user-secrets or environment variables — never in `appsettings.json`. Token files (`schwab_tokens.json`, `tradestation_tokens.json`, `tradovate_tokens.json`) contain live tokens and **must not be committed to source control** — all three are excluded by the repo's `.gitignore`.

### EF Core Migrations

Two migrations live in `CRV.Core/Migrations/`:

| Migration | Adds |
|---|---|
| `20260306200430_Initial` | All baseline tables (`Trades`, `Configs`, `BacktestRuns`) |
| `20260310184233_AddMissingStrategyConfigColumns` | `SessionStartHour`, `ExecBroker`, `ExecAccountId`, `EntryTickOffsetA`, `EntryTickOffsetB` to `Configs` |

On startup, the app checks for pending migrations and applies them automatically (`Database.Migrate()`). If no migrations are tracked (fresh clone), it falls back to `Database.EnsureCreated()`.

To generate future migrations after model changes:

```bash
dotnet ef migrations add <MigrationName> --project CRV.Core --startup-project CRV.Web
dotnet ef database update --startup-project CRV.Web
```

## Architecture

### Strategy Engine (`CRV.Core`)

`OrbStrategyEngine` processes bars one at a time. It supports two setups:

- **Setup A — Pullback**: enters on a pullback to within `NearPct`% of the ORB high/low after an initial move of at least `PullbackPct`% of the opening range
- **Setup B — Breakout Retest**: enters on a retest of the ORB boundary within `RetestPct`% after a confirmed breakout

Within each bar, entry is evaluated **before** exit. This means a bar whose high/low breaches the stop or target on the same bar as entry is processed immediately — same-bar entry+exit is possible in both backtest and live modes.

All computed price levels (stop, target, partial, pullback entry) are snapped to the nearest valid tick via `LevelCalculator.RoundToTick(price, tickSize)` using `StrategyConfig.TickSize` (default `0.25` for NQ). This prevents the engine from placing orders at non-tradeable prices.

The engine emits signals via `IStrategyEventSink`:

| Signal | Trigger |
|--------|---------|
| `EntrySignal` | Position opened |
| `PartialSignal` | Partial target hit — scale out |
| `BESignal` | Stop moved to breakeven |
| `ExitSignal` | Position closed (target, stop, session end, or manual) |
| `EngineSnapshot` | Per-bar state update |

### Live Trading (`CRV.Live`)

```
SchwabBarFeed / TradeStationBarFeed / TradovateBarFeed
    ↓  IAsyncEnumerable<Bar>                ↓  IBarFeed.OnPriceTick (L1 ticks, live only)
LiveEngineOrchestrator ─── SemaphoreSlim(1,1) ─────────────────────────────────
    → OrbStrategyEngine.ProcessBarAsync()           (bar close: arm conditions)
    → OrbStrategyEngine.ProcessPriceTickAsync()     (every L1 tick: entry/exit/stop eval)
    → IOrderExecutor (SchwabExecutor / TradeStationExecutor / TradovateExecutor / MockBrokerExecutor)
    → IStrategyEventSink → SignalREventSink → browser dashboard
```

`LiveEngineOrchestrator` is a singleton `BackgroundService`. It is started on-demand from the Settings/Live page. If the configured broker fails to resolve, it falls back to `MockBrokerExecutor` (no live orders placed) and broadcasts a warning to the dashboard.

**Tick mode** separates the evaluation pipeline into two passes:

- **`ProcessBarAsync`** — runs on confirmed execution-TF bar close; updates arm state for Setup A/B, ORB, ATR, VWAP.
- **`ProcessPriceTickAsync`** — runs on every L1 price tick (from `IBarFeed.OnPriceTick`); evaluates entry triggers, exits, partial fills, and stop moves without waiting for a bar close.

A `SemaphoreSlim(1,1)` in `LiveEngineOrchestrator` serializes both paths so they never run concurrently (the engine is not internally thread-safe). The tick handler uses a 500 ms timeout so it drops ticks rather than blocking if the engine is busy. Tick mode is gated by `engine.EnableTickMode()` which is called in live mode only — backtest continues to use bar-close evaluation.

The data broker (`cfg.Broker`) and the order execution broker (`cfg.EffectiveExecBroker`) are configured independently — e.g. you can stream bars from Tradovate while routing orders through TradeStation, or run with `ExecBroker = Mock` to paper trade live data without placing real orders.

**Bar feeds:**

| Broker | Protocol | Subscription |
|--------|----------|-------------|
| Schwab | WebSocket (Schwab Streaming API) | `CHART_FUTURES` + `LEVELONE_FUTURES` |
| TradeStation | HTTP streaming (SSE-style JSON lines) | `/v3/marketdata/stream/barcharts/{symbol}` |
| Tradovate | WebSocket (Tradovate MD API) | `md/getchart` (1-min bars) + `md/subscribeQuote` (L1 ticks) |

All feeds auto-reconnect on error with a 5 s backoff.

**Order executors:**

| Executor | Entry | Partial | BE Move | Exit |
|----------|-------|---------|---------|------|
| `SchwabExecutor` | TRIGGER + child OCO bracket | Cancel target, place new LIMIT | Cancel stop, place new STOP | Cancel remaining legs, place MARKET close |
| `TradeStationExecutor` | Market + OSO BRK bracket | Cancel target, place new Limit | Cancel stop, place new StopMarket | Cancel remaining legs, place Market close |
| `TradovateExecutor` | Market + OSO bracket (`/order/placeOSO`) | Cancel target, place new Limit | Cancel stop, place new Stop | Cancel remaining legs, place Market close |
| `MockBrokerExecutor` | Creates 3-leg OCO in memory (entry FILLED immediately, stop + target WORKING) | Logs | Moves stop order price in memory | Cancels all WORKING orders |

`MockBrokerExecutor` maintains a full in-memory order book and simulates realistic fills when `EvaluateFills(price, utcNow)` is called on every L1 tick (wired automatically in `LiveEngineOrchestrator` when `ExecBroker = Mock`). Fill rules:

| Order type | Fills when |
|------------|-----------|
| BUY STOP | `price >= stopPrice` |
| SELL STOP | `price <= stopPrice` |
| BUY LIMIT | `price <= limitPrice` |
| SELL LIMIT | `price >= limitPrice` |

When one OCO leg fills, all WORKING partners with the same `OcoGroupId` are immediately CANCELED. Orders are visible on `/trading/orders`.

All real executors are **stateful** — they track live order IDs (`_entryOrderId`, `_stopOrderId`, `_targetOrderId`) per active trade and cancel/replace them on partial fills and BE moves. `TradovateExecutor` assigns order state only **after** a confirmed `placeOSO` response (non-null entry ID) to prevent phantom exits on API failure.

**Manual broker operations (`CRV.Live.ManualBrokerOps`):**

`ManualBrokerOps` provides broker-agnostic static helpers used by the Manual Trading and Orders pages. All methods have `*Mock*` equivalents that return deterministic strings for the Mock broker.

| Method | Description | API call |
|--------|-------------|----------|
| `GetPositionsTradeStationAsync` | Returns `List<PositionView>` filtered to futures | `GET /v3/brokerage/accounts/{id}/positions` |
| `GetPositionsSchwabAsync` | Returns `List<PositionView>` filtered to futures | `GET /trader/v1/accounts/{hash}?fields=positions` |
| `GetOrdersTradeStationAsync` | Returns `List<OrderView>` for date/status filter | `GET /v3/brokerage/accounts/{id}/orders?since=…&status=…` |
| `GetOrdersSchwabAsync` | Returns `List<OrderView>` for date/status filter | `GET /trader/v1/accounts/{hash}/orders?fromEnteredTime=…` |
| `CancelOrderTradeStationAsync` | Cancels a single order by ID | `DELETE /v3/orderexecution/orders/{orderId}` |
| `CancelOrderSchwabAsync` | Cancels a single order by ID | `DELETE /trader/v1/accounts/{hash}/orders/{orderId}` |
| `CancelOrderTradovateAsync` | Cancels a single order by ID | `POST /order/cancelOrder` |
| `CancelAllTradeStationAsync` | Cancels all open orders for a ticker | Calls `GetOrders` then `CancelOrder` per order |
| `CancelAllSchwabAsync` | Cancels all open orders for a ticker | Calls `GetOrders` then `CancelOrder` per order |
| `CancelAllTradovateAsync` | Cancels all Working orders for a ticker | `GET /order/list` then `POST /order/cancelOrder` per order |
| `FlatAtMarketTradeStationAsync` | Closes N contracts at market | `POST /v3/orderexecution/orders` (Market order) |
| `FlatAtMarketSchwabAsync` | Closes N contracts at market | `POST /trader/v1/accounts/{hash}/orders` (MARKET order) |
| `FlatAtMarketTradovateAsync` | Closes N contracts at market | `GET /account/list` → `POST /order/placeOrder` (Market) |
| `GetPositionsTradovateAsync` | Returns `List<PositionView>` for open positions | `GET /position/list`; resolves symbol via `GET /contract/item?id=` |
| `GetOrdersTradovateAsync` | Returns `List<OrderView>` for date/status filter | `GET /order/list` with status filter |
| `PlaceOcoTradovateAsync` | Places a bracket order | `POST /order/placeOSO` |

**Normalized data records:**

```csharp
record PositionView(
    string Symbol, string Direction, decimal Quantity,
    decimal AveragePrice, decimal? UnrealizedPnl, decimal? DayPnl);

record OrderView(
    string OrderId, string Symbol, string Status, string StatusLabel,
    string OrderType, string Action, decimal Quantity,
    decimal? LimitPrice, decimal? StopPrice, string PlacedTime, bool CanCancel);
```

`CanCancel` is `true` for TradeStation statuses `OPN`, `ACK`, `FPR` and for Schwab statuses `WORKING`, `QUEUED`, `PENDING_ACTIVATION`, `ACCEPTED`, `AWAITING_PARENT_ORDER`, `AWAITING_CONDITION`.

**Futures symbol conversion (`CRV.Live.FuturesSymbol`):**

Brokers use different symbol formats for the same futures contract. `StrategyConfig.Ticker` always stores the **canonical** format (no slash, 2-digit year, e.g. `NQH26`). `FuturesSymbol.ForBroker()` converts to the broker-specific format at runtime inside `LiveEngineOrchestrator` and the Manual Trading page — the stored value is never modified.

`FuturesSymbol.Normalize()` accepts any variant (e.g. `/NQH26`, `NQH2026`, `NQH26`) and always returns the canonical form by stripping a leading `/` and collapsing a 4-digit year to 2-digit.

| Format | Example | Used by |
|--------|---------|---------|
| Canonical (stored) | `NQH26` | DB, UI, JS instrument selector |
| Schwab | `/NQH26` | Schwab Streaming API, order URLs |
| TradeStation | `NQH26` | TradeStation bar-feed URL, order body |
| Tradovate | `NQH6` | Tradovate MD WebSocket, order body (1-digit year) |

Schwab requires a leading `/`; Tradovate uses a **1-digit year** (`NQH26` → `NQH6`) via `FuturesSymbol.ToTradovate()`. The `Normalize()` step ensures that 4-digit years entered by the user are always collapsed before storage.

The instrument dropdown on Live Settings and the Manual Trading page auto-calculates the front month (e.g. `NQ` → `NQH26`) using the quarterly roll schedule (H/M/U/Z) with a 10-day look-ahead before expiry.

### Web Layer (`CRV.Web`)

**Pages:**

| Route | Purpose |
|-------|---------|
| `/` | Home / status summary |
| `/Dashboard` | Live P&L, trades table, engine status; session badge (ORB FORMING → TRADING → CUTOFF → SESSION ENDED); stream health indicator; alert feed; **Exit Now** buttons for each active setup (SignalR real-time) |
| `/Backtest` | Run a backtest, view results |
| `/Settings/Live` | Start/stop live engine, configure broker and strategy via instrument dropdown |
| `/Settings/Backtest` | Configure backtest date range, data source, execution TF (1–60 min resampling), and fill mode; shows inline results (per-setup metric cards, equity curve, full trade log with In/Out timestamps) after each run |
| `/trading/manual` | Place manual OCO bracket orders (Points / Dollars / Price-level input modes, single or split-partial); entry price auto-populated from last known price; Bootstrap confirmation modals on all actions; live positions table with per-row **Flat** button; **Cancel All** open orders |
| `/trading/orders` | View and manage open orders — filter by status and date range, auto-refresh (30 s), **Cancel** button per cancellable order |
| `/trading/mock` | Mock broker trade log — P&L, equity curve, and trade table for trades placed via Mock exec broker (`Source = "mock"`) |
| `/auth/schwab` | Schwab OAuth2 authorization — connect account and manage tokens |
| `/auth/tradestation` | TradeStation OAuth2 authorization — connect account and manage tokens |
| `/auth/tradovate` | Tradovate authentication — POST credentials to obtain tokens (no OAuth2 redirect required) |

**Background services:**

| Service | Lifetime | Purpose |
|---------|----------|---------|
| `LiveEngineOrchestrator` | Singleton | Owns live engine lifecycle, broker fallback alerting |
| `BacktestRunnerService` | Singleton | Runs backtests on demand, exposes `IsRunning` for spinner |
| `DailyStatsService` | Singleton | In-memory today's P&L/win stats, resets each day |
| `StrategyConfigService` | Singleton | Loads/saves `StrategyConfig` from SQLite |
| `SignalREventSink` | Singleton | Forwards engine signals to all connected browser clients |

**API:**

REST endpoints (rate-limited to 5 req/s via the `engine-api` policy) are documented at `/swagger` in Development.

**SignalR Hub:** `/hubs/trading`

| Server → Client message | Payload |
|------------------------|---------|
| `EngineStatusChanged` | Status string (`"Live"` / `"Stopped"`) |
| `BrokerFallback` | Warning message string |
| `Update` | `EngineSnapshot` JSON (every bar) |
| `Alert` | `{ time, setup, type, color, message }` — fired on entry, partial, BE move, exit |

**`Update` payload — `EngineSnapshot`:**

| JSON field | Type | Description |
|-----------|------|-------------|
| `ticker` | string | Active futures symbol |
| `isLive` | bool | Engine is processing live bars |
| `setupA` / `setupB` | `ActiveTradeView \| null` | Active trade state per setup; `null` when idle |
| `todayPnl` | decimal | Running net P&L for the session |
| `todayTrades` | int | Total closed trades today |
| `todayWins` / `todayLosses` | int | Win/loss breakdown |
| `todayMaxDD` | decimal | Largest intraday drawdown from peak (dollars) |
| `dailyLossLimit` | decimal | Configured `MaxDailyLoss` |
| `dailyLossUsed` | decimal | Dollars lost so far (0 when profitable) |
| `tradingHalted` | bool | Daily loss limit breached |
| `vwap` | decimal | Current session VWAP |
| `atr` | decimal | ATR(14) value |
| `orbHigh` / `orbLow` / `orbMid` / `orbRange` | decimal | ORB levels (0 while building) |
| `orbBullClose` / `orbBearClose` | bool | ORB close quality flags |
| `stickyTgtA` / `stickyStpA` | bool | `true` on the bar Setup A exited via target / stop; `false` all other bars |
| `stickyTgtB` / `stickyStpB` | bool | Same for Setup B |
| `orbFormed` | bool | ORB window complete and range set |
| `pastCutoff` | bool | Current time is past the no-new-entries cutoff |
| `sessionEnded` | bool | RTH session has ended |
| `lastPrice` | decimal | Most recent tick/bar price |
| `lastUpdate` | DateTime | Timestamp of last price update |
| `recentAlerts` | `AlertEvent[]` | Last 20 engine alerts |

**`ActiveTradeView` fields (camelCase in JSON):**

| Field | Type | Notes |
|-------|------|-------|
| `direction` | int | `0` = Long, `1` = Short |
| `entry` | decimal | Fill price |
| `currentStop` | decimal | Current stop (may be BE-adjusted) |
| `target` / `partial` | decimal | Full target and partial scale-out level |
| `contracts` | int | Original position size |
| `remainingContracts` | int | Contracts still open after partial |
| `partialFilled` | bool | Partial scale-out has executed |
| `unrealizedPnl` | decimal | Mark-to-market P&L (dollars) |

**Client-side event flow (`crv-hub.js`):**

`crv-hub.js` receives SignalR messages and re-dispatches them as standard DOM `CustomEvent`s so that page-level scripts can subscribe without direct SignalR coupling:

| DOM event | Fired when | `event.detail` |
|-----------|-----------|----------------|
| `crv:update` | `Update` SignalR message received **or** initial state fetched on connect | `EngineSnapshot` object |
| `crv:alert` | `Alert` SignalR message received | Alert object |
| `crv:status` | `EngineStatusChanged` received **or** initial state fetched on connect | Status string |

The nav bar (`nav-ticker`, `nav-status`) is updated directly by `crv-hub.js` on every `Update`. Page scripts subscribe to `crv:update` / `crv:alert` for their own rendering.

**Initial state sync:** When the SignalR connection is established (or re-established after a drop), `crv-hub.js` fetches `GET /api/engine/status` and immediately updates the nav bar and dispatches `crv:update` / `crv:status` with the cached snapshot. This ensures the `LIVE` / `OFFLINE` badge and dashboard cards are correct even when a page loads after the engine was already running — not just when a broadcast happens to arrive. `connection.onclose()` immediately forces the badge to `OFFLINE`.

### Broker Authentication

Schwab and TradeStation use the **OAuth2 Authorization Code** flow with refresh tokens. Tradovate uses **direct credential authentication** (username/password/cid/secret POSTed to `/auth/accesstokenrequest`) — no OAuth2 redirect needed. The Live Settings page shows a connected/not-connected banner for each broker at all times.

#### Schwab

1. Create an app at [developer.schwab.com](https://developer.schwab.com) and add your redirect URI (e.g. `https://127.0.0.1:5001/auth/schwab`)
2. Copy `App Key` → `Schwab:AppKey` (user-secrets) and `App Secret` → `Schwab:AppSecret` (user-secrets)
3. Set `Schwab:RedirectUri` in `appsettings.json` to match exactly what you registered
4. Run the app and navigate to `/auth/schwab` → click **Connect with Schwab**
5. Log in on Schwab's login page and approve the app — you are redirected back and tokens are saved to `Schwab:TokenFile`

| Token | Lifetime | Action on expiry |
|-------|----------|-----------------|
| Access token | ~30 min | Auto-refreshed silently before each API call (60 s safety buffer) |
| Refresh token | 7 days from last use | Re-authenticate manually at `/auth/schwab` |

Schwab sends credentials as a **Basic auth header** (`Authorization: Basic base64(key:secret)`).

#### TradeStation

1. Create an app at [developer.tradestation.com](https://developer.tradestation.com) and register your redirect URI (e.g. `https://127.0.0.1:5001/auth/tradestation`)
2. Copy `Client ID` → `TradeStation:ClientId` (user-secrets) and `Client Secret` → `TradeStation:ClientSecret` (user-secrets)
3. Set `TradeStation:RedirectUri` in `appsettings.json` to match exactly what you registered
4. Run the app and navigate to `/auth/tradestation` → click **Connect with TradeStation**
5. Log in on TradeStation's site and approve the scopes — you are redirected back and tokens are saved to `TradeStation:TokenFile`

Scopes requested: `openid profile offline_access MarketData ReadAccount Trade Crypto`

| Token | Lifetime | Action on expiry |
|-------|----------|-----------------|
| Access token | ~20 min | Auto-refreshed silently before each API call (60 s safety buffer) |
| Refresh token | varies | Re-authenticate manually at `/auth/tradestation` |

TradeStation sends credentials as **form-body fields** (`client_id` / `client_secret` in the POST body) rather than a Basic auth header.

#### Tradovate

Tradovate uses a direct credential POST — no OAuth2 browser redirect required.

1. Create a Tradovate API application at [tradovate.com](https://www.tradovate.com) (Settings → API) to obtain a `cid` and `secret`
2. Set credentials in user-secrets: `Tradovate:Username`, `Tradovate:Password`, `Tradovate:Cid`, `Tradovate:Secret`
3. Set `Tradovate:ApiBaseUrl`, `Tradovate:MdWssUrl`, `Tradovate:AccountId`, and `Tradovate:TokenFile` in `appsettings.json`
4. Run the app and navigate to `/auth/tradovate` → click **Connect** to authenticate and save tokens

| Token | Lifetime | Action on expiry |
|-------|----------|-----------------|
| Access token | 90 minutes | Auto-renewed silently when < 5 min remain; also renewed via **Renew** button at `/auth/tradovate` |
| MD access token | 90 minutes | Same renewal — both tokens refreshed together |

Tradovate returns **two tokens**: `accessToken` (used for REST trading endpoints) and `mdAccessToken` (used for the market-data WebSocket). Both are persisted to `Tradovate:TokenFile`.

For paper trading, change `Tradovate:ApiBaseUrl` to `https://demo.tradovateapi.com/v1` and `Tradovate:MdWssUrl` to `wss://md.tradovateapi.com/v1/websocket` (the MD WebSocket is shared between live and demo environments).

**Token file security:** `schwab_tokens.json`, `tradestation_tokens.json`, and `tradovate_tokens.json` contain live tokens. All three files are excluded by the repo's `.gitignore` and must never be committed.

### Database (`CRV.Core`)

SQLite via EF Core. `TradingDbContext` manages:

| Table | Model | Indexes |
|-------|-------|---------|
| `Trades` | `TradeRecord` | `Ticker`, `ExitReason` |
| `Configs` | `StrategyConfig` | — |
| `BacktestRuns` | `BacktestRunRow` | — |

## StrategyConfig Reference

Full property list from `CRV.Core/Models/StrategyConfig.cs`. Call `config.Validate()` to get a `IReadOnlyList<string>` of validation errors before use.

### Identity

| Property | Default | Notes |
|----------|---------|-------|
| `Name` | `"Default"` | Config label stored in DB |

### Instrument

| Property | Default | Notes |
|----------|---------|-------|
| `Ticker` | `NQH26` | Canonical futures symbol (no slash, 2-digit year); converted per broker at runtime by `FuturesSymbol.ForBroker()` |
| `Exchange` | `CME` | |
| `PointValue` | `20` | Dollar value per point |
| `TickSize` | `0.25` | Minimum price increment |

### Timeframe & Sessions

| Property | Default | Notes |
|----------|---------|-------|
| `ExecutionTFMinutes` | `1` | Bar timeframe: `1`, `2`, `5`, `15`, `30`, or `60` |
| `Timezone` | `America/New_York` | IANA timezone for session times |
| `OrbStart` / `OrbEnd` | `09:30` / `10:00` | Opening range window |
| `RthStart` / `RthEnd` | `09:30` / `16:00` | Regular trading hours |
| `SessionStartHour` | `18` | Hour (local) when the futures session starts (6 PM ET for CME); triggers new-day resets for ORB, VWAP, daily stats, and setup state |

### Filters

| Property | Default | Notes |
|----------|---------|-------|
| `AtrFilterPct` | `0.50` | Minimum ATR as % of price to trade (0 = disabled) |
| `UseVwap` | `true` | Require price on correct side of VWAP at entry |
| `UseOrbClose` | `false` | Require bar close inside ORB before taking trade |
| `UseTimeFilter` | `true` | Block new entries after cutoff time |
| `CutoffHour` | `14` | No-new-entries hour (ET, 24-hour) |
| `CutoffMinute` | `30` | No-new-entries minute |
| `UseDailyLossLimit` | `true` | Halt trading when daily loss exceeds limit |
| `MaxDailyLoss` | `500` | Daily loss limit in dollars |
| `AllowBothSameBar` | `false` | Allow Setup A and B entries on the same bar |

### Position Sizing

| Property | Default | Notes |
|----------|---------|-------|
| `Contracts` | `2` | Base position size |
| `MaxContracts` | `2` | Hard cap on contracts per trade |
| `HiVolMult` | `1.0` | Contract multiplier during high-volatility sessions |

### Broker

| Property | Default | Notes |
|----------|---------|-------|
| `Broker` | `Schwab` | Data feed source: `Schwab`, `TradeStation`, or `Tradovate` |
| `ExecBroker` | `null` | Order execution broker; `null` = same as `Broker`; set to `Mock` to paper trade live data without placing real orders |
| `AccountId` | `""` | Broker account identifier (Schwab hash, TS account ID, or Tradovate account ID) |
| `ExecAccountId` | `""` | Override account for exec broker when different from data broker |
| `CommissionPerSide` | `2.25` | Per contract per side (used in P&L calculations) |

### Setup A — Pullback

| Property | Default | Notes |
|----------|---------|-------|
| `EnableA` | `true` | Enable Setup A |
| `ModeA` | `Conservative` | `Conservative` or `Aggressive` (entry timing) |
| `MaxTradesA` | `5` | Max Setup A entries per session (0 = unlimited) |
| `NearPct` | `0.15` | Entry zone: must be within this % of ORB high/low |
| `PullbackPct` | `0.50` | Minimum pullback size as % of opening range |
| `StopPctA` | `0.10` | Stop width as % of opening range |
| `TargetPctA` | `100` | Final target as % of opening range beyond entry |
| `PartialPctA` | `50` | Partial exit target as % of `TargetPctA` |
| `MinRrA` | `1.5` | Minimum risk:reward to take the trade |
| `UsePartialA` | `true` | Scale out at partial target |
| `UseBeA` | `true` | Move stop to breakeven after partial fill |

### Setup B — Breakout Retest

| Property | Default | Notes |
|----------|---------|-------|
| `EnableB` | `true` | Enable Setup B |
| `ModeB` | `Conservative` | `Conservative` or `Aggressive` (entry timing) |
| `MaxTradesB` | `5` | Max Setup B entries per session (0 = unlimited) |
| `RetestPct` | `0.05` | Entry zone: within this % of ORB boundary after breakout |
| `TargetPctB` | `100` | Final target as % of opening range beyond breakout |
| `PartialPctB` | `50` | Partial exit target as % of `TargetPctB` |
| `MinRrB` | `1.5` | Minimum risk:reward to take the trade |
| `UsePartialB` | `true` | Scale out at partial target |
| `UseBeB` | `true` | Move stop to breakeven after partial fill |

### Forced Exit

| Property | Default | Notes |
|----------|---------|-------|
| `CloseAtRthClose` | `true` | Force-close all positions at `RthEnd` |

Positions can also be closed on-demand at any time:
- **Dashboard → Exit Now buttons** — each active setup card has an **Exit Now** button that calls `OrbStrategyEngine.RequestForceExitA/B()` via a `fetch` POST (no page reload). The engine applies the force-exit on the next bar.
- **Manual Trading page → Place Order section** — `/trading/manual` lets you place a fully specified OCO bracket order independent of the automated engine. Three input modes:
  - **Points** — stop and target specified as points away from entry
  - **Dollars** — stop and target specified as P&L dollar amounts (requires `PointValue`)
  - **Price Level** — stop and target specified as absolute price levels (requires entry price)

  Supports an optional partial split into two brackets (partial exit + runner).
- **Manual Trading page → Live Positions table** — shows all open futures positions fetched live from the broker API (auto-refreshes every 5 s). Each row has a **Flat** button that submits a market close order for that specific position using the broker-format symbol directly.
- **Manual Trading page → Cancel All** — cancels all open orders for the configured ticker in one click.
- **Orders page** — `/trading/orders` lists all orders filtered by status and date range. Each cancellable order has a **Cancel** button (status-gated: only shown when `CanCancel == true`).

## CI

GitHub Actions workflow at `.github/workflows/ci.yml` runs `dotnet build` and `dotnet test` on every push and pull request.

## Security Notes

- Never commit credentials (`AppKey`, `AppSecret`, `ClientId`, `ClientSecret`, Tradovate `Username`/`Password`/`Cid`/`Secret`) to source control
- Use `dotnet user-secrets` for local development; use environment variables or a secrets manager in production
- `AccountId`, `RedirectUri`, and API URLs are non-sensitive and can live in `appsettings.json`
- All three token files (`schwab_tokens.json`, `tradestation_tokens.json`, `tradovate_tokens.json`) contain live tokens — excluded by the repo's `.gitignore`; never commit them
- Schwab and TradeStation access tokens are short-lived and refreshed automatically with a 60 s safety buffer before expiry
- Tradovate access tokens expire after 90 minutes — auto-renewed when < 5 min remain via `POST /auth/renewAccessToken`
- Schwab refresh tokens expire after **7 days of inactivity** — re-authenticate at `/auth/schwab` when needed
- TradeStation refresh token lifetime varies — re-authenticate at `/auth/tradestation` if the engine logs an auth error
- Tradovate does not use refresh tokens — if the stored token expires, re-authenticate at `/auth/tradovate`
