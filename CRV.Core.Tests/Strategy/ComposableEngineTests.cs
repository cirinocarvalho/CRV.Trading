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

        // Allow tests to control whether entries are blocked
        public bool BlockEntries { get; set; }

        public Task<decimal?> OnEntrySignalAsync(EntrySignal signal)
        {
            Entries.Add(signal);
            return Task.FromResult<decimal?>(BlockEntries ? null : signal.Entry);
        }
    }

    private class FakeSink : IStrategyEventSink
    {
        public List<EntrySignal> Entries { get; } = new();
        public List<TradeRecord> Exits { get; } = new();
        public List<EngineSnapshot> Snapshots { get; } = new();
        public List<SizeRefusal> Refusals { get; } = new();

        public Task OnEntryAsync(EntrySignal signal) { Entries.Add(signal); return Task.CompletedTask; }
        public Task OnExitAsync(TradeRecord completed) { Exits.Add(completed); return Task.CompletedTask; }
        public Task OnSnapshotAsync(EngineSnapshot snapshot) { Snapshots.Add(snapshot); return Task.CompletedTask; }
        public Task OnSizeRefusedAsync(SizeRefusal refusal) { Refusals.Add(refusal); return Task.CompletedTask; }
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
        public TimeOnly OrbStart { get; set; } = new(9, 30);
        public TimeOnly OrbEnd   { get; set; } = new(10, 0);
        public bool UseEmaFilter => false;
        public bool BypassChopFilter => false;
        public bool CloseAtRthClose { get; set; } = true;
        public bool IsActive { get; set; }
        public bool IsArmed { get; set; }
        public bool InTrade { get; set; }
        public void SetInTrade(bool active) => InTrade = active;
        public void SeedTradeCount(int l, int s) { }
        public int CutoffHour { get; set; } = 23;
        public int CutoffMinute { get; set; } = 59;

        public int OnBarCallCount { get; private set; }
        public int OnTickCallCount { get; private set; }
        public int ResetCallCount { get; private set; }
        public bool TickModeEnabled { get; set; }

        public EntrySignal? PendingEntry { get; set; }

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
            ForceExitCalled = false;
        }
        public void ClearPendingSignals()
        {
            PendingEntry = null;
        }
        public void RevertEntry() { PendingEntry = null; IsActive = false; }
        public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd)
        {
            ForceExitCalled = true;
            ForceExitPrice = currentPrice;
            IsActive = false;
        }
        public SetupStateSnapshot GetSnapshot() => new()
        {
            SetupId = SetupId, Name = Name, Enabled = true,
            TradeCount = 1, MaxTrades = 5, Wins = 1, WinPnl = 100m,
        };
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

    private static BasketEntry BasketEntryFor(string id, StrategyType type, string ticker, int? barMinutes = null) => new()
    {
        Id = id, Label = id, Enabled = true, StrategyType = type, Ticker = ticker,
        PointValue = 2m, TickSize = 0.25m, ExecutionTFMinutes = barMinutes,
        Config = new StrategySetupConfig { Contracts = 1, MaxContracts = 1, MaxTrades = 3, StopPct = 0.10m, TargetPct = 100, PartialPct = 50 },
    };

    // 2026-09-15 is a Tuesday in EDT, so ET = UTC-4.
    private static Bar EtBar(int hour, int minute, decimal high, decimal low) =>
        new(new DateTime(2026, 9, 15, hour + 4, minute, 0, DateTimeKind.Utc), low, high, low, high, 100);

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

    // ── Reconfigure keeps the engine-level settings it was started with ──

    [Fact]
    public void Reconfigure_And_ApplyRuntimeSettings_KeepPortfolioRiskCap()
    {
        // The session-change and save paths used to rebuild the engine config without
        // MaxPortfolioRisk, which turned the cap off (0) until the next restart.
        var engine = CreateEngine();
        var cfg = new StrategyConfig { MaxPortfolioRisk = 750m, MaxDailyLoss = 600m, SessionStartHour = 17 };

        engine.Reconfigure(cfg, SessionId.NY);
        Assert.Equal(750m, engine.CurrentConfig.MaxPortfolioRisk);
        Assert.Equal(17, engine.CurrentConfig.SessionStartHour);

        cfg.MaxPortfolioRisk = 900m;
        engine.ApplyRuntimeSettings(cfg);
        Assert.Equal(900m, engine.CurrentConfig.MaxPortfolioRisk);
        Assert.Equal(600m, engine.CurrentConfig.MaxDailyLoss);
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
        Assert.Empty(sink.Exits);
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
                new EntrySignal(SetupId.A, Direction.Long, 100m, 95m, 110m, 105m, 2, DateTime.UtcNow, Ticker: "/NQH2026"))
        };

        await engine.RouteSignalsAsync(signals);

        Assert.Single(executor.Entries);
        Assert.Single(sink.Entries);
        Assert.Equal(SetupId.A, executor.Entries[0].Setup);
    }

    // ── Signal routing: exit and partial/BE tests removed — ──
    // ExitSignal, PartialSignal, BESignal records deleted.
    // RouteSignalsAsync will be updated in Task 6 to use new signal model.

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

    // ── A size refusal is routed like the portfolio ceiling: sink + RISK alert, no order ──

    [Fact]
    public async Task SizeRefusal_ReachesSinkAndAlertsFeed_PlacesNothing()
    {
        var executor = new FakeExecutor();
        var sink     = new FakeSink();
        var engine   = CreateEngine(executor, sink);
        var strategy = new FakeStrategy { Id = "retest-mnq", Ticker = "MNQM26" };

        var refusal = new SizeRefusal(
            Time: new DateTime(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc),
            SetupLabel: "retest-mnq", Ticker: "MNQM26",
            StopDistance: 120m, RiskPerContract: 240m, Budget: 210m);

        await engine.RouteSignalsAsync(new List<StrategySignals> { new(strategy, null, refusal) });

        Assert.Empty(executor.Entries);
        Assert.Empty(sink.Entries);
        Assert.Equal(new[] { refusal }, sink.Refusals);

        var alert = Assert.Single(engine.GetSnapshot().RecentAlerts, a => a.Type == "RISK");
        Assert.Equal("retest-mnq", alert.SetupLabel);
        Assert.Equal("MNQM26", alert.Ticker);
        Assert.Equal(refusal.Describe(), alert.Message);
    }

    // ── A group runs on its root's bar size ──

    [Fact]
    public async Task GroupOrb_UsesTheSetupsBarSize_NotTheEngines()
    {
        var engine = CreateEngine();                    // engine-wide bar size: 1 min
        var setup = MakeSetupConfig(SetupId.A, "/MNQZ26");
        setup.ExecutionTFMinutes = 15;
        engine.AddSetup(setup);

        // A 15-minute bar opening at 09:20 runs to 09:35 and overlaps the 09:30–10:00 range.
        // Read as a 1-minute bar it would end at 09:21 and be left out.
        await engine.ProcessBarAsync(EtBar(9, 20, 21100m, 21000m), "/MNQZ26");
        await engine.ProcessBarAsync(EtBar(9, 45, 21050m, 20990m), "/MNQZ26");
        await engine.ProcessBarAsync(EtBar(10, 0, 21045m, 21020m), "/MNQZ26");

        var a = engine.GetSnapshot().Setups.Single(s => s.Id == "A");
        Assert.True(a.OrbFormed);
        Assert.Equal(21100m, a.OrbHigh);
        Assert.Equal(15, engine.Groups["NQ"].BarMinutes);
    }

    [Fact]
    public async Task HotReload_KeepsTheGroupOnItsRootsBarSize()
    {
        var engine = CreateEngine();
        var entry = BasketEntryFor("pullback-mnq", StrategyType.Pullback, "/MNQZ26", barMinutes: 15);
        var cfg = DefaultStrategyConfig();              // engine-wide bar size: 1 min
        cfg.BasketJson = BasketCodec.Serialize(new[] { entry });
        engine.AddSetup(cfg.ToSetupConfigs().Single());

        // A save gives the setup its own 09:30–09:45 range. The hot reload swaps the engine-wide
        // config into the group, and the new range gets a fresh calculator.
        entry.Config.UseCustomOrbWindow = true;
        entry.Config.OrbStart = new TimeOnly(9, 30);
        entry.Config.OrbEnd = new TimeOnly(9, 45);
        cfg.BasketJson = BasketCodec.Serialize(new[] { entry });
        engine.ApplyRuntimeSettings(cfg);
        engine.Reconfigure(cfg.ToEngineConfig(), cfg.ToSetupConfigs());

        await engine.ProcessBarAsync(EtBar(9, 20, 21100m, 21000m), "/MNQZ26");
        await engine.ProcessBarAsync(EtBar(9, 30, 21050m, 20990m), "/MNQZ26");
        await engine.ProcessBarAsync(EtBar(9, 45, 21045m, 21020m), "/MNQZ26");

        Assert.Equal(21100m, engine.GetSnapshot().Setups.Single(s => s.Id == "pullback-mnq").OrbHigh);
        Assert.Equal(15, engine.Groups["NQ"].BarMinutes);
    }

    // ── Engine start: an entry that can't trade disables only itself ──

    private static StrategyConfig BasketWithEntriesThatCantTrade()
    {
        var cfg = DefaultStrategyConfig();
        cfg.BasketJson = BasketCodec.Serialize(new[]
        {
            BasketEntryFor("pullback-mnq", StrategyType.Pullback, "/MNQZ26", 5),
            BasketEntryFor("retest-nq",    StrategyType.Retest,   "/NQZ26",  15),
            BasketEntryFor("unknown-mes",  (StrategyType)9,       "/MESZ26", 5),
            BasketEntryFor("retest-mes",   StrategyType.Retest,   "/MESZ26", 5),
        });
        cfg.EmaBasketJson = BasketCodec.Serialize(new[]
        {
            BasketEntryFor("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26", 5),
        });
        return cfg;
    }

    private static StrategyConfig RetiredOnlyBasket()
    {
        var cfg = DefaultStrategyConfig();
        cfg.BasketJson = BasketCodec.Serialize(new[]
        {
            BasketEntryFor("ema21-mnq", SetupValidation.RetiredEma21, "/MNQZ26", 5),
        });
        return cfg;
    }

    [Fact]
    public void AddSetups_SkipsEntriesThatCantTrade_AndRegistersTheRest()
    {
        var engine = CreateEngine();

        engine.AddSetups(BasketWithEntriesThatCantTrade());

        Assert.Equal(new[] { "pullback-mnq", "retest-mes" },
            engine.GetStrategies().Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));
        var reasons = engine.DisabledSetups.ToDictionary(d => d.Id, d => d.Reason);
        Assert.Equal(3, reasons.Count);
        Assert.Equal("retired EMA21 strategy", reasons["ema21-mnq"]);
        Assert.Equal("unknown strategy type 9", reasons["unknown-mes"]);
        Assert.StartsWith("bar size 15 min differs from the 5 min pullback-mnq uses", reasons["retest-nq"]);
    }

    [Fact]
    public void GetSnapshot_ShowsEachSkippedEntryAsADisabledCard()
    {
        var engine = CreateEngine();
        engine.AddSetups(BasketWithEntriesThatCantTrade());

        var setups = engine.GetSnapshot().Setups;

        var card = setups.Single(s => s.Id == "ema21-mnq");
        Assert.False(card.Enabled);
        Assert.Equal("retired EMA21 strategy", card.DisabledReason);
        Assert.Equal("MNQZ26", card.Ticker);
        Assert.Null(setups.Single(s => s.Id == "pullback-mnq").DisabledReason);
    }

    [Fact]
    public async Task PublishCurrentStateAsync_WhenEveryEntryIsDisabled_PublishesTheDisabledCardsWithoutAnyBar()
    {
        var sink = new FakeSink();
        var engine = CreateEngine(sink: sink);
        engine.AddSetups(RetiredOnlyBasket());

        await engine.PublishCurrentStateAsync();

        var setups = Assert.Single(sink.Snapshots).Setups;
        Assert.Empty(engine.Groups);
        var card = setups.Single(s => s.Id == "ema21-mnq");
        Assert.Equal("retired EMA21 strategy", card.DisabledReason);
    }
}
