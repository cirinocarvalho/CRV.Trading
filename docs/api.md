# API & SignalR

## REST API

Endpoints are rate-limited to 5 req/s via the `engine-api` policy and documented at `/swagger` in Development.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/engine/start` | POST | Start the engine |
| `/api/engine/stop` | POST | Stop the engine |
| `/api/engine/status` | GET | Current engine state + last snapshot |
| `/api/engine/stream` | GET | SSE stream of engine snapshots (heartbeat every ~30s) |
| `/api/engine/price/{ticker}` | GET | Last known price for a ticker (tries canonical then broker-formatted) |
| `/api/engine/force-orb` | POST | Force-set ORB from historical broker bars |
| `/api/engine/bars/{groupKey}` | GET | Bar history for a ticker group (Lightweight Charts); returns `[{time, open, high, low, close, volume, vwap}]`; falls back to Schwab/Tradovate REST when in-memory buffer is empty |
| `/api/engine/trades/today` | GET | Today's completed trades for the dashboard table |
| `/api/engine/webhook/order` | POST | External order entry (TradingView alerts, scripts, any HTTP client) |

## Webhook — External Order Entry

**URL:** `POST /api/engine/webhook/order`

**Base URL:**
- Local: `https://localhost:5001/api/engine/webhook/order`
- Production: `https://<your-app>.azurewebsites.net/api/engine/webhook/order`

**Auth:** None. This path is auth-excluded in production (Entra Easy Auth) so TradingView and other external services can POST alerts. Protect with IP allow-listing or a reverse-proxy secret if exposed publicly.

**Content-Type:** `application/json`. TradingView alerts send JSON with `Content-Type: text/plain` by default — the server rewrites those to `application/json` automatically (`CRV.Web/Program.cs:249-265`), so no TradingView-side change is needed.

**Rate limit:** 5 req/s (shared `engine-api` policy).

### Request body

Defined by `WebhookOrderRequest` in `CRV.Web/Api/EngineController.cs:716`.

| Field | Type | Required | Default | Notes |
|-------|------|----------|---------|-------|
| `direction` | string | yes | — | `"long"`, `"short"`, `"buy"`, or `"sell"` (case-insensitive) |
| `entry` | decimal | yes | — | Entry price, must be > 0 |
| `stop` | decimal | yes | — | Stop loss price, must be > 0 |
| `qty` | int | yes | — | Total contracts, must be > 0 |
| `tgt1` | decimal | no | 0 | Target 1 (partial) price. Ignored if `brackets` supplied |
| `tgt2` | decimal | no | 0 | Target 2 (final) price. Ignored if `brackets` supplied |
| `partialQty` | int | no | 0 | Contracts to exit at Tgt1. `0` = auto (`qty/2`) |
| `withPartial` | bool | no | `true` | Enable partial exit at Tgt1 |
| `brackets` | array | no | null | N-bracket spec (up to 4 legs). Overrides legacy `tgt1`/`tgt2`/`partialQty`. Sum of `qty` must equal top-level `qty` |
| `brackets[].target` | decimal | yes† | — | Target price for this leg (> 0) |
| `brackets[].qty` | int | yes† | — | Contracts for this leg |
| `brackets[].moveBe` | bool | no | `false` | Move stop to breakeven after this leg fills (non-terminal legs only) |
| `moveBe` | bool | no | `true` | Legacy: move stop to BE after first partial fill |
| `autoTrail` | bool | no | `false` | Enable auto-trail stop |
| `trailStopLoss` | decimal | no | 0 | Trail distance in points (required if `autoTrail=true`) |
| `trailTrigger` | decimal | no | 0 | Trail activation trigger in points from entry (0 = immediate) |
| `trailFreq` | decimal | no | 0 | Trail ratchet frequency in points (0 = use `trailStopLoss`) |
| `ticker` | string | no | global config | Broker ticker, e.g. `"/MNQM26"`, `"NQM26"`, `"MCLM6"` |
| `pointValue` | decimal | no | global config | $ per point override |
| `tickSize` | decimal | no | global config | Tick size override |
| `orderType` | string | no | `"Market"` | `"Market"` or `"Limit"` |
| `label` | string | no | `webhook-HHmmss` | Shown in the dashboard |
| `allowTightStop` | bool | no | `false` | Bypass the market-order stop-distance guardrail |

