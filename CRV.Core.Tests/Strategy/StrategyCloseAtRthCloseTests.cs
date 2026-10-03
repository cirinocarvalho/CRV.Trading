using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>Every strategy reports whether it closes at the session or holds, straight from its config.</summary>
public class StrategyCloseAtRthCloseTests
{
    public static TheoryData<StrategyType> OrbTypes => new()
    {
        StrategyType.Pullback, StrategyType.Retest, StrategyType.OrbFakeout, StrategyType.SessionFakeout,
    };

    private static StrategySetupConfig Config(StrategyType type, bool close) => new()
    {
        Id = "s", Name = "s", StrategyType = type, Enabled = true,
        Ticker = "MNQZ26", PointValue = 2m, TickSize = 0.25m, CloseAtRthClose = close,
    };

    [Theory, MemberData(nameof(OrbTypes))]
    public void CloseAtRthClose_ComesFromTheConfig(StrategyType type)
    {
        Assert.True(StrategyFactory.Create(Config(type, true)).CloseAtRthClose);
        Assert.False(StrategyFactory.Create(Config(type, false)).CloseAtRthClose);
    }

    [Theory, MemberData(nameof(OrbTypes))]
    public void CloseAtRthClose_FollowsAReconfigure(StrategyType type)
    {
        var s = StrategyFactory.Create(Config(type, true));
        s.Reconfigure(Config(type, false));
        Assert.False(s.CloseAtRthClose);
    }

    [Fact]
    public void ManualTrades_CloseAtSessionEnd()
    {
        var signal = new EntrySignal(SetupId.F, Direction.Long, 100m, 95m, 110m, 0m, 1, DateTime.UtcNow,
            Ticker: "MNQZ26", SetupLabel: "manual-1");
        Assert.True(new ManualStrategy(signal).CloseAtRthClose);
    }
}
