using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Modules;

public class FalseBreakoutDetectorTests
{
    private FalseBreakoutDetector CreateDetector(int maxMinutesOrb = 15, int tfMinutes = 5)
    {
        var cfg = new ModuleConfig
        {
            FBMaxTimeOutsideMinutesOrb = maxMinutesOrb,
            FBMaxTimeOutsideMinutesSR  = 60,
            FBMaxPenetrationPctOrb     = 0.30m,
            FBMaxPenetrationPctSR      = 0.25m,
            FBMinRejectionBodyPct      = 0.50m,
            FBMaxTrendDayScore         = 60,
            ExecutionTFMinutes         = tfMinutes,
            TickSize                   = 0.25m,
        };
        return new FalseBreakoutDetector(cfg);
    }

    [Fact]
    public void OrbTracker_DetectsBreakoutAbove()
    {
        var det = CreateDetector();
        var today = DateTime.UtcNow.Date;

        var bar = new Bar(today.AddHours(10), 100m, 102m, 99.5m, 101.5m, 1000);
        det.OnBar(bar, today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true,
            vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.True(det.OrbTracker.BreakoutActive);
        Assert.Equal(Direction.Long, det.OrbTracker.BreakoutDirection);
        Assert.Equal(0, det.OrbTracker.BarsInBreakout);
    }

    [Fact]
    public void OrbTracker_DetectsBreakoutBelow()
    {
        var det = CreateDetector();
        var today = DateTime.UtcNow.Date;
        var bar = new Bar(today.AddHours(10), 100m, 100.5m, 98m, 98.5m, 1000);
        det.OnBar(bar, today, orbHigh: 101m, orbLow: 99m, orbFormed: true,
            vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.True(det.OrbTracker.BreakoutActive);
        Assert.Equal(Direction.Short, det.OrbTracker.BreakoutDirection);
    }

    [Fact]
    public void OrbTracker_ExpiresAfterMaxBars()
    {
        var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5); // 3 bars max
        var today = DateTime.UtcNow.Date;

        // Detection bar (BarsInBreakout = 0) + 4 continuation bars → BarsInBreakout = 4 > 3 → expires
        for (int i = 0; i < 5; i++)
        {
            var bar = new Bar(today.AddHours(10).AddMinutes(i * 5), 102m, 103m, 101.5m, 102.5m, 100);
            det.OnBar(bar, today, orbHigh: 101m, orbLow: 99m, orbFormed: true,
                vwap: 100m, trendBullScore: 30, trendBearScore: 30);
        }

        Assert.False(det.OrbTracker.BreakoutActive);
        Assert.False(det.OrbTracker.IsActivated);
    }

    [Fact]
    public void OrbTracker_ActivatesOnRejection()
    {
        var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
        var today = DateTime.UtcNow.Date;

        // Bar 1: breakout above (pen = 0.5/2 = 0.25 < 0.30)
        det.OnBar(new Bar(today.AddHours(10), 100m, 101.5m, 99.5m, 101.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);
        Assert.True(det.OrbTracker.BreakoutActive);

        // Bar 2: closes back inside with strong body (body = |100.2-101.2| = 1.0, range = 101.3-100 = 1.3, ratio = 0.77 > 0.50)
        det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101.2m, 101.3m, 100m, 100.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.True(det.OrbTracker.IsActivated);
        Assert.Equal(Direction.Long, det.OrbTracker.BreakoutDirection);
    }

    [Fact]
    public void OrbTracker_RejectsWeakBody()
    {
        var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
        var today = DateTime.UtcNow.Date;

        det.OnBar(new Bar(today.AddHours(10), 100m, 101.5m, 99.5m, 101.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        // Weak body: O=101, C=100.9 (body=0.1, range=101.3-99.8=1.5, ratio=0.067 < 0.50)
        det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101.3m, 99.8m, 100.9m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.False(det.OrbTracker.IsActivated);
    }

    [Fact]
    public void OrbTracker_RejectsExcessivePenetration()
    {
        var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
        var today = DateTime.UtcNow.Date;
        // ORB range = 2 (101-99). Max pen = 30% = 0.6. High = 102 → pen = 1.0/2 = 0.50 > 0.30
        det.OnBar(new Bar(today.AddHours(10), 100m, 102m, 99.5m, 101.5m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);
        // Close back inside with strong body
        det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.False(det.OrbTracker.IsActivated);
    }

    [Fact]
    public void OrbTracker_RejectsWrongVwapSide()
    {
        var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
        var today = DateTime.UtcNow.Date;

        // Breakout above with small pen
        det.OnBar(new Bar(today.AddHours(10), 100.5m, 101.3m, 100m, 101.1m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 101.5m, // VWAP above range = wrong side
            trendBullScore: 30, trendBearScore: 30);
        // Close back inside with strong body
        det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 101.5m, trendBullScore: 30, trendBearScore: 30);

        Assert.False(det.OrbTracker.IsActivated);
    }

    [Fact]
    public void OrbTracker_RejectsHighTrendScore()
    {
        var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
        var today = DateTime.UtcNow.Date;

        // Breakout above — opposing score is bullScore (broke in bull direction)
        det.OnBar(new Bar(today.AddHours(10), 100.5m, 101.3m, 100m, 101.1m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m,
            trendBullScore: 80, trendBearScore: 30); // bull=80 > 60 threshold
        det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m,
            trendBullScore: 80, trendBearScore: 30);

        Assert.False(det.OrbTracker.IsActivated);
    }

    [Fact]
    public void SessionRangeTracker_UsesLockedRange()
    {
        var det = CreateDetector();
        var today = DateTime.UtcNow.Date;

        det.OnSessionStart(priorSessionHigh: 100m, priorSessionLow: 96m);
        Assert.True(det.SessionRangeTracker.HasReferenceRange);

        // Bar breaks below Asia low
        det.OnBar(new Bar(today.AddHours(10), 96.5m, 97m, 95m, 95.5m, 100), today,
            orbHigh: 0, orbLow: 0, orbFormed: false, vwap: 98m, trendBullScore: 30, trendBearScore: 30);

        Assert.True(det.SessionRangeTracker.BreakoutActive);
        Assert.Equal(Direction.Short, det.SessionRangeTracker.BreakoutDirection);
    }

    [Fact]
    public void CompoundFakeout_BothActivateSameDirection()
    {
        var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
        var today = DateTime.UtcNow.Date;

        det.OnSessionStart(priorSessionHigh: 101m, priorSessionLow: 99m);

        // Bar 1: breakout above both ranges
        det.OnBar(new Bar(today.AddHours(10), 100.5m, 101.3m, 100m, 101.1m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        // Bar 2: rejection back inside both
        det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.True(det.OrbTracker.IsActivated);
        Assert.True(det.SessionRangeTracker.IsActivated);
        Assert.True(det.IsCompoundFakeout);
    }

    [Fact]
    public void NewSession_ResetsAllState()
    {
        var det = CreateDetector();
        var today = DateTime.UtcNow.Date;

        det.OnSessionStart(100m, 96m);
        det.OnBar(new Bar(today.AddHours(10), 100m, 101.5m, 99.5m, 101.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        det.NewSession(today.AddDays(1));

        Assert.False(det.OrbTracker.BreakoutActive);
        Assert.False(det.OrbTracker.IsActivated);
        Assert.False(det.SessionRangeTracker.HasReferenceRange);
    }

    [Fact]
    public void OrbTracker_NoActivationWhenOrbNotFormed()
    {
        var det = CreateDetector();
        var today = DateTime.UtcNow.Date;

        det.OnBar(new Bar(today.AddHours(10), 100m, 102m, 99.5m, 101.5m, 1000), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: false,
            vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.False(det.OrbTracker.BreakoutActive);
    }

    [Fact]
    public void OrbTracker_TickUpdatesSweepExtreme()
    {
        var det = CreateDetector();
        var today = DateTime.UtcNow.Date;

        det.OnBar(new Bar(today.AddHours(10), 100m, 101.5m, 99.5m, 101.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);

        Assert.Equal(101.5m, det.OrbTracker.SweepHigh);

        det.OnTick(102m, today.AddHours(10).AddMinutes(1));
        Assert.Equal(102m, det.OrbTracker.SweepHigh);
    }

    [Fact]
    public void ClearActivation_ResetsForNextBreakout()
    {
        var det = CreateDetector(maxMinutesOrb: 15, tfMinutes: 5);
        var today = DateTime.UtcNow.Date;

        // Activate
        det.OnBar(new Bar(today.AddHours(10), 100m, 101.3m, 99.5m, 101.1m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);
        det.OnBar(new Bar(today.AddHours(10).AddMinutes(5), 101m, 101m, 100m, 100.2m, 100), today,
            orbHigh: 101m, orbLow: 99m, orbFormed: true, vwap: 100m, trendBullScore: 30, trendBearScore: 30);
        Assert.True(det.OrbTracker.IsActivated);

        det.OrbTracker.ClearActivation();
        Assert.False(det.OrbTracker.IsActivated);
        Assert.False(det.OrbTracker.BreakoutActive);
    }
}
