namespace CRV.Live.Brokers.Tradovate;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CRV.Core.Data;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// IOrderExecutor for Tradovate — places OSO bracket orders via REST.
/// Uses POST /order/placeOSO for entry with embedded stop (bracket2) and target (bracket1).
/// Partial and BE-move operations cancel the relevant leg and place a replacement order.
/// Exit cancels all open legs then sends a market close.
///
/// NOTE: Tradovate uses account "name" (a string like "DEMO123") in order payloads,
/// not an integer account ID. The account name is resolved once and cached via
/// GetAccountAsync() → GET /account/list → first account.
/// </summary>
public class TradovateExecutor : IOrderExecutor, IGroupOrderExecutor
{
    private readonly TradovateAuthService  _auth;
    private readonly StrategyConfig        _cfg;
    private readonly ILogger               _log;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IHttpClientFactory?   _httpFactory;

    // Per-setup order leg tracking — supports all 5 setups (A/B/C/D/F)
    private class SetupState
    {
        public long?      EntryOrderId  { get; set; }
        public long?      StopOrderId   { get; set; }
        public long?      TargetOrderId { get; set; }
        public long?      PartialOrderId { get; set; }
        public string?    Ticker        { get; set; }
        public Direction? Direction     { get; set; }

        public void Clear()
        {
            EntryOrderId = StopOrderId = TargetOrderId = PartialOrderId = null;
            Direction = null;
            Ticker = null;
        }
    }

    private readonly Dictionary<string, SetupState> _states = new();
    private SetupState GetOrCreateState(string id)
    {
        if (!_states.TryGetValue(id, out var s)) { s = new SetupState(); _states[id] = s; }
        return s;
    }

    // Account cache — resolved once from GET /account/list
    private record AccountRef(string Name, long Id);
    private AccountRef? _cachedAccount;

    /// <summary>
    /// Optional WSS event stream for order registration.
    /// Set by the orchestrator after construction so WSS events can route to groups.
    /// </summary>
    public TradovateEventStream? EventStream { get; set; }

    public TradovateExecutor(
        TradovateAuthService auth,
        StrategyConfig cfg,
        ILogger<TradovateExecutor> log,
        IServiceScopeFactory? scopeFactory = null,
        IHttpClientFactory? httpFactory = null)
    {
        _auth         = auth;
        _cfg          = cfg;
        _log          = log;
        _scopeFactory = scopeFactory;
        _httpFactory  = httpFactory;
    }

    private HttpClient CreateClient() => _httpFactory?.CreateClient("Tradovate") ?? new HttpClient();

    // ── IOrderExecutor ────────────────────────────────────────────

