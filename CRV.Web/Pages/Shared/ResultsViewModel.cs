using CRV.Backtest.Results;
using CRV.Core.Models;

namespace CRV.Web.Pages;

/// <summary>Input to _ResultsView.cshtml: one result set from any source (live, paper, backtest, a session).</summary>
public sealed record ResultsViewModel(
    BacktestResult Result,
    string CsvName,
    bool ShowSource = false,
    string EmptyTitle = "No trades",
    string EmptyText = "");

/// <summary>Formatting shared by the rebuilt pages, so numbers read the same everywhere.</summary>
public static class ViewFmt
{
    public static readonly TimeZoneInfo Et = FindEt();

    private static TimeZoneInfo FindEt()
    {
        try   { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }

    public static DateTime ToEt(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Et);

    /// <summary>Signed whole dollars with a real minus sign: +$1,912 / −$684.</summary>
    public static string Money(decimal v) => (v >= 0 ? "+$" : "−$") + Math.Abs(v).ToString("N0");

    /// <summary>Unsigned whole dollars: $203.</summary>
    public static string Dollars(decimal v) => "$" + Math.Abs(v).ToString("N0");

    public static string Tone(decimal v) => v > 0 ? "c-up" : v < 0 ? "c-down" : "";

    public static string Pf(PerformanceMetrics m) =>
        m.ProfitFactor == 0 && m.NetPnl > 0 ? "∞" : m.ProfitFactor.ToString("F2");

    public static string R(decimal r) => r.ToString("+0.00;−0.00;0.00") + "R";

    public static string SetupOf(TradeRecord t) => !string.IsNullOrEmpty(t.SetupLabel) ? t.SetupLabel : t.Setup.ToString();

    public static string Reason(ExitReason r) => r switch
    {
        ExitReason.SessionEnd  => "End of day",
        ExitReason.AdverseTime => "Time stop",
        _                      => r.ToString(),
    };
}
