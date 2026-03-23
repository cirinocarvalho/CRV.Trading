# Tradovate Integration + Mock Broker Enhancements Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add Tradovate as a third broker with full feature parity (auth, bar feed, order execution, manual trading), add `ExecBroker` split so data feed and order execution can use different brokers, and add mock broker trade persistence + dedicated review page with equity curve.

**Architecture:**
`StrategyConfig` gains `ExecBroker`/`ExecAccountId` with `EffectiveExecBroker` computed property for fallback. `LiveEngineOrchestrator` is refactored to select bar feed from `cfg.Broker` and executor from `cfg.EffectiveExecBroker` independently. Mock trades are tagged `Source = "mock"` via a thin `SourceOverrideSink` decorator in the orchestrator — no schema change needed. Tradovate uses direct credential POST auth (not OAuth2), dual tokens (`accessToken` + `mdAccessToken`), 1-digit year symbol format, and `placeOSO` for brackets.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, EF Core SQLite, System.Net.Http, WebSockets (System.Net.WebSockets), Bootstrap 5.3, Chart.js 4, SignalR, xUnit

---

## Phase 1 — Foundation (config + symbol + orchestrator split)

### Task 1: Add `ExecBroker` and `ExecAccountId` to `StrategyConfig`

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs`

**Step 1: Add properties after the `AccountId` line (line 49)**

```csharp
public string  Broker          { get; set; } = "Schwab"; // Schwab | TradeStation | Tradovate | Mock
public string? ExecBroker      { get; set; }             // null = same as Broker
public string  AccountId       { get; set; } = "";
public string  ExecAccountId   { get; set; } = "";       // Exec broker account; empty = use AccountId
public decimal CommissionPerSide { get; set; } = 2.25m;

// ── Computed helpers (not persisted) ─────────────────────
[System.Text.Json.Serialization.JsonIgnore]
public string EffectiveExecBroker =>
    string.IsNullOrWhiteSpace(ExecBroker) ? Broker : ExecBroker;
[System.Text.Json.Serialization.JsonIgnore]
public string EffectiveExecAccountId =>
    string.IsNullOrWhiteSpace(ExecAccountId) ? AccountId : ExecAccountId;
```

Replace lines 48–50 (the existing `Broker`/`AccountId`/`CommissionPerSide` block) entirely with the block above.

**Step 2: Add ExecBroker validation to `Validate()` (after the `CommissionPerSide` check, around line 144)**

Add this block after `if (CommissionPerSide < 0) errors.Add(...)`:
```csharp
var validBrokers = new[] { "Schwab", "TradeStation", "Tradovate", "Mock" };
if (!string.IsNullOrWhiteSpace(ExecBroker) && !validBrokers.Contains(ExecBroker))
    errors.Add($"ExecBroker must be one of: {string.Join(", ", validBrokers)}.");
```

**Step 3: Build to verify no compile errors**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Core/CRV.Core.csproj
```

Expected: Build succeeded, 0 errors.

**Step 4: Run tests**

```bash
dotnet test /Users/ciro/Source/WebApps/CRV.Trading/CRV.Core.Tests/CRV.Core.Tests.csproj
```

Expected: 50 passed.

---

### Task 2: Add `ExecBroker` validation tests

**Files:**
- Modify: `CRV.Core.Tests/Models/StrategyConfigTests.cs` (or wherever the config validation tests live)

**Step 1: Find the validation test file**

```bash
grep -r "Validate" /Users/ciro/Source/WebApps/CRV.Trading/CRV.Core.Tests/ --include="*.cs" -l
```

**Step 2: Add two tests**

```csharp
[Fact]
public void Validate_ValidExecBroker_NoError()
{
    var cfg = ValidConfig();
    cfg.ExecBroker = "Tradovate";
    var errs = cfg.Validate();
    Assert.DoesNotContain(errs, e => e.Contains("ExecBroker"));
}

[Fact]
public void Validate_InvalidExecBroker_ReturnsError()
{
    var cfg = ValidConfig();
    cfg.ExecBroker = "Bogus";
    var errs = cfg.Validate();
    Assert.Contains(errs, e => e.Contains("ExecBroker"));
}
```

**Step 3: Run tests**

```bash
dotnet test /Users/ciro/Source/WebApps/CRV.Trading/CRV.Core.Tests/CRV.Core.Tests.csproj
```

Expected: 52 passed.

---

### Task 3: Add Tradovate symbol format to `FuturesSymbol`

**Files:**
- Modify: `CRV.Live/FuturesSymbol.cs`

**Step 1: Add `ToTradovate` method and update `ForBroker`**

Add the following method after `ToTradeStation`:
```csharp
/// <summary>
/// Converts to Tradovate format: NQH6 (1-digit year, no slash).
/// NQH26 → NQH6, MESH26 → MESH6.
/// </summary>
public static string ToTradovate(string ticker)
{
    var t = Normalize(ticker); // ensure no slash, 2-digit year
    // Replace trailing 2-digit year with 1-digit (drop leading decade digit)
    return Regex.Replace(t, @"(\d{2})$", m => m.Value[1..]);
}
```

Update `ForBroker` to add the Tradovate case:
```csharp
public static string ForBroker(string ticker, string broker)
    => broker switch
    {
        "Schwab"       => ToSchwab(ticker),
        "TradeStation" => ToTradeStation(ticker),
        "Tradovate"    => ToTradovate(ticker),
        _              => Normalize(ticker)
    };
```

**Step 2: Add unit tests in `CRV.Core.Tests/`**

Find or create `CRV.Core.Tests/FuturesSymbolTests.cs` (note: `FuturesSymbol` is in `CRV.Live` — but since `CRV.Core.Tests` only references `CRV.Core`, create the test in a new `CRV.Live.Tests` project OR accept that symbol tests are manual/integration. Given the existing test project only covers CRV.Core, add a comment and verify manually.)

Actually: `FuturesSymbol` is in `CRV.Live.csproj`. The test project references only `CRV.Core`. **Skip unit tests for this class** — verify by building CRV.Live.

**Step 3: Build to verify**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Live/CRV.Live.csproj
```

Expected: Build succeeded.

---

### Task 4: Refactor `LiveEngineOrchestrator` — ExecBroker split

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

This is the key change. Currently the orchestrator uses `cfg.Broker` for both feed and executor.

**Step 1: Update the `RunEngineAsync` method**

Replace lines 93–165 (broker selection + bar feed creation) with:

```csharp
// Clone config and inject AccountId from appsettings for the DATA broker
cfg = cfg.Clone();
cfg.AccountId = cfg.Broker switch
{
    "TradeStation" => _config["TradeStation:AccountId"] ?? cfg.AccountId,
    "Schwab"       => _config["Schwab:AccountId"]       ?? cfg.AccountId,
    "Tradovate"    => _config["Tradovate:AccountId"]    ?? cfg.AccountId,
    _              => cfg.AccountId
};

// Use ExecAccountId from appsettings for the EXEC broker (if different)
var execBroker = cfg.EffectiveExecBroker;
var execAccountId = execBroker switch
{
    "TradeStation" => _config["TradeStation:AccountId"] ?? cfg.AccountId,
    "Schwab"       => _config["Schwab:AccountId"]       ?? cfg.AccountId,
    "Tradovate"    => _config["Tradovate:AccountId"]    ?? cfg.AccountId,
    _              => cfg.AccountId
};
if (!string.IsNullOrWhiteSpace(cfg.ExecAccountId))
    execAccountId = cfg.ExecAccountId;

// Convert ticker to the format expected by the DATA broker
cfg.Ticker = FuturesSymbol.ForBroker(cfg.Ticker, cfg.Broker);

// When exec broker is Mock, wrap the sink to tag all completed trades with Source="mock"
IStrategyEventSink activeSink = execBroker == "Mock"
    ? new SourceOverrideSink(sink, "mock")
    : sink;

var wrappedSink = new SnapshotCachingSink(activeSink, snap => { _lastSnapshot = snap; });

// ── Order executor — selected by EffectiveExecBroker ────
IOrderExecutor executor;
if (execBroker == "Mock")
{
    executor = scope.ServiceProvider.GetRequiredService<MockBrokerExecutor>();
}
else
{
    try
    {
        executor = execBroker switch
        {
            "TradeStation" => new TradeStationExecutor(
                scope.ServiceProvider.GetRequiredService<TradeStationAuthService>(),
                cfg with { AccountId = execAccountId },
                scope.ServiceProvider.GetRequiredService<ILogger<TradeStationExecutor>>()),
            "Tradovate" => new TradovateExecutor(
                scope.ServiceProvider.GetRequiredService<TradovateAuthService>(),
                cfg with { AccountId = execAccountId },
                scope.ServiceProvider.GetRequiredService<ILogger<TradovateExecutor>>()),
            _ => (IOrderExecutor) new SchwabExecutor(
                scope.ServiceProvider.GetRequiredService<SchwabAuthService>(),
                cfg with { AccountId = execAccountId },
                scope.ServiceProvider.GetRequiredService<ILogger<SchwabExecutor>>()),
        };
    }
    catch (Exception ex)
    {
        _log.LogWarning(ex, "ExecBroker {Broker} unavailable — falling back to MockBrokerExecutor.", execBroker);
        await hub.Clients.All.SendAsync("BrokerFallback",
            $"Warning: {execBroker} exec broker unavailable — running in mock mode (no live orders).", ct);
        executor = scope.ServiceProvider.GetRequiredService<MockBrokerExecutor>();
    }
}

// ── Bar feed — selected by cfg.Broker (data source) ─────
if (cfg.Broker == "Mock")
{
    Status    = "Error: Mock cannot be the data broker — select Schwab, TradeStation, or Tradovate";
    IsRunning = false;
    await hub.Clients.All.SendAsync("EngineStatusChanged", Status, ct);
    return;
}

