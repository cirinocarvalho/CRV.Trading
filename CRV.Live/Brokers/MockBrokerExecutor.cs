namespace CRV.Live.Brokers;

using CRV.Core.Data;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ── Order state model ─────────────────────────────────────────

public class MockOrder
{
    public string    OrderId    { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string    Symbol     { get; set; } = "";
    public string    Action     { get; set; } = "";   // BUY | SELL
    public int       Quantity   { get; set; }
    public decimal?  LimitPrice { get; set; }
    public decimal?  StopPrice  { get; set; }
    public string    Status     { get; set; } = "WORKING"; // WORKING | FILLED | CANCELED
    public decimal?  FillPrice  { get; set; }
    public DateTime  PlacedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? FilledAt   { get; set; }
    public string?   OcoGroupId { get; set; }
    public string?   Direction  { get; set; }  // "Long" | "Short"
    public string?   SetupId    { get; set; }  // "A" | "B" | …

    public string OrderType => (LimitPrice.HasValue, StopPrice.HasValue) switch
    {
        (true,  false) => "LIMIT",
        (false, true)  => "STOP",
        (true,  true)  => "STOP_LIMIT",
        _              => "MARKET"
    };

    /// <summary>Convert to a persistable OrderRecord.</summary>
    public OrderRecord ToRecord() => new()
    {
        OrderId    = OrderId,
        Broker     = "Mock",
        Symbol     = Symbol,
        Action     = Action,
        Quantity   = Quantity,
        LimitPrice = LimitPrice,
        StopPrice  = StopPrice,
        Status     = Status,
        FillPrice  = FillPrice,
        PlacedAt   = PlacedAt,
        FilledAt   = FilledAt,
        OcoGroupId = OcoGroupId,
        Direction  = Direction,
        SetupId    = SetupId,
    };
}

// ── Executor ──────────────────────────────────────────────────

/// <summary>
/// Simulated broker — logs all signals, maintains an in-memory OCO order book,
/// and fills orders when <see cref="EvaluateFills"/> is called with a realtime price.
/// All public members are thread-safe via an internal lock.
/// </summary>
public class MockBrokerExecutor : IOrderExecutor
{
    private readonly ILogger              _log;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly List<MockOrder>      _orders = new();
    private readonly object               _lock   = new();

    public MockBrokerExecutor(ILogger<MockBrokerExecutor> log, IServiceScopeFactory scopeFactory)
    {
        _log          = log;
        _scopeFactory = scopeFactory;
    }

    // ── IOrderExecutor ────────────────────────────────────────

    public Task<decimal?> OnEntrySignalAsync(EntrySignal sig)
    {
        _log.LogInformation("[MOCK] ENTRY {OT} {D} {Q}x Setup={S} @ {E} Stop={St} Tgt={T}",
            sig.OrderType, sig.Direction, sig.TotalContracts, sig.Setup, sig.Entry, sig.Stop, sig.Tg2Price);

        var ocoId  = Guid.NewGuid().ToString("N")[..8];
        bool isLong = sig.Direction == Direction.Long;
        bool isLimit = sig.OrderType == "Limit";
        var symbol  = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : sig.Setup.ToString();
        var newOrders = new List<MockOrder>(3);

        lock (_lock)
        {
            var entry = new MockOrder
            {
                Symbol = symbol, Action = isLong ? "BUY" : "SELL",
                Quantity = sig.TotalContracts, OcoGroupId = ocoId,
                Direction = sig.Direction.ToString(),
                SetupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString(),
                // Limit: place as WORKING — EvaluateFills will fill when price reaches level.
                // Market: fill immediately at the signal price.
                LimitPrice = isLimit ? sig.Entry : null,
                Status     = isLimit ? "WORKING" : "FILLED",
                FillPrice  = isLimit ? null : sig.Entry,
                FilledAt   = isLimit ? null : DateTime.UtcNow,
            };
            var setupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString();
            var stop = new MockOrder
            {
                Symbol = symbol, Action = isLong ? "SELL" : "BUY",
                Quantity = sig.TotalContracts, StopPrice = sig.Stop, OcoGroupId = ocoId,
                SetupId = setupId
            };
            var target = new MockOrder
            {
                Symbol = symbol, Action = isLong ? "SELL" : "BUY",
                Quantity = sig.TotalContracts, LimitPrice = sig.Tg2Price, OcoGroupId = ocoId,
                SetupId = setupId
            };
            _orders.Add(entry);  newOrders.Add(entry);
            // For limit orders, stop/target remain WORKING but won't activate until entry fills
            _orders.Add(stop);   newOrders.Add(stop);
            _orders.Add(target); newOrders.Add(target);
        }
        PersistNewOrdersAsync(newOrders);
        // Limit: return null (not filled yet); Market: return entry price
        return Task.FromResult<decimal?>(isLimit ? null : sig.Entry);
    }

