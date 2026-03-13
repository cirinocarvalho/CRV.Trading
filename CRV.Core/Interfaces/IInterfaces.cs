using CRV.Core.Models;

namespace CRV.Core.Interfaces;

public interface IBarFeed
{
    IAsyncEnumerable<Bar> StreamAsync(CancellationToken ct);

    /// <summary>
    /// Raised on every realtime L1 price tick (live ticks only, not historical replay bars).
    /// Subscribe in LiveEngineOrchestrator to route ticks to OrbStrategyEngine.ProcessPriceTickAsync.
    /// The event is fired from the feed's internal background task — callers must serialize
    /// access to shared state (e.g., via SemaphoreSlim).
    /// </summary>
    event Action<decimal, DateTime>? OnPriceTick;

    /// <summary>Fetch historical daily bars for seeding module levels. Returns empty if not supported.</summary>
    Task<IReadOnlyList<Bar>> FetchDailyBarsAsync(string ticker, int count, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());
}

public interface IOrderExecutor
{
    Task OnEntrySignalAsync(EntrySignal signal);
    Task OnPartialSignalAsync(PartialSignal signal);
    Task OnBESignalAsync(BESignal signal);
    Task OnExitSignalAsync(ExitSignal signal);
}

public interface IStrategyEventSink
{
    Task OnEntryAsync(EntrySignal signal);
    Task OnPartialAsync(PartialSignal signal);
    Task OnBEMoveAsync(BESignal signal);
    Task OnExitAsync(ExitSignal signal, TradeRecord completed);
    Task OnSnapshotAsync(EngineSnapshot snapshot);
}

public interface ILastPriceProvider
{
    decimal GetLastPrice(string ticker);
    void    UpdatePrice(string ticker, decimal price);
}
