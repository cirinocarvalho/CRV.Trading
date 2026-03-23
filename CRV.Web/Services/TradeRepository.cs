namespace CRV.Web.Services;

using CRV.Core.Data;
using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;

public class TradeRepository
{
    private readonly TradingDbContext _db;
    public TradeRepository(TradingDbContext db) => _db = db;

    public async Task<List<TradeRecord>> GetTodayAsync()
    {
        // Use Eastern Time with trading-day boundary at 18:00 ET (SessionStartHour).
        // A trading day runs from 18:00 ET to 17:59 ET the next day, spanning midnight.
        // At 00:30 ET the trading day started at 18:00 ET the previous calendar day.
        var est = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, est);
        const int sessionStartHour = 18;
        var tradingDayStart = now.Hour >= sessionStartHour
            ? now.Date.AddHours(sessionStartHour)                    // e.g. today 18:00
            : now.Date.AddDays(-1).AddHours(sessionStartHour);       // e.g. yesterday 18:00
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(tradingDayStart, est);
        return await _db.Trades
            .Where(t => t.EnteredAt >= fromUtc && (t.Source == "live" || t.Source == "mock"))
            .OrderByDescending(t => t.EnteredAt)
            .ToListAsync();
    }

    public async Task<List<TradeRecord>> GetBySourceAsync(string source)
        => await _db.Trades.Where(t => t.Source == source)
                            .OrderBy(t => t.EnteredAt).ToListAsync();

    public async Task AddAsync(TradeRecord r) { _db.Trades.Add(r); await _db.SaveChangesAsync(); }
}
