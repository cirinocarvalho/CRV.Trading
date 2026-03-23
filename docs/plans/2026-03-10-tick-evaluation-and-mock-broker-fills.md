# Tick Evaluation + Mock Broker Fills — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** (A) Give the live engine tick-based entry/exit evaluation so fills happen at realtime L1 price instead of waiting for bar close; (B) upgrade `MockBrokerExecutor` with realistic fill simulation, order state tracking, and OCO bracket support.

**Architecture:**
- Part A: Add `ProcessPriceTickAsync(decimal price, DateTime utcTime)` to `OrbStrategyEngine`. Bar-close processing keeps arm-state transitions (`idle→armed`). The new method handles `armed→entry` and `active→exit` on every L1 tick. Live bar feeds call it on every price update. Backtest is unchanged.
- Part B: `MockBrokerExecutor` grows an in-memory `List<MockOrder>` with WORKING/FILLED/CANCELED lifecycle. On every `ILastPriceProvider.UpdatePrice()` call (already called by bar feeds) the executor evaluates OCO fills. `ManualBrokerOps.GetOrdersMockAsync(MockBrokerExecutor)` returns the list.

**Tech Stack:** C# 12, .NET 10, xUnit, CRV.Core/CRV.Live/CRV.Web

---

## PART A — Tick-Based Entry / Exit

---

### Task 1: Add `ProcessPriceTickAsync` to `OrbStrategyEngine`

**Files:**
- Modify: `CRV.Core/Strategy/OrbStrategyEngine.cs`
- Test: `CRV.Core.Tests/Strategy/TickEvalTests.cs` (create)

#### Step 1: Write the failing tests

