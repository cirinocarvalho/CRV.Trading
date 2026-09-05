using CRV.Core.Statistics;
using Xunit;

namespace CRV.Core.Tests.Statistics;

/// <summary>
/// The 30-minute opening range, and the per-instrument variants (MGC 08:20, MCL
/// 09:00), were chosen rather than validated — no neighbouring duration was ever
/// tested. The danger in fixing that by sweeping is picking the winner: the best
/// cell in a table of noise is still noise. What a sweep should return is a region
/// whose neighbours agree, or nothing.
/// </summary>
public class ParameterSurfaceTests
{
    private static ParameterPoint P(int minutes, decimal meanR, int n = 120, decimal sd = 1.2m)
        => new($"{minutes}m", minutes, EdgeTest.FromSummary(n, meanR, sd));

    // ── A stable region ───────────────────────────────────────────

    [Fact]
    public void ARidgeWhoseNeighboursAgreeIsReportedAsStable()
    {
        // 15/30/60 all positive and close together: the result does not depend on
        // having guessed the parameter exactly.
        var surface = new ParameterSurface(new[]
        {
            P(5,  0.02m), P(15, 0.34m), P(30, 0.38m), P(60, 0.31m), P(120, 0.05m),
        });

        Assert.True(surface.HasStableRegion);
        Assert.Equal(new[] { "15m", "30m", "60m" }, surface.StableRegion.Select(p => p.Label));
        Assert.Equal("30m", surface.Best!.Label);
    }

    [Fact]
    public void TheRecommendationIsTheMiddleOfTheRegionNotItsPeak()
    {
        // Peak at 60 but the whole ridge holds; the centre is the robust choice
        // because it is furthest from the edges where the result falls away.
        var surface = new ParameterSurface(new[]
        {
            P(5, -0.10m), P(15, 0.30m), P(30, 0.32m), P(60, 0.40m), P(120, -0.05m),
        });

        Assert.Equal("60m", surface.Best!.Label);
        Assert.Equal("30m", surface.Recommended!.Label);
    }

    // ── An isolated spike ─────────────────────────────────────────

    [Fact]
    public void AnIsolatedPeakIsNotAStableRegion()
    {
        // 30 looks wonderful and both neighbours are negative. That is a hole in
        // the noise, not a parameter worth trading.
        var surface = new ParameterSurface(new[]
        {
            P(5, -0.20m), P(15, -0.18m), P(30, 0.55m), P(60, -0.22m), P(120, -0.15m),
        });

        Assert.False(surface.HasStableRegion);
        Assert.Empty(surface.StableRegion);
        Assert.Null(surface.Recommended);
        Assert.Equal("30m", surface.Best!.Label);   // still reported, just not recommended
    }

    [Fact]
    public void ACellThatIsMerelyNotNegativeDoesNotExtendARegion()
    {
        // +0.05R over 120 trades has an interval straddling zero. Admitting it would
        // make almost every surface look stable, because two noisy neighbours rarely
        // differ significantly from each other either.
        var surface = new ParameterSurface(new[]
        {
            P(15, 0.34m), P(30, 0.38m), P(60, 0.31m), P(120, 0.05m),
        });

        Assert.Equal(EdgeVerdict.NoMeasurableEdge, surface.Points[^1].Edge.Verdict);
        Assert.DoesNotContain("120m", surface.StableRegion.Select(p => p.Label));
        Assert.Equal(3, surface.StableRegion.Count);
    }

    [Fact]
    public void ASurfaceWithNoPositiveCellsRecommendsNothing()
    {
        var surface = new ParameterSurface(new[]
        {
            P(5, -0.20m), P(15, -0.18m), P(30, -0.05m), P(60, -0.22m),
        });

        Assert.False(surface.HasStableRegion);
        Assert.Null(surface.Recommended);
    }

    // ── Under-sampled cells ───────────────────────────────────────

    [Fact]
    public void CellsWithTooFewTradesCannotAnchorARegion()
    {
        // Every cell is a 14-trade sample — the size of the best-looking cells in
        // the live book. None of them supports a conclusion, so neither does the row.
        var surface = new ParameterSurface(new[]
        {
            P(15, 0.70m, n: 14), P(30, 0.75m, n: 14), P(60, 0.72m, n: 14),
        });

        Assert.False(surface.HasStableRegion);
        Assert.Null(surface.Recommended);
        Assert.All(surface.Points, p => Assert.Equal(EdgeVerdict.InsufficientEvidence, p.Edge.Verdict));
    }

    [Fact]
    public void AnUnderSampledNeighbourBreaksTheRegion()
    {
        var surface = new ParameterSurface(new[]
        {
            P(15, 0.34m), P(30, 0.38m, n: 9), P(60, 0.31m),
        });
        Assert.False(surface.HasStableRegion);
    }

    // ── Ordering and shape ────────────────────────────────────────

    [Fact]
    public void PointsAreOrderedByParameterValueWhateverOrderTheyArrivedIn()
    {
        var surface = new ParameterSurface(new[] { P(60, 0.1m), P(5, 0.1m), P(30, 0.1m) });
        Assert.Equal(new[] { 5m, 30m, 60m }, surface.Points.Select(p => p.Value));
    }

    [Fact]
    public void TwoPointsAreTooFewToEstablishStability()
    {
        // A region needs a neighbour on each side to mean anything.
        var surface = new ParameterSurface(new[] { P(15, 0.35m), P(30, 0.38m) });
        Assert.False(surface.HasStableRegion);
    }

    // ── A sweep that varied nothing ───────────────────────────────

    [Fact]
    public void ASweepWhereEveryCellIsIdenticalIsFlaggedAsInert()
    {
        // Sweeping the ORB duration against a setup that fades the prior session's
        // range changes nothing, and six identical rows read as agreement when they
        // are actually silence. The surface has to say the parameter never applied.
        var surface = new ParameterSurface(new[]
        {
            P(5, 0.30m), P(15, 0.30m), P(30, 0.30m), P(60, 0.30m),
        });

        Assert.True(surface.IsInert);
        Assert.False(surface.HasStableRegion);
        Assert.Null(surface.Recommended);
        Assert.Contains("did not change", surface.Describe());
    }

    [Fact]
    public void ASweepWhoseCellsDifferIsNotInert()
    {
        var surface = new ParameterSurface(new[] { P(5, 0.02m), P(15, 0.34m), P(30, 0.38m) });
        Assert.False(surface.IsInert);
    }

    [Fact]
    public void ASingleCellIsNotCalledInert()
    {
        // One cell cannot be identical to a neighbour it does not have.
        Assert.False(new ParameterSurface(new[] { P(30, 0.3m) }).IsInert);
    }

    [Fact]
    public void AnEmptySweepIsNotAnError()
    {
        var surface = new ParameterSurface(Array.Empty<ParameterPoint>());
        Assert.Empty(surface.Points);
        Assert.Null(surface.Best);
        Assert.False(surface.HasStableRegion);
    }
}
