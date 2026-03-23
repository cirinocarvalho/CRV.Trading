using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CRV.Core.Data;

/// <summary>
/// Allows EF Core tools to create TradingDbContext at design time.
/// Run migrations with:
///   dotnet ef migrations add Initial --project CRV.Core --startup-project CRV.Web
///   dotnet ef database update           --project CRV.Core --startup-project CRV.Web
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite("Data Source=crv_trading.db")
            .Options;
        return new TradingDbContext(opts);
    }
}
