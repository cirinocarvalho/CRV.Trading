namespace CRV.Web.Pages.Trading;

using System.ComponentModel.DataAnnotations;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class ManualModel : PageModel
{
    private readonly StrategyConfigService   _cfgSvc;
    private readonly SchwabAuthService       _schwab;
    private readonly TradeStationAuthService _ts;
    private readonly TradovateAuthService    _tv;
    private readonly IConfiguration          _config;
    private readonly ILogger<ManualModel>    _log;

    // ── Display ──────────────────────────────────────────────────
    public string CurrentBroker     => _cfgSvc.Current.Broker;
    public string CurrentExecBroker => _cfgSvc.Current.EffectiveExecBroker;
    public string CurrentTicker     => _cfgSvc.Current.Ticker;

    // ── Result ───────────────────────────────────────────────────
    public List<string> Errors  { get; } = new();
    public List<string> Placed  { get; } = new();

    // ── Forms ─────────────────────────────────────────────────────
    [BindProperty] public ManualOrder Order { get; set; } = new();
    [BindProperty] public FlatForm    Flat  { get; set; } = new();

    public ManualModel(
        StrategyConfigService cfgSvc,
        SchwabAuthService schwab,
        TradeStationAuthService ts,
        TradovateAuthService tv,
        IConfiguration config,
        ILogger<ManualModel> log)
    {
        _cfgSvc = cfgSvc;
        _schwab = schwab;
        _ts     = ts;
        _tv     = tv;
        _config = config;
        _log    = log;
    }

    public void OnGet()
    {
        Order.Ticker     = _cfgSvc.Current.Ticker;
        Order.PointValue = _cfgSvc.Current.PointValue;
        Order.Contracts  = Math.Max(1, _cfgSvc.Current.Contracts);
        Flat.Contracts   = Math.Max(1, _cfgSvc.Current.Contracts);
    }

    // ── AJAX: live positions ──────────────────────────────────────

    public async Task<IActionResult> OnGetPositionsAsync()
    {
        var cfg = BuildCfg(_cfgSvc.Current.Ticker);
        try
        {
            var positions = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => await ManualBrokerOps.GetPositionsTradeStationAsync(_ts, cfg.AccountId),
                "Schwab"       => await ManualBrokerOps.GetPositionsSchwabAsync(_schwab, cfg.AccountId),
                "Tradovate" => await ManualBrokerOps.GetPositionsTradovateAsync(_tv, cfg.AccountId),
                _              => await ManualBrokerOps.GetPositionsMockAsync()
            };
            return new JsonResult(positions);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetPositions failed");
            return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    // ── Place bracket order ───────────────────────────────────────

    public async Task<IActionResult> OnPostAsync()
    {
        bool isLong = Order.Direction == "Long";

        if (Order.Contracts < 1)
            Errors.Add("Contracts must be at least 1.");

        if (Order.UsePartial)
        {
            if (Order.Contracts < 2)
                Errors.Add("Partial exit requires at least 2 contracts.");
            if (Order.PartialContracts < 1 || Order.PartialContracts >= Order.Contracts)
                Errors.Add("Partial contracts must be between 1 and (total contracts − 1).");
        }

        if (Errors.Count > 0) return Page();

        int remainCts = Order.Contracts - Order.PartialContracts;

        switch (Order.InputMode)
        {
            case "Dollars":
                if (Order.PointValue <= 0)
                    { Errors.Add("Point value must be > 0 for dollar-based input."); break; }
                Order.StopPoints   = Order.StopDollars   / (Order.PointValue * Order.Contracts);
                Order.TargetPoints = Order.TargetDollars / (Order.PointValue * (Order.UsePartial ? Math.Max(1, remainCts) : Order.Contracts));
                if (Order.UsePartial && Order.PartialContracts > 0)
                    Order.PartialPoints = Order.PartialDollars / (Order.PointValue * Order.PartialContracts);
                break;

            case "Price":
                if (Order.EntryPrice <= 0)
                    { Errors.Add("Entry price is required for price-based input."); break; }
                Order.StopPoints   = isLong ? Order.EntryPrice - Order.StopPrice   : Order.StopPrice   - Order.EntryPrice;
                Order.TargetPoints = isLong ? Order.TargetPrice - Order.EntryPrice : Order.EntryPrice - Order.TargetPrice;
                if (Order.UsePartial)
                    Order.PartialPoints = isLong ? Order.PartialPrice - Order.EntryPrice : Order.EntryPrice - Order.PartialPrice;
                break;
        }

        if (Errors.Count > 0) return Page();

        if (Order.StopPoints   <= 0) Errors.Add("Stop distance must be greater than 0.");
        if (Order.TargetPoints <= 0) Errors.Add("Target distance must be greater than 0.");
        if (Order.UsePartial && (Order.PartialPoints <= 0 || Order.PartialPoints >= Order.TargetPoints))
            Errors.Add("Partial distance must be > 0 and < full target distance.");

        if (Errors.Count > 0) return Page();

        var cfg = BuildCfg(Order.Ticker);

        try
        {
            var httpFactory = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
            IOrderExecutor exec = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => new TradeStationExecutor(_ts, cfg,
                    HttpContext.RequestServices.GetRequiredService<ILogger<TradeStationExecutor>>(),
                    httpFactory),
                "Schwab" => new SchwabExecutor(_schwab, cfg,
                    HttpContext.RequestServices.GetRequiredService<ILogger<SchwabExecutor>>(),
                    httpFactory),
                "Tradovate" => new TradovateExecutor(_tv, cfg,
                    HttpContext.RequestServices.GetRequiredService<ILogger<TradovateExecutor>>(),
                    httpFactory: httpFactory),
                _ => HttpContext.RequestServices.GetRequiredService<MockBrokerExecutor>()
            };

            if (!Order.UsePartial)
            {
                var sig = BuildEntry(isLong, Order.Contracts,
                    Order.EntryPrice, Order.StopPoints, Order.TargetPoints);
                await exec.OnEntrySignalAsync(sig);
                Placed.Add($"Bracket placed: {sig.Direction} {sig.Contracts}× Entry={sig.Entry} Stop={sig.Stop} Target={sig.Target}");
            }
            else
            {
                var sig1 = BuildEntry(isLong, Order.PartialContracts,
                    Order.EntryPrice, Order.StopPoints, Order.PartialPoints);
                await exec.OnEntrySignalAsync(sig1);
                Placed.Add($"Bracket 1 (partial): {sig1.Direction} {sig1.Contracts}× Entry={sig1.Entry} Stop={sig1.Stop} Target={sig1.Target}");

                var sig2 = BuildEntry(isLong, remainCts,
                    Order.EntryPrice, Order.StopPoints, Order.TargetPoints);
                await exec.OnEntrySignalAsync(sig2);
                Placed.Add($"Bracket 2 (runner):  {sig2.Direction} {sig2.Contracts}× Entry={sig2.Entry} Stop={sig2.Stop} Target={sig2.Target}");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual order placement failed");
            Errors.Add($"Order placement error: {ex.Message}");
        }

        return Page();
    }

    // ── Cancel all open orders ────────────────────────────────────

    public async Task<IActionResult> OnPostCancelAllAsync()
    {
        var cfg = BuildCfg(_cfgSvc.Current.Ticker);
        try
        {
            var results = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => await ManualBrokerOps.CancelAllTradeStationAsync(_ts, cfg.AccountId, cfg.Ticker),
                "Schwab"       => await ManualBrokerOps.CancelAllSchwabAsync(_schwab, cfg.AccountId, cfg.Ticker),
                "Tradovate" => await ManualBrokerOps.CancelAllTradovateAsync(_tv, cfg.AccountId, cfg.Ticker),
                _              => await ManualBrokerOps.CancelAllMockAsync(cfg.Ticker)
            };
            Placed.AddRange(results);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cancel all orders failed");
            Errors.Add($"Cancel failed: {ex.Message}");
        }
        return Page();
    }

    // ── Flat position (manage section form) ───────────────────────

    public async Task<IActionResult> OnPostFlatAsync()
    {
        if (Flat.Contracts < 1) { Errors.Add("Contracts to close must be at least 1."); return Page(); }

        var cfg = BuildCfg(_cfgSvc.Current.Ticker);
        bool isCurrentLong = Flat.Direction == "Long";

        try
        {
            // Cancel working stop/target orders first to avoid stale fills
            var cancelResults = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => await ManualBrokerOps.CancelAllTradeStationAsync(_ts, cfg.AccountId, cfg.Ticker),
                "Schwab"       => await ManualBrokerOps.CancelAllSchwabAsync(_schwab, cfg.AccountId, cfg.Ticker),
                "Tradovate"    => await ManualBrokerOps.CancelAllTradovateAsync(_tv, cfg.AccountId, cfg.Ticker),
                _              => await ManualBrokerOps.CancelAllMockAsync(cfg.Ticker)
            };
            Placed.AddRange(cancelResults);

            var result = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => await ManualBrokerOps.FlatAtMarketTradeStationAsync(
                                      _ts, cfg.AccountId, cfg.Ticker, Flat.Contracts, isCurrentLong),
                "Schwab"       => await ManualBrokerOps.FlatAtMarketSchwabAsync(
                                      _schwab, cfg.AccountId, cfg.Ticker, Flat.Contracts, isCurrentLong),
                "Tradovate"    => await ManualBrokerOps.FlatAtMarketTradovateAsync(
                                   _tv, cfg.AccountId, cfg.Ticker, Flat.Contracts, isCurrentLong),
                _              => await ManualBrokerOps.FlatAtMarketMockAsync(cfg.Ticker, Flat.Contracts, isCurrentLong)
            };
            Placed.Add(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Flat position failed");
            Errors.Add($"Flat failed: {ex.Message}");
        }
        return Page();
    }

    // ── Flat a specific position from the positions table ─────────

    /// <summary>
    /// Called by the Flat button on each position row.
    /// Symbol is already in broker-specific format (returned by the positions API).
    /// </summary>
    public async Task<IActionResult> OnPostFlatPositionAsync(
        string symbol, int contracts, bool isLong)
    {
        if (contracts < 1) { Errors.Add("Contracts must be at least 1."); return Page(); }

        var cfg = BuildCfgRaw();   // don't re-format the symbol — it came from the broker
        try
        {
            // Cancel working stop/target orders first to avoid stale fills
            var cancelResults = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => await ManualBrokerOps.CancelAllTradeStationAsync(_ts, cfg.AccountId, symbol),
                "Schwab"       => await ManualBrokerOps.CancelAllSchwabAsync(_schwab, cfg.AccountId, symbol),
                "Tradovate"    => await ManualBrokerOps.CancelAllTradovateAsync(_tv, cfg.AccountId, symbol),
                _              => await ManualBrokerOps.CancelAllMockAsync(symbol)
            };
            Placed.AddRange(cancelResults);

            var result = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => await ManualBrokerOps.FlatAtMarketTradeStationAsync(
                                      _ts, cfg.AccountId, symbol, contracts, isLong),
                "Schwab"       => await ManualBrokerOps.FlatAtMarketSchwabAsync(
                                      _schwab, cfg.AccountId, symbol, contracts, isLong),
                "Tradovate"    => await ManualBrokerOps.FlatAtMarketTradovateAsync(
                                   _tv, cfg.AccountId, symbol, contracts, isLong),
                _              => await ManualBrokerOps.FlatAtMarketMockAsync(symbol, contracts, isLong)
            };
            Placed.Add(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FlatPosition failed");
            Errors.Add($"Flat failed: {ex.Message}");
        }
        return Page();
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>Clone config, resolve AccountId, and convert ticker to broker format.</summary>
    private StrategyConfig BuildCfg(string ticker)
    {
        var cfg = _cfgSvc.Current.Clone();
        cfg.Ticker = ticker;
        ApplyAccountId(cfg);
        cfg.Ticker = FuturesSymbol.ForBroker(cfg.Ticker, cfg.EffectiveExecBroker);
        return cfg;
    }

    /// <summary>Clone config and resolve AccountId only — do NOT reformat the ticker.</summary>
    private StrategyConfig BuildCfgRaw()
    {
        var cfg = _cfgSvc.Current.Clone();
        ApplyAccountId(cfg);
        return cfg;
    }

    private void ApplyAccountId(StrategyConfig cfg)
    {
        var exec = cfg.EffectiveExecBroker;
        var raw = exec switch
        {
            "TradeStation" => _config["TradeStation:AccountId"],
            "Schwab"       => _config["Schwab:AccountId"],
            "Tradovate"    => _config["Tradovate:AccountId"],
            _              => null
        };
        if (!string.IsNullOrEmpty(raw)) cfg.AccountId = raw;
    }

    private static EntrySignal BuildEntry(bool isLong, int contracts,
        decimal entryPrice, decimal stopPts, decimal targetPts)
    {
        decimal stop    = isLong ? entryPrice - stopPts   : entryPrice + stopPts;
        decimal target  = isLong ? entryPrice + targetPts : entryPrice - targetPts;
        decimal partial = isLong ? entryPrice + targetPts * 0.5m : entryPrice - targetPts * 0.5m;

        return new EntrySignal(
            Setup:     SetupId.A,
            Direction: isLong ? Direction.Long : Direction.Short,
            Entry:     entryPrice,
            Stop:      stop,
            Target:    target,
            Partial:   partial,
            Contracts: contracts,
            Time:      DateTime.UtcNow
        );
    }
}