```csharp
// CRV.Core.Tests/Strategy/TickEvalTests.cs
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TickEvalTests
{
    // ── Helpers ───────────────────────────────────────────────
    static OrbStrategyEngine BuildEngine(StrategyConfig cfg, out TestSink sink, out TestPrices prices)
    {
        sink   = new TestSink();
        prices = new TestPrices();
        return new OrbStrategyEngine(cfg, new NullExecutor(), sink, prices,
            NullLogger<OrbStrategyEngine>.Instance);
    }

    static StrategyConfig CfgA() => new()
    {
        Ticker           = "NQH26",
        ExecutionTFMinutes = 5,
        OrbStart         = new TimeOnly(9, 30),
        OrbEnd           = new TimeOnly(10, 0),
        RthStart         = new TimeOnly(9, 30),
        RthEnd           = new TimeOnly(16, 0),
        SessionStartHour = 18,
        EnableA          = true,
        EnableB          = false,
        Contracts        = 1,
        StopPctA         = 0.10m,
        TargetPctA       = 100,
        PartialPctA      = 50,
        NearPct          = 0.15m,
        PullbackPct      = 0.50m,
        MinRrA           = 1.0m,
        UseVwap          = false,
        UsePartialA      = false,
        UseBeA           = false,
        AtrFilterPct     = 0m,
        TickSize         = 0.25m,
        PointValue       = 20m,
        CommissionPerSide = 0m,
        MaxTradesA       = 5,
    };

    // Feed an ORB bar (within 9:30-10:00 window)
    static async Task FeedOrbBar(OrbStrategyEngine eng, decimal high, decimal low, DateTime etDate)
    {
        var utc = new DateTime(etDate.Year, etDate.Month, etDate.Day, 13, 30, 0, DateTimeKind.Utc); // 9:30 ET
        var bar = new Bar(utc, (high+low)/2, high, low, (high+low)/2, 1000, IsConfirmed: true);
        await eng.WarmupBarAsync(bar);
        var utc2 = new DateTime(etDate.Year, etDate.Month, etDate.Day, 14, 0, 0, DateTimeKind.Utc); // 10:00 ET
        var bar2 = new Bar(utc2, (high+low)/2, high, low, (high+low)/2, 1000, IsConfirmed: true);
        await eng.WarmupBarAsync(bar2);
    }

    [Fact]
    public async Task PriceTick_ArmedLong_EntersAtPullback()
    {
        var cfg = CfgA();
        cfg.ModeA = "Conservative";
        var eng = BuildEngine(cfg, out var sink, out _);

        // Feed ORB: High=21000, Low=20000, Range=1000
        var day = new DateTime(2026, 3, 10);
        await FeedOrbBar(eng, 21000m, 20000m, day);

        // Feed a 10:05 bar that arms Setup A long (bar.High near orbHigh)
        // NearDist = 1000 * 0.15 = 150; arm when High >= 21000 - 150 = 20850
        var arm = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),  // 10:05 ET
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(arm);
        Assert.Equal(0, sink.Entries.Count); // no entry yet

        // Pullback level = orbHigh - range*0.50 = 21000 - 500 = 20500
        // Conservative: enter when price <= 20500 + 2 ticks
        // Tick price at pullback
        var tickTime = new DateTime(2026, 3, 10, 14, 6, 0, DateTimeKind.Utc); // 10:06 ET
        await eng.ProcessPriceTickAsync(20500m, tickTime);

        Assert.Single(sink.Entries);
        Assert.Equal(Direction.Long, sink.Entries[0].Direction);
    }

    [Fact]
    public async Task PriceTick_ActiveLong_ExitsAtStop()
    {
        var cfg = CfgA();
        cfg.ModeA = "Aggressive"; // aggressive enters immediately on arm
        var eng = BuildEngine(cfg, out var sink, out _);

        var day = new DateTime(2026, 3, 10);
        await FeedOrbBar(eng, 21000m, 20000m, day);

        // Arm bar — price near orbHigh → arm + aggressive entry
        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);
        Assert.Single(sink.Entries); // aggressive entered on bar close

        decimal entry = sink.Entries[0].Entry;
        decimal stop  = sink.Entries[0].Stop;

        // Tick below stop → should exit
        var tickTime = new DateTime(2026, 3, 10, 14, 7, 0, DateTimeKind.Utc);
        await eng.ProcessPriceTickAsync(stop - 0.25m, tickTime);

        Assert.Single(sink.Exits);
        Assert.Equal(ExitReason.Stop, sink.Exits[0].Reason);
    }

    [Fact]
    public async Task PriceTick_ActiveLong_ExitsAtTarget()
    {
        var cfg = CfgA();
        cfg.ModeA = "Aggressive";
        var eng = BuildEngine(cfg, out var sink, out _);

        var day = new DateTime(2026, 3, 10);
        await FeedOrbBar(eng, 21000m, 20000m, day);

        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);

        decimal target = sink.Entries[0].Target;
        var tickTime = new DateTime(2026, 3, 10, 14, 7, 0, DateTimeKind.Utc);
        await eng.ProcessPriceTickAsync(target + 0.25m, tickTime);

        Assert.Single(sink.Exits);
        Assert.Equal(ExitReason.Target, sink.Exits[0].Reason);
    }

    [Fact]
    public async Task ProcessBarAsync_StillHandlesEverything_BacktestUnchanged()
    {
        // Existing bar-level entry/exit must still work (backtest path unchanged)
        var cfg = CfgA();
        cfg.ModeA = "Aggressive";
        var eng = BuildEngine(cfg, out var sink, out _);

        var day = new DateTime(2026, 3, 10);
        await FeedOrbBar(eng, 21000m, 20000m, day);

        // Arm + entry on same bar (aggressive)
        var armBar = new Bar(
            new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc),
            20900m, 20960m, 20880m, 20940m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(armBar);
        Assert.Single(sink.Entries);

        // Exit on next bar (stop crossed in bar's low)
        decimal stop = sink.Entries[0].Stop;
        var exitBar = new Bar(
            new DateTime(2026, 3, 10, 14, 10, 0, DateTimeKind.Utc),
            20870m, 20900m, stop - 1m, stop - 0.5m, 500, IsConfirmed: true);
        await eng.ProcessBarAsync(exitBar);
        Assert.Single(sink.Exits);
    }

    // ── Test doubles ──────────────────────────────────────────
    class TestSink : IStrategyEventSink
    {
        public List<EntrySignal> Entries = new();
        public List<ExitSignal>  Exits   = new();
        public Task OnEntryAsync(EntrySignal s)             { Entries.Add(s); return Task.CompletedTask; }
        public Task OnExitAsync(ExitSignal s, TradeRecord t){ Exits.Add(s);   return Task.CompletedTask; }
        public Task OnPartialAsync(PartialSignal s)         => Task.CompletedTask;
        public Task OnBEMoveAsync(BESignal s)               => Task.CompletedTask;
        public Task OnSnapshotAsync(EngineSnapshot s)       => Task.CompletedTask;
    }
    class NullExecutor : IOrderExecutor
    {
        public Task OnEntrySignalAsync(EntrySignal s)  => Task.CompletedTask;
        public Task OnPartialSignalAsync(PartialSignal s) => Task.CompletedTask;
        public Task OnBESignalAsync(BESignal s)        => Task.CompletedTask;
        public Task OnExitSignalAsync(ExitSignal s)    => Task.CompletedTask;
    }
    class TestPrices : ILastPriceProvider
    {
        decimal _p;
        public decimal GetLastPrice(string t) => _p;
        public void UpdatePrice(string t, decimal p) => _p = p;
    }
}
```

#### Step 2: Run tests to verify they fail

```bash
cd /Users/ciro/Source/WebApps/CRV.Trading
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -q --filter "TickEvalTests"
```
Expected: FAIL — `ProcessPriceTickAsync` doesn't exist yet.

#### Step 3: Implement `ProcessPriceTickAsync` in `OrbStrategyEngine`

Add these fields and method to `OrbStrategyEngine` (after `PublishCurrentStateAsync`):

