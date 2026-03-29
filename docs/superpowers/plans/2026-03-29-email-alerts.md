# Email Alerts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add email notifications for trading alerts and session-end summaries via SMTP, with granular on/off and instant/batched controls in the settings page.

**Architecture:** New `EmailNotificationService` implements `IStrategyEventSink` alongside `SignalREventSink`. A `CompositeEventSink` dispatches events to both. SMTP credentials stored in User Secrets; preferences stored in `StrategyConfig` (SQLite). Batched alerts use an in-memory queue drained by a timer.

**Tech Stack:** ASP.NET Core 10, `System.Net.Mail.SmtpClient`, EF Core (SQLite), Razor Pages, SignalR

---

### Task 1: SmtpSettings model + appsettings config

**Files:**
- Create: `CRV.Core/Models/SmtpSettings.cs`
- Modify: `CRV.Web/appsettings.json`
- Modify: `CRV.Web/Program.cs:14` (after builder creation)

- [ ] **Step 1: Create SmtpSettings model**

```csharp
// CRV.Core/Models/SmtpSettings.cs
namespace CRV.Core.Models;

public class SmtpSettings
{
    public string Host        { get; set; } = "";
    public int    Port        { get; set; } = 587;
    public string Username    { get; set; } = "";
    public string Password    { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public bool   UseSsl      { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password) &&
        !string.IsNullOrWhiteSpace(FromAddress);
}
```

- [ ] **Step 2: Add Smtp section to appsettings.json**

Add after the `"Seq"` section:

```json
"Smtp": {
  "Host": "",
  "Port": 587,
  "FromAddress": "",
  "UseSsl": true
}
```

Username and Password come from User Secrets:
```
dotnet user-secrets set "Smtp:Username" "your@email.com"
dotnet user-secrets set "Smtp:Password" "your-app-password"
```

- [ ] **Step 3: Register SmtpSettings in Program.cs**

Add after line 65 (`AddSingleton<DailyStatsService>()`):

```csharp
// ── SMTP settings (email alerts) ─────────────────────────────
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("Smtp"));
```

Add the using at top of Program.cs:
```csharp
using CRV.Core.Models;
```

- [ ] **Step 4: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add CRV.Core/Models/SmtpSettings.cs CRV.Web/appsettings.json CRV.Web/Program.cs
git commit -m "feat(email): add SmtpSettings model and appsettings config"
```

---

### Task 2: Email preference properties on StrategyConfig

**Files:**
- Modify: `CRV.Core/Models/StrategyConfig.cs`

- [ ] **Step 1: Add email properties to StrategyConfig**

Add after the `CloseAtRthClose` property (around line 210, before the computed helpers section):

```csharp
    // ── Email Alerts ──────────────────────────────────────────────
    public bool    EmailEnabled              { get; set; } = false;
    public string  EmailRecipients           { get; set; } = "";
    public int     EmailBatchIntervalMinutes { get; set; } = 5;

    // Per alert type: enabled + mode ("instant" or "batched")
    public bool    EmailOnEntry              { get; set; } = true;
    public string  EmailOnEntryMode          { get; set; } = "instant";
    public bool    EmailOnExit               { get; set; } = true;
    public string  EmailOnExitMode           { get; set; } = "instant";
    public bool    EmailOnOrbFormed          { get; set; } = false;
    public string  EmailOnOrbFormedMode      { get; set; } = "batched";
    public bool    EmailOnSessionChange      { get; set; } = false;
    public string  EmailOnSessionChangeMode  { get; set; } = "batched";
    public bool    EmailOnDailyLossBreached  { get; set; } = true;
    public string  EmailOnDailyLossBreachedMode { get; set; } = "instant";
    public bool    EmailOnEngineStatus       { get; set; } = true;
    public string  EmailOnEngineStatusMode   { get; set; } = "instant";
    public bool    EmailOnSessionEnd         { get; set; } = false;
