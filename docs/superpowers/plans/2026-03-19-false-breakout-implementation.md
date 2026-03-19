# False Breakout (Setup C & D) Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two false breakout setups (C = ORB False Breakout, D = Session Range False Breakout) to the existing OrbStrategyEngine, reusing the same config/entry/exit/dashboard patterns as setups A and B.

**Architecture:** A new `FalseBreakoutDetector` module in `CRV.Core/Modules/` handles breakout detection and rejection filtering. The engine (`OrbStrategyEngine`) consumes the detector's `IsActivated` signal to arm setups C/D, then manages trades using the same state machine, entry/exit, and PnL tracking patterns as A/B.

**Tech Stack:** C# .NET 10, ASP.NET Razor Pages, SignalR, xUnit, EF Core/SQLite

**Spec:** `docs/superpowers/specs/2026-03-19-false-breakout-design.md`

---

## File Structure

### New Files
| File | Responsibility |
|------|---------------|
| `CRV.Core/Modules/FalseBreakoutDetector.cs` | Breakout detection module with OrbTracker + SessionRangeTracker |
| `CRV.Core.Tests/Modules/FalseBreakoutDetectorTests.cs` | Module unit tests |
| `CRV.Core.Tests/Strategy/FalseBreakoutIntegrationTests.cs` | Engine-level integration tests |
| EF migration file (auto-generated) | DB schema for C/D config columns |

### Modified Files
| File | Changes |
|------|---------|
| `CRV.Core/Models/StrategyConfig.cs` | Add ~20 properties per setup (C/D) + 6 FB module params |
| `CRV.Core/Models/SessionConfig.cs` | Add `SetupConfigC`/`SetupConfigD` classes, wire into `ToLegacyConfig`/`FromExistingConfig`/`CreateDefaults` |
| `CRV.Core/Models/Signals.cs` | Add C/D fields to `EngineSnapshot` |
| `CRV.Core/Strategy/OrbStrategyEngine.cs` | Add C/D fields, ProcessSetupC/D, EvalTickSetupC/D, wire into all integration points |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Add `ForceExitSetupC`/`ForceExitSetupD` |
| `CRV.Web/Pages/Dashboard/Index.cshtml` | Add C/D setup cards + FB context card |
| `CRV.Web/Pages/Dashboard/Index.cshtml.cs` | Add `OnPostForceExitC`/`OnPostForceExitD` |
| `CRV.Web/Pages/Settings/Live.cshtml` | Add C/D form sections + FB module params |
| `CRV.Web/Pages/Settings/Live.cshtml.cs` | Handle C/D in session JSON |
| `CRV.Web/wwwroot/js/crv-hub.js` | Handle C/D in snapshot updates |

---

## Chunk 1: FalseBreakoutDetector Module + Tests

### Task 1: RangeBreakoutTracker and FalseBreakoutDetector

**Files:**
- Create: `CRV.Core/Modules/FalseBreakoutDetector.cs`
- Test: `CRV.Core.Tests/Modules/FalseBreakoutDetectorTests.cs`

- [ ] **Step 1: Write failing test — breakout detected on bar close outside ORB**

```csharp
// File: CRV.Core.Tests/Modules/FalseBreakoutDetectorTests.cs
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Tests.Modules;

public class FalseBreakoutDetectorTests
{
    private FalseBreakoutDetector CreateDetector(int maxMinutesOrb = 15, int tfMinutes = 5)
    {
        var cfg = new StrategyConfig
        {
            FBMaxTimeOutsideMinutesOrb = maxMinutesOrb,
            FBMaxTimeOutsideMinutesSR  = 60,
            FBMaxPenetrationPctOrb     = 0.30m,
            FBMaxPenetrationPctSR      = 0.25m,
            FBMinRejectionBodyPct      = 0.50m,
            FBMaxTrendDayScore         = 60,
            ExecutionTFMinutes         = tfMinutes,
            TickSize                   = 0.25m,
        };
        return new FalseBreakoutDetector(cfg);
    }

    [Fact]
    public void OrbTracker_DetectsBreakoutAbove()
    {
        var det = CreateDetector();
        var today = DateTime.UtcNow.Date;

        // Bar closes above ORB high — breakout detected
        var bar = new Bar(today.AddHours(10), 100m, 102m, 99.5m, 101.5m, 1000);
        det.OnBar(bar, today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true,
            vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.True(det.OrbTracker.BreakoutActive);
        Assert.Equal(Direction.Long, det.OrbTracker.BreakoutDirection);
        Assert.Equal(1, det.OrbTracker.BarsInBreakout);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FalseBreakoutDetectorTests" --nologo -v q`
Expected: FAIL — `FalseBreakoutDetector` class does not exist

- [ ] **Step 3: Write FalseBreakoutDetector with RangeBreakoutTracker**

