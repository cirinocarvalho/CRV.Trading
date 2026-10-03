using System.Text.Json;
using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// A strategy's typical stop: the median stop of its last 30 trades in saved backtest runs.
/// The save-time reward / risk check measures a target against it. No history gives null,
/// never an estimate.
/// </summary>
public static class TypicalStop
{
    public const int DefaultLast = 30;

    /// <summary>Median stop distance in points, or null with no usable trades.</summary>
    public static decimal? Median(IEnumerable<TradeRecord> trades, int last = DefaultLast)
        => MedianOf(Recent(trades, last).Select(StopPoints));

    /// <summary>Median of contracts × stop points: the whole position's risk in points.</summary>
    public static decimal? MedianPosition(IEnumerable<TradeRecord> trades, int last = DefaultLast)
        => MedianOf(Recent(trades, last).Select(t => StopPoints(t) * Math.Max(1, t.Contracts)));

    /// <summary>The newest <paramref name="last"/> trades with a stop, each counted once even when
    /// overlapping runs saved it twice.</summary>
    public static IReadOnlyList<TradeRecord> Recent(IEnumerable<TradeRecord> trades, int last = DefaultLast)
        => trades.Where(HasStop)
                 .DistinctBy(TradeKey)
                 .OrderByDescending(t => t.EnteredAt)
                 .Take(last)
                 .ToList();

    /// <summary>The trades of <paramref name="setupId"/> in saved runs' result JSON. Runs are
    /// read in the order given (newest first) and lazily: reading stops once
    /// <paramref name="needed"/> distinct usable trades are found, so older result blobs are never
    /// loaded. A run that can't be read is skipped: it says nothing about this setup's stops.</summary>
    public static List<TradeRecord> FromRuns(IEnumerable<string?> resultJsons, string setupId, int needed = DefaultLast)
    {
        var found = new List<TradeRecord>();
        var distinct = new HashSet<(DateTime, decimal, decimal)>();
        foreach (var json in resultJsons)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            RunTrades? run;
            try { run = JsonSerializer.Deserialize<RunTrades>(json); }
            catch (JsonException) { continue; }
            if (run?.Trades is null) continue;

            foreach (var trade in run.Trades.Where(t => t.SetupLabel == setupId))
            {
                found.Add(trade);
                if (HasStop(trade)) distinct.Add(TradeKey(trade));
            }
            if (distinct.Count >= needed) break;
        }
        return found;
    }

    private static bool HasStop(TradeRecord t) => t.InitialStop != 0 && t.InitialStop != t.Entry;

    private static (DateTime, decimal, decimal) TradeKey(TradeRecord t) => (t.EnteredAt, t.Entry, t.InitialStop);

    private static decimal StopPoints(TradeRecord t) => Math.Abs(t.Entry - t.InitialStop);

    private static decimal? MedianOf(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    /// <summary>The part of a saved backtest result this needs.</summary>
    private sealed class RunTrades
    {
        public List<TradeRecord>? Trades { get; set; }
    }
}
