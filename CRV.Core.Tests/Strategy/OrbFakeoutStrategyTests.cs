// CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class OrbFakeoutStrategyTests
{
    private static StrategySetupConfig DefaultConfig() => new()
    {
        Name = "C", SetupId = SetupId.C,
        StrategyType = StrategyType.OrbFakeout,
        Enabled = true,
        Ticker = "NQM26", PointValue = 20m, TickSize = 0.25m,
        Contracts = 2, MaxContracts = 4, HiVolMult = 1.0m,
        StopPct = 0.10m, TargetPct = 100, PartialPct = 50,
        NearPct = 0.15m, MinRr = 1.0m, Mode = "Conservative",
        MaxTrades = 3,
        UsePartial = false, UseBe = false,
        UseVwap = false, UseOrbClose = false,
        CutoffHour = 14, CutoffMinute = 30,
    };

    // ORB: high=5200, low=5180, range=20
    private static OrbState MakeOrb(decimal high = 5200m, decimal low = 5180m) => new(
        High: high, Low: low, Mid: (high + low) / 2m, Range: high - low,
        IsSet: true, BullClose: true, BearClose: false, AtrRatio: 0.8m);

    private static IndicatorState MakeIndicators(decimal vwap = 5190m) => new(
        Atr: 25m, Vwap: vwap,
        VwapUpper1: vwap + 10, VwapLower1: vwap - 10,
        VwapUpper2: vwap + 20, VwapLower2: vwap - 20,
        LastClose: 5195m);

    private static ModuleState EmptyModules() => new(
        SessionHigh: 5210m, SessionLow: 5170m,
        AsiaHigh: 0, AsiaLow: 0, AsiaCompressed: false,
        LondonHigh: 0, LondonLow: 0,
        PDH: 5220m, PDL: 5160m, PWH: 5230m, PWL: 5150m,
        CurrentSession: SessionType.NYOpen,
        LondonSweptAsiaHigh: false, LondonSweptAsiaLow: false,
        NYBullExpansion: false, NYBearExpansion: false,
        ActiveSweeps: Array.Empty<SweepEvent>(),
        VwapState: 0, BullVwapReclaim: false, BearVwapReject: false,
        IsBullDrive: false, IsBearDrive: false,
        TrendDayBullScore: 0, TrendDayBearScore: 0,
        TrendDayBull: false, TrendDayBear: false,
        OrbFakeoutBull: false, OrbFakeoutBear: false,
        FakeoutPenetration: 0m);

    /// <summary>
    /// Modules with OrbFakeoutBull = true (breakout was long → strategy should arm SHORT).
    /// </summary>
    private static ModuleState FakeoutBullModules() =>
        EmptyModules() with { OrbFakeoutBull = true, FakeoutPenetration = 2.5m };

    /// <summary>
    /// Modules with OrbFakeoutBear = true (breakout was short → strategy should arm LONG).
    /// </summary>
    private static ModuleState FakeoutBearModules() =>
        EmptyModules() with { OrbFakeoutBear = true, FakeoutPenetration = 2.5m };

    private static Bar MakeBar(decimal open, decimal high, decimal low, decimal close,
        DateTime? time = null)
        => new(time ?? new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc),
               open, high, low, close, 100);

    // ─── Helper: arm SHORT (after a bull breakout fakeout) ──────────────────
    // OrbFakeoutBull = true means breakout was LONG → arm SHORT (fade it)
    private static OrbFakeoutStrategy ArmShort(OrbFakeoutStrategy s)
    {
        var orb = MakeOrb();
        var bar = MakeBar(5202m, 5205m, 5198m, 5201m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBullModules());
        return s;
    }

    // ─── Helper: arm LONG (after a bear breakout fakeout) ───────────────────
    // OrbFakeoutBear = true means breakout was SHORT → arm LONG (fade it)
    private static OrbFakeoutStrategy ArmLong(OrbFakeoutStrategy s)
    {
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        return s;
    }

    // ─── Helper: enter LONG ─────────────────────────────────────────────────
    // After arming LONG (fakeout bear), entry fires when bar.Close >= orbLow (5180)
    // Entry at orbLow=5180, stop=5178 → bar.Low must be > 5178 to avoid same-bar stop hit
    private static OrbFakeoutStrategy EnterLong(OrbFakeoutStrategy s)
    {
        ArmLong(s);
        s.ClearPendingSignals();
        var orb = MakeOrb();
        var entryBar = MakeBar(5179m, 5185m, 5179m, 5182m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: enter SHORT ────────────────────────────────────────────────
    // After arming SHORT (fakeout bull), entry fires when bar.Close <= orbHigh (5200)
    // Entry at orbHigh=5200, stop=5202 → bar.High must be < 5202 to avoid same-bar stop hit
    private static OrbFakeoutStrategy EnterShort(OrbFakeoutStrategy s)
    {
        ArmShort(s);
        s.ClearPendingSignals();
        var orb = MakeOrb();
        var entryBar = MakeBar(5201m, 5201m, 5198m, 5199m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Arming
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Arms_Short_WhenOrbFakeoutBull()
    {
        // OrbFakeoutBull = breakout was long → arm SHORT (fade it)
        var s = new OrbFakeoutStrategy(DefaultConfig());
        ArmShort(s);

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Arms_Long_WhenOrbFakeoutBear()
    {
        // OrbFakeoutBear = breakout was short → arm LONG (fade it)
        var s = new OrbFakeoutStrategy(DefaultConfig());
        ArmLong(s);

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void DoesNotArm_WhenNoFakeout()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5190m, 5195m, 5185m, 5192m);

        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void DoesNotArm_WhenOrbNotSet()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orbNotSet = MakeOrb() with { IsSet = false };
        var bar = MakeBar(5190m, 5195m, 5185m, 5192m);

        s.OnBar(bar, orbNotSet, MakeIndicators(), FakeoutBullModules());

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void DoesNotArm_WhenMaxTradesReached()
    {
        var cfg = DefaultConfig();
        cfg.MaxTrades = 1;
        var s = new OrbFakeoutStrategy(cfg);

        // Complete one trade
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        // Hit stop to close and increment counter
        var orb = MakeOrb(); // entry at orbLow=5180, stop = 5180 - 20*0.10 = 5178
        var stopBar = MakeBar(5179m, 5180m, 5175m, 5176m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Try to arm again — should not because MaxTrades=1 exhausted
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.False(s.IsArmed);
    }

    [Fact]
    public void DoesNotArm_WhenDisabled()
    {
        var cfg = DefaultConfig();
        cfg.Enabled = false;
        var s = new OrbFakeoutStrategy(cfg);
        var orb = MakeOrb();
        var bar = MakeBar(5190m, 5195m, 5185m, 5192m);

        s.OnBar(bar, orb, MakeIndicators(), FakeoutBullModules());

        Assert.False(s.IsArmed);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Entry
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Enters_Long_WhenPriceCrossesAboveOrbLow()
    {
        // Arm LONG (fakeout bear), then bar.Close >= orbLow
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        Assert.True(s.IsActive);
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(SetupId.C, s.PendingEntry.Setup);
    }

    [Fact]
    public void Enters_Short_WhenPriceCrossesBelowOrbHigh()
    {
        // Arm SHORT (fakeout bull), then bar.Close <= orbHigh
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterShort(s);

        Assert.True(s.IsActive);
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Short, s.PendingEntry!.Direction);
        Assert.Equal(SetupId.C, s.PendingEntry.Setup);
    }

    [Fact]
    public void Entry_UsesCalcLevels_ForStopTargetPartial()
    {
        // orbRange=20, stopPct=0.10 → stopDist=2, targetPct=100 → targetDist=20
        // Long entry at orbLow=5180: stop=5178, target=5200, partial=5190
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        var entry = s.PendingEntry!;
        Assert.Equal(5180m, entry.Entry);
        Assert.Equal(5178m, entry.Stop);      // 5180 - 20*0.10 = 5178
        Assert.Equal(5200m, entry.Target);     // 5180 + 20*1.00 = 5200
        Assert.Equal(5190m, entry.Partial);    // 5180 + 20*0.50 = 5190
    }

    [Fact]
    public void Entry_RespectsMinRr()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99.0m;  // impossibly high
        var s = new OrbFakeoutStrategy(cfg);
        ArmLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        var entryBar = MakeBar(5179m, 5185m, 5178m, 5182m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsActive);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void Entry_AppliesTickOffset()
    {
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;  // 2 ticks = 0.50
        var s = new OrbFakeoutStrategy(cfg);
        ArmLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        // entry would be orbLow=5180, but offset +2 ticks for long → 5180.50
        // stop = 5180.50 - 2 = 5178.50 → bar.Low must be > 5178.50
        var entryBar = MakeBar(5179m, 5185m, 5179m, 5182m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());

        Assert.True(s.IsActive);
        Assert.Equal(5180.50m, s.PendingEntry!.Entry);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Exit
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Exits_OnTarget()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // Long entry at 5180, target=5200. Bar hits target.
        var exitBar = MakeBar(5195m, 5205m, 5194m, 5202m);
        s.OnBar(exitBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Exits_OnStop()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // Long entry at 5180, stop=5178. Bar hits stop.
        var stopBar = MakeBar(5179m, 5180m, 5175m, 5176m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Stop, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Tick-level entry/exit
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OnTick_Enters_Long_WhenArmed_AndPriceCrossesOrbLow()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        ArmLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc);
        // Price crosses above orbLow=5180
        s.OnTick(5180m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.True(s.IsActive);
    }

    [Fact]
    public void OnTick_Enters_Short_WhenArmed_AndPriceCrossesOrbHigh()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        ArmShort(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc);
        // Price crosses below orbHigh=5200
        s.OnTick(5200m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Short, s.PendingEntry!.Direction);
        Assert.True(s.IsActive);
    }

    [Fact]
    public void OnTick_Exits_OnTarget()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        // Long entry at 5180, target=5200
        s.OnTick(5200m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void OnTick_Exits_OnStop()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        // Long entry at 5180, stop=5178
        s.OnTick(5178m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Stop, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Partial/BE
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Partial_And_BE_ProduceSignals()
    {
        var cfg = DefaultConfig();
        cfg.UsePartial = true;
        cfg.UseBe = true;
        cfg.Contracts = 4;
        var s = new OrbFakeoutStrategy(cfg);
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // entry=5180, partial=5190, target=5200, stop=5178
        // Bar hits partial but not target, stays above entry (no BE hit)
        var partBar = MakeBar(5188m, 5195m, 5187m, 5192m);
        s.OnBar(partBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingPartial);
        Assert.NotNull(s.PendingBE);
        Assert.True(s.IsActive); // trade still open
    }

    [Fact]
    public void OnTick_Partial_And_BE()
    {
        var cfg = DefaultConfig();
        cfg.UsePartial = true;
        cfg.UseBe = true;
        cfg.Contracts = 4;
        var s = new OrbFakeoutStrategy(cfg);
        EnterLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        // entry=5180, partial=5190 → tick at partial
        s.OnTick(5190m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingPartial);
        Assert.NotNull(s.PendingBE);
        Assert.True(s.IsActive);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ForceExit / Reset / Snapshot
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForceExit_ProducesExitSignal()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        s.ForceExit(5185m, new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.SessionEnd, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        ArmLong(s);
        Assert.True(s.IsArmed);

        s.Reset();

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
        Assert.Null(s.PendingEntry);
        Assert.Null(s.PendingExit);
    }

    [Fact]
    public void GetSnapshot_ReturnsCorrectState()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        ArmLong(s);

        var snap = s.GetSnapshot();

        Assert.Equal(SetupId.C, snap.SetupId);
        Assert.True(snap.IsArmed);
        Assert.False(snap.IsActive);
        Assert.Equal(DefaultConfig().MaxTrades, snap.MaxTrades);
        Assert.True(snap.Enabled);
    }

    [Fact]
    public void GetActiveTrade_ReturnsView_WhenActive()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        var view = s.GetActiveTrade(5185m);

        Assert.NotNull(view);
        Assert.Equal(SetupId.C, view!.Setup);
        Assert.Equal(Direction.Long, view.Direction);
        Assert.Equal(5180m, view.Entry);
    }

    [Fact]
    public void GetActiveTrade_ReturnsNull_WhenNotActive()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var view = s.GetActiveTrade(5185m);
        Assert.Null(view);
    }

    [Fact]
    public void ApplyFill_AdjustsLevels()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        // Original entry at 5180; simulate broker fill at 5180.50
        s.ApplyFill(5180.50m);

        var view = s.GetActiveTrade(5185m);
        Assert.NotNull(view);
        Assert.Equal(5180.50m, view!.Entry);
        // Stop, target, partial should shift by +0.50
        Assert.Equal(5178.50m, view.CurrentStop);
        Assert.Equal(5200.50m, view.Target);
    }

    [Fact]
    public void BullTraded_Guard_PreventsSameSideRearm()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        // Enter long and hit stop (bearTraded not set for long exit on stop)
        // Actually, for long trade stopped out: we DO set bullTraded (the trade was long)
        // This prevents arming long again immediately
        EnterLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        // Hit stop
        var stopBar = MakeBar(5179m, 5180m, 5175m, 5176m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Try to arm LONG again — should not arm because _bullTraded = true
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        // _bullTraded prevents re-arming long
        Assert.False(s.IsArmed);
    }

    [Fact]
    public void AllowRearmAfterBe_DoesNotSetTradedGuard()
    {
        var cfg = DefaultConfig();
        cfg.AllowRearmAfterBe = true;
        cfg.UsePartial = true;
        cfg.UseBe = true;
        cfg.Contracts = 4;
        var s = new OrbFakeoutStrategy(cfg);
        EnterLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        // Hit partial (5190) first, then BE stop (5180)
        var partBar = MakeBar(5188m, 5195m, 5187m, 5192m);
        s.OnBar(partBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();

        // Now bar hits BE stop at entry=5180
        var beBar = MakeBar(5182m, 5183m, 5178m, 5179m);
        s.OnBar(beBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Try to arm LONG again — should be allowed because BE stop didn't set bullTraded
        var armBar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(armBar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.True(s.IsArmed);
    }

    [Fact]
    public void RevertEntry_RestoresToArmedState()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        Assert.True(s.IsActive);

        s.RevertEntry();

        Assert.False(s.IsActive);
        Assert.True(s.IsArmed);
    }
}
