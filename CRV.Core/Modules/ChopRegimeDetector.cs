using CRV.Core.Indicators;
using CRV.Core.Models;

namespace CRV.Core.Modules;

/// <summary>
/// Detects low-quality / range-bound regimes where ORB breakouts statistically fail.
/// Direct port of CRV_ChopRegime_Detector.pine.
///
/// Vote-based: each enabled sub-filter casts a "chop" vote when its condition fires;
/// regime is flagged when total votes meet <see cref="ChopRegimeConfig.MinVotesForChop"/>.
///
/// Filters consume signals from existing engine indicators via constructor injection:
///   - ATR (range compression)
///   - VWAP — current value, with internal slope ring buffer (flat VWAP)
///   - ORB — high/low/open/close (compression + weak drive)
///   - VolumeSma — owned internally, rolls per bar (low volume)
/// </summary>
public sealed class ChopRegimeDetector : IEngineModule
{
    private readonly OrbCalculator       _orb;
    private readonly VwapModel           _vwap;
    private readonly AtrIndicator        _atr;
    private readonly VolumeSmaIndicator  _volSma;
    private readonly List<decimal>       _vwapHistory = new();   // size capped at SlopeLookbackBars + 1

    private ChopRegimeConfig _config;

    public ChopRegimeResult LastResult { get; private set; }

    public ChopRegimeDetector(
        ChopRegimeConfig cfg,
        OrbCalculator    orb,
        VwapModel        vwap,
        AtrIndicator     atr)
    {
        _config = (cfg ?? throw new ArgumentNullException(nameof(cfg))).Normalize();
        _orb    = orb  ?? throw new ArgumentNullException(nameof(orb));
        _vwap   = vwap ?? throw new ArgumentNullException(nameof(vwap));
        _atr    = atr  ?? throw new ArgumentNullException(nameof(atr));
        _volSma = new VolumeSmaIndicator(_config.F4_VolumeMaLength);
        LastResult = ChopRegimeResult.Pending(DateTime.MinValue);
    }

    /// <summary>Update module parameters for a new session config.</summary>
    public void Reconfigure(ChopRegimeConfig cfg)
    {
        _config = (cfg ?? throw new ArgumentNullException(nameof(cfg))).Normalize();
    }

    public void OnBar(Bar bar, DateTime tradingDate)
    {
        // Evaluate first using prior-bar volume average, THEN update so the
        // current bar contributes to subsequent evaluations.
        LastResult = Evaluate(bar);

        if (_config.F4_LowVolumeEnabled)
            _volSma.Update(bar.Volume);

        // Track VWAP slope over a rolling window of the last N+1 values.
        if (_config.F2_FlatVwapEnabled && _vwap.Vwap > 0)
        {
            _vwapHistory.Add(_vwap.Vwap);
            int cap = _config.F2_SlopeLookbackBars + 1;
            if (_vwapHistory.Count > cap)
                _vwapHistory.RemoveAt(0);
        }
    }

    public void OnTick(decimal price, DateTime utcTime)
    {
        // No-op: chop regime is a bar-close concept.
    }

    public void NewSession(DateTime tradingDate)
    {
        _volSma.Reset();
        _vwapHistory.Clear();
        LastResult = ChopRegimeResult.Pending(DateTime.MinValue);
    }

    private ChopRegimeResult Evaluate(Bar bar)
    {
        if (!_config.Enabled)
            return ChopRegimeResult.Pending(bar.Time);

        var f1 = EvaluateRangeCompression();
        var f2 = EvaluateFlatVwap(bar);
        var f3 = EvaluateWeakDrive();
        var f4 = EvaluateLowVolume(bar);

        var votes =
            (f1.Voted ? 1 : 0) +
            (f2.Voted ? 1 : 0) +
            (f3.Voted ? 1 : 0) +
            (f4.Voted ? 1 : 0);

        var enabledCount =
            (_config.F1_RangeCompressionEnabled ? 1 : 0) +
            (_config.F2_FlatVwapEnabled         ? 1 : 0) +
            (_config.F3_WeakDriveEnabled        ? 1 : 0) +
            (_config.F4_LowVolumeEnabled        ? 1 : 0);

        var votesRequired = Math.Min(_config.MinVotesForChop, Math.Max(enabledCount, 1));
        var isChop        = enabledCount > 0 && votes >= votesRequired;

        return new ChopRegimeResult(
            BarTime:            bar.Time,
            IsChop:             isChop,
            Votes:              votes,
            VotesRequired:      votesRequired,
            EnabledFilterCount: enabledCount,
            RangeCompression:   f1,
            FlatVwap:           f2,
            WeakDrive:          f3,
            LowVolume:          f4);
    }

