using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CRV.Core.Interfaces;
using CRV.Core.Models;

namespace CRV.Live.BarBuilders;

/// <summary>
/// Multiplexes multiple single-ticker <see cref="IBarFeed"/> instances into a single
/// <see cref="IMultiTickerBarFeed"/> stream. Each bar is stamped with its ticker symbol.
/// </summary>
public sealed class MultiTickerFeedMux : IMultiTickerBarFeed
{
    private readonly Dictionary<string, IBarFeed> _feeds;
    private readonly Channel<(Bar bar, string ticker)> _channel;

    public event Action<decimal, DateTime, string>? OnPriceTick;

    public MultiTickerFeedMux(Dictionary<string, IBarFeed> feeds)
    {
        _feeds = feeds;
        _channel = Channel.CreateUnbounded<(Bar, string)>(
            new UnboundedChannelOptions { SingleReader = true });
    }

    public async IAsyncEnumerable<(Bar bar, string ticker)> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Wire tick forwarding and spawn a reader task per feed
        var tasks = new List<Task>(_feeds.Count);
        foreach (var (ticker, feed) in _feeds)
        {
            var t = ticker; // capture for closure
            feed.OnPriceTick += (price, time) => OnPriceTick?.Invoke(price, time, t);

            tasks.Add(Task.Run(async () =>
            {
                await foreach (var bar in feed.StreamAsync(ct).WithCancellation(ct))
                {
                    var stamped = bar with { Ticker = t };
                    await _channel.Writer.WriteAsync((stamped, t), ct);
                }
            }, ct));
        }

        // When all feeds complete (or cancel), mark channel complete
        _ = Task.WhenAll(tasks).ContinueWith(_ => _channel.Writer.TryComplete(), ct);

        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }
    }

    public Task<IReadOnlyList<Bar>> FetchDailyBarsAsync(string ticker, int count, CancellationToken ct)
    {
        if (_feeds.TryGetValue(ticker, out var feed))
            return feed.FetchDailyBarsAsync(ticker, count, ct);
        return Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var feed in _feeds.Values)
            if (feed is IAsyncDisposable d)
                await d.DisposeAsync();
    }

    // ── Single-ticker fast path ──────────────────────────────────────

    /// <summary>
    /// Wraps a single <see cref="IBarFeed"/> as an <see cref="IMultiTickerBarFeed"/>
    /// without channel overhead.
    /// </summary>
    public static IMultiTickerBarFeed Single(string ticker, IBarFeed feed)
        => new SingleTickerAdapter(ticker, feed);

    private sealed class SingleTickerAdapter : IMultiTickerBarFeed
    {
        private readonly string _ticker;
        private readonly IBarFeed _feed;

        public event Action<decimal, DateTime, string>? OnPriceTick;

        public SingleTickerAdapter(string ticker, IBarFeed feed)
        {
            _ticker = ticker;
            _feed = feed;
            _feed.OnPriceTick += (price, time) => OnPriceTick?.Invoke(price, time, _ticker);
        }

        public async IAsyncEnumerable<(Bar bar, string ticker)> StreamAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var bar in _feed.StreamAsync(ct).WithCancellation(ct))
            {
                yield return (bar with { Ticker = _ticker }, _ticker);
            }
        }

        public Task<IReadOnlyList<Bar>> FetchDailyBarsAsync(string ticker, int count, CancellationToken ct)
            => _feed.FetchDailyBarsAsync(ticker, count, ct);

        public async ValueTask DisposeAsync()
        {
            if (_feed is IAsyncDisposable d) await d.DisposeAsync();
        }
    }
}
