using System.Text.RegularExpressions;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>Posts a browser form can't produce, sent with a valid antiforgery token.</summary>
[Collection(A11yCollection.Name)]
public class TamperedPostTests(A11yAppFixture app)
{
    private StrategyBasketService Basket => app.Services.GetRequiredService<StrategyBasketService>();
    private StrategyConfig Config => app.Services.GetRequiredService<StrategyConfigService>().Current;

    private static async Task<string> TokenFrom(HttpClient http, string page)
    {
        var html = await http.GetStringAsync(page);
        return Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    }

    private HttpClient NewClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = app.BaseAddress };

    [Fact]
    public async Task ReadOnlySetupPage_PostThatChangesRetiredTypeToTradable_ChangesNothing()
    {
        var before = Config.EmaBasketJson;
        using var http = NewClient();
        var token = await TokenFrom(http, "/setup/strategies/" + A11ySeed.RetiredId);

        var res = await http.PostAsync($"/setup/strategies/{A11ySeed.RetiredId}?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Entry.StrategyType"] = ((int)StrategyType.Pullback).ToString(),
            ["Entry.Enabled"] = "true",
        }));

        Assert.True((int)res.StatusCode is >= 200 and < 400, res.StatusCode.ToString());
        Assert.Equal(before, Config.EmaBasketJson);
        var stored = BasketCodec.Parse(Config.EmaBasketJson).Single(e => e.Id == A11ySeed.RetiredId);
        Assert.Equal(SetupValidation.RetiredEma21, stored.StrategyType);
        Assert.True(stored.Enabled);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(99)]
    public async Task StrategiesPage_AddOfATypeThatCantBeEdited_CreatesNothing(int type)
    {
        var before = (Config.BasketJson, Config.EmaBasketJson);
        using var http = NewClient();
        var token = await TokenFrom(http, "/setup/strategies");

        await http.PostAsync("/setup/strategies?handler=Add", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["type"] = type.ToString(),
            ["instrument"] = "MNQ",
        }));

        Assert.Equal(before.Item1, Config.BasketJson);
        Assert.Equal(before.Item2, Config.EmaBasketJson);
    }
}