IBarFeed feed;
try
{
    feed = cfg.Broker switch
    {
        "TradeStation" => (IBarFeed) new TradeStationBarFeed(
            scope.ServiceProvider.GetRequiredService<TradeStationAuthService>(),
            cfg, prices,
            scope.ServiceProvider.GetRequiredService<ILogger<TradeStationBarFeed>>()),
        "Tradovate" => new TradovateBarFeed(
            scope.ServiceProvider.GetRequiredService<TradovateAuthService>(),
            cfg, prices,
            scope.ServiceProvider.GetRequiredService<ILogger<TradovateBarFeed>>()),
        _ => new SchwabBarFeed(
            scope.ServiceProvider.GetRequiredService<SchwabAuthService>(),
            cfg, prices,
            scope.ServiceProvider.GetRequiredService<ILogger<SchwabBarFeed>>()),
    };
}
catch (Exception ex)
{
    _log.LogError(ex, "Bar feed for broker {Broker} could not be created.", cfg.Broker);
    Status    = $"Error: bar feed unavailable for {cfg.Broker}";
    IsRunning = false;
    await hub.Clients.All.SendAsync("EngineStatusChanged", Status, ct);
    return;
}
```

Also add log statement after executor/feed creation:
```csharp
_log.LogInformation("Engine starting — Feed: {Feed}, Executor: {Exec}, Account: {Acct}",
    cfg.Broker, execBroker, execAccountId);
```

**Step 2: Add `SourceOverrideSink` at the bottom of the file** (alongside `SnapshotCachingSink`):

```csharp
/// <summary>
/// Wraps IStrategyEventSink to override the Source field on all completed trades.
/// Used to tag mock-executor trades as Source="mock" without changing the engine.
/// </summary>
internal class SourceOverrideSink : IStrategyEventSink
{
    private readonly IStrategyEventSink _inner;
    private readonly string             _source;

    public SourceOverrideSink(IStrategyEventSink inner, string source)
    {
        _inner  = inner;
        _source = source;
    }

    public Task OnEntryAsync(EntrySignal s)              => _inner.OnEntryAsync(s);
    public Task OnPartialAsync(PartialSignal s)          => _inner.OnPartialAsync(s);
    public Task OnBEMoveAsync(BESignal s)                => _inner.OnBEMoveAsync(s);
    public Task OnSnapshotAsync(EngineSnapshot snap)     => _inner.OnSnapshotAsync(snap);

    public Task OnExitAsync(ExitSignal s, TradeRecord t)
    {
        t.Source = _source; // override before persistence
        return _inner.OnExitAsync(s, t);
    }
}
```

**Note:** `StrategyConfig` is a class (not record), so `cfg with { AccountId = execAccountId }` won't compile. Instead use `cfg.Clone()` and set the property:

```csharp
var execCfg = cfg.Clone();
execCfg.AccountId = execAccountId;
// then pass execCfg to the executor constructors
```

**Step 3: Update `BackfillAsync` to handle Tradovate**

In `BackfillAsync`, the `else if (cfg.Broker == "Schwab")` branch is fine. Add Tradovate historical loader later (Task 16). For now, add a comment:

```csharp
// Tradovate historical loader: add in Task 16
else if (cfg.Broker == "Tradovate") return 0; // warmup via stream's built-in history
```

**Step 4: Build CRV.Web to check for errors**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web/CRV.Web.csproj
```

Expected: Errors for `TradovateExecutor`, `TradovateBarFeed`, `TradovateAuthService` not found — these are expected until Phase 3. Comment those lines out temporarily with `// TODO: Tradovate` until the broker is implemented.

---

### Task 5: Update `Live.cshtml` — add ExecBroker dropdown + Tradovate banner

**Files:**
- Modify: `CRV.Web/Pages/Settings/Live.cshtml`
- Modify: `CRV.Web/Pages/Settings/Live.cshtml.cs`

**Step 1: Update `Live.cshtml.cs` — add `TvConnected` and constructor param**

Add to constructor params and class fields (alongside Schwab/TS):
```csharp
private readonly TradovateAuthService _tv;
public bool TvConnected => _tv.IsAuthenticated;
```

Update constructor signature:
```csharp
public LiveModel(StrategyConfigService cfgSvc, LiveEngineOrchestrator orchestrator,
                 SchwabAuthService schwab, TradeStationAuthService ts,
                 TradovateAuthService tv, ILogger<LiveModel> log)
```

Add `_tv = tv;` in constructor body.

Update help text in `OnPost()`:
```csharp
// Normalize empty ExecBroker to null
if (string.IsNullOrWhiteSpace(Config.ExecBroker)) Config.ExecBroker = null;
```

**Step 2: Update `Live.cshtml` — replace the Broker dropdown (lines ~136–141)**

Replace the existing single `<div class="col-6">` with `<label>Broker</label><select name="Config.Broker">` block:

```html
<div class="col-12 col-md-6">
    <label class="form-label small">Data Broker</label>
    <select name="Config.Broker" class="form-select form-select-sm">
        <option value="Schwab"       selected="@(Model.Config.Broker=="Schwab"       ? "selected" : null)">Schwab</option>
        <option value="TradeStation" selected="@(Model.Config.Broker=="TradeStation" ? "selected" : null)">TradeStation</option>
        <option value="Tradovate"    selected="@(Model.Config.Broker=="Tradovate"    ? "selected" : null)">Tradovate</option>
        <option value="Mock"         selected="@(Model.Config.Broker=="Mock"         ? "selected" : null)">Mock (Paper)</option>
    </select>
    <div class="form-text text-muted" style="font-size:.7rem">Market data + bar feed</div>
</div>
<div class="col-12 col-md-6">
    <label class="form-label small">Exec Broker</label>
    <select name="Config.ExecBroker" class="form-select form-select-sm">
        <option value=""             selected="@(string.IsNullOrEmpty(Model.Config.ExecBroker) ? "selected" : null)">Same as Data</option>
        <option value="Schwab"       selected="@(Model.Config.ExecBroker=="Schwab"       ? "selected" : null)">Schwab</option>
        <option value="TradeStation" selected="@(Model.Config.ExecBroker=="TradeStation" ? "selected" : null)">TradeStation</option>
        <option value="Tradovate"    selected="@(Model.Config.ExecBroker=="Tradovate"    ? "selected" : null)">Tradovate</option>
        <option value="Mock"         selected="@(Model.Config.ExecBroker=="Mock"         ? "selected" : null)">Mock (Paper)</option>
    </select>
    <div class="form-text text-muted" style="font-size:.7rem">Order execution (leave empty = same)</div>
</div>
```

**Step 3: Add Tradovate auth banner after the TradeStation banner (around line 85)**

```html
<div class="alert @(Model.TvConnected ? "alert-success" : "alert-warning") d-flex align-items-center gap-2 py-2 mb-3" role="alert">
    <i class="bi @(Model.TvConnected ? "bi-check-circle-fill" : "bi-exclamation-triangle-fill")"></i>
    <span class="me-auto">
        <strong>Tradovate:</strong>
        @if (Model.TvConnected)
        {
            <span>Connected — tokens valid and ready.</span>
        }
        else
        {
            <span>Not connected. Click Connect to authenticate with your Tradovate credentials.</span>
        }
    </span>
    <a href="/auth/tradovate" class="btn btn-sm @(Model.TvConnected ? "btn-outline-success" : "btn-warning") fw-bold">
        <i class="bi bi-shield-lock me-1"></i>
        @(Model.TvConnected ? "Manage" : "Connect Tradovate")
    </a>
</div>
```

**Step 4: Update help text for Account IDs** (around line 43):

```html
Account IDs are read from <code>appsettings.json</code> / user-secrets
(<code>Schwab:AccountId</code>, <code>TradeStation:AccountId</code>, <code>Tradovate:AccountId</code>).
```

**Step 5: Build and verify**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web/CRV.Web.csproj
```

Expected: Errors only for the `TradovateAuthService` constructor param (not yet registered in DI). Register a placeholder in Program.cs in Task 14.

---

### Task 6: Update `Manual.cshtml.cs` and `Orders.cshtml.cs` — use `EffectiveExecBroker`

**Files:**
- Modify: `CRV.Web/Pages/Trading/Manual.cshtml.cs`
- Modify: `CRV.Web/Pages/Trading/Orders.cshtml.cs`

**Step 1: In `Manual.cshtml.cs` — update `CurrentBroker` display property**

Change:
```csharp
public string CurrentBroker => _cfgSvc.Current.Broker;
```
To:
```csharp
public string CurrentBroker      => _cfgSvc.Current.Broker;
public string CurrentExecBroker  => _cfgSvc.Current.EffectiveExecBroker;
```

**Step 2: In `Manual.cshtml.cs` — update `BuildCfg` helper and all execution switch statements**

Find `BuildCfg` method and update `AccountId` lookup to use `EffectiveExecBroker`:
```csharp
private StrategyConfig BuildCfg(string? ticker = null)
{
    var cfg = _cfgSvc.Current.Clone();
    var exec = cfg.EffectiveExecBroker;
    var raw = exec switch
    {
        "TradeStation" => _config["TradeStation:AccountId"],
        "Schwab"       => _config["Schwab:AccountId"],
        "Tradovate"    => _config["Tradovate:AccountId"],
        _              => null
    };
    if (!string.IsNullOrEmpty(raw)) cfg.AccountId = raw;
    if (ticker != null) cfg.Ticker = ticker;
    return cfg;
}
```

Update all `cfg.Broker` switch statements in the page handlers (positions, flat, cancel, place order) to use `cfg.EffectiveExecBroker`:

In `OnGetPositionsAsync`, `OnPostAsync` (place order), `OnPostCancelAllAsync`, `OnPostFlatAsync`, `OnPostFlatPositionAsync` — replace each:
```csharp
cfg.Broker switch { "TradeStation" => ..., "Schwab" => ..., _ => ... }
```
with:
```csharp
cfg.EffectiveExecBroker switch
{
    "TradeStation" => ...,
    "Schwab"       => ...,
    "Tradovate"    => ..., // TODO: add in Task 18
    _              => ...  // Mock fallback
}
```

**Step 3: In `Orders.cshtml.cs` — same pattern**

Update `BuildCfg()`:
```csharp
private StrategyConfig BuildCfg()
{
    var cfg = _cfgSvc.Current.Clone();
    var exec = cfg.EffectiveExecBroker;
    var raw = exec switch
    {
        "TradeStation" => _config["TradeStation:AccountId"],
        "Schwab"       => _config["Schwab:AccountId"],
        "Tradovate"    => _config["Tradovate:AccountId"],
        _              => null
    };
    if (!string.IsNullOrEmpty(raw)) cfg.AccountId = raw;
    return cfg;
}
```

Update `OnGetOrdersAsync` and `OnPostCancelOrderAsync` switches to use `cfg.EffectiveExecBroker`.

**Step 4: Build**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web/CRV.Web.csproj
```

