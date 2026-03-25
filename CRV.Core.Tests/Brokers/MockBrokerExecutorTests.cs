using CRV.Core.Models;
using CRV.Live.Brokers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class MockBrokerExecutorTests
{
    static MockBrokerExecutor Build()
    {
        // Provide a real scope factory backed by an empty ServiceCollection.
        // DB persist calls will fail silently (no TradingDbContext registered),
        // which is fine for unit tests that only validate in-memory behavior.
        var sp = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new MockBrokerExecutor(NullLogger<MockBrokerExecutor>.Instance, scopeFactory);
    }

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

        exec.EvaluateFills(20501m, DateTime.UtcNow);

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

        exec.EvaluateFills(21001m, DateTime.UtcNow);

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
        await exec.CancelOrderAsync(workingId);
        Assert.Equal("CANCELED", exec.GetOrders().Single(o => o.OrderId == workingId).Status);
    }

    // OnBESignal test removed — BESignal record deleted in phase2 simplification.
}
