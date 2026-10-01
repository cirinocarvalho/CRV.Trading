namespace CRV.Web.Pages.Settings;

using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public class LiveModel : PageModel
{
    private readonly StrategyConfigService   _cfgSvc;
    private readonly LiveEngineOrchestrator  _orchestrator;
    private readonly SchwabAuthService       _schwab;
    private readonly TradeStationAuthService _ts;
    private readonly TradovateAuthService    _tv;
    private readonly ILogger<LiveModel>      _log;
    private readonly IConfiguration          _config;

    // Not bound directly: OnPostAsync merges the posted fields onto the saved settings.
    public StrategyConfig Config { get; set; } = new();
    /// <summary>Problems StrategyConfig.Validate found in the saved settings (shown, not blocking).</summary>
    public List<string> ValidationWarnings { get; } = new();
    public List<SessionConfig> Sessions { get; set; } = new();
    [BindProperty] public string? SessionsJson { get; set; } = "[]";
    // Consuming read — badge shows once after save, disappears on refresh
    public bool   Saved           => TempData["live_saved"] is not null;
    public bool   SchwabConnected => _schwab.IsAuthenticated;
    public bool   TsConnected     => _ts.IsAuthenticated;
    public bool   TvConnected     => _tv.IsAuthenticated;

    public DateTime PreviousTradingDay
    {
        get
        {
            var d = DateTime.Today.AddDays(-1);
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                d = d.AddDays(-1);
            return d;
        }
    }

    public LiveModel(StrategyConfigService cfgSvc, LiveEngineOrchestrator orchestrator,
                     SchwabAuthService schwab, TradeStationAuthService ts,
                     TradovateAuthService tv, ILogger<LiveModel> log,
                     IConfiguration configuration)
    {
        _cfgSvc       = cfgSvc;
        _orchestrator = orchestrator;
        _schwab       = schwab;
        _ts           = ts;
        _tv           = tv;
        _log          = log;
        _config       = configuration;
    }

    public bool IsNearRoll
    {
        get
        {
            try { return ContractRollCalendar.IsNearRoll(_cfgSvc.Current.Ticker); }
            catch { return false; }
        }
    }

    public string ActiveContract
    {
        get
        {
            try
            {
                var root = FuturesSymbol.RootSymbol(_cfgSvc.Current.Ticker);
                return ContractRollCalendar.ActiveContract(root);
            }
            catch { return ""; }
        }
    }

    public void OnGet()
    {
        if (TempData["live_warnings"] is string w)
            ValidationWarnings.AddRange(w.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Config = _cfgSvc.Current.Clone();
        Sessions = Config.Sessions ?? SessionConfig.CreateDefaults(Config);
        ViewData["SmtpHost"] = _config["Smtp:Host"] ?? "";
        ViewData["SmtpPort"] = _config["Smtp:Port"] ?? "587";
        ViewData["SmtpFrom"] = _config["Smtp:FromAddress"] ?? "";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Start from the saved settings and overwrite only the fields this form posted.
        // Binding onto a fresh object reset every setting the page doesn't render
        // (ExecAccountId, SessionStartHour, the sweep/drive module settings, ...) on each save.
        Config = _cfgSvc.Current.Clone();
        if (!await TryUpdateModelAsync(Config, "Config") || !ModelState.IsValid)
        {
            foreach (var (key, entry) in ModelState)
                foreach (var err in entry.Errors)
                    _log.LogWarning("Live Settings ModelState error — {Key}: {Msg}", key, err.ErrorMessage);

            // Reload from service so the form reflects persisted values, not half-bound POSTed values
            Config = _cfgSvc.Current.Clone();
            Sessions = Config.Sessions ?? SessionConfig.CreateDefaults(Config);
            return Page();
        }

        // Preserve BasketJson before the flat override from session sync
        var basketJson = Config.BasketJson;

        // Normalize empty ExecBroker to null
        if (string.IsNullOrWhiteSpace(Config.ExecBroker)) Config.ExecBroker = null;

        _log.LogInformation(
            "Saving config: Broker={Broker} Ticker={Ticker} Contracts={Cts} " +
            "ModeA={ModeA} PullbackPct={PbPct} MaxTradesA={MaxA} ModeB={ModeB} MaxTradesB={MaxB}",
            Config.Broker, Config.Ticker, Config.Contracts,
            Config.ModeA, Config.PullbackPct, Config.MaxTradesA,
            Config.ModeB, Config.MaxTradesB);

        try
        {
            if (!string.IsNullOrWhiteSpace(SessionsJson))
            {
                var jsonOpts = new System.Text.Json.JsonSerializerOptions
                {
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };
                var sessions = System.Text.Json.JsonSerializer.Deserialize<List<SessionConfig>>(SessionsJson, jsonOpts);
                if (sessions != null)
                {
                    // All setup config is now edited inline in the session tabs.
                    // Sync NY session → flat Config for backward compat (backtest, etc.)
                    var ny = sessions.FirstOrDefault(s => s.SessionId == CRV.Core.Models.SessionId.NY);
                    if (ny != null)
                    {
                        var flat = ny.ToLegacyConfig(Config);
                        // ToLegacyConfig clones Config and overwrites per-setup fields,
                        // preserving global fields (Broker, Ticker, PointValue, etc.)
                        Config = flat;
                        Config.BasketJson = basketJson;
                    }

                    Config.Sessions = sessions;
                    foreach (var sess in sessions)
                        _log.LogInformation("Session {Id}: Enabled={E} OrbStart={OS} OrbEnd={OE}",
                            sess.SessionId, sess.Enabled, sess.OrbStart, sess.OrbEnd);
                    // Sync flat config timing from the NY session (or first enabled) for backward compat
                    var primary = (ny?.Enabled == true ? ny : null)
                               ?? sessions.FirstOrDefault(s => s.Enabled);
                    if (primary != null)
                    {
                        Config.OrbStart = primary.OrbStart;
                        Config.OrbEnd   = primary.OrbEnd;
                        Config.RthStart = primary.RthStart;
                        Config.RthEnd   = primary.RthEnd;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to deserialize SessionsJson: {Json}", SessionsJson);
        }

        _cfgSvc.Update(Config);
        // Hot-reload into the running engine so toggles take effect immediately
        // (no-op if the engine is stopped — next start picks up the fresh config).
        _orchestrator.ApplyRuntimeSettings(Config);
        TempData["live_saved"] = "1";
        // Validate() existed but never ran. Report problems after saving rather than refusing
        // the save, so a config that already fails a check can still be changed quickly.
        var problems = Config.Validate();
        if (problems.Count > 0) TempData["live_warnings"] = string.Join("\n", problems);
        return RedirectToPage();
    }
}