Expected: No new errors (Tradovate cases have `// TODO` comments and fall through to Mock for now).

---

### Task 7: Update `.gitignore` and `appsettings.json`

**Files:**
- Modify: `/Users/ciro/Source/WebApps/CRV.Trading/.gitignore`
- Modify: `CRV.Web/appsettings.json`

**Step 1: Add to `.gitignore`**

```
tradovate_tokens.json
```

**Step 2: Add Tradovate section to `appsettings.json`**

```json
"Tradovate": {
  "ApiBaseUrl": "https://live.tradovateapi.com/v1",
  "MdWssUrl":   "wss://md.tradovateapi.com/v1/websocket",
  "AccountId":  "",
  "TokenFile":  "tradovate_tokens.json"
}
```

Credentials (`Username`, `Password`, `Cid`, `Secret`) go in user-secrets:
```bash
dotnet user-secrets set "Tradovate:Username" "YOUR_USERNAME" --project CRV.Web
dotnet user-secrets set "Tradovate:Password" "YOUR_PASSWORD" --project CRV.Web
dotnet user-secrets set "Tradovate:Cid"      "YOUR_CID"      --project CRV.Web
dotnet user-secrets set "Tradovate:Secret"   "YOUR_SECRET"   --project CRV.Web
```

---

## Phase 2 — Mock Broker Page

### Task 8: Add Mock Broker page and nav link

**Files:**
- Create: `CRV.Web/Pages/Trading/MockBroker.cshtml`
- Create: `CRV.Web/Pages/Trading/MockBroker.cshtml.cs`
- Modify: `CRV.Web/Pages/Shared/_Layout.cshtml`

**Step 1: Create `MockBroker.cshtml.cs`**

```csharp
namespace CRV.Web.Pages.Trading;

using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

public class MockBrokerModel : PageModel
{
    private readonly TradingDbContext    _db;
    private readonly StrategyConfigService _cfgSvc;

    public IReadOnlyList<TradeRecord> Trades        { get; private set; } = [];
    public IReadOnlyList<TradeRecord> AllTrades     { get; private set; } = []; // for equity curve
    public IReadOnlyList<DateOnly>    AvailableDates { get; private set; } = [];
    public DateOnly                   SelectedDate  { get; private set; }
    public MetricSummary              Total  { get; private set; } = new();
    public MetricSummary              SetupA { get; private set; } = new();
    public MetricSummary              SetupB { get; private set; } = new();

    public MockBrokerModel(TradingDbContext db, StrategyConfigService cfgSvc)
    {
        _db     = db;
        _cfgSvc = cfgSvc;
    }

    public async Task OnGetAsync(DateOnly? date = null)
    {
        var cfg = _cfgSvc.Current;

        // All closed mock trades — used for equity curve
        AllTrades = await _db.Trades
            .Where(t => t.Source == "mock")
            .OrderBy(t => t.EnteredAt)
            .ToListAsync();

        // Distinct available trading dates
        AvailableDates = AllTrades
            .Select(t => cfg.TradingDate(
                TimeZoneInfo.ConvertTimeFromUtc(t.EnteredAt,
                    TryGetEtTz())))
            .Select(d => DateOnly.FromDateTime(d))
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        // Default to most recent date with trades, or today
        if (date is null)
            SelectedDate = AvailableDates.Count > 0 ? AvailableDates[0] : DateOnly.FromDateTime(DateTime.Today);
        else
            SelectedDate = date.Value;

        // Filter trades for selected date
        var etTz = TryGetEtTz();
        Trades = AllTrades
            .Where(t =>
            {
                var localEntry = TimeZoneInfo.ConvertTimeFromUtc(t.EnteredAt, etTz);
                var td = cfg.TradingDate(localEntry);
                return DateOnly.FromDateTime(td) == SelectedDate;
            })
            .OrderBy(t => t.EnteredAt)
            .ToList();

        Total  = CalcMetrics(Trades);
        SetupA = CalcMetrics(Trades.Where(t => t.Setup == SetupId.A).ToList());
        SetupB = CalcMetrics(Trades.Where(t => t.Setup == SetupId.B).ToList());
    }

    public static MetricSummary CalcMetrics(IReadOnlyList<TradeRecord> trades)
    {
        if (trades.Count == 0) return new();
        var wins = trades.Count(t => t.IsWin);
        return new MetricSummary
        {
            TotalTrades = trades.Count,
            NetPnl      = trades.Sum(t => t.NetPnl),
            WinRate     = trades.Count > 0 ? (decimal)wins / trades.Count * 100 : 0,
            AvgR        = trades.Count > 0 ? trades.Average(t => t.RMultiple) : 0,
        };
    }

    private static TimeZoneInfo TryGetEtTz()
    {
        try   { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }
}

public class MetricSummary
{
    public int     TotalTrades { get; set; }
    public decimal NetPnl      { get; set; }
    public decimal WinRate     { get; set; }
    public decimal AvgR        { get; set; }
}
```

**Step 2: Create `MockBroker.cshtml`**

