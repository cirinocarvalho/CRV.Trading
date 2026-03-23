using CRV.Core.Models;

namespace CRV.Core.Modules;

public class OpeningDriveDetector : IEngineModule
{
    private ModuleConfig _cfg;

    private int _bullCount;
    private int _bearCount;
    private decimal _driveHigh;
    private decimal _driveLow;

    // Set externally before FreezeAtOrbClose
    public decimal CurrentAtr { get; set; }
    public decimal CurrentVwap { get; set; }

    // Outputs
    public bool OpeningDriveBull { get; private set; }
    public bool OpeningDriveBear { get; private set; }
    public bool OpeningDriveConfirmed { get; private set; }
    public decimal DriveRangePctATR { get; private set; }
    public decimal DrivePullbackPct { get; private set; }

    public OpeningDriveDetector(ModuleConfig cfg)
    {
        _cfg = cfg;
    }

    /// <summary>Update module parameters for a new session config.</summary>
    public void Reconfigure(ModuleConfig cfg) => _cfg = cfg;

    /// <summary>Call for each bar during the ORB window to accumulate bull/bear counts and track drive range.</summary>
    public void AccumulateOrbBar(Bar bar)
    {
        if (bar.Close >= bar.Open)
            _bullCount++;
        else
            _bearCount++;

        if (_driveHigh == 0 && _driveLow == 0)
        {
            _driveHigh = bar.High;
            _driveLow = bar.Low;
        }
        else
        {
            if (bar.High > _driveHigh) _driveHigh = bar.High;
            if (bar.Low < _driveLow) _driveLow = bar.Low;
        }
    }

    /// <summary>Evaluate the opening drive once the ORB window closes.</summary>
    public void FreezeAtOrbClose(decimal orbRange)
    {
        decimal driveRange = _driveHigh - _driveLow;

        if (driveRange <= 0 || CurrentAtr <= 0)
            return;

        DriveRangePctATR = orbRange / CurrentAtr;
        bool rangeOk = DriveRangePctATR >= _cfg.DriveRangeAtrMult;

        // Adaptive ratio: with large TF bars (e.g. 15-min) and a 30-min ORB window,
        // only 2–3 bars accumulate.  Requiring bullCount > bearCount * 2 means ALL
        // bars must be the same direction — too restrictive.  When ≤ 4 bars, relax
        // to a simple majority (> 50%) so a 2-1 or 3-1 split can still trigger drive.
        int totalBars = _bullCount + _bearCount;
        int effectiveRatio = totalBars <= 4 ? 1 : _cfg.DriveBullBearRatio;

        // Bull drive: more bull bars, close above VWAP, range meets ATR threshold
        bool bullRatio = _bullCount > _bearCount * effectiveRatio;
        bool bullVwap = CurrentVwap > 0 && _driveHigh >= CurrentVwap;
        bool bullDrive = bullRatio && bullVwap && rangeOk;

        // Bear drive: more bear bars, close below VWAP, range meets ATR threshold
        bool bearRatio = _bearCount > _bullCount * effectiveRatio;
        bool bearVwap = CurrentVwap > 0 && _driveLow <= CurrentVwap;
        bool bearDrive = bearRatio && bearVwap && rangeOk;

        if (bullDrive)
            OpeningDriveBull = true;

        if (bearDrive)
            OpeningDriveBear = true;

        OpeningDriveConfirmed = OpeningDriveBull || OpeningDriveBear;
    }

    /// <summary>Update pullback percentage as price retraces from the drive extreme.</summary>
    public void UpdatePullback(decimal currentClose)
    {
        if (!OpeningDriveConfirmed) return;

        decimal driveRange = _driveHigh - _driveLow;
        if (driveRange <= 0) return;

        if (OpeningDriveBull)
        {
            decimal retracement = _driveHigh - currentClose;
            DrivePullbackPct = retracement / driveRange;
        }
        else if (OpeningDriveBear)
        {
            decimal retracement = currentClose - _driveLow;
            DrivePullbackPct = retracement / driveRange;
        }
    }

    public bool IsPullbackValid => OpeningDriveConfirmed && DrivePullbackPct < _cfg.MaxDrivePullback;

    /// <summary>Restore cached opening drive state after engine restart.</summary>
    public void Restore(bool bull, bool bear, decimal driveRangePctATR)
    {
        OpeningDriveBull = bull;
        OpeningDriveBear = bear;
        OpeningDriveConfirmed = bull || bear;
        DriveRangePctATR = driveRangePctATR;
    }

    public void OnBar(Bar bar, DateTime tradingDate) { }
    public void OnTick(decimal price, DateTime utcTime) { }

    public void NewSession(DateTime tradingDate)
    {
        _bullCount = 0;
        _bearCount = 0;
        _driveHigh = 0;
        _driveLow = 0;
        CurrentAtr = 0;
        CurrentVwap = 0;
        OpeningDriveBull = false;
        OpeningDriveBear = false;
        OpeningDriveConfirmed = false;
        DriveRangePctATR = 0;
        DrivePullbackPct = 0;
    }
}