    public async Task<decimal?> OnEntrySignalAsync(EntrySignal sig)
    {
        _log.LogInformation("[TV] ENTRY {D} {Q}x {S} @ {E} Stop={St} Tgt={T}",
            sig.Direction, sig.Contracts, sig.Setup, sig.Entry, sig.Stop, sig.Target);

        var rawTicker   = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker;
        var symbol      = FuturesSymbol.ToTradovate(rawTicker);
        var account     = await GetAccountAsync();
        var entryAction = sig.Direction == Direction.Long ? "Buy"  : "Sell";
        var exitAction  = sig.Direction == Direction.Long ? "Sell" : "Buy";

        // POST /order/placeOSO
        // bracket1 = Limit target leg (opposite direction)
        // bracket2 = Stop  stop  leg  (opposite direction)
        bool isLimit = sig.OrderType == "Limit";
        var body = new
        {
            accountSpec = account.Name,
            accountId   = account.Id,
            action      = entryAction,
            symbol      = symbol,
            orderQty    = sig.Contracts,
            orderType   = isLimit ? "Limit" : "Market",
            price       = isLimit ? (decimal?)sig.Entry : null,
            isAutomated = true,
            bracket1    = new
            {
                action    = exitAction,
                orderType = "Limit",
                price     = sig.Target,
                orderQty  = sig.Contracts
            },
            bracket2    = new
            {
                action    = exitAction,
                orderType = "Stop",
                stopPrice = sig.Stop,
                orderQty  = sig.Contracts
            }
        };

        var (entryId, targetId, stopId) = await PlaceOsoAsync(body);

        // Only commit state if the order was actually placed
        if (entryId is null)
        {
            _log.LogError("[TV] ENTRY OSO failed — no order IDs returned. " +
                          "Engine and broker state may be inconsistent; manual check required.");
            return null;
        }

        // Store direction AFTER confirmed placement
        var state = GetOrCreateState(sig.Setup.ToString());
        state.Direction     = sig.Direction;
        state.Ticker        = rawTicker;
        state.EntryOrderId  = entryId;
        state.TargetOrderId = targetId;
        state.StopOrderId   = stopId;
        _log.LogInformation("[TV] Setup {S} bracket — entry={E} target={T} stop={St}",
            sig.Setup, entryId, targetId, stopId);

        // Poll for fill price feedback
        var fillPrice = await GetFillPriceAsync(entryId.Value);
        if (fillPrice is not null)
            _log.LogInformation("[TV] Entry fill price for {S}: {P}", sig.Setup, fillPrice);
        else
            _log.LogWarning("[TV] Could not retrieve fill price for entry order {Id}", entryId);

        // Discover bracket leg IDs if placeOSO response didn't include them.
        // Tradovate creates bracket legs asynchronously — they may only be
        // discoverable after the entry fills.
        if (state.TargetOrderId is null || state.StopOrderId is null)
        {
            _log.LogInformation("[TV] Bracket leg IDs missing (target={T}, stop={S}) — polling order list to discover",
                state.TargetOrderId, state.StopOrderId);
            var (foundTarget, foundStop) = await FindBracketLegsAsync(entryId.Value, sig.Target, sig.Stop);
            state.TargetOrderId ??= foundTarget;
            state.StopOrderId   ??= foundStop;
            _log.LogInformation("[TV] Setup {S} bracket legs resolved — target={T} stop={St}",
                sig.Setup, state.TargetOrderId, state.StopOrderId);
        }

        return fillPrice;
    }

    public async Task OnPartialSignalAsync(PartialSignal sig)
    {
        _log.LogInformation("[TV] PARTIAL {S} {Q}ct @ {P} ({R}ct remaining)",
            sig.Setup, sig.ContractsExited, sig.PartialPrice, sig.ContractsRemaining);

        var state      = GetOrCreateState(sig.Setup.ToString());
        var account    = await GetAccountAsync();
        var symbol     = FuturesSymbol.ToTradovate(state.Ticker ?? _cfg.Ticker);
        var exitAction = sig.Direction == Direction.Long ? "Sell" : "Buy";

        // Cancel the full-target bracket leg; replace with smaller partial limit
        if (state.TargetOrderId != null)
        {
            await CancelOrderAsync(state.TargetOrderId.Value);
            state.TargetOrderId = null;
        }
        else
        {
            _log.LogWarning("[TV] OnPartialSignal {S}: TargetOrderId not set — cannot cancel bracket target", sig.Setup);
        }

        var body = new
        {
            accountSpec = account.Name,
            accountId   = account.Id,
            action      = exitAction,
            symbol      = symbol,
            orderQty    = sig.ContractsExited,
            orderType   = "Limit",
            price       = sig.PartialPrice,
            isAutomated = true
        };

        state.PartialOrderId = await PlaceSingleAsync(body);
        _log.LogDebug("[TV] Setup {S} partial limit placed, ID={Id}", sig.Setup, state.PartialOrderId);
    }

