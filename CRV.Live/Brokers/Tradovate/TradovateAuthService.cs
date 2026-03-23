namespace CRV.Live.Brokers.Tradovate;

using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tradovate authentication — direct credential POST (NOT OAuth2).
/// Manages accessToken (for trading) and mdAccessToken (for market data).
/// Tokens expire in 90 minutes; renewed automatically when less than 5 min remain.
/// Persists tokens to a JSON file so they survive app restarts.
/// </summary>
public class TradovateAuthService
{
    private readonly string  _username;
    private readonly string  _password;
    private readonly int     _cid;
    private readonly string  _secret;
    private readonly string  _deviceId;
    private readonly string  _appId;
    private readonly string  _tokenFile;
    private readonly ILogger _log;
    private readonly IHttpClientFactory? _httpFactory;

    public string ApiBaseUrl { get; }
    public string MdWssUrl   { get; }

    private string?  _accessToken;
    private string?  _mdAccessToken;
    private DateTime _expiresAt   = DateTime.MinValue;
    private DateTime _mdExpiresAt = DateTime.MinValue;

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAt;

    public TradovateAuthService(
        string username, string password, int cid, string secret,
        string deviceId, string appId, string tokenFile,
        string apiBaseUrl = "https://live.tradovateapi.com/v1",
        string mdWssUrl   = "wss://md.tradovateapi.com/v1/websocket",
        IHttpClientFactory? httpFactory = null,
        ILogger<TradovateAuthService>? log = null)
    {
        _username    = username;
        _password    = password;
        _cid         = cid;
        _secret      = secret;
        _deviceId    = deviceId;
        _appId       = appId;
        _tokenFile   = tokenFile;
        _httpFactory = httpFactory;
        ApiBaseUrl   = apiBaseUrl;
        MdWssUrl     = mdWssUrl;
        _log         = log ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        TryLoadFromFile();
    }

    private HttpClient CreateClient() => _httpFactory?.CreateClient("Tradovate") ?? new HttpClient();

    /// <summary>Returns a valid access token, renewing if less than 5 min remain.</summary>
    public async Task<string> GetAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAt.AddMinutes(-5))
            return _accessToken;

        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _expiresAt)
        {
            await RenewTokenAsync();
            return _accessToken!;
        }

        await AuthenticateAsync();
        return _accessToken!;
    }

    /// <summary>Returns a valid market data access token.</summary>
    public async Task<string> GetMdAccessTokenAsync()
    {
        await GetAccessTokenAsync();
        return _mdAccessToken ?? _accessToken!;
    }

    /// <summary>POST /auth/accesstokenrequest — full re-authentication.</summary>
    public async Task AuthenticateAsync()
    {
        using var http = CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name       = _username,
            password   = _password,
            appId      = _appId,
            appVersion = "0.0.1",
            deviceId   = _deviceId,
            cid        = _cid,
            sec        = _secret
        });
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/auth/accesstokenrequest")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        var res  = await http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new Exception($"Tradovate auth failed ({(int)res.StatusCode}): {json}");

        ParseTokenResponse(json);

        if (string.IsNullOrEmpty(_accessToken))
            throw new Exception($"Tradovate auth returned OK but no accessToken in response: {json}");

        SaveToFile();
        _log.LogInformation("Tradovate authenticated — tokens valid until {Expiry}", _expiresAt);
    }

    /// <summary>POST /auth/renewAccessToken — extend expiry without re-entering credentials.</summary>
    public async Task RenewTokenAsync()
    {
        if (string.IsNullOrEmpty(_accessToken)) { await AuthenticateAsync(); return; }

        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
        var res  = await http.PostAsync($"{ApiBaseUrl}/auth/renewAccessToken", null);
        var json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("Tradovate token renewal failed — re-authenticating. Status: {Status}", (int)res.StatusCode);
            await AuthenticateAsync();
            return;
        }

        ParseTokenResponse(json);
        SaveToFile();
        _log.LogInformation("Tradovate token renewed — valid until {Expiry}", _expiresAt);
    }

    // ── Contract lookup ────────────────────────────────────────

    /// <summary>
    /// Resolves a Tradovate contract name (e.g. "NQM6") to its numeric contract ID.
    /// Caches results for the session lifetime.
    /// </summary>
    private readonly Dictionary<string, int> _contractIdCache = new();

    public async Task<int?> FindContractIdAsync(string contractName)
    {
        if (_contractIdCache.TryGetValue(contractName, out var cached))
            return cached;

        var token = await GetAccessTokenAsync();
        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url = $"{ApiBaseUrl}/contract/find?name={Uri.EscapeDataString(contractName)}";
        var res = await http.GetAsync(url);
        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("Tradovate contract/find failed for {Name}: {Status}",
                contractName, (int)res.StatusCode);
            return null;
        }

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
        {
            var id = idProp.GetInt32();
            _contractIdCache[contractName] = id;
            _log.LogInformation("Tradovate contract {Name} → id {Id}", contractName, id);
            return id;
        }

        _log.LogWarning("Tradovate contract/find returned no id for {Name}", contractName);
        return null;
    }

    // ── Private helpers ─────────────────────────────────────────

    private void ParseTokenResponse(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken   = root.TryGetProperty("accessToken",   out var at) ? at.GetString() : null;
        _mdAccessToken = root.TryGetProperty("mdAccessToken", out var md) ? md.GetString() : null;

        if (root.TryGetProperty("expirationTime", out var exp))
        {
            if (exp.ValueKind == JsonValueKind.Number)
                _expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()).UtcDateTime;
            else if (exp.ValueKind == JsonValueKind.String &&
                     DateTime.TryParse(exp.GetString(), out var dt))
                _expiresAt = dt.ToUniversalTime();
            else
                _expiresAt = DateTime.UtcNow.AddMinutes(85);
        }
        else
        {
            _expiresAt = DateTime.UtcNow.AddMinutes(85);
        }
        _mdExpiresAt = _expiresAt;
    }

    private void SaveToFile()
    {
        try
        {
            var obj = new
            {
                accessToken   = _accessToken,
                mdAccessToken = _mdAccessToken,
                expiresAt     = _expiresAt.ToString("o"),
            };
            File.WriteAllText(_tokenFile, JsonSerializer.Serialize(obj,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save Tradovate tokens to file."); }
    }

    private void TryLoadFromFile()
    {
        try
        {
            if (!File.Exists(_tokenFile)) return;
            using var doc  = JsonDocument.Parse(File.ReadAllText(_tokenFile));
            var root = doc.RootElement;
            _accessToken   = root.TryGetProperty("accessToken",   out var at) ? at.GetString() : null;
            _mdAccessToken = root.TryGetProperty("mdAccessToken", out var md) ? md.GetString() : null;
            if (root.TryGetProperty("expiresAt", out var exp) &&
                DateTime.TryParse(exp.GetString(), out var dt))
                _expiresAt = dt.ToUniversalTime();
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not load Tradovate token file"); }
    }
}