// ── Form models ──────────────────────────────────────────────────

public class ManualOrder
{
    [Required] public string  Ticker     { get; set; } = "";
    [Required] public string  Direction  { get; set; } = "Long";
    public            string  InputMode  { get; set; } = "Points";

    [Range(0.01, double.MaxValue)] public decimal EntryPrice    { get; set; }
    [Range(1, 100)]                public int     Contracts     { get; set; } = 1;
    public decimal PointValue    { get; set; }

    // Points mode
    [Range(0, double.MaxValue)] public decimal StopPoints    { get; set; }
    [Range(0, double.MaxValue)] public decimal TargetPoints  { get; set; }

    // Dollars mode
    [Range(0, double.MaxValue)] public decimal StopDollars   { get; set; }
    [Range(0, double.MaxValue)] public decimal TargetDollars { get; set; }

    // Price mode
    [Range(0, double.MaxValue)] public decimal StopPrice     { get; set; }
    [Range(0, double.MaxValue)] public decimal TargetPrice   { get; set; }

    // Partial
    public bool    UsePartial       { get; set; }
    [Range(0, 99)] public int     PartialContracts { get; set; } = 1;
    [Range(0, double.MaxValue)] public decimal PartialPoints  { get; set; }
    [Range(0, double.MaxValue)] public decimal PartialDollars { get; set; }
    [Range(0, double.MaxValue)] public decimal PartialPrice   { get; set; }
}

public class FlatForm
{
    [Required] public string Direction { get; set; } = "Long";
    [Range(1, 100)] public int Contracts { get; set; } = 1;
}
