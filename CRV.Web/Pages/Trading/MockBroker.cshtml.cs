namespace CRV.Web.Pages.Trading;

using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

public class MockBrokerModel : PageModel
{
    private readonly TradingDbContext      _db;
    private readonly StrategyConfigService _cfgSvc;

    public IReadOnlyList<TradeRecord> Trades         { get; private set; } = [];
    public IReadOnlyList<TradeRecord> AllTrades      { get; private set; } = [];
    public IReadOnlyList<DateOnly>    AvailableDates { get; private set; } = [];
    public DateOnly                   SelectedDate   { get; private set; }
    public MetricSummary              Total          { get; private set; } = new();
    public Dictionary<string, MetricSummary> PerSetup { get; private set; } = new();

    public MockBrokerModel(TradingDbContext db, StrategyConfigService cfgSvc)
    {
        _db     = db;
        _cfgSvc = cfgSvc;
    }

    public async Task OnGetAsync(DateOnly? date = null)
    {
        var cfg = _cfgSvc.Current;

        // All closed mock trades — used for equity curve
        AllTrades = await _db.Trades
            .Where(t => t.Source == "mock")
            .OrderBy(t => t.EnteredAt)
            .ToListAsync();

        // Distinct available trading dates
        var etTz = GetEtTz();
        AvailableDates = AllTrades
            .Select(t =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(t.EnteredAt, etTz);
                return DateOnly.FromDateTime(cfg.TradingDate(local));
            })
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        // Default to most recent date, or today
        SelectedDate = date ?? (AvailableDates.Count > 0 ? AvailableDates[0] : DateOnly.FromDateTime(DateTime.Today));

        // Filter trades for selected date
        Trades = AllTrades
            .Where(t =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(t.EnteredAt, etTz);
                return DateOnly.FromDateTime(cfg.TradingDate(local)) == SelectedDate;
            })
            .OrderBy(t => t.EnteredAt)
            .ToList();

        Total  = CalcMetrics(Trades);
        PerSetup = Trades
            .GroupBy(t => !string.IsNullOrEmpty(t.SetupLabel) ? t.SetupLabel : t.Setup.ToString())
            .Where(g => g.Any())
            .ToDictionary(g => g.Key, g => CalcMetrics(g.ToList()));
    }

    public static MetricSummary CalcMetrics(IReadOnlyList<TradeRecord> trades)
    {
        if (trades.Count == 0) return new();
        var wins   = trades.Where(t => t.IsWin).ToList();
        var losses = trades.Where(t => !t.IsWin).ToList();
        var winRate = (decimal)wins.Count / trades.Count * 100;
        var avgWin  = wins.Count   > 0 ? wins.Average(t => t.NetPnl)   : 0;
        var avgLoss = losses.Count > 0 ? losses.Average(t => t.NetPnl) : 0;
        return new MetricSummary
        {
            TotalTrades  = trades.Count,
            NetPnl       = trades.Sum(t => t.NetPnl),
            WinRate      = winRate,
            AvgR         = trades.Average(t => t.RMultiple),
            AvgWin       = avgWin,
            AvgLoss      = avgLoss,
            Expectancy   = (winRate / 100m) * avgWin + ((100m - winRate) / 100m) * avgLoss,
        };
    }

    private static TimeZoneInfo GetEtTz()
    {
        try   { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }
}

public class MetricSummary
{
    public int     TotalTrades { get; set; }
    public decimal NetPnl      { get; set; }
    public decimal WinRate     { get; set; }
    public decimal AvgR        { get; set; }
    public decimal AvgWin      { get; set; }
    public decimal AvgLoss     { get; set; }
    public decimal Expectancy  { get; set; }
}
