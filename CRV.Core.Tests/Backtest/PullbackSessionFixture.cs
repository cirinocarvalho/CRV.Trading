using System.Text.Json;
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Extensions.Logging.Abstractions;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// One reproducible NY session for a single MNQ pullback setup: a 30-minute opening
/// range of 18000-18020, a break above it, a pullback to 18006 — back inside the
/// range, which is what a Pullback entry actually requires — and a continuation leg
/// through the target. Shared by every test that needs a backtest to produce a
/// known trade.
/// </summary>
internal static class PullbackSessionFixture
{
    public const string Ticker = "MNQM26";

    // 2026-04-15 is a Wednesday in EDT, so ET = UTC-4 and the 09:30-10:00 ET
    // opening range sits at 13:30-14:00 UTC.
    public static readonly DateTime Open = new(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc);

    /// <summary>The setup as configured; <paramref name="tweak"/> adjusts it before serialisation.</summary>
    public static StrategyConfig Config(Action<StrategySetupConfig>? tweak = null)
    {
        var setup = new StrategySetupConfig
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
        };
        tweak?.Invoke(setup);

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
                Config = setup,
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

    public static BacktestConfig BtConfig(FillMode mode = FillMode.WithSlippage, int stopTicks = 4) => new()
    {
        From = Open.Date,
        To   = Open.Date.AddDays(1),
        FillMode = mode,
        StopSlippageTicks = stopTicks,
        ExecutionTFMinutes = 5,
        BacktestSession = "NY",
        DataSource = "CSV",
    };

    public static async IAsyncEnumerable<(string Ticker, Bar Bar)> Session()
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

    public static async Task<BacktestResult> Run(StrategyConfig? cfg = null, BacktestConfig? bt = null)
        => await new BacktestEngine(cfg ?? Config(), bt ?? BtConfig(), NullLogger<BacktestEngine>.Instance)
            .RunAsync(Session());
}
