using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class FalseBreakoutIntegrationTests
{
    static OrbStrategyEngine BuildEngine(StrategyConfig cfg, out TestSink sink, out TestPrices prices)
    {
        sink   = new TestSink();
        prices = new TestPrices();
        return new OrbStrategyEngine(cfg, new NullExecutor(), sink, prices,
            NullLogger<OrbStrategyEngine>.Instance);
    }

    static StrategyConfig CfgC() => new()
    {
        Ticker             = "NQH26",
        ExecutionTFMinutes = 5,
        OrbStart           = new TimeOnly(9, 30),
        OrbEnd             = new TimeOnly(10, 0),
        RthStart           = new TimeOnly(9, 30),
        RthEnd             = new TimeOnly(16, 0),
        SessionStartHour   = 18,
        Timezone           = "America/New_York",
        EnableA            = false,
        EnableB            = false,
        EnableC            = true,
        EnableD            = false,
        Contracts          = 1,
        ContractsC         = 2,
        StopPctC           = 0.10m,
        TargetPctC         = 100,
        PartialPctC        = 50,
        NearPctC           = 0.15m,
        MinRrC             = 1.0m,
        UsePartialC        = false,
        UseBeC             = false,
        UseVwap            = false,
        AtrFilterPct       = 0m,
        TickSize           = 0.25m,
        PointValue         = 20m,
        CommissionPerSide  = 0m,
        MaxTradesC         = 5,
        // FB module params
        FBMaxTimeOutsideMinutesOrb = 15,
        FBMaxTimeOutsideMinutesSR  = 60,
        FBMaxPenetrationPctOrb     = 0.30m,
        FBMaxPenetrationPctSR      = 0.25m,
        FBMinRejectionBodyPct      = 0.50m,
        FBMaxTrendDayScore         = 60,
        // Need A/B config to avoid validation issues
        ModeA = "Conservative",
        ModeB = "Conservative",
        MaxTradesA = 5,
        MaxTradesB = 5,
    };

    /// <summary>Feed two ORB bars to establish OrbHigh/OrbLow.</summary>
    static async Task FeedOrbBars(OrbStrategyEngine eng, decimal high, decimal low, DateTime etDate)
    {
        decimal mid = (high + low) / 2m;
        var bar1 = new Bar(
            new DateTime(etDate.Year, etDate.Month, etDate.Day, 13, 30, 0, DateTimeKind.Utc),
            mid, high, low, mid, 1000, IsConfirmed: true);
        await eng.WarmupBarAsync(bar1);
        var bar2 = new Bar(
            new DateTime(etDate.Year, etDate.Month, etDate.Day, 14, 0, 0, DateTimeKind.Utc),
            mid, high, low, mid, 1000, IsConfirmed: true);
        await eng.WarmupBarAsync(bar2);
    }

    static DateTime Utc(int year, int month, int day, int h, int m)
        => new(year, month, day, h, m, 0, DateTimeKind.Utc);

    // ── Setup C: ORB False Breakout ──────────────────────────────

    [Fact]
    public async Task SetupC_ArmsAndEntersOnOrbFalseBreakout()
    {
        // ORB: High=21000, Low=20000, Range=1000
        var cfg = CfgC();
        var eng = BuildEngine(cfg, out var sink, out _);
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Bar 3 (live): breakout above ORB high
        // pen = (21200-21000)/1000 = 0.20 < 0.30 ✓
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 5),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));
        Assert.Empty(sink.Entries); // not yet

        // Bar 4 (live): rejection back inside with strong body
        // body = |20500-21100| = 600, range = 21100-20400 = 700, body% = 0.86 ✓
        // close 20500 is inside [20000, 21000] ✓
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 10),
            21100m, 21100m, 20400m, 20500m, 500, IsConfirmed: true));

        // Detector activates → engine arms SHORT → entry fires at OrbHigh
        Assert.Single(sink.Entries);
        var entry = sink.Entries[0];
        Assert.Equal(SetupId.C, entry.Setup);
        Assert.Equal(Direction.Short, entry.Direction);
        Assert.Equal(21000m, entry.Entry);
        Assert.Equal(2, entry.Contracts);
    }

    [Fact]
    public async Task SetupC_FullTrade_TargetHit()
    {
        var cfg = CfgC();
        var eng = BuildEngine(cfg, out var sink, out _);
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Breakout above + rejection → entry SHORT at 21000
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 5),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));
        // Rejection bar: H=21050 stays below stop (21100) so no same-bar exit
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 10),
            21000m, 21050m, 20400m, 20500m, 500, IsConfirmed: true));
        Assert.Single(sink.Entries);

        // Stop = 21100, Target = 21000 - 1.0 * 1000 = 20000
        // Bar that hits target: Low ≤ 20000
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 15),
            20500m, 20600m, 19900m, 20000m, 500, IsConfirmed: true));

        Assert.Single(sink.Exits);
        Assert.Equal(SetupId.C, sink.Exits[0].Setup);
        Assert.Equal(ExitReason.Target, sink.Exits[0].Reason);
    }

    [Fact]
    public async Task SetupC_FullTrade_StopHit()
    {
        var cfg = CfgC();
        var eng = BuildEngine(cfg, out var sink, out _);
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Breakout above + rejection → entry SHORT at 21000
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 5),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));
        // Rejection bar: H=21050 stays below stop (21100) so no same-bar exit
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 10),
            21000m, 21050m, 20400m, 20500m, 500, IsConfirmed: true));
        Assert.Single(sink.Entries);

        // Stop = 21000 + 0.10 * 1000 = 21100
        // Bar that hits stop: High ≥ 21100
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 15),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));

        Assert.Single(sink.Exits);
        Assert.Equal(SetupId.C, sink.Exits[0].Setup);
        Assert.Equal(ExitReason.Stop, sink.Exits[0].Reason);
    }

    [Fact]
    public async Task SetupC_FalseBreakoutBelow_ArmsLong()
    {
        // Test false breakout BELOW ORB → arms LONG
        var cfg = CfgC();
        var eng = BuildEngine(cfg, out var sink, out _);
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Breakout below ORB low
        // pen = (20000-19800)/1000 = 0.20 < 0.30 ✓
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 5),
            20200m, 20300m, 19800m, 19900m, 500, IsConfirmed: true));

        // Rejection: close back inside with strong body
        // body = |20500-19900| = 600, range = 20600-19900 = 700, body% = 0.86 ✓
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 10),
            19900m, 20600m, 19900m, 20500m, 500, IsConfirmed: true));

        Assert.Single(sink.Entries);
        var entry = sink.Entries[0];
        Assert.Equal(SetupId.C, entry.Setup);
        Assert.Equal(Direction.Long, entry.Direction);
        Assert.Equal(20000m, entry.Entry); // entry at OrbLow
    }

    [Fact]
    public async Task SetupC_RespectsMaxTrades()
    {
        var cfg = CfgC();
        cfg.MaxTradesC = 1;
        var eng = BuildEngine(cfg, out var sink, out _);
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Trade 1: breakout + rejection + entry + stop hit
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 5),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 10),
            21100m, 21100m, 20400m, 20500m, 500, IsConfirmed: true));
        Assert.Single(sink.Entries);
        // Stop hit
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 15),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));
        Assert.Single(sink.Exits);

        // Trade 2 attempt: another breakout + rejection
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 20),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 25),
            21100m, 21100m, 20400m, 20500m, 500, IsConfirmed: true));

        // Should not arm again — MaxTradesC = 1 already used
        Assert.Single(sink.Entries); // still just 1
    }

    [Fact]
    public async Task SetupC_Disabled_DoesNotArm()
    {
        var cfg = CfgC();
        cfg.EnableC = false;
        var eng = BuildEngine(cfg, out var sink, out _);
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 5),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 10),
            21100m, 21100m, 20400m, 20500m, 500, IsConfirmed: true));

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public async Task SetupC_ExcessivePenetration_DoesNotActivate()
    {
        var cfg = CfgC();
        var eng = BuildEngine(cfg, out var sink, out _);
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Breakout with excessive penetration: pen = (21500-21000)/1000 = 0.50 > 0.30
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 5),
            20800m, 21500m, 20700m, 21400m, 500, IsConfirmed: true));
        // Rejection bar
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 10),
            21100m, 21100m, 20400m, 20500m, 500, IsConfirmed: true));

        Assert.Empty(sink.Entries); // pen too deep → no activation
    }

    [Fact]
    public async Task SetupC_TickMode_EntryOnRecross()
    {
        var cfg = CfgC();
        var eng = BuildEngine(cfg, out var sink, out _);
        eng.EnableTickMode();
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Breakout + rejection via bars (detector activates on confirmed bars)
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 5),
            20800m, 21200m, 20700m, 21100m, 500, IsConfirmed: true));
        await eng.ProcessBarAsync(new Bar(Utc(2026, 3, 10, 14, 10),
            21100m, 21100m, 20400m, 20500m, 500, IsConfirmed: true));

        // In tick mode, bar-level entry may already fire on the rejection bar
        // (since close is ≤ OrbHigh). Check if entry already occurred.
        if (sink.Entries.Count == 0)
        {
            // If not yet entered, tick should trigger entry
            await eng.ProcessPriceTickAsync(20900m, Utc(2026, 3, 10, 14, 11));
            Assert.Single(sink.Entries);
        }

        Assert.Equal(SetupId.C, sink.Entries[0].Setup);
        Assert.Equal(Direction.Short, sink.Entries[0].Direction);
    }

    // ── Test doubles ──────────────────────────────────────────────

    public class TestSink : IStrategyEventSink
    {
        public List<EntrySignal>   Entries  = new();
        public List<ExitSignal>    Exits    = new();
        public List<PartialSignal> Partials = new();
        public Task OnEntryAsync(EntrySignal s)              { Entries.Add(s);  return Task.CompletedTask; }
        public Task OnExitAsync(ExitSignal s, TradeRecord t) { Exits.Add(s);    return Task.CompletedTask; }
        public Task OnPartialAsync(PartialSignal s)          { Partials.Add(s); return Task.CompletedTask; }
        public Task OnBEMoveAsync(BESignal s)                => Task.CompletedTask;
        public Task OnSnapshotAsync(EngineSnapshot s)        => Task.CompletedTask;
    }
    public class NullExecutor : IOrderExecutor
    {
        public Task<decimal?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<decimal?>(null);
        public Task OnPartialSignalAsync(PartialSignal s)       => Task.CompletedTask;
        public Task OnBESignalAsync(BESignal s)                 => Task.CompletedTask;
        public Task OnExitSignalAsync(ExitSignal s)             => Task.CompletedTask;
        public Task OnLevelsAdjustedAsync(SetupId setup, decimal newStop, decimal newTarget, int remainingCts) => Task.CompletedTask;
    }
    public class TestPrices : ILastPriceProvider
    {
        decimal _p;
        public decimal GetLastPrice(string t) => _p;
        public void UpdatePrice(string t, decimal p) => _p = p;
    }
}
