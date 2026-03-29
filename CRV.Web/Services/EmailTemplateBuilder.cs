// CRV.Web/Services/EmailTemplateBuilder.cs
using CRV.Core.Models;

namespace CRV.Web.Services;

/// <summary>Builds HTML email bodies for alert notifications and session summaries.</summary>
public static class EmailTemplateBuilder
{
    private static readonly TimeZoneInfo _et =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Convert UTC DateTime to ET for display.</summary>
    private static string FormatET(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, _et).ToString("yyyy-MM-dd HH:mm:ss") + " ET";

    private static string FormatTimeET(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, _et).ToString("HH:mm:ss");

    private const string BaseStyle =
        "font-family:monospace;font-size:14px;color:#e0e0e0;background:#1a1a2e;padding:20px;";
    private const string ContainerStyle =
        "max-width:600px;margin:0 auto;";
    private const string HrStyle =
        "border-color:#333;margin:16px 0;";
    private const string FooterStyle =
        "color:#666;";

    public static (string subject, string body) BuildInstantAlert(AlertEvent alert)
    {
        var subject = $"[CRV] {alert.Type}: {alert.Message[..Math.Min(60, alert.Message.Length)]}";
        var color   = alert.Type switch
        {
            "Entry"                => "#4caf50",
            "Exit"                 => "#2196f3",
            "Daily Loss Breached"  => "#f44336",
            "ORB Formed"           => "#ff9800",
            "Engine"               => "#9c27b0",
            _                      => "#e0e0e0",
        };

        var setupRow = alert.Setup != default || !string.IsNullOrEmpty(alert.SetupLabel)
            ? $"<tr><td style='color:#888;padding-right:12px;'>Setup</td><td>{HtmlEncode(alert.SetupLabel)}</td></tr>"
            : "";

        var body = $"""
            <html><body style="{BaseStyle}">
            <div style="{ContainerStyle}">
              <h2 style="color:{color};margin:0 0 16px 0;">{HtmlEncode(alert.Type)}</h2>
              <table style="border-collapse:collapse;width:100%;">
                <tr><td style="color:#888;padding-right:12px;">Time</td><td>{FormatET(alert.Time)}</td></tr>
                {setupRow}
                <tr><td style="color:#888;padding-right:12px;">Detail</td><td>{HtmlEncode(alert.Message)}</td></tr>
              </table>
              <hr style="{HrStyle}" />
              <small style="{FooterStyle}">CRV.Trading Alert</small>
            </div>
            </body></html>
            """;

        return (subject, body);
    }

    public static (string subject, string body) BuildDigest(IReadOnlyList<AlertEvent> alerts)
    {
        var subject = $"[CRV] Digest: {alerts.Count} alert{(alerts.Count == 1 ? "" : "s")}";

        var rows = string.Join("\n", alerts.Select(a =>
            $"""
            <tr style="border-bottom:1px solid #333;">
              <td style="padding:6px 8px;color:#888;">{FormatTimeET(a.Time)}</td>
              <td style="padding:6px 8px;">{HtmlEncode(a.Type)}</td>
              <td style="padding:6px 8px;">{HtmlEncode(a.SetupLabel)}</td>
              <td style="padding:6px 8px;">{HtmlEncode(a.Message)}</td>
            </tr>
            """));

        var body = $"""
            <html><body style="{BaseStyle}">
            <div style="{ContainerStyle}">
              <h2 style="color:#e0e0e0;margin:0 0 16px 0;">Alert Digest — {alerts.Count} alert{(alerts.Count == 1 ? "" : "s")}</h2>
              <table style="border-collapse:collapse;width:100%;font-size:13px;">
                <thead>
                  <tr style="background:#252540;text-align:left;">
                    <th style="padding:6px 8px;">Time</th>
                    <th style="padding:6px 8px;">Type</th>
                    <th style="padding:6px 8px;">Setup</th>
                    <th style="padding:6px 8px;">Detail</th>
                  </tr>
                </thead>
                <tbody>
                  {rows}
                </tbody>
              </table>
              <hr style="{HrStyle}" />
              <small style="{FooterStyle}">CRV.Trading Digest</small>
            </div>
            </body></html>
            """;

        return (subject, body);
    }

    public static (string subject, string body) BuildSessionEndSummary(
        string sessionId, DateTime date, DailyStats stats)
    {
        var subject = $"[CRV] Session End: {sessionId} {date:yyyy-MM-dd} — Net {stats.TodayNetPnL:C2}";

        var pnlColor = stats.TodayNetPnL >= 0 ? "#4caf50" : "#f44336";
        var ddColor  = stats.DDBreached ? "#f44336" : "#e0e0e0";

        var body = $"""
            <html><body style="{BaseStyle}">
            <div style="{ContainerStyle}">
              <h2 style="color:#e0e0e0;margin:0 0 4px 0;">Session End Summary</h2>
              <p style="color:#888;margin:0 0 16px 0;">{sessionId} — {date:dddd, MMMM d, yyyy}</p>
              <table style="border-collapse:collapse;width:100%;">
                <tr><td style="color:#888;padding:4px 12px 4px 0;">Net P&L</td>
                    <td style="color:{pnlColor};font-weight:bold;">{stats.TodayNetPnL:C2}</td></tr>
                <tr><td style="color:#888;padding:4px 12px 4px 0;">Gross P&L</td>
                    <td>{stats.TodayPnL:C2}</td></tr>
                <tr><td style="color:#888;padding:4px 12px 4px 0;">Trades</td>
                    <td>{stats.TodayTrades} ({stats.TodayWins}W / {stats.TodayLosses}L)</td></tr>
                <tr><td style="color:#888;padding:4px 12px 4px 0;">Win Rate</td>
                    <td>{(stats.TodayTrades > 0 ? (double)stats.TodayWins / stats.TodayTrades * 100 : 0):F1}%</td></tr>
                <tr><td style="color:#888;padding:4px 12px 4px 0;">DD Breached</td>
                    <td style="color:{ddColor};">{(stats.DDBreached ? "Yes" : "No")}</td></tr>
              </table>
              <hr style="{HrStyle}" />
              <small style="{FooterStyle}">CRV.Trading Session Report</small>
            </div>
            </body></html>
            """;

        return (subject, body);
    }

    private static string HtmlEncode(string s) =>
        System.Web.HttpUtility.HtmlEncode(s);
}
