using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Risk;

/// <summary>
/// The portfolio ceiling as the engine actually applies it.
/// </summary>
public class PortfolioGateTests
{
    /// <summary>Records what was placed and hands back a live group, so open exposure accumulates.</summary>
    private sealed class RecordingExecutor : IGroupOrderExecutor
    {
        public List<EntrySignal> Placed { get; } = new();
        private int _n;

        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
        {
            Placed.Add(sig);
            var group = new GroupOrder
            {
                GroupOrderId = $"g{++_n}", SetupId = sig.SetupLabel, Ticker = sig.Ticker,
                Direction = sig.Direction, TotalContracts = sig.TotalContracts,
                EntryPrice = sig.Entry, InitialStopPrice = sig.Stop,
                PointValue = sig.PointValue, Status = GroupOrderStatus.Active,
            };
            return Task.FromResult<GroupOrder?>(group);
        }

        public Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty) => Task.CompletedTask;
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
            => Task.FromResult(0m);
    }

    private sealed class StubStrategy : ISetupStrategy
    {
        public bool Reverted { get; private set; }
        public string Id { get; init; } = "retest-mnq";
        public SetupId SetupId => SetupId.A;
        public StrategyType StrategyType => StrategyType.Retest;
        public string Name => Id;
        public bool IsActive => false;
        public bool IsArmed => false;
        public bool InTrade { get; private set; }
        public int CutoffHour => 16;
        public int CutoffMinute => 0;
        public string Ticker { get; init; } = "MNQM26";
        public decimal PointValue => 2m;
        public TimeOnly OrbStart => new(9, 30);
        public TimeOnly OrbEnd => new(10, 0);
        public bool UseEmaFilter => false;
        public bool BypassChopFilter => false;
        public (int, int) GetCutoffForSession(string s) => (16, 0);
        public bool IsEnabledForSession(string s) => true;
        public void OnBar(Bar b, OrbState o, IndicatorState i, ModuleState m) { }
        public void OnTick(decimal p, DateTime u, OrbState o, IndicatorState i, ModuleState m) { }
        public void Reconfigure(StrategySetupConfig c) { }
        public void Reset() { }
        public void ResetSession() { }
        public void ResetTradeCounters() { }
        public EntrySignal? PendingEntry => null;
        public void ClearPendingSignals() { }
        public void RevertEntry() => Reverted = true;
        public void ForceExit(decimal p, DateTime t, ExitReason r = ExitReason.SessionEnd) { }
        public void Disarm() { }
        public void ResetCutoff() { }
        public SetupStateSnapshot GetSnapshot() => new();
        public void SetInTrade(bool active) => InTrade = active;
        public void SeedTradeCount(int l, int s) { }
    }

    private static EntrySignal Signal(decimal entry, decimal stop, int contracts, string ticker = "MNQM26") =>
        new(SetupId.A, Direction.Long, Entry: entry, Stop: stop,
            Tg2Price: entry + 40m, Tg1Price: entry + 20m, TotalContracts: contracts,
            Time: new DateTime(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc),
            OrderType: "Limit", Ticker: ticker, SetupLabel: "retest-mnq",
            PartialContracts: 0, PointValue: 2m, UsePartial: false, UseBe: false);

    private static (ComposableEngine engine, BrokerEventHandler handler, RecordingExecutor exec)
        Build(decimal maxPortfolioRisk)
    {
        var exec    = new RecordingExecutor();
        var handler = new BrokerEventHandler(exec) { IsBacktest = true };
        var cfg     = new StrategyConfig
        {
            Ticker = "MNQM26", PointValue = 2m, TickSize = 0.25m,
            UseDailyLossLimit = false,
            MaxPortfolioRisk = maxPortfolioRisk,
        }.ToEngineConfig();

        var engine = new ComposableEngine(
            new NoopExec(), new NullSink(), new StubPrices(), cfg, handler);

        return (engine, handler, exec);
    }

    private sealed class NoopExec : IOrderExecutor
    { public Task<decimal?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<decimal?>(null); }
    private sealed class NullSink : IStrategyEventSink
    {
        public Task OnEntryAsync(EntrySignal s) => Task.CompletedTask;
        public Task OnExitAsync(TradeRecord t) => Task.CompletedTask;
        public Task OnSnapshotAsync(EngineSnapshot s) => Task.CompletedTask;
    }
    private sealed class StubPrices : ILastPriceProvider
    {
        public decimal GetLastPrice(string t) => 18000m;
        public void UpdatePrice(string t, decimal p) { }
    }

    private static async Task Route(ComposableEngine engine, ISetupStrategy strategy, EntrySignal sig)
        => await engine.RouteSignalsAsync(new List<StrategySignals> { new(strategy, sig) });

    // ── The gate in the engine ────────────────────────────────────

    [Fact]
    public async Task ASignalInsideTheCeilingIsPlaced()
    {
        var (engine, _, exec) = Build(maxPortfolioRisk: 500m);
        var strategy = new StubStrategy();

        // 20 points x 2 contracts x $2 = $80.
        await Route(engine, strategy, Signal(18020m, 18000m, 2));

        Assert.Single(exec.Placed);
        Assert.False(strategy.Reverted);
    }

    [Fact]
    public async Task ASignalThatWouldBreachTheCeilingIsNotPlaced()
    {
        var (engine, _, exec) = Build(maxPortfolioRisk: 100m);
        var strategy = new StubStrategy();

        // 20 points x 10 contracts x $2 = $400, against a $100 ceiling.
        await Route(engine, strategy, Signal(18020m, 18000m, 10));

        Assert.Empty(exec.Placed);
    }

    [Fact]
    public async Task ARefusedSignalDoesNotConsumeTheSetupsTradeSlot()
    {
        // A portfolio block is temporary — it lifts when a position closes — so the
        // strategy has to be able to re-arm. That is not true of a daily-loss breach,
        // which stops the engine outright.
        var (engine, _, _) = Build(maxPortfolioRisk: 100m);
        var strategy = new StubStrategy();

        await Route(engine, strategy, Signal(18020m, 18000m, 10));

        Assert.True(strategy.Reverted);
    }

    [Fact]
    public async Task AlreadyOpenPositionsCountAgainstTheCeiling()
    {
        var (engine, handler, exec) = Build(maxPortfolioRisk: 200m);
        var strategy = new StubStrategy();

        // First signal commits $160 (20pts x 4ct x $2) and is accepted.
        await Route(engine, strategy, Signal(18020m, 18000m, 4));
        Assert.Single(exec.Placed);

        // Second would add another $160 on top — refused, though on its own it fits.
        await Route(engine, new StubStrategy { Id = "retest-mes" }, Signal(18020m, 18000m, 4));
        Assert.Single(exec.Placed);
    }

    [Fact]
    public async Task WithNoCeilingConfiguredNothingIsBlocked()
    {
        var (engine, _, exec) = Build(maxPortfolioRisk: 0m);
        var strategy = new StubStrategy();

        await Route(engine, strategy, Signal(18020m, 18000m, 500));

        Assert.Single(exec.Placed);
        Assert.False(strategy.Reverted);
    }
}
