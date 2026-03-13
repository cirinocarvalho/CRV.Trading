# Trading Engine Modules Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add 5 analytical modules (Session Engine, Sweep Detector, VWAP Model, Opening Drive, Trend Day Filter) plus 3 composite trade setups (C/D/F) to OrbStrategyEngine using composition pattern.

**Architecture:** Each module implements `IEngineModule` and lives in `CRV.Core/Modules/`. The engine owns them as fields, calling `NewSession()`, `OnBar()`, `OnTick()` at the appropriate points. A `CompositeSetupEngine` reads all module outputs to evaluate setups C/D/F. Historical levels (PDH/PDL/PWH/PWL/PMH/PML) are seeded from broker REST daily bars at engine startup.

**Tech Stack:** C# / .NET 10, xUnit, existing CRV.Core patterns (immutable Bar record, AtrIndicator/VwapIndicator style)

**Design Doc:** `docs/plans/2026-03-12-trading-modules-design.md`

---

## Task 1: IEngineModule Interface + Module Config

**Files:**
- Create: `CRV.Core/Modules/IEngineModule.cs`
- Create: `CRV.Core/Modules/ModuleConfig.cs`

**Step 1: Create the interface**

```csharp
// CRV.Core/Modules/IEngineModule.cs
using CRV.Core.Models;

namespace CRV.Core.Modules;

public interface IEngineModule
{
    void OnBar(Bar bar, DateTime tradingDate);
    void OnTick(decimal price, DateTime utcTime);
    void NewSession(DateTime tradingDate);
}
```

**Step 2: Create module configuration**

```csharp
// CRV.Core/Modules/ModuleConfig.cs
namespace CRV.Core.Modules;

public class ModuleConfig
{
    // Session times (Eastern Time hours/minutes)
    public TimeOnly AsiaStart    { get; set; } = new(18, 0);
    public TimeOnly AsiaEnd      { get; set; } = new(0, 0);
    public TimeOnly LondonStart  { get; set; } = new(3, 0);
    public TimeOnly LondonEnd    { get; set; } = new(8, 30);
    public TimeOnly NYOpenStart  { get; set; } = new(9, 30);
    public TimeOnly NYOpenEnd    { get; set; } = new(11, 30);
    public TimeOnly MiddayStart  { get; set; } = new(11, 30);
    public TimeOnly MiddayEnd    { get; set; } = new(13, 30);
    public TimeOnly PowerStart   { get; set; } = new(15, 0);
    public TimeOnly PowerEnd     { get; set; } = new(16, 0);

    // Sweep detector
    public decimal MinTickPenetration  { get; set; } = 0.50m;  // tickSize * 2
    public decimal MinBodyReject       { get; set; } = 1.00m;  // tickSize * 4
    public decimal EqualLevelTolerance { get; set; } = 2.00m;  // tickSize * 8
    public int     ConfirmationBars    { get; set; } = 1;

    // Opening drive
    public decimal DriveRangeAtrMult   { get; set; } = 0.80m;
    public decimal MaxDrivePullback    { get; set; } = 0.35m;
    public int     DriveBullBearRatio  { get; set; } = 2;

    // Trend day
    public int     TrendDayThreshold   { get; set; } = 4;  // score >= this = trend day
    public decimal ShallowPullbackMax  { get; set; } = 0.35m;

    // VWAP
    public int     VwapDevPeriod       { get; set; } = 20;

    // Instrument (set from StrategyConfig)
    public decimal TickSize   { get; set; } = 0.25m;
    public decimal PointValue { get; set; } = 20m;
    public string  Timezone   { get; set; } = "America/New_York";
}
```

**Step 3: Commit**

```bash
git add CRV.Core/Modules/
git commit -m "feat: add IEngineModule interface and ModuleConfig"
```

---

## Task 2: Session Engine — Core

**Files:**
- Create: `CRV.Core/Modules/SessionEngine.cs`
- Create: `CRV.Core.Tests/Modules/SessionEngineTests.cs`

**Step 1: Write failing tests**

```csharp
// CRV.Core.Tests/Modules/SessionEngineTests.cs
using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

public class SessionEngineTests
{
    static readonly TimeZoneInfo ET = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    static SessionEngine Build(ModuleConfig? cfg = null)
        => new(cfg ?? new ModuleConfig(), ET);

    static Bar MakeBar(int year, int month, int day, int hour, int min, decimal close,
                       decimal high = 0, decimal low = 0)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, min, 0), ET);
        if (high == 0) high = close + 1;
        if (low == 0) low = close - 1;
        return new Bar(utc, close, high, low, close, 100);
    }

    [Fact]
    public void DetectsAsiaSession()
    {
        var se = Build();
        var bar = MakeBar(2026, 3, 12, 20, 0, 24800);  // 8 PM ET = Asia
        se.NewSession(new DateTime(2026, 3, 12));
        se.OnBar(bar, new DateTime(2026, 3, 12));
        Assert.Equal(SessionType.Asia, se.CurrentSession);
    }

    [Fact]
    public void DetectsLondonSession()
    {
        var se = Build();
        se.NewSession(new DateTime(2026, 3, 12));
        var bar = MakeBar(2026, 3, 12, 5, 0, 24800);
        se.OnBar(bar, new DateTime(2026, 3, 12));
        Assert.Equal(SessionType.London, se.CurrentSession);
    }

    [Fact]
    public void DetectsNYOpenSession()
    {
        var se = Build();
        se.NewSession(new DateTime(2026, 3, 12));
        var bar = MakeBar(2026, 3, 12, 10, 0, 24800);
        se.OnBar(bar, new DateTime(2026, 3, 12));
        Assert.Equal(SessionType.NYOpen, se.CurrentSession);
    }

    [Fact]
    public void TracksSessionHighLow()
    {
        var se = Build();
        se.NewSession(new DateTime(2026, 3, 12));
        se.OnBar(MakeBar(2026, 3, 12, 20, 0, 24800, high: 24850, low: 24750), new DateTime(2026, 3, 12));
        se.OnBar(MakeBar(2026, 3, 12, 20, 1, 24820, high: 24900, low: 24780), new DateTime(2026, 3, 12));

        Assert.Equal(24900, se.SessionHigh);
        Assert.Equal(24750, se.SessionLow);
    }

    [Fact]
    public void TracksAsiaHighLow()
    {
        var se = Build();
        se.NewSession(new DateTime(2026, 3, 12));
        se.OnBar(MakeBar(2026, 3, 12, 20, 0, 24800, high: 24850, low: 24750), new DateTime(2026, 3, 12));
        se.OnBar(MakeBar(2026, 3, 12, 21, 0, 24820, high: 24900, low: 24780), new DateTime(2026, 3, 12));

        Assert.Equal(24900, se.AsiaHigh);
        Assert.Equal(24750, se.AsiaLow);
        Assert.Equal(24825, se.AsiaMid);
        Assert.Equal(150, se.AsiaRange);
    }

    [Fact]
    public void DetectsAsiaCompressed()
    {
        var se = Build();
        se.CurrentAtr = 200;  // ATR = 200, Asia range < 200 * 1.2 = 240
        se.NewSession(new DateTime(2026, 3, 12));
        se.OnBar(MakeBar(2026, 3, 12, 20, 0, 24800, high: 24850, low: 24750), new DateTime(2026, 3, 12));
        // range = 100, ATR * 1.2 = 240 → compressed
        Assert.True(se.AsiaCompressed);
    }

    [Fact]
    public void NewSessionResetsState()
    {
        var se = Build();
        se.NewSession(new DateTime(2026, 3, 12));
        se.OnBar(MakeBar(2026, 3, 12, 20, 0, 24800, high: 24900, low: 24700), new DateTime(2026, 3, 12));
        Assert.Equal(24900, se.SessionHigh);

        se.NewSession(new DateTime(2026, 3, 13));
        Assert.Equal(0, se.SessionHigh);
        Assert.Equal(decimal.MaxValue, se.SessionLow);
    }

    [Fact]
    public void OnTickUpdatesSessionHighLow()
    {
        var se = Build();
        se.NewSession(new DateTime(2026, 3, 12));
        se.OnBar(MakeBar(2026, 3, 12, 20, 0, 24800, high: 24850, low: 24750), new DateTime(2026, 3, 12));

        var utcTime = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 3, 12, 20, 0, 30), ET);
        se.OnTick(24950, utcTime);
        Assert.Equal(24950, se.SessionHigh);
    }

    [Fact]
    public void SeedHistorySetsLevels()
    {
        var se = Build();
        var dailyBars = new[]
        {
            new Bar(new DateTime(2026, 3, 10), 24700, 24900, 24600, 24800, 50000),
            new Bar(new DateTime(2026, 3, 11), 24800, 25000, 24700, 24900, 60000),
        };
        se.SeedHistory(dailyBars);
        Assert.Equal(25000, se.PrevDayHigh);
        Assert.Equal(24700, se.PrevDayLow);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SessionEngineTests" -v q`
