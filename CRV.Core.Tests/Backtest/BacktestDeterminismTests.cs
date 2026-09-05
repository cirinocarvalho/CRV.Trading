using System.Text.Json;
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The acceptance test for backtest reproducibility: one configuration, one bar
/// series, two runs, identical trades. Runs 920-922 shared a configuration and
/// disagreed by 24.6% on net; the bars were the variable, but nothing was pinning
/// the engine itself either. This pins it.
/// </summary>
public class BacktestDeterminismTests
{
    private readonly ITestOutputHelper _out;
    public BacktestDeterminismTests(ITestOutputHelper output) => _out = output;

    private const string Ticker = "MNQM26";

    // 2026-04-15 is a Wednesday in EDT, so ET = UTC-4 and the 09:30-10:00 ET
    // opening range sits at 13:30-14:00 UTC.
    private static readonly DateTime Open = new(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc);

    private static StrategyConfig Config()
    {
        var basket = new List<BasketEntry>
        {
            new()
            {
                Id = "pullback-mnq", Enabled = true, Label = "Pullback [MNQ]",
                StrategyType = StrategyType.Pullback, Ticker = Ticker,
                PointValue = 2m, TickSize = 0.25m,
                Sessions = new()
                {
                    new() { SessionId = "Asia",   Enabled = false, CutoffHour = 1,  CutoffMinute = 30 },
                    new() { SessionId = "London", Enabled = false, CutoffHour = 8,  CutoffMinute = 0  },
                    new() { SessionId = "NY",     Enabled = true,  CutoffHour = 15, CutoffMinute = 0  },
                },
                Config = new StrategySetupConfig
                {
                    Name = "pullback-mnq", SetupId = SetupId.A,
                    StrategyType = StrategyType.Pullback, Enabled = true,
                    Ticker = Ticker, PointValue = 2m, TickSize = 0.25m,
                    Contracts = 1, MaxContracts = 1, HiVolMult = 1.0m,
                    StopPct = 0.50m, TargetPct = 100, PartialPct = 50,
                    NearPct = 0.30m, MinRr = 0.5m, Mode = "Conservative",
                    PullbackPct = 0.50m, MaxTrades = 3,
                    UsePartial = false, UseBe = false,
                    UseVwap = false, UseOrbClose = false,
                    OrbStart = new TimeOnly(9, 30), OrbEnd = new TimeOnly(10, 0),
                    CutoffHour = 15, CutoffMinute = 0,
                    OrderType = "Limit",
                },
            },
        };

        return new StrategyConfig
        {
            Ticker = Ticker, PointValue = 2m, TickSize = 0.25m,
            CommissionPerSide = 0.90m,
            ExecutionTFMinutes = 5,
            BasketJson = JsonSerializer.Serialize(basket),
        };
    }

    private static BacktestConfig BtConfig(FillMode mode = FillMode.WithSlippage, int stopTicks = 4) => new()
    {
        From = Open.Date,
        To   = Open.Date.AddDays(1),
        FillMode = mode,
        StopSlippageTicks = stopTicks,
        ExecutionTFMinutes = 5,
        BacktestSession = "NY",
        DataSource = "CSV",
    };

    /// <summary>
    /// One NY session: a 30-minute opening range of 18000-18020, a break above it,
    /// a pullback to 18006 — back inside the range, which is what a Pullback entry
    /// actually requires — and then a continuation leg through the target.
    /// </summary>
    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> Session()
    {
        var bars = new List<Bar>();
        void Add(int minute, decimal o, decimal h, decimal l, decimal c) =>
            bars.Add(new Bar(Open.AddMinutes(minute), o, h, l, c, 500));

        // Opening range: oscillate inside 18000-18020, touching each edge once.
        for (int m = 0; m < 30; m++)
        {
            decimal mid = 18010m + (m % 5 - 2) * 2m;
            Add(m, mid, m == 7 ? 18020m : mid + 3m, m == 12 ? 18000m : mid - 3m, mid);
        }

        // Legs after the range is set, as a price walk sampled one bar per minute.
        void Leg(int fromMinute, int toMinute, decimal fromPrice, decimal toPrice)
        {
            int span = toMinute - fromMinute;
            for (int i = 0; i < span; i++)
            {
                decimal a = fromPrice + (toPrice - fromPrice) * i / span;
                decimal b = fromPrice + (toPrice - fromPrice) * (i + 1) / span;
                Add(fromMinute + i, a, Math.Max(a, b), Math.Min(a, b), b);
            }
        }

        Leg(30,  40, 18020m, 18040m);   // break out above the range — arms long
        Leg(40,  50, 18040m, 18006m);   // pull back inside the range — fills at 18010
        Leg(50, 120, 18006m, 18100m);   // continuation through the target

        foreach (var b in bars) yield return (Ticker, b);
        await Task.CompletedTask;
    }

