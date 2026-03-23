using CRV.Core.Models;
using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Modules;

public class SessionEngineTests
{
    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static ModuleConfig DefaultConfig() => new();

    private static SessionEngine CreateEngine() => new(DefaultConfig());

    /// <summary>Convert Eastern time to UTC for bar construction.</summary>
    private static DateTime ToUtc(int year, int month, int day, int hour, int minute)
        => TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), EasternTz);

    private static Bar MakeBar(DateTime utcTime, decimal high, decimal low, decimal close, long volume = 100)
        => new(utcTime, (high + low) / 2m, high, low, close, volume);

    // ── Session detection ────────────────────────────────────────

    [Theory]
    [InlineData(19, 0, SessionType.Asia)]       // 7 PM ET = Asia
    [InlineData(23, 30, SessionType.Asia)]       // 11:30 PM ET = Asia (before midnight)
    [InlineData(3, 30, SessionType.London)]      // 3:30 AM ET = London
    [InlineData(9, 45, SessionType.NYOpen)]      // 9:45 AM ET = NY Open
    [InlineData(12, 0, SessionType.Midday)]      // 12:00 PM ET = Midday
    [InlineData(15, 30, SessionType.PowerHour)]  // 3:30 PM ET = Power Hour
    [InlineData(8, 45, SessionType.PreMarket)]   // 8:45 AM ET = PreMarket (gap between London and NY)
    public void DetectsSessionCorrectly(int hour, int minute, SessionType expected)
    {
        var engine = CreateEngine();
        var time = new TimeOnly(hour, minute);
        Assert.Equal(expected, engine.DetectSession(time));
    }

    [Fact]
    public void AsiaSession_WrapsAroundMidnight()
    {
        var engine = CreateEngine();
        // 18:00 is Asia start
        Assert.Equal(SessionType.Asia, engine.DetectSession(new TimeOnly(18, 0)));
        // 23:59 still Asia
        Assert.Equal(SessionType.Asia, engine.DetectSession(new TimeOnly(23, 59)));
        // 00:00 midnight is within Asia (AsiaEnd = 02:00, so 00:00 < 02:00 is true)
        Assert.Equal(SessionType.Asia, engine.DetectSession(new TimeOnly(0, 0)));
        // 01:59 still Asia
        Assert.Equal(SessionType.Asia, engine.DetectSession(new TimeOnly(1, 59)));
        // 02:00 is AsiaEnd boundary, no longer Asia
        Assert.NotEqual(SessionType.Asia, engine.DetectSession(new TimeOnly(2, 0)));
    }

    // ── Session high/low tracking ────────────────────────────────

    [Fact]
    public void TracksSessionHighLow()
    {
        var engine = CreateEngine();
        var tradingDate = new DateTime(2026, 3, 10);

        // Bar during NY Open
        var bar1 = MakeBar(ToUtc(2026, 3, 10, 9, 30), high: 5200m, low: 5180m, close: 5195m);
        var bar2 = MakeBar(ToUtc(2026, 3, 10, 9, 31), high: 5210m, low: 5190m, close: 5205m);

        engine.OnBar(bar1, tradingDate);
        engine.OnBar(bar2, tradingDate);

        Assert.Equal(5210m, engine.SessionHigh);
        Assert.Equal(5180m, engine.SessionLow);
    }

    // ── Asia high/low/mid/range tracking ─────────────────────────

    [Fact]
    public void TracksAsiaHighLowMidRange()
    {
        var engine = CreateEngine();
        var tradingDate = new DateTime(2026, 3, 10);

        // Bars during Asia session (19:00 ET)
        var bar1 = MakeBar(ToUtc(2026, 3, 10, 19, 0), high: 5200m, low: 5180m, close: 5190m);
        var bar2 = MakeBar(ToUtc(2026, 3, 10, 20, 0), high: 5220m, low: 5185m, close: 5210m);

        engine.OnBar(bar1, tradingDate);
        engine.OnBar(bar2, tradingDate);

        Assert.Equal(5220m, engine.AsiaHigh);
        Assert.Equal(5180m, engine.AsiaLow);
        Assert.Equal((5220m + 5180m) / 2m, engine.AsiaMid);
        Assert.Equal(5220m - 5180m, engine.AsiaRange);
    }

    // ── Asia compressed ──────────────────────────────────────────

    [Fact]
    public void DetectsAsiaCompressed_WhenRangeLessThanAtrThreshold()
    {
        var engine = CreateEngine();
        engine.CurrentAtr = 50m; // ATR = 50, threshold = 50 * 1.2 = 60
        var tradingDate = new DateTime(2026, 3, 10);

        // Asia bars with range = 20 (< 60 threshold)
        var bar = MakeBar(ToUtc(2026, 3, 10, 19, 0), high: 5200m, low: 5180m, close: 5190m);
        engine.OnBar(bar, tradingDate);

        Assert.True(engine.AsiaCompressed);
    }

    [Fact]
    public void AsiaNotCompressed_WhenRangeExceedsThreshold()
    {
        var engine = CreateEngine();
        engine.CurrentAtr = 10m; // ATR = 10, threshold = 12
        var tradingDate = new DateTime(2026, 3, 10);

        // Asia bars with range = 40 (> 12 threshold)
        var bar1 = MakeBar(ToUtc(2026, 3, 10, 19, 0), high: 5220m, low: 5180m, close: 5200m);
        engine.OnBar(bar1, tradingDate);

        Assert.False(engine.AsiaCompressed);
    }

    // ── NewSession resets ─────────────────────────────────────────

    [Fact]
    public void NewSession_ResetsAllState()
    {
        var engine = CreateEngine();
        var tradingDate = new DateTime(2026, 3, 10);

        // Feed some bars
        var bar = MakeBar(ToUtc(2026, 3, 10, 19, 0), high: 5200m, low: 5180m, close: 5190m);
        engine.OnBar(bar, tradingDate);

        Assert.True(engine.SessionHigh > 0);
        Assert.True(engine.AsiaHigh > 0);

        // New session
        engine.NewSession(new DateTime(2026, 3, 11));

        Assert.Equal(0m, engine.SessionHigh);
        Assert.Equal(decimal.MaxValue, engine.SessionLow);
        Assert.Equal(0m, engine.AsiaHigh);
        Assert.Equal(decimal.MaxValue, engine.AsiaLow);
        Assert.Equal(0m, engine.LondonHigh);
        Assert.False(engine.LondonSweptAsiaHigh);
        Assert.False(engine.NYBullExpansion);
        Assert.False(engine.AsiaCompressed);
        Assert.Equal(SessionType.PreMarket, engine.CurrentSession);
    }

    // ── OnTick updates session high/low ──────────────────────────

    [Fact]
    public void OnTick_UpdatesSessionHighLow()
    {
        var engine = CreateEngine();
        var tradingDate = new DateTime(2026, 3, 10);

        // Establish some baseline with a bar
        var bar = MakeBar(ToUtc(2026, 3, 10, 9, 30), high: 5200m, low: 5180m, close: 5190m);
        engine.OnBar(bar, tradingDate);

        // Tick pushes new high
        engine.OnTick(5215m, ToUtc(2026, 3, 10, 9, 31));
        Assert.Equal(5215m, engine.SessionHigh);
        Assert.Equal(5215m, engine.NYHigh);

        // Tick pushes new low
        engine.OnTick(5170m, ToUtc(2026, 3, 10, 9, 32));
        Assert.Equal(5170m, engine.SessionLow);
        Assert.Equal(5170m, engine.NYLow);
    }

    // ── SeedHistory sets PDH/PDL/PWH/PWL/PMH/PML ────────────────

    [Fact]
    public void SeedHistory_SetsPreviousDayLevels()
    {
        var engine = CreateEngine();
        var bars = new List<Bar>
        {
            MakeBar(ToUtc(2026, 3, 7, 9, 30), high: 5100m, low: 5050m, close: 5080m),
            MakeBar(ToUtc(2026, 3, 10, 9, 30), high: 5200m, low: 5150m, close: 5180m),
        };

        engine.SeedHistory(bars);

        // PDH/PDL from last bar
        Assert.Equal(5200m, engine.PDH);
        Assert.Equal(5150m, engine.PDL);

        // PWH/PWL from last 5 (only 2 bars)
        Assert.Equal(5200m, engine.PWH);
        Assert.Equal(5050m, engine.PWL);

        // PMH/PML from last 20 (only 2 bars)
        Assert.Equal(5200m, engine.PMH);
        Assert.Equal(5050m, engine.PML);
    }

    [Fact]
    public void SeedHistory_WithEmptyBars_DoesNotThrow()
    {
        var engine = CreateEngine();
        engine.SeedHistory(new List<Bar>());

        Assert.Equal(0m, engine.PDH);
        Assert.Equal(0m, engine.PDL);
    }

    // ── Cross-session signals ────────────────────────────────────

    [Fact]
    public void LondonSweptAsiaHigh_DetectedCorrectly()
    {
        var engine = CreateEngine();
        var tradingDate = new DateTime(2026, 3, 10);

        // Asia bar
        var asiaBar = MakeBar(ToUtc(2026, 3, 10, 19, 0), high: 5200m, low: 5180m, close: 5190m);
        engine.OnBar(asiaBar, tradingDate);
        Assert.Equal(5200m, engine.AsiaHigh);

        // London bar sweeps Asia high
        var londonBar = MakeBar(ToUtc(2026, 3, 11, 3, 30), high: 5210m, low: 5195m, close: 5205m);
        engine.OnBar(londonBar, tradingDate);

        Assert.True(engine.LondonSweptAsiaHigh);
    }
}
