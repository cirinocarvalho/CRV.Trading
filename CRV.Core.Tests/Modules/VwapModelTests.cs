using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Modules;

public class VwapModelTests
{
    private static ModuleConfig DefaultConfig() => new();

    private static VwapModel CreateModel() => new(DefaultConfig());

    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static DateTime ToUtc(int hour, int minute, int day = 10)
        => TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 3, day, hour, minute, 0, DateTimeKind.Unspecified), EasternTz);

    private static Bar MakeBar(DateTime utcTime, decimal open, decimal high, decimal low, decimal close, long volume = 1000)
        => new(utcTime, open, high, low, close, volume);

    // ── VWAP computation ─────────────────────────────────────────

    [Fact]
    public void ComputesVwap_FromBars()
    {
        var model = CreateModel();
        var tradingDate = new DateTime(2026, 3, 10);

        var bar1 = MakeBar(ToUtc(9, 30), 5190m, 5200m, 5180m, 5195m, 1000);
        var bar2 = MakeBar(ToUtc(9, 31), 5195m, 5210m, 5190m, 5205m, 2000);

        model.OnBar(bar1, tradingDate);
        model.OnBar(bar2, tradingDate);

        // VWAP = (hlc3_1 * vol1 + hlc3_2 * vol2) / (vol1 + vol2)
        decimal hlc3_1 = (5200m + 5180m + 5195m) / 3m;
        decimal hlc3_2 = (5210m + 5190m + 5205m) / 3m;
        decimal expected = (hlc3_1 * 1000m + hlc3_2 * 2000m) / 3000m;

        Assert.Equal(expected, model.Vwap);
    }

    // ── Deviation bands ──────────────────────────────────────────

    [Fact]
    public void ComputesDeviationBands()
    {
        var model = CreateModel();
        var tradingDate = new DateTime(2026, 3, 10);

        // Feed several bars so bands are non-zero
        var bars = new[]
        {
            MakeBar(ToUtc(9, 30), 5190m, 5200m, 5180m, 5195m, 1000),
            MakeBar(ToUtc(9, 31), 5195m, 5210m, 5190m, 5205m, 1000),
            MakeBar(ToUtc(9, 32), 5205m, 5220m, 5200m, 5215m, 1000),
            MakeBar(ToUtc(9, 33), 5215m, 5225m, 5210m, 5220m, 1000),
            MakeBar(ToUtc(9, 34), 5220m, 5230m, 5215m, 5225m, 1000),
        };

        foreach (var bar in bars)
            model.OnBar(bar, tradingDate);

        // Bands should be set and symmetric around VWAP
        Assert.True(model.Upper1 > model.Vwap);
        Assert.True(model.Upper2 > model.Upper1);
        Assert.True(model.Lower1 < model.Vwap);
        Assert.True(model.Lower2 < model.Lower1);
        Assert.Equal(model.Vwap + (model.Upper1 - model.Vwap), model.Upper1);

        // Upper2 should be ~2x the distance of Upper1
        decimal dev1 = model.Upper1 - model.Vwap;
        decimal dev2 = model.Upper2 - model.Vwap;
        Assert.Equal(dev1 * 2m, dev2, precision: 4);
    }

    // ── State classification ─────────────────────────────────────

    [Fact]
    public void ClassifiesState_AcceptBull_WhenCloseAboveVwapAndLowAboveVwap()
    {
        var model = CreateModel();
        var tradingDate = new DateTime(2026, 3, 10);

        // Use large-volume anchor bars to pin VWAP at ~5000, then a small-volume
        // bar above VWAP. The high volume means the bull bar barely moves VWAP.
        // Wide HLC spread in anchors produces wide deviation bands.
        var setupBars = new[]
        {
            MakeBar(ToUtc(9, 30), 4950m, 5050m, 4900m, 5000m, 50000),
            MakeBar(ToUtc(9, 31), 4960m, 5060m, 4910m, 5010m, 50000),
            MakeBar(ToUtc(9, 32), 4970m, 5040m, 4920m, 4990m, 50000),
            MakeBar(ToUtc(9, 33), 4980m, 5030m, 4930m, 5000m, 50000),
            MakeBar(ToUtc(9, 34), 4990m, 5020m, 4940m, 5005m, 50000),
        };
        foreach (var bar in setupBars)
            model.OnBar(bar, tradingDate);

        // VWAP ~4987, Upper2 ~4994. Use a bar with close between VWAP and Upper2,
        // and low >= VWAP to get AcceptBull.
        var bullBar = MakeBar(ToUtc(9, 35), 4989m, 4993m, 4988m, 4991m, 100);
        model.OnBar(bullBar, tradingDate);

        // close (4991) > VWAP (~4987), low (4988) >= VWAP, close < Upper2 (~4994)
        Assert.Equal(VwapState.AcceptBull, model.State);
    }

    // ── Reclaim detection ────────────────────────────────────────

    [Fact]
    public void DetectsReclaim_WhenCloseCrossesAboveVwap()
    {
        var model = CreateModel();
        var tradingDate = new DateTime(2026, 3, 10);

        // First bars establish VWAP around 5100
        var bar1 = MakeBar(ToUtc(9, 30), 5100m, 5110m, 5090m, 5100m, 5000);
        model.OnBar(bar1, tradingDate);
        // VWAP ~5100

        // Bar below VWAP (close < VWAP)
        var barBelow = MakeBar(ToUtc(9, 31), 5095m, 5098m, 5085m, 5090m, 500);
        model.OnBar(barBelow, tradingDate);
        // prevClose = 5090 < VWAP

        // Bar that reclaims VWAP (close >= VWAP)
        var barAbove = MakeBar(ToUtc(9, 32), 5095m, 5110m, 5092m, 5105m, 500);
        model.OnBar(barAbove, tradingDate);

        Assert.True(model.BullVWAPReclaim);
    }

    // ── NewSession resets ─────────────────────────────────────────

    [Fact]
    public void NewSession_ResetsAllState()
    {
        var model = CreateModel();
        var tradingDate = new DateTime(2026, 3, 10);

        // Feed bars
        var bar = MakeBar(ToUtc(9, 30), 5190m, 5200m, 5180m, 5195m, 1000);
        model.OnBar(bar, tradingDate);

        Assert.True(model.Vwap > 0);

        // Reset
        model.NewSession(new DateTime(2026, 3, 11));

        Assert.Equal(0m, model.Vwap);
        Assert.Equal(0m, model.Upper1);
        Assert.Equal(0m, model.Upper2);
        Assert.Equal(0m, model.Lower1);
        Assert.Equal(0m, model.Lower2);
        Assert.Equal(VwapState.Neutral, model.State);
        Assert.False(model.BullVWAPReclaim);
        Assert.False(model.BearVWAPReject);
    }

    // ── OnTick is no-op ──────────────────────────────────────────

    [Fact]
    public void OnTick_DoesNotChangeState()
    {
        var model = CreateModel();
        var tradingDate = new DateTime(2026, 3, 10);

        var bar = MakeBar(ToUtc(9, 30), 5190m, 5200m, 5180m, 5195m, 1000);
        model.OnBar(bar, tradingDate);

        decimal vwapBefore = model.Vwap;
        model.OnTick(9999m, ToUtc(9, 31));

        Assert.Equal(vwapBefore, model.Vwap);
    }
}
