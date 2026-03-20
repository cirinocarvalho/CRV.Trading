using CRV.Core.Indicators;
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

/// <summary>
/// Signals collected from a single strategy after bar/tick processing.
/// Returned to ComposableEngine for routing to broker/sink.
/// </summary>
public record StrategySignals(
    ISetupStrategy Strategy,
    EntrySignal? Entry,
    ExitSignal? Exit,
    PartialSignal? Partial,
    BESignal? BE);

/// <summary>
/// Per-instrument group that owns shared indicators, modules, and a list of
/// <see cref="ISetupStrategy"/> instances. Coordinates bar/tick dispatch and
/// cross-setup entry suppression via <c>_enteredThisBar</c>.
/// </summary>
public class TickerGroup
{
    private readonly string _tickerKey;
    private readonly StrategyConfig _cfg;
    private readonly List<ISetupStrategy> _strategies = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _enteredThisBar;

    // ── Indicators (shared across all strategies in this group) ──
    private readonly AtrIndicator _atr;
    private readonly VwapIndicator _vwap;
    private readonly OrbCalculator _orb;
    private readonly TimeZoneInfo _tz;
    private decimal _lastBarClose;
    private decimal _orbAtrRatio;

    // ── Modules ──────────────────────────────────────────────────
    private readonly SessionEngine _sessionEngine;
    private readonly SweepDetector _sweepDetector;
    private readonly VwapModel _vwapModel;
    private readonly OpeningDriveDetector _openingDrive;
    private readonly TrendDayFilter _trendDay;
    private readonly FalseBreakoutDetector _falseBreakout;

    // ── Session tracking ─────────────────────────────────────────
    private DateTime _lastDate = DateTime.MinValue;

    /// <summary>The group key (e.g. "NQ" for NQ/MNQ).</summary>
    public string TickerKey => _tickerKey;

    /// <summary>Read-only view of registered strategies.</summary>
    public IReadOnlyList<ISetupStrategy> Strategies => _strategies;

    public TickerGroup(string tickerKey, StrategyConfig cfg)
    {
        _tickerKey = tickerKey;
        _cfg = cfg;

        _atr = new AtrIndicator(14);
        _vwap = new VwapIndicator();
        _orb = new OrbCalculator(cfg.OrbStart, cfg.OrbEnd, cfg.Timezone, cfg.ExecutionTFMinutes);
        _tz = FindTimeZone(cfg.Timezone);

        var modCfg = new ModuleConfig
        {
            TickSize = cfg.TickSize,
            PointValue = cfg.PointValue,
            Timezone = cfg.Timezone,
            MinTickPenetration = cfg.SweepMinPenetration,
            MinBodyReject = cfg.SweepMinBodyReject,
            EqualLevelTolerance = cfg.SweepEqualTolerance,
            ConfirmationBars = cfg.SweepConfirmBars,
            DriveRangeAtrMult = cfg.DriveRangeAtrMult,
            MaxDrivePullback = cfg.DriveMaxPullback,
            DriveBullBearRatio = cfg.DriveBullBearRatio,
            TrendDayThreshold = cfg.TrendDayThreshold,
            ShallowPullbackMax = cfg.ShallowPullbackMax,
            VwapDevPeriod = cfg.VwapDevPeriod,
            ExecutionTFMinutes         = cfg.ExecutionTFMinutes,
            FBMaxTimeOutsideMinutesOrb = cfg.FBMaxTimeOutsideMinutesOrb,
            FBMaxTimeOutsideMinutesSR  = cfg.FBMaxTimeOutsideMinutesSR,
            FBMaxPenetrationPctOrb     = cfg.FBMaxPenetrationPctOrb,
            FBMaxPenetrationPctSR      = cfg.FBMaxPenetrationPctSR,
            FBMinRejectionBodyPct      = cfg.FBMinRejectionBodyPct,
            FBMaxTrendDayScore         = cfg.FBMaxTrendDayScore,
        };

        _sessionEngine = new SessionEngine(modCfg);
        _sweepDetector = new SweepDetector(modCfg);
        _vwapModel = new VwapModel(modCfg);
        _openingDrive = new OpeningDriveDetector(modCfg);
        _trendDay = new TrendDayFilter(modCfg);
        _falseBreakout = new FalseBreakoutDetector(modCfg);
    }

