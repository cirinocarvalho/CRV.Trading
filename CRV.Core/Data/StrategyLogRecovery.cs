using CRV.Core.Models;

namespace CRV.Core.Data;

/// <summary>
/// Which broker strategies a restart tries to recover. A position held past session end can
/// stay open over a weekend or holiday, so recovery looks back a week rather than to midnight.
/// A row the broker reports finished is marked completed by recovery, so older rows drop out.
/// </summary>
public static class StrategyLogRecovery
{
    public const int LookbackDays = 7;

    public static IQueryable<StrategyLog> Recoverable(IQueryable<StrategyLog> logs, DateTime utcNow)
    {
        var since = utcNow.Date.AddDays(-LookbackDays);
        return logs.Where(s => !s.IsCompleted && s.CreatedAt >= since);
    }
}
