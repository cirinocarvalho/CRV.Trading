using CRV.Core.Data;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CRV.Web.Services;

/// <summary>
/// Persistence strategy for Mock/Backtest broker.
/// Full GroupOrder + OrderLeg DB persistence (existing behavior).
/// No recovery on restart — mock orders don't survive.
/// </summary>
public class MockBrokerPersistence : IBrokerPersistence
{
    private readonly IServiceProvider _sp;
    private readonly ILogger? _log;

    public MockBrokerPersistence(IServiceProvider sp, ILogger? log = null)
    {
        _sp = sp;
        _log = log;
    }

    public bool PersistsLiveOrderState => true;

    public async Task OnGroupPlacedAsync(GroupOrder group)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            if (await db.GroupOrders.AnyAsync(g => g.GroupOrderId == group.GroupOrderId))
                return;

            db.GroupOrders.Add(group);
            foreach (var leg in group.Legs)
                db.OrderLegs.Add(leg);
            await db.SaveChangesAsync();
            _log?.LogDebug("[PERSIST-MOCK] GroupOrder {G} saved", group.GroupOrderId);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "[PERSIST-MOCK] Failed to save GroupOrder {G}", group.GroupOrderId);
        }
    }

    public async Task OnGroupCompletedAsync(GroupOrder group)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var existing = await db.GroupOrders
                .FirstOrDefaultAsync(g => g.GroupOrderId == group.GroupOrderId);

            if (existing != null)
            {
                existing.Status = group.Status;
                existing.CompletedAt = group.CompletedAt;
                existing.EntryPrice = group.EntryPrice;
                existing.AccruedPartialPnl = group.AccruedPartialPnl;

                // Update all legs
                foreach (var leg in group.Legs)
                {
                    var dbLeg = await db.OrderLegs
                        .FirstOrDefaultAsync(l => l.OrderId == leg.OrderId);
                    if (dbLeg != null)
                    {
                        dbLeg.Status = leg.Status;
                        dbLeg.FillPrice = leg.FillPrice;
                        dbLeg.FillTime = leg.FillTime;
                        dbLeg.Price = leg.Price;
                        dbLeg.Quantity = leg.Quantity;
                    }
                }
                await db.SaveChangesAsync();
            }
            else
            {
                // Fallback: insert if never persisted
                db.GroupOrders.Add(group);
                foreach (var leg in group.Legs)
                    db.OrderLegs.Add(leg);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "[PERSIST-MOCK] Failed to complete GroupOrder {G}", group.GroupOrderId);
        }
    }

    public Task<List<GroupOrder>> RecoverAsync(IGroupOrderExecutor executor, CancellationToken ct)
    {
        // Mock orders don't survive restart
        return Task.FromResult(new List<GroupOrder>());
    }
}