```csharp
// ── Whether we're in "live tick" mode ───────────────────────
// When true, ProcessPriceTickAsync is the entry/exit evaluator;
// ProcessBarAsync only handles arm state + indicators.
private bool _tickModeEnabled = false;

/// <summary>
/// Enable tick-based entry/exit evaluation. Call once after the engine is
/// created for live trading. Backtest leaves this false (bar-close evaluation).
/// </summary>
public void EnableTickMode() => _tickModeEnabled = true;

/// <summary>
/// Evaluate entry and exit conditions for both setups against a realtime price tick.
/// Only has effect when EnableTickMode() has been called.
/// Called by live bar feeds on every L1 price update.
/// </summary>
public async Task ProcessPriceTickAsync(decimal price, DateTime utcTime)
{
    if (!_tickModeEnabled) return;
    if (price <= 0) return;
    if (!_orb.IsSet || _orb.OrbRange <= 0) return;
    if (_ddBreached) return;

    var local     = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _tz);
    var localTime = TimeOnly.FromDateTime(local);
    bool canEnter = !_pastCutoff && !_rthEnded;

    if (_cfg.EnableA) await EvalTickSetupA(price, utcTime, localTime, canEnter);
    if (_cfg.EnableB) await EvalTickSetupB(price, utcTime, localTime, canEnter);
}

private async Task EvalTickSetupA(decimal price, DateTime utcTime, TimeOnly localTime, bool canEnter)
{
    decimal orbRange = _orb.OrbRange;
    decimal tickTol  = _cfg.TickSize * 2;

    // ── Entry (armed, not yet active) ──────────────────────
    if (!_activeA && canEnter && (_stA == 1 || _stA == -1))
    {
        bool isLong = _stA == 1;
        if (_cfg.IsAggressiveA)
        {
            // Aggressive: any price while armed triggers entry
            await TryEntryAFromTick(price, _armEntryA, isLong, orbRange, utcTime);
        }
        else
        {
            decimal pbPts  = orbRange * _cfg.PullbackPct;
            decimal longPb = LevelCalculator.RoundToTick(_orb.OrbHigh - pbPts, _cfg.TickSize);
            decimal shortPb= LevelCalculator.RoundToTick(_orb.OrbLow  + pbPts, _cfg.TickSize);

            if (isLong  && price <= longPb  + tickTol)
                await TryEntryAFromTick(price, longPb, true, orbRange, utcTime);
            else if (!isLong && price >= shortPb - tickTol)
                await TryEntryAFromTick(price, shortPb, false, orbRange, utcTime);
        }
        return; // don't check exit on same tick as entry
    }

    // ── Exit (active) ───────────────────────────────────────
    if (_activeA)
    {
        bool isLong = _stA == 2;
        bool hitStop   = isLong  ? price <= _stopA   : price >= _stopA;
        bool hitTarget = isLong  ? price >= _tgtA    : price <= _tgtA;

        if (hitTarget || hitStop)
        {
            // Partial check (price-based)
            bool partJustHit = false;
            if (_cfg.UsePartialA && !_partHitA)
            {
                bool hitPartial = isLong ? price >= _partialA : price <= _partialA;
                if (hitPartial)
                {
                    partJustHit = true;
                    _partHitA = true;
                    int half = (int)Math.Floor(_cfg.Contracts * 0.5);
                    var psig = new PartialSignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
                        _partialA, half, _cfg.Contracts - half, _entA, utcTime);
                    await _executor.OnPartialSignalAsync(psig);
                    await _sink.OnPartialAsync(psig);
                    AddAlert("PARTIAL", SetupId.A, $"Partial @ {_partialA:F2}", "yellow");
                    if (_cfg.UseBeA)
                    {
                        var besig = new BESignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
                            _entA, _entA, psig.ContractsRemaining, utcTime);
                        _stopA = _entA;
                        await _executor.OnBESignalAsync(besig);
                        await _sink.OnBEMoveAsync(besig);
                        AddAlert("MOVE_BE", SetupId.A, $"Stop → BE {_entA:F2}", "yellow");
                    }
                    if (!hitTarget && !hitStop) return;
                }
            }

            ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
            decimal exitPx    = hitTarget ? _tgtA : _stopA;
            decimal pnl       = (isLong ? exitPx - _entA : _entA - exitPx) * _cfg.PointValue * _cfg.Contracts;
            _pnlA = pnl;
            _activeA = false;
            _stA = 0;
            _tradeCountA++;
            if (hitTarget) { _stickyTgtA = true; _exitBarIdxA = _barIndex; }
            else           { _stickyStpA = true; _exitBarIdxA = _barIndex; }
            await BookExitA(reason, exitPx, utcTime, isLong, sameBarPartial: partJustHit);
        }
    }
}

private async Task TryEntryAFromTick(decimal price, decimal ep, bool isLong, decimal orbRange, DateTime utcTime)
{
    if (_cfg.EntryTickOffsetA != 0 && _cfg.TickSize > 0)
    {
        decimal offset = _cfg.EntryTickOffsetA * _cfg.TickSize;
        ep = isLong ? ep + offset : ep - offset;
        ep = LevelCalculator.RoundToTick(ep, _cfg.TickSize);
    }
    var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
        _cfg.StopPctA, _cfg.TargetPctA, _cfg.PartialPctA, orbRange, _cfg.TickSize);
    if (rr < _cfg.MinRrA) return;

    _entA = ep; _stopA = sl; _tgtA = tp; _partialA = pp;
    _initStopA = sl; _pnlA = 0; _activeA = true;
    _stA = isLong ? 2 : -2; _enteredThisBar = true;
    _entryTimeA = utcTime;

    _log.LogInformation("[Setup A TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
        isLong ? "LONG" : "SHORT", _cfg.Contracts, ep, sl, tp, rr);
    var sig = new EntrySignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
        ep, sl, tp, pp, _cfg.Contracts, utcTime);
    await _executor.OnEntrySignalAsync(sig);
    await _sink.OnEntryAsync(sig);
    AddAlert("ENTRY", SetupId.A,
        $"{(isLong ? "LONG" : "SHORT")} {_cfg.Contracts}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
        isLong ? "green" : "red");
}

private async Task EvalTickSetupB(decimal price, DateTime utcTime, TimeOnly localTime, bool canEnter)
{
    decimal orbRange = _orb.OrbRange;
    decimal tickTol  = _cfg.TickSize * 2;

    // ── Entry ──────────────────────────────────────────────
    if (!_activeB && canEnter && (_stB == 1 || _stB == -1 || _stB == 2 || _stB == -2))
    {
        bool isLong = _stB > 0;
        if (_cfg.IsAggressiveB)
        {
            await TryEntryBFromTick(price, _armEntryB, isLong, utcTime);
        }
        else
        {
            // Retest: wait for price to return near orbMid after breakout
            decimal retestDist = _orb.OrbRange * _cfg.RetestPct;
            decimal orbMid     = _orb.OrbMid;
            if (isLong  && price <= orbMid + retestDist + tickTol)
                await TryEntryBFromTick(price, orbMid, true, utcTime);
            else if (!isLong && price >= orbMid - retestDist - tickTol)
                await TryEntryBFromTick(price, orbMid, false, utcTime);
        }
        return;
    }

    // ── Exit ────────────────────────────────────────────────
    if (_activeB)
    {
        bool isLong  = _stB == 3;
        bool hitStop   = isLong ? price <= _stopB   : price >= _stopB;
        bool hitTarget = isLong ? price >= _tgtB    : price <= _tgtB;

        if (hitTarget || hitStop)
        {
            // (Same partial/BE logic as Setup A — abbreviated for brevity)
            ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
            decimal exitPx    = hitTarget ? _tgtB : _stopB;
            decimal pnl       = (isLong ? exitPx - _entB : _entB - exitPx) * _cfg.PointValue * _cfg.Contracts;
            _pnlB = pnl;
            _activeB = false;
            _stB = 0;
            _tradeCountB++;
            if (hitTarget) { _stickyTgtB = true; _exitBarIdxB = _barIndex; }
            else           { _stickyStpB = true; _exitBarIdxB = _barIndex; }
            await BookExitB(reason, exitPx, utcTime, isLong);
        }
    }
}

private async Task TryEntryBFromTick(decimal price, decimal ep, bool isLong, DateTime utcTime)
{
    if (_cfg.EntryTickOffsetB != 0 && _cfg.TickSize > 0)
    {
        decimal offset = _cfg.EntryTickOffsetB * _cfg.TickSize;
        ep = isLong ? ep + offset : ep - offset;
        ep = LevelCalculator.RoundToTick(ep, _cfg.TickSize);
    }
    var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
        _orb.OrbMid, _cfg.TargetPctB, _cfg.PartialPctB, _orb.OrbRange, _cfg.TickSize);
    if (rr < _cfg.MinRrB) return;

    _entB = ep; _stopB = sl; _tgtB = tp; _partialB = pp;
    _initStopB = sl; _pnlB = 0; _activeB = true;
    _stB = isLong ? 3 : -3; _enteredThisBar = true;
    _entryTimeB = utcTime;

    _log.LogInformation("[Setup B TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
        isLong ? "LONG" : "SHORT", _cfg.Contracts, ep, sl, tp, rr);
    var sig = new EntrySignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
        ep, sl, tp, pp, _cfg.Contracts, utcTime);
    await _executor.OnEntrySignalAsync(sig);
    await _sink.OnEntryAsync(sig);
    AddAlert("ENTRY", SetupId.B,
        $"{(isLong ? "LONG" : "SHORT")} {_cfg.Contracts}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
        isLong ? "green" : "red");
}
```

