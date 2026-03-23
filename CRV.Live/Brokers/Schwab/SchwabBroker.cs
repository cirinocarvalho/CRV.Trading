using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live.BarBuilders;
using Microsoft.Extensions.Logging;

namespace CRV.Live.Brokers.Schwab;

// ── Auth ─────────────────────────────────────────────────────
/// <summary>
/// Manages Schwab OAuth2 tokens using the Authorization Code flow (retail accounts).
/// Tokens are persisted to a JSON file so they survive app restarts.
/// Call BuildAuthorizationUrl() → redirect user → receive code callback → ExchangeCodeAsync().
/// Subsequent GetAccessTokenAsync() calls auto-refresh using the stored refresh token.
/// </summary>
public class SchwabAuthService
{
    private readonly string _appKey;
    private readonly string _appSecret;
    private readonly string _redirectUri;
    private readonly string _tokenFile;
    private readonly IHttpClientFactory? _httpFactory;

    /// <summary>Schwab REST API base URL (e.g. https://api.schwabapi.com). Configurable via appsettings.</summary>
    public string ApiBaseUrl { get; }

    /// <summary>Schwab WSS streaming URL. Configurable via appsettings; falls back to API-returned URL.</summary>
    public string WssBaseUrl { get; }

    private readonly SemaphoreSlim _lock = new(1, 1);

    private string   _accessToken  = "";
    private string   _refreshToken = "";
    private DateTime _tokenExpiry  = DateTime.MinValue;

    /// <summary>True if a refresh token is stored (user has authorized).</summary>
    public bool IsAuthenticated => !string.IsNullOrEmpty(_refreshToken);

    public SchwabAuthService(
        string appKey, string appSecret, string redirectUri, string tokenFile,
        string apiBaseUrl = "https://api.schwabapi.com",
        string wssBaseUrl = "wss://streamer-api.schwab.com/ws",
        IHttpClientFactory? httpFactory = null)
    {
        _appKey      = appKey;
        _appSecret   = appSecret;
        _redirectUri = redirectUri;
        _tokenFile   = tokenFile;
        _httpFactory = httpFactory;
        ApiBaseUrl   = apiBaseUrl.TrimEnd('/');
        WssBaseUrl   = wssBaseUrl.TrimEnd('/');
        LoadTokens();
    }

    private HttpClient CreateClient() => _httpFactory?.CreateClient("Schwab") ?? new HttpClient();

    /// <summary>Returns the Schwab OAuth2 authorization URL to redirect the user to.</summary>
    public string BuildAuthorizationUrl()
        => $"{ApiBaseUrl}/v1/oauth/authorize?client_id={Uri.EscapeDataString(_appKey)}" +
           $"&redirect_uri={Uri.EscapeDataString(_redirectUri)}" +
           $"&response_type=code";

