namespace CRV.Live.Brokers.Tradovate;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tradovate WSS event stream for real-time order updates.
/// Connects to the Tradovate websocket, authenticates, subscribes to
/// user/syncrequest, and parses order update messages into OrderEvents.
/// </summary>
public class TradovateEventStream : IBrokerEventStream
{
    private readonly TradovateAuthService _auth;
    private readonly ILogger _log;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Map Tradovate orderId → (GroupOrderId, LegType) for event routing
    private readonly Dictionary<long, (string GroupOrderId, LegType LegType, string StringOrderId)> _orderMap = new();
    private readonly object _mapLock = new();

    public event Action<OrderEvent>? OnOrderUpdate;
    public event Action? OnDisconnected;
    public bool IsConnected { get; private set; }

    public TradovateEventStream(TradovateAuthService auth, ILogger<TradovateEventStream> log)
    {
        _auth = auth;
        _log = log;
    }

    /// <summary>Register a Tradovate order ID for event routing.</summary>
    public void RegisterOrder(long tradovateOrderId, string groupOrderId, LegType legType)
    {
        lock (_mapLock)
            _orderMap[tradovateOrderId] = (groupOrderId, legType, tradovateOrderId.ToString());
    }

    /// <summary>True if the given Tradovate order ID is already mapped — used by orphan
    /// reconciliation to skip orders we already track.</summary>
    public bool IsOrderTracked(long tradovateOrderId)
    {
        lock (_mapLock)
            return _orderMap.ContainsKey(tradovateOrderId);
    }

    /// <summary>Emit a synthetic OrderEvent (e.g. when REST poll detects a fill WSS missed).</summary>
    public void EmitSyntheticFill(OrderEvent evt)
    {
        _log.LogInformation("[TV-WSS] Emitting synthetic fill for group {G} order {O}", evt.GroupOrderId, evt.OrderId);
        OnOrderUpdate?.Invoke(evt);
    }

