using CRV.Core.Data;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// EF Core's default SQLite mapping stores <c>decimal</c> as TEXT, which makes every
/// MIN/MAX/ORDER BY on a money or R-multiple column lexicographic rather than numeric.
/// Against the live book that reported the worst trade as -$103 when it was -$740.70
/// and the worst R as -0.07 when it was -4.32. These tests pin the numeric storage.
/// </summary>
public class DecimalStorageTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public DecimalStorageTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private string ColumnType(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT type FROM pragma_table_info('{table}') WHERE name = '{column}';";
        return (string)cmd.ExecuteScalar()!;
    }

    private double Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDouble(cmd.ExecuteScalar());
    }

    [Theory]
    [InlineData("Trades", "RMultiple")]
    [InlineData("Trades", "NetPnl")]
    [InlineData("Trades", "GrossPnl")]
    [InlineData("Trades", "Commission")]
    [InlineData("Trades", "Entry")]
    [InlineData("Trades", "InitialStop")]
    [InlineData("Trades", "Exit")]
    [InlineData("BacktestRuns", "NetPnl")]
    [InlineData("BacktestRuns", "MaxDrawdown")]
    [InlineData("BacktestRuns", "ProfitFactor")]
    [InlineData("Orders", "FillPrice")]
    public void DecimalColumnsAreStoredAsReal(string table, string column)
        => Assert.Equal("REAL", ColumnType(table, column));

    [Fact]
    public void MinAndMaxOverRMultipleAreNumericNotLexicographic()
    {
        // The three values that exposed the bug: text-sorted, "-0.07" < "-4.32"
        // because '0' < '4', so MIN returned the shallowest loss, not the deepest.
        foreach (var r in new[] { -4.3235294117647056m, -0.0732394366197183m, 2.8235294117647m })
            _db.Trades.Add(NewTrade(rMultiple: r, netPnl: r * 100m));
        _db.SaveChanges();

        Assert.Equal(-4.3235, Scalar("SELECT MIN(RMultiple) FROM Trades"), 3);
        Assert.Equal(2.8235,  Scalar("SELECT MAX(RMultiple) FROM Trades"), 3);
        Assert.Equal(-432.35, Scalar("SELECT MIN(NetPnl) FROM Trades"),    1);
    }

    [Fact]
    public void OrderByNetPnlRanksNumerically()
    {
        foreach (var p in new[] { -740.70m, -103.36m, -9.50m })
            _db.Trades.Add(NewTrade(rMultiple: -1m, netPnl: p));
        _db.SaveChanges();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT NetPnl FROM Trades ORDER BY NetPnl ASC LIMIT 1";
        Assert.Equal(-740.70, Convert.ToDouble(cmd.ExecuteScalar()), 2);
    }

    [Fact]
    public void RoundTripPreservesPriceToTheTick()
    {
        _db.Trades.Add(NewTrade(rMultiple: 1.5m, netPnl: 123.45m, entry: 24875.25m));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        Assert.Equal(24875.25m, _db.Trades.Single().Entry);
    }

    private static TradeRecord NewTrade(decimal rMultiple, decimal netPnl, decimal entry = 100m) => new()
    {
        SessionId = "s", Source = "live", Ticker = "MNQ", Contracts = 1,
        Entry = entry, InitialStop = entry - 10m, Target = entry + 20m, Exit = entry + 5m,
        GrossPnl = netPnl + 2m, Commission = 2m, NetPnl = netPnl, RMultiple = rMultiple,
        EnteredAt = DateTime.UtcNow, ExitedAt = DateTime.UtcNow.AddMinutes(5),
    };
}
