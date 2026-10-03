using System.Text.Json;
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Data;
using CRV.Core.Models;
using Microsoft.Extensions.DependencyInjection;
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
            // A slow answer from the plan endpoint must not let the scan see the "Checking…" placeholder.
            await page.RouteAsync("**/api/orders/flatten-all", async route => { await Task.Delay(500); await route.ContinueAsync(); });
            await page.RunAndWaitForResponseAsync(() => page.ClickAsync("#flatten-open"), r => r.Url.EndsWith("/api/orders/flatten-all"));
            await page.Locator("#flatten-sheet").WaitForAsync(new() { State = WaitForSelectorState.Visible });
            await page.Locator("#flatten-body .spinner-border").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            Assert.Equal(0, await page.Locator("#flatten-body .spinner-border").CountAsync());
        }, output, scope: "#flatten-sheet");

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
        }, output, scope: "#ticket");

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

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task BacktestWithNoTrades_ShowsItsSkipsAndRefusals(string theme, int width, int height)
    {
        // The guard skipped every signal of one setup and the budget refused the other's: no trades at all.
        SizeRefusal Refusal(string setup, int minute, RefusalReason reason) => new(
            Time: DateTime.UtcNow.Date.AddHours(14).AddMinutes(minute), SetupLabel: setup, Ticker: "/MESZ26",
            StopDistance: 4m, RiskPerContract: 20m, Budget: 100m,
            Reason: reason, Rr: reason == RefusalReason.MinRr ? 1.2m : 0m, MinRr: reason == RefusalReason.MinRr ? 1.5m : 0m);
        var refusals = new List<SizeRefusal>
        {
            Refusal("a11y-skipped", 0, RefusalReason.MinRr),
            Refusal("a11y-skipped", 5, RefusalReason.MinRr),
            Refusal("a11y-refused", 10, RefusalReason.Size),
        };
        var result = BacktestResultCalculator.Calculate(new List<TradeRecord>(), new StrategyConfig(), new BacktestConfig(), refusals);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var run = new BacktestRunRow
        {
            Ticker = "/MESZ26", ConfigName = "a11y-no-trades", RunAt = DateTime.UtcNow,
            ResultJson = JsonSerializer.Serialize(result),
        };
        db.BacktestRuns.Add(run);
        db.SaveChanges();
        try
        {
            var report = await PageScanner.ScanAsync(app, $"/review/results?source=backtest&run={run.Id}", theme, width, height, async page =>
            {
                Assert.Equal(1, await page.Locator(".c-empty:visible").CountAsync());
                var skips = page.Locator("[data-part='skips']");
                Assert.True(await skips.IsVisibleAsync());
                var text = await skips.InnerTextAsync();
                Assert.Matches(@"Skipped below minimum R\s+2", text);
                Assert.Matches(@"Refused for size\s+1", text);
                Assert.Contains("a11y-skipped", text);
                Assert.Contains("a11y-refused", text);
            }, output);

            Assert.True(report is null, report);
        }
        finally
        {
            db.BacktestRuns.Remove(run);
            db.SaveChanges();
        }
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task StatusBadges_InEveryState_HaveNoWcagViolations(string theme, int width, int height)
    {
        // The cockpit script switches badges between these classes as the engine runs. A stopped
        // engine shows only a few of them, so one badge per state is added next to the drawdown badge.
        var report = await PageScanner.ScanAsync(app, "/dashboard", theme, width, height, page => page.EvaluateAsync("""
            const host = document.getElementById('dash-dd-badge').parentElement;
            ['bg-success', 'bg-danger', 'bg-secondary', 'bg-warning', 'badge-halted', 'badge-live',
             'badge-paper', 'setup-armed', 'setup-idle', 'setup-retest', 'setup-long', 'setup-short'].forEach(c => {
                const b = document.createElement('span');
                b.className = 'badge ' + c;
                b.textContent = c;
                host.appendChild(b);
            });
            """), output);

        Assert.True(report is null, report);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task CockpitSetupCards_InEveryState_HaveNoWcagViolations(string theme, int width, int height)
    {
        // Cards are built from engine snapshots, and the test host never starts the engine. The page
        // is told the engine runs (display only, nothing is sent) and given the snapshot event the hub sends.
        var report = await PageScanner.ScanAsync(app, "/dashboard", theme, width, height, async page =>
        {
            await page.EvaluateAsync("""
                json => {
                    CRV.engine.status('Live');
                    document.dispatchEvent(new CustomEvent('crv:update', { detail: JSON.parse(json) }));
                }
                """, CockpitSnapshot.Json());
            // Every card has finished drawing once each status badge reads what its snapshot implies.
            // A listener error leaves a card on its placeholder, so this times out instead of scanning it.
            await page.WaitForFunctionAsync("""
                expected => JSON.stringify([...document.querySelectorAll('#setup-cards-container [id^="status-"]')]
                    .map(b => b.textContent)) === JSON.stringify(expected)
                """, CockpitSnapshot.ExpectedStatuses, new() { Timeout = 10_000 });
        }, output);

        Assert.True(report is null, report);
    }
}
