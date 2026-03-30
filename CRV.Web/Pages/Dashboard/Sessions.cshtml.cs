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
    public string SelectedDate { get; set; } = "";

    public async Task OnGetAsync(string? date = null)
    {
        var selectedDate = DateTime.TryParse(date, out var d) ? d : DateTime.Today;
        SelectedDate = selectedDate.ToString("yyyy-MM-dd");

        var trades = date != null
            ? await _trades.GetByDateAsync(selectedDate)
            : await _trades.GetTodayAsync();

        AsiaTrades   = trades.Where(t => t.SessionId == "Asia").ToList();
        LondonTrades = trades.Where(t => t.SessionId == "London").ToList();
        NYTrades     = trades.Where(t => t.SessionId == "NY").ToList();
    }
}
