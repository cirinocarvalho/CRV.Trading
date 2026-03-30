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
    public bool IsBacktest { get; init; }

    /// <summary>When true, session-end exits only clear in-memory state — broker manages the bracket lifecycle.
    /// When false (Mock/Backtest), session-end exits send cancel + market close to the executor.</summary>
    public bool BrokerManagesExits { get; init; }

    // Active groups keyed by SetupId
    private readonly Dictionary<string, (GroupOrder Group, ISetupStrategy Strategy)> _active = new();
    // Secondary index: GroupOrderId → (Group, Strategy) — includes displaced groups
    // so in-flight events can still be processed after a setupId overwrite.
    private readonly Dictionary<string, (GroupOrder Group, ISetupStrategy Strategy)> _byGroupId = new();
    private readonly object _lock = new();

    // Per-group async lock to serialize concurrent WSS events for the same group
    // (e.g. tg1 fill + stop fill racing)
    private readonly Dictionary<string, SemaphoreSlim> _groupLocks = new();
    private readonly object _groupLocksLock = new();

    // Buffer for events that arrive before RegisterGroup (race between WSS fill and registration)
    private readonly Dictionary<string, List<OrderEvent>> _earlyEvents = new();
    private readonly object _earlyLock = new();

    /// <summary>Raised when a group completes (target, stop, or manual exit).</summary>
    public event Action<GroupOrder, TradeRecord>? OnTradeCompleted;

    /// <summary>Raised when a new group is registered (Pending or Active) — persist to DB immediately.</summary>
    public event Action<GroupOrder>? OnGroupRegistered;

    /// <summary>Raised when entry fills and the group becomes Active — update DB.</summary>
    public event Action<GroupOrder>? OnEntryFilled;

    /// <summary>Raised when group state changes (Tg1 fill → PartialFilled) — update DB.</summary>
    public event Action<GroupOrder>? OnGroupStateChanged;

    public BrokerEventHandler(IGroupOrderExecutor executor, ILogger? log = null)
    {
        _executor = executor;
        _log = log;
    }

    // ── Registration ────────────────────────────────────────────

    /// <summary>Register a newly placed group order for event tracking.</summary>
    public async Task RegisterGroupAsync(GroupOrder group, ISetupStrategy strategy)
    {
        // Ensure PointValue is set for P&L calculations
        if (group.PointValue == 0 && strategy.PointValue > 0)
            group.PointValue = strategy.PointValue;

        // Ensure InitialStopPrice is captured before any BE modification
        if (group.InitialStopPrice == 0)
        {
            var stopLeg = group.GetLeg(LegType.Stop);
            if (stopLeg != null) group.InitialStopPrice = stopLeg.Price;
        }

        List<OrderEvent>? buffered = null;
        lock (_lock)
        {
            // If an existing group is still tracked for this setupId, keep it
            // in _byGroupId so in-flight events (Tg1/Tg2/Stop fills) still get
            // processed. Log the overwrite for diagnostics.
            if (_active.TryGetValue(group.SetupId, out var existing))
            {
                _log?.LogWarning(
                    "[BEH] RegisterGroup overwrite: setupId={S} old grp={OG} status={OS} replaced by grp={NG}",
                    group.SetupId, existing.Group.GroupOrderId, existing.Group.Status, group.GroupOrderId);
                // Old group stays in _byGroupId — its events will still be handled
            }

            _active[group.SetupId] = (group, strategy);
            _byGroupId[group.GroupOrderId] = (group, strategy);
        }

        // Drain any events that arrived before registration (WSS fill race)
        lock (_earlyLock)
            if (_earlyEvents.Remove(group.GroupOrderId, out buffered))
                _log?.LogInformation("[BEH] Replaying {N} buffered events for group {G}", buffered.Count, group.GroupOrderId);

        if (buffered != null)
            foreach (var evt in buffered)
                await HandleEventAsync(evt);
    }

    /// <summary>Register a newly placed group order for event tracking (sync overload).</summary>
    public void RegisterGroup(GroupOrder group, ISetupStrategy strategy)
        => RegisterGroupAsync(group, strategy).GetAwaiter().GetResult();

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

    /// <summary>Get all active group orders (for manual trade page display).</summary>
    public List<GroupOrder> GetAllActiveGroups()
    {
        lock (_lock)
            return _active.Values.Select(p => p.Group).ToList();
    }

    /// <summary>Get a group by its GroupOrderId (checks both active and displaced/pending).</summary>
    public GroupOrder? GetGroupById(string groupOrderId)
    {
        lock (_lock)
            return _byGroupId.TryGetValue(groupOrderId, out var pair) ? pair.Group : null;
    }

    /// <summary>Remove a group by its GroupOrderId (used when canceling pending groups).</summary>
    public void RemoveGroupById(string groupOrderId)
    {
        lock (_lock)
        {
            if (_byGroupId.TryGetValue(groupOrderId, out var pair))
            {
                _byGroupId.Remove(groupOrderId);
                // Also remove from _active if it's the current group for this setupId
                if (_active.TryGetValue(pair.Strategy.Id, out var activePair)
                    && activePair.Group.GroupOrderId == groupOrderId)
                {
                    _active.Remove(pair.Strategy.Id);
                    pair.Strategy.SetInTrade(false);
                }
            }
        }
    }

    /// <summary>Place an entry order and register the group for event tracking.</summary>
    public async Task PlaceEntryAsync(EntrySignal signal, ISetupStrategy strategy)
    {
        // One active trade per side per ticker — if any setup already has an active
        // group on the same ticker + direction, skip this entry.
        var ticker = signal.Ticker;
        var dir = signal.Direction;
        lock (_lock)
        {
            foreach (var (_, (g, _)) in _active)
            {
                if (g.Ticker == ticker &&
                    g.Status is GroupOrderStatus.Active or GroupOrderStatus.Pending or GroupOrderStatus.PartialFilled)
                {
                    _log?.LogDebug(
                        "[BEH] Skipping {Setup} {Dir} {Ticker} — already have active group {G} ({ExDir}) from {Existing}",
                        signal.SetupLabel, dir, ticker, g.GroupOrderId, g.Direction, g.SetupId);
                    return;
                }
            }
        }

        // If there's already an active group for this setup, exit it first.
        // This prevents orphaned groups whose events would be lost.
        var setupId = !string.IsNullOrEmpty(signal.SetupLabel) ? signal.SetupLabel : signal.Setup.ToString();
        GroupOrder? existing;
        lock (_lock)
            existing = _active.TryGetValue(setupId, out var pair) ? pair.Group : null;
        if (existing != null)
        {
            _log?.LogWarning("[BEH] PlaceEntry for {S} but group {G} already active (status={St}) — canceling old group",
                setupId, existing.GroupOrderId, existing.Status);
            foreach (var leg in existing.Legs.Where(l => l.Status == OrderLegStatus.Working))
            {
                await _executor.CancelOrderAsync(leg.OrderId);
                leg.Status = OrderLegStatus.Canceled;
            }
            existing.Status = GroupOrderStatus.Canceled;
            existing.CompletedAt = DateTime.UtcNow;
            strategy.SetInTrade(false);
            RemoveGroup(existing);
        }

        // Conservative entries use Limit at the calculated level for tighter fills (live only).
        // Backtest already fills conservative Market orders at sig.Entry via the executor,
        // so no conversion needed — converting would double-gate (require price to touch twice).
        if (!IsBacktest && signal.Mode == "Conservative" && signal.OrderType != "Limit")
            signal = signal with { OrderType = "Limit" };

        var group = await _executor.OnEntrySignalAsync(signal);
        if (group != null)
        {
            // Carry session context so completed trades are tagged correctly
            if (string.IsNullOrEmpty(group.SessionId))
                group.SessionId = signal.SessionId;

            await RegisterGroupAsync(group, strategy);

            // Persist immediately (covers both Pending limit orders and already-filled market orders)
            if (!IsBacktest)
                OnGroupRegistered?.Invoke(group);

            // Immediate fills (backtest market orders): group already Active + EntryPrice set
            // by the executor before returning. Activate the strategy now — no event roundtrip
            // needed (avoids re-entrancy with per-group semaphore).
            if (group.Status == GroupOrderStatus.Active && group.EntryPrice.HasValue)
            {
                strategy.SetInTrade(true);
                _log?.LogDebug("[BEH] Entry FILLED grp={G} @ {P}", group.GroupOrderId, group.EntryPrice);
                OnEntryFilled?.Invoke(group);
            }
        }
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

    /// <summary>Remove a group from both indexes. Only removes from _active if it still
    /// points to this group (a newer group may have replaced it via RegisterGroup).</summary>
    private void RemoveGroup(GroupOrder group)
    {
        lock (_lock)
        {
            _byGroupId.Remove(group.GroupOrderId);
            if (_active.TryGetValue(group.SetupId, out var current) &&
                current.Group.GroupOrderId == group.GroupOrderId)
            {
                _active.Remove(group.SetupId);
            }
        }
        RemoveGroupLock(group.GroupOrderId);
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
            if (_byGroupId.TryGetValue(evt.GroupOrderId, out var match))
            {
                group = match.Group;
                strategy = match.Strategy;
            }
            else
            {
                group = null;
                strategy = null;
            }
        }

        if (group == null)
        {
            // Buffer the event — the group may not be registered yet (race between
            // WSS fill arriving and PlaceManualEntryAsync finishing RegisterGroup).
            lock (_earlyLock)
            {
                if (!_earlyEvents.TryGetValue(evt.GroupOrderId, out var list))
                {
                    list = new List<OrderEvent>();
                    _earlyEvents[evt.GroupOrderId] = list;
                }
                list.Add(evt);
            }
            _log?.LogInformation("[BEH] Buffered early event for group {GroupId} leg={Leg} status={S}",
                evt.GroupOrderId, evt.LegType, evt.Status);
            return;
        }

        // Update leg status
        var leg = group.GetLegByOrderId(evt.OrderId);
        if (leg != null)
        {
            leg.Status = evt.Status;
            if (evt.FillPrice.HasValue) leg.FillPrice = evt.FillPrice;
            if (evt.Status == OrderLegStatus.Filled) leg.FillTime = evt.Timestamp;
            if (evt.ModifiedQty.HasValue)
                leg.Quantity = evt.ModifiedQty.Value;
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

    /// <summary>Manually exit a group order by its GroupOrderId.</summary>
    public async Task ExitGroupByIdAsync(string groupOrderId, decimal exitPrice = 0, ExitReason reason = ExitReason.Manual)
    {
        GroupOrder? group;
        ISetupStrategy? strategy;

        lock (_lock)
        {
            if (!_byGroupId.TryGetValue(groupOrderId, out var pair)) return;
            group = pair.Group;
            strategy = pair.Strategy;
        }

        await ExitGroupCoreAsync(group, strategy, exitPrice, reason);
    }

    /// <summary>Manually exit a group order (Cancel if pending, Market Close if active/partial).</summary>
    /// <param name="setupId">Setup identifier (e.g. "pullback-mgc").</param>
    /// <param name="exitPrice">Current market price for P&amp;L calculation. 0 = skip trade record.</param>
    /// <param name="reason">Exit reason for the trade record.</param>
    public async Task ExitGroupAsync(string setupId, decimal exitPrice = 0, ExitReason reason = ExitReason.Manual, DateTime? exitTime = null)
    {
        GroupOrder? group;
        ISetupStrategy? strategy;

        lock (_lock)
        {
            if (!_active.TryGetValue(setupId, out var pair)) return;
            group = pair.Group;
            strategy = pair.Strategy;
        }

        await ExitGroupCoreAsync(group, strategy, exitPrice, reason, exitTime);
    }

    private async Task ExitGroupCoreAsync(GroupOrder group, ISetupStrategy? strategy,
        decimal exitPrice, ExitReason reason, DateTime? exitTime = null)
    {
        // Live broker manages exits — don't send cancel/market close for session-end exits.
        // The broker's brackets (targets/stops) handle the trade lifecycle.
        // Keep the group tracked so REST poller continues monitoring.
        // Only allow manual exits (ExitReason.Manual) to send broker orders.
        if (BrokerManagesExits && reason != ExitReason.Manual)
        {
            _log?.LogInformation("[BEH] Skipping broker exit for grp={G} reason={R} — broker manages exits, group stays tracked",
                group.GroupOrderId, reason);
            return;
        }

        // Cancel all working legs on the executor
        foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
        {
            await _executor.CancelOrderAsync(leg.OrderId);
            leg.Status = OrderLegStatus.Canceled;
        }

        var now = exitTime ?? DateTime.UtcNow;

        switch (group.Status)
        {
            case GroupOrderStatus.Pending:
                group.Status = GroupOrderStatus.Canceled;
                group.CompletedAt = now;
                strategy?.SetInTrade(false);
                RemoveGroup(group);
                return;

            case GroupOrderStatus.Active:
                await _executor.PlaceMarketCloseAsync(group.Ticker, group.Direction, group.TotalContracts);
                break;

            case GroupOrderStatus.PartialFilled:
                var remaining = group.TotalContracts - group.PartialContracts;
                await _executor.PlaceMarketCloseAsync(group.Ticker, group.Direction, remaining);
                break;
        }

        // Record the trade with P&L — CompleteGroup handles TradeRecord creation,
        // fires OnTradeCompleted, and removes from _active.
        if (exitPrice > 0 && group.EntryPrice.HasValue)
        {
            await CompleteGroup(group, strategy!, reason, exitPrice, now);
        }
        else
        {
            // Fallback: no price available — just clean up without trade record
            _log?.LogWarning("[BEH] ExitGroupCoreAsync grp={G} — no exit price, trade not recorded",
                group.GroupOrderId);
            group.Status = GroupOrderStatus.Completed;
            group.CompletedAt = DateTime.UtcNow;
            strategy?.SetInTrade(false);
            RemoveGroup(group);
        }
    }

    /// <summary>Force-close all active groups (session boundary).</summary>
    /// <param name="priceResolver">Resolves current price for a ticker. Null = no trade records.</param>
    /// <param name="exitTime">Simulated time for backtest; null = DateTime.UtcNow.</param>
    public async Task ExitAllAsync(Func<string, decimal>? priceResolver = null, DateTime? exitTime = null)
    {
        if (BrokerManagesExits)
        {
            // Live broker: don't send cancel/market close, don't remove groups.
            // Broker's brackets handle exits. Groups stay tracked for REST poller.
            _log?.LogInformation("[BEH] ExitAllAsync — broker manages exits, groups stay tracked");
            return;
        }

        List<(string Id, string Ticker)> setupInfo;
        lock (_lock)
            setupInfo = _active.Select(kv => (kv.Key, kv.Value.Group.Ticker)).ToList();

        foreach (var (id, ticker) in setupInfo)
        {
            var price = priceResolver?.Invoke(ticker) ?? 0m;
            await ExitGroupAsync(id, price, ExitReason.SessionEnd, exitTime);
        }
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
            _log?.LogDebug("[BEH] Entry FILLED grp={G} @ {P}", group.GroupOrderId, evt.FillPrice);
            OnEntryFilled?.Invoke(group);
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

        // Resolve fill price — WSS may not include it
        var tg1FillPrice = evt.FillPrice;
        if (tg1FillPrice is null or 0)
        {
            _log?.LogWarning("[BEH] Tg1 fill missing price in WSS event for order {O} — fetching via REST", evt.OrderId);
            tg1FillPrice = await _executor.GetOrderFillPriceAsync(evt.OrderId);
        }

        // Accrue partial P&L
        if (group.EntryPrice.HasValue && tg1FillPrice is > 0)
        {
            bool isLong = group.Direction == Direction.Long;
            var partialPnl = isLong
                ? (tg1FillPrice.Value - group.EntryPrice.Value) * group.PointValue * group.PartialContracts
                : (group.EntryPrice.Value - tg1FillPrice.Value) * group.PointValue * group.PartialContracts;
            group.AccruedPartialPnl = partialPnl;
        }
        else
        {
            _log?.LogWarning("[BEH] Tg1 FILLED grp={G} but cannot accrue: entryPrice={E} fillPrice={F}",
                group.GroupOrderId, group.EntryPrice, tg1FillPrice);
        }

        // Handle dual-stop bracket: cancel the stop paired with Tg1 (broker OCO
        // should do this, but sometimes doesn't), then switch to Stop2 for remaining.
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null && group.EntryPrice.HasValue)
        {
            var remaining = group.TotalContracts - group.PartialContracts;

            if (!string.IsNullOrEmpty(group.Stop2OrderId))
            {
                // Dual-stop bracket: Stop1 (current) paired with Tg1, Stop2 paired with Tg2.
                // The broker strategy already modifies Stop2 (BE + reduced qty) when Tg1 fills,
                // but sometimes fails to cancel Stop1 via OCO. Safety-cancel it ourselves.
                if (stopLeg.Status == OrderLegStatus.Working)
                {
                    try
                    {
                        await _executor.CancelOrderAsync(stopLeg.OrderId);
                        _log?.LogInformation("[BEH] Tg1 FILLED grp={G} — safety-cancelled Stop1 {O}",
                            group.GroupOrderId, stopLeg.OrderId);
                    }
                    catch (Exception ex)
                    {
                        _log?.LogWarning(ex, "[BEH] Failed to safety-cancel Stop1 {O} (may already be cancelled by OCO)",
                            stopLeg.OrderId);
                    }
                }

                // Switch tracking to Stop2 (already modified by broker strategy)
                stopLeg.OrderId = group.Stop2OrderId;
                stopLeg.Status = OrderLegStatus.Working;
                stopLeg.Quantity = remaining;
                if (group.UseBe) stopLeg.Price = group.EntryPrice.Value;
                group.Stop2OrderId = null; // consumed
                _log?.LogInformation("[BEH] Tg1 FILLED grp={G} — switched to Stop2 {O}, qty={Q}",
                    group.GroupOrderId, stopLeg.OrderId, remaining);
            }
            else
            {
                // Single-stop bracket: just modify the existing stop
                var newStopPrice = group.UseBe ? group.EntryPrice.Value : stopLeg.Price;
                await _executor.ModifyOrderAsync(stopLeg.OrderId, newStopPrice, remaining);
                stopLeg.Quantity = remaining;
                if (group.UseBe) stopLeg.Price = newStopPrice;
                _log?.LogDebug("[BEH] Tg1 FILLED grp={G} — stop {BE} @ {P}, qty→{Q}",
                    group.GroupOrderId, group.UseBe ? "→BE" : "stays",
                    group.UseBe ? group.EntryPrice : stopLeg.Price, remaining);
            }
        }

        OnGroupStateChanged?.Invoke(group);
    }

    private async Task HandleTg2EventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        // Cancel stop leg and update its status on the GroupOrder
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null && stopLeg.Status == OrderLegStatus.Working)
        {
            await _executor.CancelOrderAsync(stopLeg.OrderId);
            stopLeg.Status = OrderLegStatus.Canceled;
        }

        var exitPrice = await ResolveExitFillPriceAsync(evt, "Tg2");
        await CompleteGroup(group, strategy, ExitReason.Target, exitPrice, evt.Timestamp);
    }

    private async Task HandleStopEventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        // Cancel tg1 and tg2 legs and update their status on the GroupOrder
        foreach (var leg in group.Legs.Where(l =>
            (l.LegType == LegType.Tg1 || l.LegType == LegType.Tg2) &&
            l.Status == OrderLegStatus.Working))
        {
            await _executor.CancelOrderAsync(leg.OrderId);
            leg.Status = OrderLegStatus.Canceled;
        }

        var exitPrice = await ResolveExitFillPriceAsync(evt, "Stop");
        await CompleteGroup(group, strategy, ExitReason.Stop, exitPrice, evt.Timestamp);
    }

    /// <summary>
    /// Resolve exit fill price: use WSS event price if available, otherwise fall back to REST API.
    /// Prevents exit @ 0.00 when WSS messages don't include avgFillPrice.
    /// </summary>
    private async Task<decimal> ResolveExitFillPriceAsync(OrderEvent evt, string legLabel)
    {
        if (evt.FillPrice is > 0)
            return evt.FillPrice.Value;

        // WSS didn't include fill price — fetch from REST
        _log?.LogWarning("[BEH] {Leg} fill missing price in WSS event for order {O} — fetching via REST",
            legLabel, evt.OrderId);

        try
        {
            var restPrice = await _executor.GetOrderFillPriceAsync(evt.OrderId);
            if (restPrice is > 0)
            {
                _log?.LogInformation("[BEH] {Leg} fill price resolved via REST: {P} for order {O}",
                    legLabel, restPrice, evt.OrderId);
                return restPrice.Value;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "[BEH] REST fill price lookup failed for {Leg} order {O}", legLabel, evt.OrderId);
        }

        _log?.LogError("[BEH] Could not resolve {Leg} fill price for order {O} — using 0", legLabel, evt.OrderId);
        return 0m;
    }

    private async Task CancelRemainingAndComplete(GroupOrder group, ISetupStrategy strategy, GroupOrderStatus status)
    {
        foreach (var leg in group.Legs.Where(l => l.Status == OrderLegStatus.Working))
        {
            await _executor.CancelOrderAsync(leg.OrderId);
            leg.Status = OrderLegStatus.Canceled;
        }

        group.Status = status;
        group.CompletedAt = DateTime.UtcNow;
        strategy.SetInTrade(false);
        RemoveGroup(group);
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
            var initStop = group.InitialStopPrice > 0 ? group.InitialStopPrice : (stopLeg?.Price ?? 0m);
            var tg1 = group.GetLeg(LegType.Tg1);
            var tg2 = group.GetLeg(LegType.Tg2);

            // Compute total P&L — only subtract partial contracts if partial actually filled
            bool partialFilled = group.AccruedPartialPnl != 0m;

            // Diagnostic: log when Tg1 leg exists but partial didn't fill on a Target exit
            if (!partialFilled && reason == ExitReason.Target && tg1 != null)
                _log?.LogWarning("[BEH] DIAG: Target exit but PartialFilled=false grp={G} tg1Status={S} tg1Fill={F} partialCts={P} accrued={A}",
                    group.GroupOrderId, tg1.Status, tg1.FillPrice, group.PartialContracts, group.AccruedPartialPnl);
            int remaining = partialFilled
                ? group.TotalContracts - group.PartialContracts
                : group.TotalContracts;
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

        RemoveGroup(group);

        return Task.CompletedTask;
    }
}
