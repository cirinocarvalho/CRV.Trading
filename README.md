# CRV.Trading

ASP.NET Core web application for live and backtested ORB (Opening Range Breakout) futures trading. Built on a **composable engine** architecture that supports multiple instruments simultaneously via shared WebSocket connections. Supports Schwab, TradeStation, Tradovate, and Tradovate Replay brokers with real-time dashboard via SignalR. Includes analytical modules (session detection, VWAP bands, sweep detection, opening drive, trend day filter, false breakout detection) and high-volatility position sizing.

## Projects

| Project | Purpose |
|---------|---------|
| `CRV.Core` | Composable strategy engine (`ComposableEngine`, `ISetupStrategy`, `TickerGroup`), models, indicators, analytical modules, interfaces, DB context |
| `CRV.Backtest` | Backtesting engine, bar loaders (CSV, Schwab, TradeStation, Tradovate) |
| `CRV.Live` | Broker integrations (single- and multi-ticker bar feeds, order executors), real-time bar builder |
| `CRV.Web` | ASP.NET Core Razor Pages UI, SignalR hub, background services, REST API |
| `CRV.Core.Tests` | xUnit tests covering indicators, strategy, modules, broker simulation, validation |

## Key Features

- **Composable engine** — `ComposableEngine` coordinates per-instrument `TickerGroup` instances, each owning shared indicators/modules and multiple `ISetupStrategy` implementations; `StrategyFactory` creates concrete strategies, `RiskManager` enforces daily limits, `SnapshotAggregator` builds dashboard snapshots
- **Multi-ticker support** — each setup can trade a different instrument (e.g. Setup A on NQ, Setup B on ES); indicators and modules are shared within each `TickerGroup`
- **Shared WebSocket connections** — Schwab uses comma-separated `keys` for multi-symbol subscriptions on a single WSS; Tradovate sends separate `md/getchart`/`md/subscribeQuote` per symbol on one WSS; TradeStation uses per-symbol HTTP streams (no sharing possible)
- **Setup Basket** — flexible setup configuration where each basket entry defines a strategy type + instrument + session slots + per-session cutoffs; replaces the fixed A/B/C/D slots with N user-defined entries (e.g. "Retest on MNQ for NY", "Retest on MGC for Asia+London", "SessionFakeout on MGC for NY"); dashboard dynamically renders only the cards in the basket; settings page with Add/Remove basket entries
- **Strategy types** — Pullback (trend continuation), Retest (breakout retest), OrbFakeout (ORB false breakout reversal), SessionFakeout (prior session range false breakout reversal); each type can be used multiple times across different instruments and sessions
- **Setup C** (ORB false breakout) — detects failed breakouts above/below the ORB range, enters the reversal when price re-enters the range; quality filters include rejection body %, penetration depth, VWAP side, and trend day score
- **Setup D** (session range false breakout) — same pattern applied to prior session high/low (Asia H/L for London, London H/L for NY); range locked at session boundaries via `FalseBreakoutDetector.OnSessionStart()`
- **False breakout detection module** — `FalseBreakoutDetector` with two `RangeBreakoutTracker` instances (ORB and session range); tracks breakout → bar counting → rejection → activation with configurable max time outside, penetration depth, body rejection %, and trend day score thresholds
- **Compound fakeout** — when both ORB and session range trackers activate in the same direction simultaneously, flagged as a higher-conviction signal
- **Multi-session trading** — Asia (7 PM–12 AM), London (3 AM–8 AM), and NY (9:30 AM–4 PM) sessions with independent ORB formation, per-session setup configs, and automatic engine reconfiguration at session boundaries via `SessionManager`
- **Tick-level execution** — L1 tick entry/exit/partial/BE alongside bar-level processing
- **Fill price feedback** — brokers poll for actual fill prices after market order placement; engine recalculates stop/target/partial levels based on real fills instead of theoretical entry prices
- **Per-setup last price** — each setup card shows the last traded price from its subscribed instrument with a blinking green indicator dot, visible even when idle
- **USD values on trade levels** — Stop, Partial, and Target rows show dollar risk/reward computed as `|level − entry| × contracts × pointValue`
- **High-vol position sizing** — `CalcContracts()` scales contracts when ORB/ATR ratio ≥ 1.0
- **Per-session cutoff** — each basket entry has independent cutoff times per session (Asia/London/NY); overnight session cutoffs handle midnight wrap correctly; armed states disarmed at cutoff to prevent stale entries; dashboard shows CUTOFF badge per setup
- **Early session exit** — force-exits active trades N minutes before session end (configurable per settings)
- **Daily loss limit** — halts trading when cumulative daily P&L exceeds the configured threshold, with live gauge visualization on the dashboard
- **Target % ORB options** — target levels at 25%, 50%, 75%, 100%, 125%, 150%, 200%, 250%, 300%, 350%, 400%, 450%, 500% of the ORB range
- **Tradovate Replay** — run strategy against historical market data at configurable speeds (25–400%) via Tradovate's Market Replay service (`wss://replay.tradovateapi.com`)
- **Mock broker** — full OCO order book for paper trading without a live account
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

## Documentation

| Document | Contents |
|----------|----------|
| [Architecture](docs/architecture.md) | Engine design, tick mode, bar feeds, order executors, symbol conversion, web layer, pages |
| [Configuration](docs/configuration.md) | Launch profiles, appsettings, EF migrations, full StrategyConfig property reference |
| [Broker Auth](docs/brokers.md) | Schwab/TradeStation/Tradovate authentication setup, token lifecycle, security |
| [API & SignalR](docs/api.md) | REST endpoints, SignalR hub messages, EngineSnapshot fields, client-side events |

## CI

GitHub Actions at `.github/workflows/ci.yml` runs build + test on every push and PR.

## Security

- Store credentials in `dotnet user-secrets` — never in `appsettings.json`
- Token files are excluded by `.gitignore`
- See [Broker Auth](docs/brokers.md) for token lifecycle details
