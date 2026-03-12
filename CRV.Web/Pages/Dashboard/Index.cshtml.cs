namespace CRV.Web.Pages.Dashboard;

using System.Text.RegularExpressions;
using CRV.Core.Models;
using CRV.Live;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class IndexModel : PageModel
{
    private readonly LiveEngineOrchestrator _orchestrator;

    public StrategyConfig Config { get; private set; } = new();

    public IndexModel(StrategyConfigService cfgSvc, LiveEngineOrchestrator orchestrator)
    {
        Config        = cfgSvc.Current;
        _orchestrator = orchestrator;
    }

    public bool IsNearRoll
    {
        get
        {
            try { return ContractRollCalendar.IsNearRoll(Config.Ticker); }
            catch { return false; }
        }
    }

    public string ActiveContract
    {
        get
        {
            try
            {
                var root = Regex.Replace(FuturesSymbol.Normalize(Config.Ticker), @"[HMUZ]\d{2}$", "");
                return ContractRollCalendar.ActiveContract(root);
            }
            catch { return ""; }
        }
    }

    public void OnGet() { }

    /// <summary>Force-exit the active Setup A trade on the next bar (called via fetch, returns JSON).</summary>
    public IActionResult OnPostForceExitA()
    {
        _orchestrator.ForceExitSetupA();
        return new JsonResult(new { ok = true, setup = "A" });
    }

    /// <summary>Force-exit the active Setup B trade on the next bar (called via fetch, returns JSON).</summary>
    public IActionResult OnPostForceExitB()
    {
        _orchestrator.ForceExitSetupB();
        return new JsonResult(new { ok = true, setup = "B" });
    }
}
