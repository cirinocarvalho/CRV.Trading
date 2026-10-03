using CRV.Core.Models;

namespace CRV.Live;

/// <summary>
/// Futures trading days start the evening before: with a session start of 18:00 ET, the
/// trading day "Oct 1" runs from Sep 30 18:00 ET to Oct 1 18:00 ET. Matches
/// <c>StrategyConfig.TradingDate</c>.
/// </summary>
public static class TradingDayRange
{
    /// <summary>UTC bounds for trading days <paramref name="from"/>..<paramref name="to"/> inclusive; the end is exclusive.</summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) ToUtc(DateOnly from, DateOnly to, int sessionStartHour, TimeZoneInfo et)
    {
        if (to < from) (from, to) = (to, from);
        var start = from.ToDateTime(TimeOnly.MinValue).AddDays(-1).AddHours(sessionStartHour);
        var end   = to.ToDateTime(TimeOnly.MinValue).AddHours(sessionStartHour);
        return (TimeZoneInfo.ConvertTimeToUtc(start, et), TimeZoneInfo.ConvertTimeToUtc(end, et));
    }

    /// <summary>The trading day a UTC instant belongs to.</summary>
    public static DateOnly Of(DateTime utc, int sessionStartHour, TimeZoneInfo et)
    {
        return DateOnly.FromDateTime(TradingDay.OfUtc(utc, et, sessionStartHour));
    }
}
