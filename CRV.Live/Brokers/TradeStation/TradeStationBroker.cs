using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live.BarBuilders;
using Microsoft.Extensions.Logging;

namespace CRV.Live.Brokers.TradeStation;

// ── Auth ─────────────────────────────────────────────────────
/// <summary>
/// Manages TradeStation OAuth2 tokens using the Authorization Code flow.
/// client_credentials only covers unauthenticated market-data APIs;
/// brokerage + order-execution endpoints all require an authorized user token.
///
/// One-time setup:
///   1. Register a redirect URI in your TradeStation developer app
///      (e.g. https://127.0.0.1:5001/auth/tradestation)
///   2. Set TradeStation:RedirectUri in appsettings.json
///   3. Navigate to /auth/tradestation → Connect with TradeStation
///   4. Tokens are saved to TradeStation:TokenFile and refreshed automatically.
/// </summary>
public class TradeStationAuthService
{
    // Scopes required for bar streaming + account access + order execution
    private const string Scopes =
        "openid profile offline_access MarketData ReadAccount Trade Crypto";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;
    private readonly string _tokenFile;

    /// <summary>TradeStation REST API base URL (e.g. https://api.tradestation.com). Configurable via appsettings.</summary>
    public string ApiBaseUrl  { get; }
    /// <summary>TradeStation auth/token base URL (e.g. https://signin.tradestation.com). Configurable via appsettings.</summary>
    public string AuthBaseUrl { get; }

    private readonly SemaphoreSlim _lock = new(1, 1);

    private string   _accessToken  = "";
    private string   _refreshToken = "";
    private DateTime _tokenExpiry  = DateTime.MinValue;

    /// <summary>True when a refresh token is stored (user has authorized).</summary>
    public bool IsAuthenticated => !string.IsNullOrEmpty(_refreshToken);

    public TradeStationAuthService(
        string clientId, string clientSecret, string redirectUri, string tokenFile,
        string apiBaseUrl  = "https://api.tradestation.com",
        string authBaseUrl = "https://signin.tradestation.com")
    {
        _clientId     = clientId;
        _clientSecret = clientSecret;
        _redirectUri  = redirectUri;
        _tokenFile    = tokenFile;
        ApiBaseUrl    = apiBaseUrl.TrimEnd('/');
        AuthBaseUrl   = authBaseUrl.TrimEnd('/');
        LoadTokens();
    }

    /// <summary>Returns the TradeStation OAuth2 authorization URL to redirect the user to.</summary>
    public string BuildAuthorizationUrl()
        => $"{AuthBaseUrl}/authorize?response_type=code" +
           $"&client_id={Uri.EscapeDataString(_clientId)}" +
           $"&redirect_uri={Uri.EscapeDataString(_redirectUri)}" +
           $"&audience={Uri.EscapeDataString(ApiBaseUrl)}" +
           $"&scope={Uri.EscapeDataString(Scopes)}";

    /// <summary>Exchanges the authorization code (from OAuth callback) for access + refresh tokens.</summary>
    public async Task ExchangeCodeAsync(string code)
    {
        await _lock.WaitAsync();
        try
        {
            var json = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = _clientId,
                ["client_secret"] = _clientSecret,
                ["code"]          = code,
                ["redirect_uri"]  = _redirectUri,
            });
            ParseAndSave(json);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns a valid access token, refreshing it automatically when expired.</summary>
    public async Task<string> GetAccessTokenAsync()
    {
        // Fast path — token still valid
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
            return _accessToken;

        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
                return _accessToken;

            if (string.IsNullOrEmpty(_refreshToken))
                throw new InvalidOperationException(
                    "TradeStation: not authenticated. " +
                    "Visit /auth/tradestation to connect your account.");

            var json = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = _refreshToken,
            });
            ParseAndSave(json);
            return _accessToken;
        }
        finally { _lock.Release(); }
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// TradeStation uses form-body credentials (not Basic auth header like Schwab).
    /// </summary>
    private async Task<string> PostTokenAsync(Dictionary<string, string> fields)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"{AuthBaseUrl}/oauth/token", new FormUrlEncodedContent(fields));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"TradeStation token request failed ({(int)resp.StatusCode}): {json}");
        return json;
    }

    private void ParseAndSave(string json)
    {
        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(
            doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() - 60 : 1140);

        // TradeStation returns a new refresh token on each grant — always update it
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
            _accessToken  = doc.RootElement.TryGetProperty("access_token",  out var at) ? at.GetString()  ?? "" : "";
            _refreshToken = doc.RootElement.TryGetProperty("refresh_token",  out var rt) ? rt.GetString()  ?? "" : "";
            _tokenExpiry  = doc.RootElement.TryGetProperty("expiry_utc",     out var ex)
                ? DateTime.Parse(ex.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTime.MinValue;
        }
        catch { /* ignore corrupt / missing token file */ }
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
        catch { /* token save failure is non-fatal */ }
    }
}

