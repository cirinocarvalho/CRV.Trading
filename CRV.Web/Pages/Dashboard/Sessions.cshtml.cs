namespace CRV.Web.Pages.Dashboard;

using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class SessionsModel : PageModel
{
    private readonly TradeRepository _trades;
    public SessionsModel(TradeRepository trades) => _trades = trades;

    public List<TradeRecord> AsiaTrades   { get; set; } = new();
    public List<TradeRecord> LondonTrades { get; set; } = new();
    public List<TradeRecord> NYTrades     { get; set; } = new();

    public async Task OnGetAsync()
    {
        var today = await _trades.GetTodayAsync();
        AsiaTrades   = today.Where(t => t.SessionId == "Asia").ToList();
        LondonTrades = today.Where(t => t.SessionId == "London").ToList();
        NYTrades     = today.Where(t => t.SessionId == "NY").ToList();
    }
}
