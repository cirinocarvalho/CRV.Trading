using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Modules;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class TickerGroupTests
{
    // ── Helpers ────────────────────────────────────────────────────

    private static StrategyConfig DefaultConfig() => new()
    {
        Ticker = "/NQH2026",
        PointValue = 20m,
        TickSize = 0.25m,
        Timezone = "America/New_York",
        OrbStart = new TimeOnly(9, 30),
        OrbEnd = new TimeOnly(10, 0),
        ExecutionTFMinutes = 1,
        AllowBothSameBar = false,
    };

    private static Bar MakeBar(DateTime utc, decimal open, decimal high, decimal low, decimal close,
        long volume = 100, bool confirmed = true)
        => new(utc, open, high, low, close, volume, confirmed);

    /// <summary>Minimal fake strategy for testing group dispatch without real strategy logic.</summary>
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
        public Bar? LastBar { get; private set; }
        public decimal LastTickPrice { get; private set; }
        public OrbState? LastOrbState { get; private set; }
        public IndicatorState? LastIndicatorState { get; private set; }
        public ModuleState? LastModuleState { get; private set; }

        public EntrySignal? PendingEntry { get; set; }

        public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules)
        {
            OnBarCallCount++;
            LastBar = bar;
            LastOrbState = orb;
            LastIndicatorState = indicators;
            LastModuleState = modules;
        }

        public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules)
        {
            OnTickCallCount++;
            LastTickPrice = price;
            LastOrbState = orb;
            LastIndicatorState = indicators;
            LastModuleState = modules;
        }

        public void Reconfigure(StrategySetupConfig config) { }
        public void Reset()
        {
            OnBarCallCount = 0;
        }
        public void ResetTradeCounters() { }
        public void Disarm() { }
        public void ResetCutoff() { }
        public (int Hour, int Minute) GetCutoffForSession(string s) => (CutoffHour, CutoffMinute);
        public bool IsEnabledForSession(string s) => true;
        public void ResetSession()
        {
            OnBarCallCount = 0;
            OnTickCallCount = 0;
            IsActive = false;
            IsArmed = false;
            PendingEntry = null;
        }
        /// <summary>Direction of the active trade (used by opposing position guard).</summary>
        public Direction TradeDirection { get; set; } = Direction.Long;

        public void ClearPendingSignals()
        {
            PendingEntry = null;
        }
        public void RevertEntry() { PendingEntry = null; IsActive = false; }
        public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd) { }
        public SetupStateSnapshot GetSnapshot() => new() { SetupId = SetupId, Name = Name };
    }

    // ── GetGroupKey tests ─────────────────────────────────────────

    [Theory]
    [InlineData("NQ", "NQ")]
    [InlineData("NQH2026", "NQ")]
    [InlineData("MNQ", "NQ")]
    [InlineData("MNQH2026", "NQ")]
    [InlineData("ES", "ES")]
    [InlineData("ESH2026", "ES")]
    [InlineData("MES", "ES")]
    [InlineData("MESH2026", "ES")]
    [InlineData("CL", "CL")]
    [InlineData("YM", "YM")]
    public void GetGroupKey_MapsTickersCorrectly(string ticker, string expected)
    {
        Assert.Equal(expected, TickerGroup.GetGroupKey(ticker));
    }

    // ── AddStrategy ───────────────────────────────────────────────

    [Fact]
    public void AddStrategy_RegistersStrategy()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var fake = new FakeStrategy { Id = "A", SetupId = SetupId.A };

        group.AddStrategy(fake);

        Assert.Single(group.Strategies);
        Assert.Same(fake, group.Strategies[0]);
    }

    [Fact]
    public void AddStrategy_MultipleStrategies()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        var b = new FakeStrategy { Id = "B", SetupId = SetupId.B };

        group.AddStrategy(a);
        group.AddStrategy(b);

        Assert.Equal(2, group.Strategies.Count);
    }

    // ── Bar dispatch ──────────────────────────────────────────────

    [Fact]
    public async Task ProcessBarAsync_DispatchesToAllStrategies()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        var b = new FakeStrategy { Id = "B", SetupId = SetupId.B };
        group.AddStrategy(a);
        group.AddStrategy(b);

        // Use a time inside NY session
        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc); // 10:30 ET
        var bar = MakeBar(utc, 100m, 105m, 95m, 102m);

        await group.ProcessBarAsync(bar);

        Assert.Equal(1, a.OnBarCallCount);
        Assert.Equal(1, b.OnBarCallCount);
        Assert.Equal(bar, a.LastBar);
        Assert.Equal(bar, b.LastBar);
    }

    [Fact]
    public async Task ProcessBarAsync_UpdatesIndicatorsBeforeDispatch()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        group.AddStrategy(a);

        // Feed enough bars for ATR to become ready (14 bars)
        for (int i = 0; i < 15; i++)
        {
            var utc = new DateTime(2026, 3, 20, 14, 30 + i, 0, DateTimeKind.Utc);
            var bar = MakeBar(utc, 100m + i, 105m + i, 95m + i, 102m + i);
            await group.ProcessBarAsync(bar);
        }

        // After 15 bars, ATR should be seeded
        var indState = group.GetIndicatorState();
        Assert.True(indState.Atr > 0, "ATR should be > 0 after enough bars");
    }

    [Fact]
    public async Task ProcessBarAsync_SkipsUnconfirmedBarsForIndicators()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        group.AddStrategy(a);

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        var unconfirmed = MakeBar(utc, 100m, 105m, 95m, 102m, confirmed: false);

        await group.ProcessBarAsync(unconfirmed);

        // Unconfirmed bars should not dispatch to strategies
        Assert.Equal(0, a.OnBarCallCount);
    }

    // ── Tick dispatch ─────────────────────────────────────────────

    [Fact]
    public async Task ProcessTickAsync_DispatchesToAllStrategies()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        var b = new FakeStrategy { Id = "B", SetupId = SetupId.B };
        group.AddStrategy(a);
        group.AddStrategy(b);

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        await group.ProcessTickAsync(100.50m, utc);

        Assert.Equal(1, a.OnTickCallCount);
        Assert.Equal(1, b.OnTickCallCount);
        Assert.Equal(100.50m, a.LastTickPrice);
    }

    // ── Cross-setup coordination (_enteredThisBar) ────────────────

    [Fact]
    public async Task ProcessBarAsync_EnteredThisBarBlocksSecondEntry()
    {
        var cfg = DefaultConfig();
        cfg.AllowBothSameBar = false;
        var group = new TickerGroup("NQ", cfg);

        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        var b = new FakeStrategy { Id = "B", SetupId = SetupId.B };
        group.AddStrategy(a);
        group.AddStrategy(b);

        // Strategy A will produce an entry signal
        a.PendingEntry = new EntrySignal(SetupId.A, Direction.Long, 100m, 95m, 110m, 105m, 2,
            DateTime.UtcNow, Ticker: "/NQH2026");

        // Strategy B also wants to enter
        b.PendingEntry = new EntrySignal(SetupId.B, Direction.Short, 100m, 105m, 90m, 95m, 2,
            DateTime.UtcNow, Ticker: "/NQH2026");

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        var bar = MakeBar(utc, 100m, 105m, 95m, 102m);
        await group.ProcessBarAsync(bar);

        var signals = group.CollectAndClearSignals();

        // First strategy's entry should be collected, second should be suppressed
        var entries = signals.Where(s => s.Entry != null).ToList();
        Assert.Single(entries);
        Assert.Equal(SetupId.A, entries[0].Strategy.SetupId);
    }

    [Fact]
    public async Task ProcessBarAsync_AllowBothSameBarPermitsMultipleEntries()
    {
        var cfg = DefaultConfig();
        cfg.AllowBothSameBar = true;
        var group = new TickerGroup("NQ", cfg);

        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        var b = new FakeStrategy { Id = "B", SetupId = SetupId.B };
        group.AddStrategy(a);
        group.AddStrategy(b);

        a.PendingEntry = new EntrySignal(SetupId.A, Direction.Long, 100m, 95m, 110m, 105m, 2,
            DateTime.UtcNow, Ticker: "/NQH2026");
        b.PendingEntry = new EntrySignal(SetupId.B, Direction.Short, 100m, 105m, 90m, 95m, 2,
            DateTime.UtcNow, Ticker: "/NQH2026");

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        var bar = MakeBar(utc, 100m, 105m, 95m, 102m);
        await group.ProcessBarAsync(bar);

        var signals = group.CollectAndClearSignals();
        var entries = signals.Where(s => s.Entry != null).ToList();
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task ProcessBarAsync_EnteredThisBarResetsEachBar()
    {
        var cfg = DefaultConfig();
        cfg.AllowBothSameBar = false;
        var group = new TickerGroup("NQ", cfg);

        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        group.AddStrategy(a);

        // Bar 1: entry produced
        a.PendingEntry = new EntrySignal(SetupId.A, Direction.Long, 100m, 95m, 110m, 105m, 2,
            DateTime.UtcNow, Ticker: "/NQH2026");
        var utc1 = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        await group.ProcessBarAsync(MakeBar(utc1, 100m, 105m, 95m, 102m));
        group.CollectAndClearSignals();

        // Bar 2: another entry should be allowed (flag reset)
        a.PendingEntry = new EntrySignal(SetupId.A, Direction.Long, 102m, 97m, 112m, 107m, 2,
            DateTime.UtcNow, Ticker: "/NQH2026");
        var utc2 = new DateTime(2026, 3, 20, 14, 31, 0, DateTimeKind.Utc);
        await group.ProcessBarAsync(MakeBar(utc2, 101m, 106m, 96m, 103m));

        var signals = group.CollectAndClearSignals();
        Assert.Single(signals.Where(s => s.Entry != null));
    }

    // ── CollectAndClearSignals ────────────────────────────────────

    [Fact]
    public async Task CollectAndClearSignals_ReturnsAllPendingSignals()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        group.AddStrategy(a);

        a.PendingEntry = new EntrySignal(SetupId.A, Direction.Long, 100m, 95m, 110m, 105m, 2,
            DateTime.UtcNow, Ticker: "/NQH2026");

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        await group.ProcessBarAsync(MakeBar(utc, 100m, 105m, 95m, 102m));

        var signals = group.CollectAndClearSignals();
        Assert.Single(signals);
        Assert.NotNull(signals[0].Entry);
    }

    [Fact]
    public async Task CollectAndClearSignals_ClearsAfterCollect()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        group.AddStrategy(a);

        a.PendingEntry = new EntrySignal(SetupId.A, Direction.Long, 100m, 95m, 110m, 105m, 2,
            DateTime.UtcNow, Ticker: "/NQH2026");

        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        await group.ProcessBarAsync(MakeBar(utc, 100m, 105m, 95m, 102m));

        group.CollectAndClearSignals();
        var signals2 = group.CollectAndClearSignals();
        Assert.Empty(signals2.Where(s => s.Entry != null));
    }

    // ── State accessors ───────────────────────────────────────────

    [Fact]
    public void GetOrbState_ReturnsDefaults_BeforeAnyBars()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var orb = group.GetOrbState();

        Assert.False(orb.IsSet);
        Assert.Equal(0m, orb.High);
    }

    [Fact]
    public void GetIndicatorState_ReturnsDefaults_BeforeAnyBars()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var ind = group.GetIndicatorState();

        Assert.Equal(0m, ind.Atr);
        Assert.Equal(0m, ind.Vwap);
    }

    [Fact]
    public void GetModuleState_ReturnsDefaults_BeforeAnyBars()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var mod = group.GetModuleState();

        Assert.Equal(SessionType.PreMarket, mod.CurrentSession);
    }

    // ── Reset ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_ClearsAllState()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        group.AddStrategy(a);

        // Feed a bar
        var utc = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        await group.ProcessBarAsync(MakeBar(utc, 100m, 105m, 95m, 102m));

        group.Reset();

        // After reset, strategies should have been reset
        Assert.Equal(0, a.OnBarCallCount);  // FakeStrategy.Reset clears count
    }

    // ── Semaphore serialization ───────────────────────────────────

    [Fact]
    public async Task ProcessBarAsync_SerializesConcurrentCalls()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        var a = new FakeStrategy { Id = "A", SetupId = SetupId.A };
        group.AddStrategy(a);

        // Fire multiple bars concurrently — none should throw
        var utcBase = new DateTime(2026, 3, 20, 14, 30, 0, DateTimeKind.Utc);
        var tasks = Enumerable.Range(0, 10).Select(i =>
            group.ProcessBarAsync(MakeBar(utcBase.AddMinutes(i), 100m, 105m, 95m, 102m)));

        await Task.WhenAll(tasks);

        // All 10 confirmed bars should have been dispatched
        Assert.Equal(10, a.OnBarCallCount);
    }

    // ── TickerKey property ────────────────────────────────────────

    [Fact]
    public void TickerKey_MatchesConstructorArg()
    {
        var group = new TickerGroup("NQ", DefaultConfig());
        Assert.Equal("NQ", group.TickerKey);
    }

    // NOTE: Opposing position guard tests removed — guard now uses BrokerEventHandler
    // (requires live broker context, not available in unit tests with FakeStrategy).
}
