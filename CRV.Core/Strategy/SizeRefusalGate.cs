using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Reports each refused signal once, however many ticks re-ask for it.
/// <para>
/// A strategy the budget refuses stays armed and asks again on the next tick — it
/// always did; the refusal used to be a bare <c>return</c>. That behaviour is kept:
/// with a stop mode whose stop moves bar to bar, a later ask can fit. What must not
/// happen is one signal becoming twenty-two alerts and a refusal count that measures
/// tick frequency rather than signals. A signal is the same signal while its
/// direction, entry and stop are the same.
/// </para>
/// </summary>
public sealed class SizeRefusalGate
{
    private (bool IsLong, decimal Ep, decimal Sl)? _last;

    /// <summary>The refusal to report, or null when this signal was already reported.</summary>
    public SizeRefusal? Report(bool isLong, decimal ep, decimal sl, StrategySetupConfig cfg, DateTime time)
    {
        var key = (isLong, ep, sl);
        if (_last == key) return null;

        _last = key;
        return AutoSizeByRiskCalculator.Refusal(ep, sl, cfg, time);
    }

    /// <summary>Forget the last signal — a new day or session may legitimately refuse it again.</summary>
    public void Reset() => _last = null;
}
