using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CRV.Web.Api;

/// <summary>
/// The order-destination account for Positions & orders and Flatten all. Every request that
/// changes something requires the antiforgery token the layout renders, so another site
/// can't trigger it through a signed-in browser.
/// </summary>
[ApiController]
[Route("api/orders")]
[EnableRateLimiting("engine-api")]
public class OrdersController : ControllerBase
{
    private readonly BrokerAccountService      _account;
    private readonly FlattenAllService         _flatten;
    private readonly LiveEngineOrchestrator    _engine;
    private readonly ILastPriceProvider        _prices;
    private readonly ILogger<OrdersController> _log;

    public OrdersController(BrokerAccountService account, FlattenAllService flatten, LiveEngineOrchestrator engine,
                            ILastPriceProvider prices, ILogger<OrdersController> log)
    {
        _account = account; _flatten = flatten; _engine = engine; _prices = prices; _log = log;
    }

    private string Who => User?.Identity?.Name ?? Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault() ?? "unknown";

    /// <summary>Everything open: engine groups with their legs, broker positions, working orders.</summary>
    [HttpGet("book")]
    public async Task<IActionResult> Book()
    {
        var groups = _engine.GetActiveGroups()
            .Where(g => g.Status is GroupOrderStatus.Pending or GroupOrderStatus.Active or GroupOrderStatus.PartialFilled)
            .Select(g =>
            {
                var last = _prices.GetLastPrice(FuturesSymbol.Normalize(g.Ticker));
                var qty = g.Status == GroupOrderStatus.PartialFilled ? g.TotalContracts - g.PartialContracts : g.TotalContracts;
                decimal? unreal = g.EntryPrice is decimal e && last > 0
                    ? (g.Direction == Direction.Long ? last - e : e - last) * g.PointValue * qty + g.AccruedPartialPnl
                    : null;
                return new
                {
                    id = g.GroupOrderId, setup = g.SetupId, ticker = g.Ticker, direction = g.Direction.ToString(),
                    contracts = qty, total = g.TotalContracts, status = g.Status.ToString(), entry = g.EntryPrice,
                    last = last > 0 ? last : (decimal?)null, unrealized = unreal,
                    legs = g.Legs.OrderBy(l => l.LegType).Select(l => new
                    {
                        type = l.LegType.ToString(), status = l.Status.ToString(), price = l.Price,
                        qty = l.Quantity, fill = l.FillPrice,
                    }),
                };
            })
            .ToList();

        List<PositionView> positions = new();
        List<OrderView> orders = new();
        string? problem = null;
        if (_account.HasBrokerAccount)
        {
            try
            {
                positions = await _account.GetPositionsAsync();
                orders = (await _account.GetOrdersAsync()).Where(o => o.CanCancel).ToList();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Positions & orders: could not read the broker account");
                problem = $"Couldn't read your {_account.Account.BrokerLabel} account: {ex.Message}";
            }
        }

        var tracked = groups.Select(g => FuturesSymbol.Normalize(g.ticker).ToUpperInvariant()).ToHashSet();
        var acct = _account.Account;
        return Ok(new
        {
            account = acct.Label, broker = acct.BrokerLabel, realMoney = acct.IsRealMoney, engineRunning = _engine.IsRunning,
            hasBrokerAccount = _account.HasBrokerAccount, problem, groups,
            positions = positions.Select(p => new
            {
                symbol = p.Symbol, direction = p.Direction, quantity = p.Quantity, average = p.AveragePrice,
                unrealized = p.UnrealizedPnl, assetType = p.AssetType, description = p.Description,
                trackedByEngine = tracked.Contains(FuturesSymbol.Normalize(p.Symbol).ToUpperInvariant()),
                canClose = FlattenPlanner.IsFutureSymbol(p.Symbol) && string.Equals(p.AssetType, "FUTURE", StringComparison.OrdinalIgnoreCase),
            }),
            orders = orders.Select(o => new
            {
                id = o.OrderId, symbol = o.Symbol, action = o.Action, type = o.OrderType, quantity = o.Quantity,
                limit = o.LimitPrice, stop = o.StopPrice, status = o.StatusLabel, placed = o.PlacedTime,
            }),
        });
    }

    [HttpGet("flatten-all")]
    public async Task<IActionResult> FlattenPreview() => Ok(await _flatten.PreviewAsync());

    [HttpPost("flatten-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FlattenAll() => Ok(await _flatten.ExecuteAsync(Who));

    public sealed record GroupRequest(string SetupId, string GroupOrderId);

    [HttpPost("groups/break-even")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BreakEven([FromBody] GroupRequest req)
    {
        _log.LogWarning("Move to BE requested by {User} for {Setup}", Who, req.SetupId);
        var (ok, msg) = await _engine.MoveManualToBEAsync(req.SetupId);
        return ok ? Ok(new { message = msg }) : BadRequest(new { error = msg });
    }

    [HttpPost("groups/exit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExitGroup([FromBody] GroupRequest req)
    {
        _log.LogWarning("Exit group requested by {User}: {Group} ({Setup})", Who, req.GroupOrderId, req.SetupId);
        var (ok, msg) = await _engine.FlatGroupAsync(req.GroupOrderId);
        return ok ? Ok(new { message = msg }) : BadRequest(new { error = msg });
    }

    public sealed record CloseRequest(string Symbol, int Quantity, bool IsLong);

    [HttpPost("positions/close")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClosePosition([FromBody] CloseRequest req)
    {
        if (req.Quantity < 1) return BadRequest(new { error = "Quantity must be at least 1." });
        if (!FlattenPlanner.IsFutureSymbol(req.Symbol))
            return BadRequest(new { error = "Only futures positions can be closed here. Close options in Options." });

        // The same race Flatten all avoids: an instrument the engine is tracking is closed through the engine.
        var tracked = _engine.GetActiveGroups().Any(g =>
            (g.Status is GroupOrderStatus.Active or GroupOrderStatus.PartialFilled or GroupOrderStatus.Pending) &&
            FuturesSymbol.Normalize(g.Ticker).Equals(FuturesSymbol.Normalize(req.Symbol), StringComparison.OrdinalIgnoreCase));
        if (tracked)
            return BadRequest(new { error = "The engine is managing this position. Use Exit on its bracket instead." });

        _log.LogWarning("Close position requested by {User}: {Symbol} {Side} {Qty}", Who, req.Symbol, req.IsLong ? "LONG" : "SHORT", req.Quantity);
        try { return Ok(new { messages = await _account.ClosePositionAsync(req.Symbol, req.Quantity, req.IsLong) }); }
        catch (Exception ex) { _log.LogError(ex, "Close position failed"); return StatusCode(502, new { error = ex.Message }); }
    }

    public sealed record CancelRequest(string OrderId);

    [HttpPost("cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel([FromBody] CancelRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.OrderId)) return BadRequest(new { error = "Order id is required." });
        _log.LogWarning("Cancel order requested by {User}: {Order}", Who, req.OrderId);
        try { return Ok(new { message = await _account.CancelOrderAsync(req.OrderId) }); }
        catch (Exception ex) { _log.LogError(ex, "Cancel order failed"); return StatusCode(502, new { error = ex.Message }); }
    }
}
