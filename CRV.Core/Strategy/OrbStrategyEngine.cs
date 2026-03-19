using CRV.Core.Indicators;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Modules;
using Microsoft.Extensions.Logging;

namespace CRV.Core.Strategy;

/// <summary>
/// CRV ORB Execution Engine v9 — C# port of CRV_ORB_Engine_v9.pine
/// Processes bars one at a time. Used identically for live and backtest.
/// </summary>
public class OrbStrategyEngine
{
    private StrategyConfig     _cfg;
    private readonly IOrderExecutor     _executor;
    private readonly IStrategyEventSink _sink;
    private readonly ILastPriceProvider _prices;
    private readonly ILogger            _log;

    private readonly AtrIndicator  _atr;
    private readonly VwapIndicator _vwap;
    private readonly OrbCalculator _orb;
    private readonly TimeZoneInfo  _tz;

    // ── Modules ───────────────────────────────────────────────
    private readonly SessionEngine          _sessionEngine;
    private readonly SweepDetector          _sweepDetector;
    private readonly VwapModel              _vwapModel;
    private readonly OpeningDriveDetector   _openingDrive;
    private readonly TrendDayFilter         _trendDay;
    private readonly FalseBreakoutDetector  _falseBreakout;

    // ── Session state ─────────────────────────────────────────
    private DateTime _lastDate       = DateTime.MinValue;
    private bool     _pastCutoff     = false;
    private bool     _pastCutoffA    = false;
    private bool     _pastCutoffB    = false;
    private bool     _rthEnded       = false;
    private bool     _orbLoggedFormed = false;
    private bool     _orbJustFormed   = false;
    private bool     _firstLiveBar    = true;
    private int      _warmupCooldown  = 0;  // bars to skip entry after warmup (prevents instant trades on restart)
    private bool     _orbRestoredFromCache = false;  // true when ORB loaded from cache — skip backfill ORB derivation
    private decimal  _orbAtrRatio    = 0;
    private bool      _idle              = false;  // starts active for backward compat
    private bool      _sessionManagedMode = false; // true when SessionManager drives transitions
    private string    _activeSessionId   = "";
    public  string    ActiveSessionId    => _activeSessionId;
    private decimal  _todayPnl       = 0;
    private int      _todayWins      = 0;
    private int      _todayLosses    = 0;
    private decimal  _todayPeak      = 0;
    private decimal  _todayMaxDD     = 0;
    private decimal  _todayWinPnl    = 0;
    private decimal  _todayLossPnl   = 0;
    private int      _todayWinsA     = 0;
    private int      _todayLossesA   = 0;
    private decimal  _todayWinPnlA   = 0;
    private decimal  _todayLossPnlA  = 0;
    private int      _todayWinsB     = 0;
    private int      _todayLossesB   = 0;
    private decimal  _todayWinPnlB   = 0;
    private decimal  _todayLossPnlB  = 0;
    private bool     _ddBreached     = false;
    private bool     _enteredThisBar = false;
    private decimal  _lastBarClose   = 0;   // tracks close across warmup + live bars
    private DateTime _lastSnapshotUtc = DateTime.MinValue;  // throttle unconfirmed snapshots

    // ── Bar index (Pine bar_index equivalent — never resets) ──
    private int      _barIndex       = 0;

    // ── Manual force-exit flags (set from outside, e.g. dashboard button) ──
    private volatile bool _forceExitA = false;
    private volatile bool _forceExitB = false;

    // ── Setup A ───────────────────────────────────────────────
    private int      _stA          = 0;
    private decimal  _entA         = 0;
    private decimal  _stopA        = 0;
    private decimal  _tgtA         = 0;
    private decimal  _partialA     = 0;
    private decimal  _initStopA    = 0;
    private bool     _activeA      = false;
    private int      _tradeCountA  = 0;
    private bool     _partHitA     = false;
    private decimal  _pnlA         = 0;
    private decimal  _armEntryA    = 0;
    private DateTime _entryTimeA   = DateTime.MinValue;
    // Sticky exit signals (Pine: stickyTgtA, stickyStpA, exitBarA_idx)
    private bool    _stickyTgtA   = false;
    private bool    _stickyStpA   = false;
    private int     _exitBarIdxA  = -1;   // -1 = na
    private bool    _bullTradedA  = false; // prevents re-arming same side until price returns to ORB
    private bool    _bearTradedA  = false;
    private int     _ctsA         = 0;    // effective contracts for current trade (hi-vol adjusted)

    // ── Setup B ───────────────────────────────────────────────
    private int      _stB          = 0;
    private decimal  _entB         = 0;
    private decimal  _stopB        = 0;
    private decimal  _tgtB         = 0;
    private decimal  _partialB     = 0;
    private decimal  _initStopB    = 0;
    private bool     _activeB      = false;
    private int      _tradeCountB  = 0;
    private bool     _partHitB     = false;
    private decimal  _pnlB         = 0;
    private decimal  _armEntryB    = 0;
    private DateTime _entryTimeB   = DateTime.MinValue;
    // Sticky exit signals (Pine: stickyTgtB, stickyStpB, exitBarB_idx)
    private bool    _stickyTgtB   = false;
    private bool    _stickyStpB   = false;
    private int     _exitBarIdxB  = -1;   // -1 = na
    private bool    _bullTradedB  = false; // prevents re-arming same side after trade
    private bool    _bearTradedB  = false;
    private int     _ctsB         = 0;    // effective contracts for current trade (hi-vol adjusted)

    // ── Manual force-exit flags C/D ──────────────────────────────
    private volatile bool _forceExitC = false;
    private volatile bool _forceExitD = false;

    // ── Setup C (ORB False Breakout) ─────────────────────────────
    private int      _stC          = 0;
    private decimal  _entC         = 0;
    private decimal  _stopC        = 0;
    private decimal  _tgtC         = 0;
    private decimal  _partialC     = 0;
    private decimal  _initStopC    = 0;
    private bool     _activeC      = false;
    private int      _tradeCountC  = 0;
    private bool     _partHitC     = false;
    private decimal  _pnlC         = 0;
    private decimal  _armEntryC    = 0;
    private DateTime _entryTimeC   = DateTime.MinValue;
    private bool    _stickyTgtC   = false;
    private bool    _stickyStpC   = false;
    private int     _exitBarIdxC  = -1;
    private bool    _bullTradedC  = false;
    private bool    _bearTradedC  = false;
    private int     _ctsC         = 0;
    private bool    _pastCutoffC  = false;

    // ── Setup D (Session Range False Breakout) ───────────────────
    private int      _stD          = 0;
    private decimal  _entD         = 0;
    private decimal  _stopD        = 0;
    private decimal  _tgtD         = 0;
    private decimal  _partialD     = 0;
    private decimal  _initStopD    = 0;
    private bool     _activeD      = false;
    private int      _tradeCountD  = 0;
    private bool     _partHitD     = false;
    private decimal  _pnlD         = 0;
    private decimal  _armEntryD    = 0;
    private DateTime _entryTimeD   = DateTime.MinValue;
    private bool    _stickyTgtD   = false;
    private bool    _stickyStpD   = false;
    private int     _exitBarIdxD  = -1;
    private bool    _bullTradedD  = false;
    private bool    _bearTradedD  = false;
    private int     _ctsD         = 0;
    private bool    _pastCutoffD  = false;

    // ── Per-setup daily stats C/D ────────────────────────────────
    private int     _todayWinsC    = 0;
    private int     _todayLossesC  = 0;
    private decimal _todayWinPnlC  = 0;
    private decimal _todayLossPnlC = 0;
    private int     _todayWinsD    = 0;
    private int     _todayLossesD  = 0;
    private decimal _todayWinPnlD  = 0;
    private decimal _todayLossPnlD = 0;

    // Circular buffer — O(1) add, O(k) snapshot where k = items returned
    private const int AlertCapacity = 100;
    private readonly AlertEvent[] _alertRing = new AlertEvent[AlertCapacity];
    private int _alertHead  = 0;  // next write index
    private int _alertCount = 0;  // total items in ring

    public OrbStrategyEngine(
        StrategyConfig cfg, IOrderExecutor executor,
        IStrategyEventSink sink, ILastPriceProvider prices, ILogger log)
    {
        _cfg      = cfg;
        _executor = executor;
        _sink     = sink;
        _prices   = prices;
        _log      = log;
        _atr      = new AtrIndicator(14);
        _vwap     = new VwapIndicator();
        _orb      = new OrbCalculator(cfg.OrbStart, cfg.OrbEnd, cfg.Timezone, cfg.ExecutionTFMinutes, log);
        _tz       = GetTz(cfg.Timezone);

        var modCfg = new ModuleConfig
        {
            TickSize             = cfg.TickSize,
            PointValue           = cfg.PointValue,
            Timezone             = cfg.Timezone,
            MinTickPenetration   = cfg.SweepMinPenetration,
            MinBodyReject        = cfg.SweepMinBodyReject,
            EqualLevelTolerance  = cfg.SweepEqualTolerance,
            ConfirmationBars     = cfg.SweepConfirmBars,
            DriveRangeAtrMult    = cfg.DriveRangeAtrMult,
            MaxDrivePullback     = cfg.DriveMaxPullback,
            DriveBullBearRatio   = cfg.DriveBullBearRatio,
            TrendDayThreshold    = cfg.TrendDayThreshold,
            ShallowPullbackMax   = cfg.ShallowPullbackMax,
            VwapDevPeriod        = cfg.VwapDevPeriod
        };
        _sessionEngine   = new SessionEngine(modCfg);
        _sweepDetector   = new SweepDetector(modCfg);
        _vwapModel       = new VwapModel(modCfg);
        _openingDrive    = new OpeningDriveDetector(modCfg);
        _trendDay        = new TrendDayFilter(modCfg);
        _falseBreakout   = new FalseBreakoutDetector(cfg);
    }

