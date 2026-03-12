using System.Threading.RateLimiting;
using CRV.Core.Data;
using CRV.Core.Interfaces;
using Microsoft.AspNetCore.RateLimiting;
using CRV.Live.Brokers;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Hubs;
using CRV.Web.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Structured logging → Seq ─────────────────────────────────
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console()
    .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://localhost:5341")
    .Enrich.FromLogContext());

// ── Database ──────────────────────────────────────────────────
builder.Services.AddDbContext<TradingDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                  ?? "Data Source=crv_trading.db"));

// ── Razor Pages + SignalR + Controllers ───────────────────────
builder.Services.AddRazorPages();
builder.Services.AddSignalR()
    .AddJsonProtocol(o =>
        o.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddControllers();

// ── OpenAPI / Swagger ─────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "CRV Trading API", Version = "v1" });
});

// ── Rate limiting (engine API endpoints) ─────────────────────
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("engine-api", limiter =>
    {
        limiter.PermitLimit          = 5;
        limiter.Window               = TimeSpan.FromSeconds(1);
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiter.QueueLimit           = 2;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── HTTP client factory (prevents socket exhaustion from new HttpClient()) ──
builder.Services.AddHttpClient();

// ── Core singleton services ───────────────────────────────────
builder.Services.AddSingleton<StrategyConfigService>();
builder.Services.AddSingleton<LastPriceProvider>();
builder.Services.AddSingleton<ILastPriceProvider>(sp =>
    sp.GetRequiredService<LastPriceProvider>());
builder.Services.AddSingleton<DailyStatsService>();

// ── Strategy event sink (SignalR → dashboard) ─────────────────
builder.Services.AddSingleton<SignalREventSink>();
builder.Services.AddSingleton<IStrategyEventSink>(sp =>
    sp.GetRequiredService<SignalREventSink>());

// ── Broker credentials ────────────────────────────────────────
// Store credentials in user-secrets or env vars (never in appsettings.json):
//   dotnet user-secrets set "Schwab:AppKey"       "YOUR_KEY"
//   dotnet user-secrets set "Schwab:AppSecret"    "YOUR_SECRET"
//   dotnet user-secrets set "TradeStation:ClientId"     "YOUR_ID"
//   dotnet user-secrets set "TradeStation:ClientSecret" "YOUR_SECRET"
var schwabCfg = builder.Configuration.GetSection("Schwab");
builder.Services.AddSingleton(sp => new SchwabAuthService(
    schwabCfg["AppKey"]      ?? "",
    schwabCfg["AppSecret"]   ?? "",
    schwabCfg["RedirectUri"] ?? "https://127.0.0.1:5001/auth/schwab",
    schwabCfg["TokenFile"]   ?? "schwab_tokens.json",
    schwabCfg["ApiBaseUrl"]  ?? "https://api.schwabapi.com",
    schwabCfg["WssBaseUrl"]  ?? "wss://streamer-api.schwab.com/ws",
    sp.GetRequiredService<IHttpClientFactory>()));

var tsCfg = builder.Configuration.GetSection("TradeStation");
builder.Services.AddSingleton(sp => new TradeStationAuthService(
    tsCfg["ClientId"]     ?? "",
    tsCfg["ClientSecret"] ?? "",
    tsCfg["RedirectUri"]  ?? "https://127.0.0.1:5001/auth/tradestation",
    tsCfg["TokenFile"]    ?? "tradestation_tokens.json",
    tsCfg["ApiBaseUrl"]   ?? "https://api.tradestation.com",
    tsCfg["AuthBaseUrl"]  ?? "https://signin.tradestation.com",
    sp.GetRequiredService<IHttpClientFactory>()));

var tvCfg = builder.Configuration.GetSection("Tradovate");
builder.Services.AddSingleton(sp => new TradovateAuthService(
    tvCfg["Username"]   ?? "",
    tvCfg["Password"]   ?? "",
    int.TryParse(tvCfg["Cid"], out var tvCid) ? tvCid : 0,
    tvCfg["Secret"]     ?? "",
    tvCfg["TokenFile"]  ?? "tradovate_tokens.json",
    tvCfg["ApiBaseUrl"] ?? "https://live.tradovateapi.com/v1",
    tvCfg["MdWssUrl"]   ?? "wss://md.tradovateapi.com/v1/websocket",
    sp.GetRequiredService<IHttpClientFactory>()));

// ── Broker executors ─────────────────────────────────────────
// MockBrokerExecutor is a singleton fallback registered in DI.
// Schwab/TradeStation feeds and executors are constructed directly by
// LiveEngineOrchestrator at engine start (not via DI) because they
// require a runtime StrategyConfig that is not a DI service.
builder.Services.AddSingleton<MockBrokerExecutor>();

// ── Live engine ───────────────────────────────────────────────
builder.Services.AddSingleton<LiveEngineOrchestrator>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<LiveEngineOrchestrator>());

// ── Backtest ──────────────────────────────────────────────────
builder.Services.AddSingleton<BacktestRunnerService>();
builder.Services.AddScoped<CRV.Backtest.DataLoaders.CsvBarLoader>();

var app = builder.Build();

// ── DB: apply pending migrations ──────────────────────────────
// To generate migrations: dotnet ef migrations add <Name> --project CRV.Core --startup-project CRV.Web
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

    // ── Idempotent schema repair ───────────────────────────────
    // Handles DBs that were ever managed by EnsureCreated() or had a migration
    // only partially applied. Each block is safe to run on every startup.
    try
    {
        // 1. Ensure migrations history table exists (no-op on already-migrated DBs).
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId"    TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL)
            """);

        // 2. Pre-mark AddMissingStrategyConfigColumns as applied if EntryTickOffsetA already
        //    exists (e.g. added by a prior EnsureCreated or manual column addition).
        db.Database.ExecuteSqlRaw("""
            INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            SELECT '20260310184233_AddMissingStrategyConfigColumns', '10.0.3'
            WHERE EXISTS (
                SELECT 1 FROM pragma_table_info('Configs') WHERE name = 'EntryTickOffsetA'
            )
            """);

        // 3. Add ExecAccountId / ExecBroker if they are still missing.
        //    This happens when the migration above was pre-marked at a point when only
        //    3 of the 5 columns existed (EntryTickOffsetA/B + SessionStartHour were present
        //    but ExecAccountId/ExecBroker were not yet in the model).
        using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        bool HasColumn(string col)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Configs') WHERE name='{col}'";
            return (long)cmd.ExecuteScalar()! > 0;
        }

        // Only patch columns that are recorded as migrated but still physically absent.
        using var histCmd = conn.CreateCommand();
        histCmd.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId='20260310184233_AddMissingStrategyConfigColumns'";
        bool migrationRecorded = (long)histCmd.ExecuteScalar()! > 0;

        if (migrationRecorded)
        {
            if (!HasColumn("ExecAccountId"))
                db.Database.ExecuteSqlRaw("ALTER TABLE \"Configs\" ADD COLUMN \"ExecAccountId\" TEXT NOT NULL DEFAULT ''");
            if (!HasColumn("ExecBroker"))
                db.Database.ExecuteSqlRaw("ALTER TABLE \"Configs\" ADD COLUMN \"ExecBroker\" TEXT NULL");
        }
    }
    catch { /* Fresh DB with no tables yet — Migrate() below will create everything. */ }

    db.Database.Migrate();
}

// ── Swagger (development only) ────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CRV Trading API v1"));
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.MapRazorPages();
app.MapControllers();
app.MapHub<TradingHub>("/hubs/trading");

app.Run();
