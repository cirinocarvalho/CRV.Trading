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
            sig.OrderType, sig.Direction, sig.Contracts, sig.Setup, sig.Entry, sig.Stop, sig.Target);

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
                Quantity = sig.Contracts, OcoGroupId = ocoId,
                Direction = sig.Direction.ToString(),
                SetupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString(),
                // Limit: place as WORKING with LimitPrice; Market: fill immediately
                LimitPrice = isLimit ? sig.Entry : null,
                Status     = isLimit ? "WORKING" : "FILLED",
                FillPrice  = isLimit ? null : sig.Entry,
                FilledAt   = isLimit ? null : DateTime.UtcNow,
            };
            var setupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString();
            var stop = new MockOrder
            {
                Symbol = symbol, Action = isLong ? "SELL" : "BUY",
                Quantity = sig.Contracts, StopPrice = sig.Stop, OcoGroupId = ocoId,
                SetupId = setupId
            };
            var target = new MockOrder
            {
                Symbol = symbol, Action = isLong ? "SELL" : "BUY",
                Quantity = sig.Contracts, LimitPrice = sig.Target, OcoGroupId = ocoId,
                SetupId = setupId
            };
            _orders.Add(entry);  newOrders.Add(entry);
            // For limit orders, stop/target remain WORKING but won't activate until entry fills
            _orders.Add(stop);   newOrders.Add(stop);
            _orders.Add(target); newOrders.Add(target);
        }
        PersistNewOrdersAsync(newOrders);
        // For limit orders, return null (no fill yet); for market, return the entry price
        return Task.FromResult<decimal?>(isLimit ? null : sig.Entry);
    }

    public Task OnPartialSignalAsync(PartialSignal sig)
    {
        _log.LogInformation("[MOCK] PARTIAL Setup={S} {Q}ct @ {P}",
            sig.Setup, sig.ContractsExited, sig.PartialPrice);
        return Task.CompletedTask;
    }

    public Task OnBESignalAsync(BESignal sig)
    {
        _log.LogInformation("[MOCK] MOVE_BE Setup={S} → {P}", sig.Setup, sig.NewStop);
        string? updatedId = null;
        lock (_lock)
        {
            // Find the entry order for this setup to get the OcoGroupId
            var setupSymbol = sig.Setup.ToString();
            var entryOrder = _orders.LastOrDefault(o =>
                (o.Symbol == setupSymbol || o.Direction != null) &&
                o.Status == "FILLED" && o.FillPrice.HasValue &&
                o.OcoGroupId != null &&
                o.Symbol == setupSymbol);
            // Fallback: match by symbol for ticker-based orders
            entryOrder ??= _orders.LastOrDefault(o =>
                o.Status == "FILLED" && o.FillPrice.HasValue &&
                o.OcoGroupId != null && o.Direction != null);

            var ocoGroup = entryOrder?.OcoGroupId;
            var stopOrder = ocoGroup != null
                ? _orders.FirstOrDefault(o =>
                    o.OcoGroupId == ocoGroup &&
                    o.Status == "WORKING" &&
                    o.StopPrice.HasValue)
                : _orders.FirstOrDefault(o =>
                    o.Symbol == setupSymbol &&
                    o.Status == "WORKING" &&
                    o.StopPrice.HasValue &&
                    o.OcoGroupId != null);
            if (stopOrder != null)
            {
                stopOrder.StopPrice = sig.NewStop;
                updatedId = stopOrder.OrderId;
            }
        }
        if (updatedId != null)
            PersistUpdateAsync(updatedId, o => o.StopPrice = sig.NewStop);
        return Task.CompletedTask;
    }

    public Task OnExitSignalAsync(ExitSignal sig)
    {
        _log.LogInformation("[MOCK] EXIT Setup={S} {R} @ {P} {Q}ct",
            sig.Setup, sig.Reason, sig.ExitPrice, sig.Contracts);

        var canceledIds = new List<string>();
        MockOrder exitOrder;

        lock (_lock)
        {
            foreach (var o in _orders.Where(o => o.Status == "WORKING"))
            {
                o.Status = "CANCELED";
                canceledIds.Add(o.OrderId);
            }
            var exitSymbol = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : sig.Setup.ToString();
            var entryAction = _orders.FirstOrDefault(o =>
                (o.Symbol == exitSymbol || o.Symbol == sig.Setup.ToString()) && o.Status == "FILLED" && o.FillPrice.HasValue)?.Action;
            exitOrder = new MockOrder
            {
                Symbol    = exitSymbol,
                Action    = entryAction == "BUY" ? "SELL" : "BUY",
                Quantity  = sig.Contracts,
                Status    = "FILLED",
                FillPrice = sig.ExitPrice,
                PlacedAt  = DateTime.UtcNow,
                FilledAt  = DateTime.UtcNow,
            };
            _orders.Add(exitOrder);
        }
        PersistExitAsync(canceledIds, exitOrder);
        return Task.CompletedTask;
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