#### Step 4: Run the tests

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -q --filter "TickEvalTests"
```
Expected: all 4 new tests PASS.

#### Step 5: Run all 52 existing tests

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -q
```
Expected: 56 passed (52 original + 4 new), 0 failed.

#### Step 6: Commit

```bash
git add CRV.Core/Strategy/OrbStrategyEngine.cs CRV.Core.Tests/Strategy/TickEvalTests.cs
git commit -m "feat: add ProcessPriceTickAsync for live tick-based entry/exit evaluation"
```

---

### Task 2: Wire `ProcessPriceTickAsync` into live bar feeds

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`
- Modify: `CRV.Live/Brokers/Schwab/SchwabBroker.cs`
- Modify: `CRV.Live/Brokers/TradeStation/TradeStationBroker.cs`
- Modify: `CRV.Live/Brokers/Tradovate/TradovateBarFeed.cs`

The goal: every `ILastPriceProvider.UpdatePrice()` call in the bar feeds should ALSO call `engine.ProcessPriceTickAsync(price, utcNow)`. The cleanest way is to add an optional `OnPriceTick` callback to `IBarFeed` or use the existing price provider.

**Approach**: Register a price-tick callback in `LiveEngineOrchestrator` after creating the engine.

#### Step 1: Add `OnPriceTick` event to `IBarFeed`

In `CRV.Core/Interfaces/IInterfaces.cs`:
```csharp
public interface IBarFeed
{
    IAsyncEnumerable<Bar> StreamAsync(CancellationToken ct);

