namespace CRV.Core.Tests.Brokers;

using CRV.Core.Models;
using CRV.Live.Brokers;
using Xunit;

public class MockEventStreamTests
{
    [Fact]
    public async Task PushEvent_RaisesOnOrderUpdate()
    {
        var stream = new MockEventStream();
        var received = new List<OrderEvent>();
        stream.OnOrderUpdate += e => received.Add(e);

        await stream.ConnectAsync(CancellationToken.None);
        Assert.True(stream.IsConnected);

        stream.PushEvent(new OrderEvent(
            "g1", "o1", LegType.Entry, OrderLegStatus.Filled,
            100m, 1, null, null, DateTime.UtcNow));

        // Give background loop time to deliver
        await Task.Delay(100);

        Assert.Single(received);
        Assert.Equal("g1", received[0].GroupOrderId);
        Assert.Equal(OrderLegStatus.Filled, received[0].Status);

        await stream.DisconnectAsync();
    }

    [Fact]
    public async Task Disconnect_StopsDelivery()
    {
        var stream = new MockEventStream();
        var count = 0;
        stream.OnOrderUpdate += _ => Interlocked.Increment(ref count);

        await stream.ConnectAsync(CancellationToken.None);
        await stream.DisconnectAsync();

        Assert.False(stream.IsConnected);

        stream.PushEvent(new OrderEvent(
            "g1", "o1", LegType.Entry, OrderLegStatus.Filled,
            100m, 1, null, null, DateTime.UtcNow));

        await Task.Delay(100);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task MultipleEvents_DeliveredInOrder()
    {
        var stream = new MockEventStream();
        var received = new List<string>();
        stream.OnOrderUpdate += e => received.Add(e.OrderId);

        await stream.ConnectAsync(CancellationToken.None);

        for (int i = 0; i < 5; i++)
        {
            stream.PushEvent(new OrderEvent(
                "g1", $"o{i}", LegType.Entry, OrderLegStatus.Working,
                null, null, null, null, DateTime.UtcNow));
        }

        await Task.Delay(200);

        Assert.Equal(5, received.Count);
        for (int i = 0; i < 5; i++)
            Assert.Equal($"o{i}", received[i]);

        await stream.DisconnectAsync();
    }
}