```

- [ ] **Step 2: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded. EF Core will auto-add columns to SQLite on next migration/startup.

- [ ] **Step 3: Create EF migration for the new columns**

Run: `dotnet ef migrations add AddEmailAlertColumns --project CRV.Core --startup-project CRV.Web`
Expected: Migration file created successfully

- [ ] **Step 4: Commit**

```bash
git add CRV.Core/Models/StrategyConfig.cs CRV.Core/Migrations/
git commit -m "feat(email): add email alert preference properties to StrategyConfig"
```

---

### Task 3: EmailTemplateBuilder

**Files:**
- Create: `CRV.Web/Services/EmailTemplateBuilder.cs`

- [ ] **Step 1: Create the template builder**

```csharp
// CRV.Web/Services/EmailTemplateBuilder.cs
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
```

- [ ] **Step 2: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add CRV.Web/Services/EmailTemplateBuilder.cs
git commit -m "feat(email): add EmailTemplateBuilder for alert and summary emails"
```

---

### Task 4: EmailNotificationService

**Files:**
- Create: `CRV.Web/Services/EmailNotificationService.cs`

- [ ] **Step 1: Create the email notification service**

```csharp
// CRV.Web/Services/EmailNotificationService.cs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.Extensions.Options;

namespace CRV.Web.Services;

public class EmailNotificationService : IStrategyEventSink, IDisposable
{
    private readonly IOptionsMonitor<SmtpSettings> _smtpOpts;
    private readonly StrategyConfigService         _cfgSvc;
    private readonly DailyStatsService             _statsSvc;
    private readonly ILogger<EmailNotificationService> _log;

    private readonly ConcurrentQueue<AlertEvent> _batchQueue = new();
    private Timer? _batchTimer;
    private bool _previousOrbFormed;
    private bool _previousSessionEnded;
    private string _previousSessionId = "";

    public EmailNotificationService(
        IOptionsMonitor<SmtpSettings> smtpOpts,
        StrategyConfigService cfgSvc,
        DailyStatsService statsSvc,
        ILogger<EmailNotificationService> log)
    {
        _smtpOpts = smtpOpts;
        _cfgSvc   = cfgSvc;
        _statsSvc = statsSvc;
        _log      = log;

        // Start batch timer (checks queue periodically)
        ResetBatchTimer();
        _cfgSvc.ConfigChanged += ResetBatchTimer;
    }

    private void ResetBatchTimer()
    {
        var interval = Math.Max(1, _cfgSvc.Current.EmailBatchIntervalMinutes);
        var ms = interval * 60_000;
        _batchTimer?.Dispose();
        _batchTimer = new Timer(_ => FlushBatchQueue(), null, ms, ms);
    }

    public Task OnEntryAsync(EntrySignal signal)
    {
        var cfg = _cfgSvc.Current;
        if (!cfg.EmailEnabled || !cfg.EmailOnEntry) return Task.CompletedTask;

        var alert = new AlertEvent
        {
            Time       = signal.Time,
            Type       = "Entry",
            Setup      = signal.SetupId,
            SetupLabel = signal.SetupLabel ?? signal.SetupId.ToString(),
            Message    = $"{signal.Direction} {signal.Ticker} @ {signal.Entry} (stop {signal.Stop}, target {signal.Target})",
        };

        EnqueueOrSend(alert, cfg.EmailOnEntryMode);
        return Task.CompletedTask;
    }

    public Task OnExitAsync(TradeRecord trade)
    {
        var cfg = _cfgSvc.Current;
        if (!cfg.EmailEnabled || !cfg.EmailOnExit) return Task.CompletedTask;

        var label = !string.IsNullOrEmpty(trade.SetupLabel) ? trade.SetupLabel : trade.Setup.ToString();
        var alert = new AlertEvent
        {
            Time       = trade.ExitedAt,
            Type       = "Exit",
            Setup      = trade.Setup,
            SetupLabel = label,
            Message    = $"{trade.Direction} {trade.Ticker} closed @ {trade.Exit} — {trade.ExitReason}, Net P&L: {trade.NetPnl:C2}",
        };

        EnqueueOrSend(alert, cfg.EmailOnExitMode);
        return Task.CompletedTask;
    }

    public Task OnSnapshotAsync(EngineSnapshot snap)
    {
        var cfg = _cfgSvc.Current;
        if (!cfg.EmailEnabled) return Task.CompletedTask;

        // ORB Formed — detect rising edge
        if (cfg.EmailOnOrbFormed && snap.OrbFormed && !_previousOrbFormed)
        {
            var alert = new AlertEvent
            {
                Time    = snap.Time,
                Type    = "ORB Formed",
                Message = $"ORB formed: H={snap.OrbHigh} L={snap.OrbLow} Range={snap.OrbRange}",
            };
            EnqueueOrSend(alert, cfg.EmailOnOrbFormedMode);
        }
        _previousOrbFormed = snap.OrbFormed;

        // Session change — detect session ID change
        if (cfg.EmailOnSessionChange && snap.ActiveSessionId != _previousSessionId && !string.IsNullOrEmpty(_previousSessionId))
        {
            var alert = new AlertEvent
            {
                Time    = snap.Time,
                Type    = "Session Change",
                Message = $"Session changed: {_previousSessionId} → {snap.ActiveSessionId}",
            };
            EnqueueOrSend(alert, cfg.EmailOnSessionChangeMode);
        }

        // Session end summary
        if (cfg.EmailOnSessionEnd && snap.SessionEnded && !_previousSessionEnded && !string.IsNullOrEmpty(_previousSessionId))
        {
            var stats = _statsSvc.Get();
            var (subject, body) = EmailTemplateBuilder.BuildSessionEndSummary(
                _previousSessionId, snap.Time.Date, stats);
            SendEmailAsync(subject, body);
        }
        _previousSessionEnded = snap.SessionEnded;
        _previousSessionId = snap.ActiveSessionId;

        // Daily loss breached
        if (cfg.EmailOnDailyLossBreached)
        {
            var stats = _statsSvc.Get();
            if (stats.DDBreached)
            {
                // Only send once per breach (reset when DDBreached goes false on new day)
                var alert = new AlertEvent
                {
                    Time    = snap.Time,
                    Type    = "Daily Loss Breached",
                    Message = $"Daily loss limit breached! Net P&L: {stats.TodayNetPnL:C2}",
                    Color   = "red",
                };
                EnqueueOrSend(alert, cfg.EmailOnDailyLossBreachedMode);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Notify about engine status changes (called from orchestrator, not from IStrategyEventSink).</summary>
    public void NotifyEngineStatus(string status)
    {
        var cfg = _cfgSvc.Current;
        if (!cfg.EmailEnabled || !cfg.EmailOnEngineStatus) return;

        var alert = new AlertEvent
        {
            Time    = DateTime.UtcNow,
            Type    = "Engine",
            Message = $"Engine status: {status}",
        };
        EnqueueOrSend(alert, cfg.EmailOnEngineStatusMode);
    }

    private void EnqueueOrSend(AlertEvent alert, string mode)
    {
        if (mode == "batched")
        {
            _batchQueue.Enqueue(alert);
        }
        else
        {
            var (subject, body) = EmailTemplateBuilder.BuildInstantAlert(alert);
            SendEmailAsync(subject, body);
        }
    }

    private void FlushBatchQueue()
    {
        var alerts = new List<AlertEvent>();
        while (_batchQueue.TryDequeue(out var a))
            alerts.Add(a);

        if (alerts.Count == 0) return;

        var (subject, body) = EmailTemplateBuilder.BuildDigest(alerts);
        SendEmailAsync(subject, body);
    }

    private async void SendEmailAsync(string subject, string htmlBody)
    {
        var smtp = _smtpOpts.CurrentValue;
        if (!smtp.IsConfigured)
        {
            _log.LogWarning("Email alert skipped — SMTP not configured");
            return;
        }

        var cfg = _cfgSvc.Current;
        var recipients = cfg.EmailRecipients?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients == null || recipients.Length == 0)
        {
            _log.LogWarning("Email alert skipped — no recipients configured");
            return;
        }

        try
        {
            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                Credentials = new NetworkCredential(smtp.Username, smtp.Password),
                EnableSsl = smtp.UseSsl,
            };

            var msg = new MailMessage
            {
                From       = new MailAddress(smtp.FromAddress, "CRV.Trading"),
                Subject    = subject,
                Body       = htmlBody,
                IsBodyHtml = true,
            };

            foreach (var r in recipients)
                msg.To.Add(r);

            await client.SendMailAsync(msg);
            _log.LogInformation("Email sent: {Subject}", subject);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send email: {Subject}", subject);
        }
    }

    /// <summary>Send a test email to verify SMTP configuration.</summary>
    public async Task<(bool ok, string message)> SendTestEmailAsync()
    {
        var smtp = _smtpOpts.CurrentValue;
        if (!smtp.IsConfigured)
            return (false, "SMTP not configured. Set Host, Username, Password, and FromAddress.");

        var cfg = _cfgSvc.Current;
        var recipients = cfg.EmailRecipients?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients == null || recipients.Length == 0)
            return (false, "No email recipients configured.");

        try
        {
            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                Credentials = new NetworkCredential(smtp.Username, smtp.Password),
                EnableSsl = smtp.UseSsl,
            };

            var msg = new MailMessage
            {
                From       = new MailAddress(smtp.FromAddress, "CRV.Trading"),
                Subject    = "[CRV] Test Email — Configuration Verified",
                Body       = """
                    <html><body style="font-family:monospace;font-size:14px;color:#e0e0e0;background:#1a1a2e;padding:20px;">
                    <div style="max-width:600px;margin:0 auto;">
                      <h2 style="color:#4caf50;margin:0 0 16px 0;">Email Configuration Verified</h2>
                      <p>Your CRV.Trading email alerts are working correctly.</p>
                      <hr style="border-color:#333;margin:16px 0;" />
                      <small style="color:#666;">Sent by CRV.Trading</small>
                    </div>
                    </body></html>
                    """,
                IsBodyHtml = true,
            };

            foreach (var r in recipients)
                msg.To.Add(r);

            await client.SendMailAsync(msg);
            return (true, $"Test email sent to {string.Join(", ", recipients)}");
        }
        catch (Exception ex)
        {
            return (false, $"SMTP error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _batchTimer?.Dispose();
        FlushBatchQueue(); // Send any remaining batched alerts
    }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add CRV.Web/Services/EmailNotificationService.cs
git commit -m "feat(email): add EmailNotificationService with instant/batched alert support"
```

