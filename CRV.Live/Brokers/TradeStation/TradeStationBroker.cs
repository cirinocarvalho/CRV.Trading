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
    private readonly IHttpClientFactory? _httpFactory;

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
        string authBaseUrl = "https://signin.tradestation.com",
        IHttpClientFactory? httpFactory = null)
    {
        _clientId     = clientId;
        _clientSecret = clientSecret;
        _redirectUri  = redirectUri;
        _tokenFile    = tokenFile;
        _httpFactory  = httpFactory;
        ApiBaseUrl    = apiBaseUrl.TrimEnd('/');
        AuthBaseUrl   = authBaseUrl.TrimEnd('/');
        LoadTokens();
    }

    private HttpClient CreateClient() => _httpFactory?.CreateClient("TradeStation") ?? new HttpClient();

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
        using var http = CreateClient();
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
public class TradeStationBarFeed : IBarFeed
{
    private readonly TradeStationAuthService _auth;
    private readonly StrategyConfig          _cfg;
    private readonly ILastPriceProvider      _prices;
    private readonly ILogger                 _log;
    private readonly IHttpClientFactory?     _httpFactory;
    private readonly System.Threading.Channels.Channel<Bar> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<Bar>();

    /// <inheritdoc />
    public event Action<decimal, DateTime>? OnPriceTick;

    public TradeStationBarFeed(TradeStationAuthService auth, StrategyConfig cfg, ILastPriceProvider prices,
        ILogger<TradeStationBarFeed> log, IHttpClientFactory? httpFactory = null)
    { _auth = auth; _cfg = cfg; _prices = prices; _log = log; _httpFactory = httpFactory; }

    private HttpClient CreateClient() => _httpFactory?.CreateClient("TradeStation") ?? new HttpClient();

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
                using var http = CreateClient();
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
                attempt = 0; // reset backoff on successful connect

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
                attempt++;
                var delaySec = Math.Min(60, (int)Math.Pow(2, attempt));
                var jitter   = Random.Shared.Next(0, 500);
                _log.LogError(ex, "TS stream error — reconnecting in {Delay}s (attempt {Attempt})", delaySec, attempt);
                await Task.Delay(delaySec * 1000 + jitter, ct);
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
            {
                OnPriceTick?.Invoke(close, utc);
            }

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
    private readonly IHttpClientFactory?     _httpFactory;

    // Per-setup order leg tracking — supports all 5 setups (A/B/C/D/F)
    private class SetupState
    {
        public string?    EntryOrderId  { get; set; }
        public string?    StopOrderId   { get; set; }
        public string?    TargetOrderId { get; set; }
        public string?    PartialOrderId { get; set; }
        public Direction? Direction     { get; set; }
        public string?    Ticker        { get; set; }

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

    private string OrdersUrl => $"{_auth.ApiBaseUrl}/v3/orderexecution/orders";

    public TradeStationExecutor(TradeStationAuthService auth, StrategyConfig cfg, ILogger<TradeStationExecutor> log,
        IHttpClientFactory? httpFactory = null)
    { _auth = auth; _cfg = cfg; _log = log; _httpFactory = httpFactory; }

    private HttpClient CreateClient() => _httpFactory?.CreateClient("TradeStation") ?? new HttpClient();

