using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CRV.Web.Pages.Performance;

public class PerformanceModel : PageModel
{
    private readonly TradingDbContext _db;
    private readonly StrategyConfigService _cfgSvc;

    public PerformanceModel(TradingDbContext db, StrategyConfigService cfgSvc)
    {
        _db = db;
        _cfgSvc = cfgSvc;
    }

    // Bound from query string as ET display strings (datetime-local format)
    [BindProperty(SupportsGet = true)] public string FromStr  { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string ToStr    { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Session  { get; set; } = "All";
    [BindProperty(SupportsGet = true)] public string Setup    { get; set; } = "All";

    // Resolved ET datetimes for display in inputs
    public DateTime FromEt { get; private set; }
    public DateTime ToEt   { get; private set; }

    public BacktestResult?  Result           { get; private set; }
    public List<string>     AvailableSetups  { get; private set; } = new();
    public bool             SingleDay        { get; private set; }

    public async Task OnGetAsync()
    {
        var etTz  = EtTz();
        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etTz);

        // Parse inputs (ET) or default to today midnight → now
        FromEt = DateTime.TryParse(FromStr, out var f) ? f : nowEt.Date;
        ToEt   = DateTime.TryParse(ToStr,   out var t) ? t : nowEt;

        // Ensure From ≤ To
        if (FromEt > ToEt) ToEt = FromEt;

        // Convert ET → UTC for DB query
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(FromEt, DateTimeKind.Unspecified), etTz);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(ToEt, DateTimeKind.Unspecified), etTz);

        // All distinct setup labels seen in live trades (for dropdown)
        AvailableSetups = await _db.Trades
            .Where(t => t.Source == "live" && t.SetupLabel != null && t.SetupLabel != "")
            .Select(t => t.SetupLabel!)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();

        // Build filtered query
        var query = _db.Trades.AsQueryable()
            .Where(t => t.Source == "live"
                     && t.EnteredAt >= fromUtc
                     && t.EnteredAt <= toUtc);

        if (Session != "All")
            query = query.Where(t => t.SessionId == Session);

        if (Setup != "All")
            query = query.Where(t => t.SetupLabel == Setup);

        var trades = await query.OrderBy(t => t.EnteredAt).ToListAsync();

        if (trades.Count > 0)
        {
            var btCfg = new BacktestConfig { From = fromUtc.Date, To = toUtc.Date };
            Result    = BacktestResultCalculator.Calculate(trades, _cfgSvc.Current, btCfg);
            SingleDay = trades.All(t => t.ExitedAt.Date == trades[0].ExitedAt.Date);
        }
    }

    public static TimeZoneInfo EtTz()
    {
        try   { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }
}
