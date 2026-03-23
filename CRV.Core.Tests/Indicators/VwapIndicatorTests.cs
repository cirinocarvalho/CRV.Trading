using CRV.Core.Indicators;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Indicators;

public class VwapIndicatorTests
{
    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static Bar MakeBar(DateTime utcTime, decimal high, decimal low, decimal close, long volume)
        => new(utcTime, (high + low) / 2m, high, low, close, volume);

    private static DateTime MakeUtc(int hour, int minute, int day = 1)
        => TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2025, 1, day, hour, minute, 0, DateTimeKind.Unspecified), EasternTz);

    [Fact]
    public void NotReady_WithNoVolume()
    {
        var vwap = new VwapIndicator();
        Assert.False(vwap.IsReady);
    }

    [Fact]
    public void SingleBar_VwapEqualsHlc3()
    {
        var vwap = new VwapIndicator();
        var bar  = MakeBar(MakeUtc(9, 30), 100m, 90m, 95m, 1000);
        vwap.Update(bar, EasternTz);

        decimal expectedHlc3  = (100m + 90m + 95m) / 3m;
        Assert.True(vwap.IsReady);
        Assert.Equal(expectedHlc3, vwap.Value);
    }

    [Fact]
    public void TwoBars_VwapIsVolumeWeighted()
    {
        var vwap = new VwapIndicator();
        var bar1 = MakeBar(MakeUtc(9, 30), 100m, 90m, 95m, 1000); // HLC3 = 95
        var bar2 = MakeBar(MakeUtc(9, 31), 110m, 100m, 105m, 2000); // HLC3 = 105

        vwap.Update(bar1, EasternTz);
        vwap.Update(bar2, EasternTz);

        // VWAP = (95*1000 + 105*2000) / 3000 = (95000 + 210000) / 3000 = 305000/3000 ≈ 101.667
        decimal expected = (95m * 1000m + 105m * 2000m) / 3000m;
        Assert.Equal(expected, vwap.Value);
    }

    [Fact]
    public void ResetsOnNewDay()
    {
        var vwap = new VwapIndicator();
        var day1Bar = MakeBar(MakeUtc(9, 30, day: 1), 200m, 190m, 195m, 1000);
        var day2Bar = MakeBar(MakeUtc(9, 30, day: 2), 100m, 90m, 95m, 500);

        vwap.Update(day1Bar, EasternTz);
        decimal day1Vwap = vwap.Value;

        vwap.Update(day2Bar, EasternTz);
        // Should be reset — only day2 bar in cumulative
        decimal expectedDay2 = (100m + 90m + 95m) / 3m;
        Assert.NotEqual(day1Vwap, vwap.Value);
        Assert.Equal(expectedDay2, vwap.Value);
    }
}
