# Multi-Session Trading Design

## Problem

The engine currently supports one trading session (NY: 9:30 AM - 4:00 PM ET) with one ORB window. Futures trade nearly 24 hours, and Asia (7:00-11:59 PM) and London (3:00-8:00 AM) sessions offer independent trading opportunities with their own price ranges. The engine should support all three sessions with independent configuration.

## Approach: Session Wrapper (Approach C)

A new `SessionManager` sits between the orchestrator and engine. It holds 3 session configs, determines the active session by clock time, and reconfigures a single engine instance at session boundaries. The engine stays untouched internally -- it still thinks it's running one session. All multi-session logic is isolated in the wrapper.

**Why this approach over alternatives:**
- **vs. Session-Aware Engine (A):** Engine is already 48K+ tokens. Embedding session-switching logic inside it would make it harder to reason about and edit reliably.
- **vs. One Engine Per Session (B):** Three feed subscriptions, three executor instances, three SignalR sinks. Broker state management (which engine owns the position?) becomes unnecessarily complex.

## Data Model

### SessionId Enum

```csharp
public enum SessionId { Asia, London, NY }
```

### SessionConfig

New class representing one trading session:

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `SessionId` | Asia, London, or NY |
| `Enabled` | `bool` | On/off toggle for this session |
| `OrbStart` | `TimeOnly` | ORB window start (e.g., 19:00 for Asia) |
| `OrbEnd` | `TimeOnly` | ORB window end (e.g., 19:30 for Asia) |
| `RthStart` | `TimeOnly` | Session RTH start |
| `RthEnd` | `TimeOnly` | Session RTH end |
| `ExitMinutesBefore` | `int` | Force-exit N minutes before RthEnd |
| `SetupA` | `SetupConfigA` | Full per-setup config for Setup A |
| `SetupB` | `SetupConfigB` | Full per-setup config for Setup B |
| `SetupC` | `SetupConfigC` | Full per-setup config for Setup C |
| `SetupD` | `SetupConfigD` | Full per-setup config for Setup D |
| `SetupF` | `SetupConfigF` | Full per-setup config for Setup F |

**`ToLegacyConfig(StrategyConfig global)`** -- instance method on `SessionConfig`. Accepts the parent `StrategyConfig` to copy global fields (Ticker, PointValue, Broker, Timezone, etc.) and maps this session's times + setup configs into a flat `StrategyConfig` the engine can consume. Returns a new `StrategyConfig`.

### Global vs. Per-Session Fields

Fields on `StrategyConfig` fall into two categories:

**Global fields (stay on StrategyConfig, shared across all sessions):**
- Instrument: `Ticker`, `Exchange`, `PointValue`, `TickSize`
- Timeframe: `ExecutionTFMinutes`, `Timezone`, `SessionStartHour`
- Broker: `Broker`, `ExecBroker`, `AccountId`, `ExecAccountId`, `CommissionPerSide`
- Replay: `ReplayDate`, `ReplaySpeed`, `ReplayBalance`, `SaveReplayTrades`
- Filters: `AtrFilterPct`, `UseTimeFilter`, `AllowBothSameBar`
- Daily loss limit: `UseDailyLossLimit`, `MaxDailyLoss` (applied globally across all sessions -- see Edge Cases)

**Per-session fields (move into SessionConfig):**
- `OrbStart`, `OrbEnd`, `RthStart`, `RthEnd`, `ExitMinutesBefore`
- All per-setup fields (see SetupConfig below)

### SetupConfig -- Typed Per-Setup Classes

Each setup type has fields that don't generalize. Instead of one generic `SetupConfig`, use typed classes with a shared base:

**`SetupConfigBase`** (abstract, shared fields):

