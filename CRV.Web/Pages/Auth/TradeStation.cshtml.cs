namespace CRV.Web.Pages.Auth;

using CRV.Live.Brokers.TradeStation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class TradeStationModel : PageModel
{
    private readonly TradeStationAuthService    _auth;
    private readonly ILogger<TradeStationModel> _log;

    public bool    IsAuthenticated => _auth.IsAuthenticated;
    public string? ErrorMessage    { get; private set; }
    public string? SuccessMessage  { get; private set; }

    public TradeStationModel(TradeStationAuthService auth, ILogger<TradeStationModel> log)
    {
        _auth = auth;
        _log  = log;
    }

    /// <summary>
    /// Handles both the initial page load and the OAuth2 callback.
    /// TradeStation appends ?code=... (success) or ?error=... (denied) to the redirect URI.
    /// </summary>
    public async Task<IActionResult> OnGetAsync(string? code, string? error)
    {
        if (error is not null)
        {
            ErrorMessage = $"TradeStation authorization denied: {error}";
            return Page();
        }

        if (code is not null)
        {
            try
            {
                await _auth.ExchangeCodeAsync(code);
                SuccessMessage = "Successfully connected to TradeStation! Tokens stored and ready.";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "TradeStation code exchange failed");
                ErrorMessage = $"Authorization failed: {ex.Message}";
            }
        }

        return Page();
    }

    /// <summary>Redirects the browser to the TradeStation OAuth2 authorization page.</summary>
    public IActionResult OnGetAuthorize() => Redirect(_auth.BuildAuthorizationUrl());
}
