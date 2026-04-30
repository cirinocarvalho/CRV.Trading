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
    EntrySignal? Entry);

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

    // ── BrokerEventHandler (optional — null in backtest) ──────────
    private readonly BrokerEventHandler? _brokerHandler;

    // ── Indicators (shared across all strategies in this group) ──
    private readonly AtrIndicator _atr;
    private readonly VwapIndicator _vwap;
    private readonly Ema21Indicator _ema21 = new();
    private readonly OrbCalculator _orb;  // primary ORB — feeds modules (FB/Drive/Trend) and snapshots
    /// <summary>Per-window ORB calculators. Keyed by (start, end) so multiple setups in this group
    /// with different ORB windows each get their own calculation. The primary <see cref="_orb"/>
    /// is also stored here under the global window key. Strategies that don't override a window
    /// share the primary calculator.</summary>
    private readonly Dictionary<(TimeOnly Start, TimeOnly End), OrbCalculator> _orbs = new();
    /// <summary>Maps strategy.Id → its (start, end) window key into <see cref="_orbs"/>.</summary>
    private readonly Dictionary<string, (TimeOnly Start, TimeOnly End)> _stratWindow = new();
    private readonly TimeZoneInfo _tz;
    private decimal _lastBarClose;
    private decimal _orbAtrRatio;
    private bool _orbJustFormed;  // set when ORB forms this bar, cleared after read

    // ── Bar history (for dashboard chart) ──────────────────────
    private readonly BarRingBuffer _barBuffer = new(200);
    private Bar? _currentBar;  // latest bar (including unconfirmed) for live candle

    // ── Modules ──────────────────────────────────────────────────
    private readonly SessionEngine _sessionEngine;
    private readonly SweepDetector _sweepDetector;
    private readonly VwapModel _vwapModel;
    private readonly OpeningDriveDetector _openingDrive;
    private readonly TrendDayFilter _trendDay;
    private readonly FalseBreakoutDetector _falseBreakout;

    // ── Session tracking ─────────────────────────────────────────
    private DateTime _lastDate = DateTime.MinValue;
    /// <summary>
    /// User-configured active session id ("Asia"/"London"/"NY"), set by <see cref="ComposableEngine"/>
    /// from the SessionManager. When non-empty, takes precedence over the SessionEngine module's
    /// hardcoded clock for <see cref="IsEnabledForCurrentSession"/>. Empty = fall back to module clock
    /// (live-mode bootstrap before any session reconfigure has happened).
    /// </summary>
    private string _activeSessionId = "";

    /// <summary>The group key (e.g. "NQ" for NQ/MNQ).</summary>
    public string TickerKey => _tickerKey;

    /// <summary>Read-only view of registered strategies.</summary>
    public IReadOnlyList<ISetupStrategy> Strategies => _strategies;

    public TickerGroup(string tickerKey, StrategyConfig cfg, BrokerEventHandler? brokerHandler = null)
    {
        _tickerKey = tickerKey;
        _cfg = cfg;
        _brokerHandler = brokerHandler;

        _atr = new AtrIndicator(14);
        _vwap = new VwapIndicator();
        _orb = new OrbCalculator(cfg.OrbStart, cfg.OrbEnd, cfg.Timezone, cfg.ExecutionTFMinutes);
        _orbs[(cfg.OrbStart, cfg.OrbEnd)] = _orb;
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

    public void AddStrategy(ISetupStrategy strategy)
    {
        _strategies.Add(strategy);
        var key = (strategy.OrbStart, strategy.OrbEnd);
        _stratWindow[strategy.Id] = key;
        if (!_orbs.ContainsKey(key))
            _orbs[key] = new OrbCalculator(key.Item1, key.Item2, _cfg.Timezone, _cfg.ExecutionTFMinutes);
    }

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

            // ORB tracks all bars (including unconfirmed). Update every per-window calculator;
            // the primary _orb is included in _orbs and is what feeds modules below.
            foreach (var orb in _orbs.Values)
                orb.Update(bar, tradingDate);

            // Track current bar for live chart candle (before confirmed check)
            _currentBar = bar;

            // Indicators and modules only update on confirmed bars
            if (!bar.IsConfirmed) return;

            // Store confirmed bars in history buffer for chart
            _barBuffer.Add(bar);

            _atr.Update(bar);
            _ema21.Update(bar.Close);
            _vwap.Update(bar, tradingDate);

            // Store VWAP and EMA21 values alongside the last added bar
            _barBuffer.SetVwap(_vwap.Value);
            _barBuffer.SetEma21(_ema21.Value);
            if (bar.Close > 0) _lastBarClose = bar.Close;

            // Always keep OpeningDrive's ATR/VWAP current — they are used by
            // FreezeAtOrbClose and must reflect the latest values even if ATR
            // was not ready during earlier ORB-window bars.
            _openingDrive.CurrentAtr = _atr.Value;
            _openingDrive.CurrentVwap = _vwap.Value;

            // Accumulate bull/bear counts and drive range during ORB window
            if (!_orb.IsSet)
            {
                _openingDrive.AccumulateOrbBar(bar);
            }

            // Freeze ORB/ATR ratio when ORB first forms
            if (_orb.IsSet && _orbAtrRatio == 0 && _atr.IsReady && _atr.Value > 0)
            {
                _orbAtrRatio = _orb.OrbRange / _atr.Value;
                _orbJustFormed = true;
                // Evaluate opening drive now that the ORB window has closed
                _openingDrive.FreezeAtOrbClose(_orb.OrbRange);
            }

            // Module updates
            _sessionEngine.CurrentAtr = _atr.Value;
            _sessionEngine.CurrentVwap = _vwap.Value;
            _sessionEngine.OnBar(bar, tradingDate);

            _vwapModel.OnBar(bar, tradingDate);

            _sweepDetector.SetLevels(
                pdh: _sessionEngine.PDH, pdl: _sessionEngine.PDL,
                pwh: _sessionEngine.PWH, pwl: _sessionEngine.PWL,
                pmh: _sessionEngine.PMH, pml: _sessionEngine.PML,
                sessionHigh: _sessionEngine.SessionHigh,
                sessionLow: _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : 0,
                orbHigh: _orb.OrbHigh, orbLow: _orb.OrbLow);
            _sweepDetector.OnBar(bar, tradingDate);

            // FalseBreakout BEFORE TrendDay update (matches OrbStrategyEngine order —
            // fakeout filter uses previous bar's trend scores, not current bar's)
            _falseBreakout.OnBar(bar, tradingDate,
                _orb.OrbHigh, _orb.OrbLow, _orb.IsSet,
                _vwap.Value, _trendDay.BullScore, _trendDay.BearScore);

            // Trend day scoring — only on confirmed bars (matches OrbStrategyEngine guard)
            if (_orb.IsSet && bar.IsConfirmed)
            {
                _trendDay.Update(
                    _openingDrive.OpeningDriveBull, _openingDrive.OpeningDriveBear,
                    bar.Close, _vwap.Value,
                    _orb.OrbHigh, _orb.OrbLow, _orb.OrbMid,
                    _sessionEngine.SessionHigh,
                    _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : bar.Low,
                    _orb.OrbMid);
            }

            // Build state snapshots once for all strategies
            var indState = BuildIndicatorState();
            var modState = BuildModuleState();

            // Reset cross-setup coordination flag
            _enteredThisBar = false;

            // Per-setup cutoff: compute local time once for all strategies
            var localTime = TimeOnly.FromDateTime(local);
            var barLocalTime = localTime;

            // Dispatch to each strategy, tracking whether fakeout signals get consumed.
            // OrbTracker: clear activation when Setup C newly arms (one-shot, allows new fakeout detection).
            // SessionRangeTracker: do NOT clear — session fakeout persists for the whole session,
            // allowing Setup D to re-arm after BE exits from the same session boundary signal.
            foreach (var strategy in _strategies)
            {
                // Session filter — skip if this setup isn't enabled for the current session.
                // Don't Disarm() — just skip evaluation so dashboard shows IDLE, not CUTOFF.
                if (!IsEnabledForCurrentSession(strategy))
                {
                    if (strategy.IsActive)
                    {
                        var px = bar.Close > 0 ? bar.Close : _lastBarClose;
                        strategy.ForceExit(px, DateTime.UtcNow, ExitReason.SessionEnd);
                        if (_brokerHandler != null)
                            await _brokerHandler.ExitGroupAsync(strategy.Id, px, ExitReason.SessionEnd);
                    }
                    continue;
                }

                // Per-setup cutoff — skip evaluation if past this setup's cutoff time
                // (matches OrbStrategyEngine behavior: no new arms/entries past cutoff)
                if (IsPastCutoff(strategy, localTime))
                {
                    // Force-exit active trades past cutoff (CloseAtRthClose behavior)
                    if (strategy.IsActive)
                    {
                        var px = bar.Close > 0 ? bar.Close : _lastBarClose;
                        strategy.ForceExit(px, DateTime.UtcNow, ExitReason.SessionEnd);
                        if (_brokerHandler != null)
                            await _brokerHandler.ExitGroupAsync(strategy.Id, px, ExitReason.SessionEnd);
                    }
                    // Cancel pending entries that haven't filled yet
                    else if (_brokerHandler != null)
                    {
                        var pendingGroup = _brokerHandler.GetGroupState(strategy.Id);
                        if (pendingGroup?.Status == GroupOrderStatus.Pending)
                            await _brokerHandler.ExitGroupAsync(strategy.Id, 0, ExitReason.SessionEnd, bar.Time);
                    }
                    // Disarm stale armed/waiting states so dashboard shows IDLE
                    strategy.Disarm();
                    continue;
                }
                else
                {
                    // Clear stale cutoff flag from a previous session's ForceExit
                    // so dashboard doesn't show CUTOFF when the strategy is actually active
                    strategy.ResetCutoff();
                }

                bool orbActivated = _falseBreakout.OrbTracker.IsActivated;
                bool wasArmed     = strategy.IsArmed || strategy.IsActive;

                // Per-strategy ORB: pull from this strategy's window calculator and apply
                // the time-based guard against THIS strategy's OrbEnd (not the global one).
                var stratOrbState = BuildOrbStateForStrategy(strategy);
                if (stratOrbState.IsSet && barLocalTime < strategy.OrbEnd)
                    stratOrbState = stratOrbState with { IsSet = false };

                strategy.OnBar(bar, stratOrbState, indState, modState);

                bool nowArmed = strategy.IsArmed || strategy.IsActive;
                if (orbActivated && !wasArmed && nowArmed && strategy.SetupId == SetupId.C)
                    _falseBreakout.OrbTracker.ClearActivation();

                if (strategy.PendingEntry != null)
                {
                    bool isLong = strategy.PendingEntry.Direction == Direction.Long;

                    // EMA21 directional filter: long only above EMA, short only below
                    if (IsEmaFiltered(strategy, isLong, indState.Ema21))
                    {
                        strategy.RevertEntry();
                    }
                    // Opposing position guard: block entry if another strategy has an active trade
                    // in the opposite direction on the same instrument
                    else if (HasOpposingPosition(strategy, isLong))
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

            var indState = BuildIndicatorState();
            var modState = BuildModuleState();

            // Per-setup cutoff for tick path
            var tickLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, _tz);
            var tickLocalTime = TimeOnly.FromDateTime(tickLocal);

            foreach (var strategy in _strategies)
            {
                if (!IsEnabledForCurrentSession(strategy))
                {
                    if (strategy.IsActive)
                    {
                        strategy.ForceExit(price, utc, ExitReason.SessionEnd);
                        if (_brokerHandler != null)
                            await _brokerHandler.ExitGroupAsync(strategy.Id, price, ExitReason.SessionEnd, utc);
                    }
                    continue;
                }

                if (IsPastCutoff(strategy, tickLocalTime))
                {
                    if (strategy.IsActive)
                    {
                        strategy.ForceExit(price, utc, ExitReason.SessionEnd);
                        if (_brokerHandler != null)
                            await _brokerHandler.ExitGroupAsync(strategy.Id, price, ExitReason.SessionEnd, utc);
                    }
                    strategy.Disarm();
                    continue;
                }
                else
                {
                    strategy.ResetCutoff();
                }

                // Per-strategy ORB with the strategy's own time-based guard
                var stratOrbState = BuildOrbStateForStrategy(strategy);
                if (stratOrbState.IsSet && tickLocalTime < strategy.OrbEnd)
                    stratOrbState = stratOrbState with { IsSet = false };

                strategy.OnTick(price, utc, stratOrbState, indState, modState);

                if (strategy.PendingEntry != null)
                {
                    bool isLong = strategy.PendingEntry.Direction == Direction.Long;

                    // EMA21 directional filter: long only above EMA, short only below
                    if (IsEmaFiltered(strategy, isLong, indState.Ema21))
                    {
                        strategy.RevertEntry();
                    }
                    // Opposing position guard: block entry if another strategy has an active trade
                    // in the opposite direction on the same instrument
                    else if (HasOpposingPosition(strategy, isLong))
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

    // ── Signal collection ────────────────────────────────────────

    /// <summary>
    /// Collect pending signals from all strategies, then clear them.
    /// Returns one <see cref="StrategySignals"/> per strategy (even if null).
    /// </summary>
    public List<StrategySignals> CollectAndClearSignals()
    {
        var result = new List<StrategySignals>(_strategies.Count);
        foreach (var s in _strategies)
        {
            result.Add(new StrategySignals(s, s.PendingEntry));
            s.ClearPendingSignals();
        }
        return result;
    }

    // ── Session boundary ─────────────────────────────────────────

    /// <summary>
    /// Snapshot the prior session's high/low into the FalseBreakoutDetector
    /// so SessionRangeTracker can detect false breakouts of that range.
    /// Must be called BEFORE Reset() so the prior session's data is still available.
    /// </summary>
    public void OnSessionBoundary(SessionId newSession)
    {
        if (newSession == SessionId.Asia)
            _falseBreakout.OnSessionStart(_sessionEngine.PDH, _sessionEngine.PDL);
        else if (newSession == SessionId.London)
            _falseBreakout.OnSessionStart(_sessionEngine.AsiaHigh, _sessionEngine.AsiaLow);
        else if (newSession == SessionId.NY)
            _falseBreakout.OnSessionStart(_sessionEngine.LondonHigh, _sessionEngine.LondonLow);
    }

    // ── Reset ────────────────────────────────────────────────────

    /// <summary>
    /// Reset indicators, ORB, and strategies for a new session.
    /// Does NOT reset _lastDate — date-change detection in ProcessBarAsync
    /// handles NewSession() calls naturally when the trading date actually changes.
    /// Resetting _lastDate here would cause a spurious NewSession() on the first bar
    /// after a session transition, wiping FalseBreakoutDetector's reference range
    /// that was just set by OnSessionBoundary.
    /// </summary>
    public void Reset()
    {
        // NOTE: Do NOT reset _atr — ATR is a rolling 14-bar indicator that should
        // persist across sessions.  Resetting it at every session boundary forces
        // a 70-minute warmup (14 × 5-min bars) during which ATR reads 0.
        _vwap.Reset();
        foreach (var orb in _orbs.Values) orb.Reset();
        // NOTE: Do NOT clear _barBuffer — chart history should persist across sessions.
        // The ring buffer naturally rolls off old bars as new ones arrive.
        _currentBar = null;
        _lastBarClose = 0;
        _orbAtrRatio = 0;
        _enteredThisBar = false;

        // Reset session-scoped modules. When sessions switch on the same trading date
        // (e.g. London→NY), the date-change guard in ProcessBarAsync does NOT fire,
        // so these modules would retain stale state from the prior session.
        // Matches OrbStrategyEngine.Reconfigure() which calls NewSession() on each.
        // NOTE: Do NOT reset _sessionEngine or _falseBreakout here — _sessionEngine
        // tracks daily levels (PDH/PDL, Asia/London highs) across sessions, and
        // _falseBreakout's reference range was just set by OnSessionBoundary().
        _openingDrive.NewSession(_lastDate);
        _trendDay.NewSession(_lastDate);
        _sweepDetector.NewSession(_lastDate);

        foreach (var s in _strategies)
            s.Reset();
    }

    // ── State snapshot accessors ─────────────────────────────────

    public OrbState GetOrbState() => BuildOrbState();
    public IndicatorState GetIndicatorState() => BuildIndicatorState();
    public ModuleState GetModuleState() => BuildModuleState();

    /// <summary>Return bar history for the REST chart endpoint (thread-safe).</summary>
    public List<Bar> GetBarHistory() => _barBuffer.ToList();

    /// <summary>Return bar history with VWAP values for the REST chart endpoint.</summary>
    public List<(Bar Bar, decimal Vwap)> GetBarHistoryWithVwap() => _barBuffer.ToListWithVwap();

    /// <summary>Return bar history with VWAP and EMA21 values.</summary>
    public List<(Bar Bar, decimal Vwap, decimal Ema21)> GetBarHistoryWithIndicators() => _barBuffer.ToListWithIndicators();

    /// <summary>
    /// Build a complete snapshot of this group's module/indicator state
    /// for the dashboard ticker-group selector.
    /// </summary>
    public TickerGroupSnapshot BuildGroupSnapshot(decimal livePrice = 0)
    {
        // Current bar for live candle (unconfirmed or latest confirmed)
        var bar = _currentBar;
        long barTime = 0;
        decimal barOpen = bar?.Open ?? 0, barHigh = bar?.High ?? 0;
        decimal barLow = bar?.Low ?? 0, barClose = bar?.Close ?? 0;

        if (bar != null)
        {
            barTime = new DateTimeOffset(bar.Time, TimeSpan.Zero).ToUnixTimeSeconds();
            // Incorporate live price into the current candle (updates between confirmed bars)
            if (livePrice > 0)
            {
                barClose = livePrice;
                if (livePrice > barHigh) barHigh = livePrice;
                if (livePrice < barLow)  barLow  = livePrice;
            }
        }

        return new()
        {
        GroupKey = _tickerKey,
        // Current bar
        BarTime  = barTime,
        BarOpen  = barOpen,
        BarHigh  = barHigh,
        BarLow   = barLow,
        BarClose = barClose,
        BarVolume = bar?.Volume ?? 0,
        // Session
        CurrentSession = _sessionEngine.CurrentSession.ToString(),
        SessionHigh = _sessionEngine.SessionHigh,
        SessionLow = _sessionEngine.SessionLow,
        PrevDayHigh = _sessionEngine.PDH,
        PrevDayLow = _sessionEngine.PDL,
        AsiaCompressed = _sessionEngine.AsiaCompressed,
        // VWAP
        Vwap = _vwap.Value,
        VwapUpper1 = _vwapModel.Upper1,
        VwapUpper2 = _vwapModel.Upper2,
        VwapLower1 = _vwapModel.Lower1,
        VwapLower2 = _vwapModel.Lower2,
        VwapState = (int)_vwapModel.State,
        // Opening Drive
        OpeningDriveBull = _openingDrive.OpeningDriveBull,
        OpeningDriveBear = _openingDrive.OpeningDriveBear,
        // Trend Day
        TrendScoreBull = _trendDay.BullScore,
        TrendScoreBear = _trendDay.BearScore,
        // ORB
        OrbHigh = _orb.OrbHigh,
        OrbLow = _orb.OrbLow,
        OrbMid = _orb.OrbMid,
        OrbRange = _orb.OrbRange,
        OrbBullClose = _orb.OrbBullClose,
        OrbBearClose = _orb.OrbBearClose,
        OrbAtrRatio = _orbAtrRatio,
        OrbFormed = _orb.IsSet,
        // ATR & EMA21
        Atr = _atr.Value,
        Ema21 = _ema21.Value,
        // False Breakout
        FBOrbBreakoutActive = _falseBreakout.OrbTracker.BreakoutActive,
        FBSessionBreakoutActive = _falseBreakout.SessionRangeTracker.BreakoutActive,
        FBOrbBarsInBreakout = _falseBreakout.OrbTracker.BarsInBreakout,
        FBSessionBarsInBreakout = _falseBreakout.SessionRangeTracker.BarsInBreakout,
        FBOrbPenetrationDepth = _falseBreakout.OrbTracker.PenetrationDepth,
        FBSessionPenetrationDepth = _falseBreakout.SessionRangeTracker.PenetrationDepth,
        FBOrbActivated = _falseBreakout.OrbTracker.IsActivated,
        FBSessionActivated = _falseBreakout.SessionRangeTracker.IsActivated,
        IsCompoundFakeout = _falseBreakout.IsCompoundFakeout,
        // Signal Strength
        DriveScore = ComputeDriveScore(),
        SweepScore = ComputeSweepScore(),
        VwapDevScore = ComputeVwapDevScore(),
        SignalStrength = ComputeSignalStrength(),
        };
    }

    // ── ORB cache support ────────────────────────────────────────

    /// <summary>Restore ORB state from cache after engine restart.</summary>
    public void RestoreOrb(decimal orbHigh, decimal orbLow, decimal closeRelPct, DateTime tradingDate, decimal orbAtrRatio,
        bool driveBull = false, bool driveBear = false, decimal driveRangePctATR = 0)
    {
        _orb.Restore(orbHigh, orbLow, closeRelPct, tradingDate);
        _orbAtrRatio = orbAtrRatio;
        _openingDrive.Restore(driveBull, driveBear, driveRangePctATR);

        // Seed _lastDate so the first backfill bar doesn't trigger a false
        // date-change that would call NewSession() and wipe the restored state.
        _lastDate = tradingDate;
    }

    /// <summary>Seed ORB floor from cache when restarting inside ORB window.</summary>
    public void SeedOrbFloor(decimal orbHigh, decimal orbLow, DateTime tradingDate)
    {
        _orb.SeedFloor(orbHigh, orbLow, tradingDate);
        _lastDate = tradingDate;
    }

    /// <summary>Seed module history from daily bars.</summary>
    public void SeedModuleHistory(IReadOnlyList<Bar> dailyBars)
    {
        _sessionEngine.SeedHistory(dailyBars);
    }

    /// <summary>
    /// Returns true (once) when ORB has just formed this bar.
    /// Calling this clears the flag so it fires exactly once per ORB formation.
    /// </summary>
    public bool ConsumeOrbFormed(out OrbStateCache? cache)
    {
        if (!_orbJustFormed) { cache = null; return false; }
        _orbJustFormed = false;
        // Use the first strategy's ticker (canonical) as the cache symbol
        var ticker = _strategies.Count > 0 ? _strategies[0].Ticker?.TrimStart('/') : _tickerKey;
        cache = new OrbStateCache
        {
            TradingDate = _lastDate,
            Symbol      = ticker ?? _tickerKey,
            OrbHigh     = _orb.OrbHigh,
            OrbLow      = _orb.OrbLow,
            CloseRelPct = _orb.CloseRelPct,
            OrbAtrRatio = _orbAtrRatio,
            OpeningDriveBull  = _openingDrive.OpeningDriveBull,
            OpeningDriveBear  = _openingDrive.OpeningDriveBear,
            DriveRangePctATR  = _openingDrive.DriveRangePctATR,
            SavedAtUtc  = DateTime.UtcNow,
        };
        return true;
    }

    /// <summary>Reconfigure the primary ORB window for a new session. Strategies that override
    /// the window keep their own calculators untouched. The primary calculator is re-keyed in
    /// <see cref="_orbs"/> so subsequent bar updates feed the new window. Per-strategy window
    /// mappings are refreshed from each strategy's current OrbStart/OrbEnd so non-overriding
    /// setups follow the new session, while overriding setups stay pinned.</summary>
    public void ReconfigureOrb(TimeOnly orbStart, TimeOnly orbEnd)
    {
        _orb.Reconfigure(orbStart, orbEnd);
        _cfg.OrbStart = orbStart;
        _cfg.OrbEnd = orbEnd;
        RefreshStrategyWindows();
    }

    /// <summary>Re-derive the (strategy → window) map and (window → calculator) map from each
    /// registered strategy's current OrbStart/OrbEnd. Call after strategies have been Reconfigure'd
    /// so window changes propagate to bar dispatch. Reuses existing calculators where possible.</summary>
    public void RefreshStrategyWindows()
    {
        var newOrbs = new Dictionary<(TimeOnly, TimeOnly), OrbCalculator>
        {
            [(_cfg.OrbStart, _cfg.OrbEnd)] = _orb,
        };
        _stratWindow.Clear();
        foreach (var s in _strategies)
        {
            var key = (s.OrbStart, s.OrbEnd);
            _stratWindow[s.Id] = key;
            if (!newOrbs.ContainsKey(key))
            {
                newOrbs[key] = _orbs.TryGetValue(key, out var existing)
                    ? existing
                    : new OrbCalculator(key.Item1, key.Item2, _cfg.Timezone, _cfg.ExecutionTFMinutes);
            }
        }
        _orbs.Clear();
        foreach (var kv in newOrbs) _orbs[kv.Key] = kv.Value;
    }

    // ── Ticker grouping helper ───────────────────────────────────

    /// <summary>
    /// Maps a ticker symbol to its group key.
    /// NQ/MNQ share the same price feed, ES/MES share the same price feed, etc.
    /// Strips leading '/' so broker-format tickers (Schwab "/NQH26") map correctly.
    /// </summary>
    public static string GetGroupKey(string ticker)
    {
        var t = ticker.TrimStart('/');
        if (t.StartsWith("NQ") || t.StartsWith("MNQ")) return "NQ";
        if (t.StartsWith("ES") || t.StartsWith("MES")) return "ES";
        if (t.StartsWith("GC") || t.StartsWith("MGC")) return "GC";
        if (t.StartsWith("CL") || t.StartsWith("MCL")) return "CL";
        if (t.StartsWith("YM") || t.StartsWith("MYM")) return "YM";
        if (t.StartsWith("RTY") || t.StartsWith("M2K")) return "RTY";
        if (t.StartsWith("BTC") || t.StartsWith("MBT")) return "BTC";
        return t;
    }

    // ── Private helpers ──────────────────────────────────────────

    /// <summary>Returns true when <paramref name="strategy"/> has the EMA filter enabled and
    /// the pending entry direction is blocked by the EMA21 level (long requires price above EMA,
    /// short requires price below EMA). When the EMA is not yet ready (zero) the filter passes.</summary>
    private static bool IsEmaFiltered(ISetupStrategy strategy, bool isLong, decimal ema21)
    {
        if (!strategy.UseEmaFilter || ema21 == 0 || strategy.PendingEntry == null) return false;
        decimal entryPrice = strategy.PendingEntry.Entry;
        return isLong ? entryPrice <= ema21 : entryPrice >= ema21;
    }

    /// <summary>
    /// Returns true if any OTHER active strategy in this group holds a trade in the
    /// direction opposite to <paramref name="isLong"/>. Used to prevent opposing
    /// positions on the same instrument.
    /// </summary>
    private bool HasOpposingPosition(ISetupStrategy entering, bool isLong)
    {
        if (_brokerHandler == null) return false;
        foreach (var s in _strategies)
        {
            if (s == entering || !s.InTrade) continue;
            var group = _brokerHandler.GetGroupState(s.Id);
            if (group == null) group = _brokerHandler.GetGroupState(s.SetupId.ToString());
            if (group != null && (group.Direction == Direction.Long) != isLong)
                return true;
        }
        return false;
    }

    /// <summary>Check if the current time is past this strategy's cutoff for the active session.</summary>
    private bool IsPastCutoff(ISetupStrategy strategy, TimeOnly localTime)
    {
        var sessionName = SessionTypeName(_sessionEngine.CurrentSession);
        var (h, m) = strategy.GetCutoffForSession(sessionName);
        var cutoff = new TimeOnly(h, m);

        // Overnight session handling (e.g. Asia 19:00→02:00, cutoff 01:30):
        // If cutoff is in the early morning (< 12:00) and current time is in the evening (>= 18:00),
        // we haven't crossed midnight yet — NOT past cutoff.
        // If cutoff is in the early morning and current time is also early morning, normal compare.
        if (cutoff.Hour < 12 && localTime.Hour >= 18)
            return false; // before midnight, cutoff is after midnight → not past yet

        return localTime >= cutoff;
    }

    /// <summary>Check if a strategy is enabled for the current session.</summary>
    /// <summary>
    /// Set the engine-supplied active session id ("Asia"/"London"/"NY", or "" to clear).
    /// Called by <see cref="ComposableEngine"/> on Reconfigure / SetIdle so strategy
    /// session-slot gating follows the user's session config rather than the module's
    /// hardcoded clock (which would otherwise block 06:00 NY ORBs until 09:30).
    /// </summary>
    public void SetActiveSessionId(string sessionId) => _activeSessionId = sessionId ?? "";

    private bool IsEnabledForCurrentSession(ISetupStrategy strategy)
    {
        // Prefer the user-configured active session (from SessionManager). Fall back to
        // the SessionEngine module clock only when no session has been wired in (e.g.
        // live-mode bootstrap before the first Reconfigure).
        var sessionName = !string.IsNullOrEmpty(_activeSessionId)
            ? _activeSessionId
            : SessionTypeName(_sessionEngine.CurrentSession);
        return strategy.IsEnabledForSession(sessionName);
    }

    private static string SessionTypeName(CRV.Core.Modules.SessionType st) => st switch
    {
        CRV.Core.Modules.SessionType.Asia => "Asia",
        CRV.Core.Modules.SessionType.London => "London",
        CRV.Core.Modules.SessionType.NYOpen or CRV.Core.Modules.SessionType.Midday
            or CRV.Core.Modules.SessionType.PowerHour => "NY",
        _ => ""  // PreMarket/gap between sessions — no session active, block all strategies
    };

    // ── Signal-strength helpers (ported from OrbStrategyEngine) ───

    private decimal ComputeDriveScore()
    {
        if (!_openingDrive.OpeningDriveBull && !_openingDrive.OpeningDriveBear) return 0m;
        var threshold = _cfg.DriveRangeAtrMult;
        if (threshold <= 0) return 0m;
        var raw = _openingDrive.DriveRangePctATR / threshold * 3m;
        return Math.Clamp(Math.Round(raw, 1), 0m, 5m);
    }

    private decimal ComputeSweepScore()
    {
        var count = _sweepDetector.ActiveSweeps.Count;
        if (count == 0) return 0m;
        return Math.Min(5m, count * 2.5m);
    }

    private decimal ComputeVwapDevScore()
    {
        return Math.Abs((int)_vwapModel.State) * 2.5m;
    }

    private decimal ComputeSignalStrength()
    {
        var d = ComputeDriveScore();
        var s = ComputeSweepScore();
        var v = ComputeVwapDevScore();
        return Math.Round((d + s + v) / 3m, 1);
    }

    private OrbState BuildOrbState() => new(
        _orb.OrbHigh, _orb.OrbLow, _orb.OrbMid, _orb.OrbRange,
        _orb.IsSet, _orb.OrbBullClose, _orb.OrbBearClose, _orbAtrRatio);

    /// <summary>OrbState for a strategy's declared window. Falls back to the primary ORB
    /// if the strategy was not registered (defensive — should not happen in practice).</summary>
    public OrbState GetOrbStateForStrategy(string strategyId)
    {
        if (_stratWindow.TryGetValue(strategyId, out var key) && _orbs.TryGetValue(key, out var orb))
            return new OrbState(orb.OrbHigh, orb.OrbLow, orb.OrbMid, orb.OrbRange,
                orb.IsSet, orb.OrbBullClose, orb.OrbBearClose, _orbAtrRatio);
        return BuildOrbState();
    }

    private OrbState BuildOrbStateForStrategy(ISetupStrategy strategy)
        => GetOrbStateForStrategy(strategy.Id);

    private IndicatorState BuildIndicatorState() => new(
        _atr.Value,
        _vwap.Value,
        _vwapModel.Upper1, _vwapModel.Lower1,
        _vwapModel.Upper2, _vwapModel.Lower2,
        _lastBarClose,
        _ema21.Value);

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
