using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>
/// Restart recovery re-registers a held group, which reports it as placed a second time.
/// The strategy must still end up as one row that completes once.
/// </summary>
public class LiveBrokerPersistenceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _sp;

    public LiveBrokerPersistenceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _sp = new ServiceCollection()
            .AddDbContext<TradingDbContext>(o => o.UseSqlite(_conn))
            .BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<TradingDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _sp.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task PlacedThenRecoveredThenCompleted_LeavesOneCompletedRowThatIsNotRecoverable()
    {
        var persistence = new LiveBrokerPersistence(_sp);
        var group = new GroupOrder
        {
            BrokerStrategyId = "2001", SetupId = "retest-mnq", Ticker = "MNQZ26", TotalContracts = 1,
            PointValue = 2m, CreatedAt = DateTime.UtcNow.AddDays(-1),
        };

        await persistence.OnGroupPlacedAsync(group);
        await persistence.OnGroupPlacedAsync(group);
        await persistence.OnGroupCompletedAsync(group);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var rows = await db.StrategyLogs.ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].IsCompleted);
        Assert.Empty(StrategyLogRecovery.Recoverable(db.StrategyLogs, DateTime.UtcNow));
    }

    [Fact]
    public async Task Completed_MarksEveryRowWithTheStrategyId()
    {
        var persistence = new LiveBrokerPersistence(_sp);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.StrategyLogs.AddRange(
                new StrategyLog { BrokerStrategyId = "2002", SetupId = "s", Ticker = "MNQZ26", CreatedAt = DateTime.UtcNow },
                new StrategyLog { BrokerStrategyId = "2002", SetupId = "s", Ticker = "MNQZ26", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await persistence.OnGroupCompletedAsync(new GroupOrder { BrokerStrategyId = "2002" });

        using var check = _sp.CreateScope();
        var logs = await check.ServiceProvider.GetRequiredService<TradingDbContext>().StrategyLogs.ToListAsync();
        Assert.All(logs, l => Assert.True(l.IsCompleted));
    }
}
