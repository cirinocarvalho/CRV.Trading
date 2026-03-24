// CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class SessionFakeoutStrategyTests
{
    private static StrategySetupConfig DefaultConfig() => new()
    {
        Name = "D", SetupId = SetupId.D,
        StrategyType = StrategyType.SessionFakeout,
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

    // ORB: high=5200, low=5180, range=20 (needed as fallback range)
    private static OrbState MakeOrb(decimal high = 5200m, decimal low = 5180m) => new(
        High: high, Low: low, Mid: (high + low) / 2m, Range: high - low,
        IsSet: true, BullClose: true, BearClose: false, AtrRatio: 0.8m);

    private static IndicatorState MakeIndicators(decimal vwap = 5190m) => new(
        Atr: 25m, Vwap: vwap,
        VwapUpper1: vwap + 10, VwapLower1: vwap - 10,
        VwapUpper2: vwap + 20, VwapLower2: vwap - 20,
        LastClose: 5195m);

    // Session range: 5210 high, 5170 low => range = 40
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
        FakeoutPenetration: 0m,
        SessionFakeoutBull: false, SessionFakeoutBear: false,
        SessionRangeHigh: 5210m, SessionRangeLow: 5170m);

    /// <summary>
    /// Modules with SessionFakeoutBull = true (breakout was long -> strategy should arm SHORT).
    /// </summary>
    private static ModuleState FakeoutBullModules() =>
        EmptyModules() with { SessionFakeoutBull = true };

    /// <summary>
    /// Modules with SessionFakeoutBear = true (breakout was short -> strategy should arm LONG).
    /// </summary>
    private static ModuleState FakeoutBearModules() =>
        EmptyModules() with { SessionFakeoutBear = true };

    private static Bar MakeBar(decimal open, decimal high, decimal low, decimal close,
        DateTime? time = null)
        => new(time ?? new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc),
               open, high, low, close, 100);

    // ─── Helper: arm SHORT (after a bull breakout fakeout on session range) ──
    // SessionFakeoutBull = true means breakout was LONG -> arm SHORT (fade it)
    private static SessionFakeoutStrategy ArmShort(SessionFakeoutStrategy s)
    {
        var orb = MakeOrb();
        var bar = MakeBar(5212m, 5215m, 5208m, 5211m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBullModules());
        return s;
    }

    // ─── Helper: arm LONG (after a bear breakout fakeout on session range) ──
    // SessionFakeoutBear = true means breakout was SHORT -> arm LONG (fade it)
    private static SessionFakeoutStrategy ArmLong(SessionFakeoutStrategy s)
    {
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        return s;
    }

    // ─── Helper: enter LONG ──────────────────────────────────────────
    // After arming LONG (fakeout bear), entry fires via tick at market price
    // when price >= SessionRangeLow (5170).
    // Entry at tick price=5171, levels anchored to tick price 5171: stop=5167, target=5211, partial=5191
    private static SessionFakeoutStrategy EnterLong(SessionFakeoutStrategy s)
    {
        ArmLong(s);
        s.ClearPendingSignals();
        var orb = MakeOrb();
        // Tick entry: price crosses above session range low
        s.OnTick(5171m, DateTime.UtcNow, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: enter SHORT ─────────────────────────────────────────
    // After arming SHORT (fakeout bull), entry fires via tick at market price
    // when price <= SessionRangeHigh (5210).
    // Entry at tick price=5209, levels anchored to tick price 5209: stop=5213, target=5169, partial=5189
    private static SessionFakeoutStrategy EnterShort(SessionFakeoutStrategy s)
    {
        ArmShort(s);
        s.ClearPendingSignals();
        var orb = MakeOrb();
        // Tick entry: price crosses below session range high
        s.OnTick(5209m, DateTime.UtcNow, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ═════════════════════════════════════════════════════════════════
    // Arming
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Arms_Short_WhenSessionFakeoutBull()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        ArmShort(s);

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Arms_Long_WhenSessionFakeoutBear()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        ArmLong(s);

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void DoesNotArm_WhenNoFakeout()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5190m, 5195m, 5185m, 5192m);

        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void DoesNotArm_WhenOrbNotSet()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
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
        var s = new SessionFakeoutStrategy(cfg);

        // Complete one trade
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        // Hit stop to close and increment counter
        // Entry at tick=5171, levels anchored to tick price 5171, stop = 5171 - 40*0.10 = 5167
        var orb = MakeOrb();
        var stopBar = MakeBar(5166m, 5167m, 5163m, 5164m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Try to arm again — should not because MaxTrades=1 exhausted
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.False(s.IsArmed);
    }

    [Fact]
    public void DoesNotArm_WhenDisabled()
    {
        var cfg = DefaultConfig();
        cfg.Enabled = false;
        var s = new SessionFakeoutStrategy(cfg);
        var orb = MakeOrb();
        var bar = MakeBar(5190m, 5195m, 5185m, 5192m);

        s.OnBar(bar, orb, MakeIndicators(), FakeoutBullModules());

        Assert.False(s.IsArmed);
    }

    // ═════════════════════════════════════════════════════════════════
    // Entry
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Enters_Long_WhenPriceCrossesAboveSessionRangeLow()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        Assert.True(s.IsActive);
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(SetupId.D, s.PendingEntry.Setup);
    }

    [Fact]
    public void Enters_Short_WhenPriceCrossesBelowSessionRangeHigh()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterShort(s);

        Assert.True(s.IsActive);
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Short, s.PendingEntry!.Direction);
        Assert.Equal(SetupId.D, s.PendingEntry.Setup);
    }

    [Fact]
    public void Entry_UsesSessionRange_ForStopTargetPartial()
    {
        // SessionRange = 5210 - 5170 = 40
        // Long entry via tick at 5171, levels anchored to tick price 5171
        // stop=5167, target=5211, partial=5191
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        var entry = s.PendingEntry!;
        Assert.Equal(5171m, entry.Entry);
        Assert.Equal(5167m, entry.Stop);       // 5171 - 40*0.10 = 5167
        Assert.Equal(5211m, entry.Target);      // 5171 + 40*1.00 = 5211
        Assert.Equal(5191m, entry.Partial);     // 5171 + 40*0.50 = 5191
    }

    [Fact]
    public void Entry_RespectsMinRr()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99.0m;  // impossibly high
        var s = new SessionFakeoutStrategy(cfg);
        ArmLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        var entryBar = MakeBar(5169m, 5175m, 5169m, 5172m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsActive);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void Entry_AppliesTickOffset()
    {
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;  // 2 ticks = 0.50
        var s = new SessionFakeoutStrategy(cfg);
        ArmLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        // Tick entry at 5171, offset +2 ticks for long => 5171 + 0.50 = 5171.50
        s.OnTick(5171m, DateTime.UtcNow, orb, MakeIndicators(), EmptyModules());

        Assert.True(s.IsActive);
        Assert.Equal(5171.50m, s.PendingEntry!.Entry);
    }

    // ═════════════════════════════════════════════════════════════════
    // Exit
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Exits_OnTarget()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // Long entry at 5171, target=5211. Bar hits target.
        var exitBar = MakeBar(5205m, 5215m, 5204m, 5212m);
        s.OnBar(exitBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Exits_OnStop()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // Long entry at 5171, stop=5167. Bar hits stop.
        var stopBar = MakeBar(5166m, 5167m, 5163m, 5164m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Stop, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    // ═════════════════════════════════════════════════════════════════
    // Tick-level entry/exit
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void OnTick_Enters_Long_WhenArmed_AndPriceCrossesSessionRangeLow()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        ArmLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc);
        // Price crosses above srLow=5170
        s.OnTick(5170m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.True(s.IsActive);
    }

    [Fact]
    public void OnTick_Enters_Short_WhenArmed_AndPriceCrossesSessionRangeHigh()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        ArmShort(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc);
        // Price crosses below srHigh=5210
        s.OnTick(5210m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Short, s.PendingEntry!.Direction);
        Assert.True(s.IsActive);
    }

    [Fact]
    public void OnTick_Exits_OnTarget()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        // Long entry at 5171, target=5211
        s.OnTick(5211m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void OnTick_Exits_OnStop()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        // Long entry at 5171, stop=5167
        s.OnTick(5167m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Stop, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    // ═════════════════════════════════════════════════════════════════
    // Partial/BE
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Partial_And_BE_ProduceSignals()
    {
        var cfg = DefaultConfig();
        cfg.UsePartial = true;
        cfg.UseBe = true;
        cfg.Contracts = 4;
        var s = new SessionFakeoutStrategy(cfg);
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // entry=5171, partial=5191, target=5211, stop=5167
        // Bar hits partial but not target, stays above entry (no BE hit)
        var partBar = MakeBar(5190m, 5196m, 5189m, 5193m);
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
        var s = new SessionFakeoutStrategy(cfg);
        EnterLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        // entry=5171, partial=5191 => tick at partial
        s.OnTick(5191m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingPartial);
        Assert.NotNull(s.PendingBE);
        Assert.True(s.IsActive);
    }

    // ═════════════════════════════════════════════════════════════════
    // ForceExit / Reset / Snapshot
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void ForceExit_ProducesExitSignal()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        s.ForceExit(5175m, new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.SessionEnd, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
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
        var s = new SessionFakeoutStrategy(DefaultConfig());
        ArmLong(s);

        var snap = s.GetSnapshot();

        Assert.Equal(SetupId.D, snap.SetupId);
        Assert.True(snap.IsArmed);
        Assert.False(snap.IsActive);
        Assert.Equal(DefaultConfig().MaxTrades, snap.MaxTrades);
        Assert.True(snap.Enabled);
    }

    [Fact]
    public void GetActiveTrade_ReturnsView_WhenActive()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        var view = s.GetActiveTrade(5175m);

        Assert.NotNull(view);
        Assert.Equal(SetupId.D, view!.Setup);
        Assert.Equal(Direction.Long, view.Direction);
        Assert.Equal(5171m, view.Entry);
    }

    [Fact]
    public void GetActiveTrade_ReturnsNull_WhenNotActive()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var view = s.GetActiveTrade(5175m);
        Assert.Null(view);
    }

    [Fact]
    public void ApplyFill_AdjustsLevels()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        // Original entry at 5171; simulate broker fill at 5170.50
        s.ApplyFill(5170.50m);

        var view = s.GetActiveTrade(5175m);
        Assert.NotNull(view);
        Assert.Equal(5170.50m, view!.Entry);
        // Stop, target, partial should shift by -0.50 (fill lower than entry)
        Assert.Equal(5166.50m, view.CurrentStop);
        Assert.Equal(5210.50m, view.Target);
    }

    [Fact]
    public void BullTraded_Guard_PreventsSameSideRearm()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        // Hit stop: entry=5171, stop=5167
        var stopBar = MakeBar(5166m, 5167m, 5163m, 5164m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Try to arm LONG again — should not arm because _bullTraded = true
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
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
        var s = new SessionFakeoutStrategy(cfg);
        EnterLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        // Hit partial (5191) first
        var partBar = MakeBar(5190m, 5196m, 5189m, 5193m);
        s.OnBar(partBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();

        // Now bar hits BE stop at entry=5171
        var beBar = MakeBar(5172m, 5173m, 5167m, 5168m);
        s.OnBar(beBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Try to arm LONG again — should be allowed because BE stop didn't set bullTraded
        var armBar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(armBar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.True(s.IsArmed);
    }

    [Fact]
    public void RevertEntry_RestoresToArmedState()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);
        Assert.True(s.IsActive);

        s.RevertEntry();

        Assert.False(s.IsActive);
        Assert.True(s.IsArmed);
    }

    // ═════════════════════════════════════════════════════════════════
    // Session range specific: uses session range, not ORB
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Entry_UsesSessionRange_NotOrb()
    {
        // Verify that entry is at session range level, not ORB level
        var s = new SessionFakeoutStrategy(DefaultConfig());
        EnterLong(s);

        // Session range low = 5170, ORB low = 5180 — entry via tick at 5171
        Assert.Equal(5171m, s.PendingEntry!.Entry);
    }

    [Fact]
    public void Entry_FallsBackToOrbRange_WhenSessionRangeZero()
    {
        // If session range is zero (SessionRangeHigh == SessionRangeLow), fall back to ORB range
        var s = new SessionFakeoutStrategy(DefaultConfig());
        ArmLong(s);
        s.ClearPendingSignals();

        var orb = MakeOrb(); // range=20
        var modules = EmptyModules() with { SessionRangeHigh = 5170m, SessionRangeLow = 5170m };
        // Tick entry at srLow=5170, price=5171
        s.OnTick(5171m, DateTime.UtcNow, orb, MakeIndicators(), modules);

        Assert.True(s.IsActive);
        // With ORB range=20, anchor=tick price 5171: stop = 5171 - 20*0.10 = 5169, target = 5171 + 20 = 5191
        Assert.Equal(5171m, s.PendingEntry!.Entry);
        Assert.Equal(5169m, s.PendingEntry.Stop);
        Assert.Equal(5191m, s.PendingEntry.Target);
    }
}
