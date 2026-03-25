namespace CRV.Core.Interfaces;

using CRV.Core.Models;

/// <summary>
/// Unified event stream for real-time order status updates.
/// Implemented by TradovateEventStream (WSS) and MockEventStream (Channel).
/// Lifecycle: connects/disconnects with the engine.
/// </summary>
public interface IBrokerEventStream
{
    event Action<OrderEvent>? OnOrderUpdate;
    event Action? OnDisconnected;
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
}
