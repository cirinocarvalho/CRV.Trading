namespace CRV.Core.Strategy;

/// <summary>
/// Tracks global daily PnL across all setups and enforces the daily loss limit.
/// Extracted from OrbStrategyEngine (_todayPnl / _todayPeak / _todayMaxDD / _ddBreached).
/// </summary>
public class RiskManager
{
    public decimal TodayPnl      { get; private set; }
    public decimal TodayPeak     { get; private set; }
    public decimal TodayMaxDD    { get; private set; }
    public bool    DdBreached    { get; private set; }
    public int     TodayWins     { get; private set; }
    public int     TodayLosses   { get; private set; }
    public decimal TodayWinPnl   { get; private set; }
    public decimal TodayLossPnl  { get; private set; }

    /// <summary>Records the net PnL of one completed trade and updates all counters.</summary>
    public void RecordTrade(decimal netPnl)
    {
        TodayPnl += netPnl;

        if (netPnl > 0)
        {
            TodayWins++;
            TodayWinPnl += netPnl;
        }
        else
        {
            TodayLosses++;
            TodayLossPnl += netPnl;
        }

        if (TodayPnl > TodayPeak)
            TodayPeak = TodayPnl;

        var dd = TodayPeak - TodayPnl;
        if (dd > TodayMaxDD)
            TodayMaxDD = dd;
    }

    /// <summary>
    /// Returns true when a new trade is allowed.
    /// Sets <see cref="DdBreached"/> permanently once the daily loss limit is hit.
    /// </summary>
    public bool CanTrade(bool useDailyLossLimit, decimal maxDailyLoss)
    {
        if (DdBreached) return false;

        if (useDailyLossLimit && TodayPnl <= -maxDailyLoss)
        {
            DdBreached = true;
            return false;
        }

        return true;
    }

    /// <summary>Resets all daily state (call at the start of each trading day).</summary>
    public void ResetDay()
    {
        TodayPnl     = 0;
        TodayPeak    = 0;
        TodayMaxDD   = 0;
        DdBreached   = false;
        TodayWins    = 0;
        TodayLosses  = 0;
        TodayWinPnl  = 0;
        TodayLossPnl = 0;
    }
}
