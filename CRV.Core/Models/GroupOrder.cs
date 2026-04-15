namespace CRV.Core.Models;

// ── Group Order enums ────────────────────────────────────────
// Tg1..Tg4 are target legs, indexed in sequence. Handlers treat them
// as an ordered list; fills of non-terminal targets move the stop (BE)
// and fill of the terminal target completes the group.
public enum LegType { Entry, Tg1, Tg2, Tg3, Tg4, Stop }
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
    /// cancel the orphaned stop and switch tracking to Stop2. Legacy 2-bracket field.</summary>
    public string? Stop2OrderId { get; set; }

    /// <summary>Queue of remaining stop-order IDs for brackets beyond the first
    /// (3..N-bracket orders). Stored as a comma-separated list on the row and
    /// manipulated as a list via <see cref="PendingStopIds"/>. On each non-terminal
    /// target fill, the head is popped and becomes the new active stop.</summary>
    public string? PendingStopIdsCsv { get; set; }

    /// <summary>Convenience accessor for <see cref="PendingStopIdsCsv"/>.
    /// Not persisted directly — backed by the CSV column.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<string> PendingStopIds
    {
        get => string.IsNullOrEmpty(PendingStopIdsCsv)
            ? new List<string>()
            : PendingStopIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        set => PendingStopIdsCsv = value is { Count: > 0 } ? string.Join(',', value) : null;
    }

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

    /// <summary>Target legs ordered by LegType ordinal (Tg1, Tg2, Tg3, Tg4).
    /// Used by handlers to iterate targets generically instead of hardcoding Tg1/Tg2.
    /// NotMapped — computed from <see cref="Legs"/>, which itself is ignored in the
    /// DbContext mapping. Without this attribute, EF picks up the return type as a
    /// second collection navigation to OrderLeg and creates a shadow FK (GroupOrderId2).</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyList<OrderLeg> TargetLegs =>
        Legs.Where(l => IsTargetLeg(l.LegType)).OrderBy(l => (int)l.LegType).ToList();

    /// <summary>True if the given LegType is any of Tg1..Tg4.</summary>
    public static bool IsTargetLeg(LegType t) =>
        t == LegType.Tg1 || t == LegType.Tg2 || t == LegType.Tg3 || t == LegType.Tg4;
}

// ── Bracket leg — one independent target within a multi-bracket order ──
/// <summary>
/// Describes one target leg in a multi-bracket order. Each bracket
/// carries its own quantity, profit target, and whether the stop should
/// move to break-even after this target fills (only relevant for
/// non-terminal brackets).
/// </summary>
public record BracketLeg(decimal TargetPrice, int Qty, bool MoveBe = false);

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