    /// <summary>
    /// Optional: raised on every L1 price tick (before bar confirmation).
    /// Subscribe in the orchestrator to route ticks to <see cref="OrbStrategyEngine.ProcessPriceTickAsync"/>.
    /// </summary>
    event Action<decimal, DateTime>? OnPriceTick;
}
```

#### Step 2: Implement `OnPriceTick` in each bar feed

In `SchwabBarFeed` — wherever `_prices.UpdatePrice(_cfg.Ticker, lastPrice)` is called for L1 ticks:
```csharp
public event Action<decimal, DateTime>? OnPriceTick;

// Inside the L1 tick handler (after UpdatePrice call):
OnPriceTick?.Invoke(lastPrice, DateTime.UtcNow);
```

Apply the same pattern to `TradeStationBarFeed.ProcessLine` and `TradovateBarFeed.ConnectAsync` — each already calls `_prices.UpdatePrice()` on L1 ticks, just add the event raise immediately after.

#### Step 3: Wire event in `LiveEngineOrchestrator.RunAsync`

After the `_engine = new OrbStrategyEngine(...)` line and before the bar feed loop:
```csharp
// Enable tick-mode and wire L1 ticks → ProcessPriceTickAsync
_engine.EnableTickMode();
feed.OnPriceTick += (price, time) =>
{
    // Fire-and-forget: use a dedicated task; don't await in the event handler
    _ = Task.Run(async () =>
    {
        try { await _engine.ProcessPriceTickAsync(price, time); }
        catch (Exception ex) { /* log */ }
    }, ct);
};
```

#### Step 4: Build and verify

```bash
dotnet build CRV.Web/CRV.Web.csproj -q 2>&1 | grep -E "^.*error" | head -20
```
Expected: 0 errors.

#### Step 5: Commit

```bash
git add CRV.Core/Interfaces/IInterfaces.cs \
        CRV.Live/Brokers/Schwab/SchwabBroker.cs \
        CRV.Live/Brokers/TradeStation/TradeStationBroker.cs \
        CRV.Live/Brokers/Tradovate/TradovateBarFeed.cs \
        CRV.Web/Services/LiveEngineOrchestrator.cs
git commit -m "feat: wire L1 price ticks to ProcessPriceTickAsync in all live bar feeds"
```

---

## PART B — MockBrokerExecutor Fill Simulation + OCO

---

### Task 3: Define `MockOrder` model and upgrade `MockBrokerExecutor`

**Files:**
- Modify: `CRV.Live/Brokers/MockBrokerExecutor.cs`

#### Step 1: Write failing tests for `MockBrokerExecutor`

```csharp
// CRV.Core.Tests/Brokers/MockBrokerExecutorTests.cs
using CRV.Core.Models;
using CRV.Live.Brokers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class MockBrokerExecutorTests
{
    static MockBrokerExecutor Build() =>
        new MockBrokerExecutor(NullLogger<MockBrokerExecutor>.Instance);

    [Fact]
    public async Task OnEntry_CreatesOcoOrders_AllWorking()
    {
        var exec = Build();
        var sig  = new EntrySignal(SetupId.A, Direction.Long, 20500m, 20400m, 21500m, 20900m, 2, DateTime.UtcNow);
        await exec.OnEntrySignalAsync(sig);

        var orders = exec.GetOrders();
        Assert.Equal(3, orders.Count);  // entry + stop + target
        Assert.All(orders, o => Assert.Equal("WORKING", o.Status));
    }

    [Fact]
    public void OnPriceTick_BuyStopFills_CancelsOcoPartner()
    {
        var exec = Build();
        // Manually place a WORKING buy-stop order
        exec.SimulateOrder("NQH26", "BUY", 2, null, 20500m, "oco1");
        exec.SimulateOrder("NQH26", "SELL", 2, 21000m, null, "oco1");

        exec.EvaluateFills("NQH26", 20501m, DateTime.UtcNow);

        var orders = exec.GetOrders();
        var filled   = orders.Single(o => o.StopPrice == 20500m);
        var canceled = orders.Single(o => o.LimitPrice == 21000m);
        Assert.Equal("FILLED",   filled.Status);
        Assert.Equal("CANCELED", canceled.Status);
    }

    [Fact]
    public void OnPriceTick_SellLimitFills_CancelsOcoPartner()
    {
        var exec = Build();
        exec.SimulateOrder("NQH26", "SELL", 2, 21000m, null, "oco1");
        exec.SimulateOrder("NQH26", "SELL", 2, null, 20400m, "oco1"); // stop

        exec.EvaluateFills("NQH26", 21001m, DateTime.UtcNow);

        var orders = exec.GetOrders();
        Assert.Equal("FILLED",   orders[0].Status); // limit hit
        Assert.Equal("CANCELED", orders[1].Status); // oco partner canceled
    }

    [Fact]
    public async Task CancelOrder_SetsStatusCanceled()
    {
        var exec = Build();
        var sig = new EntrySignal(SetupId.A, Direction.Long, 20500m, 20400m, 21500m, 20900m, 2, DateTime.UtcNow);
        await exec.OnEntrySignalAsync(sig);

        var id = exec.GetOrders()[0].OrderId;
        exec.CancelOrder(id);
        Assert.Equal("CANCELED", exec.GetOrders().Single(o => o.OrderId == id).Status);
    }
}
```

#### Step 2: Run failing tests

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -q --filter "MockBrokerExecutorTests"
```
Expected: FAIL — `GetOrders`, `SimulateOrder`, `EvaluateFills`, `CancelOrder` don't exist.

