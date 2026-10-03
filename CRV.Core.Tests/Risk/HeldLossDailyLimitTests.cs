using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Core.Tests.Strategy;
using Xunit;

namespace CRV.Core.Tests.Risk;

/// <summary>
/// A position held in from an earlier trading day counts its open loss against today's daily
/// loss limit. Trading day rolls at 18:00 ET; 2026-04-16 14:00 UTC is 10:00 ET on the 16th.
/// </summary>
public class HeldLossDailyLimitTests
{
    private static readonly DateTime Now       = new(2026, 4, 16, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Yesterday = new(2026, 4, 15, 18, 0, 0, DateTimeKind.Utc);   // 14:00 ET on the 15th

    private sealed class RecordingExecutor : IGroupOrderExecutor
    {
        public List<EntrySignal> Placed { get; } = new();
        public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
        {
            Placed.Add(sig);
            return Task.FromResult<GroupOrder?>(new GroupOrder
            {
                GroupOrderId = "g-" + sig.SetupLabel, SetupId = sig.SetupLabel, Ticker = sig.Ticker,
                Direction = sig.Direction, TotalContracts = sig.TotalContracts, EntryPrice = sig.Entry,
                InitialStopPrice = sig.Stop, PointValue = sig.PointValue, Status = GroupOrderStatus.Active, CreatedAt = sig.Time,
            });
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

    private sealed record Rig(ComposableEngine Engine, BrokerEventHandler Handler, RecordingExecutor Exec, Prices Prices);

    /// <summary>A $500 floor, and a 2-lot MNQ long ($2/pt) from 18000 held since <paramref name="openedAt"/>.</summary>
    private static Rig Build(DateTime openedAt, decimal? mnqPrice)
    {
        var exec = new RecordingExecutor();
        var handler = new BrokerEventHandler(exec) { IsBacktest = true };
        var prices = new Prices();
        if (mnqPrice is decimal p) prices.UpdatePrice("MNQM26", p);
        prices.UpdatePrice("MESM26", 5000m);
        var cfg = new StrategyConfig
        {
            Ticker = "MNQM26", Timezone = "America/New_York", SessionStartHour = 18,
            UseDailyLossLimit = true, MaxDailyLoss = 500m, DailyLossMode = DailyLossMode.Floor,
        }.ToEngineConfig();
        var engine = new ComposableEngine(new NoopExec(), new NullSink(), prices, cfg, handler);
        engine.AddSetup(new StrategySetupConfig { Id = "hold-mnq", Name = "hold-mnq", StrategyType = StrategyType.Pullback, Enabled = true, Ticker = "MNQM26", PointValue = 2m, CloseAtRthClose = false });
        engine.AddSetup(new StrategySetupConfig { Id = "enter-mes", Name = "enter-mes", StrategyType = StrategyType.Pullback, Enabled = true, Ticker = "MESM26", PointValue = 5m });

        var held = new GroupOrder
        {
            GroupOrderId = "held", SetupId = "hold-mnq", Ticker = "MNQM26", Direction = Direction.Long,
            TotalContracts = 2, PointValue = 2m, EntryPrice = 18000m, InitialStopPrice = 17700m,
            Status = GroupOrderStatus.Active, CreatedAt = openedAt,
        };
        var strategy = engine.GetStrategy("hold-mnq")!;
        handler.RegisterGroup(held, strategy);
        strategy.SetInTrade(true);
        return new Rig(engine, handler, exec, prices);
    }

    private static Task TrySignal(Rig rig) => rig.Engine.RouteSignalsAsync(new List<StrategySignals>
    {
        new(rig.Engine.GetStrategy("enter-mes")!, new EntrySignal(SetupId.F, Direction.Long, 5000m, 4990m, 5020m, 0m, 1, Now,
            OrderType: "Market", Ticker: "MESM26", SetupLabel: "enter-mes", PointValue: 5m, UsePartial: false, UseBe: false)),
    });

    [Fact]
    public async Task HeldLossFromAnEarlierDay_PastTheLimit_StopsNewEntries()
    {
        var rig = Build(Yesterday, mnqPrice: 17800m);   // -200 pts × 2 × $2 = -$800

        await TrySignal(rig);

        Assert.Empty(rig.Exec.Placed);
    }

    [Fact]
    public async Task HeldLossWithinTheLimit_LetsEntriesThrough()
    {
        var rig = Build(Yesterday, mnqPrice: 17900m);   // -$400

        await TrySignal(rig);

        Assert.Single(rig.Exec.Placed);
    }

    [Fact]
    public async Task HeldLossAndRealizedLoss_TripTheLimitTogether()
    {
        var rig = Build(Yesterday, mnqPrice: 17900m);   // -$400 held
        rig.Engine.Risk.RecordTrade(-200m);             // -$200 realized today

        await TrySignal(rig);

        Assert.Empty(rig.Exec.Placed);
    }

    [Fact]
    public async Task HeldWinner_DoesNotHideARealizedLoss()
    {
        var rig = Build(Yesterday, mnqPrice: 18500m);   // +$2,000 held
        rig.Engine.Risk.RecordTrade(-600m);

        await TrySignal(rig);

        Assert.Empty(rig.Exec.Placed);
    }

    [Fact]
    public async Task PositionOpenedToday_IsNotHeld()
    {
        var rig = Build(new DateTime(2026, 4, 16, 13, 45, 0, DateTimeKind.Utc), mnqPrice: 17800m);

        await TrySignal(rig);

        Assert.Single(rig.Exec.Placed);
    }

    [Fact]
    public async Task PositionOpenedInTheEveningSession_IsTodays()
    {
        // 22:30 UTC on the 15th is 18:30 ET: already the 16th's session.
        var rig = Build(new DateTime(2026, 4, 15, 22, 30, 0, DateTimeKind.Utc), mnqPrice: 17800m);

        await TrySignal(rig);

        Assert.Single(rig.Exec.Placed);
    }

    [Fact]
    public async Task HeldPositionWithNoPriceYet_CountsAsZero()
    {
        var rig = Build(Yesterday, mnqPrice: null);

        await TrySignal(rig);

        Assert.Single(rig.Exec.Placed);
    }

    [Fact]
    public async Task ATrippedLimit_DoesNotCloseTheHeldPosition()
    {
        var rig = Build(Yesterday, mnqPrice: 17800m);

        await TrySignal(rig);

        Assert.True(rig.Handler.HasActiveGroup("hold-mnq"));
        Assert.True(rig.Engine.GetStrategy("hold-mnq")!.InTrade);
    }

    [Fact]
    public void HeldOpenLoss_SumsTheLossOfPositionsFromEarlierDays()
    {
        var rig = Build(Yesterday, mnqPrice: 17800m);
        Assert.Equal(-800m, rig.Engine.HeldOpenLoss(Now));
    }

    [Fact]
    public async Task HeldPartialFilledLoss_IsNotOffsetByItsBookedPartial()
    {
        var rig = Build(Yesterday, mnqPrice: 17600m);   // remaining 1 lot: -400 pts × 1 × $2 = -$800
        var held = rig.Handler.GetAllActiveGroups().Single(g => g.SetupId == "hold-mnq");
        held.Status = GroupOrderStatus.PartialFilled;
        held.PartialContracts = 1;
        held.AccruedPartialPnl = 1000m;                 // booked on the earlier day

        Assert.Equal(-800m, rig.Engine.HeldOpenLoss(Now));

        await TrySignal(rig);

        Assert.Empty(rig.Exec.Placed);
    }

    [Fact]
    public void HeldShortLoss_IsMarkedAgainstTheShort()
    {
        var rig = Build(Yesterday, mnqPrice: 18200m);   // short 2 lots: -200 pts × 2 × $2 = -$800
        rig.Handler.GetAllActiveGroups().Single(g => g.SetupId == "hold-mnq").Direction = Direction.Short;

        Assert.Equal(-800m, rig.Engine.HeldOpenLoss(Now));
    }

    [Fact]
    public void Snapshot_WithHeldLossPastTheLimitAndNoSignalYet_IsHalted()
    {
        var rig = Build(Yesterday, mnqPrice: 17800m);

        Assert.True(rig.Engine.GetSnapshot().TradingHalted);
        Assert.True(rig.Engine.Risk.DdBreached(rig.Engine.HeldOpenLoss(DateTime.UtcNow)));
    }

    [Fact]
    public void Snapshot_AfterTheLimitIsRaised_IsNoLongerHalted()
    {
        var rig = Build(Yesterday, mnqPrice: 17800m);
        Assert.True(rig.Engine.GetSnapshot().TradingHalted);

        rig.Engine.ApplyRuntimeSettings(new StrategyConfig
        {
            Ticker = "MNQM26", Timezone = "America/New_York", SessionStartHour = 18,
            UseDailyLossLimit = true, MaxDailyLoss = 5000m, DailyLossMode = DailyLossMode.Floor,
        });

        Assert.False(rig.Engine.GetSnapshot().TradingHalted);
    }
}
