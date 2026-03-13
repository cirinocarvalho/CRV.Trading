using CRV.Core.Indicators;
using CRV.Core.Models;

namespace CRV.Core.Modules;

public enum VwapState
{
    ExtendedBear = -2,
    AcceptBear   = -1,
    Neutral      =  0,
    AcceptBull   =  1,
    ExtendedBull =  2
}

public class VwapModel : IEngineModule
{
    private readonly VwapIndicator _vwap = new();
    private readonly ModuleConfig _cfg;
    private readonly List<decimal> _deviations = new();

    // VWAP value and bands
    public decimal Vwap   => _vwap.Value;
    public decimal Upper1 { get; private set; }
    public decimal Upper2 { get; private set; }
    public decimal Lower1 { get; private set; }
    public decimal Lower2 { get; private set; }

    // State classification
    public VwapState State { get; private set; } = VwapState.Neutral;

    // Cross signals
    public bool BullVWAPReclaim { get; private set; }
    public bool BearVWAPReject  { get; private set; }

    // Reversion signals
    public bool VWAPReversionLong  { get; private set; }
    public bool VWAPReversionShort { get; private set; }

    // Pullback continuation
    public bool BullVWAPPullback { get; private set; }
    public bool BearVWAPPullback { get; private set; }

    // Set externally
    public bool TrendDayBull { get; set; }
    public bool TrendDayBear { get; set; }

    private decimal _prevClose;
    private bool _hasPrevClose;

    public VwapModel(ModuleConfig cfg)
    {
        _cfg = cfg;
    }

    public void OnBar(Bar bar, DateTime tradingDate)
    {
        _vwap.Update(bar, tradingDate);

        if (!_vwap.IsReady) return;

        // Compute deviation
        decimal hlc3 = bar.HLC3;
        decimal dev  = hlc3 - _vwap.Value;
        _deviations.Add(dev * dev);

        // Keep rolling window
        if (_deviations.Count > _cfg.VwapDevPeriod)
            _deviations.RemoveAt(0);

        // Standard deviation of price from VWAP
        decimal variance = _deviations.Average();
        decimal stdDev   = (decimal)Math.Sqrt((double)variance);

        Upper1 = _vwap.Value + stdDev;
        Upper2 = _vwap.Value + 2m * stdDev;
        Lower1 = _vwap.Value - stdDev;
        Lower2 = _vwap.Value - 2m * stdDev;

        // Reset signals each bar
        BullVWAPReclaim    = false;
        BearVWAPReject     = false;
        VWAPReversionLong  = false;
        VWAPReversionShort = false;
        BullVWAPPullback   = false;
        BearVWAPPullback   = false;

        // Cross signals: close crosses VWAP
        if (_hasPrevClose)
        {
            if (_prevClose < _vwap.Value && bar.Close >= _vwap.Value)
                BullVWAPReclaim = true;
            if (_prevClose > _vwap.Value && bar.Close <= _vwap.Value)
                BearVWAPReject = true;
        }

        // Reversion signals
        bool bullishBar = bar.Close > bar.Open;
        bool bearishBar = bar.Close < bar.Open;

        if (bar.Close <= Lower2 && bullishBar)
            VWAPReversionLong = true;
        if (bar.Close >= Upper2 && bearishBar)
            VWAPReversionShort = true;

        // Pullback continuation
        if (TrendDayBull && bar.Low >= _vwap.Value && bar.Low <= Upper1)
            BullVWAPPullback = true;
        if (TrendDayBear && bar.High <= _vwap.Value && bar.High >= Lower1)
            BearVWAPPullback = true;

        // State classification
        State = ClassifyState(bar);

        _prevClose   = bar.Close;
        _hasPrevClose = true;
    }

    public void OnTick(decimal price, DateTime utcTime)
    {
        // No-op: VWAP needs volume, only updates on bar close
    }

    public void NewSession(DateTime tradingDate)
    {
        _vwap.NewSession(tradingDate);
        _deviations.Clear();

        Upper1 = 0;
        Upper2 = 0;
        Lower1 = 0;
        Lower2 = 0;

        State = VwapState.Neutral;

        BullVWAPReclaim    = false;
        BearVWAPReject     = false;
        VWAPReversionLong  = false;
        VWAPReversionShort = false;
        BullVWAPPullback   = false;
        BearVWAPPullback   = false;

        _prevClose    = 0;
        _hasPrevClose = false;
    }

    private VwapState ClassifyState(Bar bar)
    {
        if (!_vwap.IsReady) return VwapState.Neutral;

        decimal vwap = _vwap.Value;

        if (bar.Close >= Upper2)
            return VwapState.ExtendedBull;
        if (bar.Close > vwap && bar.Low >= vwap)
            return VwapState.AcceptBull;
        if (bar.Close <= Lower2)
            return VwapState.ExtendedBear;
        if (bar.Close < vwap && bar.High <= vwap)
            return VwapState.AcceptBear;

        return VwapState.Neutral;
    }
}