    private static async Task<BacktestResult> Run(BacktestConfig? bt = null)
        => await new BacktestEngine(Config(), bt ?? BtConfig(), NullLogger<BacktestEngine>.Instance)
            .RunAsync(Session());

    [Fact]
    public async Task TwoRunsOfTheSameConfigProduceIdenticalTrades()
    {
        var a = await Run();
        var b = await Run();

        _out.WriteLine($"run A: {a.Trades.Count} trades, net {a.Total.NetPnl:F2}");
        _out.WriteLine($"run B: {b.Trades.Count} trades, net {b.Total.NetPnl:F2}");

        Assert.NotEmpty(a.Trades);   // a vacuous comparison of two empty lists proves nothing
        Assert.Equal(a.Trades.Count, b.Trades.Count);
        Assert.Equal(Fingerprint(a), Fingerprint(b));
        Assert.Equal(a.Total.NetPnl, b.Total.NetPnl);
        Assert.Equal(a.Total.WinRate, b.Total.WinRate);
        Assert.Equal(a.Total.MaxDrawdown, b.Total.MaxDrawdown);
    }

    [Fact]
    public async Task ATenRunSweepNeverDivergesEvenSlightly()
    {
        // Runs 920-922 differed by 24.6%. Three samples were enough to see it;
        // ten make an intermittent divergence very unlikely to slip through.
        var baseline = Fingerprint(await Run());
        for (int i = 0; i < 9; i++)
            Assert.Equal(baseline, Fingerprint(await Run()));
    }

    /// <summary>Every field of every trade that a result depends on, in order.</summary>
    private static string Fingerprint(BacktestResult r) =>
        string.Join('\n', r.Trades.Select(t => string.Join('|',
            t.EnteredAt.ToString("O"), t.ExitedAt.ToString("O"),
            t.SetupLabel, t.Ticker, t.Direction, t.ExitReason,
            t.Contracts, t.Entry, t.InitialStop, t.Target, t.Exit,
            t.GrossPnl, t.Commission, t.NetPnl, t.RMultiple)));
}

/// <summary>
/// A stop-out fixture, to prove the execution model is actually wired into the
/// engine rather than merely unit-tested beside it: the same session under the
/// frictionless mode and under the shipped default must not cost the same.
/// </summary>
public class StopSlippageReachesTheEngineTests
{
    private const string Ticker = "MNQM26";
    private static readonly DateTime Open = new(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc);

    private static StrategyConfig Config()
    {
        var basket = new List<BasketEntry>
        {
            new()
            {
                Id = "pullback-mnq", Enabled = true, Label = "Pullback [MNQ]",
                StrategyType = StrategyType.Pullback, Ticker = Ticker,
                PointValue = 2m, TickSize = 0.25m,
                Sessions = new()
                {
                    new() { SessionId = "Asia",   Enabled = false, CutoffHour = 1,  CutoffMinute = 30 },
                    new() { SessionId = "London", Enabled = false, CutoffHour = 8,  CutoffMinute = 0  },
                    new() { SessionId = "NY",     Enabled = true,  CutoffHour = 15, CutoffMinute = 0  },
                },
                Config = new StrategySetupConfig
                {
                    Name = "pullback-mnq", SetupId = SetupId.A,
                    StrategyType = StrategyType.Pullback, Enabled = true,
                    Ticker = Ticker, PointValue = 2m, TickSize = 0.25m,
                    Contracts = 1, MaxContracts = 1, HiVolMult = 1.0m,
                    StopPct = 0.50m, TargetPct = 100, PartialPct = 50,
                    NearPct = 0.30m, MinRr = 0.5m, Mode = "Conservative",
                    PullbackPct = 0.50m, MaxTrades = 1,
                    UsePartial = false, UseBe = false, UseVwap = false, UseOrbClose = false,
                    CutoffHour = 15, CutoffMinute = 0, OrderType = "Limit",
                },
            },
        };
        return new StrategyConfig
        {
            Ticker = Ticker, PointValue = 2m, TickSize = 0.25m,
            CommissionPerSide = 0.90m, ExecutionTFMinutes = 5,
            BasketJson = System.Text.Json.JsonSerializer.Serialize(basket),
        };
    }

