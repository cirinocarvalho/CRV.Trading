namespace CRV.Live.Brokers;

using CRV.Core.Interfaces;
using CRV.Core.Models;
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

    public string OrderType => (LimitPrice.HasValue, StopPrice.HasValue) switch
    {
        (true,  false) => "LIMIT",
        (false, true)  => "STOP",
        (true,  true)  => "STOP_LIMIT",
        _              => "MARKET"
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
    private readonly ILogger   _log;
    private readonly List<MockOrder> _orders = new();
    private readonly object    _lock   = new();

    public MockBrokerExecutor(ILogger<MockBrokerExecutor> log) => _log = log;

    // ── IOrderExecutor ────────────────────────────────────────

    public Task OnEntrySignalAsync(EntrySignal sig)
    {
        _log.LogInformation("[MOCK] ENTRY {D} {Q}x Setup={S} @ {E} Stop={St} Tgt={T}",
            sig.Direction, sig.Contracts, sig.Setup, sig.Entry, sig.Stop, sig.Target);

        var ocoId  = Guid.NewGuid().ToString("N")[..8];
        bool isLong = sig.Direction == Direction.Long;
        var symbol  = sig.Setup.ToString();

        lock (_lock)
        {
            // Leg 1: Entry — treated as filled immediately (engine already recorded the fill)
            _orders.Add(new MockOrder
            {
                Symbol     = symbol,
                Action     = isLong ? "BUY" : "SELL",
                Quantity   = sig.Contracts,
                Status     = "FILLED",
                FillPrice  = sig.Entry,
                FilledAt   = DateTime.UtcNow,
                OcoGroupId = ocoId
            });

            // Leg 2: Stop loss (working)
            _orders.Add(new MockOrder
            {
                Symbol     = symbol,
                Action     = isLong ? "SELL" : "BUY",
                Quantity   = sig.Contracts,
                StopPrice  = sig.Stop,
                OcoGroupId = ocoId
            });

            // Leg 3: Take profit (working)
            _orders.Add(new MockOrder
            {
                Symbol     = symbol,
                Action     = isLong ? "SELL" : "BUY",
                Quantity   = sig.Contracts,
                LimitPrice = sig.Target,
                OcoGroupId = ocoId
            });
        }
        return Task.CompletedTask;
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
        // Move the open stop order for this specific setup to the new stop price (breakeven)
        var setupSymbol = sig.Setup.ToString();
        lock (_lock)
        {
            var stopOrder = _orders.FirstOrDefault(o =>
                o.Symbol == setupSymbol &&
                o.Status == "WORKING"  &&
                o.StopPrice.HasValue   &&
                o.OcoGroupId != null);
            if (stopOrder != null)
                stopOrder.StopPrice = sig.NewStop;
        }
        return Task.CompletedTask;
    }

    public Task OnExitSignalAsync(ExitSignal sig)
    {
        _log.LogInformation("[MOCK] EXIT Setup={S} {R} @ {P} {Q}ct",
            sig.Setup, sig.Reason, sig.ExitPrice, sig.Contracts);
        // Engine closed the position — cancel all still-working orders
        lock (_lock)
        {
            foreach (var o in _orders.Where(o => o.Status == "WORKING"))
                o.Status = "CANCELED";
        }
        return Task.CompletedTask;
    }

    // ── Fill simulation ───────────────────────────────────────

    /// <summary>
    /// Evaluates all WORKING orders against the latest market price and fills
    /// any that have crossed their trigger level. OCO partners of a filled order
    /// are immediately CANCELED. Thread-safe.
    /// </summary>
    public void EvaluateFills(string symbol, decimal price, DateTime utcNow)
    {
        if (price <= 0) return;
        lock (_lock)
        {
            // Snapshot WORKING orders so OCO cancellation inside the loop is safe
            var working = _orders.Where(o => o.Status == "WORKING").ToList();
            foreach (var o in working)
            {
                if (o.Status != "WORKING") continue; // already canceled by a prior OCO fill

                bool fills = o.Action == "BUY"
                    ? (o.StopPrice.HasValue  && price >= o.StopPrice)   // BUY STOP
                   || (o.LimitPrice.HasValue && price <= o.LimitPrice)  // BUY LIMIT
                    : (o.StopPrice.HasValue  && price <= o.StopPrice)   // SELL STOP
                   || (o.LimitPrice.HasValue && price >= o.LimitPrice); // SELL LIMIT

                if (!fills) continue;

                o.Status    = "FILLED";
                o.FillPrice = price;
                o.FilledAt  = utcNow;
                _log.LogInformation("[MOCK FILL] {A} {Q}x @ {P} (order {Id})",
                    o.Action, o.Quantity, price, o.OrderId);

                // Cancel all WORKING OCO partners
                if (o.OcoGroupId != null)
                {
                    foreach (var peer in _orders.Where(p =>
                        p.OcoGroupId == o.OcoGroupId &&
                        p.OrderId    != o.OrderId    &&
                        p.Status     == "WORKING"))
                    {
                        peer.Status = "CANCELED";
                        _log.LogDebug("[MOCK OCO] Canceled partner {Id}", peer.OrderId);
                    }
                }
            }
        }
    }

    // ── Query / mutation helpers ───────────────────────────────

    /// <summary>Returns a snapshot copy of all orders (any status).</summary>
    public List<MockOrder> GetOrders()
    {
        lock (_lock) return _orders.ToList();
    }

    /// <summary>Cancel a single WORKING order by ID.</summary>
    public void CancelOrder(string orderId)
    {
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
        }
    }

    /// <summary>
    /// Test helper: inject a pre-constructed order directly into the order book.
    /// Useful for unit tests that need to set up specific fill scenarios.
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
}