    /// <summary>Exchanges the authorization code (from OAuth callback) for access + refresh tokens.</summary>
    public async Task ExchangeCodeAsync(string code)
    {
        await _lock.WaitAsync();
        try
        {
            var resp = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"]   = "authorization_code",
                ["code"]         = code,
                ["redirect_uri"] = _redirectUri,
            });
            ParseAndSave(resp);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns a valid access token, refreshing it automatically if expired.</summary>
    public async Task<string> GetAccessTokenAsync()
    {
        // Fast path — token still valid
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
            return _accessToken;

        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring the lock
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
                return _accessToken;

            if (string.IsNullOrEmpty(_refreshToken))
                throw new InvalidOperationException(
                    "Schwab: not authenticated. Visit /auth/schwab to connect your account.");

            var resp = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = _refreshToken,
            });
            ParseAndSave(resp);
            return _accessToken;
        }
        finally { _lock.Release(); }
    }

    public async Task<string> GetStreamerUrlAsync()
        => (await GetStreamerInfoAsync()).SocketUrl;

    /// <summary>Returns all streamer connection fields needed for WSS login.</summary>
    public async Task<SchwabStreamerInfo> GetStreamerInfoAsync()
    {
        var token = await GetAccessTokenAsync();
        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync($"{ApiBaseUrl}/trader/v1/userPreference");
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var si = doc.RootElement.GetProperty("streamerInfo")[0];
        var apiSocketUrl = si.GetProperty("streamerSocketUrl").GetString() ?? "";
        return new SchwabStreamerInfo(
            SocketUrl:    !string.IsNullOrEmpty(WssBaseUrl) ? WssBaseUrl : apiSocketUrl,
            CustomerId:   si.TryGetProperty("schwabClientCustomerId", out var cid)   ? cid.GetString()   ?? "" : "",
            CorrelId:     si.TryGetProperty("schwabClientCorrelId",   out var cor)   ? cor.GetString()   ?? "" : "",
            Channel:      si.TryGetProperty("schwabClientChannel",    out var ch)    ? ch.GetString()    ?? "" : "",
            FunctionId:   si.TryGetProperty("schwabClientFunctionId", out var fid)   ? fid.GetString()   ?? "" : ""
        );
    }

    public record SchwabStreamerInfo(
        string SocketUrl, string CustomerId, string CorrelId,
        string Channel, string FunctionId);

    // ── Helpers ──────────────────────────────────────────────────

    private async Task<string> PostTokenAsync(Dictionary<string, string> fields)
    {
        using var http = CreateClient();
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_appKey}:{_appSecret}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);

        var resp = await http.PostAsync($"{ApiBaseUrl}/v1/oauth/token", new FormUrlEncodedContent(fields));
        var json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Schwab token request failed ({(int)resp.StatusCode}): {json}");

        return json;
    }

    private void ParseAndSave(string json)
    {
        using var doc = JsonDocument.Parse(json);

        _accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(
            doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() - 60 : 1740);

        // Schwab returns a new refresh token on every grant — always update it
        if (doc.RootElement.TryGetProperty("refresh_token", out var rt) &&
            !string.IsNullOrEmpty(rt.GetString()))
            _refreshToken = rt.GetString()!;

        SaveTokens();
    }

    private void LoadTokens()
    {
        if (!File.Exists(_tokenFile)) return;
        try
        {
            var json = File.ReadAllText(_tokenFile);
            using var doc = JsonDocument.Parse(json);
            _accessToken  = doc.RootElement.TryGetProperty("access_token",  out var at)  ? at.GetString()  ?? "" : "";
            _refreshToken = doc.RootElement.TryGetProperty("refresh_token",  out var rt)  ? rt.GetString()  ?? "" : "";
            _tokenExpiry  = doc.RootElement.TryGetProperty("expiry_utc",     out var exp)
                ? DateTime.Parse(exp.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTime.MinValue;
        }
        catch (Exception) { /* non-critical: corrupt or missing token file — will re-auth */ }
    }

    private void SaveTokens()
    {
        try
        {
            var dir = Path.GetDirectoryName(_tokenFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_tokenFile, JsonSerializer.Serialize(new
            {
                access_token  = _accessToken,
                refresh_token = _refreshToken,
                expiry_utc    = _tokenExpiry.ToString("O"),
            }));
        }
        catch (Exception) { /* non-critical: token save failure — will refresh on next GetAccessTokenAsync */ }
    }
}

// ── Bar Feed ─────────────────────────────────────────────────
public class SchwabBarFeed : IBarFeed
{
    private readonly SchwabAuthService  _auth;
    private readonly StrategyConfig     _cfg;
    private readonly ILastPriceProvider _prices;
    private readonly ILogger            _log;
    private readonly System.Threading.Channels.Channel<Bar> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<Bar>();

    /// <inheritdoc />
    public event Action<decimal, DateTime>? OnPriceTick;

    public SchwabBarFeed(SchwabAuthService auth, StrategyConfig cfg, ILastPriceProvider prices, ILogger<SchwabBarFeed> log)
    { _auth = auth; _cfg = cfg; _prices = prices; _log = log; }

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

        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                var si = await _auth.GetStreamerInfoAsync();
                _log.LogInformation("Schwab WSS connecting to {Url}…", si.SocketUrl);
                await ws.ConnectAsync(new Uri(si.SocketUrl), ct);
                _log.LogInformation("Schwab WSS connected.");
                attempt = 0; // reset backoff on successful connect