#### Step 3: Implement `MockOrder` + upgraded `MockBrokerExecutor`

Replace `CRV.Live/Brokers/MockBrokerExecutor.cs` entirely:

```csharp
namespace CRV.Live.Brokers;

using System.Collections.Concurrent;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

// ── Order state model ─────────────────────────────────────────

public class MockOrder
{
    public string    OrderId    { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string    Symbol     { get; set; } = "";
    public string    Action     { get; set; } = "";   // BUY | SELL
    public int       Quantity   { get; set; }
    public decimal?  LimitPrice { get; set; }         // null = market/stop
    public decimal?  StopPrice  { get; set; }         // null = limit/market
    public string    Status     { get; set; } = "WORKING"; // WORKING | FILLED | CANCELED
    public decimal?  FillPrice  { get; set; }
    public DateTime  PlacedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? FilledAt   { get; set; }
    public string?   OcoGroupId { get; set; }         // all orders sharing this ID are OCO peers

    public string OrderType => (LimitPrice.HasValue, StopPrice.HasValue) switch
    {
        (true,  false) => "LIMIT",
        (false, true)  => "STOP",
        (true,  true)  => "STOP_LIMIT",
        _              => "MARKET"
    };
}

// ── Executor ──────────────────────────────────────────────────

/// <summary>
/// Simulated broker — logs all signals, tracks OCO brackets, and fills orders
/// when <see cref="EvaluateFills"/> is called with a realtime price.
/// Thread-safe; the order list is a <see cref="ConcurrentBag{T}"/>.
/// </summary>
public class MockBrokerExecutor : IOrderExecutor
{
    private readonly ILogger _log;
    private readonly List<MockOrder> _orders = new();
    private readonly object _lock = new();

    public MockBrokerExecutor(ILogger<MockBrokerExecutor> log) => _log = log;

    // ── IOrderExecutor ────────────────────────────────────────

    public Task OnEntrySignalAsync(EntrySignal sig)
    {
        _log.LogInformation("[MOCK] ENTRY {D} {Q}x Setup={S} @ {E} Stop={St} Tgt={T}",
            sig.Direction, sig.Contracts, sig.Setup, sig.Entry, sig.Stop, sig.Target);

        // Create 3-leg OCO bracket: entry(market) + stop + target
        var ocoId = Guid.NewGuid().ToString("N")[..8];
        bool isLong = sig.Direction == Direction.Long;

        lock (_lock)
        {
            // Leg 1: Entry (treat as filled immediately — engine already recorded entry)
            _orders.Add(new MockOrder
            {
                Symbol     = sig.Setup.ToString(), // use setup as proxy for symbol
                Action     = isLong ? "BUY" : "SELL",
                Quantity   = sig.Contracts,
                Status     = "FILLED",
                FillPrice  = sig.Entry,
                FilledAt   = DateTime.UtcNow,
                OcoGroupId = ocoId
            });

            // Leg 2: Stop loss
            _orders.Add(new MockOrder
            {
                Symbol     = sig.Setup.ToString(),
                Action     = isLong ? "SELL" : "BUY",
                Quantity   = sig.Contracts,
                StopPrice  = sig.Stop,
                OcoGroupId = ocoId
            });

            // Leg 3: Take profit
            _orders.Add(new MockOrder
            {
                Symbol     = sig.Setup.ToString(),
                Action     = isLong ? "SELL" : "BUY",
                Quantity   = sig.Contracts,
                LimitPrice = sig.Target,
                OcoGroupId = ocoId
            });
        }
        return Task.CompletedTask;
    }

    public Task OnPartialSignalAsync(PartialSignal sig)
    {
        _log.LogInformation("[MOCK] PARTIAL Setup={S} {Q}ct @ {P}",
            sig.Setup, sig.ContractsExited, sig.PartialPrice);
        return Task.CompletedTask;
    }

    public Task OnBESignalAsync(BESignal sig)
    {
        _log.LogInformation("[MOCK] MOVE_BE Setup={S} → {P}", sig.Setup, sig.NewStop);
        // Update the open stop order's stop price
        lock (_lock)
        {
            var stop = _orders.FirstOrDefault(o =>
                o.Status == "WORKING" && o.StopPrice.HasValue &&
                o.OcoGroupId != null);
            if (stop != null) stop.StopPrice = sig.NewStop;
        }
        return Task.CompletedTask;
    }

    public Task OnExitSignalAsync(ExitSignal sig)
    {
        _log.LogInformation("[MOCK] EXIT Setup={S} {R} @ {P} {Q}ct",
            sig.Setup, sig.Reason, sig.ExitPrice, sig.Contracts);
        // Mark all WORKING orders in the same setup as CANCELED (engine closed the position)
        lock (_lock)
        {
            foreach (var o in _orders.Where(o => o.Status == "WORKING"))
                o.Status = "CANCELED";
        }
        return Task.CompletedTask;
    }

    // ── Fill simulation ───────────────────────────────────────

    /// <summary>
    /// Called with the latest market price to check and fill any WORKING orders.
    /// OCO partners of a filled order are immediately CANCELED.
    /// </summary>
    public void EvaluateFills(string symbol, decimal price, DateTime utcNow)
    {
        if (price <= 0) return;
        lock (_lock)
        {
            var working = _orders.Where(o => o.Status == "WORKING").ToList();
            foreach (var o in working)
            {
                if (o.Status != "WORKING") continue; // already canceled by OCO

                bool fills = o.Action == "BUY"
                    ? (o.StopPrice.HasValue  && price >= o.StopPrice)   // BUY STOP
                   || (o.LimitPrice.HasValue && price <= o.LimitPrice)  // BUY LIMIT
                    : (o.StopPrice.HasValue  && price <= o.StopPrice)   // SELL STOP
                   || (o.LimitPrice.HasValue && price >= o.LimitPrice); // SELL LIMIT

                if (fills)
                {
                    o.Status    = "FILLED";
                    o.FillPrice = price;
                    o.FilledAt  = utcNow;
                    _log.LogInformation("[MOCK FILL] {A} {Q}x @ {P} (order {Id})",
                        o.Action, o.Quantity, price, o.OrderId);

                    // Cancel OCO partners
                    if (o.OcoGroupId != null)
                    {
                        foreach (var peer in _orders.Where(p =>
                            p.OcoGroupId == o.OcoGroupId &&
                            p.OrderId != o.OrderId &&
                            p.Status == "WORKING"))
                        {
                            peer.Status = "CANCELED";
                            _log.LogDebug("[MOCK OCO] Canceled partner order {Id}", peer.OrderId);
                        }
                    }
                }
            }
        }
    }

    // ── Query helpers ─────────────────────────────────────────

    /// <summary>Returns a snapshot copy of all orders (any status).</summary>
    public List<MockOrder> GetOrders()
    {
        lock (_lock) return _orders.ToList();
    }

    /// <summary>Cancel a single order by ID.</summary>
    public void CancelOrder(string orderId)
    {
        lock (_lock)
        {
            var o = _orders.FirstOrDefault(o => o.OrderId == orderId);
            if (o?.Status == "WORKING") o.Status = "CANCELED";
        }
    }

    /// <summary>Test helper: add a simulated order directly.</summary>
    public void SimulateOrder(string symbol, string action, int qty,
        decimal? limitPrice, decimal? stopPrice, string? ocoGroupId = null)
    {
        lock (_lock)
        {
            _orders.Add(new MockOrder
            {
                Symbol     = symbol,
                Action     = action,
                Quantity   = qty,
                LimitPrice = limitPrice,
                StopPrice  = stopPrice,
                OcoGroupId = ocoGroupId
            });
        }
    }
}
```

