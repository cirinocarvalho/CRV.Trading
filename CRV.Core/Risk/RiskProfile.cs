using CRV.Core.Models;

namespace CRV.Core.Risk;

/// <summary>What one setup actually commits per trade, measured from its own fills.</summary>
public sealed record InstrumentRisk(
    string  Setup,
    string  Ticker,
    int     Trades,
    decimal RiskPerContract,
    decimal RiskPerTrade,
    int     MedianContracts);

/// <summary>
/// How much money each setup actually risks, read off the trade record rather than
/// off the configuration.
/// <para>
/// Config says "3 contracts" for several setups and that reads as equal exposure. It
/// is not: stop distance times point value differs by instrument, and the micros span
/// two orders of magnitude in point value. Across the live book the risk committed per
/// trade ran from $50.75 to $219.12 — a 4.3x spread — with the heaviest weighting on
/// retest-mgc, which averaged +0.04R and lost $1,249. The signal was breakeven; the
/// position size did the damage.
/// </para>
/// <para>
/// Measured from fills rather than settings because the settings changed over the life
/// of the book and only the latest row survives. <c>|Entry - InitialStop|</c> is what
/// was actually risked.
/// </para>
/// </summary>
public sealed class RiskProfile
{
    /// <summary>Setups ordered by risk committed per trade, heaviest first.</summary>
    public IReadOnlyList<InstrumentRisk> Entries { get; }

    /// <summary>Ratio of the heaviest setup's risk to the lightest. 1.0 is perfectly even.</summary>
    public decimal Dispersion { get; }

    /// <summary>True when the spread is inside tolerance — nothing to normalise.</summary>
    public bool IsNormalised { get; }

    public InstrumentRisk? Heaviest => Entries.Count > 0 ? Entries[0]  : null;
    public InstrumentRisk? Lightest => Entries.Count > 0 ? Entries[^1] : null;

    /// <summary>The heaviest setup with enough fills to speak for itself.</summary>
    public InstrumentRisk? DispersionHeaviest { get; }

    /// <summary>The lightest such setup.</summary>
    public InstrumentRisk? DispersionLightest { get; }

    /// <summary>
    /// Fills a setup needs before its size counts as a policy rather than an accident.
    /// sessionfakeout-mnq has exactly one live trade at $684; letting a single fill set
    /// the headline spread buries the real one between the setups actually being traded.
    /// </summary>
    public const int MinimumFillsForDispersion = 3;

    /// <summary>
    /// The risk to normalise towards: the median of what the book already runs. Using
    /// the book's own middle means levelling neither scales the account up nor shuts
    /// it down — it only moves size between setups.
    /// </summary>
    public decimal SuggestedTarget { get; }

    /// <summary>
    /// A book is close enough to level when the heaviest setup risks no more than
    /// this multiple of the lightest. 1.5x is a judgement, not a law: below it the
    /// difference is unlikely to dominate the result, above it position size is
    /// deciding the outcome rather than the signal.
    /// </summary>
    public const decimal DefaultTolerance = 1.5m;

    private RiskProfile(List<InstrumentRisk> entries, decimal tolerance)
    {
        Entries = entries;

        if (entries.Count == 0)
        {
            Dispersion = 0m;
            IsNormalised = true;
            SuggestedTarget = 0m;
            return;
        }

        // Thin setups are listed but do not set the headline. If nothing clears the
        // bar, fall back to the whole book rather than reporting no spread at all.
        var judged = entries.Where(e => e.Trades >= MinimumFillsForDispersion).ToList();
        if (judged.Count == 0) judged = entries;

        DispersionHeaviest = judged[0];
        DispersionLightest = judged[^1];

        decimal heaviest = DispersionHeaviest.RiskPerTrade;
        decimal lightest = DispersionLightest.RiskPerTrade;

        Dispersion      = lightest > 0 ? Math.Round(heaviest / lightest, 4) : 0m;
        IsNormalised    = Dispersion <= tolerance;
        SuggestedTarget = Median(entries.Select(e => e.RiskPerTrade).ToList());
    }

    /// <summary>
    /// Contracts that would bring <paramref name="entry"/> to
    /// <paramref name="targetRiskPerTrade"/>. Zero means one contract already exceeds
    /// the budget — reported rather than rounded up to one, since rounding up is how a
    /// budget gets quietly blown by an instrument that never fitted it.
    /// </summary>
    public int SuggestedContracts(InstrumentRisk entry, decimal targetRiskPerTrade)
    {
        if (targetRiskPerTrade <= 0 || entry.RiskPerContract <= 0) return 0;
        return (int)Math.Floor(targetRiskPerTrade / entry.RiskPerContract);
    }

    /// <summary>
    /// Builds from completed trades. <paramref name="pointValueFor"/> resolves the
    /// dollar value of a point for a ticker — from the basket, normally.
    /// </summary>
    public static RiskProfile FromTrades(
        IEnumerable<TradeRecord> trades,
        Func<string, decimal> pointValueFor,
        decimal tolerance = DefaultTolerance)
    {
        var entries = trades
            // A trade with no stop recorded risked something, but not something this
            // can measure. Counting it as zero would drag every average down.
            .Where(t => t.InitialStop != 0 && t.InitialStop != t.Entry && t.Contracts > 0)
            // Grouped by root symbol, not full ticker: retest-mcl traded MCLK26 and
            // then MCLM26, and a contract roll should not split one setup into two rows.
            .GroupBy(t => (Setup: string.IsNullOrEmpty(t.SetupLabel) ? t.Setup.ToString() : t.SetupLabel,
                           Root: RootOf(t.Ticker)))
            .Select(g =>
            {
                var tickers = g.Select(t => t.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string label = tickers.Count == 1 ? tickers[0] : g.Key.Root;
                decimal pv = pointValueFor(tickers[0]);
                var perContract = g.Select(t => Math.Abs(t.Entry - t.InitialStop) * pv).ToList();
                var contracts   = g.Select(t => (decimal)t.Contracts).ToList();

                decimal medianRisk = Median(perContract);
                int medianCts = (int)Math.Round(Median(contracts), MidpointRounding.AwayFromZero);

                return new InstrumentRisk(
                    g.Key.Setup, label, g.Count(),
                    medianRisk, medianRisk * medianCts, medianCts);
            })
            .OrderByDescending(e => e.RiskPerTrade)
            .ThenBy(e => e.Setup, StringComparer.Ordinal)
            .ToList();

        return new RiskProfile(entries, tolerance);
    }

    /// <summary>"MCLK26" and "/MCLM26" both reduce to "MCL".</summary>
    private static string RootOf(string ticker)
    {
        var t = ticker.TrimStart('/').Trim();
        return t.Length > 3 ? t[..^3] : t;
    }

    private static decimal Median(List<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    /// <summary>The table plus the one sentence that matters.</summary>
    public string Describe()
    {
        if (Entries.Count == 0) return "no trades with a recorded stop";

        var lines = Entries.Select(e =>
            $"  {e.Setup,-20} {e.Ticker,-8} {e.Trades,4} trades  " +
            $"${e.RiskPerContract,7:F2}/ct x {e.MedianContracts}  =  ${e.RiskPerTrade,8:F2}  " +
            $"→ {SuggestedContracts(e, SuggestedTarget)} ct at ${SuggestedTarget:F0}");

        string verdict = IsNormalised
            ? $"risk is level within {Dispersion:F2}x"
            : $"{DispersionHeaviest!.Setup} risks {Dispersion:F2}x what {DispersionLightest!.Setup} does — " +
              $"position size, not signal, is deciding this book's result";

        return string.Join('\n', lines) + "\n" + verdict;
    }
}
