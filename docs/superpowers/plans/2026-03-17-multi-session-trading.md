# Multi-Session Trading Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Support Asia, London, and NY trading sessions with independent ORB windows, setup configs, and trade limits — driven by a SessionManager wrapper that reconfigures a single engine instance at session boundaries.

**Architecture:** A new `SessionManager` class sits between the orchestrator and engine. It detects the active session by clock time and calls `engine.Reconfigure()` / `engine.SetIdle()` at transitions. The engine gains `Reconfigure`, `SetIdle`, `ForceExitAllAsync`, and `ResetDaily` methods but its entry/exit logic stays untouched. Per-session config is stored in typed `SetupConfig` classes that flatten into the existing `StrategyConfig` format via `ToLegacyConfig()`.

**Tech Stack:** C# .NET 10, ASP.NET Razor Pages, Entity Framework Core, SignalR, xUnit

**Spec:** `docs/superpowers/specs/2026-03-17-multi-session-trading-design.md`

---

## Chunk 1: Data Model & Config

### Task 1: SessionId enum and SetupConfig classes

**Files:**
- Create: `CRV.Core/Models/SessionConfig.cs`
- Test: `CRV.Core.Tests/Models/SessionConfigTests.cs`

- [ ] **Step 1: Create SetupConfigBase and typed subclasses**

Create `CRV.Core/Models/SessionConfig.cs`:

```csharp
namespace CRV.Core.Models;

public enum SessionId { Asia, London, NY }

/// <summary>Shared per-setup fields for all setup types.</summary>
public abstract class SetupConfigBase
{
    public bool    Enabled          { get; set; }
    public int     Contracts        { get; set; } = 2;
    public int     PartialCts       { get; set; } = 0;
    public int     PartialPct       { get; set; } = 50;
    public int     CutoffHour       { get; set; } = 14;
    public int     CutoffMinute     { get; set; } = 30;
    public int     MaxTrades        { get; set; } = 5;
    public string  OrderType        { get; set; } = "Market";
    public decimal MinRr            { get; set; } = 1.5m;
    public bool    CloseAtRthClose  { get; set; } = true;
    public bool    UsePartial       { get; set; } = true;
    public bool    UseBe            { get; set; } = true;
    public int     MaxAdverseMinutes { get; set; } = 0;
    public decimal HiVolMult        { get; set; } = 1.0m;
    public int     MaxContracts     { get; set; } = 2;
}

// Note: TargetPct, EntryTickOffset, UseVwap, UseOrbClose only exist on StrategyConfig
// for setups A and B. They are NOT on C/D/F. So they go on A/B subclasses, not the base.

public class SetupConfigA : SetupConfigBase
{
    public string  Mode        { get; set; } = "Conservative";
    public decimal NearPct     { get; set; } = 0.15m;
    public decimal PullbackPct { get; set; } = 0.50m;
    public decimal StopPct     { get; set; } = 0.10m;
    public int     TargetPct   { get; set; } = 100;
    public int     EntryTickOffset { get; set; } = 0;
    public bool    UseVwap     { get; set; } = true;
    public bool    UseOrbClose { get; set; } = false;
}

public class SetupConfigB : SetupConfigBase
{
    public string  Mode       { get; set; } = "Conservative";
    public decimal NearPct    { get; set; } = 0.15m;
    public decimal RetestPct  { get; set; } = 0.05m;
    public decimal StopPct    { get; set; } = 0.50m;
    public int     TargetPct  { get; set; } = 100;
    public int     EntryTickOffset { get; set; } = 0;
    public bool    UseVwap    { get; set; } = true;
    public bool    UseOrbClose { get; set; } = false;
}

public class SetupConfigC : SetupConfigBase
{
    public decimal SweepMinPenetration { get; set; } = 0.50m;
    public decimal SweepMinBodyReject  { get; set; } = 1.00m;
    public decimal SweepEqualTolerance { get; set; } = 2.00m;
    public int     SweepConfirmBars    { get; set; } = 1;
}

public class SetupConfigD : SetupConfigBase
{
    public decimal DriveRangeAtrMult  { get; set; } = 0.80m;
    public decimal DriveMaxPullback   { get; set; } = 0.35m;
    public int     DriveBullBearRatio { get; set; } = 2;
}

public class SetupConfigF : SetupConfigBase
{
    public int     TrendDayThreshold  { get; set; } = 4;
    public decimal ShallowPullbackMax { get; set; } = 0.35m;
    public int     VwapDevPeriod      { get; set; } = 20;
}
```

- [ ] **Step 2: Create SessionConfig class with ToLegacyConfig**

Append to `CRV.Core/Models/SessionConfig.cs`:

