using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Modules;

public class ChopRegimeDetectorTests
{
    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static DateTime ToUtc(int hour, int minute, int day = 10)
        => TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 3, day, hour, minute, 0, DateTimeKind.Unspecified), EasternTz);

    private static Bar MakeBar(DateTime utcTime, decimal open, decimal high, decimal low, decimal close, long volume = 1000)
        => new(utcTime, open, high, low, close, volume);

    private static (ChopRegimeDetector det, OrbCalculator orb, VwapModel vwap, AtrIndicator atr)
        Build(ChopRegimeConfig? cfg = null, int orbTfMinutes = 5)
    {
        cfg ??= new ChopRegimeConfig();
        var orb  = new OrbCalculator(new TimeOnly(9, 30), new TimeOnly(10, 0), "America/New_York", orbTfMinutes);
        var vwap = new VwapModel(new ModuleConfig());
        var atr  = new AtrIndicator(period: 14);
        var det  = new ChopRegimeDetector(cfg, orb, vwap, atr);
        return (det, orb, vwap, atr);
    }

    /// <summary>Feed a bar through every indicator + detector in one call.</summary>
    private static void Feed(
        ChopRegimeDetector det, OrbCalculator orb, VwapModel vwap, AtrIndicator atr,
        Bar bar, DateTime tradingDate)
    {
        orb.Update(bar, tradingDate);
        atr.Update(bar);
        vwap.OnBar(bar, tradingDate);
        det.OnBar(bar, tradingDate);
    }

    // ─── Master switch ───────────────────────────────────────────────

    [Fact]
    public void MasterSwitchOff_ReturnsPending()
    {
        var (det, orb, vwap, atr) = Build(new ChopRegimeConfig { Enabled = false });
        var bar = MakeBar(ToUtc(9, 30), 5200m, 5210m, 5190m, 5205m);
        Feed(det, orb, vwap, atr, bar, new DateTime(2026, 3, 10));

        Assert.False(det.LastResult.IsChop);
        Assert.Equal(0, det.LastResult.Votes);
    }

    // ─── Config normalization ────────────────────────────────────────

    [Fact]
    public void Normalize_ClampsMinVotesToEnabledFilterCount()
    {
        var cfg = new ChopRegimeConfig
        {
            F1_RangeCompressionEnabled = true,
            F2_FlatVwapEnabled         = false,
            F3_WeakDriveEnabled        = false,
            F4_LowVolumeEnabled        = false,
            MinVotesForChop            = 4
        }.Normalize();

        Assert.Equal(1, cfg.MinVotesForChop);
    }

    [Fact]
    public void Normalize_DoesNotShrinkBelowOne_WhenAllDisabled()
    {
        var cfg = new ChopRegimeConfig
        {
            F1_RangeCompressionEnabled = false,
            F2_FlatVwapEnabled         = false,
            F3_WeakDriveEnabled        = false,
            F4_LowVolumeEnabled        = false,
            MinVotesForChop            = 5
        }.Normalize();

        Assert.Equal(1, cfg.MinVotesForChop);
    }

    // ─── Filter activation gates ─────────────────────────────────────

    [Fact]
    public void OrbNotSet_F1AndF3_AreInactive()
    {
        var (det, orb, vwap, atr) = Build();
        // Feed bars BEFORE the OR window — OR never forms
        var tradingDate = new DateTime(2026, 3, 10);
        var bar = MakeBar(ToUtc(9, 0), 5200m, 5205m, 5195m, 5200m);
        Feed(det, orb, vwap, atr, bar, tradingDate);

        Assert.False(orb.IsSet);
        Assert.False(det.LastResult.RangeCompression.Active);
        Assert.False(det.LastResult.WeakDrive.Active);
    }

    [Fact]
    public void DisabledFilter_IsInactive()
    {
        var cfg = new ChopRegimeConfig { F1_RangeCompressionEnabled = false };
        var (det, orb, vwap, atr) = Build(cfg);
        var bar = MakeBar(ToUtc(9, 0), 5200m, 5205m, 5195m, 5200m);
        Feed(det, orb, vwap, atr, bar, new DateTime(2026, 3, 10));

        Assert.False(det.LastResult.RangeCompression.Active);
    }

    // ─── Filter 4: Low Volume ────────────────────────────────────────

    [Fact]
    public void LowVolume_InactiveBeforeSmaWarmup()
    {
        var (det, orb, vwap, atr) = Build();
        // Feed 19 bars (period = 20) — SMA not ready
        var tradingDate = new DateTime(2026, 3, 10);
        for (int i = 0; i < 19; i++)
        {
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(i), 5200m, 5205m, 5195m, 5200m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        Assert.False(det.LastResult.LowVolume.Active);
    }

    [Fact]
    public void LowVolume_VotesWhenBarVolumeBelowAverage()
    {
        var (det, orb, vwap, atr) = Build();
        var tradingDate = new DateTime(2026, 3, 10);
        // Warm up VolumeSma with 20 bars at vol=1000
        for (int i = 0; i < 20; i++)
        {
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(i), 5200m, 5205m, 5195m, 5200m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        // Test bar at low volume — evaluated BEFORE the bar enters the SMA
        var test = MakeBar(ToUtc(9, 0).AddMinutes(20), 5200m, 5205m, 5195m, 5200m, volume: 500);
        Feed(det, orb, vwap, atr, test, tradingDate);

        Assert.True(det.LastResult.LowVolume.Active);
        Assert.True(det.LastResult.LowVolume.Voted);
        Assert.Equal(0.5m, det.LastResult.LowVolume.Value);
    }

    [Fact]
    public void LowVolume_DoesNotVote_WhenBarVolumeAboveAverage()
    {
        var (det, orb, vwap, atr) = Build();
        var tradingDate = new DateTime(2026, 3, 10);
        for (int i = 0; i < 20; i++)
        {
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(i), 5200m, 5205m, 5195m, 5200m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        var test = MakeBar(ToUtc(9, 0).AddMinutes(20), 5200m, 5205m, 5195m, 5200m, volume: 1500);
        Feed(det, orb, vwap, atr, test, tradingDate);

        Assert.True(det.LastResult.LowVolume.Active);
        Assert.False(det.LastResult.LowVolume.Voted);
    }

    // ─── Filter 3: Weak Opening Drive ────────────────────────────────

    [Fact]
    public void WeakDrive_VotesWhenOrbOpenAndCloseAreClose()
    {
        var (det, orb, vwap, atr) = Build(orbTfMinutes: 5);
        var tradingDate = new DateTime(2026, 3, 10);
        // OR window 9:30–10:00. 5-min bars: 9:30, 9:35, ..., 9:55 → 6 bars in window.
        // Drive a bar pattern with wide range but open ≈ close (small net move).
        // Open of first OR bar at 5200, close of last OR bar at 5202; OR high=5210, low=5190 → range=20, drive=2/20=0.1
        var bars = new (decimal open, decimal high, decimal low, decimal close)[]
        {
            (5200m, 5210m, 5198m, 5205m),  // first overlapping bar — captures OrbOpen=5200
            (5205m, 5208m, 5190m, 5200m),
            (5200m, 5206m, 5195m, 5202m),
            (5202m, 5207m, 5198m, 5204m),
            (5204m, 5209m, 5199m, 5201m),
            (5201m, 5208m, 5197m, 5202m),  // last overlapping bar — captures OrbClose=5202
        };
        for (int i = 0; i < bars.Length; i++)
        {
            var (o, h, l, c) = bars[i];
            var bar = MakeBar(ToUtc(9, 30).AddMinutes(i * 5), o, h, l, c, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        // Finalize OR with a bar after 10:00
        var seal = MakeBar(ToUtc(10, 0), 5202m, 5205m, 5200m, 5203m, volume: 1000);
        Feed(det, orb, vwap, atr, seal, tradingDate);

        Assert.True(orb.IsSet);
        Assert.Equal(5200m, orb.OrbOpen);
        Assert.Equal(5202m, orb.OrbClose);
        Assert.True(det.LastResult.WeakDrive.Active);
        Assert.True(det.LastResult.WeakDrive.Voted);
    }

    [Fact]
    public void WeakDrive_DoesNotVote_WhenOrCloseIsFarFromOrbOpen()
    {
        var (det, orb, vwap, atr) = Build(orbTfMinutes: 5);
        var tradingDate = new DateTime(2026, 3, 10);
        // OrbOpen=5200, OrbClose=5215, range=20, drive=15/20=0.75 ≥ 0.50 → no vote
        var bars = new (decimal open, decimal high, decimal low, decimal close)[]
        {
            (5200m, 5205m, 5198m, 5202m),
            (5202m, 5208m, 5200m, 5206m),
            (5206m, 5212m, 5204m, 5210m),
            (5210m, 5215m, 5208m, 5213m),
            (5213m, 5218m, 5210m, 5215m),
            (5215m, 5220m, 5210m, 5215m),
        };
        for (int i = 0; i < bars.Length; i++)
        {
            var (o, h, l, c) = bars[i];
            var bar = MakeBar(ToUtc(9, 30).AddMinutes(i * 5), o, h, l, c, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        var seal = MakeBar(ToUtc(10, 0), 5215m, 5218m, 5213m, 5216m, volume: 1000);
        Feed(det, orb, vwap, atr, seal, tradingDate);

        Assert.True(orb.IsSet);
        Assert.True(det.LastResult.WeakDrive.Active);
        Assert.False(det.LastResult.WeakDrive.Voted);
    }

    // ─── Filter 1: Range Compression ─────────────────────────────────

    [Fact]
    public void RangeCompression_VotesWhenOrIsSmallRelativeToAtr()
    {
        var (det, orb, vwap, atr) = Build(orbTfMinutes: 5);
        var tradingDate = new DateTime(2026, 3, 10);
        // Pre-OR: feed 14 bars with WIDE range to build a large ATR (~20)
        for (int i = 0; i < 14; i++)
        {
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(-70 + i * 5), 5200m, 5210m, 5190m, 5200m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        Assert.True(atr.IsReady);
        // OR window: very tight bars — OR range ~5
        for (int i = 0; i < 6; i++)
        {
            var bar = MakeBar(ToUtc(9, 30).AddMinutes(i * 5), 5200m, 5202m, 5198m, 5201m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        // Finalize OR
        var seal = MakeBar(ToUtc(10, 0), 5201m, 5204m, 5199m, 5202m, volume: 1000);
        Feed(det, orb, vwap, atr, seal, tradingDate);

        Assert.True(orb.IsSet);
        Assert.True(det.LastResult.RangeCompression.Active);
        // OR range ≈ 4 (5202-5198), ATR ≈ 20, ratio ≈ 0.2 → < 0.70, votes
        Assert.True(det.LastResult.RangeCompression.Voted);
    }

    [Fact]
    public void RangeCompression_Inactive_WhenAtrNotReady()
    {
        var (det, orb, vwap, atr) = Build(orbTfMinutes: 5);
        var tradingDate = new DateTime(2026, 3, 10);
        // Drive only the OR window — no pre-warmup → ATR period (14) not met
        for (int i = 0; i < 6; i++)
        {
            var bar = MakeBar(ToUtc(9, 30).AddMinutes(i * 5), 5200m, 5202m, 5198m, 5201m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        var seal = MakeBar(ToUtc(10, 0), 5201m, 5204m, 5199m, 5202m, volume: 1000);
        Feed(det, orb, vwap, atr, seal, tradingDate);

        Assert.True(orb.IsSet);
        Assert.False(atr.IsReady);
        Assert.False(det.LastResult.RangeCompression.Active);
    }

    // ─── Filter 2: Flat VWAP ─────────────────────────────────────────

    [Fact]
    public void FlatVwap_VotesWhenVwapBarelyMoves()
    {
        var (det, orb, vwap, atr) = Build();
        var tradingDate = new DateTime(2026, 3, 10);
        // Feed 22 bars with constant HLC3 ≈ 5200 → VWAP stays flat
        for (int i = 0; i < 22; i++)
        {
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(i), 5200m, 5200m, 5200m, 5200m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        Assert.True(det.LastResult.FlatVwap.Active);
        Assert.True(det.LastResult.FlatVwap.Voted);
        Assert.Equal(0m, det.LastResult.FlatVwap.Value);
    }

    [Fact]
    public void FlatVwap_DoesNotVote_WhenVwapTrending()
    {
        var (det, orb, vwap, atr) = Build();
        var tradingDate = new DateTime(2026, 3, 10);
        // Feed 22 bars with steadily rising HLC3 → VWAP rises significantly
        for (int i = 0; i < 22; i++)
        {
            decimal mid = 5200m + i * 5m;
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(i), mid, mid, mid, mid, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        Assert.True(det.LastResult.FlatVwap.Active);
        Assert.False(det.LastResult.FlatVwap.Voted);
    }

    [Fact]
    public void FlatVwap_InactiveBeforeWarmup()
    {
        var (det, orb, vwap, atr) = Build();
        var tradingDate = new DateTime(2026, 3, 10);
        // Default lookback is 20 → need 21 samples. Feed only 20.
        for (int i = 0; i < 20; i++)
        {
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(i), 5200m, 5200m, 5200m, 5200m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        Assert.False(det.LastResult.FlatVwap.Active);
    }

    // ─── Aggregation ─────────────────────────────────────────────────

    [Fact]
    public void Aggregation_VotesBelowMinDoNotTriggerChop()
    {
        var cfg = new ChopRegimeConfig { MinVotesForChop = 3 };
        var (det, orb, vwap, atr) = Build(cfg);
        var tradingDate = new DateTime(2026, 3, 10);
        // Warmup with flat VWAP + steady volume — likely to vote on F2 (FlatVwap)
        // but not on others, so total votes < 3 → not chop
        for (int i = 0; i < 22; i++)
        {
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(i), 5200m, 5201m, 5199m, 5200m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        Assert.True(det.LastResult.Votes < 3);
        Assert.False(det.LastResult.IsChop);
    }

    // ─── Reset / NewSession ──────────────────────────────────────────

    [Fact]
    public void NewSession_ClearsLastResultAndInternalState()
    {
        var (det, orb, vwap, atr) = Build();
        var tradingDate = new DateTime(2026, 3, 10);
        for (int i = 0; i < 22; i++)
        {
            var bar = MakeBar(ToUtc(9, 0).AddMinutes(i), 5200m, 5200m, 5200m, 5200m, volume: 1000);
            Feed(det, orb, vwap, atr, bar, tradingDate);
        }
        Assert.True(det.LastResult.FlatVwap.Active);

        det.NewSession(tradingDate.AddDays(1));
        Assert.False(det.LastResult.IsChop);
        Assert.Equal(0, det.LastResult.Votes);
    }
}
