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

    // Limit settings read by DdBreached
    private bool          _useDailyLossLimit;
    private decimal       _maxDailyLoss;
    private DailyLossMode _mode = DailyLossMode.Floor;

    /// <summary>
    /// True when the daily loss limit is breached, counting realized P&amp;L plus the open loss of
    /// positions held in from an earlier trading day (<paramref name="heldUnrealized"/>). Only a loss
    /// counts: a held winner never offsets a realized loss.
    /// Floor mode: TodayPnl + held loss &lt;= -MaxDailyLoss (absolute floor).
    /// Peak mode:  (TodayPeak - (TodayPnl + held loss)) &gt;= MaxDailyLoss (drawdown from high-water mark).
    /// Dynamic in both modes: recovers when PnL improves.
    /// </summary>
    public bool DdBreached(decimal heldUnrealized = 0m)
    {
        if (!_useDailyLossLimit) return false;
        var pnl = TodayPnl + Math.Min(0m, heldUnrealized);
        return _mode switch
        {
            DailyLossMode.Peak => (TodayPeak - pnl) >= _maxDailyLoss,
            _                  => pnl <= -_maxDailyLoss,
        };
    }

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

    /// <summary>Sets the limit that <see cref="DdBreached"/> checks, so it holds before any entry is attempted.</summary>
    public void ApplyLimit(bool useDailyLossLimit, decimal maxDailyLoss, DailyLossMode mode = DailyLossMode.Floor)
    {
        _useDailyLossLimit = useDailyLossLimit;
        _maxDailyLoss = maxDailyLoss;
        _mode = mode;
    }

    /// <summary>
    /// Returns true when a new trade is allowed.
    /// Dynamic: if PnL recovers above -maxDailyLoss, trading resumes.
    /// </summary>
    public bool CanTrade(bool useDailyLossLimit, decimal maxDailyLoss,
                         DailyLossMode mode = DailyLossMode.Floor, decimal heldUnrealized = 0m)
    {
        ApplyLimit(useDailyLossLimit, maxDailyLoss, mode);

        return !DdBreached(heldUnrealized);
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
