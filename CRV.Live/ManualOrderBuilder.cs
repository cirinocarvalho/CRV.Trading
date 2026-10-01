using System.ComponentModel.DataAnnotations;
using CRV.Core.Models;

namespace CRV.Live;

// ── Order ticket input ───────────────────────────────────────────

public class ManualOrder
{
    [Required] public string  Ticker     { get; set; } = "";
    [Required] public string  Direction  { get; set; } = "Long";
    public            string  InputMode  { get; set; } = "Points";   // "Points" | "Dollars" | "Price"
    public            string  OrderType  { get; set; } = "Market";   // "Market" | "Limit"
    public            bool    UseBe      { get; set; } = true;       // Move stop to BE on partial fill

    [Range(0.01, double.MaxValue)] public decimal EntryPrice    { get; set; }
    [Range(1, 100)]                public int     Contracts     { get; set; } = 1;
    public decimal PointValue    { get; set; }

    // Points mode
    [Range(0, double.MaxValue)] public decimal StopPoints    { get; set; }
    [Range(0, double.MaxValue)] public decimal TargetPoints  { get; set; }

    // Dollars mode
    [Range(0, double.MaxValue)] public decimal StopDollars   { get; set; }
    [Range(0, double.MaxValue)] public decimal TargetDollars { get; set; }

    // Price mode
    [Range(0, double.MaxValue)] public decimal StopPrice     { get; set; }
    [Range(0, double.MaxValue)] public decimal TargetPrice   { get; set; }

    // Partial
    public bool    UsePartial       { get; set; }
    [Range(0, 99)] public int     PartialContracts { get; set; } = 1;
    [Range(0, double.MaxValue)] public decimal PartialPoints  { get; set; }
    [Range(0, double.MaxValue)] public decimal PartialDollars { get; set; }
    [Range(0, double.MaxValue)] public decimal PartialPrice   { get; set; }

    /// <summary>When true, <see cref="Brackets"/> is used instead of the single partial + target. Price mode only.</summary>
    public bool UseMultiBracket { get; set; }
    /// <summary>Up to 4 bracket rows; only rows with Qty &gt; 0 and Target &gt; 0 are used.</summary>
    public List<ManualBracketRow> Brackets { get; set; } = new() { new(), new(), new(), new() };

    // Auto Trail
    public bool UseAutoTrail { get; set; }
    [Range(0, double.MaxValue)] public decimal AutoTrailStopLoss { get; set; }
    [Range(0, double.MaxValue)] public decimal AutoTrailTrigger  { get; set; }
    [Range(0, double.MaxValue)] public decimal AutoTrailFreq     { get; set; }
}

public class ManualBracketRow
{
    [Range(0, double.MaxValue)] public decimal Target { get; set; }
    [Range(0, 100)]             public int     Qty    { get; set; }
    public bool MoveBe { get; set; }
}

/// <summary>
/// Validates a manual order and builds its <see cref="EntrySignal"/>. Moved unchanged from the
/// Manual page so the order ticket and its tests share one set of rules.
/// </summary>
public static class ManualOrderBuilder
{
    public static (EntrySignal? Signal, List<string> Errors) Build(ManualOrder order, DateTime nowUtc)
    {
        var errors = new List<string>();
        bool isLong = order.Direction == "Long";

        if (order.Contracts < 1)
            errors.Add("Contracts must be at least 1.");

        if (order.UseMultiBracket)
            return BuildMultiBracket(order, isLong, nowUtc, errors);

        if (order.UsePartial)
        {
            if (order.Contracts < 2)
                errors.Add("Partial exit requires at least 2 contracts.");
            if (order.PartialContracts < 1 || order.PartialContracts >= order.Contracts)
                errors.Add("Partial contracts must be between 1 and (total contracts − 1).");
        }

        if (order.UseAutoTrail)
        {
            if (order.AutoTrailStopLoss <= 0) errors.Add("Auto Trail Stop Loss must be > 0.");
            if (order.AutoTrailFreq <= 0) errors.Add("Auto Trail Freq must be > 0.");
            if (!order.UsePartial && order.AutoTrailTrigger <= 0)
                errors.Add("Auto Trail Trigger is required when Partial is off.");
        }

        if (errors.Count > 0) return (null, errors);

        int remainCts = order.Contracts - order.PartialContracts;

        switch (order.InputMode)
        {
            case "Dollars":
                if (order.PointValue <= 0)
                    { errors.Add("Point value must be > 0 for dollar-based input."); break; }
                order.StopPoints   = order.StopDollars   / (order.PointValue * order.Contracts);
                order.TargetPoints = order.TargetDollars / (order.PointValue * (order.UsePartial ? Math.Max(1, remainCts) : order.Contracts));
                if (order.UsePartial && order.PartialContracts > 0)
                    order.PartialPoints = order.PartialDollars / (order.PointValue * order.PartialContracts);
                break;

            case "Price":
                if (order.EntryPrice <= 0)
                    { errors.Add("Entry price is required for price-based input."); break; }
                order.StopPoints   = isLong ? order.EntryPrice - order.StopPrice   : order.StopPrice   - order.EntryPrice;
                order.TargetPoints = isLong ? order.TargetPrice - order.EntryPrice : order.EntryPrice - order.TargetPrice;
                if (order.UsePartial)
                    order.PartialPoints = isLong ? order.PartialPrice - order.EntryPrice : order.EntryPrice - order.PartialPrice;
                break;
        }

        if (errors.Count > 0) return (null, errors);

        if (order.StopPoints   <= 0) errors.Add("Stop distance must be greater than 0.");
        if (order.TargetPoints <= 0) errors.Add("Target distance must be greater than 0.");
        if (order.UsePartial && (order.PartialPoints <= 0 || order.PartialPoints >= order.TargetPoints))
            errors.Add("Partial distance must be > 0 and < full target distance.");

        if (errors.Count > 0) return (null, errors);

        var sig = BuildEntry(isLong, order.Contracts, order.EntryPrice,
            order.StopPoints, order.TargetPoints, order.PartialPoints,
            order.Ticker, order.PointValue, order.OrderType,
            order.UsePartial, order.PartialContracts, order.UseBe,
            order.UseAutoTrail, order.AutoTrailStopLoss, order.AutoTrailTrigger, order.AutoTrailFreq, nowUtc);
        return (sig, errors);
    }

