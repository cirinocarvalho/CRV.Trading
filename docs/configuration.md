# Configuration

## Launch Profiles

| Profile | Ports | Command |
|---------|-------|---------|
| `http` | HTTP only on port 5000 | `dotnet run --launch-profile http` |
| `https` | HTTPS on 5001 + HTTP on 5000 | `dotnet run --launch-profile https` |

OAuth2 redirect URIs are configured for `https://127.0.0.1:5001`. Always use:
```bash
dotnet run --launch-profile https
```

HTTPS dev certificate: `dotnet dev-certs https --check --trust`

## appsettings.json

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

## Settings Reference

| Key | Sensitive? | Notes |
|-----|------------|-------|
| `Schwab:AppKey` / `AppSecret` | Yes (user-secrets) | OAuth2 client credentials |
| `Schwab:ApiBaseUrl` | No | REST API base URL |
| `Schwab:WssBaseUrl` | No | WebSocket streaming URL |
| `Schwab:AccountId` | No | Encrypted account hash |
| `Schwab:RedirectUri` | No | Must match registered URI |
| `Schwab:TokenFile` | No | Token persistence path |
| `TradeStation:ClientId` / `ClientSecret` | Yes (user-secrets) | From TS developer portal |
| `TradeStation:ApiBaseUrl` | No | REST API base URL |
| `TradeStation:AuthBaseUrl` | No | OAuth2 authorization server |
| `TradeStation:AccountId` | No | Account ID |
| `TradeStation:RedirectUri` | No | Must match registered URI |
| `TradeStation:TokenFile` | No | Token persistence path |
| `Tradovate:Username` / `Password` | Yes (user-secrets) | Account login |
| `Tradovate:Cid` / `Secret` | Yes (user-secrets) | API app credentials |
| `Tradovate:ApiBaseUrl` | No | REST API base URL (use `demo.tradovateapi.com/v1` for paper) |
| `Tradovate:MdWssUrl` | No | Market-data WebSocket URL |
| `Tradovate:AccountId` | No | Account ID |
| `Tradovate:TokenFile` | No | Token persistence path |
| `Options:AllowLiveOrders` | No | **Defaults to `false`.** When false the options explorer previews orders against Schwab but cannot submit them. Best set in `appsettings.Development.json` (gitignored) so it arms a workstation without arming a deployment |
| `Options:MaxTradeRisk` | No | Per-trade dollar ceiling for option structures; `0` disables it. Re-checked on the place call, not only on preview |
| `Options:MaxPortfolioRisk` | No | Ceiling on total premium at risk across all open long option positions **plus** the order being placed; `0` disables it. Fails closed — when exposure cannot be established the order is refused, because a risk gate that opens on error is not a gate |

> API URLs, `AccountId`, and `RedirectUri` are non-sensitive and live in `appsettings.json`. Credentials must always be in user-secrets or environment variables. Token files are excluded by `.gitignore`.

## EF Core Migrations

Migrations live in `CRV.Core/Migrations/`. On startup, the app applies pending migrations automatically (`Database.Migrate()`). If no migrations are tracked, it falls back to `Database.EnsureCreated()`.

| Migration | Adds |
|-----------|------|
| `20260306200430_Initial` | Baseline tables (`Trades`, `Configs`, `BacktestRuns`) |
| `20260310184233_AddMissingStrategyConfigColumns` | `SessionStartHour`, `ExecBroker`, `ExecAccountId`, `EntryTickOffsetA/B` |
| `20260311020220_AddStopPctB` | `StopPctB` column |
| `20260311234654_PerSetupConfigFields` | Per-setup fields (`ContractsA/B`, `HiVolMultA/B`, `MaxContractsA/B`, `CutoffHourA/B`, `CutoffMinuteA/B`, `UseVwapA/B`, `UseOrbCloseA/B`, `CloseAtRthCloseA/B`) |

To generate new migrations:
```bash
dotnet ef migrations add <MigrationName> --project CRV.Core --startup-project CRV.Web
dotnet ef database update --startup-project CRV.Web
```

## StrategyConfig Reference

Full property list from `CRV.Core/Models/StrategyConfig.cs`. Call `config.Validate()` for validation errors.

### Identity & Instrument

| Property | Default | Notes |
|----------|---------|-------|
| `Name` | `"Default"` | Config label |
| `Ticker` | `/NQH2026` | Futures symbol; normalized to canonical format (`NQH26`) by `FuturesSymbol.Normalize()` |
| `Exchange` | `CME` | |
| `PointValue` | `20` | Dollar value per point |
| `TickSize` | `0.25` | Minimum price increment |

### Timeframe & Sessions

