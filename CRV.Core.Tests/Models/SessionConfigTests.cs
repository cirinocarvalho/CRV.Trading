using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Models;

public class SessionConfigTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static StrategyConfig BaseGlobal() => new()
    {
        Ticker            = "/NQH2026",
        PointValue        = 20m,
        TickSize          = 0.25m,
        Broker            = "Mock",
        Contracts         = 2,
        MaxContracts      = 2,
        CommissionPerSide = 2.25m,
        // A
        EnableA           = true,
        ContractsA        = 3,
        PartialCtsA       = 1,
        PartialPctA       = 50,
        CutoffHourA       = 14,
        CutoffMinuteA     = 30,
        MaxTradesA        = 5,
        OrderTypeA        = "Limit",
        MinRrA            = 1.5m,
        CloseAtRthCloseA  = true,
        UsePartialA       = true,
        UseBeA            = true,
        HiVolMultA        = 1.2m,
        MaxContractsA     = 4,
        ModeA             = "Aggressive",
        NearPctA          = 0.20m,
        NearPct           = 0.20m,
        PullbackPct       = 0.55m,
        StopPctA          = 0.12m,
        TargetPctA        = 150,
        EntryTickOffsetA  = 2,
        UseVwapA          = false,
        UseOrbCloseA      = true,
        // B defaults are fine
        EnableB           = false,
        NearPctB          = 0.15m,
        RetestPct         = 0.05m,
        StopPctB          = 0.50m,
        TargetPctB        = 100,
        PartialPctB       = 50,
        MinRrB            = 1.5m,
        // Module params
        SweepMinPenetration  = 0.75m,
        SweepMinBodyReject   = 1.50m,
        SweepEqualTolerance  = 2.50m,
        SweepConfirmBars     = 2,
    };

    private static SessionConfig NySessionFromBase()
    {
        var g = BaseGlobal();
        return new SessionConfig
        {
            SessionId         = SessionId.NY,
            Enabled           = true,
            OrbStart          = new TimeOnly(9, 30),
            OrbEnd            = new TimeOnly(10, 0),
            RthStart          = new TimeOnly(9, 30),
            RthEnd            = new TimeOnly(16, 0),
            ExitMinutesBefore = 5,
            SetupA = new SetupConfigA
            {
                Enabled           = true,
                Contracts         = 3,
                PartialCts        = 1,
                PartialPct        = 50,
                CutoffHour        = 14,
                CutoffMinute      = 30,
                MaxTrades         = 5,
                OrderType         = "Limit",
                MinRr             = 1.5m,
                CloseAtRthClose   = true,
                UsePartial        = true,
                UseBe             = true,
                HiVolMult         = 1.2m,
                MaxContracts      = 4,
                Mode              = "Aggressive",
                NearPct           = 0.20m,
                PullbackPct       = 0.55m,
                StopPct           = 0.12m,
                TargetPct         = 150,
                EntryTickOffset   = 2,
                UseVwap           = false,
                UseOrbClose       = true,
            },
        };
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void ToLegacyConfig_MapsSessionTimes()
    {
        var session = NySessionFromBase();
        var global  = BaseGlobal();

        var result = session.ToLegacyConfig(global);

        Assert.Equal(new TimeOnly(9, 30),  result.OrbStart);
        Assert.Equal(new TimeOnly(10, 0),  result.OrbEnd);
        Assert.Equal(new TimeOnly(9, 30),  result.RthStart);
        Assert.Equal(new TimeOnly(16, 0),  result.RthEnd);
        Assert.Equal(5, result.ExitMinutesBefore);
    }

    [Fact]
    public void ToLegacyConfig_MapsSetupAFields()
    {
        var session = NySessionFromBase();
        var global  = BaseGlobal();

        var result = session.ToLegacyConfig(global);

        Assert.True(result.EnableA);
        Assert.Equal(3,       result.ContractsA);
        Assert.Equal(0.12m,   result.StopPctA);
        Assert.Equal("Aggressive", result.ModeA);
        Assert.Equal(5,       result.MaxTradesA);
        Assert.Equal(150,     result.TargetPctA);
    }


    [Fact]
    public void ToLegacyConfig_PreservesGlobalInstrumentFields()
    {
        var session = NySessionFromBase();
        var global  = BaseGlobal();
        global.Ticker = "/ESM2026";
        global.PointValue = 50m;

        var result = session.ToLegacyConfig(global);

        Assert.Equal("/ESM2026", result.Ticker);
        Assert.Equal(50m, result.PointValue);
    }

    [Fact]
    public void FromExistingConfig_PreservesNYValues()
    {
        var global = BaseGlobal();
        global.OrbStart          = new TimeOnly(9, 30);
        global.OrbEnd            = new TimeOnly(10, 0);
        global.RthStart          = new TimeOnly(9, 30);
        global.RthEnd            = new TimeOnly(16, 0);
        global.ExitMinutesBefore = 15;

        var session = SessionConfig.FromExistingConfig(global);

        Assert.Equal(SessionId.NY, session.SessionId);
        Assert.True(session.Enabled);

        // Session times
        Assert.Equal(new TimeOnly(9,  30), session.OrbStart);
        Assert.Equal(new TimeOnly(10,  0), session.OrbEnd);
        Assert.Equal(new TimeOnly(9,  30), session.RthStart);
        Assert.Equal(new TimeOnly(16,  0), session.RthEnd);
        Assert.Equal(15, session.ExitMinutesBefore);

        // Setup A fields
        Assert.True(session.SetupA.Enabled);
        Assert.Equal(global.ContractsA,  session.SetupA.Contracts);
        Assert.Equal(global.StopPctA,    session.SetupA.StopPct);
        Assert.Equal(global.ModeA,       session.SetupA.Mode);
        Assert.Equal(global.TargetPctA,  session.SetupA.TargetPct);
        Assert.Equal(global.UseVwapA,    session.SetupA.UseVwap);
        Assert.Equal(global.UseOrbCloseA, session.SetupA.UseOrbClose);

        // Setup C fields
    }

    [Fact]
    public void CreateDefaults_ReturnsThreeSessions()
    {
        var global = BaseGlobal();
        var sessions = SessionConfig.CreateDefaults(global);

        Assert.Equal(3, sessions.Count);

        var asia   = sessions.Single(s => s.SessionId == SessionId.Asia);
        var london = sessions.Single(s => s.SessionId == SessionId.London);
        var ny     = sessions.Single(s => s.SessionId == SessionId.NY);

        Assert.False(asia.Enabled,   "Asia should be disabled by default");
        Assert.False(london.Enabled, "London should be disabled by default");
        Assert.True(ny.Enabled,      "NY should be enabled");

        // Asia window
        Assert.Equal(new TimeOnly(19, 0),  asia.OrbStart);
        Assert.Equal(new TimeOnly(23, 59), asia.RthEnd);

        // London window
        Assert.Equal(new TimeOnly(3, 0),   london.OrbStart);
        Assert.Equal(new TimeOnly(8, 0),   london.RthEnd);

        // NY inherits from existing config
        Assert.Equal(global.OrbStart, ny.OrbStart);
        Assert.Equal(global.OrbEnd,   ny.OrbEnd);
        Assert.Equal(global.RthStart, ny.RthStart);
        Assert.Equal(global.RthEnd,   ny.RthEnd);
    }
}
