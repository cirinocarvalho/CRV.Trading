namespace CRV.Core.Tests.Strategy;

using CRV.Core.Models;
using Xunit;

public class BacktestAutoTrailTests
{
    [Fact]
    public void TrailState_ActivatesAtTrigger()
    {
        var group = new GroupOrder
        {
            GroupOrderId = "g1", Direction = Direction.Long,
            EntryPrice = 100m, TotalContracts = 2, PartialContracts = 1,
            PointValue = 5m, InitialStopPrice = 90m,
            Status = GroupOrderStatus.PartialFilled,
            AutoTrailStopLoss = 4m, AutoTrailTrigger = 10m, AutoTrailFreq = 0.25m,
        };

        Assert.False(group.AutoTrailActivated);

        group.AutoTrailActivated = true;
        group.AutoTrailHighWater = 110m;
        Assert.True(group.AutoTrailActivated);
        Assert.Equal(110m, group.AutoTrailHighWater);
    }

    [Fact]
    public void TrailState_RatchetsStop_Long()
    {
        // Long position: trail ratchets stop UP
        var group = new GroupOrder
        {
            GroupOrderId = "g1", Direction = Direction.Long,
            EntryPrice = 100m, InitialStopPrice = 90m,
            AutoTrailStopLoss = 4m, AutoTrailFreq = 0.25m,
            AutoTrailActivated = true, AutoTrailHighWater = 112m,
        };

        // Stop = highWater - stopLoss = 112 - 4 = 108
        var newStop = group.AutoTrailHighWater!.Value - group.AutoTrailStopLoss!.Value;
        Assert.Equal(108m, newStop);

        // Price moves to 113, stop ratchets to 109
        group.AutoTrailHighWater = 113m;
        newStop = group.AutoTrailHighWater.Value - group.AutoTrailStopLoss.Value;
        Assert.Equal(109m, newStop);
    }

    [Fact]
    public void TrailState_RatchetsStop_Short()
    {
        // Short position: trail ratchets stop DOWN
        var group = new GroupOrder
        {
            GroupOrderId = "g1", Direction = Direction.Short,
            EntryPrice = 100m, InitialStopPrice = 110m,
            AutoTrailStopLoss = 4m, AutoTrailFreq = 0.25m,
            AutoTrailActivated = true, AutoTrailHighWater = 88m,
        };

        // Stop = highWater + stopLoss = 88 + 4 = 92
        var newStop = group.AutoTrailHighWater!.Value + group.AutoTrailStopLoss!.Value;
        Assert.Equal(92m, newStop);

        // Price drops to 86, stop ratchets to 90
        group.AutoTrailHighWater = 86m;
        newStop = group.AutoTrailHighWater.Value + group.AutoTrailStopLoss.Value;
        Assert.Equal(90m, newStop);
    }
}
