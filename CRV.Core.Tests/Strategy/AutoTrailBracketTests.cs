namespace CRV.Core.Tests.Strategy;

using CRV.Core.Models;
using Xunit;

public class AutoTrailBracketTests
{
    [Fact]
    public void EntrySignal_WithAutoTrail_CarriesParams()
    {
        var sig = new EntrySignal(
            SetupId.A, Direction.Long,
            Entry: 6628.5m, Stop: 6613.0m, Tg2Price: 6659.5m, Tg1Price: 6644.0m,
            TotalContracts: 2, Time: DateTime.UtcNow,
            UsePartial: true, UseBe: false, PartialContracts: 1,
            AutoTrailStopLoss: 4.0m, AutoTrailTrigger: null, AutoTrailFreq: 0.25m);

        Assert.Equal(4.0m, sig.AutoTrailStopLoss);
        Assert.Null(sig.AutoTrailTrigger);
        Assert.Equal(0.25m, sig.AutoTrailFreq);
    }

    [Fact]
    public void EntrySignal_WithAutoTrail_NoPartial_RequiresTrigger()
    {
        var sig = new EntrySignal(
            SetupId.A, Direction.Long,
            Entry: 6628.5m, Stop: 6613.0m, Tg2Price: 6659.5m, Tg1Price: 6644.0m,
            TotalContracts: 2, Time: DateTime.UtcNow,
            UsePartial: false,
            AutoTrailStopLoss: 4.0m, AutoTrailTrigger: 8.0m, AutoTrailFreq: 0.25m);

        Assert.Equal(8.0m, sig.AutoTrailTrigger);
    }
}
