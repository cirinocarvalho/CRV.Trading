// CRV.Core.Tests/Strategy/PullbackStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// Tests for PullbackStrategy — a pure signal generator.
/// After phase2 simplification, the strategy only produces EntrySignal.
/// Trade lifecycle (exit, partial, BE) is managed by BrokerEventHandler.
/// IsActive is controlled externally via SetInTrade().
/// </summary>
public class PullbackStrategyTests
{
    private static StrategySetupConfig DefaultConfig() => new()
    {
        Name = "A", SetupId = SetupId.A,
        StrategyType = StrategyType.Pullback,
        Enabled = true,
        Ticker = "NQM26", PointValue = 20m, TickSize = 0.25m,
        Contracts = 2, MaxContracts = 4, HiVolMult = 1.0m,
        StopPct = 0.10m, TargetPct = 100, PartialPct = 50,
        NearPct = 0.15m, MinRr = 1.0m, Mode = "Conservative",
        PullbackPct = 0.50m, MaxTrades = 3,
        UsePartial = false, UseBe = false,
        UseVwap = false, UseOrbClose = false,
        CutoffHour = 14, CutoffMinute = 30,
        OrderType = "Limit",
    };

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

    private static Bar MakeBar(decimal open, decimal high, decimal low, decimal close,
        DateTime? time = null)
        => new(time ?? new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc),
               open, high, low, close, 100);

    // ─── Helper: arm the strategy long ─────────────────────────────────────
    // OrbHigh = 5200, NearPct = 0.15, OrbRange = 20 → nearDist = 3.0
    // Bar.High must be >= 5200 - 3 = 5197 to arm long
    private static PullbackStrategy ArmLong(PullbackStrategy s)
    {
        var orb = MakeOrb();
        var bar = MakeBar(5198m, 5198m, 5195m, 5196m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: arm the strategy short ────────────────────────────────────
    // OrbLow = 5180, nearDist = 3 → bar.Low <= 5180 + 3 = 5183 to arm short
    // UseVwap = false so no VWAP filter
    private static PullbackStrategy ArmShort(PullbackStrategy s)
    {
        var orb = MakeOrb();
        // Need BearClose = true for short, or UseOrbClose = false
        var orbWithBear = orb with { BearClose = true };
        var bar = MakeBar(5182m, 5185m, 5182m, 5183m);
        s.OnBar(bar, orbWithBear, MakeIndicators(), EmptyModules());
        return s;
    }

    [Fact]
    public void Arms_Long_WhenBarNearOrbHigh()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(); // high=5200, low=5180, range=20
        // nearDist = 20 * 0.15 = 3.0 → arm when bar.High >= 5197
        var bar = MakeBar(5198m, 5198m, 5195m, 5196m);

        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Arms_Short_WhenBarNearOrbLow()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb() with { BearClose = true };
        // nearDist = 3 → arm when bar.Low <= 5183
        var bar = MakeBar(5182m, 5185m, 5182m, 5183m);

        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void DoesNotArm_WhenOrbNotSet()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orbNotSet = MakeOrb() with { IsSet = false };
        var bar = MakeBar(5198m, 5198m, 5195m, 5196m);

        s.OnBar(bar, orbNotSet, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Enters_Long_OnPullback_Conservative()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(); // high=5200, low=5180, range=20
        // Arm it: bar near orbHigh
        ArmLong(s);
        Assert.True(s.IsArmed);

        // PullbackPct = 0.50 → pbPts = 20 * 0.50 = 10 → longPb = 5200 - 10 = 5190
        // tickTol = 0.25 * 2 = 0.5
        // Entry fires when bar.Low <= longPb + tickTol = 5190.5
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        // IsActive is set externally by SetInTrade(), not by entry signal generation
        Assert.False(s.IsArmed); // state returns to 0 after entry
    }

    [Fact]
    public void Enters_Immediately_Aggressive()
    {
        var cfg = DefaultConfig();
        cfg.Mode = "Aggressive";
        var s = new PullbackStrategy(cfg);
        var orb = MakeOrb(); // high=5200, low=5180, range=20

        // In aggressive mode: arming and entry happen on the SAME bar.
        var bar = MakeBar(5199m, 5199m, 5198m, 5198m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        // Entry should have fired on the arm bar itself (no pullback needed in aggressive mode)
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.False(s.IsArmed); // state returns to 0 after entry
    }

    [Fact]
    public void OnTick_Enters_WhenArmed_AndPullbackHit()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(); // high=5200, low=5180, range=20
        ArmLong(s);
        s.ClearPendingSignals();

        // longPb = 5200 - 20*0.50 = 5190, tickTol = 0.5 → fires when price <= 5190.5
        var utc = new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc);
        s.OnTick(5190m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
    }

    [Fact]
    public void DoesNotArm_WhenMaxTradesReached()
    {
        var cfg = DefaultConfig();
        cfg.MaxTrades = 1;
        var s = new PullbackStrategy(cfg);
        var orb = MakeOrb();

        // Generate an entry to exhaust the trade count (_tradeCount incremented in TryEntry)
        ArmLong(s);
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Now try to arm again — should not arm because MaxTrades=1 already used
        var armBar = MakeBar(5198m, 5199m, 5196m, 5197m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb();
        ArmLong(s);
        Assert.True(s.IsArmed);

        s.Reset();

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void ForceExit_ClearsState()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb();
        ArmLong(s);
        Assert.True(s.IsArmed);

        s.ForceExit(5195m, new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc));

        Assert.False(s.IsArmed); // state cleared
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void DoesNotArmLong_WhenVwapFilterOn_AndBelowVwap()
    {
        var cfg = DefaultConfig();
        cfg.UseVwap = true;
        var s = new PullbackStrategy(cfg);
        var orb = MakeOrb(); // high=5200

        // VWAP = 5199 so bar.Close < VWAP → aboveVwap = false → no long arm
        var ind = MakeIndicators(vwap: 5199m);
        // Bar near orbHigh
        var bar = MakeBar(5198m, 5198m, 5195m, 5196m); // close=5196 < vwap=5199

        s.OnBar(bar, orb, ind, EmptyModules());

        Assert.False(s.IsArmed);
    }

    [Fact]
    public void DoesNotReenter_WhenInTrade()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb();

        // Generate entry
        ArmLong(s);
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Simulate broker confirming fill
        s.SetInTrade(true);
        Assert.True(s.IsActive);

        // Try to arm again — should not arm because _inTrade blocks ProcessArm
        var armBar = MakeBar(5198m, 5199m, 5196m, 5197m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void GetSnapshot_ReturnsCorrectState()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb();
        ArmLong(s);

        var snap = s.GetSnapshot();

        Assert.Equal(SetupId.A, snap.SetupId);
        Assert.True(snap.IsArmed);
        Assert.False(snap.IsActive);
        Assert.Equal(DefaultConfig().MaxTrades, snap.MaxTrades);
        Assert.True(snap.Enabled);
    }
}
