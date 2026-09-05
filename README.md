# CRV.Trading

[![CI](https://github.com/cirinocarvalho/CRV.Trading/actions/workflows/ci.yml/badge.svg)](https://github.com/cirinocarvalho/CRV.Trading/actions/workflows/ci.yml)
[![Deploy to Azure](https://github.com/cirinocarvalho/CRV.Trading/actions/workflows/deploy.yml/badge.svg)](https://github.com/cirinocarvalho/CRV.Trading/actions/workflows/deploy.yml)
[![Infrastructure](https://github.com/cirinocarvalho/CRV.Trading/actions/workflows/infra.yml/badge.svg)](https://github.com/cirinocarvalho/CRV.Trading/actions/workflows/infra.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-14.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-Razor%20Pages-5C2D91?logo=dotnet&logoColor=white)](https://learn.microsoft.com/aspnet/core/)
[![SignalR](https://img.shields.io/badge/SignalR-realtime-0078D4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/aspnet/core/signalr/)
[![EF Core](https://img.shields.io/badge/EF%20Core-10.0-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/ef/core/)
[![SQLite](https://img.shields.io/badge/SQLite-Litestream-003B57?logo=sqlite&logoColor=white)](https://litestream.io/)
[![Serilog](https://img.shields.io/badge/Serilog-Seq-1B1B1B?logo=serilog&logoColor=white)](https://serilog.net/)
[![Swagger](https://img.shields.io/badge/Swagger-OpenAPI-85EA2D?logo=swagger&logoColor=black)](https://swagger.io/)
[![xUnit](https://img.shields.io/badge/tests-xUnit-5B2C6F?logo=xunit&logoColor=white)](https://xunit.net/)
[![Docker](https://img.shields.io/badge/Docker-multi--stage-2496ED?logo=docker&logoColor=white)](https://www.docker.com/)
[![Azure](https://img.shields.io/badge/Azure-App%20Service%20%2B%20Bicep-0078D4?logo=microsoftazure&logoColor=white)](https://azure.microsoft.com/)
[![GitHub Actions](https://img.shields.io/badge/CI%2FCD-GitHub%20Actions-2088FF?logo=githubactions&logoColor=white)](https://github.com/features/actions)
[![Lightweight Charts](https://img.shields.io/badge/TradingView-Lightweight%20Charts%20v4-2962FF?logo=tradingview&logoColor=white)](https://tradingview.github.io/lightweight-charts/)

ASP.NET Core web application for live and backtested ORB (Opening Range Breakout) futures trading. Built on a **composable engine** architecture where strategies are **pure signal generators** (emit `EntrySignal` only) and `BrokerEventHandler` manages the full trade lifecycle (fills, partials, break-even moves, exits). Supports multiple instruments simultaneously via shared WebSocket connections. Supports Schwab, TradeStation, Tradovate, and Tradovate Replay brokers with real-time dashboard via SignalR. Includes analytical modules (session detection, VWAP bands, sweep detection, opening drive, trend day filter, false breakout detection) and high-volatility position sizing.

## Projects

| Project | Purpose |
|---------|---------|
| `CRV.Core` | Composable strategy engine (`ComposableEngine`, `TickerGroup`, `BrokerEventHandler`), pure signal strategies (`ISetupStrategy`), models, indicators, analytical modules, options payoff/chain/structure logic (`CRV.Core/Options`), interfaces, DB context |
| `CRV.Backtest` | Backtesting engine with `BacktestGroupOrderExecutor` + `BrokerEventHandler` fill simulation, bar loaders (CSV, Schwab, TradeStation, Tradovate) |
| `CRV.Live` | Broker integrations (single- and multi-ticker bar feeds, group order executors, event streams), option chain/quote/order transport, real-time bar builder |
| `CRV.Web` | ASP.NET Core Razor Pages UI, SignalR hub, background services, REST API |
| `CRV.Core.Tests` | xUnit tests covering indicators, strategy, modules, broker simulation, validation |

## Key Features

- **Composable engine** — `ComposableEngine` coordinates per-instrument `TickerGroup` instances, each owning shared indicators/modules and multiple `ISetupStrategy` implementations; strategies are **pure signal generators** that emit only `EntrySignal`; `BrokerEventHandler` manages the full trade lifecycle (entry confirmation, tg1 partial → break-even move, tg2 target, stop exits, P&L accrual) via `IGroupOrderExecutor` and broker event streams; `StrategyFactory` creates concrete strategies, `RiskManager` enforces daily limits, `SnapshotAggregator` builds dashboard snapshots
- **Multi-ticker support** — each setup can trade a different instrument (e.g. Setup A on NQ, Setup B on ES); indicators and modules are shared within each `TickerGroup`
- **WSS-style order management** — `IGroupOrderExecutor` creates `GroupOrder` with entry/stop/tg1/tg2 legs; broker event streams (`IBrokerEventStream`) push `OrderEvent` updates; `BrokerEventHandler` processes events and drives leg transitions (entry fill → place exit legs, tg1 fill → move stop to BE, tg2/stop fill → complete trade); same architecture for live (`MockGroupOrderExecutor`, `TradovateGroupOrderExecutor`) and backtest (`BacktestGroupOrderExecutor`)
- **Shared WebSocket connections** — Schwab uses comma-separated `keys` for multi-symbol subscriptions on a single WSS; Tradovate sends separate `md/getchart`/`md/subscribeQuote` per symbol on one WSS; TradeStation uses per-symbol HTTP streams (no sharing possible)
- **Setup Basket** — flexible setup configuration where each basket entry defines a strategy type + instrument + session slots + per-session cutoffs; replaces the fixed A/B/C/D slots with N user-defined entries (e.g. "Retest on MNQ for NY", "Retest on MGC for Asia+London", "SessionFakeout on MGC for NY"); dashboard dynamically renders only the cards in the basket; settings page with Add/Remove basket entries
- **Strategy types** — Pullback (trend continuation), Retest (breakout retest), OrbFakeout (ORB false breakout reversal), SessionFakeout (prior session range false breakout reversal); each type can be used multiple times across different instruments and sessions; all strategies are pure signal generators — they emit `EntrySignal` with entry/stop/tg1/tg2 levels and `BrokerEventHandler` manages the full trade lifecycle
- **False breakout detection module** — `FalseBreakoutDetector` with two `RangeBreakoutTracker` instances (ORB and session range); tracks breakout → bar counting → rejection → activation with configurable max time outside, penetration depth, body rejection %, and trend day score thresholds; feeds OrbFakeout and SessionFakeout strategies
- **Compound fakeout** — when both ORB and session range trackers activate in the same direction simultaneously, flagged as a higher-conviction signal
- **Multi-session trading** — Asia (7 PM–12 AM), London (3 AM–8 AM), and NY (9:30 AM–4 PM) sessions with independent ORB formation, per-session setup configs, and automatic engine reconfiguration at session boundaries via `SessionManager`
- **Tick-level execution** — L1 tick entry evaluation alongside bar-level indicator/arm-state processing; `BrokerEventHandler` drives exit fills (stop/tg1/tg2) via `EvaluateFills` on each tick
- **Fill price feedback** — brokers report actual fill prices via `OrderEvent`; `BrokerEventHandler` uses real fill prices for stop/target levels and P&L calculations
- **Per-setup last price** — each setup card shows the last traded price from its subscribed instrument with a blinking green indicator dot, visible even when idle
- **USD values on trade levels** — Stop, Partial, and Target rows show dollar risk/reward computed as `|level − entry| × contracts × pointValue`
- **High-vol position sizing** — `CalcContracts()` scales contracts when ORB/ATR ratio ≥ 1.0
- **Per-session cutoff** — each basket entry has independent cutoff times per session (Asia/London/NY); overnight session cutoffs handle midnight wrap correctly; armed states disarmed at cutoff to prevent stale entries; dashboard shows CUTOFF badge per setup
- **Early session exit** — force-exits active trades N minutes before session end (configurable per settings)
- **Daily loss limit** — halts trading when cumulative daily P&L exceeds the configured threshold, with live gauge visualization on the dashboard
- **Target % ORB options** — target levels at 25%, 50%, 75%, 100%, 125%, 150%, 200%, 250%, 300%, 350%, 400%, 450%, 500% of the ORB range
- **Tradovate Replay** — run strategy against historical market data at configurable speeds (25–400%) via Tradovate's Market Replay service (`wss://replay.tradovateapi.com`)
- **Mock broker** — `MockGroupOrderExecutor` with full group order book for paper trading without a live account; events delivered via `MockEventStream` channel
- **Backtest fill simulation** — `BacktestGroupOrderExecutor` + `BrokerEventHandler` provide the same WSS-style fill simulation as live; synchronous event delivery (no async channel) for single-threaded backtest; OHLC tick-level stop/target evaluation
- **Bracket leg discovery** — all brokers automatically discover stop/target order IDs after entry fill when the initial bracket response doesn't include them (handles async child order creation by exchanges)
- **Lightweight Charts dashboard** — TradingView Lightweight Charts v4 powered by your own broker data (no CME licensing restrictions); dark theme with hollow teal up / solid dark-red down candles, volume histogram with MA(21) overlay, dynamic VWAP line (white 50% transparency), and ORB high/low/mid price lines
- **Multi-instrument chart switching** — group-selector pills (derived from basket instruments) let you switch between instruments; chart loads ~200 historical bars via REST then receives live candle updates via SignalR; visible range limited to last 8 hours to prevent thin bars on multi-session data; proper cleanup of price lines, auto-scale reset, and async race guards on instrument switch
- **Live candle formation** — between confirmed bars, the current candle's close/high/low is updated tick-by-tick using `ILastPriceProvider` so the forming bar reflects real-time price action
- **Per-group Market Context** — session, trend score, VWAP state/bands, sweep/drive/asia signals, active setup, signal strength (with drive/sweep/VWAP-dev sub-scores) all reflect the currently selected instrument group, not global/primary ticker
- **Key Levels card** — consolidated ORB (high/low/mid/range), VWAP (price + above/below position), and ATR(14) with ORB/ATR ratio in a single dashboard card below False Breakout
- **Realistic Target P&L** — active trade cards show post-partial P&L: partial contracts × partial distance + remaining contracts × target distance, instead of full-position P&L
- **Dashboard gauges** — win rate and daily loss limit displayed as half-circle gauges with color-coded thresholds (green → yellow → red)
- **Session badge** — dashboard shows the currently active session (Asia/London/NY), PreMarket/Midday, or Idle status in real-time
- **Dynamic setup cards** — dashboard renders N cards based on basket entries instead of fixed 4 slots; each card shows setup label, instrument, trade state, and per-setup ORB levels
- **Per-session stats** — Sessions detail page with tabbed per-session trade breakdown and performance metrics
- **Contract roll calendar** — auto-resolves active contract per broker; no continuous contract symbols used
- **Options explorer** (`/options/explorer`) — stock, ETF and index options on Schwab, independent of the futures engine; chain browsing with liquidity columns, click-to-build structures, expiration payoff chart, order ticket with preview, open positions, and every option order working at the broker. See [Options](docs/options.md)
- **Structure finder** — state a target price and it builds every structure the chain supports for that view, prices each at the side you would actually trade, and ranks by profit at the target; each candidate carries a sensitivity row (payoff below/at/above target) because ranking on the target alone always flatters structures that pay only at a precise price
- **Liquidity gating** — admission is `bid > 0` plus spread as a percentage of mid, not open interest; measured on a live SPY chain, 610 contracts quoted at or below $0.02 carried open interest as high as 10,717 while 62% had no bid at all
- **Options order safety** — live placement off unless `Options:AllowLiveOrders`; a spread is always one net-priced order (never legged in); leg quantity is a count rather than part of the price; OSI symbols carried verbatim from the chain; never a market order; size resets to 1 on every structure change; preview re-quotes every leg and the confirm dialog locks after 30 seconds
- **Conditional orders** — a thinkorswim Order Rule rests at Schwab as *Awaiting Condition* and can be seen and cancelled here; the REST API reports that an order awaits a condition but not what the condition is, so these are built in thinkorswim and managed in the app
- **Structured logging** via Serilog + Seq

## Quick Start

```bash
# Build
dotnet build CRV.Trading.sln

# Set broker credentials (never commit these)
cd CRV.Web
dotnet user-secrets set "Schwab:AppKey"             "YOUR_KEY"
dotnet user-secrets set "Schwab:AppSecret"          "YOUR_SECRET"
dotnet user-secrets set "TradeStation:ClientId"     "YOUR_ID"
dotnet user-secrets set "TradeStation:ClientSecret" "YOUR_SECRET"
dotnet user-secrets set "Tradovate:Username"         "YOUR_USERNAME"
dotnet user-secrets set "Tradovate:Password"         'YOUR_PASSWORD'
dotnet user-secrets set "Tradovate:Cid"              "YOUR_CID"
dotnet user-secrets set "Tradovate:Secret"           "YOUR_SECRET"
dotnet user-secrets set "Tradovate:DeviceId"         "YOUR_DEVICE_ID"
dotnet user-secrets set "Tradovate:AppId"            "YOUR_APP_ID"

# Configure account IDs in appsettings.json (non-sensitive)

# Run (HTTPS required for OAuth2)
dotnet run --launch-profile https

# Open https://localhost:5001
# Swagger: https://localhost:5001/swagger (Development only)

# Authorize brokers at /auth/schwab, /auth/tradestation, /auth/tradovate
```

## Running Tests

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj
```

## Cloud deployment (Azure)

Deployed to Azure App Service (Linux, container) via GitHub Actions with
Bicep-declared infrastructure and Azure Key Vault for secrets. Runs at
~$19/mo on a B1 plan. Full end-to-end runbook: [deploy/DEPLOY.md](deploy/DEPLOY.md).

**Shape of the deploy:**

- **Infra as code** — `deploy/main.bicep` declaratively defines ACR, App
  Service plan, Web App, Key Vault, Storage + Blob container, role
  assignments, Entra Easy Auth config
- **Two workflows** — `infra.yml` deploys Bicep (what-if on PR, apply on
  merge); `deploy.yml` builds the image in ACR and updates the Web App
- **Zero long-lived secrets** — GitHub Actions auths to Azure via OIDC
  federation; broker credentials live in Key Vault and are read via
  `@Microsoft.KeyVault(...)` app setting references
- **Persistent SQLite + continuous backup** — DB on `/home/data` (Azure
  Files mount); Litestream streams WAL writes to Azure Blob every ~1s
  with 24h of point-in-time recovery
- **Entra Easy Auth** — the site is gated by Azure AD login; `/api/engine/status`
  and `/api/engine/webhook/order` are auth-excluded for health checks
  and external webhooks
- **Timezone** — container runs in `America/New_York` so log/UI
  timestamps match trading session time

Quick bring-up:

```bash
./deploy/github-oidc-setup.sh      # one-time bootstrap
gh workflow run infra.yml -f seedPlaceholderSecrets=true
./deploy/app-entra.sh               # Easy Auth app registration
./deploy/set-secrets.sh             # populate broker creds in Key Vault
gh workflow run deploy.yml          # first real image build
```

## Documentation

| Document | Contents |
|----------|----------|
| [Architecture](docs/architecture.md) | Engine design, tick mode, bar feeds, order executors, symbol conversion, web layer, pages |
| [Configuration](docs/configuration.md) | Launch profiles, appsettings, EF migrations, full StrategyConfig property reference |
| [Options](docs/options.md) | Options explorer — payoff/chain/structure types, liquidity gating, order construction, safety model, conditional orders |
| [Broker Auth](docs/brokers.md) | Schwab/TradeStation/Tradovate authentication setup, token lifecycle, security |
| [API & SignalR](docs/api.md) | REST endpoints, SignalR hub messages, EngineSnapshot fields, client-side events |
| [Cloud Deploy](deploy/DEPLOY.md) | End-to-end Azure deployment runbook |
| [Operations](deploy/OPERATIONS.md) | Day-2 ops — logs, SQLite access, backups/restore, troubleshooting |
| [Secrets](deploy/SECRETS.md) | Credential inventory, rotation, tiers |
| [OAuth Redirects](deploy/OAUTH_REDIRECTS.md) | Broker-side URI registration |

## CI/CD

| Workflow | Trigger | Purpose |
|---|---|---|
| `.github/workflows/ci.yml` | Every push + PR | Build + test (.NET 10) |
| `.github/workflows/deploy.yml` | Push to master + manual | Build image in ACR, update Web App, smoke test |
| `.github/workflows/infra.yml` | Bicep file change + manual | `what-if` on PR, `apply` on master |

## Security

- **Local dev**: credentials in `dotnet user-secrets` (never in `appsettings.json`); OAuth token files are git-ignored
- **Production**: credentials in Azure Key Vault, fetched by App Service
  managed identity; repo has zero long-lived Azure secrets (OIDC)
- **App gate**: Entra ID Easy Auth on every request except auth-excluded health/webhook paths
- **Order webhook**: `POST /api/engine/webhook/order` places real orders and cannot use an interactive login, so it is gated by a shared secret (`Webhook:Secret`, Key Vault `Webhook--Secret`) compared in constant time. It **fails closed** — with no secret configured it refuses every caller rather than accepting anonymous orders
- **Anonymous surface**: only `/api/engine/health` (liveness, no trading data) is unauthenticated; `/api/engine/status` carries P&L and positions and stays behind Easy Auth
- Full details: [deploy/SECRETS.md](deploy/SECRETS.md), [docs/brokers.md](docs/brokers.md)

## License

Released under the [MIT License](LICENSE) — © 2026 Cirino Carvalho.

> **Disclaimer**: This software is provided for educational and research purposes.
> It is not financial advice. Trading futures involves substantial risk of loss —
> use at your own risk.