---

### Task 5: CompositeEventSink + DI wiring

**Files:**
- Create: `CRV.Web/Services/CompositeEventSink.cs`
- Modify: `CRV.Web/Program.cs:67-70`

- [ ] **Step 1: Create CompositeEventSink**

```csharp
// CRV.Web/Services/CompositeEventSink.cs
using CRV.Core.Interfaces;
using CRV.Core.Models;

namespace CRV.Web.Services;

/// <summary>
/// Dispatches strategy events to multiple sinks (SignalR + Email).
/// </summary>
public class CompositeEventSink : IStrategyEventSink
{
    private readonly IStrategyEventSink[] _sinks;

    public CompositeEventSink(IEnumerable<IStrategyEventSink> sinks)
    {
        _sinks = sinks.ToArray();
    }

    public async Task OnEntryAsync(EntrySignal signal)
    {
        foreach (var sink in _sinks)
            await sink.OnEntryAsync(signal);
    }

    public async Task OnExitAsync(TradeRecord completed)
    {
        foreach (var sink in _sinks)
            await sink.OnExitAsync(completed);
    }

    public async Task OnSnapshotAsync(EngineSnapshot snapshot)
    {
        foreach (var sink in _sinks)
            await sink.OnSnapshotAsync(snapshot);
    }
}
```

