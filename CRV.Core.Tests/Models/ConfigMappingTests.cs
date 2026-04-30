using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Models;

public class ConfigMappingTests
{
    private static StrategyConfig CustomConfig() => new()
    {
        Ticker             = "/ESH2026",
        PointValue         = 50m,
        TickSize           = 0.25m,
        ExecutionTFMinutes = 5,
        Timezone           = "America/Chicago",
        OrbStart           = new TimeOnly(8, 30),
        OrbEnd             = new TimeOnly(9, 0),
        RthStart           = new TimeOnly(8, 30),
        RthEnd             = new TimeOnly(15, 0),
        ExitMinutesBefore  = 15,
        SessionStartHour   = 17,
        AtrFilterPct       = 0.75m,
        UseDailyLossLimit  = true,
        MaxDailyLoss       = 1000m,
        CommissionPerSide  = 3.00m,
        AllowBothSameBar   = true,
        Broker             = "Tradovate",
        ExecBroker         = "Mock",
        AccountId          = "acct-1",
        ExecAccountId      = "acct-2",
        ReplayDate         = new DateTime(2026, 3, 10),
        ReplaySpeed        = 200,

        // Module params
        SweepMinPenetration  = 0.75m,
        SweepMinBodyReject   = 1.50m,
        SweepEqualTolerance  = 3.00m,
        SweepConfirmBars     = 2,
        DriveRangeAtrMult    = 0.90m,
        DriveMaxPullback     = 0.40m,
        DriveBullBearRatio   = 3,
        TrendDayThreshold    = 5,
        ShallowPullbackMax   = 0.30m,
        VwapDevPeriod        = 25,

        // False breakout module params
        FBMaxTimeOutsideMinutesOrb = 20,
        FBMaxTimeOutsideMinutesSR  = 90,
        FBMaxPenetrationPctOrb     = 0.40m,
        FBMaxPenetrationPctSR      = 0.35m,
        FBMinRejectionBodyPct      = 0.60m,
        FBMaxTrendDayScore         = 70,

        // Setup A
        EnableA            = true,
        ModeA              = "Aggressive",
        MaxTradesA         = 3,
        NearPctA           = 0.20m,
        PullbackPct        = 0.60m,
        StopPctA           = 0.15m,
        TargetPctA         = 80,
        PartialPctA        = 40,
        MinRrA             = 2.0m,
        UsePartialA        = false,
        UseBeA             = false,
        AllowRearmAfterBeA = false,
        EntryTickOffsetA   = 2,
        OrderTypeA         = "Limit",
        PartialCtsA        = 1,
        MaxAdverseMinutesA = 10,
        ContractsA         = 4,
        HiVolMultA         = 1.5m,
        MaxContractsA      = 6,
        UseVwapA           = false,
        UseOrbCloseA       = true,
        CutoffHourA        = 13,
        CutoffMinuteA      = 0,
        CloseAtRthCloseA   = false,

        // Setup B
        EnableB            = true,
        ModeB              = "Aggressive",
        MaxTradesB         = 4,
        NearPctB           = 0.10m,
        RetestPct          = 0.08m,
        StopPctB           = 0.40m,
        TargetPctB         = 90,
        PartialPctB        = 60,
        MinRrB             = 1.8m,
        UsePartialB        = true,
        UseBeB             = true,
        AllowRearmAfterBeB = false,
        EntryTickOffsetB   = 1,
        OrderTypeB         = "Limit",
        PartialCtsB        = 1,
        MaxAdverseMinutesB = 5,
        ContractsB         = 3,
        HiVolMultB         = 1.2m,
        MaxContractsB      = 4,
        UseVwapB           = false,
        UseOrbCloseB       = true,
        CutoffHourB        = 12,
        CutoffMinuteB      = 45,
        CloseAtRthCloseB   = false,

        // Setup C
        EnableC            = true,
        MaxTradesC         = 2,
        NearPctC           = 0.25m,
        StopPctC           = 0.20m,
        TargetPctC         = 70,
        PartialPctC        = 30,
        MinRrC             = 2.5m,
        UsePartialC        = false,
        UseBeC             = false,
        AllowRearmAfterBeC = false,
        EntryTickOffsetC   = 3,
        OrderTypeC         = "Limit",
        PartialCtsC        = 1,
        MaxAdverseMinutesC = 8,
        ContractsC         = 5,
        HiVolMultC         = 2.0m,
        MaxContractsC      = 8,
        CutoffHourC        = 11,
        CutoffMinuteC      = 15,
        CloseAtRthCloseC   = false,

        // Setup D
        EnableD            = true,
        MaxTradesD         = 1,
        NearPctD           = 0.30m,
        StopPctD           = 0.25m,
        TargetPctD         = 60,
        PartialPctD        = 25,
        MinRrD             = 3.0m,
        UsePartialD        = true,
        UseBeD             = true,
        AllowRearmAfterBeD = true,
        EntryTickOffsetD   = 4,
        OrderTypeD         = "Market",
        PartialCtsD        = 0,
        MaxAdverseMinutesD = 12,
        ContractsD         = 6,
        HiVolMultD         = 1.8m,
        MaxContractsD      = 10,
        CutoffHourD        = 10,
        CutoffMinuteD      = 0,
        CloseAtRthCloseD   = true,
    };

