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

    public async Task OnEntryAsync(EntrySignal sig)
    {
        await _hub.Clients.All.SendAsync("Alert", new
        {
            time    = sig.Time.ToString("HH:mm:ss"),
            setup   = sig.Setup.ToString(),
            type    = "ENTRY",
            color   = sig.Direction == Direction.Long ? "green" : "red",
            message = $"{sig.Direction} {sig.Contracts}ct @ {sig.Entry:F2} | Stop {sig.Stop:F2} | Tgt {sig.Target:F2}"
        });
    }

    public async Task OnPartialAsync(PartialSignal sig)
    {
        await _hub.Clients.All.SendAsync("Alert", new
        {
            time    = sig.Time.ToString("HH:mm:ss"),
            setup   = sig.Setup.ToString(),
            type    = "PARTIAL",
            color   = "yellow",
            message = $"Partial {sig.ContractsExited}ct @ {sig.PartialPrice:F2}"
        });
    }

    public async Task OnBEMoveAsync(BESignal sig)
    {
        await _hub.Clients.All.SendAsync("Alert", new
        {
            time    = sig.Time.ToString("HH:mm:ss"),
            setup   = sig.Setup.ToString(),
            type    = "MOVE_BE",
            color   = "yellow",
            message = $"Stop → BE {sig.NewStop:F2}"
        });
    }

    public async Task OnExitAsync(ExitSignal sig, TradeRecord trade)
    {
        // Broadcast alert immediately — don't block on DB write
        await _hub.Clients.All.SendAsync("Alert", new
        {
            time    = sig.Time.ToString("HH:mm:ss"),
            setup   = sig.Setup.ToString(),
            type    = "EXIT",
            color   = sig.Reason == ExitReason.Target ? "green" : "red",
            message = $"{sig.Reason} @ {sig.ExitPrice:F2} | Net {(trade.NetPnl >= 0 ? "+" : "")}${trade.NetPnl:F0} | {trade.RMultiple:F1}R"
        });

        // Enqueue trade for durable background persistence (retries on failure)
        if (!_tradeQueue.Writer.TryWrite(trade))
            _log.LogError("Trade queue full — trade {SessionId} may be lost!", trade.SessionId);
    }

    public Task OnSnapshotAsync(EngineSnapshot snap)
    {
        // Snapshots now stream via SSE (SnapshotBroadcastService).
        // SignalR remains for Alert and EngineStatusChanged only.
        return Task.CompletedTask;
    }
}
