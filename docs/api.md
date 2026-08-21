# API & SignalR

## REST API

Endpoints are rate-limited to 5 req/s via the `engine-api` policy and documented at `/swagger` in Development.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/engine/start` | POST | Start the engine |
| `/api/engine/stop` | POST | Stop the engine |
| `/api/engine/health` | GET | Anonymous liveness probe — `{status, running, time}`. Carries no trading data; used by the Azure health check and the deploy smoke test |
| `/api/engine/status` | GET | Current engine state + last snapshot (P&L, positions). **Requires auth** — behind Easy Auth in production |
| `/api/engine/stream` | GET | SSE stream of engine snapshots (heartbeat every ~30s) |
| `/api/engine/price/{ticker}` | GET | Last known price for a ticker (tries canonical then broker-formatted) |
| `/api/engine/force-orb` | POST | Force-set ORB from historical broker bars |
| `/api/engine/bars/{groupKey}` | GET | Bar history for a ticker group (Lightweight Charts); returns `[{time, open, high, low, close, volume, vwap}]`; falls back to Schwab/Tradovate REST when in-memory buffer is empty |
| `/api/engine/trades/today` | GET | Today's completed trades for the dashboard table |
| `/api/engine/webhook/order` | POST | External order entry (TradingView alerts, scripts). **Requires the shared secret** — see below |

### Order webhook authentication

`POST /api/engine/webhook/order` places a real order, and it is excluded from
Entra Easy Auth so external senders can reach it. A shared secret is therefore
the only thing in front of it.

Configure `Webhook:Secret` — Key Vault `Webhook--Secret` in production,
`dotnet user-secrets set "Webhook:Secret" "…"` locally. Generate one with
`openssl rand -base64 32`.

The endpoint **fails closed**: if the secret is unset, still `CHANGE_ME`, or
shorter than 16 characters, every call is refused with `503`. It never falls
back to accepting anonymous orders.

Callers present the secret either way:

```bash
# Header — scripts, curl
curl -X POST https://<host>/api/engine/webhook/order \
  -H "X-Webhook-Secret: $WEBHOOK_SECRET" \
  -H "Content-Type: application/json" \
  -d '{"direction":"long","entry":21000,"stop":20980,"qty":1,"tgt1":21020,"tgt2":21040}'
```

```json
// Body field — TradingView alerts, which cannot set custom headers
{ "secret": "…", "direction": "long", "entry": 21000, "stop": 20980, "qty": 1 }
```

| Response | Meaning |
|---|---|
| `503` | No usable secret configured server-side — the webhook is disabled |
| `401` | Missing or wrong secret |
| `400` | Authenticated, but the order failed validation |

The comparison is constant-time, so a wrong secret leaks nothing through
response timing. Rejected attempts are logged with the caller's IP; the secret
itself is never logged or echoed back.

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
| `groupSnapshots` | `dict<string, TickerGroupSnapshot>` | Per-instrument-group context data keyed by group (NQ, ES, GC, etc.) |
| `tickerA` / `tickerB` | string | Broker ticker symbol per setup |
| `pointValueA` / `pointValueB` | decimal | Dollar-per-point multiplier per setup |
| `lastPriceA` / `lastPriceB` | decimal | Last traded price per setup |

### TickerGroupSnapshot Fields

| Field | Type | Description |
|-------|------|-------------|
| `groupKey` | string | Group identifier (NQ, ES, GC, CL, etc.) |
| `ticker` | string | Representative broker ticker for this group |
| `currentSession` | string | Active session name |
| `sessionHigh` / `sessionLow` | decimal | Session high/low |
| `prevDayHigh` / `prevDayLow` | decimal | Previous day high/low |
| `asiaCompressed` | bool | Asia range < ATR × 1.2 |
| `lastSweep` | string | Last sweep event (e.g. "PDH Bear") |
| `vwap` | decimal | Session VWAP |
| `vwapUpper1` / `vwapLower1` | decimal | VWAP ±1σ bands |
| `vwapUpper2` / `vwapLower2` | decimal | VWAP ±2σ bands |
| `vwapState` | int | VWAP regime (+2/+1/0/-1/-2) |
| `openingDriveBull` / `openingDriveBear` | bool | Opening drive detected |
| `trendScoreBull` / `trendScoreBear` | int | Trend day score (0–5) |
| `orbHigh` / `orbLow` / `orbMid` / `orbRange` | decimal | ORB levels |
| `orbBullClose` / `orbBearClose` | bool | ORB close quality |
| `orbAtrRatio` | decimal | Frozen ORB/ATR ratio |
| `orbFormed` | bool | ORB window complete |
| `atr` | decimal | ATR(14) value |
| `barTime` | long | Current bar UTC unix seconds (for live chart) |
| `barOpen` / `barHigh` / `barLow` / `barClose` | decimal | Current bar OHLC (with live price injected) |
| `barVolume` | long | Current bar volume |
| `fbOrbBreakoutActive` / `fbSessionBreakoutActive` | bool | False breakout tracking active |
| `fbOrbBarsInBreakout` / `fbSessionBarsInBreakout` | int | Bars spent in breakout |
| `fbOrbPenetrationDepth` / `fbSessionPenetrationDepth` | decimal | Breakout penetration depth |
| `fbOrbActivated` / `fbSessionActivated` | bool | False breakout activated |
| `isCompoundFakeout` | bool | Both ORB + session range false breakouts active |
| `driveScore` / `sweepScore` / `vwapDevScore` | decimal | Signal component scores |
| `signalStrength` | decimal | Composite signal strength (0–5) |

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

## Dashboard Chart

The dashboard chart uses **TradingView Lightweight Charts v4** loaded from CDN. It renders OHLCV candlesticks, volume histogram, volume MA(21), dynamic VWAP line, and ORB price lines.

**Data flow:**
1. **History** — `GET /api/engine/bars/{groupKey}` returns ~200 bars from the in-memory `BarRingBuffer`; if the buffer is empty (market closed), falls back to Schwab/Tradovate REST historical API with a 15-second timeout
2. **Live updates** — each `EngineSnapshot` includes `groupSnapshots[key].barTime/barOpen/barHigh/barLow/barClose/barVolume` with live price injected from `ILastPriceProvider`; the JS `_lwcUpdateCandle()` calls `series.update()` to append or update the forming candle
3. **VWAP** — stored per-bar in `BarRingBuffer` via `SetVwap()` after each confirmed bar; rendered as a white 50% opacity `LineSeries`
4. **ORB levels** — rendered as `createPriceLine()` markers (green dashed for high/low, yellow for mid); removed and recreated on instrument switch
5. **Timezone** — bars are stored as UTC; JS applies an ET offset before feeding to Lightweight Charts