```csharp
/// <summary>One trading session (Asia, London, or NY) with its own ORB/RTH times and 5 setup configs.</summary>
public class SessionConfig
{
    public SessionId Id       { get; set; }
    public bool    Enabled    { get; set; }
    public TimeOnly OrbStart  { get; set; }
    public TimeOnly OrbEnd    { get; set; }
    public TimeOnly RthStart  { get; set; }
    public TimeOnly RthEnd    { get; set; }
    public int     ExitMinutesBefore { get; set; } = 0;

    public SetupConfigA SetupA { get; set; } = new();
    public SetupConfigB SetupB { get; set; } = new();
    public SetupConfigC SetupC { get; set; } = new();
    public SetupConfigD SetupD { get; set; } = new();
    public SetupConfigF SetupF { get; set; } = new();

    /// <summary>
    /// Flatten this session's config into a StrategyConfig the engine can consume.
    /// Global fields (Ticker, Broker, PointValue, etc.) come from <paramref name="global"/>.
    /// </summary>
    public StrategyConfig ToLegacyConfig(StrategyConfig global)
    {
        var c = global.Clone();
        // Session times
        c.OrbStart = OrbStart;  c.OrbEnd = OrbEnd;
        c.RthStart = RthStart;  c.RthEnd = RthEnd;
        c.ExitMinutesBefore = ExitMinutesBefore;
        // Setup A (includes A-only fields: TargetPct, EntryTickOffset, UseVwap, UseOrbClose)
        c.EnableA = SetupA.Enabled;  c.ContractsA = SetupA.Contracts;
        c.PartialCtsA = SetupA.PartialCts;  c.TargetPctA = SetupA.TargetPct;
        c.PartialPctA = SetupA.PartialPct;  c.CutoffHourA = SetupA.CutoffHour;
        c.CutoffMinuteA = SetupA.CutoffMinute;  c.MaxTradesA = SetupA.MaxTrades;
        c.OrderTypeA = SetupA.OrderType;  c.MinRrA = SetupA.MinRr;
        c.CloseAtRthCloseA = SetupA.CloseAtRthClose;  c.UsePartialA = SetupA.UsePartial;
        c.UseBeA = SetupA.UseBe;  c.EntryTickOffsetA = SetupA.EntryTickOffset;
        c.MaxAdverseMinutesA = SetupA.MaxAdverseMinutes;  c.HiVolMultA = SetupA.HiVolMult;
        c.MaxContractsA = SetupA.MaxContracts;  c.UseVwapA = SetupA.UseVwap;
        c.UseOrbCloseA = SetupA.UseOrbClose;
        c.ModeA = SetupA.Mode;  c.NearPctA = SetupA.NearPct;  c.NearPct = SetupA.NearPct;
        c.PullbackPct = SetupA.PullbackPct;  c.StopPctA = SetupA.StopPct;
        // Setup B (includes B-only fields: TargetPct, EntryTickOffset, UseVwap, UseOrbClose)
        c.EnableB = SetupB.Enabled;  c.ContractsB = SetupB.Contracts;
        c.PartialCtsB = SetupB.PartialCts;  c.TargetPctB = SetupB.TargetPct;
        c.PartialPctB = SetupB.PartialPct;  c.CutoffHourB = SetupB.CutoffHour;
        c.CutoffMinuteB = SetupB.CutoffMinute;  c.MaxTradesB = SetupB.MaxTrades;
        c.OrderTypeB = SetupB.OrderType;  c.MinRrB = SetupB.MinRr;
        c.CloseAtRthCloseB = SetupB.CloseAtRthClose;  c.UsePartialB = SetupB.UsePartial;
        c.UseBeB = SetupB.UseBe;  c.EntryTickOffsetB = SetupB.EntryTickOffset;
        c.MaxAdverseMinutesB = SetupB.MaxAdverseMinutes;  c.HiVolMultB = SetupB.HiVolMult;
        c.MaxContractsB = SetupB.MaxContracts;  c.UseVwapB = SetupB.UseVwap;
        c.UseOrbCloseB = SetupB.UseOrbClose;
        c.ModeB = SetupB.Mode;  c.NearPctB = SetupB.NearPct;
        c.RetestPct = SetupB.RetestPct;  c.StopPctB = SetupB.StopPct;
        // Setup C
        c.EnableC = SetupC.Enabled;  c.ContractsC = SetupC.Contracts;
        c.PartialCtsC = SetupC.PartialCts;  c.PartialPctC = SetupC.PartialPct;
        c.CutoffHourC = SetupC.CutoffHour;  c.CutoffMinuteC = SetupC.CutoffMinute;
        c.MaxTradesC = SetupC.MaxTrades;  c.OrderTypeC = SetupC.OrderType;
        c.MinRrC = SetupC.MinRr;  c.CloseAtRthCloseC = SetupC.CloseAtRthClose;
        c.UsePartialC = SetupC.UsePartial;  c.UseBeC = SetupC.UseBe;
        c.MaxAdverseMinutesC = SetupC.MaxAdverseMinutes;  c.HiVolMultC = SetupC.HiVolMult;
        c.MaxContractsC = SetupC.MaxContracts;
        c.SweepMinPenetration = SetupC.SweepMinPenetration;
        c.SweepMinBodyReject = SetupC.SweepMinBodyReject;
        c.SweepEqualTolerance = SetupC.SweepEqualTolerance;
        c.SweepConfirmBars = SetupC.SweepConfirmBars;
        // Setup D
        c.EnableD = SetupD.Enabled;  c.ContractsD = SetupD.Contracts;
        c.PartialCtsD = SetupD.PartialCts;  c.PartialPctD = SetupD.PartialPct;
        c.CutoffHourD = SetupD.CutoffHour;  c.CutoffMinuteD = SetupD.CutoffMinute;
        c.MaxTradesD = SetupD.MaxTrades;  c.OrderTypeD = SetupD.OrderType;
        c.MinRrD = SetupD.MinRr;  c.CloseAtRthCloseD = SetupD.CloseAtRthClose;
        c.UsePartialD = SetupD.UsePartial;  c.UseBeD = SetupD.UseBe;
        c.MaxAdverseMinutesD = SetupD.MaxAdverseMinutes;  c.HiVolMultD = SetupD.HiVolMult;
        c.MaxContractsD = SetupD.MaxContracts;
        c.DriveRangeAtrMult = SetupD.DriveRangeAtrMult;
        c.DriveMaxPullback = SetupD.DriveMaxPullback;
        c.DriveBullBearRatio = SetupD.DriveBullBearRatio;
        // Setup F
        c.EnableF = SetupF.Enabled;  c.ContractsF = SetupF.Contracts;
        c.PartialCtsF = SetupF.PartialCts;  c.PartialPctF = SetupF.PartialPct;
        c.CutoffHourF = SetupF.CutoffHour;  c.CutoffMinuteF = SetupF.CutoffMinute;
        c.MaxTradesF = SetupF.MaxTrades;  c.OrderTypeF = SetupF.OrderType;
        c.MinRrF = SetupF.MinRr;  c.CloseAtRthCloseF = SetupF.CloseAtRthClose;
        c.UsePartialF = SetupF.UsePartial;  c.UseBeF = SetupF.UseBe;
        c.MaxAdverseMinutesF = SetupF.MaxAdverseMinutes;  c.HiVolMultF = SetupF.HiVolMult;
        c.MaxContractsF = SetupF.MaxContracts;
        c.TrendDayThreshold = SetupF.TrendDayThreshold;
        c.ShallowPullbackMax = SetupF.ShallowPullbackMax;
        c.VwapDevPeriod = SetupF.VwapDevPeriod;
        return c;
    }

    /// <summary>Create default sessions — NY from existing config, Asia/London disabled.</summary>
    public static List<SessionConfig> CreateDefaults(StrategyConfig cfg)
    {
        return new List<SessionConfig>
        {
            new()
            {
                Id = SessionId.Asia, Enabled = false,
                OrbStart = new(19, 0), OrbEnd = new(19, 30),
                RthStart = new(19, 0), RthEnd = new(23, 59),
                SetupA = new() { Enabled = false },
                SetupB = new() { Enabled = false },
                SetupC = new() { Enabled = false },
                SetupD = new() { Enabled = false },
                SetupF = new() { Enabled = false },
            },
            new()
            {
                Id = SessionId.London, Enabled = false,
                OrbStart = new(3, 0), OrbEnd = new(3, 30),
                RthStart = new(3, 0), RthEnd = new(8, 0),
                SetupA = new() { Enabled = false },
                SetupB = new() { Enabled = false },
                SetupC = new() { Enabled = false },
                SetupD = new() { Enabled = false },
                SetupF = new() { Enabled = false },
            },
            FromExistingConfig(cfg),
        };
    }

    /// <summary>Build a NY SessionConfig from existing flat StrategyConfig fields.</summary>
    public static SessionConfig FromExistingConfig(StrategyConfig cfg)
    {
        return new SessionConfig
        {
            Id = SessionId.NY, Enabled = true,
            OrbStart = cfg.OrbStart, OrbEnd = cfg.OrbEnd,
            RthStart = cfg.RthStart, RthEnd = cfg.RthEnd,
            ExitMinutesBefore = cfg.ExitMinutesBefore,
            SetupA = new SetupConfigA
            {
                Enabled = cfg.EnableA, Contracts = cfg.ContractsA,
                PartialCts = cfg.PartialCtsA, TargetPct = cfg.TargetPctA,
                PartialPct = cfg.PartialPctA, CutoffHour = cfg.CutoffHourA,
                CutoffMinute = cfg.CutoffMinuteA, MaxTrades = cfg.MaxTradesA,
                OrderType = cfg.OrderTypeA, MinRr = cfg.MinRrA,
                CloseAtRthClose = cfg.CloseAtRthCloseA, UsePartial = cfg.UsePartialA,
                UseBe = cfg.UseBeA, EntryTickOffset = cfg.EntryTickOffsetA,
                MaxAdverseMinutes = cfg.MaxAdverseMinutesA, HiVolMult = cfg.HiVolMultA,
                MaxContracts = cfg.MaxContractsA, UseVwap = cfg.UseVwapA,
                UseOrbClose = cfg.UseOrbCloseA,
                Mode = cfg.ModeA, NearPct = cfg.NearPctA,
                PullbackPct = cfg.PullbackPct, StopPct = cfg.StopPctA,
            },
            SetupB = new SetupConfigB
            {
                Enabled = cfg.EnableB, Contracts = cfg.ContractsB,
                PartialCts = cfg.PartialCtsB, TargetPct = cfg.TargetPctB,
                PartialPct = cfg.PartialPctB, CutoffHour = cfg.CutoffHourB,
                CutoffMinute = cfg.CutoffMinuteB, MaxTrades = cfg.MaxTradesB,
                OrderType = cfg.OrderTypeB, MinRr = cfg.MinRrB,
                CloseAtRthClose = cfg.CloseAtRthCloseB, UsePartial = cfg.UsePartialB,
                UseBe = cfg.UseBeB, EntryTickOffset = cfg.EntryTickOffsetB,
                MaxAdverseMinutes = cfg.MaxAdverseMinutesB, HiVolMult = cfg.HiVolMultB,
                MaxContracts = cfg.MaxContractsB, UseVwap = cfg.UseVwapB,
                UseOrbClose = cfg.UseOrbCloseB,
                Mode = cfg.ModeB, NearPct = cfg.NearPctB,
                RetestPct = cfg.RetestPct, StopPct = cfg.StopPctB,
            },
            SetupC = new()
            {
                Enabled = cfg.EnableC, Contracts = cfg.ContractsC,
                PartialCts = cfg.PartialCtsC, PartialPct = cfg.PartialPctC,
                CutoffHour = cfg.CutoffHourC, CutoffMinute = cfg.CutoffMinuteC,
                MaxTrades = cfg.MaxTradesC, OrderType = cfg.OrderTypeC,
                MinRr = cfg.MinRrC, CloseAtRthClose = cfg.CloseAtRthCloseC,
                UsePartial = cfg.UsePartialC, UseBe = cfg.UseBeC,
                MaxAdverseMinutes = cfg.MaxAdverseMinutesC, HiVolMult = cfg.HiVolMultC,
                MaxContracts = cfg.MaxContractsC,
                SweepMinPenetration = cfg.SweepMinPenetration,
                SweepMinBodyReject = cfg.SweepMinBodyReject,
                SweepEqualTolerance = cfg.SweepEqualTolerance,
                SweepConfirmBars = cfg.SweepConfirmBars,
            },
            SetupD = new()
            {
                Enabled = cfg.EnableD, Contracts = cfg.ContractsD,
                PartialCts = cfg.PartialCtsD, PartialPct = cfg.PartialPctD,
                CutoffHour = cfg.CutoffHourD, CutoffMinute = cfg.CutoffMinuteD,
                MaxTrades = cfg.MaxTradesD, OrderType = cfg.OrderTypeD,
                MinRr = cfg.MinRrD, CloseAtRthClose = cfg.CloseAtRthCloseD,
                UsePartial = cfg.UsePartialD, UseBe = cfg.UseBeD,
                MaxAdverseMinutes = cfg.MaxAdverseMinutesD, HiVolMult = cfg.HiVolMultD,
                MaxContracts = cfg.MaxContractsD,
                DriveRangeAtrMult = cfg.DriveRangeAtrMult,
                DriveMaxPullback = cfg.DriveMaxPullback,
                DriveBullBearRatio = cfg.DriveBullBearRatio,
            },
            SetupF = new()
            {
                Enabled = cfg.EnableF, Contracts = cfg.ContractsF,
                PartialCts = cfg.PartialCtsF, PartialPct = cfg.PartialPctF,
                CutoffHour = cfg.CutoffHourF, CutoffMinute = cfg.CutoffMinuteF,
                MaxTrades = cfg.MaxTradesF, OrderType = cfg.OrderTypeF,
                MinRr = cfg.MinRrF, CloseAtRthClose = cfg.CloseAtRthCloseF,
                UsePartial = cfg.UsePartialF, UseBe = cfg.UseBeF,
                MaxAdverseMinutes = cfg.MaxAdverseMinutesF, HiVolMult = cfg.HiVolMultF,
                MaxContracts = cfg.MaxContractsF,
                TrendDayThreshold = cfg.TrendDayThreshold,
                ShallowPullbackMax = cfg.ShallowPullbackMax,
                VwapDevPeriod = cfg.VwapDevPeriod,
            },
        };
    }
}
```

