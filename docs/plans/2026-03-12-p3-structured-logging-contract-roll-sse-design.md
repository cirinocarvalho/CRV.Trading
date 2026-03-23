# P3 Design: Structured Logging, Contract Roll Calendar, SSE Dashboard

Date: 2026-03-12

---

## P3-A: Structured Logging to Seq

### Goal
Route all `ILogger<T>` output to Seq so engine decisions, trade events, and broker calls are queryable in Seq's web UI.

### Changes

**Packages (CRV.Web.csproj):**
- `Serilog.AspNetCore`
- `Serilog.Sinks.Seq`

**Program.cs:**
```csharp
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console()
    .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://localhost:5341")
    .Enrich.FromLogContext());
```

**appsettings.json:**
```json
"Seq": { "Url": "http://localhost:5341" }
```

### Scope
~30 lines in Program.cs + 2 NuGet packages + appsettings entry. All existing `ILogger<T>` calls (already structured) flow through Serilog automatically. No engine code changes needed.

### Docker (optional, for local dev)
```bash
docker run --name seq -d -e ACCEPT_EULA=Y -p 5341:80 datalust/seq
```

---

## P3-B: Futures Contract Roll Calendar

### Goal
Auto-resolve the active front-month contract symbol for CME equity index futures so the user doesn't have to manually update the ticker each quarter.

### Roll Logic
CME quarterly contracts (H=Mar, M=Jun, U=Sep, Z=Dec) expire on the 3rd Friday of the contract month. Volume migrates ~8 trading days before expiry (2nd Thursday before expiry). The calendar computes this deterministically.

### API
```csharp
public static class ContractRollCalendar
{
    /// Returns the active front-month contract, e.g. "NQM26"
    public static string ActiveContract(string rootSymbol, DateTime? asOf = null);

    /// Returns true when within 14 calendar days of the roll date
    public static bool IsNearRoll(string ticker, DateTime? asOf = null);

    /// Returns the roll date (2nd Thursday before 3rd Friday) for a contract
    public static DateTime RollDate(string ticker);
}
```

### New File
`CRV.Live/ContractRollCalendar.cs` (~60 lines)

### Integration Points
- **Settings page (Live):** When user types a root symbol (NQ, ES, MNQ, MES), auto-fill the ticker with `ContractRollCalendar.ActiveContract()`. Show warning badge if current ticker is near roll.
- **Dashboard:** Show a yellow "ROLL SOON" badge next to the ticker when `IsNearRoll()` is true.
- **FuturesSymbol.cs:** No changes — `ContractRollCalendar` is a separate static class.

### Tests
Unit tests for roll date computation across multiple quarters and edge cases (roll day itself, day before/after).

---

## P3-C: SSE for Dashboard Reads

### Goal
Stream `EngineSnapshot` to the dashboard via Server-Sent Events (SSE) instead of SignalR. SSE is simpler to debug, auto-reconnects, and doesn't require sticky sessions. SignalR remains for alerts, status changes, and write operations (force-exit).

### Architecture
```
SnapshotCachingSink
  |
  +---> SnapshotBroadcast (Channel<EngineSnapshot>, fan-out to N subscribers)
  |       |
  |       +---> SSE endpoint subscriber 1 (dashboard tab 1)
  |       +---> SSE endpoint subscriber 2 (dashboard tab 2)
  |
  +---> SignalR hub (Alert, EngineStatusChanged — unchanged)
```

### New Endpoint
`GET /api/engine/stream` — returns `text/event-stream`

```csharp
[HttpGet("stream")]
public async IAsyncEnumerable<EngineSnapshot> Stream([EnumeratorCancellation] CancellationToken ct)
{
    // Send cached last snapshot immediately on connect
    // Then yield from broadcast channel
}
```

### Broadcast Service
`SnapshotBroadcastService` (singleton) — holds a cached last snapshot and a list of subscriber channels. `SnapshotCachingSink` publishes to it; SSE endpoint subscribes.

### JS Changes (crv-hub.js)
- Add `EventSource("/api/engine/stream")` for snapshot updates (`crv:update`)
- Remove `connection.on("Update", ...)` handler
- Keep SignalR for `Alert` and `EngineStatusChanged`
- Force-exit buttons continue to POST to Razor handlers

### Fallback
`EventSource` auto-reconnects on disconnect (built-in browser behavior). Server sends the cached last snapshot on each new connection.

### Scope
- New `SnapshotBroadcastService` (~50 lines)
- New SSE action in `EngineController` (~30 lines)
- Update `SnapshotCachingSink` to publish to broadcast service (~5 lines)
- Update `crv-hub.js` (~20 lines)
- Remove `Update` SendAsync from `SignalREventSink.OnSnapshotAsync` (alerts stay)

---

## Implementation Order

| Task | Item | Effort | Dependencies |
|------|------|--------|--------------|
| 1 | Serilog + Seq wiring | 0.5h | None |
| 2 | ContractRollCalendar + tests | 1h | None |
| 3 | Settings/Dashboard roll UI | 0.5h | Task 2 |
| 4 | SnapshotBroadcastService | 0.5h | None |
| 5 | SSE endpoint | 0.5h | Task 4 |
| 6 | JS client SSE switch | 0.5h | Task 5 |
| 7 | Build + test verification | 0.25h | All |