- [ ] **Step 2: Update DI registration in Program.cs**

Replace the existing sink registration block (lines 67-70):

```csharp
// ── Strategy event sink (SignalR → dashboard) ─────────────────
builder.Services.AddSingleton<SignalREventSink>();
builder.Services.AddSingleton<IStrategyEventSink>(sp =>
    sp.GetRequiredService<SignalREventSink>());
```

With:

```csharp
// ── Strategy event sinks (SignalR + Email) ───────────────────
builder.Services.AddSingleton<SignalREventSink>();
builder.Services.AddSingleton<EmailNotificationService>();
builder.Services.AddSingleton<IStrategyEventSink>(sp =>
    new CompositeEventSink(new IStrategyEventSink[]
    {
        sp.GetRequiredService<SignalREventSink>(),
        sp.GetRequiredService<EmailNotificationService>(),
    }));
```

- [ ] **Step 3: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add CRV.Web/Services/CompositeEventSink.cs CRV.Web/Program.cs
git commit -m "feat(email): wire CompositeEventSink to dispatch events to SignalR + Email"
```

---

### Task 6: Engine status notifications from orchestrator

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

- [ ] **Step 1: Add EmailNotificationService field and constructor injection**

Find the constructor of `LiveEngineOrchestrator`. Add `EmailNotificationService` as a parameter and store it:

```csharp
private readonly EmailNotificationService _emailSvc;
```

Add to constructor parameters:
```csharp
EmailNotificationService emailSvc
```

And in the constructor body:
```csharp
_emailSvc = emailSvc;
```

- [ ] **Step 2: Add engine status notifications**

In the `StartAsync` method, after `Status = "Running"`:
```csharp
_emailSvc.NotifyEngineStatus("Started");
```

In the `StopEngine` method, after `Status = "Stopped"`:
```csharp
_emailSvc.NotifyEngineStatus("Stopped");
```

In the `RunEngineAsync` catch block (if engine crashes), before re-throwing or logging:
```csharp
_emailSvc.NotifyEngineStatus($"Error: {ex.Message}");
```

- [ ] **Step 3: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add CRV.Web/Services/LiveEngineOrchestrator.cs
git commit -m "feat(email): add engine start/stop/error email notifications"
```