| Field | Type | Description |
|-------|------|-------------|
| `Enabled` | `bool` | On/off for this setup in this session |
| `Contracts` | `int` | Base contracts |
| `PartialCts` | `int` | Fixed partial contracts (0 = auto 50% floor) |
| `TargetPct` | `int` | Target as % of ORB range |
| `PartialPct` | `int` | Partial target as % of target |
| `CutoffHour` | `int` | Cutoff hour (ET) |
| `CutoffMinute` | `int` | Cutoff minute |
| `MaxTrades` | `int` | Max trades per session |
| `OrderType` | `string` | "Market" or "Limit" |
| `MinRr` | `decimal` | Minimum R:R to enter |
| `CloseAtRthClose` | `bool` | Force-close at session end |
| `UsePartial` | `bool` | Enable partial exit |
| `UseBe` | `bool` | Enable breakeven move |
| `EntryTickOffset` | `int` | Entry price tick offset |
| `MaxAdverseMinutes` | `int` | Adverse timeout (0 = disabled) |
| `HiVolMult` | `decimal` | High-vol contract multiplier |
| `MaxContracts` | `int` | Max contracts cap |
| `UseVwap` | `bool` | VWAP filter |
| `UseOrbClose` | `bool` | ORB close filter |

**`SetupConfigA : SetupConfigBase`** (Setup A specific):

| Field | Type | Description |
|-------|------|-------------|
| `Mode` | `string` | "Conservative" or "Aggressive" |
| `NearPct` | `decimal` | Near percentage (0-1) |
| `PullbackPct` | `decimal` | Pullback percentage (0-1) |
| `StopPct` | `decimal` | Stop as fraction of ORB range |

**`SetupConfigB : SetupConfigBase`** (Setup B specific):

| Field | Type | Description |
|-------|------|-------------|
| `Mode` | `string` | "Conservative" or "Aggressive" |
| `NearPct` | `decimal` | Near percentage (0-1) |
| `RetestPct` | `decimal` | Retest percentage |
| `StopPct` | `decimal` | Stop as fraction of ORB range |

**`SetupConfigC : SetupConfigBase`** (Setup C specific):

| Field | Type | Description |
|-------|------|-------------|
| `SweepMinPenetration` | `decimal` | Ticks past level |
| `SweepMinBodyReject` | `decimal` | Points rejection |
| `SweepEqualTolerance` | `decimal` | Equal-level tolerance |
| `SweepConfirmBars` | `int` | Confirmation bars |

**`SetupConfigD : SetupConfigBase`** (Setup D specific):

| Field | Type | Description |
|-------|------|-------------|
| `DriveRangeAtrMult` | `decimal` | ORB range vs ATR threshold |
| `DriveMaxPullback` | `decimal` | Max VWAP pullback fraction |
| `DriveBullBearRatio` | `int` | Bull:bear bar ratio |

**`SetupConfigF : SetupConfigBase`** (Setup F specific):

| Field | Type | Description |
|-------|------|-------------|
| `TrendDayThreshold` | `int` | Score >= this = trend day |
| `ShallowPullbackMax` | `decimal` | Max pullback fraction |
| `VwapDevPeriod` | `int` | VWAP deviation lookback |

### StrategyConfig Changes

- Keep all existing flat fields (backward compat for existing DB, serialization)
- Add `List<SessionConfig>? Sessions` property (null = legacy single-session mode)
- `ToLegacyConfig` lives on `SessionConfig`, not on `StrategyConfig`

### Persistence

`Sessions` is serialized as a JSON column on `StrategyConfig` using EF value conversion (`HasConversion` with `System.Text.Json`). This avoids new tables and foreign keys -- the session list is a single JSON blob in one column. The existing flat fields remain as separate columns for backward compat.

## SessionManager

**File:** `CRV.Core/Strategy/SessionManager.cs`

### Responsibilities

1. Holds `List<SessionConfig>` (all 3 sessions)
2. On each bar/tick timestamp, determines which session should be active (or none)
3. When active session changes -> calls `engine.Reconfigure(flatConfig)` with new session's flattened config
4. When session ends -> calls `engine.ForceExitAllAsync()` then `engine.SetIdle()`
5. When no session is active -> engine stays idle (skips processing)
6. Tracks per-session stats (wins/losses/PnL) separately for the dashboard

### Session Detection

```
GetActiveSession(TimeOnly localTime):
  for each enabled session in Sessions (ordered by RthStart):
    if localTime >= session.RthStart AND localTime < session.RthEnd:
      return session
  return null
```

Sessions are strictly sequential -- never overlap:
- Asia: 7:00 PM - 11:59 PM
- London: 3:00 AM - 8:00 AM
- NY: 9:30 AM - 4:00 PM

