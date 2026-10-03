using CRV.Core.Strategy;

namespace CRV.Core.Models;

/// <summary>A basket entry that is switched on but does not trade, and why.</summary>
public sealed record DisabledSetup(string Id, string Label, StrategyType Type, string Ticker, string Reason);

/// <summary>
/// The checks that web save, engine start and the bar feed share, so they agree on which entries
/// trade. An entry that fails is disabled on its own and the rest of the basket keeps trading.
/// Strategies on one root (NQ and MNQ, ES and MES, …) run on one bar feed, so every switched-on
/// entry on a root must use the same bar size. Problems are lower-case phrases that read after
/// "Disabled: "; <see cref="Sentences"/> turns them into sentences for a save error.
/// </summary>
public static class SetupValidation
{
    /// <summary>The number the EMA21 strategy had. Reserved so stored baskets and saved backtest runs never reuse it.</summary>
    public const StrategyType RetiredEma21 = (StrategyType)4;

    /// <summary>Why an entry of the retired type doesn't trade.</summary>
    public const string RetiredEma21Reason = "retired EMA21 strategy";

    /// <summary>Bar sizes (minutes) the feeds can build.</summary>
    public static readonly int[] BarSizes = { 1, 2, 5, 10, 15, 20, 30, 60 };

    private static readonly StrategyType[] Tradable =
        { StrategyType.Pullback, StrategyType.Retest, StrategyType.OrbFakeout, StrategyType.SessionFakeout };

    /// <summary>
    /// What's wrong with one entry on its own, whether or not it's switched on.
    /// <paramref name="cfg"/> is where config-wide values for the checks come from.
    /// </summary>
    public static IReadOnlyList<string> Entry(BasketEntry entry, StrategyConfig cfg)
    {
        var problems = new List<string>();
        if (entry.StrategyType == RetiredEma21)
            problems.Add(RetiredEma21Reason);
        else if (entry.StrategyType == StrategyType.Ema)
            problems.Add("the EMA strategy can't run in this version");
        else if (!Tradable.Contains(entry.StrategyType))
            problems.Add($"unknown strategy type {(int)entry.StrategyType}");

        if (string.IsNullOrWhiteSpace(entry.Ticker))
            problems.Add("it has no instrument");

        if (entry.ExecutionTFMinutes is int tf && tf > 0 && !BarSizes.Contains(tf))
            problems.Add($"bar size {tf} min isn't one of {string.Join(", ", BarSizes)} min");
        if (entry.Config.UseCustomOrbWindow && entry.Config.OrbEnd <= entry.Config.OrbStart)
            problems.Add("its opening range ends before it starts");
        return problems;
    }

    /// <summary>One sentence per root whose switched-on entries that can trade (both baskets) use more than one bar size.</summary>
    public static IReadOnlyList<string> RootBarSizes(StrategyConfig cfg)
    {
        var problems = new List<string>();
        var byRoot = cfg.EnumerateBasketEntries()
            .Where(e => IsSwitchedOnAndTradable(e, cfg))
            .GroupBy(e => TickerGroup.GetGroupKey(e.Ticker));
        foreach (var root in byRoot)
        {
            if (root.Select(e => BarMinutes(e, cfg)).Distinct().Count() < 2) continue;
            problems.Add($"Every {TickerGroup.GroupLabel(root.Key)} strategy must use one bar size: " +
                         string.Join(", ", root.Select(e => $"{Name(e)} {BarMinutes(e, cfg)} min")) + ".");
        }
        return problems;
    }

