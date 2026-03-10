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

    private async Task ConnectAsync(CancellationToken ct)
    {
        var builder = new RealTimeBarBuilder(_cfg.Ticker, _cfg.ExecutionTFMinutes, _prices, _log);
        builder.BarClosed  += bar => _channel.Writer.TryWrite(bar);
        builder.BarUpdated += bar => _channel.Writer.TryWrite(bar);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(builder, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "TradovateBarFeed stream error — reconnecting in 5s");
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(5_000, ct);
        }
    }

    private async Task ConnectOnceAsync(RealTimeBarBuilder builder, CancellationToken ct)
    {
        var symbol = FuturesSymbol.ToTradovate(_cfg.Ticker);
        var wssUri = new Uri(_auth.MdWssUrl);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(wssUri, ct);
        _log.LogInformation("TradovateBarFeed connected to {Url}", _auth.MdWssUrl);

        // ── Authenticate ──────────────────────────────────────────
        var mdToken = await _auth.GetMdAccessTokenAsync();
        await SendFrameAsync(ws, $"authorize\n0\n\n{mdToken}", ct);

        var authResp = await ReceiveMessageAsync(ws, ct);
        _log.LogDebug("Tradovate MD auth response: {Resp}", authResp);

        // ── Subscribe to OHLCV chart data ─────────────────────────
        // Use asFarAsTimestamp to cover from 20 TF-bars before the ORB start through now.
        // This ensures ORB is fully built even when the engine starts mid-session.
        var etTz     = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var nowEt    = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etTz);
        var orbStart = nowEt.Date.Add(_cfg.OrbStart.ToTimeSpan());
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

        // ── Message loop ──────────────────────────────────────────
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            string raw;
            try
            {
                raw = await ReceiveMessageAsync(ws, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("TradovateBarFeed receive error: {E}", ex.Message);
                break;
            }

            // Tradovate heartbeat — echo back to keep connection alive
            if (raw == "h")
            {
                await SendFrameAsync(ws, "h", ct);
                continue;
            }

            // Tradovate data frames arrive as: a["<json>"] or a["<json>","<json>",...]
            if (!raw.StartsWith("a[")) continue;

            string[] rawMessages;
            try
            {
                rawMessages = JsonSerializer.Deserialize<string[]>(raw[1..]) ?? [];
            }
            catch (Exception ex)
            {
                _log.LogWarning("TradovateBarFeed frame parse error: {E}", ex.Message);
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

    private void ProcessMessage(string msgJson, RealTimeBarBuilder builder)
    {
        using var doc = JsonDocument.Parse(msgJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("e", out var eProp)) return;
        var evt = eProp.GetString();

        // ── OHLCV chart bars ──────────────────────────────────────
        if (evt == "chart" && root.TryGetProperty("d", out var d))
        {
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

        // ── L1 quote — last price updates ────────────────────────
        if (evt == "quote" && root.TryGetProperty("d", out var qd))
        {
            // Tradovate quote: { "price": <decimal>, ... } or nested { "trade": { "price": ... } }
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
