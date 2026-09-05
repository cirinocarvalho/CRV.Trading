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

namespace CRV.Web.Pages.Settings;

public class BacktestSettingsModel : PageModel
{
    private readonly BacktestRunnerService _runner;
    private readonly StrategyConfigService _cfgSvc;
    private readonly TradingDbContext      _db;
    private readonly ILogger               _log;
    private readonly string                _contentRootPath;

    [BindProperty]
    public BacktestConfig Config { get; set; } = new();

    public BacktestResult? Result    { get; private set; }
    public bool            IsRunning { get; private set; }

    public BacktestSettingsModel(
        BacktestRunnerService runner,
        StrategyConfigService cfgSvc,
        TradingDbContext db,
        IWebHostEnvironment env,
        ILogger<BacktestSettingsModel> log)
    {
        _runner          = runner;
        _cfgSvc          = cfgSvc;
        _db              = db;
        _contentRootPath = env.ContentRootPath;
        _log             = log;
    }

    public async Task OnGetAsync()
    {
        Config = new BacktestConfig
        {
            From               = DateTime.UtcNow.AddDays(-7).Date,
            To                 = DateTime.UtcNow.Date,
            DataSource         = "CSV",
            FillMode           = FillMode.WithSlippage,
            SlippageTicks      = 1,
            StopSlippageTicks  = 4,
            CsvPath            = TempData["bt_CsvPath"] as string ?? "",
            ExecutionTFMinutes = _cfgSvc.Current.ExecutionTFMinutes,  // seed from live config
        };

        IsRunning = _runner.IsRunning;

        var latest = await _db.BacktestRuns
            .OrderByDescending(r => r.RunAt)
            .FirstOrDefaultAsync();

        if (latest?.ResultJson != null)
            try { Result = JsonSerializer.Deserialize<BacktestResult>(latest.ResultJson); }
            catch { /* ignore stale */ }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        if (_runner.IsRunning)
        {
            ModelState.AddModelError("", "A backtest is already running.");
            return Page();
        }

        if (string.Equals(Config.DataSource, "CSV", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveCsvPath(Config.CsvPath, out var resolvedPath, out var error))
            {
                ModelState.AddModelError("Config.CsvPath", error ?? "Invalid CSV path.");
                return Page();
            }
            Config.CsvPath = resolvedPath;
        }

        // Persist CSV path across PRG so the field repopulates
        TempData["bt_CsvPath"] = Config.CsvPath;

        // Broker data sources (Schwab, TradeStation) start from the overnight session and
        // already arrive at the execution TF — ATR warms up naturally in the pre-market hours.
        // Applying WarmupBars to broker data can skip the entire morning session on day 1
        // for large TFs (e.g. 60-min: only ~9 bars before the ORB window).
        if (!string.Equals(Config.DataSource, "CSV", StringComparison.OrdinalIgnoreCase))
            Config.WarmupBars = 0;

        // When To is midnight (date-only), the REST API would exclude the entire RTH session
        // for that day.  Extend To to 23:59:59 UTC so the full trading day is covered.
        // This is safe for multi-day ranges too — the extra seconds are harmless.
        if (Config.To.TimeOfDay == TimeSpan.Zero)
            Config.To = Config.To.Date.AddDays(1).AddSeconds(-1);

        // Force-reload config from DB so direct sqlite3 edits are picked up
        _cfgSvc.Reload();
        var cfg   = _cfgSvc.Current.Clone();
        // Let the backtest form override the execution TF independently of live settings
        cfg.ExecutionTFMinutes = Config.ExecutionTFMinutes;

        // Auto-derive commission from the execution broker (ExecBroker if set, else Broker).
        // When running Mock (paper), commission is $0; real brokers get their actual rates.
        var commBroker = cfg.ExecBroker ?? cfg.Broker;
        cfg.CommissionPerSide = FuturesSymbol.DefaultCommission(commBroker, cfg.Ticker);

        var btCfg = Config;

        try
        {
            await RunAndSaveAsync(cfg, btCfg);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Backtest run failed.");
            // Show full stack trace so divide-by-zero and similar bugs can be pinpointed
            var inner = ex.InnerException != null ? $"\nInner: {ex.InnerException.Message}" : "";
            ModelState.AddModelError("", $"Backtest failed: {ex.Message}{inner}\n{ex.StackTrace}");
            return Page();
        }

        return RedirectToPage();
    }

    private bool TryResolveCsvPath(string? inputPath, out string resolvedPath, out string? error)
    {
        resolvedPath = "";
        error        = null;

        var raw = (inputPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "CSV File Path is required when Source is CSV.";
            return false;
        }

        var candidates = new[]
        {
            raw,
            Path.GetFullPath(raw, Directory.GetCurrentDirectory()),
            Path.GetFullPath(raw, _contentRootPath),
            Path.GetFullPath(Path.Combine(_contentRootPath, "CRV.Web", raw)),
        };

        var match = candidates.FirstOrDefault(p => System.IO.File.Exists(p));
        if (match == null)
        {
            error = $"CSV file not found. Tried: {string.Join(" | ", candidates.Distinct())}";
            return false;
        }

        resolvedPath = match;
        return true;
    }

    private async Task RunAndSaveAsync(StrategyConfig cfg, BacktestConfig btCfg)
    {
        var result = await _runner.RunAsync(cfg, btCfg);
        string sessId = $"bt-{DateTime.UtcNow:yyyyMMddHHmmss}";

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
    }
}

