using System.Net;
using CRV.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CRV.Web.A11yTests;

[Collection(A11yCollection.Name)]
public class FixtureTests(A11yAppFixture app)
{
    [Fact]
    public async Task Dashboard_ServesOk_FromEmptyTempDataDir()
    {
        using var http = new HttpClient { BaseAddress = app.BaseAddress };

        var res = await http.GetAsync("/dashboard");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(File.Exists(Path.Combine(app.DataDir, "crv_trading.db")),
            "The app should create its SQLite DB in the temp DATA_DIR");
        Assert.False(File.Exists(Path.Combine(app.DataDir, "schwab_tokens.json")),
            "The temp DATA_DIR must never contain broker tokens");
    }

    [Fact]
    public void Seed_UsesMockBroker()
    {
        var cfg = app.Services.GetRequiredService<StrategyConfigService>().Current;

        Assert.Equal("Mock", cfg.Broker);
    }

    [Theory]
    [InlineData("/setup/strategies/" + A11ySeed.RetestId)]
    [InlineData("/setup/strategies/" + A11ySeed.Ema21Id)]
    public async Task SeededStrategyPage_ServesOk(string route)
    {
        using var http = new HttpClient { BaseAddress = app.BaseAddress };

        var res = await http.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
