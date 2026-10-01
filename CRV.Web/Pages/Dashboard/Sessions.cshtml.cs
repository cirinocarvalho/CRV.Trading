namespace CRV.Web.Pages.Dashboard;

using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>One trading day split into Asia, London and NY. Shows live and paper trades together.</summary>
public class SessionsModel : PageModel
{
    public static readonly string[] SessionOrder = { "Asia", "London", "NY" };

    private readonly TradeRepository _trades;
    private readonly StrategyConfigService _cfgSvc;

    public SessionsModel(TradeRepository trades, StrategyConfigService cfgSvc)
    {
        _trades = trades;
        _cfgSvc = cfgSvc;
    }

    [BindProperty(SupportsGet = true)] public DateOnly? Date { get; set; }
    [BindProperty(SupportsGet = true, Name = "s")] public string? Session { get; set; }

    public DateOnly Day { get; private set; }
    public bool IsToday { get; private set; }
    public Dictionary<string, int> Counts { get; private set; } = new();
    public BacktestResult Result { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var today = CRV.Live.TradingDayRange.Of(DateTime.UtcNow, _cfgSvc.Current.SessionStartHour, ViewFmt.Et);
        Day = Date ?? today;
        IsToday = Day == today;

        var trades = Date == null
            ? await _trades.GetTodayAsync()
            : await _trades.GetByDateAsync(Day.ToDateTime(TimeOnly.MinValue));

        Counts = SessionOrder.ToDictionary(s => s, s => trades.Count(t => t.SessionId == s));

        // Default to the session of the day's latest trade, else NY.
        if (Session == null || !SessionOrder.Contains(Session))
            Session = trades.OrderByDescending(t => t.EnteredAt).Select(t => t.SessionId).FirstOrDefault(SessionOrder.Contains) ?? "NY";

        var selected = trades.Where(t => t.SessionId == Session).OrderBy(t => t.EnteredAt).ToList();
        var day = Day.ToDateTime(TimeOnly.MinValue);
        Result = BacktestResultCalculator.Calculate(selected, _cfgSvc.Current, new BacktestConfig { From = day, To = day });
    }
}
