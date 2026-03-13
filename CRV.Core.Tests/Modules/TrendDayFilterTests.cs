using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Modules;

public class TrendDayFilterTests
{
    private static ModuleConfig DefaultConfig() => new()
    {
        TrendDayThreshold = 4,
        ShallowPullbackMax = 0.35m,
    };

    // ── 1. Scores bull trend day ────────────────────────────────────

    [Fact]
    public void ScoresBullTrendDay()
    {
        var filter = new TrendDayFilter(DefaultConfig());

        // All 5 conditions met:
        // 1. openingDriveBull = true
        // 2. close(5120) > orbHigh(5050) && sessionLow(5040) > orbMid(5025)
        // 3. close(5120) > vwap(5060)
        // 4. shallowPullback: moveUp = 5130-5010 = 120, (5130-5120)/120 = 0.083 < 0.35
        // 5. sessionHigh(5130) > orbHigh(5050)
        filter.Update(
            openingDriveBull: true, openingDriveBear: false,
            close: 5120m, vwap: 5060m,
            orbHigh: 5050m, orbLow: 5000m, orbMid: 5025m,
            sessionHigh: 5130m, sessionLow: 5040m, sessionOpen: 5010m);

        Assert.Equal(5, filter.BullScore);
        Assert.True(filter.TrendDayBull);
        Assert.False(filter.TrendDayBear);
    }

    // ── 2. Scores bear trend day ────────────────────────────────────

    [Fact]
    public void ScoresBearTrendDay()
    {
        var filter = new TrendDayFilter(DefaultConfig());

        // All 5 bear conditions met:
        // 1. openingDriveBear = true
        // 2. close(4880) < orbLow(4950) && sessionHigh(4960) < orbMid(4975)
        // 3. close(4880) < vwap(4940)
        // 4. shallowPullback: moveDown = 4990-4870 = 120, (4880-4870)/120 = 0.083 < 0.35
        // 5. sessionLow(4870) < orbLow(4950)
        filter.Update(
            openingDriveBull: false, openingDriveBear: true,
            close: 4880m, vwap: 4940m,
            orbHigh: 5000m, orbLow: 4950m, orbMid: 4975m,
            sessionHigh: 4960m, sessionLow: 4870m, sessionOpen: 4990m);

        Assert.Equal(5, filter.BearScore);
        Assert.True(filter.TrendDayBear);
        Assert.False(filter.TrendDayBull);
    }

    // ── 3. Low score is not trend day ───────────────────────────────

    [Fact]
    public void LowScoreNotTrendDay()
    {
        var filter = new TrendDayFilter(DefaultConfig());

        // Only close > vwap met (score = 1)
        filter.Update(
            openingDriveBull: false, openingDriveBear: false,
            close: 5030m, vwap: 5020m,
            orbHigh: 5050m, orbLow: 5000m, orbMid: 5025m,
            sessionHigh: 5040m, sessionLow: 4990m, sessionOpen: 5010m);

        Assert.True(filter.BullScore < 4);
        Assert.False(filter.TrendDayBull);
        Assert.False(filter.TrendDayBear);
    }

    // ── 4. NewSession resets ────────────────────────────────────────

    [Fact]
    public void NewSessionResets()
    {
        var filter = new TrendDayFilter(DefaultConfig());

        filter.Update(
            openingDriveBull: true, openingDriveBear: false,
            close: 5120m, vwap: 5060m,
            orbHigh: 5050m, orbLow: 5000m, orbMid: 5025m,
            sessionHigh: 5130m, sessionLow: 5040m, sessionOpen: 5010m);

        Assert.True(filter.TrendDayBull);

        filter.NewSession(new DateTime(2026, 3, 13));

        Assert.Equal(0, filter.BullScore);
        Assert.Equal(0, filter.BearScore);
        Assert.False(filter.TrendDayBull);
        Assert.False(filter.TrendDayBear);
    }
}
