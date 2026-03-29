// CRV.Web/Services/EmailNotificationService.cs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using CRV.Core.Interfaces;
using CRV.Core.Models;
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
    private bool _dailyLossBreachSent;

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

        ResetBatchTimer();
        _cfgSvc.ConfigChanged += _ => ResetBatchTimer();
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
            Setup      = signal.Setup,
            SetupLabel = signal.SetupLabel ?? signal.Setup.ToString(),
            Message    = $"{signal.Direction} {signal.Ticker} @ {signal.Entry} (stop {signal.Stop}, target {signal.Tg2Price})",
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

        // Session change — detect transition between non-empty session IDs
        // (empty → X = engine start, X → empty = session end, not a "change")
        if (cfg.EmailOnSessionChange
            && snap.ActiveSessionId != _previousSessionId
            && !string.IsNullOrEmpty(_previousSessionId)
            && !string.IsNullOrEmpty(snap.ActiveSessionId))
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

        // Daily loss breached — send once per breach
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
                _dailyLossBreachSent = false;
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
        FlushBatchQueue();
    }
}
