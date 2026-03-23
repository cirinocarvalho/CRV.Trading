using System.Threading.Channels;
using CRV.Core.Data;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CRV.Web.Services;

/// <summary>
/// IStrategyEventSink implementation for live trading.
/// Pushes all engine events to the dashboard via SignalR
/// and persists completed trades to SQLite via a durable background queue.
/// </summary>
public class SignalREventSink : IStrategyEventSink, IDisposable
{
    private readonly IHubContext<TradingHub> _hub;
    private readonly IServiceScopeFactory    _scopeFactory;
    private readonly ILogger                 _log;

    // Durable write queue — bounded to prevent memory leak, drops never happen
    // because trades arrive at human speed (max a few per minute).
    private readonly Channel<TradeRecord> _tradeQueue =
        Channel.CreateBounded<TradeRecord>(new BoundedChannelOptions(256)
        {
            FullMode      = BoundedChannelFullMode.Wait,
            SingleReader  = true,
            SingleWriter  = false
        });

    private readonly Task _consumerTask;

    public SignalREventSink(
        IHubContext<TradingHub> hub,
        IServiceScopeFactory scopeFactory,
        ILogger<SignalREventSink> log)
    {
        _hub          = hub;
        _scopeFactory = scopeFactory;
        _log          = log;
        _consumerTask = Task.Run(ConsumeTradesAsync);
    }

    /// <summary>
    /// Background consumer — drains the trade queue and persists to DB with retry.
    /// </summary>
    private async Task ConsumeTradesAsync()
    {
        await foreach (var trade in _tradeQueue.Reader.ReadAllAsync())
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                    db.Trades.Add(trade);
                    await db.SaveChangesAsync();
                    break; // success
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to persist trade (attempt {Attempt}/3)", attempt + 1);
                    if (attempt < 2) await Task.Delay(500 * (attempt + 1));
                }
            }
        }
    }

    public void Dispose()
    {
        _tradeQueue.Writer.TryComplete();
        _consumerTask.Wait(TimeSpan.FromSeconds(5));
    }

    // Alert events (entry/partial/BE/exit) are handled by the engine's AddAlert ring buffer
    // and pushed to the dashboard via OnSnapshotAsync. No duplicate SignalR "Alert" here.

    public Task OnEntryAsync(EntrySignal sig) => Task.CompletedTask;

    public Task OnPartialAsync(PartialSignal sig) => Task.CompletedTask;

    public Task OnBEMoveAsync(BESignal sig) => Task.CompletedTask;

    public async Task OnExitAsync(ExitSignal sig, TradeRecord trade)
    {
        // Push completed trade to dashboard table in real-time
        await _hub.Clients.All.SendAsync("Trade", new
        {
            time     = TimeZoneInfo.ConvertTimeFromUtc(sig.Time, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).ToString("HH:mm:ss"),
            setup    = sig.Setup.ToString(),
            ticker   = trade.Ticker?.TrimStart('/') ?? "",
            dir      = trade.Direction.ToString(),
            entry    = trade.Entry,
            exit     = trade.Exit,
            reason   = trade.ExitReason.ToString(),
            netPnl   = trade.NetPnl,
            r        = trade.RMultiple
        });

        // Enqueue trade for durable background persistence (retries on failure)
        if (!_tradeQueue.Writer.TryWrite(trade))
            _log.LogError("Trade queue full — trade {SessionId} may be lost!", trade.SessionId);
    }

    public async Task OnSnapshotAsync(EngineSnapshot snap)
    {
        await _hub.Clients.All.SendAsync("Update", snap);
    }
}
