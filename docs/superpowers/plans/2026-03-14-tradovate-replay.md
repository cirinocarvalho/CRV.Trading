# Tradovate Replay Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add "Tradovate Replay" as a broker option that connects the live engine to Tradovate's Market Replay service for strategy testing and manual practice trading against historical market data.

**Architecture:** Reuse existing `TradovateBarFeed` and `TradovateExecutor` pointed at `wss://replay.tradovateapi.com/v1/websocket` via a separate `TradovateAuthService` instance. Add a post-auth delegate to `TradovateBarFeed` for sending `replay/initializeclock`. UI fields for replay config appear conditionally on Live Settings when "Tradovate Replay" is selected.

**Tech Stack:** C# / ASP.NET Core Razor Pages / WebSocket / SignalR

**Spec:** `docs/superpowers/specs/2026-03-14-tradovate-replay-design.md`

---

## Chunk 1: Core Config & Symbol Mapping

### Task 1: Add replay config fields to StrategyConfig

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs`

- [ ] **Step 1: Add replay properties**

Add after the existing `ExecAccountId` property (around line 50):

```csharp
// ── Replay ───────────────────────────────────────────────
public DateTime? ReplayDate        { get; set; }
public int       ReplaySpeed       { get; set; } = 100;
public int       ReplayBalance     { get; set; } = 50000;
public bool      SaveReplayTrades  { get; set; } = false;
```

- [ ] **Step 2: Add "TradovateReplay" to valid brokers**

In `Validate()`, change:
```csharp
var validBrokers = new[] { "Schwab", "TradeStation", "Tradovate", "Mock" };
```
to:
```csharp
var validBrokers = new[] { "Schwab", "TradeStation", "Tradovate", "TradovateReplay", "Mock" };
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build CRV.Core/CRV.Core.csproj --no-restore -v q`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Models/StrategyConfig.cs
git commit -m "feat: add Tradovate Replay config fields to StrategyConfig"
```

---

### Task 2: Add TradovateReplay to FuturesSymbol

**Files:**
- Modify: `CRV.Live/FuturesSymbol.cs`

- [ ] **Step 1: Add TradovateReplay case to DefaultCommission**

Change the switch in `DefaultCommission` (line 82-88):
```csharp
return broker switch
{
    "TradeStation"    => micro ? 0.75m : 2.00m,
    "Tradovate" or "TradovateReplay" => micro ? 0.80m : 2.20m,
    "Mock"            => 0m,
    _                 => 2.25m,
};
```

- [ ] **Step 2: Add TradovateReplay case to ContinuousSymbol**

Change the switch in `ContinuousSymbol` (line 110-116):
```csharp
return broker switch
{
    "Schwab"       => "/" + root,
    "TradeStation" => "@" + root,
    "Tradovate" or "TradovateReplay" => root,
    _              => root
};
```

- [ ] **Step 3: Add TradovateReplay case to ForBroker**

Change the switch in `ForBroker` (line 124-130):
```csharp
=> broker switch
{
    "Schwab"       => ToSchwab(ticker),
    "TradeStation" => ToTradeStation(ticker),
    "Tradovate" or "TradovateReplay" => ToTradovate(ticker),
    _              => Normalize(ticker)
};
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build CRV.Live/CRV.Live.csproj --no-restore -v q`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add CRV.Live/FuturesSymbol.cs
git commit -m "feat: add TradovateReplay cases to FuturesSymbol"
```

---

## Chunk 2: TradovateBarFeed Post-Auth Hook

### Task 3: Add PostAuthAction and ChartStartOverride to TradovateBarFeed

**Files:**
- Modify: `CRV.Live/Brokers/Tradovate/TradovateBarFeed.cs`

- [ ] **Step 1: Add properties**

Add two public properties to the `TradovateBarFeed` class, after the constructor:

```csharp
/// <summary>
/// Optional delegate called after WSS auth succeeds and before chart subscription.
/// Used by replay to send replay/initializeclock.
/// </summary>
public Func<ClientWebSocket, CancellationToken, Task>? PostAuthAction { get; set; }

/// <summary>
/// When set, overrides DateTime.UtcNow for computing the chart subscription's
/// asFarAsTimestamp. Used by replay to anchor history to the replay date.
/// </summary>
public DateTime? ChartStartOverride { get; set; }
```

- [ ] **Step 2: Call PostAuthAction after auth**

In `ConnectOnceAsync`, after line 105 (`_log.LogDebug("Tradovate MD auth response: {Resp}", authResp);`), add:

```csharp
// ── Post-auth hook (replay clock init) ──────────────────
if (PostAuthAction != null)
    await PostAuthAction(ws, ct);