    /// <summary>Force-exit Setup A immediately at the current market price.</summary>
    public async Task RequestForceExitA()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0) { _forceExitA = true; return; }  // fallback: next bar
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        await ForceExitA(bar);
        AddAlert("EXIT", SetupId.A, $"Force exit @ {px:F2}", "orange");
        await PublishSnapshot(bar);
    }

    /// <summary>Force-exit Setup B immediately at the current market price.</summary>
    public async Task RequestForceExitB()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0) { _forceExitB = true; return; }
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        await ForceExitB(bar);
        AddAlert("EXIT", SetupId.B, $"Force exit @ {px:F2}", "orange");
        await PublishSnapshot(bar);
    }

    /// <summary>Force-exit Setup C immediately at the current market price.</summary>
    public async Task RequestForceExitC()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0) { _forceExitC = true; return; }
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        await ForceExitC(bar);
        AddAlert("EXIT", SetupId.C, $"Force exit @ {px:F2}", "orange");
        await PublishSnapshot(bar);
    }

    /// <summary>Force-exit Setup D immediately at the current market price.</summary>
    public async Task RequestForceExitD()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0) { _forceExitD = true; return; }
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        await ForceExitD(bar);
        AddAlert("EXIT", SetupId.D, $"Force exit @ {px:F2}", "orange");
        await PublishSnapshot(bar);
    }

    /// <summary>Put engine into idle mode — all ProcessBar/ProcessPriceTick calls become no-ops.</summary>
    public void SetIdle()
    {
        _idle = true;
        _activeSessionId = "";
        _log.LogInformation("Engine set to idle");
    }

    /// <summary>
    /// Clear idle flag so warmup bars can flow through between sessions.
    /// Used by BacktestEngine to keep ATR/VWAP alive during inter-session gaps.
    /// </summary>
    public void ClearIdle() => _idle = false;

    /// <summary>
    /// Swap config for a new session. Resets session-scoped state (ORB, trade counts,
    /// cutoff flags, arm states) but preserves daily-scoped state (ATR, VWAP, daily levels).
    /// </summary>
    public void Reconfigure(StrategyConfig cfg, SessionId sessionId)
    {
        _cfg = cfg;
        _activeSessionId = sessionId.ToString();

        // Update ORB calculator window times and reset ORB state
        _orb.Reconfigure(cfg.OrbStart, cfg.OrbEnd);
        _orb.Reset();

        // Update module configs (setup-specific parameters may differ per session)
        var modCfg = new ModuleConfig
        {
            TickSize             = cfg.TickSize,
            PointValue           = cfg.PointValue,
            Timezone             = cfg.Timezone,
            MinTickPenetration   = cfg.SweepMinPenetration,
            MinBodyReject        = cfg.SweepMinBodyReject,
            EqualLevelTolerance  = cfg.SweepEqualTolerance,
            ConfirmationBars     = cfg.SweepConfirmBars,
            DriveRangeAtrMult    = cfg.DriveRangeAtrMult,
            MaxDrivePullback     = cfg.DriveMaxPullback,
            DriveBullBearRatio   = cfg.DriveBullBearRatio,
            TrendDayThreshold    = cfg.TrendDayThreshold,
            ShallowPullbackMax   = cfg.ShallowPullbackMax,
            VwapDevPeriod        = cfg.VwapDevPeriod,
        };
        // Reconfigure modules with updated setup-specific parameters.
        // NOTE: Do NOT reconfigure _sessionEngine here — it tracks daily levels
        // (PDH/PDL, Asia/London highs) across all sessions. Only reset at daily boundary.
        _sweepDetector.Reconfigure(modCfg);
        _vwapModel.Reconfigure(modCfg);
        _openingDrive.Reconfigure(modCfg);
        _trendDay.Reconfigure(modCfg);
        _falseBreakout.Reconfigure(cfg);

        // Snapshot prior session range for false breakout detector
        if (sessionId == SessionId.London)
            _falseBreakout.OnSessionStart(_sessionEngine.AsiaHigh, _sessionEngine.AsiaLow);
        else if (sessionId == SessionId.NY)
            _falseBreakout.OnSessionStart(_sessionEngine.LondonHigh, _sessionEngine.LondonLow);

        // Reset session-scoped state
        _pastCutoff = false; _pastCutoffA = false; _pastCutoffB = false;
        _rthEnded = false; _orbLoggedFormed = false; _orbJustFormed = false;
        _firstLiveBar = true; _warmupCooldown = 0; _orbAtrRatio = 0;
        _orbRestoredFromCache = false;

        // Reset per-setup win/loss/PnL counters for the new session.
        // IMPORTANT: Do NOT reset _todayPnl here — it accumulates across all sessions
        // for the daily loss limit check. _todayPnl is reset only in ResetDaily().
        _todayWins = 0; _todayLosses = 0;
        _todayPeak = _todayPnl; _todayMaxDD = 0;
        _todayWinPnl = 0; _todayLossPnl = 0;
        _todayWinsA = 0; _todayLossesA = 0; _todayWinPnlA = 0; _todayLossPnlA = 0;
        _todayWinsB = 0; _todayLossesB = 0; _todayWinPnlB = 0; _todayLossPnlB = 0;
        _pastCutoffC = false; _tradeCountC = 0;
        _todayWinsC = 0; _todayLossesC = 0; _todayWinPnlC = 0; _todayLossPnlC = 0;
        _pastCutoffD = false; _tradeCountD = 0;
        _todayWinsD = 0; _todayLossesD = 0; _todayWinPnlD = 0; _todayLossPnlD = 0;
        _lastTickTime = DateTime.MinValue;

        // Reset per-setup trade state
        _tradeCountA = 0; _tradeCountB = 0;

        // Reset session-level module state (sweep buffer, drive detection, etc.)
        // NOTE: Do NOT call _sessionEngine.NewSession() or _vwap.NewSession() —
        // those track daily-scope data and only reset at daily boundary.
        var td = DateTime.Today;
        _sweepDetector.NewSession(td);
        _openingDrive.NewSession(td);
        _trendDay.NewSession(td);

        _idle = false;
        _sessionManagedMode = true;

        // Immediately update the SessionEngine's CurrentSession so the snapshot
        // reflects the correct ICT session (not stale PreMarket from ResetDaily).
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);
        _sessionEngine.CurrentSession = _sessionEngine.DetectSession(TimeOnly.FromDateTime(now));

        _log.LogInformation("Engine reconfigured for session {Session}", sessionId);
    }

    /// <summary>Force-exit all active trades. Called by SessionManager at session end.</summary>
    public async Task ForceExitAllAsync()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0)
        {
            // Fallback: set volatile flags so next bar handles the exit
            _forceExitA = true; _forceExitB = true;
            _forceExitC = true; _forceExitD = true;
            _rthEnded = true;
            return;
        }
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        if (_activeA) await ForceExitA(bar);
        if (_activeB) await ForceExitB(bar);
        if (_activeC) await ForceExitC(bar);
        if (_activeD) await ForceExitD(bar);
        _rthEnded = true;
    }

    /// <summary>
    /// Daily-scope reset — ATR, VWAP, session levels, daily loss limit, daily PnL.
    /// Called by SessionManager at SessionStartHour boundary (before first session of the day).
    /// </summary>
    public void ResetDaily()
    {
        _atr.Reset();
        _vwap.NewSession(DateTime.Today);
        _sessionEngine.NewSession(DateTime.Today);
        _todayPnl = 0;  // daily loss limit accumulator — resets once per day
        _ddBreached = false;
        _lastDate = DateTime.MinValue;
        ResetSetupC(); ResetSetupD();
        _todayWinsC = 0; _todayLossesC = 0; _todayWinPnlC = 0; _todayLossPnlC = 0;
        _todayWinsD = 0; _todayLossesD = 0; _todayWinPnlD = 0; _todayLossPnlD = 0;
        _falseBreakout.NewSession(DateTime.Today);
        _log.LogInformation("Daily reset complete");
    }

    /// <summary>
    /// Process a bar through the full strategy pipeline (entry, exit, broker signals, snapshot).
    /// </summary>
    public Task ProcessBarAsync(Bar bar, CancellationToken ct = default)
        => ProcessBarInternalAsync(bar, warmupOnly: false, ct);

    /// <summary>
    /// Process a historical bar for indicator warm-up only.
    /// Updates ORB, ATR and VWAP — does NOT run strategy logic or fire any broker/sink signals.
    /// Use this during backfill so past bars do not consume trade-count budgets or place orders.
    /// </summary>
    public Task WarmupBarAsync(Bar bar, CancellationToken ct = default)
        => ProcessBarInternalAsync(bar, warmupOnly: true, ct);

    /// <summary>
    /// Publishes the current engine state (ORB/ATR/VWAP, setup states, daily stats)
    /// to the event sink (SignalR dashboard). Call after backfill so the dashboard
    /// shows warmed-up indicator values immediately.
    /// </summary>
    public Task PublishCurrentStateAsync()
    {
        var now = DateTime.UtcNow;
        return PublishSnapshot(new Bar(now, 0, 0, 0, 0, 0, IsConfirmed: false));
    }

    /// <summary>
    /// Restore ORB state from a cached snapshot (saved during streaming).
    /// Call before backfill so the engine uses correct ORB values instead
    /// of re-deriving from potentially incorrect REST historical data.
    /// </summary>
    public void RestoreOrb(OrbStateCache cache)
    {
        _orb.Restore(cache.OrbHigh, cache.OrbLow, cache.CloseRelPct, cache.TradingDate);
        _orbLoggedFormed     = true;
        _orbRestoredFromCache = true;
        _orbAtrRatio         = cache.OrbAtrRatio;
        _log.LogInformation("ORB restored from cache — H:{H:F2} L:{L:F2} R:{R:F2} ATR%:{A:F3}",
            cache.OrbHigh, cache.OrbLow, _orb.OrbRange, cache.OrbAtrRatio);
    }

    /// <summary>
    /// Seed ORB high/low from cache as a floor when restarting inside the ORB window.
    /// The ORB stays in "forming" state — warmup bars can expand the range but not shrink it.
    /// </summary>
    public void SeedOrbFloor(OrbStateCache cache)
    {
        _orb.SeedFloor(cache.OrbHigh, cache.OrbLow);
        _log.LogInformation("ORB seeded floor from cache — H:{H:F2} L:{L:F2} (forming, will expand)",
            cache.OrbHigh, cache.OrbLow);
    }

    public void SeedModuleHistory(IReadOnlyList<Bar> dailyBars)
    {
        _sessionEngine.SeedHistory(dailyBars);
    }

    // ── Tick-mode flag ────────────────────────────────────────────
    private bool _tickModeEnabled = false;
    private DateTime _lastTickTime = DateTime.MinValue;

    /// <summary>
    /// Enable tick-based entry/exit evaluation.
    /// When enabled, <see cref="ProcessPriceTickAsync"/> evaluates entries/exits on each price tick
    /// and <see cref="ProcessBarAsync"/> only updates indicators and arm state (skips bar-level
    /// entry/exit).  Used for both live trading (L1 ticks) and backtest tick simulation
    /// (1-min OHLC prices fired as four sequential ticks per bar).
    /// </summary>
    public void EnableTickMode() => _tickModeEnabled = true;

    /// <summary>
    /// Returns the best exit timestamp for bar-level fallback exits.
    /// In tick mode the TF bar's Time is the bucket start, which can precede
    /// the entry tick time when TF &gt; 1.  Use the last processed tick time instead.
    /// </summary>
    private DateTime BarExitTime(DateTime barTime)
        => _tickModeEnabled && _lastTickTime > barTime ? _lastTickTime : barTime;

    /// <summary>
    /// Check daily loss limit immediately after a trade exit updates _todayPnl.
    /// Called from every BookExit method so the limit is enforced at tick granularity
    /// rather than waiting for the next bar close.
    /// </summary>
    private void CheckDailyLossLimit()
    {
        if (_cfg.UseDailyLossLimit && _todayPnl <= -_cfg.MaxDailyLoss && !_ddBreached)
        {
            _log.LogWarning("Daily loss limit breached: PnL={Pnl:C0} Limit={Limit:C0} — trading halted",
                _todayPnl, _cfg.MaxDailyLoss);
            _ddBreached = true;
            AddAlert("DD_LIMIT", SetupId.A, "Daily loss limit — trading halted", "red");
        }
    }

    /// <summary>
    /// Update engine entry price and recalculate stop/target/partial after receiving
    /// the actual broker fill price. Shifts all levels by the slippage delta so the
    /// original risk/reward structure is preserved.
    /// Called from TryEntry methods immediately after OnEntrySignalAsync returns a fill.
    /// </summary>
    private async Task ApplyFillPrice(SetupId setup, decimal fillPrice)
    {
        decimal delta;
        switch (setup)
        {
            case SetupId.A:
                delta = fillPrice - _entA;
                _log.LogInformation("[Setup A] Fill adjustment: theoretical={T:F2} actual={A:F2} delta={D:F2}",
                    _entA, fillPrice, delta);
                _entA      = fillPrice;
                _stopA     = LevelCalculator.RoundToTick(_stopA + delta, _cfg.TickSize);
                _tgtA      = LevelCalculator.RoundToTick(_tgtA + delta, _cfg.TickSize);
                _partialA  = LevelCalculator.RoundToTick(_partialA + delta, _cfg.TickSize);
                _initStopA = _stopA;
                await _executor.OnLevelsAdjustedAsync(setup, _stopA, _tgtA, _ctsA);
                break;
            case SetupId.B:
                delta = fillPrice - _entB;
                _log.LogInformation("[Setup B] Fill adjustment: theoretical={T:F2} actual={A:F2} delta={D:F2}",
                    _entB, fillPrice, delta);
                _entB      = fillPrice;
                _stopB     = LevelCalculator.RoundToTick(_stopB + delta, _cfg.TickSize);
                _tgtB      = LevelCalculator.RoundToTick(_tgtB + delta, _cfg.TickSize);
                _partialB  = LevelCalculator.RoundToTick(_partialB + delta, _cfg.TickSize);
                _initStopB = _stopB;
                await _executor.OnLevelsAdjustedAsync(setup, _stopB, _tgtB, _ctsB);
                break;
            case SetupId.C:
                delta = fillPrice - _entC;
                _log.LogInformation("[Setup C] Fill adjustment: theoretical={T:F2} actual={A:F2} delta={D:F2}",
                    _entC, fillPrice, delta);
                _entC      = fillPrice;
                _stopC     = LevelCalculator.RoundToTick(_stopC + delta, _cfg.TickSize);
                _tgtC      = LevelCalculator.RoundToTick(_tgtC + delta, _cfg.TickSize);
                _partialC  = LevelCalculator.RoundToTick(_partialC + delta, _cfg.TickSize);
                _initStopC = _stopC;
                await _executor.OnLevelsAdjustedAsync(setup, _stopC, _tgtC, _ctsC);
                break;
            case SetupId.D:
                delta = fillPrice - _entD;
                _log.LogInformation("[Setup D] Fill adjustment: theoretical={T:F2} actual={A:F2} delta={D:F2}",
                    _entD, fillPrice, delta);
                _entD      = fillPrice;
                _stopD     = LevelCalculator.RoundToTick(_stopD + delta, _cfg.TickSize);
                _tgtD      = LevelCalculator.RoundToTick(_tgtD + delta, _cfg.TickSize);
                _partialD  = LevelCalculator.RoundToTick(_partialD + delta, _cfg.TickSize);
                _initStopD = _stopD;
                await _executor.OnLevelsAdjustedAsync(setup, _stopD, _tgtD, _ctsD);
                break;
        }
    }

    /// <summary>
    /// Evaluate entry and exit conditions against a realtime L1 price tick.
    /// Only active after EnableTickMode() has been called.
    /// WARNING: NOT thread-safe. Shares mutable state with ProcessBarAsync.
    /// The caller MUST serialize all calls to this method and ProcessBarAsync
    /// (e.g., via SemaphoreSlim(1,1)) to prevent race conditions.
    /// </summary>
    public async Task ProcessPriceTickAsync(decimal price, DateTime utcTime)
    {
        if (_idle) return;
        if (!_tickModeEnabled) return;
        if (price <= 0) return;
        if (!_orb.IsSet || _orb.OrbRange <= 0) return;
        if (_ddBreached) return;
        if (_warmupCooldown > 0) return;   // block tick entries during cooldown
        if (_orbJustFormed) return;        // block tick entries on ORB-forming bar

        if (_rthEnded) return;

        _lastTickTime = utcTime;
        _sessionEngine.OnTick(price, utcTime);

        // Check early session exit on tick
        if (_cfg.ExitMinutesBefore > 0 && !_rthEnded)
        {
            var localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcTime, _tz));
            var exitTime  = _cfg.RthEnd.AddMinutes(-_cfg.ExitMinutesBefore);
            var sessStart = new TimeOnly(_cfg.SessionStartHour, 0);
            if (localTime >= exitTime && localTime < sessStart)
            {
                _log.LogInformation("Tick exit at {Time} ({Min}min before RthEnd)", localTime, _cfg.ExitMinutesBefore);
                AddAlert("SESSION", SetupId.A, $"Early exit {exitTime:HH:mm} ({_cfg.ExitMinutesBefore}min early)", "red");
                _rthEnded = true;
                var bar = new Bar(utcTime, price, price, price, price, 0);
                if (_activeA) await ForceExitA(bar);
                if (_activeB) await ForceExitB(bar);
                if (_activeC) await ForceExitC(bar);
                if (_activeD) await ForceExitD(bar);
                return;
            }
        }

        // ── Adverse excursion time check (tick mode) ───────────────
        var tickBar = new Bar(utcTime, price, price, price, price, 0);
        if (IsAdverseTimeout(_activeA, _stA == 2, _entA, price, _entryTimeA, utcTime, _cfg.MaxAdverseMinutesA))
        {
            _log.LogInformation("[A] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesA);
            AddAlert("ADVERSE", SetupId.A, $"Underwater >{_cfg.MaxAdverseMinutesA}min — exit", "orange");
            await ForceExitA(tickBar, ExitReason.AdverseTime);
        }
        if (IsAdverseTimeout(_activeB, _stB == 3, _entB, price, _entryTimeB, utcTime, _cfg.MaxAdverseMinutesB))
        {
            _log.LogInformation("[B] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesB);
            AddAlert("ADVERSE", SetupId.B, $"Underwater >{_cfg.MaxAdverseMinutesB}min — exit", "orange");
            await ForceExitB(tickBar, ExitReason.AdverseTime);
        }
        if (IsAdverseTimeout(_activeC, _stC == 2, _entC, price, _entryTimeC, utcTime, _cfg.MaxAdverseMinutesC))
        {
            _log.LogInformation("[C] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesC);
            AddAlert("ADVERSE", SetupId.C, $"Underwater >{_cfg.MaxAdverseMinutesC}min — exit", "orange");
            await ForceExitC(tickBar, ExitReason.AdverseTime);
        }
        if (IsAdverseTimeout(_activeD, _stD == 2, _entD, price, _entryTimeD, utcTime, _cfg.MaxAdverseMinutesD))
        {
            _log.LogInformation("[D] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesD);
            AddAlert("ADVERSE", SetupId.D, $"Underwater >{_cfg.MaxAdverseMinutesD}min — exit", "orange");
            await ForceExitD(tickBar, ExitReason.AdverseTime);
        }

        if (_cfg.EnableA && !_pastCutoffA) await EvalTickSetupA(price, utcTime);
        if (_cfg.EnableB && !_pastCutoffB) await EvalTickSetupB(price, utcTime);
        if (_cfg.EnableC && !_pastCutoffC) await EvalTickSetupC(price, utcTime);
        if (_cfg.EnableD && !_pastCutoffD) await EvalTickSetupD(price, utcTime);
    }

    private async Task EvalTickSetupA(decimal price, DateTime utcTime)
    {
        decimal orbRange = _orb.OrbRange;
        decimal tickTol  = _cfg.TickSize * 2;

        // ── Entry: armed but not yet active ─────────────────────
        if (!_activeA && (_stA == 1 || _stA == -1))
        {
            bool isLong = _stA == 1;
            if (_cfg.IsAggressiveA)
            {
                // Aggressive: any tick while armed fires entry
                await TryEntryAFromTick(_armEntryA, isLong, orbRange, utcTime);
            }
            else
            {
                decimal pbPts   = orbRange * _cfg.PullbackPct;
                decimal longPb  = LevelCalculator.RoundToTick(_orb.OrbHigh - pbPts, _cfg.TickSize);
                decimal shortPb = LevelCalculator.RoundToTick(_orb.OrbLow  + pbPts, _cfg.TickSize);

                if (isLong  && price <= longPb  + tickTol)
                    await TryEntryAFromTick(longPb, true, orbRange, utcTime);
                else if (!isLong && price >= shortPb - tickTol)
                    await TryEntryAFromTick(shortPb, false, orbRange, utcTime);
            }
            return; // don't check exit on same tick as entry attempt
        }

        // ── Exit: active position ────────────────────────────────
        if (!_activeA) return;
        bool long_ = _stA == 2;
        bool hitStop      = long_ ? price <= _stopA    : price >= _stopA;
        bool hitTarget    = long_ ? price >= _tgtA     : price <= _tgtA;
        // Include partial level in the early-return guard so the partial block below is
        // reachable even when neither stop nor target has been hit yet.
        bool hitPartialPx = _cfg.UsePartialA && !_partHitA &&
                            (long_ ? price >= _partialA : price <= _partialA);
        if (!hitStop && !hitTarget && !hitPartialPx) return;

        // Partial fill check (price-based)
        bool partJustHit = false;
        if (hitPartialPx && !hitTarget) // partial without full close
        {
            int half    = CalcPartialCts(_ctsA, _cfg.PartialCtsA);
            int remaining = _ctsA - half;
            if (half > 0)
            {
                partJustHit = true;
                _partHitA   = true;
                // Accumulate partial PnL so bar-mode ExitProcessor (safety net) sees it
                _pnlA += (long_ ? _partialA - _entA : _entA - _partialA) * _cfg.PointValue * half;
                var psig    = new PartialSignal(SetupId.A, long_ ? Direction.Long : Direction.Short,
                    _partialA, half, remaining, _entA, utcTime);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.A, $"Partial @ {_partialA:F2}", "yellow");

                if (_cfg.UseBeA)
                {
                    var besig = new BESignal(SetupId.A, long_ ? Direction.Long : Direction.Short,
                        _entA, _entA, remaining, utcTime);
                    _stopA = _entA;
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.A, $"Stop → BE {_entA:F2}", "yellow");
                }
                else
                {
                    // No BE move, but still reduce broker stop/target qty to match remaining contracts
                    await _executor.OnLevelsAdjustedAsync(SetupId.A, _stopA, _tgtA, remaining);
                }
            }
            if (!hitTarget && !hitStop) return; // partial only, trade still open
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _tgtA : _stopA;
        // _pnlA already contains partial PnL (accumulated when partial fired).
        // Add remaining contracts' PnL at the exit price.
        int remCts = (_partHitA && _cfg.UsePartialA)
            ? _ctsA - CalcPartialCts(_ctsA, _cfg.PartialCtsA) : _ctsA;
        decimal pnl = _pnlA + (long_ ? exitPx - _entA : _entA - exitPx) * _cfg.PointValue * remCts;
        _pnlA    = pnl;
        _activeA = false;
        _stA     = 0;
        _tradeCountA++;
        // Lock direction on exit to prevent immediate re-arm in the same direction.
        // Exception: BE stop-outs (exit at entry) — allow re-arm since no real loss occurred.
        bool isBE = _cfg.AllowRearmAfterBeA && reason == ExitReason.Stop && exitPx == _entA;
        if (!isBE) { if (long_) _bullTradedA = true; else _bearTradedA = true; }
        if (hitTarget) { _stickyTgtA = true; _exitBarIdxA = _barIndex; }
        else           { _stickyStpA = true; _exitBarIdxA = _barIndex; }
        await BookExitA(reason, exitPx, utcTime, long_, sameBarPartial: partJustHit);
    }

    private async Task TryEntryAFromTick(decimal ep, bool isLong, decimal orbRange, DateTime utcTime)
    {
        if (HasOpposingPosition(isLong)) return;
        if (_cfg.EntryTickOffsetA != 0 && _cfg.TickSize > 0)
        {
            decimal off = _cfg.EntryTickOffsetA * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + off : ep - off, _cfg.TickSize);
        }
        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPctA, _cfg.TargetPctA, _cfg.PartialPctA, orbRange, _cfg.TickSize);
        if (rr < _cfg.MinRrA) return;

        _entA = ep; _stopA = sl; _tgtA = tp; _partialA = pp;
        _initStopA = sl; _pnlA = 0; _activeA = true;
        _ctsA = CalcContracts(_cfg.ContractsA, _cfg.HiVolMultA, _cfg.MaxContractsA);
        _stA = isLong ? 2 : -2; _enteredThisBar = true;
        _entryTimeA = utcTime;

        _log.LogInformation("[Setup A TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _ctsA, ep, sl, tp, rr);
        if (IsEntryPriceStale(SetupId.A, ep)) return;
        var sig = new EntrySignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _ctsA, utcTime, _cfg.OrderTypeA);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.A, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.A,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsA}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task EvalTickSetupB(decimal price, DateTime utcTime)
    {
        decimal orbRange = _orb.OrbRange;
        decimal tickTol  = _cfg.TickSize * 2;

        // ── Entry: armed/retest state ────────────────────────────
        // _stB: ±1=Armed (above/below ORB), ±2=Retest (price returned near orbHigh/orbLow)
        if (!_activeB && (_stB == 1 || _stB == -1 || _stB == 2 || _stB == -2))
        {
            bool isLong = _stB > 0;
            if (_cfg.IsAggressiveB)
            {
                await TryEntryBFromTick(_armEntryB, isLong, utcTime);
            }
            else
            {
                // Conservative B: enter when price retests the ORB breakout level (orbHigh / orbLow).
                // Entry = orbHigh (Long) or orbLow (Short), matching bar-mode TryEntryB.
                // Stop = entry ± orbRange * StopPctB (from CalcLevelsB).  Default 0.50 → orbMid equivalent.
                // Bug fixed: previously used orbMid as entry, making entry == stop → risk = 0 → RR = 0
                // → all trades filtered out by MinRrB even with EntryTickOffsetB = 0.
                decimal retestDist = orbRange * _cfg.RetestPct;
                decimal orbHigh    = _orb.OrbHigh;
                decimal orbLow     = _orb.OrbLow;
                if (isLong  && price <= orbHigh + retestDist + tickTol)
                    await TryEntryBFromTick(orbHigh, true, utcTime);
                else if (!isLong && price >= orbLow - retestDist - tickTol)
                    await TryEntryBFromTick(orbLow, false, utcTime);
            }
            return;
        }

        // ── Exit: active position ────────────────────────────────
        if (!_activeB) return;
        bool longB = _stB == 3;
        bool hitStop      = longB ? price <= _stopB    : price >= _stopB;
        bool hitTarget    = longB ? price >= _tgtB     : price <= _tgtB;
        // Include partial level in the early-return guard so the partial block below is
        // reachable even when neither stop nor target has been hit yet.
        bool hitPartialPx = _cfg.UsePartialB && !_partHitB &&
                            (longB ? price >= _partialB : price <= _partialB);
        if (!hitStop && !hitTarget && !hitPartialPx) return;

        // Partial fill check (price-based)
        bool partJustHit = false;
        if (hitPartialPx && !hitTarget) // partial without full close
        {
            int half    = CalcPartialCts(_ctsB, _cfg.PartialCtsB);
            int remaining = _ctsB - half;
            if (half > 0)
            {
                partJustHit = true;
                _partHitB   = true;
                // Accumulate partial PnL so bar-mode ExitProcessor (safety net) sees it
                _pnlB += (longB ? _partialB - _entB : _entB - _partialB) * _cfg.PointValue * half;
                var psig    = new PartialSignal(SetupId.B, longB ? Direction.Long : Direction.Short,
                    _partialB, half, remaining, _entB, utcTime);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.B, $"Partial @ {_partialB:F2}", "yellow");

                if (_cfg.UseBeB)
                {
                    var besig = new BESignal(SetupId.B, longB ? Direction.Long : Direction.Short,
                        _entB, _entB, remaining, utcTime);
                    _stopB = _entB;
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.B, $"Stop → BE {_entB:F2}", "yellow");
                }
                else
                {
                    await _executor.OnLevelsAdjustedAsync(SetupId.B, _stopB, _tgtB, remaining);
                }
            }
            if (!hitTarget && !hitStop) return; // partial only, trade still open
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _tgtB : _stopB;
        // _pnlB already contains partial PnL (accumulated when partial fired).
        // Add remaining contracts' PnL at the exit price.
        int remCts = (_partHitB && _cfg.UsePartialB)
            ? _ctsB - CalcPartialCts(_ctsB, _cfg.PartialCtsB) : _ctsB;
        decimal pnl = _pnlB + (longB ? exitPx - _entB : _entB - exitPx) * _cfg.PointValue * remCts;
        _pnlB    = pnl;
        _activeB = false;
        _stB     = 0;
        _tradeCountB++;
        // Lock direction on exit to prevent immediate re-arm in the same direction.
        // Exception: BE stop-outs (exit at entry) — allow re-arm since no real loss occurred.
        bool isBE = _cfg.AllowRearmAfterBeB && reason == ExitReason.Stop && exitPx == _entB;
        if (!isBE) { if (longB) _bullTradedB = true; else _bearTradedB = true; }
        if (hitTarget) { _stickyTgtB = true; _exitBarIdxB = _barIndex; }
        else           { _stickyStpB = true; _exitBarIdxB = _barIndex; }
        await BookExitB(reason, exitPx, utcTime, longB, sameBarPartial: partJustHit);
    }

    private async Task TryEntryBFromTick(decimal ep, bool isLong, DateTime utcTime)
    {
        if (HasOpposingPosition(isLong)) return;
        if (_cfg.EntryTickOffsetB != 0 && _cfg.TickSize > 0)
        {
            decimal off = _cfg.EntryTickOffsetB * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + off : ep - off, _cfg.TickSize);
        }
        var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
            _cfg.TargetPctB, _cfg.PartialPctB, _orb.OrbRange, _cfg.StopPctB, _cfg.TickSize);
        if (rr < _cfg.MinRrB) return;

        _entB = ep; _stopB = sl; _tgtB = tp; _partialB = pp;
        _initStopB = sl; _pnlB = 0; _activeB = true;
        _ctsB = CalcContracts(_cfg.ContractsB, _cfg.HiVolMultB, _cfg.MaxContractsB);
        _stB = isLong ? 3 : -3; _enteredThisBar = true;
        _entryTimeB = utcTime;

        _log.LogInformation("[Setup B TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _ctsB, ep, sl, tp, rr);
        if (IsEntryPriceStale(SetupId.B, ep)) return;
        var sig = new EntrySignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _ctsB, utcTime, _cfg.OrderTypeB);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.B, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.B,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsB}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task ProcessBarInternalAsync(Bar bar, bool warmupOnly, CancellationToken ct)
    {
        if (_idle) return;
        var local     = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, _tz);
        var tradingDate = _cfg.TradingDate(local);
        var localTime = TimeOnly.FromDateTime(local);

        bool newDay = tradingDate != _lastDate;
        if (newDay && !_sessionManagedMode)
        {
            // Legacy single-session path (backtest, non-SessionManager live).
            // When SessionManager is active, it calls Reconfigure/ResetDaily instead.
            _log.LogInformation("New trading session {Date} for {Ticker} (local: {LocalTime})",
                tradingDate.ToString("yyyy-MM-dd"), _cfg.Ticker, local.ToString("HH:mm"));
            _lastDate    = tradingDate;
            _pastCutoff      = false;
            _pastCutoffA     = false;
            _pastCutoffB     = false;
            _pastCutoffC     = false;
            _pastCutoffD     = false;
            _rthEnded        = false;
            _orbLoggedFormed = false;
            _orbJustFormed   = false;
            _firstLiveBar    = true;
            _warmupCooldown  = 0;
            _orbAtrRatio = 0;
            _todayPnl    = 0;
            _todayWins   = 0;
            _todayLosses = 0;
            _todayPeak   = 0;
            _todayMaxDD  = 0;
            _todayWinPnl = 0; _todayLossPnl = 0;
            _todayWinsA  = 0; _todayLossesA = 0; _todayWinPnlA = 0; _todayLossPnlA = 0;
            _todayWinsB  = 0; _todayLossesB = 0; _todayWinPnlB = 0; _todayLossPnlB = 0;
            _todayWinsC  = 0; _todayLossesC = 0; _todayWinPnlC = 0; _todayLossPnlC = 0;
            _todayWinsD  = 0; _todayLossesD = 0; _todayWinPnlD = 0; _todayLossPnlD = 0;
            _ddBreached  = false;
            _lastTickTime = DateTime.MinValue;
            ResetSetupA();
            ResetSetupB();
            ResetSetupC();
            ResetSetupD();
            // Reset VWAP immediately at the session boundary so the dashboard
            // shows 0 right at 6 PM ET — not delayed until the first confirmed bar.
            _vwap.NewSession(tradingDate);
            _sessionEngine.NewSession(tradingDate);
            _sweepDetector.NewSession(tradingDate);
            _vwapModel.NewSession(tradingDate);
            _openingDrive.NewSession(tradingDate);
            _trendDay.NewSession(tradingDate);
            _falseBreakout.NewSession(tradingDate);
        }
        else if (newDay)
        {
            _lastDate = tradingDate;  // track date even in managed mode
        }

        // First live bar/tick after warmup — fire summary alerts immediately
        if (!warmupOnly && _firstLiveBar)
        {
            _firstLiveBar = false;
            // If ORB already formed during warmup, add 1-bar cooldown so the first
            // confirmed bar only evaluates arm state (no instant entry on restart).
            // Also reset arm states built during warmup — setups must re-arm on live data.
            if (_orbLoggedFormed && _orb.IsSet && _orb.OrbRange > 0)
            {
                _warmupCooldown = 1;
                _stA = 0; _armEntryA = 0;
                _stB = 0; _armEntryB = 0;
                _stC = 0; _armEntryC = 0;
                _stD = 0; _armEntryD = 0;
            }
            AddAlert("ENGINE", SetupId.A, $"Engine: Live", "blue");
            if (_orbLoggedFormed && _orb.IsSet && _orb.OrbRange > 0)
                AddAlert("ORB", SetupId.A, $"ORB formed — H:{_orb.OrbHigh:F2} L:{_orb.OrbLow:F2} R:{_orb.OrbRange:F2}", "blue");
        }

        // ORB tracks the developing bar in real time (included before confirmed guard).
        // Skip during warmup when ORB was restored from cache — REST backfill data can
        // differ from streaming (especially Schwab back-month futures).
        if (!warmupOnly || !_orbRestoredFromCache)
            _orb.Update(bar, tradingDate);

        // ATR and VWAP only advance on closed bars — one update per execution-TF bar.
        // Moving them after the guard prevents partial/duplicate updates from in-progress
        // bar ticks double-counting volume (VWAP) or adding spurious TRs (ATR).
        if (!bar.IsConfirmed)
        {
            // Throttle unconfirmed bar snapshots (L1 ticks can arrive many times/second)
            if (!warmupOnly && (DateTime.UtcNow - _lastSnapshotUtc).TotalSeconds >= 2)
                await PublishSnapshot(bar);
            return;
        }

        _atr.Update(bar);
        _vwap.Update(bar, tradingDate);
        if (bar.Close > 0) _lastBarClose = bar.Close;

        // ── Module updates ───────────────────────────────────
        _sessionEngine.CurrentAtr = _atr.Value;
        _sessionEngine.CurrentVwap = _vwap.Value;
        _sessionEngine.OnBar(bar, tradingDate);

        // Feed ORB bars to opening drive detector
        if (!_orb.IsSet)
        {
            _openingDrive.CurrentAtr = _atr.Value;
            _openingDrive.CurrentVwap = _vwap.Value;
            _openingDrive.AccumulateOrbBar(bar);
        }

        _vwapModel.OnBar(bar, tradingDate);

        // Sweep detector: update levels from session engine + orb
        _sweepDetector.SetLevels(
            pdh: _sessionEngine.PDH, pdl: _sessionEngine.PDL,
            pwh: _sessionEngine.PWH, pwl: _sessionEngine.PWL,
            pmh: _sessionEngine.PMH, pml: _sessionEngine.PML,
            sessionHigh: _sessionEngine.SessionHigh,
            sessionLow: _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : 0,
            orbHigh: _orb.OrbHigh, orbLow: _orb.OrbLow
        );
        _sweepDetector.OnBar(bar, tradingDate);

        _falseBreakout.OnBar(bar, tradingDate,
            _orb.OrbHigh, _orb.OrbLow, _orb.IsSet,
            _vwap.Value, _trendDay.BullScore, _trendDay.BearScore);

        // Warmup mode: indicators are built + arm state is evaluated so the dashboard
        // shows correct Armed/Idle badges when the engine starts mid-session.
        // No entries, exits, broker signals, trade counting, or snapshots.
        if (warmupOnly)
        {
            // Track session flags so stale alerts don't fire on first live bar
            if (_orb.IsSet && _orb.OrbRange > 0)
            {
                _orbLoggedFormed = true;
                if (_orbAtrRatio == 0 && _atr.IsReady && _atr.Value > 0)
                    _orbAtrRatio = _orb.OrbRange / _atr.Value;
            }
            var wSessStart = new TimeOnly(_cfg.SessionStartHour, 0);
            // Guard against overnight bars (19:00–23:59 ET) falsely triggering the cutoff.
            // Only set these flags for bars within the calendar trading day (before session restart).
            var wCutoffA = new TimeOnly(_cfg.CutoffHourA, _cfg.CutoffMinuteA);
            if (_cfg.UseTimeFilter && localTime >= wCutoffA && localTime < wSessStart) _pastCutoffA = true;
            var wCutoffB = new TimeOnly(_cfg.CutoffHourB, _cfg.CutoffMinuteB);
            if (_cfg.UseTimeFilter && localTime >= wCutoffB && localTime < wSessStart) _pastCutoffB = true;
            var wCutoffC = new TimeOnly(_cfg.CutoffHourC, _cfg.CutoffMinuteC);
            if (_cfg.UseTimeFilter && localTime >= wCutoffC && localTime < wSessStart) _pastCutoffC = true;
            var wCutoffD = new TimeOnly(_cfg.CutoffHourD, _cfg.CutoffMinuteD);
            if (_cfg.UseTimeFilter && localTime >= wCutoffD && localTime < wSessStart) _pastCutoffD = true;
            _pastCutoff = _pastCutoffA && _pastCutoffB;
            if (localTime >= _cfg.RthEnd.AddMinutes(-_cfg.ExitMinutesBefore) && localTime < wSessStart) _rthEnded = true;
            EvaluateArmState(bar, localTime);
            return;
        }

        // Advance bar index (Pine: bar_index++)
        _barIndex++;

        // Clear sticky exit signals when bar has advanced past the exit bar
        // (Pine: if not na(exitBarA_idx) and bar_index > exitBarA_idx => clear)
        if (_exitBarIdxA != -1 && _barIndex > _exitBarIdxA)
            { _stickyTgtA = false; _stickyStpA = false; _exitBarIdxA = -1; }
        if (_exitBarIdxB != -1 && _barIndex > _exitBarIdxB)
            { _stickyTgtB = false; _stickyStpB = false; _exitBarIdxB = -1; }
        if (_exitBarIdxC != -1 && _barIndex > _exitBarIdxC)
            { _stickyTgtC = false; _stickyStpC = false; _exitBarIdxC = -1; }
        if (_exitBarIdxD != -1 && _barIndex > _exitBarIdxD)
            { _stickyTgtD = false; _stickyStpD = false; _exitBarIdxD = -1; }

        var sessStart   = new TimeOnly(_cfg.SessionStartHour, 0);

        // Per-setup cutoff — each setup can have its own cutoff time
        var cutoffA = new TimeOnly(_cfg.CutoffHourA, _cfg.CutoffMinuteA);
        if (_cfg.UseTimeFilter && localTime >= cutoffA && localTime < sessStart && !_pastCutoffA)
        {
            _pastCutoffA = true;
            // Disarm armed/waiting states — don't hold stale setups past cutoff
            if (!_activeA && (_stA == 1 || _stA == -1)) { _stA = 0; _armEntryA = 0; }
            _log.LogInformation("Cutoff A reached at {Time}", localTime);
            AddAlert("CUTOFF", SetupId.A, $"Cutoff A {cutoffA:HH:mm} — no new entries", "yellow");
        }

        var cutoffB = new TimeOnly(_cfg.CutoffHourB, _cfg.CutoffMinuteB);
        if (_cfg.UseTimeFilter && localTime >= cutoffB && localTime < sessStart && !_pastCutoffB)
        {
            _pastCutoffB = true;
            // Disarm armed/retest states — don't hold stale setups past cutoff
            if (!_activeB && (_stB == 1 || _stB == -1 || _stB == 2 || _stB == -2)) { _stB = 0; _armEntryB = 0; }
            _log.LogInformation("Cutoff B reached at {Time}", localTime);
            AddAlert("CUTOFF", SetupId.B, $"Cutoff B {cutoffB:HH:mm} — no new entries", "yellow");
        }

        var cutoffC = new TimeOnly(_cfg.CutoffHourC, _cfg.CutoffMinuteC);
        if (_cfg.UseTimeFilter && localTime >= cutoffC && localTime < sessStart && !_pastCutoffC)
        {
            _pastCutoffC = true;
            if (!_activeC && (_stC == 1 || _stC == -1)) { _stC = 0; _armEntryC = 0; }
            _log.LogInformation("Cutoff C reached at {Time}", localTime);
            AddAlert("CUTOFF", SetupId.C, $"Cutoff C {cutoffC:HH:mm} — no new entries", "yellow");
        }

        var cutoffD = new TimeOnly(_cfg.CutoffHourD, _cfg.CutoffMinuteD);
        if (_cfg.UseTimeFilter && localTime >= cutoffD && localTime < sessStart && !_pastCutoffD)
        {
            _pastCutoffD = true;
            if (!_activeD && (_stD == 1 || _stD == -1)) { _stD = 0; _armEntryD = 0; }
            _log.LogInformation("Cutoff D reached at {Time}", localTime);
            AddAlert("CUTOFF", SetupId.D, $"Cutoff D {cutoffD:HH:mm} — no new entries", "yellow");
        }

        _pastCutoff = _pastCutoffA && _pastCutoffB;

        var exitTime = _cfg.RthEnd.AddMinutes(-_cfg.ExitMinutesBefore);
        bool rthJustEndedA = _cfg.CloseAtRthCloseA && localTime >= exitTime && localTime < sessStart && !_rthEnded;
        bool rthJustEndedB = _cfg.CloseAtRthCloseB && localTime >= exitTime && localTime < sessStart && !_rthEnded;
        bool rthJustEndedC = _cfg.CloseAtRthCloseC && localTime >= exitTime && localTime < sessStart && !_rthEnded;
        bool rthJustEndedD = _cfg.CloseAtRthCloseD && localTime >= exitTime && localTime < sessStart && !_rthEnded;
        if (localTime >= exitTime && localTime < sessStart && !_rthEnded)
        {
            _log.LogInformation("Session exit at {Time} ({Min}min before RthEnd)", localTime, _cfg.ExitMinutesBefore);
            AddAlert("SESSION", SetupId.A, $"Session exit {exitTime:HH:mm} ({_cfg.ExitMinutesBefore}min early)", "red");
            _rthEnded = true;
        }

        if (_cfg.UseDailyLossLimit && _todayPnl <= -_cfg.MaxDailyLoss && !_ddBreached)
        {
            _log.LogWarning("Daily loss limit breached: PnL={Pnl:C0} Limit={Limit:C0} — trading halted", _todayPnl, _cfg.MaxDailyLoss);
            _ddBreached = true;
            AddAlert("DD_LIMIT", SetupId.A, "Daily loss limit — trading halted", "red");
        }

        if (!_orb.IsSet || _orb.OrbRange <= 0)
        {
            _log.LogDebug("ORB not yet set at {Time} — skipping", localTime);
            await PublishSnapshot(bar); return;
        }

        if (!_orbLoggedFormed)
        {
            _orbLoggedFormed = true;
            _orbJustFormed  = true;           // gate: skip entry on this bar
            _openingDrive.FreezeAtOrbClose(_orb.OrbRange);
            _orbAtrRatio = _atr.IsReady && _atr.Value > 0 ? _orb.OrbRange / _atr.Value : 0;
            _log.LogInformation("ORB formed: High={H} Low={L} Range={R} ATR/ORB={Ratio:F2} at {Time}",
                _orb.OrbHigh, _orb.OrbLow, _orb.OrbRange, _orbAtrRatio, localTime);
            AddAlert("ORB", SetupId.A, $"ORB formed — H:{_orb.OrbHigh:F2} L:{_orb.OrbLow:F2} R:{_orb.OrbRange:F2}", "blue");

            // Persist ORB state so engine restarts use streaming-derived values
            OrbStateCacheService.Save(new OrbStateCache
            {
                TradingDate = bar.Time.Date,
                Symbol      = _cfg.Ticker,
                SessionId   = _activeSessionId,
                OrbHigh     = _orb.OrbHigh,
                OrbLow      = _orb.OrbLow,
                CloseRelPct = _orb.CloseRelPct,
                OrbAtrRatio = _orbAtrRatio,
                SavedAtUtc  = DateTime.UtcNow
            });
        }

        // Trend day + composite setups (only after ORB formed)
        if (_orb.IsSet && bar.IsConfirmed)
        {
            _vwapModel.TrendDayBull = _trendDay.TrendDayBull;
            _vwapModel.TrendDayBear = _trendDay.TrendDayBear;

            _trendDay.Update(
                openingDriveBull: _openingDrive.OpeningDriveBull,
                openingDriveBear: _openingDrive.OpeningDriveBear,
                close: bar.Close, vwap: _vwap.Value,
                orbHigh: _orb.OrbHigh, orbLow: _orb.OrbLow, orbMid: _orb.OrbMid,
                sessionHigh: _sessionEngine.SessionHigh,
                sessionLow: _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : bar.Low,
                sessionOpen: _orb.OrbMid
            );

        }

        bool atrOk = !_atr.IsReady || (_orb.OrbRange >= _atr.Value * _cfg.AtrFilterPct);
        if (!atrOk)
        {
            _log.LogDebug("ATR filter blocked: OrbRange={Range:F2} ATR={Atr:F2} Threshold={Thr:F2}",
                _orb.OrbRange, _atr.Value, _atr.Value * _cfg.AtrFilterPct);
            await PublishSnapshot(bar); return;
        }

        bool aboveVwapA  = !_cfg.UseVwapA || (_vwap.IsReady && bar.Close > _vwap.Value);
        bool belowVwapA  = !_cfg.UseVwapA || (_vwap.IsReady && bar.Close < _vwap.Value);
        bool orbLongOkA  = !_cfg.UseOrbCloseA || _orb.OrbBullClose;
        bool orbShortOkA = !_cfg.UseOrbCloseA || _orb.OrbBearClose;

        bool aboveVwapB  = !_cfg.UseVwapB || (_vwap.IsReady && bar.Close > _vwap.Value);
        bool belowVwapB  = !_cfg.UseVwapB || (_vwap.IsReady && bar.Close < _vwap.Value);
        bool orbLongOkB  = !_cfg.UseOrbCloseB || _orb.OrbBullClose;
        bool orbShortOkB = !_cfg.UseOrbCloseB || _orb.OrbBearClose;

        _log.LogDebug("Bar {Time} O={O} H={H} L={L} C={C} | VWAP={Vwap:F2} | ORB H={Oh:F2} L={Ol:F2} Range={Or:F2}",
            localTime, bar.Open, bar.High, bar.Low, bar.Close,
            _vwap.IsReady ? _vwap.Value : 0m,
            _orb.OrbHigh, _orb.OrbLow, _orb.OrbRange);

        _enteredThisBar = false;

        // Skip entry on the bar the ORB just formed — arm can evaluate, entry blocked
        if (_orbJustFormed)
        {
            _orbJustFormed = false;
            _log.LogDebug("ORB just formed this bar — arm OK, entry skipped");
            EvaluateArmState(bar, localTime);
            await PublishSnapshot(bar); return;
        }

        // Warmup cooldown: arm evaluates but entry blocked (prevents instant trades
        // on engine restart mid-session)
        if (_warmupCooldown > 0)
        {
            _warmupCooldown--;
            _log.LogInformation("Warmup cooldown — arm OK, entry blocked ({N} bars remaining)", _warmupCooldown);
            EvaluateArmState(bar, localTime);
            await PublishSnapshot(bar); return;
        }

        if (_cfg.EnableA && !_ddBreached && !_pastCutoffA)
            await ProcessSetupA(bar, aboveVwapA, belowVwapA, orbLongOkA, orbShortOkA);

        if (_cfg.EnableB && !_ddBreached && !_pastCutoffB)
            await ProcessSetupB(bar, aboveVwapB, belowVwapB, orbLongOkB, orbShortOkB);

        if (_cfg.EnableC && !_ddBreached && !_pastCutoffC)
            await ProcessSetupC(bar);
        if (_cfg.EnableD && !_ddBreached && !_pastCutoffD)
            await ProcessSetupD(bar);

        // ── Adverse excursion time check (bar mode) ────────────────
        var barTime = BarExitTime(bar.Time);
        if (IsAdverseTimeout(_activeA, _stA == 2, _entA, bar.Close, _entryTimeA, barTime, _cfg.MaxAdverseMinutesA))
        {
            _log.LogInformation("[A] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesA);
            AddAlert("ADVERSE", SetupId.A, $"Underwater >{_cfg.MaxAdverseMinutesA}min — exit", "orange");
            await ForceExitA(bar, ExitReason.AdverseTime);
        }
        if (IsAdverseTimeout(_activeB, _stB == 3, _entB, bar.Close, _entryTimeB, barTime, _cfg.MaxAdverseMinutesB))
        {
            _log.LogInformation("[B] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesB);
            AddAlert("ADVERSE", SetupId.B, $"Underwater >{_cfg.MaxAdverseMinutesB}min — exit", "orange");
            await ForceExitB(bar, ExitReason.AdverseTime);
        }
        if (IsAdverseTimeout(_activeC, _stC == 2, _entC, bar.Close, _entryTimeC, barTime, _cfg.MaxAdverseMinutesC))
        {
            _log.LogInformation("[C] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesC);
            AddAlert("ADVERSE", SetupId.C, $"Underwater >{_cfg.MaxAdverseMinutesC}min — exit", "orange");
            await ForceExitC(bar, ExitReason.AdverseTime);
        }
        if (IsAdverseTimeout(_activeD, _stD == 2, _entD, bar.Close, _entryTimeD, barTime, _cfg.MaxAdverseMinutesD))
        {
            _log.LogInformation("[D] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesD);
            AddAlert("ADVERSE", SetupId.D, $"Underwater >{_cfg.MaxAdverseMinutesD}min — exit", "orange");
            await ForceExitD(bar, ExitReason.AdverseTime);
        }

        if (rthJustEndedA || _forceExitA) { _forceExitA = false; await ForceExitA(bar); }
        if (rthJustEndedB || _forceExitB) { _forceExitB = false; await ForceExitB(bar); }
        if (rthJustEndedC || _forceExitC) { _forceExitC = false; await ForceExitC(bar); }
        if (rthJustEndedD || _forceExitD) { _forceExitD = false; await ForceExitD(bar); }

        await PublishSnapshot(bar);
    }

    // ── Setup A ───────────────────────────────────────────────
    private async Task ProcessSetupA(Bar bar, bool aboveVwap, bool belowVwap, bool orbLongOk, bool orbShortOk)
    {
        decimal orbHigh  = _orb.OrbHigh;
        decimal orbLow   = _orb.OrbLow;
        decimal orbRange = _orb.OrbRange;
        decimal tickTol  = _cfg.TickSize * 2;

        // ── Entry (runs when not yet active) ──────────────────────
        if (!_activeA)
        {
            if ((_stA == 1 && bar.Close < orbLow) || (_stA == -1 && bar.Close > orbHigh))
                { _stA = 0; _armEntryA = 0; }
            else
            {
                bool isReady = _tradeCountA < _cfg.MaxTradesA;

                // Arm — don't re-arm the same side until price leaves the arm zone
                if (isReady && _stA == 0)
                {
                    decimal nearDist = orbRange * _cfg.NearPctA;
                    // Clear directional lock once price leaves the arm zone
                    if (_bullTradedA && bar.High < orbHigh - nearDist) _bullTradedA = false;
                    if (_bearTradedA && bar.Low  > orbLow  + nearDist) _bearTradedA = false;

                    if (bar.High >= orbHigh - nearDist && orbLongOk && aboveVwap && !_bullTradedA)
                    {
                        _stA = 1; _armEntryA = bar.Open;
                        _log.LogDebug("Setup A armed LONG: OrbHigh={H:F2} NearDist={N:F2}", orbHigh, nearDist);
                        AddAlert("ARMED", SetupId.A, "A LONG armed", "gray");
                    }
                    else if (bar.Low <= orbLow + nearDist && orbShortOk && belowVwap && !_bearTradedA)
                    {
                        _stA = -1; _armEntryA = bar.Open;
                        _log.LogDebug("Setup A armed SHORT: OrbLow={L:F2} NearDist={N:F2}", orbLow, nearDist);
                        AddAlert("ARMED", SetupId.A, "A SHORT armed", "gray");
                    }
                }

                // Bar-level entry.  Normally in tick mode entries fire via
                // ProcessPriceTickAsync for finer execution.  However, on a fast
                // move the arm + pullback can both occur on the SAME bar, and by
                // the next tick price has already run past the pullback zone.
                // Allow bar-level entry in tick mode for this "same-bar" case.
                {
                    bool canEnterA  = _cfg.AllowBothSameBar || !_enteredThisBar;
                    decimal pbPts   = orbRange * _cfg.PullbackPct;
                    decimal longPb  = LevelCalculator.RoundToTick(orbHigh - pbPts, _cfg.TickSize);
                    decimal shortPb = LevelCalculator.RoundToTick(orbLow  + pbPts, _cfg.TickSize);

                    if (_cfg.IsAggressiveA)
                    {
                        if (_stA == 1 && canEnterA) await TryEntryA(bar, _armEntryA, true, orbRange);
                        else if (_stA == -1 && canEnterA) await TryEntryA(bar, _armEntryA, false, orbRange);
                    }
                    else
                    {
                        if (_stA == 1 && canEnterA && bar.Low <= longPb + tickTol)
                            await TryEntryA(bar, longPb, true, orbRange);
                        else if (_stA == -1 && canEnterA && bar.High >= shortPb - tickTol)
                            await TryEntryA(bar, shortPb, false, orbRange);
                    }
                }
            }
        }

        // ── Exit (bar-level fallback) ─────────────────────────────────────
        // In tick mode, exits normally fire via ProcessPriceTickAsync.
        // But bar-level exit is kept as a safety net — if the tick path
        // missed an exit (e.g. gap through stop/target), the bar catches it.
        if (_activeA)
        {
            bool isLong   = _stA == 2;
            bool prevPart = _partHitA;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entA, _stopA, _tgtA, _partialA,
                _ctsA, _pnlA, _partHitA,
                _cfg.UsePartialA, _cfg.UseBeA, _cfg.PointValue,
                bar.High, bar.Low, _cfg.PartialCtsA);

            _pnlA     = result.NewPnl;
            _activeA  = result.StillActive;
            _partHitA = result.PartialHit;
            _stopA    = result.NewStop;

            bool partJustHit = _partHitA && !prevPart;
            bool closingNow  = result.HitTarget || result.HitStop;

            // Fire partial/BE broker signals ONLY when partial fires on a bar where the trade
            // stays open. If partial AND close happen on the same bar the broker's bracket is
            // still fully in place — we'll close the full position in OnExitSignalAsync.
            if (partJustHit && !closingNow)
            {
                int half = CalcPartialCts(_ctsA, _cfg.PartialCtsA);
                int remaining = _ctsA - half;
                if (half > 0)
                {
                    var psig = new PartialSignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
                        _partialA, half, remaining, _entA, bar.Time);
                    await _executor.OnPartialSignalAsync(psig);
                    await _sink.OnPartialAsync(psig);
                    AddAlert("PARTIAL", SetupId.A, $"Partial @ {_partialA:F2}", "yellow");

                    if (_cfg.UseBeA)
                    {
                        var besig = new BESignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
                            _entA, _entA, remaining, bar.Time);
                        await _executor.OnBESignalAsync(besig);
                        await _sink.OnBEMoveAsync(besig);
                        AddAlert("MOVE_BE", SetupId.A, $"Stop → BE {_entA:F2}", "yellow");
                    }
                    else
                    {
                        await _executor.OnLevelsAdjustedAsync(SetupId.A, _stopA, _tgtA, remaining);
                    }
                }
            }

            if (closingNow)
            {
                _stA = 0; _tradeCountA++;
                var exitPxA = result.HitTarget ? _tgtA : _stopA;
                bool isBEa = _cfg.AllowRearmAfterBeA && !result.HitTarget && exitPxA == _entA;
                if (!isBEa) { if (isLong) _bullTradedA = true; else _bearTradedA = true; }
                if (result.HitTarget) { _stickyTgtA = true; _exitBarIdxA = _barIndex; }
                else                  { _stickyStpA = true; _exitBarIdxA = _barIndex; }
                await BookExitA(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    exitPxA, BarExitTime(bar.Time), isLong,
                    sameBarPartial: partJustHit);
            }
        }
    }

    private async Task TryEntryA(Bar bar, decimal ep, bool isLong, decimal orbRange)
    {
        if (HasOpposingPosition(isLong)) return;
        // Apply entry tick offset: Long → price + ticks, Short → price - ticks
        if (_cfg.EntryTickOffsetA != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffsetA * _cfg.TickSize;
            ep = isLong ? ep + offset : ep - offset;
            ep = LevelCalculator.RoundToTick(ep, _cfg.TickSize);
        }

        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPctA, _cfg.TargetPctA, _cfg.PartialPctA, orbRange, _cfg.TickSize);
        if (rr < _cfg.MinRrA) return;

        _entA = ep; _stopA = sl; _tgtA = tp; _partialA = pp;
        _initStopA = sl; _pnlA = 0; _activeA = true;
        _ctsA = CalcContracts(_cfg.ContractsA, _cfg.HiVolMultA, _cfg.MaxContractsA);
        _stA = isLong ? 2 : -2; _enteredThisBar = true;
        _entryTimeA = BarExitTime(bar.Time);

        _log.LogInformation("[Setup A] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _ctsA, ep, sl, tp, rr);
        if (IsEntryPriceStale(SetupId.A, ep)) return;
        var sig = new EntrySignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _ctsA, bar.Time, _cfg.OrderTypeA);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.A, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.A,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsA}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task BookExitA(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _ctsA * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlA - comm;
        decimal risk = Math.Abs(_entA - _initStopA) * _cfg.PointValue * _ctsA;
        decimal rMult = risk > 0 ? _pnlA / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.A, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _ctsA,
            Entry = _entA, InitialStop = _initStopA, Target = _tgtA, Partial = _partialA,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitA, PartialPrice = _partialA,   // correctly true for same-bar partial too
            GrossPnl = _pnlA, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeA, ExitedAt = time,
            SessionId = string.IsNullOrEmpty(_activeSessionId) ? "NY" : _activeSessionId
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        CheckDailyLossLimit();
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsA++;   _todayWinPnlA  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesA++; _todayLossPnlA += net; }

        _log.LogInformation("[Setup A] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        // Broker contract count:
        //   • Prior-bar partial:     partial order was already placed → only half remains → send half.
        //   • Same-bar partial+close: partial/BE signals were suppressed → full bracket still in place → send all.
        //   • No partial:            send all.
        int remCtsA = (_partHitA && _cfg.UsePartialA && !sameBarPartial)
            ? _ctsA - CalcPartialCts(_ctsA, _cfg.PartialCtsA)
            : _ctsA;
        var sig = new ExitSignal(SetupId.A, reason, exitPx, remCtsA, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.A,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeA = false; _stA = 0; _partHitA = false; _pnlA = 0;
        // Adverse-time exit: block re-arming for the rest of this session
        if (reason == ExitReason.AdverseTime)
        {
            _tradeCountA++;
            _pastCutoffA = true;
        }
    }

    // ── Setup B ───────────────────────────────────────────────
    private async Task ProcessSetupB(Bar bar, bool aboveVwap, bool belowVwap, bool orbLongOk, bool orbShortOk)
    {
        decimal orbHigh  = _orb.OrbHigh;
        decimal orbLow   = _orb.OrbLow;
        decimal orbRange = _orb.OrbRange;

        // ── Entry (runs when not yet active) ──────────────────────
        if (!_activeB)
        {
            if ((_stB > 0 && _stB < 3 && bar.Close < orbLow) ||
                     (_stB < 0 && _stB > -3 && bar.Close > orbHigh))
                { _stB = 0; _armEntryB = 0; }
            else
            {
                bool isReady   = _tradeCountB < _cfg.MaxTradesB;
                bool canEnterB = _cfg.AllowBothSameBar || !_enteredThisBar;

                // Breakout detection (NearPctB allows arming when price is near the ORB level)
                if (isReady && _stB == 0)
                {
                    decimal nearDistB = orbRange * _cfg.NearPctB;
                    // Clear directional lock once price leaves the arm zone
                    // (price pulled back → next approach is a fresh setup)
                    if (_bullTradedB && bar.Close < orbHigh - nearDistB) _bullTradedB = false;
                    if (_bearTradedB && bar.Close > orbLow  + nearDistB) _bearTradedB = false;

                    if (bar.Close >= orbHigh - nearDistB && orbLongOk && aboveVwap && !_bullTradedB)
                    { _stB = 1; _armEntryB = bar.Open; AddAlert("ARMED", SetupId.B, "B Bull break", "gray"); }
                    else if (bar.Close <= orbLow + nearDistB && orbShortOk && belowVwap && !_bearTradedB)
                    { _stB = -1; _armEntryB = bar.Open; AddAlert("ARMED", SetupId.B, "B Bear break", "gray"); }
                }

                if (_cfg.IsAggressiveB)
                {
                    // Bar-level entry.  Allow in tick mode for same-bar arm+entry
                    // (on fast breakouts the next tick may already be past the entry).
                    if (_stB == 1 && canEnterB)
                    {
                        decimal ep = _armEntryB > 0 ? _armEntryB : orbHigh;
                        await TryEntryB(bar, ep, true, orbRange);
                    }
                    else if (_stB == -1 && canEnterB)
                    {
                        decimal ep = _armEntryB > 0 ? _armEntryB : orbLow;
                        await TryEntryB(bar, ep, false, orbRange);
                    }
                }
                else
                {
                    // Conservative — retest-zone state transitions always run (arm → retest → de-arm)
                    decimal retestW = orbRange * _cfg.RetestPct;
                    if (_stB == 1 && bar.Low <= orbHigh + retestW && bar.High >= orbHigh - retestW) _stB = 2;
                    if (_stB == -1 && bar.High >= orbLow - retestW && bar.Low <= orbLow + retestW) _stB = -2;

                    // Bar-level entry.  In tick mode the entry normally fires via
                    // ProcessPriceTickAsync on the next tick.  However, on a fast
                    // breakout the arm+retest+entry may all occur on the SAME bar,
                    // and by the time the next tick arrives price has already run
                    // past the retest zone.  Allow bar-level entry in tick mode when
                    // this "same-bar completion" occurs so we don't miss the trade.
                    if (_stB == 2 && bar.Close > orbHigh && canEnterB)
                        await TryEntryB(bar, orbHigh, true, orbRange);
                    else if (_stB == -2 && bar.Close < orbLow && canEnterB)
                        await TryEntryB(bar, orbLow, false, orbRange);

                    if (_stB == 2  && bar.Close < _orb.OrbMid) _stB = 0;
                    if (_stB == -2 && bar.Close > _orb.OrbMid) _stB = 0;
                }
            }
        }

        // ── Exit (bar-level fallback — safety net when tick path misses) ──
        if (_activeB)
        {
            bool isLong   = _stB == 3;
            bool prevPart = _partHitB;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entB, _stopB, _tgtB, _partialB,
                _ctsB, _pnlB, _partHitB,
                _cfg.UsePartialB, _cfg.UseBeB, _cfg.PointValue,
                bar.High, bar.Low, _cfg.PartialCtsB);

            _pnlB     = result.NewPnl;
            _activeB  = result.StillActive;
            _partHitB = result.PartialHit;
            _stopB    = result.NewStop;

            bool partJustHit = _partHitB && !prevPart;
            bool closingNow  = result.HitTarget || result.HitStop;

            // Fire partial/BE broker signals ONLY when partial fires on a bar where the trade
            // stays open. If partial AND close happen on the same bar the broker's bracket is
            // still fully in place — we'll close the full position in OnExitSignalAsync.
            if (partJustHit && !closingNow)
            {
                int half = CalcPartialCts(_ctsB, _cfg.PartialCtsB);
                int remaining = _ctsB - half;
                if (half > 0)
                {
                    var psig = new PartialSignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
                        _partialB, half, remaining, _entB, bar.Time);
                    await _executor.OnPartialSignalAsync(psig);
                    await _sink.OnPartialAsync(psig);
                    AddAlert("PARTIAL", SetupId.B, $"Partial @ {_partialB:F2}", "yellow");

                    if (_cfg.UseBeB)
                    {
                        var besig = new BESignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
                            _entB, _entB, remaining, bar.Time);
                        await _executor.OnBESignalAsync(besig);
                        await _sink.OnBEMoveAsync(besig);
                        AddAlert("MOVE_BE", SetupId.B, $"Stop → BE {_entB:F2}", "yellow");
                    }
                    else
                    {
                        await _executor.OnLevelsAdjustedAsync(SetupId.B, _stopB, _tgtB, remaining);
                    }
                }
            }

            if (closingNow)
            {
                _stB = 0; _tradeCountB++;
                var exitPxB = result.HitTarget ? _tgtB : _stopB;
                bool isBEb = _cfg.AllowRearmAfterBeB && !result.HitTarget && exitPxB == _entB;
                if (!isBEb) { if (isLong) _bullTradedB = true; else _bearTradedB = true; }
                if (result.HitTarget) { _stickyTgtB = true; _exitBarIdxB = _barIndex; }
                else                  { _stickyStpB = true; _exitBarIdxB = _barIndex; }
                await BookExitB(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    exitPxB, BarExitTime(bar.Time), isLong,
                    sameBarPartial: partJustHit);
            }
        }
    }

    private async Task TryEntryB(Bar bar, decimal ep, bool isLong, decimal orbRange)
    {
        if (HasOpposingPosition(isLong)) return;
        // Apply entry tick offset: Long → price + ticks, Short → price - ticks
        if (_cfg.EntryTickOffsetB != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffsetB * _cfg.TickSize;
            ep = isLong ? ep + offset : ep - offset;
            ep = LevelCalculator.RoundToTick(ep, _cfg.TickSize);
        }

        var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
            _cfg.TargetPctB, _cfg.PartialPctB, orbRange, _cfg.StopPctB, _cfg.TickSize);
        if (rr < _cfg.MinRrB) return;

        _entB = ep; _stopB = sl; _tgtB = tp; _partialB = pp;
        _initStopB = sl; _pnlB = 0; _activeB = true;
        _ctsB = CalcContracts(_cfg.ContractsB, _cfg.HiVolMultB, _cfg.MaxContractsB);
        _stB = isLong ? 3 : -3; _enteredThisBar = true;
        _entryTimeB = BarExitTime(bar.Time);

        _log.LogInformation("[Setup B] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _ctsB, ep, sl, tp, rr);
        if (IsEntryPriceStale(SetupId.B, ep)) return;
        var sig = new EntrySignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _ctsB, bar.Time, _cfg.OrderTypeB);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.B, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.B,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsB}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task BookExitB(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _ctsB * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlB - comm;
        decimal risk = Math.Abs(_entB - _initStopB) * _cfg.PointValue * _ctsB;
        decimal rMult = risk > 0 ? _pnlB / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.B, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _ctsB,
            Entry = _entB, InitialStop = _initStopB, Target = _tgtB, Partial = _partialB,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitB, PartialPrice = _partialB,   // correctly true for same-bar partial too
            GrossPnl = _pnlB, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeB, ExitedAt = time,
            SessionId = string.IsNullOrEmpty(_activeSessionId) ? "NY" : _activeSessionId
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        CheckDailyLossLimit();
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsB++;   _todayWinPnlB  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesB++; _todayLossPnlB += net; }

        _log.LogInformation("[Setup B] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        // Broker contract count:
        //   • Prior-bar partial:     partial order was already placed → only half remains → send half.
        //   • Same-bar partial+close: partial/BE signals were suppressed → full bracket still in place → send all.
        //   • No partial:            send all.
        int remCtsB = (_partHitB && _cfg.UsePartialB && !sameBarPartial)
            ? _ctsB - CalcPartialCts(_ctsB, _cfg.PartialCtsB)
            : _ctsB;
        var sig = new ExitSignal(SetupId.B, reason, exitPx, remCtsB, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.B,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeB = false; _stB = 0; _partHitB = false; _pnlB = 0;
        // Adverse-time exit: block re-arming for the rest of this session
        if (reason == ExitReason.AdverseTime)
        {
            _tradeCountB++;
            _pastCutoffB = true;
        }
    }

    // ── Setup C (ORB False Breakout) ─────────────────────────────
    private async Task ProcessSetupC(Bar bar)
    {
        if (!_activeC)
        {
            // Clear sticky markers
            if (_exitBarIdxC != -1 && _barIndex > _exitBarIdxC)
                { _stickyTgtC = false; _stickyStpC = false; _exitBarIdxC = -1; }

            // Force-exit flag
            if (_forceExitC) { _forceExitC = false; return; }

            // Check activation: false breakout on ORB
            if (_stC == 0 && _tradeCountC < _cfg.MaxTradesC && _falseBreakout.OrbTracker.IsActivated)
            {
                // Direction is OPPOSITE of breakout
                bool breakoutLong = _falseBreakout.OrbTracker.BreakoutDirection == Direction.Long;
                _stC = breakoutLong ? -1 : 1; // short if breakout was long, long if breakout was short
                _armEntryC = bar.Close;
                _falseBreakout.OrbTracker.ClearActivation();
                AddAlert("ARMED", SetupId.C, $"C {(_stC == 1 ? "LONG" : "SHORT")} armed (ORB false break)", "gray");
            }

            // Bar-level entry
            if (_stC == 1 || _stC == -1)
            {
                bool isLong = _stC == 1;
                bool canEnter = _cfg.AllowBothSameBar || !_enteredThisBar;
                if (canEnter)
                {
                    // Long: price crosses above ORB low (after false break below)
                    // Short: price crosses below ORB high (after false break above)
                    if (isLong && bar.Close >= _orb.OrbLow)
                        await TryEntryC(bar, _orb.OrbLow, true);
                    else if (!isLong && bar.Close <= _orb.OrbHigh)
                        await TryEntryC(bar, _orb.OrbHigh, false);
                }
            }
        }

        // Exit (bar-level fallback)
        if (_activeC)
        {
            bool isLong   = _stC == 2;
            bool prevPart = _partHitC;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entC, _stopC, _tgtC, _partialC,
                _ctsC, _pnlC, _partHitC,
                _cfg.UsePartialC, _cfg.UseBeC, _cfg.PointValue,
                bar.High, bar.Low, _cfg.PartialCtsC);

            _pnlC     = result.NewPnl;
            _activeC  = result.StillActive;
            _partHitC = result.PartialHit;
            _stopC    = result.NewStop;

            bool partJustHit = _partHitC && !prevPart;
            bool closingNow  = result.HitTarget || result.HitStop;

            if (partJustHit && !closingNow)
            {
                int half = CalcPartialCts(_ctsC, _cfg.PartialCtsC);
                int remaining = _ctsC - half;
                if (half > 0)
                {
                    var psig = new PartialSignal(SetupId.C, isLong ? Direction.Long : Direction.Short,
                        _partialC, half, remaining, _entC, bar.Time);
                    await _executor.OnPartialSignalAsync(psig);
                    await _sink.OnPartialAsync(psig);
                    AddAlert("PARTIAL", SetupId.C, $"Partial @ {_partialC:F2}", "yellow");

                    if (_cfg.UseBeC)
                    {
                        var besig = new BESignal(SetupId.C, isLong ? Direction.Long : Direction.Short,
                            _entC, _entC, remaining, bar.Time);
                        _stopC = _entC;
                        await _executor.OnBESignalAsync(besig);
                        await _sink.OnBEMoveAsync(besig);
                        AddAlert("MOVE_BE", SetupId.C, $"Stop → BE {_entC:F2}", "yellow");
                    }
                    else
                    {
                        await _executor.OnLevelsAdjustedAsync(SetupId.C, _stopC, _tgtC, remaining);
                    }
                }
            }

            if (closingNow)
            {
                _stC = 0; _tradeCountC++;
                var exitPxC = result.HitTarget ? _tgtC : _stopC;
                bool isBEc = _cfg.AllowRearmAfterBeC && !result.HitTarget && exitPxC == _entC;
                if (!isBEc) { if (isLong) _bullTradedC = true; else _bearTradedC = true; }
                if (result.HitTarget) { _stickyTgtC = true; _exitBarIdxC = _barIndex; }
                else                  { _stickyStpC = true; _exitBarIdxC = _barIndex; }
                await BookExitC(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    exitPxC, BarExitTime(bar.Time), isLong,
                    sameBarPartial: partJustHit);
            }
        }
    }

    private async Task TryEntryC(Bar bar, decimal ep, bool isLong)
    {
        if (HasOpposingPosition(isLong)) return;
        decimal orbRange = _orb.OrbRange;
        if (_cfg.EntryTickOffsetC != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffsetC * _cfg.TickSize;
            ep = isLong ? ep + offset : ep - offset;
            ep = LevelCalculator.RoundToTick(ep, _cfg.TickSize);
        }

        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPctC, _cfg.TargetPctC, _cfg.PartialPctC, orbRange, _cfg.TickSize);
        if (rr < _cfg.MinRrC) return;

        _entC = ep; _stopC = sl; _tgtC = tp; _partialC = pp;
        _initStopC = sl; _pnlC = 0; _activeC = true;
        _ctsC = CalcContracts(_cfg.ContractsC, _cfg.HiVolMultC, _cfg.MaxContractsC);
        _stC = isLong ? 2 : -2; _enteredThisBar = true;
        _entryTimeC = BarExitTime(bar.Time);

        _log.LogInformation("[Setup C] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _ctsC, ep, sl, tp, rr);
        if (IsEntryPriceStale(SetupId.C, ep)) return;
        var sig = new EntrySignal(SetupId.C, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _ctsC, bar.Time, _cfg.OrderTypeC);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.C, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.C,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsC}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    // ── Setup D (Session Range False Breakout) ─────────────────
    private async Task ProcessSetupD(Bar bar)
    {
        if (!_activeD)
        {
            // Clear sticky markers
            if (_exitBarIdxD != -1 && _barIndex > _exitBarIdxD)
                { _stickyTgtD = false; _stickyStpD = false; _exitBarIdxD = -1; }

            // Force-exit flag
            if (_forceExitD) { _forceExitD = false; return; }

            // Check activation: false breakout on session range
            if (_stD == 0 && _tradeCountD < _cfg.MaxTradesD && _falseBreakout.SessionRangeTracker.IsActivated)
            {
                // Direction is OPPOSITE of breakout
                bool breakoutLong = _falseBreakout.SessionRangeTracker.BreakoutDirection == Direction.Long;
                _stD = breakoutLong ? -1 : 1;
                _armEntryD = bar.Close;
                _falseBreakout.SessionRangeTracker.ClearActivation();
                AddAlert("ARMED", SetupId.D, $"D {(_stD == 1 ? "LONG" : "SHORT")} armed (session range false break)", "gray");
            }

            // Bar-level entry
            if (_stD == 1 || _stD == -1)
            {
                bool isLong = _stD == 1;
                bool canEnter = _cfg.AllowBothSameBar || !_enteredThisBar;
                if (canEnter)
                {
                    var srLow  = _falseBreakout.SessionRangeTracker.RangeLow;
                    var srHigh = _falseBreakout.SessionRangeTracker.RangeHigh;
                    if (isLong && bar.Close >= srLow)
                        await TryEntryD(bar, srLow, true);
                    else if (!isLong && bar.Close <= srHigh)
                        await TryEntryD(bar, srHigh, false);
                }
            }
        }

        // Exit (bar-level fallback)
        if (_activeD)
        {
            bool isLong   = _stD == 2;
            bool prevPart = _partHitD;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entD, _stopD, _tgtD, _partialD,
                _ctsD, _pnlD, _partHitD,
                _cfg.UsePartialD, _cfg.UseBeD, _cfg.PointValue,
                bar.High, bar.Low, _cfg.PartialCtsD);

            _pnlD     = result.NewPnl;
            _activeD  = result.StillActive;
            _partHitD = result.PartialHit;
            _stopD    = result.NewStop;

            bool partJustHit = _partHitD && !prevPart;
            bool closingNow  = result.HitTarget || result.HitStop;

            if (partJustHit && !closingNow)
            {
                int half = CalcPartialCts(_ctsD, _cfg.PartialCtsD);
                int remaining = _ctsD - half;
                if (half > 0)
                {
                    var psig = new PartialSignal(SetupId.D, isLong ? Direction.Long : Direction.Short,
                        _partialD, half, remaining, _entD, bar.Time);
                    await _executor.OnPartialSignalAsync(psig);
                    await _sink.OnPartialAsync(psig);
                    AddAlert("PARTIAL", SetupId.D, $"Partial @ {_partialD:F2}", "yellow");

                    if (_cfg.UseBeD)
                    {
                        var besig = new BESignal(SetupId.D, isLong ? Direction.Long : Direction.Short,
                            _entD, _entD, remaining, bar.Time);
                        _stopD = _entD;
                        await _executor.OnBESignalAsync(besig);
                        await _sink.OnBEMoveAsync(besig);
                        AddAlert("MOVE_BE", SetupId.D, $"Stop → BE {_entD:F2}", "yellow");
                    }
                    else
                    {
                        await _executor.OnLevelsAdjustedAsync(SetupId.D, _stopD, _tgtD, remaining);
                    }
                }
            }

            if (closingNow)
            {
                _stD = 0; _tradeCountD++;
                var exitPxD = result.HitTarget ? _tgtD : _stopD;
                bool isBEd = _cfg.AllowRearmAfterBeD && !result.HitTarget && exitPxD == _entD;
                if (!isBEd) { if (isLong) _bullTradedD = true; else _bearTradedD = true; }
                if (result.HitTarget) { _stickyTgtD = true; _exitBarIdxD = _barIndex; }
                else                  { _stickyStpD = true; _exitBarIdxD = _barIndex; }
                await BookExitD(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    exitPxD, BarExitTime(bar.Time), isLong,
                    sameBarPartial: partJustHit);
            }
        }
    }

    private async Task TryEntryD(Bar bar, decimal ep, bool isLong)
    {
        if (HasOpposingPosition(isLong)) return;
        decimal rangeSize = _falseBreakout.SessionRangeTracker.RangeHigh - _falseBreakout.SessionRangeTracker.RangeLow;
        if (rangeSize <= 0) rangeSize = _orb.OrbRange;
        if (_cfg.EntryTickOffsetD != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffsetD * _cfg.TickSize;
            ep = isLong ? ep + offset : ep - offset;
            ep = LevelCalculator.RoundToTick(ep, _cfg.TickSize);
        }

        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPctD, _cfg.TargetPctD, _cfg.PartialPctD, rangeSize, _cfg.TickSize);
        if (rr < _cfg.MinRrD) return;

        _entD = ep; _stopD = sl; _tgtD = tp; _partialD = pp;
        _initStopD = sl; _pnlD = 0; _activeD = true;
        _ctsD = CalcContracts(_cfg.ContractsD, _cfg.HiVolMultD, _cfg.MaxContractsD);
        _stD = isLong ? 2 : -2; _enteredThisBar = true;
        _entryTimeD = BarExitTime(bar.Time);

        _log.LogInformation("[Setup D] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _ctsD, ep, sl, tp, rr);
        if (IsEntryPriceStale(SetupId.D, ep)) return;
        var sig = new EntrySignal(SetupId.D, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _ctsD, bar.Time, _cfg.OrderTypeD);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.D, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.D,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsD}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task BookExitC(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _ctsC * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlC - comm;
        decimal risk = Math.Abs(_entC - _initStopC) * _cfg.PointValue * _ctsC;
        decimal rMult = risk > 0 ? _pnlC / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.C, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _ctsC,
            Entry = _entC, InitialStop = _initStopC, Target = _tgtC, Partial = _partialC,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitC, PartialPrice = _partialC,
            GrossPnl = _pnlC, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeC, ExitedAt = time,
            SessionId = string.IsNullOrEmpty(_activeSessionId) ? "NY" : _activeSessionId
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        CheckDailyLossLimit();
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsC++;   _todayWinPnlC  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesC++; _todayLossPnlC += net; }

        _log.LogInformation("[Setup C] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        int remCtsC = (_partHitC && _cfg.UsePartialC && !sameBarPartial)
            ? _ctsC - CalcPartialCts(_ctsC, _cfg.PartialCtsC)
            : _ctsC;
        var sig = new ExitSignal(SetupId.C, reason, exitPx, remCtsC, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.C,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeC = false; _stC = 0; _partHitC = false; _pnlC = 0;
        if (reason == ExitReason.AdverseTime)
        {
            _tradeCountC++;
            _pastCutoffC = true;
        }
    }

    private async Task BookExitD(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _ctsD * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlD - comm;
        decimal risk = Math.Abs(_entD - _initStopD) * _cfg.PointValue * _ctsD;
        decimal rMult = risk > 0 ? _pnlD / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.D, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _ctsD,
            Entry = _entD, InitialStop = _initStopD, Target = _tgtD, Partial = _partialD,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitD, PartialPrice = _partialD,
            GrossPnl = _pnlD, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeD, ExitedAt = time,
            SessionId = string.IsNullOrEmpty(_activeSessionId) ? "NY" : _activeSessionId
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        CheckDailyLossLimit();
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsD++;   _todayWinPnlD  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesD++; _todayLossPnlD += net; }

        _log.LogInformation("[Setup D] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        int remCtsD = (_partHitD && _cfg.UsePartialD && !sameBarPartial)
            ? _ctsD - CalcPartialCts(_ctsD, _cfg.PartialCtsD)
            : _ctsD;
        var sig = new ExitSignal(SetupId.D, reason, exitPx, remCtsD, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.D,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeD = false; _stD = 0; _partHitD = false; _pnlD = 0;
        if (reason == ExitReason.AdverseTime)
        {
            _tradeCountD++;
            _pastCutoffD = true;
        }
    }

    private async Task ForceExitC(Bar bar, ExitReason reason = ExitReason.SessionEnd)
    {
        if (!_activeC) return;
        bool isLong = _stC == 2;
        _pnlC += ExitProcessor.ForcedExit(isLong, _entC, bar.Close,
            _ctsC, _partHitC, _cfg.UsePartialC, _cfg.PointValue, _cfg.PartialCtsC);
        await BookExitC(reason, bar.Close, BarExitTime(bar.Time), isLong);
    }

    private async Task ForceExitD(Bar bar, ExitReason reason = ExitReason.SessionEnd)
    {
        if (!_activeD) return;
        bool isLong = _stD == 2;
        _pnlD += ExitProcessor.ForcedExit(isLong, _entD, bar.Close,
            _ctsD, _partHitD, _cfg.UsePartialD, _cfg.PointValue, _cfg.PartialCtsD);
        await BookExitD(reason, bar.Close, BarExitTime(bar.Time), isLong);
    }

    // ── Tick-mode entry/exit for Setup C ───────────────────────────
    private async Task EvalTickSetupC(decimal price, DateTime utcTime)
    {
        // ── Entry: armed but not yet active ─────────────────────
        if (!_activeC && (_stC == 1 || _stC == -1))
        {
            bool isLong = _stC == 1;
            // Long: price crosses above ORB low (after false break below)
            // Short: price crosses below ORB high (after false break above)
            if (isLong && price >= _orb.OrbLow)
                await TryEntryCFromTick(_orb.OrbLow, true, utcTime);
            else if (!isLong && price <= _orb.OrbHigh)
                await TryEntryCFromTick(_orb.OrbHigh, false, utcTime);
            return;
        }

        // ── Exit: active position ────────────────────────────────
        if (!_activeC) return;
        bool long_ = _stC == 2;
        bool hitStop      = long_ ? price <= _stopC    : price >= _stopC;
        bool hitTarget    = long_ ? price >= _tgtC     : price <= _tgtC;
        bool hitPartialPx = _cfg.UsePartialC && !_partHitC &&
                            (long_ ? price >= _partialC : price <= _partialC);
        if (!hitStop && !hitTarget && !hitPartialPx) return;

        bool partJustHit = false;
        if (hitPartialPx && !hitTarget)
        {
            int half    = CalcPartialCts(_ctsC, _cfg.PartialCtsC);
            int remaining = _ctsC - half;
            if (half > 0)
            {
                partJustHit = true;
                _partHitC   = true;
                _pnlC += (long_ ? _partialC - _entC : _entC - _partialC) * _cfg.PointValue * half;
                var psig    = new PartialSignal(SetupId.C, long_ ? Direction.Long : Direction.Short,
                    _partialC, half, remaining, _entC, utcTime);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.C, $"Partial @ {_partialC:F2}", "yellow");

                if (_cfg.UseBeC)
                {
                    var besig = new BESignal(SetupId.C, long_ ? Direction.Long : Direction.Short,
                        _entC, _entC, remaining, utcTime);
                    _stopC = _entC;
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.C, $"Stop → BE {_entC:F2}", "yellow");
                }
                else
                {
                    await _executor.OnLevelsAdjustedAsync(SetupId.C, _stopC, _tgtC, remaining);
                }
            }
            if (!hitTarget && !hitStop) return;
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _tgtC : _stopC;
        int remCts = (_partHitC && _cfg.UsePartialC)
            ? _ctsC - CalcPartialCts(_ctsC, _cfg.PartialCtsC) : _ctsC;
        decimal pnl = _pnlC + (long_ ? exitPx - _entC : _entC - exitPx) * _cfg.PointValue * remCts;
        _pnlC    = pnl;
        _activeC = false;
        _stC     = 0;
        _tradeCountC++;
        bool isBE = _cfg.AllowRearmAfterBeC && reason == ExitReason.Stop && exitPx == _entC;
        if (!isBE) { if (long_) _bullTradedC = true; else _bearTradedC = true; }
        if (hitTarget) { _stickyTgtC = true; _exitBarIdxC = _barIndex; }
        else           { _stickyStpC = true; _exitBarIdxC = _barIndex; }
        await BookExitC(reason, exitPx, utcTime, long_, sameBarPartial: partJustHit);
    }

    private async Task TryEntryCFromTick(decimal ep, bool isLong, DateTime utcTime)
    {
        if (HasOpposingPosition(isLong)) return;
        decimal orbRange = _orb.OrbRange;

        // NearPct guard
        decimal nearDist = orbRange * _cfg.NearPctC;
        if (isLong && ep > _orb.OrbLow + nearDist) return;
        if (!isLong && ep < _orb.OrbHigh - nearDist) return;

        if (_cfg.EntryTickOffsetC != 0 && _cfg.TickSize > 0)
        {
            decimal off = _cfg.EntryTickOffsetC * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + off : ep - off, _cfg.TickSize);
        }
        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPctC, _cfg.TargetPctC, _cfg.PartialPctC, orbRange, _cfg.TickSize);
        if (rr < _cfg.MinRrC) return;

        _entC = ep; _stopC = sl; _tgtC = tp; _partialC = pp;
        _initStopC = sl; _pnlC = 0; _activeC = true;
        _ctsC = CalcContracts(_cfg.ContractsC, _cfg.HiVolMultC, _cfg.MaxContractsC);
        _stC = isLong ? 2 : -2; _enteredThisBar = true;
        _entryTimeC = utcTime;

        _log.LogInformation("[Setup C TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _ctsC, ep, sl, tp, rr);
        if (IsEntryPriceStale(SetupId.C, ep)) return;
        var sig = new EntrySignal(SetupId.C, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _ctsC, utcTime, _cfg.OrderTypeC);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.C, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.C,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsC}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    // ── Tick-mode entry/exit for Setup D ───────────────────────────
    private async Task EvalTickSetupD(decimal price, DateTime utcTime)
    {
        // ── Entry: armed but not yet active ─────────────────────
        if (!_activeD && (_stD == 1 || _stD == -1))
        {
            bool isLong = _stD == 1;
            var srLow  = _falseBreakout.SessionRangeTracker.RangeLow;
            var srHigh = _falseBreakout.SessionRangeTracker.RangeHigh;
            // Long: price crosses above session range low (after false break below)
            // Short: price crosses below session range high (after false break above)
            if (isLong && price >= srLow)
                await TryEntryDFromTick(srLow, true, utcTime);
            else if (!isLong && price <= srHigh)
                await TryEntryDFromTick(srHigh, false, utcTime);
            return;
        }

        // ── Exit: active position ────────────────────────────────
        if (!_activeD) return;
        bool long_ = _stD == 2;
        bool hitStop      = long_ ? price <= _stopD    : price >= _stopD;
        bool hitTarget    = long_ ? price >= _tgtD     : price <= _tgtD;
        bool hitPartialPx = _cfg.UsePartialD && !_partHitD &&
                            (long_ ? price >= _partialD : price <= _partialD);
        if (!hitStop && !hitTarget && !hitPartialPx) return;

        bool partJustHit = false;
        if (hitPartialPx && !hitTarget)
        {
            int half    = CalcPartialCts(_ctsD, _cfg.PartialCtsD);
            int remaining = _ctsD - half;
            if (half > 0)
            {
                partJustHit = true;
                _partHitD   = true;
                _pnlD += (long_ ? _partialD - _entD : _entD - _partialD) * _cfg.PointValue * half;
                var psig    = new PartialSignal(SetupId.D, long_ ? Direction.Long : Direction.Short,
                    _partialD, half, remaining, _entD, utcTime);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.D, $"Partial @ {_partialD:F2}", "yellow");

                if (_cfg.UseBeD)
                {
                    var besig = new BESignal(SetupId.D, long_ ? Direction.Long : Direction.Short,
                        _entD, _entD, remaining, utcTime);
                    _stopD = _entD;
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.D, $"Stop → BE {_entD:F2}", "yellow");
                }
                else
                {
                    await _executor.OnLevelsAdjustedAsync(SetupId.D, _stopD, _tgtD, remaining);
                }
            }
            if (!hitTarget && !hitStop) return;
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _tgtD : _stopD;
        int remCts = (_partHitD && _cfg.UsePartialD)
            ? _ctsD - CalcPartialCts(_ctsD, _cfg.PartialCtsD) : _ctsD;
        decimal pnl = _pnlD + (long_ ? exitPx - _entD : _entD - exitPx) * _cfg.PointValue * remCts;
        _pnlD    = pnl;
        _activeD = false;
        _stD     = 0;
        _tradeCountD++;
        bool isBE = _cfg.AllowRearmAfterBeD && reason == ExitReason.Stop && exitPx == _entD;
        if (!isBE) { if (long_) _bullTradedD = true; else _bearTradedD = true; }
        if (hitTarget) { _stickyTgtD = true; _exitBarIdxD = _barIndex; }
        else           { _stickyStpD = true; _exitBarIdxD = _barIndex; }
        await BookExitD(reason, exitPx, utcTime, long_, sameBarPartial: partJustHit);
    }

    private async Task TryEntryDFromTick(decimal ep, bool isLong, DateTime utcTime)
    {
        if (HasOpposingPosition(isLong)) return;
        decimal rangeSize = _falseBreakout.SessionRangeTracker.RangeHigh - _falseBreakout.SessionRangeTracker.RangeLow;
        if (rangeSize <= 0) rangeSize = _orb.OrbRange;

        // NearPct guard
        decimal nearDist = rangeSize * _cfg.NearPctD;
        var srLow  = _falseBreakout.SessionRangeTracker.RangeLow;
        var srHigh = _falseBreakout.SessionRangeTracker.RangeHigh;
        if (isLong && ep > srLow + nearDist) return;
        if (!isLong && ep < srHigh - nearDist) return;

        if (_cfg.EntryTickOffsetD != 0 && _cfg.TickSize > 0)
        {
            decimal off = _cfg.EntryTickOffsetD * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + off : ep - off, _cfg.TickSize);
        }
        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPctD, _cfg.TargetPctD, _cfg.PartialPctD, rangeSize, _cfg.TickSize);
        if (rr < _cfg.MinRrD) return;

        _entD = ep; _stopD = sl; _tgtD = tp; _partialD = pp;
        _initStopD = sl; _pnlD = 0; _activeD = true;
        _ctsD = CalcContracts(_cfg.ContractsD, _cfg.HiVolMultD, _cfg.MaxContractsD);
        _stD = isLong ? 2 : -2; _enteredThisBar = true;
        _entryTimeD = utcTime;

        _log.LogInformation("[Setup D TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _ctsD, ep, sl, tp, rr);
        if (IsEntryPriceStale(SetupId.D, ep)) return;
        var sig = new EntrySignal(SetupId.D, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _ctsD, utcTime, _cfg.OrderTypeD);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.D, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.D,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsD}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    // ── C/D/F Priority Helpers ─────────────────────────────────

    /// <summary>
    /// Returns true if any setup has an active trade in the opposite direction.
    /// Prevents opposing entries that would net at the broker and create orphan orders.
    /// </summary>
    private bool HasOpposingPosition(bool isLong)
    {
        // Check all setups for an active trade in the opposite direction
        if (_activeA && (_stA == 2) != isLong) return true;  // A: 2=long, -2=short
        if (_activeB && (_stB == 3) != isLong) return true;  // B: 3=long, -3=short
        if (_activeC && (_stC == 2) != isLong) return true;  // C: 2=long, -2=short
        if (_activeD && (_stD == 2) != isLong) return true;  // D: 2=long, -2=short
        return false;
    }

    /// <summary>
    /// Sanity check: reject entry if the entry price is too far from the last market price.
    /// Prevents phantom trades from stale bar data where the broker rejects/ignores the order
    /// but the engine continues as if it entered.
    /// Tolerance: NearPct × ORB range (matches the setup's arm-zone sensitivity).
    /// </summary>
    private bool IsEntryPriceStale(SetupId setup, decimal entryPrice)
    {
        var lastPx = _prices.GetLastPrice(_cfg.Ticker);
        if (lastPx <= 0) return false; // no price available, allow entry

        decimal nearPct = setup switch
        {
            SetupId.A => _cfg.NearPctA,
            SetupId.B => _cfg.NearPctB,
            _         => Math.Max(_cfg.NearPctA, _cfg.NearPctB) // C/D/F use the larger NearPct
        };
        decimal tolerance = _orb.IsSet && _orb.OrbRange > 0
            ? _orb.OrbRange * nearPct
            : lastPx * 0.005m;  // 0.5% fallback if ORB not formed

        decimal diff = Math.Abs(entryPrice - lastPx);
        if (diff > tolerance)
        {
            _log.LogWarning("[{Setup}] Entry REJECTED — price {Entry:F2} too far from market {Last:F2} (diff={Diff:F2} > tolerance={Tol:F2}, NearPct={NP})",
                setup, entryPrice, lastPx, diff, tolerance, nearPct);
            return true;
        }
        return false;
    }

    private async Task ForceExitA(Bar bar, ExitReason reason = ExitReason.SessionEnd)
    {
        if (!_activeA) return;
        bool isLong = _stA == 2;
        _pnlA += ExitProcessor.ForcedExit(isLong, _entA, bar.Close,
            _ctsA, _partHitA, _cfg.UsePartialA, _cfg.PointValue, _cfg.PartialCtsA);
        await BookExitA(reason, bar.Close, BarExitTime(bar.Time), isLong);
    }

    private async Task ForceExitB(Bar bar, ExitReason reason = ExitReason.SessionEnd)
    {
        if (!_activeB) return;
        bool isLong = _stB == 3;
        _pnlB += ExitProcessor.ForcedExit(isLong, _entB, bar.Close,
            _ctsB, _partHitB, _cfg.UsePartialB, _cfg.PointValue, _cfg.PartialCtsB);
        await BookExitB(reason, bar.Close, BarExitTime(bar.Time), isLong);
    }

    // ── Adverse excursion time check ─────────────────────────────
    // If trade is underwater (price below entry for long, above for short)
    // after MaxAdverseMinutes, force exit at market.
    private bool IsAdverseTimeout(bool active, bool isLong, decimal entry,
        decimal currentPrice, DateTime entryTime, DateTime now, int maxMinutes)
    {
        if (!active || maxMinutes <= 0) return false;
        if ((now - entryTime).TotalMinutes < maxMinutes) return false;
        // Only exit if underwater
        return isLong ? currentPrice < entry : currentPrice > entry;
    }

    /// <summary>
    /// Evaluates arm conditions for Setup A and B without placing entries/exits.
    /// Called during warmup so the engine starts with the correct armed state.
    /// </summary>
    private void EvaluateArmState(Bar bar, TimeOnly localTime)
    {
        // Track cutoff — armed state is cleared past cutoff (per-setup).
        // Guard against overnight bars (>=SessionStartHour) falsely triggering the cutoff.
        var sessStart = new TimeOnly(_cfg.SessionStartHour, 0);
        var cutoffA   = new TimeOnly(_cfg.CutoffHourA, _cfg.CutoffMinuteA);
        if (_cfg.UseTimeFilter && localTime >= cutoffA && localTime < sessStart)
        {
            _pastCutoffA = true;
            if (!_activeA && (_stA == 1 || _stA == -1)) { _stA = 0; _armEntryA = 0; }
        }
        var cutoffB   = new TimeOnly(_cfg.CutoffHourB, _cfg.CutoffMinuteB);
        if (_cfg.UseTimeFilter && localTime >= cutoffB && localTime < sessStart)
        {
            _pastCutoffB = true;
            if (!_activeB && (_stB == 1 || _stB == -1 || _stB == 2 || _stB == -2)) { _stB = 0; _armEntryB = 0; }
        }
        _pastCutoff = _pastCutoffA && _pastCutoffB;

        if (!_orb.IsSet || _orb.OrbRange <= 0) return;

        bool aboveVwapA  = !_cfg.UseVwapA || (_vwap.IsReady && bar.Close > _vwap.Value);
        bool belowVwapA  = !_cfg.UseVwapA || (_vwap.IsReady && bar.Close < _vwap.Value);
        bool orbLongOkA  = !_cfg.UseOrbCloseA || _orb.OrbBullClose;
        bool orbShortOkA = !_cfg.UseOrbCloseA || _orb.OrbBearClose;
        bool aboveVwapB  = !_cfg.UseVwapB || (_vwap.IsReady && bar.Close > _vwap.Value);
        bool belowVwapB  = !_cfg.UseVwapB || (_vwap.IsReady && bar.Close < _vwap.Value);
        bool orbLongOkB  = !_cfg.UseOrbCloseB || _orb.OrbBullClose;
        bool orbShortOkB = !_cfg.UseOrbCloseB || _orb.OrbBearClose;
        decimal orbHigh = _orb.OrbHigh, orbLow = _orb.OrbLow, orbRange = _orb.OrbRange;

        // ── Setup A — arm only (no entry/exit) ───────────────────
        if (_cfg.EnableA && !_activeA && !_pastCutoffA)
        {
            if ((_stA == 1 && bar.Close < orbLow) || (_stA == -1 && bar.Close > orbHigh))
                { _stA = 0; _armEntryA = 0; }
            else
            {
                bool isReady = _tradeCountA < _cfg.MaxTradesA;
                if (isReady && _stA == 0)
                {
                    decimal nearDist = orbRange * _cfg.NearPctA;
                    // Clear directional lock once price leaves the arm zone
                    if (_bullTradedA && bar.High < orbHigh - nearDist) _bullTradedA = false;
                    if (_bearTradedA && bar.Low  > orbLow  + nearDist) _bearTradedA = false;

                    if (bar.High >= orbHigh - nearDist && orbLongOkA && aboveVwapA && !_bullTradedA)
                        { _stA = 1; _armEntryA = bar.Open; }
                    else if (bar.Low <= orbLow + nearDist && orbShortOkA && belowVwapA && !_bearTradedA)
                        { _stA = -1; _armEntryA = bar.Open; }
                }
            }
        }

        // ── Setup B — arm + retest only (no entry/exit) ──────────
        if (_cfg.EnableB && !_activeB && !_pastCutoffB)
        {
            if ((_stB > 0 && _stB < 3 && bar.Close < orbLow) ||
                     (_stB < 0 && _stB > -3 && bar.Close > orbHigh))
                { _stB = 0; _armEntryB = 0; }
            else
            {
                bool isReady = _tradeCountB < _cfg.MaxTradesB;
                if (isReady && _stB == 0)
                {
                    decimal nearDistB = orbRange * _cfg.NearPctB;
                    // Clear directional lock once price leaves the arm zone
                    if (_bullTradedB && bar.Close < orbHigh - nearDistB) _bullTradedB = false;
                    if (_bearTradedB && bar.Close > orbLow  + nearDistB) _bearTradedB = false;

                    if (bar.Close >= orbHigh - nearDistB && orbLongOkB && aboveVwapB && !_bullTradedB)
                        { _stB = 1; _armEntryB = bar.Open; }
                    else if (bar.Close <= orbLow + nearDistB && orbShortOkB && belowVwapB && !_bearTradedB)
                        { _stB = -1; _armEntryB = bar.Open; }
                }
                // Conservative retest zone detection
                if (!_cfg.IsAggressiveB)
                {
                    decimal retestW = orbRange * _cfg.RetestPct;
                    if (_stB == 1  && bar.Low  <= orbHigh + retestW && bar.High >= orbHigh - retestW) _stB = 2;
                    if (_stB == -1 && bar.High >= orbLow  - retestW && bar.Low  <= orbLow  + retestW) _stB = -2;
                    if (_stB == 2  && bar.Close < _orb.OrbMid) _stB = 0;
                    if (_stB == -2 && bar.Close > _orb.OrbMid) _stB = 0;
                }
            }
        }
    }

    private async Task PublishSnapshot(Bar bar)
    {
        _lastSnapshotUtc = DateTime.UtcNow;
        decimal lastPx = _prices.GetLastPrice(_cfg.Ticker);
        if (lastPx == 0) lastPx = bar.Close > 0 ? bar.Close : _lastBarClose;

        ActiveTradeView? viewA = null;
        if (_activeA)
        {
            bool isLong = _stA == 2;
            int rem = _partHitA ? _ctsA - CalcPartialCts(_ctsA, _cfg.PartialCtsA) : _ctsA;
            viewA = new ActiveTradeView
            {
                Setup = SetupId.A, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entA, CurrentStop = _stopA, Target = _tgtA, Partial = _partialA,
                Contracts = _ctsA, RemainingContracts = rem,
                PartialFilled = _partHitA, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entA : _entA - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeA
            };
        }

        ActiveTradeView? viewB = null;
        if (_activeB)
        {
            bool isLong = _stB == 3;
            int rem = _partHitB ? _ctsB - CalcPartialCts(_ctsB, _cfg.PartialCtsB) : _ctsB;
            viewB = new ActiveTradeView
            {
                Setup = SetupId.B, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entB, CurrentStop = _stopB, Target = _tgtB, Partial = _partialB,
                Contracts = _ctsB, RemainingContracts = rem,
                PartialFilled = _partHitB, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entB : _entB - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeB
            };
        }

        ActiveTradeView? viewC = null;
        if (_activeC)
        {
            bool isLong = _stC == 2;
            int rem = _partHitC ? _ctsC - CalcPartialCts(_ctsC, _cfg.PartialCtsC) : _ctsC;
            viewC = new ActiveTradeView
            {
                Setup = SetupId.C, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entC, CurrentStop = _stopC, Target = _tgtC, Partial = _partialC,
                Contracts = _ctsC, RemainingContracts = rem,
                PartialFilled = _partHitC, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entC : _entC - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeC
            };
        }

        ActiveTradeView? viewD = null;
        if (_activeD)
        {
            bool isLong = _stD == 2;
            int rem = _partHitD ? _ctsD - CalcPartialCts(_ctsD, _cfg.PartialCtsD) : _ctsD;
            viewD = new ActiveTradeView
            {
                Setup = SetupId.D, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entD, CurrentStop = _stopD, Target = _tgtD, Partial = _partialD,
                Contracts = _ctsD, RemainingContracts = rem,
                PartialFilled = _partHitD, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entD : _entD - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeD
            };
        }

        await _sink.OnSnapshotAsync(new EngineSnapshot
        {
            Time   = bar.Time, Ticker = _cfg.Ticker, IsLive = true,
            LastPrice  = lastPx,
            LastUpdate = DateTime.UtcNow,
            SetupA = viewA, SetupB = viewB,
            SetupC = viewC, SetupD = viewD,

            TodayPnl    = _todayPnl,
            TodayTrades = _todayWins + _todayLosses,
            TodayWins   = _todayWins,
            TodayLosses = _todayLosses,
            TodayMaxDD  = _todayMaxDD,

            TradeCountA = _tradeCountA, MaxTradesA = _cfg.MaxTradesA,
            TradeCountB = _tradeCountB, MaxTradesB = _cfg.MaxTradesB,
            TradeCountC = _tradeCountC, MaxTradesC = _cfg.MaxTradesC,
            TradeCountD = _tradeCountD, MaxTradesD = _cfg.MaxTradesD,

            Expectancy  = CalcExpectancy(_todayWins, _todayLosses, _todayWinPnl, _todayLossPnl),
            ExpectancyA = CalcExpectancy(_todayWinsA, _todayLossesA, _todayWinPnlA, _todayLossPnlA),
            ExpectancyB = CalcExpectancy(_todayWinsB, _todayLossesB, _todayWinPnlB, _todayLossPnlB),

            DailyLossLimit = _cfg.MaxDailyLoss,
            DailyLossUsed  = Math.Abs(Math.Min(0, _todayPnl)),
            TradingHalted  = _ddBreached,

            Vwap = _vwap.Value,
            Atr  = _atr.Value,

            OrbHigh      = _orb.OrbHigh,
            OrbLow       = _orb.OrbLow,
            OrbMid       = _orb.OrbMid,
            OrbRange     = _orb.OrbRange,
            OrbBullClose = _orb.OrbBullClose,
            OrbBearClose = _orb.OrbBearClose,
            OrbAtrRatio  = _orbAtrRatio,

            OrbFormed    = _orb.IsSet,
            PastCutoff   = _pastCutoff,
            PastCutoffA  = _pastCutoffA,
            PastCutoffB  = _pastCutoffB,
            PastCutoffC  = _pastCutoffC,
            PastCutoffD  = _pastCutoffD,
            SessionEnded = _rthEnded || IsWeekendClosed(),
            ActiveSessionId = _activeSessionId,
            OrbWindowStart  = _cfg.OrbStart.ToString("HH:mm"),
            OrbWindowEnd    = _cfg.OrbEnd.ToString("HH:mm"),

            SetupAEnabled = _cfg.EnableA,
            SetupBEnabled = _cfg.EnableB,
            SetupCEnabled = _cfg.EnableC,
            SetupDEnabled = _cfg.EnableD,
            SetupAState = _stA,
            SetupBState = _stB,
            SetupCState = _stC,
            SetupDState = _stD,

            StickyTgtA = _stickyTgtA,
            StickyStpA = _stickyStpA,
            StickyTgtB = _stickyTgtB,
            StickyStpB = _stickyStpB,
            StickyTgtC = _stickyTgtC,
            StickyStpC = _stickyStpC,
            StickyTgtD = _stickyTgtD,
            StickyStpD = _stickyStpD,

            // FalseBreakout module context
            FBOrbBreakoutActive      = _falseBreakout.OrbTracker.BreakoutActive,
            FBSessionBreakoutActive  = _falseBreakout.SessionRangeTracker.BreakoutActive,
            FBOrbBarsInBreakout      = _falseBreakout.OrbTracker.BarsInBreakout,
            FBSessionBarsInBreakout  = _falseBreakout.SessionRangeTracker.BarsInBreakout,
            FBOrbPenetrationDepth    = _falseBreakout.OrbTracker.PenetrationDepth,
            FBSessionPenetrationDepth = _falseBreakout.SessionRangeTracker.PenetrationDepth,
            FBOrbActivated           = _falseBreakout.OrbTracker.IsActivated,
            FBSessionActivated       = _falseBreakout.SessionRangeTracker.IsActivated,
            IsCompoundFakeout        = _falseBreakout.IsCompoundFakeout,

            // Per-setup daily stats
            TodayWinsA    = _todayWinsA,
            TodayLossesA  = _todayLossesA,
            TodayWinPnlA  = _todayWinPnlA,
            TodayLossPnlA = _todayLossPnlA,
            TodayWinsB    = _todayWinsB,
            TodayLossesB  = _todayLossesB,
            TodayWinPnlB  = _todayWinPnlB,
            TodayLossPnlB = _todayLossPnlB,
            TodayWinsC    = _todayWinsC,
            TodayLossesC  = _todayLossesC,
            TodayWinPnlC  = _todayWinPnlC,
            TodayLossPnlC = _todayLossPnlC,
            TodayWinsD    = _todayWinsD,
            TodayLossesD  = _todayLossesD,
            TodayWinPnlD  = _todayWinPnlD,
            TodayLossPnlD = _todayLossPnlD,

            // Module outputs
            CurrentSession  = _sessionEngine.CurrentSession.ToString(),
            SessionHigh     = _sessionEngine.SessionHigh,
            SessionLow      = _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : 0,
            PrevDayHigh     = _sessionEngine.PDH,
            PrevDayLow      = _sessionEngine.PDL,
            AsiaCompressed  = _sessionEngine.AsiaCompressed,
            LastSweep       = _sweepDetector.LastSweep != null
                ? $"{_sweepDetector.LastSweep.Type} {_sweepDetector.LastSweep.Direction}" : "",
            VwapUpper1      = _vwapModel.Upper1,
            VwapUpper2      = _vwapModel.Upper2,
            VwapLower1      = _vwapModel.Lower1,
            VwapLower2      = _vwapModel.Lower2,
            VwapState       = (int)_vwapModel.State,
            OpeningDriveBull = _openingDrive.OpeningDriveBull,
            OpeningDriveBear = _openingDrive.OpeningDriveBear,
            TrendScoreBull  = _trendDay.BullScore,
            TrendScoreBear  = _trendDay.BearScore,

            // Signal Strength sub-scores (0–5 each)
            DriveScore      = ComputeDriveScore(),
            SweepScore      = ComputeSweepScore(),
            VwapDevScore    = ComputeVwapDevScore(),
            SignalStrength  = ComputeSignalStrength(),

            RecentAlerts = GetRecentAlerts(20)
        });
    }

    // ── Signal Strength helpers (each returns 0–5) ────────────────

    /// <summary>Opening Drive score: 0 if no drive; scaled by DriveRangePctATR relative to threshold.</summary>
    private decimal ComputeDriveScore()
    {
        if (!_openingDrive.OpeningDriveBull && !_openingDrive.OpeningDriveBear)
            return 0m;
        var threshold = _cfg.DriveRangeAtrMult;
        if (threshold <= 0) return 0m;
        // At threshold = 3, at 2× threshold = 5
        var raw = _openingDrive.DriveRangePctATR / threshold * 3m;
        return Math.Clamp(Math.Round(raw, 1), 0m, 5m);
    }

    /// <summary>Sweep score: 0 if no sweeps; 2.5 per active sweep, capped at 5.</summary>
    private decimal ComputeSweepScore()
    {
        var count = _sweepDetector.ActiveSweeps.Count;
        if (count == 0) return 0m;
        return Math.Min(5m, count * 2.5m);
    }

    /// <summary>VWAP deviation score: |VwapState| mapped to 0–5 (0→0, ±1→2.5, ±2→5).</summary>
    private decimal ComputeVwapDevScore()
    {
        return Math.Abs((int)_vwapModel.State) * 2.5m;
    }

    /// <summary>Composite signal strength: average of the three sub-scores.</summary>
    private decimal ComputeSignalStrength()
    {
        var d = ComputeDriveScore();
        var s = ComputeSweepScore();
        var v = ComputeVwapDevScore();
        return Math.Round((d + s + v) / 3m, 1);
    }

    private void ResetSetupA()
    {
        _stA = 0; _entA = 0; _stopA = 0; _tgtA = 0; _partialA = 0;
        _activeA = false; _tradeCountA = 0; _partHitA = false;
        _pnlA = 0; _armEntryA = 0; _entryTimeA = DateTime.MinValue;
        _stickyTgtA = false; _stickyStpA = false; _exitBarIdxA = -1;
        _bullTradedA = false; _bearTradedA = false; _ctsA = 0;
    }

    private void ResetSetupB()
    {
        _stB = 0; _entB = 0; _stopB = 0; _tgtB = 0; _partialB = 0;
        _activeB = false; _tradeCountB = 0; _partHitB = false;
        _pnlB = 0; _armEntryB = 0; _entryTimeB = DateTime.MinValue;
        _stickyTgtB = false; _stickyStpB = false; _exitBarIdxB = -1;
        _bullTradedB = false; _bearTradedB = false; _ctsB = 0;
    }

    private void ResetSetupC()
    {
        _stC = 0; _entC = 0; _stopC = 0; _tgtC = 0; _partialC = 0;
        _activeC = false; _tradeCountC = 0; _partHitC = false;
        _pnlC = 0; _armEntryC = 0; _entryTimeC = DateTime.MinValue;
        _stickyTgtC = false; _stickyStpC = false; _exitBarIdxC = -1;
        _bullTradedC = false; _bearTradedC = false; _ctsC = 0;
        _pastCutoffC = false;
    }

    private void ResetSetupD()
    {
        _stD = 0; _entD = 0; _stopD = 0; _tgtD = 0; _partialD = 0;
        _activeD = false; _tradeCountD = 0; _partHitD = false;
        _pnlD = 0; _armEntryD = 0; _entryTimeD = DateTime.MinValue;
        _stickyTgtD = false; _stickyStpD = false; _exitBarIdxD = -1;
        _bullTradedD = false; _bearTradedD = false; _ctsD = 0;
        _pastCutoffD = false;
    }

    /// <summary>
    /// Compute effective contracts for a trade, applying high-vol multiplier when ORB ≥ 1×ATR.
    /// Matches Pine: contracts = min(isHighVol ? round(base × mult) : base, cap)
    /// </summary>
    private int CalcContracts(int baseCts, decimal hiVolMult, int maxCts)
    {
        bool isHighVol = _orbAtrRatio >= 1.0m;
        int cts = isHighVol ? (int)Math.Round(baseCts * hiVolMult) : baseCts;
        return Math.Min(cts, maxCts);
    }

    /// <summary>
    /// Compute partial exit contracts. If fixedCts > 0, use that (capped at totalCts - 1).
    /// Otherwise fall back to Math.Floor(totalCts * 0.5).
    /// </summary>
    private static int CalcPartialCts(int totalCts, int fixedCts)
    {
        if (fixedCts > 0)
            return Math.Min(fixedCts, totalCts - 1);
        return (int)Math.Floor(totalCts * 0.5);
    }

    /// <summary>
    /// Returns true when the futures market is closed for the weekend:
    /// Friday after RthEnd (16:00 ET) through Sunday before SessionStartHour (18:00 ET).
    /// </summary>
    private bool IsWeekendClosed()
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);
        var day = now.DayOfWeek;
        var time = TimeOnly.FromDateTime(now);
        // Friday after RTH end
        if (day == DayOfWeek.Friday && time >= _cfg.RthEnd) return true;
        // All day Saturday
        if (day == DayOfWeek.Saturday) return true;
        // Sunday before session start (18:00)
        if (day == DayOfWeek.Sunday && time < new TimeOnly(_cfg.SessionStartHour, 0)) return true;
        return false;
    }

    private static decimal CalcExpectancy(int wins, int losses, decimal winPnl, decimal lossPnl)
    {
        int total = wins + losses;
        if (total == 0) return 0;
        decimal winRate  = (decimal)wins   / total;
        decimal lossRate = (decimal)losses / total;
        decimal avgWin   = wins   > 0 ? winPnl  / wins   : 0;
        decimal avgLoss  = losses > 0 ? lossPnl / losses : 0;
        return winRate * avgWin - lossRate * avgLoss;  // avgLoss is negative → subtracting it adds the loss contribution
    }

    private void AddAlert(string type, SetupId setup, string msg, string color)
    {
        _alertRing[_alertHead] = new AlertEvent
        {
            Time = DateTime.UtcNow, Type = type, Setup = setup, Message = msg, Color = color
        };
        _alertHead = (_alertHead + 1) % AlertCapacity;
        if (_alertCount < AlertCapacity) _alertCount++;
    }

    /// <summary>Returns the last <paramref name="n"/> alerts in chronological order (O(n)).</summary>
    private List<AlertEvent> GetRecentAlerts(int n)
    {
        var take = Math.Min(n, _alertCount);
        var list = new List<AlertEvent>(take);
        // Oldest of the last 'take' items is at (head - take) mod capacity
        int start = (_alertHead - take + AlertCapacity) % AlertCapacity;
        for (int i = 0; i < take; i++)
            list.Add(_alertRing[(start + i) % AlertCapacity]);
        return list;
    }

    private static TimeZoneInfo GetTz(string tz)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch
        {
            var win = tz switch
            {
                "America/New_York"    => "Eastern Standard Time",
                "America/Chicago"     => "Central Standard Time",
                "America/Los_Angeles" => "Pacific Standard Time",
                "Europe/London"       => "GMT Standard Time",
                _                     => tz
            };
            return TimeZoneInfo.FindSystemTimeZoneById(win);
        }
    }
}
