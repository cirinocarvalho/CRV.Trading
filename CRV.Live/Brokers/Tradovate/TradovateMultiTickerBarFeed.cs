using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live.BarBuilders;
using Microsoft.Extensions.Logging;

namespace CRV.Live.Brokers.Tradovate;

/// <summary>
/// Multi-ticker <see cref="IMultiTickerBarFeed"/> for Tradovate that uses a single WSS connection
/// with separate md/getchart and md/subscribeQuote requests per symbol.
/// Routes incoming chart/quote events to per-ticker <see cref="RealTimeBarBuilder"/> instances
/// by tracking subscription request IDs → ticker mappings.
/// </summary>
public sealed class TradovateMultiTickerBarFeed : IMultiTickerBarFeed
{
    private readonly TradovateAuthService  _auth;
    private readonly StrategyConfig        _cfg;
    private readonly ILastPriceProvider    _prices;
    private readonly ILogger               _log;
    private readonly List<string>          _tickers;
    private readonly Dictionary<string, RealTimeBarBuilder> _builders = new();
    private readonly Channel<(Bar bar, string ticker)>      _channel;

    // Subscription ID tracking: request ID → ticker (set when sending subscriptions)
    // and chart realtime ID → ticker (set when parsing subscription responses)
    private readonly Dictionary<int, string> _reqIdToTicker   = new();
    private readonly Dictionary<int, string> _chartIdToTicker = new();
    private readonly Dictionary<int, string> _quoteIdToTicker = new();

    public event Action<decimal, DateTime, string>? OnPriceTick;

    /// <summary>
    /// Optional delegate called after WSS auth succeeds and before chart subscriptions.
    /// Used by replay to send replay/initializeclock.
    /// </summary>
    public Func<ClientWebSocket, CancellationToken, Task>? PostAuthAction { get; set; }

    /// <summary>
    /// When set, overrides DateTime.UtcNow for computing chart subscription timestamps.
    /// Used by replay to anchor history to the replay date.
    /// </summary>
    public DateTime? ChartStartOverride { get; set; }

