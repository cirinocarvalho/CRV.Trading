using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Models;

/// <summary>Futures sessions span midnight: from the session start hour (18:00 ET) on, it is the next trading day.</summary>
public class TradingDayTests
{
    private static readonly TimeZoneInfo Ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Theory]
    [InlineData(17, 59, 15)]
    [InlineData(18, 0, 16)]
    [InlineData(9, 30, 15)]
    public void Of_RollsAtTheSessionStartHour(int hour, int minute, int expectedDay)
        => Assert.Equal(new DateTime(2026, 4, expectedDay), TradingDay.Of(new DateTime(2026, 4, 15, hour, minute, 0), 18));

    [Theory]
    [InlineData(2026, 4, 15, 21, 59, 15)]   // 17:59 EDT
    [InlineData(2026, 4, 15, 22, 0, 16)]    // 18:00 EDT
    [InlineData(2026, 4, 16, 1, 0, 16)]     // 21:00 EDT the evening before; UTC already says the 16th
    public void OfUtc_UsesTheExchangeClock(int y, int mo, int d, int h, int mi, int expectedDay)
        => Assert.Equal(new DateTime(2026, 4, expectedDay),
            TradingDay.OfUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc), Ny, 18));

    [Theory]
    [InlineData(2026, 1, 15, 22, 59, 15)]   // 17:59 EST
    [InlineData(2026, 1, 15, 23, 0, 16)]    // 18:00 EST
    public void OfUtc_InWinter_RollsAtEighteenEst(int y, int mo, int d, int h, int mi, int expectedDay)
        => Assert.Equal(new DateTime(2026, 1, expectedDay),
            TradingDay.OfUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc), Ny, 18));

    [Theory]
    [InlineData("2026-03-07T22:59:00Z", "2026-03-07")]   // 17:59 EST, day before DST starts
    [InlineData("2026-03-07T23:00:00Z", "2026-03-08")]   // 18:00 EST
    [InlineData("2026-03-08T21:59:00Z", "2026-03-08")]   // 17:59 EDT, DST start day
    [InlineData("2026-03-08T22:00:00Z", "2026-03-09")]   // 18:00 EDT
    [InlineData("2026-10-31T21:59:00Z", "2026-10-31")]   // 17:59 EDT, day before DST ends
    [InlineData("2026-10-31T22:00:00Z", "2026-11-01")]   // 18:00 EDT
    [InlineData("2026-11-01T22:59:00Z", "2026-11-01")]   // 17:59 EST, DST end day
    [InlineData("2026-11-01T23:00:00Z", "2026-11-02")]   // 18:00 EST
    public void OfUtc_AroundDstChanges_FollowsTheLocalClock(string utc, string expectedDay)
        => Assert.Equal(DateTime.Parse(expectedDay),
            TradingDay.OfUtc(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), Ny, 18));

    [Fact]
    public void StrategyConfig_AgreesWithTradingDay()
    {
        var cfg = new StrategyConfig { Timezone = "America/New_York", SessionStartHour = 18 };
        var utc = new DateTime(2026, 4, 15, 22, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 4, 16), cfg.TradingDateOfUtc(utc));
        Assert.Equal(new DateTime(2026, 4, 16), cfg.TradingDate(new DateTime(2026, 4, 15, 18, 30, 0)));
    }
}
