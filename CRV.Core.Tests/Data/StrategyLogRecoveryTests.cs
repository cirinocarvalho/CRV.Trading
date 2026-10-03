using CRV.Core.Data;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// On restart, live recovery re-discovers every broker strategy that may still be open.
/// A position held over a weekend was placed days ago and must still be found.
/// </summary>
public class StrategyLogRecoveryTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public StrategyLogRecoveryTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static StrategyLog Log(string id, DateTime createdAt, bool completed) => new()
    {
        BrokerStrategyId = id, SetupId = "retest-mnq", Ticker = "MNQZ26", TotalContracts = 1,
        PointValue = 2m, CreatedAt = createdAt, IsCompleted = completed,
    };

    [Fact]
    public void Recoverable_IncludesAnOpenPositionHeldOverTheWeekend()
    {
        var monday = new DateTime(2026, 4, 20, 14, 0, 0, DateTimeKind.Utc);
        _db.StrategyLogs.AddRange(
            Log("1001", monday.AddHours(-1), completed: false),                                     // this morning
            Log("1002", new DateTime(2026, 4, 17, 19, 0, 0, DateTimeKind.Utc), completed: false),   // Friday, held
            Log("1003", new DateTime(2026, 4, 17, 15, 0, 0, DateTimeKind.Utc), completed: true),    // Friday, closed
            Log("1004", monday.AddDays(-30), completed: false));                                    // long gone
        _db.SaveChanges();

        var ids = StrategyLogRecovery.Recoverable(_db.StrategyLogs, monday)
            .Select(s => s.BrokerStrategyId).OrderBy(s => s).ToList();

        Assert.Equal(new[] { "1001", "1002" }, ids);
    }
}