    public TradovateMultiTickerBarFeed(
        TradovateAuthService auth,
        StrategyConfig cfg,
        ILastPriceProvider prices,
        IReadOnlyList<string> tickers,
        ILogger<TradovateMultiTickerBarFeed> log)
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
            var t = ticker;
            var builder = new RealTimeBarBuilder(t, cfg.ExecutionTFMinutes, prices, log);
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
        _log.LogDebug("TradovateMultiTickerBarFeed: FetchDailyBarsAsync skipped — levels seeded from realtime stream");
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
                await ConnectOnceAsync(ct);
                attempt = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                attempt++;
                var delaySec = Math.Min(60, (int)Math.Pow(2, attempt));
                var jitter   = Random.Shared.Next(0, 500);
                _log.LogError(ex, "TradovateMultiTicker stream error — reconnecting in {Delay}s (attempt {Attempt})",
                    delaySec, attempt);
                await Task.Delay(delaySec * 1000 + jitter, ct);
                continue;
            }

            if (!ct.IsCancellationRequested)
            {
                attempt++;
                var delaySec = Math.Min(60, (int)Math.Pow(2, attempt));
                var jitter   = Random.Shared.Next(0, 500);
                _log.LogWarning("TradovateMultiTicker disconnected — reconnecting in {Delay}s", delaySec);
                await Task.Delay(delaySec * 1000 + jitter, ct);
            }
        }

        _channel.Writer.TryComplete();
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        // Clear subscription mappings for reconnect
        _reqIdToTicker.Clear();
        _chartIdToTicker.Clear();
        _quoteIdToTicker.Clear();

        var wssUri = new Uri(_auth.MdWssUrl);
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(wssUri, ct);
        _log.LogInformation("TradovateMultiTicker connected to {Url} for {Tickers}",
            _auth.MdWssUrl, string.Join(", ", _tickers));

        // ── Wait for SockJS open frame ─────────────────────────────
        var openFrame = await ReceiveMessageAsync(ws, ct);
        if (openFrame == "o")
            _log.LogDebug("TradovateMultiTicker SockJS open frame received");
        else
            _log.LogWarning("TradovateMultiTicker expected 'o' frame, got: {F}", openFrame);

        // ── Authenticate ──────────────────────────────────────────
        var mdToken = await _auth.GetMdAccessTokenAsync();
        await SendFrameAsync(ws, $"authorize\n0\n\n{mdToken}", ct);
        var authResp = await ReceiveMessageAsync(ws, ct);
        _log.LogInformation("TradovateMultiTicker MD auth response: {Resp}", authResp);

        // ── Post-auth hook (replay clock init) ──────────────────
        if (PostAuthAction != null)
            await PostAuthAction(ws, ct);

        // ── Compute chart start timestamp ────────────────────────
        var etTz       = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var refTimeUtc = ChartStartOverride ?? DateTime.UtcNow;
        var nowEt      = TimeZoneInfo.ConvertTimeFromUtc(refTimeUtc, etTz);

        var earliestOrbStart = _cfg.OrbStart;
        if (_cfg.Sessions != null)
        {
            foreach (var s in _cfg.Sessions)
            {
                if (s.Enabled && s.OrbStart < earliestOrbStart)
                    earliestOrbStart = s.OrbStart;
            }
        }
        var orbStart = nowEt.Date.Add(earliestOrbStart.ToTimeSpan());
        if (orbStart > nowEt) orbStart = orbStart.AddDays(-1);
        var fromEt  = orbStart.AddMinutes(-_cfg.ExecutionTFMinutes * 20);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(fromEt, DateTimeKind.Unspecified), etTz);

        // ── Subscribe to chart + quotes for each ticker ──────────
        int reqId = 1;
        foreach (var ticker in _tickers)
        {
            var symbol = ticker; // already in Tradovate format

            // Chart subscription
            var chartReqId = reqId++;
            _reqIdToTicker[chartReqId] = ticker;
            var chartReq = JsonSerializer.Serialize(new
            {
                symbol           = symbol,
                chartDescription = new
                {
                    underlyingType  = "MinuteBar",
                    elementSize     = _cfg.ExecutionTFMinutes,
                    elementSizeUnit = "UnderlyingUnits"
                },
                timeRange = new { asFarAsTimestamp = fromUtc.ToString("O") }
            });
            await SendFrameAsync(ws, $"md/getchart\n{chartReqId}\n\n{chartReq}", ct);

            // Quote subscription
            var quoteReqId = reqId++;
            _reqIdToTicker[quoteReqId] = ticker;
            var quoteReq = JsonSerializer.Serialize(new { symbol });
            await SendFrameAsync(ws, $"md/subscribeQuote\n{quoteReqId}\n\n{quoteReq}", ct);

            _log.LogInformation("TradovateMultiTicker subscribed — {Symbol} {Tf}min (chart reqId={CId}, quote reqId={QId})",
                symbol, _cfg.ExecutionTFMinutes, chartReqId, quoteReqId);
        }

        // ── Heartbeat timer ──────────────────────────────────────
        var heartbeatLock = new SemaphoreSlim(1, 1);
        var lastActivity  = DateTime.UtcNow;

        using var heartbeatTimer = new System.Timers.Timer(2000);
        heartbeatTimer.Elapsed += async (_, _) =>
        {
            if (ct.IsCancellationRequested || ws.State != WebSocketState.Open) return;
            if ((DateTime.UtcNow - lastActivity).TotalMilliseconds < 2000) return;
            if (!await heartbeatLock.WaitAsync(0)) return;
            try
            {
                await SendFrameAsync(ws, "[]", ct);
                _log.LogDebug("TradovateMultiTicker heartbeat sent");
            }
            catch { /* connection lost — main loop handles */ }
            finally { heartbeatLock.Release(); }
        };
        heartbeatTimer.Start();

        // ── Token renewal timer ──────────────────────────────────
        using var renewTimer = new System.Timers.Timer(60 * 60 * 1000);
        renewTimer.Elapsed += async (_, _) =>
        {
            if (ct.IsCancellationRequested || ws.State != WebSocketState.Open) return;
            if (!await heartbeatLock.WaitAsync(0)) return;
            try
            {
                await _auth.RenewTokenAsync();
                var freshToken = await _auth.GetMdAccessTokenAsync();
                await SendFrameAsync(ws, $"authorize\n0\n\n{freshToken}", ct);
                _log.LogInformation("TradovateMultiTicker token renewed on active WebSocket");
            }
            catch (Exception ex) { _log.LogWarning(ex, "TradovateMultiTicker token renewal failed"); }
            finally { heartbeatLock.Release(); }
        };
        renewTimer.Start();

        // ── Message loop ─────────────────────────────────────────
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            string raw;
            try
            {
                raw = await ReceiveMessageAsync(ws, ct);
                lastActivity = DateTime.UtcNow;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("TradovateMultiTicker receive error: {E}", ex.Message);
                break;
            }

            // Heartbeat
            if (raw == "h" || raw == "[]")
            {
                await heartbeatLock.WaitAsync(ct);
                try { await SendFrameAsync(ws, "[]", ct); }
                finally { heartbeatLock.Release(); }
                continue;
            }

            if (!raw.StartsWith("a[")) continue;

            List<string> rawMessages;
            try
            {
                var jsonPart = raw[1..];
                jsonPart = Regex.Replace(jsonPart, @":,", ":null,");

                using var arr = JsonDocument.Parse(jsonPart);
                rawMessages = new List<string>();
                foreach (var el in arr.RootElement.EnumerateArray())
                {
                    rawMessages.Add(el.ValueKind == JsonValueKind.String
                        ? el.GetString()!
                        : el.GetRawText());
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("TradovateMultiTicker frame parse error: {E}", ex.Message);
                continue;
            }

            foreach (var msgJson in rawMessages)
            {
                try { ProcessMessage(msgJson); }
                catch (Exception ex)
                {
                    _log.LogWarning("TradovateMultiTicker message process error: {E}", ex.Message);
                }
            }
        }

        _log.LogWarning("TradovateMultiTicker disconnected (state={State}).", ws.State);
    }

    // ── Message processing ───────────────────────────────────────

    /// <summary>Unwrap Tradovate "d" property (can be JSON object or stringified JSON).</summary>
    private static JsonElement UnwrapD(JsonElement root, out JsonDocument? innerDoc)
    {
        innerDoc = null;
        if (!root.TryGetProperty("d", out var d)) return default;
        if (d.ValueKind == JsonValueKind.String)
        {
            var inner = d.GetString();
            if (string.IsNullOrEmpty(inner)) return default;
            innerDoc = JsonDocument.Parse(inner);
            return innerDoc.RootElement;
        }
        return d;
    }

    private void ProcessMessage(string msgJson)
    {
        using var doc = JsonDocument.Parse(msgJson);
        var root = doc.RootElement;

        // ── Subscription responses (non-event) — extract chart/quote IDs ──
        if (!root.TryGetProperty("e", out var eProp))
        {
            ParseSubscriptionResponse(root);
            return;
        }
        var evt = eProp.GetString();

        // ── Chart bars ───────────────────────────────────────────
        if (evt == "chart")
        {
            var d = UnwrapD(root, out var innerDoc);
            try
            {
                if (d.ValueKind == JsonValueKind.Undefined) return;

                // Try to route by chart ID in "d"
                string? ticker = ResolveChartTicker(d);

                // Check for "charts" array (some Tradovate responses wrap bars in charts[])
                if (d.TryGetProperty("charts", out var charts))
                {
                    foreach (var chart in charts.EnumerateArray())
                    {
                        var chartTicker = ResolveChartTicker(chart) ?? ticker;
                        if (chartTicker != null && chart.TryGetProperty("bars", out var chartBars))
                            ProcessBars(chartBars, chartTicker);
                    }
                    return;
                }

                // Direct bars on "d"
                if (d.TryGetProperty("bars", out var bars))
                {
                    ticker ??= _tickers.Count == 1 ? _tickers[0] : null;
                    if (ticker != null)
                        ProcessBars(bars, ticker);
                    else
                        _log.LogDebug("TradovateMultiTicker chart event: cannot route — no ID match and multiple tickers");
                }
            }
            finally { innerDoc?.Dispose(); }
            return;
        }

        // ── Clock sync (replay) ─────────────────────────────────
        if (evt == "clock")
        {
            var cd = UnwrapD(root, out var clockDoc);
            try
            {
                if (cd.ValueKind == JsonValueKind.Undefined) return;
                if (cd.TryGetProperty("t", out var tProp))
                {
                    var replayTime = DateTime.Parse(tProp.GetString()!, null,
                        System.Globalization.DateTimeStyles.RoundtripKind);
                    _log.LogDebug("TradovateMultiTicker replay clock: {Time:u}", replayTime);
                }
            }
            catch (Exception ex) { _log.LogDebug("Clock parse error: {E}", ex.Message); }
            finally { clockDoc?.Dispose(); }
            return;
        }

        // ── L1 quotes ───────────────────────────────────────────
        if (evt == "quote")
        {
            var qd = UnwrapD(root, out var quoteDoc);
            try
            {
                if (qd.ValueKind == JsonValueKind.Undefined) return;

                // Try to route by quote subscription ID
                string? ticker = null;
                if (qd.TryGetProperty("id", out var qIdProp) &&
                    qIdProp.ValueKind == JsonValueKind.Number)
                {
                    _quoteIdToTicker.TryGetValue(qIdProp.GetInt32(), out ticker);
                }

                // Try to find symbol in "entries" object (keyed by symbol)
                if (ticker == null && qd.TryGetProperty("entries", out var entries) &&
                    entries.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in entries.EnumerateObject())
                    {
                        // entry.Name is the symbol — find matching ticker
                        var matchedTicker = _tickers.FirstOrDefault(t =>
                            string.Equals(t, entry.Name, StringComparison.OrdinalIgnoreCase));
                        if (matchedTicker != null)
                        {
                            decimal price = 0;
                            if (entry.Value.TryGetProperty("price", out var p) && p.GetDecimal() > 0)
                                price = p.GetDecimal();
                            else if (entry.Value.TryGetProperty("trade", out var tr) &&
                                     tr.TryGetProperty("price", out var tp) && tp.GetDecimal() > 0)
                                price = tp.GetDecimal();

                            if (price > 0)
                            {
                                _prices.UpdatePrice(matchedTicker, price);
                                OnPriceTick?.Invoke(price, DateTime.UtcNow, matchedTicker);
                            }
                        }
                    }
                    return;
                }

                // Direct price on "d"
                decimal lastPrice = 0;
                if (qd.TryGetProperty("price", out var priceProp) && priceProp.GetDecimal() > 0)
                    lastPrice = priceProp.GetDecimal();
                else if (qd.TryGetProperty("trade", out var trade) &&
                         trade.TryGetProperty("price", out var tp2) && tp2.GetDecimal() > 0)
                    lastPrice = tp2.GetDecimal();

                if (lastPrice > 0)
                {
                    ticker ??= _tickers.Count == 1 ? _tickers[0] : null;
                    if (ticker != null)
                    {
                        _prices.UpdatePrice(ticker, lastPrice);
                        OnPriceTick?.Invoke(lastPrice, DateTime.UtcNow, ticker);
                    }
                }
            }
            finally { quoteDoc?.Dispose(); }
        }
    }

    /// <summary>
    /// Parse non-event subscription responses to extract chart/quote IDs for routing.
    /// Response format: {"s":200, "i":reqId, "d":{...id fields...}}
    /// </summary>
    private void ParseSubscriptionResponse(JsonElement root)
    {
        // Extract request ID — "i" field
        if (!root.TryGetProperty("i", out var iProp)) return;
        int reqId = iProp.ValueKind == JsonValueKind.Number ? iProp.GetInt32() : 0;
        if (reqId == 0 || !_reqIdToTicker.TryGetValue(reqId, out var ticker))
        {
            _log.LogDebug("TradovateMultiTicker non-event msg (reqId={I}): no ticker mapping", reqId);
            return;
        }

        var d = UnwrapD(root, out var innerDoc);
        try
        {
            if (d.ValueKind == JsonValueKind.Undefined) return;

            // Chart subscription response: d.charts[].id or d.realtimeId
            if (d.TryGetProperty("charts", out var charts))
            {
                foreach (var chart in charts.EnumerateArray())
                {
                    if (chart.TryGetProperty("id", out var idProp) &&
                        idProp.ValueKind == JsonValueKind.Number)
                    {
                        var chartId = idProp.GetInt32();
                        _chartIdToTicker[chartId] = ticker;
                        _log.LogInformation("TradovateMultiTicker mapped chartId={Id} → {Ticker}",
                            chartId, ticker);
                    }

                    // Process any historical bars in the subscription response
                    if (chart.TryGetProperty("bars", out var bars))
                        ProcessBars(bars, ticker);
                }
            }

            if (d.TryGetProperty("realtimeId", out var rtId) &&
                rtId.ValueKind == JsonValueKind.Number)
            {
                _chartIdToTicker[rtId.GetInt32()] = ticker;
                _log.LogInformation("TradovateMultiTicker mapped realtimeId={Id} → {Ticker}",
                    rtId.GetInt32(), ticker);
            }

            // Quote subscription response: d.subscriptionId or d.id
            if (d.TryGetProperty("subscriptionId", out var subId) &&
                subId.ValueKind == JsonValueKind.Number)
            {
                _quoteIdToTicker[subId.GetInt32()] = ticker;
                _log.LogInformation("TradovateMultiTicker mapped quoteSubId={Id} → {Ticker}",
                    subId.GetInt32(), ticker);
            }
            else if (d.TryGetProperty("id", out var dId) &&
                     dId.ValueKind == JsonValueKind.Number)
            {
                // Some responses use "id" instead of "subscriptionId"
                _quoteIdToTicker[dId.GetInt32()] = ticker;
            }

            // Also process direct bars in subscription response (d.bars without charts wrapper)
            if (d.TryGetProperty("bars", out var directBars))
                ProcessBars(directBars, ticker);
        }
        finally { innerDoc?.Dispose(); }
    }

    /// <summary>Resolve ticker from chart event's "id" field using the chartId→ticker map.</summary>
    private string? ResolveChartTicker(JsonElement d)
    {
        if (d.TryGetProperty("id", out var idProp) &&
            idProp.ValueKind == JsonValueKind.Number &&
            _chartIdToTicker.TryGetValue(idProp.GetInt32(), out var ticker))
            return ticker;
        return null;
    }

    /// <summary>Process a JSON array of bars, routing to the correct builder.</summary>
    private void ProcessBars(JsonElement bars, string ticker)
    {
        if (!_builders.TryGetValue(ticker, out var builder))
        {
            _log.LogDebug("TradovateMultiTicker: no builder for ticker {Ticker}", ticker);
            return;
        }

        foreach (var b in bars.EnumerateArray())
        {
            if (!b.TryGetProperty("timestamp", out var ts)) continue;
            var epochMs = ts.GetInt64();
            var time    = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;

            decimal open  = b.TryGetProperty("open",  out var o) ? o.GetDecimal() : 0;
            decimal high  = b.TryGetProperty("high",  out var h) ? h.GetDecimal() : 0;
            decimal low   = b.TryGetProperty("low",   out var l) ? l.GetDecimal() : 0;
            decimal close = b.TryGetProperty("close", out var c) ? c.GetDecimal() : 0;
            if (close <= 0) continue;

            long upVol   = b.TryGetProperty("upVolume",   out var uv) ? uv.GetInt64() : 0;
            long downVol = b.TryGetProperty("downVolume", out var dv) ? dv.GetInt64() : 0;
            long volume  = upVol + downVol;

            bool isHistorical = b.TryGetProperty("isHistorical", out var ih) && ih.GetBoolean();

            _prices.UpdatePrice(ticker, close);

            if (isHistorical)
            {
                _channel.Writer.TryWrite((new Bar(
                    time,
                    open  > 0 ? open  : close,
                    high  > 0 ? high  : close,
                    low   > 0 ? low   : close,
                    close,
                    volume,
                    IsConfirmed: true,
                    Ticker: ticker), ticker));
            }
            else
            {
                if (high > 0 && high != close) builder.OnTick(high, 0, time);
                if (low  > 0 && low  != close) builder.OnTick(low,  0, time);
                builder.OnTick(close, volume, time);
                OnPriceTick?.Invoke(close, time, ticker);
            }
        }
    }

    // ── WebSocket helpers ────────────────────────────────────────

    private static async Task SendFrameAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb  = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Tradovate MD WebSocket closed by server.");
            sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
        } while (!result.EndOfMessage);

        return sb.ToString();
    }
}