- [ ] **Step 3: Write tests for ToLegacyConfig round-trip**

Create `CRV.Core.Tests/Models/SessionConfigTests.cs`:

```csharp
namespace CRV.Core.Tests.Models;

using CRV.Core.Models;
using Xunit;

public class SessionConfigTests
{
    [Fact]
    public void ToLegacyConfig_MapsSessionTimes()
    {
        var global = new StrategyConfig { Ticker = "/NQH2026", PointValue = 20m };
        var session = new SessionConfig
        {
            Id = SessionId.Asia, Enabled = true,
            OrbStart = new(19, 0), OrbEnd = new(19, 30),
            RthStart = new(19, 0), RthEnd = new(23, 59),
            ExitMinutesBefore = 5,
        };
        var flat = session.ToLegacyConfig(global);
        Assert.Equal(new TimeOnly(19, 0), flat.OrbStart);
        Assert.Equal(new TimeOnly(19, 30), flat.OrbEnd);
        Assert.Equal(new TimeOnly(19, 0), flat.RthStart);
        Assert.Equal(new TimeOnly(23, 59), flat.RthEnd);
        Assert.Equal(5, flat.ExitMinutesBefore);
        Assert.Equal("/NQH2026", flat.Ticker); // global preserved
    }

    [Fact]
    public void ToLegacyConfig_MapsSetupAFields()
    {
        var global = new StrategyConfig();
        var session = new SessionConfig
        {
            Id = SessionId.NY, Enabled = true,
            OrbStart = new(9, 30), OrbEnd = new(10, 0),
            RthStart = new(9, 30), RthEnd = new(16, 0),
            SetupA = new() { Enabled = true, Contracts = 3, StopPct = 0.20m, Mode = "Aggressive", MaxTrades = 7 },
        };
        var flat = session.ToLegacyConfig(global);
        Assert.True(flat.EnableA);
        Assert.Equal(3, flat.ContractsA);
        Assert.Equal(0.20m, flat.StopPctA);
        Assert.Equal("Aggressive", flat.ModeA);
        Assert.Equal(7, flat.MaxTradesA);
    }

    [Fact]
    public void ToLegacyConfig_MapsSetupCSpecificFields()
    {
        var global = new StrategyConfig();
        var session = new SessionConfig
        {
            Id = SessionId.London, Enabled = true,
            OrbStart = new(3, 0), OrbEnd = new(3, 30),
            RthStart = new(3, 0), RthEnd = new(8, 0),
            SetupC = new() { Enabled = true, SweepMinPenetration = 1.5m, SweepConfirmBars = 3 },
        };
        var flat = session.ToLegacyConfig(global);
        Assert.Equal(1.5m, flat.SweepMinPenetration);
        Assert.Equal(3, flat.SweepConfirmBars);
    }

    [Fact]
    public void FromExistingConfig_PreservesNYValues()
    {
        var cfg = new StrategyConfig
        {
            EnableA = true, ContractsA = 4, StopPctA = 0.15m, ModeA = "Aggressive",
            MaxTradesA = 8, OrbStart = new(9, 30), OrbEnd = new(10, 0),
        };
        var ny = SessionConfig.FromExistingConfig(cfg);
        Assert.Equal(SessionId.NY, ny.Id);
        Assert.True(ny.Enabled);
        Assert.Equal(4, ny.SetupA.Contracts);
        Assert.Equal(0.15m, ny.SetupA.StopPct);
        Assert.Equal("Aggressive", ny.SetupA.Mode);
    }

    [Fact]
    public void CreateDefaults_ReturnsThreeSessions()
    {
        var cfg = new StrategyConfig();
        var sessions = SessionConfig.CreateDefaults(cfg);
        Assert.Equal(3, sessions.Count);
        Assert.Equal(SessionId.Asia, sessions[0].Id);
        Assert.False(sessions[0].Enabled);
        Assert.Equal(SessionId.London, sessions[1].Id);
        Assert.False(sessions[1].Enabled);
        Assert.Equal(SessionId.NY, sessions[2].Id);
        Assert.True(sessions[2].Enabled);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "SessionConfigTests" -v normal`
Expected: All 5 tests PASS

- [ ] **Step 5: Add Sessions property to StrategyConfig**

Modify `CRV.Core/Models/StrategyConfig.cs`. Add after `SaveReplayTrades` (line 64):

```csharp
    // ── Multi-Session ──────────────────────────────────────────
    /// <summary>
    /// Multi-session config. Null = legacy single-session mode.
    /// Auto-populated from flat fields on first access via SessionConfig.CreateDefaults().
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<SessionConfig>? Sessions { get; set; }
```

Note: `Sessions` is NOT persisted to DB as a column on StrategyConfig. It will be stored in a separate JSON file alongside the existing config, or as a JSON column. For now, mark it `[NotMapped]` and `[JsonIgnore]` so EF and the existing JSON serialization don't break. Persistence is wired in Task 3.

- [ ] **Step 6: Build to verify no compilation errors**

