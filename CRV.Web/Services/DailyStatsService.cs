namespace CRV.Web.Services;

using CRV.Core.Models;

// ─────────────────────────────────────────────────────────────
// DailyStatsService — in-memory today's trading stats
// Resets on new trading day; TradeClosed events update it
// ─────────────────────────────────────────────────────────────

public class PerSetupStats
{
    public int     Wins    { get; set; }
    public int     Losses  { get; set; }
    public decimal WinPnl  { get; set; }
    public decimal LossPnl { get; set; }

    public int     Trades   => Wins + Losses;
    public decimal WinRate  => Trades > 0 ? (decimal)Wins / Trades * 100 : 0;
    public decimal AvgWin   => Wins   > 0 ? WinPnl  / Wins   : 0;
    public decimal AvgLoss  => Losses > 0 ? LossPnl / Losses : 0;
    public decimal Expectancy => Trades > 0
        ? (WinRate / 100m) * AvgWin - ((100m - WinRate) / 100m) * AvgLoss : 0;
}

public class DailyStats
{
    public decimal TodayPnL        { get; set; }
    public decimal TodayNetPnL     { get; set; }
    public int     TodayTrades     { get; set; }
    public int     TodayWins       { get; set; }
    public int     TodayLosses     { get; set; }
    public decimal TodayMaxDD      { get; set; }
    public decimal TodayPeak       { get; set; }
    public bool    DDBreached      { get; set; }
    public DateTime Date           { get; set; } = DateTime.UtcNow.Date;

    // Running P&L sums for Expectancy calculation
    public decimal TodayWinPnl     { get; set; }
    public decimal TodayLossPnl    { get; set; }

    // Per-setup stats keyed by string Id (e.g. "A", "B", "b-mnq-1")
    public Dictionary<string, PerSetupStats> PerSetup { get; set; } = new();

    // Computed helpers
    public decimal WinRate    => TodayTrades > 0 ? (decimal)TodayWins / TodayTrades * 100 : 0;
    public decimal AvgWin     => TodayWins   > 0 ? TodayWinPnl  / TodayWins   : 0;
    public decimal AvgLoss    => TodayLosses > 0 ? TodayLossPnl / TodayLosses : 0;
    public decimal Expectancy => TodayTrades > 0
        ? (WinRate / 100m) * AvgWin - ((100m - WinRate) / 100m) * AvgLoss : 0;

    // Legacy accessors for backward compatibility
    public int     TodayWinsA      => GetSetup("A").Wins;
    public int     TodayLossesA    => GetSetup("A").Losses;
    public decimal TodayWinPnlA    => GetSetup("A").WinPnl;
    public decimal TodayLossPnlA   => GetSetup("A").LossPnl;
    public decimal WinRateA        => GetSetup("A").WinRate;
    public decimal AvgWinA         => GetSetup("A").AvgWin;
    public decimal AvgLossA        => GetSetup("A").AvgLoss;
    public decimal ExpectancyA     => GetSetup("A").Expectancy;

    public int     TodayWinsB      => GetSetup("B").Wins;
    public int     TodayLossesB    => GetSetup("B").Losses;
    public decimal TodayWinPnlB    => GetSetup("B").WinPnl;
    public decimal TodayLossPnlB   => GetSetup("B").LossPnl;
    public decimal WinRateB        => GetSetup("B").WinRate;
    public decimal AvgWinB         => GetSetup("B").AvgWin;
    public decimal AvgLossB        => GetSetup("B").AvgLoss;
    public decimal ExpectancyB     => GetSetup("B").Expectancy;

    private static readonly PerSetupStats _empty = new();
    public PerSetupStats GetSetup(string id) =>
        PerSetup.TryGetValue(id, out var s) ? s : _empty;
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

            // Resolve setup key: prefer SetupLabel (basket ID), fall back to enum name
            var setupKey = !string.IsNullOrEmpty(r.SetupLabel) ? r.SetupLabel : r.Setup.ToString();
            if (!_stats.PerSetup.TryGetValue(setupKey, out var ps))
            {
                ps = new PerSetupStats();
                _stats.PerSetup[setupKey] = ps;
            }

            if (r.NetPnl >= 0)
            {
                _stats.TodayWins++;
                _stats.TodayWinPnl += r.NetPnl;
                ps.Wins++;
                ps.WinPnl += r.NetPnl;
            }
            else
            {
                _stats.TodayLosses++;
                _stats.TodayLossPnl += r.NetPnl;
                ps.Losses++;
                ps.LossPnl += r.NetPnl;
            }

            _stats.TodayPeak = Math.Max(_stats.TodayPeak, _stats.TodayNetPnL);
            decimal dd = _stats.TodayPeak - _stats.TodayNetPnL;
            _stats.TodayMaxDD = Math.Max(_stats.TodayMaxDD, dd);
            _stats.DDBreached = _stats.TodayNetPnL <= -Math.Abs(maxDailyLoss);
        }
    }
}
