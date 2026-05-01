using System.Runtime.CompilerServices;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Live.BarBuilders;
using Xunit;

namespace CRV.Core.Tests.BarBuilders;

public class MultiTickerFeedMuxTests
{
    // ── Helpers ────────────────────────────────────────────────────────

    private sealed class FakeBarFeed : IBarFeed, IAsyncDisposable
    {
        private readonly List<Bar> _bars;
        public bool Disposed { get; private set; }
        public event Action<decimal, DateTime>? OnPriceTick;
        /// <summary>True once the mux has subscribed to OnPriceTick — lets tests poll
        /// for wiring deterministically instead of sleeping a fixed delay.</summary>
        public bool HasTickSubscriber => OnPriceTick != null;

        public FakeBarFeed(params Bar[] bars) => _bars = bars.ToList();

        public async IAsyncEnumerable<Bar> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var bar in _bars)
            {
                await Task.Yield();
                yield return bar;
            }
        }

        public Task<IReadOnlyList<Bar>> FetchDailyBarsAsync(string ticker, int count, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<Bar>>(
                _bars.Take(count).ToList().AsReadOnly());
        }

        public void RaiseTick(decimal price, DateTime time) => OnPriceTick?.Invoke(price, time);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static Bar MakeBar(string ticker = "", int minute = 0) =>
        new(new DateTime(2026, 3, 20, 9, minute, 0, DateTimeKind.Utc),
            100m, 101m, 99m, 100.5m, 1000, true, ticker);

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SingleTickerAdapter_StampsTicker()
    {
        var feed = new FakeBarFeed(MakeBar(minute: 0), MakeBar(minute: 1));
        await using var mux = MultiTickerFeedMux.Single("ES", feed);

        var bars = new List<(Bar bar, string ticker)>();
        await foreach (var item in mux.StreamAsync(CancellationToken.None))
            bars.Add(item);

        Assert.Equal(2, bars.Count);
        Assert.All(bars, b =>
        {
            Assert.Equal("ES", b.ticker);
            Assert.Equal("ES", b.bar.Ticker);
        });
    }

    [Fact]
    public async Task MultiTicker_InterleavesBarsFromTwoFeeds()
    {
        var feedA = new FakeBarFeed(MakeBar(minute: 0), MakeBar(minute: 1));
        var feedB = new FakeBarFeed(MakeBar(minute: 2));

        var feeds = new Dictionary<string, IBarFeed>
        {
            ["ES"] = feedA,
            ["NQ"] = feedB
        };

        await using var mux = new MultiTickerFeedMux(feeds);

        var results = new List<(Bar bar, string ticker)>();
        await foreach (var item in mux.StreamAsync(CancellationToken.None))
            results.Add(item);

        Assert.Equal(3, results.Count);
        Assert.Equal(2, results.Count(r => r.ticker == "ES"));
        Assert.Equal(1, results.Count(r => r.ticker == "NQ"));
        Assert.All(results.Where(r => r.ticker == "ES"), r => Assert.Equal("ES", r.bar.Ticker));
        Assert.All(results.Where(r => r.ticker == "NQ"), r => Assert.Equal("NQ", r.bar.Ticker));
    }

    [Fact]
    public async Task TickEvents_IncludeCorrectTicker()
    {
        var feedA = new FakeBarFeed();
        var feedB = new FakeBarFeed();

        var feeds = new Dictionary<string, IBarFeed>
        {
            ["ES"] = feedA,
            ["NQ"] = feedB
        };

        await using var mux = new MultiTickerFeedMux(feeds);

        var ticks = new List<(decimal price, DateTime time, string ticker)>();
        mux.OnPriceTick += (p, t, tk) => ticks.Add((p, t, tk));

        // Start streaming to wire up event handlers, then cancel immediately
        using var cts = new CancellationTokenSource();
        var streamTask = Task.Run(async () =>
        {
            await foreach (var _ in mux.StreamAsync(cts.Token)) { }
        });

        // Wait deterministically for the mux to wire OnPriceTick on both feeds —
        // a fixed Task.Delay races on slow CI agents.
        Assert.True(
            SpinWait.SpinUntil(() => feedA.HasTickSubscriber && feedB.HasTickSubscriber, 5000),
            "Mux did not wire OnPriceTick subscribers within 5s");

        var now = DateTime.UtcNow;
        feedA.RaiseTick(5000m, now);
        feedB.RaiseTick(17000m, now);

        Assert.Equal(2, ticks.Count);
        Assert.Contains(ticks, t => t.ticker == "ES" && t.price == 5000m);
        Assert.Contains(ticks, t => t.ticker == "NQ" && t.price == 17000m);

        cts.Cancel();
        try { await streamTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SingleTickerAdapter_TickEvents_IncludeCorrectTicker()
    {
        var feed = new FakeBarFeed();
        await using var mux = MultiTickerFeedMux.Single("ES", feed);

        var ticks = new List<(decimal price, DateTime time, string ticker)>();
        mux.OnPriceTick += (p, t, tk) => ticks.Add((p, t, tk));

        var now = DateTime.UtcNow;
        feed.RaiseTick(5000m, now);

        Assert.Single(ticks);
        Assert.Equal("ES", ticks[0].ticker);
        Assert.Equal(5000m, ticks[0].price);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_DelegatesToCorrectFeed()
    {
        var feedA = new FakeBarFeed(MakeBar(minute: 0), MakeBar(minute: 1));
        var feedB = new FakeBarFeed(MakeBar(minute: 2));

        var feeds = new Dictionary<string, IBarFeed>
        {
            ["ES"] = feedA,
            ["NQ"] = feedB
        };

        await using var mux = new MultiTickerFeedMux(feeds);

        var esDaily = await mux.FetchDailyBarsAsync("ES", 5, CancellationToken.None);
        Assert.Equal(2, esDaily.Count); // feedA has 2 bars

        var nqDaily = await mux.FetchDailyBarsAsync("NQ", 5, CancellationToken.None);
        Assert.Single(nqDaily);

        var unknown = await mux.FetchDailyBarsAsync("YM", 5, CancellationToken.None);
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task Dispose_DisposesAllFeeds()
    {
        var feedA = new FakeBarFeed();
        var feedB = new FakeBarFeed();

        var feeds = new Dictionary<string, IBarFeed>
        {
            ["ES"] = feedA,
            ["NQ"] = feedB
        };

        var mux = new MultiTickerFeedMux(feeds);
        await mux.DisposeAsync();

        Assert.True(feedA.Disposed);
        Assert.True(feedB.Disposed);
    }

    [Fact]
    public async Task SingleTickerAdapter_Dispose_DisposesFeed()
    {
        var feed = new FakeBarFeed();
        var mux = MultiTickerFeedMux.Single("ES", feed);
        await mux.DisposeAsync();

        Assert.True(feed.Disposed);
    }
}