Expected: FAIL (SessionEngine class doesn't exist)

**Step 3: Implement SessionEngine**

```csharp
// CRV.Core/Modules/SessionEngine.cs
using CRV.Core.Models;

namespace CRV.Core.Modules;

public enum SessionType { PreMarket, Asia, London, NYOpen, Midday, PowerHour }

public class SessionEngine : IEngineModule
{
    private readonly ModuleConfig _cfg;
    private readonly TimeZoneInfo _tz;

    // Current session
    public SessionType CurrentSession { get; private set; } = SessionType.PreMarket;

    // Full day
    public decimal SessionHigh { get; private set; }
    public decimal SessionLow  { get; private set; } = decimal.MaxValue;

    // Asia
    public decimal AsiaHigh { get; private set; }
    public decimal AsiaLow  { get; private set; } = decimal.MaxValue;
    public decimal AsiaMid  => AsiaHigh > 0 && AsiaLow < decimal.MaxValue
        ? (AsiaHigh + AsiaLow) / 2 : 0;
    public decimal AsiaRange => AsiaHigh > 0 && AsiaLow < decimal.MaxValue
        ? AsiaHigh - AsiaLow : 0;
    public bool AsiaCompressed => AsiaRange > 0 && CurrentAtr > 0
        && AsiaRange < CurrentAtr * 1.2m;

    // London
    public decimal LondonHigh { get; private set; }
    public decimal LondonLow  { get; private set; } = decimal.MaxValue;

    // NY
    public decimal NYHigh { get; private set; }
    public decimal NYLow  { get; private set; } = decimal.MaxValue;

    // Historical levels
    public decimal PrevDayHigh   { get; private set; }
    public decimal PrevDayLow    { get; private set; }
    public decimal PrevWeekHigh  { get; private set; }
    public decimal PrevWeekLow   { get; private set; }
    public decimal PrevMonthHigh { get; private set; }
    public decimal PrevMonthLow  { get; private set; }

    // Cross-session signals
    public bool LondonSweptAsiaHigh { get; private set; }
    public bool LondonSweptAsiaLow  { get; private set; }
    public bool NYBullExpansion     { get; private set; }
    public bool NYBearExpansion     { get; private set; }

    // Bias
    public int SessionBias { get; private set; }  // -1, 0, +1

    // Set by engine before calling OnBar
    public decimal CurrentAtr  { get; set; }
    public decimal CurrentVwap { get; set; }

    public SessionEngine(ModuleConfig cfg, TimeZoneInfo tz)
    {
        _cfg = cfg;
        _tz  = tz;
    }

    public void SeedHistory(IReadOnlyList<Bar> dailyBars)
    {
        if (dailyBars.Count == 0) return;

        // Most recent bar = previous day
        var last = dailyBars[^1];
        PrevDayHigh = last.High;
        PrevDayLow  = last.Low;

        // Previous week: last 5 trading days
        var weekBars = dailyBars.TakeLast(Math.Min(5, dailyBars.Count)).ToList();
        PrevWeekHigh = weekBars.Max(b => b.High);
        PrevWeekLow  = weekBars.Min(b => b.Low);

        // Previous month: last 20 trading days
        var monthBars = dailyBars.TakeLast(Math.Min(20, dailyBars.Count)).ToList();
        PrevMonthHigh = monthBars.Max(b => b.High);
        PrevMonthLow  = monthBars.Min(b => b.Low);
    }

    public void NewSession(DateTime tradingDate)
    {
        // Roll current day into previous (only if we had data)
        if (SessionHigh > 0 && SessionLow < decimal.MaxValue)
        {
            PrevDayHigh = SessionHigh;
            PrevDayLow  = SessionLow;
        }

        SessionHigh = 0;
        SessionLow  = decimal.MaxValue;
        AsiaHigh = 0;  AsiaLow = decimal.MaxValue;
        LondonHigh = 0; LondonLow = decimal.MaxValue;
        NYHigh = 0; NYLow = decimal.MaxValue;
        LondonSweptAsiaHigh = false;
        LondonSweptAsiaLow  = false;
        NYBullExpansion = false;
        NYBearExpansion = false;
        SessionBias = 0;
        CurrentSession = SessionType.PreMarket;
    }

    public void OnBar(Bar bar, DateTime tradingDate)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, _tz);
        var localTime = TimeOnly.FromDateTime(local);

        // Update session detection
        CurrentSession = ClassifySession(localTime);

        // Update session-level high/low
        if (bar.High > SessionHigh) SessionHigh = bar.High;
        if (bar.Low  < SessionLow)  SessionLow  = bar.Low;

        // Update per-session high/low
        switch (CurrentSession)
        {
            case SessionType.Asia:
                if (bar.High > AsiaHigh) AsiaHigh = bar.High;
                if (bar.Low  < AsiaLow)  AsiaLow  = bar.Low;
                break;
            case SessionType.London:
                if (bar.High > LondonHigh) LondonHigh = bar.High;
                if (bar.Low  < LondonLow)  LondonLow  = bar.Low;
                // London sweep of Asia levels
                if (AsiaHigh > 0 && bar.High > AsiaHigh && bar.Close < AsiaHigh)
                    LondonSweptAsiaHigh = true;
                if (AsiaLow < decimal.MaxValue && bar.Low < AsiaLow && bar.Close > AsiaLow)
                    LondonSweptAsiaLow = true;
                break;
            case SessionType.NYOpen:
                if (bar.High > NYHigh) NYHigh = bar.High;
                if (bar.Low  < NYLow)  NYLow  = bar.Low;
                // NY expansion
                if (AsiaHigh > 0 && bar.Close > AsiaHigh && CurrentVwap > 0 && bar.Close > CurrentVwap)
                    NYBullExpansion = true;
                if (AsiaLow < decimal.MaxValue && bar.Close < AsiaLow && CurrentVwap > 0 && bar.Close < CurrentVwap)
                    NYBearExpansion = true;
                break;
        }

        // Session bias
        if (AsiaMid > 0)
            SessionBias = bar.Close > AsiaMid ? 1 : bar.Close < AsiaMid ? -1 : 0;
    }

    public void OnTick(decimal price, DateTime utcTime)
    {
        if (price > SessionHigh) SessionHigh = price;
        if (price < SessionLow)  SessionLow  = price;

        var local = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _tz);
        var localTime = TimeOnly.FromDateTime(local);
        var session = ClassifySession(localTime);

        switch (session)
        {
            case SessionType.Asia:
                if (price > AsiaHigh) AsiaHigh = price;
                if (price < AsiaLow)  AsiaLow  = price;
                break;
            case SessionType.London:
                if (price > LondonHigh) LondonHigh = price;
                if (price < LondonLow)  LondonLow  = price;
                break;
            case SessionType.NYOpen:
                if (price > NYHigh) NYHigh = price;
                if (price < NYLow)  NYLow  = price;
                break;
        }
    }

    private SessionType ClassifySession(TimeOnly t)
    {
        // Asia wraps midnight: 18:00-00:00
        if (t >= _cfg.AsiaStart || t < _cfg.AsiaEnd)
            return SessionType.Asia;
        if (t >= _cfg.LondonStart && t < _cfg.LondonEnd)
            return SessionType.London;
        if (t >= _cfg.NYOpenStart && t < _cfg.NYOpenEnd)
            return SessionType.NYOpen;
        if (t >= _cfg.MiddayStart && t < _cfg.MiddayEnd)
            return SessionType.Midday;
        if (t >= _cfg.PowerStart && t < _cfg.PowerEnd)
            return SessionType.PowerHour;
        return SessionType.PreMarket;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~SessionEngineTests" -v q`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add CRV.Core/Modules/SessionEngine.cs CRV.Core.Tests/Modules/SessionEngineTests.cs
git commit -m "feat: add SessionEngine module with session detection, level tracking, and cross-session signals"
```

---

## Task 3: VWAP Model

**Files:**
- Create: `CRV.Core/Modules/VwapModel.cs`
- Create: `CRV.Core.Tests/Modules/VwapModelTests.cs`

**Step 1: Write failing tests**

```csharp
// CRV.Core.Tests/Modules/VwapModelTests.cs
using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

public class VwapModelTests
{
    static VwapModel Build() => new(new ModuleConfig());

    static Bar MakeBar(decimal open, decimal high, decimal low, decimal close, long vol = 1000)
        => new(DateTime.UtcNow, open, high, low, close, vol);

    [Fact]
    public void ComputesVwapFromBars()
    {
        var vm = Build();
        vm.NewSession(DateTime.Today);
        vm.OnBar(MakeBar(100, 105, 95, 102, 1000), DateTime.Today);
        Assert.True(vm.Vwap > 0);
    }

    [Fact]
    public void ComputesDeviationBands()
    {
        var vm = Build();
        vm.NewSession(DateTime.Today);
        // Feed enough bars to compute standard deviation
        for (int i = 0; i < 25; i++)
        {
            decimal c = 100 + i * 0.5m;
            vm.OnBar(MakeBar(c - 1, c + 2, c - 2, c, 1000), DateTime.Today);
        }
        Assert.True(vm.Upper1 > vm.Vwap);
        Assert.True(vm.Upper2 > vm.Upper1);
        Assert.True(vm.Lower1 < vm.Vwap);
        Assert.True(vm.Lower2 < vm.Lower1);
    }

    [Fact]
    public void ClassifiesState_BullAccept()
    {
        var vm = Build();
        vm.NewSession(DateTime.Today);
        // Feed bars all above a baseline to get VWAP, then a bar with low >= VWAP
        for (int i = 0; i < 5; i++)
            vm.OnBar(MakeBar(100, 102, 99, 101, 1000), DateTime.Today);
        // Bar with close > VWAP and low >= VWAP
        var vwap = vm.Vwap;
        vm.OnBar(MakeBar(vwap + 1, vwap + 5, vwap, vwap + 3, 1000), DateTime.Today);
        Assert.True(vm.VwapState >= 1);  // AcceptBull or ExtendedBull
    }

    [Fact]
    public void DetectsReclaim()
    {
        var vm = Build();
        vm.NewSession(DateTime.Today);
        // Bars below then above VWAP
        for (int i = 0; i < 5; i++)
            vm.OnBar(MakeBar(100, 101, 99, 100, 1000), DateTime.Today);
        var vwap = vm.Vwap;
        // Bar closes below
        vm.OnBar(MakeBar(vwap - 1, vwap, vwap - 3, vwap - 2, 1000), DateTime.Today);
        Assert.False(vm.BullVWAPReclaim);
        // Bar closes above
        vm.OnBar(MakeBar(vwap - 1, vwap + 3, vwap - 1, vwap + 2, 1000), DateTime.Today);
        Assert.True(vm.BullVWAPReclaim);
    }

    [Fact]
    public void NewSessionResets()
    {
        var vm = Build();
        vm.NewSession(DateTime.Today);
        vm.OnBar(MakeBar(100, 105, 95, 102, 1000), DateTime.Today);
        Assert.True(vm.Vwap > 0);
        vm.NewSession(DateTime.Today.AddDays(1));
        Assert.Equal(0, vm.Vwap);
        Assert.Equal(0, vm.VwapState);
    }
}
```

**Step 2: Run tests — expected FAIL**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~VwapModelTests" -v q`

**Step 3: Implement VwapModel**

```csharp
// CRV.Core/Modules/VwapModel.cs
using CRV.Core.Models;
using CRV.Core.Indicators;

namespace CRV.Core.Modules;

public class VwapModel : IEngineModule
{
    private readonly ModuleConfig _cfg;
    private readonly VwapIndicator _vwap = new();
    private readonly Queue<decimal> _hlc3s = new();  // for std dev

    // Bands
    public decimal Vwap   => _vwap.Value;
    public decimal Upper1 { get; private set; }
    public decimal Upper2 { get; private set; }
    public decimal Lower1 { get; private set; }
    public decimal Lower2 { get; private set; }

    // State
    public int VwapState { get; private set; }  // -2 to +2

    // Signals
    public bool BullVWAPReclaim     { get; private set; }
    public bool BearVWAPReject      { get; private set; }
    public bool VWAPReversionLong   { get; private set; }
    public bool VWAPReversionShort  { get; private set; }
    public bool BullVWAPPullback    { get; private set; }
    public bool BearVWAPPullback    { get; private set; }

    // State for cross detection
    private bool _prevCloseAboveVwap;

    // External inputs (set by engine before OnBar)
    public bool TrendDayBull { get; set; }
    public bool TrendDayBear { get; set; }

    public VwapModel(ModuleConfig cfg) => _cfg = cfg;

    public void NewSession(DateTime tradingDate)
    {
        _vwap.NewSession(tradingDate);
        _hlc3s.Clear();
        Upper1 = Upper2 = Lower1 = Lower2 = 0;
        VwapState = 0;
        BullVWAPReclaim = BearVWAPReject = false;
        VWAPReversionLong = VWAPReversionShort = false;
        BullVWAPPullback = BearVWAPPullback = false;
        _prevCloseAboveVwap = false;
    }

    public void OnBar(Bar bar, DateTime tradingDate)
    {
        _vwap.Update(bar, tradingDate);
        if (!_vwap.IsReady) return;

        var vwap = _vwap.Value;

        // Track HLC3 for std dev
        _hlc3s.Enqueue(bar.HLC3);
        if (_hlc3s.Count > _cfg.VwapDevPeriod)
            _hlc3s.Dequeue();

        // Compute std dev bands
        if (_hlc3s.Count >= 2)
        {
            decimal mean = _hlc3s.Average();
            decimal sumSq = _hlc3s.Sum(x => (x - mean) * (x - mean));
            decimal stdDev = (decimal)Math.Sqrt((double)(sumSq / _hlc3s.Count));
            Upper1 = vwap + stdDev;
            Upper2 = vwap + stdDev * 2;
            Lower1 = vwap - stdDev;
            Lower2 = vwap - stdDev * 2;
        }

        // State classification
        bool closeAbove = bar.Close > vwap;
        bool closeBelow = bar.Close < vwap;

        if (Upper2 > 0 && bar.Close > Upper2)
            VwapState = 2;   // ExtendedBull
        else if (Lower2 > 0 && bar.Close < Lower2)
            VwapState = -2;  // ExtendedBear
        else if (closeAbove && bar.Low >= vwap)
            VwapState = 1;   // AcceptBull
        else if (closeBelow && bar.High <= vwap)
            VwapState = -1;  // AcceptBear
        else
            VwapState = 0;   // Neutral

        // Cross signals
        BullVWAPReclaim = closeAbove && !_prevCloseAboveVwap;
        BearVWAPReject  = closeBelow && _prevCloseAboveVwap;
        _prevCloseAboveVwap = closeAbove;

        // Close position relative to bar range
        decimal barRange = bar.High - bar.Low;
        bool closeNearHigh = barRange > 0 && (bar.High - bar.Close) <= barRange * 0.25m;
        bool closeNearLow  = barRange > 0 && (bar.Close - bar.Low) <= barRange * 0.25m;

        // Reversion signals
        VWAPReversionLong  = Lower2 > 0 && bar.Close < Lower2 && bar.Close > bar.Open && closeNearHigh;
        VWAPReversionShort = Upper2 > 0 && bar.Close > Upper2 && bar.Close < bar.Open && closeNearLow;

        // Pullback continuation
        BullVWAPPullback = TrendDayBull && bar.Low <= Upper1 && bar.Low >= vwap && closeAbove;
        BearVWAPPullback = TrendDayBear && bar.High >= Lower1 && bar.High <= vwap && closeBelow;
    }

    public void OnTick(decimal price, DateTime utcTime)
    {
        // VWAP only updates on bar close (needs volume)
    }
}
```

**Step 4: Run tests — expected PASS**

Run: `dotnet test CRV.Core.Tests --filter "FullyQualifiedName~VwapModelTests" -v q`

**Step 5: Commit**

```bash
git add CRV.Core/Modules/VwapModel.cs CRV.Core.Tests/Modules/VwapModelTests.cs
git commit -m "feat: add VwapModel with deviation bands, state classification, and setup signals"
```

---

## Task 4: Sweep Detector

**Files:**
- Create: `CRV.Core/Modules/SweepDetector.cs`
- Create: `CRV.Core.Tests/Modules/SweepDetectorTests.cs`

**Step 1: Write failing tests**

```csharp
// CRV.Core.Tests/Modules/SweepDetectorTests.cs
using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

public class SweepDetectorTests
{
    static SweepDetector Build(ModuleConfig? cfg = null)
        => new(cfg ?? new ModuleConfig { MinTickPenetration = 0.50m, MinBodyReject = 1.00m, EqualLevelTolerance = 2.00m });

    [Fact]
    public void DetectsBearishSweepOfPDH()
    {
        var sd = Build();
        sd.SetLevels(pdh: 25000, pdl: 24800);
        // Bar sweeps PDH: high above by > minTickPen, close below, valid wick
        var bar = new Bar(DateTime.UtcNow, 24990, 25005, 24980, 24995, 1000);
        // high=25005, PDH=25000, pen=5 > 0.50 ✓, close=24995 < 25000 ✓
        // wick = 25005 - max(24990,24995) = 10 >= 1.00 ✓
        sd.OnBar(bar, DateTime.Today);
        Assert.True(sd.AnyBearSweep);
        Assert.Contains(sd.ActiveSweeps, s => s.Level == 25000 && s.Type == SweepType.PDH);
    }

    [Fact]
    public void DetectsBullishSweepOfPDL()
    {
        var sd = Build();
        sd.SetLevels(pdh: 25000, pdl: 24800);
        // Bar sweeps PDL: low below by > minTickPen, close above, valid wick
        var bar = new Bar(DateTime.UtcNow, 24810, 24820, 24795, 24805, 1000);
        sd.OnBar(bar, DateTime.Today);
        Assert.True(sd.AnyBullSweep);
    }

    [Fact]
    public void NoSweepWhenPenetrationTooSmall()
    {
        var sd = Build();
        sd.SetLevels(pdh: 25000, pdl: 24800);
        // high = 25000.25 — pen = 0.25 < 0.50 minTickPen
        var bar = new Bar(DateTime.UtcNow, 24990, 25000.25m, 24980, 24995, 1000);
        sd.OnBar(bar, DateTime.Today);
        Assert.False(sd.AnyBearSweep);
    }

    [Fact]
    public void NoSweepWhenWickTooSmall()
    {
        var sd = Build();
        sd.SetLevels(pdh: 25000, pdl: 24800);
        // high = 25005 but close = 25004.50 → wick = 0.50 < 1.00 minBodyReject
        var bar = new Bar(DateTime.UtcNow, 25003, 25005, 24999, 25004.50m, 1000);
        sd.OnBar(bar, DateTime.Today);
        Assert.False(sd.AnyBearSweep);
    }

    [Fact]
    public void DetectsEqualHighs()
    {
        var sd = Build(new ModuleConfig { EqualLevelTolerance = 2.00m, MinTickPenetration = 0.50m, MinBodyReject = 1.00m });
        // Two bars with highs within tolerance
        var bar1 = new Bar(DateTime.UtcNow.AddMinutes(-2), 100, 105, 99, 103, 1000);
        var bar2 = new Bar(DateTime.UtcNow.AddMinutes(-1), 101, 105.5m, 100, 102, 1000);
        sd.OnBar(bar1, DateTime.Today);
        sd.OnBar(bar2, DateTime.Today);
        // Equal highs at ~105: sweep bar
        var sweepBar = new Bar(DateTime.UtcNow, 104, 108, 103, 104, 1000);
        sd.OnBar(sweepBar, DateTime.Today);
        Assert.True(sd.AnyBearSweep);
    }

    [Fact]
    public void SweepsResetOnNewSession()
    {
        var sd = Build();
        sd.SetLevels(pdh: 25000, pdl: 24800);
        var bar = new Bar(DateTime.UtcNow, 24990, 25005, 24980, 24995, 1000);
        sd.OnBar(bar, DateTime.Today);
        Assert.True(sd.AnyBearSweep);

        sd.NewSession(DateTime.Today.AddDays(1));
        Assert.False(sd.AnyBearSweep);
        Assert.Empty(sd.ActiveSweeps);
    }
}
```

**Step 2: Run tests — expected FAIL**

**Step 3: Implement SweepDetector**

```csharp
// CRV.Core/Modules/SweepDetector.cs
using CRV.Core.Models;

namespace CRV.Core.Modules;

public enum SweepType { PDH, PDL, PWH, PWL, PMH, PML, SessionHigh, SessionLow, OrbHigh, OrbLow, EqualHigh, EqualLow }
public enum SweepDirection { Bull, Bear }

public record SweepEvent(decimal Level, SweepType Type, SweepDirection Direction, DateTime Time);

public class SweepDetector : IEngineModule
{
    private readonly ModuleConfig _cfg;
    private readonly List<Bar> _recentBars = new();
    private const int MaxRecentBars = 5;

    // Levels (set externally before OnBar)
    private decimal _pdh, _pdl, _pwh, _pwl, _pmh, _pml;
    private decimal _sessionHigh, _sessionLow;
    private decimal _orbHigh, _orbLow;

    // Outputs
    public List<SweepEvent> ActiveSweeps { get; } = new();
    public bool AnyBullSweep => ActiveSweeps.Any(s => s.Direction == SweepDirection.Bull);
    public bool AnyBearSweep => ActiveSweeps.Any(s => s.Direction == SweepDirection.Bear);
    public SweepEvent? LastSweep => ActiveSweeps.LastOrDefault();

    // Equal high/low levels
    private readonly List<(decimal level, SweepType type)> _equalLevels = new();

    public SweepDetector(ModuleConfig cfg) => _cfg = cfg;

    public void SetLevels(decimal pdh = 0, decimal pdl = 0, decimal pwh = 0, decimal pwl = 0,
                          decimal pmh = 0, decimal pml = 0, decimal sessionHigh = 0, decimal sessionLow = 0,
                          decimal orbHigh = 0, decimal orbLow = 0)
    {
        _pdh = pdh; _pdl = pdl; _pwh = pwh; _pwl = pwl;
        _pmh = pmh; _pml = pml; _sessionHigh = sessionHigh; _sessionLow = sessionLow;
        _orbHigh = orbHigh; _orbLow = orbLow;
    }

    public void NewSession(DateTime tradingDate)
    {
        ActiveSweeps.Clear();
        _recentBars.Clear();
        _equalLevels.Clear();
    }

    public void OnBar(Bar bar, DateTime tradingDate)
    {
        // Detect equal highs/lows from recent bars
        if (_recentBars.Count >= 2)
        {
            var prev1 = _recentBars[^1];
            var prev2 = _recentBars[^2];
            if (Math.Abs(prev1.High - prev2.High) <= _cfg.EqualLevelTolerance)
            {
                var eqLevel = Math.Max(prev1.High, prev2.High);
                if (!_equalLevels.Any(e => e.type == SweepType.EqualHigh && Math.Abs(e.level - eqLevel) <= _cfg.EqualLevelTolerance))
                    _equalLevels.Add((eqLevel, SweepType.EqualHigh));
            }
            if (Math.Abs(prev1.Low - prev2.Low) <= _cfg.EqualLevelTolerance)
            {
                var eqLevel = Math.Min(prev1.Low, prev2.Low);
                if (!_equalLevels.Any(e => e.type == SweepType.EqualLow && Math.Abs(e.level - eqLevel) <= _cfg.EqualLevelTolerance))
                    _equalLevels.Add((eqLevel, SweepType.EqualLow));
            }
        }

        // Check all levels for sweeps
        CheckBearSweep(bar, _pdh, SweepType.PDH);
        CheckBearSweep(bar, _pwh, SweepType.PWH);
        CheckBearSweep(bar, _pmh, SweepType.PMH);
        CheckBearSweep(bar, _sessionHigh, SweepType.SessionHigh);
        CheckBearSweep(bar, _orbHigh, SweepType.OrbHigh);

        CheckBullSweep(bar, _pdl, SweepType.PDL);
        CheckBullSweep(bar, _pwl, SweepType.PWL);
        CheckBullSweep(bar, _pml, SweepType.PML);
        CheckBullSweep(bar, _sessionLow, SweepType.SessionLow);
        CheckBullSweep(bar, _orbLow, SweepType.OrbLow);

        // Equal levels
        foreach (var (level, type) in _equalLevels)
        {
            if (type == SweepType.EqualHigh)
                CheckBearSweep(bar, level, SweepType.EqualHigh);
            else
                CheckBullSweep(bar, level, SweepType.EqualLow);
        }

        // Keep recent bars for equal detection
        _recentBars.Add(bar);
        if (_recentBars.Count > MaxRecentBars)
            _recentBars.RemoveAt(0);
    }

    public void OnTick(decimal price, DateTime utcTime) { }

    private void CheckBearSweep(Bar bar, decimal level, SweepType type)
    {
        if (level <= 0) return;
        bool swept = bar.High > level + _cfg.MinTickPenetration;
        bool rejected = bar.Close < level;
        decimal wick = bar.High - Math.Max(bar.Open, bar.Close);
        bool validWick = wick >= _cfg.MinBodyReject;
        if (swept && rejected && validWick)
            ActiveSweeps.Add(new SweepEvent(level, type, SweepDirection.Bear, bar.Time));
    }

    private void CheckBullSweep(Bar bar, decimal level, SweepType type)
    {
        if (level <= 0) return;
        bool swept = bar.Low < level - _cfg.MinTickPenetration;
        bool rejected = bar.Close > level;
        decimal wick = Math.Min(bar.Open, bar.Close) - bar.Low;
        bool validWick = wick >= _cfg.MinBodyReject;
        if (swept && rejected && validWick)
            ActiveSweeps.Add(new SweepEvent(level, type, SweepDirection.Bull, bar.Time));
    }
}
```

**Step 4: Run tests — expected PASS**

**Step 5: Commit**

```bash
git add CRV.Core/Modules/SweepDetector.cs CRV.Core.Tests/Modules/SweepDetectorTests.cs
git commit -m "feat: add SweepDetector with multi-level sweep detection and equal highs/lows"
```

---

## Task 5: Opening Drive Detector

**Files:**
- Create: `CRV.Core/Modules/OpeningDriveDetector.cs`
- Create: `CRV.Core.Tests/Modules/OpeningDriveDetectorTests.cs`

**Step 1: Write failing tests**

```csharp
// CRV.Core.Tests/Modules/OpeningDriveDetectorTests.cs
using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

public class OpeningDriveDetectorTests
{
    static OpeningDriveDetector Build() => new(new ModuleConfig());

    [Fact]
    public void DetectsBullishDrive()
    {
        var od = Build();
        od.NewSession(DateTime.Today);
        od.CurrentAtr = 50;
        od.CurrentVwap = 100;
        // 6 bull bars, 1 bear → ratio > 2:1
        for (int i = 0; i < 6; i++)
            od.AccumulateOrbBar(new Bar(DateTime.UtcNow, 100 + i, 103 + i, 99 + i, 102 + i, 1000));
        od.AccumulateOrbBar(new Bar(DateTime.UtcNow, 106, 107, 105, 105.5m, 1000)); // bear

        od.FreezeAtOrbClose(orbRange: 45);  // 45/50 = 0.90 > 0.80 threshold
        Assert.True(od.OpeningDriveBull);
        Assert.False(od.OpeningDriveBear);
        Assert.True(od.OpeningDriveConfirmed);
    }

    [Fact]
    public void DetectsBearishDrive()
    {
        var od = Build();
        od.NewSession(DateTime.Today);
        od.CurrentAtr = 50;
        od.CurrentVwap = 110;
        for (int i = 0; i < 6; i++)
            od.AccumulateOrbBar(new Bar(DateTime.UtcNow, 105 - i, 106 - i, 103 - i, 104 - i, 1000));
        od.AccumulateOrbBar(new Bar(DateTime.UtcNow, 99, 100.5m, 99, 100, 1000)); // bull

        od.FreezeAtOrbClose(orbRange: 45);
        Assert.True(od.OpeningDriveBear);
        Assert.False(od.OpeningDriveBull);
    }

    [Fact]
    public void NoDriveWhenRangeTooSmall()
    {
        var od = Build();
        od.NewSession(DateTime.Today);
        od.CurrentAtr = 100;
        od.CurrentVwap = 100;
        for (int i = 0; i < 6; i++)
            od.AccumulateOrbBar(new Bar(DateTime.UtcNow, 100, 101, 99, 100.5m, 1000));

        od.FreezeAtOrbClose(orbRange: 10);  // 10/100 = 0.10 < 0.80
        Assert.False(od.OpeningDriveConfirmed);
    }

    [Fact]
    public void NewSessionResets()
    {
        var od = Build();
        od.NewSession(DateTime.Today);
        od.CurrentAtr = 50;
        od.CurrentVwap = 100;
        for (int i = 0; i < 6; i++)
            od.AccumulateOrbBar(new Bar(DateTime.UtcNow, 100, 103, 99, 102, 1000));
        od.FreezeAtOrbClose(orbRange: 45);
        Assert.True(od.OpeningDriveBull);

        od.NewSession(DateTime.Today.AddDays(1));
        Assert.False(od.OpeningDriveBull);
        Assert.False(od.OpeningDriveConfirmed);
    }
}
```

**Step 2: Run tests — expected FAIL**

**Step 3: Implement OpeningDriveDetector**

```csharp
// CRV.Core/Modules/OpeningDriveDetector.cs
using CRV.Core.Models;

namespace CRV.Core.Modules;

public class OpeningDriveDetector : IEngineModule
{
    private readonly ModuleConfig _cfg;
    private int _bullCount, _bearCount;
    private decimal _driveHigh, _driveLow;
    private decimal _lastClose;
    private bool _frozen;

    // Outputs
    public bool OpeningDriveBull      { get; private set; }
    public bool OpeningDriveBear      { get; private set; }
    public bool OpeningDriveConfirmed { get; private set; }
    public decimal DriveRangePctATR   { get; private set; }
    public decimal DrivePullbackPct   { get; private set; }

    // Set by engine
    public decimal CurrentAtr  { get; set; }
    public decimal CurrentVwap { get; set; }

    public OpeningDriveDetector(ModuleConfig cfg) => _cfg = cfg;

    public void NewSession(DateTime tradingDate)
    {
        _bullCount = _bearCount = 0;
        _driveHigh = 0; _driveLow = decimal.MaxValue;
        _lastClose = 0;
        _frozen = false;
        OpeningDriveBull = OpeningDriveBear = OpeningDriveConfirmed = false;
        DriveRangePctATR = DrivePullbackPct = 0;
    }

    /// <summary>Call for each bar during the ORB window.</summary>
    public void AccumulateOrbBar(Bar bar)
    {
        if (_frozen) return;
        if (bar.Close > bar.Open) _bullCount++;
        else if (bar.Close < bar.Open) _bearCount++;

        if (bar.High > _driveHigh) _driveHigh = bar.High;
        if (bar.Low  < _driveLow)  _driveLow  = bar.Low;
        _lastClose = bar.Close;
    }

    /// <summary>Call once when ORB window closes.</summary>
    public void FreezeAtOrbClose(decimal orbRange)
    {
        if (_frozen) return;
        _frozen = true;

        if (CurrentAtr <= 0) return;
        DriveRangePctATR = orbRange / CurrentAtr;
        bool rangeOk = DriveRangePctATR >= _cfg.DriveRangeAtrMult;
        if (!rangeOk) return;

        decimal driveRange = _driveHigh - _driveLow;
        if (driveRange <= 0) return;

        bool bullDrive = _bullCount > _bearCount * _cfg.DriveBullBearRatio
                      && _lastClose > CurrentVwap
                      && _lastClose >= _driveLow + driveRange * 0.7m;

        bool bearDrive = _bearCount > _bullCount * _cfg.DriveBullBearRatio
                      && _lastClose < CurrentVwap
                      && _lastClose <= _driveLow + driveRange * 0.3m;

        // Pullback check
        if (bullDrive)
        {
            DrivePullbackPct = driveRange > 0 ? (_driveHigh - _lastClose) / driveRange : 0;
            OpeningDriveBull = DrivePullbackPct < _cfg.MaxDrivePullback;
        }
        if (bearDrive)
        {
            DrivePullbackPct = driveRange > 0 ? (_lastClose - _driveLow) / driveRange : 0;
            OpeningDriveBear = DrivePullbackPct < _cfg.MaxDrivePullback;
        }

        OpeningDriveConfirmed = OpeningDriveBull || OpeningDriveBear;
    }

    // IEngineModule — these are called but opening drive uses AccumulateOrbBar + FreezeAtOrbClose instead
    public void OnBar(Bar bar, DateTime tradingDate) { }
    public void OnTick(decimal price, DateTime utcTime) { }
}
```

**Step 4: Run tests — expected PASS**

**Step 5: Commit**

```bash
git add CRV.Core/Modules/OpeningDriveDetector.cs CRV.Core.Tests/Modules/OpeningDriveDetectorTests.cs
git commit -m "feat: add OpeningDriveDetector with ORB window accumulation and drive classification"
```

---

## Task 6: Trend Day Filter

**Files:**
- Create: `CRV.Core/Modules/TrendDayFilter.cs`
- Create: `CRV.Core.Tests/Modules/TrendDayFilterTests.cs`

**Step 1: Write failing tests**

```csharp
// CRV.Core.Tests/Modules/TrendDayFilterTests.cs
using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

public class TrendDayFilterTests
{
    static TrendDayFilter Build() => new(new ModuleConfig { TrendDayThreshold = 4, ShallowPullbackMax = 0.35m });

    [Fact]
    public void ScoresBullTrendDay()
    {
        var tf = Build();
        tf.Update(
            openingDriveBull: true, openingDriveBear: false,
            close: 25100, vwap: 25000,
            orbHigh: 25050, orbLow: 24950, orbMid: 25000,
            sessionHigh: 25150, sessionLow: 24980, sessionOpen: 25000
        );
        Assert.Equal(5, tf.BullScore);  // all 5 conditions met
        Assert.True(tf.TrendDayBull);
    }

    [Fact]
    public void ScoresBearTrendDay()
    {
        var tf = Build();
        tf.Update(
            openingDriveBull: false, openingDriveBear: true,
            close: 24900, vwap: 25000,
            orbHigh: 25050, orbLow: 24950, orbMid: 25000,
            sessionHigh: 25020, sessionLow: 24850, sessionOpen: 25000
        );
        Assert.Equal(5, tf.BearScore);
        Assert.True(tf.TrendDayBear);
    }

    [Fact]
    public void LowScoreNotTrendDay()
    {
        var tf = Build();
        tf.Update(
            openingDriveBull: false, openingDriveBear: false,
            close: 25010, vwap: 25000,
            orbHigh: 25050, orbLow: 24950, orbMid: 25000,
            sessionHigh: 25020, sessionLow: 24980, sessionOpen: 25000
        );
        Assert.True(tf.BullScore < 4);
        Assert.False(tf.TrendDayBull);
    }

    [Fact]
    public void NewSessionResets()
    {
        var tf = Build();
        tf.Update(openingDriveBull: true, openingDriveBear: false,
            close: 25100, vwap: 25000, orbHigh: 25050, orbLow: 24950, orbMid: 25000,
            sessionHigh: 25150, sessionLow: 24980, sessionOpen: 25000);
        Assert.True(tf.TrendDayBull);

        tf.NewSession(DateTime.Today);
        Assert.Equal(0, tf.BullScore);
        Assert.False(tf.TrendDayBull);
    }
}
```

**Step 2: Run tests — expected FAIL**

**Step 3: Implement TrendDayFilter**

```csharp
// CRV.Core/Modules/TrendDayFilter.cs
using CRV.Core.Models;

namespace CRV.Core.Modules;

public class TrendDayFilter : IEngineModule
{
    private readonly ModuleConfig _cfg;

    public int BullScore    { get; private set; }
    public int BearScore    { get; private set; }
    public bool TrendDayBull => BullScore >= _cfg.TrendDayThreshold;
    public bool TrendDayBear => BearScore >= _cfg.TrendDayThreshold;

    public TrendDayFilter(ModuleConfig cfg) => _cfg = cfg;

    public void NewSession(DateTime tradingDate)
    {
        BullScore = BearScore = 0;
    }

    /// <summary>Call on every bar after ORB has formed. Reads outputs from other modules.</summary>
    public void Update(
        bool openingDriveBull, bool openingDriveBear,
        decimal close, decimal vwap,
        decimal orbHigh, decimal orbLow, decimal orbMid,
        decimal sessionHigh, decimal sessionLow, decimal sessionOpen)
    {
        // Bull score
        int bs = 0;
        if (openingDriveBull) bs++;
        if (close > orbHigh && sessionLow > orbMid) bs++;  // accepted above ORB
        if (close > vwap) bs++;
        // Shallow pullback from high
        decimal moveUp = sessionHigh - sessionOpen;
        if (moveUp > 0 && (sessionHigh - close) / moveUp < _cfg.ShallowPullbackMax) bs++;
        if (sessionHigh > orbHigh) bs++;
        BullScore = bs;

        // Bear score
        int brs = 0;
        if (openingDriveBear) brs++;
        if (close < orbLow && sessionHigh < orbMid) brs++;  // accepted below ORB
        if (close < vwap) brs++;
        // Shallow pullback from low
        decimal moveDown = sessionOpen - sessionLow;
        if (moveDown > 0 && (close - sessionLow) / moveDown < _cfg.ShallowPullbackMax) brs++;
        if (sessionLow < orbLow) brs++;
        BearScore = brs;
    }

    // IEngineModule — TrendDayFilter uses Update() with explicit params instead
    public void OnBar(Bar bar, DateTime tradingDate) { }
    public void OnTick(decimal price, DateTime utcTime) { }
}
```

**Step 4: Run tests — expected PASS**

**Step 5: Commit**

```bash
git add CRV.Core/Modules/TrendDayFilter.cs CRV.Core.Tests/Modules/TrendDayFilterTests.cs
git commit -m "feat: add TrendDayFilter with score-based trend day classification"
```

---

## Task 7: Composite Setup Engine

**Files:**
- Create: `CRV.Core/Modules/CompositeSetupEngine.cs`
- Create: `CRV.Core.Tests/Modules/CompositeSetupEngineTests.cs`

**Step 1: Write failing tests**

```csharp
// CRV.Core.Tests/Modules/CompositeSetupEngineTests.cs
using CRV.Core.Modules;
using Xunit;

public class CompositeSetupEngineTests
{
    [Fact]
    public void ArmsSetupC_SweepReversal_Long()
    {
        var cse = new CompositeSetupEngine();
        cse.Evaluate(
            anyBullSweep: true, anyBearSweep: false,
            close: 25050, vwap: 25000,
            bullScore: 3, bearScore: 0,
            openingDriveBull: false, openingDriveBear: false,
            trendDayBull: false, trendDayBear: false,
            bullVwapPullback: false, bearVwapPullback: false,
            vwapReversionLong: false, vwapReversionShort: false,
            inMidday: false,
            londonSweptAsiaLow: false, londonSweptAsiaHigh: false,
            nyBullExpansion: false, nyBearExpansion: false
        );
        Assert.True(cse.SetupCBull);
        Assert.False(cse.SetupCBear);
    }

    [Fact]
    public void ArmsSetupD_DrivePullback_Long()
    {
        var cse = new CompositeSetupEngine();
        cse.Evaluate(
            anyBullSweep: false, anyBearSweep: false,
            close: 25050, vwap: 25000,
            bullScore: 4, bearScore: 0,
            openingDriveBull: true, openingDriveBear: false,
            trendDayBull: true, trendDayBear: false,
            bullVwapPullback: true, bearVwapPullback: false,
            vwapReversionLong: false, vwapReversionShort: false,
            inMidday: false,
            londonSweptAsiaLow: false, londonSweptAsiaHigh: false,
            nyBullExpansion: false, nyBearExpansion: false
        );
        Assert.True(cse.SetupDBull);
    }

    [Fact]
    public void ArmsSetupF_VwapReversion_Long()
    {
        var cse = new CompositeSetupEngine();
        cse.Evaluate(
            anyBullSweep: false, anyBearSweep: false,
            close: 24900, vwap: 25000,
            bullScore: 1, bearScore: 2,
            openingDriveBull: false, openingDriveBear: false,
            trendDayBull: false, trendDayBear: false,
            bullVwapPullback: false, bearVwapPullback: false,
            vwapReversionLong: true, vwapReversionShort: false,
            inMidday: true,
            londonSweptAsiaLow: false, londonSweptAsiaHigh: false,
            nyBullExpansion: false, nyBearExpansion: false
        );
        Assert.True(cse.SetupFBull);
    }

    [Fact]
    public void ArmsSessionExpansion_Long()
    {
        var cse = new CompositeSetupEngine();
        cse.Evaluate(
            anyBullSweep: false, anyBearSweep: false,
            close: 25050, vwap: 25000,
            bullScore: 2, bearScore: 0,
            openingDriveBull: false, openingDriveBear: false,
            trendDayBull: false, trendDayBear: false,
            bullVwapPullback: false, bearVwapPullback: false,
            vwapReversionLong: false, vwapReversionShort: false,
            inMidday: false,
            londonSweptAsiaLow: true, londonSweptAsiaHigh: false,
            nyBullExpansion: true, nyBearExpansion: false
        );
        Assert.True(cse.SessionExpansionBull);
    }

    [Fact]
    public void NoSetupWhenConditionsNotMet()
    {
        var cse = new CompositeSetupEngine();
        cse.Evaluate(
            anyBullSweep: false, anyBearSweep: false,
            close: 25000, vwap: 25000,
            bullScore: 0, bearScore: 0,
            openingDriveBull: false, openingDriveBear: false,
            trendDayBull: false, trendDayBear: false,
            bullVwapPullback: false, bearVwapPullback: false,
            vwapReversionLong: false, vwapReversionShort: false,
            inMidday: false,
            londonSweptAsiaLow: false, londonSweptAsiaHigh: false,
            nyBullExpansion: false, nyBearExpansion: false
        );
        Assert.False(cse.SetupCBull);
        Assert.False(cse.SetupDBull);
        Assert.False(cse.SetupFBull);
        Assert.False(cse.SessionExpansionBull);
    }
}
```

**Step 2: Run tests — expected FAIL**

**Step 3: Implement CompositeSetupEngine**

```csharp
// CRV.Core/Modules/CompositeSetupEngine.cs
namespace CRV.Core.Modules;

public class CompositeSetupEngine
{
    // Setup C — Sweep Reversal
    public bool SetupCBull { get; private set; }
    public bool SetupCBear { get; private set; }

    // Setup D — Opening Drive Pullback
    public bool SetupDBull { get; private set; }
    public bool SetupDBear { get; private set; }

    // Setup F — Midday VWAP Reversion
    public bool SetupFBull { get; private set; }
    public bool SetupFBear { get; private set; }

    // Session Expansion
    public bool SessionExpansionBull { get; private set; }
    public bool SessionExpansionBear { get; private set; }

    public bool AnySetupActive => SetupCBull || SetupCBear || SetupDBull || SetupDBear
        || SetupFBull || SetupFBear || SessionExpansionBull || SessionExpansionBear;

    public void Evaluate(
        bool anyBullSweep, bool anyBearSweep,
        decimal close, decimal vwap,
        int bullScore, int bearScore,
        bool openingDriveBull, bool openingDriveBear,
        bool trendDayBull, bool trendDayBear,
        bool bullVwapPullback, bool bearVwapPullback,
        bool vwapReversionLong, bool vwapReversionShort,
        bool inMidday,
        bool londonSweptAsiaLow, bool londonSweptAsiaHigh,
        bool nyBullExpansion, bool nyBearExpansion)
    {
        // Setup C — Sweep Reversal
        SetupCBull = anyBullSweep && close > vwap && bullScore >= 2;
        SetupCBear = anyBearSweep && close < vwap && bearScore >= 2;

        // Setup D — Opening Drive Pullback
        SetupDBull = openingDriveBull && trendDayBull && bullVwapPullback;
        SetupDBear = openingDriveBear && trendDayBear && bearVwapPullback;

        // Setup F — Midday VWAP Reversion
        SetupFBull = inMidday && !trendDayBear && vwapReversionLong;
        SetupFBear = inMidday && !trendDayBull && vwapReversionShort;

        // Session Expansion
        SessionExpansionBull = londonSweptAsiaLow && nyBullExpansion;
        SessionExpansionBear = londonSweptAsiaHigh && nyBearExpansion;
    }

    public void Reset()
    {
        SetupCBull = SetupCBear = false;
        SetupDBull = SetupDBear = false;
        SetupFBull = SetupFBear = false;
        SessionExpansionBull = SessionExpansionBear = false;
    }
}
```

**Step 4: Run tests — expected PASS**

**Step 5: Commit**

```bash
git add CRV.Core/Modules/CompositeSetupEngine.cs CRV.Core.Tests/Modules/CompositeSetupEngineTests.cs
git commit -m "feat: add CompositeSetupEngine combining all module signals into setups C/D/F"
```

---

## Task 8: EngineSnapshot Extensions

**Files:**
- Modify: `CRV.Core/Models/Signals.cs` — add new fields to EngineSnapshot

**Step 1: Add new properties to EngineSnapshot**

Add after the existing `StickyStpB` property (around line 160):

```csharp
    // ── Module outputs ───────────────────────────────────────
    // Session
    public string  CurrentSession  { get; set; } = "";
    public decimal SessionHigh     { get; set; }
    public decimal SessionLow      { get; set; }
    public decimal PrevDayHigh     { get; set; }
    public decimal PrevDayLow      { get; set; }
    public bool    AsiaCompressed  { get; set; }

    // Sweep
    public string  LastSweep       { get; set; } = "";

    // VWAP Model
    public decimal VwapUpper1      { get; set; }
    public decimal VwapUpper2      { get; set; }
    public decimal VwapLower1      { get; set; }
    public decimal VwapLower2      { get; set; }
    public int     VwapState       { get; set; }

    // Opening Drive
    public bool    OpeningDriveBull { get; set; }
    public bool    OpeningDriveBear { get; set; }

    // Trend Day
    public int     TrendScoreBull  { get; set; }
    public int     TrendScoreBear  { get; set; }

    // Composite Setups
    public bool    SetupCBull      { get; set; }
    public bool    SetupCBear      { get; set; }
    public bool    SetupDBull      { get; set; }
    public bool    SetupDBear      { get; set; }
    public bool    SetupFBull      { get; set; }
    public bool    SetupFBear      { get; set; }
```

**Step 2: Verify build**

Run: `dotnet build CRV.Core -v q`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add CRV.Core/Models/Signals.cs
git commit -m "feat: extend EngineSnapshot with module output fields"
```

---

## Task 9: Wire Modules into OrbStrategyEngine

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

**Step 1: Add module fields to constructor**

After the existing indicator fields (`_atr`, `_vwap`, `_orb`, around line 14), add:

```csharp
    // ── Modules ───────────────────────────────────────────────
    private readonly SessionEngine          _sessionEngine;
    private readonly SweepDetector          _sweepDetector;
    private readonly VwapModel              _vwapModel;
    private readonly OpeningDriveDetector   _openingDrive;
    private readonly TrendDayFilter         _trendDay;
    private readonly CompositeSetupEngine   _compositeSetups;
```

In the constructor body, after the existing indicator initialization, add:

```csharp
        var modCfg = new ModuleConfig
        {
            TickSize   = cfg.TickSize,
            PointValue = cfg.PointValue,
            Timezone   = cfg.Timezone
        };
        _sessionEngine   = new SessionEngine(modCfg, _tz);
        _sweepDetector   = new SweepDetector(modCfg);
        _vwapModel       = new VwapModel(modCfg);
        _openingDrive    = new OpeningDriveDetector(modCfg);
        _trendDay        = new TrendDayFilter(modCfg);
        _compositeSetups = new CompositeSetupEngine();
```

**Step 2: Call modules in NewSession block**

In `ProcessBarInternalAsync`, inside the `if (newDay)` block, after existing resets, add:

```csharp
            _sessionEngine.NewSession(tradingDate);
            _sweepDetector.NewSession(tradingDate);
            _vwapModel.NewSession(tradingDate);
            _openingDrive.NewSession(tradingDate);
            _trendDay.NewSession(tradingDate);
            _compositeSetups.Reset();
```

**Step 3: Call modules in ProcessBarInternalAsync (after indicators)**

After `_vwap.Update(bar, tradingDate)` and `_atr.Update(bar)`, add:

```csharp
        // ── Module updates ───────────────────────────────────
        _sessionEngine.CurrentAtr = _atr.Value;
        _sessionEngine.CurrentVwap = _vwap.Value;
        _sessionEngine.OnBar(bar, tradingDate);

        // Feed ORB bars to opening drive detector
        if (!_orb.IsSet)
            _openingDrive.AccumulateOrbBar(bar);

        _vwapModel.OnBar(bar, tradingDate);

        // Sweep detector: update levels from session engine + orb
        _sweepDetector.SetLevels(
            pdh: _sessionEngine.PrevDayHigh, pdl: _sessionEngine.PrevDayLow,
            pwh: _sessionEngine.PrevWeekHigh, pwl: _sessionEngine.PrevWeekLow,
            pmh: _sessionEngine.PrevMonthHigh, pml: _sessionEngine.PrevMonthLow,
            sessionHigh: _sessionEngine.SessionHigh,
            sessionLow: _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : 0,
            orbHigh: _orb.OrbHigh, orbLow: _orb.OrbLow
        );
        _sweepDetector.OnBar(bar, tradingDate);
```

**Step 4: Freeze opening drive when ORB forms**

Find the existing ORB formation log (search for `_orbLoggedFormed`). Right after `_orbLoggedFormed = true`, add:

```csharp
            _openingDrive.CurrentAtr = _atr.Value;
            _openingDrive.CurrentVwap = _vwap.Value;
            _openingDrive.FreezeAtOrbClose(_orb.OrbRange);
```

**Step 5: Evaluate trend day + composite setups after ORB (every bar)**

After the ORB formation block, add:

```csharp
        // Trend day + composite setups (only after ORB formed)
        if (_orb.IsSet)
        {
            _vwapModel.TrendDayBull = _trendDay.TrendDayBull;
            _vwapModel.TrendDayBear = _trendDay.TrendDayBear;

            _trendDay.Update(
                openingDriveBull: _openingDrive.OpeningDriveBull,
                openingDriveBear: _openingDrive.OpeningDriveBear,
                close: bar.Close, vwap: _vwap.Value,
                orbHigh: _orb.OrbHigh, orbLow: _orb.OrbLow, orbMid: _orb.OrbMid,
                sessionHigh: _sessionEngine.SessionHigh,
                sessionLow: _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : bar.Low,
                sessionOpen: _orb.OrbMid  // approximate session open
            );

            _compositeSetups.Evaluate(
                anyBullSweep: _sweepDetector.AnyBullSweep,
                anyBearSweep: _sweepDetector.AnyBearSweep,
                close: bar.Close, vwap: _vwap.Value,
                bullScore: _trendDay.BullScore, bearScore: _trendDay.BearScore,
                openingDriveBull: _openingDrive.OpeningDriveBull,
                openingDriveBear: _openingDrive.OpeningDriveBear,
                trendDayBull: _trendDay.TrendDayBull,
                trendDayBear: _trendDay.TrendDayBear,
                bullVwapPullback: _vwapModel.BullVWAPPullback,
                bearVwapPullback: _vwapModel.BearVWAPPullback,
                vwapReversionLong: _vwapModel.VWAPReversionLong,
                vwapReversionShort: _vwapModel.VWAPReversionShort,
                inMidday: _sessionEngine.CurrentSession == SessionType.Midday,
                londonSweptAsiaLow: _sessionEngine.LondonSweptAsiaLow,
                londonSweptAsiaHigh: _sessionEngine.LondonSweptAsiaHigh,
                nyBullExpansion: _sessionEngine.NYBullExpansion,
                nyBearExpansion: _sessionEngine.NYBearExpansion
            );
        }
```

**Step 6: Call modules in ProcessPriceTickAsync**

After the existing guard checks (before `EvalTickSetupA`), add:

```csharp
        _sessionEngine.OnTick(price, utcTime);
```

**Step 7: Populate new snapshot fields in PublishSnapshot**

In the `PublishSnapshot` method, add to the `new EngineSnapshot { ... }` block after the existing fields:

```csharp
            // Module outputs
            CurrentSession  = _sessionEngine.CurrentSession.ToString(),
            SessionHigh     = _sessionEngine.SessionHigh,
            SessionLow      = _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : 0,
            PrevDayHigh     = _sessionEngine.PrevDayHigh,
            PrevDayLow      = _sessionEngine.PrevDayLow,
            AsiaCompressed  = _sessionEngine.AsiaCompressed,
            LastSweep       = _sweepDetector.LastSweep != null
                ? $"{_sweepDetector.LastSweep.Type} {_sweepDetector.LastSweep.Direction}" : "",
            VwapUpper1      = _vwapModel.Upper1,
            VwapUpper2      = _vwapModel.Upper2,
            VwapLower1      = _vwapModel.Lower1,
            VwapLower2      = _vwapModel.Lower2,
            VwapState       = _vwapModel.VwapState,
            OpeningDriveBull = _openingDrive.OpeningDriveBull,
            OpeningDriveBear = _openingDrive.OpeningDriveBear,
            TrendScoreBull  = _trendDay.BullScore,
            TrendScoreBear  = _trendDay.BearScore,
            SetupCBull      = _compositeSetups.SetupCBull,
            SetupCBear      = _compositeSetups.SetupCBear,
            SetupDBull      = _compositeSetups.SetupDBull,
            SetupDBear      = _compositeSetups.SetupDBear,
            SetupFBull      = _compositeSetups.SetupFBull,
            SetupFBear      = _compositeSetups.SetupFBear,
```

**Step 8: Add using directive**

At top of `OrbStrategyEngine.cs`:

```csharp
using CRV.Core.Modules;
```

**Step 9: Build and run all tests**

Run: `dotnet build CRV.Core -v q && dotnet test CRV.Core.Tests -v q`
Expected: Build succeeded, all tests pass

**Step 10: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: wire all modules into OrbStrategyEngine using composition pattern"
```

---

## Task 10: Daily Bar Fetch in LiveEngineOrchestrator

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`
- Modify: `CRV.Core/Interfaces/IInterfaces.cs` (add `IBarFeed.FetchDailyBarsAsync`)

**Step 1: Extend IBarFeed with daily bar fetch**

Add to `IBarFeed` interface:

```csharp
    /// <summary>Fetch historical daily bars for seeding module levels. Returns empty if not supported.</summary>
    Task<IReadOnlyList<Bar>> FetchDailyBarsAsync(string ticker, int count, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());
```

**Step 2: Wire daily bar fetch in LiveEngineOrchestrator**

In `RunEngineAsync`, after engine creation and before `feed.StreamAsync`, add:

```csharp
            // Seed module historical levels from daily bars
            try
            {
                var dailyBars = await feed.FetchDailyBarsAsync(cfg.Ticker, 30, ct);
                if (dailyBars.Count > 0)
                {
                    _log.LogInformation("Seeded {Count} daily bars for module levels", dailyBars.Count);
                    // Engine will need a method to pass these to SessionEngine
                    newEngine.SeedModuleHistory(dailyBars);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Failed to fetch daily bars for modules: {Err}", ex.Message);
            }
```

**Step 3: Add SeedModuleHistory to OrbStrategyEngine**

```csharp
    public void SeedModuleHistory(IReadOnlyList<Bar> dailyBars)
    {
        _sessionEngine.SeedHistory(dailyBars);
    }
```

**Step 4: Build and verify**

Run: `dotnet build CRV.Web -v q`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add CRV.Core/Interfaces/IInterfaces.cs CRV.Web/Services/LiveEngineOrchestrator.cs CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: seed session engine with daily bars from broker at engine start"
```

---

## Task 11: Dashboard UI — Module Output Display

**Files:**
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml`
- Modify: `CRV.Web/wwwroot/js/crv-hub.js` (or inline JS in the cshtml)

**Step 1: Add module output cards to dashboard**

After the existing ORB/VWAP/ATR row (Row 3), add a new row:

```html
<!-- ── Row 4: Module Outputs ──────────────────────────────── -->
<div class="row g-3 mb-3">
    <div class="col-6 col-md-3">
        <div class="card p-3 text-center">
            <div class="stat-label">Session</div>
            <div class="stat-value font-monospace" id="stat-session">—</div>
            <div class="small text-muted" id="stat-session-range">—</div>
        </div>
    </div>
    <div class="col-6 col-md-3">
        <div class="card p-3 text-center">
            <div class="stat-label">VWAP State</div>
            <div class="stat-value font-monospace" id="stat-vwap-state">—</div>
            <div class="small" id="stat-vwap-bands">—</div>
        </div>
    </div>
    <div class="col-6 col-md-3">
        <div class="card p-3 text-center">
            <div class="stat-label">Trend Score</div>
            <div class="d-flex justify-content-center gap-2">
                <span class="font-monospace"><span class="text-success">▲</span> <span id="stat-trend-bull">0</span></span>
                <span class="font-monospace"><span class="text-danger">▼</span> <span id="stat-trend-bear">0</span></span>
            </div>
            <div class="small" id="stat-trend-label">—</div>
        </div>
    </div>
    <div class="col-6 col-md-3">
        <div class="card p-3 text-center">
            <div class="stat-label">Active Setup</div>
            <div class="stat-value font-monospace" id="stat-active-setup">—</div>
            <div class="small" id="stat-sweep-info">—</div>
        </div>
    </div>
</div>
```

**Step 2: Add JS update logic**

In the `crv:update` event handler, add:

```javascript
    // ── Module outputs ───────────────────────────────────────
    setText("stat-session", d.currentSession || "—");
    if (d.sessionHigh > 0)
        setText("stat-session-range",
            `H: ${d.sessionHigh.toFixed(2)} L: ${(d.sessionLow > 0 ? d.sessionLow.toFixed(2) : "—")}`);

    // VWAP state
    const vwapLabels = { 2: "Extended▲", 1: "Accept▲", 0: "Neutral", "-1": "Accept▼", "-2": "Extended▼" };
    const vwapColors = { 2: "pnl-pos", 1: "pnl-pos", 0: "", "-1": "pnl-neg", "-2": "pnl-neg" };
    setText("stat-vwap-state", vwapLabels[d.vwapState] || "—", vwapColors[d.vwapState] || "");
    if (d.vwapUpper1 > 0)
        setText("stat-vwap-bands",
            `±1σ: ${d.vwapUpper1.toFixed(1)} / ${d.vwapLower1.toFixed(1)}`);

    // Trend score
    setText("stat-trend-bull", d.trendScoreBull ?? 0);
    setText("stat-trend-bear", d.trendScoreBear ?? 0);
    const trendLabel = d.trendScoreBull >= 4 ? "TREND DAY ▲" : d.trendScoreBear >= 4 ? "TREND DAY ▼" : "Rotational";
    const trendClass = d.trendScoreBull >= 4 ? "pnl-pos" : d.trendScoreBear >= 4 ? "pnl-neg" : "text-muted";
    setText("stat-trend-label", trendLabel, "small " + trendClass);

    // Active setups
    let activeSetup = "—";
    if (d.setupCBull) activeSetup = "C ▲ Sweep Rev";
    else if (d.setupCBear) activeSetup = "C ▼ Sweep Rev";
    else if (d.setupDBull) activeSetup = "D ▲ Drive PB";
    else if (d.setupDBear) activeSetup = "D ▼ Drive PB";
    else if (d.setupFBull) activeSetup = "F ▲ VWAP Rev";
    else if (d.setupFBear) activeSetup = "F ▼ VWAP Rev";
    setText("stat-active-setup", activeSetup);

    // Sweep info + opening drive
    let infoStr = "";
    if (d.lastSweep) infoStr += d.lastSweep;
    if (d.openingDriveBull) infoStr += (infoStr ? " | " : "") + "Drive ▲";
    if (d.openingDriveBear) infoStr += (infoStr ? " | " : "") + "Drive ▼";
    if (d.asiaCompressed) infoStr += (infoStr ? " | " : "") + "Asia ◆";
    setText("stat-sweep-info", infoStr || "—", "small text-muted");
```

**Step 3: Build and verify**

Run: `dotnet build CRV.Web -v q`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add CRV.Web/Pages/Dashboard/Index.cshtml CRV.Web/wwwroot/js/crv-hub.js
git commit -m "feat: add module outputs row to dashboard (session, VWAP state, trend score, active setup)"
```

---

## Task 12: Final Integration Test + All Tests Green

**Step 1: Run all tests**

Run: `dotnet test CRV.Core.Tests -v q`
Expected: ALL PASS

**Step 2: Build entire solution**

Run: `dotnet build -v q`
Expected: Build succeeded, 0 warnings

**Step 3: Final commit**

```bash
git add -A
git commit -m "chore: final integration — all modules wired, tests green, dashboard updated"
```

---

## Summary of New Files

| File | Purpose |
|------|---------|
| `CRV.Core/Modules/IEngineModule.cs` | Common module interface |
| `CRV.Core/Modules/ModuleConfig.cs` | Configuration for all modules |
| `CRV.Core/Modules/SessionEngine.cs` | Session detection + level tracking |
| `CRV.Core/Modules/SweepDetector.cs` | Liquidity sweep detection |
| `CRV.Core/Modules/VwapModel.cs` | VWAP bands + state + signals |
| `CRV.Core/Modules/OpeningDriveDetector.cs` | Opening drive classification |
| `CRV.Core/Modules/TrendDayFilter.cs` | Score-based trend day filter |
| `CRV.Core/Modules/CompositeSetupEngine.cs` | Combined setup evaluation |
| `CRV.Core.Tests/Modules/SessionEngineTests.cs` | 8 tests |
| `CRV.Core.Tests/Modules/VwapModelTests.cs` | 5 tests |
| `CRV.Core.Tests/Modules/SweepDetectorTests.cs` | 6 tests |
| `CRV.Core.Tests/Modules/OpeningDriveDetectorTests.cs` | 4 tests |
| `CRV.Core.Tests/Modules/TrendDayFilterTests.cs` | 4 tests |
| `CRV.Core.Tests/Modules/CompositeSetupEngineTests.cs` | 5 tests |

## Modified Files

| File | Change |
|------|--------|
| `CRV.Core/Models/Signals.cs` | Add ~25 new EngineSnapshot properties |
| `CRV.Core/Strategy/OrbStrategyEngine.cs` | Wire modules (constructor, NewSession, OnBar, OnTick, PublishSnapshot) |
| `CRV.Core/Interfaces/IInterfaces.cs` | Add `FetchDailyBarsAsync` to `IBarFeed` |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Daily bar fetch + SeedModuleHistory |
| `CRV.Web/Pages/Dashboard/Index.cshtml` | New module output cards |
| `CRV.Web/wwwroot/js/crv-hub.js` | New snapshot field rendering |
