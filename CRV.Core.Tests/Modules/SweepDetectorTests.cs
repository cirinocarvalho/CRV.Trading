using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Modules;

public class SweepDetectorTests
{
    private static ModuleConfig DefaultConfig() => new()
    {
        MinTickPenetration = 0.50m,
        MinBodyReject = 1.00m,
        EqualLevelTolerance = 2.00m,
    };

    private static SweepDetector CreateDetector(ModuleConfig? cfg = null)
        => new(cfg ?? DefaultConfig());

    private static readonly DateTime T = new(2026, 3, 12, 14, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime TradingDate = new(2026, 3, 12);

    // ── 1. Bearish sweep of PDH ───────────────────────────────────

    [Fact]
    public void DetectsBearishSweepOfPDH()
    {
        var detector = CreateDetector();
        // PDH at 5100. Bar pokes above by more than 0.50 and closes below.
        // Upper wick must be >= 1.00
        detector.SetLevels(
            pdh: 5100m, pdl: 5000m,
            pwh: 0, pwl: 0, pmh: 0, pml: 0,
            sessionHigh: 0, sessionLow: 0,
            orbHigh: 0, orbLow: 0);

        // Open=5099, High=5101 (1.0 above PDH), Low=5097, Close=5098
        // upperWick = 5101 - max(5099, 5098) = 5101 - 5099 = 2.0 >= 1.0
        var bar = new Bar(T, Open: 5099m, High: 5101m, Low: 5097m, Close: 5098m, Volume: 100);
        detector.OnBar(bar, TradingDate);

        Assert.Single(detector.ActiveSweeps);
        var sweep = detector.ActiveSweeps[0];
        Assert.Equal(SweepType.PDH, sweep.Type);
        Assert.Equal(SweepDirection.Bear, sweep.Direction);
        Assert.Equal(5100m, sweep.Level);
        Assert.True(detector.AnyBearSweep);
        Assert.False(detector.AnyBullSweep);
    }

    // ── 2. Bullish sweep of PDL ───────────────────────────────────

    [Fact]
    public void DetectsBullishSweepOfPDL()
    {
        var detector = CreateDetector();
        // PDL at 5000. Bar pokes below by more than 0.50 and closes above.
        detector.SetLevels(
            pdh: 5100m, pdl: 5000m,
            pwh: 0, pwl: 0, pmh: 0, pml: 0,
            sessionHigh: 0, sessionLow: 0,
            orbHigh: 0, orbLow: 0);

        // Open=5001, High=5003, Low=4999 (1.0 below PDL), Close=5002
        // lowerWick = min(5001, 5002) - 4999 = 5001 - 4999 = 2.0 >= 1.0
        var bar = new Bar(T, Open: 5001m, High: 5003m, Low: 4999m, Close: 5002m, Volume: 100);
        detector.OnBar(bar, TradingDate);

        Assert.Single(detector.ActiveSweeps);
        var sweep = detector.ActiveSweeps[0];
        Assert.Equal(SweepType.PDL, sweep.Type);
        Assert.Equal(SweepDirection.Bull, sweep.Direction);
        Assert.Equal(5000m, sweep.Level);
        Assert.True(detector.AnyBullSweep);
        Assert.False(detector.AnyBearSweep);
    }

    // ── 3. No sweep when penetration too small ────────────────────

    [Fact]
    public void NoSweepWhenPenetrationTooSmall()
    {
        var detector = CreateDetector();
        detector.SetLevels(
            pdh: 5100m, pdl: 5000m,
            pwh: 0, pwl: 0, pmh: 0, pml: 0,
            sessionHigh: 0, sessionLow: 0,
            orbHigh: 0, orbLow: 0);

        // High=5100.25 → only 0.25 above PDH, less than MinTickPenetration of 0.50
        var bar = new Bar(T, Open: 5099m, High: 5100.25m, Low: 5097m, Close: 5098m, Volume: 100);
        detector.OnBar(bar, TradingDate);

        Assert.Empty(detector.ActiveSweeps);
        Assert.Null(detector.LastSweep);
    }

    // ── 4. No sweep when wick too small ───────────────────────────

    [Fact]
    public void NoSweepWhenWickTooSmall()
    {
        var detector = CreateDetector();
        detector.SetLevels(
            pdh: 5100m, pdl: 5000m,
            pwh: 0, pwl: 0, pmh: 0, pml: 0,
            sessionHigh: 0, sessionLow: 0,
            orbHigh: 0, orbLow: 0);

        // High=5101 (penetration=1.0, OK), but Open=5100.50 so
        // upperWick = 5101 - max(5100.50, 5098) = 5101 - 5100.50 = 0.50 < 1.0
        var bar = new Bar(T, Open: 5100.50m, High: 5101m, Low: 5097m, Close: 5098m, Volume: 100);
        detector.OnBar(bar, TradingDate);

        Assert.Empty(detector.ActiveSweeps);
    }

    // ── 5. Detects equal highs then sweep ─────────────────────────

    [Fact]
    public void DetectsEqualHighs()
    {
        var detector = CreateDetector();
        detector.SetLevels(
            pdh: 0, pdl: 0,
            pwh: 0, pwl: 0, pmh: 0, pml: 0,
            sessionHigh: 0, sessionLow: 0,
            orbHigh: 0, orbLow: 0);

        // Two bars with similar highs (within tolerance=2.00)
        var bar1 = new Bar(T, Open: 5090m, High: 5100m, Low: 5088m, Close: 5095m, Volume: 100);
        var bar2 = new Bar(T.AddMinutes(5), Open: 5092m, High: 5101m, Low: 5089m, Close: 5094m, Volume: 100);
        detector.OnBar(bar1, TradingDate);
        detector.OnBar(bar2, TradingDate);

        // Equal high level = (5100 + 5101) / 2 = 5100.50
        // Now a sweep bar that pokes above and reverses
        // High=5101.50 (1.0 above 5100.50, penetration OK), Close=5099 (below 5100.50)
        // upperWick = 5101.50 - max(5100, 5099) = 1.50 >= 1.0
        var sweepBar = new Bar(T.AddMinutes(10), Open: 5100m, High: 5101.50m, Low: 5098m, Close: 5099m, Volume: 100);
        detector.OnBar(sweepBar, TradingDate);

        var equalSweeps = detector.ActiveSweeps
            .Where(s => s.Type == SweepType.EqualHigh)
            .ToList();
        Assert.Single(equalSweeps);
        Assert.Equal(SweepDirection.Bear, equalSweeps[0].Direction);
    }

    // ── 6. Sweeps reset on new session ────────────────────────────

    [Fact]
    public void SweepsResetOnNewSession()
    {
        var detector = CreateDetector();
        detector.SetLevels(
            pdh: 5100m, pdl: 5000m,
            pwh: 0, pwl: 0, pmh: 0, pml: 0,
            sessionHigh: 0, sessionLow: 0,
            orbHigh: 0, orbLow: 0);

        var bar = new Bar(T, Open: 5099m, High: 5101m, Low: 5097m, Close: 5098m, Volume: 100);
        detector.OnBar(bar, TradingDate);
        Assert.NotEmpty(detector.ActiveSweeps);

        detector.NewSession(TradingDate.AddDays(1));

        Assert.Empty(detector.ActiveSweeps);
        Assert.False(detector.AnyBullSweep);
        Assert.False(detector.AnyBearSweep);
        Assert.Null(detector.LastSweep);
    }
}
