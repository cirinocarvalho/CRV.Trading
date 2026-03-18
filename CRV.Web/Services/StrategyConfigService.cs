namespace CRV.Web.Services;

using CRV.Core.Data;
using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────
// StrategyConfigService — singleton holding current live config
// Source of truth: SQLite Configs table (Id = 1)
// Falls back to live_settings.json on first run to seed the DB
// ─────────────────────────────────────────────────────────────

public class StrategyConfigService
{
    private static readonly string JsonBackupPath = "live_settings.json";
    private static readonly int    ConfigId       = 1;

    private StrategyConfig _current;
    private readonly object           _lock = new();
    private readonly IServiceProvider _sp;
    private readonly ILogger<StrategyConfigService> _log;

    public event Action<StrategyConfig>? ConfigChanged;

    public StrategyConfigService(IServiceProvider sp, ILogger<StrategyConfigService> log)
    {
        _sp      = sp;
        _log     = log;
        _current = LoadFromDb();
    }

    public StrategyConfig Current
    {
        get { lock (_lock) { return _current; } }
    }

    public void Update(StrategyConfig cfg)
    {
        cfg.Id        = ConfigId;
        cfg.UpdatedAt = DateTime.UtcNow;

        lock (_lock) { _current = cfg; }

        SaveToDb(cfg);
        SaveJsonBackup(cfg);
        SaveSessions(cfg.Sessions);
        ConfigChanged?.Invoke(cfg);
    }

    // ── Private helpers ───────────────────────────────────────

    private StrategyConfig LoadFromDb()
    {
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var cfg = db.Configs.AsNoTracking().FirstOrDefault(c => c.Id == ConfigId);
            if (cfg != null)
            {
                _log.LogInformation("StrategyConfig loaded from DB (Id={Id}, UpdatedAt={At})",
                    cfg.Id, cfg.UpdatedAt);
                cfg.Sessions = LoadSessions();
                return cfg;
            }

            // First run — seed from JSON backup if it exists
            cfg = LoadJsonBackup();
            cfg.Id        = ConfigId;
            cfg.UpdatedAt = DateTime.UtcNow;

            db.Configs.Add(cfg);
            db.SaveChanges();
            _log.LogInformation("StrategyConfig seeded into DB from JSON backup.");
            cfg.Sessions = LoadSessions();
            return cfg;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load StrategyConfig from DB — using defaults.");
            return new StrategyConfig { Id = ConfigId };
        }
    }

    private void SaveToDb(StrategyConfig cfg)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var tracked = db.Configs.Find(ConfigId);
            if (tracked != null)
            {
                // Copy every property onto the tracked entity
                db.Entry(tracked).CurrentValues.SetValues(cfg);
            }
            else
            {
                cfg.Id = ConfigId;
                db.Configs.Add(cfg);
            }

            db.SaveChanges();
            _log.LogInformation("StrategyConfig saved to DB (Id={Id}).", ConfigId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save StrategyConfig to DB.");
        }
    }

    private StrategyConfig LoadJsonBackup()
    {
        if (!File.Exists(JsonBackupPath)) return new StrategyConfig();
        try
        {
            var json = File.ReadAllText(JsonBackupPath);
            return JsonSerializer.Deserialize<StrategyConfig>(json) ?? new StrategyConfig();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read {Path} — using defaults.", JsonBackupPath);
            return new StrategyConfig();
        }
    }

    private void SaveJsonBackup(StrategyConfig cfg)
    {
        try
        {
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(JsonBackupPath, json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write JSON backup to {Path}.", JsonBackupPath);
        }
    }

    private static readonly string SessionsPath = "sessions.json";

    private void SaveSessions(List<SessionConfig>? sessions)
    {
        try
        {
            if (sessions != null)
                File.WriteAllText(SessionsPath, JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save sessions.json"); }
    }

    private List<SessionConfig>? LoadSessions()
    {
        try
        {
            if (File.Exists(SessionsPath))
                return JsonSerializer.Deserialize<List<SessionConfig>>(File.ReadAllText(SessionsPath));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to load sessions.json"); }
        return null;
    }
}


