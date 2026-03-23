using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live.BarBuilders;
using Microsoft.Extensions.Logging;

namespace CRV.Live.Brokers.Tradovate;

/// <summary>
/// IBarFeed implementation for Tradovate using the Market Data WebSocket.
/// Connects to wss://md.tradovateapi.com/v1/websocket,
/// authenticates with mdAccessToken, subscribes to OHLCV chart data and
/// L1 quote ticks for real-time price updates.
///
/// Historical bars (isHistorical=true from getchart response) are written
/// directly to the channel as confirmed bars for warmup.
/// Live ticks are routed through RealTimeBarBuilder, which aggregates them
/// into confirmed OHLCV bars at the configured execution timeframe.
/// </summary>
public class TradovateBarFeed : IBarFeed
{
    private readonly TradovateAuthService _auth;
    private readonly StrategyConfig       _cfg;
    private readonly ILastPriceProvider   _prices;
    private readonly ILogger              _log;

    private readonly System.Threading.Channels.Channel<Bar> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<Bar>();

    /// <inheritdoc />
    public event Action<decimal, DateTime>? OnPriceTick;

    /// <summary>
    /// Optional delegate called after WSS auth succeeds and before chart subscription.
    /// Used by replay to send replay/initializeclock.
    /// </summary>
    public Func<ClientWebSocket, CancellationToken, Task>? PostAuthAction { get; set; }

    /// <summary>
    /// When set, overrides DateTime.UtcNow for computing the chart subscription's
    /// asFarAsTimestamp. Used by replay to anchor history to the replay date.
    /// </summary>
    public DateTime? ChartStartOverride { get; set; }

    public TradovateBarFeed(
        TradovateAuthService auth,
        StrategyConfig cfg,
        ILastPriceProvider prices,
        ILogger<TradovateBarFeed> log)
    {
        _auth   = auth;
        _cfg    = cfg;
        _prices = prices;
        _log    = log;
    }

