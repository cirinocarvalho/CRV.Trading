# Tradovate Replay Integration

## Summary

Add "Tradovate Replay" as a broker option that connects the engine to Tradovate's Market Replay service. Replay uses the same WSS protocol as live Tradovate but on a dedicated host, allowing both automated strategy execution and manual practice trading against historical market data at configurable speeds.

## Tradovate Replay Protocol

- **WSS Endpoint:** `wss://replay.tradovateapi.com/v1/websocket`
- **REST Base URL:** `https://replay.tradovateapi.com/v1`
- **Auth:** Same as live — `authorize` frame with `mdAccessToken`
- **Clock init:** After auth, send frame in standard Tradovate format:
  ```
  replay/initializeclock\n2\n\n{"startTimestamp":"2026-03-13T09:30:00.000Z","speed":100,"initialBalance":50000}
  ```
  - `startTimestamp` — ISO 8601 string (UTC)
  - `speed` — integer, valid range 0–400
  - `initialBalance` — integer, dollar amount
- **Data/orders:** Same protocol as live. Market data and order placement follow identical message formats.

## Design

### Broker Option

Add `"TradovateReplay"` to the valid brokers list in `StrategyConfig.Validate()`. It appears in the Data Broker and Exec Broker dropdowns on the Live Settings page alongside existing options (Schwab, TradeStation, Tradovate, Mock).

Also add `"TradovateReplay"` handling to all switch expressions that match on broker: `FuturesSymbol.ForBroker`, `FuturesSymbol.DefaultCommission`, `FuturesSymbol.ContinuousSymbol`.

### Config Fields

Four new properties on `StrategyConfig`:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `ReplayDate` | `DateTime?` | `null` | Start date for replay session. UI pre-populates with previous trading day via page model logic. `null` means not set. |
| `ReplaySpeed` | `int` | 100 | Replay speed percentage (25, 50, 100, 200, 400) |
| `ReplayBalance` | `int` | 50000 | Initial cash balance for replay account |
| `SaveReplayTrades` | `bool` | false | Persist trades to DB when checked |

### UI — Live Settings Page

When "Tradovate Replay" is selected as the broker, four additional fields appear below the broker dropdown:

- **Replay Date** — date picker (pre-populated with previous trading day)
- **Replay Speed** — dropdown with 25%, 50%, 100%, 200%, 400%
- **Initial Balance** — numeric input
- **Save Replay Trades** — checkbox

These fields are hidden when any other broker is selected (JavaScript show/hide).

### Engine Orchestration

`LiveEngineOrchestrator` handles `"TradovateReplay"` as follows:

1. **Auth service:** Create a **separate** `TradovateAuthService` instance with:
   - `apiBaseUrl = "https://replay.tradovateapi.com/v1"`
   - `mdWssUrl = "wss://replay.tradovateapi.com/v1/websocket"`
   - Same credentials as live Tradovate (username, password, cid, secret, deviceId, appId)

   This is needed because `TradovateBarFeed` reads the WSS URL from `_auth.MdWssUrl` and `TradovateExecutor` reads the REST URL from `_auth.ApiBaseUrl`. The auth service constructor already accepts both URLs as parameters.

2. **Bar feed:** Create `TradovateBarFeed` with the replay auth service instance. The feed uses `_auth.MdWssUrl` internally, so it will connect to the replay endpoint.

3. **Clock init:** After the bar feed authenticates and before subscribing to charts, send the `replay/initializeclock` frame. This is implemented via an optional post-auth delegate on `TradovateBarFeed` (see below).

4. **Order execution:** Use `TradovateExecutor` with the same replay auth service. Orders go to the auto-created replay account.

5. **Warmup cutoff:** Use `ReplayDate` (converted to UTC) instead of `DateTime.UtcNow` for computing `warmupCutoffUtc`. In replay mode, "now" is the replay start time, not wall-clock time. Without this fix, all replay bars would be treated as warmup.

6. **Trade sink:** Wrap `IStrategyEventSink` in a `SourceOverrideSink` with `Source = "replay"`. If `SaveReplayTrades` is false, wrap in a `ReplayFilterSink` that passes through `OnSnapshotAsync` (so the dashboard works) and `OnEntryAsync`/`OnPartialAsync`/`OnBEAsync` (so the engine functions) but suppresses `OnExitAsync` persistence — the `TradeRecord` is not saved to the DB.

### TradovateBarFeed Changes

1. **Post-auth delegate:** Add an optional `Func<ClientWebSocket, CancellationToken, Task>? PostAuthAction` property. In `ConnectOnceAsync`, after the `authorize` frame succeeds and before `md/getchart`, call this delegate if set. For replay, the orchestrator sets it to send `replay/initializeclock`. For live, it's null (no-op).

2. **`asFarAsTimestamp` override:** Add an optional `DateTime? ChartStartOverride` property. When set, use this instead of `DateTime.UtcNow` for computing the chart subscription's `asFarAsTimestamp`. The orchestrator sets this to `ReplayDate` for replay sessions.

3. **Tick timestamps:** L1 quote processing currently uses `DateTime.UtcNow` for tick time. In replay mode this creates a mismatch (wall-clock vs replay-clock). For now, this is acceptable — the bar timestamps from `md/getchart` are server-sourced and correct. This is documented as a known limitation for future improvement.

### Trade Persistence

| SaveReplayTrades | Behavior |
|------------------|----------|
| true | Trades saved with `Source = "replay"`, visible in trades table. Dashboard works. |
| false | Dashboard works (snapshots broadcast via SignalR). Trades logged to console but not persisted to DB. |

### Cleanup

No special cleanup needed. Tradovate automatically discards the replay account when the session ends. Stopping the engine closes the WSS connection normally.

### Scope Exclusions

- No replay-specific dashboard UI (reuses existing dashboard)
- No replay controls (pause/resume/speed change) mid-session — speed is set at start. The post-auth delegate pattern provides a future extension point for `replay/changeclock`.
- No backtest integration — replay is live-engine only
- No separate replay history page — replay trades appear in the normal trades table when saved

## Files to Change

| File | Change |
|------|--------|
| `CRV.Core/Models/StrategyConfig.cs` | Add `ReplayDate`, `ReplaySpeed`, `ReplayBalance`, `SaveReplayTrades` fields; add `"TradovateReplay"` to valid brokers |
| `CRV.Core/Models/FuturesSymbol.cs` | Add `"TradovateReplay"` case to `ForBroker`, `DefaultCommission`, `ContinuousSymbol` (same as Tradovate) |
| `CRV.Web/Pages/Settings/Live.cshtml` | Add replay fields (conditional show/hide); add "Tradovate Replay" to broker dropdowns |
| `CRV.Web/Pages/Settings/Live.cshtml.cs` | Bind new replay config fields from form POST; pre-populate `ReplayDate` with previous trading day |
| `CRV.Live/Brokers/Tradovate/TradovateBarFeed.cs` | Add `PostAuthAction` delegate, `ChartStartOverride` property |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Handle `"TradovateReplay"` case: create replay auth service, set post-auth delegate, set chart start override, compute warmup cutoff from replay date, configure sink |
