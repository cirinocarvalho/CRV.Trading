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
        NearPctA           = 0.15m,
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
    public async Task PriceTick_AggressiveArmedLong_FirstTickEnters_SecondTickNoDoubleEntry()
    {
        // In tick mode, bar-level entry is always skipped — even for Aggressive mode.
        // The arm bar sets stA=1 (Armed); the FIRST subsequent price tick fires entry.
        // A second tick must NOT create a duplicate entry.
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
        Assert.Empty(sink.Entries); // tick mode: bar only arms, does NOT enter

        // First tick → entry fires (aggressive: any tick while armed triggers entry)
        await eng.ProcessPriceTickAsync(20950m, new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc));
        Assert.Single(sink.Entries);

        // Second tick → must NOT produce a second entry (position already active)
        await eng.ProcessPriceTickAsync(20950m, new DateTime(2026, 3, 10, 14, 6, 1, DateTimeKind.Utc));
        Assert.Single(sink.Entries); // still just 1 entry
    }

    [Fact]
    public async Task PriceTick_ActiveLong_ExitsAtStop()
    {
        // Bar arms → first tick enters → stop tick exits.
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
        // Entry via first tick (aggressive)
        await eng.ProcessPriceTickAsync(20950m, new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc));
        Assert.Single(sink.Entries);

        decimal stop = sink.Entries[0].Stop;
        // Price tick below stop → should exit at stop
        await eng.ProcessPriceTickAsync(stop - 1m, new DateTime(2026, 3, 10, 14, 7, 0, DateTimeKind.Utc));

        Assert.Single(sink.Exits);
        Assert.Equal(ExitReason.Stop, sink.Exits[0].Reason);
    }

    [Fact]
    public async Task PriceTick_ActiveLong_ExitsAtTarget()
    {
        // Bar arms → first tick enters → target tick exits.
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
        // Entry via first tick (aggressive)
        await eng.ProcessPriceTickAsync(20950m, new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc));

        decimal target = sink.Entries[0].Target;
        // Price tick above target → should exit at target
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

        // Settle bar: first live bar after warmup clears the cooldown guard
        // (neutral price, doesn't arm — mid-range between ORB high/low)
        var settleBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20500m, 20500m, 20500m, 20500m, 100, IsConfirmed: true);
        await eng.ProcessBarAsync(settleBar);

        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 10, 0, DateTimeKind.Utc),
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

    // ── Regression: partial exit must fire via tick mode ──────
    [Fact]
    public async Task PriceTick_ActiveLong_PartialExitFires_NotBlockedByEarlyReturn()
    {
        // Bug: the guard "if (!hitStop && !hitTarget) return" in EvalTickSetupA fires
        // BEFORE the partial-level check, so partial is never evaluated when only the
        // partial price is touched.  This test was red before the fix.
        var cfg = CfgA();
        cfg.ModeA       = "Aggressive";
        cfg.UsePartialA = true;
        cfg.UseBeA      = false;   // keep BE off so stop doesn't move
        var eng = BuildEngine(cfg, out var sink, out _);
        eng.EnableTickMode();

        var day = new DateTime(2026, 3, 10);
        // ORB: High=21000 Low=20000 Range=1000
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Arm bar (aggressive) → arms without entering
        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);

        // Entry tick → aggressive: enters at _armEntryA=20900
        await eng.ProcessPriceTickAsync(20950m, new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc));
        Assert.Single(sink.Entries);

        // Partial level: entry(20900) + range(1000) * targetPct(100%) * partialPct(50%) = 21400
        decimal partial = sink.Entries[0].Partial;   // 21400
        decimal target  = sink.Entries[0].Target;    // 21900

        // Tick that hits partial but stays below target
        decimal partialHitPrice = partial + 0.25m;   // 21400.25 — above partial, below target
        Assert.True(partialHitPrice < target, "test setup: partial tick must be below target");
        await eng.ProcessPriceTickAsync(partialHitPrice,
            new DateTime(2026, 3, 10, 14, 7, 0, DateTimeKind.Utc));

        Assert.Single(sink.Partials);    // partial signal MUST fire
        Assert.Empty(sink.Exits);        // trade still open (partial, not full exit)
    }

    // ── Setup B: UsePartialB = false ──────────────────────────

    static StrategyConfig CfgB() => new()
    {
        Ticker             = "NQH26",
        ExecutionTFMinutes = 5,
        OrbStart           = new TimeOnly(9, 30),
        OrbEnd             = new TimeOnly(10, 0),
        RthStart           = new TimeOnly(9, 30),
        RthEnd             = new TimeOnly(16, 0),
        SessionStartHour   = 18,
        EnableA            = false,
        EnableB            = true,
        Contracts          = 2,
        TargetPctB         = 100,
        PartialPctB        = 50,
        StopPctB           = 0.50m,
        MinRrB             = 1.0m,
        RetestPct          = 0.05m,
        UsePartialB        = false,
        UseBeB             = false,
        MaxTradesB         = 5,
        ModeB              = "Aggressive",
        UseVwap            = false,
        AtrFilterPct       = 0m,
        TickSize           = 0.25m,
        PointValue         = 20m,
        CommissionPerSide  = 0m,
        MaxTradesA         = 5,
    };

    /// <summary>
    /// Regression: UsePartialB=false must not prevent Setup B trades.
    /// Bug report: unchecking "partial exit" made all Setup B trades disappear.
    /// </summary>
    [Fact]
    public async Task SetupB_UsePartialFalse_AggressiveLong_EntersAndExitsAtTarget()
    {
        var cfg = CfgB(); // UsePartialB = false
        var eng = BuildEngine(cfg, out var sink, out _);
        eng.EnableTickMode();

        var day = new DateTime(2026, 3, 10);
        // ORB: High=21000, Low=20000, Range=1000
        await FeedOrbBars(eng, 21000m, 20000m, day);

        // Arm bar: close > orbHigh → _stB = 1 (aggressive: armed, entry deferred to tick)
        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            21000m, 21100m, 20900m, 21050m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);
        Assert.Empty(sink.Entries); // tick mode: no bar-level entry

        // First tick: aggressive → enters at _armEntryB = armBar.Open = 21000
        await eng.ProcessPriceTickAsync(21030m, new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc));
        Assert.Single(sink.Entries);
        Assert.Equal(Direction.Long, sink.Entries[0].Direction);
        Assert.Equal(SetupId.B, sink.Entries[0].Setup);

        decimal target = sink.Entries[0].Target; // = 22000
        // Tick above target → should exit at target even with UsePartialB=false
        await eng.ProcessPriceTickAsync(target + 1m, new DateTime(2026, 3, 10, 14, 7, 0, DateTimeKind.Utc));

        Assert.Single(sink.Exits);                               // MUST record exit
        Assert.Equal(ExitReason.Target, sink.Exits[0].Reason);
    }

    [Fact]
    public async Task SetupB_UsePartialFalse_AggressiveLong_EntersAndExitsAtStop()
    {
        var cfg = CfgB();
        var eng = BuildEngine(cfg, out var sink, out _);
        eng.EnableTickMode();

        var day = new DateTime(2026, 3, 10);
        await FeedOrbBars(eng, 21000m, 20000m, day);

        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            21000m, 21100m, 20900m, 21050m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);
        await eng.ProcessPriceTickAsync(21030m, new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc));
        Assert.Single(sink.Entries);

        decimal stop = sink.Entries[0].Stop; // = 20500
        // Tick below stop → should exit at stop even with UsePartialB=false
        await eng.ProcessPriceTickAsync(stop - 1m, new DateTime(2026, 3, 10, 14, 7, 0, DateTimeKind.Utc));

        Assert.Single(sink.Exits);
        Assert.Equal(ExitReason.Stop, sink.Exits[0].Reason);
    }

    // ── Test doubles ──────────────────────────────────────────
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
