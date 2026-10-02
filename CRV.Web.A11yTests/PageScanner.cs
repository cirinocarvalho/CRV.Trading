using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace CRV.Web.A11yTests;

/// <summary>
/// Opens one route in one theme and viewport, checks the page really rendered in that theme,
/// opens every collapsed section, runs an optional interaction, and scans with axe.
/// </summary>
public static class PageScanner
{
    private static readonly string ScreenshotDir = Path.Combine(AppContext.BaseDirectory, "a11y-screens");

    public static async Task<string?> ScanAsync(A11yAppFixture app, string route, string theme,
        int width, int height, Func<IPage, Task>? interact = null, ITestOutputHelper? output = null)
    {
        await using var context = await app.Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
        });
        await context.AddInitScriptAsync($"try {{ localStorage.setItem('crv-theme', '{theme}'); }} catch (e) {{ }}");
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync(new Uri(app.BaseAddress, route).ToString(),
            new() { WaitUntil = WaitUntilState.NetworkIdle });

        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.Equal(theme, await page.GetAttributeAsync("html", "data-bs-theme"));

        await page.EvaluateAsync("document.querySelectorAll('details:not([open])').forEach(d => d.open = true)");
        if (interact != null) await interact(page);

        var result = await AxeReport.RunAsync(page);
        var scan = $"{route} [{theme}, {width}px]";

        foreach (var item in result.Incomplete)
            output?.WriteLine($"needs review: {scan} {item.Id} — {item.Help}");

        if (!result.Violations.Any()) return null;

        Directory.CreateDirectory(ScreenshotDir);
        var file = string.Concat($"{route}-{theme}-{width}".Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        await page.ScreenshotAsync(new() { Path = Path.Combine(ScreenshotDir, file + ".png"), FullPage = true });

        return AxeReport.Format(scan, result);
    }
}
