using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// The session-end exit the live orchestrator and the backtest both call
/// (ComposableEngine.ForceExitAllAsync), with a mock executor.
/// </summary>
public class HoldPastSessionEndTests
{
    private static readonly DateTime EnteredAt = new(2026, 4, 15, 18, 0, 0, DateTimeKind.Utc);   // 14:00 ET
    private static readonly DateTime SessionEnd = new(2026, 4, 15, 20, 0, 0, DateTimeKind.Utc);  // 16:00 ET
    private static readonly DateTime NextMorning = new(2026, 4, 16, 14, 0, 0, DateTimeKind.Utc); // 10:00 ET

    /// <summary>Fills every entry at once and hands back a group with a working stop and target.</summary>
    private sealed class FillingExecutor : IGroupOrderExecutor
    {
        public Dictionary<string, GroupOrder> Groups { get; } = new();
        public bool IsSimulated => true;

        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
        {
            var id = "g-" + sig.SetupLabel;
            var g = new GroupOrder
            {
                GroupOrderId = id, SetupId = sig.SetupLabel, Ticker = sig.Ticker, Direction = sig.Direction,
                TotalContracts = sig.TotalContracts, PointValue = sig.PointValue, EntryPrice = sig.Entry,
                InitialStopPrice = sig.Stop, Status = GroupOrderStatus.Active, CreatedAt = sig.Time,
            };
            g.Legs.Add(new OrderLeg { GroupOrderId = id, OrderId = id + "-e", LegType = LegType.Entry, Status = OrderLegStatus.Filled, Price = sig.Entry, Quantity = sig.TotalContracts });
            g.Legs.Add(new OrderLeg { GroupOrderId = id, OrderId = id + "-t", LegType = LegType.Tg2, Price = sig.Tg2Price, Quantity = sig.TotalContracts });
            g.Legs.Add(new OrderLeg { GroupOrderId = id, OrderId = id + "-s", LegType = LegType.Stop, Price = sig.Stop, Quantity = sig.TotalContracts });
            Groups[sig.SetupLabel] = g;
            return Task.FromResult<GroupOrder?>(g);
        }

        public Task ModifyOrderAsync(string id, decimal? p, int? q) => Task.CompletedTask;
        public Task CancelOrderAsync(string id) => Task.CompletedTask;
        public Task<decimal> PlaceMarketCloseAsync(string t, Direction d, int q) => Task.FromResult(0m);
    }

    private sealed class Prices : ILastPriceProvider
    {
        private readonly Dictionary<string, decimal> _p = new();
        public decimal GetLastPrice(string t) => _p.TryGetValue(t, out var v) ? v : 0m;
        public void UpdatePrice(string t, decimal p) => _p[t] = p;
    }

    private sealed record Rig(ComposableEngine Engine, BrokerEventHandler Handler, FillingExecutor Exec, List<TradeRecord> Trades);

    private static Rig Build()
    {
        var exec = new FillingExecutor();
        var handler = new BrokerEventHandler(exec) { IsBacktest = true };
        var prices = new Prices();
        prices.UpdatePrice("MNQM26", 18050m);
        prices.UpdatePrice("MESM26", 5010m);
        var cfg = new StrategyConfig { Ticker = "MNQM26", PointValue = 2m, TickSize = 0.25m, UseDailyLossLimit = false }.ToEngineConfig();
        var engine = new ComposableEngine(new NoopExec(), new NullSink(), prices, cfg, handler);
        engine.AddSetup(Setup("hold-mnq", "MNQM26", 2m, close: false));
        engine.AddSetup(Setup("close-mes", "MESM26", 5m, close: true));
        var trades = new List<TradeRecord>();
        handler.OnTradeCompleted += (_, t) => trades.Add(t);
        return new Rig(engine, handler, exec, trades);
    }

    private static StrategySetupConfig Setup(string id, string ticker, decimal pv, bool close) => new()
    {
        Id = id, Name = id, StrategyType = StrategyType.Pullback, Enabled = true,
        Ticker = ticker, PointValue = pv, TickSize = 0.25m, CloseAtRthClose = close,
    };

