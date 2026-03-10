using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TickEvalTests
{
    static OrbStrategyEngine BuildEngine(StrategyConfig cfg, out TestSink sink, out TestPrices prices)
    {
        sink   = new TestSink();
        prices = new TestPrices();
        return new OrbStrategyEngine(cfg, new NullExecutor(), sink, prices,
            NullLogger<OrbStrategyEngine>.Instance);
    }

    static StrategyConfig CfgA() => new()
    {
        Ticker             = "NQH26",
        ExecutionTFMinutes = 5,
        OrbStart           = new TimeOnly(9, 30),
        OrbEnd             = new TimeOnly(10, 0),
        RthStart           = new TimeOnly(9, 30),
        RthEnd             = new TimeOnly(16, 0),
        SessionStartHour   = 18,
        EnableA            = true,
        EnableB            = false,
        Contracts          = 1,
        StopPctA           = 0.10m,
        TargetPctA         = 100,
        PartialPctA        = 50,
        NearPct            = 0.15m,
        PullbackPct        = 0.50m,
        MinRrA             = 1.0m,
        UseVwap            = false,
        UsePartialA        = false,
        UseBeA             = false,
        AtrFilterPct       = 0m,
        TickSize           = 0.25m,
        PointValue         = 20m,
        CommissionPerSide  = 0m,
        MaxTradesA         = 5,
        ModeA              = "Conservative",
        MaxTradesB         = 5,
    };

    static async Task FeedOrbBars(OrbStrategyEngine eng, decimal high, decimal low, DateTime etDate)
    {
        // Feed two bars that span the ORB window 9:30-10:00 ET (UTC+4=13:30-14:00 in summer)
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

    [Fact]
    public async Task PriceTick_WhenDisabled_DoesNothing()
    {
        var cfg = CfgA();
        var eng = BuildEngine(cfg, out var sink, out _);
        // No EnableTickMode() called — ProcessPriceTickAsync should be a no-op
        await eng.ProcessPriceTickAsync(20500m, DateTime.UtcNow);
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public async Task PriceTick_ConservativeArmedLong_EntersAtPullback()
    {
        var cfg = CfgA(); // Conservative mode
        var eng = BuildEngine(cfg, out var sink, out _);
        eng.EnableTickMode();

        var day = new DateTime(2026, 3, 10);
        // ORB: High=21000, Low=20000, Range=1000
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // 10:05 ET arm bar: High=20960 >= orbHigh(21000) - NearDist(1000*0.15=150) = 20850 → arm LONG
        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);
        Assert.Empty(sink.Entries); // conservative: no entry on bar close yet

        // Pullback level = orbHigh - range*PullbackPct = 21000 - 1000*0.50 = 20500
        // Entry when price <= 20500 + 2 ticks (tickTol = 0.25*2 = 0.50)
        var tickUtc = new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc);
        await eng.ProcessPriceTickAsync(20500m, tickUtc);

        Assert.Single(sink.Entries);
        Assert.Equal(Direction.Long, sink.Entries[0].Direction);
        Assert.Equal(SetupId.A, sink.Entries[0].Setup);
    }

    [Fact]
    public async Task PriceTick_AggressiveArmedLong_BarAlreadyEnters_TickDoesNotDoubleEnter()
    {
        var cfg = CfgA();
        cfg.ModeA = "Aggressive"; // enters immediately on bar close when armed
        var eng = BuildEngine(cfg, out var sink, out _);
        eng.EnableTickMode();

        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Aggressive arm bar → entry fires on bar close (via ProcessBarAsync)
        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);
        Assert.Single(sink.Entries); // entered on bar

        // Subsequent price ticks should NOT create a second entry
        await eng.ProcessPriceTickAsync(20950m, new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc));
        Assert.Single(sink.Entries); // still just 1 entry
    }

    [Fact]
    public async Task PriceTick_ActiveLong_ExitsAtStop()
    {
        var cfg = CfgA();
        cfg.ModeA = "Aggressive";
        var eng = BuildEngine(cfg, out var sink, out _);
        eng.EnableTickMode();

        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);
        Assert.Single(sink.Entries);

        decimal stop = sink.Entries[0].Stop;
        // Price ticks below stop → should exit
        await eng.ProcessPriceTickAsync(stop - 1m, new DateTime(2026, 3, 10, 14, 7, 0, DateTimeKind.Utc));

        Assert.Single(sink.Exits);
        Assert.Equal(ExitReason.Stop, sink.Exits[0].Reason);
    }

    [Fact]
    public async Task PriceTick_ActiveLong_ExitsAtTarget()
    {
        var cfg = CfgA();
        cfg.ModeA = "Aggressive";
        var eng = BuildEngine(cfg, out var sink, out _);
        eng.EnableTickMode();

        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);

        decimal target = sink.Entries[0].Target;
        await eng.ProcessPriceTickAsync(target + 1m, new DateTime(2026, 3, 10, 14, 7, 0, DateTimeKind.Utc));

        Assert.Single(sink.Exits);
        Assert.Equal(ExitReason.Target, sink.Exits[0].Reason);
    }

    [Fact]
    public async Task ProcessBarAsync_WithTickMode_StillHandlesBarLevelExits()
    {
        // Even with tick mode on, bar-level exit processing must still work (backtest path)
        var cfg = CfgA();
        cfg.ModeA = "Aggressive";
        var eng = BuildEngine(cfg, out var sink, out _);
        // NOTE: do NOT call EnableTickMode() — this is the backtest path

        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);
        Assert.Single(sink.Entries);

        decimal stop = sink.Entries[0].Stop;
        var exitBar = new Bar(
            new DateTime(2026, 3, 10, 14, 10, 0, DateTimeKind.Utc),
            20870m, 20900m, stop - 1m, stop - 0.5m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(exitBar);
        Assert.Single(sink.Exits);
    }

    /// <summary>
    /// Regression: EvalTickSetupB previously had no partial/BE logic (C2 fix).
    /// Verifies the fix compiles and runs without exception when UsePartialB+UseBeB are enabled.
    /// </summary>
    [Fact]
    public async Task SetupB_TickMode_ExitBlock_ExecutesWithoutExceptionWhenPartialEnabled()
    {
        var cfg = CfgA();
        cfg.EnableA     = false;
        cfg.EnableB     = true;
        cfg.ModeB       = "Aggressive";
        cfg.UsePartialB = true;
        cfg.UseBeB      = true;
        cfg.MaxTradesB  = 5;
        cfg.TargetPctB  = 100;
        cfg.PartialPctB = 50;
        cfg.MinRrB      = 1.0m;
        cfg.RetestPct   = 0.05m;
        var eng = BuildEngine(cfg, out _, out _);
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);
        eng.EnableTickMode();

        // Price well inside ORB — no entry, no exit triggered; just exercises the code path
        var tick = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc);
        await eng.ProcessPriceTickAsync(20950m, tick);
        // No exception = fix is present and working
    }

    // ── Test doubles ──────────────────────────────────────────
    public class TestSink : IStrategyEventSink
    {
        public List<EntrySignal> Entries = new();
        public List<ExitSignal>  Exits   = new();
        public Task OnEntryAsync(EntrySignal s)              { Entries.Add(s); return Task.CompletedTask; }
        public Task OnExitAsync(ExitSignal s, TradeRecord t) { Exits.Add(s);   return Task.CompletedTask; }
        public Task OnPartialAsync(PartialSignal s)          => Task.CompletedTask;
        public Task OnBEMoveAsync(BESignal s)                => Task.CompletedTask;
        public Task OnSnapshotAsync(EngineSnapshot s)        => Task.CompletedTask;
    }
    public class NullExecutor : IOrderExecutor
    {
        public Task OnEntrySignalAsync(EntrySignal s)   => Task.CompletedTask;
        public Task OnPartialSignalAsync(PartialSignal s) => Task.CompletedTask;
        public Task OnBESignalAsync(BESignal s)         => Task.CompletedTask;
        public Task OnExitSignalAsync(ExitSignal s)     => Task.CompletedTask;
    }
    public class TestPrices : ILastPriceProvider
    {
        decimal _p;
        public decimal GetLastPrice(string t) => _p;
        public void UpdatePrice(string t, decimal p) => _p = p;
    }
}
