namespace CRV.Web.Pages.Options;

using CRV.Live.Brokers.Schwab;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
/// Open option positions, exposure, working orders and submitted structures. The data comes
/// from the Explorer's handlers (/options/explorer?handler=...), which own the Schwab calls,
/// the risk ceilings and the closing-order rules; this page only shows and acts on it.
/// </summary>
public class PositionsModel : PageModel
{
    private readonly SchwabAuthService _schwab;
    private readonly IConfiguration    _config;

    public PositionsModel(SchwabAuthService schwab, IConfiguration config)
    {
        _schwab = schwab;
        _config = config;
    }

    public bool LiveOrdersEnabled   => _config.GetValue("Options:AllowLiveOrders", false);
    public bool SchwabAuthenticated => _schwab.IsAuthenticated;
    public bool AccountConfigured   => !string.IsNullOrEmpty(_config["Schwab:AccountId"]);

    public void OnGet() { }
}
