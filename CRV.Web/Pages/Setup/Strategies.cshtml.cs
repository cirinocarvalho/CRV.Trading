using CRV.Core.Data;
using CRV.Core.Strategy;
using CRV.Live;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CRV.Web.Pages.Setup;

public class StrategiesModel : PageModel
{
    private readonly StrategyBasketService _basket;
    private readonly StrategyConfigService _cfgSvc;
    private readonly LiveEngineOrchestrator _engine;
    private readonly TradingDbContext _db;

    public StrategiesModel(StrategyBasketService basket, StrategyConfigService cfgSvc, LiveEngineOrchestrator engine, TradingDbContext db)
    {
        _basket = basket; _cfgSvc = cfgSvc; _engine = engine; _db = db;
    }

    public List<BasketItem> Items { get; private set; } = new();
    public Dictionary<string, (int Trades, decimal Net)> Last30 { get; private set; } = new();
    public string ResultSource { get; private set; } = "live";
    public bool EngineRunning => _engine.IsRunning;
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public bool LegacyInUse { get; private set; }

    public async Task OnGetAsync()
    {
        Message = TempData["strategies_msg"] as string;
        Error = TempData["strategies_err"] as string;
        try { Items = _basket.All(); }
        catch { Error ??= "The saved strategy list couldn't be read. Nothing has been changed; check the app log."; }
        LegacyInUse = !Items.Any(i => !i.IsEma21);

        // Results by setup for the last 30 days, from the account orders go to.
        ResultSource = _cfgSvc.Current.EffectiveExecBroker == "Mock" ? "mock" : "live";
        var since = DateTime.UtcNow.AddDays(-30);
        Last30 = (await _db.Trades.Where(t => t.Source == ResultSource && t.EnteredAt >= since)
                .Select(t => new { t.SetupLabel, t.NetPnl }).ToListAsync())
            .GroupBy(t => t.SetupLabel ?? "")
            .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(x => x.NetPnl)));
    }

    public IActionResult OnPostToggle(string id, bool enabled)
    {
        var change = _basket.SetEnabled(id, enabled, Who);
        if (!change.Ok) TempData["strategies_err"] = change.Error;
        else TempData["strategies_msg"] = enabled
            ? (_engine.IsRunning ? $"{id} is on. The running engine starts trading it the next time it starts." : $"{id} is on.")
            : (_engine.IsRunning ? $"{id} is off. It takes no new trades from now; an open trade keeps its stop and target." : $"{id} is off.");
        return RedirectToPage();
    }

    public IActionResult OnPostAdd(StrategyType type, string instrument)
    {
        var root = (instrument ?? "").Trim().ToUpperInvariant();
        if (root == "") { TempData["strategies_err"] = "Choose an instrument."; return RedirectToPage(); }
        var ticker = ContractRollCalendar.ActiveContract(root);
        var pv = FuturesSymbol.PointValue(ticker);
        var tick = Request.Form.TryGetValue("tick", out var tk) && decimal.TryParse(tk, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 0.25m;
        var (change, id) = _basket.Add(type, ticker, pv > 0 ? pv : 1m, tick, Who);
        if (!change.Ok) { TempData["strategies_err"] = change.Error; return RedirectToPage(); }
        TempData["strategy_saved"] = "Added, switched off. Check the settings, then turn it on.";
        return RedirectToPage("/Setup/Strategy", new { id });
    }

    private string Who => User?.Identity?.Name ?? Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault() ?? "unknown";
}
