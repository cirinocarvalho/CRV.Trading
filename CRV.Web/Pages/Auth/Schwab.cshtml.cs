namespace CRV.Web.Pages.Auth;

using System.Net.Http.Headers;
using System.Text.Json;
using CRV.Live.Brokers.Schwab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class SchwabModel : PageModel
{
    private readonly SchwabAuthService    _auth;
    private readonly ILogger<SchwabModel> _log;
    private readonly IHttpClientFactory   _httpFactory;

    public bool    IsAuthenticated => _auth.IsAuthenticated;
    public string? ErrorMessage    { get; private set; }
    public string? SuccessMessage  { get; private set; }

    /// <summary>
    /// Account hashes fetched from the Schwab API (populated when connected).
    /// Each entry is (displayNumber, hashValue, accountType) — hashValue goes in Schwab:AccountId.
    /// </summary>
    public List<(string AccountNumber, string Hash, string AccountType)> Accounts { get; } = new();

    public SchwabModel(SchwabAuthService auth, ILogger<SchwabModel> log, IHttpClientFactory httpFactory)
    {
        _auth        = auth;
        _log         = log;
        _httpFactory = httpFactory;
    }

    /// <summary>
    /// Handles both the initial page load and the OAuth2 callback.
    /// Schwab appends ?code=... (success) or ?error=... (denied) to the redirect URI.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(string? code, string? error)
    {
        if (error is not null)
        {
            ErrorMessage = $"Schwab authorization denied: {error}";
            return Page();
        }

        if (code is not null)
        {
            try
            {
                await _auth.ExchangeCodeAsync(code);
                SuccessMessage = "Successfully connected to Schwab! Tokens stored and ready.";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Schwab code exchange failed");
                ErrorMessage = $"Authorization failed: {ex.Message}";
            }
        }

        // When connected, fetch the account list so we can show the hash value
        if (_auth.IsAuthenticated)
            await TryLoadAccountsAsync();

        return Page();
    }

    /// <summary>Redirects the browser to the Schwab OAuth2 authorization page.</summary>
    public IActionResult OnGetAuthorize() => Redirect(_auth.BuildAuthorizationUrl());

    // ── Helpers ──────────────────────────────────────────────────

    private async Task TryLoadAccountsAsync()
    {
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            using var http = _httpFactory.CreateClient("Schwab");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Step 1: fetch account number → hash mapping
            var numbersResp = await http.GetAsync($"{_auth.ApiBaseUrl}/trader/v1/accounts/accountNumbers");
            if (!numbersResp.IsSuccessStatusCode) return;

            var numbersJson = await numbersResp.Content.ReadAsStringAsync();
            using var numbersDoc = JsonDocument.Parse(numbersJson);

            // Response: [ { "accountNumber": "XXXX1234", "hashValue": "ABC..." }, ... ]
            var hashMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in numbersDoc.RootElement.EnumerateArray())
            {
                var num  = item.TryGetProperty("accountNumber", out var n) ? n.GetString() ?? "" : "";
                var hash = item.TryGetProperty("hashValue",     out var h) ? h.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(num) && !string.IsNullOrEmpty(hash))
                    hashMap[num] = hash;
            }

            // Step 2: fetch account types (MARGIN, CASH, FUTURES, etc.)
            // Response: [ { "securitiesAccount": { "type": "FUTURES", "accountNumber": "XXXX1234", ... } } ]
            var typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var accountsResp = await http.GetAsync($"{_auth.ApiBaseUrl}/trader/v1/accounts");
            if (accountsResp.IsSuccessStatusCode)
            {
                var accountsJson = await accountsResp.Content.ReadAsStringAsync();
                using var accountsDoc = JsonDocument.Parse(accountsJson);
                foreach (var item in accountsDoc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("securitiesAccount", out var sa)) continue;
                    var num  = sa.TryGetProperty("accountNumber", out var n) ? n.GetString() ?? "" : "";
                    var type = sa.TryGetProperty("type",          out var t) ? t.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(num))
                        typeMap[num] = type;
                }
            }

            // Step 3: join hash + type
            foreach (var (num, hash) in hashMap)
            {
                typeMap.TryGetValue(num, out var accountType);
                Accounts.Add((num, hash, accountType ?? ""));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not fetch Schwab account numbers");
        }
    }
}
