using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Tracks global daily PnL across all setups and enforces the daily loss limit.
/// The limit is dynamic: if winning trades recover the PnL above the threshold,
/// trading resumes automatically.
/// </summary>
public class RiskManager
{
    public decimal TodayPnl      { get; private set; }
    public decimal TodayPeak     { get; private set; }
    public decimal TodayMaxDD    { get; private set; }
    public int     TodayWins     { get; private set; }
    public int     TodayLosses   { get; private set; }
    public decimal TodayWinPnl   { get; private set; }
    public decimal TodayLossPnl  { get; private set; }

    // Cached limit config for the DdBreached property
    private bool          _useDailyLossLimit;
    private decimal       _maxDailyLoss;
    private DailyLossMode _mode = DailyLossMode.Floor;

    /// <summary>
    /// True when the daily loss limit is breached.
    /// Floor mode: TodayPnl &lt;= -MaxDailyLoss (absolute floor).
    /// Peak mode:  (TodayPeak - TodayPnl) &gt;= MaxDailyLoss (drawdown from high-water mark).
    /// Dynamic in both modes: recovers when PnL improves.
    /// </summary>
    public bool DdBreached => _useDailyLossLimit && _mode switch
    {
        DailyLossMode.Peak  => (TodayPeak - TodayPnl) >= _maxDailyLoss,
        _                   => TodayPnl <= -_maxDailyLoss,
    };

    /// <summary>
    /// How much of the daily loss limit has been "used".
    /// Floor mode: absolute negative PnL (0 when positive).
    /// Peak mode: drawdown from high-water mark (TodayPeak - TodayPnl).
    /// </summary>
    public decimal DailyLossUsed => _mode switch
    {
        DailyLossMode.Peak  => TodayPeak - TodayPnl,
        _                   => Math.Abs(Math.Min(0, TodayPnl)),
    };

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
    /// Dynamic: if PnL recovers above -maxDailyLoss, trading resumes.
    /// </summary>
    public bool CanTrade(bool useDailyLossLimit, decimal maxDailyLoss,
                         DailyLossMode mode = DailyLossMode.Floor)
    {
        // Cache for DdBreached property (used by ProcessPriceTickAsync)
        _useDailyLossLimit = useDailyLossLimit;
        _maxDailyLoss = maxDailyLoss;
        _mode = mode;

        return !DdBreached;
    }

    /// <summary>Resets all daily state (call at the start of each trading day).</summary>
    public void ResetDay()
    {
        TodayPnl     = 0;
        TodayPeak    = 0;
        TodayMaxDD   = 0;
        TodayWins    = 0;
        TodayLosses  = 0;
        TodayWinPnl  = 0;
        TodayLossPnl = 0;
    }
}