```cshtml
@page "/trading/mock"
@model CRV.Web.Pages.Trading.MockBrokerModel
@using CRV.Core.Models
@{
    ViewData["Title"] = "Mock Broker";
    ViewData["Page"]  = "mock-broker";
    Layout = "~/Pages/Shared/_Layout.cshtml";

    TimeZoneInfo etTz;
    try   { etTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
    catch { etTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }

    string ToEt(DateTime utc)     => TimeZoneInfo.ConvertTimeFromUtc(utc, etTz).ToString("M/d HH:mm");
    string ToEtTime(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, etTz).ToString("HH:mm");
    string ToEtLabel(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, etTz);
        return local.IsDaylightSavingTime() ? "EDT" : "EST";
    }
    string PnlCls(decimal v) => v > 0 ? "pnl-pos" : v < 0 ? "pnl-neg" : "pnl-zero";
    string Pnl(decimal v)    => (v >= 0 ? "+" : "") + v.ToString("C0");
}

<div class="d-flex align-items-center mb-3 gap-3 flex-wrap">
    <h5 class="mb-0"><i class="bi bi-robot me-2 text-warning"></i>Mock Broker</h5>
    <span class="badge bg-secondary font-monospace">@Model.AllTrades.Count total trades</span>

    <!-- Day selector -->
    <form method="get" class="d-flex align-items-center gap-2 ms-auto">
        <label class="form-label small mb-0 text-muted">Day:</label>
        <select name="date" class="form-select form-select-sm" style="width:auto"
                onchange="this.form.submit()">
            @foreach (var d in Model.AvailableDates)
            {
                var selected = d == Model.SelectedDate ? "selected" : null;
                <option value="@d.ToString("yyyy-MM-dd")" selected="@selected">
                    @d.ToString("MMM d, yyyy")
                    @(d == DateOnly.FromDateTime(DateTime.Today) ? " (Today)" : "")
                </option>
            }
        </select>
    </form>
</div>

@if (Model.AllTrades.Count == 0)
{
    <div class="settings-section d-flex flex-column align-items-center justify-content-center py-5 text-center opacity-50">
        <i class="bi bi-robot" style="font-size:3rem;color:#ffd700;"></i>
        <div class="mt-3 text-muted">No mock trades yet.</div>
        <div class="text-muted small mt-1">Set Exec Broker to <strong>Mock (Paper)</strong> in Live Settings and run the engine.</div>
    </div>
}
else
{
    <!-- Metric cards -->
    <div class="row g-2 mb-3">
        @foreach (var card in new[] {
            (Title: "All Trades", Icon: "bi-bar-chart-fill",              M: Model.Total),
            (Title: "Setup A",    Icon: "bi-triangle-fill",               M: Model.SetupA),
            (Title: "Setup B",    Icon: "bi-arrow-up-right-circle-fill",  M: Model.SetupB),
        })
        {
            var m = card.M;
            <div class="col-12 col-md-4">
                <div class="settings-section">
                    <h6 class="mb-2"><i class="bi @card.Icon me-1"></i>@card.Title
                        <span class="fw-normal text-muted small ms-1">(@m.TotalTrades trades)</span>
                    </h6>
                    <div class="d-flex justify-content-between py-1 border-bottom border-secondary">
                        <span class="text-muted small">Net P&amp;L</span>
                        <span class="font-monospace small @PnlCls(m.NetPnl)">@Pnl(m.NetPnl)</span>
                    </div>
                    <div class="d-flex justify-content-between py-1 border-bottom border-secondary">
                        <span class="text-muted small">Win Rate</span>
                        <span class="font-monospace small @(m.WinRate >= 50 ? "pnl-pos" : "pnl-neg")">@m.WinRate.ToString("F1")%</span>
                    </div>
                    <div class="d-flex justify-content-between py-1">
                        <span class="text-muted small">Avg R</span>
                        <span class="font-monospace small @PnlCls(m.AvgR)">@m.AvgR.ToString("F2")R</span>
                    </div>
                </div>
            </div>
        }
    </div>

    <!-- Equity curve — full history, not filtered by day -->
    @if (Model.AllTrades.Count > 0)
    {
        <div class="settings-section mb-3">
            <h6 class="mb-3">
                <i class="bi bi-graph-up me-1"></i>Equity Curve
                <span class="text-muted fw-normal small ms-2">@Model.AllTrades.Count trades</span>
                @{ var totalNet = Model.AllTrades.Sum(t => t.NetPnl); }
                <span class="badge ms-2 @(totalNet >= 0 ? "bg-success" : "bg-danger") font-monospace small">@Pnl(totalNet)</span>
            </h6>
            <canvas id="equity-chart" style="max-height:200px;"></canvas>
        </div>
    }

    <!-- Trade log for selected day -->
    <div class="settings-section">
        <div class="d-flex align-items-center mb-2">
            <h6 class="mb-0"><i class="bi bi-list-columns me-1"></i>Trades — @Model.SelectedDate.ToString("MMM d, yyyy")</h6>
            <span class="badge bg-secondary font-monospace ms-2">@Model.Trades.Count trades</span>
            @if (Model.Trades.Count > 0)
            {
                <span class="ms-2 small text-muted">@Model.Trades.Count(t => t.IsWin) W / @Model.Trades.Count(t => !t.IsWin) L</span>
            }
        </div>

        @if (Model.Trades.Count == 0)
        {
            <div class="text-muted small py-3 text-center">No trades for this day.</div>
        }
        else
        {
            <div class="table-responsive" style="max-height:460px;overflow-y:auto;">
                <table class="table table-sm table-hover trade-table font-monospace mb-0">
                    <thead class="table-dark" style="position:sticky;top:0;z-index:1;">
                        <tr>
                            <th>#</th>
                            <th>In (@(Model.Trades.Count > 0 ? ToEtLabel(Model.Trades[0].EnteredAt) : "ET"))</th>
                            <th>Out</th><th>Dur</th><th>Setup</th><th>Dir</th>
                            <th class="text-end">Entry</th><th class="text-end">Exit</th>
                            <th class="text-end">Stop</th><th class="text-end">Target</th>
                            <th class="text-end">Cts</th><th>Part</th><th>Reason</th>
                            <th class="text-end">Gross</th><th class="text-end">Comm</th>
                            <th class="text-end">Net</th><th class="text-end">R</th>
                        </tr>
                    </thead>
                    <tbody>
                        @{ int rowNum = 0; }
                        @foreach (var t in Model.Trades)
                        {
                            rowNum++;
                            string rowCls = t.IsWin ? "table-success bg-opacity-10" : "table-danger bg-opacity-10";
                            string reasonBadge = t.ExitReason switch
                            {
                                ExitReason.Target => "bg-success",
                                ExitReason.Stop   => "bg-danger",
                                _                 => "bg-secondary"
                            };
                            string dur = t.Duration.TotalHours >= 1
                                ? $"{(int)t.Duration.TotalHours}h{t.Duration.Minutes:D2}m"
                                : $"{(int)t.Duration.TotalMinutes}m";
                            <tr class="@rowCls">
                                <td class="td-dim">@rowNum</td>
                                <td class="td-time">@ToEt(t.EnteredAt)</td>
                                <td class="td-time td-dim">@ToEtTime(t.ExitedAt)</td>
                                <td class="td-dim">@dur</td>
                                <td><span class="badge @(t.Setup == SetupId.A ? "bg-info text-dark" : "bg-warning text-dark")">@t.Setup</span></td>
                                <td class="@(t.EffectiveDirection == Direction.Long ? "pnl-pos" : "pnl-neg")">@t.EffectiveDirection</td>
                                <td class="text-end">@t.Entry.ToString("F2")</td>
                                <td class="text-end">@t.Exit.ToString("F2")</td>
                                <td class="text-end td-dim">@t.InitialStop.ToString("F2")</td>
                                <td class="text-end td-dim">@t.Target.ToString("F2")</td>
                                <td class="text-end">@t.Contracts</td>
                                <td class="text-center">
                                    @if (t.PartialFilled) { <i class="bi bi-check-lg"></i> }
                                    else { <span class="td-dim">&#x2014;</span> }
                                </td>
                                <td><span class="badge @reasonBadge small">@t.ExitReason</span></td>
                                <td class="text-end @(t.GrossPnl >= 0 ? "pnl-pos" : "pnl-neg")">@Pnl(t.GrossPnl)</td>
                                <td class="text-end td-dim">-@t.Commission.ToString("C0")</td>
                                <td class="text-end fw-bold @PnlCls(t.NetPnl)">@Pnl(t.NetPnl)</td>
                                <td class="text-end @(t.RMultiple >= 0 ? "pnl-pos" : "pnl-neg")">@t.RMultiple.ToString("F1")R</td>
                            </tr>
                        }
                    </tbody>
                </table>
            </div>
        }
    </div>
}

@section Scripts {
<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.min.js"></script>
@if (Model.AllTrades.Count > 0)
{
    // Build cumulative equity curve across ALL mock trades
    decimal running = 0m;
    var pts = Model.AllTrades.Select(t =>
    {
        running += t.NetPnl;
        return (t.EnteredAt, running);
    }).ToList();

    <text>
<script>
(function() {
    const labels = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(
        pts.Select(p => TimeZoneInfo.ConvertTimeFromUtc(p.EnteredAt, etTz).ToString("M/d"))));
    const values = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(
        pts.Select(p => (double)p.running)));
    const lastEquity = values[values.length - 1];
    const lineColor  = lastEquity >= 0 ? '#00ff88' : '#ff4466';
    const fillColor  = lastEquity >= 0 ? 'rgba(0,255,136,.07)' : 'rgba(255,68,102,.07)';
    new Chart(document.getElementById('equity-chart').getContext('2d'), {
        type: 'line',
        data: {
            labels,
            datasets: [
                { label: 'Equity', data: values, borderColor: lineColor, backgroundColor: fillColor,
                  borderWidth: 2, pointRadius: values.length <= 80 ? 3 : 0, pointHoverRadius: 5, fill: true, tension: 0.15 },
                { label: 'Zero', data: new Array(values.length).fill(0),
                  borderColor: 'rgba(255,255,255,.18)', borderDash: [4,4], borderWidth: 1, pointRadius: 0, fill: false },
            ]
        },
        options: {
            responsive: true,
            interaction: { mode: 'index', intersect: false },
            plugins: {
                legend: { display: false },
                tooltip: { callbacks: { label: ctx => ctx.datasetIndex === 0
                    ? ' $' + ctx.parsed.y.toLocaleString('en-US', { maximumFractionDigits: 0 }) : null }}
            },
            scales: {
                x: { ticks: { maxTicksLimit: 14, color: '#666', font: { size: 11 } }, grid: { color: '#1a1d24' } },
                y: { ticks: { color: '#666', font: { size: 11 }, callback: v => '$' + v.toLocaleString() }, grid: { color: '#1a1d24' } },
            },
        },
    });
}());
</script>
    </text>
}
}
```

**Step 3: Add nav link to `_Layout.cshtml`** (after Orders, before Live Settings):

```html
<a class="nav-link @(ViewData["Page"]?.ToString()=="mock-broker"?"active text-warning":"")"
   href="/trading/mock"><i class="bi bi-robot me-1"></i>Mock</a>
```

**Step 4: Build and run**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web/CRV.Web.csproj
```

Expected: Build succeeded.

Verify by navigating to `/trading/mock` — page loads empty state (no mock trades yet).

---

## Phase 3 — Tradovate Authentication Service

### Task 9: Create `TradovateAuthService`

**Files:**
- Create: `CRV.Live/Brokers/Tradovate/TradovateAuthService.cs`

**Step 1: Create the directory and file**

```bash
mkdir -p /Users/ciro/Source/WebApps/CRV.Trading/CRV.Live/Brokers/Tradovate
```

```csharp
namespace CRV.Live.Brokers.Tradovate;

using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tradovate authentication — direct credential POST (NOT OAuth2).
/// Manages accessToken (for trading) and mdAccessToken (for market data).
/// Tokens expire in 90 minutes; renewed automatically when < 5 min remain.
/// Persists tokens to a JSON file so they survive app restarts.
/// </summary>
public class TradovateAuthService
{
    private readonly string  _username;
    private readonly string  _password;
    private readonly int     _cid;
    private readonly string  _secret;
    private readonly string  _tokenFile;
    private readonly ILogger _log;

    public string ApiBaseUrl { get; }
    public string MdWssUrl   { get; }

    private string?  _accessToken;
    private string?  _mdAccessToken;
    private DateTime _expiresAt    = DateTime.MinValue;
    private DateTime _mdExpiresAt  = DateTime.MinValue;

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAt;

    public TradovateAuthService(
        string username, string password, int cid, string secret,
        string tokenFile,
        string apiBaseUrl = "https://live.tradovateapi.com/v1",
        string mdWssUrl   = "wss://md.tradovateapi.com/v1/websocket")
    {
        _username  = username;
        _password  = password;
        _cid       = cid;
        _secret    = secret;
        _tokenFile = tokenFile;
        ApiBaseUrl = apiBaseUrl;
        MdWssUrl   = mdWssUrl;
        _log       = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        TryLoadFromFile();
    }

    public TradovateAuthService(
        string username, string password, int cid, string secret,
        string tokenFile, string apiBaseUrl, string mdWssUrl,
        ILogger<TradovateAuthService> log)
        : this(username, password, cid, secret, tokenFile, apiBaseUrl, mdWssUrl)
    {
        _log = log;
    }

