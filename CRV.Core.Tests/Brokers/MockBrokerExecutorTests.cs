using CRV.Core.Models;
using CRV.Live.Brokers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class MockBrokerExecutorTests
{
    static MockBrokerExecutor Build() =>
        new MockBrokerExecutor(NullLogger<MockBrokerExecutor>.Instance);

    [Fact]
    public async Task OnEntry_CreatesOcoOrders_AllWorking()
    {
        var exec = Build();
        var sig  = new EntrySignal(SetupId.A, Direction.Long, 20500m, 20400m, 21500m, 20900m, 2, DateTime.UtcNow);
        await exec.OnEntrySignalAsync(sig);

        var orders = exec.GetOrders();
        // Entry leg is FILLED immediately; stop + target are WORKING
        Assert.Equal(3, orders.Count);
        Assert.Single(orders, o => o.Status == "FILLED");
        Assert.Equal(2, orders.Count(o => o.Status == "WORKING"));
    }

    [Fact]
    public void EvaluateFills_BuyStopFills_CancelsOcoPartner()
    {
        var exec = Build();
        exec.SimulateOrder("NQH26", "BUY", 2, null, 20500m, "oco1");
        exec.SimulateOrder("NQH26", "SELL", 2, 21000m, null, "oco1");

        exec.EvaluateFills("NQH26", 20501m, DateTime.UtcNow);

        var orders   = exec.GetOrders();
        var filled   = orders.Single(o => o.StopPrice == 20500m);
        var canceled = orders.Single(o => o.LimitPrice == 21000m);
        Assert.Equal("FILLED",   filled.Status);
        Assert.Equal("CANCELED", canceled.Status);
    }

    [Fact]
    public void EvaluateFills_SellLimitFills_CancelsOcoPartner()
    {
        var exec = Build();
        exec.SimulateOrder("NQH26", "SELL", 2, 21000m, null, "oco1");
        exec.SimulateOrder("NQH26", "SELL", 2, null, 20400m, "oco1");

        exec.EvaluateFills("NQH26", 21001m, DateTime.UtcNow);

        var orders = exec.GetOrders();
        Assert.Equal("FILLED",   orders[0].Status);
        Assert.Equal("CANCELED", orders[1].Status);
    }

    [Fact]
    public async Task CancelOrder_SetsStatusCanceled()
    {
        var exec = Build();
        var sig = new EntrySignal(SetupId.A, Direction.Long, 20500m, 20400m, 21500m, 20900m, 2, DateTime.UtcNow);
        await exec.OnEntrySignalAsync(sig);

        var workingId = exec.GetOrders().First(o => o.Status == "WORKING").OrderId;
        exec.CancelOrder(workingId);
        Assert.Equal("CANCELED", exec.GetOrders().Single(o => o.OrderId == workingId).Status);
    }

    [Fact]
    public async Task OnBESignal_MovesCorrectSetupStopPrice()
    {
        var exec = Build();

        // Setup A entry
        var sigA = new EntrySignal(SetupId.A, Direction.Long, 20500m, 20400m, 21500m, 20900m, 2, DateTime.UtcNow);
        await exec.OnEntrySignalAsync(sigA);

        // Setup B entry
        var sigB = new EntrySignal(SetupId.B, Direction.Long, 20950m, 20850m, 21200m, 21100m, 1, DateTime.UtcNow);
        await exec.OnEntrySignalAsync(sigB);

        // BE move on Setup A — stop should move to 20500 (entry price)
        var beSig = new BESignal(SetupId.A, Direction.Long, 20500m, 20500m, 2, DateTime.UtcNow);
        await exec.OnBESignalAsync(beSig);

        var orders   = exec.GetOrders();
        var stopA    = orders.Single(o => o.Symbol == "A" && o.Status == "WORKING" && o.StopPrice.HasValue);
        var stopB    = orders.Single(o => o.Symbol == "B" && o.Status == "WORKING" && o.StopPrice.HasValue);

        Assert.Equal(20500m, stopA.StopPrice); // Setup A stop moved to BE
        Assert.Equal(20850m, stopB.StopPrice); // Setup B stop unchanged
    }
}
