namespace CRV.Web.Pages.Settings;

using System.Text.RegularExpressions;
using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

public class LiveModel : PageModel
{
    private readonly StrategyConfigService   _cfgSvc;
    private readonly LiveEngineOrchestrator  _orchestrator;
    private readonly SchwabAuthService       _schwab;
    private readonly TradeStationAuthService _ts;
    private readonly TradovateAuthService    _tv;
    private readonly ILogger<LiveModel>      _log;

    [BindProperty] public StrategyConfig Config { get; set; } = new();
    public List<SessionConfig> Sessions { get; set; } = new();
    [BindProperty] public string SessionsJson { get; set; } = "[]";
    // Consuming read — badge shows once after save, disappears on refresh
    public bool   Saved           => TempData["live_saved"] is not null;
    public bool   IsRunning       => _orchestrator.IsRunning;
    public string EngineStatus    => _orchestrator.Status;
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
                     TradovateAuthService tv, ILogger<LiveModel> log)
    {
        _cfgSvc       = cfgSvc;
        _orchestrator = orchestrator;
        _schwab       = schwab;
        _ts           = ts;
        _tv           = tv;
        _log          = log;
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
                var root = Regex.Replace(FuturesSymbol.Normalize(_cfgSvc.Current.Ticker), @"[HMUZ]\d{2}$", "");
                return ContractRollCalendar.ActiveContract(root);
            }
            catch { return ""; }
        }
    }

    public void OnGet()
    {
        Config = _cfgSvc.Current.Clone();
        Sessions = Config.Sessions ?? SessionConfig.CreateDefaults(Config);
    }

    public IActionResult OnPost()
    {
        if (!ModelState.IsValid)
        {
            foreach (var (key, entry) in ModelState)
                foreach (var err in entry.Errors)
                    _log.LogWarning("Live Settings ModelState error — {Key}: {Msg}", key, err.ErrorMessage);

            // Reload from service so the form reflects persisted values, not half-bound POSTed values
            Config = _cfgSvc.Current.Clone();
            return Page();
        }

        // PullbackPct is not shown in the UI — derive it from ModeA so it stays consistent
        Config.PullbackPct = Config.IsAggressiveA ? 0.25m : 0.50m;

        // Preserve AccountId from the current in-memory config (not editable in the form)
        Config.AccountId = _cfgSvc.Current.AccountId;

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
            var sessions = System.Text.Json.JsonSerializer.Deserialize<List<SessionConfig>>(SessionsJson);
            if (sessions != null) Config.Sessions = sessions;
        }
        catch { /* ignore invalid JSON on save */ }

        _cfgSvc.Update(Config);
        TempData["live_saved"] = "1";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStartAsync()
    {
        await _orchestrator.StartAsync(_cfgSvc.Current);
        return RedirectToPage();
    }

    public IActionResult OnPostStop()
    {
        _orchestrator.StopEngine();
        return RedirectToPage();
    }
}
