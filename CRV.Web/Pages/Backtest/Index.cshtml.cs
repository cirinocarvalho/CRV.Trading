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

namespace CRV.Web.Pages.Backtest;

public class BacktestModel : PageModel
{
    private readonly BacktestRunnerService _runner;
    private readonly StrategyConfigService _cfgSvc;
    private readonly TradingDbContext      _db;
    private readonly ILogger               _log;
    private readonly string           _contentRootPath;

    [BindProperty] public StrategyConfig Config   { get; set; } = new();
    [BindProperty] public BacktestConfig BtConfig { get; set; } = new();

    public BacktestResult? Result    { get; private set; }
    public bool            IsRunning { get; private set; }

    public BacktestModel(
        BacktestRunnerService runner,
        StrategyConfigService cfgSvc,
        TradingDbContext db,
        IWebHostEnvironment env,
        ILogger<BacktestModel> log)
    {
        _runner          = runner;
        _cfgSvc          = cfgSvc;
        _db              = db;
        _contentRootPath = env.ContentRootPath;
        _log             = log;
    }

    public async Task OnGetAsync()
    {
        Config   = _cfgSvc.Current.Clone();
        // Restore form values from TempData after PRG redirect
        if (TempData["bt_ExecutionTFMinutes"] is string tfStr && int.TryParse(tfStr, out var tf))
            Config.ExecutionTFMinutes = tf;
        if (TempData["bt_Ticker"]     is string ticker)  Config.Ticker     = ticker;
        if (TempData["bt_EnableA"]    is string ea)      Config.EnableA    = ea == "true";
        if (TempData["bt_EnableB"]    is string eb)      Config.EnableB    = eb == "true";
        if (TempData["bt_Contracts"]  is string cStr && int.TryParse(cStr, out var c))  Config.Contracts = c;
        if (TempData["bt_Commission"] is string coStr && decimal.TryParse(coStr, out var co)) Config.CommissionPerSide = co;

        BtConfig = new BacktestConfig
        {
            From          = TempData["bt_From"]          is string f && DateTime.TryParse(f, out var fd) ? fd : DateTime.UtcNow.AddMonths(-3),
            To            = TempData["bt_To"]            is string t && DateTime.TryParse(t, out var td) ? td : DateTime.UtcNow,
            DataSource    = TempData["bt_DataSource"]    as string ?? "CSV",
            FillMode      = TempData["bt_FillMode"]      is string fm && Enum.TryParse<FillMode>(fm, out var fme) ? fme : FillMode.AtTouch,
            SlippageTicks = TempData["bt_SlippageTicks"] is string st && int.TryParse(st, out var sti) ? sti : 1,
            CsvPath       = TempData["bt_CsvPath"]       as string ?? "",
            WarmupBars    = TempData["bt_WarmupBars"]    is string wb && int.TryParse(wb, out var wbi) ? wbi : 0,
        };
        IsRunning = _runner.IsRunning;

        var latest = await _db.BacktestRuns
            .OrderByDescending(r => r.RunAt)
            .FirstOrDefaultAsync();

        if (latest?.ResultJson != null)
        {
            try { Result = JsonSerializer.Deserialize<BacktestResult>(latest.ResultJson); }
            catch { /* ignore stale */ }
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        if (_runner.IsRunning)
        {
            ModelState.AddModelError("", "A backtest is already running.");
            return Page();
        }

        if (string.Equals(BtConfig.DataSource, "CSV", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveCsvPath(BtConfig.CsvPath, out var resolvedPath, out var error))
            {
                ModelState.AddModelError("BtConfig.CsvPath", error ?? "Invalid CSV path.");
                return Page();
            }
            BtConfig.CsvPath = resolvedPath;
        }

        // Persist form values across PRG so the form repopulates after redirect
        TempData["bt_From"]               = BtConfig.From.ToString("o");
        TempData["bt_To"]                 = BtConfig.To.ToString("o");
        TempData["bt_DataSource"]         = BtConfig.DataSource;
        TempData["bt_FillMode"]           = BtConfig.FillMode.ToString();
        TempData["bt_SlippageTicks"]      = BtConfig.SlippageTicks.ToString();
        TempData["bt_CsvPath"]            = BtConfig.CsvPath;
        TempData["bt_WarmupBars"]         = BtConfig.WarmupBars.ToString();
        TempData["bt_ExecutionTFMinutes"] = Config.ExecutionTFMinutes.ToString();
        TempData["bt_Ticker"]             = Config.Ticker;
        TempData["bt_EnableA"]            = Config.EnableA ? "true" : "false";
        TempData["bt_EnableB"]            = Config.EnableB ? "true" : "false";
        TempData["bt_Contracts"]          = Config.Contracts.ToString();
        TempData["bt_Commission"]         = Config.CommissionPerSide.ToString();

        // Auto-derive commission from data-source broker + ticker so backtests
        // don't inherit a stale $0 value when the form field isn't explicitly set.
        if (Config.CommissionPerSide == 0m)
        {
            var commBroker = BtConfig.DataSource is "Schwab" or "TradeStation" or "Tradovate"
                ? BtConfig.DataSource
                : Config.Broker;
            Config.CommissionPerSide = FuturesSymbol.DefaultCommission(commBroker, Config.Ticker);
        }

        var cfg   = Config;
        var btCfg = BtConfig;

        try
        {
            await RunAndSaveAsync(cfg, btCfg);
            TempData["bt_success"] = "Backtest complete.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Backtest run failed.");
            ModelState.AddModelError("", $"Backtest failed: {ex.Message}");
            return Page();
        }

        return RedirectToPage();
    }

    private bool TryResolveCsvPath(string? inputPath, out string resolvedPath, out string? error)
    {
        resolvedPath = "";
        error = null;

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
            Path.GetFullPath(Path.Combine(_contentRootPath, "CRV.Web", raw))
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
        string sessionId = $"bt-{DateTime.UtcNow:yyyyMMddHHmmss}";

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
            ResultJson   = JsonSerializer.Serialize(result)
        };
        _db.BacktestRuns.Add(row);

        foreach (var t in result.Trades)
        {
            t.Source    = $"backtest:{sessionId}";
            t.SessionId = sessionId;
            _db.Trades.Add(t);
        }

        await _db.SaveChangesAsync();
    }
}
