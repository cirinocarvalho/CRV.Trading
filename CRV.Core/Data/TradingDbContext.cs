using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CRV.Core.Data;

public class TradingDbContext : DbContext
{
    public TradingDbContext(DbContextOptions<TradingDbContext> opts) : base(opts) { }

    public DbSet<TradeRecord>    Trades  { get; set; } = null!;
    public DbSet<StrategyConfig> Configs { get; set; } = null!;
    public DbSet<BacktestRunRow> BacktestRuns { get; set; } = null!;
    public DbSet<OrderRecord>   Orders  { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TradeRecord>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Setup).HasConversion<string>();
            e.Property(t => t.Direction).HasConversion<string>();
            e.Property(t => t.ExitReason).HasConversion<string>();
            e.HasIndex(t => t.EnteredAt);
            e.HasIndex(t => t.SessionId);
            e.HasIndex(t => t.Source);
            e.HasIndex(t => t.Ticker);
            e.HasIndex(t => t.ExitReason);
        });
        b.Entity<StrategyConfig>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.BasketJson).HasColumnType("TEXT");
        });
        b.Entity<BacktestRunRow>(e => e.HasKey(r => r.Id));
        b.Entity<OrderRecord>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.Broker);
            e.HasIndex(o => o.PlacedAt);
            e.HasIndex(o => o.OrderId);
        });
    }
}

public class BacktestRunRow
{
    public int      Id           { get; set; }
    public string   Ticker       { get; set; } = "";
    public string   ConfigName   { get; set; } = "";
    public DateTime From         { get; set; }
    public DateTime To           { get; set; }
    public string   DataSource   { get; set; } = "";
    public string   FillMode     { get; set; } = "";
    public int      TotalTrades  { get; set; }
    public decimal  NetPnl       { get; set; }
    public decimal  WinRate      { get; set; }
    public decimal  ProfitFactor { get; set; }
    public decimal  MaxDrawdown  { get; set; }
    public DateTime RunAt        { get; set; }
    public string?  ResultJson   { get; set; }
}