    // ── Strategy registration ────────────────────────────────────

    public void AddStrategy(ISetupStrategy strategy) => _strategies.Add(strategy);

    // ── Bar processing ───────────────────────────────────────────

    /// <summary>
    /// Process a confirmed or unconfirmed bar:
    /// 1. Update ORB (always)
    /// 2. If confirmed: update ATR, VWAP, modules
    /// 3. Build state snapshots
    /// 4. Dispatch to each strategy
    /// </summary>
    public async Task ProcessBarAsync(Bar bar)
    {
        await _semaphore.WaitAsync();
        try
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, _tz);
            var tradingDate = _cfg.TradingDate(local);

            // New session detection
            if (tradingDate != _lastDate)
            {
                _lastDate = tradingDate;
                _vwap.NewSession(tradingDate);
                _sessionEngine.NewSession(tradingDate);
                _sweepDetector.NewSession(tradingDate);
                _vwapModel.NewSession(tradingDate);
                _openingDrive.NewSession(tradingDate);
                _trendDay.NewSession(tradingDate);
                _falseBreakout.NewSession(tradingDate);
                _orbAtrRatio = 0;
            }

            // ORB tracks all bars (including unconfirmed)
            _orb.Update(bar, tradingDate);

            // Indicators and modules only update on confirmed bars
            if (!bar.IsConfirmed) return;

            _atr.Update(bar);
            _vwap.Update(bar, tradingDate);
            if (bar.Close > 0) _lastBarClose = bar.Close;

            // Freeze ORB/ATR ratio when ORB first forms
            if (_orb.IsSet && _orbAtrRatio == 0 && _atr.IsReady && _atr.Value > 0)
                _orbAtrRatio = _orb.OrbRange / _atr.Value;

            // Module updates
            _sessionEngine.CurrentAtr = _atr.Value;
            _sessionEngine.CurrentVwap = _vwap.Value;
            _sessionEngine.OnBar(bar, tradingDate);

            if (!_orb.IsSet)
            {
                _openingDrive.CurrentAtr = _atr.Value;
                _openingDrive.CurrentVwap = _vwap.Value;
                _openingDrive.AccumulateOrbBar(bar);
            }

            _vwapModel.OnBar(bar, tradingDate);

            _sweepDetector.SetLevels(
                pdh: _sessionEngine.PDH, pdl: _sessionEngine.PDL,
                pwh: _sessionEngine.PWH, pwl: _sessionEngine.PWL,
                pmh: _sessionEngine.PMH, pml: _sessionEngine.PML,
                sessionHigh: _sessionEngine.SessionHigh,
                sessionLow: _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : 0,
                orbHigh: _orb.OrbHigh, orbLow: _orb.OrbLow);
            _sweepDetector.OnBar(bar, tradingDate);

            _falseBreakout.OnBar(bar, tradingDate,
                _orb.OrbHigh, _orb.OrbLow, _orb.IsSet,
                _vwap.Value, _trendDay.BullScore, _trendDay.BearScore);

            // Build state snapshots once for all strategies
            var orbState = BuildOrbState();
            var indState = BuildIndicatorState();
            var modState = BuildModuleState();

            // Reset cross-setup coordination flag
            _enteredThisBar = false;

