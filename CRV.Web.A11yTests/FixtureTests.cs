using Xunit;
using System.Net;

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
}