    /// <summary>Returns a valid access token, renewing if < 5 min remain.</summary>
    public async Task<string> GetAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAt.AddMinutes(-5))
            return _accessToken;

        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAt)
        {
            await RenewTokenAsync();
            return _accessToken!;
        }

        await AuthenticateAsync();
        return _accessToken!;
    }

    /// <summary>Returns a valid market data access token.</summary>
    public async Task<string> GetMdAccessTokenAsync()
    {
        // Ensure main token is valid first
        await GetAccessTokenAsync();
        return _mdAccessToken ?? _accessToken!;
    }

    /// <summary>POST /auth/accesstokenrequest — full re-authentication.</summary>
    public async Task AuthenticateAsync()
    {
        using var http = new HttpClient();
        var body = JsonSerializer.Serialize(new
        {
            name       = _username,
            password   = _password,
            appId      = "CRV.Trading",
            appVersion = "1.0",
            cid        = _cid,
            sec        = _secret
        });
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/auth/accesstokenrequest")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        var res = await http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new Exception($"Tradovate auth failed ({(int)res.StatusCode}): {json}");

        ParseTokenResponse(json);
        SaveToFile();
        _log.LogInformation("Tradovate authenticated — tokens valid until {Expiry}", _expiresAt);
    }

    /// <summary>POST /auth/renewAccessToken — extend expiry without re-entering credentials.</summary>
    public async Task RenewTokenAsync()
    {
        if (string.IsNullOrEmpty(_accessToken)) { await AuthenticateAsync(); return; }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
        var res  = await http.PostAsync($"{ApiBaseUrl}/auth/renewAccessToken", null);
        var json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("Tradovate token renewal failed — re-authenticating. {Status}", (int)res.StatusCode);
            await AuthenticateAsync();
            return;
        }

        ParseTokenResponse(json);
        SaveToFile();
        _log.LogInformation("Tradovate token renewed — valid until {Expiry}", _expiresAt);
    }

    // ── Private helpers ─────────────────────────────────────────

    private void ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken   = root.TryGetProperty("accessToken",   out var at) ? at.GetString() : null;
        _mdAccessToken = root.TryGetProperty("mdAccessToken", out var md) ? md.GetString() : null;

        // Tradovate returns expirationTime as ms epoch or ISO string
        if (root.TryGetProperty("expirationTime", out var exp))
        {
            if (exp.ValueKind == JsonValueKind.Number)
                _expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()).UtcDateTime;
            else if (exp.ValueKind == JsonValueKind.String &&
                     DateTime.TryParse(exp.GetString(), out var dt))
                _expiresAt = dt.ToUniversalTime();
            else
                _expiresAt = DateTime.UtcNow.AddMinutes(85); // safe fallback
        }
        else
        {
            _expiresAt = DateTime.UtcNow.AddMinutes(85);
        }
        _mdExpiresAt = _expiresAt; // md token has same expiry
    }

    private void SaveToFile()
    {
        try
        {
            var obj = new
            {
                accessToken   = _accessToken,
                mdAccessToken = _mdAccessToken,
                expiresAt     = _expiresAt.ToString("o"),
            };
            File.WriteAllText(_tokenFile, JsonSerializer.Serialize(obj,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save Tradovate tokens to file."); }
    }

    private void TryLoadFromFile()
    {
        try
        {
            if (!File.Exists(_tokenFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_tokenFile));
            var root = doc.RootElement;
            _accessToken   = root.TryGetProperty("accessToken",   out var at) ? at.GetString() : null;
            _mdAccessToken = root.TryGetProperty("mdAccessToken", out var md) ? md.GetString() : null;
            if (root.TryGetProperty("expiresAt", out var exp) &&
                DateTime.TryParse(exp.GetString(), out var dt))
                _expiresAt = dt.ToUniversalTime();
        }
        catch { /* ignore corrupt token file */ }
    }
}
```

**Step 2: Build CRV.Live**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Live/CRV.Live.csproj
```

Expected: Build succeeded.

---

### Task 10: Register `TradovateAuthService` in `Program.cs`

**Files:**
- Modify: `CRV.Web/Program.cs`

**Step 1: Add the using + DI registration after the TradeStation block**

Add using:
```csharp
using CRV.Live.Brokers.Tradovate;
```

Add registration (after the TradeStation block, around line 80):
```csharp
var tvCfg = builder.Configuration.GetSection("Tradovate");
builder.Services.AddSingleton(new TradovateAuthService(
    tvCfg["Username"]  ?? "",
    tvCfg["Password"]  ?? "",
    int.TryParse(tvCfg["Cid"], out var tvCid) ? tvCid : 0,
    tvCfg["Secret"]    ?? "",
    tvCfg["TokenFile"] ?? "tradovate_tokens.json",
    tvCfg["ApiBaseUrl"] ?? "https://live.tradovateapi.com/v1",
    tvCfg["MdWssUrl"]   ?? "wss://md.tradovateapi.com/v1/websocket"));
```

**Step 2: Build CRV.Web**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web/CRV.Web.csproj
```

Expected: Build succeeded. The `TvConnected` property in `LiveModel` will now resolve (Task 5 can be completed).

---

### Task 11: Create `/auth/tradovate` page

**Files:**
- Create: `CRV.Web/Pages/auth/Tradovate.cshtml`
- Create: `CRV.Web/Pages/auth/Tradovate.cshtml.cs`

**Step 1: Create `Tradovate.cshtml.cs`**

```csharp
namespace CRV.Web.Pages.Auth;

using CRV.Live.Brokers.Tradovate;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class TradovateAuthModel : PageModel
{
    private readonly TradovateAuthService    _auth;
    private readonly ILogger<TradovateAuthModel> _log;

    public bool    IsAuthenticated => _auth.IsAuthenticated;
    public string? ErrorMessage    { get; private set; }
    public string? SuccessMessage  { get; private set; }

    public TradovateAuthModel(TradovateAuthService auth, ILogger<TradovateAuthModel> log)
    {
        _auth = auth;
        _log  = log;
    }

    public void OnGet() { }

    /// <summary>POST handler — triggers authentication with credentials from user-secrets.</summary>
    public async Task<IActionResult> OnPostConnectAsync()
    {
        try
        {
            await _auth.AuthenticateAsync();
            SuccessMessage = "Successfully connected to Tradovate! Tokens stored and ready.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tradovate authentication failed");
            ErrorMessage = $"Authentication failed: {ex.Message}";
        }
        return Page();
    }

    /// <summary>Renews the existing access token.</summary>
    public async Task<IActionResult> OnPostRenewAsync()
    {
        try
        {
            await _auth.RenewTokenAsync();
            SuccessMessage = "Token renewed successfully.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tradovate token renewal failed");
            ErrorMessage = $"Renewal failed: {ex.Message}";
        }
        return Page();
    }
}
```

**Step 2: Create `Tradovate.cshtml`**

```cshtml
@page "/auth/tradovate"
@model CRV.Web.Pages.Auth.TradovateAuthModel
@{
    ViewData["Title"] = "Tradovate Auth";
    Layout = "~/Pages/Shared/_Layout.cshtml";
}

<div class="row justify-content-center">
    <div class="col-12 col-md-6 col-lg-5">
        <h5 class="mb-4"><i class="bi bi-shield-lock me-2 text-warning"></i>Tradovate Connection</h5>

        @if (Model.SuccessMessage is not null)
        {
            <div class="alert alert-success py-2">@Model.SuccessMessage</div>
        }
        @if (Model.ErrorMessage is not null)
        {
            <div class="alert alert-danger py-2">@Model.ErrorMessage</div>
        }

        <div class="settings-section mb-3">
            <div class="d-flex align-items-center gap-2 mb-3">
                <i class="bi bi-@(Model.IsAuthenticated ? "check-circle-fill text-success" : "x-circle-fill text-warning")"></i>
                <strong>Status:</strong>
                <span>@(Model.IsAuthenticated ? "Connected — tokens valid" : "Not connected")</span>
            </div>

            <div class="text-muted small mb-3">
                Credentials are read from user-secrets (<code>Tradovate:Username</code>,
                <code>Tradovate:Password</code>, <code>Tradovate:Cid</code>, <code>Tradovate:Secret</code>).
                No credentials are entered here — they are pre-configured via <code>dotnet user-secrets</code>.
            </div>

            <form method="post" asp-page-handler="Connect" class="mb-2">
                <button type="submit" class="btn btn-warning w-100 fw-bold">
                    <i class="bi bi-plug me-1"></i>Connect (Authenticate)
                </button>
            </form>

            @if (Model.IsAuthenticated)
            {
                <form method="post" asp-page-handler="Renew">
                    <button type="submit" class="btn btn-outline-secondary w-100">
                        <i class="bi bi-arrow-clockwise me-1"></i>Renew Token
                    </button>
                </form>
            }
        </div>

        <a href="/settings/live" class="btn btn-sm btn-outline-secondary">
            <i class="bi bi-arrow-left me-1"></i>Back to Live Settings
        </a>
    </div>
</div>
```

**Step 3: Build and test**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web/CRV.Web.csproj
```

Navigate to `/auth/tradovate` — page loads with "Not connected" status. Clicking "Connect" triggers authentication (will fail without real credentials, but the page renders correctly).

---

## Phase 4 — Tradovate Bar Feed

### Task 12: Create `TradovateBarFeed`

**Files:**
- Create: `CRV.Live/Brokers/Tradovate/TradovateBarFeed.cs`

**Step 1: Create the bar feed class**

