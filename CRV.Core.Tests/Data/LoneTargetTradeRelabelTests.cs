using CRV.Core.Data;
using CRV.Core.Migrations;
using CRV.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// Before single-bracket orders were labelled Tg2, their trades were saved with Target = 0, the
/// target price in Partial, and PartialFilled set when the target filled. The data migration
/// moves those rows back and leaves every other trade alone.
/// </summary>
public class LoneTargetTradeRelabelTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public LoneTargetTradeRelabelTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static TradeRecord Trade(string label, decimal target, decimal partial, bool partialFilled,
        decimal partialPrice, ExitReason reason, decimal exit) => new()
    {
        SessionId = "NY", Source = "live", Setup = SetupId.F, SetupLabel = label, Direction = Direction.Long,
        Ticker = "MNQZ26", Contracts = 1, Entry = 21000m, InitialStop = 20980m, Target = target, Partial = partial,
        PartialFilled = partialFilled, PartialPrice = partialPrice, Exit = exit, ExitReason = reason,
        GrossPnl = (exit - 21000m) * 2m, NetPnl = (exit - 21000m) * 2m - 1.24m, Commission = 1.24m,
        RMultiple = (exit - 21000m) / 20m, EnteredAt = DateTime.UtcNow, ExitedAt = DateTime.UtcNow,
    };

    private TradeRecord Reload(string label)
    {
        _db.ChangeTracker.Clear();
        return _db.Trades.Single(t => t.SetupLabel == label);
    }

    [Fact]
    public void Relabel_MovesLoneTargetsBack_AndLeavesOtherTradesAlone()
    {
        _db.Trades.AddRange(
            Trade("lone-hit",     target: 0m,     partial: 21040m, partialFilled: true,  partialPrice: 21040m, ExitReason.Target, exit: 21040m),
            Trade("lone-stopped", target: 0m,     partial: 21040m, partialFilled: false, partialPrice: 21040m, ExitReason.Stop,   exit: 20980m),
            Trade("two-target",   target: 21060m, partial: 21030m, partialFilled: true,  partialPrice: 21030m, ExitReason.Target, exit: 21060m),
            Trade("already-ok",   target: 21040m, partial: 0m,     partialFilled: false, partialPrice: 0m,     ExitReason.Target, exit: 21040m));
        _db.SaveChanges();

        _db.Database.ExecuteSqlRaw(RelabelLoneTargetTrades.Sql);

        foreach (var label in new[] { "lone-hit", "lone-stopped" })
        {
            var t = Reload(label);
            Assert.Equal(21040m, t.Target);
            Assert.Equal(0m, t.Partial);
            Assert.False(t.PartialFilled);
            Assert.Equal(0m, t.PartialPrice);
        }

        var two = Reload("two-target");
        Assert.Equal((21060m, 21030m, true, 21030m), (two.Target, two.Partial, two.PartialFilled, two.PartialPrice));
        var ok = Reload("already-ok");
        Assert.Equal((21040m, 0m, false, 0m), (ok.Target, ok.Partial, ok.PartialFilled, ok.PartialPrice));

        // P&L, exit and R are never touched.
        var hit = Reload("lone-hit");
        Assert.Equal((21040m, 80m, 2m), (hit.Exit, hit.GrossPnl, hit.RMultiple));
    }
}
