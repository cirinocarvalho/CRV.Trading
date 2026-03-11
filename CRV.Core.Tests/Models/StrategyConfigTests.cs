using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Models;

public class StrategyConfigTests
{
    private static StrategyConfig ValidConfig() => new()
    {
        Ticker           = "/NQH2026",
        PointValue       = 20m,
        TickSize         = 0.25m,
        ExecutionTFMinutes = 1,
        OrbStart         = new TimeOnly(9, 30),
        OrbEnd           = new TimeOnly(10, 0),
        RthStart         = new TimeOnly(9, 30),
        RthEnd           = new TimeOnly(16, 0),
        AtrFilterPct     = 0.5m,
        MaxDailyLoss     = 500m,
        UseDailyLossLimit = true,
        Contracts        = 2,
        MaxContracts     = 2,
        CommissionPerSide = 2.25m,
        CutoffHour       = 14,
        CutoffMinute     = 30,
        EnableA          = true,
        StopPctA         = 0.10m,
        NearPct          = 0.15m,
        PullbackPct      = 0.50m,
        MinRrA           = 1.5m,
        MaxTradesA       = 5,
        TargetPctA       = 100,
        PartialPctA      = 50,
        EnableB          = true,
        StopPctB         = 0.50m,
        RetestPct        = 0.05m,
        MinRrB           = 1.5m,
        MaxTradesB       = 5,
        TargetPctB       = 100,
        PartialPctB      = 50,
    };

    [Fact]
    public void ValidConfig_ReturnsNoErrors()
    {
        var errors = ValidConfig().Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void EmptyTicker_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.Ticker = "";
        Assert.Contains(cfg.Validate(), e => e.Contains("Ticker"));
    }

    [Fact]
    public void NegativePointValue_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.PointValue = -1m;
        Assert.Contains(cfg.Validate(), e => e.Contains("PointValue"));
    }

    [Fact]
    public void ZeroTickSize_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.TickSize = 0m;
        Assert.Contains(cfg.Validate(), e => e.Contains("TickSize"));
    }

    [Fact]
    public void OrbEndBeforeOrbStart_ReturnsError()
    {
        var cfg = ValidConfig();
        cfg.OrbStart = new TimeOnly(10, 0);
        cfg.OrbEnd   = new TimeOnly(9, 30);
        Assert.Contains(cfg.Validate(), e => e.Contains("OrbEnd"));
    }

    [Fact]
    public void RthEndBeforeRthStart_ReturnsError()
    {
        var cfg = ValidConfig();
        cfg.RthStart = new TimeOnly(16, 0);
        cfg.RthEnd   = new TimeOnly(9, 30);
        Assert.Contains(cfg.Validate(), e => e.Contains("RthEnd"));
    }

    [Fact]
    public void ZeroContracts_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.Contracts = 0;
        Assert.Contains(cfg.Validate(), e => e.Contains("Contracts"));
    }

    [Fact]
    public void MaxContractsLessThanContracts_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.Contracts = 4; cfg.MaxContracts = 2;
        Assert.Contains(cfg.Validate(), e => e.Contains("MaxContracts"));
    }

    [Fact]
    public void InvalidCutoffHour_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.CutoffHour = 25;
        Assert.Contains(cfg.Validate(), e => e.Contains("CutoffHour"));
    }

    [Fact]
    public void NegativeMinRrA_WhenEnabledA_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.EnableA = true; cfg.MinRrA = -1m;
        Assert.Contains(cfg.Validate(), e => e.Contains("MinRrA"));
    }

    [Fact]
    public void InvalidPullbackPct_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.EnableA = true; cfg.PullbackPct = 1.5m;
        Assert.Contains(cfg.Validate(), e => e.Contains("PullbackPct"));
    }

    [Fact]
    public void InvalidRetestPct_WhenEnabledB_ReturnsError()
    {
        var cfg = ValidConfig(); cfg.EnableB = true; cfg.RetestPct = 0m;
        Assert.Contains(cfg.Validate(), e => e.Contains("RetestPct"));
    }

    [Fact]
    public void DailyLossLimitEnabled_ZeroMaxDailyLoss_ReturnsError()
    {
        var cfg = ValidConfig();
        cfg.UseDailyLossLimit = true;
        cfg.MaxDailyLoss      = 0m;
        Assert.Contains(cfg.Validate(), e => e.Contains("MaxDailyLoss"));
    }

    [Fact]
    public void MultipleErrors_AllReported()
    {
        var cfg = ValidConfig();
        cfg.Ticker     = "";
        cfg.PointValue = -1m;
        cfg.Contracts  = 0;
        var errors = cfg.Validate();
        Assert.True(errors.Count >= 3, $"Expected >= 3 errors, got {errors.Count}: {string.Join(", ", errors)}");
    }

    [Fact]
    public void Validate_ValidExecBroker_NoError()
    {
        var cfg = ValidConfig();
        cfg.ExecBroker = "Tradovate";
        var errs = cfg.Validate();
        Assert.DoesNotContain(errs, e => e.Contains("ExecBroker"));
    }

    [Fact]
    public void Validate_InvalidExecBroker_ReturnsError()
    {
        var cfg = ValidConfig();
        cfg.ExecBroker = "Bogus";
        var errs = cfg.Validate();
        Assert.Contains(errs, e => e.Contains("ExecBroker"));
    }

    [Fact]
    public void Validate_EnableB_StopPctBZero_ReturnsError()
    {
        var cfg = ValidConfig();
        cfg.EnableB   = true;
        cfg.StopPctB  = 0m;      // invalid: must be > 0
        var errors = cfg.Validate();
        Assert.Contains(errors, e => e.Contains("StopPctB"));
    }

    [Fact]
    public void Validate_EnableB_StopPctBPositive_NoError()
    {
        var cfg = ValidConfig();
        cfg.EnableB   = true;
        cfg.StopPctB  = 0.50m;   // valid
        var errors = cfg.Validate();
        Assert.DoesNotContain(errors, e => e.Contains("StopPctB"));
    }
}
