using CRV.Backtest.DataLoaders;
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Models;
using CRV.Core.Statistics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CRV.Backtest.Experiments;

/// <summary>
/// Runs the same bars through varied configurations and reads the results
/// statistically rather than competitively.
/// <para>
/// This is only worth doing because a run now reproduces itself. Sweeping a
/// parameter across data that changes between runs measures the data, not the
/// parameter — which is why every variant here replays one snapshot rather than
/// refetching per run.
/// </para>
/// </summary>
public sealed class ValidationRunner
{
    private readonly BarSnapshotStore _snapshots;
    private readonly ILogger _log;

    public ValidationRunner(BarSnapshotStore snapshots, ILogger<ValidationRunner> log)
    {
        _snapshots = snapshots;
        _log       = log;
    }

    /// <summary>One variant's configuration change, and the label it reports under.</summary>
    public sealed record Variant(string Label, decimal Value, Action<StrategyConfig> Apply);

    // ── P1-1: in-sample / out-of-sample ───────────────────────────

    /// <summary>
    /// Runs one configuration and splits its trades chronologically. Nothing here
    /// chooses a parameter — that is the point. Once a parameter has been picked
    /// against the out-of-sample trades, they are in-sample and this number is spent.
    /// </summary>
    public async Task<SampleSplit> SplitAsync(StrategyConfig cfg, BacktestConfig btCfg,
        double inSampleFraction = 0.70, TimeSpan? embargo = null, CancellationToken ct = default)
    {
        var result = await RunAsync(cfg, btCfg, ct);
        var split  = SampleSplit.ByFraction(result.Trades, inSampleFraction, embargo);
        _log.LogInformation("IS/OOS split:\n{Report}", split.Describe());
        return split;
    }

    // ── P1-2: parameter sweep ─────────────────────────────────────

    /// <summary>Runs each variant over identical bars and reads the surface for a stable region.</summary>
    public async Task<ParameterSurface> SweepAsync(StrategyConfig cfg, BacktestConfig btCfg,
        IEnumerable<Variant> variants, CancellationToken ct = default)
    {
        var points = new List<ParameterPoint>();

        foreach (var v in variants)
        {
            ct.ThrowIfCancellationRequested();
            var variantCfg = cfg.Clone();
            v.Apply(variantCfg);

            var result = await RunAsync(variantCfg, btCfg, ct);
            points.Add(new ParameterPoint(v.Label, v.Value,
                EdgeTest.FromSamples(result.Trades.Select(t => t.RMultiple).ToList())));
        }

        var surface = new ParameterSurface(points);
        _log.LogInformation("Parameter sweep:\n{Report}", surface.Describe());
        return surface;
    }

    /// <summary>
    /// The opening-range durations that were never tested. Each keeps the session's
    /// start and moves only the end, so the comparison is of duration alone.
    /// </summary>
    public static IEnumerable<Variant> OrbDurations(TimeOnly start, params int[] minutes) =>
        minutes.Select(m => new Variant($"{m}m", m, cfg =>
        {
            cfg.OrbStart = start;
            cfg.OrbEnd   = start.AddMinutes(m);
        }));

    // ── P1-3: ablation ────────────────────────────────────────────

    /// <summary>
    /// Measures each filter against the same configuration with every filter off.
    /// The baseline is the naked opening-range break.
    /// </summary>
    public async Task<AblationStudy> AblateAsync(StrategyConfig cfg, BacktestConfig btCfg,
        CancellationToken ct = default)
    {
        var bare = cfg.Clone();
        DisableAllFilters(bare);
        var baseline = EdgeTest.FromSamples(
            (await RunAsync(bare, btCfg, ct)).Trades.Select(t => t.RMultiple).ToList());

        var ablations = new List<Ablation>();
        foreach (var (name, enable) in FilterSwitches)
        {
            ct.ThrowIfCancellationRequested();

            // Baseline plus this one filter — so what is measured is that filter's
            // own contribution, not its contribution given whatever else is on.
            var only = cfg.Clone();
            DisableAllFilters(only);
            enable(only);

            var trades = (await RunAsync(only, btCfg, ct)).Trades;
            ablations.Add(new Ablation(baseline,
                EdgeTest.FromSamples(trades.Select(t => t.RMultiple).ToList()), name));
        }

        var study = new AblationStudy(baseline, ablations);
        _log.LogInformation("Ablation study:\n{Report}", study.Describe());
        return study;
    }

    /// <summary>The filters stacked on the raw break, each switchable on its own.</summary>
    public static readonly (string Name, Action<StrategyConfig> Enable)[] FilterSwitches =
    {
        ("vwap",     c => c.MapBasketEntries(e => e.Config.UseVwap = true)),
        ("chop",     c => { c.UseChopFilter = true; c.MapBasketEntries(e => e.Config.BypassChopFilter = false); }),
        ("ema",      c => c.MapBasketEntries(e => e.Config.UseEmaFilter = true)),
        ("atr",      c => c.AtrFilterPct = 0.50m),
        ("orbclose", c => c.MapBasketEntries(e => e.Config.UseOrbClose = true)),
    };

    /// <summary>Strips the configuration back to the raw opening-range break.</summary>
    public static void DisableAllFilters(StrategyConfig c)
    {
        c.UseChopFilter = false;
        c.AtrFilterPct  = 0m;
        c.MapBasketEntries(e =>
        {
            e.Config.UseVwap          = false;
            e.Config.UseEmaFilter     = false;
            e.Config.UseOrbClose      = false;
            e.Config.BypassChopFilter = true;
        });
    }

    /// <summary>
    /// Runs one configuration over the snapshotted bars. The snapshot must already
    /// exist — a validation study is not the place to discover the broker is down
    /// halfway through variant seven.
    /// </summary>
    private async Task<BacktestResult> RunAsync(StrategyConfig cfg, BacktestConfig btCfg,
        CancellationToken ct)
    {
        var tickers = cfg.ToSetupConfigs().Where(s => s.Enabled)
            .Select(s => s.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var key = BarSnapshotStore.KeyFor(btCfg, tickers);
        if (!_snapshots.Has(key))
            throw new BarLoadException(
                $"No bar snapshot for key {key}. Run the backtest once to capture it before validating — " +
                "every variant must see identical bars, or the sweep measures the data rather than the parameter.");

        var engine = new BacktestEngine(cfg, btCfg, NullLogger<BacktestEngine>.Instance);
        return await engine.RunAsync(_snapshots.Replay(key, ct), ct);
    }
}
