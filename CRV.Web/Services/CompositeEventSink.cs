using CRV.Core.Interfaces;
using CRV.Core.Models;

namespace CRV.Web.Services;

/// <summary>
/// Dispatches strategy events to multiple sinks (SignalR + Email).
/// </summary>
public class CompositeEventSink : IStrategyEventSink
{
    private readonly IStrategyEventSink[] _sinks;

    public CompositeEventSink(IEnumerable<IStrategyEventSink> sinks)
    {
        _sinks = sinks.ToArray();
    }

    public async Task OnEntryAsync(EntrySignal signal)
    {
        foreach (var sink in _sinks)
            await sink.OnEntryAsync(signal);
    }

    public async Task OnExitAsync(TradeRecord completed)
    {
        foreach (var sink in _sinks)
            await sink.OnExitAsync(completed);
    }

    public async Task OnSnapshotAsync(EngineSnapshot snapshot)
    {
        foreach (var sink in _sinks)
            await sink.OnSnapshotAsync(snapshot);
    }
}
