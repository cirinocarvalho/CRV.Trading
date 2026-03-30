using CRV.Core.Data;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CRV.Web.Services;

/// <summary>
/// Persistence strategy for live brokers (Tradovate, Schwab, TradeStation).
/// Writes a single StrategyLog row on placement, marks it completed on exit.
/// All live order state comes from broker REST — no GroupOrder/OrderLeg DB rows.
/// Recovery on startup: query StrategyLog → RecoverStrategyAsync via REST.
/// </summary>
public class LiveBrokerPersistence : IBrokerPersistence
{
    private readonly IServiceProvider _sp;
    private readonly ILogger? _log;

    public LiveBrokerPersistence(IServiceProvider sp, ILogger? log = null)
    {
        _sp = sp;
        _log = log;
    }

    public bool PersistsLiveOrderState => false;

    public async Task OnGroupPlacedAsync(GroupOrder group)
    {
        if (string.IsNullOrEmpty(group.BrokerStrategyId)) return;

        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            db.StrategyLogs.Add(new StrategyLog
            {
                BrokerStrategyId = group.BrokerStrategyId,
                SetupId          = group.SetupId,
                Ticker           = group.Ticker,
                Direction        = group.Direction,
                TotalContracts   = group.TotalContracts,
                PartialContracts = group.PartialContracts,
                UseBe            = group.UseBe,
                PointValue       = group.PointValue,
                CreatedAt        = group.CreatedAt,
            });
            await db.SaveChangesAsync();
            _log?.LogInformation("[PERSIST] StrategyLog saved: {S} {Setup} {Ticker}",
                group.BrokerStrategyId, group.SetupId, group.Ticker);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "[PERSIST] Failed to save StrategyLog for {S}", group.BrokerStrategyId);
        }
    }

    public async Task OnGroupCompletedAsync(GroupOrder group)
    {
        if (string.IsNullOrEmpty(group.BrokerStrategyId)) return;

        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var log = await db.StrategyLogs
                .FirstOrDefaultAsync(s => s.BrokerStrategyId == group.BrokerStrategyId);
            if (log != null)
            {
                log.IsCompleted = true;
                await db.SaveChangesAsync();
                _log?.LogDebug("[PERSIST] StrategyLog completed: {S}", group.BrokerStrategyId);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "[PERSIST] Failed to complete StrategyLog for {S}", group.BrokerStrategyId);
        }
    }

    public async Task<List<GroupOrder>> RecoverAsync(IGroupOrderExecutor executor, CancellationToken ct)
    {
        var result = new List<GroupOrder>();
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            // Find uncompleted strategies from today
            var today = DateTime.UtcNow.Date;
            var logs = await db.StrategyLogs
                .Where(s => !s.IsCompleted && s.CreatedAt >= today)
                .ToListAsync(ct);

            _log?.LogInformation("[RECOVER] Found {N} uncompleted strategies in StrategyLog", logs.Count);

            foreach (var log in logs)
            {
                if (!long.TryParse(log.BrokerStrategyId, out var stratId)) continue;

                try
                {
                    var group = await executor.RecoverStrategyAsync(
                        stratId, log.Ticker, log.Direction,
                        log.TotalContracts, log.PartialContracts, log.UseBe, log.SetupId);

                    if (group != null)
                    {
                        group.PointValue = log.PointValue;
                        group.CreatedAt = log.CreatedAt;

                        if (group.Status == GroupOrderStatus.Completed)
                        {
                            // Trade already finished at broker — mark log completed, still return
                            // group so orchestrator can record the TradeRecord if needed
                            log.IsCompleted = true;
                            _log?.LogInformation("[RECOVER] Strategy {S} already completed at broker — will record trade",
                                log.BrokerStrategyId);
                        }

                        result.Add(group);
                        _log?.LogInformation("[RECOVER] Restored strategy {S} → group {G} status={St}",
                            log.BrokerStrategyId, group.GroupOrderId, group.Status);
                    }
                    else
                    {
                        // Strategy has no legs at broker — probably already completed/canceled
                        log.IsCompleted = true;
                        _log?.LogDebug("[RECOVER] Strategy {S} has no legs — marking completed", log.BrokerStrategyId);
                    }
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "[RECOVER] Failed to recover strategy {S}", log.BrokerStrategyId);
                }
            }

            await db.SaveChangesAsync(ct); // persist any IsCompleted = true changes
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "[RECOVER] Failed to recover strategies from StrategyLog");
        }

        return result;
    }
}
