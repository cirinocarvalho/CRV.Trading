namespace CRV.Core.Strategy;

using CRV.Core.Models;

/// <summary>
/// Determines the active trading session by clock time and drives
/// engine transitions (Reconfigure / SetIdle / ResetDaily).
/// </summary>
public class SessionManager
{
    private readonly List<SessionConfig> _sessions;
    private SessionConfig? _activeSession;

    public SessionManager(List<SessionConfig> sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public SessionConfig? ActiveSession => _activeSession;

    /// <summary>Find the enabled session whose RTH window contains the given local time, or null.</summary>
    public SessionConfig? GetActiveSession(TimeOnly localTime)
    {
        foreach (var s in _sessions)
        {
            if (!s.Enabled) continue;
            if (localTime >= s.RthStart && localTime < s.RthEnd)
                return s;
        }
        return null;
    }

    /// <summary>
    /// Check for session transitions. Returns the transition type and new session (if any).
    /// Call on every bar/tick with the current local time.
    /// </summary>
    public (TransitionType type, SessionConfig? session) CheckTransition(TimeOnly localTime)
    {
        var newSession = GetActiveSession(localTime);
        var newId = newSession?.SessionId;
        var oldId = _activeSession?.SessionId;

        if (newId == oldId) return (TransitionType.None, _activeSession);

        // Session changed
        var prevSession = _activeSession;
        _activeSession = newSession;

        if (newSession == null)
            return (TransitionType.SessionEnded, null);

        if (prevSession == null)
            return (TransitionType.SessionStarted, newSession);

        // Direct transition (shouldn't happen with gaps, but handle gracefully)
        return (TransitionType.SessionStarted, newSession);
    }

    /// <summary>Validate session configs. Returns list of error messages (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(List<SessionConfig> sessions)
    {
        var errors = new List<string>();

        foreach (var s in sessions.Where(s => s.Enabled))
        {
            if (s.RthEnd <= s.RthStart)
                errors.Add($"Session {s.SessionId}: RthEnd must be after RthStart (cannot span midnight).");
            if (s.OrbEnd <= s.OrbStart)
                errors.Add($"Session {s.SessionId}: OrbEnd must be after OrbStart.");
            if (s.OrbStart < s.RthStart || s.OrbEnd > s.RthEnd)
                errors.Add($"Session {s.SessionId}: ORB window must be within RTH window.");
        }

        // Check for overlap between enabled sessions
        var enabled = sessions.Where(s => s.Enabled).OrderBy(s => s.RthStart).ToList();
        for (int i = 0; i < enabled.Count - 1; i++)
        {
            if (enabled[i].RthEnd > enabled[i + 1].RthStart)
                errors.Add($"Sessions {enabled[i].SessionId} and {enabled[i + 1].SessionId} overlap.");
        }

        return errors;
    }
}

public enum TransitionType { None, SessionStarted, SessionEnded }
