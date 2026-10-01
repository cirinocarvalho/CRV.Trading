using CRV.Backtest.DataLoaders;
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Live;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CRV.Web.Pages.Review;

/// <summary>
/// One results screen for every source of trades: live fills, paper (Mock) fills, and
/// backtest runs. All three are rendered from a <see cref="BacktestResult"/>.
/// </summary>
public class ResultsModel : PageModel
{
    private readonly TradingDbContext      _db;
    private readonly StrategyConfigService _cfgSvc;
    private readonly BacktestRunnerService _runner;
    private readonly ILogger<ResultsModel> _log;
    private readonly string                _contentRootPath;

    public ResultsModel(TradingDbContext db, StrategyConfigService cfgSvc, BacktestRunnerService runner,
                        IWebHostEnvironment env, ILogger<ResultsModel> log)
    {
        _db = db; _cfgSvc = cfgSvc; _runner = runner; _log = log;
        _contentRootPath = env.ContentRootPath;
    }

    /// <summary>"live", "paper" or "backtest".</summary>
    [BindProperty(SupportsGet = true)] public string? Source { get; set; } = "live";
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To   { get; set; }
    [BindProperty(SupportsGet = true)] public int? Run { get; set; }

    /// <summary>The new-backtest form.</summary>
    [BindProperty] public BacktestConfig Config { get; set; } = new();

    public BacktestResult?      Result      { get; private set; }
    public List<BacktestRunRow> RecentRuns  { get; private set; } = new();
    public BacktestRunRow?      SelectedRun { get; private set; }
    public string?              Error       { get; private set; }
    public bool                 OpenForm    { get; private set; }
    public bool                 RunnerBusy  => _runner.IsRunning;
    public int                  SessionStartHour => _cfgSvc.Current.SessionStartHour;

    public bool IsBacktest => Source == "backtest";
    public string SourceName => Source switch { "paper" => "Paper", "backtest" => "Backtest", _ => "Live" };

