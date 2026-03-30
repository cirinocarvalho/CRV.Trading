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

    /// <summary>
    /// Called after fill price adjustment or partial fill changes stop/target levels or quantity.
    /// Broker should cancel + replace the existing bracket legs with updated price/qty.
    /// </summary>
    Task OnLevelsAdjustedAsync(string setupId, decimal newStop, decimal newTarget, int contracts) => Task.CompletedTask;
}

/// <summary>
/// New order executor that returns GroupOrder and supports modify/cancel.
/// Replaces IOrderExecutor once strategy simplification is complete.
/// </summary>
public interface IGroupOrderExecutor
{
    Task<GroupOrder?> OnEntrySignalAsync(EntrySignal signal);
    Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty);
    Task CancelOrderAsync(string orderId);
    Task PlaceMarketCloseAsync(string ticker, Direction direction, int qty);

    /// <summary>
    /// Poll broker REST API for current status of all legs in a group.
    /// Returns OrderEvents for any legs whose status changed.
    /// Used as a reliable alternative to WSS for order state sync.
    /// </summary>
    Task<List<OrderEvent>> PollOrderStatusesAsync(GroupOrder group) => Task.FromResult(new List<OrderEvent>());

    /// <summary>
    /// Fetch the fill price for a specific order via REST API.
    /// Used as fallback when WSS events don't include avgFillPrice.
    /// </summary>
    Task<decimal?> GetOrderFillPriceAsync(string orderId) => Task.FromResult<decimal?>(null);

    /// <summary>
    /// Fetch order IDs for bracket legs linked to a strategy ID.
    /// Returns dict of LegType label → orderId (e.g. "Entry" → "439010903012").
    /// </summary>
    Task<Dictionary<string, long>> GetStrategyOrderIdsAsync(long strategyId)
        => Task.FromResult(new Dictionary<string, long>());

    /// <summary>
    /// Recover an orphaned broker strategy by re-discovering bracket legs via REST.
    /// Creates a fully populated GroupOrder from the broker's current state.
    /// </summary>
    Task<GroupOrder?> RecoverStrategyAsync(long strategyId, string ticker, Direction direction,
        int totalContracts, int partialContracts, bool useBe, string setupId)
        => Task.FromResult<GroupOrder?>(null);
}

public interface IStrategyEventSink
{
    Task OnEntryAsync(EntrySignal signal);
    Task OnExitAsync(TradeRecord completed);
    Task OnSnapshotAsync(EngineSnapshot snapshot);
}

public interface ILastPriceProvider
{
    decimal GetLastPrice(string ticker);
    void    UpdatePrice(string ticker, decimal price);
}
