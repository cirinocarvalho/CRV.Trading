using CRV.Core.Models;
using Microsoft.Playwright;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>What a cockpit setup card says about an open trade, drawn from a running-engine snapshot.</summary>
[Collection(A11yCollection.Name)]
public class CockpitCardTests(A11yAppFixture app)
{
    [Fact]
    public async Task TradeWithoutPartial_ShowsNoPartial_AndTargetCountsEveryContract()
    {
        // 2 contracts short from 21250, one target at 21210, MES at $2/pt: 40 pts × 2 cts × $2 = $160.
        var cells = await CardCells(Trade("a11y-nopartial", new ActiveTradeView
        {
            Setup = SetupId.F, Direction = Direction.Short, Entry = 21250m, InitialStop = 21270m,
            CurrentStop = 21270m, Target = 21210m, Partial = 0m, Contracts = 2, RemainingContracts = 2,
            LastPrice = 21236.25m, PointValue = 2m, Ticker = "/MESZ26", GroupStatus = "Filled",
        }));

        Assert.Equal("—", cells["partial"]);
        Assert.Equal("21210.00 | $160 | 40.00 pts", cells["target"]);
    }

    [Fact]
    public async Task TradeWithPartialFilled_ShowsPartialAndTargetForEachHalf()
    {
        // 2 contracts long from 21200: 1 out at 21230 (30 pts × $2 = $60), 1 to 21260 (60 pts × $2 = $120).
        var cells = await CardCells(Trade("a11y-partial", new ActiveTradeView
        {
            Setup = SetupId.F, Direction = Direction.Long, Entry = 21200m, InitialStop = 21180m,
            CurrentStop = 21200m, Target = 21260m, Partial = 21230m, Contracts = 2, PartialContracts = 1,
            RemainingContracts = 1, PartialFilled = true, LastPrice = 21236.25m, PointValue = 2m,
            Ticker = "/MESZ26", GroupStatus = "PartialFilled",
        }));

        Assert.Equal("21230.00 | $60 | 30.00 pts ✓", cells["partial"]);
        Assert.Equal("21260.00 | $180 | 60.00 pts", cells["target"]);
    }

    private static SetupSnapshot Trade(string id, ActiveTradeView trade) =>
        CockpitSnapshot.Setup(id, "ORB fakeout [MES]", "OrbFakeout", state: 0, trade: trade);

    private async Task<Dictionary<string, string>> CardCells(SetupSnapshot setup)
    {
        await using var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, "/dashboard").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.EvaluateAsync("""
            json => {
                CRV.engine.status('Live');
                document.dispatchEvent(new CustomEvent('crv:update', { detail: JSON.parse(json) }));
            }
            """, CockpitSnapshot.Json([setup]));

        var card = page.Locator($"#card-{setup.Id}");
        await card.Locator("[id^='status-']").Filter(new() { HasTextRegex = new("ACTIVE") }).WaitForAsync();
        return new()
        {
            ["partial"] = (await card.Locator($"#{setup.Id}-partial").TextContentAsync())!.Trim(),
            ["target"]  = (await card.Locator($"#{setup.Id}-target").TextContentAsync())!.Trim(),
        };
    }

    [Fact]
    public async Task DisabledSetup_ShowsReasonAndFixLink_AndKeepsThemThroughUpdates()
    {
        await using var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(app.BaseAddress, "/dashboard").ToString(), new() { WaitUntil = WaitUntilState.NetworkIdle });
        var json = CockpitSnapshot.Json([CockpitSnapshot.Disabled(A11ySeed.RetiredId, "EMA21 [MNQ]", "retired EMA21 strategy")]);

        // Two snapshots: the first draws the card, the second runs the per-update repaint over it.
        for (var i = 0; i < 2; i++)
            await page.EvaluateAsync("""
                json => {
                    CRV.engine.status('Live');
                    document.dispatchEvent(new CustomEvent('crv:update', { detail: JSON.parse(json) }));
                }
                """, json);

        var card = page.Locator($"#card-{A11ySeed.RetiredId}");
        await card.WaitForAsync();
        Assert.Equal("DISABLED", (await card.Locator($"#status-{A11ySeed.RetiredId}").TextContentAsync())!.Trim());
        Assert.Contains("Disabled: retired EMA21 strategy", await card.InnerTextAsync());
        Assert.Equal("/setup/strategies/" + A11ySeed.RetiredId,
            await card.GetByRole(AriaRole.Link, new() { Name = "Fix in Setup" }).GetAttributeAsync("href"));
        Assert.Equal("", await card.EvaluateAsync<string>("c => c.style.opacity"));
    }
}
