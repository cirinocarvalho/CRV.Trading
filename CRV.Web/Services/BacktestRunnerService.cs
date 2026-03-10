using CRV.Backtest.DataLoaders;
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Interfaces;
using CRV.Core.Models;
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

    public bool IsRunning { get; private set; }

    public BacktestRunnerService(IServiceProvider sp, ILogger<BacktestRunnerService> log)
    {
        _sp  = sp;
        _log = log;
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
            var bars = await GetBarsAsync(cfg, btCfg, scope, ct);
            var log = scope.ServiceProvider.GetRequiredService<ILogger<BacktestEngine>>();
            var engine = new BacktestEngine(cfg, btCfg, log);

            return await engine.RunAsync(bars, ct);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static async Task<IAsyncEnumerable<CRV.Core.Models.Bar>> GetBarsAsync(
        StrategyConfig cfg, BacktestConfig btCfg, IServiceScope scope, CancellationToken ct)
    {
        return btCfg.DataSource switch
        {
            "Schwab"       => await LoadSchwabBarsAsync(cfg, btCfg, scope),
            "TradeStation" => await LoadTradeStationBarsAsync(cfg, btCfg, scope),
            "Tradovate"    => await LoadTradovateBarsAsync(cfg, btCfg, scope),
            _              => LoadCsvBars(cfg, btCfg, scope) // default: CSV
        };
    }

    private static IAsyncEnumerable<CRV.Core.Models.Bar> LoadCsvBars(
        StrategyConfig cfg, BacktestConfig btCfg, IServiceScope scope)
    {
        if (string.IsNullOrEmpty(btCfg.CsvPath))
            throw new InvalidOperationException("CSV path not set.");

        var loader = scope.ServiceProvider.GetRequiredService<CsvBarLoader>();
        // Return raw 1-minute bars.  BacktestEngine.RunAsync handles execution-TF aggregation
        // internally and fires four OHLC ticks per 1-min bar for intra-TF entry/exit evaluation.
        return loader.LoadAsync(btCfg.CsvPath, btCfg.From, btCfg.To);
    }

    private static async Task<IAsyncEnumerable<CRV.Core.Models.Bar>> LoadSchwabBarsAsync(
        StrategyConfig cfg, BacktestConfig btCfg, IServiceScope scope)
    {
        var auth   = scope.ServiceProvider.GetRequiredService<CRV.Live.Brokers.Schwab.SchwabAuthService>();
        var token  = await auth.GetAccessTokenAsync();
        var log    = scope.ServiceProvider.GetRequiredService<ILogger<SchwabHistoricalLoader>>();
        var loader = new SchwabHistoricalLoader(token, log, auth.ApiBaseUrl);
        return loader.LoadAsync(cfg.Ticker, cfg.ExecutionTFMinutes, btCfg.From, btCfg.To);
    }

    private static async Task<IAsyncEnumerable<CRV.Core.Models.Bar>> LoadTradeStationBarsAsync(
        StrategyConfig cfg, BacktestConfig btCfg, IServiceScope scope)
    {
        var auth   = scope.ServiceProvider.GetRequiredService<CRV.Live.Brokers.TradeStation.TradeStationAuthService>();
        var token  = await auth.GetAccessTokenAsync();
        var log    = scope.ServiceProvider.GetRequiredService<ILogger<TradeStationHistoricalLoader>>();
        var loader = new TradeStationHistoricalLoader(token, log, auth.ApiBaseUrl);
        return loader.LoadAsync(cfg.Ticker, cfg.ExecutionTFMinutes, btCfg.From, btCfg.To);
    }

    private static async Task<IAsyncEnumerable<CRV.Core.Models.Bar>> LoadTradovateBarsAsync(
        StrategyConfig cfg, BacktestConfig btCfg, IServiceScope scope)
    {
        var auth    = scope.ServiceProvider.GetRequiredService<CRV.Live.Brokers.Tradovate.TradovateAuthService>();
        var mdToken = await auth.GetMdAccessTokenAsync();
        var log     = scope.ServiceProvider.GetRequiredService<ILogger<TradovateHistoricalLoader>>();
        var loader  = new TradovateHistoricalLoader(mdToken, log, auth.MdWssUrl);
        return loader.LoadAsync(cfg.Ticker, cfg.ExecutionTFMinutes, btCfg.From, btCfg.To);
    }
}
