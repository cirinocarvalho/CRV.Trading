using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// A signal the risk budget refused has to be visible in the run that refused it.
/// Before this, a backtest over a budget too small for the setup's stops reported
/// "0 trades" and nothing else — indistinguishable from a setup that never fired.
/// The fixture's trade risks $20 a contract (10-point stop at $2 a point).
/// </summary>
public class SizeRefusalReachesTheResultTests
{
    [Fact]
    public async Task BudgetBelowOneContract_RunReportsTheRefusal_NotJustZeroTrades()
    {
        var cfg = PullbackSessionFixture.Config(s =>
        {
            s.AutoSizeByRisk = true;
            s.MaxTradeRisk   = 10m;   // half a contract's risk
        });

        var result = await PullbackSessionFixture.Run(cfg);

        Assert.Empty(result.Trades);

        var refusal = Assert.Single(result.SizeRefusals);
        Assert.Equal("pullback-mnq", refusal.SetupLabel);
        Assert.Equal(PullbackSessionFixture.Ticker, refusal.Ticker);
        Assert.Equal(10m, refusal.StopDistance);
        Assert.Equal(20m, refusal.RiskPerContract);
        Assert.Equal(10m, refusal.Budget);

        Assert.Equal(1, result.Total.SizeRefusals);
        Assert.Equal(1, result.PerSetup["pullback-mnq"].SizeRefusals);
        Assert.Equal(0, result.PerSetup["pullback-mnq"].TotalTrades);
    }

    [Fact]
    public async Task BudgetFitsOneContract_TradesAndRefusesNothing()
    {
        var cfg = PullbackSessionFixture.Config(s =>
        {
            s.AutoSizeByRisk = true;
            s.MaxTradeRisk   = 25m;   // one $20 contract fits
            s.Contracts      = 2;     // asks for two; the old floor rule would have refused
            s.MaxContracts   = 2;
        });

        var result = await PullbackSessionFixture.Run(cfg);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(1, trade.Contracts);
        Assert.Empty(result.SizeRefusals);
        Assert.Equal(0, result.Total.SizeRefusals);
    }
}