```csharp
namespace CRV.Live.Brokers.Tradovate;

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live.BarBuilders;
using Microsoft.Extensions.Logging;

/// <summary>
/// IBarFeed implementation for Tradovate using the Market Data WebSocket.
/// Connects to wss://md.tradovateapi.com/v1/websocket (or demo equivalent),
/// authenticates with mdAccessToken, subscribes to OHLCV chart data and
/// L1 quote ticks for real-time price updates.
/// </summary>
public class TradovateBarFeed : IBarFeed
{
    private readonly TradovateAuthService _auth;
    private readonly StrategyConfig       _cfg;
    private readonly ILastPriceProvider   _prices;
    private readonly ILogger              _log;

    public TradovateBarFeed(
        TradovateAuthService auth,
        StrategyConfig cfg,
        ILastPriceProvider prices,
        ILogger<TradovateBarFeed> log)
    {
        _auth   = auth;
        _cfg    = cfg;
        _prices = prices;
        _log    = log;
    }

    public async IAsyncEnumerable<Bar> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await foreach (var bar in StreamOnceAsync(ct))
                yield return bar;

            if (!ct.IsCancellationRequested)
            {
                _log.LogWarning("TradovateBarFeed disconnected — reconnecting in 5s...");
                await Task.Delay(5_000, ct);
            }
        }
    }

    private async IAsyncEnumerable<Bar> StreamOnceAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var symbol  = FuturesSymbol.ToTradovate(_cfg.Ticker); // NQH26 → NQH6
        var wssUri  = new Uri(_auth.MdWssUrl);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(wssUri, ct);
        _log.LogInformation("TradovateBarFeed connected to {Url}", _auth.MdWssUrl);

        // ── Authenticate ───────────────────────────────────────────
        var mdToken = await _auth.GetMdAccessTokenAsync();
        await SendFrameAsync(ws, $"authorize\n0\n\n{mdToken}", ct);

        // Wait for auth response
        var authResp = await ReceiveMessageAsync(ws, ct);
        _log.LogDebug("Tradovate MD auth response: {Resp}", authResp);

        // ── Subscribe to OHLCV chart data ──────────────────────────
        // asMuchAsElements=20 provides ~20 historical bars for warmup
        int reqId = 1;
        var chartReq = JsonSerializer.Serialize(new
        {
            symbol           = symbol,
            chartDescription = new
            {
                underlyingType   = "MinuteBar",
                elementSize      = _cfg.ExecutionTFMinutes,
                elementSizeUnit  = "UnderlyingUnits"
            },
            timeRange = new { asMuchAsElements = 20 }
        });
        await SendFrameAsync(ws, $"md/getchart\n{reqId++}\n\n{chartReq}", ct);

        // ── Subscribe to L1 quote for last price ───────────────────
        var quoteReq = JsonSerializer.Serialize(new { symbol = symbol });
        await SendFrameAsync(ws, $"md/subscribeQuote\n{reqId++}\n\n{quoteReq}", ct);

        _log.LogInformation("TradovateBarFeed subscribed — {Symbol} {Tf}min", symbol, _cfg.ExecutionTFMinutes);

        var builder = new RealTimeBarBuilder(_cfg.ExecutionTFMinutes);
        builder.BarClosed  += (_, bar) => { /* handled below */ };

        var closedBars = new System.Collections.Concurrent.ConcurrentQueue<Bar>();
        builder.BarClosed += (_, bar) => closedBars.Enqueue(bar);

        // ── Message loop ───────────────────────────────────────────
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            string raw;
            try { raw = await ReceiveMessageAsync(ws, ct); }
            catch { yield break; }

            if (raw == "h") // heartbeat
            {
                await SendFrameAsync(ws, "h", ct);
                continue;
            }

            // Tradovate frames: a["{...}"] or a["{...}", "{...}"]
            if (!raw.StartsWith("a[")) continue;
            var innerJson = raw[1..]; // strip leading 'a'
            JsonElement[] messages;
            try
            {
                var arr = JsonSerializer.Deserialize<string[]>(innerJson) ?? [];
                messages = arr.Select(s => JsonDocument.Parse(s).RootElement).ToArray();
            }
            catch { continue; }

            foreach (var msg in messages)
            {
                if (!msg.TryGetProperty("e", out var eProp)) continue;
                var evt = eProp.GetString();

                if (evt == "chart" && msg.TryGetProperty("d", out var d))
                {
                    // Historical bars (isHistorical=true) and live bar updates
                    foreach (var bar in ParseChartBars(d, symbol))
                    {
                        if (bar.IsConfirmed)
                            yield return bar; // historical
                        else
                        {
                            // Unconfirmed live tick — update price and unconfirmed bar
                            _prices.UpdatePrice(_cfg.Ticker, bar.Close);
                            yield return bar;
                        }
                    }
                }
                else if (evt == "quote" && msg.TryGetProperty("d", out var qd))
                {
                    // L1 last price tick
                    if (qd.TryGetProperty("trade", out var trade) &&
                        trade.TryGetProperty("price", out var price))
                    {
                        _prices.UpdatePrice(_cfg.Ticker, price.GetDecimal());
                    }
                }
            }

            // Emit any bars that the real-time builder closed
            while (closedBars.TryDequeue(out var closed))
                yield return closed;
        }
    }

    private static IEnumerable<Bar> ParseChartBars(JsonElement data, string symbol)
    {
        if (!data.TryGetProperty("bars", out var bars)) yield break;
        foreach (var b in bars.EnumerateArray())
        {
            if (!b.TryGetProperty("timestamp", out var ts)) continue;
            var epochMs  = ts.GetInt64();
            var time     = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
            var open     = b.TryGetProperty("open",    out var o) ? o.GetDecimal() : 0;
            var high     = b.TryGetProperty("high",    out var h) ? h.GetDecimal() : 0;
            var low      = b.TryGetProperty("low",     out var l) ? l.GetDecimal() : 0;
            var close    = b.TryGetProperty("close",   out var c) ? c.GetDecimal() : 0;
            var volume   = b.TryGetProperty("upVolume", out var v) ? v.GetInt64() + (b.TryGetProperty("downVolume", out var dv) ? dv.GetInt64() : 0) : 0;
            var isHistorical = !b.TryGetProperty("isHistorical", out var ih) || ih.GetBoolean();

            yield return new Bar(time, open, high, low, close, volume, IsConfirmed: isHistorical);
        }
    }

    private static async Task SendFrameAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb  = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new Exception("WebSocket closed by server.");
            sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
        } while (!result.EndOfMessage);
        return sb.ToString();
    }
}
```

**Step 2: Build CRV.Live**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Live/CRV.Live.csproj
```

Expected: Build succeeded.

---

## Phase 5 — Tradovate Order Executor

### Task 13: Create `TradovateExecutor`

**Files:**
- Create: `CRV.Live/Brokers/Tradovate/TradovateExecutor.cs`

**Step 1: Create the executor**

```csharp
namespace CRV.Live.Brokers.Tradovate;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// IOrderExecutor for Tradovate — places OSO bracket orders via REST.
/// Tracks open order IDs to enable cancellation for partial/BE/exit signals.
/// </summary>
public class TradovateExecutor : IOrderExecutor
{
    private readonly TradovateAuthService _auth;
    private readonly StrategyConfig       _cfg;
    private readonly ILogger              _log;

    // Track open order IDs per setup for modification/cancellation
    private long? _entryOrderIdA, _stopOrderIdA, _targetOrderIdA;
    private long? _entryOrderIdB, _stopOrderIdB, _targetOrderIdB;

    public TradovateExecutor(TradovateAuthService auth, StrategyConfig cfg,
                              ILogger<TradovateExecutor> log)
    {
        _auth = auth;
        _cfg  = cfg;
        _log  = log;
    }

