using CRV.Backtest.DataLoaders;
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers.Schwab;
using Microsoft.Extensions.Logging;

namespace CRV.Web.Services;

/// <summary>
/// Singleton service that runs backtests on demand.
/// Exposes IsRunning so the UI can show a spinner while running.
/// </summary>
public class BacktestRunnerService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger          _log;
    private readonly BarSnapshotStore _snapshots;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Key of the snapshot the most recent run read or wrote. Surfaced so a result
    /// can say which bars produced it — a run you cannot re-feed is not an experiment.
    /// </summary>
    public string? LastSnapshotKey { get; private set; }

    /// <summary>True when the last run replayed a stored snapshot rather than refetching.</summary>
    public bool LastRunWasReplay { get; private set; }

    public BacktestRunnerService(IServiceProvider sp, ILogger<BacktestRunnerService> log,
        BarSnapshotStore snapshots)
    {
        _sp        = sp;
        _log       = log;
        _snapshots = snapshots;
    }

    public async Task<BacktestResult> RunAsync(
        StrategyConfig cfg, BacktestConfig btCfg,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Backtest already running.");

        IsRunning = true;
        try
        {
            using var scope = _sp.CreateScope();

            // Collect distinct tickers from enabled setup configs
            var setupTickers = cfg.ToSetupConfigs()
                .Where(s => s.Enabled)
                .Select(s => s.Ticker)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var log = scope.ServiceProvider.GetRequiredService<ILogger<BacktestEngine>>();
            var engine = new BacktestEngine(cfg, btCfg, log);

            if (btCfg.DataSource == "CSV")
            {
                // CSV only has one ticker's data — use single-stream overload.
                // Setups on other tickers won't receive bars and won't trade (no ghost trades).
                var bars = LoadCsvBars(cfg, btCfg, scope);
                var loggedBars = LogBarsAsync(bars, cfg.Ticker, btCfg, ct);
                return await engine.RunAsync(loggedBars, ct);
            }
            else
            {
                // API sources: fetch bars per distinct ticker, merge chronologically.
                //
                // The merged stream is snapshotted to disk under a hash of the run's
                // inputs, and a later run with the same inputs replays that file instead
                // of refetching. Refetching every time meant each run measured a slightly
                // different market: runs 920-922 shared a configuration and disagreed by
                // 24.6% on net. The snapshot is taken on the raw merged stream, before the
                // anomaly filter, so the filter itself stays re-runnable against fixed data.
                var key = BarSnapshotStore.KeyFor(btCfg, setupTickers);
                LastSnapshotKey  = key;
                LastRunWasReplay = _snapshots.Has(key);

                if (LastRunWasReplay)
                    _log.LogInformation("Backtest replaying bar snapshot {Key} from {Path}.",
                        key, _snapshots.Describe(key));
                else
                    _log.LogInformation("Backtest capturing bar snapshot {Key} to {Path}.",
                        key, _snapshots.Describe(key));

                var taggedBars = _snapshots.Has(key)
                    ? _snapshots.Replay(key, ct)
                    : _snapshots.Capture(key,
                        await LoadMultiTickerBarsAsync(cfg, btCfg, setupTickers, scope, ct), ct);

                var loggedBars = LogTaggedBarsAsync(taggedBars, btCfg, ct);
                return await engine.RunAsync(loggedBars, ct);
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Loads bars for multiple tickers from the API and merges them chronologically
    /// into a single tagged stream for multi-ticker backtesting.
    /// </summary>
    private async Task<IAsyncEnumerable<(string Ticker, Bar Bar)>> LoadMultiTickerBarsAsync(
        StrategyConfig cfg, BacktestConfig btCfg, List<string> tickers,
        IServiceScope scope, CancellationToken ct)
    {
        var tickerStreams = new Dictionary<string, IAsyncEnumerable<Bar>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ticker in tickers)
        {
            // Create a temporary cfg clone pointing to each ticker for bar loading
            var bars = btCfg.DataSource switch
            {
                "Schwab"       => await LoadBarsForTickerAsync(ticker, btCfg, scope, "Schwab"),
                "TradeStation" => await LoadBarsForTickerAsync(ticker, btCfg, scope, "TradeStation"),
                "Tradovate"    => await LoadBarsForTickerAsync(ticker, btCfg, scope, "Tradovate"),
                _              => throw new InvalidOperationException($"Unknown data source: {btCfg.DataSource}")
            };
            tickerStreams[ticker] = bars;
        }

        return MergeChronologicallyAsync(tickerStreams, ct);
    }

    /// <summary>
    /// Fetches bars for a single ticker from the configured API source,
    /// handling contract roll splits.
    /// </summary>
    private async Task<IAsyncEnumerable<Bar>> LoadBarsForTickerAsync(
        string ticker, BacktestConfig btCfg, IServiceScope scope, string source)
    {
        var root = FuturesSymbol.RootSymbol(ticker);
        var segments = ContractRollCalendar.SplitByContract(root, btCfg.From, btCfg.To);

        return source switch
        {
            "Schwab" => ConcatSegments(segments, (t, from, to) =>
            {
                var auth   = scope.ServiceProvider.GetRequiredService<SchwabAuthService>();
                var log    = scope.ServiceProvider.GetRequiredService<ILogger<SchwabHistoricalLoader>>();
                var hf     = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                var loader = new SchwabHistoricalLoader(auth.GetAccessTokenAsync().Result, log, auth.ApiBaseUrl, hf);
                return loader.LoadAsync(FuturesSymbol.ToSchwab(t), 1, from, to);
            }),
            "TradeStation" => ConcatSegments(segments, (t, from, to) =>
            {
                var auth   = scope.ServiceProvider.GetRequiredService<CRV.Live.Brokers.TradeStation.TradeStationAuthService>();
                var log    = scope.ServiceProvider.GetRequiredService<ILogger<TradeStationHistoricalLoader>>();
                var hf     = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                var loader = new TradeStationHistoricalLoader(auth.GetAccessTokenAsync().Result, log, auth.ApiBaseUrl, hf);
                return loader.LoadAsync(FuturesSymbol.ToTradeStation(t), 1, from, to);
            }),
            "Tradovate" => ConcatSegments(segments, (t, from, to) =>
            {
                var auth   = scope.ServiceProvider.GetRequiredService<CRV.Live.Brokers.Tradovate.TradovateAuthService>();
                var log    = scope.ServiceProvider.GetRequiredService<ILogger<TradovateHistoricalLoader>>();
                var loader = new TradovateHistoricalLoader(auth.GetMdAccessTokenAsync().Result, log, auth.MdWssUrl);
                return loader.LoadAsync(FuturesSymbol.ToTradovate(t), 1, from, to);
            }),
            _ => throw new InvalidOperationException($"Unknown source: {source}")
        };
    }

    /// <summary>
    /// Merges multiple per-ticker bar streams into a single chronologically-ordered
    /// tagged stream using a sorted merge (priority queue).
    /// </summary>
    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> MergeChronologicallyAsync(
        Dictionary<string, IAsyncEnumerable<Bar>> streams,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Materialize each stream into a list first (bars are already in memory for API sources)
        // then merge-sort them by time. This avoids complex async enumerator juggling.
        var allBars = new List<(string Ticker, Bar Bar)>();

        foreach (var (ticker, stream) in streams)
        {
            await foreach (var bar in stream.WithCancellation(ct))
                allBars.Add((ticker, bar));
        }

        // Sort by bar time, then by ticker for deterministic ordering
        allBars.Sort((a, b) =>
        {
            int cmp = a.Bar.Time.CompareTo(b.Bar.Time);
            return cmp != 0 ? cmp : string.Compare(a.Ticker, b.Ticker, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var item in allBars)
            yield return item;
    }

    /// <summary>
    /// Wraps a bar stream to log the first and last bar timestamps/prices and total count.
    /// Detects price jumps >10% between consecutive bars which indicate stale/wrong contract data.
    /// </summary>
    private async IAsyncEnumerable<Bar> LogBarsAsync(
        IAsyncEnumerable<Bar> bars, string ticker, BacktestConfig btCfg,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Bar? first = null;
        Bar? last = null;
        int count = 0;
        int skipped = 0;

        await foreach (var bar in bars.WithCancellation(ct))
        {
            first ??= bar;

            if (last != null && last.Close > 0 && bar.Open > 0 && last.Time.Date == bar.Time.Date)
            {
                decimal pctChange = Math.Abs(bar.Open - last.Close) / last.Close;
                if (pctChange > 0.10m)
                {
                    _log.LogWarning(
                        "Data anomaly [{Ticker}]: intra-session price jump {Pct:P1} between {T1:u} C={C1:F2} and {T2:u} O={O2:F2}",
                        ticker, pctChange, last.Time, last.Close, bar.Time, bar.Open);
                    skipped++;
                    continue;
                }
            }

            last = bar;
            count++;
            yield return bar;
        }

        if (first != null)
            _log.LogInformation(
                "Backtest data [{Source}/{Ticker}]: {Count} bars ({Skipped} skipped), first={FirstTime:u} last={LastTime:u}",
                btCfg.DataSource, ticker, count, skipped, first.Time, last!.Time);
        else
            _log.LogWarning("Backtest data [{Source}/{Ticker}]: 0 bars returned!", btCfg.DataSource, ticker);
    }

    /// <summary>
    /// Wraps a tagged bar stream with per-ticker logging and anomaly detection.
    /// </summary>
    private async IAsyncEnumerable<(string Ticker, Bar Bar)> LogTaggedBarsAsync(
        IAsyncEnumerable<(string Ticker, Bar Bar)> bars, BacktestConfig btCfg,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var lastByTicker = new Dictionary<string, Bar>(StringComparer.OrdinalIgnoreCase);
        var countByTicker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var droppedByTicker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await foreach (var (ticker, bar) in bars.WithCancellation(ct))
        {
            // Per-ticker anomaly detection
            if (lastByTicker.TryGetValue(ticker, out var last) &&
                last.Close > 0 && bar.Open > 0 && last.Time.Date == bar.Time.Date)
            {
                decimal pctChange = Math.Abs(bar.Open - last.Close) / last.Close;
                if (pctChange > 0.10m)
                {
                    _log.LogWarning(
                        "Data anomaly [{Ticker}]: intra-session price jump {Pct:P1} between {T1:u} and {T2:u}",
                        ticker, pctChange, last.Time, bar.Time);
                    droppedByTicker[ticker] = droppedByTicker.GetValueOrDefault(ticker) + 1;
                    continue; // skip anomalous bars
                }
            }

            lastByTicker[ticker] = bar;
            countByTicker[ticker] = countByTicker.GetValueOrDefault(ticker) + 1;
            yield return (ticker, bar);
        }

        foreach (var (ticker, count) in countByTicker)
            _log.LogInformation("Backtest data [{Source}/{Ticker}]: {Count} bars loaded.", btCfg.DataSource, ticker, count);

        // Dropped bars change the result, so say so in one line rather than leaving
        // the reader to total up scattered per-bar warnings.
        foreach (var (ticker, dropped) in droppedByTicker)
            _log.LogWarning(
                "Backtest data [{Source}/{Ticker}]: {Dropped} of {Total} bars dropped as anomalous — " +
                "this run does not price the full series.",
                btCfg.DataSource, ticker, dropped, dropped + countByTicker.GetValueOrDefault(ticker));
    }

    private IAsyncEnumerable<Bar> LoadCsvBars(
        StrategyConfig cfg, BacktestConfig btCfg, IServiceScope scope)
    {
        if (string.IsNullOrEmpty(btCfg.CsvPath))
            throw new InvalidOperationException("CSV path not set.");

        var loader = scope.ServiceProvider.GetRequiredService<CsvBarLoader>();
        return loader.LoadAsync(btCfg.CsvPath, btCfg.From, btCfg.To);
    }

    /// <summary>
    /// Concatenates bar streams from multiple contract segments into a single stream.
    /// Each segment uses the correct ticker for its date range (auto-roll).
    /// </summary>
    private async IAsyncEnumerable<Bar> ConcatSegments(
        List<(string Ticker, DateTime From, DateTime To)> segments,
        Func<string, DateTime, DateTime, IAsyncEnumerable<Bar>> loadFn,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Bar? prevSegmentLast = null;
        foreach (var (ticker, from, to) in segments)
        {
            var expected = ContractRollCalendar.ActiveContract(FuturesSymbol.RootSymbol(ticker), from);
            if (!string.Equals(ticker, expected, StringComparison.OrdinalIgnoreCase))
                _log.LogWarning(
                    "Contract mismatch: segment uses {Ticker} but active contract on {Date:d} is {Expected}",
                    ticker, from, expected);

            await foreach (var bar in loadFn(ticker, from, to).WithCancellation(ct))
            {
                if (prevSegmentLast != null && prevSegmentLast.Close > 0 && bar.Open > 0)
                {
                    decimal pctChange = Math.Abs(bar.Open - prevSegmentLast.Close) / prevSegmentLast.Close;
                    if (pctChange > 0.10m)
                        _log.LogWarning(
                            "Cross-segment price jump {Pct:P1}: {Prev:F2} → {Cur:F2} at segment boundary",
                            pctChange, prevSegmentLast.Close, bar.Open);
                }
                prevSegmentLast = bar;
                yield return bar;
            }
        }
    }
}