    public Task OnLevelsAdjustedAsync(string setupId, decimal newStop, decimal newTarget, int contracts)
    {
        _log.LogInformation("[MOCK] LEVELS_ADJUSTED Setup={S} Stop={St} Tgt={T} Qty={Q}",
            setupId, newStop, newTarget, contracts);
        var setupSymbol = setupId;
        lock (_lock)
        {
            foreach (var o in _orders.Where(o =>
                o.Symbol == setupSymbol && o.Status == "WORKING"))
            {
                if (o.StopPrice.HasValue)  o.StopPrice  = newStop;
                if (o.LimitPrice.HasValue) o.LimitPrice = newTarget;
            }
        }
        return Task.CompletedTask;
    }

    // ── Fill simulation ───────────────────────────────────────

    /// <summary>
    /// Evaluates all WORKING orders against the latest market price and fills
    /// any that have crossed their trigger level. OCO partners of a filled order
    /// are immediately CANCELED. Thread-safe.
    /// </summary>
    /// <remarks>
    /// All WORKING orders are evaluated regardless of symbol — correct for a
    /// single-instrument executor where every order is for the same instrument.
    /// </remarks>
    public void EvaluateFills(decimal price, DateTime utcNow)
    {
        if (price <= 0) return;
        var filledIds   = new List<string>();
        var canceledIds = new List<string>();

        lock (_lock)
        {
            var working = _orders.Where(o => o.Status == "WORKING").ToList();
            foreach (var o in working)
            {
                if (o.Status != "WORKING") continue;

                bool fills = o.Action == "BUY"
                    ? (o.StopPrice.HasValue  && price >= o.StopPrice)
                   || (o.LimitPrice.HasValue && price <= o.LimitPrice)
                    : (o.StopPrice.HasValue  && price <= o.StopPrice)
                   || (o.LimitPrice.HasValue && price >= o.LimitPrice);

                if (!fills) continue;

                o.Status    = "FILLED";
                o.FillPrice = price;
                o.FilledAt  = utcNow;
                filledIds.Add(o.OrderId);
                _log.LogInformation("[MOCK FILL] {A} {Q}x @ {P} (order {Id})",
                    o.Action, o.Quantity, price, o.OrderId);

                if (o.OcoGroupId != null)
                {
                    foreach (var peer in _orders.Where(p =>
                        p.OcoGroupId == o.OcoGroupId &&
                        p.OrderId    != o.OrderId    &&
                        p.Status     == "WORKING"))
                    {
                        peer.Status = "CANCELED";
                        canceledIds.Add(peer.OrderId);
                        _log.LogDebug("[MOCK OCO] Canceled partner {Id}", peer.OrderId);
                    }
                }
            }
        }

        if (filledIds.Count > 0 || canceledIds.Count > 0)
            PersistFillsAsync(filledIds, canceledIds, price, utcNow);
    }

    // ── Query / mutation helpers ───────────────────────────────

    /// <summary>Returns a snapshot copy of all orders (any status).</summary>
    public List<MockOrder> GetOrders()
    {
        lock (_lock) return _orders.ToList();
    }