```csharp
// File: CRV.Core/Modules/FalseBreakoutDetector.cs
using CRV.Core.Models;

namespace CRV.Core.Modules;

/// <summary>
/// Tracks false breakout conditions on two range sources:
/// OrbTracker (ORB high/low) and SessionRangeTracker (prior session high/low).
/// </summary>
public class FalseBreakoutDetector : IEngineModule
{
    private StrategyConfig _cfg;

    public RangeBreakoutTracker OrbTracker          { get; }
    public RangeBreakoutTracker SessionRangeTracker  { get; }

    public bool IsCompoundFakeout =>
        OrbTracker.IsActivated && SessionRangeTracker.IsActivated &&
        OrbTracker.BreakoutDirection == SessionRangeTracker.BreakoutDirection;

    public FalseBreakoutDetector(StrategyConfig cfg)
    {
        _cfg = cfg;
        int tfMin = Math.Max(1, cfg.ExecutionTFMinutes);
        OrbTracker = new RangeBreakoutTracker(
            maxBarsAllowed: Math.Max(1, cfg.FBMaxTimeOutsideMinutesOrb / tfMin),
            maxPenetrationPct: cfg.FBMaxPenetrationPctOrb,
            minRejectionBodyPct: cfg.FBMinRejectionBodyPct,
            maxTrendDayScore: cfg.FBMaxTrendDayScore);
        SessionRangeTracker = new RangeBreakoutTracker(
            maxBarsAllowed: Math.Max(1, cfg.FBMaxTimeOutsideMinutesSR / tfMin),
            maxPenetrationPct: cfg.FBMaxPenetrationPctSR,
            minRejectionBodyPct: cfg.FBMinRejectionBodyPct,
            maxTrendDayScore: cfg.FBMaxTrendDayScore);
    }

    public void Reconfigure(StrategyConfig cfg)
    {
        _cfg = cfg;
        int tfMin = Math.Max(1, cfg.ExecutionTFMinutes);
        OrbTracker.UpdateConfig(
            Math.Max(1, cfg.FBMaxTimeOutsideMinutesOrb / tfMin),
            cfg.FBMaxPenetrationPctOrb, cfg.FBMinRejectionBodyPct, cfg.FBMaxTrendDayScore);
        SessionRangeTracker.UpdateConfig(
            Math.Max(1, cfg.FBMaxTimeOutsideMinutesSR / tfMin),
            cfg.FBMaxPenetrationPctSR, cfg.FBMinRejectionBodyPct, cfg.FBMaxTrendDayScore);
    }

    /// <summary>Snapshot prior session range at session boundary.</summary>
    public void OnSessionStart(decimal priorSessionHigh, decimal priorSessionLow)
    {
        SessionRangeTracker.SetReferenceRange(priorSessionHigh, priorSessionLow);
    }

    public void OnBar(Bar bar, DateTime tradingDate,
        decimal orbHigh, decimal orbLow, bool orbFormed,
        decimal vwap, int trendBullScore, int trendBearScore)
    {
        if (orbFormed)
            OrbTracker.OnBar(bar, orbHigh, orbLow, orbHigh - orbLow, vwap, trendBullScore, trendBearScore);

        if (SessionRangeTracker.HasReferenceRange)
            SessionRangeTracker.OnBar(bar,
                SessionRangeTracker.RangeHigh, SessionRangeTracker.RangeLow,
                SessionRangeTracker.RangeHigh - SessionRangeTracker.RangeLow,
                vwap, trendBullScore, trendBearScore);
    }

    // IEngineModule — simplified interface (engine calls the richer OnBar overload)
    void IEngineModule.OnBar(Bar bar, DateTime tradingDate) { }

    public void OnTick(decimal price, DateTime utcTime)
    {
        OrbTracker.OnTick(price);
        SessionRangeTracker.OnTick(price);
    }

    public void NewSession(DateTime tradingDate)
    {
        OrbTracker.Reset();
        SessionRangeTracker.Reset();
    }
}

/// <summary>
/// Tracks a single range source for false breakout detection.
/// Monitors: breakout → bar counting → rejection → activation.
/// </summary>
public class RangeBreakoutTracker
{
    private int     _maxBarsAllowed;
    private decimal _maxPenetrationPct;
    private decimal _minRejectionBodyPct;
    private int     _maxTrendDayScore;

    // Reference range (set externally for session range; set via OnBar params for ORB)
    public decimal RangeHigh          { get; private set; }
    public decimal RangeLow           { get; private set; }
    public bool    HasReferenceRange  { get; private set; }

    // Breakout state
    public bool       BreakoutActive     { get; private set; }
    public Direction? BreakoutDirection  { get; private set; }
    public int        BarsInBreakout     { get; private set; }
    public decimal    SweepHigh          { get; private set; }
    public decimal    SweepLow           { get; private set; }

    // Rejection / activation
    public bool    IsActivated        { get; private set; }
    public decimal RejectionBarHigh   { get; private set; }
    public decimal RejectionBarLow    { get; private set; }
    public decimal PenetrationDepth   { get; private set; }

    public RangeBreakoutTracker(int maxBarsAllowed, decimal maxPenetrationPct,
        decimal minRejectionBodyPct, int maxTrendDayScore)
    {
        _maxBarsAllowed      = maxBarsAllowed;
        _maxPenetrationPct   = maxPenetrationPct;
        _minRejectionBodyPct = minRejectionBodyPct;
        _maxTrendDayScore    = maxTrendDayScore;
    }

    public void UpdateConfig(int maxBars, decimal maxPen, decimal minBody, int maxTrend)
    {
        _maxBarsAllowed      = maxBars;
        _maxPenetrationPct   = maxPen;
        _minRejectionBodyPct = minBody;
        _maxTrendDayScore    = maxTrend;
    }

    public void SetReferenceRange(decimal high, decimal low)
    {
        RangeHigh = high;
        RangeLow  = low;
        HasReferenceRange = high > low && low > 0;
    }

    public void OnBar(Bar bar, decimal rangeHigh, decimal rangeLow, decimal rangeSize,
        decimal vwap, int trendBullScore, int trendBearScore)
    {
        if (rangeSize <= 0) return;
        if (IsActivated) return; // already activated, engine must consume and reset

        if (!BreakoutActive)
        {
            // Check for new breakout
            if (bar.Close > rangeHigh)
            {
                BreakoutActive    = true;
                BreakoutDirection = Direction.Long; // broke above
                BarsInBreakout    = 1;
                SweepHigh         = bar.High;
                SweepLow          = bar.Low;
                PenetrationDepth  = (bar.High - rangeHigh) / rangeSize;
            }
            else if (bar.Close < rangeLow)
            {
                BreakoutActive    = true;
                BreakoutDirection = Direction.Short; // broke below
                BarsInBreakout    = 1;
                SweepHigh         = bar.High;
                SweepLow          = bar.Low;
                PenetrationDepth  = (rangeLow - bar.Low) / rangeSize;
            }
            return;
        }

        // Breakout active — update
        BarsInBreakout++;
        if (bar.High > SweepHigh) SweepHigh = bar.High;
        if (bar.Low  < SweepLow)  SweepLow  = bar.Low;

        // Check expiry
        if (BarsInBreakout > _maxBarsAllowed)
        {
            ResetBreakout();
            return;
        }

        // Check rejection: price closed back inside range
        bool closedInside = bar.Close <= rangeHigh && bar.Close >= rangeLow;
        if (!closedInside) return;

        // Quality filters
        decimal bodySize  = Math.Abs(bar.Close - bar.Open);
        decimal totalSize = bar.High - bar.Low;
        if (totalSize <= 0) return;
        if (bodySize / totalSize < _minRejectionBodyPct) return;
        if (PenetrationDepth > _maxPenetrationPct) return;

        // VWAP filter: VWAP must be on opposite side of breakout
        bool vwapOk = BreakoutDirection == Direction.Long
            ? vwap < rangeHigh  // broke above, VWAP should be inside/below
            : vwap > rangeLow;  // broke below, VWAP should be inside/above
        if (!vwapOk) return;

        // TrendDay filter: opposing direction score must be below threshold
        int opposingScore = BreakoutDirection == Direction.Long ? trendBullScore : trendBearScore;
        if (opposingScore > _maxTrendDayScore) return;

        // All filters pass
        IsActivated      = true;
        RejectionBarHigh = bar.High;
        RejectionBarLow  = bar.Low;
    }

    public void OnTick(decimal price)
    {
        if (!BreakoutActive || IsActivated) return;
        if (price > SweepHigh) SweepHigh = price;
        if (price < SweepLow)  SweepLow  = price;
    }

    public void Reset()
    {
        ResetBreakout();
        IsActivated      = false;
        RejectionBarHigh = 0;
        RejectionBarLow  = 0;
        HasReferenceRange = false;
        RangeHigh = 0;
        RangeLow  = 0;
    }

    /// <summary>Clear activation flag after engine consumes it for arming.</summary>
    public void ClearActivation()
    {
        IsActivated      = false;
        RejectionBarHigh = 0;
        RejectionBarLow  = 0;
        ResetBreakout();
    }

    private void ResetBreakout()
    {
        BreakoutActive    = false;
        BreakoutDirection = null;
        BarsInBreakout    = 0;
        SweepHigh         = 0;
        SweepLow          = 0;
        PenetrationDepth  = 0;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "OrbTracker_DetectsBreakoutAbove" --nologo -v q`
Expected: PASS