                var buf = new byte[65536];

                // 1) Send LOGIN and wait for acknowledgment
                var token = await _auth.GetAccessTokenAsync();
                await SendAsync(ws, BuildLoginReq(token, si), ct);

                var loginResp = await ReceiveTextAsync(ws, buf, ct);
                if (loginResp == null)
                {
                    attempt++;
                    var d = Math.Min(60, (int)Math.Pow(2, attempt));
                    _log.LogWarning("Schwab WSS closed before login response. Retry in {Delay}s", d);
                    await Task.Delay(d * 1000 + Random.Shared.Next(0, 500), ct);
                    continue;
                }
                _log.LogInformation("Schwab WSS login response: {Resp}",
                    loginResp.Length > 500 ? loginResp[..500] + "…" : loginResp);

                // Check login success — response.content.code == 0
                if (!IsLoginSuccess(loginResp))
                {
                    attempt++;
                    var d = Math.Min(60, (int)Math.Pow(2, attempt + 3)); // start at 16s for auth failures
                    _log.LogError("Schwab WSS login FAILED. Retrying in {Delay}s (attempt {Attempt})", d, attempt);
                    await Task.Delay(d * 1000 + Random.Shared.Next(0, 500), ct);
                    continue;
                }

                // 2) Now subscribe
                await SendAsync(ws, BuildChartSubReq(_cfg.Ticker, si), ct);
                await SendAsync(ws, BuildL1SubReq(_cfg.Ticker, si), ct);
                _log.LogInformation("Schwab WSS subscribed: CHART_FUTURES + LEVELONE_FUTURES for {Ticker}", _cfg.Ticker);

