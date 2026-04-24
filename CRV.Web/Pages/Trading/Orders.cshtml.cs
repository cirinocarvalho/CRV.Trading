namespace CRV.Web.Pages.Trading;

using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using System.Text.Json;
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
    private readonly LiveEngineOrchestrator  _orchestrator;
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
        LiveEngineOrchestrator orchestrator,
        TradingDbContext db,
        IConfiguration config,
        ILogger<OrdersModel> log)
    {
        _cfgSvc       = cfgSvc;
        _schwab       = schwab;
        _ts           = ts;
        _tv           = tv;
        _mockExec     = mockExec;
        _orchestrator = orchestrator;
        _db           = db;
        _config       = config;
        _log          = log;
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

    // ── Flat a specific group order ────────────────────────────────

    public async Task<IActionResult> OnPostFlatGroupAsync(string groupOrderId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(groupOrderId))
                return new JsonResult(new { error = "Group order ID is required." }) { StatusCode = 400 };

            var (ok, msg) = await _orchestrator.FlatGroupAsync(groupOrderId);
            if (!ok)
                return new JsonResult(new { error = msg }) { StatusCode = 400 };

            return new JsonResult(new { message = msg });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FlatGroup {Id} failed", groupOrderId);
            return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
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
        // Convert date range (ET) to UTC for consistent comparison
        var fromEt = DateTime.SpecifyKind(from, DateTimeKind.Unspecified);
        var toEt   = DateTime.SpecifyKind(to.AddDays(1), DateTimeKind.Unspecified);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromEt, _et);
        var toUtc   = TimeZoneInfo.ConvertTimeToUtc(toEt, _et);

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

    // ── AJAX: returns group orders (WSS-managed) as JSON ─────────

    public async Task<IActionResult> OnGetGroupOrdersAsync(
        string? broker = null, string? from = null, string? to = null)
    {
        try
        {
            var fromDate = DateTime.TryParse(from, out var fd) ? fd : DateTime.Today;
            var toDate   = DateTime.TryParse(to,   out var td) ? td : DateTime.Today;
            // User enters dates in ET — convert boundaries to UTC for DB comparison
            var fromEt = DateTime.SpecifyKind(fromDate, DateTimeKind.Unspecified);
            var toEt   = DateTime.SpecifyKind(toDate.AddDays(1), DateTimeKind.Unspecified);
            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromEt, _et);
            var toUtc   = TimeZoneInfo.ConvertTimeToUtc(toEt, _et);

            // 1. Completed groups — from DB GroupOrders (Mock) or Trades table (live)
            var result = new List<GroupOrderDto>();
            var dbGroupIds = new HashSet<string>();

            bool isLiveBroker = !string.IsNullOrEmpty(broker) && broker != "Mock" && broker != "Backtest";

            if (isLiveBroker)
            {
                // Live broker: fetch ALL strategies from broker REST with real order data
                var tvExec = _orchestrator.GetGroupExecutor() as TradovateExecutor;
                if (tvExec != null)
                {
                    var allStrategies = await tvExec.GetAllStrategiesAsync();

                    // Load strategy logs to match setupId labels
                    var stratLogs = await _db.StrategyLogs.AsNoTracking()
                        .Where(s => s.CreatedAt >= fromUtc && s.CreatedAt < toUtc)
                        .ToDictionaryAsync(s => s.BrokerStrategyId, s => s);

                    // Filter strategies by date range
                    var filtered = allStrategies
                        .Where(s => s.Timestamp >= fromUtc && s.Timestamp < toUtc)
                        .OrderByDescending(s => s.Timestamp)
                        .ToList();

                    foreach (var strat in filtered)
                    {
                        // Match with StrategyLog for setup label
                        var stratIdStr = strat.StrategyId.ToString();
                        stratLogs.TryGetValue(stratIdStr, out var log);

                        var direction = strat.Action == "Sell" ? "Short" : "Long";
                        var exitAction = strat.Action == "Sell" ? "Buy" : "Sell";

                        // Classify legs from order links
                        var legs = new List<LegDto>();
                        foreach (var link in strat.OrderLinks.OrderBy(l => l.OrderId))
                        {
                            string legType;
                            if (link.Label == "256" || link.Action == strat.Action)
                                legType = "Entry";
                            else if (link.OrderType == "Stop")
                                legType = "Stop";
                            else if (link.OrderType == "Limit")
                                legType = legs.Any(l => l.LegType == "Tg1") ? "Tg2" : "Tg1";
                            else
                                legType = "Unknown";

                            legs.Add(new LegDto
                            {
                                OrderId = link.OrderId.ToString(),
                                LegType = legType,
                                OrderType = link.OrderType,
                                Action = link.Action,
                                Quantity = link.Qty,
                                Price = link.Price,
                                Status = link.Status,
                                FillPrice = link.FillPrice,
                            });
                        }

                        var entryLeg = legs.FirstOrDefault(l => l.LegType == "Entry");
                        var isCompleted = strat.Status == "ExecutionInterrupted" || strat.Status == "Completed";

                        result.Add(new GroupOrderDto
                        {
                            GroupOrderId = stratIdStr,
                            SetupId = log?.SetupId ?? "",  // blank if not from CRV
                            Ticker = log?.Ticker ?? $"contract-{strat.ContractId}",
                            Direction = direction,
                            TotalContracts = strat.EntryQty,
                            EntryPrice = entryLeg?.FillPrice ?? strat.EntryPrice,
                            Status = isCompleted ? "Completed" : strat.Status,
                            Broker = broker ?? "",
                            CreatedAt = DateTime.SpecifyKind(log?.CreatedAt ?? strat.Timestamp, DateTimeKind.Utc).ToString("o"),
                            CompletedAt = isCompleted ? DateTime.SpecifyKind(strat.Timestamp, DateTimeKind.Utc).ToString("o") : null,
                            Legs = legs
                        });
                    }
                }
            }
            else
            {
                // Mock/Backtest: use GroupOrders + OrderLegs from DB (existing behavior)
                var query = _db.GroupOrders.AsNoTracking()
                    .Where(g => g.CreatedAt >= fromUtc && g.CreatedAt < toUtc);
                if (!string.IsNullOrEmpty(broker))
                    query = query.Where(g => g.Broker == broker);
                var groups = await query
                    .OrderByDescending(g => g.CreatedAt)
                    .ToListAsync();

                var groupIds = groups.Select(g => g.GroupOrderId).ToList();
                dbGroupIds = new HashSet<string>(groupIds);
                var legs = await _db.OrderLegs.AsNoTracking()
                    .Where(l => groupIds.Contains(l.GroupOrderId))
                    .ToListAsync();

                var legsByGroup = legs.GroupBy(l => l.GroupOrderId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                result = groups.Select(g => MapGroup(g,
                    legsByGroup.TryGetValue(g.GroupOrderId, out var gl) ? gl : new())).ToList();
            }

            // 2. Active in-memory groups (pending/active/partial)
            // Collect broker strategy IDs already shown from REST to avoid duplicates
            var brokerStratIds = isLiveBroker
                ? new HashSet<string>(result.Select(r => r.GroupOrderId))
                : new HashSet<string>();
            var activeGroups = _orchestrator.GetActiveGroups();
            foreach (var ag in activeGroups)
            {
                if (dbGroupIds.Contains(ag.GroupOrderId)) continue;
                // For live brokers, skip if the broker strategy is already in the REST results
                if (!string.IsNullOrEmpty(ag.BrokerStrategyId) && brokerStratIds.Contains(ag.BrokerStrategyId)) continue;
                if (!string.IsNullOrEmpty(broker) && ag.Broker != broker) continue;
                result.Add(MapGroup(ag, ag.Legs ?? new()));
            }

            // Sort: active first, then by created descending
            result.Sort((a, b) =>
            {
                bool aActive = a.Status is "Active" or "Pending" or "PartialFilled";
                bool bActive = b.Status is "Active" or "Pending" or "PartialFilled";
                if (aActive != bActive) return aActive ? -1 : 1;
                return string.Compare(b.CreatedAt, a.CreatedAt, StringComparison.Ordinal);
            });

            var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            return new JsonResult(result, jsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetGroupOrders failed");
            return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    private GroupOrderDto MapGroup(GroupOrder g, List<OrderLeg> groupLegs) => new()
    {
        GroupOrderId = g.GroupOrderId,
        SetupId = g.SetupId,
        Ticker = g.Ticker,
        Direction = g.Direction.ToString(),
        TotalContracts = g.TotalContracts,
        PartialContracts = g.PartialContracts,
        EntryPrice = g.EntryPrice,
        Status = g.Status.ToString(),
        Broker = g.Broker,
        CreatedAt = ToEtString(g.CreatedAt),
        CompletedAt = g.CompletedAt.HasValue ? ToEtString(g.CompletedAt.Value) : null,
        Legs = groupLegs.Select(l => new LegDto
        {
            OrderId = l.OrderId,
            LegType = l.LegType.ToString(),
            OrderType = l.OrderType,
            Action = l.Action,
            Quantity = l.Quantity,
            Price = l.Price,
            Status = l.Status.ToString(),
            FillPrice = l.FillPrice,
            FillTime = l.FillTime.HasValue ? ToEtString(l.FillTime.Value) : null,
        }).ToList()
    };

    private class GroupOrderDto
    {
        public string GroupOrderId { get; set; } = "";
        public string SetupId { get; set; } = "";
        public string Ticker { get; set; } = "";
        public string Direction { get; set; } = "";
        public int TotalContracts { get; set; }
        public int PartialContracts { get; set; }
        public decimal? EntryPrice { get; set; }
        public string Status { get; set; } = "";
        public string Broker { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string? CompletedAt { get; set; }
        public List<LegDto> Legs { get; set; } = new();
    }

    private class LegDto
    {
        public string OrderId { get; set; } = "";
        public string LegType { get; set; } = "";
        public string OrderType { get; set; } = "";
        public string Action { get; set; } = "";
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string Status { get; set; } = "";
        public decimal? FillPrice { get; set; }
        public string? FillTime { get; set; }
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
