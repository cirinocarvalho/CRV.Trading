namespace CRV.Core.Strategy;

using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Central event handler that reacts to broker order events and drives
/// all trade lifecycle: entry confirmation, tg1 → BE move, completion,
/// P&amp;L accrual, and group state transitions.
///
/// Subscribes to <see cref="IBrokerEventStream.OnOrderUpdate"/> events.
/// Both Mock and Tradovate event streams feed into this same handler.
/// </summary>
public class BrokerEventHandler
{
    private readonly IGroupOrderExecutor _executor;
    private readonly ILogger? _log;

    // Active groups keyed by SetupId
    private readonly Dictionary<string, (GroupOrder Group, ISetupStrategy Strategy)> _active = new();
    private readonly object _lock = new();

    // Per-group async lock to serialize concurrent WSS events for the same group
    // (e.g. tg1 fill + stop fill racing)
    private readonly Dictionary<string, SemaphoreSlim> _groupLocks = new();
    private readonly object _groupLocksLock = new();

    /// <summary>Raised when a group completes (target, stop, or manual exit).</summary>
    public event Action<GroupOrder, TradeRecord>? OnTradeCompleted;

    public BrokerEventHandler(IGroupOrderExecutor executor, ILogger? log = null)
    {
        _executor = executor;
        _log = log;
    }

    // ── Registration ────────────────────────────────────────────

    /// <summary>Register a newly placed group order for event tracking.</summary>
    public void RegisterGroup(GroupOrder group, ISetupStrategy strategy)
    {
        // Ensure PointValue is set for P&L calculations
        if (group.PointValue == 0 && strategy.PointValue > 0)
            group.PointValue = strategy.PointValue;

        lock (_lock)
            _active[group.SetupId] = (group, strategy);
    }

    /// <summary>Get the active group for a setup, or null.</summary>
    public GroupOrder? GetActiveGroup(string setupId)
    {
        lock (_lock)
            return _active.TryGetValue(setupId, out var pair) ? pair.Group : null;
    }

    /// <summary>Check if a setup has an active group order.</summary>
    public bool HasActiveGroup(string setupId)
    {
        lock (_lock)
            return _active.ContainsKey(setupId);
    }

    /// <summary>Get group state for snapshot building.</summary>
    public GroupOrder? GetGroupState(string setupId)
    {
        lock (_lock)
            return _active.TryGetValue(setupId, out var pair) ? pair.Group : null;
    }

    /// <summary>Place an entry order and register the group for event tracking.</summary>
    public async Task PlaceEntryAsync(EntrySignal signal, ISetupStrategy strategy)
    {
        var group = await _executor.OnEntrySignalAsync(signal);
        if (group != null)
            RegisterGroup(group, strategy);
    }

    // ── Event handling ──────────────────────────────────────────