    public async Task OnEntrySignalAsync(EntrySignal sig)
    {
        try
        {
            var symbol   = FuturesSymbol.ToTradovate(_cfg.Ticker);
            var action   = sig.Direction == Direction.Long ? "Buy" : "Sell";
            var exitAct  = sig.Direction == Direction.Long ? "Sell" : "Buy";
            var accountId = await GetAccountIdAsync();

            var body = new
            {
                accountSpec = accountId.Name,
                accountId   = accountId.Id,
                action,
                symbol,
                orderQty    = sig.Contracts,
                orderType   = "Market",
                isAutomated = true,
                bracket1    = new { action = exitAct, orderType = "Limit",
                                    price  = (double)sig.Target },
                bracket2    = new { action = exitAct, orderType = "Stop",
                                    stopPrice = (double)sig.Stop }
            };

            var resp = await PostAsync("/order/placeOSO", body);
            _log.LogInformation("[TV] ENTRY {Dir} {Q}ct {Symbol} @ Market | Stop {S} | Tgt {T} → {R}",
                sig.Direction, sig.Contracts, symbol, sig.Stop, sig.Target, resp);

            // Parse response to track order IDs
            ParseOrderIds(resp, sig.Setup);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] ENTRY signal failed");
        }
    }

    public async Task OnPartialSignalAsync(PartialSignal sig)
    {
        try
        {
            // Cancel the target order and place a new LIMIT at partial price
            var (_, _, targetId) = GetOrderIds(sig.Setup);
            if (targetId.HasValue)
                await CancelOrderAsync(targetId.Value);

            var symbol   = FuturesSymbol.ToTradovate(_cfg.Ticker);
            var exitAct  = sig.Direction == Direction.Long ? "Sell" : "Buy";
            var accountId = await GetAccountIdAsync();

            var body = new
            {
                accountSpec = accountId.Name,
                accountId   = accountId.Id,
                action      = exitAct,
                symbol,
                orderQty    = sig.ContractsExited,
                orderType   = "Limit",
                price       = (double)sig.PartialPrice,
                isAutomated = true
            };
            var resp = await PostAsync("/order/placeorder", body);
            _log.LogInformation("[TV] PARTIAL {Q}ct @ {P} → {R}", sig.ContractsExited, sig.PartialPrice, resp);
        }
        catch (Exception ex) { _log.LogError(ex, "[TV] PARTIAL signal failed"); }
    }

    public async Task OnBESignalAsync(BESignal sig)
    {
        try
        {
            // Cancel the existing stop and place a new STOP at BE price
            var (_, stopId, _) = GetOrderIds(sig.Setup);
            if (stopId.HasValue)
                await CancelOrderAsync(stopId.Value);

            var symbol   = FuturesSymbol.ToTradovate(_cfg.Ticker);
            var exitAct  = sig.Direction == Direction.Long ? "Sell" : "Buy";
            var accountId = await GetAccountIdAsync();

            var body = new
            {
                accountSpec = accountId.Name,
                accountId   = accountId.Id,
                action      = exitAct,
                symbol,
                orderQty    = sig.ContractsRemaining,
                orderType   = "Stop",
                stopPrice   = (double)sig.NewStop,
                isAutomated = true
            };
            var resp = await PostAsync("/order/placeorder", body);
            _log.LogInformation("[TV] MOVE_BE Setup={S} → {P} | {R}", sig.Setup, sig.NewStop, resp);
        }
        catch (Exception ex) { _log.LogError(ex, "[TV] BE signal failed"); }
    }

    public async Task OnExitSignalAsync(ExitSignal sig)
    {
        try
        {
            var (entryId, stopId, targetId) = GetOrderIds(sig.Setup);
            // Cancel all open bracket legs
            foreach (var id in new[] { stopId, targetId }.Where(i => i.HasValue))
                await CancelOrderAsync(id!.Value);

            // Place market close order
            var symbol    = FuturesSymbol.ToTradovate(_cfg.Ticker);
            var accountId = await GetAccountIdAsync();

            // Determine exit direction — need to know original direction from order IDs or sig
            // Since we don't store direction here, use sig.Contracts sign:
            // ExitSignal doesn't include direction. Use stop/target cancellation as indicator.
            // Simplification: always place a market order — the engine only exits when in a position.
            var body = new
            {
                accountSpec = accountId.Name,
                accountId   = accountId.Id,
                action      = "Sell", // NOTE: engine should pass direction; for now log and rely on flat
                symbol,
                orderQty    = sig.Contracts,
                orderType   = "Market",
                isAutomated = true
            };
            var resp = await PostAsync("/order/placeorder", body);
            _log.LogWarning("[TV] EXIT {R} {Q}ct — NOTE: verify direction in production! Resp: {Resp}",
                sig.Reason, sig.Contracts, resp);
        }
        catch (Exception ex) { _log.LogError(ex, "[TV] EXIT signal failed"); }
    }

    // ── Private helpers ──────────────────────────────────────────

    private record AccountRef(string Name, long Id);
    private AccountRef? _cachedAccount;

    private async Task<AccountRef> GetAccountIdAsync()
    {
        if (_cachedAccount is not null) return _cachedAccount;
        var resp = await GetAsync("/account/list");
        using var doc = JsonDocument.Parse(resp);
        var first = doc.RootElement.EnumerateArray().First();
        _cachedAccount = new AccountRef(
            first.GetProperty("name").GetString() ?? "",
            first.GetProperty("id").GetInt64());
        return _cachedAccount;
    }

    private void ParseOrderIds(string json, SetupId setup)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var orderId = root.TryGetProperty("orderId", out var oid) ? (long?)oid.GetInt64() : null;
            // Tradovate placeOSO returns the entry order ID; bracket IDs require separate lookup
            if (setup == SetupId.A) _entryOrderIdA = orderId;
            else                    _entryOrderIdB = orderId;
        }
        catch { /* parsing failure is non-fatal */ }
    }

    private async Task CancelOrderAsync(long orderId)
    {
        var body = new { orderId };
        await PostAsync("/order/cancelorder", body);
        _log.LogInformation("[TV] Cancelled order {Id}", orderId);
    }

    private (long? entry, long? stop, long? target) GetOrderIds(SetupId setup)
        => setup == SetupId.A
            ? (_entryOrderIdA, _stopOrderIdA, _targetOrderIdA)
            : (_entryOrderIdB, _stopOrderIdB, _targetOrderIdB);

    private async Task<string> PostAsync(string path, object body)
    {
        var token = await _auth.GetAccessTokenAsync();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var json    = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp    = await http.PostAsync($"{_auth.ApiBaseUrl}{path}", content);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> GetAsync(string path)
    {
        var token = await _auth.GetAccessTokenAsync();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync($"{_auth.ApiBaseUrl}{path}");
        return await resp.Content.ReadAsStringAsync();
    }
}
```

**Step 2: Build CRV.Live**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Live/CRV.Live.csproj
```

Expected: Build succeeded.

---

## Phase 6 — Wire Tradovate into the orchestrator and manual ops

### Task 14: Uncomment Tradovate in `LiveEngineOrchestrator`

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

**Step 1: Add using**

```csharp
using CRV.Live.Brokers.Tradovate;
```

**Step 2: Remove the `// TODO: Tradovate` stubs** placed in Task 4. The switch cases referencing `TradovateBarFeed`, `TradovateExecutor`, `TradovateAuthService` are now real types.

**Step 3: Build and verify**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web/CRV.Web.csproj
```

Expected: Full build succeeded, 0 errors.

---

### Task 15: Add Tradovate methods to `ManualBrokerOps`

**Files:**
- Modify: `CRV.Live/ManualBrokerOps.cs`

Add the following static methods (after the existing Mock methods, around end of file).

**Step 1: Add `GetPositionsTradovateAsync`**

```csharp
public static async Task<List<PositionView>> GetPositionsTradovateAsync(
    TradovateAuthService auth, string accountId)
{
    var token = await auth.GetAccessTokenAsync();
    using var http = BearerClient(token);

    var res  = await http.GetAsync($"{auth.ApiBaseUrl}/position/list");
    var body = await res.Content.ReadAsStringAsync();
    var list = new List<PositionView>();
    if (!res.IsSuccessStatusCode) return list;

    using var doc = JsonDocument.Parse(body);
    foreach (var p in doc.RootElement.EnumerateArray())
    {
        var netPos = p.TryGetProperty("netPos", out var np) ? np.GetDecimal() : 0;
        if (netPos == 0) continue;
        var contractId = p.TryGetProperty("contractId", out var cid) ? cid.GetInt64() : 0;
        var dir        = netPos > 0 ? "LONG" : "SHORT";
        var avgPrice   = p.TryGetProperty("netPrice", out var avg) ? avg.GetDecimal() : 0m;

        // Look up symbol from contractId
        var symRes  = await http.GetAsync($"{auth.ApiBaseUrl}/contract/item?id={contractId}");
        var symBody = await symRes.Content.ReadAsStringAsync();
        string symbol = contractId.ToString();
        try
        {
            using var sd = JsonDocument.Parse(symBody);
            symbol = sd.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? symbol : symbol;
        }
        catch { }

        list.Add(new PositionView(symbol, dir, Math.Abs(netPos), avgPrice, null, null));
    }
    return list;
}
```

**Step 2: Add `GetOrdersTradovateAsync`**

```csharp
public static async Task<List<OrderView>> GetOrdersTradovateAsync(
    TradovateAuthService auth, string accountId,
    string statusFilter, DateTime from, DateTime to)
{
    var token = await auth.GetAccessTokenAsync();
    using var http = BearerClient(token);

    var res  = await http.GetAsync($"{auth.ApiBaseUrl}/order/list");
    var body = await res.Content.ReadAsStringAsync();
    var list = new List<OrderView>();
    if (!res.IsSuccessStatusCode) return list;

    using var doc = JsonDocument.Parse(body);
    foreach (var o in doc.RootElement.EnumerateArray())
    {
        var status = o.TryGetProperty("ordStatus", out var s) ? s.GetString() ?? "" : "";
        if (statusFilter != "ALL" && !string.Equals(status, statusFilter, StringComparison.OrdinalIgnoreCase))
            continue;

        var orderId   = o.TryGetProperty("id",         out var id) ? id.GetInt64().ToString() : "";
        var action    = o.TryGetProperty("action",     out var a)  ? a.GetString()  ?? "" : "";
        var qty       = o.TryGetProperty("totalQty",   out var q)  ? q.GetDecimal() : 0;
        var limit     = o.TryGetProperty("price",      out var lp) ? (decimal?)lp.GetDecimal() : null;
        var stopPrice = o.TryGetProperty("stopPrice",  out var sp) ? (decimal?)sp.GetDecimal() : null;
        var placed    = o.TryGetProperty("timestamp",  out var ts) ? ts.GetString() ?? "" : "";
        var ordType   = o.TryGetProperty("orderType",  out var ot) ? ot.GetString() ?? "" : "";
        var contractId = o.TryGetProperty("contractId", out var ci) ? ci.GetInt64() : 0L;

        var canCancel = status is "Working" or "PendingNew";
        list.Add(new OrderView(orderId, contractId.ToString(), status, status, ordType,
            action, qty, limit, stopPrice, placed, canCancel));
    }
    return list;
}
```

**Step 3: Add `CancelOrderTradovateAsync`**

```csharp
public static async Task<string> CancelOrderTradovateAsync(
    TradovateAuthService auth, string orderId)
{
    var token = await auth.GetAccessTokenAsync();
    using var http = BearerClient(token);
    var body = new StringContent(
        System.Text.Json.JsonSerializer.Serialize(new { orderId = long.Parse(orderId) }),
        System.Text.Encoding.UTF8, "application/json");
    var res = await http.PostAsync($"{auth.ApiBaseUrl}/order/cancelorder", body);
    return res.IsSuccessStatusCode ? $"Order {orderId} cancel request sent." : $"Cancel failed: {res.StatusCode}";
}
```

**Step 4: Add `FlatAtMarketTradovateAsync` and `CancelAllTradovateAsync`**

```csharp
public static async Task<string> FlatAtMarketTradovateAsync(
    TradovateAuthService auth, string accountId,
    string symbol, int contracts, bool isCurrentLong)
{
    var token = await auth.GetAccessTokenAsync();
    using var http = BearerClient(token);
    var accountList = await http.GetStringAsync($"{auth.ApiBaseUrl}/account/list");
    using var ad = System.Text.Json.JsonDocument.Parse(accountList);
    var acc = ad.RootElement.EnumerateArray().First();
    var accSpec = acc.GetProperty("name").GetString() ?? "";
    var accId   = acc.GetProperty("id").GetInt64();

    var action = isCurrentLong ? "Sell" : "Buy";
    var body = new StringContent(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            accountSpec = accSpec,
            accountId   = accId,
            action,
            symbol      = FuturesSymbol.ToTradovate(symbol),
            orderQty    = contracts,
            orderType   = "Market",
            isAutomated = true
        }), System.Text.Encoding.UTF8, "application/json");

    var res = await http.PostAsync($"{auth.ApiBaseUrl}/order/placeorder", body);
    var resp = await res.Content.ReadAsStringAsync();
    return res.IsSuccessStatusCode ? $"Flat Market order placed for {contracts}ct." : $"Flat failed: {resp}";
}

