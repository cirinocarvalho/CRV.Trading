namespace CRV.Core.Tests.Brokers;

using System.Net;
using CRV.Live.Brokers.Tradovate;
using Xunit;

/// <summary>
/// Tradovate returns an "apiHosts" object on login. Organizations on dedicated
/// infrastructure get their own demo host; a client that keeps using the shared
/// demo host is answered with 307 (REST) or 421 (WebSocket).
/// </summary>
public class TradovateApiHostsTests : IDisposable
{
    private readonly string _tokenFile = Path.Combine(Path.GetTempPath(), $"tv-hosts-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tokenFile)) File.Delete(_tokenFile);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public List<Uri> Requests { get; } = new();
        public StubHandler(params string[] bodies) => _bodies = new(bodies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(req.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(_bodies.Dequeue()) });
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public StubFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private static string Login(string? apiHosts, int expiresInMinutes = 90) =>
        $"{{\"accessToken\":\"tok\",\"mdAccessToken\":\"md\"," +
        $"\"expirationTime\":\"{DateTime.UtcNow.AddMinutes(expiresInMinutes):o}\"" +
        (apiHosts is null ? "" : $",\"apiHosts\":{apiHosts}") + "}";

    private const string OrgHosts =
        "{\"live\":\"live.tradovateapi.com\",\"demo\":\"acme-demo.example.com\"," +
        "\"mdLive\":\"md.tradovateapi.com\",\"mdDemo\":\"acme-md-demo.example.com\"," +
        "\"replay\":\"acme-replay.example.com\",\"reportingDemo\":\"r.example.com\",\"futureHost\":\"x.example.com\"}";

    private TradovateAuthService Demo(StubHandler h, string mdWssUrl = "wss://md-demo.tradovateapi.com/v1/websocket") =>
        new("u", "p", 1, "s", "d", "app", _tokenFile,
            apiBaseUrl: "https://demo.tradovateapi.com/v1",
            mdWssUrl: mdWssUrl,
            httpFactory: new StubFactory(h));

    [Fact]
    public async Task Login_SwitchesToReturnedDemoHosts()
    {
        var auth = Demo(new StubHandler(Login(OrgHosts)));

        await auth.AuthenticateAsync();

        Assert.Equal("https://acme-demo.example.com/v1", auth.ApiBaseUrl);
        Assert.Equal("wss://acme-md-demo.example.com/v1/websocket", auth.MdWssUrl);
    }

    [Fact]
    public async Task LiveConfig_UsesLiveEntries()
    {
        var h = new StubHandler(Login(
            "{\"live\":\"acme-live.example.com\",\"demo\":\"acme-demo.example.com\"," +
            "\"mdLive\":\"acme-md.example.com\",\"mdDemo\":\"acme-md-demo.example.com\"}"));
        var auth = new TradovateAuthService("u", "p", 1, "s", "d", "app", _tokenFile,
            apiBaseUrl: "https://live.tradovateapi.com/v1",
            mdWssUrl: "wss://md.tradovateapi.com/v1/websocket",
            httpFactory: new StubFactory(h));

        await auth.AuthenticateAsync();

        Assert.Equal("https://acme-live.example.com/v1", auth.ApiBaseUrl);
        Assert.Equal("wss://acme-md.example.com/v1/websocket", auth.MdWssUrl);
    }

    [Fact]
    public async Task ReplayMarketData_UsesReplayEntry()
    {
        var auth = Demo(new StubHandler(Login(OrgHosts)), "wss://replay.tradovateapi.com/v1/websocket");

        await auth.AuthenticateAsync();

        Assert.Equal("https://acme-demo.example.com/v1", auth.ApiBaseUrl);
        Assert.Equal("wss://acme-replay.example.com/v1/websocket", auth.MdWssUrl);
    }

    [Fact]
    public async Task ResponseWithoutApiHosts_KeepsConfiguredHosts()
    {
        var auth = Demo(new StubHandler(Login(null)));

        await auth.AuthenticateAsync();

        Assert.Equal("https://demo.tradovateapi.com/v1", auth.ApiBaseUrl);
        Assert.Equal("wss://md-demo.tradovateapi.com/v1/websocket", auth.MdWssUrl);
    }

    [Fact]
    public async Task MalformedHost_IsIgnored()
    {
        var auth = Demo(new StubHandler(Login("{\"demo\":\"https://bad/host\",\"mdDemo\":\"\"}")));

        await auth.AuthenticateAsync();

        Assert.Equal("https://demo.tradovateapi.com/v1", auth.ApiBaseUrl);
        Assert.Equal("wss://md-demo.tradovateapi.com/v1/websocket", auth.MdWssUrl);
    }

    [Fact]
    public async Task Login_AlwaysGoesToConfiguredHost_RenewGoesToResolvedHost()
    {
        // Second login response moves the org back to the shared host.
        var h = new StubHandler(
            Login(OrgHosts, expiresInMinutes: 3),
            Login("{\"demo\":\"acme2-demo.example.com\"}"),
            Login("{\"demo\":\"demo.tradovateapi.com\"}"));
        var auth = Demo(h);

        await auth.AuthenticateAsync();
        await auth.GetAccessTokenAsync();   // < 5 min left → renew
        await auth.AuthenticateAsync();

        Assert.Equal("demo.tradovateapi.com", h.Requests[0].Host);
        Assert.EndsWith("/auth/accesstokenrequest", h.Requests[0].AbsolutePath);
        Assert.Equal("acme-demo.example.com", h.Requests[1].Host);
        Assert.EndsWith("/auth/renewAccessToken", h.Requests[1].AbsolutePath);
        Assert.Equal("demo.tradovateapi.com", h.Requests[2].Host);
        Assert.Equal("https://demo.tradovateapi.com/v1", auth.ApiBaseUrl);
    }

    [Fact]
    public async Task Restart_ReusesPersistedHostsWithPersistedToken()
    {
        await Demo(new StubHandler(Login(OrgHosts))).AuthenticateAsync();

        var restarted = Demo(new StubHandler());

        Assert.True(restarted.IsAuthenticated);
        Assert.Equal("https://acme-demo.example.com/v1", restarted.ApiBaseUrl);
        Assert.Equal("wss://acme-md-demo.example.com/v1/websocket", restarted.MdWssUrl);
    }
}
