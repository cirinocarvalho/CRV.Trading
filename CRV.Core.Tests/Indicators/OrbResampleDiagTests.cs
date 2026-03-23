using CRV.Core.Indicators;
using CRV.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace CRV.Core.Tests.Indicators;

/// <summary>
/// Diagnostic tests that trace ORB detection across 1/2/5/15/30/60-min resampled bars.
/// Uses a synthetic day: pre-market 7:28 ET, ORB 9:30-10:00 ET, trading until 16:00 ET.
/// Mirrors the real CSV structure (timestamps stored as UTC, originally -05:00 offset).
/// </summary>
public class OrbResampleDiagTests
{
    private readonly ITestOutputHelper _out;
    // CSV is EST (-05:00), so 9:30 ET = 14:30 UTC
    private static readonly TimeZoneInfo EtTz =
        TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public OrbResampleDiagTests(ITestOutputHelper output) => _out = output;

    // Build a full day of 1-min bars: 7:28 ET → 16:59 ET  (matches real CSV)
    private static List<Bar> Build1MinBars()
    {
        var bars = new List<Bar>();
        // 7:28 ET = 12:28 UTC
        var start = new DateTime(2026, 3, 6, 12, 28, 0, DateTimeKind.Utc);
        // 16:59 ET = 21:59 UTC
        var end   = new DateTime(2026, 3, 6, 21, 59, 0, DateTimeKind.Utc);
        decimal price = 24000m;
        for (var t = start; t <= end; t = t.AddMinutes(1))
        {
            price += (decimal)(new Random(t.Minute).NextDouble() - 0.5) * 10m;
            bars.Add(new Bar(t, price, price + 5m, price - 5m, price + 1m, 100));
        }
        return bars;
    }

    private static List<Bar> Resample(IEnumerable<Bar> source, int tfMinutes)
    {
        if (tfMinutes <= 1) return source.ToList();
        var result = new List<Bar>();
        DateTime? bucketKey = null;
        decimal open = 0, high = 0, low = 0, close = 0;
        long vol = 0;
        foreach (var b in source)
        {
            int totalMins  = b.Time.Hour * 60 + b.Time.Minute;
            int bucketMins = totalMins / tfMinutes * tfMinutes;
            var key = b.Time.Date.AddMinutes(bucketMins);
            if (bucketKey == null || key != bucketKey)
            {
                if (bucketKey != null)
                    result.Add(new Bar(bucketKey.Value, open, high, low, close, vol));
                bucketKey = key; open = b.Open; high = b.High; low = b.Low; close = b.Close; vol = b.Volume;
            }
            else
            {
                if (b.High > high) high = b.High;
                if (b.Low  < low)  low  = b.Low;
                close = b.Close; vol += b.Volume;
            }
        }
        if (bucketKey != null)
            result.Add(new Bar(bucketKey.Value, open, high, low, close, vol));
        return result;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void OrbIsSet_ForAllTimeframes(int tf)
    {
        var raw      = Build1MinBars();
        var resampled = Resample(raw, tf);

        _out.WriteLine($"\nTF={tf}min: {resampled.Count} total bars");

        var orbCalc = new OrbCalculator(
            new TimeOnly(9, 30), new TimeOnly(10, 0), "America/New_York", tf);

        int orbBarsProcessed = 0;
        bool orbSet = false;
        foreach (var bar in resampled)
        {
            var etTime = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, EtTz);
            orbCalc.Update(bar);
            bool inOrbWindow = etTime.Hour * 60 + etTime.Minute < 10 * 60 &&
                               etTime.Hour * 60 + etTime.Minute + tf > 9 * 60 + 30;
            if (inOrbWindow) orbBarsProcessed++;
            if (!orbSet && orbCalc.IsSet)
            {
                orbSet = true;
                _out.WriteLine($"  ORB set after bar at {etTime:HH:mm} ET | Range={orbCalc.OrbRange:F2} H={orbCalc.OrbHigh:F2} L={orbCalc.OrbLow:F2}");
            }
        }

        int postOrbBars = resampled.Count(b => TimeZoneInfo.ConvertTimeFromUtc(b.Time, EtTz).Hour >= 10);
        _out.WriteLine($"  ORB bars processed: {orbBarsProcessed} | ORB set: {orbSet} | OrbRange={orbCalc.OrbRange:F2}");
        _out.WriteLine($"  Total bars: {resampled.Count} | Post-ORB bars: {postOrbBars}");

        Assert.True(orbSet,    $"TF={tf}: ORB was never set");
        Assert.True(orbCalc.OrbRange > 0, $"TF={tf}: ORB range is zero");
        Assert.True(resampled.Count > 0,  $"TF={tf}: No bars produced after resampling");
        // Post-ORB bars are what matters for trading — must have at least 1
        Assert.True(postOrbBars >= 1, $"TF={tf}: No post-ORB bars available for trading (total={resampled.Count})");
    }
}