- [ ] **Step 5: Write remaining module tests**

Add to `FalseBreakoutDetectorTests.cs`:

```csharp
[Fact]
public void OrbTracker_DetectsBreakoutBelow()
{
    var det = CreateDetector();
    var today = DateTime.UtcNow.Date;
    var bar = new Bar(today.AddHours(10), 100m, 100.5m, 98m, 98.5m, 1000);
    det.OnBar(bar, today, orbHigh: 101m, orbLow: 99m, orbFormed: true,
        vwap: 100m, trendBullScore: 30, trendBearScore: 30);

    Assert.True(det.OrbTracker.BreakoutActive);
    Assert.Equal(Direction.Short, det.OrbTracker.BreakoutDirection);
}

[Fact]
public void OrbTracker_ExpiresAfterMaxBars()
{
    var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5); // 3 bars max
    var today = DateTime.UtcNow.Date;

    // 4 bars outside range → expires
    for (int i = 0; i < 4; i++)
    {
        var bar = new Bar(today.AddHours(10).AddMinutes(i * 5), 102m, 103m, 101.5m, 102.5m, 100);
        det.OnBar(bar, today, orbHigh: 101m, orbLow: 99m, orbFormed: true,
            vwap: 100m, trendBullScore: 30, trendBearScore: 30);
    }

    Assert.False(det.OrbTracker.BreakoutActive);
    Assert.False(det.OrbTracker.IsActivated);
}

[Fact]
public void OrbTracker_ActivatesOnRejection()
{
    var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
    var today = DateTime.UtcNow.Date;

    // Bar 1: breakout above
    det.OnBar(new Bar(today.AddHours(10), 100m, 101.5m, 99.5m, 101.2m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);
    Assert.True(det.OrbTracker.BreakoutActive);

    // Bar 2: closes back inside with strong body (rejection) → activated
    det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101.2m, 101.3m, 100m, 100.2m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

    Assert.True(det.OrbTracker.IsActivated);
    Assert.Equal(Direction.Long, det.OrbTracker.BreakoutDirection);
}

[Fact]
public void OrbTracker_RejectsWeakBody()
{
    var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
    var today = DateTime.UtcNow.Date;

    // Breakout above
    det.OnBar(new Bar(today.AddHours(10), 100m, 101.5m, 99.5m, 101.2m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

    // Weak body: O=101, C=100.9 (body=0.1 of range 101.3-99.8=1.5 = 6.7% < 50%)
    det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101.3m, 99.8m, 100.9m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

    Assert.False(det.OrbTracker.IsActivated);
}

[Fact]
public void OrbTracker_RejectsExcessivePenetration()
{
    var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
    var today = DateTime.UtcNow.Date;
    // ORB range = 2 (101-99). Max pen = 30% = 0.6. Actual pen = 1.0 → reject
    det.OnBar(new Bar(today.AddHours(10), 100m, 102m, 99.5m, 101.5m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);
    // Close back inside
    det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

    Assert.False(det.OrbTracker.IsActivated);
}

[Fact]
public void OrbTracker_RejectsWrongVwapSide()
{
    var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
    var today = DateTime.UtcNow.Date;

    // Breakout above with small pen
    det.OnBar(new Bar(today.AddHours(10), 100.5m, 101.3m, 100m, 101.1m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 101.5m, // VWAP above range = wrong side
        trendBullScore: 30, trendBearScore: 30);
    // Close back inside with strong body
    det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 101.5m, trendBullScore: 30, trendBearScore: 30);

    Assert.False(det.OrbTracker.IsActivated);
}

[Fact]
public void OrbTracker_RejectsHighTrendScore()
{
    var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
    var today = DateTime.UtcNow.Date;

    // Breakout above — opposing score is bullScore (broke in bull direction)
    det.OnBar(new Bar(today.AddHours(10), 100.5m, 101.3m, 100m, 101.1m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m,
        trendBullScore: 80, trendBearScore: 30); // bull=80 > 60 threshold
    det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m,
        trendBullScore: 80, trendBearScore: 30);

    Assert.False(det.OrbTracker.IsActivated);
}

[Fact]
public void SessionRangeTracker_UsesLockedRange()
{
    var det = CreateDetector();
    var today = DateTime.UtcNow.Date;

    // Simulate London start: lock Asia range
    det.OnSessionStart(priorSessionHigh: 100m, priorSessionLow: 96m);
    Assert.True(det.SessionRangeTracker.HasReferenceRange);

    // Bar breaks below Asia low
    det.OnBar(new Bar(today.AddHours(10), 96.5m, 97m, 95m, 95.5m, 100), today,
        orbHigh: 0, orbLow: 0, orbFormed: false, vwap: 98m, trendBullScore: 30, trendBearScore: 30);

    Assert.True(det.SessionRangeTracker.BreakoutActive);
    Assert.Equal(Direction.Short, det.SessionRangeTracker.BreakoutDirection);
}

[Fact]
public void CompoundFakeout_BothActivateSameDirection()
{
    var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
    var today = DateTime.UtcNow.Date;

    // Lock session range = same as ORB for compound test
    det.OnSessionStart(priorSessionHigh: 101m, priorSessionLow: 99m);

    // Bar 1: breakout above both ranges
    det.OnBar(new Bar(today.AddHours(10), 100.5m, 101.3m, 100m, 101.1m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

    // Bar 2: rejection back inside both
    det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

    Assert.True(det.OrbTracker.IsActivated);
    Assert.True(det.SessionRangeTracker.IsActivated);
    Assert.True(det.IsCompoundFakeout);
}

[Fact]
public void NewSession_ResetsAllState()
{
    var det = CreateDetector();
    var today = DateTime.UtcNow.Date;

    det.OnSessionStart(100m, 96m);
    det.OnBar(new Bar(today.AddHours(10), 100m, 101.5m, 99.5m, 101.2m, 100), today,
        orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

    det.NewSession(today.AddDays(1));

    Assert.False(det.OrbTracker.BreakoutActive);
    Assert.False(det.OrbTracker.IsActivated);
    Assert.False(det.SessionRangeTracker.HasReferenceRange);
}
```

- [ ] **Step 6: Run all module tests**

Run: `dotnet test --filter "FalseBreakoutDetectorTests" --nologo -v q`
Expected: All PASS

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Modules/FalseBreakoutDetector.cs CRV.Core.Tests/Modules/FalseBreakoutDetectorTests.cs
git commit -m "feat: add FalseBreakoutDetector module with OrbTracker and SessionRangeTracker"
```

---

## Chunk 2: Config Properties (StrategyConfig, SessionConfig, Signals)

### Task 2: StrategyConfig C/D Properties + FB Module Params

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs`

- [ ] **Step 1: Add Setup C properties after Setup B block**

Follow the exact pattern from Setup A/B (lines 79-163). Add after the last Setup B property:

```csharp
// ── Setup C — ORB False Breakout ──────────────────────────────────────
public bool    EnableC            { get; set; } = false;
public int     MaxTradesC         { get; set; } = 3;
public decimal NearPctC           { get; set; } = 0.15m;
public decimal StopPctC           { get; set; } = 0.10m;
public int     TargetPctC         { get; set; } = 100;
public int     PartialPctC        { get; set; } = 50;
public decimal MinRrC             { get; set; } = 1.5m;
public bool    UsePartialC        { get; set; } = true;
public bool    UseBeC             { get; set; } = true;
public bool    AllowRearmAfterBeC { get; set; } = true;
public int     EntryTickOffsetC   { get; set; } = 0;
public string  OrderTypeC         { get; set; } = "Market";
public int     PartialCtsC        { get; set; } = 0;
public int     MaxAdverseMinutesC { get; set; } = 0;
public int     ContractsC         { get; set; } = 2;
public decimal HiVolMultC         { get; set; } = 1.0m;
public int     MaxContractsC      { get; set; } = 2;
public int     CutoffHourC        { get; set; } = 14;
public int     CutoffMinuteC      { get; set; } = 30;
public bool    CloseAtRthCloseC   { get; set; } = true;
```

Same set for D (`EnableD`, `MaxTradesD`, etc.).

- [ ] **Step 2: Add FB module params**

```csharp
// ── False Breakout Module Params ──────────────────────────────────────
public int     FBMaxTimeOutsideMinutesOrb { get; set; } = 15;
public int     FBMaxTimeOutsideMinutesSR  { get; set; } = 60;
public decimal FBMaxPenetrationPctOrb     { get; set; } = 0.30m;
public decimal FBMaxPenetrationPctSR      { get; set; } = 0.25m;
public decimal FBMinRejectionBodyPct      { get; set; } = 0.50m;
public int     FBMaxTrendDayScore         { get; set; } = 60;
```

- [ ] **Step 3: Build to verify no errors**

Run: `dotnet build --nologo -v q`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Models/StrategyConfig.cs
git commit -m "feat: add Setup C/D and FB module params to StrategyConfig"
```

### Task 3: SessionConfig — SetupConfigC/D + ToLegacyConfig

**Files:**
- Modify: `CRV.Core/Models/SessionConfig.cs`

- [ ] **Step 1: Add SetupConfigC and SetupConfigD classes**

After `SetupConfigB` class (line 59):

```csharp
// ── Setup C — ORB False Breakout ─────────────────────────────────────
public class SetupConfigC : SetupConfigBase
{
    public decimal NearPct          { get; set; } = 0.15m;
    public decimal StopPct          { get; set; } = 0.10m;
    public int     TargetPct        { get; set; } = 100;
    public int     EntryTickOffset  { get; set; } = 0;
}

// ── Setup D — Session Range False Breakout ───────────────────────────
public class SetupConfigD : SetupConfigBase
{
    public decimal NearPct          { get; set; } = 0.15m;
    public decimal StopPct          { get; set; } = 0.10m;
    public int     TargetPct        { get; set; } = 100;
    public int     EntryTickOffset  { get; set; } = 0;
}
```

- [ ] **Step 2: Add SetupC/D to SessionConfig**

After `SetupB` property (line 77):

```csharp
public SetupConfigC SetupC        { get; set; } = new();
public SetupConfigD SetupD        { get; set; } = new();
```

- [ ] **Step 3: Add C/D mapping to ToLegacyConfig()**

After the Setup B block (line 149), add:

```csharp
// ── Setup C ────────────────────────────────────────────────────────
c.EnableC           = SetupC.Enabled;
c.ContractsC        = SetupC.Contracts;
c.PartialCtsC       = SetupC.PartialCts;
c.PartialPctC       = SetupC.PartialPct;
c.CutoffHourC       = SetupC.CutoffHour;
c.CutoffMinuteC     = SetupC.CutoffMinute;
c.MaxTradesC        = SetupC.MaxTrades;
c.OrderTypeC        = SetupC.OrderType;
c.MinRrC            = SetupC.MinRr;
c.CloseAtRthCloseC  = SetupC.CloseAtRthClose;
c.UsePartialC       = SetupC.UsePartial;
c.UseBeC            = SetupC.UseBe;
c.AllowRearmAfterBeC = SetupC.AllowRearmAfterBE;
c.MaxAdverseMinutesC = SetupC.MaxAdverseMinutes;
c.HiVolMultC        = SetupC.HiVolMult;
c.MaxContractsC     = SetupC.MaxContracts;
c.NearPctC          = SetupC.NearPct;
c.StopPctC          = SetupC.StopPct;
c.TargetPctC        = SetupC.TargetPct;
c.EntryTickOffsetC  = SetupC.EntryTickOffset;

// ── Setup D ────────────────────────────────────────────────────────
c.EnableD           = SetupD.Enabled;
c.ContractsD        = SetupD.Contracts;
c.PartialCtsD       = SetupD.PartialCts;
c.PartialPctD       = SetupD.PartialPct;
c.CutoffHourD       = SetupD.CutoffHour;
c.CutoffMinuteD     = SetupD.CutoffMinute;
c.MaxTradesD        = SetupD.MaxTrades;
c.OrderTypeD        = SetupD.OrderType;
c.MinRrD            = SetupD.MinRr;
c.CloseAtRthCloseD  = SetupD.CloseAtRthClose;
c.UsePartialD       = SetupD.UsePartial;
c.UseBeD            = SetupD.UseBe;
c.AllowRearmAfterBeD = SetupD.AllowRearmAfterBE;
c.MaxAdverseMinutesD = SetupD.MaxAdverseMinutes;
c.HiVolMultD        = SetupD.HiVolMult;
c.MaxContractsD     = SetupD.MaxContracts;
c.NearPctD          = SetupD.NearPct;
c.StopPctD          = SetupD.StopPct;
c.TargetPctD        = SetupD.TargetPct;
c.EntryTickOffsetD  = SetupD.EntryTickOffset;
```

- [ ] **Step 4: Add C/D to FromExistingConfig()**

After the SetupB block (line 224), add SetupC and SetupD initialization blocks following the same pattern.

- [ ] **Step 5: Add C/D to CreateDefaults()**

In the `asia` and `london` session configs, the default `SetupC`/`SetupD` auto-initialize from default constructors. No changes needed there since `new SessionConfig()` already includes `SetupC = new()`.

- [ ] **Step 6: Build to verify**

Run: `dotnet build --nologo -v q`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Models/SessionConfig.cs
git commit -m "feat: add SetupConfigC/D to SessionConfig with ToLegacyConfig mapping"
```

### Task 4: EngineSnapshot C/D Fields

**Files:**
- Modify: `CRV.Core/Models/Signals.cs`

- [ ] **Step 1: Add C/D fields to EngineSnapshot**

After the existing SetupB fields in `EngineSnapshot`, add:

```csharp
// ── Setup C/D ─────────────────────────────────────────────────────
public ActiveTradeView? SetupC        { get; init; }
public ActiveTradeView? SetupD        { get; init; }
public int  TradeCountC   { get; init; }
public int  TradeCountD   { get; init; }
public int  MaxTradesC    { get; init; }
public int  MaxTradesD    { get; init; }
public int  SetupCState   { get; init; }
public int  SetupDState   { get; init; }
public bool SetupCEnabled { get; init; }
public bool SetupDEnabled { get; init; }
public bool PastCutoffC   { get; init; }
public bool PastCutoffD   { get; init; }
public bool StickyTgtC    { get; init; }
public bool StickyStpC    { get; init; }
public bool StickyTgtD    { get; init; }
public bool StickyStpD    { get; init; }

// ── FalseBreakout module context ──────────────────────────────────
public bool    FBOrbBreakoutActive      { get; init; }
public bool    FBSessionBreakoutActive  { get; init; }
public int     FBOrbBarsInBreakout      { get; init; }
public int     FBSessionBarsInBreakout  { get; init; }
public decimal FBOrbPenetrationDepth    { get; init; }
public decimal FBSessionPenetrationDepth { get; init; }
public bool    FBOrbActivated           { get; init; }
public bool    FBSessionActivated       { get; init; }
public bool    IsCompoundFakeout        { get; init; }

// ── Per-setup daily stats C/D ─────────────────────────────────────
public int     TodayWinsC    { get; init; }
public int     TodayLossesC  { get; init; }
public decimal TodayWinPnlC  { get; init; }
public decimal TodayLossPnlC { get; init; }
public int     TodayWinsD    { get; init; }
public int     TodayLossesD  { get; init; }
public decimal TodayWinPnlD  { get; init; }
public decimal TodayLossPnlD { get; init; }
```

- [ ] **Step 2: Build + run existing tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: Build succeeded, all 146 tests pass

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Models/Signals.cs
git commit -m "feat: add Setup C/D and FalseBreakout fields to EngineSnapshot"
```

---

## Chunk 3: Engine Integration (OrbStrategyEngine)

### Task 5: Engine Fields + Reset/Reconfigure

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

- [ ] **Step 1: Add C/D fields and FalseBreakoutDetector**

After `_trendDay` field (line 31), add:
```csharp
private readonly FalseBreakoutDetector _falseBreakout;
```

After Setup B fields block (line 116), add:
```csharp
// ── Manual force-exit flags C/D ──
private volatile bool _forceExitC = false;
private volatile bool _forceExitD = false;

// ── Setup C — ORB False Breakout ────────────────────────────────
private int      _stC          = 0;
private decimal  _entC         = 0;
private decimal  _stopC        = 0;
private decimal  _tgtC         = 0;
private decimal  _partialC     = 0;
private decimal  _initStopC    = 0;
private bool     _activeC      = false;
private int      _tradeCountC  = 0;
private bool     _partHitC     = false;
private decimal  _pnlC         = 0;
private decimal  _armEntryC    = 0;
private DateTime _entryTimeC   = DateTime.MinValue;
private bool    _stickyTgtC   = false;
private bool    _stickyStpC   = false;
private int     _exitBarIdxC  = -1;
private bool    _bullTradedC  = false;
private bool    _bearTradedC  = false;
private int     _ctsC         = 0;
private bool    _pastCutoffC  = false;

// ── Setup D — Session Range False Breakout ──────────────────────
private int      _stD          = 0;
private decimal  _entD         = 0;
private decimal  _stopD        = 0;
private decimal  _tgtD         = 0;
private decimal  _partialD     = 0;
private decimal  _initStopD    = 0;
private bool     _activeD      = false;
private int      _tradeCountD  = 0;
private bool     _partHitD     = false;
private decimal  _pnlD         = 0;
private decimal  _armEntryD    = 0;
private DateTime _entryTimeD   = DateTime.MinValue;
private bool    _stickyTgtD   = false;
private bool    _stickyStpD   = false;
private int     _exitBarIdxD  = -1;
private bool    _bullTradedD  = false;
private bool    _bearTradedD  = false;
private int     _ctsD         = 0;
private bool    _pastCutoffD  = false;

// Per-setup C/D daily stats
private int     _todayWinsC    = 0;
private int     _todayLossesC  = 0;
private decimal _todayWinPnlC  = 0;
private decimal _todayLossPnlC = 0;
private int     _todayWinsD    = 0;
private int     _todayLossesD  = 0;
private decimal _todayWinPnlD  = 0;
private decimal _todayLossPnlD = 0;
```

- [ ] **Step 2: Initialize FalseBreakoutDetector in constructor**

After `_trendDay = new TrendDayFilter(modCfg);` (line 158), add:
```csharp
_falseBreakout = new FalseBreakoutDetector(cfg);
```

- [ ] **Step 3: Add ResetSetupC/D and RequestForceExitC/D methods**

After `ResetSetupB()` (line ~1843), add:
```csharp
private void ResetSetupC()
{
    _stC = 0; _entC = 0; _stopC = 0; _tgtC = 0; _partialC = 0;
    _activeC = false; _tradeCountC = 0; _partHitC = false;
    _pnlC = 0; _armEntryC = 0; _entryTimeC = DateTime.MinValue;
    _stickyTgtC = false; _stickyStpC = false; _exitBarIdxC = -1;
    _bullTradedC = false; _bearTradedC = false; _ctsC = 0;
    _pastCutoffC = false;
}

private void ResetSetupD()
{
    _stD = 0; _entD = 0; _stopD = 0; _tgtD = 0; _partialD = 0;
    _activeD = false; _tradeCountD = 0; _partHitD = false;
    _pnlD = 0; _armEntryD = 0; _entryTimeD = DateTime.MinValue;
    _stickyTgtD = false; _stickyStpD = false; _exitBarIdxD = -1;
    _bullTradedD = false; _bearTradedD = false; _ctsD = 0;
    _pastCutoffD = false;
}
```

After `RequestForceExitB()` (line ~181), add:
```csharp
public async Task RequestForceExitC()
{
    var px = _prices.GetLastPrice(_cfg.Ticker);
    if (px <= 0) { _forceExitC = true; return; }
    var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
    await ForceExitC(bar);
    AddAlert("EXIT", SetupId.C, $"Force exit @ {px:F2}", "orange");
    await PublishSnapshot(bar);
}

public async Task RequestForceExitD()
{
    var px = _prices.GetLastPrice(_cfg.Ticker);
    if (px <= 0) { _forceExitD = true; return; }
    var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
    await ForceExitD(bar);
    AddAlert("EXIT", SetupId.D, $"Force exit @ {px:F2}", "orange");
    await PublishSnapshot(bar);
}
```

- [ ] **Step 4: Wire C/D into Reconfigure()**

In `Reconfigure()` method, after the existing A/B resets, add:
```csharp
_pastCutoffC = false; _tradeCountC = 0;
_todayWinsC = 0; _todayLossesC = 0; _todayWinPnlC = 0; _todayLossPnlC = 0;
_pastCutoffD = false; _tradeCountD = 0;
_todayWinsD = 0; _todayLossesD = 0; _todayWinPnlD = 0; _todayLossPnlD = 0;
_falseBreakout.Reconfigure(cfg);
```

- [ ] **Step 5: Wire C/D into ResetDaily()**

In `ResetDaily()`, after existing `ResetSetupB()`, add:
```csharp
ResetSetupC(); ResetSetupD();
_todayWinsC = 0; _todayLossesC = 0; _todayWinPnlC = 0; _todayLossPnlC = 0;
_todayWinsD = 0; _todayLossesD = 0; _todayWinPnlD = 0; _todayLossPnlD = 0;
_falseBreakout.NewSession(tradingDate);
```

- [ ] **Step 6: Wire C/D into ForceExitAllAsync()**

In `ForceExitAllAsync()`, after `ForceExitB(bar)`, add:
```csharp
if (_activeC) await ForceExitC(bar);
if (_activeD) await ForceExitD(bar);
```

And in the fallback path (no price), add:
```csharp
_forceExitC = true; _forceExitD = true;
```

- [ ] **Step 7: Wire C/D into ApplyFillPrice()**

Add cases to the switch:
```csharp
case SetupId.C:
    var deltaC = fillPrice - _entC;
    _entC = fillPrice;
    _stopC = LevelCalculator.RoundToTick(_stopC + deltaC, _cfg.TickSize);
    _tgtC  = LevelCalculator.RoundToTick(_tgtC  + deltaC, _cfg.TickSize);
    _partialC = LevelCalculator.RoundToTick(_partialC + deltaC, _cfg.TickSize);
    _initStopC = _stopC;
    await _executor.OnLevelsAdjustedAsync(SetupId.C, _stopC, _tgtC, _ctsC);
    break;
