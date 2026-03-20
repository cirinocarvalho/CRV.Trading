using CRV.Core.Models;

namespace CRV.Core.Modules;

/// <summary>
/// Tracks false breakout conditions on two range sources:
/// OrbTracker (ORB high/low) and SessionRangeTracker (prior session high/low).
/// </summary>
public class FalseBreakoutDetector : IEngineModule
{
    public RangeBreakoutTracker OrbTracker          { get; }
    public RangeBreakoutTracker SessionRangeTracker  { get; }

    public bool IsCompoundFakeout =>
        OrbTracker.IsActivated && SessionRangeTracker.IsActivated &&
        OrbTracker.BreakoutDirection == SessionRangeTracker.BreakoutDirection;

    public FalseBreakoutDetector(ModuleConfig cfg)
    {
        int tfMin = Math.Max(1, cfg.ExecutionTFMinutes);
        OrbTracker = new RangeBreakoutTracker(
            maxBarsAllowed: Math.Max(1, cfg.FBMaxTimeOutsideMinutesOrb / tfMin),
            maxPenetrationPct: cfg.FBMaxPenetrationPctOrb,
            minRejectionBodyPct: cfg.FBMinRejectionBodyPct,
            maxTrendDayScore: cfg.FBMaxTrendDayScore);
        SessionRangeTracker = new RangeBreakoutTracker(
            maxBarsAllowed: Math.Max(1, cfg.FBMaxTimeOutsideMinutesSR / tfMin),
            maxPenetrationPct: cfg.FBMaxPenetrationPctSR,
            minRejectionBodyPct: cfg.FBMinRejectionBodyPct,
            maxTrendDayScore: cfg.FBMaxTrendDayScore);
    }

    public void Reconfigure(ModuleConfig cfg)
    {
        int tfMin = Math.Max(1, cfg.ExecutionTFMinutes);
        OrbTracker.UpdateConfig(
            Math.Max(1, cfg.FBMaxTimeOutsideMinutesOrb / tfMin),
            cfg.FBMaxPenetrationPctOrb, cfg.FBMinRejectionBodyPct, cfg.FBMaxTrendDayScore);
        SessionRangeTracker.UpdateConfig(
            Math.Max(1, cfg.FBMaxTimeOutsideMinutesSR / tfMin),
            cfg.FBMaxPenetrationPctSR, cfg.FBMinRejectionBodyPct, cfg.FBMaxTrendDayScore);
    }

    /// <summary>Snapshot prior session range at session boundary.</summary>
    public void OnSessionStart(decimal priorSessionHigh, decimal priorSessionLow)
    {
        SessionRangeTracker.SetReferenceRange(priorSessionHigh, priorSessionLow);
    }

    public void OnBar(Bar bar, DateTime tradingDate,
        decimal orbHigh, decimal orbLow, bool orbFormed,
        decimal vwap, int trendBullScore, int trendBearScore)
    {
        if (orbFormed)
            OrbTracker.OnBar(bar, orbHigh, orbLow, orbHigh - orbLow, vwap, trendBullScore, trendBearScore);

        if (SessionRangeTracker.HasReferenceRange)
            SessionRangeTracker.OnBar(bar,
                SessionRangeTracker.RangeHigh, SessionRangeTracker.RangeLow,
                SessionRangeTracker.RangeHigh - SessionRangeTracker.RangeLow,
                vwap, trendBullScore, trendBearScore);
    }

    // IEngineModule — simplified interface (engine calls the richer OnBar overload)
    void IEngineModule.OnBar(Bar bar, DateTime tradingDate) { }

    public void OnTick(decimal price, DateTime utcTime)
    {
        OrbTracker.OnTick(price);
        SessionRangeTracker.OnTick(price);
    }

    public void NewSession(DateTime tradingDate)
    {
        OrbTracker.Reset();
        SessionRangeTracker.Reset();
    }
}

/// <summary>
/// Tracks a single range source for false breakout detection.
/// Monitors: breakout → bar counting → rejection → activation.
/// </summary>
public class RangeBreakoutTracker
{
    private int     _maxBarsAllowed;
    private decimal _maxPenetrationPct;
    private decimal _minRejectionBodyPct;
    private int     _maxTrendDayScore;

    // Reference range (set externally for session range; set via OnBar params for ORB)
    public decimal RangeHigh          { get; private set; }
    public decimal RangeLow           { get; private set; }
    public bool    HasReferenceRange  { get; private set; }

    // Breakout state
    public bool       BreakoutActive     { get; private set; }
    public Direction? BreakoutDirection  { get; private set; }
    public int        BarsInBreakout     { get; private set; }
    public decimal    SweepHigh          { get; private set; }
    public decimal    SweepLow           { get; private set; }