    /// <summary>
    /// Switched-on entries that can't trade, with the reason. Entries are walked in basket order
    /// (ORB basket, then EMA basket); on each root the first entry that passes <see cref="Entry"/>
    /// sets the bar size, and a later one on another bar size is disabled.
    /// </summary>
    public static IReadOnlyList<DisabledSetup> DisabledSetups(StrategyConfig cfg)
    {
        var disabled = new List<DisabledSetup>();
        var rootSetBy = new Dictionary<string, BasketEntry>();
        foreach (var e in cfg.EnumerateBasketEntries())
        {
            if (!e.Enabled) continue;
            var reason = Entry(e, cfg).FirstOrDefault();
            if (reason == null)
            {
                var root = TickerGroup.GetGroupKey(e.Ticker);
                if (!rootSetBy.TryGetValue(root, out var first)) rootSetBy[root] = e;
                else if (BarMinutes(first, cfg) != BarMinutes(e, cfg)) reason = BarSizeConflict(e, first, cfg);
            }
            if (reason != null) disabled.Add(new DisabledSetup(e.Id, Name(e), e.StrategyType, e.Ticker, reason));
        }
        return disabled;
    }

    /// <summary>
    /// Why saving <paramref name="entry"/> into <paramref name="cfg"/>'s baskets is refused: its own
    /// problems and, when it's switched on, any other switched-on entry on its root (that can trade)
    /// with a different bar size. <paramref name="cfg"/> holds the baskets as saved; the entry with the
    /// same Id is the one being replaced and is skipped.
    /// </summary>
    public static IReadOnlyList<string> SaveErrors(BasketEntry entry, StrategyConfig cfg)
    {
        var problems = Entry(entry, cfg).ToList();
        if (!entry.Enabled || problems.Count > 0) return problems;

        var root = TickerGroup.GetGroupKey(entry.Ticker);
        var bar = BarMinutes(entry, cfg);
        var other = cfg.EnumerateBasketEntries().FirstOrDefault(o =>
            o.Id != entry.Id && IsSwitchedOnAndTradable(o, cfg) &&
            TickerGroup.GetGroupKey(o.Ticker) == root && BarMinutes(o, cfg) != bar);
        if (other != null) problems.Add(BarSizeConflict(entry, other, cfg));
        return problems;
    }

    /// <summary>Why a saved entry doesn't trade, for its setup page; null when it trades (or is simply switched off).</summary>
    public static string? DisabledReason(BasketEntry entry, StrategyConfig cfg) =>
        Entry(entry, cfg).FirstOrDefault()
        ?? (entry.Enabled ? DisabledSetups(cfg).FirstOrDefault(d => d.Id == entry.Id)?.Reason : null);

    /// <summary>One sentence per basket whose JSON can't be read. None of that basket's entries trade.</summary>
    public static IReadOnlyList<string> BasketErrors(StrategyConfig cfg)
    {
        var problems = new List<string>();
        foreach (var (json, name) in new[] { (cfg.BasketJson, "opening-range"), (cfg.EmaBasketJson, "EMA") })
        {
            try { BasketCodec.Parse(json); }
            catch (Exception ex) { problems.Add($"The {name} strategy list can't be read, so none of its strategies trade ({ex.Message})."); }
        }
        return problems;
    }

    /// <summary>Bar size an entry runs on: its own, or the config's when it has none.</summary>
    public static int BarMinutes(BasketEntry entry, StrategyConfig cfg) =>
        entry.ExecutionTFMinutes is int tf && tf > 0 ? tf : Math.Max(1, cfg.ExecutionTFMinutes);

    /// <summary>The entry's label, or its Id when it has none.</summary>
    public static string Name(BasketEntry entry) => entry.Label is { Length: > 0 } ? entry.Label : entry.Id;

    /// <summary>Problems as sentences: "retired EMA21 strategy" → "Retired EMA21 strategy."</summary>
    public static string Sentences(IEnumerable<string> problems) =>
        string.Join(" ", problems.Select(p => char.ToUpperInvariant(p[0]) + p[1..] + "."));

    private static bool IsSwitchedOnAndTradable(BasketEntry entry, StrategyConfig cfg) =>
        entry.Enabled && Entry(entry, cfg).Count == 0;

    private static string BarSizeConflict(BasketEntry entry, BasketEntry other, StrategyConfig cfg) =>
        $"bar size {BarMinutes(entry, cfg)} min differs from the {BarMinutes(other, cfg)} min {Name(other)} uses; " +
        $"every {TickerGroup.GroupLabel(TickerGroup.GetGroupKey(entry.Ticker))} strategy shares one bar size";
}
