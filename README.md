# CRV.Trading

ASP.NET Core web application for live and backtested ORB (Opening Range Breakout) futures trading. Supports Schwab, TradeStation, and Tradovate brokers with real-time dashboard via SignalR.

## Projects

| Project | Purpose |
|---------|---------|
| `CRV.Core` | Strategy engine, models, indicators, interfaces, DB context |
| `CRV.Backtest` | Backtesting engine, bar loaders (CSV, Schwab, TradeStation) |
| `CRV.Live` | Broker integrations (bar feeds + order executors), real-time bar builder |
| `CRV.Web` | ASP.NET Core Razor Pages UI, SignalR hub, background services, REST API |
| `CRV.Core.Tests` | xUnit tests (129 tests) covering indicators, strategy, modules, broker simulation, validation |

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
dotnet user-secrets set "Tradovate:Password"         "YOUR_PASSWORD"
dotnet user-secrets set "Tradovate:Cid"              "YOUR_CID"
dotnet user-secrets set "Tradovate:Secret"           "YOUR_SECRET"

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