case SetupId.D:
    var deltaD = fillPrice - _entD;
    _entD = fillPrice;
    _stopD = LevelCalculator.RoundToTick(_stopD + deltaD, _cfg.TickSize);
    _tgtD  = LevelCalculator.RoundToTick(_tgtD  + deltaD, _cfg.TickSize);
    _partialD = LevelCalculator.RoundToTick(_partialD + deltaD, _cfg.TickSize);
    _initStopD = _stopD;
    await _executor.OnLevelsAdjustedAsync(SetupId.D, _stopD, _tgtD, _ctsD);
    break;
```

- [ ] **Step 8: Build to verify**

Run: `dotnet build --nologo -v q`
Expected: Build succeeded

- [ ] **Step 9: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: add C/D fields, reset, reconfigure, force-exit to engine"
```

### Task 6: ProcessSetupC/D + EvalTickSetupC/D + BookExitC/D + ForceExitC/D

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

- [ ] **Step 1: Add ProcessSetupC method**

After `ProcessSetupB` method. Follow the A arm pattern but with false breakout activation as the arm condition:

```csharp
private async Task ProcessSetupC(Bar bar)
{
    if (!_cfg.EnableC || _pastCutoffC) return;

    // Clear sticky markers from prior exit bar
    if (_exitBarIdxC >= 0 && _barIndex > _exitBarIdxC)
    { _stickyTgtC = false; _stickyStpC = false; _exitBarIdxC = -1; }

    // Force-exit flag check
    if (_forceExitC && _activeC)
    { await ForceExitC(bar); _forceExitC = false; return; }
    _forceExitC = false;

    if (_activeC) return; // trade management via ticks

    // Arm from false breakout detector
    if (_stC == 0 && _falseBreakout.OrbTracker.IsActivated)
    {
        if (_tradeCountC >= _cfg.MaxTradesC) return;
        // Direction: opposite of breakout
        bool isLong = _falseBreakout.OrbTracker.BreakoutDirection == Direction.Long
            ? false : true; // broke above → short fakeout → long entry; broke below → long fakeout → short entry
        // Wait — spec says: "short breakout → long arm". So:
        // BreakoutDirection.Long (broke above) → arm SHORT (fakeout reversal)
        // BreakoutDirection.Short (broke below) → arm LONG (fakeout reversal)

        if (isLong && _bullTradedC && !_cfg.AllowRearmAfterBeC) return;
        if (!isLong && _bearTradedC && !_cfg.AllowRearmAfterBeC) return;

        _stC = isLong ? 1 : -1;
        _armEntryC = bar.Close;
        _falseBreakout.OrbTracker.ClearActivation();
        AddAlert("ARM", SetupId.C, $"Armed {(isLong ? "Long" : "Short")} (ORB fakeout)", "cyan");
    }
}
```

- [ ] **Step 2: Add ProcessSetupD method** (same pattern, uses SessionRangeTracker)

```csharp
private async Task ProcessSetupD(Bar bar)
{
    if (!_cfg.EnableD || _pastCutoffD) return;

    if (_exitBarIdxD >= 0 && _barIndex > _exitBarIdxD)
    { _stickyTgtD = false; _stickyStpD = false; _exitBarIdxD = -1; }

    if (_forceExitD && _activeD)
    { await ForceExitD(bar); _forceExitD = false; return; }
    _forceExitD = false;

    if (_activeD) return;

    if (_stD == 0 && _falseBreakout.SessionRangeTracker.IsActivated)
    {
        if (_tradeCountD >= _cfg.MaxTradesD) return;
        bool isLong = _falseBreakout.SessionRangeTracker.BreakoutDirection != Direction.Long;

        if (isLong && _bullTradedD && !_cfg.AllowRearmAfterBeD) return;
        if (!isLong && _bearTradedD && !_cfg.AllowRearmAfterBeD) return;

        _stD = isLong ? 1 : -1;
        _armEntryD = bar.Close;
        _falseBreakout.SessionRangeTracker.ClearActivation();
        AddAlert("ARM", SetupId.D, $"Armed {(isLong ? "Long" : "Short")} (session fakeout)", "cyan");
    }
}
```

- [ ] **Step 3: Add EvalTickSetupC**

Follow the `EvalTickSetupA` pattern (lines 495-604) but with false breakout entry logic:

