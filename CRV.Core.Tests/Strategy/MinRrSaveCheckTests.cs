using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>Saving is blocked when the target is below MinRr x the typical stop. MNQ ($2 a point),
/// minimum 1.5R, typical stop 40 pts, typical position risk 80 pts.</summary>
public class MinRrSaveCheckTests
{
    private static StrategySetupConfig Cfg(Action<StrategySetupConfig> set)
    {
        var c = new StrategySetupConfig { MinRr = 1.5m, StopPct = 0.10m, StopMode = "OrbPct" };
        set(c);
        return c;
    }

    private static MinRrSaveResult Check(StrategySetupConfig c, decimal? stop = 40m, decimal? position = 80m)
        => MinRrSaveCheck.Check(c, pointValue: 2m, stop, position);

    [Theory]
    [InlineData(119.99, "Raise the target to at least $120 a contract, or lower the minimum reward / risk.")]
    [InlineData(120,    null)]
    [InlineData(120.01, null)]
    public void Dollars_PerContract_AtTheBoundary(double dollars, string? error)
    {
        var r = Check(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = (decimal)dollars; }));

        Assert.Equal(error, r.Error);
        Assert.Null(r.Warning);
    }

    [Theory]
    [InlineData(239.99, "Raise the target to at least $240 for the whole position, or lower the minimum reward / risk.")]
    [InlineData(240,    null)]
    [InlineData(240.01, null)]
    public void Dollars_WholePosition_AtTheBoundary(double dollars, string? error)
    {
        var r = Check(Cfg(c =>
        {
            c.TargetMode = TargetMode.Dollars; c.TargetDollars = (decimal)dollars;
            c.TargetDollarsBasis = TargetDollarsBasis.WholePosition;
        }));

        Assert.Equal(error, r.Error);
    }

    [Theory]
    [InlineData(14, "Raise the target to at least 15% of the range, or lower the minimum reward / risk.")]
    [InlineData(15, null)]
    [InlineData(16, null)]
    public void RangePct_OrbPctStop_AtTheBoundary(int targetPct, string? error)
        => Assert.Equal(error, Check(Cfg(c => c.TargetPct = targetPct), stop: null).Error);

    [Fact]
    public void RangePct_RoundsTheMinimumUpToAWholePercent()
        => Assert.Equal("Raise the target to at least 53% of the range, or lower the minimum reward / risk.",
            Check(Cfg(c => { c.StopPct = 0.35m; c.TargetPct = 52; })).Error);

    [Theory]
    [InlineData(1.49, "Raise the target to at least 1.5R, or lower the minimum reward / risk.")]
    [InlineData(1.5,  null)]
    [InlineData(1.51, null)]
    public void RiskMultiple_AtTheBoundary(double tp2, string? error)
        => Assert.Equal(error, Check(Cfg(c => { c.TargetMode = TargetMode.RiskMultiple; c.AtrTp2Mult = (decimal)tp2; })).Error);

    [Fact]
    public void Dollars_WithoutHistory_SavesWithTheWarning()
    {
        var r = Check(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 1m; }), stop: null, position: null);

        Assert.Null(r.Error);
        Assert.Equal("The reward / risk check runs once this strategy has a backtest.", r.Warning);
    }

    [Fact]
    public void RangePct_BarStop_SavesWithAWarning()
    {
        var r = Check(Cfg(c => { c.StopMode = "BarHL"; c.TargetPct = 1; }));

        Assert.Null(r.Error);
        Assert.Equal(MinRrSaveCheck.StopMovesWithBar, r.Warning);
    }

    [Fact]
    public void Atr_SavesWithAWarning()
        => Assert.Equal(MinRrSaveCheck.AtrPerTrade, Check(Cfg(c => c.TargetMode = TargetMode.Atr)).Warning);

    [Fact]
    public void GuardOff_NoCheckAndNoWarning()
    {
        var r = Check(Cfg(c => { c.EnforceMinRr = false; c.TargetMode = TargetMode.Dollars; c.TargetDollars = 1m; }));

        Assert.Equal(new MinRrSaveResult(null, null), r);
    }
}
