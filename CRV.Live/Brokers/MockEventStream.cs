namespace CRV.Live.Brokers;

using System.Threading.Channels;
using CRV.Core.Interfaces;
using CRV.Core.Models;

/// <summary>
/// Mock implementation of IBrokerEventStream backed by an unbounded Channel.
/// MockGroupOrderExecutor pushes events onto the channel; a background loop
/// delivers them to OnOrderUpdate subscribers.
/// </summary>
public class MockEventStream : IBrokerEventStream
{
    private readonly Channel<OrderEvent> _channel = Channel.CreateUnbounded<OrderEvent>();
    private CancellationTokenSource? _cts;
    private Task? _consumerTask;

    public event Action<OrderEvent>? OnOrderUpdate;
    public event Action? OnDisconnected;
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsConnected = true;
        _consumerTask = ConsumeLoop(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        _cts?.Cancel();
        if (_consumerTask != null)
        {
            try { await _consumerTask; }
            catch (OperationCanceledException) { }
        }
        OnDisconnected?.Invoke();
    }

    /// <summary>Push an event onto the channel (called by MockGroupOrderExecutor).</summary>
    public void PushEvent(OrderEvent evt)
    {
        _channel.Writer.TryWrite(evt);
    }

    private async Task ConsumeLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            {
                OnOrderUpdate?.Invoke(evt);
            }
        }
        catch (OperationCanceledException) { }
    }
}
