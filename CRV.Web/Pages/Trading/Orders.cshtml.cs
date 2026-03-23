namespace CRV.Web.Pages.Trading;

using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

public class OrdersModel : PageModel
{
    private readonly StrategyConfigService   _cfgSvc;
    private readonly SchwabAuthService       _schwab;
    private readonly TradeStationAuthService _ts;
    private readonly TradovateAuthService    _tv;
    private readonly MockBrokerExecutor      _mockExec;
    private readonly TradingDbContext         _db;
    private readonly IConfiguration          _config;
    private readonly ILogger<OrdersModel>    _log;

    /// <summary>The broker whose order book is being displayed (exec broker, not data broker).</summary>
    public string CurrentBroker => _cfgSvc.Current.EffectiveExecBroker;
    public string CurrentTicker => _cfgSvc.Current.Ticker;

    public List<string> Messages { get; } = new();

    public OrdersModel(
        StrategyConfigService cfgSvc,
        SchwabAuthService schwab,
        TradeStationAuthService ts,
        TradovateAuthService tv,
        MockBrokerExecutor mockExec,
        TradingDbContext db,
        IConfiguration config,
        ILogger<OrdersModel> log)
    {
        _cfgSvc   = cfgSvc;
        _schwab   = schwab;
        _ts       = ts;
        _tv       = tv;
        _mockExec = mockExec;
        _db       = db;
        _config   = config;
        _log      = log;
    }

    public void OnGet() { }

    // ── AJAX: returns orders as JSON ──────────────────────────────

    public async Task<IActionResult> OnGetOrdersAsync(
        string  broker = "",
        string  status = "ALL",
        string? from   = null,
        string? to     = null)
    {
        try
        {
            if (string.IsNullOrEmpty(broker))
                broker = _cfgSvc.Current.EffectiveExecBroker;

            var cfg      = BuildCfg(broker);
            var fromDate = DateTime.TryParse(from, out var fd) ? fd : DateTime.Today;
            var toDate   = DateTime.TryParse(to,   out var td) ? td : DateTime.Today;

            var orders = broker switch
            {
                "TradeStation" => await ManualBrokerOps.GetOrdersTradeStationAsync(
                                      _ts, cfg.AccountId, status, fromDate, toDate),
                "Schwab"       => await ManualBrokerOps.GetOrdersSchwabAsync(
                                      _schwab, cfg.AccountId, status, fromDate, toDate),
                "Tradovate" or "TradovateReplay"
                               => await ManualBrokerOps.GetOrdersTradovateAsync(
                                      _tv, cfg.AccountId, status, fromDate, toDate),
                _              => GetMockOrdersFromMemory(status, fromDate, toDate)
            };
            return new JsonResult(orders);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetOrders failed");
            return new JsonResult(new { error = $"{ex.GetType().Name}: {ex.Message}" }) { StatusCode = 500 };
        }
    }

    // ── Cancel single order ───────────────────────────────────────

    public async Task<IActionResult> OnPostCancelOrderAsync(string orderId, string broker = "")
    {
        bool isAjax = Request.Headers["RequestVerificationToken"].Count > 0;

        if (string.IsNullOrWhiteSpace(orderId))
        {
            if (isAjax) return new JsonResult(new { error = "Order ID is required." }) { StatusCode = 400 };
            Messages.Add("Order ID is required.");
            return Page();
        }

        if (string.IsNullOrEmpty(broker))
            broker = _cfgSvc.Current.EffectiveExecBroker;

        var cfg = BuildCfg(broker);

        try
        {
            var result = broker switch
            {
                "TradeStation" => await ManualBrokerOps.CancelOrderTradeStationAsync(_ts, orderId),
                "Schwab"       => await ManualBrokerOps.CancelOrderSchwabAsync(_schwab, cfg.AccountId, orderId),
                "Tradovate"    => await ManualBrokerOps.CancelOrderTradovateAsync(_tv, orderId),
                _              => await ManualBrokerOps.CancelOrderMockAsync(orderId, _mockExec)
            };
            if (isAjax) return new JsonResult(new { message = result });
            Messages.Add(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CancelOrder {Id} failed", orderId);
            if (isAjax) return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
            Messages.Add($"Cancel failed: {ex.Message}");
        }

        return Page();
    }

    // ── Mock orders: merge in-memory (live) + DB (persisted) ─────

    private static readonly TimeZoneInfo _et =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static string ToEtString(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), _et)
            .ToString("M/d/yyyy, h:mm:ss tt");

    private List<OrderView> GetMockOrdersFromMemory(
        string status, DateTime from, DateTime to)
    {
        // Convert date range to UTC for consistent comparison
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc   = DateTime.SpecifyKind(to.AddDays(1), DateTimeKind.Utc);

        // In-memory orders (current engine run — has SetupId + live status)
        var memAll = _mockExec.GetOrders();
        var memFiltered = memAll
            .Where(o => o.PlacedAt >= fromUtc && o.PlacedAt < toUtc);
        if (status != "ALL")
            memFiltered = memFiltered.Where(o => o.Status == status);
        var memList = memFiltered.ToList();

        OrderView ToView(MockOrder o) => new(
            OrderId:     o.OrderId,
            Symbol:      o.Symbol,
            Status:      o.Status,
            StatusLabel: o.Status,
            OrderType:   o.OrderType,
            Action:      o.Action,
            Quantity:    o.Quantity,
            LimitPrice:  o.LimitPrice,
            StopPrice:   o.StopPrice,
            PlacedTime:  ToEtString(o.PlacedAt),
            CanCancel:   o.Status == "WORKING",
            Setup:       o.SetupId
        );

        // If in-memory has orders, use those (freshest status + SetupId)
        if (memList.Count > 0)
            return memList.Select(ToView)
                          .OrderByDescending(o => o.PlacedTime).ToList();

        // Fallback: DB orders (engine restarted — in-memory is empty)
        var dbQuery = _db.Orders.AsNoTracking()
            .Where(o => o.Broker == "Mock" && o.PlacedAt >= fromUtc && o.PlacedAt < toUtc);
        if (status != "ALL")
            dbQuery = dbQuery.Where(o => o.Status == status);

        return dbQuery.OrderByDescending(o => o.PlacedAt)
            .AsEnumerable()
            .Select(o => new OrderView(
                OrderId:     o.OrderId,
                Symbol:      o.Symbol,
                Status:      o.Status,
                StatusLabel: o.Status,
                OrderType:   o.OrderType,
                Action:      o.Action,
                Quantity:    o.Quantity,
                LimitPrice:  o.LimitPrice,
                StopPrice:   o.StopPrice,
                PlacedTime:  ToEtString(o.PlacedAt),
                CanCancel:   false,
                Setup:       o.SetupId
            )).ToList();
    }

    // ── Helper ────────────────────────────────────────────────────

    private StrategyConfig BuildCfg(string broker)
    {
        var cfg = _cfgSvc.Current.Clone();
        var raw = broker switch
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