    // Rejection / activation
    public bool    IsActivated        { get; private set; }
    public decimal RejectionBarHigh   { get; private set; }
    public decimal RejectionBarLow    { get; private set; }
    public decimal PenetrationDepth   { get; private set; }

    public RangeBreakoutTracker(int maxBarsAllowed, decimal maxPenetrationPct,
        decimal minRejectionBodyPct, int maxTrendDayScore)
    {
        _maxBarsAllowed      = maxBarsAllowed;
        _maxPenetrationPct   = maxPenetrationPct;
        _minRejectionBodyPct = minRejectionBodyPct;
        _maxTrendDayScore    = maxTrendDayScore;
    }

    public void UpdateConfig(int maxBars, decimal maxPen, decimal minBody, int maxTrend)
    {
        _maxBarsAllowed      = maxBars;
        _maxPenetrationPct   = maxPen;
        _minRejectionBodyPct = minBody;
        _maxTrendDayScore    = maxTrend;
    }

    public void SetReferenceRange(decimal high, decimal low)
    {
        RangeHigh = high;
        RangeLow  = low;
        HasReferenceRange = high > low && low > 0;
    }

    public void OnBar(Bar bar, decimal rangeHigh, decimal rangeLow, decimal rangeSize,
        decimal vwap, int trendBullScore, int trendBearScore)
    {
        if (rangeSize <= 0) return;
        if (IsActivated) return; // already activated, engine must consume and reset

        if (!BreakoutActive)
        {
            // Check for new breakout
            if (bar.Close > rangeHigh)
            {
                BreakoutActive    = true;
                BreakoutDirection = Direction.Long; // broke above
                BarsInBreakout    = 1;
                SweepHigh         = bar.High;
                SweepLow          = bar.Low;
                PenetrationDepth  = (bar.High - rangeHigh) / rangeSize;
            }
            else if (bar.Close < rangeLow)
            {
                BreakoutActive    = true;
                BreakoutDirection = Direction.Short; // broke below
                BarsInBreakout    = 1;
                SweepHigh         = bar.High;
                SweepLow          = bar.Low;
                PenetrationDepth  = (rangeLow - bar.Low) / rangeSize;
            }
            return;
        }

        // Breakout active — update
        BarsInBreakout++;
        if (bar.High > SweepHigh) SweepHigh = bar.High;
        if (bar.Low  < SweepLow)  SweepLow  = bar.Low;

        // Check expiry
        if (BarsInBreakout > _maxBarsAllowed)
        {
            ResetBreakout();
            return;
        }

        // Check rejection: price closed back inside range
        bool closedInside = bar.Close <= rangeHigh && bar.Close >= rangeLow;
        if (!closedInside) return;

        // Quality filters
        decimal bodySize  = Math.Abs(bar.Close - bar.Open);
        decimal totalSize = bar.High - bar.Low;
        if (totalSize <= 0) return;
        if (bodySize / totalSize < _minRejectionBodyPct) return;
        if (PenetrationDepth > _maxPenetrationPct) return;

        // VWAP filter: VWAP must be on opposite side of breakout
        bool vwapOk = BreakoutDirection == Direction.Long
            ? vwap < rangeHigh  // broke above, VWAP should be inside/below
            : vwap > rangeLow;  // broke below, VWAP should be inside/above
        if (!vwapOk) return;

        // TrendDay filter: opposing direction score must be below threshold
        int opposingScore = BreakoutDirection == Direction.Long ? trendBullScore : trendBearScore;
        if (opposingScore > _maxTrendDayScore) return;

        // All filters pass
        IsActivated      = true;
        RejectionBarHigh = bar.High;
        RejectionBarLow  = bar.Low;
    }

    public void OnTick(decimal price)
    {
        if (!BreakoutActive || IsActivated) return;
        if (price > SweepHigh) SweepHigh = price;
        if (price < SweepLow)  SweepLow  = price;
    }

    public void Reset()
    {
        ResetBreakout();
        IsActivated       = false;
        RejectionBarHigh  = 0;
        RejectionBarLow   = 0;
        HasReferenceRange = false;
        RangeHigh = 0;
        RangeLow  = 0;
    }

    /// <summary>Clear activation flag after engine consumes it for arming.</summary>
    public void ClearActivation()
    {
        IsActivated      = false;
        RejectionBarHigh = 0;
        RejectionBarLow  = 0;
        ResetBreakout();
    }

    private void ResetBreakout()
    {
        BreakoutActive    = false;
        BreakoutDirection = null;
        BarsInBreakout    = 0;
        SweepHigh         = 0;
        SweepLow          = 0;
        PenetrationDepth  = 0;
    }
}
