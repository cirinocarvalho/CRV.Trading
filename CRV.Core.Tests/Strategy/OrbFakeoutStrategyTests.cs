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
        Assert.Equal(5200m, entry.Tg2Price);     // 5180 + 20*1.00 = 5200
        Assert.Equal(5190m, entry.Tg1Price);    // 5180 + 20*0.50 = 5190
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

    [Fact]
    public void BudgetBelowOneContract_RefusesAndSaysSo()
    {
        // Entry at orb.Low 5180, StopPct 0.10 x range 20 = 2 pts, $20/pt ⇒ $40 a contract.
        var cfg = DefaultConfig();
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk   = 30m;
        var s = new OrbFakeoutStrategy(cfg);
        var orb = MakeOrb();
        var bar = MakeBar(5178m, 5182m, 5175m, 5179m);
        s.OnBar(bar, orb, MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        var r = s.PendingSizeRefusal;
        Assert.NotNull(r);
        Assert.Equal(2m,  r!.StopDistance);
        Assert.Equal(40m, r.RiskPerContract);
        Assert.Equal(30m, r.Budget);

        s.ClearPendingSignals();
        Assert.Null(s.PendingSizeRefusal);
    }

    [Fact]
    public void LongOnly_DoesNotArmShort_OnBullFakeout()
    {
        var cfg = DefaultConfig();
        cfg.AllowShort = false;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5202m, 5205m, 5198m, 5201m), MakeOrb(), MakeIndicators(), FakeoutBullModules());

        Assert.Null(s.PendingEntry);
        Assert.False(s.IsArmed);
    }

    [Fact]
    public void ShortOnly_DoesNotArmLong_OnBearFakeout()
    {
        var cfg = DefaultConfig();
        cfg.AllowLong = false;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

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
        var s = new OrbFakeoutStrategy(cfg);
        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
        Assert.True(s.IsArmed);
        Assert.Null(s.PendingEntry);

        var shortOnly = DefaultConfig();
        shortOnly.AllowLong = false;
        s.Reconfigure(shortOnly);
        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), EmptyModules());

        Assert.Null(s.PendingEntry);
        Assert.False(s.IsArmed);
    }

    // ═══════════════════════════════════════════════════════════════
    // Targets after sizing, and the reward / risk guard
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TickOffset_TargetAndPartialAreMeasuredFromTheFill()
    {
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;                 // fill 5180.50
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        var e = s.PendingEntry!;
        Assert.Equal(5180.50m, e.Entry);
        Assert.Equal(5178m, e.Stop);             // stop stays measured from the signal price
        Assert.Equal(5200.50m, e.Tg2Price);      // 20 pts from the fill
        Assert.Equal(5190.50m, e.Tg1Price);
    }

    [Fact]
    public void TickOffset_RewardRiskIsMeasuredFromTheFill()
    {
        // From the fill: 20 / 2.5 = 8R. From the signal price it would have been 10R.
        var cfg = DefaultConfig();
        cfg.EntryTickOffset = 2;
        cfg.MinRr = 9m;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        var skip = s.PendingSizeRefusal!;
        Assert.Equal(RefusalReason.MinRr, skip.Reason);
        Assert.Equal(8m, skip.Rr);
        Assert.Equal("Skipped: 8.0R below 9.0R", skip.Describe());
        Assert.Equal(skip.Describe(), s.GetSnapshot().LastSkip);
        Assert.True(s.IsArmed);                  // a skipped trade leaves the setup armed, as before

        s.Reset();
        Assert.Null(s.GetSnapshot().LastSkip);
    }

    [Theory]
    [InlineData("ForceExit")]
    [InlineData("Disarm")]
    [InlineData("SideSwitchedOff")]
    public void MinRrSkip_IsReportedOncePerArmedEpisode_AndLastSkipSurvivesTheEpisode(string endsEpisode)
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99m;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingSizeRefusal);

        s.ClearPendingSignals();
        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
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
                s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
                cfg.AllowLong = true;
                break;
        }
        Assert.False(s.IsArmed);
        Assert.NotNull(s.GetSnapshot().LastSkip); // outlives the episode

        s.ClearPendingSignals();
        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
        Assert.NotNull(s.PendingSizeRefusal);    // second episode reports again
    }

    [Fact]
    public void FailsBudgetAndMinimum_ReportsTheSizeRefusalNotAMinRrSkip()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99m;
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk = 1m;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Null(s.PendingEntry);
        Assert.Equal(RefusalReason.Size, s.PendingSizeRefusal!.Reason);
        Assert.Null(s.GetSnapshot().LastSkip);
    }

    [Theory]
    [InlineData(40,  1, 5200)]
    [InlineData(80,  2, 5190)]
    [InlineData(160, 4, 5185)]
    public void WholePositionDollars_TargetIsSpreadOverTheSizedContracts(int budget, int contracts, int target)
    {
        // Stop 2 pts x $20 = $40 a contract, so the budget sizes 1, 2 or 4; $400 over the position.
        var cfg = DefaultConfig();
        cfg.AutoSizeByRisk = true;
        cfg.MaxTradeRisk = budget;
        cfg.MaxContracts = 4;
        cfg.TargetMode = TargetMode.Dollars;
        cfg.TargetDollars = 400m;
        cfg.TargetDollarsBasis = TargetDollarsBasis.WholePosition;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        var e = s.PendingEntry!;
        Assert.Equal(contracts, e.TotalContracts);
        Assert.Equal((decimal)target, e.Tg2Price);
    }

    [Fact]
    public void OneContractWithPartial_DollarTarget_IsASingleTg2Bracket()
    {
        var cfg = DefaultConfig();
        cfg.Contracts = 1;
        cfg.MaxContracts = 1;
        cfg.UsePartial = true;
        cfg.TargetMode = TargetMode.Dollars;
        cfg.TargetDollars = 400m;                // $400 / $20 = 20 pts
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        var leg = Assert.Single(s.PendingEntry!.ResolveBrackets());
        Assert.Equal(5200m, leg.TargetPrice);
        Assert.Equal(1, leg.Qty);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_EntersAtTheMinimum()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 12m;                         // range target gives 10R
        cfg.MinRrAction = MinRrAction.RaiseTarget;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        var e = s.PendingEntry!;
        Assert.Equal(5204m, e.Tg2Price);         // 12 x 2 pts
        Assert.Equal(5192m, e.Tg1Price);         // 50% of 24 pts
        Assert.Null(s.PendingSizeRefusal);

        var unraised = DefaultConfig();
        unraised.MinRr = 1m;
        var baseline = new OrbFakeoutStrategy(unraised);
        baseline.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());
        Assert.Equal(baseline.PendingEntry!.Stop, e.Stop);
        Assert.Equal(baseline.PendingEntry.TotalContracts, e.TotalContracts);
    }

    [Fact]
    public void GuardOff_TakesTradeBelowMinimum()
    {
        var cfg = DefaultConfig();
        cfg.MinRr = 99m;
        cfg.EnforceMinRr = false;
        var s = new OrbFakeoutStrategy(cfg);

        s.OnBar(MakeBar(5178m, 5182m, 5175m, 5179m), MakeOrb(), MakeIndicators(), FakeoutBearModules());

        Assert.Equal(5200m, s.PendingEntry!.Tg2Price);
        Assert.False(s.GetSnapshot().MinRrEnforced);
        Assert.Equal(99m, s.GetSnapshot().MinRr);
    }
}