**Validation:** On startup, `SessionManager` validates that no two enabled sessions overlap. Sessions must not span midnight (enforced: `RthEnd > RthStart` for each session). Asia's default 19:00-23:59 is valid because it stays within one calendar day.

Gaps between sessions (engine idle):
- Asia end -> London start: 11:59 PM - 3:00 AM (3 hours)
- London end -> NY start: 8:00 AM - 9:30 AM (1.5 hours)
- NY end -> Asia start: 4:00 PM - 7:00 PM (3 hours)

### Transition Flow

```
SessionManager detects RthEnd approaching (localTime >= RthEnd - ExitMinutesBefore)
  -> SessionManager calls engine.ForceExitAllAsync()
  -> Waits for exits to complete
  -> SessionManager calls engine.SetIdle()

Gap period (no session active)
  -> Engine skips all bar/tick processing

Next session starts (time enters next session's RthStart)
  -> SessionManager calls engine.Reconfigure(nextSession.ToLegacyConfig(globalCfg))
  -> Engine resets: ORB state, trade counts, cutoffs, active flags
  -> Engine begins forming new ORB from new session's OrbStart
```

## Engine Changes

### Readonly Fields and Reconfiguration

The engine declares `_cfg`, `_orb`, and modules as `private readonly`. To support `Reconfigure()`:

1. **`_cfg`** -- remove `readonly`. `Reconfigure()` assigns a new `StrategyConfig` reference.
2. **`_orb` (OrbCalculator)** -- add `OrbCalculator.Reconfigure(TimeOnly start, TimeOnly end)` method that updates its internal `_orbStart`/`_orbEnd` fields (also remove their `readonly`). This avoids constructing a new `OrbCalculator` and losing accumulated ATR/VWAP state that persists across sessions.
3. **Modules (`_sessionEngine`, `_sweepDetector`, `_vwapModel`, `_openingDrive`, `_trendDay`)** -- these receive `ModuleConfig` at construction. Setup-specific module parameters (sweep, drive, trend day) are per-session, so modules need a `Reconfigure(ModuleConfig)` method. Add this to each module's interface. `NewSession()` already exists and resets per-session state; `Reconfigure()` additionally updates the config parameters.
4. **`_atr`, `_vwap`** -- these are indicators that accumulate across the full trading day. They do NOT reset on session transition. `_atr` and `_vwap` stay `readonly` and unchanged.

### New Methods

**`Reconfigure(StrategyConfig cfg, SessionId sessionId)`**
- Assigns `_cfg = cfg`
- Calls `_orb.Reconfigure(cfg.OrbStart, cfg.OrbEnd)` with new window times
- Calls `_orb.Reset()` to clear ORB high/low/formed state
- Updates modules: `_sweepDetector.Reconfigure(moduleConfig)`, `_openingDrive.Reconfigure(moduleConfig)`, `_trendDay.Reconfigure(moduleConfig)`
- Resets all per-setup state (active flags, trade counts, cutoff flags)
- Resets session PnL/stats (NOT daily ATR/VWAP -- those persist)
- Sets `_activeSessionId = sessionId`
- Clears `_idle = false`
- Logs "Session reconfigured: {SessionId}"

**`ForceExitAllAsync()`**
- Force-exits all active trades with `ExitReason.SessionEnd`
- Same logic as current RTH close, extracted into a callable method

**`SetIdle()`**
- Sets `_idle = true`
- When idle, `ProcessBarAsync` and `ProcessPriceTickAsync` early-return

### Two-Level Reset: Daily vs. Session

**Daily reset (at `SessionStartHour` boundary, before first session):**
- Resets ATR, VWAP indicators
- Resets `SessionEngine` module (daily levels: PDH/PDL, PWH/PWL)
- Resets daily loss limit accumulator
- Clears ORB cache
- `SessionManager` resets all 3 sessions' stats

**Session reset (on `Reconfigure()`):**
- Resets ORB state (high/low/formed)
- Resets per-setup state (arm flags, active trades, trade counts, cutoff flags)
- Resets session PnL/stats
- Updates module configs for setup-specific parameters
- Does NOT reset ATR, VWAP, daily levels, or daily loss accumulator

