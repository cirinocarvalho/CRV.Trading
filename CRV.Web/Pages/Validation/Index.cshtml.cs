using CRV.Backtest.DataLoaders;
using CRV.Backtest.Engine;
using CRV.Backtest.Experiments;
using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Core.Risk;
using CRV.Core.Statistics;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CRV.Web.Pages.Validation;

/// <summary>
/// The quant-validation studies: does the result survive out of sample, is the ORB
/// duration a stable choice, and does any filter beat the bare break.
/// <para>
/// Every study replays the bar snapshot a backtest already captured. If none exists
/// the page says so rather than fetching, because a sweep across bars that differ
/// per variant measures the data instead of the parameter.
/// </para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly TradingDbContext   _db;
    private readonly ValidationRunner   _runner;
    private readonly BarSnapshotStore   _snapshots;
    private readonly StrategyConfigService _cfgSvc;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(ValidationRunner runner, BarSnapshotStore snapshots,
        StrategyConfigService cfgSvc, TradingDbContext db, ILogger<IndexModel> log)
    {
        _db        = db;
        _runner    = runner;
        _snapshots = snapshots;
        _cfgSvc    = cfgSvc;
        _log       = log;
    }

    [BindProperty(SupportsGet = true)] public string FromStr { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string ToStr   { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Study   { get; set; } = "";

    public RiskProfile?      Risk     { get; private set; }
    public SampleSplit?      Split    { get; private set; }
    public ParameterSurface? Surface  { get; private set; }
    public AblationStudy?    Ablation { get; private set; }

    public string? Error         { get; private set; }
    public bool    HasSnapshot   { get; private set; }
    public string  SnapshotKey   { get; private set; } = "";
    public int     EnabledSetups { get; private set; }

    /// <summary>Opening-range durations to sweep — the 30-minute default plus its untested neighbours.</summary>
    public static readonly int[] SweptDurations = { 5, 15, 30, 45, 60, 90 };

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cfg   = _cfgSvc.Current;
        var btCfg = BuildBtConfig();

        var tickers = cfg.ToSetupConfigs().Where(s => s.Enabled)
            .Select(s => s.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        EnabledSetups = tickers.Count;
        SnapshotKey   = BarSnapshotStore.KeyFor(btCfg, tickers);
        HasSnapshot   = _snapshots.Has(SnapshotKey);

        // Position sizing is read off the live trade record, not the bar snapshot,
        // so it answers without needing a study to have been run.
        Risk = RiskProfile.FromTrades(
            _db.Trades.Where(t => t.Source == "live").AsEnumerable(), PointValueFor);

        // The basket first, since it is what the engine trades on, then the symbol
        // table. The book holds instruments no longer in the basket — MYM has eight
        // trades and no entry — and pricing those at the global point value put MYM
        // at $114 a contract when it is $0.50 a point.
        decimal PointValueFor(string ticker)
        {
            decimal fromBasket = cfg.PointValueFor(ticker);
            if (fromBasket != cfg.PointValue) return fromBasket;

            decimal known = CRV.Live.FuturesSymbol.PointValue(ticker);
            return known > 0 ? known : fromBasket;
        }

        if (string.IsNullOrEmpty(Study)) return;

        if (!HasSnapshot)
        {
            Error = "No bar snapshot for this date range and basket. Run a backtest over the same " +
                    "window first — every variant has to see identical bars, or the study measures " +
                    "the data rather than the parameter.";
            return;
        }

        try
        {
            switch (Study)
            {
                case "split":
                    Split = await _runner.SplitAsync(cfg, btCfg,
                        inSampleFraction: 0.70, embargo: TimeSpan.FromDays(1), ct: ct);
                    break;

                case "sweep":
                    Surface = await _runner.SweepAsync(cfg, btCfg,
                        ValidationRunner.OrbDurations(cfg.OrbStart, SweptDurations), ct);
                    break;

                case "ablation":
                    Ablation = await _runner.AblateAsync(cfg, btCfg, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Validation study {Study} failed", Study);
            Error = ex.Message;
        }
    }

    private BacktestConfig BuildBtConfig()
    {
        var from = DateTime.TryParse(FromStr, out var f)
            ? DateTime.SpecifyKind(f.Date, DateTimeKind.Utc)
            : DateTime.UtcNow.Date.AddMonths(-6);
        var to = DateTime.TryParse(ToStr, out var t)
            ? DateTime.SpecifyKind(t.Date, DateTimeKind.Utc)
            : DateTime.UtcNow.Date;

        FromStr = from.ToString("yyyy-MM-dd");
        ToStr   = to.ToString("yyyy-MM-dd");

        return new BacktestConfig
        {
            From = from, To = to,
            DataSource = _cfgSvc.Current.Broker,
            ExecutionTFMinutes = _cfgSvc.Current.ExecutionTFMinutes,
            BacktestSession = "All",
        };
    }

    public static string VerdictClass(EdgeVerdict v) => v switch
    {
        EdgeVerdict.EdgePresent          => "text-success",
        EdgeVerdict.NoMeasurableEdge     => "text-warning",
        _                                => "text-muted",
    };

    public static string VerdictClass(AblationVerdict v) => v switch
    {
        AblationVerdict.Earns => "text-success",
        AblationVerdict.Harms => "text-danger",
        AblationVerdict.NoMeasurableEffect => "text-warning",
        _ => "text-muted",
    };

    public static string VerdictLabel(EdgeVerdict v) => v switch
    {
        EdgeVerdict.EdgePresent      => "EDGE",
        EdgeVerdict.NoMeasurableEdge => "NO EDGE",
        _                            => "INSUFFICIENT",
    };
}
