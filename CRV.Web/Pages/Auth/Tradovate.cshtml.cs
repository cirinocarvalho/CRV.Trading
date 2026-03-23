namespace CRV.Web.Pages.Auth;

using CRV.Live.Brokers.Tradovate;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class TradovateAuthModel : PageModel
{
    private readonly TradovateAuthService        _auth;
    private readonly ILogger<TradovateAuthModel> _log;

    public bool    IsAuthenticated => _auth.IsAuthenticated;
    public string? ErrorMessage    { get; private set; }
    public string? SuccessMessage  { get; private set; }

    public TradovateAuthModel(TradovateAuthService auth, ILogger<TradovateAuthModel> log)
    {
        _auth = auth;
        _log  = log;
    }

    public void OnGet() { }

    /// <summary>POST handler — triggers full re-authentication with credentials from user-secrets.</summary>
    public async Task<IActionResult> OnPostConnectAsync()
    {
        try
        {
            await _auth.AuthenticateAsync();
            SuccessMessage = "Successfully connected to Tradovate! Tokens stored and ready.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tradovate authentication failed");
            ErrorMessage = $"Authentication failed: {ex.Message}";
        }
        return Page();
    }

    /// <summary>Renews the existing access token.</summary>
    public async Task<IActionResult> OnPostRenewAsync()
    {
        try
        {
            await _auth.RenewTokenAsync();
            SuccessMessage = "Token renewed successfully.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tradovate token renewal failed");
            ErrorMessage = $"Renewal failed: {ex.Message}";
        }
        return Page();
    }
}
