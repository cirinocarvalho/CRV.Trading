using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

namespace CRV.Backtest.DataLoaders;

// ── CSV Loader ────────────────────────────────────────────────
public class CsvBarLoader
{
    private readonly ILogger _log;
    public CsvBarLoader(ILogger<CsvBarLoader> log) => _log = log;

    public async IAsyncEnumerable<Bar> LoadAsync(
        string filePath, DateTime from, DateTime to,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(filePath);
        string? header = await reader.ReadLineAsync(ct);
        if (header == null) yield break;

        var cols = header.Split(',').Select(c => c.Trim().ToLower()).ToArray();
        int iDt = Array.IndexOf(cols, "datetime");
        int iD  = Array.IndexOf(cols, "date");
        int iT  = Array.IndexOf(cols, "time");
        int iO  = Array.FindIndex(cols, c => c.Contains("open"));
        int iH  = Array.FindIndex(cols, c => c.Contains("high"));
        int iL  = Array.FindIndex(cols, c => c.Contains("low"));
        int iC  = Array.FindIndex(cols, c => c.Contains("close"));
        int iV  = Array.FindIndex(cols, c => c.Contains("volume"));

        int line = 1;
        while (!ct.IsCancellationRequested)
        {
            var row = await reader.ReadLineAsync(ct);
            if (row == null) break;
            if (string.IsNullOrWhiteSpace(row)) continue;
            line++;
            var p = row.Split(',');
            if (p.Length < 5) continue;

            DateTime dt;
            try
            {
                dt = iDt >= 0
                    ? DateTime.Parse(p[iDt].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                    : DateTime.Parse($"{p[iD].Trim()} {p[iT].Trim()}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            }
            catch { _log.LogWarning("CSV line {N}: bad datetime", line); continue; }

            if (dt < from || dt > to) continue;

            yield return new Bar(dt,
                Dec(p, iO), Dec(p, iH), Dec(p, iL), Dec(p, iC), Lng(p, iV));
        }
    }

    private static decimal Dec(string[] p, int i) =>
        i >= 0 && i < p.Length && decimal.TryParse(p[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static long Lng(string[] p, int i) =>
        i >= 0 && i < p.Length && long.TryParse(p[i], out var v) ? v : 0;
}

// ── Schwab Historical Loader ──────────────────────────────────
public class SchwabHistoricalLoader
{
    private readonly string  _token;
    private readonly string  _apiBaseUrl;
    private readonly ILogger _log;
    private readonly IHttpClientFactory? _httpFactory;
    public SchwabHistoricalLoader(string token, ILogger<SchwabHistoricalLoader> log,
        string apiBaseUrl = "https://api.schwabapi.com", IHttpClientFactory? httpFactory = null)
    { _token = token; _log = log; _apiBaseUrl = apiBaseUrl.TrimEnd('/'); _httpFactory = httpFactory; }

    public async IAsyncEnumerable<Bar> LoadAsync(
        string symbol, int tf, DateTime from, DateTime to,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = _httpFactory?.CreateClient("Schwab") ?? new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        // Schwab market data API requires the leading slash for futures symbols (e.g. /NQH26)
        var apiSymbol = symbol.StartsWith('/') ? symbol : "/" + symbol;

        foreach (var (cFrom, cTo) in ChunkByMonth(from, to))
        {
            ct.ThrowIfCancellationRequested();
            long sMs = new DateTimeOffset(cFrom).ToUnixTimeMilliseconds();
            long eMs = new DateTimeOffset(cTo).ToUnixTimeMilliseconds();
            // needExtendedHoursData=false for futures — futures trade nearly 24h,
            // extended hours flag is for equities only.
            var url = $"{_apiBaseUrl}/marketdata/v1/pricehistory?symbol={Uri.EscapeDataString(apiSymbol)}" +
                      $"&periodType=day&frequencyType=minute&frequency={tf}&startDate={sMs}&endDate={eMs}&needExtendedHoursData=false";
            // A chunk that cannot be fetched is a hole in the series, not a minor
            // inconvenience: skipping it silently produced backtests of the same
            // configuration that disagreed by 24.6%. Stop the run instead.
            HttpResponseMessage resp;
            try { resp = await http.GetAsync(url, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new BarLoadException(
                    $"Schwab history request failed for {symbol} {cFrom:u}..{cTo:u}.", ex);
            }
            if (!resp.IsSuccessStatusCode)
                throw new BarLoadException(
                    $"Schwab history returned HTTP {(int)resp.StatusCode} for {symbol} {cFrom:u}..{cTo:u}.");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("candles", out var candles)) continue;

            foreach (var c in candles.EnumerateArray())
            {
                long tsMs = c.TryGetProperty("datetime", out var ts) ? ts.GetInt64() : 0;
                if (tsMs == 0) continue;
                var t = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime;
                if (t < from || t > to) continue;
                yield return new Bar(t,
                    c.TryGetProperty("open",   out var o)  ? o.GetDecimal()  : 0,
                    c.TryGetProperty("high",   out var h)  ? h.GetDecimal()  : 0,
                    c.TryGetProperty("low",    out var l)  ? l.GetDecimal()  : 0,
                    c.TryGetProperty("close",  out var cl) ? cl.GetDecimal() : 0,
                    c.TryGetProperty("volume", out var v)  ? v.GetInt64()    : 0);
            }
            await Task.Delay(200, ct);
        }
    }

    private static IEnumerable<(DateTime, DateTime)> ChunkByMonth(DateTime from, DateTime to)
    {
        var cur = from;
        while (cur < to)
        {
            var end = new DateTime(cur.Year, cur.Month, DateTime.DaysInMonth(cur.Year, cur.Month), 23, 59, 59, DateTimeKind.Utc);
            if (end > to) end = to;
            yield return (cur, end);
            cur = end.AddSeconds(1);
        }
    }
}

// ── TradeStation Historical Loader ────────────────────────────
public class TradeStationHistoricalLoader
{
    private readonly string  _token;
    private readonly string  _apiBaseUrl;
    private readonly ILogger _log;
    private readonly IHttpClientFactory? _httpFactory;
    public TradeStationHistoricalLoader(string token, ILogger<TradeStationHistoricalLoader> log,
        string apiBaseUrl = "https://api.tradestation.com", IHttpClientFactory? httpFactory = null)
    { _token = token; _log = log; _apiBaseUrl = apiBaseUrl.TrimEnd('/'); _httpFactory = httpFactory; }

    public async IAsyncEnumerable<Bar> LoadAsync(
        string symbol, int tf, DateTime from, DateTime to,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = _httpFactory?.CreateClient("TradeStation") ?? new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        foreach (var (cFrom, cTo) in ChunkByWeeks(from, to, 2))
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{_apiBaseUrl}/v3/marketdata/barcharts/{Uri.EscapeDataString(symbol)}" +
                      $"?interval={tf}&unit=Minute&firstdate={cFrom:MM-dd-yyyy}&lastdate={cTo:MM-dd-yyyy}&sessiontemplate=Default";

            HttpResponseMessage resp;
            try { resp = await http.GetAsync(url, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new BarLoadException(
                    $"TradeStation history request failed for {symbol} {cFrom:u}..{cTo:u}.", ex);
            }
            if (!resp.IsSuccessStatusCode)
                throw new BarLoadException(
                    $"TradeStation history returned HTTP {(int)resp.StatusCode} for {symbol} {cFrom:u}..{cTo:u}.");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Bars", out var bars)) continue;

            foreach (var b in bars.EnumerateArray())
            {
                if (!b.TryGetProperty("TimeStamp", out var tsProp)) continue;
                if (!DateTime.TryParse(tsProp.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) continue;
                if (t < from || t > to) continue;
                yield return new Bar(t,
                    Dec(b, "Open"), Dec(b, "High"), Dec(b, "Low"), Dec(b, "Close"),
                    (long)Dec(b, "TotalVolume"));
            }
            await Task.Delay(200, ct);
        }
    }

    private static decimal Dec(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number ? p.GetDecimal()
            : decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static IEnumerable<(DateTime, DateTime)> ChunkByWeeks(DateTime from, DateTime to, int weeks)
    {
        var cur = from;
        while (cur < to)
        {
            var end = cur.AddDays(weeks * 7);
            if (end > to) end = to;
            yield return (cur, end);
            cur = end.AddSeconds(1);
        }
    }
}

// ── Tradovate Historical Loader ───────────────────────────────
public class TradovateHistoricalLoader
{
    private readonly string  _mdToken;
    private readonly string  _mdWssUrl;
    private readonly ILogger _log;

    public TradovateHistoricalLoader(string mdToken, ILogger<TradovateHistoricalLoader> log,
        string mdWssUrl = "wss://md.tradovateapi.com/v1/websocket")
    { _mdToken = mdToken; _log = log; _mdWssUrl = mdWssUrl; }

    public async IAsyncEnumerable<Bar> LoadAsync(
        string symbol, int tf, DateTime from, DateTime to,
        bool continuous = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Tradovate uses 1-digit year: NQH26 → NQH6
        var tvSymbol = Regex.Replace(symbol.TrimStart('/'), @"(\d{2})$", m => m.Value[1..]);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(_mdWssUrl), ct);

        // Wait for SockJS open frame
        var openFrame = await ReceiveMessageAsync(ws, ct);
        if (openFrame != "o")
            _log.LogWarning("TradovateHistorical expected 'o' frame, got: {F}", openFrame);

        // Authenticate
        await SendFrameAsync(ws, $"authorize\n0\n\n{_mdToken}", ct);
        _ = await ReceiveMessageAsync(ws, ct);

        // Request all bars going back to 'from'.
        var chartReq = JsonSerializer.Serialize(new
        {
            symbol           = tvSymbol,
            chartDescription = new
            {
                underlyingType  = "MinuteBar",
                elementSize     = tf,
                elementSizeUnit = "UnderlyingUnits"
            },
            timeRange  = new { asFarAsTimestamp = from.ToUniversalTime().ToString("O") }
        });
        await SendFrameAsync(ws, $"md/getchart\n1\n\n{chartReq}", ct);

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            string raw;
            try { raw = await ReceiveMessageAsync(ws, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "TradovateHistorical WSS receive failed"); break; }

            if (raw == "h" || raw == "[]") { await SendFrameAsync(ws, "[]", ct); continue; }
            if (!raw.StartsWith("a[")) continue;

            List<string> msgs;
            try
            {
                var jsonPart = raw[1..];
                jsonPart = Regex.Replace(jsonPart, @":,", ":null,");
                using var arr = JsonDocument.Parse(jsonPart);
                msgs = new List<string>();
                foreach (var el in arr.RootElement.EnumerateArray())
                    msgs.Add(el.ValueKind == JsonValueKind.String ? el.GetString()! : el.GetRawText());
            }
            catch (Exception ex) { _log.LogDebug(ex, "TradovateHistorical failed to parse frame"); continue; }

            bool doneWithHistorical = false;
            foreach (var msgJson in msgs)
            {
                using var doc = JsonDocument.Parse(msgJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("e", out var e) || e.GetString() != "chart") continue;
                if (!root.TryGetProperty("d", out var d) || !d.TryGetProperty("bars", out var bars)) continue;

                foreach (var b in bars.EnumerateArray())
                {
                    if (!b.TryGetProperty("timestamp", out var ts)) continue;
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64()).UtcDateTime;

                    bool isHistorical = b.TryGetProperty("isHistorical", out var ih) && ih.GetBoolean();
                    if (!isHistorical) { doneWithHistorical = true; break; }
                    if (time < from || time > to) continue;

                    decimal open  = b.TryGetProperty("open",  out var o) ? o.GetDecimal() : 0;
                    decimal high  = b.TryGetProperty("high",  out var h) ? h.GetDecimal() : 0;
                    decimal low   = b.TryGetProperty("low",   out var l) ? l.GetDecimal() : 0;
                    decimal close = b.TryGetProperty("close", out var c) ? c.GetDecimal() : 0;
                    if (close <= 0) continue;

                    long upVol   = b.TryGetProperty("upVolume",   out var uv) ? uv.GetInt64() : 0;
                    long downVol = b.TryGetProperty("downVolume", out var dv) ? dv.GetInt64() : 0;

                    yield return new Bar(time,
                        open  > 0 ? open  : close,
                        high  > 0 ? high  : close,
                        low   > 0 ? low   : close,
                        close,
                        upVol + downVol,
                        IsConfirmed: true);
                }
                if (doneWithHistorical) break;
            }
            if (doneWithHistorical) break;
        }

        _log.LogInformation("TradovateHistoricalLoader complete for {Symbol}", tvSymbol);
        if (ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private static async Task SendFrameAsync(ClientWebSocket ws, string msg, CancellationToken ct)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb  = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Tradovate WS closed.");
            sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
        } while (!result.EndOfMessage);
        return sb.ToString();
    }
}
