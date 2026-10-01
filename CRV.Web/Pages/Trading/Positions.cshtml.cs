namespace CRV.Web.Pages.Trading;

using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>Live view of everything open. Data comes from /api/orders/book; actions from /api/orders.</summary>
public class PositionsModel : PageModel
{
    public void OnGet() { }
}
