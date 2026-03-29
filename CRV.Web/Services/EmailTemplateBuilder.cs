using CRV.Core.Models;

namespace CRV.Web.Services;

public static class EmailTemplateBuilder
{
    public static (string subject, string body) BuildInstantAlert(AlertEvent alert)
    {
        var subject = $"[CRV] {alert.Type}: {alert.Message}";
        var body = $"""
            <html><body style="font-family:monospace;font-size:14px;color:#e0e0e0;background:#1a1a2e;padding:20px;">
            <div style="max-width:600px;margin:0 auto;">
              <h2 style="color:#f0c040;margin:0 0 16px 0;">{alert.Type}</h2>
              <table style="border-collapse:collapse;width:100%;">
                <tr><td style="padding:4px 8px;color:#888;">Time</td><td style="padding:4px 8px;">{alert.Time:HH:mm:ss}</td></tr>
                <tr><td style="padding:4px 8px;color:#888;">Setup</td><td style="padding:4px 8px;">{alert.SetupLabel}</td></tr>
                <tr><td style="padding:4px 8px;color:#888;">Message</td><td style="padding:4px 8px;">{alert.Message}</td></tr>
              </table>
              <hr style="border-color:#333;margin:16px 0;" />
              <small style="color:#666;">Sent by CRV.Trading</small>
            </div>
            </body></html>
            """;
        return (subject, body);
    }

    public static (string subject, string body) BuildDigest(IReadOnlyList<AlertEvent> alerts)
    {
        var first = alerts[0].Time;
        var last  = alerts[^1].Time;
        var subject = $"[CRV] Alert Digest — {alerts.Count} alerts ({first:HH:mm}–{last:HH:mm})";

        var rows = string.Join("\n", alerts.Select(a =>
            $"""<tr><td style="padding:4px 8px;color:#888;">{a.Time:HH:mm:ss}</td><td style="padding:4px 8px;color:#f0c040;">{a.Type}</td><td style="padding:4px 8px;">{a.Message}</td></tr>"""));

        var body = $"""
            <html><body style="font-family:monospace;font-size:14px;color:#e0e0e0;background:#1a1a2e;padding:20px;">
            <div style="max-width:600px;margin:0 auto;">
              <h2 style="color:#f0c040;margin:0 0 16px 0;">Alert Digest — {alerts.Count} alerts</h2>
              <table style="border-collapse:collapse;width:100%;">
                <tr style="border-bottom:1px solid #333;">
                  <th style="padding:4px 8px;text-align:left;color:#888;">Time</th>
                  <th style="padding:4px 8px;text-align:left;color:#888;">Type</th>
                  <th style="padding:4px 8px;text-align:left;color:#888;">Message</th>
                </tr>
                {rows}
              </table>
              <hr style="border-color:#333;margin:16px 0;" />
              <small style="color:#666;">Sent by CRV.Trading</small>
            </div>
            </body></html>
            """;
        return (subject, body);
    }

    public static (string subject, string body) BuildSessionEndSummary(
        string sessionId, DateTime date, DailyStats stats)
    {
        var subject = $"[CRV] Session End: {sessionId} — {date:yyyy-MM-dd}";

        var setupRows = string.Join("\n", stats.PerSetup.Select(kv =>
        {
            var s = kv.Value;
            return $"""<tr><td style="padding:4px 8px;">{kv.Key}</td><td style="padding:4px 8px;">{s.Wins}</td><td style="padding:4px 8px;">{s.Losses}</td><td style="padding:4px 8px;">{s.WinPnl:C2}</td><td style="padding:4px 8px;">{s.LossPnl:C2}</td><td style="padding:4px 8px;">{s.WinRate:F1}%</td><td style="padding:4px 8px;">{s.Expectancy:C2}</td></tr>""";
        }));

        var body = $"""
            <html><body style="font-family:monospace;font-size:14px;color:#e0e0e0;background:#1a1a2e;padding:20px;">
            <div style="max-width:700px;margin:0 auto;">
              <h2 style="color:#f0c040;margin:0 0 16px 0;">Session End: {sessionId}</h2>
              <h3 style="color:#ccc;margin:0 0 12px 0;">Overall</h3>
              <table style="border-collapse:collapse;width:100%;">
                <tr><td style="padding:4px 8px;color:#888;">P&amp;L</td><td style="padding:4px 8px;">{stats.TodayPnL:C2}</td></tr>
                <tr><td style="padding:4px 8px;color:#888;">Net P&amp;L</td><td style="padding:4px 8px;{(stats.TodayNetPnL >= 0 ? "color:#4caf50" : "color:#f44336")};">{stats.TodayNetPnL:C2}</td></tr>
                <tr><td style="padding:4px 8px;color:#888;">Trades</td><td style="padding:4px 8px;">{stats.TodayTrades}</td></tr>
                <tr><td style="padding:4px 8px;color:#888;">Win Rate</td><td style="padding:4px 8px;">{stats.WinRate:F1}%</td></tr>
                <tr><td style="padding:4px 8px;color:#888;">Avg Win</td><td style="padding:4px 8px;">{stats.AvgWin:C2}</td></tr>
                <tr><td style="padding:4px 8px;color:#888;">Avg Loss</td><td style="padding:4px 8px;">{stats.AvgLoss:C2}</td></tr>
                <tr><td style="padding:4px 8px;color:#888;">Max Drawdown</td><td style="padding:4px 8px;">{stats.TodayMaxDD:C2}</td></tr>
              </table>
              {(stats.PerSetup.Count > 0 ? $"""
              <h3 style="color:#ccc;margin:16px 0 12px 0;">Per Setup</h3>
              <table style="border-collapse:collapse;width:100%;">
                <tr style="border-bottom:1px solid #333;">
                  <th style="padding:4px 8px;text-align:left;color:#888;">Setup</th>
                  <th style="padding:4px 8px;text-align:left;color:#888;">W</th>
                  <th style="padding:4px 8px;text-align:left;color:#888;">L</th>
                  <th style="padding:4px 8px;text-align:left;color:#888;">Win$</th>
                  <th style="padding:4px 8px;text-align:left;color:#888;">Loss$</th>
                  <th style="padding:4px 8px;text-align:left;color:#888;">WR</th>
                  <th style="padding:4px 8px;text-align:left;color:#888;">Exp</th>
                </tr>
                {setupRows}
              </table>
              """ : "")}
              <hr style="border-color:#333;margin:16px 0;" />
              <small style="color:#666;">Sent by CRV.Trading</small>
            </div>
            </body></html>
            """;
        return (subject, body);
    }
}
