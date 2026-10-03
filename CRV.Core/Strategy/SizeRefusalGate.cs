using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Reports each refused signal once, however many ticks re-ask for it.
/// <para>
/// A strategy the budget refuses stays armed and asks again on the next tick — it
/// always did; the refusal used to be a bare <c>return</c>. That behaviour is kept:
/// with a stop mode whose stop moves bar to bar, a later ask can fit. What must not
/// happen is one signal becoming twenty-two alerts and a refusal count that measures
/// tick frequency rather than signals. A size refusal is the same signal
/// while its direction, entry and stop are the same.
/// </para>
/// <para>
/// A min-R skip is reported once per direction and reason until <see cref="EndEpisode"/> or <see cref="Reset"/>: the
/// strategies re-ask on every tick with the moving tick price as the entry, so keying on the
/// price would alert on every tick.
/// </para>
/// </summary>
public sealed class SizeRefusalGate
{
    private (bool IsLong, decimal Ep, decimal Sl)? _last;
    private readonly HashSet<(bool IsLong, bool NoRewardOrRisk)> _reportedSkips = new();

    /// <summary>The last signal skipped for reward / risk since the last reset; shown on the cockpit card.</summary>
    public SizeRefusal? LastMinRrSkip { get; private set; }

    /// <summary>The refusal to report, or null when this signal was already reported.</summary>
    public SizeRefusal? Report(bool isLong, decimal ep, decimal sl, StrategySetupConfig cfg, DateTime time)
    {
        var key = (isLong, ep, sl);
        if (_last == key) return null;

        _last = key;
        return AutoSizeByRiskCalculator.Refusal(ep, sl, cfg, time);
    }

    /// <summary>The min-R skip to report, or null when this direction and reason was already reported.</summary>
    public SizeRefusal? ReportMinRr(bool isLong, decimal ep, decimal sl, decimal rr, StrategySetupConfig cfg, DateTime time)
    {
        if (!_reportedSkips.Add((isLong, rr <= 0))) return null;

        LastMinRrSkip = AutoSizeByRiskCalculator.Refusal(ep, sl, cfg, time)
            with { Reason = RefusalReason.MinRr, Rr = rr, MinRr = cfg.MinRr };
        return LastMinRrSkip;
    }

    /// <summary>
    /// End the armed episode: the next min-R skip is reported again. Keeps
    /// <see cref="LastMinRrSkip"/>, which lasts until the session resets.
    /// </summary>
    public void EndEpisode() => _reportedSkips.Clear();

    /// <summary>Forget the last signals — a new day or session may legitimately refuse them again.</summary>
    public void Reset()
    {
        _last = null;
        _reportedSkips.Clear();
        LastMinRrSkip = null;
    }
}