                // 3) Read stream
                int msgCount = 0;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogWarning("Schwab WSS received Close frame: {Reason}",
                            ws.CloseStatusDescription ?? ws.CloseStatus?.ToString() ?? "unknown");
                        break;
                    }
                    if (r.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(buf, 0, r.Count);
                        ProcessMessage(text, builder);
                        msgCount++;
                    }
                }

                attempt++;
                var dDc = Math.Min(60, (int)Math.Pow(2, attempt));
                _log.LogWarning("Schwab WSS disconnected (state={State}, msgs={N}). Reconnecting in {Delay}s…",
                    ws.State, msgCount, dDc);
                await Task.Delay(dDc * 1000 + Random.Shared.Next(0, 500), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                attempt++;
                var dErr = Math.Min(60, (int)Math.Pow(2, attempt));
                _log.LogError(ex, "Schwab stream error — reconnecting in {Delay}s (attempt {Attempt})", dErr, attempt);
                await Task.Delay(dErr * 1000 + Random.Shared.Next(0, 500), ct);
            }
        }
    }

    /// <summary>Read one complete text message. Returns null if the connection closes.</summary>
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, byte[] buf, CancellationToken ct)
    {
        var r = await ws.ReceiveAsync(buf, ct);
        if (r.MessageType == WebSocketMessageType.Close) return null;
        if (r.MessageType == WebSocketMessageType.Text)
            return Encoding.UTF8.GetString(buf, 0, r.Count);
        return "";
    }

    /// <summary>Check if the login response indicates success (code == 0).</summary>
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

    private void ProcessMessage(string json, RealTimeBarBuilder builder)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("service", out var svc)) continue;

                // CHART_FUTURES field mapping (Schwab streaming API):
                //   0=key, 1=chart_time(epoch ms), 2=open, 3=high, 4=low, 5=close, 6=volume
                if (svc.GetString() == "CHART_FUTURES" && item.TryGetProperty("content", out var content))
                    foreach (var row in content.EnumerateArray())
                    {
                        if (!row.TryGetProperty("1", out var ts)) continue;
                        var t = DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64()).UtcDateTime;
                        decimal close = row.TryGetProperty("5", out var c) ? c.GetDecimal() : 0;
                        if (close <= 0) continue;
                        decimal high  = row.TryGetProperty("3", out var h) ? h.GetDecimal() : 0;
                        decimal low   = row.TryGetProperty("4", out var l) ? l.GetDecimal() : 0;
                        long    vol   = row.TryGetProperty("6", out var v) ? v.GetInt64()   : 0;

                        _prices.UpdatePrice(_cfg.Ticker, close);

                        // Feed high/low/close so the builder captures the full bar range.
                        // Schwab CHART_FUTURES sends 1-min candles (not ticks), so we must
                        // push the extremes through the builder to preserve H/L accuracy.
                        if (high > 0 && high != close) builder.OnTick(high, 0, t);
                        if (low  > 0 && low  != close) builder.OnTick(low,  0, t);
                        builder.OnTick(close, vol, t);
                    }

                if (svc.GetString() == "LEVELONE_FUTURES" && item.TryGetProperty("content", out var l1))
                    foreach (var row in l1.EnumerateArray())
                        if (row.TryGetProperty("3", out var last) && last.GetDecimal() > 0)
                        {
                            var lp  = last.GetDecimal();
                            var now = DateTime.UtcNow;
                            builder.OnTick(lp, 0, now);
                            OnPriceTick?.Invoke(lp, now);
                        }
            }
        }
        catch (Exception ex) { _log.LogWarning("Schwab msg parse error: {E}", ex.Message); }
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
    private static string BuildChartSubReq(string sym, SchwabAuthService.SchwabStreamerInfo si) => JsonSerializer.Serialize(new
    {
        requests = new[] { new { service = "CHART_FUTURES", requestid = "1", command = "SUBS",
            SchwabClientCustomerId = si.CustomerId,
            SchwabClientCorrelId   = si.CorrelId,
            parameters = new { keys = sym, fields = "0,1,2,3,4,5,6" } } }
    });
    private static string BuildL1SubReq(string sym, SchwabAuthService.SchwabStreamerInfo si) => JsonSerializer.Serialize(new
    {
        requests = new[] { new { service = "LEVELONE_FUTURES", requestid = "2", command = "SUBS",
            SchwabClientCustomerId = si.CustomerId,
            SchwabClientCorrelId   = si.CorrelId,
            parameters = new { keys = sym, fields = "0,3,4,5,8" } } }
    });
    private static async Task SendAsync(ClientWebSocket ws, string msg, CancellationToken ct)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
}

// ── Order Executor ────────────────────────────────────────────
public class SchwabExecutor : CRV.Core.Interfaces.IOrderExecutor
{
    private readonly SchwabAuthService    _auth;
    private readonly StrategyConfig       _cfg;
    private readonly ILogger              _log;
    private readonly IHttpClientFactory?  _httpFactory;

    // Per-setup order leg tracking — supports all 5 setups (A/B/C/D/F)
    private class SetupState
    {
        public string?    EntryOrderId  { get; set; }
        public string?    StopOrderId   { get; set; }
        public string?    TargetOrderId { get; set; }
        public string?    PartialOrderId { get; set; }
        public Direction? Direction     { get; set; }

        public string? Ticker { get; set; }
        public void Clear()
        {
            EntryOrderId = StopOrderId = TargetOrderId = PartialOrderId = null;
            Direction = null;
            Ticker = null;
        }
    }

    private readonly Dictionary<SetupId, SetupState> _states = new()
    {
        [SetupId.A] = new(), [SetupId.B] = new(),
        [SetupId.C] = new(), [SetupId.D] = new(), [SetupId.F] = new()
    };

    private string BaseUrl =>
        $"{_auth.ApiBaseUrl}/trader/v1/accounts/{_cfg.AccountId}/orders";

    public SchwabExecutor(SchwabAuthService auth, StrategyConfig cfg, ILogger<SchwabExecutor> log,
        IHttpClientFactory? httpFactory = null)
    { _auth = auth; _cfg = cfg; _log = log; _httpFactory = httpFactory; }

