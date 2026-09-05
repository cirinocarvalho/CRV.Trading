using System.Net;
using CRV.Backtest.DataLoaders;
using CRV.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The historical loaders chunk a request by month (Schwab) or fortnight
/// (TradeStation) and used to <c>continue</c> past any chunk that failed. The run
/// then completed normally with a hole in its data and reported a result as if
/// nothing had happened — the mechanism behind two "identical" backtests landing
/// 24.6% apart. A missing chunk must stop the run.
/// </summary>
public class LoaderFailsLoudTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public int Calls { get; private set; }
        public StubHandler(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            var next = _responses.Count > 0 ? _responses.Dequeue() : () => Ok("{\"candles\":[]}");
            return Task.FromResult(next());
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public StubFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Error(HttpStatusCode code) => new(code);

    private static string OneCandle(long unixMs, decimal close) =>
        $"{{\"candles\":[{{\"datetime\":{unixMs},\"open\":{close},\"high\":{close},\"low\":{close},\"close\":{close},\"volume\":10}}]}}";

    private static async Task<List<Bar>> Drain(IAsyncEnumerable<Bar> bars)
    {
        var o = new List<Bar>();
        await foreach (var b in bars) o.Add(b);
        return o;
    }

    private static readonly DateTime From = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To   = new(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task SchwabLoaderThrowsWhenAChunkFails(HttpStatusCode code)
    {
        long ms = new DateTimeOffset(new DateTime(2026, 3, 15, 14, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var handler = new StubHandler(
            () => Ok(OneCandle(ms, 18000m)),   // March succeeds
            () => Error(code));                // April fails — must not be skipped

        var loader = new SchwabHistoricalLoader("tok", NullLogger<SchwabHistoricalLoader>.Instance,
            "https://example.test", new StubFactory(handler));

        var ex = await Assert.ThrowsAsync<BarLoadException>(
            () => Drain(loader.LoadAsync("/MNQM26", 1, From, To)));

        Assert.Contains("MNQM26", ex.Message);
        Assert.Contains(((int)code).ToString(), ex.Message);
    }

    [Fact]
    public async Task SchwabLoaderThrowsWhenTheRequestItselfFaults()
    {
        var handler = new StubHandler(() => throw new HttpRequestException("connection reset"));
        var loader  = new SchwabHistoricalLoader("tok", NullLogger<SchwabHistoricalLoader>.Instance,
            "https://example.test", new StubFactory(handler));

        var ex = await Assert.ThrowsAsync<BarLoadException>(
            () => Drain(loader.LoadAsync("/MNQM26", 1, From, To)));
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task SchwabLoaderYieldsEveryChunkWhenAllSucceed()
    {
        long mar = new DateTimeOffset(new DateTime(2026, 3, 15, 14, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long apr = new DateTimeOffset(new DateTime(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long may = new DateTimeOffset(new DateTime(2026, 5, 15, 14, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var handler = new StubHandler(
            () => Ok(OneCandle(mar, 18000m)),
            () => Ok(OneCandle(apr, 18100m)),
            () => Ok(OneCandle(may, 18200m)));

        var loader = new SchwabHistoricalLoader("tok", NullLogger<SchwabHistoricalLoader>.Instance,
            "https://example.test", new StubFactory(handler));

        var bars = await Drain(loader.LoadAsync("/MNQM26", 1, From, To));
        Assert.Equal(3, bars.Count);
        Assert.Equal(new[] { 18000m, 18100m, 18200m }, bars.Select(b => b.Close));
    }

    [Fact]
    public async Task TradeStationLoaderThrowsWhenAChunkFails()
    {
        var handler = new StubHandler(
            () => Ok("{\"Bars\":[]}"),
            () => Error(HttpStatusCode.BadGateway));

        var loader = new TradeStationHistoricalLoader("tok", NullLogger<TradeStationHistoricalLoader>.Instance,
            "https://example.test", new StubFactory(handler));

        var ex = await Assert.ThrowsAsync<BarLoadException>(
            () => Drain(loader.LoadAsync("MNQM26", 1, From, To)));
        Assert.Contains("502", ex.Message);
    }
}