    /// <summary>Remove all mappings for a group (on completion/cancel).</summary>
    public void UnregisterGroup(string groupOrderId)
    {
        lock (_mapLock)
        {
            var toRemove = _orderMap.Where(kv => kv.Value.GroupOrderId == groupOrderId)
                                     .Select(kv => kv.Key).ToList();
            foreach (var key in toRemove)
                _orderMap.Remove(key);
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await ConnectAndAuthAsync(_cts.Token);
        _receiveTask = ReceiveLoop(_cts.Token);
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        _cts?.Cancel();

        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None); }
            catch { /* ignore close errors */ }
        }

        if (_receiveTask != null)
        {
            try { await _receiveTask; }
            catch (OperationCanceledException) { }
        }

        _ws?.Dispose();
        _ws = null;
    }

    /// <summary>
    /// After reconnect, reconcile missed events by querying REST order/list.
    /// Emits synthetic OrderEvents for any transitions missed during disconnection.
    /// </summary>
    public async Task ReconcileAfterReconnectAsync()
    {
        _log.LogInformation("[TV-WSS] Reconciliation after reconnect — checking for missed fills");

        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var resp = await http.GetStringAsync($"{_auth.ApiBaseUrl}/order/list");
            using var doc = JsonDocument.Parse(resp);

            lock (_mapLock)
            {
                foreach (var order in doc.RootElement.EnumerateArray())
                {
                    var id = order.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number
                        ? idp.GetInt64() : 0L;
                    if (id == 0 || !_orderMap.TryGetValue(id, out var mapping)) continue;

                    var status = order.TryGetProperty("ordStatus", out var sp) ? sp.GetString() : null;
                    var fillPrice = order.TryGetProperty("avgFillPrice", out var fp) && fp.ValueKind == JsonValueKind.Number
                        ? fp.GetDecimal() : (decimal?)null;
                    var fillQty = order.TryGetProperty("filledQty", out var fq) && fq.ValueKind == JsonValueKind.Number
                        ? fq.GetInt32() : (int?)null;

                    var legStatus = status switch
                    {
                        "Filled" => OrderLegStatus.Filled,
                        "Canceled" or "Cancelled" => OrderLegStatus.Canceled,
                        "Rejected" => OrderLegStatus.Rejected,
                        _ => (OrderLegStatus?)null
                    };

                    // Only emit for terminal states we might have missed
                    if (legStatus is OrderLegStatus.Filled or OrderLegStatus.Canceled or OrderLegStatus.Rejected)
                    {
                        _log.LogInformation("[TV-WSS] Reconcile: order {Id} is {Status} — emitting synthetic event",
                            id, legStatus);
                        OnOrderUpdate?.Invoke(new OrderEvent(
                            mapping.GroupOrderId, mapping.StringOrderId, mapping.LegType,
                            legStatus.Value, fillPrice, fillQty, null, null, DateTime.UtcNow));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV-WSS] Reconciliation failed — state may be stale");
        }
    }

    private async Task ConnectAndAuthAsync(CancellationToken ct)
    {
        // Tradovate WSS URL derived from API base: https://... → wss://...
        var wssUrl = _auth.ApiBaseUrl
            .Replace("https://", "wss://")
            .Replace("/v1", "/v1/websocket");

        var token = await _auth.GetAccessTokenAsync();

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(wssUrl), ct);
        _log.LogInformation("[TV-WSS] Connected to {Url}", wssUrl);

        // Wait for SockJS open frame before authenticating
        var openFrame = await ReceiveOneAsync(ct);
        if (openFrame == "o")
            _log.LogDebug("[TV-WSS] SockJS open frame received");
        else
            _log.LogWarning("[TV-WSS] Expected 'o' frame, got: {F}", openFrame);

        // Authenticate via WSS
        var authMsg = $"authorize\n0\n\n{token}";
        await SendAsync(authMsg, ct);

        // Wait for auth response
        var resp = await ReceiveOneAsync(ct);
        if (resp.Contains("\"s\":200"))
        {
            IsConnected = true;
            _log.LogInformation("[TV-WSS] Authenticated successfully");

            // Get user ID and subscribe to order sync events
            var userId = await GetUserIdAsync(ct);
            if (userId > 0)
            {
                var syncMsg = $"user/syncrequest\n1\n\n{{\"users\":[{userId}]}}";
                await SendAsync(syncMsg, ct);
                _log.LogInformation("[TV-WSS] Subscribed to user/syncrequest for userId={UserId}", userId);
            }
            else
            {
                _log.LogWarning("[TV-WSS] Could not resolve user ID — order events may not be received");
            }
        }
        else
        {
            _log.LogError("[TV-WSS] Auth failed: {Resp}", resp);
        }
    }

    /// <summary>Resolve the Tradovate user ID from REST API for subscription.</summary>
    private async Task<long> GetUserIdAsync(CancellationToken ct)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var resp = await http.GetStringAsync($"{_auth.ApiBaseUrl}/user/list", ct);
            using var doc = JsonDocument.Parse(resp);

            foreach (var user in doc.RootElement.EnumerateArray())
            {
                if (user.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    return idProp.GetInt64();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV-WSS] Failed to get user ID");
        }
        return 0;
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var msgBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    IsConnected = false;
                    OnDisconnected?.Invoke();
                    _log.LogWarning("[TV-WSS] Server closed connection");
                    await ReconnectWithBackoffAsync(ct);
                    continue;
                }

                // Accumulate multi-frame messages
                msgBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var msg = Encoding.UTF8.GetString(msgBuffer.GetBuffer(), 0, (int)msgBuffer.Length);
                msgBuffer.SetLength(0);

                // Tradovate heartbeat
                if (msg.StartsWith("h"))
                    continue;

                ProcessMessage(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogWarning(ex, "[TV-WSS] WebSocket error — reconnecting");
            IsConnected = false;
            OnDisconnected?.Invoke();

            if (!ct.IsCancellationRequested)
                await ReconnectWithBackoffAsync(ct);
        }
    }

    private void ProcessMessage(string raw)
    {
        // Tradovate WSS messages: "a[{...},{...}]"
        if (!raw.StartsWith("a[")) return;

        try
        {
            var jsonPart = raw[1..]; // Strip "a" prefix, keep "[...]"
            using var doc = JsonDocument.Parse(jsonPart);

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("e", out var eventType)) continue;
                var eType = eventType.GetString();

                if (eType == "order" && element.TryGetProperty("d", out var data))
                    ProcessOrderUpdate(data);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[TV-WSS] Failed to parse message: {Raw}", raw[..Math.Min(200, raw.Length)]);
        }
    }

    private void ProcessOrderUpdate(JsonElement data)
    {
        if (!data.TryGetProperty("id", out var idProp)) return;
        var tradovateId = idProp.GetInt64();

        (string GroupOrderId, LegType LegType, string StringOrderId) mapping;
        lock (_mapLock)
        {
            if (!_orderMap.TryGetValue(tradovateId, out mapping)) return;
        }

        var status = data.TryGetProperty("ordStatus", out var sp) ? sp.GetString() : null;
        var fillPrice = data.TryGetProperty("avgFillPrice", out var fp) && fp.ValueKind == JsonValueKind.Number
            ? fp.GetDecimal() : (decimal?)null;
        var fillQty = data.TryGetProperty("filledQty", out var fq) && fq.ValueKind == JsonValueKind.Number
            ? fq.GetInt32() : (int?)null;

        var legStatus = status switch
        {
            "Filled" => OrderLegStatus.Filled,
            "Working" => OrderLegStatus.Working,
            "Canceled" or "Cancelled" => OrderLegStatus.Canceled,
            "Rejected" => OrderLegStatus.Rejected,
            _ => (OrderLegStatus?)null
        };

        if (legStatus == null) return;

        var evt = new OrderEvent(
            mapping.GroupOrderId, mapping.StringOrderId, mapping.LegType,
            legStatus.Value, fillPrice, fillQty, null, null, DateTime.UtcNow);

        _log.LogInformation("[TV-WSS] Order update: {OrderId} {LegType} → {Status} @ {Price}",
            tradovateId, mapping.LegType, legStatus, fillPrice);

        OnOrderUpdate?.Invoke(evt);
    }

    private async Task ReconnectWithBackoffAsync(CancellationToken ct)
    {
        int[] delays = { 1000, 2000, 5000, 10000, 30000 };

        for (int i = 0; i < delays.Length && !ct.IsCancellationRequested; i++)
        {
            _log.LogInformation("[TV-WSS] Reconnect attempt {N} in {D}ms", i + 1, delays[i]);
            await Task.Delay(delays[i], ct);

            try
            {
                _ws?.Dispose();
                await ConnectAndAuthAsync(ct);
                if (IsConnected)
                {
                    await ReconcileAfterReconnectAsync();
                    _log.LogInformation("[TV-WSS] Reconnected successfully");
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[TV-WSS] Reconnect attempt {N} failed", i + 1);
            }
        }

        _log.LogError("[TV-WSS] All reconnect attempts exhausted");
    }

    private async Task SendAsync(string message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task<string> ReceiveOneAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var result = await _ws!.ReceiveAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
