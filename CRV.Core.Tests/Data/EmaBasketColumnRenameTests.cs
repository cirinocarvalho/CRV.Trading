using CRV.Core.Data;
using CRV.Core.Migrations;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// The EMA basket column is renamed with its content. Stored EMA21 entries stay as they are,
/// and no other column changes.
/// </summary>
public class EmaBasketColumnRenameTests : IDisposable
{
    private const string MigrationBefore = "20261002194726_RelabelLoneTargetTrades";
    private const string MigrationUnderTest = "20261002215534_RenameEmaBasketColumn";

    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public EmaBasketColumnRenameTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private List<string> ConfigColumns()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('Configs') ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private string? Text(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public void RenameEmaBasketColumn_KeepsContentAndEveryOtherColumn_BothWays()
    {
        const string ema = """[{"Id":"ema21-mnq","Enabled":true,"StrategyType":4,"Ticker":"/MNQZ26"}]""";
        _db.Configs.Add(new StrategyConfig { Id = 1, BasketJson = "[]", EmaBasketJson = ema, EmailRecipients = "" });
        _db.SaveChanges();
        var renamed = ConfigColumns();

        var migrator = _db.GetService<IMigrator>();
        migrator.Migrate(MigrationBefore);

        var original = ConfigColumns();
        Assert.Contains(RenameEmaBasketColumn.OldName, original);
        Assert.DoesNotContain(RenameEmaBasketColumn.NewName, original);
        Assert.Equal(
            renamed.Select(c => c == RenameEmaBasketColumn.NewName ? RenameEmaBasketColumn.OldName : c).OrderBy(c => c, StringComparer.Ordinal),
            original.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(ema, Text($"SELECT \"{RenameEmaBasketColumn.OldName}\" FROM \"Configs\" WHERE \"Id\" = 1"));

        migrator.Migrate(MigrationUnderTest);

        _db.ChangeTracker.Clear();
        var cfg = _db.Configs.AsNoTracking().Single(c => c.Id == 1);
        Assert.Equal(ema, cfg.EmaBasketJson);
        Assert.Equal("[]", cfg.BasketJson);
        Assert.Equal(renamed, ConfigColumns());
    }
}
