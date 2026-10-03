// CRV.Core.Tests/Strategy/SessionFakeoutStrategyTests.cs
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// Tests for SessionFakeoutStrategy — a pure signal generator.
/// After phase2 simplification, the strategy only produces EntrySignal.
/// Trade lifecycle (exit, partial, BE) is managed by BrokerEventHandler.
/// IsActive is controlled externally via SetInTrade().
///
/// NOTE: SessionFakeout arms and enters on the same bar. Once armed, the bar-level
/// entry fires immediately using session range levels.
/// Session range: high=5210, low=5170, range=40.
/// </summary>
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

    private static ModuleState FakeoutBullModules() =>
        EmptyModules() with { SessionFakeoutBull = true };

    private static ModuleState FakeoutBearModules() =>
        EmptyModules() with { SessionFakeoutBear = true };

    private static Bar MakeBar(decimal open, decimal high, decimal low, decimal close,
        DateTime? time = null)
        => new(time ?? new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc),
               open, high, low, close, 100);

    // ═════════════════════════════════════════════════════════════════
    // Arming + immediate entry on bar
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void ArmsAndEnters_Short_WhenSessionFakeoutBull()
    {
        // SessionFakeoutBull = breakout was LONG -> arm SHORT (fade it)
        // Bar-level entry fires immediately: ep = SessionRangeHigh = 5210
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5212m, 5215m, 5208m, 5211m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBullModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Short, s.PendingEntry!.Direction);
        Assert.Equal(5210m, s.PendingEntry.Entry); // entry at session range high
    }

    [Fact]
    public void ArmsAndEnters_Long_WhenSessionFakeoutBear()
    {
        // SessionFakeoutBear = breakout was SHORT -> arm LONG (fade it)
        // Bar-level entry fires immediately: ep = SessionRangeLow = 5170
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.NotNull(s.PendingEntry);
        Assert.Equal(Direction.Long, s.PendingEntry!.Direction);
        Assert.Equal(5170m, s.PendingEntry.Entry); // entry at session range low
    }

    [Fact]
    public void DoesNotArm_WhenNoFakeout()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5190m, 5195m, 5185m, 5192m);

        s.OnBar(bar, orb, MakeIndicators(), EmptyModules());

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void DoesNotArm_WhenOrbNotSet()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
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
        var s = new SessionFakeoutStrategy(cfg);

        // First entry uses the trade count
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Try again — should not arm
        var bar2 = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar2, orb, MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
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
    // Entry levels
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Entry_UsesSessionRange_ForStopTargetPartial()
    {
        // SessionRange = 40, Long entry at srLow=5170
        // stop = 5170 - 40*0.10 = 5166, target = 5170 + 40 = 5210, partial = 5170 + 20 = 5190
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        var entry = s.PendingEntry!;
        Assert.Equal(5170m, entry.Entry);
        Assert.Equal(5166m, entry.Stop);
        Assert.Equal(5210m, entry.Tg2Price);
        Assert.Equal(5190m, entry.Tg1Price);
    }

    [Fact]
    public void Entry_RespectsMinRr()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99.0m;  // impossibly high
        var s = new SessionFakeoutStrategy(cfg);
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void Entry_AppliesTickOffset()
    {
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;  // 2 ticks = 0.50
        var s = new SessionFakeoutStrategy(cfg);
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.NotNull(s.PendingEntry);
        // Long entry at srLow=5170 + 2 ticks (0.50) = 5170.50
        Assert.Equal(5170.50m, s.PendingEntry!.Entry);
    }

    // ═════════════════════════════════════════════════════════════════
    // ForceExit / Reset / Snapshot
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void ForceExit_ClearsState()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);

        s.ForceExit(5175m, new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc));

        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        s.Reset();

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void GetSnapshot_ReturnsCorrectState()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        var snap = s.GetSnapshot();

        Assert.Equal(SetupId.D, snap.SetupId);
        Assert.True(snap.Enabled);
    }

    [Fact]
    public void BullTraded_Guard_PreventsSameSideRearm()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        // Enter long (sets _bullTraded = true)
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Try to arm LONG again — should not because _bullTraded = true
        var bar2 = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar2, orb, MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void DoesNotReenter_WhenInTrade()
    {
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingEntry);
        s.ClearPendingSignals();

        // Simulate broker confirming fill
        s.SetInTrade(true);
        Assert.True(s.IsActive);

        // Try to arm again — blocked by _inTrade
        var bar2 = MakeBar(5212m, 5215m, 5208m, 5211m);
        s.OnBar(bar2, orb, MakeIndicators(), FakeoutBullModules());

        Assert.Null(s.PendingEntry);
    }

    // ═════════════════════════════════════════════════════════════════
    // Session range specific
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Entry_UsesSessionRange_NotOrb()
    {
        // Session range low = 5170, ORB low = 5180
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.Equal(5170m, s.PendingEntry!.Entry); // not 5180 (ORB low)
    }

    [Fact]
    public void Entry_FallsBackToOrbRange_WhenSessionRangeZero()
    {
        // Session range zero => fallback to ORB range for level calc
        var s = new SessionFakeoutStrategy(DefaultConfig());
        var orb = MakeOrb(); // range=20
        // Arm via bar with fakeout, but srLow = srHigh = 5170
        var modules = FakeoutBearModules() with { SessionRangeHigh = 5170m, SessionRangeLow = 5170m };
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), modules);

        Assert.NotNull(s.PendingEntry);
        // Entry at srLow=5170, ORB range=20 for level calc
        Assert.Equal(5170m, s.PendingEntry!.Entry);
        Assert.Equal(5168m, s.PendingEntry.Stop);    // 5170 - 20*0.10 = 5168
        Assert.Equal(5190m, s.PendingEntry.Tg2Price);   // 5170 + 20 = 5190
    }

    [Fact]
    public void BudgetBelowOneContract_RefusesAndSaysSo()
    {
        // Entry at session-range low 5170, StopPct 0.10 x session range 40 = 4 pts, $20/pt ⇒ $80 a contract.
        var cfg = DefaultConfig();
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk   = 50m;
        var s = new SessionFakeoutStrategy(cfg);
        var orb = MakeOrb();
        var bar = MakeBar(5168m, 5172m, 5165m, 5169m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        var r = s.PendingSizeRefusal;
        Assert.NotNull(r);
        Assert.Equal(4m,  r!.StopDistance);
        Assert.Equal(80m, r.RiskPerContract);
        Assert.Equal(50m, r.Budget);

        s.ClearPendingSignals();
        Assert.Null(s.PendingSizeRefusal);
    }

    [Fact]
    public void LongOnly_DoesNotArmShort_OnBullFakeout()
    {
        var cfg = DefaultConfig();
        cfg.AllowShort = false;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5212m, 5215m, 5208m, 5211m), MakeOrb(), MakeIndicators(), FakeoutBullModules());

        Assert.Null(s.PendingEntry);
        Assert.False(s.IsArmed);
    }

    [Fact]
    public void ShortOnly_DoesNotArmLong_OnBearFakeout()
    {
        var cfg = DefaultConfig();
        cfg.AllowLong = false;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        Assert.False(s.IsArmed);
    }

    [Fact]
    public void ArmedSide_SwitchedOffWhileArmed_DoesNotEnter()
    {
        // A rejected entry leaves the strategy armed; a settings change can then switch that side
        // off without disarming it. The next bar must not enter the side that is now off.
        var cfg = DefaultConfig();
        cfg.MinRr = 1000m;                       // every entry is rejected, so the long stays armed
        var s = new SessionFakeoutStrategy(cfg);
        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
        Assert.True(s.IsArmed);
        Assert.Null(s.PendingEntry);

        var shortOnly = DefaultConfig();
        shortOnly.AllowLong = false;
        s.Reconfigure(shortOnly);
        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), EmptyModules());

        Assert.Null(s.PendingEntry);
        Assert.False(s.IsArmed);
    }

    // ═════════════════════════════════════════════════════════════════
    // Targets after sizing, and the reward / risk guard
    // ═════════════════════════════════════════════════════════════════

    [Fact]
    public void TickOffset_RewardRiskIsMeasuredFromTheFill()
    {
        // Fill 5170.50, stop 5166, target 5210.50: 40 / 4.5 = 8.89R. From the signal price: 10R.
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;
        cfg.MinRr = 9m;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        var skip = s.PendingSizeRefusal!;
        Assert.Equal(RefusalReason.MinRr, skip.Reason);
        Assert.Equal(40m / 4.5m, skip.Rr);
        Assert.Equal(skip.Describe(), s.GetSnapshot().LastSkip);
    }

    [Fact]
    public void TickOffset_TargetIsMeasuredFromTheFill()
    {
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Equal(5210.50m, s.PendingEntry!.Tg2Price);
    }

    [Theory]
    [InlineData("ForceExit")]
    [InlineData("Disarm")]
    [InlineData("SideSwitchedOff")]
    public void MinRrSkip_IsReportedOncePerArmedEpisode_AndLastSkipSurvivesTheEpisode(string endsEpisode)
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99m;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingSizeRefusal);

        s.ClearPendingSignals();
        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
        Assert.Null(s.PendingSizeRefusal);       // same episode: already reported

        switch (endsEpisode)
        {
            case "ForceExit":
                s.ForceExit(5185m, new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc));
                break;
            case "Disarm":
                s.Disarm();
                s.ResetCutoff();
                break;
            default:
                cfg.AllowLong = false;
                s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
                cfg.AllowLong = true;
                break;
        }
        Assert.False(s.IsArmed);
        Assert.NotNull(s.GetSnapshot().LastSkip); // outlives the episode

        s.ClearPendingSignals();
        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingSizeRefusal);    // second episode reports again
    }

    [Fact]
    public void FailsBudgetAndMinimum_ReportsTheSizeRefusalNotAMinRrSkip()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99m;
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk = 1m;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        Assert.Equal(RefusalReason.Size, s.PendingSizeRefusal!.Reason);
        Assert.Null(s.GetSnapshot().LastSkip);
    }

    [Fact]
    public void WholePositionDollars_TargetIsSpreadOverTheSizedContracts()
    {
        // Stop 4 pts x $20 = $80 a contract; $160 budget = 2 contracts; $400 / ($20 x 2) = 10 pts.
        var cfg = DefaultConfig();
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk = 160m;
        cfg.TargetMode = TargetMode.Dollars;
        cfg.TargetDollars = 400m;
        cfg.TargetDollarsBasis = TargetDollarsBasis.WholePosition;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Equal(2, s.PendingEntry!.TotalContracts);
        Assert.Equal(5180m, s.PendingEntry.Tg2Price);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_EntersAtTheMinimum()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 12m;
        cfg.MinRrAction = MinRrAction.RaiseTarget;
        var s = new SessionFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5168m, 5172m, 5165m, 5169m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Equal(5218m, s.PendingEntry!.Tg2Price);   // 12 x 4 pts
        Assert.Equal(5194m, s.PendingEntry.Tg1Price);
    }
}