---

### Task 7: Test email API endpoint

**Files:**
- Modify: `CRV.Web/Api/EngineController.cs`

- [ ] **Step 1: Add EmailNotificationService to EngineController**

Add to the constructor parameters:
```csharp
EmailNotificationService emailSvc
```

Add field:
```csharp
private readonly EmailNotificationService _emailSvc;
```

In constructor body:
```csharp
_emailSvc = emailSvc;
```

- [ ] **Step 2: Add test email endpoint**

Add after the existing endpoints:

```csharp
    /// <summary>Send a test email to verify SMTP and recipient configuration.</summary>
    [HttpPost("email/test")]
    public async Task<IActionResult> TestEmail()
    {
        var (ok, message) = await _emailSvc.SendTestEmailAsync();
        return ok ? Ok(new { status = "ok", message }) : BadRequest(new { status = "error", message });
    }
```

- [ ] **Step 3: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add CRV.Web/Api/EngineController.cs
git commit -m "feat(email): add POST /api/engine/email/test endpoint"
```

---

### Task 8: Settings UI — Email Alerts section

**Files:**
- Modify: `CRV.Web/Pages/Settings/Live.cshtml`

- [ ] **Step 1: Add Email Alerts settings section**

Add at the end of the `<form>` in Live.cshtml, before the closing `</form>` tag. Use the same card/table pattern as the rest of the page:

```html
<!-- ── Email Alerts ─────────────────────────────────────────── -->
<div class="card bg-dark border-secondary mb-3">
  <div class="card-header d-flex align-items-center">
    <i class="bi bi-envelope me-2 text-warning"></i>
    <strong>Email Alerts</strong>
    <div class="form-check form-switch ms-auto">
      <input class="form-check-input" type="checkbox" id="emailEnabled"
             asp-for="Config.EmailEnabled" onchange="toggleEmailSection(this)" />
      <label class="form-check-label" for="emailEnabled">Enable</label>
    </div>
  </div>
  <div class="card-body" id="emailSettingsBody">

    <!-- SMTP Connection -->
    <h6 class="text-warning mt-2 mb-2"><i class="bi bi-gear me-1"></i>SMTP Connection</h6>
    <small class="text-muted d-block mb-2">
      Username &amp; Password are stored in <code>user-secrets</code>. Set via:
      <code>dotnet user-secrets set "Smtp:Username" "you@email.com"</code>
    </small>
    <div class="row g-2 mb-2">
      <div class="col-md-4">
        <label class="form-label small">Host</label>
        <input type="text" class="form-control form-control-sm bg-dark text-light border-secondary"
               asp-for="Config.EmailRecipients" placeholder="smtp.gmail.com"
               name="Config.EmailRecipients" style="display:none" />
        <input type="text" class="form-control form-control-sm bg-dark text-light border-secondary"
               value="@(ViewData["SmtpHost"] ?? "")" disabled
               placeholder="Set in appsettings.json" />
      </div>
      <div class="col-md-2">
        <label class="form-label small">Port</label>
        <input type="number" class="form-control form-control-sm bg-dark text-light border-secondary"
               value="@(ViewData["SmtpPort"] ?? "587")" disabled
               placeholder="587" />
      </div>
      <div class="col-md-4">
        <label class="form-label small">From Address</label>
        <input type="text" class="form-control form-control-sm bg-dark text-light border-secondary"
               value="@(ViewData["SmtpFrom"] ?? "")" disabled
               placeholder="Set in appsettings.json" />
      </div>
      <div class="col-md-2 d-flex align-items-end">
        <button type="button" class="btn btn-outline-warning btn-sm w-100" onclick="sendTestEmail()">
          <i class="bi bi-send me-1"></i>Test
        </button>
      </div>
    </div>

    <!-- Recipients -->
    <div class="mb-3">
      <label class="form-label small">Recipients (comma-separated)</label>
      <input type="text" class="form-control form-control-sm bg-dark text-light border-secondary"
             asp-for="Config.EmailRecipients" placeholder="you@email.com, alerts@email.com" />
    </div>

    <!-- Batch Interval -->
    <div class="mb-3">
      <label class="form-label small">Batch Interval (minutes)</label>
      <input type="number" class="form-control form-control-sm bg-dark text-light border-secondary"
             asp-for="Config.EmailBatchIntervalMinutes" min="1" max="60" style="max-width:120px" />
    </div>

    <!-- Alert Types Table -->
    <h6 class="text-warning mt-3 mb-2"><i class="bi bi-bell me-1"></i>Alert Types</h6>
    <table class="table table-dark table-sm table-bordered mb-3">
      <thead>
        <tr>
          <th>Alert Type</th>
          <th style="width:80px" class="text-center">Enabled</th>
          <th style="width:140px" class="text-center">Mode</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Entry Signal</td>
          <td class="text-center"><input type="checkbox" class="form-check-input" asp-for="Config.EmailOnEntry" /></td>
          <td class="text-center">
            <select class="form-select form-select-sm bg-dark text-light border-secondary" asp-for="Config.EmailOnEntryMode">
              <option value="instant">Instant</option>
              <option value="batched">Batched</option>
            </select>
          </td>
        </tr>
        <tr>
          <td>Exit Signal</td>
          <td class="text-center"><input type="checkbox" class="form-check-input" asp-for="Config.EmailOnExit" /></td>
          <td class="text-center">
            <select class="form-select form-select-sm bg-dark text-light border-secondary" asp-for="Config.EmailOnExitMode">
              <option value="instant">Instant</option>
              <option value="batched">Batched</option>
            </select>
          </td>
        </tr>
        <tr>
          <td>ORB Formed</td>
          <td class="text-center"><input type="checkbox" class="form-check-input" asp-for="Config.EmailOnOrbFormed" /></td>
          <td class="text-center">
            <select class="form-select form-select-sm bg-dark text-light border-secondary" asp-for="Config.EmailOnOrbFormedMode">
              <option value="instant">Instant</option>
              <option value="batched">Batched</option>
            </select>
          </td>
        </tr>
        <tr>
          <td>Session Start/End</td>
          <td class="text-center"><input type="checkbox" class="form-check-input" asp-for="Config.EmailOnSessionChange" /></td>
          <td class="text-center">
            <select class="form-select form-select-sm bg-dark text-light border-secondary" asp-for="Config.EmailOnSessionChangeMode">
              <option value="instant">Instant</option>
              <option value="batched">Batched</option>
            </select>
          </td>
        </tr>
        <tr>
          <td>Daily Loss Breached</td>
          <td class="text-center"><input type="checkbox" class="form-check-input" asp-for="Config.EmailOnDailyLossBreached" /></td>
          <td class="text-center">
            <select class="form-select form-select-sm bg-dark text-light border-secondary" asp-for="Config.EmailOnDailyLossBreachedMode">
              <option value="instant">Instant</option>
              <option value="batched">Batched</option>
            </select>
          </td>
        </tr>
        <tr>
          <td>Engine Status</td>
          <td class="text-center"><input type="checkbox" class="form-check-input" asp-for="Config.EmailOnEngineStatus" /></td>
          <td class="text-center">
            <select class="form-select form-select-sm bg-dark text-light border-secondary" asp-for="Config.EmailOnEngineStatusMode">
              <option value="instant">Instant</option>
              <option value="batched">Batched</option>
            </select>
          </td>
        </tr>
      </tbody>
    </table>

    <!-- Session End Summary -->
    <div class="form-check form-switch mb-2">
      <input class="form-check-input" type="checkbox" asp-for="Config.EmailOnSessionEnd" />
      <label class="form-check-label" asp-for="Config.EmailOnSessionEnd">
        Send session-end summary email (P&amp;L, win rate, per-setup breakdown)
      </label>
    </div>

    <!-- Test result toast -->
    <div id="emailTestResult" class="mt-2" style="display:none"></div>
  </div>
