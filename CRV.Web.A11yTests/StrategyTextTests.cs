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
}