**Who triggers daily reset:** `SessionManager` detects the `SessionStartHour` boundary (currently 18:00) and calls `engine.ResetDaily()` -- a new method that does only the daily-scope reset. This replaces the engine's self-managed `TradingDate()` detection. The engine no longer checks `TradingDate()` internally.

### EngineSnapshot Additions

- `ActiveSessionId` (string -- "Asia", "London", "NY", or "" when idle)

### ORB Cache Interaction

The ORB cache (`orb_cache.json`) stores the last-known ORB levels for fast restart. With multiple sessions, the cache key must include `SessionId` to avoid one session's ORB overwriting another's. Format: `{Ticker}_{SessionId}` as the cache key.

### What Does NOT Change

- All entry logic (A/B/C/D/F) -- unchanged, reads `_cfg.ContractsA` etc.
- All exit logic (ProcessBar, ForcedExit) -- unchanged
- Cutoff checks -- unchanged, reads `_cfg.CutoffHourA` etc.
- ORB formation logic -- unchanged, reads `_cfg.OrbStart`/`_cfg.OrbEnd`
- Broker integration -- unchanged
- Fill price feedback -- unchanged

## Orchestrator Changes

**`LiveEngineOrchestrator`** changes:

```
StartAsync(config):
  1. Create SessionManager with config.Sessions
  2. Create single engine instance (starts idle)
  3. Start bar feed + tick subscription (always connected)
  4. On each bar/tick:
     a. SessionManager.CheckTransition(localTime)
     b. If transition -> Reconfigure, ForceExitAll + SetIdle, or ResetDaily
     c. If not idle -> forward bar/tick to engine
```

- Feed stays connected across all sessions -- no reconnect between Asia and London
- "Start Engine" button starts feed + SessionManager. If current time is within an enabled session, engine configures immediately. Otherwise waits idle.
- "Stop Engine" force-exits active trades, stops feed, tears down everything.
- Weekend handling stays in engine via `IsWeekendClosed()`.

## Settings UI

### Layout

Session tabs at the top: `[Asia] [London] [NY]`

Each tab shows:
- **Session header:** On/Off toggle, ORB Start/End, RTH Start/End, Exit Minutes Before
- **5 setup sections** (A/B/C/D/F) -- identical layout to today's setup cards, each with own On/Off toggle scoped to that session. Setup-specific fields (sweep params for C, drive params for D, trend params for F) appear only in their respective setup section.

### Migration Path

1. On load, if `Sessions` is null/empty -> auto-generate 3 sessions:
   - **NY:** populated from current flat config fields (exact current values preserved, including all per-setup fields)
   - **Asia:** disabled, default times (19:00-23:59), all setups off
   - **London:** disabled, default times (3:00-8:00), all setups off
2. Save writes to `Sessions` list going forward
3. Flat fields remain on `StrategyConfig` for single-session backtest compat

## Dashboard Changes

### Main Dashboard (Active Session Prominent)

- **Active session badge** at top: "London Session (3:00 AM - 8:00 AM)" or "Idle (next: NY at 9:30 AM)"
- **Stats cards** show active session's PnL, trades, wins, losses
- **Summary row** for other sessions: `Asia: 2W 1L +$450 | London: running | NY: 9:30 AM`
- **Trades table, chart, alerts** scoped to active session
- ORB levels on chart reflect active session's ORB

### Session Detail Page (Tabbed Review)

- New page or section: `Dashboard/Sessions`
- Tabs: `[Asia] [London] [NY]`
- Each tab: full stats, trade history, per-setup counters `[N/Max]`
- Accessible after session ends -- data persists in SessionManager until daily reset

### SignalR & DB

- `EngineSnapshot` gets `ActiveSessionId` field
- `TradeRecord` gets `SessionId` column (string: "Asia", "London", "NY")
- `GetTodayAsync()` can filter by session
- Dashboard groups "today's trades" by session

## Backtest Support

### Session Selector

Dropdown on backtest page: `Session: [All | Asia | London | NY]`

- **"All"** -- runs all 3 enabled sessions sequentially through the day's data. Each session forms its own ORB, trades independently, resets between sessions. Results show combined + per-session breakdown.
- **Single session** -- runs only that session's time window and config.

### Implementation