    // ── Filter 1: Range Compression (OR / ATR < threshold) ────────────
    private ChopFilterDiagnostic EvaluateRangeCompression()
    {
        const string name = "RangeCompression";
        var threshold = _config.F1_CompressionRatio;

        if (!_config.F1_RangeCompressionEnabled || !_orb.IsSet)
            return new(name, Active: false, Voted: false, Value: 0m, Threshold: threshold);

        var orRange = _orb.OrbRange;
        var atr     = _atr.HasValue ? _atr.Value : 0m;

        if (orRange <= 0 || atr <= 0)
            return new(name, Active: false, Voted: false, Value: 0m, Threshold: threshold);

        var ratio = orRange / atr;
        var voted = ratio < threshold;
        return new(name, Active: true, Voted: voted, Value: ratio, Threshold: threshold);
    }

    // ── Filter 2: Flat VWAP Slope (|Δvwap over N bars| / price < threshold%) ──
    private ChopFilterDiagnostic EvaluateFlatVwap(Bar bar)
    {
        const string name = "FlatVwap";
        var threshold = _config.F2_FlatSlopeThresholdPct;

        if (!_config.F2_FlatVwapEnabled)
            return new(name, Active: false, Voted: false, Value: 0m, Threshold: threshold);

        // Need at least N+1 samples (N bars ago + current) to compute slope.
        var required = _config.F2_SlopeLookbackBars + 1;
        if (_vwapHistory.Count < required || bar.Close <= 0 || _vwap.Vwap <= 0)
            return new(name, Active: false, Voted: false, Value: 0m, Threshold: threshold);

        var prior   = _vwapHistory[0];
        var current = _vwap.Vwap;
        var slopePct = Math.Abs(current - prior) / bar.Close * 100m;
        var voted    = slopePct < threshold;
        return new(name, Active: true, Voted: voted, Value: slopePct, Threshold: threshold);
    }

    // ── Filter 3: Weak Opening Drive (|OR open→close| / OR height < threshold) ──
    private ChopFilterDiagnostic EvaluateWeakDrive()
    {
        const string name = "WeakDrive";
        var threshold = _config.F3_MinDriveRatio;

        if (!_config.F3_WeakDriveEnabled || !_orb.IsSet)
            return new(name, Active: false, Voted: false, Value: 0m, Threshold: threshold);

        var orRange = _orb.OrbRange;
        if (orRange <= 0 || _orb.OrbOpen <= 0 || _orb.OrbClose <= 0)
            return new(name, Active: false, Voted: false, Value: 0m, Threshold: threshold);

        var netMove    = Math.Abs(_orb.OrbClose - _orb.OrbOpen);
        var driveRatio = netMove / orRange;
        var voted      = driveRatio < threshold;
        return new(name, Active: true, Voted: voted, Value: driveRatio, Threshold: threshold);
    }

    // ── Filter 4: Low Volume (current bar volume / SMA(volume) < threshold) ──
    private ChopFilterDiagnostic EvaluateLowVolume(Bar bar)
    {
        const string name = "LowVolume";
        var threshold = _config.F4_MinVolumeRatio;

        if (!_config.F4_LowVolumeEnabled || !_volSma.IsReady || _volSma.Value <= 0)
            return new(name, Active: false, Voted: false, Value: 0m, Threshold: threshold);

        var ratio = bar.Volume / _volSma.Value;
        var voted = ratio < threshold;
        return new(name, Active: true, Voted: voted, Value: ratio, Threshold: threshold);
    }
}
