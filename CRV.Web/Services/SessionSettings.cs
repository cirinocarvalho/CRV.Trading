using System.Text.Json;
using System.Text.Json.Serialization;
using CRV.Core.Models;

namespace CRV.Web.Services;

/// <summary>
/// Applies the per-session settings a settings page posts (as JSON) to the config,
/// keeping the flat legacy fields the backtest reads in step with them.
/// </summary>
public static class SessionSettings
{
    private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    /// <returns>The config to save: a new instance when the NY session's setups were copied onto it.</returns>
    public static StrategyConfig Apply(StrategyConfig config, string? sessionsJson, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(sessionsJson)) return config;
        try
        {
            var sessions = JsonSerializer.Deserialize<List<SessionConfig>>(sessionsJson, Options);
            if (sessions == null) return config;

            // Sync the NY session's setups onto the flat config for backward compat (backtest, etc.).
            var basketJson = config.BasketJson;
            var ny = sessions.FirstOrDefault(s => s.SessionId == SessionId.NY);
            if (ny != null)
            {
                // ToLegacyConfig clones the config and overwrites the per-setup fields,
                // keeping the global ones (Broker, Ticker, PointValue, ...).
                config = ny.ToLegacyConfig(config);
                config.BasketJson = basketJson;
            }

            config.Sessions = sessions;
            foreach (var s in sessions)
                log.LogInformation("Session {Id}: Enabled={E} OrbStart={OS} OrbEnd={OE}", s.SessionId, s.Enabled, s.OrbStart, s.OrbEnd);

            // The flat timing follows the NY session when it is on, else the first one that is.
            var primary = (ny?.Enabled == true ? ny : null) ?? sessions.FirstOrDefault(s => s.Enabled);
            if (primary != null)
            {
                config.OrbStart = primary.OrbStart;
                config.OrbEnd   = primary.OrbEnd;
                config.RthStart = primary.RthStart;
                config.RthEnd   = primary.RthEnd;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to deserialize SessionsJson: {Json}", sessionsJson);
        }
        return config;
    }
}