    private HttpClient CreateClient() => _httpFactory?.CreateClient("Schwab") ?? new HttpClient();

    public async Task<decimal?> OnEntrySignalAsync(EntrySignal sig)
    {
        _log.LogInformation("[SCHWAB] ENTRY {D} {Q}x {S} @ {E} Stop={St} Tgt={T}",
            sig.Direction, sig.Contracts, sig.Setup, sig.Entry, sig.Stop, sig.Target);

        var ticker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker;
        var state = _states[sig.Setup];
        state.Direction = sig.Direction;
        state.Ticker = ticker;
        // Futures instructions: BUY / SELL (not BUY_TO_OPEN / SELL_TO_CLOSE — those are options-only)
        var entryInstr = sig.Direction == Direction.Long ? "BUY"  : "SELL";
        var closeInstr = sig.Direction == Direction.Long ? "SELL" : "BUY";

        bool isLimit = sig.OrderType == "Limit";
        var body = new
        {
            orderStrategyType = "TRIGGER",
            session           = "NORMAL",
            duration          = "DAY",
            orderType         = isLimit ? "LIMIT" : "MARKET",
            price             = isLimit ? (decimal?)sig.Entry : null,
            quantity          = sig.Contracts,
            orderLegCollection = new[]
            {
                new { instruction = entryInstr, quantity = sig.Contracts,
                      instrument  = new { symbol = ticker, assetType = "FUTURE" } }
            },
            childOrderStrategies = new[]
            {
                new
                {
                    orderStrategyType    = "OCO",
                    childOrderStrategies = new object[]
                    {
                        new // Limit target
                        {
                            orderStrategyType  = "SINGLE",
                            orderType          = "LIMIT",
                            session            = "NORMAL",
                            duration           = "DAY",
                            price              = sig.Target,
                            quantity           = sig.Contracts,
                            orderLegCollection = new[]
                            {
                                new { instruction = closeInstr, quantity = sig.Contracts,
                                      instrument  = new { symbol = ticker, assetType = "FUTURE" } }
                            }
                        },
                        new // Stop
                        {
                            orderStrategyType  = "SINGLE",
                            orderType          = "STOP",
                            session            = "NORMAL",
                            duration           = "DAY",
                            stopPrice          = sig.Stop,
                            quantity           = sig.Contracts,
                            orderLegCollection = new[]
                            {
                                new { instruction = closeInstr, quantity = sig.Contracts,
                                      instrument  = new { symbol = ticker, assetType = "FUTURE" } }
                            }
                        }
                    }
                }
            }
        };

        state.EntryOrderId = await PlaceOrderAsync(body);
        _log.LogInformation("[SCHWAB] Setup {S} bracket accepted, entryOrderId={Id}", sig.Setup, state.EntryOrderId);

        // The TRIGGER POST returns only the parent order ID in the Location header.
        // Child OCO order IDs are not in the response body — we must GET the order to find them.
        decimal? fillPrice = null;
        if (state.EntryOrderId != null)
        {
            fillPrice = await GetFillPriceAsync(state.EntryOrderId);
            (state.TargetOrderId, state.StopOrderId) = await FetchChildOrderIdsAsync(state.EntryOrderId);
        }

        return fillPrice;
    }

    public async Task OnPartialSignalAsync(PartialSignal sig)
    {
        _log.LogInformation("[SCHWAB] PARTIAL {S} {Q}ct @ {P} ({R}ct remaining)",
            sig.Setup, sig.ContractsExited, sig.PartialPrice, sig.ContractsRemaining);

        var state = _states[sig.Setup];
        if (state.TargetOrderId != null)
        {
            await CancelOrderAsync(state.TargetOrderId);
            state.TargetOrderId = null;
        }
        else
        {
            _log.LogWarning("[SCHWAB] OnPartialSignal {S}: TargetOrderId not set — cannot cancel bracket target", sig.Setup);
        }

        var closeInstr = sig.Direction == Direction.Long ? "SELL" : "BUY";
        var body = new
        {
            orderStrategyType  = "SINGLE",
            session            = "NORMAL",
            duration           = "DAY",
            orderType          = "LIMIT",
            price              = sig.PartialPrice,
            quantity           = sig.ContractsExited,
            orderLegCollection = new[]
            {
                new { instruction = closeInstr, quantity = sig.ContractsExited,
                      instrument  = new { symbol = state.Ticker ?? _cfg.Ticker, assetType = "FUTURE" } }
            }
        };

        state.PartialOrderId = await PlaceOrderAsync(body);
        _log.LogDebug("[SCHWAB] Setup {S} partial limit placed, ID={Id}", sig.Setup, state.PartialOrderId);
    }