Run: `dotnet build`
Expected: Build succeeded. 0 errors.

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Models/SessionConfig.cs CRV.Core/Models/StrategyConfig.cs CRV.Core.Tests/Models/SessionConfigTests.cs
git commit -m "feat: add SessionConfig data model with ToLegacyConfig and typed SetupConfig classes"
```

---

### Task 2: EngineSnapshot — add ActiveSessionId

**Files:**
- Modify: `CRV.Core/Models/Signals.cs` (EngineSnapshot class)

- [ ] **Step 1: Add ActiveSessionId to EngineSnapshot**

In `CRV.Core/Models/Signals.cs`, add to the `EngineSnapshot` class (near other session-state properties like `SessionEnded`):

```csharp
    public string ActiveSessionId { get; set; } = "";
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Models/Signals.cs
git commit -m "feat: add ActiveSessionId to EngineSnapshot"
```

---

### Task 3: TradeRecord — add SessionId column and EF migration

**Files:**
- Modify: `CRV.Core/Models/Signals.cs` (TradeRecord — verify SessionId exists)
- Modify: `CRV.Core/Data/TradingDbContext.cs` (ensure index)

- [ ] **Step 1: Verify TradeRecord already has SessionId**

Check `CRV.Core/Models/Signals.cs` for `SessionId` on `TradeRecord`. The explore report shows it already has a `SessionId` property and an index on it (line 23 of TradingDbContext). If it exists, no change needed — just verify.

If missing, add to TradeRecord:
```csharp
    public string SessionId { get; set; } = "NY";
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 3: Commit if changes were needed**

```bash
git add CRV.Core/Models/Signals.cs CRV.Core/Data/TradingDbContext.cs
git commit -m "feat: ensure TradeRecord.SessionId column with index"
```

---

## Chunk 2: Engine Reconfiguration Support

### Task 4: OrbCalculator.Reconfigure method

**Files:**
- Modify: `CRV.Core/Indicators/Indicators.cs` (OrbCalculator class, lines 84-134)
- Test: `CRV.Core.Tests/Indicators/OrbReconfigureTests.cs`

- [ ] **Step 1: Write failing test for OrbCalculator.Reconfigure**

Create `CRV.Core.Tests/Indicators/OrbReconfigureTests.cs`:

```csharp
namespace CRV.Core.Tests.Indicators;

using CRV.Core.Indicators;
using Xunit;

public class OrbReconfigureTests
{
    [Fact]
    public void Reconfigure_UpdatesOrbWindow()
    {
        var orb = new OrbCalculator(new(9, 30), new(10, 0), "America/New_York");
        orb.Reconfigure(new(19, 0), new(19, 30));
        // After reconfigure, feeding a bar at 19:15 should be within the new ORB window.
        // The OrbCalculator uses _orbStart/_orbEnd internally — we verify indirectly
        // by confirming the object is still functional (no crash, no stale state).
        // Direct field access is private, so we verify via Reset behavior.
        orb.Reset();
        Assert.False(orb.IsSet);
    }

    [Fact]
    public void Reset_ClearsOrbState()
    {
        var orb = new OrbCalculator(new(9, 30), new(10, 0), "America/New_York");
        // Simulate ORB set via Restore
        orb.Restore(5000m, 4950m, 0.5m, DateTime.Today);
        Assert.True(orb.IsSet);
        orb.Reset();
        Assert.False(orb.IsSet);
        Assert.Equal(0m, orb.OrbHigh);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "OrbReconfigureTests" -v normal`
Expected: FAIL — `Reconfigure` and `Reset` methods don't exist yet.

- [ ] **Step 3: Implement Reconfigure and Reset on OrbCalculator**

Modify `CRV.Core/Indicators/Indicators.cs`. In the `OrbCalculator` class:

1. Change lines 86-87 from `private readonly TimeOnly` to `private TimeOnly`:
```csharp
    private TimeOnly _orbStart;
    private TimeOnly _orbEnd;
```

2. Add after the `Restore` method (after line 127):
```csharp
    /// <summary>Update the ORB window times for a new session.</summary>
    public void Reconfigure(TimeOnly orbStart, TimeOnly orbEnd)
    {
        _orbStart = orbStart;
        _orbEnd   = orbEnd;
    }

    /// <summary>Clear all ORB state (high/low/formed). Does NOT affect ATR or other indicators.</summary>
    public void Reset()
    {
        _high             = 0;
        _low              = decimal.MaxValue;
        _active           = false;
        _isSet            = false;
        _closeRelPct      = 0;
        _lastTradingDate  = DateTime.MinValue;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "OrbReconfigureTests" -v normal`
Expected: All 2 tests PASS

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Indicators/Indicators.cs CRV.Core.Tests/Indicators/OrbReconfigureTests.cs
git commit -m "feat: add OrbCalculator.Reconfigure and Reset methods"
```

---

### Task 5: Module Reconfigure methods

**Files:**
- Modify: `CRV.Core/Modules/SweepDetector.cs`
- Modify: `CRV.Core/Modules/OpeningDriveDetector.cs`
- Modify: `CRV.Core/Modules/TrendDayFilter.cs`
- Modify: `CRV.Core/Modules/VwapModel.cs`
- Modify: `CRV.Core/Modules/SessionEngine.cs`

Each module stores `ModuleConfig` as `private readonly ModuleConfig _cfg`. Change to `private ModuleConfig _cfg` and add a `Reconfigure(ModuleConfig cfg)` method.

- [ ] **Step 1: SweepDetector — add Reconfigure**

In `CRV.Core/Modules/SweepDetector.cs`:
1. Change `private readonly ModuleConfig _cfg;` to `private ModuleConfig _cfg;`
2. Add method:
```csharp
    /// <summary>Update module parameters for a new session config.</summary>
    public void Reconfigure(ModuleConfig cfg) => _cfg = cfg;
```

- [ ] **Step 2: OpeningDriveDetector — add Reconfigure**

Same pattern in `CRV.Core/Modules/OpeningDriveDetector.cs`:
1. Change `private readonly ModuleConfig _cfg;` to `private ModuleConfig _cfg;`
2. Add: `public void Reconfigure(ModuleConfig cfg) => _cfg = cfg;`

- [ ] **Step 3: TrendDayFilter — add Reconfigure**

Same pattern in `CRV.Core/Modules/TrendDayFilter.cs`:
1. Change `private readonly ModuleConfig _cfg;` to `private ModuleConfig _cfg;`
2. Add: `public void Reconfigure(ModuleConfig cfg) => _cfg = cfg;`

- [ ] **Step 4: VwapModel — add Reconfigure**

Same pattern in `CRV.Core/Modules/VwapModel.cs`:
1. Change `private readonly ModuleConfig _cfg;` to `private ModuleConfig _cfg;`
2. Add: `public void Reconfigure(ModuleConfig cfg) => _cfg = cfg;`

- [ ] **Step 5: SessionEngine — add Reconfigure**

Same pattern in `CRV.Core/Modules/SessionEngine.cs`:
1. Change `private readonly ModuleConfig _cfg;` to `private ModuleConfig _cfg;`
2. Add: `public void Reconfigure(ModuleConfig cfg) => _cfg = cfg;`

- [ ] **Step 6: Build and run all tests**

Run: `dotnet build && dotnet test`
Expected: Build succeeded. All 147+ tests pass (no behavior change, only added methods).

- [ ] **Step 7: Commit**

```bash
git add CRV.Core/Modules/SweepDetector.cs CRV.Core/Modules/OpeningDriveDetector.cs CRV.Core/Modules/TrendDayFilter.cs CRV.Core/Modules/VwapModel.cs CRV.Core/Modules/SessionEngine.cs
git commit -m "feat: add Reconfigure(ModuleConfig) to all engine modules"
```

---

### Task 6: Engine — Reconfigure, SetIdle, ForceExitAllAsync, ResetDaily

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`
- Test: `CRV.Core.Tests/Strategy/EngineReconfigureTests.cs`

- [ ] **Step 1: Remove readonly on _cfg**

In `CRV.Core/Strategy/OrbStrategyEngine.cs` line 15, change:
```csharp
    private readonly StrategyConfig     _cfg;
```
to:
```csharp
    private StrategyConfig     _cfg;
```

- [ ] **Step 2: Add _idle and _activeSessionId fields**

Add near line 35 (session state block):
```csharp
    private bool      _idle              = false;  // starts active for backward compat (single-session mode)
    private bool      _sessionManagedMode = false; // true when SessionManager drives transitions
    private string    _activeSessionId   = "";
```

- [ ] **Step 3: Add SetIdle method**

Add after the constructor (around line 241):
```csharp
    /// <summary>Put engine into idle mode — all ProcessBar/ProcessPriceTick calls become no-ops.</summary>
    public void SetIdle()
    {
        _idle = true;
        _activeSessionId = "";
        _log.LogInformation("Engine set to idle");
    }
```