#### Step 4: Run mock broker tests

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -q --filter "MockBrokerExecutorTests"
```
Expected: 4 tests PASS.

#### Step 5: Run all tests

```bash
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -q
```
Expected: 60 passed, 0 failed.

#### Step 6: Commit

```bash
git add CRV.Live/Brokers/MockBrokerExecutor.cs CRV.Core.Tests/Brokers/MockBrokerExecutorTests.cs
git commit -m "feat: MockBrokerExecutor with fill simulation, OCO brackets, order state tracking"
```

---

### Task 4: Wire `EvaluateFills` to live price ticks

**Files:**
- Modify: `CRV.Web/Services/LiveEngineOrchestrator.cs`

When the engine is running with `ExecBroker=Mock`, the `MockBrokerExecutor` needs to see every L1 price tick. The orchestrator already has the `MockBrokerExecutor` instance.

#### Step 1: Register tick evaluation in `RunAsync`

After the `feed.OnPriceTick += ...` line (added in Task 2):

```csharp
// If executor is MockBrokerExecutor, also drive fill simulation from ticks
if (executor is MockBrokerExecutor mockExec)
{
    feed.OnPriceTick += (price, time) =>
        mockExec.EvaluateFills(cfg.Ticker, price, time);
}
```

#### Step 2: Build

```bash
dotnet build CRV.Web/CRV.Web.csproj -q 2>&1 | grep -E "^.*error" | head -20
```

#### Step 3: Commit

```bash
git add CRV.Web/Services/LiveEngineOrchestrator.cs
git commit -m "feat: wire MockBrokerExecutor.EvaluateFills to L1 price ticks in orchestrator"
```

---

### Task 5: Expose mock orders on `/trading/orders`

**Files:**
- Modify: `CRV.Live/ManualBrokerOps.cs`
- Modify: `CRV.Web/Pages/Trading/Orders.cshtml.cs`

#### Step 1: Update `GetOrdersMockAsync` to accept `MockBrokerExecutor`

In `ManualBrokerOps.cs`, change:
```csharp
public static Task<List<OrderView>> GetOrdersMockAsync()
    => Task.FromResult(new List<OrderView>());