            // Dispatch to each strategy
            foreach (var strategy in _strategies)
            {
                strategy.OnBar(bar, orbState, indState, modState);

                if (strategy.PendingEntry != null)
                {
                    bool isLong = strategy.PendingEntry.Direction == Direction.Long;

                    // Opposing position guard: block entry if another strategy has an active trade
                    // in the opposite direction on the same instrument
                    if (HasOpposingPosition(strategy, isLong))
                    {
                        strategy.RevertEntry();
                    }
                    // Cross-setup coordination: suppress entry if another already entered this bar
                    else if (!_cfg.AllowBothSameBar && _enteredThisBar)
                    {
                        strategy.RevertEntry();
                    }
                    else
                    {
                        _enteredThisBar = true;
                    }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Tick processing ──────────────────────────────────────────

    /// <summary>
    /// Dispatch a price tick to all strategies.
    /// </summary>
    public async Task ProcessTickAsync(decimal price, DateTime utc)
    {
        await _semaphore.WaitAsync();
        try
        {
            _sessionEngine.OnTick(price, utc);

            var orbState = BuildOrbState();
            var indState = BuildIndicatorState();
            var modState = BuildModuleState();

            foreach (var strategy in _strategies)
            {
                strategy.OnTick(price, utc, orbState, indState, modState);

                if (strategy.PendingEntry != null)
                {
                    bool isLong = strategy.PendingEntry.Direction == Direction.Long;

                    // Opposing position guard: block entry if another strategy has an active trade
                    // in the opposite direction on the same instrument
                    if (HasOpposingPosition(strategy, isLong))
                    {
                        strategy.RevertEntry();
                    }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Signal collection ────────────────────────────────────────

    /// <summary>
    /// Collect pending signals from all strategies, then clear them.
    /// Returns one <see cref="StrategySignals"/> per strategy (even if all null).
    /// </summary>
    public List<StrategySignals> CollectAndClearSignals()
    {
        var result = new List<StrategySignals>(_strategies.Count);
        foreach (var s in _strategies)
        {
            result.Add(new StrategySignals(s, s.PendingEntry, s.PendingExit, s.PendingPartial, s.PendingBE));
            s.ClearPendingSignals();
        }
        return result;
    }

    // ── Reset ────────────────────────────────────────────────────

    /// <summary>
    /// Reset all indicators, modules, and strategies for a new session.
    /// </summary>
    public void Reset()
    {
        _atr.Reset();
        _vwap.Reset();
        _orb.Reset();
        _lastBarClose = 0;
        _orbAtrRatio = 0;
        _lastDate = DateTime.MinValue;
        _enteredThisBar = false;

        foreach (var s in _strategies)
            s.Reset();
    }

    // ── State snapshot accessors ─────────────────────────────────

    public OrbState GetOrbState() => BuildOrbState();
    public IndicatorState GetIndicatorState() => BuildIndicatorState();
    public ModuleState GetModuleState() => BuildModuleState();

    // ── ORB cache support ────────────────────────────────────────

    /// <summary>Restore ORB state from cache after engine restart.</summary>
    public void RestoreOrb(decimal orbHigh, decimal orbLow, decimal closeRelPct, DateTime tradingDate, decimal orbAtrRatio)
    {
        _orb.Restore(orbHigh, orbLow, closeRelPct, tradingDate);
        _orbAtrRatio = orbAtrRatio;
    }

    /// <summary>Seed ORB floor from cache when restarting inside ORB window.</summary>
    public void SeedOrbFloor(decimal orbHigh, decimal orbLow)
    {
        _orb.SeedFloor(orbHigh, orbLow);
    }

    /// <summary>Seed module history from daily bars.</summary>
    public void SeedModuleHistory(IReadOnlyList<Bar> dailyBars)
    {
        _sessionEngine.SeedHistory(dailyBars);
    }

    /// <summary>Reconfigure ORB window for a new session.</summary>
    public void ReconfigureOrb(TimeOnly orbStart, TimeOnly orbEnd)
    {
        _orb.Reconfigure(orbStart, orbEnd);
    }

    // ── Ticker grouping helper ───────────────────────────────────

    /// <summary>
    /// Maps a ticker symbol to its group key.
    /// NQ/MNQ share the same price feed, ES/MES share the same price feed, etc.
    /// </summary>
    public static string GetGroupKey(string ticker)
    {
        if (ticker.StartsWith("NQ") || ticker.StartsWith("MNQ")) return "NQ";
        if (ticker.StartsWith("ES") || ticker.StartsWith("MES")) return "ES";
        return ticker;
    }

    // ── Private helpers ──────────────────────────────────────────

    /// <summary>
    /// Returns true if any OTHER active strategy in this group holds a trade in the
    /// direction opposite to <paramref name="isLong"/>. Used to prevent opposing
    /// positions on the same instrument.
    /// </summary>
    private bool HasOpposingPosition(ISetupStrategy entering, bool isLong)
    {
        foreach (var s in _strategies)
        {
            if (s == entering) continue;
            if (!s.IsActive) continue;
            // price doesn't matter for direction check — pass 0
            var trade = s.GetActiveTrade(0);
            if (trade != null && (trade.Direction == Direction.Long) != isLong)
                return true;
        }
        return false;
    }

    private OrbState BuildOrbState() => new(
        _orb.OrbHigh, _orb.OrbLow, _orb.OrbMid, _orb.OrbRange,
        _orb.IsSet, _orb.OrbBullClose, _orb.OrbBearClose, _orbAtrRatio);

    private IndicatorState BuildIndicatorState() => new(
        _atr.Value,
        _vwap.Value,
        _vwapModel.Upper1, _vwapModel.Lower1,
        _vwapModel.Upper2, _vwapModel.Lower2,
        _lastBarClose);

    private ModuleState BuildModuleState() => new(
        SessionHigh: _sessionEngine.SessionHigh,
        SessionLow: _sessionEngine.SessionLow,
        AsiaHigh: _sessionEngine.AsiaHigh,
        AsiaLow: _sessionEngine.AsiaLow,
        AsiaCompressed: _sessionEngine.AsiaCompressed,
        LondonHigh: _sessionEngine.LondonHigh,
        LondonLow: _sessionEngine.LondonLow,
        PDH: _sessionEngine.PDH,
        PDL: _sessionEngine.PDL,
        PWH: _sessionEngine.PWH,
        PWL: _sessionEngine.PWL,
        CurrentSession: _sessionEngine.CurrentSession,
        LondonSweptAsiaHigh: _sessionEngine.LondonSweptAsiaHigh,
        LondonSweptAsiaLow: _sessionEngine.LondonSweptAsiaLow,
        NYBullExpansion: _sessionEngine.NYBullExpansion,
        NYBearExpansion: _sessionEngine.NYBearExpansion,
        ActiveSweeps: _sweepDetector.ActiveSweeps,
        VwapState: (int)_vwapModel.State,
        BullVwapReclaim: _vwapModel.BullVWAPReclaim,
        BearVwapReject: _vwapModel.BearVWAPReject,
        IsBullDrive: _openingDrive.OpeningDriveBull,
        IsBearDrive: _openingDrive.OpeningDriveBear,
        TrendDayBullScore: _trendDay.BullScore,
        TrendDayBearScore: _trendDay.BearScore,
        TrendDayBull: _trendDay.TrendDayBull,
        TrendDayBear: _trendDay.TrendDayBear,
        OrbFakeoutBull: _falseBreakout.OrbTracker.IsActivated &&
                        _falseBreakout.OrbTracker.BreakoutDirection == Direction.Long,
        OrbFakeoutBear: _falseBreakout.OrbTracker.IsActivated &&
                        _falseBreakout.OrbTracker.BreakoutDirection == Direction.Short,
        FakeoutPenetration: _falseBreakout.OrbTracker.PenetrationDepth,
        SessionFakeoutBull: _falseBreakout.SessionRangeTracker.IsActivated &&
                            _falseBreakout.SessionRangeTracker.BreakoutDirection == Direction.Long,
        SessionFakeoutBear: _falseBreakout.SessionRangeTracker.IsActivated &&
                            _falseBreakout.SessionRangeTracker.BreakoutDirection == Direction.Short,
        SessionRangeHigh: _falseBreakout.SessionRangeTracker.RangeHigh,
        SessionRangeLow: _falseBreakout.SessionRangeTracker.RangeLow);

    private static TimeZoneInfo FindTimeZone(string tz)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch
        {
            var win = tz switch
            {
                "America/New_York" => "Eastern Standard Time",
                "America/Chicago" => "Central Standard Time",
                "America/Los_Angeles" => "Pacific Standard Time",
                "Europe/London" => "GMT Standard Time",
                _ => tz
            };
            return TimeZoneInfo.FindSystemTimeZoneById(win);
        }
    }
}
