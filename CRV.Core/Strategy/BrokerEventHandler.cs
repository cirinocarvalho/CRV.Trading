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

    /// <summary>Commission per side per contract (e.g. $0.59). Applied as 2 × contracts × rate.</summary>
    public decimal CommissionPerSide { get; set; }

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
        // BrokerManagesExits only blocks mid-trade interference (e.g. engine restart).
        // Always allow: manual exits, session end/cutoff, and pending entry cancellation.
        if (BrokerManagesExits
            && reason != ExitReason.Manual
            && reason != ExitReason.SessionEnd
            && group.Status != GroupOrderStatus.Pending)
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

        // Cancel Stop2 if it was never promoted into Legs (i.e. Tg1 never filled).
        // Stop2OrderId is set at entry and moved to the Stop leg only on Tg1 fill.
        // Without this, Stop2 stays Working at the broker after a cutoff/session-end exit.
        if (!string.IsNullOrEmpty(group.Stop2OrderId))
        {
            await _executor.CancelOrderAsync(group.Stop2OrderId);
            group.Stop2OrderId = null;
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
            {
                var fillPrice = await _executor.PlaceMarketCloseAsync(group.Ticker, group.Direction, group.TotalContracts);
                if (fillPrice > 0) exitPrice = fillPrice;
                break;
            }

            case GroupOrderStatus.PartialFilled:
            {
                var remaining = group.TotalContracts - group.PartialContracts;
                var fillPrice = await _executor.PlaceMarketCloseAsync(group.Ticker, group.Direction, remaining);
                if (fillPrice > 0) exitPrice = fillPrice;
                break;
            }
        }

        // Record the trade with P&L — CompleteGroup handles TradeRecord creation,
        // fires OnTradeCompleted, and removes from _active.
        if (group.EntryPrice.HasValue)
        {
            // If broker fill price was unavailable, fall back to the price passed by the caller
            // (bar close / tick price). If even that is 0, record at entry price so the trade
            // is never silently dropped — P&L can be corrected manually later.
            var recordPrice = exitPrice > 0 ? exitPrice : group.EntryPrice.Value;
            if (exitPrice <= 0)
                _log?.LogWarning("[BEH] ExitGroupCoreAsync grp={G} — no exit price, recording trade at entry price {P}",
                    group.GroupOrderId, recordPrice);
            await CompleteGroup(group, strategy!, reason, recordPrice, now);
        }
        else
        {
            // No entry price at all — group never filled, just clean up
            _log?.LogWarning("[BEH] ExitGroupCoreAsync grp={G} — no entry price, trade not recorded",
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

    private async Task HandleEntryEventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status == OrderLegStatus.Filled)
        {
            group.Status = GroupOrderStatus.Active;

            // WSS fill events may not include the fill price — fetch via REST as fallback
            var fillPrice = evt.FillPrice;
            if (fillPrice is null or 0)
            {
                _log?.LogWarning("[BEH] Entry fill missing price in WSS event for order {O} — fetching via REST", evt.OrderId);
                fillPrice = await _executor.GetOrderFillPriceAsync(evt.OrderId);
            }

            group.EntryPrice = fillPrice;
            strategy.SetInTrade(true);
            _log?.LogDebug("[BEH] Entry FILLED grp={G} @ {P}", group.GroupOrderId, group.EntryPrice);
            OnEntryFilled?.Invoke(group);
        }
        else if (evt.Status == OrderLegStatus.Rejected || evt.Status == OrderLegStatus.Canceled)
        {
            _log?.LogWarning("[BEH] Entry {S} grp={G}", evt.Status, group.GroupOrderId);
            await CancelRemainingAndComplete(group, strategy, GroupOrderStatus.Canceled);
        }
    }

    private async Task HandleTg1EventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        group.Status = GroupOrderStatus.PartialFilled;

        // Always verify Tg1 fill price via REST — never trust WSS
        decimal? tg1FillPrice = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                tg1FillPrice = await _executor.GetOrderFillPriceAsync(evt.OrderId);
                if (tg1FillPrice is > 0) break;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "[BEH] Tg1 REST fill price lookup failed (attempt {A}) for order {O}", attempt + 1, evt.OrderId);
            }
            if (attempt < 2) await Task.Delay(500);
        }
        // Final fallback: WSS price or Tg1 leg price
        if (tg1FillPrice is null or 0)
        {
            var tg1Leg = group.GetLeg(LegType.Tg1);
            tg1FillPrice = evt.FillPrice ?? tg1Leg?.Price;
            _log?.LogWarning("[BEH] Tg1 fill price unresolved via REST — using fallback {P} for order {O}",
                tg1FillPrice, evt.OrderId);
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

        // Update stop tracking and move to BE at broker if needed.
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null)
        {
            var remaining = group.TotalContracts - group.PartialContracts;

            if (!string.IsNullOrEmpty(group.Stop2OrderId))
            {
                // Dual-stop bracket: switch tracking to Stop2 (broker handles Stop1 cancel + Stop2 BE)
                stopLeg.OrderId = group.Stop2OrderId;
                stopLeg.Status = OrderLegStatus.Working;
                group.Stop2OrderId = null;
            }
            stopLeg.Quantity = remaining;
            bool hasAutoTrail = group.AutoTrailStopLoss.HasValue;
            if (group.UseBe && group.EntryPrice.HasValue && !hasAutoTrail)
            {
                stopLeg.Price = group.EntryPrice.Value;
                // Push the BE price into the executor so that:
                //   - Backtest: EvaluateFillsAsync uses the new StopPrice (not original stop)
                //   - Live (dual-stop bracket): Stop2 is already at BE — this is a harmless no-op
                await _executor.ModifyOrderAsync(stopLeg.OrderId, group.EntryPrice.Value, remaining);
            }
            _log?.LogInformation("[BEH] Tg1 FILLED grp={G} — tracking Stop {O}, qty={Q}{BE}",
                group.GroupOrderId, stopLeg.OrderId, remaining,
                hasAutoTrail ? " AutoTrail active (BE skipped)" : group.UseBe ? $" BE={group.EntryPrice}" : "");
        }

        OnGroupStateChanged?.Invoke(group);
    }

    private async Task HandleTg2EventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        // Cancel stop via executor and mark in memory — broker OCO handles the actual cancel
        // for live, but executor must still be notified for tracking/test purposes.
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null && stopLeg.Status == OrderLegStatus.Working)
        {
            await _executor.CancelOrderAsync(stopLeg.OrderId);
            stopLeg.Status = OrderLegStatus.Canceled;
        }

        var tg2Leg = group.GetLeg(LegType.Tg2);
        var exitPrice = await ResolveExitFillPriceAsync(evt, "Tg2", fallbackPrice: tg2Leg?.Price ?? 0m);
        await CompleteGroup(group, strategy, ExitReason.Target, exitPrice, evt.Timestamp);
    }

    private async Task HandleStopEventAsync(GroupOrder group, ISetupStrategy strategy, OrderEvent evt)
    {
        if (evt.Status != OrderLegStatus.Filled) return;

        // If this fill is from Stop2 (BE stop) but Tg1 fill hasn't been processed yet,
        // force-process Tg1 first so partial P&L accrues and stop tracking switches correctly.
        // This happens when WSS delivers Stop2 fill before the poll delivers Tg1 fill.
        var stopLeg = group.GetLeg(LegType.Stop);
        if (stopLeg != null && evt.OrderId != stopLeg.OrderId)
        {
            var tg1Leg = group.GetLeg(LegType.Tg1);
            if (tg1Leg != null && tg1Leg.Status != OrderLegStatus.Filled)
            {
                _log?.LogWarning("[BEH] Stop2 fill arrived before Tg1 fill for grp={G} — force-processing Tg1 at {P}",
                    group.GroupOrderId, tg1Leg.Price);
                tg1Leg.Status = OrderLegStatus.Filled;
                tg1Leg.FillPrice ??= tg1Leg.Price;
                tg1Leg.FillTime = evt.Timestamp;
                await HandleTg1EventAsync(group, strategy, new OrderEvent(
                    group.GroupOrderId, tg1Leg.OrderId, LegType.Tg1,
                    OrderLegStatus.Filled, tg1Leg.FillPrice, tg1Leg.Quantity, null, null, evt.Timestamp));
                // Re-read stop leg — HandleTg1EventAsync may have switched it to Stop2
                stopLeg = group.GetLeg(LegType.Stop);
            }
        }

        // Cancel tg1/tg2 via executor and mark in memory — broker OCO handles the actual cancel
        // for live, but executor must still be notified for tracking/test purposes.
        foreach (var leg in group.Legs.Where(l =>
            (l.LegType == LegType.Tg1 || l.LegType == LegType.Tg2) &&
            l.Status == OrderLegStatus.Working).ToList())
        {
            await _executor.CancelOrderAsync(leg.OrderId);
            leg.Status = OrderLegStatus.Canceled;
        }

        // Contextual fallback: use entry price only if BE was actually activated (partial filled),
        // not just because UseBe is enabled in config. Without a partial fill, stop is still at
        // its original price.
        bool beActivated = group.Status == GroupOrderStatus.PartialFilled
            || group.AccruedPartialPnl != 0m;
        var contextualFallback = (beActivated && group.EntryPrice.HasValue)
            ? group.EntryPrice.Value
            : stopLeg?.Price ?? 0m;
        var exitPrice = await ResolveExitFillPriceAsync(evt, "Stop", fallbackPrice: contextualFallback);
        await CompleteGroup(group, strategy, ExitReason.Stop, exitPrice, evt.Timestamp);
    }

    /// <summary>
    /// Resolve exit fill price: always verify via REST /fill/deps.
    /// Never trust WSS fill prices — they are unreliable for bracket stops.
    /// Falls back to contextual price only after retries exhaust.
    /// </summary>
    private async Task<decimal> ResolveExitFillPriceAsync(OrderEvent evt, string legLabel, decimal fallbackPrice = 0m)
    {
        // Skip REST entirely for mock/backtest orders (non-numeric IDs like "abc123-t2")
        // Short IDs (1-3 chars like "s1","t1") are test fakes — don't skip, let REST resolve.
        bool isNonNumeric = !long.TryParse(evt.OrderId?.Split('-')[0], out _);
        bool isMockOrder = isNonNumeric && (evt.OrderId?.Length ?? 0) > 3;
        if (isMockOrder && evt.FillPrice is > 0)
            return evt.FillPrice.Value;
        if (isMockOrder && fallbackPrice > 0)
            return fallbackPrice;

        // Always try REST first — even if WSS provided a price
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var restPrice = await _executor.GetOrderFillPriceAsync(evt.OrderId);
                if (restPrice is > 0)
                {
                    _log?.LogInformation("[BEH] {Leg} fill price verified via REST: {P} for order {O} (attempt {A})",
                        legLabel, restPrice, evt.OrderId, attempt + 1);
                    return restPrice.Value;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "[BEH] REST fill price lookup failed for {Leg} order {O} (attempt {A})",
                    legLabel, evt.OrderId, attempt + 1);
            }

            if (attempt < 2) await Task.Delay(500);
        }

        // REST exhausted — use contextual fallback
        if (fallbackPrice > 0)
        {
            _log?.LogWarning("[BEH] {Leg} fill price unresolved after 3 REST attempts — using fallback {P} for order {O}",
                legLabel, fallbackPrice, evt.OrderId);
            return fallbackPrice;
        }

        // Last resort: use WSS price if available
        if (evt.FillPrice is > 0)
        {
            _log?.LogWarning("[BEH] {Leg} fill price using WSS fallback {P} for order {O} (REST exhausted, no contextual fallback)",
                legLabel, evt.FillPrice.Value, evt.OrderId);
            return evt.FillPrice.Value;
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

        // Cancel Stop2 if it was never promoted into Legs (Tg1 never filled)
        if (!string.IsNullOrEmpty(group.Stop2OrderId))
        {
            await _executor.CancelOrderAsync(group.Stop2OrderId);
            group.Stop2OrderId = null;
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

        // Build trade record — fall back to entry leg limit price if fill price not yet set
        var entryLeg = group.GetLeg(LegType.Entry);
        var tradeEntry = group.EntryPrice ?? entryLeg?.FillPrice ?? entryLeg?.Price;
        if (tradeEntry.HasValue && tradeEntry.Value > 0)
        {
            if (!group.EntryPrice.HasValue)
                _log?.LogWarning("[BEH] EntryPrice null at CompleteGroup — using entry leg price {P} for grp={G}",
                    tradeEntry.Value, group.GroupOrderId);
            bool isLong = group.Direction == Direction.Long;
            var entry = tradeEntry.Value;
            var stopLeg = group.GetLeg(LegType.Stop);
            var initStop = group.InitialStopPrice > 0 ? group.InitialStopPrice : (stopLeg?.Price ?? 0m);
            var tg1 = group.GetLeg(LegType.Tg1);
            var tg2 = group.GetLeg(LegType.Tg2);

            // Detect partial fill from Tg1 leg status or group status — not just AccruedPartialPnl
            bool partialFilled = group.Status == GroupOrderStatus.PartialFilled
                || tg1?.Status == OrderLegStatus.Filled
                || group.AccruedPartialPnl != 0m;

            // Recalculate partial P&L if Tg1 filled but accrued wasn't set
            decimal partialPnl = group.AccruedPartialPnl;
            if (partialFilled && partialPnl == 0m && group.PartialContracts > 0)
            {
                var tg1Fill = tg1?.FillPrice ?? tg1?.Price ?? 0m;
                if (tg1Fill > 0)
                {
                    partialPnl = isLong
                        ? (tg1Fill - entry) * group.PointValue * group.PartialContracts
                        : (entry - tg1Fill) * group.PointValue * group.PartialContracts;
                    _log?.LogWarning("[BEH] Recalculated partial P&L for grp={G}: tg1Fill={F} partialPnl={P}",
                        group.GroupOrderId, tg1Fill, partialPnl);
                }
            }

            int remaining = partialFilled
                ? group.TotalContracts - group.PartialContracts
                : group.TotalContracts;
            decimal exitPnl = isLong
                ? (exitPrice - entry) * group.PointValue * remaining
                : (entry - exitPrice) * group.PointValue * remaining;
            decimal totalPnl = exitPnl + partialPnl;

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
                PartialFilled = partialFilled,
                PartialPrice = tg1?.FillPrice ?? tg1?.Price ?? 0m,
                GrossPnl = totalPnl,
                Commission = CommissionPerSide * 2 * group.TotalContracts,
                NetPnl = totalPnl - (CommissionPerSide * 2 * group.TotalContracts),
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
