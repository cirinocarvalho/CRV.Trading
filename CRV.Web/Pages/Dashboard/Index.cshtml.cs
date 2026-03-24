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

    /// <summary>Force-exit the active trade for a setup (called via fetch, returns JSON).</summary>
    public async Task<IActionResult> OnPostForceExit(string setupId)
    {
        if (string.IsNullOrEmpty(setupId))
            return new JsonResult(new { ok = false, error = "setupId required" });
        await _orchestrator.ForceExitSetup(setupId);
        return new JsonResult(new { ok = true, setup = setupId });
    }

    // Legacy handlers for backward compatibility with hardcoded dashboard buttons
    public Task<IActionResult> OnPostForceExitA() => OnPostForceExit("A");
    public Task<IActionResult> OnPostForceExitB() => OnPostForceExit("B");
    public Task<IActionResult> OnPostForceExitC() => OnPostForceExit("C");
    public Task<IActionResult> OnPostForceExitD() => OnPostForceExit("D");

}
