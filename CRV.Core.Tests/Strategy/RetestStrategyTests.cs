// CRV.Core.Tests/Strategy/RetestStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class RetestStrategyTests
{
    private static StrategySetupConfig DefaultConfig() => new()
    {
        Name = "B", SetupId = SetupId.B,
        StrategyType = StrategyType.Retest,
        Enabled = true,
        Ticker = "NQM26", PointValue = 20m, TickSize = 0.25m,
        Contracts = 2, MaxContracts = 4, HiVolMult = 1.0m,
        StopPct = 0.50m, TargetPct = 100, PartialPct = 50,
        NearPct = 0.15m, MinRr = 1.0m, Mode = "Conservative",
        RetestPct = 0.05m, MaxTrades = 3,
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

    // ── ORB: High=5200, Low=5180, Range=20, Mid=5190 ──
    // NearPct=0.15, nearDist=3 → arm long when bar.Close >= 5197
    // RetestPct=0.05, retestW=1 → retest zone for long: bar.Low <= 5201 && bar.High >= 5199
    // StopPct=0.50 → stop distance = 20*0.50=10 → long stop = entry - 10
    // TargetPct=100 → target distance = 20 → long target = entry + 20

    // ─── Helper: arm the strategy long (state=1) ─────────────────────
    // OrbHigh=5200, NearPct=0.15, Range=20 → nearDist=3 → arm when bar.Close >= 5197
    // RetestPct=0.05, retestW=1 → retest zone: bar.Low <= 5201 AND bar.High >= 5199
    // To arm WITHOUT entering retest, bar.High must be < 5199
    private static RetestStrategy ArmLong(RetestStrategy s)
    {
        var orb = MakeOrb();
        // bar.Close=5198 >= 5197 → arm. bar.High=5198.75 < 5199 → no retest transition
        var bar = MakeBar(5198m, 5198.75m, 5195m, 5198m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: arm the strategy short (state=-1) ───────────────────
    // OrbLow=5180, nearDist=3 → arm when bar.Close <= 5183
    // Retest zone: bar.High >= 5179 AND bar.Low <= 5181
    // To arm WITHOUT entering retest, bar.Low must be > 5181
    private static RetestStrategy ArmShort(RetestStrategy s)
    {
        var orb = MakeOrb() with { BearClose = true };
        // bar.Close=5182 <= 5183 → arm. bar.Low=5181.25 > 5181 → no retest transition
        var bar = MakeBar(5182m, 5185m, 5181.25m, 5182m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: transition to retest long (state=2) ─────────────────
    // retestW = 20 * 0.05 = 1. Retest zone: bar.Low <= 5201, bar.High >= 5199
    private static RetestStrategy RetestLong(RetestStrategy s)
    {
        ArmLong(s);
        var orb = MakeOrb();
        // Price leaves the zone: bar.Low dips below orbHigh - retestW (5199)
        // Then returns: bar.High touches orbHigh zone (>= 5199)
        // retestW = 20 * 0.05 = 1 → zone = [5199, 5201]
        var retestBar = MakeBar(5197m, 5202m, 5197m, 5200m);
        s.OnBar(retestBar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: enter long from retest ──────────────────────────────
    // After retest (state=2), bar.Close > orbHigh stages tick confirmation.
    // Tick at orbHigh confirms entry at 5200.
    private static RetestStrategy EnterLong(RetestStrategy s)
    {
        RetestLong(s);
        var orb = MakeOrb();
        // Bar closes above orbHigh, staging tick confirmation (theoreticalEntry=orbHigh=5200)
        var entryBar = MakeBar(5199m, 5206m, 5199m, 5205m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        // Tick at orbHigh confirms entry at 5200
        s.OnTick(5200m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
                 orb, MakeIndicators(), EmptyModules());
        return s;
    }

    [Fact]
    public void Arms_Long_WhenBarNearOrbHigh()
    {
        var s = new RetestStrategy(DefaultConfig());
        ArmLong(s);

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
        // state should be 1 (armed long)
        Assert.Equal(1, s.GetSnapshot().State);
    }

    [Fact]
    public void Arms_Short_WhenBarNearOrbLow()
    {
        var s = new RetestStrategy(DefaultConfig());
        var orb = MakeOrb() with { BearClose = true };
        // bar.Close=5182 <= 5183 → arm. bar.Low=5181.25 > 5181 → no retest
        var bar = MakeBar(5182m, 5185m, 5181.25m, 5182m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
        Assert.Equal(-1, s.GetSnapshot().State);
    }

    [Fact]
    public void DoesNotArm_WhenOrbNotSet()
    {
        var s = new RetestStrategy(DefaultConfig());
        var orbNotSet = MakeOrb() with { IsSet = false };
        var bar = MakeBar(5198m, 5202m, 5195m, 5198m);

        s.OnBar(bar, orbNotSet, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Transitions_ToRetest_WhenPriceReturnsToOrbLevel()
    {
        var s = new RetestStrategy(DefaultConfig());
        RetestLong(s);

        Assert.True(s.IsArmed);
        Assert.Equal(2, s.GetSnapshot().State);
    }

    [Fact]
    public void DeArms_WhenPriceCrossesOrbMid()
    {
        var s = new RetestStrategy(DefaultConfig());
        RetestLong(s);
        Assert.Equal(2, s.GetSnapshot().State);

        var orb = MakeOrb(); // mid=5190
        // Bar closes below OrbMid → de-arm
        var dearmBar = MakeBar(5195m, 5198m, 5185m, 5188m);
        s.OnBar(dearmBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.Equal(0, s.GetSnapshot().State);
    }

    [Fact]
    public void Enters_Long_WhenRetestBarClosesAboveOrbHigh()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);

        Assert.True(s.IsActive);
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        // Entry at orbHigh=5200
        Assert.Equal(5200m, s.PendingEntry.Entry);
    }

    [Fact]
    public void UsesCalcLevelsB_ForStopCalculation()
    {
        // CalcLevelsB: stop = entry ± orbRange * StopPct
        // entry=5200, orbRange=20, StopPct=0.50 → stopDist=10 → stop=5190
        // target=5200 + 20=5220
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(5190m, s.PendingEntry!.Stop);
        Assert.Equal(5220m, s.PendingEntry.Target);
    }

    [Fact]
    public void Enters_Immediately_Aggressive()
    {
        var cfg = DefaultConfig();
        cfg.Mode = "Aggressive";
        var s = new RetestStrategy(cfg);
        var orb = MakeOrb();
        var ind = MakeIndicators();
        var mod = EmptyModules();

        // Aggressive: arm on bar close, enter on next tick at market price
        var bar = MakeBar(5199m, 5202m, 5198m, 5198m);
        s.OnBar(bar, orb, ind, mod);

        // After bar: armed but not entered yet (tick entry)
        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);

        // Next tick triggers entry at the actual tick price
        s.OnTick(5199.50m, DateTime.UtcNow, orb, ind, mod);

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(5199.50m, s.PendingEntry.Entry);
        Assert.True(s.IsActive);
    }

    [Fact]
    public void Exits_OnTarget()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // entry=5200, target=5220 → bar.High >= 5220
        var exitBar = MakeBar(5210m, 5225m, 5208m, 5222m);
        s.OnBar(exitBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Exits_OnStop()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // entry=5200, stop=5190 → bar.Low <= 5190
        var stopBar = MakeBar(5195m, 5196m, 5188m, 5189m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Stop, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void OnTick_Enters_WhenArmedAndRetesting()
    {
        var s = new RetestStrategy(DefaultConfig());
        RetestLong(s);
        s.ClearPendingSignals();
        Assert.Equal(2, s.GetSnapshot().State);

        var orb = MakeOrb();
        // retestDist = 20*0.05=1, tickTol=0.5 → fires when price <= 5200 + 1 + 0.5 = 5201.5
        // Tick confirmation enters at tick price (5201), not orbHigh
        var utc = new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc);
        s.OnTick(5201m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(5201m, s.PendingEntry.Entry); // enters at tick price
        Assert.True(s.IsActive);
    }

    [Fact]
    public void OnTick_Exits_OnTarget()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // entry=5200, target=5220
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        s.OnTick(5220m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void OnTick_Exits_OnStop()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // entry=5200, stop=5190
        var utc = new DateTime(2026, 3, 10, 14, 35, 0, DateTimeKind.Utc);
        s.OnTick(5190m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Stop, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void DoesNotArm_WhenMaxTradesReached()
    {
        var cfg = DefaultConfig();
        cfg.MaxTrades = 1;
        var s = new RetestStrategy(cfg);
        var orb = MakeOrb();

        // Complete a trade
        EnterLongWithStrategy(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        // Hit stop to close trade
        var stopBar = MakeBar(5195m, 5196m, 5188m, 5189m);
        s.OnBar(stopBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Try to arm again
        var armBar = MakeBar(5198m, 5202m, 5195m, 5198m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var s = new RetestStrategy(DefaultConfig());
        ArmLong(s);
        Assert.True(s.IsArmed);

        s.Reset();

        Assert.False(s.IsArmed);
        Assert.False(s.IsActive);
        Assert.Null(s.PendingEntry);
        Assert.Null(s.PendingExit);
        Assert.Equal(0, s.GetSnapshot().State);
    }

    [Fact]
    public void ForceExit_ProducesExitSignal()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);
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
        var s = new RetestStrategy(cfg);
        var orb = MakeOrb(); // high=5200

        // VWAP = 5199 so bar.Close < VWAP → aboveVwap = false → no long arm
        var ind = MakeIndicators(vwap: 5199m);
        var bar = MakeBar(5198m, 5202m, 5195m, 5196m); // close=5196 < vwap=5199

        s.OnBar(bar, orb, ind, EmptyModules());

        Assert.False(s.IsArmed);
    }

    [Fact]
    public void Partial_And_BE_ProduceSignals()
    {
        var cfg = DefaultConfig();
        cfg.UsePartial = true;
        cfg.UseBe = true;
        cfg.Contracts = 4;
        var s = new RetestStrategy(cfg);
        EnterLongWithStrategy(s);
        s.ClearPendingSignals();
        Assert.True(s.IsActive);

        var orb = MakeOrb();
        // entry=5200, partial = 5200 + 20*(50/100) = 5210
        // target=5220. Bar touches partial but not target.
        var partBar = MakeBar(5202m, 5215m, 5202m, 5212m);
        s.OnBar(partBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingPartial);
        Assert.NotNull(s.PendingBE);
        Assert.True(s.IsActive); // trade still open
    }

    [Fact]
    public void GetSnapshot_ReturnsCorrectState()
    {
        var s = new RetestStrategy(DefaultConfig());
        ArmLong(s);

        var snap = s.GetSnapshot();

        Assert.Equal(SetupId.B, snap.SetupId);
        Assert.True(snap.IsArmed);
        Assert.False(snap.IsActive);
        Assert.Equal(DefaultConfig().MaxTrades, snap.MaxTrades);
        Assert.True(snap.Enabled);
    }

    [Fact]
    public void Arms_CancelledWhenPriceClosesBelow_OrbLow_Long()
    {
        var s = new RetestStrategy(DefaultConfig());
        ArmLong(s);
        Assert.True(s.IsArmed);

        // Use wider ORB so cancel price doesn't re-arm on the other side
        // orbHigh=5300, orbLow=5100, nearDist=200*0.15=30
        // Short arm zone: close <= 5130. Cancel bar close=5090 is below orbLow but also
        // inside short arm zone. Use mid-range close instead.
        var wideOrb = MakeOrb(5300m, 5100m); // range=200, nearDist=30
        // Cancel: close=5090 < orbLow=5100 → cancel. close=5090 <= 5130 → re-arms short.
        // To truly test cancel without re-arm, close between orbLow and orbMid (5100-5200).
        // But cancel requires close < orbLow for long cancel. Contradiction.
        // Instead: test that long arm is gone (state != 1) — it either goes to 0 or re-arms short.
        var orb = MakeOrb();
        var cancelBar = MakeBar(5185m, 5190m, 5175m, 5178m);
        s.OnBar(cancelBar, orb, MakeIndicators(), EmptyModules());

        // Long arm is cancelled — state is no longer +1 (armed long)
        Assert.NotEqual(1, s.GetSnapshot().State);
    }

    [Fact]
    public void Short_FullCycle_ArmRetestEntryExit()
    {
        var cfg = DefaultConfig();
        var s = new RetestStrategy(cfg);
        var orb = MakeOrb() with { BearClose = true };

        // Step 1: arm short (bar.Close <= 5183, bar.Low > 5181 → no retest)
        var armBar = MakeBar(5182m, 5185m, 5181.25m, 5182m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());
        Assert.Equal(-1, s.GetSnapshot().State);

        // Step 2: retest — price leaves zone (bar.High > orbLow + retestW = 5181)
        // then returns (bar.Low touches zone <= 5181)
        // retestW = 20 * 0.05 = 1 → zone = [5179, 5181]
        var retestBar = MakeBar(5183m, 5183m, 5179m, 5180m);
        s.OnBar(retestBar, orb, MakeIndicators(), EmptyModules());
        Assert.Equal(-2, s.GetSnapshot().State);

        // Step 3: entry bar stages tick confirmation (theoreticalEntry=orbLow=5180)
        var entryBar = MakeBar(5181m, 5181m, 5175m, 5176m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        // Tick at orbLow confirms entry at 5180
        s.OnTick(5180m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
                 orb, MakeIndicators(), EmptyModules());
        Assert.True(s.IsActive);
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Short, s.PendingEntry!.Direction);
        Assert.Equal(5180m, s.PendingEntry.Entry);
        Assert.Equal(5190m, s.PendingEntry.Stop);
        Assert.Equal(5160m, s.PendingEntry.Target);
        s.ClearPendingSignals();

        // Step 4: exit on target
        var exitBar = MakeBar(5170m, 5172m, 5158m, 5159m);
        s.OnBar(exitBar, orb, MakeIndicators(), EmptyModules());
        Assert.NotNull(s.PendingExit);
        Assert.Equal(ExitReason.Target, s.PendingExit!.Reason);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void SixStates_Observed()
    {
        var s = new RetestStrategy(DefaultConfig());
        Assert.Equal(0, s.GetSnapshot().State); // idle

        ArmLong(s);
        Assert.Equal(1, s.GetSnapshot().State); // armed long

        var orb = MakeOrb();
        // Transition to retest
        var retestBar = MakeBar(5202m, 5202m, 5199m, 5200m);
        s.OnBar(retestBar, orb, MakeIndicators(), EmptyModules());
        Assert.Equal(2, s.GetSnapshot().State); // retest long

        // Entry bar stages tick confirmation
        var entryBar = MakeBar(5199m, 5206m, 5199m, 5205m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        // Tick confirms entry → active
        s.OnTick(5200m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
                 orb, MakeIndicators(), EmptyModules());
        Assert.Equal(3, s.GetSnapshot().State); // active long
    }

    [Fact]
    public void ApplyFill_AdjustsLevels()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);
        Assert.NotNull(s.PendingEntry);

        // Simulate fill at 5200.50 (0.50 slippage)
        s.ApplyFill(5200.50m);

        var trade = s.GetActiveTrade(5205m);
        Assert.NotNull(trade);
        Assert.Equal(5200.50m, trade!.Entry);
        Assert.Equal(5190.50m, trade.CurrentStop);
        Assert.Equal(5220.50m, trade.Target);
    }

    [Fact]
    public void GetActiveTrade_ReturnsNull_WhenNotActive()
    {
        var s = new RetestStrategy(DefaultConfig());
        Assert.Null(s.GetActiveTrade(5200m));
    }

    [Fact]
    public void GetActiveTrade_ReturnsView_WhenActive()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);

        var view = s.GetActiveTrade(5205m);
        Assert.NotNull(view);
        Assert.Equal(SetupId.B, view!.Setup);
        Assert.Equal(5200m, view.Entry);
        Assert.Equal(5190m, view.CurrentStop);
        Assert.Equal(5220m, view.Target);
    }

    [Fact]
    public void DoesNotArm_WhenDisabled()
    {
        var cfg = DefaultConfig();
        cfg.Enabled = false;
        var s = new RetestStrategy(cfg);
        var orb = MakeOrb();

        var bar = MakeBar(5198m, 5202m, 5195m, 5198m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
    }

    [Fact]
    public void AllowRearmAfterBe_DoesNotLockSide()
    {
        var cfg = DefaultConfig();
        cfg.AllowRearmAfterBe = true;
        cfg.UseBe = true;
        cfg.UsePartial = true;
        cfg.Contracts = 4;
        var s = new RetestStrategy(cfg);

        // Enter long, partial fires, stop moves to BE, then stop is hit at BE
        EnterLongWithStrategy(s);
        s.ClearPendingSignals();

        var orb = MakeOrb();
        // partial=5210, entry=5200 → hit partial
        var partBar = MakeBar(5202m, 5215m, 5202m, 5212m);
        s.OnBar(partBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();

        // Now stop is at 5200 (BE). Hit it.
        var beStopBar = MakeBar(5205m, 5205m, 5199m, 5199m);
        s.OnBar(beStopBar, orb, MakeIndicators(), EmptyModules());
        s.ClearPendingSignals();
        Assert.False(s.IsActive);

        // Try to arm long again — should be allowed because exit was at BE
        var armBar = MakeBar(5198m, 5202m, 5195m, 5198m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());
        Assert.True(s.IsArmed);
    }

    // ── Private helper: enter long using the full sequence ──────────
    private static void EnterLongWithStrategy(RetestStrategy s)
    {
        var orb = MakeOrb();
        // Step 1: arm
        var armBar = MakeBar(5198m, 5202m, 5195m, 5198m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());

        // Step 2: retest
        var retestBar = MakeBar(5202m, 5202m, 5199m, 5200m);
        s.OnBar(retestBar, orb, MakeIndicators(), EmptyModules());

        // Step 3: entry bar stages tick confirmation
        var entryBar = MakeBar(5199m, 5206m, 5199m, 5205m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());

        // Step 4: tick at orbHigh confirms entry at 5200
        s.OnTick(5200m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
                 orb, MakeIndicators(), EmptyModules());
    }
}
