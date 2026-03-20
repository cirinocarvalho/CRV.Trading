// CRV.Core.Tests/Strategy/PullbackStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

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
        Assert.True(s.IsActive);
    }

    [Fact]
    public void Enters_Immediately_Aggressive()
    {
        var cfg = DefaultConfig();
        cfg.Mode = "Aggressive";
        var s = new PullbackStrategy(cfg);
        var orb = MakeOrb(); // high=5200, low=5180, range=20

        // In aggressive mode: arming and entry happen on the SAME bar.
        // Entry uses _armEntry = bar.Open. With open=5199, stop = 5199 - 2 = 5197.
        // Bar Low must be >= stop (5197) to avoid immediate same-bar stop hit.
        // nearDist = 3 → arm when bar.High >= 5197. Use high=5199, low=5198.
        var bar = MakeBar(5199m, 5199m, 5198m, 5198m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        // Entry should have fired on the arm bar itself (no pullback needed in aggressive mode)
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.True(s.IsActive);
        Assert.False(s.IsArmed); // transitioned to active, no longer in armed state
    }

    [Fact]
    public void Exits_OnTarget()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(); // range=20
        // Enter long: ep = 5190, stopPct=0.10 → stopDist=2, stop=5188
        //   targetPct=100 → targetDist=20, target=5210
        ArmLong(s);
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        // Bar that hits the target (longPb=5190, target = 5190 + 20 = 5210)
        var exitBar = MakeBar(5200m, 5215m, 5198m, 5212m);
        s.OnBar(exitBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Exits_OnStop()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(); // range=20
        // ep = 5190, stop = 5190 - 20*0.10 = 5188
        ArmLong(s);
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        // Bar where Low <= stop (5188)
        var stopBar = MakeBar(5190m, 5191m, 5185m, 5186m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Stop, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
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
        Assert.True(s.IsActive);
    }

    [Fact]
    public void OnTick_Exits_OnTarget()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb(); // range=20
        // Enter first via bar
        ArmLong(s);
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        // Tick at or above target
        // ep=5190, target = 5190 + 20 = 5210
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        s.OnTick(5210m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void DoesNotArm_WhenMaxTradesReached()
    {
        var cfg = DefaultConfig();
        cfg.MaxTrades = 1;
        var s = new PullbackStrategy(cfg);
        var orb = MakeOrb();

        // Complete a trade to exhaust the trade count
        ArmLong(s);
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();

        // Hit stop to close trade and increment counter
        var stopBar = MakeBar(5190m, 5191m, 5185m, 5186m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Now try to arm again — should not arm because MaxTrades=1 already reached
        var armBar = MakeBar(5198m, 5199m, 5196m, 5197m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
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
        Assert.Null(s.PendingExit);
    }

    [Fact]
    public void ForceExit_ProducesExitSignal()
    {
        var s = new PullbackStrategy(DefaultConfig());
        var orb = MakeOrb();
        ArmLong(s);
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        s.ForceExit(5195m, new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.SessionEnd, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
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
    public void Partial_And_BE_ProduceSignals()
    {
        var cfg = DefaultConfig();
        cfg.UsePartial = true;
        cfg.UseBe = true;
        cfg.Contracts = 4;     // so partial is meaningful (2 out of 4)
        var s = new PullbackStrategy(cfg);
        var orb = MakeOrb(); // range=20
        ArmLong(s);
        var entryBar = MakeBar(5191m, 5192m, 5189m, 5191m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        // partial = ep + targetDist * (partialPct/100) = 5190 + 20*(50/100) = 5190 + 10 = 5200
        // Bar touches partial but doesn't reach target (5210) and stays above entry (BE=5190)
        // Low must be > 5190 to avoid immediately hitting the BE stop on the same bar
        var partBar = MakeBar(5192m, 5205m, 5192m, 5202m);
        s.OnBar(partBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingPartial);
        Assert.NotNull(s.PendingBE);
        Assert.True(s.IsActive); // trade still open
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
