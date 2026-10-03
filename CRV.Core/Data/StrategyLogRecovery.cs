using CRV.Core.Models;

namespace CRV.Core.Data;

/// <summary>
/// Which broker strategies a restart tries to recover: every row not yet completed, however
/// old, because a position held past session end can stay open for any number of days.
/// Recovery marks completed each row the broker reports finished, canceled or without legs,
/// so those drop out; a failed broker lookup leaves the row for the next restart.
/// </summary>
public static class StrategyLogRecovery
{
    public static IQueryable<StrategyLog> Recoverable(IQueryable<StrategyLog> logs)
        => logs.Where(s => !s.IsCompleted);
}
