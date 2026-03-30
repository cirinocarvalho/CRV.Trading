using CRV.Core.Models;

namespace CRV.Core.Interfaces;

/// <summary>
/// Abstracts order persistence strategy: live brokers use lightweight StrategyLog + REST recovery,
/// Mock/Backtest uses full GroupOrder/OrderLeg DB persistence.
/// </summary>
public interface IBrokerPersistence
{
    /// <summary>Write-once: record that a strategy was placed (called on group creation).</summary>
    Task OnGroupPlacedAsync(GroupOrder group);

    /// <summary>Mark a strategy as completed (called on trade completion/cancellation).</summary>
    Task OnGroupCompletedAsync(GroupOrder group);

    /// <summary>Recover active groups on startup. Live brokers rediscover from REST, Mock returns empty.</summary>
    Task<List<GroupOrder>> RecoverAsync(IGroupOrderExecutor executor, CancellationToken ct);

    /// <summary>If true, mid-trade DB updates (entry fill, state change, leg discovery) are persisted.
    /// False for live brokers — REST is the source of truth.</summary>
    bool PersistsLiveOrderState { get; }
}
