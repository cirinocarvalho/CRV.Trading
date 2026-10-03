using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Web.Pages.Setup;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>The plain-words description says whether a setup closes at session end or holds.</summary>
public class StrategyTextTests
{
    private static BasketEntry Entry(bool close) => new()
    {
        Id = "retest-mnq", Label = "Retest [MNQ]", StrategyType = StrategyType.Retest, Ticker = "/MNQZ26",
        Config = new StrategySetupConfig { CloseAtRthClose = close },
    };

    [Fact]
    public void Describe_ClosingSetup_SaysItClosesAtSessionEnd()
        => Assert.EndsWith("Closes at the end of the session.", StrategyText.Describe(Entry(true)));

    [Fact]
    public void Describe_HoldingSetup_SaysItHoldsPastTheCutoff()
        => Assert.EndsWith("Holds an open trade past the cutoff.", StrategyText.Describe(Entry(false)));

    private static StrategySetupConfig Cfg(Action<StrategySetupConfig>? set = null)
    {
        var c = new StrategySetupConfig { TargetPct = 100, MinRr = 1.5m };
        set?.Invoke(c);
        return c;
    }

    private static BasketEntry Entry(StrategySetupConfig c) => new()
    {
        Id = "of-mnq", StrategyType = StrategyType.OrbFakeout, Ticker = "/MNQZ26", Config = c,
    };

    [Fact]
    public void Target_DescribesEveryMode()
    {
        Assert.Equal("target 100% of range", StrategyText.Target(Cfg()));
        Assert.Equal("target $400 / contract", StrategyText.Target(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 400m; })));
        Assert.Equal("target $150 / position", StrategyText.Target(Cfg(c =>
            { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 150m; c.TargetDollarsBasis = TargetDollarsBasis.WholePosition; })));
        Assert.Equal("target $1,500 / contract", StrategyText.Target(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 1500m; })));
        Assert.Equal("target 2R", StrategyText.Target(Cfg(c => { c.TargetMode = TargetMode.RiskMultiple; c.AtrTp2Mult = 2m; })));
    }

    [Fact]
    public void Describe_GuardOff_SaysItTakesTradesBelowTheMinimum()
        => Assert.Contains("Takes trades below 1.5R: the reward / risk guard is off.",
            StrategyText.Describe(Entry(Cfg(c => c.EnforceMinRr = false))));

    [Fact]
    public void Describe_GuardOn_SaysWhatHappensBelowTheMinimum()
    {
        Assert.Contains("Skips trades below 1.5R.", StrategyText.Describe(Entry(Cfg())));
        Assert.Contains("Moves the target out to 1.5R when a trade's stop would leave less.",
            StrategyText.Describe(Entry(Cfg(c => c.MinRrAction = MinRrAction.RaiseTarget))));
    }

    [Fact]
    public void Describe_DollarTarget_NamesTheAmount()
        => Assert.Contains("target is $400 a contract",
            StrategyText.Describe(Entry(Cfg(c => { c.TargetMode = TargetMode.Dollars; c.TargetDollars = 400m; }))));

    [Fact]
    public void GuardOffNote_CountsStrategies()
    {
        Assert.Equal("1 strategy that's on doesn't enforce its minimum reward / risk, so it can take trades that risk more than they can win.",
            StrategyText.GuardOffNote(1));
        Assert.Equal("3 strategies that are on don't enforce their minimum reward / risk, so they can take trades that risk more than they can win.",
            StrategyText.GuardOffNote(3));
    }
}