    public async Task<decimal?> OnEntrySignalAsync(EntrySignal sig)
    {
        _log.LogInformation("[TS] ENTRY {D} {Q}x {S} @ {E} Stop={St} Tgt={T}",
            sig.Direction, sig.Contracts, sig.Setup, sig.Entry, sig.Stop, sig.Target);

        var ticker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker;
        var state = _states[sig.Setup];
        state.Direction = sig.Direction;
        state.Ticker = ticker;
        var tradeAction = sig.Direction == Direction.Long ? "BUY"  : "SELLSHORT";
        var closeAction = sig.Direction == Direction.Long ? "SELL" : "BUYTOCOVER";

        bool isLimit = sig.OrderType == "Limit";
        var body = new
        {
            AccountID   = _cfg.AccountId,
            Symbol      = ticker,
            Quantity    = sig.Contracts.ToString(),
            OrderType   = isLimit ? "Limit" : "Market",
            LimitPrice  = isLimit ? sig.Entry.ToString("F2") : null,
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
                            Symbol      = ticker,
                            Quantity    = sig.Contracts.ToString(),
                            OrderType   = "Limit",
                            LimitPrice  = sig.Target.ToString("F2"),
                            TradeAction = closeAction,
                            TimeInForce = new { Duration = "DAY" }
                        },
                        new // Stop market
                        {
                            AccountID   = _cfg.AccountId,
                            Symbol      = ticker,
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
        state.EntryOrderId  = entryId;
        state.TargetOrderId = targetId;
        state.StopOrderId   = stopId;
        _log.LogDebug("[TS] Setup {S} bracket placed — entry={E} target={T} stop={St}",
            sig.Setup, entryId, targetId, stopId);

        // Poll broker for the actual fill price
        if (state.EntryOrderId != null)
        {
            var fill = await GetFillPriceAsync(state.EntryOrderId);
            if (fill.HasValue)
                _log.LogInformation("[TS] Entry fill price for {S}: {Fill}", sig.Setup, fill.Value);

            // Discover bracket leg IDs if the POST response didn't include them.
            // TradeStation OSO/BRK bracket responses may only return the parent entry ID —
            // child orders (stop/target) are created by the exchange when the entry fills.
            if (state.TargetOrderId is null || state.StopOrderId is null)
            {
                _log.LogInformation("[TS] Bracket leg IDs missing (target={T}, stop={S}) — polling account orders to discover",
                    state.TargetOrderId, state.StopOrderId);
                var (foundTarget, foundStop) = await FindBracketLegsAsync(
                    state.EntryOrderId, sig.Target, sig.Stop, sig.Direction);
                state.TargetOrderId ??= foundTarget;
                state.StopOrderId   ??= foundStop;
                _log.LogInformation("[TS] Setup {S} bracket legs resolved — target={T} stop={St}",
                    sig.Setup, state.TargetOrderId, state.StopOrderId);
            }

            return fill;
        }

        return null;
    }

    public async Task OnPartialSignalAsync(PartialSignal sig)
    {
        _log.LogInformation("[TS] PARTIAL {S} {Q}ct @ {P} ({R}ct remaining)",
            sig.Setup, sig.ContractsExited, sig.PartialPrice, sig.ContractsRemaining);

        var state = _states[sig.Setup];
        if (state.TargetOrderId != null)
        {
            await CancelOrderAsync(state.TargetOrderId);
            state.TargetOrderId = null;
        }
        else
        {
            _log.LogWarning("[TS] OnPartialSignal {S}: TargetOrderId not set — cannot cancel bracket target", sig.Setup);
        }

        var closeAction = sig.Direction == Direction.Long ? "SELL" : "BUYTOCOVER";
        var sym = state.Ticker ?? _cfg.Ticker;
        var body = new
        {
            AccountID   = _cfg.AccountId,
            Symbol      = sym,
            Quantity    = sig.ContractsExited.ToString(),
            OrderType   = "Limit",
            LimitPrice  = sig.PartialPrice.ToString("F2"),
            TradeAction = closeAction,
            TimeInForce = new { Duration = "DAY" }
        };

        state.PartialOrderId = await PlaceSingleAsync(body);
        _log.LogDebug("[TS] Setup {S} partial limit placed, ID={Id}", sig.Setup, state.PartialOrderId);
    }

    public async Task OnBESignalAsync(BESignal sig)
    {
        _log.LogInformation("[TS] MOVE_BE {S} → {P} ({Q}ct)",
            sig.Setup, sig.NewStop, sig.ContractsRemaining);

        var state = _states[sig.Setup];
        if (state.StopOrderId != null)
        {
            var closeAction = sig.Direction == Direction.Long ? "SELL" : "BUYTOCOVER";
            var body = new
            {
                AccountID   = _cfg.AccountId,
                Symbol      = state.Ticker ?? _cfg.Ticker,
                Quantity    = sig.ContractsRemaining.ToString(),
                OrderType   = "StopMarket",
                StopPrice   = sig.NewStop.ToString("F2"),
                TradeAction = closeAction,
                TimeInForce = new { Duration = "DAY" }
            };
            var ok = await ReplaceOrderAsync(state.StopOrderId, body);
            if (ok) _log.LogDebug("[TS] Setup {S} stop modified to BE @ {P}", sig.Setup, sig.NewStop);
        }
        else
        {
            _log.LogWarning("[TS] OnBESignal {S}: StopOrderId not set — cannot modify stop", sig.Setup);
        }
    }

    public async Task OnExitSignalAsync(ExitSignal sig)
    {
        _log.LogInformation("[TS] EXIT {S} {R} @ {P} {Q}ct",
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
            _log.LogDebug("[TS] EXIT {S}: {R} — broker bracket filled, no market order needed", sig.Setup, sig.Reason);
            return;
        }

        var closeAction = direction == Direction.Long ? "SELL" : "BUYTOCOVER";
        var body = new
        {
            AccountID   = _cfg.AccountId,
            Symbol      = exitTicker,
            Quantity    = sig.Contracts.ToString(),
            OrderType   = "Market",
            TradeAction = closeAction,
            TimeInForce = new { Duration = "DAY" }
        };

        var orderId = await PlaceSingleAsync(body);
        _log.LogDebug("[TS] Market close placed, ID={Id}", orderId);
    }

    public async Task OnLevelsAdjustedAsync(SetupId setup, decimal newStop, decimal newTarget, int contracts)
    {
        _log.LogInformation("[TS] LEVELS_ADJUSTED {S} Stop={St} Tgt={T} Qty={Q}", setup, newStop, newTarget, contracts);

        var state       = _states[setup];
        var direction   = state.Direction ?? Direction.Long;
        var closeAction = direction == Direction.Long ? "SELL" : "BUYTOCOVER";
        var sym = state.Ticker ?? _cfg.Ticker;
        var qty = contracts.ToString();

        // Modify stop
        if (state.StopOrderId != null)
        {
            var stopBody = new
            {
                AccountID   = _cfg.AccountId,
                Symbol      = sym,
                Quantity    = qty,
                OrderType   = "StopMarket",
                StopPrice   = newStop.ToString("F2"),
                TradeAction = closeAction,
                TimeInForce = new { Duration = "DAY" }
            };
            var ok = await ReplaceOrderAsync(state.StopOrderId, stopBody);
            if (ok) _log.LogDebug("[TS] Setup {S} stop modified → {P}", setup, newStop);
        }
        else
            _log.LogWarning("[TS] OnLevelsAdjusted {S}: StopOrderId not set", setup);

        // Modify target
        if (state.TargetOrderId != null)
        {
            var tgtBody = new
            {
                AccountID   = _cfg.AccountId,
                Symbol      = sym,
                Quantity    = qty,
                OrderType   = "Limit",
                LimitPrice  = newTarget.ToString("F2"),
                TradeAction = closeAction,
                TimeInForce = new { Duration = "DAY" }
            };
            var ok = await ReplaceOrderAsync(state.TargetOrderId, tgtBody);
            if (ok) _log.LogDebug("[TS] Setup {S} target modified → {P}", setup, newTarget);
        }
        else
            _log.LogWarning("[TS] OnLevelsAdjusted {S}: TargetOrderId not set", setup);
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Polls the broker for the fill price of a market entry order.
    /// Retries up to 15 times at 200ms intervals. Returns null on timeout or error.
    /// </summary>
    private async Task<decimal?> GetFillPriceAsync(string orderId)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"{_auth.ApiBaseUrl}/v3/orderexecution/orders/{orderId}";

            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(200);

                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogDebug("[TS] GetFillPrice poll {I} — {Status}", i + 1, (int)resp.StatusCode);
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);

                // Response may wrap in Orders array or return the order directly
                JsonElement order;
                if (doc.RootElement.TryGetProperty("Orders", out var orders))
                {
                    var first = orders.EnumerateArray().FirstOrDefault();
                    order = first.ValueKind == JsonValueKind.Undefined ? doc.RootElement : first;
                }
                else
                {
                    order = doc.RootElement;
                }

                if (order.TryGetProperty("FilledPrice", out var fp))
                {
                    if (fp.ValueKind == JsonValueKind.Number)
                        return fp.GetDecimal();
                    if (fp.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(fp.GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
                        return v;
                }

                // Check status — if still open/queued, keep polling
                var status = order.TryGetProperty("StatusDescription", out var sd) ? sd.GetString() : null;
                if (status != null && (status.Contains("Filled", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("Rejected", StringComparison.OrdinalIgnoreCase)))
                    break;
            }

            _log.LogWarning("[TS] GetFillPrice timed out for order {Id}", orderId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TS] GetFillPriceAsync failed for order {Id}", orderId);
        }

        return null;
    }

    /// <summary>Places bracket order and returns (entryId, targetId, stopId) from Orders array.</summary>
    private async Task<(string? entry, string? target, string? stop)> PlaceBracketAsync(object body)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
            _log.LogDebug("[TS] POST {Url} {Body}", OrdersUrl, json);

            using var resp = await http.PostAsync(OrdersUrl,
                new StringContent(json, Encoding.UTF8, "application/json"));

            var respBody = await resp.Content.ReadAsStringAsync();
            _log.LogInformation("[TS] PlaceBracket raw response: {Body}", respBody);

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

            var result = (GetId(0), GetId(1), GetId(2));
            _log.LogInformation("[TS] PlaceBracket parsed — entry={E} target={T} stop={S}",
                result.Item1, result.Item2, result.Item3);
            return result;
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
            using var http = CreateClient();
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
            using var http = CreateClient();
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

    /// <summary>
    /// Discovers bracket leg order IDs by querying account orders after the entry fills.
    /// TradeStation's OSO/BRK bracket POST response may only return the parent entry ID —
    /// child orders are created by the exchange when the entry fills.
    /// Polls GET /v3/orderexecution/orders/{accountId} and matches by price, direction, and order type.
    /// </summary>
    private async Task<(string? targetId, string? stopId)> FindBracketLegsAsync(
        string entryOrderId, decimal targetPrice, decimal stopPrice, Direction direction)
    {
        const int maxAttempts = 10;
        const int delayMs     = 300;

        var closeAction = direction == Direction.Long ? "SELL" : "BUYTOCOVER";

        try
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(delayMs);

                var token = await _auth.GetAccessTokenAsync();
                using var http = CreateClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var url = $"{_auth.ApiBaseUrl}/v3/orderexecution/orders/{_cfg.AccountId}";
                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogDebug("[TS] FindBracketLegs attempt {I} — GET orders returned {Status}",
                        attempt + 1, (int)resp.StatusCode);
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);

                if (!doc.RootElement.TryGetProperty("Orders", out var orders)) continue;

                string? targetId = null, stopId = null;

                foreach (var order in orders.EnumerateArray())
                {
                    var status = order.TryGetProperty("StatusDescription", out var sd) ? sd.GetString() : "";
                    if (status is null ||
                        !(status.Contains("Open", StringComparison.OrdinalIgnoreCase) ||
                          status.Contains("Ack", StringComparison.OrdinalIgnoreCase) ||
                          status.Contains("Queued", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var orderId = order.TryGetProperty("OrderID", out var oid) ? oid.GetString() : null;
                    if (orderId is null || orderId == entryOrderId) continue;

                    var tradeAction = order.TryGetProperty("TradeAction", out var ta) ? ta.GetString() : "";
                    if (!string.Equals(tradeAction, closeAction, StringComparison.OrdinalIgnoreCase)) continue;

                    var orderType = order.TryGetProperty("OrderType", out var ot) ? ot.GetString() : "";

                    // Match target: Limit order at our target price
                    if (targetId is null && orderType == "Limit" &&
                        order.TryGetProperty("LimitPrice", out var lp))
                    {
                        var lpStr = lp.GetString() ?? lp.ToString();
                        if (lpStr == targetPrice.ToString("F2"))
                            targetId = orderId;
                    }
                    // Match stop: StopMarket order at our stop price
                    else if (stopId is null && (orderType == "StopMarket" || orderType == "Stop") &&
                             order.TryGetProperty("StopPrice", out var sp))
                    {
                        var spStr = sp.GetString() ?? sp.ToString();
                        if (spStr == stopPrice.ToString("F2"))
                            stopId = orderId;
                    }

                    if (targetId is not null && stopId is not null)
                        return (targetId, stopId);
                }

                if (targetId is not null && stopId is not null)
                    return (targetId, stopId);

                _log.LogDebug("[TS] FindBracketLegs attempt {I}/{Max} — target={T} stop={S}",
                    attempt + 1, maxAttempts, targetId, stopId);
            }

            _log.LogWarning("[TS] FindBracketLegsAsync timed out after {Max} attempts", maxAttempts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TS] FindBracketLegsAsync failed");
        }

        return (null, null);
    }

    /// <summary>Replaces/modifies an existing order via PUT /v3/orderexecution/orders/{orderId}.</summary>
    private async Task<bool> ReplaceOrderAsync(string orderId, object body)
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"{OrdersUrl}/{orderId}";
            _log.LogDebug("[TS] PUT {Url}", url);

            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PutAsync(url, content);
            var respBody = await resp.Content.ReadAsStringAsync();
            _log.LogDebug("[TS] Replace {Status} {Body}", (int)resp.StatusCode, respBody);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("[TS] Replace order {Id} failed: {Status} {Body}",
                    orderId, (int)resp.StatusCode, respBody);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TS] ReplaceOrderAsync {Id} failed", orderId);
            return false;
        }
    }
}
