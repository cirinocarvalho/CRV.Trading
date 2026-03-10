namespace CRV.Web.Pages.Trading;

using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class OrdersModel : PageModel
{
    private readonly StrategyConfigService   _cfgSvc;
    private readonly SchwabAuthService       _schwab;
    private readonly TradeStationAuthService _ts;
    private readonly TradovateAuthService    _tv;
    private readonly MockBrokerExecutor      _mockExec;
    private readonly IConfiguration          _config;
    private readonly ILogger<OrdersModel>    _log;

    public string CurrentBroker => _cfgSvc.Current.Broker;
    public string CurrentTicker => _cfgSvc.Current.Ticker;

    public List<string> Messages { get; } = new();

    public OrdersModel(
        StrategyConfigService cfgSvc,
        SchwabAuthService schwab,
        TradeStationAuthService ts,
        TradovateAuthService tv,
        MockBrokerExecutor mockExec,
        IConfiguration config,
        ILogger<OrdersModel> log)
    {
        _cfgSvc   = cfgSvc;
        _schwab   = schwab;
        _ts       = ts;
        _tv       = tv;
        _mockExec = mockExec;
        _config   = config;
        _log      = log;
    }

    public void OnGet() { }

    // ── AJAX: returns orders as JSON ──────────────────────────────

    public async Task<IActionResult> OnGetOrdersAsync(
        string status = "ALL",
        string? from  = null,
        string? to    = null)
    {
        try
        {
            var cfg      = BuildCfg();
            var fromDate = DateTime.TryParse(from, out var fd) ? fd : DateTime.Today;
            var toDate   = DateTime.TryParse(to,   out var td) ? td : DateTime.Today;

            var orders = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => await ManualBrokerOps.GetOrdersTradeStationAsync(
                                      _ts, cfg.AccountId, status, fromDate, toDate),
                "Schwab"       => await ManualBrokerOps.GetOrdersSchwabAsync(
                                      _schwab, cfg.AccountId, status, fromDate, toDate),
                "Tradovate" => await ManualBrokerOps.GetOrdersTradovateAsync(
                                   _tv, cfg.AccountId, status, fromDate, toDate),
                _              => await ManualBrokerOps.GetOrdersMockAsync(_mockExec)
            };
            return new JsonResult(orders);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetOrders failed");
            // Return error as JSON so the client error banner can display it
            return new JsonResult(new { error = $"{ex.GetType().Name}: {ex.Message}" }) { StatusCode = 500 };
        }
    }

    // ── Cancel single order ───────────────────────────────────────

    public async Task<IActionResult> OnPostCancelOrderAsync(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            Messages.Add("Order ID is required.");
            return Page();
        }

        var cfg = BuildCfg();

        try
        {
            var result = cfg.EffectiveExecBroker switch
            {
                "TradeStation" => await ManualBrokerOps.CancelOrderTradeStationAsync(_ts, orderId),
                "Schwab"       => await ManualBrokerOps.CancelOrderSchwabAsync(_schwab, cfg.AccountId, orderId),
                "Tradovate" => await ManualBrokerOps.CancelOrderTradovateAsync(_tv, orderId),
                _              => await ManualBrokerOps.CancelOrderMockAsync(orderId)
            };
            Messages.Add(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CancelOrder {Id} failed", orderId);
            Messages.Add($"Cancel failed: {ex.Message}");
        }

        return Page();
    }

    // ── Helper ────────────────────────────────────────────────────

    private StrategyConfig BuildCfg()
    {
        var cfg  = _cfgSvc.Current.Clone();
        var exec = cfg.EffectiveExecBroker;
        var raw  = exec switch
        {
            "TradeStation" => _config["TradeStation:AccountId"],
            "Schwab"       => _config["Schwab:AccountId"],
            "Tradovate"    => _config["Tradovate:AccountId"],
            _              => null
        };
        if (!string.IsNullOrEmpty(raw)) cfg.AccountId = raw;
        return cfg;
    }
}