    // ── Multi-bracket path (price-based, N up to 4) ──────────────
    private static (EntrySignal? Signal, List<string> Errors) BuildMultiBracket(
        ManualOrder order, bool isLong, DateTime nowUtc, List<string> errors)
    {
        if (order.EntryPrice <= 0) errors.Add("Entry price is required for multi-bracket mode.");
        if (order.StopPrice  <= 0) errors.Add("Stop price is required for multi-bracket mode.");

        var populated = order.Brackets
            .Where(b => b.Target > 0 && b.Qty > 0)
            .Take(4)
            .ToList();
        if (populated.Count == 0)
            errors.Add("At least one bracket row with Target > 0 and Qty > 0 is required.");

        var qtySum = populated.Sum(b => b.Qty);
        if (populated.Count > 0 && qtySum != order.Contracts)
            errors.Add($"Sum of bracket quantities ({qtySum}) must equal total Contracts ({order.Contracts}).");

        foreach (var b in populated)
        {
            var profitable = isLong ? b.Target > order.EntryPrice : b.Target < order.EntryPrice;
            if (!profitable)
                errors.Add($"Bracket target {b.Target} is not on the profit side of entry {order.EntryPrice}.");
        }

        if (order.StopPrice > 0)
        {
            var validStop = isLong ? order.StopPrice < order.EntryPrice : order.StopPrice > order.EntryPrice;
            if (!validStop)
                errors.Add($"Stop price {order.StopPrice} is on the wrong side of entry {order.EntryPrice}.");
        }

        if (order.UseAutoTrail)
        {
            if (order.AutoTrailStopLoss <= 0) errors.Add("Auto Trail Stop Loss must be > 0.");
            if (order.AutoTrailFreq <= 0) errors.Add("Auto Trail Freq must be > 0.");
        }

        if (errors.Count > 0) return (null, errors);

        var brackets = populated.Select(b => new BracketLeg(b.Target, b.Qty, b.MoveBe)).ToList();

        var sig = new EntrySignal(
            Setup:             SetupId.F,
            Direction:         isLong ? Direction.Long : Direction.Short,
            Entry:             order.EntryPrice,
            Stop:              order.StopPrice,
            // Legacy Tg1/Tg2 fields populated for non-Tradovate brokers
            Tg2Price:          brackets[^1].TargetPrice,
            Tg1Price:          brackets[0].TargetPrice,
            TotalContracts:    order.Contracts,
            Time:              nowUtc,
            OrderType:         order.OrderType,
            Ticker:            order.Ticker,
            SetupLabel:        $"Manual-{nowUtc:HHmmss}",
            PartialContracts:  brackets.Count >= 2 ? brackets[0].Qty : 0,
            UsePartial:        brackets.Count >= 2,
            UseBe:             order.UseBe,
            AutoTrailStopLoss: order.UseAutoTrail ? order.AutoTrailStopLoss : null,
            AutoTrailTrigger:  order.UseAutoTrail && order.AutoTrailTrigger > 0 ? order.AutoTrailTrigger : null,
            AutoTrailFreq:     order.UseAutoTrail ? order.AutoTrailFreq : null,
            Brackets:          brackets);
        return (sig, errors);
    }

    private static EntrySignal BuildEntry(bool isLong, int contracts,
        decimal entryPrice, decimal stopPts, decimal targetPts,
        decimal partialPts, string ticker, decimal pointValue,
        string orderType, bool usePartial, int partialContracts, bool useBe,
        bool useAutoTrail, decimal autoTrailStopLoss, decimal autoTrailTrigger, decimal autoTrailFreq,
        DateTime nowUtc)
    {
        decimal stop    = isLong ? entryPrice - stopPts   : entryPrice + stopPts;
        decimal target  = isLong ? entryPrice + targetPts : entryPrice - targetPts;
        decimal partial = partialPts > 0
            ? (isLong ? entryPrice + partialPts : entryPrice - partialPts)
            : (isLong ? entryPrice + targetPts * 0.5m : entryPrice - targetPts * 0.5m);

        return new EntrySignal(
            Setup:     SetupId.A,
            Direction: isLong ? Direction.Long : Direction.Short,
            Entry:     entryPrice,
            Stop:      stop,
            Tg2Price:       target,
            Tg1Price:       partial,
            TotalContracts: contracts,
            Time:           nowUtc,
            OrderType:      orderType,
            Ticker:         ticker,
            SetupLabel:     $"Manual-{nowUtc:HHmmss}",
            PartialContracts: usePartial ? partialContracts : 0,
            UsePartial:       usePartial,
            UseBe:            useBe,
            AutoTrailStopLoss: useAutoTrail ? autoTrailStopLoss : null,
            AutoTrailTrigger:  useAutoTrail ? (autoTrailTrigger > 0 ? autoTrailTrigger : (usePartial ? (decimal?)null : 0m)) : null,
            AutoTrailFreq:     useAutoTrail ? autoTrailFreq : null
        );
    }
}
