using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CRV.Core.Models;

namespace CRV.Web.Services;

/// <summary>
/// Singleton that fans out EngineSnapshot to N SSE subscribers.
/// The orchestrator publishes via Publish(); SSE endpoints subscribe via SubscribeAsync().
/// </summary>
public class SnapshotBroadcastService
{
    private readonly object _lock = new();
    private readonly List<Channel<EngineSnapshot>> _subscribers = new();

    /// <summary>Last snapshot cached for new SSE connections (sent immediately on connect).</summary>
    public EngineSnapshot? LastSnapshot { get; private set; }

    /// <summary>Publish a snapshot to all active subscribers.</summary>
    public void Publish(EngineSnapshot snapshot)
    {
        LastSnapshot = snapshot;

        lock (_lock)
        {
            _subscribers.RemoveAll(ch => !ch.Writer.TryWrite(snapshot));
        }
    }

    /// <summary>
    /// Returns an async enumerable that yields snapshots as they arrive.
    /// Yields the cached last snapshot immediately if available.
    /// </summary>
    public async IAsyncEnumerable<EngineSnapshot> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<EngineSnapshot>(
            new BoundedChannelOptions(8)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        // Send cached snapshot immediately
        if (LastSnapshot is not null)
            yield return LastSnapshot;

        lock (_lock) { _subscribers.Add(channel); }

        try
        {
            await foreach (var snap in channel.Reader.ReadAllAsync(ct))
                yield return snap;
        }
        finally
        {
            lock (_lock) { _subscribers.Remove(channel); }
            channel.Writer.TryComplete();
        }
    }
}
