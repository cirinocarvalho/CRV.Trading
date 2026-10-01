namespace CRV.Core.Tests.Brokers;

using CRV.Live;
using Xunit;

public class TradingDayRangeTests
{
    private static readonly TimeZoneInfo Et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void OneDay_StartsTheEveningBefore()
    {
        // Oct 1 2026 is EDT (UTC-4): Sep 30 18:00 ET = 22:00 UTC.
        var (from, to) = TradingDayRange.ToUtc(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), 18, Et);
        Assert.Equal(new DateTime(2026, 9, 30, 22, 0, 0), from);
        Assert.Equal(new DateTime(2026, 10, 1, 22, 0, 0), to);
    }

    [Fact]
    public void ReversedRange_IsSwapped()
    {
        var a = TradingDayRange.ToUtc(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3), 18, Et);
        var b = TradingDayRange.ToUtc(new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 1), 18, Et);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("2026-09-30T22:30:00Z", "2026-10-01")] // 18:30 ET → next trading day
    [InlineData("2026-09-30T21:59:00Z", "2026-09-30")] // 17:59 ET → same day
    [InlineData("2026-10-01T13:45:00Z", "2026-10-01")] // NY morning
    public void Of_UsesSessionStart(string utc, string expected)
    {
        var t = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(DateOnly.Parse(expected), TradingDayRange.Of(t, 18, Et));
    }
}
