using CRV.Core.Models;

namespace CRV.Core.Modules;

public enum SweepType
{
    PDH, PDL, PWH, PWL, PMH, PML,
    SessionHigh, SessionLow,
    OrbHigh, OrbLow,
    EqualHigh, EqualLow
}

public enum SweepDirection { Bull, Bear }

public record SweepEvent(decimal Level, SweepType Type, SweepDirection Direction, DateTime Time);

public class SweepDetector : IEngineModule
{
    private ModuleConfig _cfg;

    // Fixed levels set externally
    private decimal _pdh, _pdl, _pwh, _pwl, _pmh, _pml;
    private decimal _sessionHigh, _sessionLow, _orbHigh, _orbLow;

    // Recent bars for equal high/low detection
    private readonly List<Bar> _recentBars = new();

    // Dynamically discovered equal levels
    private readonly List<(decimal Level, SweepType Type)> _equalLevels = new();

    // Active sweeps for the current session
    public List<SweepEvent> ActiveSweeps { get; } = new();

    public bool AnyBullSweep => ActiveSweeps.Any(s => s.Direction == SweepDirection.Bull);
    public bool AnyBearSweep => ActiveSweeps.Any(s => s.Direction == SweepDirection.Bear);
    public SweepEvent? LastSweep => ActiveSweeps.Count > 0 ? ActiveSweeps[^1] : null;

    public SweepDetector(ModuleConfig cfg)
    {
        _cfg = cfg;
    }

    /// <summary>Update module parameters for a new session config.</summary>
    public void Reconfigure(ModuleConfig cfg) => _cfg = cfg;

    public void SetLevels(
        decimal pdh, decimal pdl,
        decimal pwh, decimal pwl,
        decimal pmh, decimal pml,
        decimal sessionHigh, decimal sessionLow,
        decimal orbHigh, decimal orbLow)
    {
        _pdh = pdh; _pdl = pdl;
        _pwh = pwh; _pwl = pwl;
        _pmh = pmh; _pml = pml;
        _sessionHigh = sessionHigh; _sessionLow = sessionLow;
        _orbHigh = orbHigh; _orbLow = orbLow;
    }

    public void OnBar(Bar bar, DateTime tradingDate)
    {
        // Detect equal highs/lows from recent bars before evaluating sweeps
        DetectEqualLevels(bar);

        // Build the list of levels to check
        var bearLevels = new (decimal Level, SweepType Type)[]
        {
            (_pdh, SweepType.PDH),
            (_pwh, SweepType.PWH),
            (_pmh, SweepType.PMH),
            (_sessionHigh, SweepType.SessionHigh),
            (_orbHigh, SweepType.OrbHigh),
        };

        var bullLevels = new (decimal Level, SweepType Type)[]
        {
            (_pdl, SweepType.PDL),
            (_pwl, SweepType.PWL),
            (_pml, SweepType.PML),
            (_sessionLow, SweepType.SessionLow),
            (_orbLow, SweepType.OrbLow),
        };

        decimal upperWick = bar.High - Math.Max(bar.Open, bar.Close);
        decimal lowerWick = Math.Min(bar.Open, bar.Close) - bar.Low;

        // Bearish sweeps: price pokes above a level then closes below
        foreach (var (level, type) in bearLevels)
        {
            if (level <= 0) continue;
            if (bar.High > level + _cfg.MinTickPenetration
                && bar.Close < level
                && upperWick >= _cfg.MinBodyReject)
            {
                ActiveSweeps.Add(new SweepEvent(level, type, SweepDirection.Bear, bar.Time));
            }
        }

        // Bullish sweeps: price pokes below a level then closes above
        foreach (var (level, type) in bullLevels)
        {
            if (level <= 0) continue;
            if (bar.Low < level - _cfg.MinTickPenetration
                && bar.Close > level
                && lowerWick >= _cfg.MinBodyReject)
            {
                ActiveSweeps.Add(new SweepEvent(level, type, SweepDirection.Bull, bar.Time));
            }
        }

        // Equal-level sweeps
        foreach (var (level, type) in _equalLevels)
        {
            if (type == SweepType.EqualHigh)
            {
                if (bar.High > level + _cfg.MinTickPenetration
                    && bar.Close < level
                    && upperWick >= _cfg.MinBodyReject)
                {
                    ActiveSweeps.Add(new SweepEvent(level, type, SweepDirection.Bear, bar.Time));
                }
            }
            else // EqualLow
            {
                if (bar.Low < level - _cfg.MinTickPenetration
                    && bar.Close > level
                    && lowerWick >= _cfg.MinBodyReject)
                {
                    ActiveSweeps.Add(new SweepEvent(level, type, SweepDirection.Bull, bar.Time));
                }
            }
        }

        // Maintain recent bars (keep last 5)
        _recentBars.Add(bar);
        if (_recentBars.Count > 5)
            _recentBars.RemoveAt(0);
    }

    public void OnTick(decimal price, DateTime utcTime)
    {
        // Sweeps are evaluated on bar close only
    }

    public void NewSession(DateTime tradingDate)
    {
        ActiveSweeps.Clear();
        _recentBars.Clear();
        _equalLevels.Clear();
    }

    private void DetectEqualLevels(Bar currentBar)
    {
        if (_recentBars.Count == 0) return;

        foreach (var recent in _recentBars)
        {
            // Equal highs
            if (Math.Abs(recent.High - currentBar.High) <= _cfg.EqualLevelTolerance)
            {
                decimal level = (recent.High + currentBar.High) / 2m;
                if (!_equalLevels.Any(e => e.Type == SweepType.EqualHigh
                    && Math.Abs(e.Level - level) <= _cfg.EqualLevelTolerance))
                {
                    _equalLevels.Add((level, SweepType.EqualHigh));
                }
            }

            // Equal lows
            if (Math.Abs(recent.Low - currentBar.Low) <= _cfg.EqualLevelTolerance)
            {
                decimal level = (recent.Low + currentBar.Low) / 2m;
                if (!_equalLevels.Any(e => e.Type == SweepType.EqualLow
                    && Math.Abs(e.Level - level) <= _cfg.EqualLevelTolerance))
                {
                    _equalLevels.Add((level, SweepType.EqualLow));
                }
            }
        }
    }
}
