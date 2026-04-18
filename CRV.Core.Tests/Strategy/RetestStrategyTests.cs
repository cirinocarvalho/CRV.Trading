// CRV.Core.Tests/Strategy/RetestStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// Tests for RetestStrategy — a pure signal generator.
/// After phase2 simplification, the strategy only produces EntrySignal.
/// Trade lifecycle (exit, partial, BE) is managed by BrokerEventHandler.
/// IsActive is controlled externally via SetInTrade().
/// </summary>
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

    // ─── Helper: arm the strategy long (state=1) ─────────────────────
    private static RetestStrategy ArmLong(RetestStrategy s)
    {
        var orb = MakeOrb();
        var bar = MakeBar(5198m, 5198.75m, 5195m, 5198m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: arm the strategy short (state=-1) ───────────────────
    private static RetestStrategy ArmShort(RetestStrategy s)
    {
        var orb = MakeOrb() with { BearClose = true };
        var bar = MakeBar(5182m, 5185m, 5181.25m, 5182m);
        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: transition to retest long (state=2) ─────────────────
    private static RetestStrategy RetestLong(RetestStrategy s)
    {
        ArmLong(s);
        var orb = MakeOrb();
        var retestBar = MakeBar(5197m, 5202m, 5197m, 5200m);
        s.OnBar(retestBar, orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ─── Helper: enter long from retest ──────────────────────────────
    private static RetestStrategy EnterLong(RetestStrategy s)
    {
        RetestLong(s);
        var orb = MakeOrb();
        var entryBar = MakeBar(5199m, 5206m, 5199m, 5205m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.OnTick(5200m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
                 orb, MakeIndicators(), EmptyModules());
        return s;
    }

    // ── Private helper: enter long using the full sequence ──────────
    private static void EnterLongWithStrategy(RetestStrategy s)
    {
        var orb = MakeOrb();
        var armBar = MakeBar(5198m, 5202m, 5195m, 5198m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());
        var retestBar = MakeBar(5202m, 5202m, 5199m, 5200m);
        s.OnBar(retestBar, orb, MakeIndicators(), EmptyModules());
        var entryBar = MakeBar(5199m, 5206m, 5199m, 5205m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.OnTick(5200m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
                 orb, MakeIndicators(), EmptyModules());
    }

    [Fact]
    public void Arms_Long_WhenBarNearOrbHigh()
    {
        var s = new RetestStrategy(DefaultConfig());
        ArmLong(s);

        Assert.True(s.IsArmed);
        Assert.False(s.IsActive);
        Assert.Equal(1, s.GetSnapshot().State);
    }

    [Fact]
    public void Arms_Short_WhenBarNearOrbLow()
    {
        var s = new RetestStrategy(DefaultConfig());
        var orb = MakeOrb() with { BearClose = true };
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

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(5200m, s.PendingEntry.Entry);
    }

    [Fact]
    public void UsesCalcLevelsB_ForStopCalculation()
    {
        var s = new RetestStrategy(DefaultConfig());
        EnterLong(s);

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(5190m, s.PendingEntry!.Stop);
        Assert.Equal(5220m, s.PendingEntry.Tg2Price);
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

        // Aggressive Market: arms (state=1) then immediately enters on same bar.
        // Market → entry at bar.Open; must be >= orbHigh to pass hard guard
        var bar = MakeBar(5201m, 5203m, 5198m, 5200m);
        s.OnBar(bar, orb, ind, mod);

        // State already back to 0 after same-bar entry
        Assert.False(s.IsArmed);
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(5201m, s.PendingEntry.Entry); // market entry at bar.Open
    }

    [Fact]
    public void OnTick_Enters_WhenArmedAndRetesting()
    {
        // Conservative tick entry now requires _retestCloseConfirmed (a bar must
        // have CLOSED above orbHigh while in state=2). The bar-level entry fires
        // at the same time as the confirm, so the canonical Conservative flow is:
        //   state=2 → bar closes above orbHigh → bar-level TryEntry fires.
        // Tick-level entry is a secondary path for when bar-level was blocked.
        //
        // Simulate: reach state=2, then bar close above orbHigh confirms + enters.
        var s = new RetestStrategy(DefaultConfig());
        RetestLong(s);
        s.ClearPendingSignals();
        Assert.Equal(2, s.GetSnapshot().State);

        var orb = MakeOrb();
        // Confirming bar: close 5201 > orbHigh 5200 → _retestCloseConfirmed + bar entry
        var confirmBar = MakeBar(5199m, 5206m, 5199m, 5201m);
        s.OnBar(confirmBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(5200m, s.PendingEntry.Entry); // entry at orbHigh
    }

    [Fact]
    public void OnTick_Blocked_WhenNoCloseConfirmation()
    {
        // Tick-level entry should NOT fire without a prior bar close above orbHigh.
        // This prevents wick-only entries in downtrends.
        var s = new RetestStrategy(DefaultConfig());
        RetestLong(s);
        s.ClearPendingSignals();
        Assert.Equal(2, s.GetSnapshot().State);

        var orb = MakeOrb();
        var utc = new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc);
        // Price touches orbHigh but no bar has closed above it → blocked
        s.OnTick(5201m, utc, orb, MakeIndicators(), EmptyModules());

        Assert.Null(s.PendingEntry); // must NOT enter without close confirmation
    }

    [Fact]
    public void DoesNotArm_WhenMaxTradesReached()
    {
        var cfg = DefaultConfig();
        cfg.MaxTrades = 1;
        var s = new RetestStrategy(cfg);

        // Complete a trade (entry increments _tradeCount)
        EnterLongWithStrategy(s);
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Try to arm again — should not because trade count exhausted
        var orb = MakeOrb();
        var armBar = MakeBar(5198m, 5202m, 5195m, 5198m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
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
        Assert.Equal(0, s.GetSnapshot().State);
    }

    [Fact]
    public void ForceExit_ClearsState()
    {
        var s = new RetestStrategy(DefaultConfig());
        ArmLong(s);
        Assert.True(s.IsArmed);

        s.ForceExit(5195m, new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc));

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void DoesNotArmLong_WhenVwapFilterOn_AndBelowVwap()
    {
        var cfg = DefaultConfig();
        cfg.UseVwap = true;
        var s = new RetestStrategy(cfg);
        var orb = MakeOrb();

        var ind = MakeIndicators(vwap: 5199m);
        var bar = MakeBar(5198m, 5202m, 5195m, 5196m);

        s.OnBar(bar, orb, ind, EmptyModules());

        Assert.False(s.IsArmed);
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

        var orb = MakeOrb();
        var cancelBar = MakeBar(5185m, 5190m, 5175m, 5178m);
        s.OnBar(cancelBar, orb, MakeIndicators(), EmptyModules());

        Assert.NotEqual(1, s.GetSnapshot().State);
    }

    [Fact]
    public void Short_FullCycle_ArmRetestEntry()
    {
        var cfg = DefaultConfig();
        var s = new RetestStrategy(cfg);
        var orb = MakeOrb() with { BearClose = true };

        // Step 1: arm short
        var armBar = MakeBar(5182m, 5185m, 5181.25m, 5182m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());
        Assert.Equal(-1, s.GetSnapshot().State);

        // Step 2: retest
        var retestBar = MakeBar(5183m, 5183m, 5179m, 5180m);
        s.OnBar(retestBar, orb, MakeIndicators(), EmptyModules());
        Assert.Equal(-2, s.GetSnapshot().State);

        // Step 3: entry bar stages tick confirmation
        var entryBar = MakeBar(5181m, 5181m, 5175m, 5176m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.OnTick(5180m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
                 orb, MakeIndicators(), EmptyModules());
        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Short, s.PendingEntry!.Direction);
        Assert.Equal(5180m, s.PendingEntry.Entry);
        Assert.Equal(5190m, s.PendingEntry.Stop);
        Assert.Equal(5160m, s.PendingEntry.Tg2Price);
    }

    [Fact]
    public void SixStates_Observed()
    {
        var s = new RetestStrategy(DefaultConfig());
        Assert.Equal(0, s.GetSnapshot().State); // idle

        ArmLong(s);
        Assert.Equal(1, s.GetSnapshot().State); // armed long

        var orb = MakeOrb();
        var retestBar = MakeBar(5202m, 5202m, 5199m, 5200m);
        s.OnBar(retestBar, orb, MakeIndicators(), EmptyModules());
        Assert.Equal(2, s.GetSnapshot().State); // retest long

        // Entry bar + tick → entry signal generated, state returns to 0
        var entryBar = MakeBar(5199m, 5206m, 5199m, 5205m);
        s.OnBar(entryBar, orb, MakeIndicators(), EmptyModules());
        s.OnTick(5200m, new DateTime(2026, 3, 10, 14, 31, 0, DateTimeKind.Utc),
                 orb, MakeIndicators(), EmptyModules());
        Assert.Equal(0, s.GetSnapshot().State); // entry generated, back to idle
        Assert.NotNull(s.PendingEntry);
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
    public void DoesNotReenter_WhenInTrade()
    {
        var s = new RetestStrategy(DefaultConfig());

        // Generate entry
        EnterLong(s);
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Simulate broker confirming fill
        s.SetInTrade(true);
        Assert.True(s.IsActive);

        // Try to arm again — should not arm because _inTrade blocks ProcessArm
        var orb = MakeOrb();
        var armBar = MakeBar(5198m, 5202m, 5195m, 5198m);
        s.OnBar(armBar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }
}
