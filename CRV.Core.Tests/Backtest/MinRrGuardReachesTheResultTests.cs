using CRV.Backtest.Results;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The fixture's trade: entry 18010, stop 18000 (10 pts), target 18030 (20 pts) = 2R, and price
/// then runs to 18100. A 3R minimum puts it below the guard.
/// </summary>
public class MinRrGuardReachesTheResultTests
{
    [Fact]
    public async Task BelowMinimum_Skip_RunReportsTheSkip_NotASizeRefusal()
    {
        var result = await PullbackSessionFixture.Run(PullbackSessionFixture.Config(s => s.MinRr = 3m));

        Assert.Empty(result.Trades);
        Assert.Empty(result.SizeRefusals);
        var skip = Assert.Single(result.MinRrSkips);
        Assert.Equal(RefusalReason.MinRr, skip.Reason);
        Assert.Equal(2m, skip.Rr);
        Assert.Equal(3m, skip.MinRr);
        Assert.Equal(1, result.PerSetup["pullback-mnq"].MinRrSkips);
        Assert.Equal(0, result.PerSetup["pullback-mnq"].SizeRefusals);
        Assert.Equal(new RrGuardState(true, 3m, MinRrAction.Skip), result.PerSetup["pullback-mnq"].RrGuard);
    }

    [Fact]
    public async Task BelowMinimum_RaiseTarget_TradesWithTheTargetAtTheMinimum()
    {
        var result = await PullbackSessionFixture.Run(PullbackSessionFixture.Config(s =>
        {
            s.MinRr = 3m;
            s.MinRrAction = MinRrAction.RaiseTarget;
        }));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(18040m, trade.Target);      // 3 x 10 pts
        Assert.Empty(result.MinRrSkips);
    }

    [Fact]
    public async Task GuardOff_TradesBelowMinimum_AndTheResultSaysSo()
    {
        var result = await PullbackSessionFixture.Run(PullbackSessionFixture.Config(s =>
        {
            s.MinRr = 3m;
            s.EnforceMinRr = false;
        }));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(18030m, trade.Target);
        Assert.False(result.PerSetup["pullback-mnq"].RrGuard!.Enforced);
    }
}