    /// <summary>Same opening range, but the pullback keeps going and takes the stop out.</summary>
    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> StoppedOutSession()
    {
        var bars = new List<Bar>();
        void Add(int m, decimal o, decimal h, decimal l, decimal c) =>
            bars.Add(new Bar(Open.AddMinutes(m), o, h, l, c, 500));
        void Leg(int from, int to, decimal a0, decimal b0)
        {
            int span = to - from;
            for (int i = 0; i < span; i++)
            {
                decimal a = a0 + (b0 - a0) * i / span;
                decimal b = a0 + (b0 - a0) * (i + 1) / span;
                Add(from + i, a, Math.Max(a, b), Math.Min(a, b), b);
            }
        }

        for (int m = 0; m < 30; m++)
        {
            decimal mid = 18010m + (m % 5 - 2) * 2m;
            Add(m, mid, m == 7 ? 18020m : mid + 3m, m == 12 ? 18000m : mid - 3m, mid);
        }
        Leg(30,  40, 18020m, 18040m);   // break out, arm long
        Leg(40,  50, 18040m, 18008m);   // pull back, fill at 18010
        Leg(50, 120, 18008m, 17950m);   // keep falling — through the 18000 stop

        foreach (var b in bars) yield return (Ticker, b);
        await Task.CompletedTask;
    }

    /// <summary>Asserts exactly one trade, and says what was actually there when not.</summary>
    private static TradeRecord SingleTrade(BacktestResult r, string label)
    {
        if (r.Trades.Count == 1) return r.Trades[0];

        string detail = r.Trades.Count == 0
            ? "none at all"
            : string.Join("; ", r.Trades.Select(t =>
                $"{t.EnteredAt:HH:mm}→{t.ExitedAt:HH:mm} {t.Direction} {t.Contracts}ct " +
                $"entry {t.Entry} stop {t.InitialStop} exit {t.Exit} ({t.ExitReason})"));

        throw new Xunit.Sdk.XunitException(
            $"Expected exactly one {label} trade from this fixture, got {r.Trades.Count}: {detail}");
    }

    private static async Task<BacktestResult> Run(FillMode mode, int stopTicks) =>
        await new BacktestEngine(Config(), new BacktestConfig
        {
            From = Open.Date, To = Open.Date.AddDays(1),
            FillMode = mode, StopSlippageTicks = stopTicks,
            ExecutionTFMinutes = 5, BacktestSession = "NY", DataSource = "CSV",
        }, NullLogger<BacktestEngine>.Instance).RunAsync(StoppedOutSession());

    [Fact]
    public async Task TheFrictionlessModeBooksAStopOutAsCheaperThanItIs()
    {
        var free    = await Run(FillMode.AtTouch,      stopTicks: 4);
        var charged = await Run(FillMode.WithSlippage, stopTicks: 4);

        // Spelled out rather than left to Assert.Single: this failed once during
        // development and could not be reproduced, and a bare "Single() failure"
        // says nothing about which run misbehaved or how.
        var freeTrade    = SingleTrade(free,    "frictionless");
        var chargedTrade = SingleTrade(charged, "with slippage");

        Assert.Equal(ExitReason.Stop, freeTrade.ExitReason);
        Assert.Equal(ExitReason.Stop, chargedTrade.ExitReason);

        // 4 ticks x 0.25 x $2/pt = $2 worse on a one-contract MNQ stop.
        Assert.Equal(freeTrade.Exit - 1m, chargedTrade.Exit);
        Assert.True(chargedTrade.NetPnl < freeTrade.NetPnl);
        Assert.Equal(-2m, chargedTrade.NetPnl - freeTrade.NetPnl);
    }

    [Fact]
    public async Task ZeroStopTicksReproducesTheOldBehaviourExactly()
    {
        var old  = await Run(FillMode.AtTouch,      stopTicks: 4);
        var none = await Run(FillMode.WithSlippage, stopTicks: 0);
        Assert.Equal(Assert.Single(old.Trades).Exit, Assert.Single(none.Trades).Exit);
    }
}
