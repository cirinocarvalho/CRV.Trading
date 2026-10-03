using System.Text.Json;
using CRV.Backtest.Results;
using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>
/// The setup page's dollar-target preview and reward / risk guard. Saving only rewrites the
/// seeded basket (restored afterwards); nothing is sent to a broker and the engine never runs.
/// </summary>
[Collection(A11yCollection.Name)]
public class StrategyPageTests(A11yAppFixture app)
{
    private async Task<IPage> Open(IBrowserContext ctx, string id)
    {
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, "/setup/strategies/" + id).ToString(),
            new() { WaitUntil = WaitUntilState.NetworkIdle });
        return page;
    }

    private static ILocator ById(IPage page, string id) => page.Locator($"[id='{id}']");

    private static async Task<string> Text(IPage page, string id) => (await ById(page, id).TextContentAsync())!.Trim();

    /// <summary>Clicks Save and waits for the page it lands on: the form again with errors, or the redirect after saving.
    /// Both have the same URL, so the old document is marked and the wait ends once the marker is gone.</summary>
    private static async Task Save(IPage page)
    {
        await page.EvaluateAsync("() => { window.stBeforeSave = true; }");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !window.stBeforeSave");
        await page.WaitForLoadStateAsync(LoadState.Load);
    }

    [Theory]
    [InlineData(2, "40 pts · $400 over 2 contracts", "$400", "$300 (1 at partial, 1 at target)")]
    [InlineData(1, "80 pts · $400 over 1 contract",  "$400", "No partial with 1 contract")]
    public async Task DollarPreview_WholePosition_ShowsBothOutcomes(int contracts, string dist, string all, string mix)
    {
        // MES at $5 a point, $400 for the whole position, partial at 50%.
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await Open(ctx, A11ySeed.DollarsId);

        await ById(page, "Entry.Config.TargetDollars").FillAsync("400");
        await page.Locator("label[for='st-basis-WholePosition']").ClickAsync();
        await ById(page, "st-preview-cts").FillAsync(contracts.ToString());

        Assert.Equal(dist, await Text(page, "st-u-dist"));
        Assert.Equal(all,  await Text(page, "st-u-all"));
        Assert.Equal(mix,  await Text(page, "st-u-mix"));
    }

    [Fact]
    public async Task GuardOff_GreysItsFieldsAndShowsTheBadge()
    {
        await using var ctx = await app.Browser.NewContextAsync();

        var off = await Open(ctx, A11ySeed.GuardOffId);            // strategy off: grey badge
        Assert.True(await ById(off, "Entry.Config.MinRr").IsDisabledAsync());
        Assert.True(await ById(off, "Entry.Config.MinRrAction").IsDisabledAsync());
        Assert.Equal(2, await off.Locator(".st-rr-off:visible").CountAsync());
        Assert.DoesNotContain("warn", await ById(off, "st-rr-badge").GetAttributeAsync("class"));

        var on = await Open(ctx, A11ySeed.DollarsId);              // strategy on: amber badge
        Assert.Contains("warn", await ById(on, "st-rr-badge").GetAttributeAsync("class"));

        await on.Locator("input[type=checkbox][name='Entry.Config.EnforceMinRr']").CheckAsync();
        Assert.False(await ById(on, "Entry.Config.MinRr").IsDisabledAsync());
        Assert.Equal(0, await on.Locator(".st-rr-off:visible").CountAsync());
    }

    [Fact]
    public async Task SaveBelowMinimum_WithBacktestHistory_IsBlocked()
    {
        // Three backtest trades with a 40-point stop: 1.5R x 40 pts x $5 = $300 a contract minimum.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var trades = Enumerable.Range(1, 3).Select(d => new TradeRecord
        {
            SetupLabel = A11ySeed.DollarsId, Ticker = "/MESZ26", Direction = Direction.Long, Contracts = 1,
            Entry = 6000m, InitialStop = 5960m, Target = 6080m, Exit = 6080m, ExitReason = ExitReason.Target,
            EnteredAt = DateTime.UtcNow.Date.AddDays(-d), ExitedAt = DateTime.UtcNow.Date.AddDays(-d).AddMinutes(30),
        }).ToList();
        var run = new BacktestRunRow
        {
            Ticker = "/MESZ26", ConfigName = "a11y-history", RunAt = DateTime.UtcNow,
            TotalTrades = trades.Count,
            ResultJson = JsonSerializer.Serialize(new BacktestResult { Trades = trades }),
        };
        db.BacktestRuns.Add(run);
        db.SaveChanges();
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, A11ySeed.DollarsId);
            Assert.Contains("40 pts", await page.Locator(".st-field", new() { HasText = "Typical stop" }).TextContentAsync());

            await page.Locator("input[type=checkbox][name='Entry.Config.EnforceMinRr']").CheckAsync();
            await page.Locator("label[for='st-basis-PerContract']").ClickAsync();
            await ById(page, "Entry.Config.TargetDollars").FillAsync("50");
            await Save(page);

            Assert.Contains("Raise the target to at least $300 a contract",
                await page.Locator(".c-note.bad[role=alert]").TextContentAsync());
        }
        finally
        {
            db.BacktestRuns.Remove(run);
            db.SaveChanges();
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }

    [Fact]
    public async Task TypicalStop_ReadsPastNewerRunsWithoutThisSetup()
    {
        // 25 newer runs hold only another setup's trades; this setup's 40-point stops are in an older run.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        BacktestRunRow Run(string setup, int daysAgo)
        {
            var trades = Enumerable.Range(1, 3).Select(d => new TradeRecord
            {
                SetupLabel = setup, Ticker = "/MESZ26", Direction = Direction.Long, Contracts = 1,
                Entry = 6000m, InitialStop = 5960m, Target = 6080m, Exit = 6080m, ExitReason = ExitReason.Target,
                EnteredAt = DateTime.UtcNow.Date.AddDays(-daysAgo - d), ExitedAt = DateTime.UtcNow.Date.AddDays(-daysAgo - d).AddMinutes(30),
            }).ToList();
            return new BacktestRunRow
            {
                Ticker = "/MESZ26", ConfigName = "a11y-history", RunAt = DateTime.UtcNow.AddDays(-daysAgo),
                TotalTrades = trades.Count,
                ResultJson = JsonSerializer.Serialize(new BacktestResult { Trades = trades }),
            };
        }
        var runs = Enumerable.Range(1, 25).Select(d => Run("another-setup", d))
            .Append(Run(A11ySeed.DollarsId, 30)).ToList();
        db.BacktestRuns.AddRange(runs);
        db.SaveChanges();
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, A11ySeed.DollarsId);
            Assert.Contains("40 pts", await page.Locator(".st-field", new() { HasText = "Typical stop" }).TextContentAsync());
        }
        finally
        {
            db.BacktestRuns.RemoveRange(runs);
            db.SaveChanges();
        }
    }

    [Fact]
    public async Task SaveWithoutHistory_PassesWithTheWarning()
    {
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, A11ySeed.DollarsId);

            await page.Locator("input[type=checkbox][name='Entry.Config.EnforceMinRr']").CheckAsync();
            await ById(page, "Entry.Config.TargetDollars").FillAsync("50");
            await Save(page);

            Assert.Contains("The reward / risk check runs once this strategy has a backtest.",
                await page.Locator(".c-note.warn").First.TextContentAsync());
        }
        finally
        {
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }

    [Fact]
    public async Task SaveZeroDollarsTarget_IsBlocked()
    {
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, A11ySeed.DollarsId);

            await ById(page, "Entry.Config.TargetDollars").FillAsync("0");
            await Save(page);

            Assert.Contains("Dollars target must be above 0.",
                await page.Locator(".c-note.bad[role=alert]").TextContentAsync());
            var cfg = app.Services.GetRequiredService<StrategyBasketService>().Find(A11ySeed.DollarsId)!.Entry.Config;
            Assert.Equal(150m, cfg.TargetDollars);
        }
        finally
        {
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }

    [Fact]
    public async Task SaveWithGuardOff_KeepsStoredMinimumAndAction()
    {
        try
        {
            await using var ctx = await app.Browser.NewContextAsync();
            var page = await Open(ctx, A11ySeed.GuardOffId);

            await ById(page, "Entry.Config.TargetDollars").FillAsync("450");
            await Save(page);

            Assert.Equal(0, await page.Locator(".c-note.bad[role=alert]").CountAsync());
            var cfg = app.Services.GetRequiredService<StrategyBasketService>().Find(A11ySeed.GuardOffId)!.Entry.Config;
            Assert.Equal(450m, cfg.TargetDollars);
            Assert.False(cfg.EnforceMinRr);
            Assert.Equal(2.5m, cfg.MinRr);
            Assert.Equal(MinRrAction.RaiseTarget, cfg.MinRrAction);
        }
        finally
        {
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }

    [Fact]
    public async Task StrategiesList_FlagsGuardOffStrategies_AmberWhenOnGreyWhenOff()
    {
        await using var ctx = await app.Browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, "/setup/strategies").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });

        var notes = string.Join(" ", await page.Locator(".c-note.warn").AllTextContentsAsync());
        Assert.Contains("1 strategy that's on doesn't enforce its minimum reward / risk", notes);
        var on  = page.Locator($".st-row:has(a[href='/setup/strategies/{A11ySeed.DollarsId}'])");
        var off = page.Locator($".st-row:has(a[href='/setup/strategies/{A11ySeed.GuardOffId}'])");
        Assert.Equal("c-badge warn", await on.Locator(".c-badge").GetAttributeAsync("class"));
        Assert.Equal("c-badge", (await off.Locator(".c-badge").GetAttributeAsync("class"))!.Trim());
        Assert.Contains("target $150 / position", await on.InnerTextAsync());
        Assert.Contains("target $400 / contract", await off.InnerTextAsync());
        Assert.Equal(0, await page.Locator($".st-row:has(a[href='/setup/strategies/{A11ySeed.RetestId}']) .c-badge").CountAsync());
    }
}
