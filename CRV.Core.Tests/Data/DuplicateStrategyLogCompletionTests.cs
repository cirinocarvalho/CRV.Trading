using CRV.Core.Data;
using CRV.Core.Migrations;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// Restart recovery once wrote a second StrategyLog row for a strategy it re-registered, and only
/// one of the pair was completed. Recovery looks back seven days, so those leftover rows would
/// come back as duplicate trades. The data migration completes them and nothing else.
/// </summary>
public class DuplicateStrategyLogCompletionTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public DuplicateStrategyLogCompletionTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static StrategyLog Row(string strategyId, bool completed) => new()
    {
        BrokerStrategyId = strategyId, SetupId = "retest-mnq", Ticker = "MNQZ26", TotalContracts = 1,
        PointValue = 2m, CreatedAt = DateTime.UtcNow.AddDays(-2), IsCompleted = completed,
    };

    [Fact]
    public void Migration_CompletesALeftoverDuplicate_AndLeavesAGenuinelyOpenRowAlone()
    {
        _db.StrategyLogs.AddRange(
            Row("5001", completed: true),
            Row("5001", completed: false),
            Row("5002", completed: false));
        _db.SaveChanges();

        _db.Database.ExecuteSqlRaw(CompleteDuplicateStrategyLogs.Sql);

        _db.ChangeTracker.Clear();
        Assert.All(_db.StrategyLogs.Where(s => s.BrokerStrategyId == "5001").ToList(), s => Assert.True(s.IsCompleted));
        Assert.False(_db.StrategyLogs.Single(s => s.BrokerStrategyId == "5002").IsCompleted);
    }
}