    public async Task OnGetAsync()
    {
        Normalise();
        SeedForm();
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostRunBacktestAsync()
    {
        Source = "backtest";
        Run = null;

        string? problem = null;
        if (!ModelState.IsValid)
        {
            foreach (var (key, entry) in ModelState)
                foreach (var err in entry.Errors)
                    _log.LogWarning("Backtest form invalid — {Key}: {Msg}", key, err.ErrorMessage);
            problem = "Some of the backtest settings aren't valid. Check the dates and numbers.";
        }
        else if (_runner.IsRunning)
            problem = "A backtest is already running. Wait for it to finish, then try again.";
        else if (string.Equals(Config.DataSource, "CSV", StringComparison.OrdinalIgnoreCase))
        {
            if (TryResolveCsvPath(Config.CsvPath, out var resolved, out var csvError)) Config.CsvPath = resolved;
            else problem = csvError;
        }

        if (problem == null)
        {
            TempData["bt_CsvPath"] = Config.CsvPath;

            // Broker data starts from the overnight session at the execution TF, so ATR warms
            // up on its own; warmup bars there can skip day 1's morning on large TFs.
            if (!string.Equals(Config.DataSource, "CSV", StringComparison.OrdinalIgnoreCase))
                Config.WarmupBars = 0;

            // A date-only To would exclude that day's session from REST requests.
            if (Config.To.TimeOfDay == TimeSpan.Zero)
                Config.To = Config.To.Date.AddDays(1).AddSeconds(-1);

            _cfgSvc.Reload();   // pick up direct database edits
            var cfg = _cfgSvc.Current.Clone();
            cfg.ExecutionTFMinutes = Config.ExecutionTFMinutes;
            cfg.CommissionPerSide  = FuturesSymbol.DefaultCommission(cfg.ExecBroker ?? cfg.Broker, cfg.Ticker);

            try
            {
                var id = await RunAndSaveAsync(cfg, Config);
                return RedirectToPage(new { source = "backtest", run = id });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Backtest run failed.");
                // Data-loading failures carry a message written for the user; anything else is a
                // bug whose details belong in the log, not on the page.
                problem = ex is BarLoadException
                    ? $"Backtest failed: {ex.Message}"
                    : "Backtest failed because of an unexpected error. The details are in the app log.";
            }
        }

        Error = problem;
        OpenForm = true;
        await LoadAsync(prefillForm: false);
        return Page();
    }

    private void Normalise()
    {
        Source = (Source ?? "").ToLowerInvariant() switch
        {
            "paper" or "mock" => "paper",
            "backtest"        => "backtest",
            _                 => "live",
        };
    }

    private void SeedForm()
    {
        Config = new BacktestConfig
        {
            From               = DateTime.UtcNow.AddDays(-7).Date,
            To                 = DateTime.UtcNow.Date,
            DataSource         = "CSV",
            FillMode           = FillMode.WithSlippage,
            SlippageTicks      = 1,
            StopSlippageTicks  = 4,
            CsvPath            = TempData.Peek("bt_CsvPath") as string ?? "",
            ExecutionTFMinutes = _cfgSvc.Current.ExecutionTFMinutes,
        };
    }

    private async Task LoadAsync(bool prefillForm = true)
    {
        if (IsBacktest) await LoadBacktestAsync(prefillForm);
        else            await LoadTradesAsync(Source == "paper" ? "mock" : "live");
    }

    private async Task LoadTradesAsync(string dbSource)
    {
        var cfg = _cfgSvc.Current;
        var hour = cfg.SessionStartHour;

        if (From == null || To == null)
        {
            // Default to the most recent trading day that has trades for this source.
            var last = await _db.Trades.Where(t => t.Source == dbSource)
                .OrderByDescending(t => t.EnteredAt).Select(t => (DateTime?)t.EnteredAt).FirstOrDefaultAsync();
            var day = TradingDayRange.Of(last ?? DateTime.UtcNow, hour, ViewFmt.Et);
            From ??= day;
            To   ??= From;
        }
        if (To < From) (From, To) = (To, From);

        var (fromUtc, toUtc) = TradingDayRange.ToUtc(From!.Value, To!.Value, hour, ViewFmt.Et);
        var trades = await _db.Trades
            .Where(t => t.Source == dbSource && t.EnteredAt >= fromUtc && t.EnteredAt < toUtc)
            .OrderBy(t => t.EnteredAt)
            .ToListAsync();

        var btCfg = new BacktestConfig { From = From.Value.ToDateTime(TimeOnly.MinValue), To = To.Value.ToDateTime(TimeOnly.MinValue) };
        Result = BacktestResultCalculator.Calculate(trades, cfg, btCfg);
    }

    private async Task LoadBacktestAsync(bool prefillForm)
    {
        RecentRuns = await _db.BacktestRuns
            .OrderByDescending(r => r.RunAt)
            .Take(20)
            .Select(r => new BacktestRunRow
            {
                Id = r.Id, RunAt = r.RunAt, From = r.From, To = r.To, DataSource = r.DataSource,
                FillMode = r.FillMode, TotalTrades = r.TotalTrades, NetPnl = r.NetPnl, Ticker = r.Ticker,
            })
            .ToListAsync();

        var id = Run ?? RecentRuns.FirstOrDefault()?.Id;
        if (id == null) return;

        SelectedRun = await _db.BacktestRuns.FirstOrDefaultAsync(r => r.Id == id);
        if (SelectedRun?.ResultJson == null) return;

        try
        {
            Result = JsonSerializer.Deserialize<BacktestResult>(SelectedRun.ResultJson);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Backtest run {Id} has a result this version can't read.", id);
            Error = "This backtest was saved by an older version and can't be shown. Run it again.";
            return;
        }

        // Prefill the form from the run being shown, so "run it again with one change" is easy.
        if (prefillForm && Result?.BtConfig is { } prev)
        {
            Config.DataSource         = prev.DataSource;
            Config.From               = prev.From.Date;
            Config.To                 = prev.To.Date;
            Config.ExecutionTFMinutes = prev.ExecutionTFMinutes;
            Config.FillMode           = prev.FillMode;
            Config.SlippageTicks      = prev.SlippageTicks;
            Config.StopSlippageTicks  = prev.StopSlippageTicks;
            Config.WarmupBars         = prev.WarmupBars;
            Config.BacktestSession    = prev.BacktestSession;
            if (!string.IsNullOrEmpty(prev.CsvPath)) Config.CsvPath = prev.CsvPath;
        }
    }

    private async Task<int> RunAndSaveAsync(StrategyConfig cfg, BacktestConfig btCfg)
    {
        var result = await _runner.RunAsync(cfg, btCfg);
        var row = new BacktestRunRow
        {
            Ticker       = cfg.Ticker,
            ConfigName   = cfg.Name,
            From         = btCfg.From,
            To           = btCfg.To,
            DataSource   = btCfg.DataSource,
            FillMode     = btCfg.FillMode.ToString(),
            TotalTrades  = result.Total.TotalTrades,
            NetPnl       = result.Total.NetPnl,
            WinRate      = result.Total.WinRate,
            ProfitFactor = result.Total.ProfitFactor,
            MaxDrawdown  = result.Total.MaxDrawdown,
            RunAt        = DateTime.UtcNow,
            ResultJson   = JsonSerializer.Serialize(result),
        };
        _db.BacktestRuns.Add(row);
        await _db.SaveChangesAsync();
        return row.Id;
    }

    private bool TryResolveCsvPath(string? inputPath, out string resolvedPath, out string? error)
    {
        resolvedPath = "";
        error        = null;

        var raw = (inputPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Enter a CSV file path when the data source is CSV.";
            return false;
        }

        var candidates = new[]
        {
            raw,
            Path.GetFullPath(raw, Directory.GetCurrentDirectory()),
            Path.GetFullPath(raw, _contentRootPath),
            Path.GetFullPath(Path.Combine(_contentRootPath, "CRV.Web", raw)),
        };

        var match = candidates.FirstOrDefault(System.IO.File.Exists);
        if (match == null)
        {
            error = $"CSV file not found. Tried: {string.Join(" | ", candidates.Distinct())}";
            return false;
        }

        resolvedPath = match;
        return true;
    }
}