    private static Task Enter(Rig rig, string id, string ticker, decimal entry, decimal stop, decimal pv) =>
        rig.Engine.RouteSignalsAsync(new List<StrategySignals>
        {
            new(rig.Engine.GetStrategy(id)!, new EntrySignal(SetupId.F, Direction.Long, entry, stop, entry + 40m, 0m, 1, EnteredAt,
                OrderType: "Market", Ticker: ticker, SetupLabel: id, PointValue: pv, UsePartial: false, UseBe: false)),
        });

    [Fact]
    public async Task SessionEnd_ClosesTheClosingSetup_AndKeepsTheHoldingOne()
    {
        var rig = Build();
        await Enter(rig, "hold-mnq", "MNQM26", 18010m, 18000m, 2m);
        await Enter(rig, "close-mes", "MESM26", 5000m, 4990m, 5m);

        await rig.Engine.ForceExitAllAsync(SessionEnd);

        Assert.True(rig.Handler.HasActiveGroup("hold-mnq"));
        Assert.True(rig.Engine.GetStrategy("hold-mnq")!.InTrade);
        Assert.False(rig.Handler.HasActiveGroup("close-mes"));
        Assert.False(rig.Engine.GetStrategy("close-mes")!.InTrade);
        var t = Assert.Single(rig.Trades);
        Assert.Equal(("close-mes", ExitReason.SessionEnd), (t.SetupLabel, t.ExitReason));
    }

    [Fact]
    public async Task HeldPosition_StillExitsOnItsStop_NextSession()
    {
        var rig = Build();
        await Enter(rig, "hold-mnq", "MNQM26", 18010m, 18000m, 2m);
        await rig.Engine.ForceExitAllAsync(SessionEnd);

        // Next trading day: daily reset, then the next session starts.
        rig.Engine.ResetDaily();
        rig.Engine.Reconfigure(new StrategyConfig { Ticker = "MNQM26", UseDailyLossLimit = false }, SessionId.NY);
        Assert.True(rig.Engine.GetStrategy("hold-mnq")!.InTrade);

        var g = rig.Exec.Groups["hold-mnq"];
        await rig.Handler.HandleEventAsync(new OrderEvent(g.GroupOrderId, g.GroupOrderId + "-s", LegType.Stop,
            OrderLegStatus.Filled, 18000m, 1, null, null, NextMorning));

        Assert.False(rig.Handler.HasActiveGroup("hold-mnq"));
        Assert.False(rig.Engine.GetStrategy("hold-mnq")!.InTrade);
        var t = Assert.Single(rig.Trades);
        Assert.Equal((ExitReason.Stop, 18000m, NextMorning), (t.ExitReason, t.Exit, t.ExitedAt));
    }

    [Fact]
    public async Task SessionEnd_CancelsAHoldingSetupsUnfilledEntry()
    {
        var rig = Build();
        var pending = new GroupOrder
        {
            GroupOrderId = "g-pending", SetupId = "hold-mnq", Ticker = "MNQM26", Direction = Direction.Long,
            TotalContracts = 1, PointValue = 2m, Status = GroupOrderStatus.Pending,
        };
        pending.Legs.Add(new OrderLeg { GroupOrderId = "g-pending", OrderId = "g-pending-e", LegType = LegType.Entry, Price = 18010m, Quantity = 1 });
        rig.Handler.RegisterGroup(pending, rig.Engine.GetStrategy("hold-mnq")!);

        await rig.Engine.ForceExitAllAsync(SessionEnd);

        Assert.False(rig.Handler.HasActiveGroup("hold-mnq"));
        Assert.Equal(GroupOrderStatus.Canceled, pending.Status);
    }

    [Fact]
    public async Task SessionEnd_ClosesAManualTrade()
    {
        var rig = Build();
        var signal = new EntrySignal(SetupId.F, Direction.Long, 18010m, 18000m, 18050m, 0m, 1, EnteredAt,
            Ticker: "MNQM26", SetupLabel: "manual-1", PointValue: 2m);
        var g = (await rig.Exec.OnEntrySignalAsync(signal))!;
        rig.Handler.RegisterGroup(g, new ManualStrategy(signal) { PointValue = 2m });

        await rig.Engine.ForceExitAllAsync(SessionEnd);

        Assert.False(rig.Handler.HasActiveGroup("manual-1"));
    }
}
