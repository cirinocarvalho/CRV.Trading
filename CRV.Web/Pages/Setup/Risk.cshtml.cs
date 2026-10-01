using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CRV.Web.Pages.Setup;

/// <summary>
/// Daily limits, sessions, instrument and bar size, and the market filters.
/// Saving writes only the fields this page shows (plus the sessions); the rest keep their values.
/// </summary>
public class RiskModel : PageModel
{
    private readonly StrategyConfigService  _cfgSvc;
    private readonly LiveEngineOrchestrator _engine;
    private readonly ILogger<RiskModel>     _log;

    public RiskModel(StrategyConfigService cfgSvc, LiveEngineOrchestrator engine, ILogger<RiskModel> log)
    {
        _cfgSvc = cfgSvc; _engine = engine; _log = log;
    }

    public StrategyConfig Config { get; private set; } = new();
    public List<SessionConfig> Sessions { get; private set; } = new();
    [BindProperty] public string? SessionsJson { get; set; }

    public bool EngineRunning => _engine.IsRunning;
    public string? Message { get; private set; }
    public string? Error   { get; private set; }
    public List<string> Warnings { get; } = new();

    /// <summary>True when opening-range strategies exist, so the engine ignores setups A–D.</summary>
    public bool BasketInUse
    {
        get
        {
            try { return BasketCodec.Parse(Config.BasketJson).Count > 0; }
            catch { return false; }
        }
    }

    public void OnGet()
    {
        Load();
        Message = TempData["risk_msg"] as string;
        Error   = TempData["risk_err"] as string;
        if (TempData["risk_warnings"] is string w) Warnings.AddRange(w.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Config = _cfgSvc.Current.Clone();
        var ok = await TryUpdateModelAsync(Config, "Config",
            // Daily limits
            c => c.UseDailyLossLimit, c => c.MaxDailyLoss, c => c.DailyLossMode, c => c.MaxPortfolioRisk,
            // Instrument and bars (commission is posted only when the instrument changes)
            c => c.Ticker, c => c.PointValue, c => c.TickSize, c => c.CommissionPerSide,
            c => c.ExecutionTFMinutes, c => c.Timezone,
            // Filters
            c => c.AllowBothSameBar, c => c.UseChopFilter, c => c.ChopBlockMode, c => c.ChopMinVotes,
            c => c.ChopUseRangeCompression, c => c.ChopCompressionRatio, c => c.ChopUseFlatVwap, c => c.ChopFlatSlopeThresholdPct,
            c => c.ChopUseWeakDrive, c => c.ChopMinDriveRatio, c => c.ChopUseLowVolume, c => c.ChopMinVolumeRatio,
            c => c.FBMaxTimeOutsideMinutesOrb, c => c.FBMaxTimeOutsideMinutesSR, c => c.FBMaxPenetrationPctOrb,
            c => c.FBMaxPenetrationPctSR, c => c.FBMinRejectionBodyPct, c => c.FBMaxTrendDayScore);
        if (!ok || !ModelState.IsValid)
        {
            TempData["risk_err"] = "Not saved: " + string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToPage();
        }

        Config = SessionSettings.Apply(Config, SessionsJson, _log);
        _log.LogInformation("Sessions & risk saved by {Who}: daily loss {On}/{Max} {Mode}, portfolio cap {Cap}, ticker {T}",
            Who, Config.UseDailyLossLimit, Config.MaxDailyLoss, Config.DailyLossMode, Config.MaxPortfolioRisk, Config.Ticker);
        _cfgSvc.Update(Config);
        // Hot-reload into the running engine (no-op if stopped; the next start reads the saved config).
        _engine.ApplyRuntimeSettings(Config);

        TempData["risk_msg"] = _engine.IsRunning
            ? "Saved. The daily loss limit, portfolio cap and chop filter apply to the running engine now; the rest from the next session or start."
            : "Saved.";
        var problems = Config.Validate();
        if (problems.Count > 0) TempData["risk_warnings"] = string.Join("\n", problems);
        return RedirectToPage();
    }

    private void Load()
    {
        Config = _cfgSvc.Current.Clone();
        Sessions = Config.Sessions ?? SessionConfig.CreateDefaults(Config);
    }

    private string Who => User?.Identity?.Name ?? Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault() ?? "unknown";
}
