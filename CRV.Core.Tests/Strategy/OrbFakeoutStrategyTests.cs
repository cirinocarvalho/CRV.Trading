// CRV.Core.Tests/Strategy/OrbFakeoutStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// Tests for OrbFakeoutStrategy — a pure signal generator.
/// After phase2 simplification, the strategy only produces EntrySignal.
/// Trade lifecycle (exit, partial, BE) is managed by BrokerEventHandler.
/// IsActive is controlled externally via SetInTrade().
///
/// NOTE: OrbFakeout arms and enters on the same bar. Once armed, the bar-level
/// entry fires immediately (TryEntry called from ProcessArm). The entry price
/// is orb.Low for long (fade bear breakout) or orb.High for short (fade bull breakout).
/// </summary>
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
        FakeoutPenetration: 0m,
        SessionFakeoutBull: false, SessionFakeoutBear: false,
        SessionRangeHigh: 0m, SessionRangeLow: 0m);

    private static ModuleState FakeoutBullModules() =>
        EmptyModules() with { OrbFakeoutBull = true, FakeoutPenetration = 2.5m };

    private static ModuleState FakeoutBearModules() =>
        EmptyModules() with { OrbFakeoutBear = true, FakeoutPenetration = 2.5m };

    private static Bar MakeBar(decimal open, decimal high, decimal low, decimal close,
        DateTime? time = null)
        => new(time ?? new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc),
               open, high, low, close, 100);

    // ═══════════════════════════════════════════════════════════════
    // Arming + immediate entry on bar
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ArmsAndEnters_Short_WhenOrbFakeoutBull()
    {
        // OrbFakeoutBull = breakout was long -> arm SHORT (fade it)
        // Bar-level entry fires immediately: ep = orb.High = 5200
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5202m, 5205m, 5198m, 5201m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBullModules());

        // Entry fires on the same bar as arm
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Short, s.PendingEntry!.Direction);
        Assert.Equal(5200m, s.PendingEntry.Entry); // entry at orb.High
        Assert.False(s.IsArmed); // state back to 0 after entry
    }

    [Fact]
    public void ArmsAndEnters_Long_WhenOrbFakeoutBear()
    {
        // OrbFakeoutBear = breakout was short -> arm LONG (fade it)
        // Bar-level entry fires immediately: ep = orb.Low = 5180
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(5180m, s.PendingEntry.Entry); // entry at orb.Low
    }

    [Fact]
    public void DoesNotArm_WhenNoFakeout()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5190m, 5195m, 5185m, 5192m);

        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void DoesNotArm_WhenOrbNotSet()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orbNotSet = MakeOrb() with { IsSet = false };
        var bar = MakeBar(5190m, 5195m, 5185m, 5192m);

        s.OnBar(bar, orbNotSet, MakeIndicators(), FakeoutBullModules());

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void DoesNotArm_WhenMaxTradesReached()
    {
        var cfg = DefaultConfig();
        cfg.MaxTrades = 1;
        var s = new OrbFakeoutStrategy(cfg);

        // First entry uses the trade count
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Try again — should not arm
        var bar2 = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar2, orb, MakeIndicators(), FakeoutBearModules());

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
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

    // ═══════════════════════════════════════════════════════════════
    // Entry levels
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Entry_UsesCalcLevels_ForStopTargetPartial()
    {
        // Long entry at orb.Low=5180, orbRange=20, stopPct=0.10 -> stopDist=2
        // stop=5178, target=5200, partial=5190
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

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
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void Entry_AppliesTickOffset()
    {
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;  // 2 ticks = 0.50
        var s = new OrbFakeoutStrategy(cfg);
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.NotNull(s.PendingEntry);
        // Long entry at orb.Low=5180 + 2 ticks (0.50) = 5180.50
        Assert.Equal(5180.50m, s.PendingEntry!.Entry);
    }

    // ═══════════════════════════════════════════════════════════════
    // ForceExit / Reset / Snapshot
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ForceExit_ClearsState()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);

        s.ForceExit(5185m, new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc));

        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);

        s.Reset();

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void GetSnapshot_ReturnsCorrectState()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        // After entry fires, snapshot state should be 0
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        var snap = s.GetSnapshot();

        Assert.Equal(SetupId.C, snap.SetupId);
        Assert.Equal(0, snap.State); // entry fired, back to idle
        Assert.True(snap.Enabled);
    }

    [Fact]
    public void BullTraded_Guard_PreventsSameSideRearm()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        // Enter long (fade bear breakout) — sets _bullTraded = true
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        s.ClearPendingSignals();

        // Try to arm LONG again — should not because _bullTraded = true
        var bar2 = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar2, orb, MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void DoesNotReenter_WhenInTrade()
    {
        var s = new OrbFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Simulate broker confirming fill
        s.SetInTrade(true);
        Assert.True(s.IsActive);

        // Try to arm again — blocked by _inTrade
        var bar2 = MakeBar(5202m, 5205m, 5198m, 5201m);
        s.OnBar(bar2, orb, MakeIndicators(), FakeoutBullModules());

        Assert.Null(s.PendingEntry);
    }
}
