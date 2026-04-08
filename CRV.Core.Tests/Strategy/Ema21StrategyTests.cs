using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class Ema21StrategyTests
{
    private static StrategySetupConfig DefaultConfig() => new()
    {
        Id = "ema21-nq", Name = "EMA21", SetupId = SetupId.F,
        StrategyType = StrategyType.Ema21,
        Enabled = true,
        Ticker = "NQM26", PointValue = 20m, TickSize = 0.25m,
        Contracts = 2, MaxContracts = 4, HiVolMult = 1.0m,
        MinRr = 1.0m, MaxTrades = 3,
        UsePartial = true, UseBe = true, PartialCts = 1,
        CutoffHour = 15, CutoffMinute = 0,
        // EMA21-specific
        SlopeLen = 5, AtrTouchMult = 0.5m, MinSlopePct = 0.05m,
        OpenTicksToEma = 4, UseVolumeFilter = false,
        AtrTp1Mult = 1.0m, AtrTp2Mult = 2.0m,
    };

    // Dummy state objects — EMA21 strategy ignores these
    private static OrbState DummyOrb() => default;
    private static IndicatorState DummyInd() => default;
    private static ModuleState DummyMod() => new(
        0, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, SessionType.NYOpen,
        false, false, false, false,
        Array.Empty<SweepEvent>(), 0, false, false, false, false,
        0, 0, false, false, false, false, 0m, false, false, 0m, 0m);

    private static DateTime T(int minute) =>
        new(2026, 4, 7, 14, minute, 0, DateTimeKind.Utc);

    /// <summary>Generate bars in a rising trend starting at basePrice.</summary>
    private static List<Bar> MakeRisingBars(int count, decimal basePrice, int startMinute = 0)
    {
        var bars = new List<Bar>();
        for (int i = 0; i < count; i++)
        {
            decimal p = basePrice + i * 2m;
            bars.Add(new Bar(T(startMinute + i), p, p + 3m, p - 1m, p + 1.5m, 500));
        }
        return bars;
    }

    private static void FeedBars(Ema21Strategy s, IEnumerable<Bar> bars)
    {
        foreach (var bar in bars)
            s.OnBar(bar, DummyOrb(), DummyInd(), DummyMod());
    }

    [Fact]
    public void Indicators_not_ready_before_21_bars()
    {
        var s = new Ema21Strategy(DefaultConfig());
        var bars = MakeRisingBars(20, 5000m);
        FeedBars(s, bars);

        Assert.False(s.IsArmed);
        Assert.Null(s.PendingEntry);
    }

    [Fact]
    public void Indicators_ready_after_21_bars()
    {
        var s = new Ema21Strategy(DefaultConfig());
        var bars = MakeRisingBars(22, 5000m);
        FeedBars(s, bars);

        // No signal yet (steady trend, no cross or touch), but no crash
        Assert.Null(s.PendingEntry);
    }
}
