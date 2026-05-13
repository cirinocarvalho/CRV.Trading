using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live.BarBuilders;
using Microsoft.Extensions.Logging;

namespace CRV.Live.Brokers.Schwab;

/// <summary>
/// Multi-ticker <see cref="IMultiTickerBarFeed"/> for Schwab that uses a single WSS connection
/// with comma-separated symbol keys in CHART_FUTURES and LEVELONE_FUTURES subscriptions.
/// Routes incoming bars/ticks to per-ticker <see cref="RealTimeBarBuilder"/> instances
/// based on the symbol key in each content row.
/// </summary>
public sealed class SchwabMultiTickerBarFeed : IMultiTickerBarFeed
{
    private readonly SchwabAuthService              _auth;
    private readonly StrategyConfig                 _cfg;
    private readonly ILastPriceProvider             _prices;
    private readonly ILogger                        _log;
    private readonly List<string>                   _tickers;
    private readonly Dictionary<string, RealTimeBarBuilder> _builders = new();
    private readonly Channel<(Bar bar, string ticker)>      _channel;

    public event Action<decimal, DateTime, string>? OnPriceTick;

    public SchwabMultiTickerBarFeed(
        SchwabAuthService auth,
        StrategyConfig cfg,
        ILastPriceProvider prices,
        IReadOnlyList<string> tickers,
        ILogger<SchwabMultiTickerBarFeed> log)
    {
        _auth    = auth;
        _cfg     = cfg;
        _prices  = prices;
        _tickers = tickers.ToList();
        _log     = log;
        _channel = Channel.CreateUnbounded<(Bar, string)>(
            new UnboundedChannelOptions { SingleReader = true });

        foreach (var ticker in _tickers)
        {
            var t = ticker; // capture for closure
            var builder = new RealTimeBarBuilder(t, cfg.TfMinutesFor(t), prices, log);
            builder.BarClosed  += bar => _channel.Writer.TryWrite((bar, t));
            builder.BarUpdated += bar => _channel.Writer.TryWrite((bar, t));
            _builders[t] = builder;
        }
    }

