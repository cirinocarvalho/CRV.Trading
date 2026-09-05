using System.Text.Json;
using CRV.Backtest.DataLoaders;
using CRV.Backtest.Engine;
using CRV.Backtest.Experiments;
using CRV.Core.Models;
using CRV.Core.Statistics;
using CRV.Core.Strategy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The sweep and ablation machinery, end to end against the engine. What matters
/// here is not that a particular variant wins — it is that every variant sees the
/// same bars, that a config change actually reaches the engine, and that the runner
/// refuses to guess when the bars are not pinned.
/// </summary>
public class ValidationRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crv-val-" + Guid.NewGuid().ToString("N"));
    private const string Ticker = "MNQM26";
    private static readonly DateTime Open = new(2026, 4, 15, 13, 30, 0, DateTimeKind.Utc);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private BarSnapshotStore Store() => new(_dir);
    private ValidationRunner Runner() => new(Store(), NullLogger<ValidationRunner>.Instance);

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
                    UsePartial = false, UseBe = false, UseVwap = false, UseOrbClose = false,
                    CutoffHour = 15, CutoffMinute = 0, OrderType = "Limit",
                },
            },
        };
        return new StrategyConfig
        {
            Ticker = Ticker, PointValue = 2m, TickSize = 0.25m,
            CommissionPerSide = 0.90m, ExecutionTFMinutes = 5,
            BasketJson = JsonSerializer.Serialize(basket),
        };
    }

    private static BacktestConfig BtConfig() => new()
    {
        From = Open.Date, To = Open.Date.AddDays(1),
        FillMode = FillMode.WithSlippage, ExecutionTFMinutes = 5,
        BacktestSession = "NY", DataSource = "CSV",
    };

    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> Session()
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
        Leg(30,  40, 18020m, 18040m);
        Leg(40,  50, 18040m, 18006m);
        Leg(50, 120, 18006m, 18100m);

        foreach (var b in bars) yield return (Ticker, b);
        await Task.CompletedTask;
    }

    private async Task CaptureBars()
    {
        var key = BarSnapshotStore.KeyFor(BtConfig(), new[] { Ticker });
        await foreach (var _ in Store().Capture(key, Session())) { }
    }

    // ── The guard ─────────────────────────────────────────────────

    [Fact]
    public async Task ValidatingWithoutASnapshotIsRefusedRatherThanRunOnWhateverArrives()
    {
        var ex = await Assert.ThrowsAsync<BarLoadException>(
            () => Runner().SplitAsync(Config(), BtConfig()));
        Assert.Contains("snapshot", ex.Message);
    }

    // ── P1-2: the sweep ───────────────────────────────────────────

    [Fact]
    public async Task EveryVariantInASweepIsScored()
    {
        await CaptureBars();
        var surface = await Runner().SweepAsync(Config(), BtConfig(),
            ValidationRunner.OrbDurations(new TimeOnly(9, 30), 5, 15, 30, 60));

        Assert.Equal(4, surface.Points.Count);
        Assert.Equal(new[] { "5m", "15m", "30m", "60m" }, surface.Points.Select(p => p.Label));
    }

    [Fact]
    public async Task ChangingTheOrbDurationChangesTheTrades()
    {
        // If the variant's config never reached the engine, every cell would be
        // identical and the whole sweep would be theatre.
        await CaptureBars();
        var surface = await Runner().SweepAsync(Config(), BtConfig(),
            ValidationRunner.OrbDurations(new TimeOnly(9, 30), 5, 30));

        Assert.NotEqual(surface.Points[0].Edge.Count, surface.Points[1].Edge.Count);
    }

    [Fact]
    public async Task ASweepOnOneSessionOfDataRecommendsNothing()
    {
        // One session yields a handful of trades per cell. The right answer is
        // silence, not whichever cell happened to land highest.
        await CaptureBars();
        var surface = await Runner().SweepAsync(Config(), BtConfig(),
            ValidationRunner.OrbDurations(new TimeOnly(9, 30), 5, 15, 30, 60));

        Assert.All(surface.Points, p => Assert.Equal(EdgeVerdict.InsufficientEvidence, p.Edge.Verdict));
        Assert.False(surface.HasStableRegion);
        Assert.Null(surface.Recommended);
    }

    [Fact]
    public async Task RunningTheSameSweepTwiceGivesTheSameSurface()
    {
        await CaptureBars();
        var variants = ValidationRunner.OrbDurations(new TimeOnly(9, 30), 15, 30, 60).ToList();

        var a = await Runner().SweepAsync(Config(), BtConfig(), variants);
        var b = await Runner().SweepAsync(Config(), BtConfig(), variants);

        Assert.Equal(a.Points.Select(p => p.Edge.MeanR), b.Points.Select(p => p.Edge.MeanR));
        Assert.Equal(a.Points.Select(p => p.Edge.Count), b.Points.Select(p => p.Edge.Count));
    }

    // ── P1-3: the ablation ────────────────────────────────────────

    [Fact]
    public async Task EveryFilterIsMeasuredAgainstTheBareBreak()
    {
        await CaptureBars();
        var study = await Runner().AblateAsync(Config(), BtConfig());

        Assert.Equal(ValidationRunner.FilterSwitches.Length, study.Ranked.Count);
        Assert.Equal(
            ValidationRunner.FilterSwitches.Select(f => f.Name).OrderBy(n => n),
            study.Ranked.Select(a => a.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task OnAThinSampleNoFilterIsCreditedOrCondemned()
    {
        await CaptureBars();
        var study = await Runner().AblateAsync(Config(), BtConfig());

        Assert.Empty(study.Earning);
        Assert.Empty(study.Candidates);
        Assert.All(study.Ranked, a => Assert.Equal(AblationVerdict.InsufficientEvidence, a.Verdict));
    }

    // ── The config plumbing the studies depend on ─────────────────

    [Fact]
    public void DisablingFiltersReachesEveryBasketEntry()
    {
        var cfg = Config();
        cfg.MapBasketEntries(e => e.Config.UseVwap = true);
        Assert.All(cfg.ToSetupConfigs(), s => Assert.True(s.UseVwap));

        ValidationRunner.DisableAllFilters(cfg);
        Assert.All(cfg.ToSetupConfigs(), s => Assert.False(s.UseVwap));
        Assert.All(cfg.ToSetupConfigs(), s => Assert.False(s.UseEmaFilter));
        Assert.Equal(0m, cfg.AtrFilterPct);
    }

    [Fact]
    public void AVariantDoesNotMutateTheConfigItWasClonedFrom()
    {
        var original = Config();
        var clone    = original.Clone();
        ValidationRunner.DisableAllFilters(clone);
        clone.MapBasketEntries(e => e.Config.UseVwap = true);

        Assert.All(original.ToSetupConfigs(), s => Assert.False(s.UseVwap));
        Assert.All(clone.ToSetupConfigs(),    s => Assert.True(s.UseVwap));
    }

    [Fact]
    public void OrbDurationVariantsMoveOnlyTheEnd()
    {
        var cfg = Config();
        var variants = ValidationRunner.OrbDurations(new TimeOnly(9, 30), 5, 60).ToList();

        variants[0].Apply(cfg);
        Assert.Equal(new TimeOnly(9, 30), cfg.OrbStart);
        Assert.Equal(new TimeOnly(9, 35), cfg.OrbEnd);

        variants[1].Apply(cfg);
        Assert.Equal(new TimeOnly(9, 30),  cfg.OrbStart);
        Assert.Equal(new TimeOnly(10, 30), cfg.OrbEnd);
    }
}
