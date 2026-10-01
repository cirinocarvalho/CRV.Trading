using System.Text.RegularExpressions;
using CRV.Core.Models;

namespace CRV.Live;

public sealed record FlattenGroup(string GroupOrderId, string Setup, string Ticker, string Direction, int Contracts, string Status);
public sealed record FlattenPosition(string Symbol, bool IsLong, int Quantity);

/// <summary>What "Flatten all" will do, worked out before anything is sent.</summary>
public sealed record FlattenPlan(
    IReadOnlyList<FlattenGroup>    Groups,        // engine-tracked: closed (or cancelled, if unfilled) through the engine
    IReadOnlyList<FlattenPosition> Positions,     // broker futures positions the engine isn't tracking: cancel orders, close at market
    IReadOnlyList<string>          OrderSymbols,  // working futures orders on instruments with no position: cancel
    IReadOnlyList<string>          LeftAlone)     // shown, not touched (options, unknown instruments)
{
    public bool IsEmpty => Groups.Count == 0 && Positions.Count == 0 && OrderSymbols.Count == 0;
}

/// <summary>
/// Decides what Flatten all touches. Pure, so the rules are tested rather than read.
/// <para>
/// An instrument the engine is tracking is closed through the engine only. Closing it at the
/// broker as well would race the engine's own market exit: while that exit is in flight the
/// broker still reports the position, and a second close would open the opposite position.
/// </para>
/// </summary>
public static class FlattenPlanner
{
    private static readonly Regex FutureContract = new(@"^[A-Z0-9]{1,4}[FGHJKMNQUVXZ]\d{2}$", RegexOptions.Compiled);

    public static bool IsFutureSymbol(string symbol) =>
        !string.IsNullOrWhiteSpace(symbol) && FutureContract.IsMatch(FuturesSymbol.Normalize(symbol).ToUpperInvariant());

    private static string Key(string symbol) => FuturesSymbol.Normalize(symbol ?? "").ToUpperInvariant();

    public static FlattenPlan Build(
        IEnumerable<GroupOrder> engineGroups,
        IEnumerable<PositionView> brokerPositions,
        IEnumerable<OrderView> brokerOrders)
    {
        var groups = engineGroups
            .Where(g => g.Status is GroupOrderStatus.Pending or GroupOrderStatus.Active or GroupOrderStatus.PartialFilled)
            .Select(g => new FlattenGroup(
                g.GroupOrderId, g.SetupId, g.Ticker, g.Direction.ToString(),
                g.Status == GroupOrderStatus.PartialFilled ? g.TotalContracts - g.PartialContracts : g.TotalContracts,
                g.Status.ToString()))
            .ToList();
        var tracked = groups.Select(g => Key(g.Ticker)).ToHashSet();

        var positions = new List<FlattenPosition>();
        var leftAlone = new List<string>();
        foreach (var p in brokerPositions.Where(p => p.Quantity > 0))
        {
            var isFuture = string.Equals(p.AssetType, "FUTURE", StringComparison.OrdinalIgnoreCase) && IsFutureSymbol(p.Symbol);
            if (!isFuture)
            {
                leftAlone.Add($"{p.Description ?? p.Symbol} ({p.AssetType.ToLowerInvariant()}, {p.Direction.ToLowerInvariant()} {p.Quantity:0.##})");
                continue;
            }
            if (tracked.Contains(Key(p.Symbol))) continue;   // the engine closes it
            positions.Add(new FlattenPosition(p.Symbol, string.Equals(p.Direction, "LONG", StringComparison.OrdinalIgnoreCase), (int)p.Quantity));
        }

        var covered = tracked.Concat(positions.Select(p => Key(p.Symbol))).ToHashSet();
        var orderSymbols = brokerOrders
            .Where(o => o.CanCancel && IsFutureSymbol(o.Symbol) && !covered.Contains(Key(o.Symbol)))
            .GroupBy(o => Key(o.Symbol))
            .Select(g => g.First().Symbol)
            .ToList();

        return new FlattenPlan(groups, positions, orderSymbols, leftAlone);
    }
}
