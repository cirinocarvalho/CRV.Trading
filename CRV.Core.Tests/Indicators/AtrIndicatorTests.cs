using CRV.Core.Indicators;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Indicators;

public class AtrIndicatorTests
{
    private static Bar MakeBar(decimal high, decimal low, decimal close, DateTime? time = null)
        => new(time ?? DateTime.UtcNow, (high + low) / 2m, high, low, close, 100);

    [Fact]
    public void NotReady_Before14Bars()
    {
        var atr = new AtrIndicator(14);
        for (int i = 0; i < 13; i++)
            atr.Update(MakeBar(100 + i, 90 + i, 95 + i));
        Assert.False(atr.IsReady);
        Assert.True(atr.HasValue);          // at least one bar fed
        Assert.Equal(0m, atr.Value);        // Value returns 0 during warmup (engine safety)
        Assert.True(atr.PartialValue > 0);  // PartialValue returns running avg for display
    }

    [Fact]
    public void IsReady_After14Bars()
    {
        var atr = new AtrIndicator(14);
        for (int i = 0; i < 14; i++)
            atr.Update(MakeBar(100 + i, 90 + i, 95 + i));
        Assert.True(atr.IsReady);
        Assert.True(atr.Value > 0);
    }

    [Fact]
    public void FirstBar_NoGap_TrueRangeIsHighMinusLow()
    {
        var atr = new AtrIndicator(1);
        var result = atr.Update(MakeBar(110, 100, 105));
        Assert.Equal(1, atr.IsReady ? 1 : 0); // ready after 1 bar with period=1
        Assert.Equal(10m, result); // TR = high - low = 110 - 100
    }

    [Fact]
    public void GapUp_TrueRangeIncludesPreviousClose()
    {
        var atr = new AtrIndicator(1);
        atr.Update(MakeBar(100, 90, 95));  // bar 1: close = 95
        decimal val = atr.Update(MakeBar(110, 100, 105)); // bar 2: gap, high-prevClose = 110-95 = 15
        Assert.Equal(15m, val); // max(110-100, |110-95|, |100-95|) = max(10, 15, 5) = 15
    }

    [Fact]
    public void WilderSmoothing_AppliedAfterSeed()
    {
        var atr = new AtrIndicator(3);
        // 3 bars to seed: TR = 10 each → initial ATR = 10
        atr.Update(MakeBar(110, 100, 105));
        atr.Update(MakeBar(111, 101, 106));
        decimal seedValue = atr.Update(MakeBar(112, 102, 107));
        Assert.Equal(10m, seedValue);

        // 4th bar: TR = 5 (narrow bar)
        decimal smoothed = atr.Update(MakeBar(107, 102, 104));
        // Wilder: (10 * 2 + 5) / 3 = 25/3 ≈ 8.333
        Assert.True(smoothed < 10m, "ATR should decrease with a narrower bar");
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var atr = new AtrIndicator(14);
        for (int i = 0; i < 20; i++) atr.Update(MakeBar(100 + i, 90 + i, 95 + i));
        Assert.True(atr.IsReady);
        atr.Reset();
        Assert.False(atr.IsReady);
        Assert.Equal(0m, atr.Value);
    }
}
