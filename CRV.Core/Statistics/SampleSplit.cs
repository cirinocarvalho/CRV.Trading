using CRV.Core.Models;

namespace CRV.Core.Statistics;

/// <summary>
/// A chronological in-sample / out-of-sample split.
/// <para>
/// Nothing in this system has ever been validated on data it was not chosen
/// against. The subset of the live book that looked promising — retest, NY,
/// MNQ/MCL, long — was identified by reading the results of those same trades.
/// That makes it a hypothesis, and a hypothesis needs unseen data before it counts
/// as evidence. Fitting on the out-of-sample side spends it: once a parameter has
/// been chosen against those trades, they are in-sample too.
/// </para>
/// <para>
/// The split is always by time. Sampling at random from a time series lets the
/// future inform the past, which flatters every result.
/// </para>
/// </summary>
public sealed class SampleSplit
{
    public IReadOnlyList<TradeRecord> InSample    { get; }
    public IReadOnlyList<TradeRecord> OutOfSample { get; }

    /// <summary>Trades dropped by the embargo — counted so the loss is visible.</summary>
    public int EmbargoedCount { get; }

    /// <summary>The boundary: trades before it are in-sample, on or after it are not.</summary>
    public DateTime Boundary { get; }

    public EdgeTest InSampleEdge    { get; }
    public EdgeTest OutOfSampleEdge { get; }

    private SampleSplit(List<TradeRecord> inSample, List<TradeRecord> outOfSample,
        int embargoed, DateTime boundary)
    {
        InSample        = inSample;
        OutOfSample     = outOfSample;
        EmbargoedCount  = embargoed;
        Boundary        = boundary;
        InSampleEdge    = EdgeTest.FromSamples(inSample.Select(t => t.RMultiple).ToList());
        OutOfSampleEdge = EdgeTest.FromSamples(outOfSample.Select(t => t.RMultiple).ToList());
    }

    /// <summary>
    /// How much of the in-sample edge failed to survive. Positive means the result
    /// got worse out of sample, which is the signature of a fit rather than an edge.
    /// </summary>
    public decimal Degradation => InSampleEdge.MeanR - OutOfSampleEdge.MeanR;

    /// <summary>
    /// True when in-sample showed an edge and out-of-sample did not, on a sample
    /// large enough for the absence to mean something. An under-sampled
    /// out-of-sample side is reported as insufficient evidence, never as a failure:
    /// a test that could not have been passed was not failed.
    /// </summary>
    public bool FailedOutOfSample =>
        InSampleEdge.Verdict    == EdgeVerdict.EdgePresent &&
        InSampleEdge.MeanR      > 0 &&
        OutOfSampleEdge.Verdict != EdgeVerdict.InsufficientEvidence &&
        OutOfSampleEdge.MeanR   < InSampleEdge.MeanR / 2m;

    /// <summary>Splits so that <paramref name="inSampleFraction"/> of the trades, earliest first, are in-sample.</summary>
    public static SampleSplit ByFraction(IEnumerable<TradeRecord> trades, double inSampleFraction,
        TimeSpan? embargo = null)
    {
        if (inSampleFraction <= 0 || inSampleFraction >= 1)
            throw new ArgumentOutOfRangeException(nameof(inSampleFraction),
                inSampleFraction, "Must be strictly between 0 and 1 — a split that empties one side tests nothing.");

        var ordered = trades.OrderBy(t => t.EnteredAt).ToList();
        if (ordered.Count == 0) return Empty();

        int cut = (int)Math.Round(ordered.Count * inSampleFraction, MidpointRounding.AwayFromZero);
        cut = Math.Clamp(cut, 1, ordered.Count - 1);

        var boundary = ordered[cut].EnteredAt;
        return Build(ordered, boundary, embargo);
    }

    /// <summary>Splits at a fixed date: before it is in-sample, on or after it is out.</summary>
    public static SampleSplit ByDate(IEnumerable<TradeRecord> trades, DateTime boundary,
        TimeSpan? embargo = null)
    {
        var ordered = trades.OrderBy(t => t.EnteredAt).ToList();
        return ordered.Count == 0 ? Empty() : Build(ordered, boundary, embargo);
    }

    private static SampleSplit Build(List<TradeRecord> ordered, DateTime boundary, TimeSpan? embargo)
    {
        var inSample = ordered.Where(t => t.EnteredAt < boundary).ToList();
        var after    = ordered.Where(t => t.EnteredAt >= boundary).ToList();

        // The embargo drops the trades immediately after the boundary. A position
        // opened in-sample can still be open across it, and its outcome would then
        // be informed by the same market move the in-sample side already saw.
        if (embargo is not { } gap || gap <= TimeSpan.Zero)
            return new SampleSplit(inSample, after, 0, boundary);

        var outOfSample = after.Where(t => t.EnteredAt >= boundary + gap).ToList();
        return new SampleSplit(inSample, outOfSample, after.Count - outOfSample.Count, boundary);
    }

    private static SampleSplit Empty() =>
        new(new List<TradeRecord>(), new List<TradeRecord>(), 0, DateTime.MinValue);

    /// <summary>Two lines: what each side showed, and whether the result survived.</summary>
    public string Describe()
    {
        string embargoed = EmbargoedCount > 0 ? $" ({EmbargoedCount} embargoed)" : "";
        string verdict = FailedOutOfSample
            ? $"FAILED out of sample — {Degradation:+0.000;-0.000}R of the in-sample edge did not survive"
            : OutOfSampleEdge.Verdict == EdgeVerdict.InsufficientEvidence
                ? "out-of-sample sample too small to conclude anything"
                : "held up out of sample";

        return $"IS  {InSampleEdge.Describe()}\n" +
               $"OOS {OutOfSampleEdge.Describe()}{embargoed}\n" +
               $"    {verdict}";
    }
}
