namespace CRV.Web.Services;

using CRV.Core.Models;

// ─────────────────────────────────────────────────────────────
// DailyStatsService — in-memory today's trading stats
// Resets on new trading day; TradeClosed events update it
// ─────────────────────────────────────────────────────────────

public class DailyStats
{
    public decimal TodayPnL        { get; set; }
    public decimal TodayNetPnL     { get; set; }
    public int     TodayTrades     { get; set; }
    public int     TodayWins       { get; set; }
    public int     TodayLosses     { get; set; }
    public int     TodayWinsA      { get; set; }
    public int     TodayLossesA    { get; set; }
    public int     TodayWinsB      { get; set; }
    public int     TodayLossesB    { get; set; }
    public decimal TodayMaxDD      { get; set; }
    public decimal TodayPeak       { get; set; }
    public bool    DDBreached      { get; set; }
    public DateTime Date           { get; set; } = DateTime.UtcNow.Date;

    // Running P&L sums for Expectancy calculation
    public decimal TodayWinPnl     { get; set; }
    public decimal TodayLossPnl    { get; set; }
    public decimal TodayWinPnlA    { get; set; }
    public decimal TodayLossPnlA   { get; set; }
    public decimal TodayWinPnlB    { get; set; }
    public decimal TodayLossPnlB   { get; set; }

    // Computed helpers
    public decimal WinRate    => TodayTrades > 0 ? (decimal)TodayWins / TodayTrades * 100 : 0;
    public decimal AvgWin     => TodayWins   > 0 ? TodayWinPnl  / TodayWins   : 0;
    public decimal AvgLoss    => TodayLosses > 0 ? TodayLossPnl / TodayLosses : 0;
    public decimal Expectancy => TodayTrades > 0
        ? (WinRate / 100m) * AvgWin - ((100m - WinRate) / 100m) * AvgLoss : 0;

    public decimal AvgWinA     => TodayWinsA   > 0 ? TodayWinPnlA  / TodayWinsA   : 0;
    public decimal AvgLossA    => TodayLossesA > 0 ? TodayLossPnlA / TodayLossesA : 0;
    public decimal WinRateA    => (TodayWinsA + TodayLossesA) > 0 ? (decimal)TodayWinsA / (TodayWinsA + TodayLossesA) * 100 : 0;
    public decimal ExpectancyA => (TodayWinsA + TodayLossesA) > 0
        ? (WinRateA / 100m) * AvgWinA - ((100m - WinRateA) / 100m) * AvgLossA : 0;

    public decimal AvgWinB     => TodayWinsB   > 0 ? TodayWinPnlB  / TodayWinsB   : 0;
    public decimal AvgLossB    => TodayLossesB > 0 ? TodayLossPnlB / TodayLossesB : 0;
    public decimal WinRateB    => (TodayWinsB + TodayLossesB) > 0 ? (decimal)TodayWinsB / (TodayWinsB + TodayLossesB) * 100 : 0;
    public decimal ExpectancyB => (TodayWinsB + TodayLossesB) > 0
        ? (WinRateB / 100m) * AvgWinB - ((100m - WinRateB) / 100m) * AvgLossB : 0;
}

public class DailyStatsService
{
    private DailyStats _stats = new();
    private readonly object _lock = new();

    public DailyStats Get() { lock(_lock){ return _stats; } }

    public void OnTradeClosed(TradeRecord r, decimal maxDailyLoss)
    {
        lock (_lock)
        {
            if (_stats.Date != DateTime.UtcNow.Date)
            {
                _stats = new DailyStats { Date = DateTime.UtcNow.Date };
            }

            _stats.TodayPnL    += r.GrossPnl;
            _stats.TodayNetPnL += r.NetPnl;
            _stats.TodayTrades++;

            if (r.NetPnl >= 0)
            {
                _stats.TodayWins++;
                _stats.TodayWinPnl += r.NetPnl;
                if (r.Setup == SetupId.A) { _stats.TodayWinsA++;   _stats.TodayWinPnlA  += r.NetPnl; }
                else                      { _stats.TodayWinsB++;   _stats.TodayWinPnlB  += r.NetPnl; }
            }
            else
            {
                _stats.TodayLosses++;
                _stats.TodayLossPnl += r.NetPnl;
                if (r.Setup == SetupId.A) { _stats.TodayLossesA++; _stats.TodayLossPnlA += r.NetPnl; }
                else                      { _stats.TodayLossesB++; _stats.TodayLossPnlB += r.NetPnl; }
            }

            _stats.TodayPeak = Math.Max(_stats.TodayPeak, _stats.TodayNetPnL);
            decimal dd = _stats.TodayPeak - _stats.TodayNetPnL;
            _stats.TodayMaxDD = Math.Max(_stats.TodayMaxDD, dd);
            _stats.DDBreached = _stats.TodayNetPnL <= -Math.Abs(maxDailyLoss);
        }
    }
}
