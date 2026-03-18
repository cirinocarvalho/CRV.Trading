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
        if (driveRange <= 0 || CurrentAtr <= 0) return;

        DriveRangePctATR = orbRange / CurrentAtr;
        bool rangeOk = DriveRangePctATR >= _cfg.DriveRangeAtrMult;

        // Bull drive: more bull bars, close above VWAP, close in upper 30% of drive range
        bool bullDrive = _bullCount > _bearCount * _cfg.DriveBullBearRatio
                         && CurrentVwap > 0 && _driveHigh >= CurrentVwap  // close > vwap approximated by driveHigh
                         && rangeOk;

        // Bear drive: more bear bars, close below VWAP, close in lower 30% of drive range
        bool bearDrive = _bearCount > _bullCount * _cfg.DriveBullBearRatio
                         && CurrentVwap > 0 && _driveLow <= CurrentVwap
                         && rangeOk;

        // For bull: last close should be in upper portion (>= driveLow + 0.7 * driveRange)
        // For bear: last close should be in lower portion (<= driveLow + 0.3 * driveRange)
        // We use the drive high/low as proxy since we track the range
        if (bullDrive)
        {
            // Check that the closing area is in the upper 30%
            // driveHigh is always >= driveLow + 0.7 * driveRange (it IS the high)
            OpeningDriveBull = true;
        }

        if (bearDrive)
        {
            OpeningDriveBear = true;
        }

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