```

- [ ] **Step 3: Use ChartStartOverride for asFarAsTimestamp**

In `ConnectOnceAsync`, change the `nowEt` computation (line 112):

From:
```csharp
var nowEt    = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etTz);
```
To:
```csharp
var refTimeUtc = ChartStartOverride ?? DateTime.UtcNow;
var nowEt      = TimeZoneInfo.ConvertTimeFromUtc(refTimeUtc, etTz);
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build CRV.Live/CRV.Live.csproj --no-restore -v q`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add CRV.Live/Brokers/Tradovate/TradovateBarFeed.cs
git commit -m "feat: add PostAuthAction and ChartStartOverride to TradovateBarFeed"
```

---

## Chunk 3: ReplayFilterSink & Orchestrator

### Task 4: Add ReplayFilterSink

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

- [ ] **Step 1: Add ReplayFilterSink class**

Add at the bottom of the file, after `SourceOverrideSink`:

```csharp
/// <summary>
/// Wraps IStrategyEventSink to suppress trade persistence while keeping
/// dashboard snapshots and engine signals flowing. Used when SaveReplayTrades=false.
/// </summary>
internal class ReplayFilterSink : IStrategyEventSink
{
    private readonly IStrategyEventSink _inner;
    private readonly ILogger _log;

    public ReplayFilterSink(IStrategyEventSink inner, ILogger log)
    {
        _inner = inner;
        _log   = log;
    }

    public Task OnEntryAsync(EntrySignal s)          => _inner.OnEntryAsync(s);
    public Task OnPartialAsync(PartialSignal s)      => _inner.OnPartialAsync(s);
    public Task OnBEMoveAsync(BESignal s)            => _inner.OnBEMoveAsync(s);
    public Task OnSnapshotAsync(EngineSnapshot snap) => _inner.OnSnapshotAsync(snap);

    public Task OnExitAsync(ExitSignal s, TradeRecord t)
    {
        _log.LogInformation("[REPLAY] Trade completed but NOT persisted: {Setup} {Dir} {Entry}→{Exit} PnL={Pnl:F2}",
            t.Setup, t.Direction, t.Entry, t.Exit, t.NetPnl);
        return Task.CompletedTask; // skip persistence, dashboard snapshot already sent
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build CRV.Web/CRV.Web.csproj --no-restore -v q`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add CRV.Web/Services/LiveEngineOrchestrator.cs
git commit -m "feat: add ReplayFilterSink for non-persisted replay trades"
```

---

### Task 5: Wire TradovateReplay into LiveEngineOrchestrator

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

- [ ] **Step 1: Handle TradovateReplay in AccountId switch**

In the `cfg.Broker switch` for AccountId (around line 136), add the replay case:

```csharp
"Tradovate" or "TradovateReplay" => _config["Tradovate:AccountId"] ?? cfg.AccountId,
```

Do the same for the `execBroker switch` (around line 146):
```csharp
"Tradovate" or "TradovateReplay" => _config["Tradovate:AccountId"] ?? cfg.AccountId,
```

- [ ] **Step 2: Handle TradovateReplay in executor switch**

In the `execBroker switch` for executor creation (around line 183), add a new case before the Tradovate case. The replay executor needs a separate auth service with the replay API URL:

```csharp
"TradovateReplay" => new TradovateExecutor(
    CreateReplayAuthService(scope),
    execCfg,
    scope.ServiceProvider.GetRequiredService<ILogger<TradovateExecutor>>(),
    scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
    httpFactory),