// ── Bar Feed ─────────────────────────────────────────────────
public class TradeStationBarFeed : IBarFeed
{
    private readonly TradeStationAuthService _auth;
    private readonly StrategyConfig          _cfg;
    private readonly ILastPriceProvider      _prices;
    private readonly ILogger                 _log;
    private readonly System.Threading.Channels.Channel<Bar> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<Bar>();

    /// <inheritdoc />
    public event Action<decimal, DateTime>? OnPriceTick;

    public TradeStationBarFeed(TradeStationAuthService auth, StrategyConfig cfg, ILastPriceProvider prices, ILogger<TradeStationBarFeed> log)
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

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var http = new HttpClient();
                var token = await _auth.GetAccessTokenAsync();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var sym = Uri.EscapeDataString(_cfg.Ticker);
                // Calculate bars needed to cover from ATR-warmup start (20 TF-bars before ORB)
                // through to now, so ORB is fully built even when engine starts mid-session.
                var etTz       = TimeZoneInfo.FindSystemTimeZoneById(
                    OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
                var nowEt      = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etTz);
                var orbStart   = nowEt.Date.Add(_cfg.OrbStart.ToTimeSpan());
                var minsSince  = Math.Max(0, (nowEt - orbStart).TotalMinutes);
                var barsNeeded = (int)Math.Ceiling(minsSince / _cfg.ExecutionTFMinutes) + 22;
                var url = $"{_auth.ApiBaseUrl}/v3/marketdata/stream/barcharts/{sym}" +
                          $"?interval={_cfg.ExecutionTFMinutes}&unit=Minute&barsback={barsNeeded}&sessiontemplate=Default";

                using var resp   = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                using var reader = new System.IO.StreamReader(await resp.Content.ReadAsStreamAsync(ct));