    // ── ToEngineConfig tests ──────────────────────────────────────

    [Fact]
    public void ToEngineConfig_MapsAllGlobalFields()
    {
        var cfg = CustomConfig();
        var ec = cfg.ToEngineConfig();

        Assert.Equal(cfg.SessionStartHour,   ec.SessionStartHour);
        Assert.Equal(cfg.OrbStart,           ec.OrbStart);
        Assert.Equal(cfg.OrbEnd,             ec.OrbEnd);
        Assert.Equal(cfg.RthStart,           ec.RthStart);
        Assert.Equal(cfg.RthEnd,             ec.RthEnd);
        Assert.Equal(cfg.ExitMinutesBefore,  ec.ExitMinutesBefore);
        Assert.Equal(cfg.Timezone,           ec.Timezone);
        Assert.Equal(cfg.ExecutionTFMinutes, ec.ExecutionTFMinutes);
        Assert.Equal(cfg.Ticker,             ec.Ticker);
        Assert.Equal(cfg.PointValue,         ec.PointValue);
        Assert.Equal(cfg.TickSize,           ec.TickSize);
        Assert.Equal(cfg.UseDailyLossLimit,  ec.UseDailyLossLimit);
        Assert.Equal(cfg.MaxDailyLoss,       ec.MaxDailyLoss);
        Assert.Equal(cfg.AtrFilterPct,       ec.AtrFilterPct);
        Assert.Equal(cfg.CommissionPerSide,  ec.CommissionPerSide);
        Assert.Equal(cfg.AllowBothSameBar,   ec.AllowBothSameBar);
        Assert.Equal(cfg.Broker,             ec.Broker);
        Assert.Equal(cfg.ExecBroker,         ec.ExecBroker);
        Assert.Equal(cfg.AccountId,          ec.AccountId);
        Assert.Equal(cfg.ExecAccountId,      ec.ExecAccountId);
        Assert.Equal(cfg.ReplayDate,         ec.ReplayDate);
        Assert.Equal(cfg.ReplaySpeed,        ec.ReplaySpeed);
    }

    [Fact]
    public void ToEngineConfig_MapsModuleConfig()
    {
        var cfg = CustomConfig();
        var ec = cfg.ToEngineConfig();
        var m = ec.Modules;

        Assert.Equal(cfg.TickSize,            m.TickSize);
        Assert.Equal(cfg.PointValue,          m.PointValue);
        Assert.Equal(cfg.Timezone,            m.Timezone);
        Assert.Equal(cfg.SweepMinPenetration, m.MinTickPenetration);
        Assert.Equal(cfg.SweepMinBodyReject,  m.MinBodyReject);
        Assert.Equal(cfg.SweepEqualTolerance, m.EqualLevelTolerance);
        Assert.Equal(cfg.SweepConfirmBars,    m.ConfirmationBars);
        Assert.Equal(cfg.DriveRangeAtrMult,   m.DriveRangeAtrMult);
        Assert.Equal(cfg.DriveMaxPullback,    m.MaxDrivePullback);
        Assert.Equal(cfg.DriveBullBearRatio,  m.DriveBullBearRatio);
        Assert.Equal(cfg.TrendDayThreshold,   m.TrendDayThreshold);
        Assert.Equal(cfg.ShallowPullbackMax,  m.ShallowPullbackMax);
        Assert.Equal(cfg.VwapDevPeriod,       m.VwapDevPeriod);
    }

