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

    /// <summary>Force-exit the active Setup A trade immediately (called via fetch, returns JSON).</summary>
    public async Task<IActionResult> OnPostForceExitA()
    {
        await _orchestrator.ForceExitSetup(SetupId.A);
        return new JsonResult(new { ok = true, setup = "A" });
    }

    /// <summary>Force-exit the active Setup B trade immediately (called via fetch, returns JSON).</summary>
    public async Task<IActionResult> OnPostForceExitB()
    {
        await _orchestrator.ForceExitSetup(SetupId.B);
        return new JsonResult(new { ok = true, setup = "B" });
    }

    /// <summary>Force-exit the active Setup C trade immediately (called via fetch, returns JSON).</summary>
    public async Task<IActionResult> OnPostForceExitC()
    {
        await _orchestrator.ForceExitSetup(SetupId.C);
        return new JsonResult(new { ok = true, setup = "C" });
    }

    /// <summary>Force-exit the active Setup D trade immediately (called via fetch, returns JSON).</summary>
    public async Task<IActionResult> OnPostForceExitD()
    {
        await _orchestrator.ForceExitSetup(SetupId.D);
        return new JsonResult(new { ok = true, setup = "D" });
    }

}