                _log.LogInformation("TradeStation bar stream connected.");

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ProcessLine(line, builder);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(ex, "TS stream error — reconnecting in 5s");
                await Task.Delay(5000, ct);
            }
        }
    }

    private void ProcessLine(string line, RealTimeBarBuilder builder)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("Close", out var closeProp)) return;
            decimal close = DecStr(doc.RootElement, "Close");
            if (close <= 0) return;

            if (!doc.RootElement.TryGetProperty("TimeStamp", out var tsProp)) return;
            // Use DateTimeOffset.TryParse — correctly handles ISO offset strings like
            // "2026-03-09T09:30:00-04:00" on any server timezone without throwing.
            if (!DateTimeOffset.TryParse(tsProp.GetString(), out var tsOffset)) return;
            var utc = tsOffset.UtcDateTime;

            long vol = doc.RootElement.TryGetProperty("TotalVolume", out var vp)
                ? (long.TryParse(vp.GetString(), out var v) ? v : 0) : 0;

            _prices.UpdatePrice(_cfg.Ticker, close);

            // Historical bars (from barsback replay) are already fully closed — write them
            // directly to the channel with proper OHLCV so ORB range is accurate.
            // Live bars go through RealTimeBarBuilder to aggregate ticks into confirmed bars.
            bool isHistorical = doc.RootElement.TryGetProperty("IsRealtime", out var rtProp)
                && rtProp.ValueKind == JsonValueKind.False;

            if (!isHistorical)
                OnPriceTick?.Invoke(close, utc);

            if (isHistorical)
            {
                decimal open = DecStr(doc.RootElement, "Open");
                decimal high = DecStr(doc.RootElement, "High");
                decimal low  = DecStr(doc.RootElement, "Low");
                _channel.Writer.TryWrite(new Bar(utc,
                    open > 0 ? open : close,
                    high > 0 ? high : close,
                    low  > 0 ? low  : close,
                    close, vol, IsConfirmed: true));
            }
            else
            {
                builder.OnTick(close, vol, utc);
                // "IsEndOfHistory" marks the boundary between replay and live stream
                bool endOfHistory = doc.RootElement.TryGetProperty("IsEndOfHistory", out var eoh)
                    && eoh.ValueKind == JsonValueKind.True;
                if (endOfHistory) builder.ForceClose(utc);
            }
        }
        catch (Exception ex) { _log.LogWarning("TS line parse error: {E}", ex.Message); }
    }

    private static decimal DecStr(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number ? p.GetDecimal()
             : decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Any,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

// ── Order Executor ────────────────────────────────────────────
public class TradeStationExecutor : CRV.Core.Interfaces.IOrderExecutor
{
    private readonly TradeStationAuthService _auth;
    private readonly StrategyConfig          _cfg;
    private readonly ILogger                 _log;

    // Stateful order tracking — cleared on each new entry
    private string?   _entryOrderId;
    private string?   _stopOrderId;    // bracket stop leg (may move to BE after partial)
    private string?   _targetOrderId;  // bracket limit-target leg (cancelled when partial fires)
    private string?   _partialOrderId; // partial-exit LIMIT order placed at partial level
    private Direction _direction;

    private string OrdersUrl => $"{_auth.ApiBaseUrl}/v3/orderexecution/orders";

    public TradeStationExecutor(TradeStationAuthService auth, StrategyConfig cfg, ILogger<TradeStationExecutor> log)
    { _auth = auth; _cfg = cfg; _log = log; }

    public async Task OnEntrySignalAsync(EntrySignal sig)
    {
        _log.LogInformation("[TS] ENTRY {D} {Q}x {S} @ {E} Stop={St} Tgt={T}",
            sig.Direction, sig.Contracts, sig.Setup, sig.Entry, sig.Stop, sig.Target);

        _direction = sig.Direction;
        var tradeAction = sig.Direction == Direction.Long ? "BUY"  : "SELLSHORT";
        var closeAction = sig.Direction == Direction.Long ? "SELL" : "BUYTOCOVER";

        var body = new
        {
            AccountID   = _cfg.AccountId,
            Symbol      = _cfg.Ticker,
            Quantity    = sig.Contracts.ToString(),
            OrderType   = "Market",
            TradeAction = tradeAction,
            TimeInForce = new { Duration = "DAY" },
            OSOs        = new[]
            {
                new
                {
                    Type   = "BRK",
                    Orders = new object[]
                    {
                        new // Limit target
                        {
                            AccountID   = _cfg.AccountId,
                            Symbol      = _cfg.Ticker,
                            Quantity    = sig.Contracts.ToString(),
                            OrderType   = "Limit",
                            LimitPrice  = sig.Target.ToString("F2"),
                            TradeAction = closeAction,
                            TimeInForce = new { Duration = "DAY" }
                        },
                        new // Stop market
                        {
                            AccountID   = _cfg.AccountId,
                            Symbol      = _cfg.Ticker,
                            Quantity    = sig.Contracts.ToString(),
                            OrderType   = "StopMarket",
                            StopPrice   = sig.Stop.ToString("F2"),
                            TradeAction = closeAction,
                            TimeInForce = new { Duration = "DAY" }
                        }
                    }
                }
            }
        };

        var (entryId, targetId, stopId) = await PlaceBracketAsync(body);
        _entryOrderId  = entryId;
        _targetOrderId = targetId;
        _stopOrderId   = stopId;
        _log.LogDebug("[TS] Bracket placed — entry={E} target={T} stop={S}",
            _entryOrderId, _targetOrderId, _stopOrderId);
    }

    public async Task OnPartialSignalAsync(PartialSignal sig)
    {
        _log.LogInformation("[TS] PARTIAL {S} {Q}ct @ {P} ({R}ct remaining)",
            sig.Setup, sig.ContractsExited, sig.PartialPrice, sig.ContractsRemaining);

        if (_targetOrderId != null)
        {
            await CancelOrderAsync(_targetOrderId);
            _targetOrderId = null;
        }
        else
        {
            _log.LogWarning("[TS] OnPartialSignal: _targetOrderId not set — cannot cancel bracket target");
        }

        var closeAction = sig.Direction == Direction.Long ? "SELL" : "BUYTOCOVER";
        var body = new
        {
            AccountID   = _cfg.AccountId,
            Symbol      = _cfg.Ticker,
            Quantity    = sig.ContractsExited.ToString(),
            OrderType   = "Limit",
            LimitPrice  = sig.PartialPrice.ToString("F2"),
            TradeAction = closeAction,
            TimeInForce = new { Duration = "DAY" }
        };

        _partialOrderId = await PlaceSingleAsync(body);
        _log.LogDebug("[TS] Partial limit placed, ID={Id}", _partialOrderId);
    }

    public async Task OnBESignalAsync(BESignal sig)
    {
        _log.LogInformation("[TS] MOVE_BE {S} → {P} ({Q}ct)",
            sig.Setup, sig.NewStop, sig.ContractsRemaining);

        if (_stopOrderId != null)
        {
            await CancelOrderAsync(_stopOrderId);
            _stopOrderId = null;
        }
        else
        {
            _log.LogWarning("[TS] OnBESignal: _stopOrderId not set — cannot cancel existing stop");
        }

        var closeAction = sig.Direction == Direction.Long ? "SELL" : "BUYTOCOVER";
        var body = new
        {
            AccountID   = _cfg.AccountId,
            Symbol      = _cfg.Ticker,
            Quantity    = sig.ContractsRemaining.ToString(),
            OrderType   = "StopMarket",
            StopPrice   = sig.NewStop.ToString("F2"),
            TradeAction = closeAction,
            TimeInForce = new { Duration = "DAY" }
        };

        _stopOrderId = await PlaceSingleAsync(body);
        _log.LogDebug("[TS] BE stop placed, ID={Id}", _stopOrderId);
    }

    public async Task OnExitSignalAsync(ExitSignal sig)
    {
        _log.LogInformation("[TS] EXIT {S} {R} @ {P} {Q}ct",
            sig.Setup, sig.Reason, sig.ExitPrice, sig.Contracts);

        // Cancel any open bracket / partial legs
        foreach (var id in new[] { _stopOrderId, _targetOrderId, _partialOrderId }.Where(x => x != null))
            await CancelOrderAsync(id!);

        _entryOrderId = _stopOrderId = _targetOrderId = _partialOrderId = null;

        var closeAction = _direction == Direction.Long ? "SELL" : "BUYTOCOVER";
        var body = new
        {
            AccountID   = _cfg.AccountId,
            Symbol      = _cfg.Ticker,
            Quantity    = sig.Contracts.ToString(),
            OrderType   = "Market",
            TradeAction = closeAction,
            TimeInForce = new { Duration = "DAY" }
        };

        var orderId = await PlaceSingleAsync(body);
        _log.LogDebug("[TS] Market close placed, ID={Id}", orderId);
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>Places bracket order and returns (entryId, targetId, stopId) from Orders array.</summary>
    private async Task<(string? entry, string? target, string? stop)> PlaceBracketAsync(object body)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
            _log.LogDebug("[TS] POST {Url} {Body}", OrdersUrl, json);

            using var resp = await http.PostAsync(OrdersUrl,
                new StringContent(json, Encoding.UTF8, "application/json"));

            var respBody = await resp.Content.ReadAsStringAsync();
            _log.LogDebug("[TS] Response {Status} {Body}", (int)resp.StatusCode, respBody);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("[TS] Bracket order failed: {Status} {Body}", (int)resp.StatusCode, respBody);
                return (null, null, null);
            }

            using var doc = JsonDocument.Parse(respBody);
            if (!doc.RootElement.TryGetProperty("Orders", out var orders))
                return (null, null, null);

            var arr = orders.EnumerateArray().ToList();
            string? GetId(int idx) =>
                arr.Count > idx && arr[idx].TryGetProperty("OrderID", out var p)
                    ? p.GetString() : null;

            return (GetId(0), GetId(1), GetId(2));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TS] PlaceBracketAsync failed");
            return (null, null, null);
        }
    }

    private async Task<string?> PlaceSingleAsync(object body)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
            _log.LogDebug("[TS] POST {Url} {Body}", OrdersUrl, json);

            using var resp = await http.PostAsync(OrdersUrl,
                new StringContent(json, Encoding.UTF8, "application/json"));

            var respBody = await resp.Content.ReadAsStringAsync();
            _log.LogDebug("[TS] Response {Status} {Body}", (int)resp.StatusCode, respBody);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("[TS] Order failed: {Status} {Body}", (int)resp.StatusCode, respBody);
                return null;
            }

            using var doc = JsonDocument.Parse(respBody);
            if (doc.RootElement.TryGetProperty("Orders", out var orders))
            {
                var first = orders.EnumerateArray().FirstOrDefault();
                if (first.TryGetProperty("OrderID", out var idProp))
                    return idProp.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TS] PlaceSingleAsync failed");
            return null;
        }
    }

    private async Task CancelOrderAsync(string orderId)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"{OrdersUrl}/{orderId}";
            _log.LogDebug("[TS] DELETE {Url}", url);

            using var resp = await http.DeleteAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            _log.LogDebug("[TS] Cancel {Status} {Body}", (int)resp.StatusCode, body);

            if (!resp.IsSuccessStatusCode)
                _log.LogError("[TS] Cancel order {Id} failed: {Status} {Body}",
                    orderId, (int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TS] CancelOrderAsync {Id} failed", orderId);
        }
    }
}
