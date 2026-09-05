namespace CRV.Core.Statistics;

/// <summary>What a sample of R-multiples supports being said about it.</summary>
public enum EdgeVerdict
{
    /// <summary>Too few trades to conclude anything. Not the same as no edge.</summary>
    InsufficientEvidence,

    /// <summary>Enough trades to look, and the interval straddles zero.</summary>
    NoMeasurableEdge,

    /// <summary>The interval excludes zero. Sign says which direction.</summary>
    EdgePresent,
}

/// <summary>
/// The inference the existing <c>PerformanceMetrics</c> does not do.
/// <para>
/// A run reporting "+$228, expectancy +$1" reads as a small win. Over 176 trades
/// that mean is -0.0029R with a 95% interval of [-0.177, +0.171] and a t of -0.03:
/// the honest statement is that the edge, if any, is smaller than the measurement.
/// Without this, a parameter sweep just picks the largest number in the table, and
/// the largest number in a table of noise is still noise.
/// </para>
/// </summary>
public sealed record EdgeTest(
    int     Count,
    decimal MeanR,
    decimal StandardDeviation,
    decimal StandardError,
    decimal TStatistic,
    decimal LowerBound,
    decimal UpperBound,
    EdgeVerdict Verdict)
{
    /// <summary>
    /// Below this many trades no verdict is offered. Twenty is not a magic number —
    /// it is the point below which the t-interval on a typical sd of ~1.2R is wider
    /// than any expectancy this system has ever produced, so the answer would be
    /// "no measurable edge" regardless of the data. Saying "insufficient evidence"
    /// is the truthful version of that.
    /// </summary>
    public const int MinimumSample = 20;

    /// <summary>True when the interval excludes zero on an adequate sample.</summary>
    public bool IsSignificant => Verdict == EdgeVerdict.EdgePresent;

    /// <summary>
    /// Trades required for this mean and spread to reach significance, or null when
    /// the mean is zero and no sample size would do it. Usually a sobering number.
    /// </summary>
    public int? TradesNeededForSignificance
    {
        get
        {
            if (MeanR == 0 || StandardDeviation <= 0) return null;
            double t = StudentT.TwoSided95(Math.Max(1, Count - 1));
            double needed = Math.Pow(t * (double)StandardDeviation / Math.Abs((double)MeanR), 2);
            return needed > int.MaxValue ? int.MaxValue : (int)Math.Ceiling(needed);
        }
    }

    /// <summary>Builds from raw R-multiples, one per trade.</summary>
    public static EdgeTest FromSamples(IReadOnlyCollection<decimal> rMultiples)
    {
        int n = rMultiples.Count;
        if (n == 0) return Empty;

        decimal mean = rMultiples.Sum() / n;
        if (n == 1) return FromSummary(1, mean, 0m);

        // Sample standard deviation: n-1, because these trades are a sample of the
        // strategy's behaviour, not the whole of it.
        double ss = rMultiples.Sum(r => Math.Pow((double)(r - mean), 2));
        return FromSummary(n, mean, (decimal)Math.Sqrt(ss / (n - 1)));
    }

    /// <summary>Builds from an already-computed mean and standard deviation.</summary>
    public static EdgeTest FromSummary(int n, decimal meanR, decimal sdR)
    {
        if (n <= 0) return Empty;

        decimal se = n > 1 && sdR > 0 ? sdR / (decimal)Math.Sqrt(n) : 0m;
        decimal t  = se > 0 ? meanR / se : 0m;

        decimal half = se > 0 ? (decimal)StudentT.TwoSided95(n - 1) * se : 0m;
        decimal lo = meanR - half, hi = meanR + half;

        var verdict = n < MinimumSample                ? EdgeVerdict.InsufficientEvidence
                    : se <= 0                          ? EdgeVerdict.InsufficientEvidence
                    : lo > 0 || hi < 0                 ? EdgeVerdict.EdgePresent
                    :                                    EdgeVerdict.NoMeasurableEdge;

        return new EdgeTest(n, meanR, sdR, se, t, lo, hi, verdict);
    }

    /// <summary>
    /// Whether two variants can be told apart — a Welch comparison, guarded so that
    /// no conclusion is drawn when either side is under-sampled. A sweep that skips
    /// this guard will confidently rank fourteen trades above two hundred.
    /// </summary>
    public static bool Differ(EdgeTest a, EdgeTest b)
    {
        if (a.Count < MinimumSample || b.Count < MinimumSample) return false;
        if (a.StandardError <= 0 || b.StandardError <= 0) return false;

        double seA = (double)a.StandardError, seB = (double)b.StandardError;
        double se  = Math.Sqrt(seA * seA + seB * seB);
        double t   = Math.Abs((double)(a.MeanR - b.MeanR)) / se;

        // Welch-Satterthwaite degrees of freedom.
        double vA = seA * seA, vB = seB * seB;
        double df = Math.Pow(vA + vB, 2)
                  / (vA * vA / Math.Max(1, a.Count - 1) + vB * vB / Math.Max(1, b.Count - 1));

        return t > StudentT.TwoSided95((int)Math.Max(1, Math.Floor(df)));
    }

    private static readonly EdgeTest Empty =
        new(0, 0m, 0m, 0m, 0m, 0m, 0m, EdgeVerdict.InsufficientEvidence);

    /// <summary>One line, safe to log or show: mean, interval, and what it supports.</summary>
    public string Describe() => Count == 0
        ? "no trades"
        : Verdict switch
        {
            EdgeVerdict.InsufficientEvidence =>
                $"{MeanR:+0.000;-0.000}R over {Count} trades — insufficient evidence (needs {MinimumSample})",
            EdgeVerdict.NoMeasurableEdge =>
                $"{MeanR:+0.000;-0.000}R over {Count} trades, 95% CI [{LowerBound:+0.000;-0.000}, {UpperBound:+0.000;-0.000}] — no measurable edge",
            _ =>
                $"{MeanR:+0.000;-0.000}R over {Count} trades, 95% CI [{LowerBound:+0.000;-0.000}, {UpperBound:+0.000;-0.000}], t={TStatistic:0.00} — edge present",
        };
}