"Tradovate" => new TradovateExecutor(
    ...existing code...
```

- [ ] **Step 3: Handle TradovateReplay in feed switch**

In the `cfg.Broker switch` for feed creation (around line 225), add the replay case:

```csharp
"TradovateReplay" => CreateReplayBarFeed(scope, cfg, prices),
"Tradovate" => new TradovateBarFeed(
    ...existing code...
```

- [ ] **Step 4: Handle TradovateReplay sink wrapping**

After the existing Mock sink wrapping (around line 160), add:

```csharp
if (execBroker == "TradovateReplay")
{
    activeSink = new SourceOverrideSink(activeSink, "replay");
    if (!cfg.SaveReplayTrades)
        activeSink = new ReplayFilterSink(activeSink, _log);
}
```

- [ ] **Step 5: Handle TradovateReplay warmup cutoff**

Change the warmupCutoffUtc computation (line 338):

From:
```csharp
var warmupCutoffUtc = DateTime.UtcNow.AddMinutes(-cfg.ExecutionTFMinutes);
```
To:
```csharp
var warmupCutoffUtc = (cfg.Broker == "TradovateReplay" && cfg.ReplayDate.HasValue
    ? cfg.ReplayDate.Value.ToUniversalTime()
    : DateTime.UtcNow)
    .AddMinutes(-cfg.ExecutionTFMinutes);
```

- [ ] **Step 6: Handle TradovateReplay in BackfillAsync**

In `BackfillAsync`, add the replay case alongside Tradovate (around line 455):

```csharp
else if (cfg.Broker is "Tradovate" or "TradovateReplay") return 0;
```

- [ ] **Step 7: Add helper methods**

Add these private methods to `LiveEngineOrchestrator`:

```csharp
private TradovateAuthService CreateReplayAuthService(IServiceScope scope)
{
    return new TradovateAuthService(
        _config["Tradovate:Username"] ?? "",
        _config["Tradovate:Password"] ?? "",
        int.TryParse(_config["Tradovate:Cid"], out var cid) ? cid : 0,
        _config["Tradovate:Secret"] ?? "",
        _config["Tradovate:DeviceId"] ?? "",
        _config["Tradovate:AppId"] ?? "CRVBot",
        Path.Combine(Path.GetTempPath(), "crv-tradovate-replay-tokens.json"),
        apiBaseUrl: "https://replay.tradovateapi.com/v1",
        mdWssUrl: "wss://replay.tradovateapi.com/v1/websocket",
        httpFactory: scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(),
        log: scope.ServiceProvider.GetRequiredService<ILogger<TradovateAuthService>>());
}

private IBarFeed CreateReplayBarFeed(IServiceScope scope, StrategyConfig cfg, ILastPriceProvider prices)
{
    var replayAuth = CreateReplayAuthService(scope);
    var feed = new TradovateBarFeed(replayAuth, cfg, prices,
        scope.ServiceProvider.GetRequiredService<ILogger<TradovateBarFeed>>());

    // Set up replay clock initialization
    var replayDate = cfg.ReplayDate ?? DateTime.UtcNow.AddDays(-1);
    feed.ChartStartOverride = replayDate.ToUniversalTime();
    feed.PostAuthAction = async (ws, ct) =>
    {
        var clockReq = System.Text.Json.JsonSerializer.Serialize(new
        {
            startTimestamp = replayDate.ToUniversalTime().ToString("O"),
            speed          = cfg.ReplaySpeed,
            initialBalance = cfg.ReplayBalance
        });
        var frame = $"replay/initializeclock\n2\n\n{clockReq}";
        await ws.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(frame),
            System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
        scope.ServiceProvider.GetRequiredService<ILogger<TradovateBarFeed>>()
            .LogInformation("Replay clock initialized: {Date} speed={Speed}% balance=${Bal}",
                replayDate, cfg.ReplaySpeed, cfg.ReplayBalance);
    };

    return feed;
}
```

- [ ] **Step 8: Build and verify**

Run: `dotnet build CRV.Web/CRV.Web.csproj --no-restore -v q`
Expected: 0 errors

- [ ] **Step 9: Commit**

```bash
git add CRV.Web/Services/LiveEngineOrchestrator.cs
git commit -m "feat: wire TradovateReplay into LiveEngineOrchestrator"
```

---

## Chunk 4: UI — Live Settings Page

### Task 6: Add TradovateReplay to broker dropdowns and replay config fields

**Files:**
- Modify: `CRV.Web/Pages/Settings/Live.cshtml`
- Modify: `CRV.Web/Pages/Settings/Live.cshtml.cs`

- [ ] **Step 1: Add TradovateReplay option to Data Broker dropdown**

In `Live.cshtml`, after the Tradovate option (line 167), add:

```html
<option value="TradovateReplay" selected="@(Model.Config.Broker=="TradovateReplay" ? "selected" : null)">Tradovate Replay</option>
```

- [ ] **Step 2: Add TradovateReplay option to Exec Broker dropdown**

In `Live.cshtml`, after the Tradovate option in the Exec Broker select (line 178), add:

```html
<option value="TradovateReplay" selected="@(Model.Config.ExecBroker=="TradovateReplay" ? "selected" : null)">Tradovate Replay</option>
```

- [ ] **Step 3: Add replay settings panel**

After the Exec Broker `</div>` (around line 181), add:

```html
</div>
<div class="row g-2 mt-1" id="replaySettings" style="display:none">
    <div class="col-12 col-md-3">
        <label class="form-label small">Replay Date</label>
        <input type="date" name="Config.ReplayDate" class="form-control form-control-sm"
               value="@(Model.Config.ReplayDate?.ToString("yyyy-MM-dd") ?? Model.PreviousTradingDay.ToString("yyyy-MM-dd"))" />
    </div>
    <div class="col-12 col-md-3">
        <label class="form-label small">Replay Speed</label>
        <select name="Config.ReplaySpeed" class="form-select form-select-sm">
            @foreach (var v in new[]{25,50,100,200,400})
            {
                <option value="@v" selected="@(Model.Config.ReplaySpeed==v ? "selected" : null)">@v%</option>
            }
        </select>
    </div>
    <div class="col-12 col-md-3">
        <label class="form-label small">Initial Balance</label>
        <input type="number" name="Config.ReplayBalance" class="form-control form-control-sm"
               value="@Model.Config.ReplayBalance" min="1000" step="1000" />
    </div>
    <div class="col-12 col-md-3 d-flex align-items-end">
        <div class="form-check">
            <input type="checkbox" name="Config.SaveReplayTrades" value="true"
                   class="form-check-input" id="saveReplayTrades"
                   checked="@(Model.Config.SaveReplayTrades ? "checked" : null)" />
            <label class="form-check-label small" for="saveReplayTrades">Save Replay Trades</label>
        </div>
    </div>
</div>
```

- [ ] **Step 4: Add JavaScript to show/hide replay fields**

In the `<script>` section at the bottom, add:

```javascript
// ── Show/hide replay settings based on broker selection ────
(function () {
    var brokerSel     = document.querySelector('select[name="Config.Broker"]');
    var replayPanel   = document.getElementById('replaySettings');
    if (!brokerSel || !replayPanel) return;

    function toggle() {
        replayPanel.style.display = brokerSel.value === 'TradovateReplay' ? '' : 'none';
    }
    brokerSel.addEventListener('change', toggle);
    toggle(); // initial state
})();
```

- [ ] **Step 5: Add PreviousTradingDay to page model**

In `Live.cshtml.cs`, add a computed property:

```csharp
public DateTime PreviousTradingDay
{
    get
    {
        var d = DateTime.Today.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            d = d.AddDays(-1);
        return d;
    }
}
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build CRV.Web/CRV.Web.csproj --no-restore -v q`
Expected: 0 errors

- [ ] **Step 7: Commit**

```bash
git add CRV.Web/Pages/Settings/Live.cshtml CRV.Web/Pages/Settings/Live.cshtml.cs
git commit -m "feat: add Tradovate Replay UI to Live Settings page"
```

---

## Chunk 5: Integration Test

### Task 7: Manual integration test

- [ ] **Step 1: Start the app**

Run: `dotnet run --project CRV.Web`

- [ ] **Step 2: Verify UI**

1. Navigate to `/settings/live`
2. Select "Tradovate Replay" as Data Broker
3. Verify replay settings panel appears (date, speed, balance, save trades checkbox)
4. Select any other broker — verify panel hides
5. Save settings with TradovateReplay selected — verify no errors

- [ ] **Step 3: Test engine start**

1. Set Data Broker = "Tradovate Replay", Exec Broker = "Same as Data"
2. Pick a replay date (previous trading day)
3. Set speed to 400%
4. Click "Start Engine"
5. Verify in console logs:
   - `TradovateBarFeed connected to wss://replay.tradovateapi.com/v1/websocket`
   - `Replay clock initialized: <date> speed=400% balance=$50000`
   - Historical bars start streaming
   - Dashboard updates with market data

- [ ] **Step 4: Verify trade persistence**

1. Run with "Save Replay Trades" unchecked — verify trades log to console but don't appear in trades DB
2. Run with "Save Replay Trades" checked — verify trades appear with `Source = "replay"`

- [ ] **Step 5: Commit any fixes**

```bash
git add -A
git commit -m "fix: integration test fixes for Tradovate Replay"
```
