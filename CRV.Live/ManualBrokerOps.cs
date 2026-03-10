using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CRV.Live.Brokers;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;

namespace CRV.Live;

// ── View records (serialised to JSON by the page handlers) ───────

public record PositionView(
    string  Symbol,
    string  Direction,      // "LONG" | "SHORT"
    decimal Quantity,
    decimal AveragePrice,
    decimal? UnrealizedPnl,
    decimal? DayPnl);

public record OrderView(
    string   OrderId,
    string   Symbol,
    string   Status,        // raw broker status code
    string   StatusLabel,   // human-readable
    string   OrderType,     // MARKET | LIMIT | STOP | …
    string   Action,        // BUY | SELL | BUY_TO_OPEN | SELL_TO_CLOSE | …
    decimal  Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    string   PlacedTime,
    bool     CanCancel);

/// <summary>
/// One-shot broker operations used by the Manual Trading and Orders pages.
/// All methods are stateless — no order-ID tracking, no DI plumbing required.
/// </summary>
public static class ManualBrokerOps
{
    // Base URLs are derived from the auth services (configured via appsettings.json).

    // ── Positions — TradeStation ──────────────────────────────────

    public static async Task<List<PositionView>> GetPositionsTradeStationAsync(
        TradeStationAuthService auth, string accountId)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        var res  = await http.GetAsync($"{auth.ApiBaseUrl}/v3/brokerage/accounts/{accountId}/positions");
        var body = await res.Content.ReadAsStringAsync();
        var list = new List<PositionView>();
        if (!res.IsSuccessStatusCode) return list;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("Positions", out var positions)) return list;

        foreach (var p in positions.EnumerateArray())
        {
            if (!p.TryGetProperty("AssetType", out var at) || at.GetString() != "FUTURE") continue;

            var symbol = p.TryGetProperty("Symbol", out var s) ? s.GetString() ?? "" : "";
            var qtyStr = p.TryGetProperty("Quantity", out var q) ? q.GetString() ?? "0" : "0";
            decimal qty = decimal.TryParse(qtyStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var qd) ? qd : 0;

            var dir = qty > 0 ? "LONG" : qty < 0 ? "SHORT" : "FLAT";
            if (dir == "FLAT") continue;

            var avgPrice = ParseDs(p, "AveragePrice");
            var unreal   = ParseDs(p, "UnrealizedProfitLoss");
            var dayPnl   = ParseDs(p, "TodaysProfitLoss");

            list.Add(new PositionView(symbol, dir, Math.Abs(qty), avgPrice ?? 0, unreal, dayPnl));
        }
        return list;
    }

    // ── Positions — Schwab ────────────────────────────────────────

    public static async Task<List<PositionView>> GetPositionsSchwabAsync(
        SchwabAuthService auth, string accountId)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        var res  = await http.GetAsync($"{auth.ApiBaseUrl}/trader/v1/accounts/{accountId}?fields=positions");
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Schwab positions API ({(int)res.StatusCode}): {body}");

        var list = new List<PositionView>();

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("securitiesAccount", out var acct)) return list;
        if (!acct.TryGetProperty("positions", out var positions)) return list;

        foreach (var p in positions.EnumerateArray())
        {
            if (!p.TryGetProperty("instrument", out var instr)) continue;
            if (!instr.TryGetProperty("assetType", out var at) || at.GetString() != "FUTURE") continue;

            var symbol   = instr.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
            var longQty  = p.TryGetProperty("longQuantity",  out var lq) ? lq.GetDecimal() : 0m;
            var shortQty = p.TryGetProperty("shortQuantity", out var sq) ? sq.GetDecimal() : 0m;
            var avgPrice = p.TryGetProperty("averagePrice",  out var ap) ? ap.GetDecimal() : 0m;

            string dir; decimal qty;
            if      (longQty  > 0) { dir = "LONG";  qty = longQty;  }
            else if (shortQty > 0) { dir = "SHORT"; qty = shortQty; }
            else continue;

            // Broker provides open P&L directly
            decimal? unreal = null;
            if      (p.TryGetProperty("longOpenProfitLoss",  out var lop)) unreal = lop.GetDecimal();
            else if (p.TryGetProperty("shortOpenProfitLoss", out var sop)) unreal = sop.GetDecimal();

            decimal? dayPnl = p.TryGetProperty("currentDayProfitLoss", out var dp) ? dp.GetDecimal() : null;

            list.Add(new PositionView(symbol, dir, qty, avgPrice, unreal, dayPnl));
        }
        return list;
    }

    // ── Flat at Market — TradeStation ─────────────────────────────

    /// <param name="isCurrentLong">True if position is LONG → we SELL to close.</param>
    public static async Task<string> FlatAtMarketTradeStationAsync(
        TradeStationAuthService auth, string accountId, string symbol, int contracts, bool isCurrentLong)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        var closeAction = isCurrentLong ? "SELL" : "BUYTOCOVER";
        var body = JsonSerializer.Serialize(new
        {
            AccountID   = accountId,
            Symbol      = symbol,
            Quantity    = contracts.ToString(),
            OrderType   = "Market",
            TradeAction = closeAction,
            TimeInForce = new { Duration = "DAY" },
            Route       = "Intelligent"
        });

        var res     = await http.PostAsync($"{auth.ApiBaseUrl}/v3/orderexecution/orders", Json(body));
        var resBody = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            return $"Flat TS order failed ({res.StatusCode}): {resBody}";

        string? orderId = ExtractTsOrderId(resBody);
        return $"Flat: {closeAction} {contracts}× {symbol} at Market" +
               (orderId is not null ? $" (TS order {orderId})" : "");
    }

    // ── Flat at Market — Schwab ───────────────────────────────────

    public static async Task<string> FlatAtMarketSchwabAsync(
        SchwabAuthService auth, string accountId, string symbol, int contracts, bool isCurrentLong)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        // Futures instructions: SELL / BUY (not SELL_TO_CLOSE / BUY_TO_CLOSE — those are options-only)
        var instruction = isCurrentLong ? "SELL" : "BUY";
        var body = JsonSerializer.Serialize(new
        {
            orderStrategyType  = "SINGLE",
            session            = "NORMAL",
            duration           = "DAY",
            orderType          = "MARKET",
            quantity           = contracts,
            orderLegCollection = new[]
            {
                new { instruction, quantity = contracts,
                      instrument = new { symbol, assetType = "FUTURE" } }
            }
        });

        var url     = $"{auth.ApiBaseUrl}/trader/v1/accounts/{accountId}/orders";
        var res     = await http.PostAsync(url, Json(body));
        var resBody = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            return $"Flat Schwab order failed ({res.StatusCode}): {resBody}";

        var orderId = res.Headers.Location?.ToString().Split('/').LastOrDefault();
        return $"Flat: {instruction} {contracts}× {symbol} at Market" +
               (orderId is not null ? $" (Schwab order {orderId})" : "");
    }

    // ── Cancel All Open Orders — TradeStation ─────────────────────

    public static async Task<List<string>> CancelAllTradeStationAsync(
        TradeStationAuthService auth, string accountId, string symbol)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        var getRes  = await http.GetAsync($"{auth.ApiBaseUrl}/v3/brokerage/accounts/{accountId}/orders?status=Open");
        var getBody = await getRes.Content.ReadAsStringAsync();
        var results = new List<string>();

        if (!getRes.IsSuccessStatusCode)
        { results.Add($"Failed to list TS open orders ({getRes.StatusCode})"); return results; }

        using var doc = JsonDocument.Parse(getBody);
        if (!doc.RootElement.TryGetProperty("Orders", out var orders))
        { results.Add($"No open orders found for {symbol}."); return results; }

        foreach (var order in orders.EnumerateArray())
        {
            if (!order.TryGetProperty("Symbol", out var sym) ||
                !string.Equals(sym.GetString(), symbol, StringComparison.OrdinalIgnoreCase)) continue;

            if (!order.TryGetProperty("OrderID", out var oid)) continue;
            var orderId = oid.GetString();
            if (orderId is null) continue;

            var r = await http.DeleteAsync($"{auth.ApiBaseUrl}/v3/orderexecution/orders/{orderId}");
            results.Add(r.IsSuccessStatusCode
                ? $"Cancelled TS order {orderId} ({symbol})"
                : $"Cancel TS order {orderId} failed ({r.StatusCode})");
        }

        if (results.Count == 0) results.Add($"No open orders found for {symbol}.");
        return results;
    }

    // ── Cancel All Open Orders — Schwab ──────────────────────────

    public static async Task<List<string>> CancelAllSchwabAsync(
        SchwabAuthService auth, string accountId, string symbol)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);
        var baseUrl = $"{auth.ApiBaseUrl}/trader/v1/accounts/{accountId}/orders";

        // No status filter — fetch all recent orders and cancel any that are cancellable.
        // ACCEPTED orders would be missed if we filter on WORKING only.
        var getRes  = await http.GetAsync($"{baseUrl}?maxResults=100");
        var getBody = await getRes.Content.ReadAsStringAsync();
        var results = new List<string>();

        if (!getRes.IsSuccessStatusCode)
        { results.Add($"Failed to list Schwab open orders ({getRes.StatusCode})"); return results; }

        using var doc = JsonDocument.Parse(getBody);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        { results.Add($"No open orders found for {symbol}."); return results; }

        var symNorm = symbol.TrimStart('/');
        foreach (var order in doc.RootElement.EnumerateArray())
        {
            if (!MatchesSchwabSymbol(order, symNorm)) continue;
            var st = order.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "" : "";
            if (!SchCanCancel(st)) continue;   // skip filled, expired, already cancelled, etc.
            if (!order.TryGetProperty("orderId", out var oidProp)) continue;
            var orderId = oidProp.GetInt64().ToString();

            var r = await http.DeleteAsync($"{baseUrl}/{orderId}");
            results.Add(r.IsSuccessStatusCode
                ? $"Cancelled Schwab order {orderId} ({symbol})"
                : $"Cancel Schwab order {orderId} failed ({r.StatusCode})");
        }

        if (results.Count == 0) results.Add($"No open orders found for {symbol}.");
        return results;
    }

    // ── Cancel Single Order — TradeStation ───────────────────────

    public static async Task<string> CancelOrderTradeStationAsync(
        TradeStationAuthService auth, string orderId)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);
        var r = await http.DeleteAsync($"{auth.ApiBaseUrl}/v3/orderexecution/orders/{orderId}");
        return r.IsSuccessStatusCode
            ? $"TS order {orderId} cancelled."
            : $"Cancel TS order {orderId} failed ({r.StatusCode}): {await r.Content.ReadAsStringAsync()}";
    }

    // ── Cancel Single Order — Schwab ─────────────────────────────

    public static async Task<string> CancelOrderSchwabAsync(
        SchwabAuthService auth, string accountId, string orderId)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);
        var r = await http.DeleteAsync($"{auth.ApiBaseUrl}/trader/v1/accounts/{accountId}/orders/{orderId}");
        return r.IsSuccessStatusCode
            ? $"Schwab order {orderId} cancelled."
            : $"Cancel Schwab order {orderId} failed ({r.StatusCode}): {await r.Content.ReadAsStringAsync()}";
    }

    // ── Orders — TradeStation ─────────────────────────────────────

    public static async Task<List<OrderView>> GetOrdersTradeStationAsync(
        TradeStationAuthService auth, string accountId, string status, DateTime from, DateTime to)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        // TS status for "all": omit status parameter; otherwise pass the code directly
        var statusParam = status == "ALL" ? "" : $"&status={status}";
        var sinceStr    = from.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var url = $"{auth.ApiBaseUrl}/v3/brokerage/accounts/{accountId}/orders?since={sinceStr}{statusParam}";

        var res  = await http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"TradeStation orders API ({(int)res.StatusCode}): {body}");

        var list = new List<OrderView>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("Orders", out var orders)) return list;

        foreach (var o in orders.EnumerateArray())
        {
            var orderId   = o.TryGetProperty("OrderID",       out var oid) ? oid.GetString() ?? "" : "";
            var sym       = o.TryGetProperty("Symbol",        out var s)   ? s.GetString()   ?? "" : "";
            var st        = o.TryGetProperty("Status",        out var sst) ? sst.GetString() ?? "" : "";
            var oType     = o.TryGetProperty("OrderType",     out var ot)  ? ot.GetString()  ?? "" : "";
            var action    = o.TryGetProperty("TradeAction",   out var ta)  ? ta.GetString()  ?? "" : "";
            var qtyStr    = o.TryGetProperty("Quantity",      out var q)   ? q.GetString()   ?? "0" : "0";
            var placedStr = o.TryGetProperty("OpenedDateTime",out var dt)  ? dt.GetString()  ?? "" : "";
            decimal qty   = decimal.TryParse(qtyStr, out var qd) ? qd : 0;
            var lp        = ParseDs(o, "LimitPrice");
            var sp        = ParseDs(o, "StopPrice");

            list.Add(new OrderView(orderId, sym, st, TsStatusLabel(st), oType, action,
                qty, lp, sp, placedStr, TsCanCancel(st)));
        }
        return list;
    }

    // ── Orders — Schwab ───────────────────────────────────────────

    public static async Task<List<OrderView>> GetOrdersSchwabAsync(
        SchwabAuthService auth, string accountId, string status, DateTime from, DateTime to)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        // Use UTC-kind dates so the trailing Z is accurate
        var fromStr     = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var toStr       = DateTime.SpecifyKind(to.Date.AddDays(1), DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var statusParam = status == "ALL" ? "" : $"&status={status}";
        var url = $"{auth.ApiBaseUrl}/trader/v1/accounts/{accountId}/orders" +
                  $"?fromEnteredTime={fromStr}&toEnteredTime={toStr}&maxResults=200{statusParam}";

        var res  = await http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Schwab orders API ({(int)res.StatusCode}): {body}");

        var list = new List<OrderView>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"Unexpected Schwab orders response: {body[..Math.Min(300, body.Length)]}");

        foreach (var o in doc.RootElement.EnumerateArray())
        {
            var orderId  = o.TryGetProperty("orderId",    out var oid) && oid.ValueKind == JsonValueKind.Number ? oid.GetInt64().ToString() : "";
            var st       = o.TryGetProperty("status",     out var sst) ? sst.GetString() ?? "" : "";
            var oType    = o.TryGetProperty("orderType",  out var ot)  ? ot.GetString()  ?? "" : "";
            var entered  = o.TryGetProperty("enteredTime",out var et)  ? et.GetString()  ?? "" : "";
            // price / stopPrice can be JSON null for MARKET/TRIGGER orders — guard ValueKind
            var lp       = o.TryGetProperty("price",      out var pp)  && pp.ValueKind  == JsonValueKind.Number ? (decimal?)pp.GetDecimal()  : null;
            var sp       = o.TryGetProperty("stopPrice",  out var spp) && spp.ValueKind == JsonValueKind.Number ? (decimal?)spp.GetDecimal() : null;

            string sym = "", action = "", assetType = "";
            decimal qty = 0;
            if (o.TryGetProperty("orderLegCollection", out var legs) &&
                legs.ValueKind == JsonValueKind.Array)
            {
                var first = legs.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Undefined)
                {
                    action = first.TryGetProperty("instruction", out var ins) ? ins.GetString() ?? "" : "";
                    qty    = first.TryGetProperty("quantity",    out var qq)  && qq.ValueKind == JsonValueKind.Number ? qq.GetDecimal() : 0;
                    if (first.TryGetProperty("instrument", out var instr))
                    {
                        sym       = instr.TryGetProperty("symbol",    out var ss) ? ss.GetString() ?? "" : "";
                        assetType = instr.TryGetProperty("assetType", out var at) ? at.GetString() ?? "" : "";
                    }
                }
            }

            // Only include futures orders — skip equity, options, etc.
            if (!string.Equals(assetType, "FUTURE", StringComparison.OrdinalIgnoreCase)) continue;

            list.Add(new OrderView(orderId, sym, st, SchStatusLabel(st), oType, action,
                qty, lp, sp, entered, SchCanCancel(st)));
        }
        return list;
    }

    // ── Mock ─────────────────────────────────────────────────────

    public static Task<List<string>> CancelAllMockAsync(string symbol)
        => Task.FromResult(new List<string> { $"Mock: cancel all open orders for {symbol} (no-op)" });

    public static Task<string> FlatAtMarketMockAsync(string symbol, int contracts, bool isCurrentLong)
        => Task.FromResult($"Mock: flat {(isCurrentLong ? "SELL" : "BUY")} {contracts}× {symbol} at Market (no-op)");

    public static Task<List<PositionView>> GetPositionsMockAsync()
        => Task.FromResult(new List<PositionView>());

    public static Task<List<OrderView>> GetOrdersMockAsync(MockBrokerExecutor? exec = null)
    {
        if (exec == null) return Task.FromResult(new List<OrderView>());
        var orders = exec.GetOrders().Select(o => new OrderView(
            OrderId:     o.OrderId,
            Symbol:      o.Symbol,
            Status:      o.Status,
            StatusLabel: o.Status,
            OrderType:   o.OrderType,
            Action:      o.Action,
            Quantity:    o.Quantity,
            LimitPrice:  o.LimitPrice,
            StopPrice:   o.StopPrice,
            PlacedTime:  o.PlacedAt.ToString("o"),
            CanCancel:   o.Status == "WORKING"
        )).ToList();
        return Task.FromResult(orders);
    }

    public static Task<string> CancelOrderMockAsync(string orderId)
        => Task.FromResult($"Mock: cancel order {orderId} (no-op)");

    // ── Positions — Tradovate ─────────────────────────────────────

    public static async Task<List<PositionView>> GetPositionsTradovateAsync(
        TradovateAuthService auth, string accountId)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        var res  = await http.GetAsync($"{auth.ApiBaseUrl}/position/list");
        var body = await res.Content.ReadAsStringAsync();
        var list = new List<PositionView>();
        if (!res.IsSuccessStatusCode) return list;

        using var doc = JsonDocument.Parse(body);
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var netPos = p.TryGetProperty("netPos", out var np) ? np.GetDecimal() : 0;
            if (netPos == 0) continue;
            var contractId = p.TryGetProperty("contractId", out var cid) ? cid.GetInt64() : 0;
            var dir        = netPos > 0 ? "LONG" : "SHORT";
            var avgPrice   = p.TryGetProperty("netPrice", out var avg) ? avg.GetDecimal() : 0m;

            // Look up symbol from contractId
            var symRes  = await http.GetAsync($"{auth.ApiBaseUrl}/contract/item?id={contractId}");
            var symBody = await symRes.Content.ReadAsStringAsync();
            string symbol = contractId.ToString();
            try
            {
                using var sd = JsonDocument.Parse(symBody);
                symbol = sd.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? symbol : symbol;
            }
            catch { }

            list.Add(new PositionView(symbol, dir, Math.Abs(netPos), avgPrice, null, null));
        }
        return list;
    }

    // ── Orders — Tradovate ────────────────────────────────────────

    public static async Task<List<OrderView>> GetOrdersTradovateAsync(
        TradovateAuthService auth, string accountId,
        string statusFilter, DateTime from, DateTime to)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);

        var res  = await http.GetAsync($"{auth.ApiBaseUrl}/order/list");
        var body = await res.Content.ReadAsStringAsync();
        var list = new List<OrderView>();
        if (!res.IsSuccessStatusCode) return list;

        using var doc = JsonDocument.Parse(body);
        foreach (var o in doc.RootElement.EnumerateArray())
        {
            var status = o.TryGetProperty("ordStatus", out var s) ? s.GetString() ?? "" : "";
            if (statusFilter != "ALL" && !string.Equals(status, statusFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var orderId    = o.TryGetProperty("id",         out var id) ? id.GetInt64().ToString() : "";
            var action     = o.TryGetProperty("action",     out var a)  ? a.GetString()  ?? "" : "";
            var qty        = o.TryGetProperty("totalQty",   out var q)  ? q.GetDecimal() : 0;
            var limit      = o.TryGetProperty("price",      out var lp) ? (decimal?)lp.GetDecimal() : null;
            var stopPrice  = o.TryGetProperty("stopPrice",  out var sp) ? (decimal?)sp.GetDecimal() : null;
            var placed     = o.TryGetProperty("timestamp",  out var ts) ? ts.GetString() ?? "" : "";
            var ordType    = o.TryGetProperty("orderType",  out var ot) ? ot.GetString() ?? "" : "";
            var contractId = o.TryGetProperty("contractId", out var ci) ? ci.GetInt64() : 0L;

            var canCancel  = status is "Working" or "PendingNew";
            list.Add(new OrderView(orderId, contractId.ToString(), status, status, ordType,
                action, qty, limit, stopPrice, placed, canCancel));
        }
        return list;
    }

    // ── Cancel Single Order — Tradovate ──────────────────────────

    public static async Task<string> CancelOrderTradovateAsync(
        TradovateAuthService auth, string orderId)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);
        var body = new StringContent(
            JsonSerializer.Serialize(new { orderId = long.Parse(orderId) }),
            Encoding.UTF8, "application/json");
        var res = await http.PostAsync($"{auth.ApiBaseUrl}/order/cancelOrder", body);
        return res.IsSuccessStatusCode
            ? $"Tradovate order {orderId} cancel request sent."
            : $"Cancel failed ({res.StatusCode}): {await res.Content.ReadAsStringAsync()}";
    }

    // ── Flat at Market — Tradovate ────────────────────────────────

    public static async Task<string> FlatAtMarketTradovateAsync(
        TradovateAuthService auth, string accountId,
        string symbol, int contracts, bool isCurrentLong)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);
        var accountList = await http.GetStringAsync($"{auth.ApiBaseUrl}/account/list");
        using var ad    = JsonDocument.Parse(accountList);
        var acc         = ad.RootElement.EnumerateArray().First();
        var accSpec     = acc.GetProperty("name").GetString() ?? "";
        var accId       = acc.GetProperty("id").GetInt64();

        var action = isCurrentLong ? "Sell" : "Buy";
        var body = new StringContent(
            JsonSerializer.Serialize(new
            {
                accountSpec = accSpec,
                accountId   = accId,
                action,
                symbol      = FuturesSymbol.ToTradovate(symbol),
                orderQty    = contracts,
                orderType   = "Market",
                isAutomated = true
            }), Encoding.UTF8, "application/json");

        var res  = await http.PostAsync($"{auth.ApiBaseUrl}/order/placeOrder", body);
        var resp = await res.Content.ReadAsStringAsync();
        return res.IsSuccessStatusCode
            ? $"Flat Market order placed for {contracts}ct."
            : $"Flat failed ({res.StatusCode}): {resp}";
    }

    // ── Cancel All Open Orders — Tradovate ───────────────────────

    public static async Task<List<string>> CancelAllTradovateAsync(
        TradovateAuthService auth, string accountId, string symbol)
    {
        var orders = await GetOrdersTradovateAsync(auth, accountId, "Working",
            DateTime.Today, DateTime.Today.AddDays(1));
        var sym    = FuturesSymbol.ToTradovate(symbol);
        var results = new List<string>();
        foreach (var o in orders.Where(o => o.CanCancel))
        {
            var r = await CancelOrderTradovateAsync(auth, o.OrderId);
            results.Add(r);
        }
        if (results.Count == 0) results.Add($"No working orders found for {symbol}.");
        return results;
    }

    // ── Place OCO bracket — Tradovate ─────────────────────────────

    public static async Task<string> PlaceOcoTradovateAsync(
        TradovateAuthService auth, string accountId,
        string symbol, string action, int qty,
        decimal entryPrice, decimal stopPrice, decimal targetPrice,
        bool isMarket = true)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = BearerClient(token);
        var accountList = await http.GetStringAsync($"{auth.ApiBaseUrl}/account/list");
        using var ad    = JsonDocument.Parse(accountList);
        var acc         = ad.RootElement.EnumerateArray().First();
        var accSpec     = acc.GetProperty("name").GetString() ?? "";
        var accId       = acc.GetProperty("id").GetInt64();
        var exitAct     = action == "Buy" ? "Sell" : "Buy";

        var body = new StringContent(JsonSerializer.Serialize(new
        {
            accountSpec  = accSpec,
            accountId    = accId,
            action,
            symbol       = FuturesSymbol.ToTradovate(symbol),
            orderQty     = qty,
            orderType    = isMarket ? "Market" : "Limit",
            price        = isMarket ? (double?)null : (double)entryPrice,
            isAutomated  = true,
            bracket1     = new { action = exitAct, orderType = "Limit",  price     = (double)targetPrice },
            bracket2     = new { action = exitAct, orderType = "Stop",   stopPrice = (double)stopPrice }
        }), Encoding.UTF8, "application/json");

        var res  = await http.PostAsync($"{auth.ApiBaseUrl}/order/placeOSO", body);
        var resp = await res.Content.ReadAsStringAsync();
        return res.IsSuccessStatusCode
            ? $"OCO bracket placed for {qty}ct."
            : $"Order failed ({res.StatusCode}): {resp}";
    }

    // ── Status helpers ───────────────────────────────────────────

    private static bool TsCanCancel(string s) =>
        s is "OPN" or "ACK" or "FPR" or "LAT";

    private static string TsStatusLabel(string s) => s switch
    {
        "OPN" => "Open",
        "ACK" => "Acknowledged",
        "FPR" => "Partial Fill",
        "FLL" => "Filled",
        "CAN" => "Cancelled",
        "REJ" => "Rejected",
        "EXP" => "Expired",
        "TSC" => "Too Late to Cancel",
        "STP" => "Stopped",
        _     => s
    };

    private static bool SchCanCancel(string s) =>
        s is "WORKING" or "QUEUED" or "PENDING_ACTIVATION" or "ACCEPTED"
          or "AWAITING_PARENT_ORDER" or "AWAITING_CONDITION" or "AWAITING_STOP_CONDITION";

    private static string SchStatusLabel(string s) => s switch
    {
        "WORKING"                => "Working",
        "QUEUED"                 => "Queued",
        "ACCEPTED"               => "Accepted",
        "FILLED"                 => "Filled",
        "CANCELED"               => "Cancelled",
        "REJECTED"               => "Rejected",
        "EXPIRED"                => "Expired",
        "PENDING_ACTIVATION"     => "Pending Activation",
        "PENDING_CANCEL"         => "Pending Cancel",
        "REPLACED"               => "Replaced",
        "AWAITING_PARENT_ORDER"  => "Awaiting Parent",
        "AWAITING_CONDITION"     => "Awaiting Condition",
        "AWAITING_STOP_CONDITION"=> "Awaiting Stop",
        _                        => s
    };

    // ── Shared helpers ───────────────────────────────────────────

    private static bool MatchesSchwabSymbol(JsonElement order, string symNorm)
    {
        if (!order.TryGetProperty("orderLegCollection", out var legs) ||
            legs.ValueKind != JsonValueKind.Array) return false;
        foreach (var leg in legs.EnumerateArray())
        {
            if (leg.TryGetProperty("instrument", out var instr) &&
                instr.TryGetProperty("symbol", out var s) &&
                string.Equals(s.GetString()?.TrimStart('/'), symNorm,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Parses a string-valued decimal property (TradeStation style).</summary>
    private static decimal? ParseDs(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDecimal();
        return decimal.TryParse(p.GetString(),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string? ExtractTsOrderId(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("Orders", out var arr))
            {
                var first = arr.EnumerateArray().FirstOrDefault();
                if (first.TryGetProperty("OrderID", out var id)) return id.GetString();
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static HttpClient BearerClient(string token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static StringContent Json(string json)
        => new(json, Encoding.UTF8, "application/json");
}
