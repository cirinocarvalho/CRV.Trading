using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Modules;

public class OpeningDriveDetectorTests
{
    private static ModuleConfig DefaultConfig() => new()
    {
        DriveRangeAtrMult = 0.80m,
        MaxDrivePullback = 0.35m,
        DriveBullBearRatio = 2,
    };

    private static readonly DateTime T = new(2026, 3, 12, 10, 0, 0, DateTimeKind.Utc);

    // ── 1. Detects bullish drive ────────────────────────────────────

    [Fact]
    public void DetectsBullishDrive()
    {
        var detector = new OpeningDriveDetector(DefaultConfig());

        // 6 bull bars + 1 bear bar → bullCount(6) > bearCount(1) * 2
        for (int i = 0; i < 6; i++)
            detector.AccumulateOrbBar(new Bar(T.AddMinutes(i), Open: 5000 + i, High: 5005 + i, Low: 4998 + i, Close: 5004 + i, Volume: 100));

        // 1 bear bar
        detector.AccumulateOrbBar(new Bar(T.AddMinutes(6), Open: 5010m, High: 5011m, Low: 5005m, Close: 5006m, Volume: 100));

        detector.CurrentAtr = 50m;
        detector.CurrentVwap = 5000m; // close > vwap

        // orbRange = 45, 45/50 = 0.90 >= 0.80
        detector.FreezeAtOrbClose(orbRange: 45m);

        Assert.True(detector.OpeningDriveBull);
        Assert.False(detector.OpeningDriveBear);
        Assert.True(detector.OpeningDriveConfirmed);
        Assert.Equal(0.90m, detector.DriveRangePctATR);
    }

    // ── 2. Detects bearish drive ────────────────────────────────────

    [Fact]
    public void DetectsBearishDrive()
    {
        var detector = new OpeningDriveDetector(DefaultConfig());

        // 6 bear bars + 1 bull bar → bearCount(6) > bullCount(1) * 2
        for (int i = 0; i < 6; i++)
            detector.AccumulateOrbBar(new Bar(T.AddMinutes(i), Open: 5010 - i, High: 5012 - i, Low: 5005 - i, Close: 5006 - i, Volume: 100));

        // 1 bull bar
        detector.AccumulateOrbBar(new Bar(T.AddMinutes(6), Open: 5000m, High: 5005m, Low: 4999m, Close: 5004m, Volume: 100));

        detector.CurrentAtr = 50m;
        detector.CurrentVwap = 5010m; // driveLow <= vwap

        detector.FreezeAtOrbClose(orbRange: 45m);

        Assert.False(detector.OpeningDriveBull);
        Assert.True(detector.OpeningDriveBear);
        Assert.True(detector.OpeningDriveConfirmed);
    }

    // ── 3. No drive when range too small ────────────────────────────

    [Fact]
    public void NoDriveWhenRangeTooSmall()
    {
        var detector = new OpeningDriveDetector(DefaultConfig());

        for (int i = 0; i < 6; i++)
            detector.AccumulateOrbBar(new Bar(T.AddMinutes(i), Open: 5000 + i, High: 5005 + i, Low: 4998 + i, Close: 5004 + i, Volume: 100));

        detector.CurrentAtr = 50m;
        detector.CurrentVwap = 5000m;

        // orbRange = 30, 30/50 = 0.60 < 0.80 → range too small
        detector.FreezeAtOrbClose(orbRange: 30m);

        Assert.False(detector.OpeningDriveBull);
        Assert.False(detector.OpeningDriveBear);
        Assert.False(detector.OpeningDriveConfirmed);
    }

    // ── 4. NewSession resets ────────────────────────────────────────

    [Fact]
    public void NewSessionResets()
    {
        var detector = new OpeningDriveDetector(DefaultConfig());

        for (int i = 0; i < 6; i++)
            detector.AccumulateOrbBar(new Bar(T.AddMinutes(i), Open: 5000 + i, High: 5005 + i, Low: 4998 + i, Close: 5004 + i, Volume: 100));

        detector.CurrentAtr = 50m;
        detector.CurrentVwap = 5000m;
        detector.FreezeAtOrbClose(orbRange: 45m);
        Assert.True(detector.OpeningDriveConfirmed);

        detector.NewSession(new DateTime(2026, 3, 13));

        Assert.False(detector.OpeningDriveBull);
        Assert.False(detector.OpeningDriveBear);
        Assert.False(detector.OpeningDriveConfirmed);
        Assert.Equal(0m, detector.DriveRangePctATR);
        Assert.Equal(0m, detector.DrivePullbackPct);
        Assert.Equal(0m, detector.CurrentAtr);
        Assert.Equal(0m, detector.CurrentVwap);
    }
}
