using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace CRV.Web.A11yTests;

[Collection(A11yCollection.Name)]
public class PageScanTests(A11yAppFixture app, ITestOutputHelper output)
{
    public static TheoryData<string, string, int, int> Pages()
    {
        var data = new TheoryData<string, string, int, int>();
        foreach (var route in A11yPages.Routes)
        foreach (var theme in A11yPages.Themes)
        foreach (var (w, h) in A11yPages.Viewports)
            data.Add(route, theme, w, h);
        return data;
    }

    public static TheoryData<string, int, int> Variants()
    {
        var data = new TheoryData<string, int, int>();
        foreach (var theme in A11yPages.Themes)
        foreach (var (w, h) in A11yPages.Viewports)
            data.Add(theme, w, h);
        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Page_HasNoWcagViolations(string route, string theme, int width, int height)
    {
        var report = await PageScanner.ScanAsync(app, route, theme, width, height, output: output);

        Assert.True(report is null, report);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task FlattenSheet_HasNoWcagViolations(string theme, int width, int height)
    {
        // Opens the confirmation sheet only. The hold-to-flatten button is never pressed.
        var report = await PageScanner.ScanAsync(app, "/dashboard", theme, width, height, async page =>
        {
            await page.ClickAsync("#flatten-open");
            await page.Locator("#flatten-sheet").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        }, output);

        Assert.True(report is null, report);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task OrderTicket_HasNoWcagViolations(string theme, int width, int height)
    {
        // ?ticket=1 opens the drawer through crv-ticket.js. Nothing is submitted.
        var report = await PageScanner.ScanAsync(app, "/dashboard?ticket=1", theme, width, height, async page =>
        {
            await page.Locator("#ticket").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        }, output);

        Assert.True(report is null, report);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task LegacySetups_HaveNoWcagViolations(string theme, int width, int height)
    {
        // Setups A–D render on the Risk page only while the ORB basket is empty.
        A11ySeed.SetOrbBasket(app.Services, "");
        try
        {
            var report = await PageScanner.ScanAsync(app, "/setup/risk", theme, width, height, async page =>
            {
                await page.GetByText("Setups A–D").First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
                // Each setup's detail row starts collapsed; show them all so their fields are scanned too.
                await page.EvaluateAsync("document.querySelectorAll('.setup-detail-row').forEach(r => r.style.display = '')");
            }, output);

            Assert.True(report is null, report);
        }
        finally
        {
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }
}