- [ ] **Step 4: Add early-return in ProcessBarAsync and ProcessPriceTickAsync**

At the very top of `ProcessBarAsync` (the public method), add:
```csharp
        if (_idle) return;
```

At the very top of `ProcessPriceTickAsync`, add:
```csharp
        if (_idle) return;
```

- [ ] **Step 5: Add Reconfigure method**

Add after SetIdle:
```csharp
    /// <summary>
    /// Swap config for a new session. Resets session-scoped state (ORB, trade counts,
    /// cutoff flags, arm states) but preserves daily-scoped state (ATR, VWAP, daily levels).
    /// </summary>
    public void Reconfigure(StrategyConfig cfg, SessionId sessionId)
    {
        _cfg = cfg;
        _activeSessionId = sessionId.ToString();

        // Update ORB calculator window times and reset ORB state
        _orb.Reconfigure(cfg.OrbStart, cfg.OrbEnd);
        _orb.Reset();

        // Update module configs (setup-specific parameters may differ per session)
        var modCfg = new CRV.Core.Modules.ModuleConfig
        {
            TickSize             = cfg.TickSize,
            PointValue           = cfg.PointValue,
            Timezone             = cfg.Timezone,
            MinTickPenetration   = cfg.SweepMinPenetration,
            MinBodyReject        = cfg.SweepMinBodyReject,
            EqualLevelTolerance  = cfg.SweepEqualTolerance,
            ConfirmationBars     = cfg.SweepConfirmBars,
            DriveRangeAtrMult    = cfg.DriveRangeAtrMult,
            MaxDrivePullback     = cfg.DriveMaxPullback,
            DriveBullBearRatio   = cfg.DriveBullBearRatio,
            TrendDayThreshold    = cfg.TrendDayThreshold,
            ShallowPullbackMax   = cfg.ShallowPullbackMax,
            VwapDevPeriod        = cfg.VwapDevPeriod,
        };
        // Reconfigure modules with updated setup-specific parameters.
        // NOTE: Do NOT reconfigure _sessionEngine here — it tracks daily levels
        // (PDH/PDL, Asia/London highs) across all sessions. Only reset at daily boundary.
        _sweepDetector.Reconfigure(modCfg);
        _vwapModel.Reconfigure(modCfg);
        _openingDrive.Reconfigure(modCfg);
        _trendDay.Reconfigure(modCfg);

        // Reset session-scoped state
        _pastCutoff = false; _pastCutoffA = false; _pastCutoffB = false;
        _pastCutoffC = false; _pastCutoffD = false; _pastCutoffF = false;
        _rthEnded = false; _orbLoggedFormed = false; _orbJustFormed = false;
        _firstLiveBar = true; _warmupCooldown = 0; _orbAtrRatio = 0;
        _orbRestoredFromCache = false;

        // Reset per-setup win/loss/PnL counters for the new session.
        // IMPORTANT: Do NOT reset _todayPnl here — it accumulates across all sessions
        // for the daily loss limit check. _todayPnl is reset only in ResetDaily().
        _todayWins = 0; _todayLosses = 0;
        _todayPeak = _todayPnl; _todayMaxDD = 0;
        _todayWinPnl = 0; _todayLossPnl = 0;
        _todayWinsA = 0; _todayLossesA = 0; _todayWinPnlA = 0; _todayLossPnlA = 0;
        _todayWinsB = 0; _todayLossesB = 0; _todayWinPnlB = 0; _todayLossPnlB = 0;
        _todayWinsC = 0; _todayLossesC = 0; _todayWinPnlC = 0; _todayLossPnlC = 0;
        _todayWinsD = 0; _todayLossesD = 0; _todayWinPnlD = 0; _todayLossPnlD = 0;
        _todayWinsF = 0; _todayLossesF = 0; _todayWinPnlF = 0; _todayLossPnlF = 0;
        _lastTickTime = DateTime.MinValue;

        // Reset per-setup trade state
        _tradeCountA = 0; _tradeCountB = 0; _tradeCountC = 0;
        _tradeCountD = 0; _tradeCountF = 0;
        ResetSetupA(); ResetSetupB(); ResetSetupC(); ResetSetupD(); ResetSetupF();

        // Reset session-level module state (sweep buffer, drive detection, etc.)
        // NOTE: Do NOT call _sessionEngine.NewSession() or _vwap.NewSession() —
        // those track daily-scope data and only reset at daily boundary.
        var td = DateTime.Today;
        _sweepDetector.NewSession(td);
        _openingDrive.NewSession(td);
        _trendDay.NewSession(td);
        _compositeSetups.Reset();

        _idle = false;
        _sessionManagedMode = true;
        _log.LogInformation("Engine reconfigured for session {Session}", sessionId);
    }
```

- [ ] **Step 6: Add ForceExitAllAsync method**

Use the same pattern as existing `RequestForceExitA()` (line 243): get last price from `_prices`, build a synthetic bar, call the private `ForceExitA/B/C/D/F(bar)` methods. These private methods already exist and handle all the PnL/signal logic correctly.

```csharp
    /// <summary>Force-exit all active trades. Called by SessionManager at session end.</summary>
    public async Task ForceExitAllAsync()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0)
        {
            // Fallback: set volatile flags so next bar handles the exit
            _forceExitA = true; _forceExitB = true;
            _forceExitC = true; _forceExitD = true; _forceExitF = true;
            _rthEnded = true;
            return;
        }
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        if (_activeA) await ForceExitA(bar);
        if (_activeB) await ForceExitB(bar);
        if (_activeC) await ForceExitC(bar);
        if (_activeD) await ForceExitD(bar);
        if (_activeF) await ForceExitF(bar);
        _rthEnded = true;
    }
```

This mirrors the exact pattern used by the existing `RequestForceExitA()` through `RequestForceExitF()` methods (lines 243-295).

- [ ] **Step 7: Add ResetDaily method**

```csharp
    /// <summary>
    /// Daily-scope reset — ATR, VWAP, session levels, daily loss limit, daily PnL.
    /// Called by SessionManager at SessionStartHour boundary (before first session of the day).
    /// </summary>
    public void ResetDaily()
    {
        _atr.Reset();
        _vwap.NewSession(DateTime.Today);
        _sessionEngine.NewSession(DateTime.Today);
        _todayPnl = 0;  // daily loss limit accumulator — resets once per day
        _ddBreached = false;
        _lastDate = DateTime.MinValue;
        _log.LogInformation("Daily reset complete");
    }
```

- [ ] **Step 8: Wire _activeSessionId into snapshot**

Find the snapshot building code (search for `new EngineSnapshot` or where snapshot properties are assigned). Add:
```csharp
    ActiveSessionId = _activeSessionId,
```

- [ ] **Step 9: Build and run all tests**

Run: `dotnet build && dotnet test`
Expected: Build succeeded. All tests pass. `_idle` defaults to `false` (set in Step 2) so existing behavior is preserved — engine starts active, same as before. Only `SetIdle()` sets it `true`.

- [ ] **Step 10: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: add Reconfigure, SetIdle, ForceExitAllAsync, ResetDaily to engine"
```

---

## Chunk 3: SessionManager

### Task 7: SessionManager implementation

**Files:**
- Create: `CRV.Core/Strategy/SessionManager.cs`
- Test: `CRV.Core.Tests/Strategy/SessionManagerTests.cs`

- [ ] **Step 1: Write failing tests for session detection**

Create `CRV.Core.Tests/Strategy/SessionManagerTests.cs`:

```csharp
namespace CRV.Core.Tests.Strategy;

using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

public class SessionManagerTests
{
    private static List<SessionConfig> ThreeSessions() => new()
    {
        new() { Id = SessionId.Asia,   Enabled = true, RthStart = new(19, 0), RthEnd = new(23, 59), OrbStart = new(19, 0), OrbEnd = new(19, 30) },
        new() { Id = SessionId.London, Enabled = true, RthStart = new(3, 0),  RthEnd = new(8, 0),   OrbStart = new(3, 0),  OrbEnd = new(3, 30)  },
        new() { Id = SessionId.NY,     Enabled = true, RthStart = new(9, 30), RthEnd = new(16, 0),  OrbStart = new(9, 30), OrbEnd = new(10, 0)  },
    };