- Backtest engine gets the same `SessionManager` wrapper as live
- Feed replays full day of bars; `SessionManager` activates/deactivates engine at each session's boundaries
- `BacktestResult` / trade records include `SessionId`
- Results table gets a session column and per-session summary stats
- When "All" selected, summary shows totals + per-session subtotals

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| **Time gaps between sessions** | Engine idle, feed connected, bars/ticks ignored |
| **All sessions disabled** | Engine starts but stays idle. No error. |
| **Config changed mid-run** | Not supported. Requires engine restart (save + restart). |
| **DST / timezone** | All times in ET. DST shifts apply uniformly. |
| **ORB not formed** (no bars in window) | Engine skips entries for that session. No carry-over. |
| **Trade active at session end** | `SessionManager` calls `ForceExitAllAsync()` before `SetIdle()`. Explicit sequencing prevents missed exits. |
| **Daily reset** | At `SessionStartHour` (18:00). `SessionManager` calls `engine.ResetDaily()`, then resets all session stats. Happens before Asia starts (19:00). |
| **Sessions must not span midnight** | Enforced by validation: `RthEnd > RthStart` required. Asia 19:00-23:59 is valid. |
| **Daily loss limit** | Applied globally across all sessions. If Asia loses $400 and limit is $500, London has $100 remaining. Resets at daily boundary. |
| **ORB cache with multiple sessions** | Cache key includes SessionId: `{Ticker}_{SessionId}`. Each session's ORB cached independently. |

## Existing Module Interaction

`SessionEngine` (the level-tracking module) stays as a separate concern. It tracks Asia/London/NY highs and lows across the full day regardless of which trading session is active. The new multi-session system controls *when the engine trades*, not the level-tracking module.

The ATR and VWAP indicators persist across sessions within a day -- they are daily-scope indicators, not session-scope. Only session-specific state (ORB, trade counts, arm flags) resets on session transition.

## Files Modified

| File | Change |
|------|--------|
| `CRV.Core/Models/SessionConfig.cs` | New: `SessionId`, `SessionConfig`, `SetupConfigBase`, `SetupConfigA/B/C/D/F` |
| `CRV.Core/Models/StrategyConfig.cs` | Add `Sessions` list |
| `CRV.Core/Models/Signals.cs` | `EngineSnapshot.ActiveSessionId` |
| `CRV.Core/Strategy/SessionManager.cs` | New: session detection, transition, daily reset, stats |
| `CRV.Core/Strategy/OrbStrategyEngine.cs` | `Reconfigure()`, `SetIdle()`, `ForceExitAllAsync()`, `ResetDaily()`, remove `readonly` on `_cfg`, remove `TradingDate()` self-detection |
| `CRV.Core/Indicators/Indicators.cs` | `OrbCalculator.Reconfigure(TimeOnly, TimeOnly)`, remove `readonly` on `_orbStart`/`_orbEnd` |
| `CRV.Core/Modules/SweepDetector.cs` | Add `Reconfigure(ModuleConfig)` |
| `CRV.Core/Modules/OpeningDriveDetector.cs` | Add `Reconfigure(ModuleConfig)` |
| `CRV.Core/Modules/TrendDayFilter.cs` | Add `Reconfigure(ModuleConfig)` |
| `CRV.Core/Data/TradeRecord.cs` | Add `SessionId` column |
| `CRV.Core/Data/TradingDbContext.cs` | EF migration for `SessionId` column + JSON column for `Sessions` |
| `CRV.Live/Services/LiveEngineOrchestrator.cs` | Wire SessionManager into bar/tick loop |
| `CRV.Web/Pages/Settings/Live.cshtml(.cs)` | Session tabs, per-session setup config |
| `CRV.Web/Pages/Dashboard/Index.cshtml` | Active session badge, summary row, session scoping |
| `CRV.Web/Pages/Dashboard/Sessions.cshtml(.cs)` | New: tabbed session detail page |
| `CRV.Backtest/Engine/BacktestEngine.cs` | SessionManager wrapper, session selector |
| `CRV.Web/Pages/Backtest/Index.cshtml` | Session dropdown, per-session results |
| `CRV.Web/Services/TradeRepository.cs` | Filter by SessionId |