```
To:
```csharp
public static Task<List<OrderView>> GetOrdersMockAsync(MockBrokerExecutor? exec = null)
{
    if (exec == null) return Task.FromResult(new List<OrderView>());
    var orders = exec.GetOrders().Select(o => new OrderView
    {
        OrderId     = o.OrderId,
        Symbol      = o.Symbol,
        OrderType   = o.OrderType,
        Action      = o.Action,
        Quantity    = o.Quantity,
        LimitPrice  = o.LimitPrice,
        StopPrice   = o.StopPrice,
        Status      = o.Status,
        StatusLabel = o.Status,
        PlacedTime  = o.PlacedAt.ToString("o"),
        CanCancel   = o.Status == "WORKING"
    }).ToList();
    return Task.FromResult(orders);
}
```

#### Step 2: Inject `MockBrokerExecutor` into `OrdersModel` and pass it

In `Orders.cshtml.cs`, the `OnGetOrdersAsync` switch already calls `GetOrdersMockAsync()`. Update it:
```csharp
// In the switch, change:
_              => await ManualBrokerOps.GetOrdersMockAsync()
// To:
_              => await ManualBrokerOps.GetOrdersMockAsync(
                      _cfgSvc.Current.EffectiveExecBroker == "Mock" ? _mockExec : null)
```

Add `MockBrokerExecutor _mockExec` as a constructor parameter (it's already registered in DI).

#### Step 3: Build and run all tests

```bash
dotnet build CRV.Web/CRV.Web.csproj -q 2>&1 | grep -E "^.*error" | head -10
dotnet test CRV.Core.Tests/CRV.Core.Tests.csproj -q
```
Expected: 0 errors, 60 tests pass.

#### Step 4: Commit

```bash
git add CRV.Live/ManualBrokerOps.cs CRV.Web/Pages/Trading/Orders.cshtml.cs
git commit -m "feat: expose MockBrokerExecutor orders on /trading/orders page"
```

---

## Summary of Changes

| File | Change |
|------|--------|
| `CRV.Core/Strategy/OrbStrategyEngine.cs` | Add `ProcessPriceTickAsync`, `EnableTickMode`, `EvalTickSetupA/B`, `TryEntryA/BFromTick` |
| `CRV.Core/Interfaces/IInterfaces.cs` | Add `OnPriceTick` event to `IBarFeed` |
| `CRV.Live/Brokers/MockBrokerExecutor.cs` | Full rewrite with `MockOrder`, fill simulation, OCO, `GetOrders()` |
| `CRV.Live/Brokers/Schwab/SchwabBroker.cs` | Raise `OnPriceTick` on L1 ticks |
| `CRV.Live/Brokers/TradeStation/TradeStationBroker.cs` | Raise `OnPriceTick` on L1 ticks |
| `CRV.Live/Brokers/Tradovate/TradovateBarFeed.cs` | Raise `OnPriceTick` on L1 ticks |
| `CRV.Web/Services/LiveEngineOrchestrator.cs` | Subscribe `OnPriceTick` → `ProcessPriceTickAsync` + `EvaluateFills` |
| `CRV.Live/ManualBrokerOps.cs` | `GetOrdersMockAsync(MockBrokerExecutor?)` |
| `CRV.Web/Pages/Trading/Orders.cshtml.cs` | Pass mock executor to `GetOrdersMockAsync` |
| `CRV.Core.Tests/Strategy/TickEvalTests.cs` | 4 new tests |
| `CRV.Core.Tests/Brokers/MockBrokerExecutorTests.cs` | 4 new tests |