```csharp
private async Task EvalTickSetupC(decimal price, DateTime utcTime)
{
    decimal orbHigh  = _orb.OrbHigh;
    decimal orbLow   = _orb.OrbLow;
    decimal orbRange = _orb.OrbRange;

    // Entry: armed but not yet active
    if (!_activeC && (_stC == 1 || _stC == -1))
    {
        bool isLong = _stC == 1;
        // Long entry: tick crosses above ORB low (was false break below)
        // Short entry: tick crosses below ORB high (was false break above)
        bool triggerLong  = isLong  && price >= orbLow;
        bool triggerShort = !isLong && price <= orbHigh;

        if (triggerLong || triggerShort)
            await TryEntryCFromTick(isLong ? orbLow : orbHigh, isLong, orbRange, utcTime);
        return;
    }

    // Exit: active position
    if (!_activeC) return;
    bool long_ = _stC == 2;
    bool hitStop   = long_ ? price <= _stopC  : price >= _stopC;
    bool hitTarget = long_ ? price >= _tgtC   : price <= _tgtC;
    bool hitPartPx = _cfg.UsePartialC && !_partHitC &&
                     (long_ ? price >= _partialC : price <= _partialC);
    if (!hitStop && !hitTarget && !hitPartPx) return;

    bool partJustHit = false;
    if (hitPartPx && !hitTarget)
    {
        int half = CalcPartialCts(_ctsC, _cfg.PartialCtsC);
        int remaining = _ctsC - half;
        if (half > 0)
        {
            partJustHit = true;
            _partHitC = true;
            _pnlC += (long_ ? _partialC - _entC : _entC - _partialC) * _cfg.PointValue * half;
            var psig = new PartialSignal(SetupId.C, long_ ? Direction.Long : Direction.Short,
                _partialC, half, remaining, _entC, utcTime);
            await _executor.OnPartialSignalAsync(psig);
            await _sink.OnPartialAsync(psig);
            AddAlert("PARTIAL", SetupId.C, $"Partial @ {_partialC:F2}", "yellow");

            if (_cfg.UseBeC)
            {
                var besig = new BESignal(SetupId.C, long_ ? Direction.Long : Direction.Short,
                    _entC, _entC, remaining, utcTime);
                _stopC = _entC;
                await _executor.OnBESignalAsync(besig);
                await _sink.OnBEMoveAsync(besig);
                AddAlert("MOVE_BE", SetupId.C, $"Stop → BE {_entC:F2}", "yellow");
            }
            else
            {
                await _executor.OnLevelsAdjustedAsync(SetupId.C, _stopC, _tgtC, remaining);
            }
        }
        if (!hitTarget && !hitStop) return;
    }

    ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
    decimal exitPx = hitTarget ? _tgtC : _stopC;
    int remCts = (_partHitC && _cfg.UsePartialC)
        ? _ctsC - CalcPartialCts(_ctsC, _cfg.PartialCtsC) : _ctsC;
    decimal pnl = _pnlC + (long_ ? exitPx - _entC : _entC - exitPx) * _cfg.PointValue * remCts;
    _pnlC = pnl;
    _activeC = false; _stC = 0; _tradeCountC++;
    bool isBE = _cfg.AllowRearmAfterBeC && reason == ExitReason.Stop && exitPx == _entC;
    if (!isBE) { if (long_) _bullTradedC = true; else _bearTradedC = true; }
    if (hitTarget) { _stickyTgtC = true; _exitBarIdxC = _barIndex; }
    else           { _stickyStpC = true; _exitBarIdxC = _barIndex; }
    await BookExitC(reason, exitPx, utcTime, long_, sameBarPartial: partJustHit);
}

private async Task TryEntryCFromTick(decimal ep, bool isLong, decimal orbRange, DateTime utcTime)
{
    if (HasOpposingPosition(isLong)) return;

    // NearPct guard
    decimal nearDist = orbRange * _cfg.NearPctC;
    decimal refLevel = isLong ? _orb.OrbLow : _orb.OrbHigh;
    if (Math.Abs(ep - refLevel) > nearDist) return;

    if (_cfg.EntryTickOffsetC != 0 && _cfg.TickSize > 0)
    {
        decimal off = _cfg.EntryTickOffsetC * _cfg.TickSize;
        ep = LevelCalculator.RoundToTick(isLong ? ep + off : ep - off, _cfg.TickSize);
    }
    var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
        _cfg.StopPctC, _cfg.TargetPctC, _cfg.PartialPctC, orbRange, _cfg.TickSize);
    if (rr < _cfg.MinRrC) return;

    _entC = ep; _stopC = sl; _tgtC = tp; _partialC = pp;
    _initStopC = sl; _pnlC = 0; _activeC = true;
    _ctsC = CalcContracts(_cfg.ContractsC, _cfg.HiVolMultC, _cfg.MaxContractsC);
    _stC = isLong ? 2 : -2; _enteredThisBar = true;
    _entryTimeC = utcTime;

    var sig = new EntrySignal(SetupId.C, isLong ? Direction.Long : Direction.Short,
        ep, sl, tp, pp, _ctsC, utcTime, _cfg.OrderTypeC, _activeSessionId);
    var fillPx = await _executor.OnEntrySignalAsync(sig);
    if (fillPx.HasValue && fillPx.Value != ep) await ApplyFillPrice(SetupId.C, fillPx.Value);
    await _sink.OnEntryAsync(sig);
    AddAlert("ENTRY", SetupId.C, $"{(isLong ? "Long" : "Short")} @ {ep:F2}", isLong ? "green" : "red");
}
```

- [ ] **Step 4: Add EvalTickSetupD** (same pattern, uses session range for levels)

Same as EvalTickSetupC but using `_falseBreakout.SessionRangeTracker.RangeHigh/RangeLow` for range reference and `SetupId.D`.

- [ ] **Step 5: Add BookExitC/D and ForceExitC/D**

Follow the exact `BookExitA`/`ForceExitA` patterns (lines 1228-1563), replacing all `A` suffixed fields/calls with `C`/`D`. Key differences: `_todayWinsC`/`_todayLossesC` instead of A.

- [ ] **Step 6: Wire ProcessSetupC/D into ProcessBarInternalAsync**

After `await ProcessSetupB(bar, ...)` call (line ~949), add:
```csharp
await ProcessSetupC(bar);
await ProcessSetupD(bar);
```

Add cutoff checks for C/D alongside A/B cutoff checks (line ~900):
```csharp
var cutoffC = new TimeOnly(_cfg.CutoffHourC, _cfg.CutoffMinuteC);
if (localTime >= cutoffC && !_pastCutoffC)
{
    _pastCutoffC = true;
    if (!_activeC && (_stC == 1 || _stC == -1)) { _stC = 0; _armEntryC = 0; }
}
var cutoffD = new TimeOnly(_cfg.CutoffHourD, _cfg.CutoffMinuteD);
if (localTime >= cutoffD && !_pastCutoffD)
{
    _pastCutoffD = true;
    if (!_activeD && (_stD == 1 || _stD == -1)) { _stD = 0; _armEntryD = 0; }
}
```

- [ ] **Step 7: Wire EvalTickSetupC/D into ProcessPriceTickAsync**

After `EvalTickSetupB` call (line ~490), add:
```csharp
if (_cfg.EnableC && !_pastCutoffC) await EvalTickSetupC(price, utcTime);
if (_cfg.EnableD && !_pastCutoffD) await EvalTickSetupD(price, utcTime);
```

- [ ] **Step 8: Wire FalseBreakoutDetector.OnBar into ProcessBarInternalAsync**

After module updates (after `_sweepDetector.OnBar`, line ~861), add:
```csharp
_falseBreakout.OnBar(bar, tradingDate,
    _orb.OrbHigh, _orb.OrbLow, _orb.IsSet,
    _vwap.Value, _trendDay.BullScore, _trendDay.BearScore);
```

- [ ] **Step 9: Wire OnSessionStart into Reconfigure for session range snapshots**

In `Reconfigure()`, after existing module reconfigs, add logic to snapshot prior session range:
```csharp
// Snapshot prior session range for FalseBreakout detector
if (sessionId == SessionId.London)
    _falseBreakout.OnSessionStart(_sessionEngine.AsiaHigh, _sessionEngine.AsiaLow);
else if (sessionId == SessionId.NY)
    _falseBreakout.OnSessionStart(_sessionEngine.LondonHigh, _sessionEngine.LondonLow);
```

- [ ] **Step 10: Wire C/D into PublishSnapshot**

