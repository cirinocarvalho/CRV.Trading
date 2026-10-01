using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CRV.Web.Pages.Setup;

/// <summary>Email alerts. Saving writes only the email fields; everything else keeps its value.</summary>
public class AlertsModel : PageModel
{
    /// <summary>Each alert type: its on/off field, its instant-or-batched field, and plain words.</summary>
    public static readonly (string On, string Mode, string Label, string? Help)[] Types =
    {
        ("EmailOnEntry",             "EmailOnEntryMode",             "A trade opens",                 "Entry price, stop and target."),
        ("EmailOnExit",              "EmailOnExitMode",              "A trade closes",                "Exit price and result."),
        ("EmailOnOrbFormed",         "EmailOnOrbFormedMode",         "The opening range is set",      "High, low and size of the range."),
        ("EmailOnSessionChange",     "EmailOnSessionChangeMode",     "A session starts or ends",      null),
        ("EmailOnDailyLossBreached", "EmailOnDailyLossBreachedMode", "The daily loss limit is hit",   "Trading stops for the day."),
        ("EmailOnEngineStatus",      "EmailOnEngineStatusMode",      "The engine starts or stops",    null),
    };

    private readonly StrategyConfigService  _cfgSvc;
    private readonly LiveEngineOrchestrator _engine;
    private readonly IConfiguration         _config;
    private readonly ILogger<AlertsModel>   _log;

    public AlertsModel(StrategyConfigService cfgSvc, LiveEngineOrchestrator engine, IConfiguration config, ILogger<AlertsModel> log)
    {
        _cfgSvc = cfgSvc; _engine = engine; _config = config; _log = log;
    }

    public StrategyConfig Config { get; private set; } = new();
    public string SmtpHost => _config["Smtp:Host"] ?? "";
    public string SmtpPort => _config["Smtp:Port"] ?? "587";
    public string SmtpFrom => _config["Smtp:FromAddress"] ?? "";
    public string? Message { get; private set; }
    public string? Error   { get; private set; }

    public bool On(string field)     => (bool)typeof(StrategyConfig).GetProperty(field)!.GetValue(Config)!;
    public string Mode(string field) => (string)typeof(StrategyConfig).GetProperty(field)!.GetValue(Config)!;

    public void OnGet()
    {
        Config = _cfgSvc.Current.Clone();
        Message = TempData["alerts_msg"] as string;
        Error   = TempData["alerts_err"] as string;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Config = _cfgSvc.Current.Clone();
        var ok = await TryUpdateModelAsync(Config, "Config",
            c => c.EmailEnabled, c => c.EmailRecipients, c => c.EmailBatchIntervalMinutes, c => c.EmailOnSessionEnd,
            c => c.EmailOnEntry, c => c.EmailOnEntryMode, c => c.EmailOnExit, c => c.EmailOnExitMode,
            c => c.EmailOnOrbFormed, c => c.EmailOnOrbFormedMode, c => c.EmailOnSessionChange, c => c.EmailOnSessionChangeMode,
            c => c.EmailOnDailyLossBreached, c => c.EmailOnDailyLossBreachedMode, c => c.EmailOnEngineStatus, c => c.EmailOnEngineStatusMode);
        if (!ok || !ModelState.IsValid)
        {
            TempData["alerts_err"] = "Not saved: " + string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToPage();
        }
        foreach (var (_, mode, _, _) in Types)
        {
            var p = typeof(StrategyConfig).GetProperty(mode)!;
            if ((string?)p.GetValue(Config) is not ("instant" or "batched")) p.SetValue(Config, "instant");
        }
        Config.EmailBatchIntervalMinutes = Math.Clamp(Config.EmailBatchIntervalMinutes, 1, 60);
        Config.EmailRecipients = (Config.EmailRecipients ?? "").Trim();

        _log.LogInformation("Alerts saved by {Who}: email {On}, recipients {N}", Who, Config.EmailEnabled,
            Config.EmailRecipients.Split(',', StringSplitOptions.RemoveEmptyEntries).Length);
        _cfgSvc.Update(Config);
        _engine.ApplyRuntimeSettings(Config);
        TempData["alerts_msg"] = "Saved.";
        return RedirectToPage();
    }

    private string Who => User?.Identity?.Name ?? Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault() ?? "unknown";
}
