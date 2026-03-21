# Lightweight Charts Integration — Design Spec

## Goal
Replace the TradingView embed widget on the dashboard with TradingView Lightweight Charts (v4), fed from the engine's own bar data via Tradovate. This gives full CME futures charting with no licensing restrictions.

## Decisions
- **Timeframe**: Same as execution TF (bars the engine already processes)
- **History depth**: ~200 bars (ring buffer) — roughly one full session plus prior context
- **Overlays**: ORB high/low/mid + VWAP as price lines
- **Trade markers**: Entry/exit arrows on the candles where trades fire
- **Data flow**: REST endpoint for history on load, SignalR snapshot for live candle updates

## Architecture

### 1. Bar Ring Buffer (`CRV.Core/Strategy/BarRingBuffer.cs`) — NEW
- Generic fixed-capacity ring buffer, capacity = 200
- API: `Add(Bar)`, `ToList()` (oldest-to-newest), `Clear()`, `Count`
- ~40 lines, no dependencies

### 2. TickerGroup Changes (`CRV.Core/Strategy/TickerGroup.cs`)
- Add `private readonly BarRingBuffer _barBuffer = new(200);`
- In `ProcessBar()` (or wherever bars are consumed): `_barBuffer.Add(bar)`
- New method `GetBarHistory()` → returns `_barBuffer.ToList()`
- In `BuildGroupSnapshot()`: populate 5 new bar fields from the buffer's most recent entry

### 3. TickerGroupSnapshot Changes (`CRV.Core/Models/Signals.cs`)
Add to `TickerGroupSnapshot`:
```csharp
public DateTime BarTime  { get; set; }
public decimal  BarOpen  { get; set; }
public decimal  BarHigh  { get; set; }
public decimal  BarLow   { get; set; }
public decimal  BarClose { get; set; }
```

### 4. ComposableEngine Passthrough (`CRV.Core/Strategy/ComposableEngine.cs`)
- New method: `GetBarHistory(string groupKey)` → delegates to `_groups[groupKey].GetBarHistory()`

### 5. LiveEngineOrchestrator Passthrough (`CRV.Web/Services/LiveEngineOrchestrator.cs`)
- New method: `GetBarHistory(string groupKey)` → delegates to engine

### 6. REST Endpoint (`CRV.Web/Api/EngineController.cs`)
```
GET /api/engine/bars/{groupKey}
```
Returns JSON array:
```json
[{ "time": 1710900000, "open": 21050.25, "high": 21065.00, "low": 21048.50, "close": 21060.75 }, ...]
```
- `time` = UTC unix timestamp in seconds (Lightweight Charts requirement)
- No volume (Lightweight Charts candlestick series doesn't require it)

### 7. Dashboard JS (`CRV.Web/Pages/Dashboard/Index.cshtml`)

**CDN**: `https://unpkg.com/lightweight-charts@4/dist/lightweight-charts.standalone.production.js`

**Chart setup** (once on page load):
- `createChart()` in `tv-chart-container` with dark theme colors matching dashboard
- Create one `CandlestickSeries`
- Attach `ResizeObserver` for responsive sizing

**Group pill click** (`_selectGroup`):
1. `fetch("/api/engine/bars/" + groupKey)` → seed data
2. `series.setData(bars)` — replaces candle data
3. Add/update price lines: ORB high (green dashed), ORB low (red dashed), ORB mid (yellow dotted), VWAP (blue solid)
4. Clear trade markers from previous group
5. `chart.timeScale().fitContent()` — auto-fit

**Live candle update** (in `crv:update` handler):
1. Read `barTime/Open/High/Low/Close` from `groupSnapshots[selectedGroup]`
2. If `barTime` changed → new candle being formed
3. `series.update({ time, open, high, low, close })` — Lightweight Charts handles append vs update automatically
4. Update VWAP price line value (moves intraday); ORB lines are static after formation

**Trade markers** (in `crv:alert` handler):
- Entry long → `arrowUp` below bar, green
- Entry short → `arrowDown` above bar, red
- Exit → `circle` above/below bar, white
- Markers accumulate for session, stored in JS array
- Cleared on group switch

**Chart theme** (match dashboard dark theme):
```javascript
{
    layout: { background: { color: '#1a1a2e' }, textColor: '#a0a0b0' },
    grid: { vertLines: { color: '#2a2a3e' }, horzLines: { color: '#2a2a3e' } },
    crosshair: { mode: 0 },
    timeScale: { timeVisible: true, secondsVisible: false, borderColor: '#2a2a3e' },
    rightPriceScale: { borderColor: '#2a2a3e' }
}
```

Candlestick colors:
```javascript
{ upColor: '#22c55e', downColor: '#ef4444', borderUpColor: '#22c55e', borderDownColor: '#ef4444', wickUpColor: '#22c55e', wickDownColor: '#ef4444' }
```

## File Change Summary

| Layer | File | Change |
|-------|------|--------|
| **New** | `CRV.Core/Strategy/BarRingBuffer.cs` | Ring buffer class (~40 lines) |
| **Model** | `CRV.Core/Models/Signals.cs` | Add 5 bar fields to `TickerGroupSnapshot` |
| **Engine** | `CRV.Core/Strategy/TickerGroup.cs` | Add `_barBuffer`, feed bars, expose history, populate snapshot |
| **Engine** | `CRV.Core/Strategy/ComposableEngine.cs` | Add `GetBarHistory(groupKey)` passthrough |
| **Orchestrator** | `CRV.Web/Services/LiveEngineOrchestrator.cs` | Add `GetBarHistory(groupKey)` passthrough |
| **API** | `CRV.Web/Api/EngineController.cs` | Add `GET /api/engine/bars/{groupKey}` |
| **Dashboard** | `CRV.Web/Pages/Dashboard/Index.cshtml` | Replace embed widget with Lightweight Charts |

## Implementation Notes (from spec review)

1. **Unconfirmed bars for live candle**: `TickerGroup.ProcessBarAsync()` early-returns for unconfirmed bars. The buffer stores confirmed bars only (for history), but track a separate `_currentBar` field updated on every bar (confirmed or not) — use it for the snapshot's BarTime/OHLC fields so the dashboard sees the forming candle, not just the last closed bar.

2. **Thread safety on `GetBarHistory()`**: The REST endpoint calls `GetBarHistory()` from an ASP.NET thread while the engine writes bars on its own thread. Use a lock inside `BarRingBuffer` or snapshot the buffer under the existing `_semaphore`. Internal lock is simplest.

3. **Clear buffer on session reset**: Call `_barBuffer.Clear()` in `TickerGroup.Reset()` so the chart starts fresh each session without stale bars.

4. **Guard for stopped engine**: REST endpoint returns empty `[]` when engine is not running (match existing `Status()` pattern).

5. **LWC v4 API**: Use `chart.addSeries(LightweightCharts.CandlestickSeries, options)` — the v3 `addCandlestickSeries()` was removed in v4.

6. **Trade marker time snapping**: Marker `time` must be floored to the bar boundary to match a candle timestamp, otherwise markers won't render.

7. **DateTime-to-Unix**: Use `new DateTimeOffset(bar.Time, TimeSpan.Zero).ToUnixTimeSeconds()` to avoid timezone bugs.

## What We're NOT Changing
- SignalR hub / `crv-hub.js` — bar fields ride on existing snapshot
- Bar aggregation logic — buffer just observes
- Any existing indicators or modules
- No NuGet packages — CDN only