    public async Task OnBESignalAsync(BESignal sig)
    {
        _log.LogInformation("[TV] MOVE_BE {S} → {P} ({Q}ct)",
            sig.Setup, sig.NewStop, sig.ContractsRemaining);

        var state = GetOrCreateState(sig.Setup.ToString());
        if (state.StopOrderId != null)
        {
            var ok = await ModifyOrderAsync(state.StopOrderId.Value, sig.ContractsRemaining, stopPrice: sig.NewStop);
            if (ok) _log.LogDebug("[TV] Setup {S} stop modified to BE @ {P}", sig.Setup, sig.NewStop);
        }
        else
            _log.LogWarning("[TV] OnBESignal {S}: StopOrderId not set — cannot modify stop", sig.Setup);
    }

    public async Task OnExitSignalAsync(ExitSignal sig)
    {
        _log.LogInformation("[TV] EXIT {S} {R} @ {P} {Q}ct",
            sig.Setup, sig.Reason, sig.ExitPrice, sig.Contracts);

        var state      = GetOrCreateState(sig.Setup.ToString());
        var storedDir  = state.Direction;
        var exitTicker = state.Ticker ?? (!string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker);
        var legIds     = new[] { state.EntryOrderId, state.StopOrderId, state.TargetOrderId, state.PartialOrderId };
        state.Clear();

        // For Stop/Target exits the broker's OSO bracket already filled the exit —
        // we only need to cancel any remaining legs (the other side of the bracket).
        // For SessionEnd/AdverseTime/Manual the broker hasn't exited, so we must
        // cancel all legs AND place a market close order.
        bool brokerHandledExit = sig.Reason is ExitReason.Stop or ExitReason.Target;

        // Cancel all open bracket / partial legs (including OSO parent)
        foreach (var id in legIds.Where(x => x.HasValue))
            await CancelOrderAsync(id!.Value);

        if (brokerHandledExit)
        {
            _log.LogDebug("[TV] EXIT {S}: {R} — broker bracket filled, no market order needed", sig.Setup, sig.Reason);
            return;
        }

        // SessionEnd / AdverseTime / Manual — broker hasn't exited, place market close
        var account = await GetAccountAsync();
        var symbol  = FuturesSymbol.ToTradovate(exitTicker);

        // Try to recover direction from DB if lost (e.g. app restart mid-trade)
        if (storedDir is null)
        {
            storedDir = await RecoverDirectionAsync(sig.Setup);
            if (storedDir is not null)
                _log.LogInformation("[TV] EXIT {S}: recovered direction={D} from DB", sig.Setup, storedDir);
            else
                _log.LogWarning("[TV] EXIT {S}: direction unknown (app restart mid-trade?) — defaulting to Sell (safe for Long, DANGEROUS for Short)", sig.Setup);
        }
        var exitAction = (storedDir ?? Direction.Long) == Direction.Long ? "Sell" : "Buy";

        var body = new
        {
            accountSpec = account.Name,
            accountId   = account.Id,
            action      = exitAction,
            symbol      = symbol,
            orderQty    = sig.Contracts,
            orderType   = "Market",
            isAutomated = true
        };

        var orderId = await PlaceSingleAsync(body);
        _log.LogDebug("[TV] Market close placed, ID={Id}", orderId);
    }

    public async Task OnLevelsAdjustedAsync(string setupId, decimal newStop, decimal newTarget, int contracts)
    {
        _log.LogInformation("[TV] LEVELS_ADJUSTED {S} → Stop={St} Target={T} Qty={Q}", setupId, newStop, newTarget, contracts);

        var state = GetOrCreateState(setupId);

        if (state.StopOrderId != null)
        {
            var ok = await ModifyOrderAsync(state.StopOrderId.Value, contracts, stopPrice: newStop);
            if (ok) _log.LogDebug("[TV] Setup {S} stop modified → {P}", setupId, newStop);
        }
        else
            _log.LogWarning("[TV] OnLevelsAdjusted {S}: StopOrderId not set", setupId);

        if (state.TargetOrderId != null)
        {
            var ok = await ModifyOrderAsync(state.TargetOrderId.Value, contracts, limitPrice: newTarget);
            if (ok) _log.LogDebug("[TV] Setup {S} target modified → {P}", setupId, newTarget);
        }
        else
            _log.LogWarning("[TV] OnLevelsAdjusted {S}: TargetOrderId not set", setupId);
    }