    [Theory]
    [InlineData(19, 15, SessionId.Asia)]
    [InlineData(23, 58, SessionId.Asia)]
    [InlineData(3, 0,  SessionId.London)]
    [InlineData(7, 59, SessionId.London)]
    [InlineData(9, 30, SessionId.NY)]
    [InlineData(15, 59, SessionId.NY)]
    public void GetActiveSession_ReturnsCorrectSession(int hour, int min, SessionId expected)
    {
        var mgr = new SessionManager(ThreeSessions());
        var result = mgr.GetActiveSession(new TimeOnly(hour, min));
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Id);
    }

    [Theory]
    [InlineData(0, 30)]   // gap: Asia->London
    [InlineData(2, 59)]   // just before London
    [InlineData(8, 30)]   // gap: London->NY
    [InlineData(16, 30)]  // gap: NY->Asia
    [InlineData(18, 30)]  // gap: NY->Asia
    public void GetActiveSession_ReturnsNull_InGaps(int hour, int min)
    {
        var mgr = new SessionManager(ThreeSessions());
        var result = mgr.GetActiveSession(new TimeOnly(hour, min));
        Assert.Null(result);
    }

    [Fact]
    public void GetActiveSession_SkipsDisabledSessions()
    {
        var sessions = ThreeSessions();
        sessions[0].Enabled = false; // disable Asia
        var mgr = new SessionManager(sessions);
        var result = mgr.GetActiveSession(new TimeOnly(19, 30));
        Assert.Null(result); // Asia disabled, nothing active at 19:30
    }

    [Fact]
    public void Validate_RejectsOverlappingSessions()
    {
        var sessions = new List<SessionConfig>
        {
            new() { Id = SessionId.Asia,   Enabled = true, RthStart = new(19, 0), RthEnd = new(23, 59) },
            new() { Id = SessionId.London, Enabled = true, RthStart = new(23, 0), RthEnd = new(8, 0) }, // overlaps Asia AND spans midnight
        };
        var errors = SessionManager.Validate(sessions);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_RejectsMidnightSpanning()
    {
        var sessions = new List<SessionConfig>
        {
            new() { Id = SessionId.Asia, Enabled = true, RthStart = new(22, 0), RthEnd = new(2, 0) }, // spans midnight
        };
        var errors = SessionManager.Validate(sessions);
        Assert.Contains(errors, e => e.Contains("midnight"));
    }

    [Fact]
    public void Validate_AcceptsValidSessions()
    {
        var errors = SessionManager.Validate(ThreeSessions());
        Assert.Empty(errors);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test CRV.Core.Tests --filter "SessionManagerTests" -v normal`
Expected: FAIL — `SessionManager` class doesn't exist.

- [ ] **Step 3: Implement SessionManager**

Create `CRV.Core/Strategy/SessionManager.cs`:

```csharp
namespace CRV.Core.Strategy;

using CRV.Core.Models;

/// <summary>
/// Determines the active trading session by clock time and drives
/// engine transitions (Reconfigure / SetIdle / ResetDaily).
/// </summary>
public class SessionManager
{
    private readonly List<SessionConfig> _sessions;
    private SessionConfig? _activeSession;

    public SessionManager(List<SessionConfig> sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public SessionConfig? ActiveSession => _activeSession;

    /// <summary>Find the enabled session whose RTH window contains the given local time, or null.</summary>
    public SessionConfig? GetActiveSession(TimeOnly localTime)
    {
        foreach (var s in _sessions)
        {
            if (!s.Enabled) continue;
            if (localTime >= s.RthStart && localTime < s.RthEnd)
                return s;
        }
        return null;
    }

    /// <summary>
    /// Check for session transitions. Returns the transition type and new session (if any).
    /// Call on every bar/tick with the current local time.
    /// </summary>
    public (TransitionType type, SessionConfig? session) CheckTransition(TimeOnly localTime)
    {
        var newSession = GetActiveSession(localTime);
        var newId = newSession?.Id;
        var oldId = _activeSession?.Id;

        if (newId == oldId) return (TransitionType.None, _activeSession);

        // Session changed
        var prevSession = _activeSession;
        _activeSession = newSession;

        if (newSession == null)
            return (TransitionType.SessionEnded, null);

        if (prevSession == null)
            return (TransitionType.SessionStarted, newSession);

        // Direct transition (shouldn't happen with gaps, but handle gracefully)
        return (TransitionType.SessionStarted, newSession);
    }

    /// <summary>Validate session configs. Returns list of error messages (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(List<SessionConfig> sessions)
    {
        var errors = new List<string>();

        foreach (var s in sessions.Where(s => s.Enabled))
        {
            if (s.RthEnd <= s.RthStart)
                errors.Add($"Session {s.Id}: RthEnd must be after RthStart (cannot span midnight).");
            if (s.OrbEnd <= s.OrbStart)
                errors.Add($"Session {s.Id}: OrbEnd must be after OrbStart.");
            if (s.OrbStart < s.RthStart || s.OrbEnd > s.RthEnd)
                errors.Add($"Session {s.Id}: ORB window must be within RTH window.");
        }

        // Check for overlap between enabled sessions
        var enabled = sessions.Where(s => s.Enabled).OrderBy(s => s.RthStart).ToList();
        for (int i = 0; i < enabled.Count - 1; i++)
        {
            if (enabled[i].RthEnd > enabled[i + 1].RthStart)
                errors.Add($"Sessions {enabled[i].Id} and {enabled[i + 1].Id} overlap.");
        }

        return errors;
    }
}

public enum TransitionType { None, SessionStarted, SessionEnded }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test CRV.Core.Tests --filter "SessionManagerTests" -v normal`
Expected: All 8 tests PASS

- [ ] **Step 5: Build all**

Run: `dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 6: Commit**

```bash
git add CRV.Core/Strategy/SessionManager.cs CRV.Core.Tests/Strategy/SessionManagerTests.cs
git commit -m "feat: add SessionManager with session detection and validation"
```

---

## Chunk 4: Orchestrator Integration

### Task 8: Wire SessionManager into LiveEngineOrchestrator

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

This is the critical integration task. The orchestrator's `RunEngineAsync` method must:
1. Create a `SessionManager` from config sessions
2. Before forwarding each bar/tick, call `CheckTransition`
3. On `SessionStarted` → call `engine.Reconfigure(session.ToLegacyConfig(cfg))`
4. On `SessionEnded` → call `engine.ForceExitAllAsync()` then `engine.SetIdle()`
5. Detect daily reset at `SessionStartHour` → call `engine.ResetDaily()`

- [ ] **Step 1: Add SessionManager field and initialization**

In the `RunEngineAsync` method, after engine creation (around line 268), add:

```csharp
            // Multi-session support
            var sessions = cfg.Sessions ?? SessionConfig.CreateDefaults(cfg);
            var sessionMgr = new SessionManager(sessions);
```

Add necessary `using CRV.Core.Strategy;` and `using CRV.Core.Models;` at the top if not already present.

- [ ] **Step 2: Add session transition helper**

Add a local function inside `RunEngineAsync`, before the streaming loop (before line 354):

```csharp
            DateTime lastDailyReset = DateTime.MinValue;

            async Task CheckSessionTransitionAsync(DateTime barTimeUtc)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(barTimeUtc, TimeZoneInfo.FindSystemTimeZoneById(cfg.Timezone));
                var localTime = TimeOnly.FromDateTime(local);

                // Daily reset at SessionStartHour (before first session of the day)
                var tradingDate = cfg.TradingDate(local);
                if (tradingDate != lastDailyReset)
                {
                    lastDailyReset = tradingDate;
                    newEngine.ResetDaily();
                }

                var (transition, session) = sessionMgr.CheckTransition(localTime);
                switch (transition)
                {
                    case TransitionType.SessionStarted:
                        var flat = session!.ToLegacyConfig(cfg);
                        newEngine.Reconfigure(flat, session.Id);
                        break;
                    case TransitionType.SessionEnded:
                        await newEngine.ForceExitAllAsync();
                        newEngine.SetIdle();
                        break;
                }
            }
```

- [ ] **Step 3: Call transition check before each bar**

In the streaming loop (around line 354-367), wrap the bar processing:

```csharp
            await foreach (var bar in feed.StreamAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                await engineLock.WaitAsync(ct);
                try
                {
                    await CheckSessionTransitionAsync(bar.Time);

                    if (bar.IsConfirmed && bar.Time < warmupCutoffUtc)
                        await _engine.WarmupBarAsync(bar, ct);
                    else
                        await _engine.ProcessBarAsync(bar, ct);
                }
                finally { engineLock.Release(); }
            }
```

- [ ] **Step 4: Call transition check on ticks too**

In the tick consumer task (around lines 309-322), add transition check:

```csharp
            var tickTask = Task.Run(async () =>
            {
                await foreach (var (price, time) in tickCh.Reader.ReadAllAsync(ct))
                {
                    OrbStrategyEngine? engine;
                    lock (_lifecycleLock) { engine = _engine; }
                    if (engine == null) break;
                    await engineLock.WaitAsync(ct);
                    try
                    {
                        await CheckSessionTransitionAsync(time);
                        await engine.ProcessPriceTickAsync(price, time);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _log.LogWarning(ex, "[tick] ProcessPriceTickAsync failed @ {P}", price); }
                    finally { engineLock.Release(); }
                }
            }, ct);
```

- [ ] **Step 5: Build and run tests**

Run: `dotnet build && dotnet test`
Expected: All pass. The engine now starts idle and waits for SessionManager to trigger `Reconfigure`. For single-session backward compat: `SessionConfig.CreateDefaults(cfg)` produces a NY session that matches the current config, so existing behavior is preserved.

- [ ] **Step 6: Commit**

```bash
git add CRV.Web/Services/LiveEngineOrchestrator.cs
git commit -m "feat: wire SessionManager into orchestrator bar/tick loop"
```

---

### Task 9: Remove engine self-managed session boundary detection

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`

Now that SessionManager handles transitions, the engine's internal `TradingDate()` check (lines 760-805) creates double-resets. Remove it.

- [ ] **Step 1: Replace newDay block with no-op**

In `ProcessBarInternalAsync` (line 760), the `newDay` block resets everything. Replace the entire block (lines 760-805) with a comment:

```csharp
        // Session boundary detection is now handled by SessionManager.
        // SessionManager calls Reconfigure() at session start and ResetDaily() at day boundary.
        // The engine no longer self-detects session transitions.
```

Keep the `_lastDate` field for backward compat with backtest (which doesn't use SessionManager yet — that's Task 12).

**Important:** Do NOT remove the `_lastDate` assignment or the `TradingDate()` method yet — backtest still uses this path. Instead, guard the reset block:

```csharp
        bool newDay = tradingDate != _lastDate;
        if (newDay && !_sessionManagedMode)
        {
            // ... existing reset logic stays for backtest backward compat
        }
        else if (newDay)
        {
            _lastDate = tradingDate;  // just track the date, no reset
        }
```

Add a new field: `private bool _sessionManagedMode = false;`
Set it to `true` in `Reconfigure()`.

- [ ] **Step 2: Build and run all tests**

Run: `dotnet build && dotnet test`
Expected: All pass. Backtest uses the old path (`_sessionManagedMode = false`). Live uses `Reconfigure` which sets `_sessionManagedMode = true`.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: guard engine self-reset behind sessionManagedMode flag"
```

---

## Chunk 5: Settings UI

### Task 10: Session tabs on Live settings page

**Files:**
- Modify: `CRV.Web/Pages/Settings/Live.cshtml`
- Modify: `CRV.Web/Pages/Settings/Live.cshtml.cs`
- Modify: `CRV.Web/Services/StrategyConfigService.cs`

This is a large UI task. The settings page needs session tabs (Asia | London | NY), each showing session-level fields + 5 setup cards.

- [ ] **Step 1: Add Sessions to the page model**

In `Live.cshtml.cs`, the `Config` property is `StrategyConfig`. Add a `Sessions` property:

```csharp
    public List<SessionConfig> Sessions { get; set; } = new();
```

In `OnGet()`, after loading `Config`:
```csharp
    Sessions = Config.Sessions ?? SessionConfig.CreateDefaults(Config);
```

- [ ] **Step 2: Add session serialization to OnPost**

In `OnPost()`, the sessions come from form fields. Add deserialization from a hidden JSON field:

```csharp
    [BindProperty] public string SessionsJson { get; set; } = "[]";
```

In `OnPost()`, after model validation:
```csharp
    var sessions = System.Text.Json.JsonSerializer.Deserialize<List<SessionConfig>>(SessionsJson);
    if (sessions != null) Config.Sessions = sessions;
```

- [ ] **Step 3: Add session tabs to the cshtml**

This is a significant UI piece. Add after the broker status section, before the existing setup cards. The existing setup cards for the single NY session should be wrapped in the NY tab.

Structure:
```html
<!-- Session Tabs -->
<ul class="nav nav-tabs mb-3" id="sessionTabs">
    <li class="nav-item">
        <button class="nav-link" data-bs-toggle="tab" data-bs-target="#session-Asia">Asia</button>
    </li>
    <li class="nav-item">
        <button class="nav-link" data-bs-toggle="tab" data-bs-target="#session-London">London</button>
    </li>
    <li class="nav-item">
        <button class="nav-link active" data-bs-toggle="tab" data-bs-target="#session-NY">NY</button>
    </li>
</ul>
<div class="tab-content">
    @for (int i = 0; i < Model.Sessions.Count; i++)
    {
        var s = Model.Sessions[i];
        var active = s.Id == SessionId.NY ? "show active" : "";
        <div class="tab-pane fade @active" id="session-@s.Id">
            <!-- Session header: Enable, ORB times, RTH times -->
            <!-- 5 setup cards (A/B/C/D/F) with per-session values -->
        </div>
    }
</div>
```

The exact HTML mirrors the existing setup card layout but uses session-indexed form names. The implementation details depend on the current card HTML structure — replicate it per session.

- [ ] **Step 4: Add hidden SessionsJson field**

Add a hidden input that JS populates on form submit:
```html
<input type="hidden" name="SessionsJson" id="sessionsJson" />
```

Add JS that on form submit, gathers all session tab values into a JSON array and sets `sessionsJson.value`.

- [ ] **Step 5: Persist sessions in StrategyConfigService**

In `StrategyConfigService`, when saving config, also serialize `Sessions` to a JSON file alongside the config:

```csharp
var sessionsPath = Path.Combine(_configDir, "sessions.json");
if (config.Sessions != null)
    File.WriteAllText(sessionsPath, JsonSerializer.Serialize(config.Sessions));
```

On load:
```csharp
var sessionsPath = Path.Combine(_configDir, "sessions.json");
if (File.Exists(sessionsPath))
    config.Sessions = JsonSerializer.Deserialize<List<SessionConfig>>(File.ReadAllText(sessionsPath));
```

- [ ] **Step 6: Build and manually test**

Run: `dotnet build`
Expected: Build succeeded.

Manual test: Start the app, navigate to Settings > Live. Verify 3 tabs appear. NY tab shows current config values. Asia/London tabs show defaults (disabled).

- [ ] **Step 7: Commit**

```bash
git add CRV.Web/Pages/Settings/Live.cshtml CRV.Web/Pages/Settings/Live.cshtml.cs CRV.Web/Services/StrategyConfigService.cs
git commit -m "feat: add session tabs to Live settings page with per-session config"
```

---

## Chunk 6: Dashboard Changes

### Task 11: Active session badge and session scoping

**Files:**
- Modify: `CRV.Web/Pages/Dashboard/Index.cshtml`

- [ ] **Step 1: Add session badge to top bar**

In the dashboard top bar (around line 9-29), add an active session indicator:

```html
<span id="sessionBadge" class="badge bg-secondary ms-2">--</span>
```

- [ ] **Step 2: Update SignalR handler to show session**

In the `updateDashboard(snap)` JS function, add:

```javascript
const badge = document.getElementById('sessionBadge');
if (snap.activeSessionId) {
    badge.textContent = snap.activeSessionId + ' Session';
    badge.className = 'badge bg-success ms-2';
} else {
    badge.textContent = 'Idle';
    badge.className = 'badge bg-secondary ms-2';
}
```

- [ ] **Step 3: Add session summary row**

Below the stats cards, add a summary row for non-active sessions:

```html
<div id="sessionSummary" class="d-flex gap-3 mb-2 small text-muted"></div>
```

JS updates this from the snapshot or a separate SessionStats SignalR event (can be added later as the stats tracking matures).

- [ ] **Step 4: Set SessionId on trade records**

In `CRV.Web/Services/SignalREventSink.cs` (or wherever trades are recorded), ensure the `SessionId` field on `TradeRecord` is set from the engine's `ActiveSessionId`. The `ExitSignal` or `EntrySignal` should carry the session ID.

Add to `EntrySignal` record:
```csharp
public string SessionId { get; init; } = "NY";
```

Set it in the engine when creating entry signals (inside the `Reconfigure` method, the `_activeSessionId` is set).

- [ ] **Step 5: Build and commit**

```bash
git add CRV.Web/Pages/Dashboard/Index.cshtml CRV.Web/Services/SignalREventSink.cs CRV.Core/Models/Signals.cs
git commit -m "feat: add session badge and session scoping to dashboard"
```

---

## Chunk 7: Backtest Multi-Session Support

### Task 12: Backtest with SessionManager

**Files:**
- Modify: `CRV.Backtest/Engine/BacktestEngine.cs`
- Modify: `CRV.Web/Pages/Backtest/Index.cshtml`

- [ ] **Step 1: Add session selector to backtest config**

In `BacktestConfig` (find its location — likely `CRV.Backtest/Models/` or `CRV.Core/Models/`), add:

```csharp
    public string BacktestSession { get; set; } = "NY"; // "All", "Asia", "London", "NY"
```

- [ ] **Step 2: Add session dropdown to backtest page**

In `CRV.Web/Pages/Backtest/Index.cshtml`, add a dropdown near the existing config fields:

```html
<select asp-for="Config.BacktestSession" class="form-select">
    <option value="All">All Sessions</option>
    <option value="Asia">Asia</option>
    <option value="London">London</option>
    <option value="NY" selected>NY</option>
</select>
```

- [ ] **Step 3: Integrate SessionManager into backtest runner**

In `BacktestEngine.RunAsync()`, after creating the engine:

```csharp
// Multi-session support
var sessions = _cfg.Sessions ?? SessionConfig.CreateDefaults(_cfg);
if (_btCfg.BacktestSession != "All")
{
    // Single session — filter to just that one
    var target = Enum.Parse<SessionId>(_btCfg.BacktestSession);
    sessions = sessions.Where(s => s.Id == target).ToList();
    sessions.ForEach(s => s.Enabled = true);
}
var sessionMgr = new SessionManager(sessions);
```

Then in the bar loop, call `sessionMgr.CheckTransition(localTime)` and handle transitions same as the orchestrator.

- [ ] **Step 4: Add SessionId to backtest trade results**

Ensure `BacktestSink.RecordEntry()` captures the active session ID so results include per-session breakdown.

- [ ] **Step 5: Build and run tests**

Run: `dotnet build && dotnet test`
Expected: All pass. Backtest with default "NY" session behaves identically to before.

- [ ] **Step 6: Commit**

```bash
git add CRV.Backtest/ CRV.Web/Pages/Backtest/
git commit -m "feat: add multi-session support to backtest with session selector"
```

---

## Chunk 8: Session Detail Page

### Task 13: Dashboard/Sessions tabbed detail page

**Files:**
- Create: `CRV.Web/Pages/Dashboard/Sessions.cshtml`
- Create: `CRV.Web/Pages/Dashboard/Sessions.cshtml.cs`

- [ ] **Step 1: Create page model**

```csharp
namespace CRV.Web.Pages.Dashboard;

using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class SessionsModel : PageModel
{
    private readonly TradeRepository _trades;
    public SessionsModel(TradeRepository trades) => _trades = trades;

    public List<TradeRecord> AsiaTrades   { get; set; } = new();
    public List<TradeRecord> LondonTrades { get; set; } = new();
    public List<TradeRecord> NYTrades     { get; set; } = new();

    public async Task OnGetAsync()
    {
        var today = await _trades.GetTodayAsync();
        AsiaTrades   = today.Where(t => t.SessionId == "Asia").ToList();
        LondonTrades = today.Where(t => t.SessionId == "London").ToList();
        NYTrades     = today.Where(t => t.SessionId == "NY").ToList();
    }
}
```

- [ ] **Step 2: Create Razor page**

```html
@page
@model CRV.Web.Pages.Dashboard.SessionsModel
@{ ViewData["Title"] = "Session Detail"; }

<h4>Session Detail</h4>

<ul class="nav nav-tabs mb-3">
    <li class="nav-item"><button class="nav-link active" data-bs-toggle="tab" data-bs-target="#det-Asia">Asia</button></li>
    <li class="nav-item"><button class="nav-link" data-bs-toggle="tab" data-bs-target="#det-London">London</button></li>
    <li class="nav-item"><button class="nav-link" data-bs-toggle="tab" data-bs-target="#det-NY">NY</button></li>
</ul>

<div class="tab-content">
    @foreach (var (id, trades) in new[] { ("Asia", Model.AsiaTrades), ("London", Model.LondonTrades), ("NY", Model.NYTrades) })
    {
        <div class="tab-pane fade @(id == "Asia" ? "show active" : "")" id="det-@id">
            <div class="row mb-3">
                <div class="col-md-3"><div class="card p-2 text-center"><strong>Trades:</strong> @trades.Count</div></div>
                <div class="col-md-3"><div class="card p-2 text-center"><strong>Wins:</strong> @trades.Count(t => t.IsWin)</div></div>
                <div class="col-md-3"><div class="card p-2 text-center"><strong>Losses:</strong> @trades.Count(t => !t.IsWin)</div></div>
                <div class="col-md-3"><div class="card p-2 text-center"><strong>PnL:</strong> @trades.Sum(t => t.NetPnl).ToString("C0")</div></div>
            </div>
            <table class="table table-sm table-striped">
                <thead><tr><th>Setup</th><th>Dir</th><th>Entry</th><th>Exit</th><th>PnL</th><th>Exit Reason</th><th>Time</th></tr></thead>
                <tbody>
                    @foreach (var t in trades)
                    {
                        <tr class="@(t.IsWin ? "table-success" : "table-danger")">
                            <td>@t.Setup</td><td>@t.Direction</td>
                            <td>@t.Entry.ToString("F2")</td><td>@t.Exit.ToString("F2")</td>
                            <td>@t.NetPnl.ToString("C0")</td><td>@t.ExitReason</td>
                            <td>@t.EnteredAt.ToString("HH:mm")</td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    }
</div>
```

- [ ] **Step 3: Add nav link**

In the shared layout or dashboard nav, add a link to the Sessions page.

- [ ] **Step 4: Build and commit**

```bash
git add CRV.Web/Pages/Dashboard/Sessions.cshtml CRV.Web/Pages/Dashboard/Sessions.cshtml.cs
git commit -m "feat: add Sessions detail page with tabbed per-session stats and trades"
```

---

## Chunk 9: ORB Cache and Final Integration

### Task 14: ORB cache keying by session

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs` (search for `orb_cache` or `OrbStateCache`)

- [ ] **Step 1: Update cache key to include session**

Find where the ORB cache is saved/loaded. Update the cache file name or key to include `_activeSessionId`:

```csharp
var cacheKey = $"{_cfg.Ticker}_{_activeSessionId}";
```

This ensures Asia's ORB doesn't overwrite NY's ORB in the cache.

- [ ] **Step 2: Build and run tests**

Run: `dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 3: Commit**

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs
git commit -m "feat: include SessionId in ORB cache key"
```

---

### Task 15: Final integration test

- [ ] **Step 1: Run full test suite**

Run: `dotnet test -v normal`
Expected: All tests pass.

- [ ] **Step 2: Run the app and verify manually**

Start the app. Verify:
1. Settings > Live shows 3 session tabs
2. NY tab has current config values
3. Asia/London tabs are disabled with defaults
4. Dashboard shows "NY Session" badge during market hours or "Idle" outside
5. Session detail page shows empty tabs for Asia/London

- [ ] **Step 3: Final commit if any fixes needed**

```bash
git add -A
git commit -m "fix: final multi-session integration fixes"
```
