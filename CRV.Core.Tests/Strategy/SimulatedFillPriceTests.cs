namespace CRV.Core.Tests.Strategy;

using System.Diagnostics;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

/// <summary>
/// A simulated executor (backtest, Mock) reports its own fill price, slippage included.
/// That price must be used as is: there is no broker to ask, and asking one whose order
/// IDs happened to look like a real broker's used to replace the slipped price with the
/// bare stop price after three 500 ms retries, so the same backtest gave different R.
/// </summary>
public class SimulatedFillPriceTests
{
    private sealed class SimulatedExecutor : IGroupOrderExecutor
    {
        public int FillPriceLookups;
        public bool IsSimulated => true;
        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal signal) => Task.FromResult<GroupOrder?>(null);
        public Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty) => Task.CompletedTask;
        public Task CancelOrderAsync(string orderId) => Task.CompletedTask;
        public Task<decimal> PlaceMarketCloseAsync(string ticker, Direction direction, int qty) => Task.FromResult(0m);
        public Task<decimal?> GetOrderFillPriceAsync(string orderId)
        {
            Interlocked.Increment(ref FillPriceLookups);
            return Task.FromResult<decimal?>(null);
        }
    }

    private sealed class Setup : ISetupStrategy
    {
        public string Id { get; set; } = "A";
        public SetupId SetupId { get; set; } = SetupId.A;
        public StrategyType StrategyType => StrategyType.Pullback;
        public string Name => "Test";
        public bool IsActive => false;
        public bool IsArmed => false;
        public bool InTrade { get; set; }
        public int CutoffHour => 16;
        public int CutoffMinute => 0;
        public string Ticker => "MNQZ26";
        public decimal PointValue => 2m;
        public TimeOnly OrbStart => new(9, 30);
        public TimeOnly OrbEnd   => new(10, 0);
        public bool UseEmaFilter => false;
        public bool BypassChopFilter => false;
        public bool CloseAtRthClose { get; set; } = true;
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
        public void RevertEntry() { }
        public void ForceExit(decimal p, DateTime t, ExitReason r = ExitReason.SessionEnd) { }
        public void Disarm() { }
        public void ResetCutoff() { }
        public SetupStateSnapshot GetSnapshot() => new();
        public void SetInTrade(bool active) => InTrade = active;
        public void SeedTradeCount(int l, int s) { }
    }

    // An all-digit group ID: what a random 8-hex-character ID is about 2% of the time.
    private const string Gid = "12345678";

    private static GroupOrder ActiveGroup()
    {
        var g = new GroupOrder
        {
            GroupOrderId = Gid, SetupId = "A", Ticker = "MNQZ26", Direction = Direction.Long,
            TotalContracts = 1, PartialContracts = 0, PointValue = 2m,
            Status = GroupOrderStatus.Active, EntryPrice = 20000m, InitialStopPrice = 19950m, Broker = "Mock",
        };
        g.Legs.Add(new OrderLeg { GroupOrderId = Gid, OrderId = Gid + "-e",  LegType = LegType.Entry, OrderType = "Limit", Action = "BUY",  Quantity = 1, Price = 20000m });
        g.Legs.Add(new OrderLeg { GroupOrderId = Gid, OrderId = Gid + "-t2", LegType = LegType.Tg2,   OrderType = "Limit", Action = "SELL", Quantity = 1, Price = 20100m });
        g.Legs.Add(new OrderLeg { GroupOrderId = Gid, OrderId = Gid + "-s",  LegType = LegType.Stop,  OrderType = "Stop",  Action = "SELL", Quantity = 1, Price = 19950m });
        return g;
    }

    private static OrderEvent Filled(string orderId, LegType leg, decimal price) =>
        new(Gid, orderId, leg, OrderLegStatus.Filled, price, 1, null, null, DateTime.UtcNow);

    [Fact]
    public async Task AStopFillKeepsItsSimulatedSlippageEvenWhenTheIdLooksNumeric()
    {
        var exec = new SimulatedExecutor();
        var handler = new BrokerEventHandler(exec);
        TradeRecord? trade = null;
        handler.OnTradeCompleted += (_, t) => trade = t;
        handler.RegisterGroup(ActiveGroup(), new Setup());

        var sw = Stopwatch.StartNew();
        await handler.HandleEventAsync(Filled(Gid + "-s", LegType.Stop, 19947.50m));   // stop 19950, slipped 10 ticks

        Assert.NotNull(trade);
        Assert.Equal(19947.50m, trade!.Exit);
        Assert.Equal(0, exec.FillPriceLookups);
        Assert.True(sw.ElapsedMilliseconds < 400, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ATargetFillUsesItsSimulatedPriceWithoutAskingABroker()
    {
        var exec = new SimulatedExecutor();
        var handler = new BrokerEventHandler(exec);
        TradeRecord? trade = null;
        handler.OnTradeCompleted += (_, t) => trade = t;
        handler.RegisterGroup(ActiveGroup(), new Setup());

        var sw = Stopwatch.StartNew();
        await handler.HandleEventAsync(Filled(Gid + "-t2", LegType.Tg2, 20100m));

        Assert.NotNull(trade);
        Assert.Equal(20100m, trade!.Exit);
        Assert.Equal(0, exec.FillPriceLookups);
        Assert.True(sw.ElapsedMilliseconds < 400, $"took {sw.ElapsedMilliseconds} ms");
    }
}
