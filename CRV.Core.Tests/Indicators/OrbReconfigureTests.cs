namespace CRV.Core.Tests.Indicators;

using CRV.Core.Indicators;
using Xunit;

public class OrbReconfigureTests
{
    [Fact]
    public void Reconfigure_UpdatesOrbWindow()
    {
        var orb = new OrbCalculator(new(9, 30), new(10, 0), "America/New_York");
        orb.Reconfigure(new(19, 0), new(19, 30));
        orb.Reset();
        Assert.False(orb.IsSet);
    }

    [Fact]
    public void Reset_ClearsOrbState()
    {
        var orb = new OrbCalculator(new(9, 30), new(10, 0), "America/New_York");
        orb.Restore(5000m, 4950m, 0.5m, DateTime.Today);
        Assert.True(orb.IsSet);
        orb.Reset();
        Assert.False(orb.IsSet);
        Assert.Equal(0m, orb.OrbHigh);
    }
}