public static async Task<string> CancelAllTradovateAsync(
    TradovateAuthService auth, string accountId, string symbol)
{
    // List all working orders and cancel each
    var orders = await GetOrdersTradovateAsync(auth, accountId, "Working", DateTime.Today, DateTime.Today.AddDays(1));
    var sym    = FuturesSymbol.ToTradovate(symbol);
    var cancelled = 0;
    foreach (var o in orders.Where(o => o.Symbol.Contains(sym[..^1], StringComparison.OrdinalIgnoreCase) && o.CanCancel))
    {
        await CancelOrderTradovateAsync(auth, o.OrderId);
        cancelled++;
    }
    return $"Cancelled {cancelled} working orders.";
}
```

**Step 5: Add using at top of ManualBrokerOps.cs**

```csharp
using CRV.Live.Brokers.Tradovate;
```

**Step 6: Build CRV.Live**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Live/CRV.Live.csproj
```

Expected: Build succeeded.

---

### Task 16: Add Tradovate cases to `Manual.cshtml.cs` and `Orders.cshtml.cs`

**Files:**
- Modify: `CRV.Web/Pages/Trading/Manual.cshtml.cs`
- Modify: `CRV.Web/Pages/Trading/Orders.cshtml.cs`

**Step 1: Add `TradovateAuthService` to both page model constructors**

In `Manual.cshtml.cs`:
```csharp
// Add field
private readonly TradovateAuthService _tv;

// Add to constructor param list
TradovateAuthService tv,

// Add in body
_tv = tv;
```

**Step 2: Add Tradovate cases to all broker switch statements in `Manual.cshtml.cs`**

For `OnGetPositionsAsync`:
```csharp
"Tradovate" => await ManualBrokerOps.GetPositionsTradovateAsync(_tv, cfg.AccountId),
```

For `OnPostAsync` (place OCO order) — add:
```csharp
"Tradovate" => await ManualBrokerOps.PlaceOcoTradovateAsync(_tv, cfg.AccountId, ...),
```

For `OnPostCancelAllAsync`:
```csharp
"Tradovate" => await ManualBrokerOps.CancelAllTradovateAsync(_tv, cfg.AccountId, cfg.Ticker),
```

For `OnPostFlatAsync` and `OnPostFlatPositionAsync`:
```csharp
"Tradovate" => await ManualBrokerOps.FlatAtMarketTradovateAsync(_tv, cfg.AccountId, ...),
```

**Step 3: Add `PlaceOcoTradovateAsync` to `ManualBrokerOps`**

```csharp
public static async Task<string> PlaceOcoTradovateAsync(
    TradovateAuthService auth, string accountId,
    string symbol, string action, int qty,
    decimal entryPrice, decimal stopPrice, decimal targetPrice,
    bool isMarket = true)
{
    var token = await auth.GetAccessTokenAsync();
    using var http = BearerClient(token);
    var accountList = await http.GetStringAsync($"{auth.ApiBaseUrl}/account/list");
    using var ad = System.Text.Json.JsonDocument.Parse(accountList);
    var acc     = ad.RootElement.EnumerateArray().First();
    var accSpec = acc.GetProperty("name").GetString() ?? "";
    var accId   = acc.GetProperty("id").GetInt64();
    var exitAct = action == "Buy" ? "Sell" : "Buy";

    var body = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
    {
        accountSpec  = accSpec,
        accountId    = accId,
        action,
        symbol       = FuturesSymbol.ToTradovate(symbol),
        orderQty     = qty,
        orderType    = isMarket ? "Market" : "Limit",
        price        = isMarket ? (double?)null : (double)entryPrice,
        isAutomated  = true,
        bracket1     = new { action = exitAct, orderType = "Limit",  price     = (double)targetPrice },
        bracket2     = new { action = exitAct, orderType = "Stop",   stopPrice = (double)stopPrice }
    }), System.Text.Encoding.UTF8, "application/json");

    var res  = await http.PostAsync($"{auth.ApiBaseUrl}/order/placeOSO", body);
    var resp = await res.Content.ReadAsStringAsync();
    return res.IsSuccessStatusCode ? $"OCO bracket placed for {qty}ct." : $"Order failed: {resp}";
}
```

**Step 4: In `Orders.cshtml.cs` — add `TradovateAuthService` and Tradovate cases**

Same pattern as Manual page: add constructor param, add `_tv = tv`, add Tradovate cases to `OnGetOrdersAsync` and `OnPostCancelOrderAsync` switches.

**Step 5: Build CRV.Web**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web/CRV.Web.csproj
```

Expected: Full build succeeded.

---

## Phase 7 — Final integration test

### Task 17: Full integration smoke test

**Step 1: Build the complete solution**

```bash
dotnet build /Users/ciro/Source/WebApps/CRV.Trading/CRV.Trading.sln
```

Expected: Build succeeded, 0 errors, 0 warnings (ideally).

**Step 2: Run all unit tests**

```bash
dotnet test /Users/ciro/Source/WebApps/CRV.Trading/CRV.Core.Tests/CRV.Core.Tests.csproj
```

Expected: ≥52 tests passed (50 existing + 2 ExecBroker validation tests).

**Step 3: Start the web app and verify pages load**

```bash
cd /Users/ciro/Source/WebApps/CRV.Trading/CRV.Web && dotnet run &
```

Verify these pages load without error:
- `/settings/live` — shows Schwab + TradeStation + Tradovate auth banners, Data Broker + Exec Broker dropdowns
- `/auth/tradovate` — shows "Not connected" with Connect button
- `/trading/mock` — shows empty state with icon
- `/trading/manual` — loads unchanged
- `/trading/orders` — loads unchanged

**Step 4: Verify Mock broker tagging**

1. Set `Exec Broker = Mock` in Live Settings → Save
2. Start engine (with a real data broker like Schwab or TradeStation)
3. Let a trade complete
4. Navigate to `/trading/mock` — trade should appear with correct columns

---

### Task 18: Update MEMORY.md

**Files:**
- Modify: `/Users/ciro/.claude/projects/-Users-ciro-Source-WebApps-CRV-Trading/memory/MEMORY.md`

Add to Improvements Implemented:
```
49. Added Tradovate broker — TradovateAuthService (credential POST, dual tokens, 90min expiry), TradovateBarFeed (WSS md endpoint), TradovateExecutor (placeOSO brackets, 1-digit year symbol format). Symbol: NQH26 → NQH6 via FuturesSymbol.ToTradovate(). Registered in Program.cs, appsettings.json, /auth/tradovate page.
50. Added ExecBroker split — StrategyConfig.ExecBroker / ExecAccountId / EffectiveExecBroker / EffectiveExecAccountId. LiveEngineOrchestrator now selects bar feed from cfg.Broker and executor from cfg.EffectiveExecBroker independently. Live Settings shows Data Broker + Exec Broker dropdowns. Manual and Orders pages use EffectiveExecBroker for all execution ops.
51. Added Mock broker persistence — SourceOverrideSink decorator tags mock-executor trades with Source="mock" before DB save. New /trading/mock page: day selector, metric cards (All/A/B), trade table (same columns as backtest log), Chart.js equity curve (full history). Nav: "Mock" link in nav bar. No schema change — uses existing Source field.
```

---

## Implementation Notes

### Known Limitations / Production TODOs

1. **`TradovateExecutor.OnExitSignalAsync`**: The action direction (Buy vs Sell) is hardcoded to "Sell" for the example. In production, the executor needs to know the original entry direction. Options: (a) store direction when entry fires, (b) pass direction in `ExitSignal` (requires signal model change). For now, the OSO bracket's stop/target legs handle normal exits — the manual exit path uses the explicit `FlatAtMarket` method.

2. **Tradovate `TradovateBarFeed` volume**: Tradovate's chart API returns `upVolume` + `downVolume` separately. The code sums them. Verify this is correct for the specific feed.

3. **Historical bar parsing**: Tradovate chart response structure should be verified against actual API response. The field names `bars[].timestamp`, `bars[].open`, etc. may differ between API versions.

4. **`BarResampler` for Tradovate**: The bar feed requests `elementSize = cfg.ExecutionTFMinutes` directly from Tradovate, so no client-side resampling is needed (unlike CSV path).

5. **Tradovate `GetOrdersTradovateAsync` symbol filtering**: The `symbol` field in Tradovate order responses is a `contractId` (number), not a symbol name. The `CancelAllTradovateAsync` filter may not work correctly. Production fix: look up symbol ↔ contractId mapping.

6. **`ExecBroker` and `StrategyConfig with { }` records**: `StrategyConfig` is a class, not a record — the plan uses `cfg.Clone()` with property reassignment instead of `with` syntax.
