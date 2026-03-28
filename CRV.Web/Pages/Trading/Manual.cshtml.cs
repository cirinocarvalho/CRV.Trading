namespace CRV.Web.Pages.Trading;

using System.ComponentModel.DataAnnotations;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class ManualModel : PageModel
{
    private readonly StrategyConfigService     _cfgSvc;
    private readonly SchwabAuthService         _schwab;
    private readonly TradeStationAuthService   _ts;
    private readonly TradovateAuthService      _tv;
    private readonly LiveEngineOrchestrator    _orchestrator;
    private readonly IConfiguration            _config;
    private readonly ILogger<ManualModel>      _log;

    // ── Display ──────────────────────────────────────────────────
    public string CurrentBroker     => _cfgSvc.Current.Broker;
    public string CurrentExecBroker => _cfgSvc.Current.EffectiveExecBroker;
    public string CurrentTicker     => _cfgSvc.Current.Ticker;

    // ── Active groups for display ─────────────────────────────────
    public List<GroupOrder> ActiveGroups => _orchestrator.GetActiveGroups();

    // ── Result ───────────────────────────────────────────────────
    public List<string> Errors  { get; } = new();
    public List<string> Placed  { get; } = new();

    // ── Forms ─────────────────────────────────────────────────────
    [BindProperty] public ManualOrder Order { get; set; } = new();
    [BindProperty] public FlatForm    Flat  { get; set; } = new();

    private readonly ILastPriceProvider       _prices;

    public ManualModel(
        StrategyConfigService cfgSvc,
        SchwabAuthService schwab,
        TradeStationAuthService ts,
        TradovateAuthService tv,
        LiveEngineOrchestrator orchestrator,
        ILastPriceProvider prices,
        IConfiguration config,
        ILogger<ManualModel> log)
    {
        _cfgSvc       = cfgSvc;
        _schwab       = schwab;
        _ts           = ts;
        _tv           = tv;
        _orchestrator = orchestrator;
        _prices       = prices;
        _config       = config;
        _log          = log;
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
                _              => ManualBrokerOps.GetMockPositionsFromGroups(_orchestrator.GetActiveGroups())
            };

            // Enrich positions with P&L using live engine prices + Schwab quotes as fallback
            var missing = positions
                .Where(p => _prices.GetLastPrice(FuturesSymbol.Normalize(p.Symbol)) <= 0)
                .Select(p => FuturesSymbol.ToSchwab(p.Symbol))
                .Distinct().ToList();

            var schwabQuotes = missing.Count > 0
                ? await ManualBrokerOps.GetQuotesSchwabAsync(_schwab, missing)
                : new Dictionary<string, decimal>();

            var enriched = positions.Select(p =>
            {
                var normalized = FuturesSymbol.Normalize(p.Symbol);
                var lastPrice = _prices.GetLastPrice(normalized);
                if (lastPrice <= 0)
                    schwabQuotes.TryGetValue(normalized, out lastPrice);
                if (lastPrice <= 0) return p;

                var pv = FuturesSymbol.PointValue(normalized);
                var diff = p.Direction == "LONG"
                    ? lastPrice - p.AveragePrice
                    : p.AveragePrice - lastPrice;
                return p with { UnrealizedPnl = diff * pv * p.Quantity };
            }).ToList();

            return new JsonResult(enriched);
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
            var sig = BuildEntry(isLong, Order.Contracts, Order.EntryPrice,
                Order.StopPoints, Order.TargetPoints, Order.PartialPoints,
                Order.Ticker, Order.PointValue, Order.OrderType,
                Order.UsePartial, Order.PartialContracts, Order.UseBe);

            // Always route through the orchestrator so a GroupOrder is created,
            // registered with BrokerEventHandler, and visible in Group Orders /
            // alerts / session tabs — regardless of broker (Mock or live).
            var (ok, msg, group) = await _orchestrator.PlaceManualEntryAsync(sig, Order.PointValue);
            if (ok)
                Placed.Add(msg);
            else
                Errors.Add(msg);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual order placement failed");
            Errors.Add($"Order placement error: {ex.Message}");
        }

        return Page();
    }

    // ── Cancel all open orders ────────────────────────────────────

    public async Task<IActionResult> OnPostCancelAllAsync(string? ticker)
    {
        var cfg = BuildCfg(ticker ?? _cfgSvc.Current.Ticker);
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

        var cfg = BuildCfg(Flat.Ticker ?? _cfgSvc.Current.Ticker);
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

    // ── Move to Break-Even ────────────────────────────────────────

    public async Task<IActionResult> OnPostMoveToBEAsync(string setupId)
    {
        if (string.IsNullOrEmpty(setupId)) { Errors.Add("Setup ID required."); return Page(); }

        try
        {
            var (ok, msg) = await _orchestrator.MoveManualToBEAsync(setupId);
            if (ok) Placed.Add(msg);
            else Errors.Add(msg);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Move to BE failed");
            Errors.Add($"Move to BE failed: {ex.Message}");
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
        decimal entryPrice, decimal stopPts, decimal targetPts,
        decimal partialPts = 0, string ticker = "", decimal pointValue = 0,
        string orderType = "Market", bool usePartial = false, int partialContracts = 0,
        bool useBe = true)
    {
        decimal stop    = isLong ? entryPrice - stopPts   : entryPrice + stopPts;
        decimal target  = isLong ? entryPrice + targetPts : entryPrice - targetPts;
        decimal partial = partialPts > 0
            ? (isLong ? entryPrice + partialPts : entryPrice - partialPts)
            : (isLong ? entryPrice + targetPts * 0.5m : entryPrice - targetPts * 0.5m);

        return new EntrySignal(
            Setup:     SetupId.A,
            Direction: isLong ? Direction.Long : Direction.Short,
            Entry:     entryPrice,
            Stop:      stop,
            Tg2Price:       target,
            Tg1Price:       partial,
            TotalContracts: contracts,
            Time:           DateTime.UtcNow,
            OrderType:      orderType,
            Ticker:         ticker,
            SetupLabel:     $"Manual-{DateTime.UtcNow:HHmmss}",
            PartialContracts: usePartial ? partialContracts : 0,
            UsePartial:       usePartial,
            UseBe:            useBe
        );
    }
}

// ── Form models ──────────────────────────────────────────────────

public class ManualOrder
{
    [Required] public string  Ticker     { get; set; } = "";
    [Required] public string  Direction  { get; set; } = "Long";
    public            string  InputMode  { get; set; } = "Points";
    public            string  OrderType  { get; set; } = "Market";  // "Market" | "Limit"
    public            bool    UseBe      { get; set; } = true;      // Move stop to BE on partial fill

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
    public string? Ticker { get; set; }
    [Required] public string Direction { get; set; } = "Long";
    [Range(1, 100)] public int Contracts { get; set; } = 1;
}
