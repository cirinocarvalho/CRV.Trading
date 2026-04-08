namespace CRV.Core.Tests.Strategy;

using CRV.Backtest.Engine;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Integration test: BacktestGroupOrderExecutor + BrokerEventHandler
/// Reproduces the bug where Tg2 (target) fills but Tg1 (partial) doesn't.
/// </summary>
public class BacktestPartialFillTests
{
    private readonly ITestOutputHelper _out;

    public BacktestPartialFillTests(ITestOutputHelper output) => _out = output;

    private class FakeSetup : ISetupStrategy
    {
        public string Id { get; set; } = "orbfakeout-mgc";
        public SetupId SetupId { get; set; } = SetupId.A;
        public StrategyType StrategyType => StrategyType.OrbFakeout;
        public string Name => "Test";
        public bool IsActive => false;
        public bool IsArmed => false;
        public bool InTrade { get; set; }
        public int CutoffHour => 16;
        public int CutoffMinute => 0;
        public string Ticker => "MGCM26";
        public decimal PointValue => 10m;
        public bool UseEmaFilter => false;
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

    [Fact]
    public async Task Tg1_Fills_Before_Tg2_For_Long_2Contract_Trade()
    {
        // Arrange: simulate the exact scenario from user's bug report
        // Long entry at 5025.1, partial at 5074.2, target at 5123.2, stop at 4959.7
        var cfg = new StrategyConfig { PointValue = 10, TickSize = 0.1m };
        var btCfg = new BacktestConfig { FillMode = FillMode.AtTouch };
        var executor = new BacktestGroupOrderExecutor(btCfg, cfg);
        var handler = new BrokerEventHandler(executor);

        TradeRecord? completedTrade = null;
        handler.OnTradeCompleted += (_, trade) => completedTrade = trade;

        executor.OnEvent = evt => handler.HandleEventAsync(evt);

        var strategy = new FakeSetup();
        var signal = new EntrySignal(
            SetupId.A, Direction.Long,
            Entry: 5025.1m, Stop: 4959.7m, Tg2Price: 5123.2m, Tg1Price: 5074.2m,
            TotalContracts: 2, Time: new DateTime(2026, 3, 3, 15, 0, 0, DateTimeKind.Utc),
            OrderType: "Limit", Ticker: "MGCM26", SetupLabel: "orbfakeout-mgc",
            PartialContracts: 1, PointValue: 10m, UsePartial: true, UseBe: true);

        // Act: place entry (Limit order — stays WORKING)
        await handler.PlaceEntryAsync(signal, strategy);

        var t = new DateTime(2026, 3, 3, 15, 0, 0, DateTimeKind.Utc);

        // Simulate OHLC sub-ticks over several bars
        // Bar 1: O=5030, L=5020, H=5040, C=5035 (bullish) — entry fills on L
        await Tick(executor, 5030m, t, "MGCM26"); // O: entry? 5030 <= 5025.1? NO
        await Tick(executor, 5020m, t.AddSeconds(15), "MGCM26"); // L: entry? 5020 <= 5025.1? YES → fills
        _out.WriteLine($"After entry fill: InTrade={strategy.InTrade}");

        await Tick(executor, 5040m, t.AddSeconds(30), "MGCM26"); // H: tg1? 5040 >= 5074.2? NO
        await Tick(executor, 5035m, t.AddSeconds(45), "MGCM26"); // C: NO

        // Bar 2: O=5050, L=5045, H=5080, C=5075 — tg1 fills on H
        t = t.AddMinutes(1);
        await Tick(executor, 5050m, t, "MGCM26");
        await Tick(executor, 5045m, t.AddSeconds(15), "MGCM26");
        await Tick(executor, 5080m, t.AddSeconds(30), "MGCM26"); // H: tg1? 5080 >= 5074.2? YES
        _out.WriteLine($"After tg1 tick: completedTrade is null? {completedTrade == null}");

        await Tick(executor, 5075m, t.AddSeconds(45), "MGCM26");

        // Bar 3: O=5100, L=5095, H=5130, C=5125 — tg2 fills on H
        t = t.AddMinutes(1);
        await Tick(executor, 5100m, t, "MGCM26");
        await Tick(executor, 5095m, t.AddSeconds(15), "MGCM26");
        await Tick(executor, 5130m, t.AddSeconds(30), "MGCM26"); // H: tg2? 5130 >= 5123.2? YES
        await Tick(executor, 5125m, t.AddSeconds(45), "MGCM26");

        // Assert
        Assert.NotNull(completedTrade);
        _out.WriteLine($"PartialFilled={completedTrade!.PartialFilled}, GrossPnl={completedTrade.GrossPnl}, ExitReason={completedTrade.ExitReason}");
        Assert.Equal(ExitReason.Target, completedTrade.ExitReason);
        Assert.True(completedTrade.PartialFilled, "Tg1 should have filled before Tg2");
        Assert.True(completedTrade.GrossPnl > 0);
    }

    [Fact]
    public async Task Tg1_Fills_When_Same_Bar_As_Tg2()
    {
        // Scenario: entry fills, then same bar has H above both tg1 and tg2
        var cfg = new StrategyConfig { PointValue = 10, TickSize = 0.1m };
        var btCfg = new BacktestConfig { FillMode = FillMode.AtTouch };
        var executor = new BacktestGroupOrderExecutor(btCfg, cfg);
        var handler = new BrokerEventHandler(executor);

        TradeRecord? completedTrade = null;
        handler.OnTradeCompleted += (_, trade) => completedTrade = trade;
        executor.OnEvent = evt => handler.HandleEventAsync(evt);

        var strategy = new FakeSetup();
        var signal = new EntrySignal(
            SetupId.A, Direction.Long,
            Entry: 5025.1m, Stop: 4959.7m, Tg2Price: 5123.2m, Tg1Price: 5074.2m,
            TotalContracts: 2, Time: new DateTime(2026, 3, 3, 15, 0, 0, DateTimeKind.Utc),
            OrderType: "Limit", Ticker: "MGCM26", SetupLabel: "orbfakeout-mgc",
            PartialContracts: 1, PointValue: 10m, UsePartial: true, UseBe: true);

        await handler.PlaceEntryAsync(signal, strategy);

        var t = new DateTime(2026, 3, 3, 15, 0, 0, DateTimeKind.Utc);

        // Single bar: O=5020, L=5010, H=5130, C=5125 — entry on O, tg1 on H, tg2 on C
        await Tick(executor, 5020m, t, "MGCM26");             // entry fills (5020 <= 5025.1)
        await Tick(executor, 5010m, t.AddSeconds(15), "MGCM26"); // L — no fills
        await Tick(executor, 5130m, t.AddSeconds(30), "MGCM26"); // H — tg1 fills (5130 >= 5074.2), break
        await Tick(executor, 5125m, t.AddSeconds(45), "MGCM26"); // C — tg2 fills (5125 >= 5123.2)

        Assert.NotNull(completedTrade);
        _out.WriteLine($"PartialFilled={completedTrade!.PartialFilled}, GrossPnl={completedTrade.GrossPnl}");
        Assert.Equal(ExitReason.Target, completedTrade.ExitReason);
        Assert.True(completedTrade.PartialFilled, "Tg1 should fill on H before Tg2 fills on C");
    }

    private static Task Tick(BacktestGroupOrderExecutor exec, decimal price, DateTime t, string ticker)
        => exec.EvaluateFillsAsync(price, t, ticker);
}
