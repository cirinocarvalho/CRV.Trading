namespace CRV.Web.Pages.Auth;

using CRV.Live.Brokers.Schwab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
/// Schwab's OAuth2 redirect URI. It must stay at /auth/schwab because that address is
/// registered with Schwab; the login is finished here and the user returns to Brokers.
/// </summary>
public class SchwabModel : PageModel
{
    private readonly SchwabAuthService    _auth;
    private readonly ILogger<SchwabModel> _log;

    public SchwabModel(SchwabAuthService auth, ILogger<SchwabModel> log)
    {
        _auth = auth;
        _log  = log;
    }

    /// <summary>Schwab appends ?code=... (success) or ?error=... (denied) to the redirect URI.</summary>
    public async Task<IActionResult> OnGetAsync(string? code, string? error)
    {
        if (error is not null)
            TempData["brokers_err"] = $"Schwab login was refused: {error}";
        else if (code is not null)
        {
            try
            {
                await _auth.ExchangeCodeAsync(code);
                TempData["brokers_msg"] = "Connected to Schwab.";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Schwab code exchange failed");
                TempData["brokers_err"] = $"Schwab didn't connect: {ex.Message}";
            }
        }
        return Redirect("/setup/brokers");
    }

    /// <summary>Sends the browser to Schwab's login page.</summary>
    public IActionResult OnGetAuthorize() => Redirect(_auth.BuildAuthorizationUrl());
}
