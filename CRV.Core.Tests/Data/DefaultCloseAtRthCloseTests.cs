using CRV.Core.Data;
using CRV.Core.Migrations;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CRV.Core.Tests.Data;

/// <summary>
/// Holding is opt-in. The data migration sets CloseAtRthClose to true on every stored basket
/// entry and legacy setup, so no setup starts holding positions until someone switches it off.
/// </summary>
public class DefaultCloseAtRthCloseTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TradingDbContext _db;

    public DefaultCloseAtRthCloseTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static BasketEntry Entry(string id, bool close, decimal stopPct, int maxTrades) => new()
    {
        Id = id, Enabled = true, Label = id, StrategyType = StrategyType.Retest, Ticker = "MNQZ26",
        PointValue = 2m, TickSize = 0.25m,
        Config = new StrategySetupConfig
        {
            CloseAtRthClose = close, StopPct = stopPct, MaxTrades = maxTrades, CutoffHour = 15, CutoffMinute = 45,
        },
    };

    private void Migrate()
    {
        _db.Database.ExecuteSqlRaw(DefaultCloseAtRthClose.BasketSql("BasketJson"));
        _db.Database.ExecuteSqlRaw(DefaultCloseAtRthClose.BasketSql(DefaultCloseAtRthClose.EmaBasketColumn));
        _db.Database.ExecuteSqlRaw(DefaultCloseAtRthClose.LegacySql);
    }

    private StrategyConfig Reload()
    {
        _db.ChangeTracker.Clear();
        return _db.Configs.Single();
    }

    [Fact]
    public void Migrate_EveryBasketEntryAndLegacySetup_ClosesAtSessionEnd()
    {
        _db.Configs.Add(new StrategyConfig
        {
            Name = "live",
            BasketJson = BasketCodec.Serialize(new[] { Entry("retest-mnq", false, 0.35m, 3), Entry("pullback-mes", true, 0.10m, 5) }),
            EmaBasketJson = BasketCodec.Serialize(new[] { Entry("ema21-mnq", false, 0.20m, 2) }),
            CloseAtRthCloseA = false, CloseAtRthCloseB = false, CloseAtRthCloseC = true, CloseAtRthCloseD = false,
        });
        _db.SaveChanges();

        Migrate();

        var cfg = Reload();
        var entries = BasketCodec.Parse(cfg.BasketJson).Concat(BasketCodec.Parse(cfg.EmaBasketJson)).ToList();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.True(e.Config.CloseAtRthClose));
        Assert.True(cfg.CloseAtRthCloseA && cfg.CloseAtRthCloseB && cfg.CloseAtRthCloseC && cfg.CloseAtRthCloseD);
    }

    [Fact]
    public void Migrate_ChangesNothingButCloseAtRthClose()
    {
        var before = new[] { Entry("retest-mnq", false, 0.35m, 3), Entry("pullback-mes", true, 0.10m, 5) };
        _db.Configs.Add(new StrategyConfig { Name = "live", MaxDailyLoss = 750m, BasketJson = BasketCodec.Serialize(before) });
        _db.SaveChanges();

        Migrate();

        var expected = BasketCodec.Parse(BasketCodec.Serialize(before));
        foreach (var e in expected) e.Config.CloseAtRthClose = true;
        var cfg = Reload();
        Assert.Equal(BasketCodec.Serialize(expected), BasketCodec.Serialize(BasketCodec.Parse(cfg.BasketJson)));
        Assert.Equal(new[] { "retest-mnq", "pullback-mes" }, BasketCodec.Parse(cfg.BasketJson).Select(e => e.Id));
        Assert.Equal(("live", 750m), (cfg.Name, cfg.MaxDailyLoss));
    }

    [Fact]
    public void Migrate_EmptyOrMissingBaskets_AreLeftAlone()
    {
        _db.Configs.Add(new StrategyConfig { Name = "legacy", BasketJson = "", EmaBasketJson = null });
        _db.SaveChanges();

        Migrate();

        var cfg = Reload();
        Assert.Equal("", cfg.BasketJson);
        Assert.Null(cfg.EmaBasketJson);
    }

    [Fact]
    public void ANewEntry_ClosesAtSessionEnd()
    {
        Assert.True(new StrategySetupConfig().CloseAtRthClose);
        Assert.True(new BasketEntry().Config.CloseAtRthClose);
    }
}
