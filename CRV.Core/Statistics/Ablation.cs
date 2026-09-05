namespace CRV.Core.Statistics;

/// <summary>What removing a filter showed.</summary>
public enum AblationVerdict
{
    /// <summary>Not enough trades on one side or the other to say.</summary>
    InsufficientEvidence,

    /// <summary>Measurably better with the filter than without it.</summary>
    Earns,

    /// <summary>Measurably worse with the filter than without it.</summary>
    Harms,

    /// <summary>The difference is inside the noise. The filter has not been shown to do anything.</summary>
    NoMeasurableEffect,
}

/// <summary>
/// One filter, measured against the strategy without it.
/// <para>
/// The engine stacks VWAP, ATR, chop and EMA filters on the raw opening-range break
/// and none has been tested against that break alone. Every filter costs trades, and
/// a smaller sample is a worse measurement, so a filter has to pay for the trades it
/// removes. "No measurable effect" is the common and most useful answer: it means the
/// filter is complexity with nothing behind it.
/// </para>
/// </summary>
public sealed class Ablation
{
    public string   Name       { get; }
    public EdgeTest Baseline   { get; }
    public EdgeTest WithFilter { get; }

    public Ablation(EdgeTest baseline, EdgeTest withFilter, string name)
    {
        Baseline   = baseline;
        WithFilter = withFilter;
        Name       = name;
    }

    /// <summary>Mean R with the filter minus mean R without it. Positive means it helped.</summary>
    public decimal Contribution => WithFilter.MeanR - Baseline.MeanR;

    /// <summary>Share of baseline trades the filter lets through. A filter that blocks almost everything cannot be judged.</summary>
    public double PassRate => Baseline.Count == 0 ? 0 : (double)WithFilter.Count / Baseline.Count;

    public AblationVerdict Verdict =>
        Baseline.Count   < EdgeTest.MinimumSample ||
        WithFilter.Count < EdgeTest.MinimumSample   ? AblationVerdict.InsufficientEvidence
      : !EdgeTest.Differ(WithFilter, Baseline)      ? AblationVerdict.NoMeasurableEffect
      : Contribution > 0                            ? AblationVerdict.Earns
      :                                               AblationVerdict.Harms;

    public string Describe() => Verdict switch
    {
        AblationVerdict.InsufficientEvidence =>
            $"{Name,-8} insufficient evidence — {WithFilter.Count} of {Baseline.Count} trades survive the filter ({PassRate:P0})",
        AblationVerdict.Earns =>
            $"{Name,-8} {Contribution:+0.000;-0.000}R — earns its place ({PassRate:P0} of trades pass)",
        AblationVerdict.Harms =>
            $"{Name,-8} {Contribution:+0.000;-0.000}R — actively harms ({PassRate:P0} of trades pass)",
        _ =>
            $"{Name,-8} {Contribution:+0.000;-0.000}R — no measurable effect ({PassRate:P0} of trades pass)",
    };
}

/// <summary>A whole filter stack measured against one baseline.</summary>
public sealed class AblationStudy
{
    public EdgeTest Baseline { get; }
    public IReadOnlyList<Ablation> Ranked { get; }

    public AblationStudy(EdgeTest baseline, IEnumerable<Ablation> ablations)
    {
        Baseline = baseline;
        Ranked   = ablations.OrderByDescending(a => a.Contribution).ToList();
    }

    /// <summary>Filters that measurably improve on the baseline.</summary>
    public IReadOnlyList<Ablation> Earning =>
        Ranked.Where(a => a.Verdict == AblationVerdict.Earns).ToList();

    /// <summary>
    /// Filters to consider deleting: those that harm, and those that do nothing
    /// measurable. Under-sampled ones are excluded — they are unmeasured, not useless.
    /// </summary>
    public IReadOnlyList<Ablation> Candidates =>
        Ranked.Where(a => a.Verdict is AblationVerdict.Harms or AblationVerdict.NoMeasurableEffect).ToList();

    public string Describe()
    {
        var lines = new List<string> { $"baseline  {Baseline.Describe()}" };
        lines.AddRange(Ranked.Select(a => "  " + a.Describe()));

        lines.Add(Earning.Count == 0
            ? "nothing measurably improves on the baseline"
            : $"earning their place: {string.Join(", ", Earning.Select(a => a.Name))}");

        if (Candidates.Count > 0)
            lines.Add($"consider deleting: {string.Join(", ", Candidates.Select(a => a.Name))}");

        return string.Join('\n', lines);
    }
}