    private SemaphoreSlim GetGroupLock(string groupOrderId)
    {
        lock (_groupLocksLock)
        {
            if (!_groupLocks.TryGetValue(groupOrderId, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _groupLocks[groupOrderId] = sem;
            }
            return sem;
        }
    }

    private void RemoveGroupLock(string groupOrderId)
    {
        lock (_groupLocksLock)
            _groupLocks.Remove(groupOrderId);
    }

    /// <summary>Handle an order event from the broker event stream.</summary>
    public async Task HandleEventAsync(OrderEvent evt)
    {
        // Serialize events for the same group to prevent race conditions
        var groupLock = GetGroupLock(evt.GroupOrderId);
        await groupLock.WaitAsync();
        try
        {
            await HandleEventCoreAsync(evt);
        }
        finally
        {
            groupLock.Release();
        }
    }

    private async Task HandleEventCoreAsync(OrderEvent evt)
    {
        GroupOrder? group;
        ISetupStrategy? strategy;

        lock (_lock)
        {
            var match = _active.Values.FirstOrDefault(p => p.Group.GroupOrderId == evt.GroupOrderId);
            group = match.Group;
            strategy = match.Strategy;
        }

        if (group == null)
        {
            _log?.LogWarning("[BEH] Event for unknown group {GroupId}", evt.GroupOrderId);
            return;
        }

        // Update leg status
        var leg = group.GetLegByOrderId(evt.OrderId);
        if (leg != null)
        {
            leg.Status = evt.Status;
            if (evt.FillPrice.HasValue) leg.FillPrice = evt.FillPrice;
            if (evt.Status == OrderLegStatus.Filled) leg.FillTime = evt.Timestamp;
            if (evt.ModifiedPrice.HasValue)
            {
                leg.Price = evt.ModifiedPrice.Value;
                leg.LastModifiedAt = evt.Timestamp;
            }
        }

        switch (evt.LegType)
        {
            case LegType.Entry:
                await HandleEntryEventAsync(group, strategy!, evt);
                break;
            case LegType.Tg1:
                await HandleTg1EventAsync(group, strategy!, evt);
                break;
            case LegType.Tg2:
                await HandleTg2EventAsync(group, strategy!, evt);
                break;
            case LegType.Stop:
                await HandleStopEventAsync(group, strategy!, evt);
                break;
        }
    }

    // ── Manual exit ─────────────────────────────────────────────

    /// <summary>Manually exit a group order (Cancel if pending, Market Close if active).</summary>
    public async Task ExitGroupAsync(string setupId)
    {
        GroupOrder? group;
        ISetupStrategy? strategy;

        lock (_lock)
        {
            if (!_active.TryGetValue(setupId, out var pair)) return;
            group = pair.Group;
            strategy = pair.Strategy;
        }

        switch (group.Status)
        {
            case GroupOrderStatus.Pending:
                foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
                    await _executor.CancelOrderAsync(leg.OrderId);
                group.Status = GroupOrderStatus.Canceled;
                group.CompletedAt = DateTime.UtcNow;
                break;

            case GroupOrderStatus.Active:
                foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
                    await _executor.CancelOrderAsync(leg.OrderId);
                await _executor.PlaceMarketCloseAsync(group.Ticker, group.Direction, group.TotalContracts);
                group.Status = GroupOrderStatus.Completed;
                group.CompletedAt = DateTime.UtcNow;
                break;

            case GroupOrderStatus.PartialFilled:
                foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
                    await _executor.CancelOrderAsync(leg.OrderId);
                var remaining = group.TotalContracts - group.PartialContracts;
                await _executor.PlaceMarketCloseAsync(group.Ticker, group.Direction, remaining);
                group.Status = GroupOrderStatus.Completed;
                group.CompletedAt = DateTime.UtcNow;
                break;
        }

        strategy?.SetInTrade(false);

        lock (_lock)
            _active.Remove(setupId);
        RemoveGroupLock(group.GroupOrderId);
    }

    /// <summary>Force-close all active groups (session boundary).</summary>
    public async Task ExitAllAsync()
    {
        List<string> setupIds;
        lock (_lock)
            setupIds = _active.Keys.ToList();

        foreach (var id in setupIds)
            await ExitGroupAsync(id);
    }

    // ── P&L ─────────────────────────────────────────────────────

    /// <summary>Calculate unrealized P&amp;L for a setup.</summary>
    public decimal GetUnrealizedPnl(string setupId, decimal currentPrice)
    {
        GroupOrder? group;
        lock (_lock)
        {
            if (!_active.TryGetValue(setupId, out var pair)) return 0m;
            group = pair.Group;
        }

        if (group.EntryPrice is null) return 0m;

        var entry = group.EntryPrice.Value;
        var pv = group.PointValue;
        bool isLong = group.Direction == Direction.Long;

        int remainingQty = group.Status == GroupOrderStatus.PartialFilled
            ? group.TotalContracts - group.PartialContracts
            : group.TotalContracts;

        var unrealized = isLong
            ? (currentPrice - entry) * pv * remainingQty
            : (entry - currentPrice) * pv * remainingQty;

        return unrealized + group.AccruedPartialPnl;
    }

    // ── Private event handlers ──────────────────────────────────

    private Task HandleEntryEventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status == OrderLegStatus.Filled)
        {
            group.Status = GroupOrderStatus.Active;
            group.EntryPrice = evt.FillPrice;
            strategy.SetInTrade(true);
            _log?.LogInformation("[BEH] Entry FILLED grp={G} @ {P}", group.GroupOrderId, evt.FillPrice);
        }
        else if (evt.Status == OrderLegStatus.Rejected)
        {
            _log?.LogWarning("[BEH] Entry REJECTED grp={G}", group.GroupOrderId);
            return CancelRemainingAndComplete(group, strategy, GroupOrderStatus.Canceled);
        }