    // ── IGroupOrderExecutor ────────────────────────────────────────

    /// <summary>
    /// Place an entry via OSO (entry+stop) plus separate tg1/tg2 limit orders.
    /// Returns a GroupOrder with all leg IDs populated, or null on failure.
    /// </summary>
    async Task<GroupOrder?> IGroupOrderExecutor.OnEntrySignalAsync(EntrySignal sig)
    {
        var rawTicker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker;
        var symbol = FuturesSymbol.ToTradovate(rawTicker);
        var account = await GetAccountAsync();
        var entryAction = sig.Direction == Direction.Long ? "Buy" : "Sell";
        var exitAction = sig.Direction == Direction.Long ? "Sell" : "Buy";
        bool isLimit = sig.OrderType == "Limit";

        var groupId = Guid.NewGuid().ToString("N")[..8];
        var partialCts = sig.EffectivePartialContracts();
        var remainCts = sig.Contracts - partialCts;

        // 1. Place Entry + Stop via placeOSO
        var osoBody = new
        {
            accountSpec = account.Name, accountId = account.Id,
            action = entryAction, symbol, orderQty = sig.Contracts,
            orderType = isLimit ? "Limit" : "Market",
            price = isLimit ? (decimal?)sig.Entry : null,
            isAutomated = true,
            bracket1 = new { action = exitAction, orderType = "Stop", stopPrice = sig.Stop, orderQty = sig.Contracts }
        };
        var (entryId, _, stopId) = await PlaceOsoAsync(osoBody);
        if (entryId is null)
        {
            _log.LogError("[TV-GRP] Entry OSO failed — no order IDs returned");
            return null;
        }

        // 2. Place Tg1 (partial limit)
        var tg1Body = new
        {
            accountSpec = account.Name, accountId = account.Id,
            action = exitAction, symbol,
            orderQty = partialCts, orderType = "Limit", price = sig.Partial,
            isAutomated = true
        };
        var tg1Id = await PlaceSingleAsync(tg1Body);

        // 3. Place Tg2 (remaining limit)
        var tg2Body = new
        {
            accountSpec = account.Name, accountId = account.Id,
            action = exitAction, symbol,
            orderQty = remainCts, orderType = "Limit", price = sig.Target,
            isAutomated = true
        };
        var tg2Id = await PlaceSingleAsync(tg2Body);

        // Discover stop if placeOSO response didn't include it
        if (stopId is null)
        {
            var (_, foundStop) = await FindBracketLegsAsync(entryId.Value, 0, sig.Stop);
            stopId = foundStop;
        }

        var setupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString();
        var buyAction = entryAction == "Buy" ? "BUY" : "SELL";
        var sellAction = exitAction == "Sell" ? "SELL" : "BUY";

        var group = new GroupOrder
        {
            GroupOrderId = groupId,
            SetupId = setupId,
            Ticker = rawTicker,
            Direction = sig.Direction,
            TotalContracts = sig.Contracts,
            PartialContracts = partialCts,
            Status = GroupOrderStatus.Pending,
            Broker = "Tradovate",
        };

        group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = entryId.Value.ToString(), LegType = LegType.Entry, OrderType = sig.OrderType, Action = buyAction, Quantity = sig.Contracts, Price = sig.Entry });
        if (tg1Id.HasValue)
            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = tg1Id.Value.ToString(), LegType = LegType.Tg1, OrderType = "Limit", Action = sellAction, Quantity = partialCts, Price = sig.Partial });
        if (tg2Id.HasValue)
            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = tg2Id.Value.ToString(), LegType = LegType.Tg2, OrderType = "Limit", Action = sellAction, Quantity = remainCts, Price = sig.Target });
        if (stopId.HasValue)
            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = stopId.Value.ToString(), LegType = LegType.Stop, OrderType = "Stop", Action = sellAction, Quantity = sig.Contracts, Price = sig.Stop });

        // Register all legs with WSS event stream for real-time routing
        if (EventStream != null)
        {
            EventStream.RegisterOrder(entryId.Value, groupId, LegType.Entry);
            if (tg1Id.HasValue) EventStream.RegisterOrder(tg1Id.Value, groupId, LegType.Tg1);
            if (tg2Id.HasValue) EventStream.RegisterOrder(tg2Id.Value, groupId, LegType.Tg2);
            if (stopId.HasValue) EventStream.RegisterOrder(stopId.Value, groupId, LegType.Stop);
        }

        _log.LogInformation("[TV-GRP] Group {G} placed: entry={E} tg1={T1} tg2={T2} stop={S}",
            groupId, entryId, tg1Id, tg2Id, stopId);

        return group;
    }

    async Task IGroupOrderExecutor.ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
    {
        if (!long.TryParse(orderId, out var id)) return;
        // Determine order type from price context: stopPrice if modifying stop, limitPrice for targets
        await ModifyOrderAsync(id, newQty ?? 0, stopPrice: newPrice);
    }

    async Task IGroupOrderExecutor.CancelOrderAsync(string orderId)
    {
        if (!long.TryParse(orderId, out var id)) return;
        await CancelOrderAsync(id);
    }

    async Task IGroupOrderExecutor.PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
    {
        var account = await GetAccountAsync();
        var symbol = FuturesSymbol.ToTradovate(ticker);
        var action = direction == Direction.Long ? "Sell" : "Buy";
        var body = new
        {
            accountSpec = account.Name, accountId = account.Id,
            action, symbol, orderQty = qty,
            orderType = "Market", isAutomated = true
        };
        var orderId = await PlaceSingleAsync(body);
        _log.LogInformation("[TV-GRP] Market close placed, ID={Id}", orderId);
    }

    // ── Private helpers ───────────────────────────────────────────

    /// <summary>
    /// Recovers the entry direction from the Orders DB — used when the app restarts
    /// mid-trade and in-memory direction is lost.
    /// </summary>
    private async Task<Direction?> RecoverDirectionAsync(SetupId setup)
    {
        if (_scopeFactory is null) return null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var lastEntry = await db.Orders
                .Where(o => o.Status == "FILLED" && o.Direction != null)
                .OrderByDescending(o => o.FilledAt)
                .FirstOrDefaultAsync();

            if (lastEntry?.Direction is not null &&
                Enum.TryParse<Direction>(lastEntry.Direction, out var dir))
                return dir;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV] Failed to recover direction from DB for setup {S}", setup);
        }
        return null;
    }

    /// <summary>
    /// Polls GET /order/item?id={orderId} for the fill price.
    /// Retries up to 15 times at 200ms intervals. Returns null on timeout or error.
    /// </summary>
    private async Task<decimal?> GetFillPriceAsync(long orderId)
    {
        const int maxRetries   = 15;
        const int delayMs      = 200;

        try
        {
            for (int i = 0; i < maxRetries; i++)
            {
                await Task.Delay(delayMs);
                var json = await GetAsync($"/order/item?id={orderId}");
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("avgFillPrice", out var fp) &&
                    fp.ValueKind == JsonValueKind.Number)
                {
                    return fp.GetDecimal();
                }

                _log.LogDebug("[TV] GetFillPrice poll {I}/{Max} — avgFillPrice not yet available for order {Id}",
                    i + 1, maxRetries, orderId);
            }

            _log.LogWarning("[TV] GetFillPrice timed out after {Max} retries for order {Id}", maxRetries, orderId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV] GetFillPriceAsync failed for order {Id}", orderId);
        }

        return null;
    }

    /// <summary>
    /// Resolves and caches the Tradovate account from GET /account/list.
    /// Uses the first account in the list.
    /// </summary>
    private async Task<AccountRef> GetAccountAsync()
    {
        if (_cachedAccount is not null) return _cachedAccount;

        var json = await GetAsync("/account/list");
        using var doc = JsonDocument.Parse(json);

        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Tradovate: /account/list returned empty array — no accounts found.");

        var name = first.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        var id   = first.TryGetProperty("id",   out var ip) ? ip.GetInt64()      : 0;

        _cachedAccount = new AccountRef(name, id);
        _log.LogInformation("[TV] Resolved account: name={Name} id={Id}", name, id);
        return _cachedAccount;
    }

    /// <summary>
    /// POST /order/placeOSO — places a market entry with two bracket legs.
    /// Returns (entryOrderId, targetOrderId, stopOrderId) parsed from the response.
    /// Tradovate returns a single orderId for the entry; the bracket leg IDs are
    /// available inside the response's "bracket1" / "bracket2" objects.
    /// </summary>
    private async Task<(long? entry, long? target, long? stop)> PlaceOsoAsync(object body)
    {
        try
        {
            var url  = $"{_auth.ApiBaseUrl}/order/placeOSO";
            var resp = await PostAsync(url, body);

            _log.LogInformation("[TV] PlaceOSO raw response: {Resp}", resp);

            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;

            long? GetId(JsonElement el)
                => el.TryGetProperty("orderId", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetInt64()
                    // Also try "id" as fallback property name
                    : el.TryGetProperty("id", out var p2) && p2.ValueKind == JsonValueKind.Number
                        ? p2.GetInt64()
                        : null;

            var entryId  = GetId(root);

            // Try bracket1/bracket2 properties (documented response format)
            var targetId = root.TryGetProperty("bracket1", out var b1) ? GetId(b1) : null;
            var stopId   = root.TryGetProperty("bracket2", out var b2) ? GetId(b2) : null;

            // Fallback: try "oso1Id" / "oso2Id" or "bracket1OrdId" / "bracket2OrdId"
            // (Tradovate response format varies by API version)
            if (targetId is null && root.TryGetProperty("oso1Id", out var o1) && o1.ValueKind == JsonValueKind.Number)
                targetId = o1.GetInt64();
            if (stopId is null && root.TryGetProperty("oso2Id", out var o2) && o2.ValueKind == JsonValueKind.Number)
                stopId = o2.GetInt64();

            _log.LogInformation("[TV] PlaceOSO parsed — entry={E} target={T} stop={S}",
                entryId, targetId, stopId);

            return (entryId, targetId, stopId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] PlaceOsoAsync failed");
            return (null, null, null);
        }
    }

    /// <summary>Places a single order (limit, stop, or market) at the given URL.
    /// Returns the Tradovate orderId from the response.</summary>
    private async Task<long?> PlaceSingleAsync(object body)
    {
        try
        {
            var url  = $"{_auth.ApiBaseUrl}/order/placeOrder";
            var resp = await PostAsync(url, body);

            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("orderId", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number)
                return idProp.GetInt64();

            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] PlaceSingleAsync failed");
            return null;
        }
    }

    /// <summary>Cancels an order via POST /order/cancelOrder.</summary>
    private async Task CancelOrderAsync(long orderId)
    {
        try
        {
            var url  = $"{_auth.ApiBaseUrl}/order/cancelOrder";
            var body = new { orderId };
            var resp = await PostAsync(url, body);
            _log.LogDebug("[TV] CancelOrder {Id} response: {Resp}", orderId, resp);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] CancelOrderAsync {Id} failed", orderId);
        }
    }

    /// <summary>Modifies an existing order via POST /order/modifyorder (stop and/or limit price).</summary>
    private async Task<bool> ModifyOrderAsync(long orderId, int qty, decimal? stopPrice = null, decimal? limitPrice = null)
    {
        try
        {
            var url  = $"{_auth.ApiBaseUrl}/order/modifyorder";
            var body = new
            {
                orderId,
                orderQty    = qty,
                orderType   = stopPrice.HasValue ? "Stop" : "Limit",
                stopPrice   = stopPrice,
                price       = limitPrice,
                isAutomated = true
            };
            var resp = await PostAsync(url, body);
            _log.LogDebug("[TV] ModifyOrder {Id} response: {Resp}", orderId, resp);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] ModifyOrderAsync {Id} failed", orderId);
            return false;
        }
    }

    /// <summary>
    /// Discovers bracket leg order IDs by querying open orders after the entry fills.
    /// Tradovate's placeOSO response may not include bracket leg IDs — the child orders
    /// are created asynchronously when the entry fills. This method polls GET /order/list
    /// and matches by price and order type.
    /// </summary>
    private async Task<(long? targetId, long? stopId)> FindBracketLegsAsync(
        long entryOrderId, decimal targetPrice, decimal stopPrice)
    {
        const int maxAttempts = 10;
        const int delayMs     = 300;

        try
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(delayMs);

                var json = await GetAsync("/order/list");
                using var doc = JsonDocument.Parse(json);

                long? targetId = null, stopId = null;

                foreach (var order in doc.RootElement.EnumerateArray())
                {
                    // Only look at working/accepted orders
                    var status = order.TryGetProperty("ordStatus", out var sp) ? sp.GetString() : null;
                    if (status is not ("Working" or "Accepted" or "Queued")) continue;

                    var orderType = order.TryGetProperty("orderType", out var otp) ? otp.GetString() : null;
                    long orderId  = order.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number
                        ? idp.GetInt64() : 0;

                    if (orderId == 0 || orderId == entryOrderId) continue;

                    // Match target: Limit order at our target price
                    if (orderType == "Limit" && targetId is null)
                    {
                        if (order.TryGetProperty("price", out var pp) &&
                            pp.ValueKind == JsonValueKind.Number &&
                            pp.GetDecimal() == targetPrice)
                        {
                            targetId = orderId;
                        }
                    }
                    // Match stop: Stop order at our stop price
                    else if (orderType == "Stop" && stopId is null)
                    {
                        if (order.TryGetProperty("stopPrice", out var spp) &&
                            spp.ValueKind == JsonValueKind.Number &&
                            spp.GetDecimal() == stopPrice)
                        {
                            stopId = orderId;
                        }
                    }

                    if (targetId is not null && stopId is not null)
                        return (targetId, stopId);
                }

                if (targetId is not null && stopId is not null)
                    return (targetId, stopId);

                _log.LogDebug("[TV] FindBracketLegs attempt {I}/{Max} — target={T} stop={S}",
                    attempt + 1, maxAttempts, targetId, stopId);
            }

            _log.LogWarning("[TV] FindBracketLegsAsync timed out after {Max} attempts", maxAttempts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] FindBracketLegsAsync failed");
        }

        return (null, null);
    }

    /// <summary>GET helper — returns response body as string.</summary>
    private async Task<string> GetAsync(string path)
    {
        var token = await _auth.GetAccessTokenAsync();
        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var url  = path.StartsWith("http") ? path : $"{_auth.ApiBaseUrl}{path}";
        _log.LogDebug("[TV] GET {Url}", url);

        var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        _log.LogDebug("[TV] GET {Url} → {Status} {Body}", url, (int)resp.StatusCode, body);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Tradovate GET {path} failed ({(int)resp.StatusCode}): {body}");

        return body;
    }

    /// <summary>POST helper — serialises body as JSON, returns response body string.</summary>
    private async Task<string> PostAsync(string url, object body)
    {
        var token = await _auth.GetAccessTokenAsync();
        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var json    = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
        _log.LogDebug("[TV] POST {Url} {Body}", url, json);

        using var resp = await http.PostAsync(url,
            new StringContent(json, Encoding.UTF8, "application/json"));

        var respBody = await resp.Content.ReadAsStringAsync();
        _log.LogDebug("[TV] POST {Url} → {Status} {Body}", url, (int)resp.StatusCode, respBody);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("[TV] POST {Url} rejected: {Status} {Body}",
                url, (int)resp.StatusCode, respBody);
            throw new InvalidOperationException(
                $"Tradovate POST {url} failed ({(int)resp.StatusCode}): {respBody}");
        }

        return respBody;
    }
}