    [Fact]
    public void ToEngineConfig_MapsFalseBreakoutParams()
    {
        var cfg = CustomConfig();
        var ec = cfg.ToEngineConfig();

        Assert.Equal(cfg.FBMaxTimeOutsideMinutesOrb, ec.FBMaxTimeOutsideMinutesOrb);
        Assert.Equal(cfg.FBMaxTimeOutsideMinutesSR,  ec.FBMaxTimeOutsideMinutesSR);
        Assert.Equal(cfg.FBMaxPenetrationPctOrb,     ec.FBMaxPenetrationPctOrb);
        Assert.Equal(cfg.FBMaxPenetrationPctSR,      ec.FBMaxPenetrationPctSR);
        Assert.Equal(cfg.FBMinRejectionBodyPct,      ec.FBMinRejectionBodyPct);
        Assert.Equal(cfg.FBMaxTrendDayScore,         ec.FBMaxTrendDayScore);
    }

    [Fact]
    public void ToChopRegimeConfig_MapsAllChopFields()
    {
        var cfg = new StrategyConfig
        {
            UseChopFilter             = true,
            ChopMinVotes              = 3,
            ChopUseRangeCompression   = false,
            ChopCompressionRatio      = 0.65m,
            ChopUseFlatVwap           = true,
            ChopFlatSlopeThresholdPct = 0.08m,
            ChopUseWeakDrive          = false,
            ChopMinDriveRatio         = 0.40m,
            ChopUseLowVolume          = true,
            ChopMinVolumeRatio        = 0.85m,
        };

        var c = cfg.ToChopRegimeConfig();

        Assert.True(c.Enabled);
        // MinVotes is clamped by Normalize() when used; raw mapping should preserve it.
        // (Detector calls Normalize() internally.)
        Assert.Equal(3,         c.MinVotesForChop);
        Assert.False(c.F1_RangeCompressionEnabled);
        Assert.Equal(0.65m,     c.F1_CompressionRatio);
        Assert.True(c.F2_FlatVwapEnabled);
        Assert.Equal(0.08m,     c.F2_FlatSlopeThresholdPct);
        Assert.False(c.F3_WeakDriveEnabled);
        Assert.Equal(0.40m,     c.F3_MinDriveRatio);
        Assert.True(c.F4_LowVolumeEnabled);
        Assert.Equal(0.85m,     c.F4_MinVolumeRatio);
    }

    [Fact]
    public void ToChopRegimeConfig_DisabledWhenFilterOff()
    {
        var cfg = new StrategyConfig { UseChopFilter = false };
        Assert.False(cfg.ToChopRegimeConfig().Enabled);
    }

    [Fact]
    public void ToEngineConfig_PreservesChopFields()
    {
        // Regression: AddSetup → BuildStrategyConfigFromEngine round-trip used to
        // strip chop fields, so backtests ignored the user's chop config entirely.
        var cfg = new StrategyConfig
        {
            UseChopFilter             = true,
            ChopBlockMode             = ChopBlockMode.RestOfDay,
            ChopMinVotes              = 3,
            ChopUseRangeCompression   = false,
            ChopCompressionRatio      = 0.65m,
            ChopUseFlatVwap           = true,
            ChopFlatSlopeThresholdPct = 0.08m,
            ChopUseWeakDrive          = false,
            ChopMinDriveRatio         = 0.40m,
            ChopUseLowVolume          = true,
            ChopMinVolumeRatio        = 0.85m,
        };
        var ec = cfg.ToEngineConfig();

        Assert.True(ec.UseChopFilter);
        Assert.Equal(ChopBlockMode.RestOfDay, ec.ChopBlockMode);
        Assert.Equal(3,        ec.ChopMinVotes);
        Assert.False(ec.ChopUseRangeCompression);
        Assert.Equal(0.65m,    ec.ChopCompressionRatio);
        Assert.True(ec.ChopUseFlatVwap);
        Assert.Equal(0.08m,    ec.ChopFlatSlopeThresholdPct);
        Assert.False(ec.ChopUseWeakDrive);
        Assert.Equal(0.40m,    ec.ChopMinDriveRatio);
        Assert.True(ec.ChopUseLowVolume);
        Assert.Equal(0.85m,    ec.ChopMinVolumeRatio);
    }

    [Fact]
    public void ToSetupConfigs_FromBasket_PropagatesBypassChopAndFakeoutSession()
    {
        // Regression: ToSetupConfig(BasketEntry) used to silently drop BypassChopFilter
        // and FakeoutReferenceSession, so per-setup chop opt-out and Setup-D fade-session
        // choice never reached the strategy when configured via the basket editor.
        var basket = """
        [{
          "Id":"d-mnq-1",
          "Enabled":true,
          "Label":"D - SessionFakeout",
          "StrategyType":3,
          "Ticker":"/MNQM26",
          "Config":{
            "BypassChopFilter":true,
            "FakeoutReferenceSession":2
          }
        }]
        """;
        var cfg = new StrategyConfig { BasketJson = basket };
        var setups = cfg.ToSetupConfigs();

        Assert.Single(setups);
        Assert.True(setups[0].BypassChopFilter);
        Assert.Equal(FakeoutSession.Asia, setups[0].FakeoutReferenceSession);
    }

