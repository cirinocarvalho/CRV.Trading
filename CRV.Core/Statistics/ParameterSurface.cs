namespace CRV.Core.Statistics;

/// <summary>One cell of a sweep: a parameter value and what the trades at that value showed.</summary>
public sealed record ParameterPoint(string Label, decimal Value, EdgeTest Edge);

/// <summary>
/// A one-dimensional parameter sweep, read for stability rather than for a winner.
/// <para>
/// The 30-minute opening range was chosen, not validated: no neighbouring duration
/// was ever tested, so there is no evidence the result is insensitive to the choice.
/// Sweeping fixes that only if the sweep is read correctly. The best cell in a table
/// is always positive by construction — with five cells of pure noise, one of them
/// will look excellent. What distinguishes a parameter worth trading is that its
/// neighbours agree: if 30 works and 15 and 60 do not, the 30 is an artefact of
/// having guessed exactly right, and live will not oblige.
/// </para>
/// </summary>
public sealed class ParameterSurface
{
    public IReadOnlyList<ParameterPoint> Points { get; }

    public ParameterSurface(IEnumerable<ParameterPoint> points)
        => Points = points.OrderBy(p => p.Value).ToList();

    /// <summary>The highest-scoring cell. Reported for reference — never a recommendation on its own.</summary>
    public ParameterPoint? Best => Points.Count == 0
        ? null
        : Points.Aggregate((a, b) => b.Edge.MeanR > a.Edge.MeanR ? b : a);

    /// <summary>
    /// The longest run of adjacent cells that each show a positive edge in their own
    /// right, and none of which is distinguishable from its neighbour. Empty when no
    /// such run of at least three exists.
    /// </summary>
    public IReadOnlyList<ParameterPoint> StableRegion => _region ??= FindRegion();
    private IReadOnlyList<ParameterPoint>? _region;

    public bool HasStableRegion => !IsInert && StableRegion.Count >= MinimumRegion;

    /// <summary>
    /// True when every cell scored identically, which means the parameter never
    /// reached the strategy — sweeping the opening-range duration against a setup
    /// that fades the previous session's range, say. Identical rows read as strong
    /// agreement, so this has to be called out rather than left to be misread.
    /// </summary>
    public bool IsInert =>
        Points.Count > 1 &&
        Points.All(p => p.Edge.Count == Points[0].Edge.Count &&
                        p.Edge.MeanR == Points[0].Edge.MeanR);

    /// <summary>
    /// The centre of the stable region — the value furthest from where the result
    /// falls away — or null when there is no region to recommend from.
    /// </summary>
    public ParameterPoint? Recommended =>
        HasStableRegion ? StableRegion[StableRegion.Count / 2] : null;

    /// <summary>A region needs a neighbour on each side; two adjacent cells prove nothing.</summary>
    public const int MinimumRegion = 3;

    private IReadOnlyList<ParameterPoint> FindRegion()
    {
        if (Points.Count < MinimumRegion) return Array.Empty<ParameterPoint>();

        var best = new List<ParameterPoint>();
        var run  = new List<ParameterPoint>();

        foreach (var p in Points)
        {
            // Each cell must show an edge on its own. A cell that is merely not
            // negative — +0.05R with an interval straddling zero — is indistinguishable
            // from noise, and letting it extend a region would make almost any surface
            // "stable", since adjacent noisy cells rarely differ significantly either.
            bool usable = p.Edge.Verdict == EdgeVerdict.EdgePresent && p.Edge.MeanR > 0;

            // A neighbour that scores differently in a statistically meaningful way
            // ends the run: the result is then sensitive to the parameter, which is
            // the thing a stable region is defined by not being.
            bool continues = usable && run.Count > 0 &&
                             !EdgeTest.Differ(run[^1].Edge, p.Edge);

            if (continues) run.Add(p);
            else run = usable ? new List<ParameterPoint> { p } : new List<ParameterPoint>();

            if (run.Count > best.Count) best = new List<ParameterPoint>(run);
        }

        return best.Count >= MinimumRegion ? best : Array.Empty<ParameterPoint>();
    }

    /// <summary>A table plus the one sentence that matters.</summary>
    public string Describe()
    {
        if (Points.Count == 0) return "no cells swept";

        var lines = Points.Select(p =>
        {
            string mark = StableRegion.Contains(p) ? " *" : "  ";
            return $"{mark} {p.Label,6}  {p.Edge.Describe()}";
        });

        string verdict = IsInert
            ? "every cell scored identically — this parameter did not change the enabled setups, " +
              "so the sweep measured nothing"
            : HasStableRegion
            ? $"stable across {StableRegion[0].Label}-{StableRegion[^1].Label}; recommend {Recommended!.Label}"
            : Best is { } b && b.Edge.MeanR > 0
                ? $"no stable region — {b.Label} scores best but its neighbours do not agree, " +
                   "which is what an artefact looks like"
                : "no stable region and nothing positive to recommend";

        return string.Join('\n', lines) + "\n" + verdict;
    }
}
