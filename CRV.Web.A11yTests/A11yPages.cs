namespace CRV.Web.A11yTests;

/// <summary>Every real page, in both themes, at desktop and phone width. OAuth callbacks and legacy redirects are left out.</summary>
public static class A11yPages
{
    public static readonly string[] Routes =
    [
        "/dashboard",
        "/dashboard/prospectus",
        "/dashboard/sessions",
        "/review/results?source=live",
        "/review/results?source=backtest",
        "/validation",
        "/trading/positions",
        "/trading/orders",
        "/options/explorer",
        "/options/positions",
        "/setup/strategies",
        "/setup/strategies/" + A11ySeed.RetestId,
        "/setup/strategies/" + A11ySeed.RetiredId,
        "/setup/strategies/" + A11ySeed.DollarsId,
        "/setup/strategies/" + A11ySeed.GuardOffId,
        "/setup/brokers",
        "/setup/risk",
        "/setup/alerts",
    ];

    public static readonly string[] Themes = ["dark", "light"];

    public static readonly (int Width, int Height)[] Viewports = [(1440, 900), (390, 844)];
}