</div>

<script>
function toggleEmailSection(cb) {
    document.getElementById('emailSettingsBody').style.opacity = cb.checked ? '1' : '0.4';
    document.getElementById('emailSettingsBody').style.pointerEvents = cb.checked ? 'auto' : 'none';
}
// Init on load
document.addEventListener('DOMContentLoaded', function() {
    var cb = document.getElementById('emailEnabled');
    if (cb) toggleEmailSection(cb);
});

async function sendTestEmail() {
    const el = document.getElementById('emailTestResult');
    el.style.display = 'block';
    el.innerHTML = '<span class="text-muted"><i class="bi bi-hourglass-split me-1"></i>Sending test email...</span>';
    try {
        const res = await fetch('/api/engine/email/test', { method: 'POST' });
        const data = await res.json();
        if (res.ok) {
            el.innerHTML = `<span class="text-success"><i class="bi bi-check-circle me-1"></i>${data.message}</span>`;
        } else {
            el.innerHTML = `<span class="text-danger"><i class="bi bi-x-circle me-1"></i>${data.message}</span>`;
        }
    } catch (err) {
        el.innerHTML = `<span class="text-danger"><i class="bi bi-x-circle me-1"></i>Request failed: ${err.message}</span>`;
    }
}
</script>
```

- [ ] **Step 2: Pass SMTP display values from page model**

In `Live.cshtml.cs`, update `OnGet()` to pass SMTP info for display:

Add to constructor parameters:
```csharp
IConfiguration configuration
```

Add field:
```csharp
private readonly IConfiguration _config;
```

In constructor body:
```csharp
_config = configuration;
```

In `OnGet()`, after `Sessions = ...`:
```csharp
ViewData["SmtpHost"] = _config["Smtp:Host"] ?? "";
ViewData["SmtpPort"] = _config["Smtp:Port"] ?? "587";
ViewData["SmtpFrom"] = _config["Smtp:FromAddress"] ?? "";
```

- [ ] **Step 3: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add CRV.Web/Pages/Settings/Live.cshtml CRV.Web/Pages/Settings/Live.cshtml.cs
git commit -m "feat(email): add Email Alerts settings section to Live settings page"
```