| Property | Default | Notes |
|----------|---------|-------|
| `ExecutionTFMinutes` | `1` | Bar timeframe (1/2/5/15/30/60) |
| `Timezone` | `America/New_York` | |
| `OrbStart` / `OrbEnd` | `09:30` / `10:00` | Opening range window |
| `RthStart` / `RthEnd` | `09:30` / `16:00` | Regular trading hours |
| `SessionStartHour` | `18` | Futures session start (6 PM ET for CME) |

### Filters (Global)

| Property | Default | Notes |
|----------|---------|-------|
| `AtrFilterPct` | `0.50` | Minimum ATR as % of price (0 = disabled) |
| `UseTimeFilter` | `true` | Block entries after cutoff |
| `UseDailyLossLimit` | `true` | Halt on daily loss limit |
| `MaxDailyLoss` | `500` | Daily loss limit in dollars |
| `AllowBothSameBar` | `false` | Allow A and B entries on same bar |
| `CommissionPerSide` | `2.25` | Per contract per side; auto-set by UI based on broker + contract type (see below) |

### Commission Rates

Auto-populated by the Live Settings UI when broker or instrument changes. Editable for manual override.

| Broker | Micro ($/side) | Mini ($/side) |
|--------|---------------|--------------|
| Schwab | 2.25 | 2.25 |
| TradeStation | 0.75 | 2.00 |
| Tradovate | 0.80 | 2.20 |
| Mock | 0.00 | 0.00 |

Micro contracts: MNQ, MES, MGC, MCL. Mini contracts: NQ, ES, GC, CL.

Server-side: `FuturesSymbol.DefaultCommission(broker, ticker)`. Client-side: `CRV_getCommission(broker, tickerBase)` in `crv-instruments.js`.

### Broker

| Property | Default | Notes |
|----------|---------|-------|
| `Broker` | `Schwab` | Data feed source |
| `ExecBroker` | `null` | Order execution broker; `null` = same as `Broker`; `Mock` for paper trading |
| `AccountId` | `""` | Broker account ID |
| `ExecAccountId` | `""` | Override account for exec broker |

### Setup A — Pullback

| Property | Default | Notes |
|----------|---------|-------|
| `EnableA` | `true` | Enable Setup A |
| `ModeA` | `Conservative` | Entry timing mode |
| `MaxTradesA` | `5` | Max entries per session |
| `NearPct` | `0.15` | Entry zone (% of ORB) |
| `PullbackPct` | `0.50` | Min pullback size (% of ORB) |
| `StopPctA` | `0.10` | Stop width (% of ORB) |
| `TargetPctA` | `100` | Target (% of ORB) |
| `PartialPctA` | `50` | Partial target (% of target) |
| `MinRrA` | `1.5` | Minimum risk:reward |
| `UsePartialA` / `UseBeA` | `true` | Partial scaling / BE stop |
| `EntryTickOffsetA` | `0` | Ticks added in trade direction |
| `ContractsA` | `2` | Base position size |
| `HiVolMultA` | `1.0` | High-vol contract multiplier |
| `MaxContractsA` | `2` | Hard cap on contracts |
| `CutoffHourA` / `CutoffMinuteA` | `14` / `30` | Per-setup cutoff time |
| `UseVwapA` | `true` | VWAP filter |
| `UseOrbCloseA` | `false` | ORB close filter |
| `CloseAtRthCloseA` | `true` | Force-close at RTH end |

### Setup B — Breakout Retest

| Property | Default | Notes |
|----------|---------|-------|
| `EnableB` | `true` | Enable Setup B |
| `ModeB` | `Conservative` | Entry timing mode |
| `MaxTradesB` | `5` | Max entries per session |
| `RetestPct` | `0.05` | Entry zone (% of ORB) |
| `StopPctB` | `0.50` | Stop width (% of ORB) |
| `TargetPctB` | `100` | Target (% of ORB) |
| `PartialPctB` | `50` | Partial target (% of target) |
| `MinRrB` | `1.5` | Minimum risk:reward |
| `UsePartialB` / `UseBeB` | `true` | Partial scaling / BE stop |
| `EntryTickOffsetB` | `0` | Ticks added in trade direction |
| `ContractsB` | `2` | Base position size |
| `HiVolMultB` | `1.0` | High-vol contract multiplier |
| `MaxContractsB` | `2` | Hard cap on contracts |
| `CutoffHourB` / `CutoffMinuteB` | `14` / `30` | Per-setup cutoff time |
| `UseVwapB` | `true` | VWAP filter |
| `UseOrbCloseB` | `false` | ORB close filter |
| `CloseAtRthCloseB` | `true` | Force-close at RTH end |
