namespace CRV.Core.Models;

// ── Group Order enums ────────────────────────────────────────
public enum LegType { Entry, Tg1, Tg2, Stop }
public enum OrderLegStatus { Working, Filled, Modified, Canceled, Rejected }
public enum GroupOrderStatus { Pending, Active, PartialFilled, Completed, Canceled }

// ── Order event — emitted by broker event stream ────────────
public record OrderEvent(
    string GroupOrderId,
    string OrderId,
    LegType LegType,
    OrderLegStatus Status,
    decimal? FillPrice,
    int? FillQty,
    decimal? ModifiedPrice,
    int? ModifiedQty,
    DateTime Timestamp);

// ── Group Order — multi-leg trade unit ──────────────────────
public class GroupOrder
{
    public int Id { get; set; }
    public string GroupOrderId { get; set; } = "";
    public string SetupId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public Direction Direction { get; set; }
    public int TotalContracts { get; set; }
    public int PartialContracts { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal InitialStopPrice { get; set; }
    public decimal PointValue { get; set; }
    public decimal AccruedPartialPnl { get; set; }
    public GroupOrderStatus Status { get; set; } = GroupOrderStatus.Pending;
    public string Broker { get; set; } = "";
    public string? BrokerStrategyId { get; set; }
    public bool UseBe { get; set; } = true;
    public string? SessionId { get; set; }
    /// <summary>Second stop order ID from the other partial bracket. When Tg1 fills,
    /// the broker OCO should cancel the paired stop — but if it doesn't, we explicitly
    /// cancel the orphaned stop and switch tracking to Stop2.</summary>
    public string? Stop2OrderId { get; set; }

    // ── Auto-trail state (transient — backtest simulation only, not persisted) ──
    public decimal? AutoTrailStopLoss { get; set; }
    public decimal? AutoTrailTrigger { get; set; }
    public decimal? AutoTrailFreq { get; set; }
    public bool AutoTrailActivated { get; set; }
    public decimal? AutoTrailHighWater { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public List<OrderLeg> Legs { get; set; } = new();

    /// <summary>Find leg by type. Returns null if not found.</summary>
    public OrderLeg? GetLeg(LegType type) => Legs.FirstOrDefault(l => l.LegType == type);

    /// <summary>Find leg by broker order ID.</summary>
    public OrderLeg? GetLegByOrderId(string orderId) => Legs.FirstOrDefault(l => l.OrderId == orderId);

    /// <summary>Remaining contracts after partial fill.</summary>
    public int RemainingContracts => TotalContracts - PartialContracts;
}

// ── Order Leg — individual order within a group ─────────────
public class OrderLeg
{
    public int Id { get; set; }
    public string GroupOrderId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public LegType LegType { get; set; }
    public string OrderType { get; set; } = "";   // Market | Limit | Stop
    public string Action { get; set; } = "";       // BUY | SELL
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public OrderLegStatus Status { get; set; } = OrderLegStatus.Working;
    public decimal? FillPrice { get; set; }
    public DateTime? FillTime { get; set; }
    public DateTime? LastModifiedAt { get; set; }
}
