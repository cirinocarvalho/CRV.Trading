namespace CRV.Core.Models;

/// <summary>
/// The trading day a moment belongs to. Futures sessions span midnight: from the session
/// start hour (18:00 ET for CME) on, it is already the next calendar day's session.
/// </summary>
public static class TradingDay
{
    public static DateTime Of(DateTime localTime, int sessionStartHour)
        => localTime.Hour >= sessionStartHour ? localTime.Date.AddDays(1) : localTime.Date;

    public static DateTime OfUtc(DateTime utc, TimeZoneInfo zone, int sessionStartHour)
        => Of(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone), sessionStartHour);
}