    /// <summary>Cancel a single WORKING order by ID. Awaits DB persist so callers see the update.</summary>
    public async Task CancelOrderAsync(string orderId)
    {
        bool canceled = false;
        lock (_lock)
        {
            var o = _orders.FirstOrDefault(o => o.OrderId == orderId);
            if (o == null)
            {
                _log.LogWarning("[MOCK] CancelOrder: order {Id} not found", orderId);
                return;
            }
            if (o.Status != "WORKING")
            {
                _log.LogWarning("[MOCK] CancelOrder: order {Id} is already {Status}, cannot cancel",
                    orderId, o.Status);
                return;
            }
            o.Status = "CANCELED";
            canceled = true;
        }
        if (canceled)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var row = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId);
                if (row != null) { row.Status = "CANCELED"; await db.SaveChangesAsync(); }
            }
            catch (Exception ex) { _log.LogError(ex, "[MOCK] Failed to persist cancel for {Id}", orderId); }
        }
    }

    /// <summary>
    /// Test helper: inject a pre-constructed order directly into the order book.
    /// </summary>
    public void SimulateOrder(string symbol, string action, int qty,
        decimal? limitPrice, decimal? stopPrice, string? ocoGroupId = null)
    {
        lock (_lock)
        {
            _orders.Add(new MockOrder
            {
                Symbol     = symbol,
                Action     = action,
                Quantity   = qty,
                LimitPrice = limitPrice,
                StopPrice  = stopPrice,
                OcoGroupId = ocoGroupId
            });
        }
    }

    // ── DB persistence (fire-and-forget, off hot path) ────────

    private void PersistNewOrdersAsync(List<MockOrder> orders)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                db.Orders.AddRange(orders.Select(o => o.ToRecord()));
                await db.SaveChangesAsync();
            }
            catch (Exception ex) { _log.LogError(ex, "[MOCK] Failed to persist new orders"); }
        });
    }

    private void PersistUpdateAsync(string orderId, Action<OrderRecord> mutate)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var row = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId);
                if (row != null) { mutate(row); await db.SaveChangesAsync(); }
            }
            catch (Exception ex) { _log.LogError(ex, "[MOCK] Failed to update order {Id}", orderId); }
        });
    }

    private void PersistExitAsync(List<string> canceledIds, MockOrder exitOrder)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                foreach (var id in canceledIds)
                {
                    var row = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == id);
                    if (row != null) row.Status = "CANCELED";
                }
                db.Orders.Add(exitOrder.ToRecord());
                await db.SaveChangesAsync();
            }
            catch (Exception ex) { _log.LogError(ex, "[MOCK] Failed to persist exit orders"); }
        });
    }

    private void PersistFillsAsync(List<string> filledIds, List<string> canceledIds,
        decimal fillPrice, DateTime filledAt)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                foreach (var id in filledIds)
                {
                    var row = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == id);
                    if (row != null) { row.Status = "FILLED"; row.FillPrice = fillPrice; row.FilledAt = filledAt; }
                }
                foreach (var id in canceledIds)
                {
                    var row = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == id);
                    if (row != null) row.Status = "CANCELED";
                }
                await db.SaveChangesAsync();
            }
            catch (Exception ex) { _log.LogError(ex, "[MOCK] Failed to persist fills"); }
        });
    }
}

// ── New group-order executor ─────────────────────────────────

/// <summary>
/// New mock broker — dumb order book that emits events via MockEventStream.
/// No OCO auto-cancel — BrokerEventHandler manages all leg transitions.
/// </summary>
public class MockGroupOrderExecutor : IGroupOrderExecutor
{
    private readonly MockEventStream _stream;
    private readonly ILogger _log;
    private readonly Dictionary<string, Dictionary<string, MockLegState>> _ordersByGroup = new();
    private readonly object _lock = new();

    private class MockLegState
    {
        public string OrderId { get; set; } = "";
        public string GroupOrderId { get; set; } = "";
        public LegType LegType { get; set; }
        public string Action { get; set; } = "";
        public int Quantity { get; set; }
        public decimal? LimitPrice { get; set; }
        public decimal? StopPrice { get; set; }
        public string Status { get; set; } = "WORKING";
    }

    public MockGroupOrderExecutor(MockEventStream stream, ILogger<MockGroupOrderExecutor> log)
    {
        _stream = stream;
        _log = log;
    }

