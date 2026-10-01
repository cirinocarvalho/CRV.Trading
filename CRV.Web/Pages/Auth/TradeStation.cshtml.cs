namespace CRV.Web.Pages.Auth;

using CRV.Live.Brokers.TradeStation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
/// TradeStation's OAuth2 redirect URI. It must stay at /auth/tradestation because that
/// address is registered with TradeStation; the login is finished here and the user
/// returns to Brokers.
/// </summary>
public class TradeStationModel : PageModel
{
    private readonly TradeStationAuthService    _auth;
    private readonly ILogger<TradeStationModel> _log;

    public TradeStationModel(TradeStationAuthService auth, ILogger<TradeStationModel> log)
    {
        _auth = auth;
        _log  = log;
    }

    /// <summary>TradeStation appends ?code=... (success) or ?error=... (denied) to the redirect URI.</summary>
    public async Task<IActionResult> OnGetAsync(string? code, string? error)
    {
        if (error is not null)
            TempData["brokers_err"] = $"TradeStation login was refused: {error}";
        else if (code is not null)
        {
            try
            {
                await _auth.ExchangeCodeAsync(code);
                TempData["brokers_msg"] = "Connected to TradeStation.";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "TradeStation code exchange failed");
                TempData["brokers_err"] = $"TradeStation didn't connect: {ex.Message}";
            }
        }
        return Redirect("/setup/brokers");
    }

    /// <summary>Sends the browser to TradeStation's login page.</summary>
    public IActionResult OnGetAuthorize() => Redirect(_auth.BuildAuthorizationUrl());
}