    // ── ToSetupConfigs tests ─────────────────────────────────────

    [Fact]
    public void ToSetupConfigs_ProducesFourConfigs()
    {
        var cfg = CustomConfig();
        var setups = cfg.ToSetupConfigs();

        Assert.Equal(4, setups.Count);
        Assert.Equal("A", setups[0].Name);
        Assert.Equal("B", setups[1].Name);
        Assert.Equal("C", setups[2].Name);
        Assert.Equal("D", setups[3].Name);
    }

    [Fact]
    public void ToSetupConfigs_SetupA_HasCorrectValues()
    {
        var cfg = CustomConfig();
        var a = cfg.ToSetupConfigs()[0];

        Assert.Equal(SetupId.A,           a.SetupId);
        Assert.Equal(StrategyType.Pullback, a.StrategyType);
        Assert.True(a.Enabled);
        Assert.Equal(cfg.Ticker,           a.Ticker); // no custom ticker
        Assert.Equal(cfg.PointValue,       a.PointValue);
        Assert.Equal(cfg.TickSize,         a.TickSize);
        Assert.Equal(cfg.ContractsA,       a.Contracts);
        Assert.Equal(cfg.HiVolMultA,       a.HiVolMult);
        Assert.Equal(cfg.MaxContractsA,    a.MaxContracts);
        Assert.Equal(cfg.StopPctA,         a.StopPct);
        Assert.Equal(cfg.TargetPctA,       a.TargetPct);
        Assert.Equal(cfg.PartialPctA,      a.PartialPct);
        Assert.Equal(cfg.NearPctA,         a.NearPct);
        Assert.Equal(cfg.MinRrA,           a.MinRr);
        Assert.Equal(cfg.ModeA,            a.Mode);
        Assert.Equal(cfg.PullbackPct,      a.PullbackPct);
        Assert.Equal(cfg.EntryTickOffsetA, a.EntryTickOffset);
        Assert.Equal(cfg.OrderTypeA,       a.OrderType);
        Assert.Equal(cfg.UseVwapA,         a.UseVwap);
        Assert.Equal(cfg.UseOrbCloseA,     a.UseOrbClose);
        Assert.Equal(cfg.CutoffHourA,      a.CutoffHour);
        Assert.Equal(cfg.CutoffMinuteA,    a.CutoffMinute);
        Assert.Equal(cfg.CloseAtRthCloseA, a.CloseAtRthClose);
        Assert.Equal(cfg.MaxTradesA,       a.MaxTrades);
        Assert.Equal(cfg.MaxAdverseMinutesA, a.MaxAdverseMinutes);
        Assert.Equal(cfg.UsePartialA,      a.UsePartial);
        Assert.Equal(cfg.UseBeA,           a.UseBe);
        Assert.Equal(cfg.PartialCtsA,      a.PartialCts);
        Assert.Equal(cfg.AllowRearmAfterBeA, a.AllowRearmAfterBe);
    }

    [Fact]
    public void ToSetupConfigs_SetupB_HasCorrectValues()
    {
        var cfg = CustomConfig();
        var b = cfg.ToSetupConfigs()[1];

        Assert.Equal(SetupId.B,         b.SetupId);
        Assert.Equal(StrategyType.Retest, b.StrategyType);
        Assert.True(b.Enabled);
        Assert.Equal(cfg.StopPctB,      b.StopPct);
        Assert.Equal(cfg.RetestPct,     b.RetestPct);
        Assert.Equal(cfg.ModeB,         b.Mode);
        Assert.Equal(cfg.NearPctB,      b.NearPct);
        Assert.Equal(cfg.ContractsB,    b.Contracts);
    }

    [Fact]
    public void ToSetupConfigs_SetupC_ForcesConservativeAndNoVwap()
    {
        var cfg = CustomConfig();
        var c = cfg.ToSetupConfigs()[2];

        Assert.Equal(SetupId.C,              c.SetupId);
        Assert.Equal(StrategyType.OrbFakeout, c.StrategyType);
        Assert.Equal("Conservative",          c.Mode);
        Assert.False(c.UseVwap);
        Assert.False(c.UseOrbClose);
    }

