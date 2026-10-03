using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// A held position outlives session and day resets. Only the broker event handler's
/// completion clears InTrade.
/// </summary>
public class StrategyResetKeepsTradeTests
{
    public static TheoryData<StrategyType> OrbTypes => new()
    {
        StrategyType.Pullback, StrategyType.Retest, StrategyType.OrbFakeout, StrategyType.SessionFakeout,
    };

    private static StrategySetupConfig Config(StrategyType type) => new()
    {
        Id = "s", Name = "s", StrategyType = type, Enabled = true,
        Ticker = "MNQZ26", PointValue = 2m, TickSize = 0.25m, CloseAtRthClose = false,
    };

    [Theory, MemberData(nameof(OrbTypes))]
    public void Resets_KeepAnOpenTrade(StrategyType type)
    {
        var s = StrategyFactory.Create(Config(type));
        s.SetInTrade(true);

        s.ResetSession();
        Assert.True(s.InTrade);
        s.Reset();
        Assert.True(s.InTrade);
        s.ResetTradeCounters();
        Assert.True(s.InTrade);
    }

    [Fact]
    public void ResetWarmupCounters_KeepsARecoveredPosition()
    {
        var engine = new ComposableEngine(new NoopExec(), new NullSink(), new FixedPrices(), new EngineConfig());
        engine.AddSetup(Config(StrategyType.Retest));
        var s = engine.GetStrategy("s")!;
        s.SetInTrade(true);   // what restart recovery does for a position still open at the broker

        engine.ResetWarmupCounters();

        Assert.True(s.InTrade);
    }

    [Theory, MemberData(nameof(OrbTypes))]
    public async Task StopFill_ClearsInTrade(StrategyType type)
    {
        var handler = new BrokerEventHandler(new SimulatedGroupExec());
        var s = StrategyFactory.Create(Config(type));
        var group = new GroupOrder
        {
            GroupOrderId = "g1", SetupId = "s", Ticker = "MNQZ26", Direction = Direction.Long,
            TotalContracts = 1, PointValue = 2m, EntryPrice = 18010m, InitialStopPrice = 18000m,
            Status = GroupOrderStatus.Active,
        };
        group.Legs.Add(new OrderLeg { GroupOrderId = "g1", OrderId = "g1-s", LegType = LegType.Stop, Price = 18000m, Quantity = 1 });
        handler.RegisterGroup(group, s);
        s.SetInTrade(true);
        s.ResetSession();
        Assert.True(s.InTrade);

        await handler.HandleEventAsync(new OrderEvent("g1", "g1-s", LegType.Stop, OrderLegStatus.Filled,
            18000m, 1, null, null, DateTime.UtcNow));

        Assert.False(s.InTrade);
    }
}
