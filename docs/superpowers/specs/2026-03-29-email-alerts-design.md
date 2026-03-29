# Email Alerts Design

## Overview

Add email notification capabilities to CRV.Trading, allowing users to receive trading alerts and session summaries via email. Uses SMTP configuration with credentials stored in User Secrets.

## Features

### 1. SMTP Configuration

**appsettings.json** (non-sensitive):
```json
"Smtp": {
  "Host": "smtp.gmail.com",
  "Port": 587,
  "FromAddress": "you@gmail.com",
  "UseSsl": true
}
```

**User Secrets** (sensitive):
```
Smtp:Username = you@gmail.com
Smtp:Password = app-password-here
```

Bound to `SmtpSettings` class via `IOptions<SmtpSettings>`.

### 2. Email Preferences in StrategyConfig

Added to `StrategyConfig` model and persisted to SQLite:

- `EmailEnabled` (bool) — master toggle
- `EmailRecipients` (string) — comma-separated email addresses
- `EmailBatchIntervalMinutes` (int) — digest window, default 5

Per alert type toggles and mode (instant or batched):

| Property (Enabled) | Property (Mode) | Alert Type |
|---|---|---|
| `EmailOnEntry` | `EmailOnEntryMode` | Entry signals |
| `EmailOnExit` | `EmailOnExitMode` | Exit signals |
| `EmailOnOrbFormed` | `EmailOnOrbFormedMode` | ORB window complete |
| `EmailOnSessionChange` | `EmailOnSessionChangeMode` | Session start/end |
| `EmailOnDailyLossBreached` | `EmailOnDailyLossBreachedMode` | Daily loss limit hit |
| `EmailOnEngineStatus` | `EmailOnEngineStatusMode` | Engine started/stopped/error |

Mode values: `"instant"` or `"batched"`.

- `EmailOnSessionEnd` (bool) — toggle for session-end summary email

### 3. EmailNotificationService

Singleton implementing `IStrategyEventSink`:

- **On each event**: checks `StrategyConfig` for whether that alert type is enabled
  - **Instant mode**: sends email immediately via `SmtpClient`
  - **Batched mode**: enqueues to `ConcurrentQueue<AlertEvent>`
- **Batch timer**: `System.Threading.Timer` fires every `EmailBatchIntervalMinutes`. If queue has items, composes a single digest email with all queued alerts, sends it, clears queue.
- **Session end**: on session-end event, pulls stats from `DailyStatsService`, composes summary email.
- **Error handling**: failed sends logged via `ILogger`, no retries (best-effort). If SMTP credentials missing/invalid, logs warning on startup and disables itself.
- **Config changes**: listens to `StrategyConfigService.ConfigChanged` to pick up preference changes without restart.

### 4. Email Templates

Simple HTML emails using C# string interpolation (no template engine).

**Instant alert email:**
- Subject: `[CRV] {AlertType}: {short message}`
  - Example: `[CRV] Entry: Long MNQ @ 21450.25`
- Body: Alert time, type, setup label, full message, color-coded badge

**Batched digest email:**
- Subject: `[CRV] Alert Digest — {count} alerts ({timeRange})`
- Body: Alerts grouped by type, each showing time and message, ordered chronologically

**Session end summary email:**
- Subject: `[CRV] Session End: {SessionId} — {date}`
- Body:
  - Overall stats: P&L, net P&L, trade count, win rate, max drawdown
  - Per-setup table: setup name, wins, losses, win P&L, loss P&L, expectancy
  - Avg win, avg loss
- Footer: "Sent by CRV.Trading"

### 5. Settings UI

New "Email Alerts" section in `Live.cshtml`, containing:

- **SMTP Connection** — Host, Port, FromAddress, SSL toggle, plus "Test Email" button
- **Recipients** — text input for comma-separated addresses
- **Master Toggle** — `EmailEnabled` on/off, disables everything below when off
- **Alert Types Table**:

| Alert Type | Enabled | Mode |
|---|---|---|
| Entry Signal | toggle | instant / batched |
| Exit Signal | toggle | instant / batched |
| ORB Formed | toggle | instant / batched |
| Session Start/End | toggle | instant / batched |
| Daily Loss Breached | toggle | instant / batched |
| Engine Status | toggle | instant / batched |

- **Batch Interval** — number input (minutes), visible when at least one type is batched
- **Session End Summary** — separate toggle

All saved via existing form POST flow to `StrategyConfig` in SQLite.

### 6. Wiring & Registration

**Program.cs:**
- `builder.Services.Configure<SmtpSettings>(config.GetSection("Smtp"))`
- Register `EmailNotificationService` as singleton
- Register as second `IStrategyEventSink` via `IEnumerable<IStrategyEventSink>`

**Multi-sink dispatch:**
- Change orchestrator to inject `IEnumerable<IStrategyEventSink>` instead of `SignalREventSink` directly
- Dispatch events to all sinks (SignalR + Email)

**Test email endpoint:**
- `POST /api/email/test` — validates SMTP config, sends test email, returns success/failure

## New Files

| File | Purpose |
|---|---|
| `CRV.Core/Models/SmtpSettings.cs` | SMTP configuration class |
| `CRV.Web/Services/EmailNotificationService.cs` | Email event sink service |
| `CRV.Web/Services/EmailTemplateBuilder.cs` | HTML email composition helpers |

## Modified Files

| File | Changes |
|---|---|
| `CRV.Core/Models/StrategyConfig.cs` | Add email preference properties |
| `CRV.Web/Program.cs` | DI registration for SmtpSettings and EmailNotificationService |
| `CRV.Web/Pages/Settings/Live.cshtml` | Email alerts settings UI section |
| `CRV.Web/Pages/Settings/Live.cshtml.cs` | Bind email settings in page model |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Dispatch events to multiple IStrategyEventSink instances |
| `appsettings.json` | Add Smtp section (non-sensitive parts only) |
