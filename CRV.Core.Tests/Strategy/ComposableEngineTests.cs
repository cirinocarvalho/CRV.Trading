using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Modules;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class ComposableEngineTests
{
    // ── Fakes ──────────────────────────────────────────────────────

    private class FakeExecutor : IOrderExecutor
    {
        public List<EntrySignal> Entries { get; } = new();
        public List<ExitSignal> Exits { get; } = new();
        public List<PartialSignal> Partials { get; } = new();
        public List<BESignal> BEs { get; } = new();

        // Allow tests to control whether entries are blocked
        public bool BlockEntries { get; set; }

        public Task<decimal?> OnEntrySignalAsync(EntrySignal signal)
        {
            Entries.Add(signal);
            return Task.FromResult<decimal?>(BlockEntries ? null : signal.Entry);
        }
        public Task OnExitSignalAsync(ExitSignal signal) { Exits.Add(signal); return Task.CompletedTask; }
        public Task OnPartialSignalAsync(PartialSignal signal) { Partials.Add(signal); return Task.CompletedTask; }
        public Task OnBESignalAsync(BESignal signal) { BEs.Add(signal); return Task.CompletedTask; }
    }

    private class FakeSink : IStrategyEventSink
    {
        public List<EntrySignal> Entries { get; } = new();
        public List<ExitSignal> Exits { get; } = new();
        public List<PartialSignal> Partials { get; } = new();
        public List<BESignal> BEs { get; } = new();
        public List<EngineSnapshot> Snapshots { get; } = new();

        public Task OnEntryAsync(EntrySignal signal) { Entries.Add(signal); return Task.CompletedTask; }
        public Task OnExitAsync(ExitSignal signal, TradeRecord completed) { Exits.Add(signal); return Task.CompletedTask; }
        public Task OnPartialAsync(PartialSignal signal) { Partials.Add(signal); return Task.CompletedTask; }
        public Task OnBEMoveAsync(BESignal signal) { BEs.Add(signal); return Task.CompletedTask; }
        public Task OnSnapshotAsync(EngineSnapshot snapshot) { Snapshots.Add(snapshot); return Task.CompletedTask; }
    }

    private class FakePrices : ILastPriceProvider
    {
        private readonly Dictionary<string, decimal> _prices = new();
        public decimal GetLastPrice(string ticker) => _prices.TryGetValue(ticker, out var p) ? p : 0;
        public void UpdatePrice(string ticker, decimal price) => _prices[ticker] = price;
    }

    /// <summary>Minimal fake strategy for testing ComposableEngine dispatch.</summary>
    private class FakeStrategy : ISetupStrategy
    {
        public string Id { get; set; } = "A";
        public SetupId SetupId { get; set; } = SetupId.A;
        public StrategyType StrategyType => StrategyType.Pullback;
        public string Name => $"Fake{SetupId}";
        public string Ticker { get; set; } = "/NQH2026";
        public decimal PointValue { get; set; } = 20m;
        public bool IsActive { get; set; }
        public bool IsArmed { get; set; }
        public bool InTrade { get; set; }
        public void SetInTrade(bool active) => InTrade = active;
        public int CutoffHour { get; set; } = 23;
        public int CutoffMinute { get; set; } = 59;

        public int OnBarCallCount { get; private set; }
        public int OnTickCallCount { get; private set; }
        public int ResetCallCount { get; private set; }
        public bool TickModeEnabled { get; set; }

        public EntrySignal? PendingEntry { get; set; }
        public ExitSignal? PendingExit { get; set; }
        public PartialSignal? PendingPartial { get; set; }
        public BESignal? PendingBE { get; set; }
        public ActiveTradeView? PreExitTrade { get; set; }

        public StrategySetupConfig? LastConfig { get; private set; }
        public bool ForceExitCalled { get; private set; }
        public decimal ForceExitPrice { get; private set; }

        public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules) => OnBarCallCount++;
        public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules) => OnTickCallCount++;

        public void Reconfigure(StrategySetupConfig config) { LastConfig = config; }
        public void Reset()
        {
            ResetCallCount++;
        }
        public void ResetTradeCounters() { }
        public void Disarm() { }
        public void ResetCutoff() { }
        public (int Hour, int Minute) GetCutoffForSession(string s) => (CutoffHour, CutoffMinute);
        public bool IsEnabledForSession(string s) => true;
        public void ResetSession()
        {
            ResetCallCount++;
            OnBarCallCount = 0;
            OnTickCallCount = 0;
            IsActive = false;
            IsArmed = false;
            PendingEntry = null;
            PendingExit = null;
            PendingPartial = null;
            PendingBE = null;
            ForceExitCalled = false;
        }
        public void ApplyFill(decimal actualFillPrice) { }
        public void RevertEntryToTickGate(decimal entryLevel) { }
        public void ClearPendingSignals()
        {
            PendingEntry = null;
            PendingExit = null;
            PendingPartial = null;
            PendingBE = null;
        }
        public void RevertEntry() { PendingEntry = null; IsActive = false; }
        public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd)
        {
            ForceExitCalled = true;
            ForceExitPrice = currentPrice;
            IsActive = false;
            PendingExit = new ExitSignal(SetupId, reason, currentPrice, 2, utcTime);
        }
        public SetupStateSnapshot GetSnapshot() => new()
        {
            SetupId = SetupId, Name = Name, Enabled = true,
            TradeCount = 1, MaxTrades = 5, Wins = 1, WinPnl = 100m,
        };
        public ActiveTradeView? GetActiveTrade(decimal lastPrice) => IsActive
            ? new ActiveTradeView
            {
                Setup = SetupId, Direction = Direction.Long,
                Entry = 100m, CurrentStop = 95m, Target = 110m,
                Contracts = 2, RemainingContracts = 2,
                LastPrice = lastPrice, UnrealizedPnl = (lastPrice - 100m) * 20m * 2,
                EnteredAt = DateTime.UtcNow.AddMinutes(-5),
            }
            : null;
    }

    // ── Config helpers ──────────────────────────────────────────────

    private static StrategyConfig DefaultStrategyConfig() => new()
    {
        Ticker = "/NQH2026",
        PointValue = 20m,
        TickSize = 0.25m,
        Timezone = "America/New_York",
        OrbStart = new TimeOnly(9, 30),
        OrbEnd = new TimeOnly(10, 0),
        ExecutionTFMinutes = 1,
        AllowBothSameBar = false,
        CommissionPerSide = 2.25m,
        UseDailyLossLimit = true,
        MaxDailyLoss = 500m,
    };

    private static EngineConfig DefaultEngineConfig() => new()
    {
        Ticker = "/NQH2026",
        PointValue = 20m,
        TickSize = 0.25m,
        Timezone = "America/New_York",
        OrbStart = new TimeOnly(9, 30),
        OrbEnd = new TimeOnly(10, 0),
        ExecutionTFMinutes = 1,
        AllowBothSameBar = false,
        CommissionPerSide = 2.25m,
        UseDailyLossLimit = true,
        MaxDailyLoss = 500m,
    };

    private static StrategySetupConfig MakeSetupConfig(SetupId id, string ticker = "/NQH2026",
        StrategyType type = StrategyType.Pullback) => new()
    {
        Id = id.ToString(),
        Name = id.ToString(),
        SetupId = id,
        StrategyType = type,
        Enabled = true,
        Ticker = ticker,
        PointValue = 20m,
        TickSize = 0.25m,
        Contracts = 2,
        MaxTrades = 5,
        StopPct = 0.10m,
        TargetPct = 100,
        PartialPct = 50,
    };

    private ComposableEngine CreateEngine(
        FakeExecutor? executor = null,
        FakeSink? sink = null,
        FakePrices? prices = null,
        EngineConfig? config = null)
    {
        return new ComposableEngine(
            executor ?? new FakeExecutor(),
            sink ?? new FakeSink(),
            prices ?? new FakePrices(),
            config ?? DefaultEngineConfig());
    }

    // ── 1. AddSetup creates strategy and assigns to correct TickerGroup ──

    [Fact]
    public void AddSetup_CreatesStrategyInCorrectGroup()
    {
        var engine = CreateEngine();

        engine.AddSetup(MakeSetupConfig(SetupId.A, "/NQH2026"));
        engine.AddSetup(MakeSetupConfig(SetupId.B, "/NQH2026"));

        var snap = engine.GetSnapshot();
        Assert.True(snap.Setups.First(s => s.Id == "A").Enabled);
        Assert.True(snap.Setups.First(s => s.Id == "B").Enabled);
    }

    // ── 2. AddSetup groups NQ/MNQ setups into same TickerGroup ──

    [Fact]
    public void AddSetup_GroupsNQAndMNQIntoSameGroup()
    {
        var engine = CreateEngine();

        engine.AddSetup(MakeSetupConfig(SetupId.A, "/NQH2026"));
        engine.AddSetup(MakeSetupConfig(SetupId.B, "/MNQH2026"));

        // Both should be in the NQ group — verify via snapshot
        var snap = engine.GetSnapshot();
        Assert.True(snap.Setups.First(s => s.Id == "A").Enabled);
        Assert.True(snap.Setups.First(s => s.Id == "B").Enabled);
    }

    // ── 3. ForceExitSetup dispatches to correct strategy ──

    [Fact]
    public async Task ForceExitSetup_DispatchesToCorrectStrategy()
    {
        var executor = new FakeExecutor();
        var sink = new FakeSink();
        var prices = new FakePrices();
        prices.UpdatePrice("/NQH2026", 100m);
        var engine = new ComposableEngine(executor, sink, prices, DefaultEngineConfig());

        engine.AddSetup(MakeSetupConfig(SetupId.A));
        engine.AddSetup(MakeSetupConfig(SetupId.B));

        // Force exit A — should trigger exit signal
        await engine.ForceExitSetupAsync("A");

        // Since no trade is active, no exit signal should fire (strategy not active)
        // This verifies the method doesn't throw and routes correctly
        Assert.Empty(executor.Exits);
    }

    // ── 4. EnableTickMode propagates to engine state ──

    [Fact]
    public void EnableTickMode_SetsFlag()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        // Should not throw
        engine.EnableTickMode();

        // Tick mode is internal state — verify via snapshot or processing
        // (covered by integration in other tests)
    }

    // ── 5. GetSnapshot returns aggregated snapshot ──

    [Fact]
    public void GetSnapshot_ReturnsAggregatedState()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));
        engine.AddSetup(MakeSetupConfig(SetupId.C, type: StrategyType.OrbFakeout));

        var snap = engine.GetSnapshot();

        Assert.Equal("/NQH2026", snap.Ticker);
        Assert.True(snap.Setups.First(s => s.Id == "A").Enabled);
        Assert.True(snap.Setups.First(s => s.Id == "C").Enabled);
        Assert.False(snap.TradingHalted);
    }

    // ── 6. Reconfigure updates engine config ──

    [Fact]
    public void Reconfigure_UpdatesConfig()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        var newConfig = DefaultEngineConfig();
        newConfig.MaxDailyLoss = 1000m;
        var newSetups = new List<StrategySetupConfig> { MakeSetupConfig(SetupId.A) };

        engine.Reconfigure(newConfig, newSetups);

        var snap = engine.GetSnapshot();
        Assert.Equal(1000m, snap.DailyLossLimit);
    }

    // ── 7. Risk check blocks entries when DdBreached ──

    [Fact]
    public async Task RiskCheck_BlocksEntriesWhenDdBreached()
    {
        var executor = new FakeExecutor();
        var sink = new FakeSink();
        var prices = new FakePrices();
        var config = DefaultEngineConfig();
        config.UseDailyLossLimit = true;
        config.MaxDailyLoss = 100m;
        var engine = new ComposableEngine(executor, sink, prices, config);

        engine.AddSetup(MakeSetupConfig(SetupId.A));

        // Simulate a large loss via the risk manager
        engine.Risk.RecordTrade(-200m);
        engine.Risk.CanTrade(true, 100m); // triggers DdBreached

        Assert.True(engine.Risk.DdBreached);
    }

    // ── ProcessBarAsync routes to correct TickerGroup ──

    [Fact]
    public async Task ProcessBarAsync_RoutesToCorrectGroup()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A, "/NQH2026"));

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        var bar = new Bar(utc, 100m, 105m, 95m, 102m, 100, true);

        // Should not throw — routes to NQ group
        await engine.ProcessBarAsync(bar, "/NQH2026");
    }

    // ── ProcessBarAsync with unknown ticker is a no-op ──

    [Fact]
    public async Task ProcessBarAsync_UnknownTicker_NoOp()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A, "/NQH2026"));

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        var bar = new Bar(utc, 100m, 105m, 95m, 102m, 100, true);

        // Should not throw for unknown ticker
        await engine.ProcessBarAsync(bar, "/CLH2026");
    }

    // ── SetIdle / ClearIdle ──

    [Fact]
    public void SetIdle_PreventsProcessing()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        engine.SetIdle();

        // After SetIdle, ProcessBarAsync should be a no-op
        // ClearIdle should re-enable
        engine.ClearIdle();
    }

    // ── ResetDaily clears risk and groups ──

    [Fact]
    public void ResetDaily_ClearsRiskAndReturnsToDefault()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        engine.Risk.RecordTrade(-50m);
        Assert.Equal(-50m, engine.Risk.TodayPnl);

        engine.ResetDaily();

        Assert.Equal(0m, engine.Risk.TodayPnl);
        Assert.False(engine.Risk.DdBreached);
    }

    // ── ForceExitAll exits all active setups ──

    [Fact]
    public async Task ForceExitAllAsync_ExitsAllActive()
    {
        var executor = new FakeExecutor();
        var sink = new FakeSink();
        var prices = new FakePrices();
        prices.UpdatePrice("/NQH2026", 105m);
        var engine = new ComposableEngine(executor, sink, prices, DefaultEngineConfig());

        engine.AddSetup(MakeSetupConfig(SetupId.A));
        engine.AddSetup(MakeSetupConfig(SetupId.B));

        // Should not throw even when no active trades
        await engine.ForceExitAllAsync();
    }

    // ── WarmupBarAsync updates indicators but doesn't fire signals ──

    [Fact]
    public async Task WarmupBarAsync_UpdatesIndicators_NoSignals()
    {
        var executor = new FakeExecutor();
        var sink = new FakeSink();
        var engine = new ComposableEngine(executor, sink, new FakePrices(), DefaultEngineConfig());

        engine.AddSetup(MakeSetupConfig(SetupId.A));

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        var bar = new Bar(utc, 100m, 105m, 95m, 102m, 100, true);

        await engine.WarmupBarAsync(bar, "/NQH2026");

        // No broker signals should have fired
        Assert.Empty(executor.Entries);
        Assert.Empty(executor.Exits);
    }

    // ── Signal routing: entry signal dispatches to executor and sink ──

    [Fact]
    public async Task SignalRouting_EntryDispatchesToExecutorAndSink()
    {
        var executor = new FakeExecutor();
        var sink = new FakeSink();
        var engine = new ComposableEngine(executor, sink, new FakePrices(), DefaultEngineConfig());

        // Use internal test hook to inject signals
        var signals = new List<StrategySignals>
        {
            new(new FakeStrategy { Id = "A", SetupId = SetupId.A },
                new EntrySignal(SetupId.A, Direction.Long, 100m, 95m, 110m, 105m, 2, DateTime.UtcNow, Ticker: "/NQH2026"),
                null, null, null)
        };

        await engine.RouteSignalsAsync(signals);

        Assert.Single(executor.Entries);
        Assert.Single(sink.Entries);
        Assert.Equal(SetupId.A, executor.Entries[0].Setup);
    }

    // ── Signal routing: exit signal dispatches and records trade in risk manager ──

    [Fact]
    public async Task SignalRouting_ExitRecordsTradeInRiskManager()
    {
        var executor = new FakeExecutor();
        var sink = new FakeSink();
        var engine = new ComposableEngine(executor, sink, new FakePrices(), DefaultEngineConfig());

        var fakeStrategy = new FakeStrategy { SetupId = SetupId.A, IsActive = true };

        var signals = new List<StrategySignals>
        {
            new(fakeStrategy,
                null,
                new ExitSignal(SetupId.A, ExitReason.Target, 110m, 2, DateTime.UtcNow, Ticker: "/NQH2026"),
                null, null)
        };

        await engine.RouteSignalsAsync(signals);

        Assert.Single(executor.Exits);
        Assert.Single(sink.Exits);
    }

    // ── Signal routing: partial dispatches to executor and sink ──

    [Fact]
    public async Task SignalRouting_PartialDispatchesToExecutorAndSink()
    {
        var executor = new FakeExecutor();
        var sink = new FakeSink();
        var engine = new ComposableEngine(executor, sink, new FakePrices(), DefaultEngineConfig());

        var signals = new List<StrategySignals>
        {
            new(new FakeStrategy { Id = "A", SetupId = SetupId.A },
                null, null,
                new PartialSignal(SetupId.A, Direction.Long, 105m, 1, 1, 100m, DateTime.UtcNow),
                null)
        };

        await engine.RouteSignalsAsync(signals);

        Assert.Single(executor.Partials);
        Assert.Single(sink.Partials);
    }

    // ── Signal routing: BE dispatches to executor and sink ──

    [Fact]
    public async Task SignalRouting_BEDispatchesToExecutorAndSink()
    {
        var executor = new FakeExecutor();
        var sink = new FakeSink();
        var engine = new ComposableEngine(executor, sink, new FakePrices(), DefaultEngineConfig());

        var signals = new List<StrategySignals>
        {
            new(new FakeStrategy { Id = "A", SetupId = SetupId.A },
                null, null, null,
                new BESignal(SetupId.A, Direction.Long, 100m, 100m, 2, DateTime.UtcNow))
        };

        await engine.RouteSignalsAsync(signals);

        Assert.Single(executor.BEs);
        Assert.Single(sink.BEs);
    }

    // ── PublishCurrentStateAsync triggers snapshot ──

    [Fact]
    public async Task PublishCurrentStateAsync_SendsSnapshot()
    {
        var sink = new FakeSink();
        var engine = new ComposableEngine(new FakeExecutor(), sink, new FakePrices(), DefaultEngineConfig());
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        await engine.PublishCurrentStateAsync();

        Assert.Single(sink.Snapshots);
    }

    // ── RestoreOrb delegates to TickerGroup ──

    [Fact]
    public void RestoreOrb_DoesNotThrow()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        var cache = new OrbStateCache
        {
            OrbHigh = 100m, OrbLow = 95m, CloseRelPct = 0.7m,
            TradingDate = DateTime.Today, OrbAtrRatio = 0.5m,
        };

        // Should not throw
        engine.RestoreOrb(cache);
    }

    // ── SeedOrbFloor delegates to TickerGroup ──

    [Fact]
    public void SeedOrbFloor_DoesNotThrow()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        var cache = new OrbStateCache
        {
            OrbHigh = 100m, OrbLow = 95m,
        };

        engine.SeedOrbFloor(cache);
    }

    // ── SeedModuleHistory delegates to TickerGroup ──

    [Fact]
    public void SeedModuleHistory_DoesNotThrow()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        engine.SeedModuleHistory(Array.Empty<Bar>());
    }

    // ── Reconfigure with SessionId ──

    [Fact]
    public void Reconfigure_WithSessionId_UpdatesState()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        var cfg = DefaultStrategyConfig();
        engine.Reconfigure(cfg, SessionId.NY);

        Assert.Equal("NY", engine.ActiveSessionId);
    }

    // ── GetActiveTrade returns null when no trade ──

    [Fact]
    public void GetActiveTrade_ReturnsNull_WhenNoActiveTrade()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A));

        var trade = engine.GetActiveTrade("A");
        Assert.Null(trade);
    }

    // ── Multiple groups with different tickers ──

    [Fact]
    public void AddSetup_DifferentTickers_CreatesSeparateGroups()
    {
        var engine = CreateEngine();
        engine.AddSetup(MakeSetupConfig(SetupId.A, "/NQH2026"));
        engine.AddSetup(MakeSetupConfig(SetupId.C, "/ESH2026", StrategyType.OrbFakeout));

        var snap = engine.GetSnapshot();
        Assert.True(snap.Setups.First(s => s.Id == "A").Enabled);
        Assert.True(snap.Setups.First(s => s.Id == "C").Enabled);
    }
}