After the A/B snapshot fields, add C/D:
```csharp
SetupC = _activeC ? new ActiveTradeView { /* same pattern as A */ } : null,
SetupD = _activeD ? new ActiveTradeView { /* same pattern as D */ } : null,
TradeCountC = _tradeCountC, TradeCountD = _tradeCountD,
MaxTradesC = _cfg.MaxTradesC, MaxTradesD = _cfg.MaxTradesD,
SetupCState = _stC, SetupDState = _stD,
SetupCEnabled = _cfg.EnableC, SetupDEnabled = _cfg.EnableD,
PastCutoffC = _pastCutoffC, PastCutoffD = _pastCutoffD,
StickyTgtC = _stickyTgtC, StickyStpC = _stickyStpC,
StickyTgtD = _stickyTgtD, StickyStpD = _stickyStpD,
FBOrbBreakoutActive = _falseBreakout.OrbTracker.BreakoutActive,
FBSessionBreakoutActive = _falseBreakout.SessionRangeTracker.BreakoutActive,
FBOrbBarsInBreakout = _falseBreakout.OrbTracker.BarsInBreakout,
FBSessionBarsInBreakout = _falseBreakout.SessionRangeTracker.BarsInBreakout,
FBOrbPenetrationDepth = _falseBreakout.OrbTracker.PenetrationDepth,
FBSessionPenetrationDepth = _falseBreakout.SessionRangeTracker.PenetrationDepth,
FBOrbActivated = _falseBreakout.OrbTracker.IsActivated,
FBSessionActivated = _falseBreakout.SessionRangeTracker.IsActivated,
IsCompoundFakeout = _falseBreakout.IsCompoundFakeout,
TodayWinsC = _todayWinsC, TodayLossesC = _todayLossesC,
TodayWinPnlC = _todayWinPnlC, TodayLossPnlC = _todayLossPnlC,
TodayWinsD = _todayWinsD, TodayLossesD = _todayLossesD,
TodayWinPnlD = _todayWinPnlD, TodayLossPnlD = _todayLossPnlD,
```

- [ ] **Step 11: Build + run all tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 12: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: integrate Setup C/D into OrbStrategyEngine with full trade lifecycle"
```

---

## Chunk 4: Web Layer (Orchestrator, Dashboard, Settings, SignalR)

### Task 7: LiveEngineOrchestrator + Dashboard

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml`
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml.cs`
- Modify: `CRV.Web/wwwroot/js/crv-hub.js`

- [ ] **Step 1: Add ForceExitSetupC/D to orchestrator**

After `ForceExitSetupB()` (line ~52), add:
```csharp
public async Task ForceExitSetupC()
{
    using var _ = await _lifecycleLock.LockAsync();
    var eng = _engine;
    if (eng != null) await eng.RequestForceExitC();
}

public async Task ForceExitSetupD()
{
    using var _ = await _lifecycleLock.LockAsync();
    var eng = _engine;
    if (eng != null) await eng.RequestForceExitD();
}
```

- [ ] **Step 2: Add OnPostForceExitC/D to Dashboard code-behind**

After `OnPostForceExitB()` (line ~56), add:
```csharp
public async Task<IActionResult> OnPostForceExitC()
{
    await _orchestrator.ForceExitSetupC();
    return RedirectToPage();
}

public async Task<IActionResult> OnPostForceExitD()
{
    await _orchestrator.ForceExitSetupD();
    return RedirectToPage();
}
```

- [ ] **Step 3: Add C/D setup cards to Dashboard HTML**

Clone existing Setup A/B card markup for C and D. Update:
- Card headers: "Setup C — ORB Fakeout" / "Setup D — Session Fakeout"
- JS references: `updateSetup('C', d.SetupC, ...)` etc.
- Force exit form: `asp-page-handler="ForceExitC"` / `"ForceExitD"`

- [ ] **Step 4: Add False Breakout context card to Market Context area**

Add a card showing FB detector state from snapshot.

- [ ] **Step 5: Update crv-hub.js for C/D snapshot handling**

Add `updateSetup('C', ...)` and `updateSetup('D', ...)` calls in the snapshot handler. Add FB module context display updates.

- [ ] **Step 6: Build + verify pages load**

Run: `dotnet build --nologo -v q`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add CRV.Web/Services/LiveEngineOrchestrator.cs CRV.Web/Pages/Dashboard/ CRV.Web/wwwroot/js/crv-hub.js
git commit -m "feat: add Setup C/D to dashboard with force-exit and FB context card"
```

### Task 8: Settings Page

**Files:**
- Modify: `CRV.Web/Pages/Settings/Live.cshtml`
- Modify: `CRV.Web/Pages/Settings/Live.cshtml.cs`

- [ ] **Step 1: Add C/D form sections to session config tabs**

Clone the Setup A/B form sections. Update field names to `SetupC.*` / `SetupD.*`.

- [ ] **Step 2: Add FB module params to Market Context Modules area**

Add inputs for the 6 `FB*` params.

- [ ] **Step 3: Add C/D rows to Setups summary table**

- [ ] **Step 4: Handle C/D in session JSON deserialization (code-behind)**

Ensure `SetupConfigC`/`SetupConfigD` are included in `JsonSerializerOptions` and session save/load.

- [ ] **Step 5: Build + verify**

Run: `dotnet build --nologo -v q`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add CRV.Web/Pages/Settings/
git commit -m "feat: add Setup C/D config to Settings page"
```

---

## Chunk 5: EF Migration + Final Tests

### Task 9: EF Migration

- [ ] **Step 1: Add migration**

Run: `dotnet ef migrations add AddSetupCDAndFalseBreakout --project CRV.Core --startup-project CRV.Web`

- [ ] **Step 2: Apply migration**

Run: `dotnet ef database update --project CRV.Core --startup-project CRV.Web`

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Migrations/
git commit -m "feat: add EF migration for Setup C/D config columns"
```

### Task 10: Integration Tests

**Files:**
- Create: `CRV.Core.Tests/Strategy/FalseBreakoutIntegrationTests.cs`

- [ ] **Step 1: Write integration test — Setup C arms on detector activation**

Test that feeding bars through OrbStrategyEngine with a false breakout sequence results in Setup C arming.

- [ ] **Step 2: Write integration test — Setup C entry fires on tick recross**

Test that after arming, a price tick crossing back inside ORB fires an EntrySignal with SetupId.C.

- [ ] **Step 3: Write integration test — NearPct guard rejects distant entry**

- [ ] **Step 4: Write integration test — Full trade lifecycle (entry → partial → target)**

- [ ] **Step 5: Write integration test — Setup D with session range**

- [ ] **Step 6: Run all tests**

Run: `dotnet test --nologo -v q`
Expected: All pass (146 existing + new tests)

- [ ] **Step 7: Commit**

```bash
git add CRV.Core.Tests/Strategy/FalseBreakoutIntegrationTests.cs
git commit -m "test: add Setup C/D integration tests"
```

### Task 11: Final Build + Full Test Suite

- [ ] **Step 1: Full build**

Run: `dotnet build --nologo -v q`
Expected: 0 errors, 0 warnings

- [ ] **Step 2: Full test suite**

Run: `dotnet test --nologo -v q`
Expected: All tests pass

- [ ] **Step 3: Verify dashboard loads with C/D cards**

Start the web app and verify Setup C/D cards render, FB context card shows, settings page includes C/D sections.
