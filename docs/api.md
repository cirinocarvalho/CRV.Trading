# API & SignalR

## REST API

Endpoints are rate-limited to 5 req/s via the `engine-api` policy and documented at `/swagger` in Development.

## SignalR Hub

**Endpoint:** `/hubs/trading`

### Server-to-Client Messages

| Message | Payload |
|---------|---------|
| `EngineStatusChanged` | Status string (`"Live"` / `"Stopped"`) |
| `BrokerFallback` | Warning message string |
| `Update` | `EngineSnapshot` JSON (every bar) |
| `Alert` | `{ time, setup, type, color, message }` |

### EngineSnapshot Fields

| JSON field | Type | Description |
|-----------|------|-------------|
| `time` | DateTime | Bar timestamp |
| `ticker` | string | Active futures symbol |
| `isLive` | bool | Engine is processing live bars |
| `setupA` / `setupB` | `ActiveTradeView?` | Active trade state per setup |
| `todayPnl` | decimal | Running net P&L for session |
| `todayTrades` | int | Total closed trades today |
| `todayWins` / `todayLosses` | int | Win/loss breakdown |
| `todayMaxDD` | decimal | Largest intraday drawdown |
| `expectancy` / `expectancyA` / `expectancyB` | decimal | Per-trade expectancy (total + per setup) |
| `dailyLossLimit` | decimal | Configured `MaxDailyLoss` |
| `dailyLossUsed` | decimal | Dollars lost so far |
| `tradingHalted` | bool | Daily loss limit breached |
| `lastPrice` | decimal | Most recent tick/bar price |
| `lastUpdate` | DateTime | Timestamp of last price update |
| `vwap` | decimal | Current session VWAP |
| `atr` | decimal | ATR(14) value |
| `orbHigh` / `orbLow` / `orbMid` / `orbRange` | decimal | ORB levels |
| `orbBullClose` / `orbBearClose` | bool | ORB close quality flags |
| `orbAtrRatio` | decimal | Frozen ORB/ATR ratio (captured at ORB formation) |
| `orbFormed` | bool | ORB window complete |
| `pastCutoff` | bool | Past no-new-entries cutoff (both setups) |
| `pastCutoffA` / `pastCutoffB` | bool | Per-setup cutoff flags |
| `sessionEnded` | bool | RTH session ended |
| `setupAEnabled` / `setupBEnabled` | bool | Setup enabled flags from config |
| `setupAState` / `setupBState` | int | Setup state machine (A: 0=Idle, +/-1=Armed, +/-2=Active; B: 0=Idle, +/-1=Armed, +/-2=Retest, +/-3=Active) |
| `stickyTgtA` / `stickyStpA` | bool | Setup A exited via target/stop this bar |
| `stickyTgtB` / `stickyStpB` | bool | Setup B exited via target/stop this bar |
| `recentAlerts` | `AlertEvent[]` | Last 20 engine alerts |
| `currentSession` | string | Current session: Asia, London, NYOpen, Midday, PowerHour, PreMarket |
| `sessionHigh` / `sessionLow` | decimal | Full-day session high/low |
| `prevDayHigh` / `prevDayLow` | decimal | Previous day high/low |
| `asiaCompressed` | bool | Asia range < ATR * 1.2 |
| `lastSweep` | string | Last sweep event description (e.g. "PDH Bear") |
| `vwapUpper1` / `vwapUpper2` | decimal | VWAP +1/+2 sigma bands |
| `vwapLower1` / `vwapLower2` | decimal | VWAP -1/-2 sigma bands |
| `vwapState` | int | VWAP regime: +2 Extended Bull, +1 Accept Bull, 0 Neutral, -1 Accept Bear, -2 Extended Bear |
| `openingDriveBull` / `openingDriveBear` | bool | Opening drive detected |
| `trendScoreBull` / `trendScoreBear` | int | Trend day score (0-5) |
| `setupCBull` / `setupCBear` | bool | Sweep Reversal setup active |
| `setupDBull` / `setupDBear` | bool | Opening Drive Pullback setup active |
| `setupFBull` / `setupFBear` | bool | Midday VWAP Reversion setup active |

### ActiveTradeView Fields

| Field | Type | Notes |
|-------|------|-------|
| `setup` | int | `0` = A, `1` = B (SetupId enum) |
| `direction` | int | `0` = Long, `1` = Short (Direction enum) |
| `entry` | decimal | Fill price |
| `currentStop` | decimal | Current stop (may be BE-adjusted) |
| `target` / `partial` | decimal | Full target and partial level |
| `contracts` | int | Original position size |
| `remainingContracts` | int | Contracts still open after partial |
| `partialFilled` | bool | Partial scale-out executed |
| `lastPrice` | decimal | Most recent price for this setup |
| `unrealizedPnl` | decimal | Mark-to-market P&L |
| `enteredAt` | DateTime | Entry timestamp |

## Client-Side Events (`crv-hub.js`)

`crv-hub.js` re-dispatches SignalR messages as DOM `CustomEvent`s:

| DOM event | Fired when | `event.detail` |
|-----------|-----------|----------------|
| `crv:update` | `Update` received or initial state fetched | `EngineSnapshot` |
| `crv:alert` | `Alert` received | Alert object |
| `crv:status` | `EngineStatusChanged` or initial state fetched | Status string |

The nav bar is updated directly by `crv-hub.js`. Page scripts subscribe to `crv:update` / `crv:alert` for rendering.

**Alert sounds:** `crv-hub.js` plays a synthesized ding (Web Audio API) on every `Alert` message. Each alert type has a distinct frequency: ENTRY (880 Hz), EXIT (660 Hz), PARTIAL/MOVE_BE (784 Hz). No audio files required.

**Initial state sync:** On SignalR connect/reconnect, `crv-hub.js` fetches `GET /api/engine/status` and dispatches `crv:update` / `crv:status` immediately. `connection.onclose()` forces the badge to `OFFLINE`.