    public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
    {
        var groupId = Guid.NewGuid().ToString("N")[..8];
        bool isLong = sig.Direction == Direction.Long;
        var exitAction = isLong ? "SELL" : "BUY";
        var entryAction = isLong ? "BUY" : "SELL";
        var ticker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : sig.Setup.ToString();
        var setupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString();
        var partialCts = sig.PartialContracts > 0 ? sig.PartialContracts : sig.TotalContracts / 2;
        var remainCts = sig.TotalContracts - partialCts;

        var group = new GroupOrder
        {
            GroupOrderId = groupId,
            SetupId = setupId,
            Ticker = ticker,
            Direction = sig.Direction,
            TotalContracts = sig.TotalContracts,
            PartialContracts = partialCts,
            Status = GroupOrderStatus.Pending,
            Broker = "Mock",
        };

        var entryLeg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-e", LegType = LegType.Entry, OrderType = sig.OrderType, Action = entryAction, Quantity = sig.TotalContracts, Price = sig.Entry };
        var tg1Leg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-t1", LegType = LegType.Tg1, OrderType = "Limit", Action = exitAction, Quantity = partialCts, Price = sig.Tg1Price };
        var tg2Leg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-t2", LegType = LegType.Tg2, OrderType = "Limit", Action = exitAction, Quantity = remainCts, Price = sig.Tg2Price };
        var stopLeg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-s", LegType = LegType.Stop, OrderType = "Stop", Action = exitAction, Quantity = sig.TotalContracts, Price = sig.Stop };
        group.Legs.AddRange(new[] { entryLeg, tg1Leg, tg2Leg, stopLeg });

        // Register in internal order book
        var legs = new Dictionary<string, MockLegState>
        {
            [entryLeg.OrderId] = new() { OrderId = entryLeg.OrderId, GroupOrderId = groupId, LegType = LegType.Entry, Action = entryAction, Quantity = sig.TotalContracts, LimitPrice = sig.OrderType == "Limit" ? sig.Entry : null },
            [tg1Leg.OrderId] = new() { OrderId = tg1Leg.OrderId, GroupOrderId = groupId, LegType = LegType.Tg1, Action = exitAction, Quantity = partialCts, LimitPrice = sig.Tg1Price },
            [tg2Leg.OrderId] = new() { OrderId = tg2Leg.OrderId, GroupOrderId = groupId, LegType = LegType.Tg2, Action = exitAction, Quantity = remainCts, LimitPrice = sig.Tg2Price },
            [stopLeg.OrderId] = new() { OrderId = stopLeg.OrderId, GroupOrderId = groupId, LegType = LegType.Stop, Action = exitAction, Quantity = sig.TotalContracts, StopPrice = sig.Stop },
        };

        lock (_lock)
            _ordersByGroup[groupId] = legs;

        // Market entry: fill immediately
        if (sig.OrderType != "Limit")
        {
            legs[entryLeg.OrderId].Status = "FILLED";
            _stream.PushEvent(new OrderEvent(groupId, entryLeg.OrderId, LegType.Entry,
                OrderLegStatus.Filled, sig.Entry, sig.TotalContracts, null, null, DateTime.UtcNow));
        }

        _log.LogInformation("[MOCK-GRP] Group {G} placed: {D} {Q}x @ {E}", groupId, sig.Direction, sig.TotalContracts, sig.Entry);
        return Task.FromResult<GroupOrder?>(group);
    }

    public Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
    {
        lock (_lock)
        {
            foreach (var (groupId, legs) in _ordersByGroup)
            {
                if (legs.TryGetValue(orderId, out var leg))
                {
                    if (newPrice.HasValue)
                    {
                        if (leg.StopPrice.HasValue) leg.StopPrice = newPrice;
                        else leg.LimitPrice = newPrice;
                    }
                    if (newQty.HasValue) leg.Quantity = newQty.Value;

                    _stream.PushEvent(new OrderEvent(groupId, orderId, leg.LegType,
                        OrderLegStatus.Modified, null, null, newPrice, newQty, DateTime.UtcNow));
                    return Task.CompletedTask;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(string orderId)
    {
        lock (_lock)
        {
            foreach (var (groupId, legs) in _ordersByGroup)
            {
                if (legs.TryGetValue(orderId, out var leg) && leg.Status == "WORKING")
                {
                    leg.Status = "CANCELED";
                    _stream.PushEvent(new OrderEvent(groupId, orderId, leg.LegType,
                        OrderLegStatus.Canceled, null, null, null, null, DateTime.UtcNow));
                    return Task.CompletedTask;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
    {
        // In mock mode, market close is a no-op — BrokerEventHandler already manages
        // the group state transition. In live brokers this actually places a market order.
        // We log it for visibility in mock testing.
        _log.LogInformation("[MOCK-GRP] Market close {D} {Q}x {T} (simulated — position flattened)", direction, qty, ticker);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Evaluate fills for all WORKING orders against current price.
    /// Called on each tick. Emits OrderEvent for each fill — NO OCO auto-cancel.
    /// BrokerEventHandler handles all leg management.
    /// </summary>
    public void EvaluateFills(decimal price, DateTime utcNow)
    {
        if (price <= 0) return;

        lock (_lock)
        {
            foreach (var (groupId, legs) in _ordersByGroup)
            {
                foreach (var leg in legs.Values.Where(l => l.Status == "WORKING").ToList())
                {
                    bool fills = false;

                    if (leg.Action == "BUY")
                    {
                        fills = (leg.StopPrice.HasValue && price >= leg.StopPrice)
                             || (leg.LimitPrice.HasValue && price <= leg.LimitPrice);
                    }
                    else // SELL
                    {
                        fills = (leg.StopPrice.HasValue && price <= leg.StopPrice)
                             || (leg.LimitPrice.HasValue && price >= leg.LimitPrice);
                    }

                    if (!fills) continue;

                    leg.Status = "FILLED";
                    _stream.PushEvent(new OrderEvent(groupId, leg.OrderId, leg.LegType,
                        OrderLegStatus.Filled, price, leg.Quantity, null, null, utcNow));

                    _log.LogInformation("[MOCK-GRP FILL] {A} {Q}x @ {P} (order {Id})",
                        leg.Action, leg.Quantity, price, leg.OrderId);
                }
            }
        }
    }
}
