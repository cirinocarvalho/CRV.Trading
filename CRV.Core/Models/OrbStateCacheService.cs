using System.Text.Json;

namespace CRV.Core.Models;

public static class OrbStateCacheService
{
    private const string FileName = "orb_cache.json";

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public static void Save(OrbStateCache cache)
    {
        try
        {
            // Load existing entries, upsert by symbol+sessionId+tradingDate, write back.
            // This accumulates one entry per symbol/session/day instead of overwriting.
            var entries = LoadAll();
            var idx = entries.FindIndex(e =>
                e.Symbol == cache.Symbol
                && e.SessionId == cache.SessionId
                && e.TradingDate.Date == cache.TradingDate.Date);
            if (idx >= 0) entries[idx] = cache; else entries.Add(cache);

            // Prune entries older than 90 days to keep the file from growing unbounded
            var cutoff = DateTime.UtcNow.AddDays(-90);
            entries.RemoveAll(e => e.TradingDate < cutoff);

            var json = JsonSerializer.Serialize(entries, _opts);
            File.WriteAllText(FileName, json);
        }
        catch { /* best-effort, don't crash engine */ }
    }

    public static OrbStateCache? Load(string symbol, DateTime tradingDate, string sessionId = "")
    {
        try
        {
            var entries = LoadAll();
            return entries.FirstOrDefault(e =>
                e.TradingDate.Date == tradingDate.Date &&
                e.Symbol == symbol &&
                e.SessionId == sessionId);
        }
        catch { return null; }
    }

    private static List<OrbStateCache> LoadAll()
    {
        try
        {
            if (!File.Exists(FileName)) return new();
            var json = File.ReadAllText(FileName);
            // Support both legacy single-object and new array format
            json = json.TrimStart();
            if (json.StartsWith('['))
                return JsonSerializer.Deserialize<List<OrbStateCache>>(json) ?? new();
            var single = JsonSerializer.Deserialize<OrbStateCache>(json);
            return single != null ? new() { single } : new();
        }
        catch { return new(); }
    }
}