    public async Task OnBESignalAsync(BESignal sig)
    {
        _log.LogInformation("[SCHWAB] MOVE_BE {S} → {P} ({Q}ct)",
            sig.Setup, sig.NewStop, sig.ContractsRemaining);

        var state = _states[sig.Setup];
        if (state.StopOrderId != null)
        {
            var closeInstr = sig.Direction == Direction.Long ? "SELL" : "BUY";
            var body = new
            {
                orderStrategyType  = "SINGLE",
                session            = "NORMAL",
                duration           = "DAY",
                orderType          = "STOP",
                stopPrice          = sig.NewStop,
                quantity           = sig.ContractsRemaining,
                orderLegCollection = new[]
                {
                    new { instruction = closeInstr, quantity = sig.ContractsRemaining,
                          instrument  = new { symbol = state.Ticker ?? _cfg.Ticker, assetType = "FUTURE" } }
                }
            };
            var ok = await ReplaceOrderAsync(state.StopOrderId, body);
            if (ok) _log.LogDebug("[SCHWAB] Setup {S} stop modified to BE @ {P}", sig.Setup, sig.NewStop);
        }
        else
        {
            _log.LogWarning("[SCHWAB] OnBESignal {S}: StopOrderId not set — cannot modify stop", sig.Setup);
        }
    }

    public async Task OnExitSignalAsync(ExitSignal sig)
    {
        _log.LogInformation("[SCHWAB] EXIT {S} {R} @ {P} {Q}ct",
            sig.Setup, sig.Reason, sig.ExitPrice, sig.Contracts);

        var state = _states[sig.Setup];

        // Cancel any open bracket / partial legs
        foreach (var id in new[] { state.EntryOrderId, state.StopOrderId, state.TargetOrderId, state.PartialOrderId }
                     .Where(x => x != null))
            await CancelOrderAsync(id!);

        var direction = state.Direction ?? Direction.Long;
        var exitTicker = state.Ticker ?? (!string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker);
        state.Clear();

        // For Stop/Target exits the broker's bracket already filled — no market order needed.
        // Only SessionEnd/AdverseTime/Manual require a market close.
        bool brokerHandledExit = sig.Reason is ExitReason.Stop or ExitReason.Target;
        if (brokerHandledExit)
        {
            _log.LogDebug("[SCHWAB] EXIT {S}: {R} — broker bracket filled, no market order needed", sig.Setup, sig.Reason);
            return;
        }

        var closeInstr = direction == Direction.Long ? "SELL" : "BUY";
        var body = new
        {
            orderStrategyType  = "SINGLE",
            session            = "NORMAL",
            duration           = "DAY",
            orderType          = "MARKET",
            quantity           = sig.Contracts,
            orderLegCollection = new[]
            {
                new { instruction = closeInstr, quantity = sig.Contracts,
                      instrument  = new { symbol = exitTicker, assetType = "FUTURE" } }
            }
        };

        var orderId = await PlaceOrderAsync(body);
        _log.LogDebug("[SCHWAB] Market close placed, ID={Id}", orderId);
    }