---

### Task 9: Daily loss breach deduplication

**Files:**
- Modify: `CRV.Web/Services/EmailNotificationService.cs`

The current implementation would send a daily-loss-breach email on every snapshot while `DDBreached` is true. Add a flag to send only once per breach.

- [ ] **Step 1: Add deduplication flag**

Add field to `EmailNotificationService`:
```csharp
private bool _dailyLossBreachSent;
```

- [ ] **Step 2: Guard the daily loss breach logic**

In `OnSnapshotAsync`, wrap the daily loss breach block:

```csharp
if (cfg.EmailOnDailyLossBreached)
{
    var stats = _statsSvc.Get();
    if (stats.DDBreached && !_dailyLossBreachSent)
    {
        _dailyLossBreachSent = true;
        var alert = new AlertEvent
        {
            Time    = snap.Time,
            Type    = "Daily Loss Breached",
            Message = $"Daily loss limit breached! Net P&L: {stats.TodayNetPnL:C2}",
            Color   = "red",
        };
        EnqueueOrSend(alert, cfg.EmailOnDailyLossBreachedMode);
    }
    else if (!stats.DDBreached)
    {
        _dailyLossBreachSent = false; // Reset on new day
    }
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add CRV.Web/Services/EmailNotificationService.cs
git commit -m "fix(email): deduplicate daily loss breach email to fire once per breach"
```

---

### Task 10: Final build verification and integration test

**Files:** None (verification only)

- [ ] **Step 1: Full build**

Run: `dotnet build CRV.Web`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Run existing tests**

Run: `dotnet test CRV.Core.Tests`
Expected: All existing tests pass (email changes don't affect core engine tests)

- [ ] **Step 3: Commit**

```bash
git commit --allow-empty -m "chore(email): verify full build and existing tests pass"
```
