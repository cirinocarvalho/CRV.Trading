using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>Saving strategies: what a save refuses, and what it never touches.</summary>
[Collection(A11yCollection.Name)]
public class BasketSaveTests(A11yAppFixture app)
{
    private StrategyBasketService Basket => app.Services.GetRequiredService<StrategyBasketService>();
    private StrategyConfig Config => app.Services.GetRequiredService<StrategyConfigService>().Current;

    [Fact]
    public void TurningOn_AStrategyOnAnotherBarSizeOfItsRoot_IsRefused()
    {
        // The seeded MNQ strategies use the config's bar size (1 min); this NQ one asks for 15.
        var (added, id) = Basket.Add(StrategyType.Retest, "/NQZ26", 20m, 0.25m, "test");
        Assert.True(added.Ok, added.Error);
        try
        {
            Assert.True(Basket.Update(id!, e => e.ExecutionTFMinutes = 15, "test").Ok);

            var change = Basket.SetEnabled(id!, true, "test");

            Assert.False(change.Ok);
            Assert.Contains("Bar size 15 min differs", change.Error);
            Assert.Contains("every NQ / MNQ strategy shares one bar size", change.Error);
            Assert.False(Basket.Find(id!)!.Entry.Enabled);
        }
        finally { Basket.Remove(id!, "test"); }
    }

    [Fact]
    public void SavingAnEditThatBreaksTheRootsBarSize_IsRefused_AndChangesNothing()
    {
        var before = Config.BasketJson;
        var edited = BasketCodec.Parse(BasketCodec.Serialize(new[] { Basket.Find(A11ySeed.RetestId)!.Entry }))[0];
        edited.ExecutionTFMinutes = 15;

        var change = Basket.Replace(A11ySeed.RetestId, edited, "test");

        Assert.False(change.Ok);
        Assert.Contains("Bar size 15 min differs", change.Error);
        Assert.Equal(before, Config.BasketJson);
    }

    [Fact]
    public void ARetiredStrategy_CanBeSwitchedOff_AndSurvivesOtherSaves()
    {
        try
        {
            Assert.True(Basket.SetEnabled(A11ySeed.RetiredId, false, "test").Ok);
            Assert.True(Basket.SetEnabled(A11ySeed.RetestId, true, "test").Ok);   // every save rewrites both lists

            var retired = BasketCodec.Parse(Config.EmaBasketJson).Single(e => e.Id == A11ySeed.RetiredId);
            Assert.Equal(SetupValidation.RetiredEma21, retired.StrategyType);
            Assert.False(retired.Enabled);
        }
        finally
        {
            // Back to the seed: switched on, so the cockpit and setup page show it disabled.
            Basket.Update(A11ySeed.RetiredId, e => e.Enabled = true, "test");
        }
    }

    [Fact]
    public async Task StrategiesPage_UnreadableList_SaysSo_InsteadOfOfferingSetupsAToD()
    {
        A11ySeed.SetOrbBasket(app.Services, "not json {{");
        try
        {
            using var http = new HttpClient { BaseAddress = app.BaseAddress };

            var html = await http.GetStringAsync("/setup/strategies");

            Assert.Contains("opening-range strategy list", html);
            Assert.DoesNotContain("older A–D setups", html);
        }
        finally
        {
            A11ySeed.SetOrbBasket(app.Services, A11ySeed.OrbBasketJson);
        }
    }
}
