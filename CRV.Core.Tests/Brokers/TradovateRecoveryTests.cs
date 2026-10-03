namespace CRV.Core.Tests.Brokers;

using System.Net;
using CRV.Core.Models;
using CRV.Live.Brokers.Tradovate;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Restart recovery against a stubbed Tradovate REST API (no broker connection, no orders).
/// A failed lookup must not read as "the strategy has no legs", or the caller marks a
/// live position completed and stops tracking it.
/// </summary>
public class TradovateRecoveryTests : IDisposable
{
    private readonly string _tokenFile = Path.Combine(Path.GetTempPath(), $"tv-recover-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tokenFile)) File.Delete(_tokenFile);
    }

    /// <summary>Answers the login; every other request fails with a server error.</summary>
    private sealed class FailingRestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/accesstokenrequest"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"accessToken\":\"tok\",\"mdAccessToken\":\"md\",\"expirationTime\":\"{DateTime.UtcNow.AddMinutes(90):o}\"}}"),
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("{\"errorText\":\"unavailable\"}") });
        }
    }

    private sealed class StubFactory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    [Fact]
    public async Task RecoverStrategyAsync_RestRequestFails_Throws()
    {
        var factory  = new StubFactory(new FailingRestHandler());
        var auth     = new TradovateAuthService("u", "p", 1, "s", "d", "app", _tokenFile,
            apiBaseUrl: "https://tv.example.com/v1", mdWssUrl: "wss://md.example.com/v1/websocket",
            httpFactory: factory);
        var executor = new TradovateExecutor(auth, new StrategyConfig(), NullLogger<TradovateExecutor>.Instance,
            httpFactory: factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.RecoverStrategyAsync(4242, "MNQZ26", Direction.Long, 1, 0, false, "retest-mnq"));
    }
}