        return Task.CompletedTask;
    }

    private async Task HandleTg1EventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        group.Status = GroupOrderStatus.PartialFilled;

        // Accrue partial P&L
        if (group.EntryPrice.HasValue && evt.FillPrice.HasValue)
        {
            bool isLong = group.Direction == Direction.Long;
            var partialPnl = isLong
                ? (evt.FillPrice.Value - group.EntryPrice.Value) * group.PointValue * group.PartialContracts
                : (group.EntryPrice.Value - evt.FillPrice.Value) * group.PointValue * group.PartialContracts;
            group.AccruedPartialPnl = partialPnl;
        }

        // Move stop to BE (if enabled) with reduced qty
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null && group.EntryPrice.HasValue)
        {
            var remaining = group.TotalContracts - group.PartialContracts;
            var newStopPrice = group.UseBe ? group.EntryPrice.Value : stopLeg.Price;
            await _executor.ModifyOrderAsync(stopLeg.OrderId, newStopPrice, remaining);
            if (group.UseBe)
                _log?.LogInformation("[BEH] Tg1 FILLED grp={G} — stop→BE @ {P}, qty→{Q}",
                    group.GroupOrderId, group.EntryPrice, remaining);
            else
                _log?.LogInformation("[BEH] Tg1 FILLED grp={G} — stop stays @ {P}, qty→{Q}",
                    group.GroupOrderId, stopLeg.Price, remaining);
        }
    }

    private async Task HandleTg2EventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        // Cancel stop leg
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null && stopLeg.Status == OrderLegStatus.Working)
            await _executor.CancelOrderAsync(stopLeg.OrderId);

        await CompleteGroup(group, strategy, ExitReason.Target, evt.FillPrice ?? 0m, evt.Timestamp);
    }

    private async Task HandleStopEventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        // Cancel tg1 and tg2 legs that are still working
        foreach (var leg in group.Legs.Where(l =>
            (l.LegType == LegType.Tg1 || l.LegType == LegType.Tg2) &&
            l.Status == OrderLegStatus.Working))
        {
            await _executor.CancelOrderAsync(leg.OrderId);
        }

        await CompleteGroup(group, strategy, ExitReason.Stop, evt.FillPrice ?? 0m, evt.Timestamp);
    }

    private async Task CancelRemainingAndComplete(GroupOrder group, ISetupStrategy strategy, GroupOrderStatus status)
    {
        foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
            await _executor.CancelOrderAsync(leg.OrderId);

        group.Status = status;
        group.CompletedAt = DateTime.UtcNow;
        strategy.SetInTrade(false);

        lock (_lock)
            _active.Remove(group.SetupId);
        RemoveGroupLock(group.GroupOrderId);
    }

    private Task CompleteGroup(GroupOrder group, ISetupStrategy strategy,
        ExitReason reason, decimal exitPrice, DateTime exitTime)
    {
        group.Status = GroupOrderStatus.Completed;
        group.CompletedAt = exitTime;
        strategy.SetInTrade(false);

        // Build trade record
        if (group.EntryPrice.HasValue)
        {
            bool isLong = group.Direction == Direction.Long;
            var entry = group.EntryPrice.Value;
            var stopLeg = group.GetLeg(LegType.Stop);
            var initStop = stopLeg?.Price ?? 0m;
            var tg1 = group.GetLeg(LegType.Tg1);
            var tg2 = group.GetLeg(LegType.Tg2);

            // Compute total P&L
            int remaining = group.TotalContracts - group.PartialContracts;
            decimal exitPnl = isLong
                ? (exitPrice - entry) * group.PointValue * remaining
                : (entry - exitPrice) * group.PointValue * remaining;
            decimal totalPnl = exitPnl + group.AccruedPartialPnl;

            decimal risk = Math.Abs(entry - initStop) * group.PointValue * group.TotalContracts;
            decimal rMult = risk > 0 ? totalPnl / risk : 0m;

            var trade = new TradeRecord
            {
                Setup = strategy.SetupId,
                SetupLabel = strategy.Id,
                Direction = group.Direction,
                Ticker = group.Ticker.TrimStart('/'),
                Contracts = group.TotalContracts,
                Entry = entry,
                InitialStop = initStop,
                Target = tg2?.Price ?? 0m,
                Partial = tg1?.Price ?? 0m,
                Exit = exitPrice,
                ExitReason = reason,
                PartialFilled = group.AccruedPartialPnl != 0m,
                PartialPrice = tg1?.FillPrice ?? tg1?.Price ?? 0m,
                GrossPnl = totalPnl,
                Commission = 0m,  // calculated downstream by risk manager
                NetPnl = totalPnl,
                RMultiple = rMult,
                EnteredAt = group.CreatedAt,
                ExitedAt = exitTime,
                SessionId = group.SessionId ?? "",
            };

            OnTradeCompleted?.Invoke(group, trade);
        }

        lock (_lock)
            _active.Remove(group.SetupId);
        RemoveGroupLock(group.GroupOrderId);

        return Task.CompletedTask;
    }
}