    public async IAsyncEnumerable<Bar> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _ = Task.Run(() => ConnectAsync(ct), ct);
        await foreach (var bar in _channel.Reader.ReadAllAsync(ct))
            yield return bar;
    }

    /// <summary>
    /// Tradovate demo MD API doesn't support historical daily bar requests reliably.
    /// Module levels (ATR, prior-day H/L) will be computed from realtime bars as they arrive,
    /// same approach as Schwab. Returns empty — the default IBarFeed behavior.
    /// </summary>
    public Task<IReadOnlyList<Bar>> FetchDailyBarsAsync(string ticker, int count, CancellationToken ct)
    {
        _log.LogDebug("TradovateBarFeed: FetchDailyBarsAsync skipped — levels seeded from realtime stream");
        return Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var builder = new RealTimeBarBuilder(_cfg.Ticker, _cfg.ExecutionTFMinutes, _prices, _log);
        builder.BarClosed  += bar => _channel.Writer.TryWrite(bar);
        builder.BarUpdated += bar => _channel.Writer.TryWrite(bar);

        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(builder, ct);
                attempt = 0; // reset on clean disconnect
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                attempt++;
                var delaySec = Math.Min(60, (int)Math.Pow(2, attempt));
                var jitter   = Random.Shared.Next(0, 500);
                _log.LogError(ex, "TradovateBarFeed stream error — reconnecting in {Delay}s (attempt {Attempt})",
                    delaySec, attempt);
                await Task.Delay(delaySec * 1000 + jitter, ct);
                continue;
            }

            if (!ct.IsCancellationRequested)
            {
                attempt++;
                var delaySec = Math.Min(60, (int)Math.Pow(2, attempt));
                var jitter   = Random.Shared.Next(0, 500);
                _log.LogWarning("TradovateBarFeed disconnected — reconnecting in {Delay}s", delaySec);
                await Task.Delay(delaySec * 1000 + jitter, ct);
            }
        }
    }

    private async Task ConnectOnceAsync(RealTimeBarBuilder builder, CancellationToken ct)
    {
        var symbol = FuturesSymbol.ToTradovate(_cfg.Ticker);
        var wssUri = new Uri(_auth.MdWssUrl);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(wssUri, ct);
        _log.LogInformation("TradovateBarFeed connected to {Url}", _auth.MdWssUrl);

        // ── Wait for SockJS open frame ─────────────────────────────
        var openFrame = await ReceiveMessageAsync(ws, ct);
        if (openFrame == "o")
            _log.LogDebug("TradovateBarFeed SockJS open frame received");
        else
            _log.LogWarning("TradovateBarFeed expected 'o' frame, got: {F}", openFrame);

        // ── Authenticate ──────────────────────────────────────────
        var mdToken = await _auth.GetMdAccessTokenAsync();
        await SendFrameAsync(ws, $"authorize\n0\n\n{mdToken}", ct);

        var authResp = await ReceiveMessageAsync(ws, ct);
        _log.LogInformation("Tradovate MD auth response: {Resp}", authResp);

        // ── Post-auth hook (replay clock init) ──────────────────
        if (PostAuthAction != null)
            await PostAuthAction(ws, ct);

        // ── Subscribe to OHLCV chart data ─────────────────────────
        // Use asFarAsTimestamp to cover from 20 TF-bars before the earliest ORB start
        // across all enabled sessions. This ensures ORB is fully built even when the
        // engine starts mid-session (e.g. London at 03:00, not just NY at 09:30).
        var etTz     = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var refTimeUtc = ChartStartOverride ?? DateTime.UtcNow;
        var nowEt      = TimeZoneInfo.ConvertTimeFromUtc(refTimeUtc, etTz);

        // Find the earliest OrbStart across all enabled sessions (or fall back to flat config)
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
        // If earliest ORB start is in the evening (e.g. Asia 19:00) and it's now past midnight,
        // the ORB start was yesterday evening — reach back to yesterday
        if (orbStart > nowEt)
            orbStart = orbStart.AddDays(-1);
        var fromEt   = orbStart.AddMinutes(-_cfg.ExecutionTFMinutes * 20);
        var fromUtc  = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(fromEt, DateTimeKind.Unspecified), etTz);

        int reqId    = 1;
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
        await SendFrameAsync(ws, $"md/getchart\n{reqId++}\n\n{chartReq}", ct);

        // ── Subscribe to L1 quote for last-price updates ──────────
        var quoteReq = JsonSerializer.Serialize(new { symbol = symbol });
        await SendFrameAsync(ws, $"md/subscribeQuote\n{reqId++}\n\n{quoteReq}", ct);

        _log.LogInformation("TradovateBarFeed subscribed — {Symbol} {Tf}min", symbol, _cfg.ExecutionTFMinutes);

        // ── Message loop with proactive heartbeats ─────────────────
        // Tradovate requires a heartbeat (empty "[]" frame) every 2.5s of inactivity.
        // CancelAfter on ws.ReceiveAsync aborts the WebSocket, so instead we use a
        // background timer that sends heartbeats independently of the receive loop.
        var heartbeatLock = new SemaphoreSlim(1, 1);
        var lastActivity  = DateTime.UtcNow;

        using var heartbeatTimer = new System.Timers.Timer(2000); // check every 2s
        heartbeatTimer.Elapsed += async (_, _) =>
        {
            if (ct.IsCancellationRequested || ws.State != WebSocketState.Open) return;
            if ((DateTime.UtcNow - lastActivity).TotalMilliseconds < 2000) return;
            if (!await heartbeatLock.WaitAsync(0)) return; // skip if send in progress
            try
            {
                await SendFrameAsync(ws, "[]", ct);
                _log.LogDebug("TradovateBarFeed heartbeat sent");
            }
            catch { /* connection lost — main loop will handle */ }
            finally { heartbeatLock.Release(); }
        };
        heartbeatTimer.Start();

        // ── Proactive token renewal — re-authorize before the 85-min expiry ──
        // Renew every 60 minutes to stay well within the token lifetime.
        using var renewTimer = new System.Timers.Timer(60 * 60 * 1000); // every 60 min
        renewTimer.Elapsed += async (_, _) =>
        {
            if (ct.IsCancellationRequested || ws.State != WebSocketState.Open) return;
            if (!await heartbeatLock.WaitAsync(0)) return;
            try
            {
                await _auth.RenewTokenAsync();
                var freshToken = await _auth.GetMdAccessTokenAsync();
                await SendFrameAsync(ws, $"authorize\n0\n\n{freshToken}", ct);
                _log.LogInformation("TradovateBarFeed token renewed on active WebSocket");
            }
            catch (Exception ex) { _log.LogWarning(ex, "TradovateBarFeed token renewal failed — will reconnect on disconnect"); }
            finally { heartbeatLock.Release(); }
        };
        renewTimer.Start();

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
                _log.LogWarning("TradovateBarFeed receive error: {E}", ex.Message);
                break;
            }

            // Tradovate heartbeat — just acknowledge
            if (raw == "h" || raw == "[]")
            {
                await heartbeatLock.WaitAsync(ct);
                try { await SendFrameAsync(ws, "[]", ct); }
                finally { heartbeatLock.Release(); }
                continue;
            }

            // Tradovate data frames arrive as: a["<json>"] or a[{...},{...}]
            if (!raw.StartsWith("a[")) continue;

            List<string> rawMessages;
            try
            {
                // Tradovate sometimes sends malformed JSON (e.g. "i":, with missing value)
                // Fix known patterns before parsing
                var jsonPart = raw[1..];
                jsonPart = System.Text.RegularExpressions.Regex.Replace(jsonPart, @":,", ":null,");

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
                _log.LogWarning("TradovateBarFeed frame parse error: {E} | raw[1..50]={Raw}",
                    ex.Message, raw.Length > 51 ? raw[..51] : raw);
                continue;
            }

            foreach (var msgJson in rawMessages)
            {
                try
                {
                    ProcessMessage(msgJson, builder);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("TradovateBarFeed message process error: {E}", ex.Message);
                }
            }
        }

        _log.LogWarning("TradovateBarFeed disconnected (state={State}).", ws.State);
    }

    /// <summary>
    /// Unwrap the "d" property which Tradovate sends as either a JSON object or a JSON string.
    /// Caller must dispose the returned doc if non-null.
    /// </summary>
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

    private void ProcessMessage(string msgJson, RealTimeBarBuilder builder)
    {
        using var doc = JsonDocument.Parse(msgJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("e", out var eProp))
        {
            // Log subscription responses (s/i/d) for debugging
            _log.LogInformation("TradovateBarFeed non-event msg: {Msg}",
                msgJson.Length > 200 ? msgJson[..200] : msgJson);
            return;
        }
        var evt = eProp.GetString();

        // ── OHLCV chart bars ──────────────────────────────────────
        if (evt == "chart")
        {
            var d = UnwrapD(root, out var innerDoc);
            try
            {
            if (d.ValueKind == JsonValueKind.Undefined) return;
            if (!d.TryGetProperty("bars", out var bars)) return;
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

                // Tradovate returns upVolume + downVolume separately
                long upVol   = b.TryGetProperty("upVolume",   out var uv) ? uv.GetInt64() : 0;
                long downVol = b.TryGetProperty("downVolume", out var dv) ? dv.GetInt64() : 0;
                long volume  = upVol + downVol;

                // Historical bars (from warmup replay) are already closed — write directly.
                // Live ticks route through RealTimeBarBuilder for proper OHLCV aggregation.
                bool isHistorical = b.TryGetProperty("isHistorical", out var ih)
                    ? ih.GetBoolean()
                    : false;

                _prices.UpdatePrice(_cfg.Ticker, close);

                if (isHistorical)
                {
                    _channel.Writer.TryWrite(new Bar(
                        time,
                        open  > 0 ? open  : close,
                        high  > 0 ? high  : close,
                        low   > 0 ? low   : close,
                        close,
                        volume,
                        IsConfirmed: true));
                }
                else
                {
                    // Feed high/low/close through the builder to preserve full bar range.
                    // Tradovate sends bar updates, not individual ticks, so push the extremes
                    // explicitly so RealTimeBarBuilder captures correct H/L.
                    if (high > 0 && high != close) builder.OnTick(high, 0, time);
                    if (low  > 0 && low  != close) builder.OnTick(low,  0, time);
                    builder.OnTick(close, volume, time);
                    OnPriceTick?.Invoke(close, time);
                }
            }

            return;
            }
            finally { innerDoc?.Dispose(); }
        }

        // ── Replay clock sync ───────────────────────────────────
        if (evt == "clock")
        {
            var cd = UnwrapD(root, out var clockDoc);
            try
            {
                if (cd.ValueKind == JsonValueKind.Undefined) return;
                if (cd.TryGetProperty("t", out var tProp))
                {
                    var replayTime = DateTime.Parse(tProp.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
                    var speed = cd.TryGetProperty("s", out var sProp) ? sProp.GetInt32() : 0;
                    _log.LogDebug("Replay clock: {Time:u} speed={Speed}%", replayTime, speed);
                }
            }
            catch (Exception ex) { _log.LogDebug("Clock parse error: {E}", ex.Message); }
            finally { clockDoc?.Dispose(); }
            return;
        }

        // ── L1 quote — last price updates ────────────────────────
        if (evt == "quote")
        {
            var qd = UnwrapD(root, out var quoteDoc);
            try
            {
                if (qd.ValueKind == JsonValueKind.Undefined) return;
                decimal lastPrice = 0;
                if (qd.TryGetProperty("price", out var priceProp) && priceProp.GetDecimal() > 0)
                    lastPrice = priceProp.GetDecimal();
                else if (qd.TryGetProperty("trade", out var trade) &&
                         trade.TryGetProperty("price", out var tp) && tp.GetDecimal() > 0)
                    lastPrice = tp.GetDecimal();

                if (lastPrice > 0)
                {
                    _prices.UpdatePrice(_cfg.Ticker, lastPrice);
                    OnPriceTick?.Invoke(lastPrice, DateTime.UtcNow);
                }
            }
            finally { quoteDoc?.Dispose(); }
        }
    }

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
