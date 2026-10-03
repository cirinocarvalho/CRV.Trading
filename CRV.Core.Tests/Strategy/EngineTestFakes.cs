using CRV.Core.Interfaces;
using CRV.Core.Models;

namespace CRV.Core.Tests.Strategy;

/// <summary>Order executor that places nothing.</summary>
internal sealed class NoopExec : IOrderExecutor
{
    public Task<decimal?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<decimal?>(null);
}

/// <summary>Event sink that discards every event.</summary>
internal sealed class NullSink : IStrategyEventSink
{
    public Task OnEntryAsync(EntrySignal s) => Task.CompletedTask;
    public Task OnExitAsync(TradeRecord t) => Task.CompletedTask;
    public Task OnSnapshotAsync(EngineSnapshot s) => Task.CompletedTask;
}

/// <summary>Last-price provider that returns one fixed price for every ticker.</summary>
internal sealed class FixedPrices(decimal price = 0m) : ILastPriceProvider
{
    public decimal GetLastPrice(string t) => price;
    public void UpdatePrice(string t, decimal p) { }
}

/// <summary>Group-order executor that reports itself simulated and places nothing.</summary>
internal sealed class SimulatedGroupExec : IGroupOrderExecutor
{
    public bool IsSimulated => true;
    public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal s) => Task.FromResult<GroupOrder?>(null);
    public Task ModifyOrderAsync(string id, decimal? p, int? q) => Task.CompletedTask;
    public Task CancelOrderAsync(string id) => Task.CompletedTask;
    public Task<decimal> PlaceMarketCloseAsync(string t, Direction d, int q) => Task.FromResult(0m);
}