    public async Task OnLevelsAdjustedAsync(SetupId setup, decimal newStop, decimal newTarget, int contracts)
    {
        _log.LogInformation("[SCHWAB] LEVELS_ADJUSTED {S} → Stop={St} Target={T} Qty={Q}",
            setup, newStop, newTarget, contracts);

        var state      = _states[setup];
        var direction  = state.Direction ?? Direction.Long;
        var closeInstr = direction == Direction.Long ? "SELL" : "BUY";
        var sym        = state.Ticker ?? _cfg.Ticker;

        // Modify stop
        if (state.StopOrderId != null)
        {
            var stopBody = new
            {
                orderStrategyType  = "SINGLE",
                session            = "NORMAL",
                duration           = "DAY",
                orderType          = "STOP",
                stopPrice          = newStop,
                quantity           = contracts,
                orderLegCollection = new[]
                {
                    new { instruction = closeInstr, quantity = contracts,
                          instrument  = new { symbol = sym, assetType = "FUTURE" } }
                }
            };
            var ok = await ReplaceOrderAsync(state.StopOrderId, stopBody);
            if (ok) _log.LogDebug("[SCHWAB] Setup {S} stop modified → {P}", setup, newStop);
        }
        else
            _log.LogWarning("[SCHWAB] OnLevelsAdjusted {S}: StopOrderId not set", setup);

        // Modify target
        if (state.TargetOrderId != null)
        {
            var targetBody = new
            {
                orderStrategyType  = "SINGLE",
                session            = "NORMAL",
                duration           = "DAY",
                orderType          = "LIMIT",
                price              = newTarget,
                quantity           = contracts,
                orderLegCollection = new[]
                {
                    new { instruction = closeInstr, quantity = contracts,
                          instrument  = new { symbol = sym, assetType = "FUTURE" } }
                }
            };
            var ok = await ReplaceOrderAsync(state.TargetOrderId, targetBody);
            if (ok) _log.LogDebug("[SCHWAB] Setup {S} target modified → {P}", setup, newTarget);
        }
        else
            _log.LogWarning("[SCHWAB] OnLevelsAdjusted {S}: TargetOrderId not set", setup);
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Polls the Schwab order status endpoint for a fill price.
    /// Retries up to 15 times at 200ms intervals. Returns null on timeout or error.
    /// </summary>
    private async Task<decimal?> GetFillPriceAsync(string orderId)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            try
            {
                var token = await _auth.GetAccessTokenAsync();
                using var http = CreateClient();
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                var resp = await http.GetAsync($"{BaseUrl}/{orderId}");
                if (!resp.IsSuccessStatusCode) continue;

                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);

                // Schwab fills: status == "FILLED", price in orderActivityCollection[].executionLegs[].price
                if (doc.RootElement.TryGetProperty("status", out var status) &&
                    status.GetString() == "FILLED" &&
                    doc.RootElement.TryGetProperty("orderActivityCollection", out var activities))
                {
                    foreach (var activity in activities.EnumerateArray())
                    {
                        if (!activity.TryGetProperty("executionLegs", out var legs)) continue;
                        foreach (var leg in legs.EnumerateArray())
                        {
                            if (leg.TryGetProperty("price", out var p))
                            {
                                var fill = p.GetDecimal();
                                _log.LogInformation("[SCHWAB] Fill price for order {Id}: {Price}",
                                    orderId, fill);
                                return fill;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[SCHWAB] GetFillPriceAsync attempt {N} failed", attempt + 1);
            }
        }

        _log.LogWarning("[SCHWAB] Could not retrieve fill price for order {Id} after polling", orderId);
        return null;
    }

    /// <summary>
    /// Schwab TRIGGER orders return only the parent order ID in the Location header.
    /// This method GETs the parent order and extracts the LIMIT-target and STOP child IDs
    /// from the nested OCO so they can be cancelled individually later.
    /// Retries up to 3 times with 1 s delay in case Schwab needs a moment to create children.
    /// </summary>
    private async Task<(string? target, string? stop)> FetchChildOrderIdsAsync(string parentId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(1000);
            try
            {
                var token = await _auth.GetAccessTokenAsync();
                using var http = CreateClient();
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                var resp = await http.GetAsync($"{BaseUrl}/{parentId}");
                var body = await resp.Content.ReadAsStringAsync();
                _log.LogDebug("[SCHWAB] GET order {Id}: {Status}", parentId, (int)resp.StatusCode);

                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(body);

                // Structure: TRIGGER.childOrderStrategies[0] = OCO
                //            OCO.childOrderStrategies[0]     = LIMIT target
                //            OCO.childOrderStrategies[1]     = STOP
                if (!doc.RootElement.TryGetProperty("childOrderStrategies", out var triggerKids))
                    continue;

                var oco = triggerKids.EnumerateArray().FirstOrDefault();
                if (oco.ValueKind == JsonValueKind.Undefined) continue;
                if (!oco.TryGetProperty("childOrderStrategies", out var ocoKids)) continue;

                var kids = ocoKids.EnumerateArray().ToList();
                string? GetId(int i) =>
                    kids.Count > i &&
                    kids[i].TryGetProperty("orderId", out var p) &&
                    p.ValueKind == JsonValueKind.Number
                        ? p.GetInt64().ToString() : null;

                var targetId = GetId(0);
                var stopId   = GetId(1);

                if (targetId != null || stopId != null)
                {
                    _log.LogInformation("[SCHWAB] Bracket child IDs — target={T} stop={S}",
                        targetId, stopId);
                    return (targetId, stopId);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[SCHWAB] FetchChildOrderIdsAsync attempt {N} failed", attempt + 1);
            }
        }

        _log.LogWarning("[SCHWAB] Could not fetch bracket child IDs for order {Id} — " +
                        "partial/BE cancellations will be skipped", parentId);
        return (null, null);
    }

    private async Task<string?> PlaceOrderAsync(object body)
    {
        var token = await _auth.GetAccessTokenAsync();
        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
        _log.LogDebug("[SCHWAB] POST {Url} {Body}", BaseUrl, json);

        using var resp = await http.PostAsync(BaseUrl,
            new StringContent(json, Encoding.UTF8, "application/json"));

        var respBody = await resp.Content.ReadAsStringAsync();
        _log.LogDebug("[SCHWAB] Response {Status} {Body}", (int)resp.StatusCode, respBody);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("[SCHWAB] Order rejected: {Status} {Body}", (int)resp.StatusCode, respBody);
            throw new InvalidOperationException(
                $"Schwab order rejected ({(int)resp.StatusCode}): {respBody}");
        }

        // Extract order ID from Location header: .../orders/{orderId}
        if (resp.Headers.Location is { } loc)
        {
            var id = loc.Segments.Last().TrimEnd('/');
            _log.LogDebug("[SCHWAB] Order ID from Location: {Id}", id);
            return id;
        }

        return null;
    }

    private async Task CancelOrderAsync(string orderId)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"{BaseUrl}/{orderId}";
            _log.LogDebug("[SCHWAB] DELETE {Url}", url);

            using var resp = await http.DeleteAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            _log.LogDebug("[SCHWAB] Cancel {Status} {Body}", (int)resp.StatusCode, body);

            if (!resp.IsSuccessStatusCode)
                _log.LogError("[SCHWAB] Cancel order {Id} failed: {Status} {Body}",
                    orderId, (int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SCHWAB] CancelOrderAsync {Id} failed", orderId);
        }
    }

    /// <summary>Replaces/modifies an existing order via PUT /trader/v1/accounts/{hash}/orders/{orderId}.</summary>
    private async Task<bool> ReplaceOrderAsync(string orderId, object orderBody)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"{BaseUrl}/{orderId}";
            _log.LogDebug("[SCHWAB] PUT {Url}", url);

            var json = JsonSerializer.Serialize(orderBody);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PutAsync(url, content);
            var body = await resp.Content.ReadAsStringAsync();
            _log.LogDebug("[SCHWAB] Replace {Status} {Body}", (int)resp.StatusCode, body);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("[SCHWAB] Replace order {Id} failed: {Status} {Body}",
                    orderId, (int)resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[SCHWAB] ReplaceOrderAsync {Id} failed", orderId);
            return false;
        }
    }
}
