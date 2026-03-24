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

/// <summary>
/// Multiplexed bar feed that streams bars from multiple tickers.
/// Each bar is paired with its ticker symbol.
/// </summary>
public interface IMultiTickerBarFeed : IAsyncDisposable
{
    IAsyncEnumerable<(Bar bar, string ticker)> StreamAsync(CancellationToken ct);
    event Action<decimal, DateTime, string>? OnPriceTick;
    Task<IReadOnlyList<Bar>> FetchDailyBarsAsync(string ticker, int count, CancellationToken ct);
}

public interface IOrderExecutor
{
    /// <summary>
    /// Place an entry order. Returns the actual broker fill price, or null if unavailable.
    /// When null, the engine keeps the theoretical entry price.
    /// </summary>
    Task<decimal?> OnEntrySignalAsync(EntrySignal signal);
    Task OnPartialSignalAsync(PartialSignal signal);
    Task OnBESignalAsync(BESignal signal);
    Task OnExitSignalAsync(ExitSignal signal);

    /// <summary>
    /// Called after fill price adjustment or partial fill changes stop/target levels or quantity.
    /// Broker should cancel + replace the existing bracket legs with updated price/qty.
    /// </summary>
    Task OnLevelsAdjustedAsync(string setupId, decimal newStop, decimal newTarget, int contracts) => Task.CompletedTask;
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