† Required only when `brackets` is supplied.

**Market-order stop guardrail:** for `orderType="Market"`, the server rejects orders whose stop is within `max(3 ticks, 0.1% × price)` of the current market price (would stop out on the same tick the entry fills). Pass `"allowTightStop": true` to override, or use a Limit order.

### Sample payloads

**1. Minimal long with Tgt1/Tgt2 partial**

```json
{
  "direction": "long",
  "entry": 20000,
  "stop": 19950,
  "qty": 2,
  "tgt1": 20050,
  "tgt2": 20100,
  "orderType": "Market",
  "label": "TradingView-ORB-Long"
}
```

**2. Full payload with every field populated**

```json
{
  "direction": "long",
  "entry": 20000.25,
  "stop": 19950.00,
  "qty": 4,
  "tgt1": 20050.00,
  "tgt2": 20120.00,
  "partialQty": 2,
  "withPartial": true,
  "moveBe": true,
  "autoTrail": true,
  "trailStopLoss": 25,
  "trailTrigger": 50,
  "trailFreq": 10,
  "ticker": "/MNQM26",
  "pointValue": 20,
  "tickSize": 0.25,
  "orderType": "Market",
  "label": "ORB-Breakout-Long",
  "allowTightStop": false
}
```

**3. N-bracket (3 legs) short via explicit `brackets`**

```json
{
  "direction": "short",
  "entry": 4500.00,
  "stop": 4510.00,
  "qty": 3,
  "brackets": [
    { "target": 4490.00, "qty": 1, "moveBe": false },
    { "target": 4475.00, "qty": 1, "moveBe": true  },
    { "target": 4450.00, "qty": 1, "moveBe": false }
  ],
  "orderType": "Limit",
  "label": "Retest-Short"
}
```

**4. TradingView alert message body**

Paste into the alert's *Message* field; `{{strategy.order.action}}` resolves to `buy`/`sell` and the `{{strategy.*}}` placeholders resolve per bar:

```json
{
  "direction": "{{strategy.order.action}}",
  "entry": {{strategy.order.price}},
  "stop": {{plot("Stop")}},
  "tgt1": {{plot("Tgt1")}},
  "tgt2": {{plot("Tgt2")}},
  "qty": {{strategy.order.contracts}},
  "ticker": "{{ticker}}",
  "label": "TV-{{strategy.order.id}}"
}
```

### Success response (`200 OK`)

```json
{
  "status": "ok",
  "message": "Manual entry placed",
  "groupOrderId": "g_20260424_093045_001",
  "brokerStrategyId": null,
  "ticker": "NQM6",
  "broker": "Tradovate",
  "orderType": "Market",
  "direction": "Long",
  "entry": 20000.25,
  "stop": 19950.00,
  "tgt1": 20050.00,
  "tgt2": 20120.00,
  "brackets": null,
  "qty": 4,
  "usePartial": true,
  "moveBe": true,
  "autoTrail": true,
  "label": "ORB-Breakout-Long"
}
```

The echoed `ticker` is re-formatted for the executing broker (Tradovate: `NQM6`, Schwab: `/NQM26`, TradeStation: `NQM26`).

### Error response (`400 Bad Request`)

```json
{ "status": "error", "message": "Direction must be 'long', 'short', 'buy', or 'sell'." }
```

Common error messages: missing/invalid `direction`, `entry <= 0`, `stop <= 0`, `qty <= 0`, `> 4 brackets`, bracket quantities not summing to `qty`, and the market-order stop guardrail.

### Test with curl

```bash
curl -X POST https://localhost:5001/api/engine/webhook/order \
  -H "Content-Type: application/json" \
  -d '{"direction":"long","entry":20000,"stop":19950,"qty":2,"tgt1":20050,"tgt2":20100}'
```

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