    public async IAsyncEnumerable<(Bar bar, string ticker)> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        _ = Task.Run(() => ConnectAsync(ct), ct);
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
            yield return item;
    }

    public Task<IReadOnlyList<Bar>> FetchDailyBarsAsync(string ticker, int count, CancellationToken ct)
    {
        // Schwab REST historical bars are fetched separately via BackfillAsync in orchestrator
        return Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── Connection loop ──────────────────────────────────────────

    private async Task ConnectAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                var si = await _auth.GetStreamerInfoAsync();
                _log.LogInformation("Schwab multi-ticker WSS connecting to {Url} for {Tickers}…",
                    si.SocketUrl, string.Join(", ", _tickers));
                await ws.ConnectAsync(new Uri(si.SocketUrl), ct);
                _log.LogInformation("Schwab multi-ticker WSS connected.");
                attempt = 0;

                var buf = new byte[65536];

                // 1) LOGIN
                var token = await _auth.GetAccessTokenAsync();
                await SendAsync(ws, BuildLoginReq(token, si), ct);

                var loginResp = await ReceiveTextAsync(ws, buf, ct);
                if (loginResp == null)
                {
                    attempt++;
                    var d = Math.Min(60, (int)Math.Pow(2, attempt));
                    _log.LogWarning("Schwab multi-ticker WSS closed before login response. Retry in {Delay}s", d);
                    await Task.Delay(d * 1000 + Random.Shared.Next(0, 500), ct);
                    continue;
                }
                _log.LogInformation("Schwab multi-ticker WSS login response: {Resp}",
                    loginResp.Length > 500 ? loginResp[..500] + "…" : loginResp);

                if (!IsLoginSuccess(loginResp))
                {
                    attempt++;
                    var d = Math.Min(60, (int)Math.Pow(2, attempt + 3));
                    _log.LogError("Schwab multi-ticker WSS login FAILED. Retrying in {Delay}s (attempt {Attempt})", d, attempt);
                    await Task.Delay(d * 1000 + Random.Shared.Next(0, 500), ct);
                    continue;
                }

                // 2) Subscribe with comma-separated keys for ALL tickers on one connection
                var allKeys = string.Join(",", _tickers);
                await SendAsync(ws, BuildChartSubReq(allKeys, si), ct);
                await SendAsync(ws, BuildL1SubReq(allKeys, si), ct);
                _log.LogInformation("Schwab multi-ticker WSS subscribed: CHART_FUTURES + LEVELONE_FUTURES for [{Keys}]", allKeys);

                // 3) Read stream
                int msgCount = 0;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogWarning("Schwab multi-ticker WSS received Close frame: {Reason}",
                            ws.CloseStatusDescription ?? ws.CloseStatus?.ToString() ?? "unknown");
                        break;
                    }
                    if (r.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(buf, 0, r.Count);
                        ProcessMessage(text);
                        msgCount++;
                    }
                }

                attempt++;
                var dDc = Math.Min(60, (int)Math.Pow(2, attempt));
                _log.LogWarning("Schwab multi-ticker WSS disconnected (state={State}, msgs={N}). Reconnecting in {Delay}s…",
                    ws.State, msgCount, dDc);
                await Task.Delay(dDc * 1000 + Random.Shared.Next(0, 500), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                attempt++;
                var dErr = Math.Min(60, (int)Math.Pow(2, attempt));
                _log.LogError(ex, "Schwab multi-ticker stream error — reconnecting in {Delay}s (attempt {Attempt})", dErr, attempt);
                await Task.Delay(dErr * 1000 + Random.Shared.Next(0, 500), ct);
            }
        }

        _channel.Writer.TryComplete();
    }

    // ── Message processing with per-ticker routing ───────────────

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("service", out var svc)) continue;

                // CHART_FUTURES: "key" property = symbol (e.g. "/NQH26")
                if (svc.GetString() == "CHART_FUTURES" && item.TryGetProperty("content", out var content))
                {
                    foreach (var row in content.EnumerateArray())
                    {
                        // Extract symbol from "key" property for routing (same as L1)
                        string? sym = row.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : null;
                        if (sym == null || !_builders.TryGetValue(sym, out var builder))
                        {
                            // Fallback: if only one ticker, use it
                            if (_builders.Count == 1)
                                builder = _builders.Values.First();
                            else
                            {
                                _log.LogDebug("Schwab CHART_FUTURES: unknown symbol key '{Key}' — skipping", sym);
                                continue;
                            }
                        }

                        if (!row.TryGetProperty("1", out var ts)) continue;
                        var t     = DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64()).UtcDateTime;
                        decimal close = row.TryGetProperty("5", out var c) ? c.GetDecimal() : 0;
                        if (close <= 0) continue;
                        decimal high = row.TryGetProperty("3", out var h) ? h.GetDecimal() : 0;
                        decimal low  = row.TryGetProperty("4", out var l) ? l.GetDecimal() : 0;
                        long    vol  = row.TryGetProperty("6", out var v) ? v.GetInt64()   : 0;

                        var ticker = sym ?? _tickers[0];
                        _prices.UpdatePrice(ticker, close);

                        if (high > 0 && high != close) builder.OnTick(high, 0, t);
                        if (low  > 0 && low  != close) builder.OnTick(low,  0, t);
                        builder.OnTick(close, vol, t);
                    }
                }

                // LEVELONE_FUTURES: "key" property = symbol (e.g. "/NQH26"), "3" = last price
                if (svc.GetString() == "LEVELONE_FUTURES" && item.TryGetProperty("content", out var l1))
                {
                    foreach (var row in l1.EnumerateArray())
                    {
                        if (!row.TryGetProperty("3", out var last) || last.GetDecimal() <= 0) continue;

                        string? sym = row.TryGetProperty("key", out var kp) ? kp.GetString() : null;
                        if (sym == null || !_builders.TryGetValue(sym, out var builder))
                        {
                            if (_builders.Count == 1)
                                builder = _builders.Values.First();
                            else
                            {
                                _log.LogDebug("Schwab LEVELONE_FUTURES: unknown key '{Key}' — skipping", sym);
                                continue;
                            }
                        }

                        var lp  = last.GetDecimal();
                        var now = DateTime.UtcNow;
                        builder.OnTick(lp, 0, now);

                        var ticker = sym ?? _tickers[0];
                        OnPriceTick?.Invoke(lp, now, ticker);
                    }
                }
            }
        }
        catch (Exception ex) { _log.LogWarning("Schwab multi-ticker msg parse error: {E}", ex.Message); }
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, byte[] buf, CancellationToken ct)
    {
        var r = await ws.ReceiveAsync(buf, ct);
        if (r.MessageType == WebSocketMessageType.Close) return null;
        if (r.MessageType == WebSocketMessageType.Text)
            return Encoding.UTF8.GetString(buf, 0, r.Count);
        return "";
    }

    private bool IsLoginSuccess(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("response", out var resp))
                foreach (var item in resp.EnumerateArray())
                    if (item.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("code", out var code) &&
                        code.GetInt32() == 0)
                        return true;
            return false;
        }
        catch { return false; }
    }

    private static string BuildLoginReq(string token, SchwabAuthService.SchwabStreamerInfo si) =>
        JsonSerializer.Serialize(new
        {
            requests = new[] { new {
                service = "ADMIN", requestid = "0", command = "LOGIN",
                SchwabClientCustomerId = si.CustomerId,
                SchwabClientCorrelId   = si.CorrelId,
                parameters = new {
                    Authorization          = token,
                    SchwabClientChannel    = si.Channel,
                    SchwabClientFunctionId = si.FunctionId
                }
            } }
        });

    private static string BuildChartSubReq(string keys, SchwabAuthService.SchwabStreamerInfo si) =>
        JsonSerializer.Serialize(new
        {
            requests = new[] { new { service = "CHART_FUTURES", requestid = "1", command = "SUBS",
                SchwabClientCustomerId = si.CustomerId,
                SchwabClientCorrelId   = si.CorrelId,
                parameters = new { keys, fields = "0,1,2,3,4,5,6" } } }
        });

    private static string BuildL1SubReq(string keys, SchwabAuthService.SchwabStreamerInfo si) =>
        JsonSerializer.Serialize(new
        {
            requests = new[] { new { service = "LEVELONE_FUTURES", requestid = "2", command = "SUBS",
                SchwabClientCustomerId = si.CustomerId,
                SchwabClientCorrelId   = si.CorrelId,
                parameters = new { keys, fields = "0,3,4,5,8" } } }
        });

    private static async Task SendAsync(ClientWebSocket ws, string msg, CancellationToken ct)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
}