    [Fact]
    public void ToSetupConfigs_SetupD_ForcesConservativeAndNoVwap()
    {
        var cfg = CustomConfig();
        var d = cfg.ToSetupConfigs()[3];

        Assert.Equal(SetupId.D,                   d.SetupId);
        Assert.Equal(StrategyType.SessionFakeout,  d.StrategyType);
        Assert.Equal("Conservative",               d.Mode);
        Assert.False(d.UseVwap);
        Assert.False(d.UseOrbClose);
        Assert.Equal(cfg.ContractsD,               d.Contracts);
        Assert.Equal(cfg.MaxAdverseMinutesD,       d.MaxAdverseMinutes);
    }

    [Fact]
    public void ToSetupConfigs_CustomTicker_ResolvesEffectiveValues()
    {
        var cfg = new StrategyConfig
        {
            Ticker     = "/NQH2026",
            PointValue = 20m,
            TickSize   = 0.25m,
            UseCustomTickerA = true,
            TickerA          = "/ESH2026",
            PointValueA      = 50m,
            TickSizeA        = 0.25m,
        };
        var a = cfg.ToSetupConfigs()[0];

        Assert.Equal("/ESH2026", a.Ticker);
        Assert.Equal(50m,        a.PointValue);
    }

    // ── Round-trip: ToSetupConfigs matches engine's BuildSetupConfig ──

    [Fact]
    public void ToSetupConfigs_MatchesEngineBuildHelpers()
    {
        var cfg = CustomConfig();
        var setups = cfg.ToSetupConfigs();

        // The methods on StrategyConfig are the canonical source;
        // verify they produce results identical to direct calls.
        AssertSetupConfigEqual(cfg.BuildSetupConfigA(), setups[0]);
        AssertSetupConfigEqual(cfg.BuildSetupConfigB(), setups[1]);
        AssertSetupConfigEqual(cfg.BuildSetupConfigC(), setups[2]);
        AssertSetupConfigEqual(cfg.BuildSetupConfigD(), setups[3]);
    }

    private static void AssertSetupConfigEqual(StrategySetupConfig expected, StrategySetupConfig actual)
    {
        Assert.Equal(expected.Name,              actual.Name);
        Assert.Equal(expected.SetupId,           actual.SetupId);
        Assert.Equal(expected.StrategyType,      actual.StrategyType);
        Assert.Equal(expected.Enabled,           actual.Enabled);
        Assert.Equal(expected.Ticker,            actual.Ticker);
        Assert.Equal(expected.PointValue,        actual.PointValue);
        Assert.Equal(expected.TickSize,          actual.TickSize);
        Assert.Equal(expected.Contracts,         actual.Contracts);
        Assert.Equal(expected.HiVolMult,         actual.HiVolMult);
        Assert.Equal(expected.MaxContracts,      actual.MaxContracts);
        Assert.Equal(expected.StopPct,           actual.StopPct);
        Assert.Equal(expected.TargetPct,         actual.TargetPct);
        Assert.Equal(expected.PartialPct,        actual.PartialPct);
        Assert.Equal(expected.NearPct,           actual.NearPct);
        Assert.Equal(expected.MinRr,             actual.MinRr);
        Assert.Equal(expected.Mode,              actual.Mode);
        Assert.Equal(expected.PullbackPct,       actual.PullbackPct);
        Assert.Equal(expected.RetestPct,         actual.RetestPct);
        Assert.Equal(expected.EntryTickOffset,   actual.EntryTickOffset);
        Assert.Equal(expected.OrderType,         actual.OrderType);
        Assert.Equal(expected.UseVwap,           actual.UseVwap);
        Assert.Equal(expected.UseOrbClose,       actual.UseOrbClose);
        Assert.Equal(expected.CutoffHour,        actual.CutoffHour);
        Assert.Equal(expected.CutoffMinute,      actual.CutoffMinute);
        Assert.Equal(expected.CloseAtRthClose,   actual.CloseAtRthClose);
        Assert.Equal(expected.MaxTrades,         actual.MaxTrades);
        Assert.Equal(expected.MaxAdverseMinutes, actual.MaxAdverseMinutes);
        Assert.Equal(expected.UsePartial,        actual.UsePartial);
        Assert.Equal(expected.UseBe,             actual.UseBe);
        Assert.Equal(expected.PartialCts,        actual.PartialCts);
        Assert.Equal(expected.AllowRearmAfterBe, actual.AllowRearmAfterBe);
    }
}
