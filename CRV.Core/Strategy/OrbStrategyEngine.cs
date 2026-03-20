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

    // ── Extracted strategies ─────────────────────────────────
    private PullbackStrategy   _setupA;
    private RetestStrategy     _setupB;
    private OrbFakeoutStrategy _setupC;
    // Setup A is fully handled by _setupA (PullbackStrategy instance)
    // Setup B is fully handled by _setupB (RetestStrategy instance)
    // Setup C is fully handled by _setupC (OrbFakeoutStrategy instance)

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

    // ── Setup A is handled by _setupA (PullbackStrategy) ──────
    // ── Setup B is handled by _setupB (RetestStrategy) ──────
    // ── Setup C is handled by _setupC (OrbFakeoutStrategy) ──────

    // ── Manual force-exit flags C/D ──────────────────────────────
    private volatile bool _forceExitC = false;
    private volatile bool _forceExitD = false;

    // ── Setup C cutoff (engine-managed, strategy doesn't own cutoff) ──
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
        _setupA          = new PullbackStrategy(BuildSetupConfigA(cfg));
        _setupB          = new RetestStrategy(BuildSetupConfigB(cfg));
        _setupC          = new OrbFakeoutStrategy(BuildSetupConfigC(cfg));
    }

    /// <summary>Force-exit Setup A immediately at the current market price.</summary>
    public async Task RequestForceExitA()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0) { _forceExitA = true; return; }  // fallback: next bar
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        if (_setupA.IsActive)
        {
            var pre = _setupA.GetActiveTrade(px);
            _setupA.ForceExit(px, DateTime.UtcNow);
            await DispatchSignals(_setupA, DateTime.UtcNow, pre);
        }
        AddAlert("EXIT", SetupId.A, $"Force exit @ {px:F2}", "orange");
        await PublishSnapshot(bar);
    }

    /// <summary>Force-exit Setup B immediately at the current market price.</summary>
    public async Task RequestForceExitB()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0) { _forceExitB = true; return; }
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        if (_setupB.IsActive)
        {
            var preB = _setupB.GetActiveTrade(px);
            _setupB.ForceExit(px, DateTime.UtcNow);
            await DispatchSignals(_setupB, DateTime.UtcNow, preB);
        }
        AddAlert("EXIT", SetupId.B, $"Force exit @ {px:F2}", "orange");
        await PublishSnapshot(bar);
    }

    /// <summary>Force-exit Setup C immediately at the current market price.</summary>
    public async Task RequestForceExitC()
    {
        var px = _prices.GetLastPrice(_cfg.Ticker);
        if (px <= 0) { _forceExitC = true; return; }
        var bar = new Bar(DateTime.UtcNow, px, px, px, px, 0, IsConfirmed: true);
        if (_setupC.IsActive)
        {
            var pre = _setupC.GetActiveTrade(px);
            _setupC.ForceExit(px, DateTime.UtcNow);
            await DispatchSignals(_setupC, DateTime.UtcNow, pre);
        }
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
        _setupA.Reconfigure(BuildSetupConfigA(cfg));
        _setupB.Reconfigure(BuildSetupConfigB(cfg));
        _setupC.Reconfigure(BuildSetupConfigC(cfg));

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
        _pastCutoffC = false;
        _todayWinsC = 0; _todayLossesC = 0; _todayWinPnlC = 0; _todayLossPnlC = 0;
        _pastCutoffD = false; _tradeCountD = 0;
        _todayWinsD = 0; _todayLossesD = 0; _todayWinPnlD = 0; _todayLossPnlD = 0;
        _lastTickTime = DateTime.MinValue;

        // Reset per-setup trade state
        _setupA.Reset();
        _setupB.Reset();
        _setupC.Reset();

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
    /// <param name="utcTime">Optional simulated time (for backtest). Falls back to DateTime.UtcNow for live.</param>
    public async Task ForceExitAllAsync(DateTime? utcTime = null)
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
        var ts = utcTime ?? DateTime.UtcNow;
        var bar = new Bar(ts, px, px, px, px, 0, IsConfirmed: true);
        if (_setupA.IsActive)
        {
            var pre = _setupA.GetActiveTrade(px);
            _setupA.ForceExit(px, ts);
            await DispatchSignals(_setupA, ts, pre);
        }
        if (_setupB.IsActive)
        {
            var preB = _setupB.GetActiveTrade(px);
            _setupB.ForceExit(px, ts);
            await DispatchSignals(_setupB, ts, preB);
        }
        if (_setupC.IsActive)
        {
            var preC = _setupC.GetActiveTrade(px);
            _setupC.ForceExit(px, ts);
            await DispatchSignals(_setupC, ts, preC);
        }
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
        _setupC.Reset(); ResetSetupD();
        _pastCutoffC = false;
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

    // ── Per-setup effective instrument helpers ──────────────────
    private decimal PointValueFor(SetupId setup) => setup switch
    {
        SetupId.A => _cfg.EffectivePointValueA,
        SetupId.B => _cfg.EffectivePointValueB,
        SetupId.C => _cfg.EffectivePointValueC,
        SetupId.D => _cfg.EffectivePointValueD,
        _         => _cfg.PointValue
    };
    private decimal TickSizeFor(SetupId setup) => setup switch
    {
        SetupId.A => _cfg.EffectiveTickSizeA,
        SetupId.B => _cfg.EffectiveTickSizeB,
        SetupId.C => _cfg.EffectiveTickSizeC,
        SetupId.D => _cfg.EffectiveTickSizeD,
        _         => _cfg.TickSize
    };
    private string TickerFor(SetupId setup) => setup switch
    {
        SetupId.A => _cfg.EffectiveTickerA,
        SetupId.B => _cfg.EffectiveTickerB,
        SetupId.C => _cfg.EffectiveTickerC,
        SetupId.D => _cfg.EffectiveTickerD,
        _         => _cfg.Ticker
    };

    // ── Strategy config builders ────────────────────────────────
    private StrategySetupConfig BuildSetupConfigA(StrategyConfig cfg) => new()
    {
        Name = "A", SetupId = SetupId.A, StrategyType = StrategyType.Pullback,
        Enabled = cfg.EnableA,
        Ticker = cfg.EffectiveTickerA, PointValue = cfg.EffectivePointValueA,
        TickSize = cfg.EffectiveTickSizeA,
        Contracts = cfg.ContractsA, HiVolMult = cfg.HiVolMultA, MaxContracts = cfg.MaxContractsA,
        StopPct = cfg.StopPctA, TargetPct = cfg.TargetPctA, PartialPct = cfg.PartialPctA,
        NearPct = cfg.NearPctA, MinRr = cfg.MinRrA, Mode = cfg.ModeA,
        PullbackPct = cfg.PullbackPct, EntryTickOffset = cfg.EntryTickOffsetA,
        OrderType = cfg.OrderTypeA,
        UseVwap = cfg.UseVwapA, UseOrbClose = cfg.UseOrbCloseA,
        CutoffHour = cfg.CutoffHourA, CutoffMinute = cfg.CutoffMinuteA,
        CloseAtRthClose = cfg.CloseAtRthCloseA, MaxTrades = cfg.MaxTradesA,
        MaxAdverseMinutes = cfg.MaxAdverseMinutesA,
        UsePartial = cfg.UsePartialA, UseBe = cfg.UseBeA,
        PartialCts = cfg.PartialCtsA, AllowRearmAfterBe = cfg.AllowRearmAfterBeA,
    };

    private StrategySetupConfig BuildSetupConfigB(StrategyConfig cfg) => new()
    {
        Name = "B", SetupId = SetupId.B, StrategyType = StrategyType.Retest,
        Enabled = cfg.EnableB,
        Ticker = cfg.EffectiveTickerB, PointValue = cfg.EffectivePointValueB,
        TickSize = cfg.EffectiveTickSizeB,
        Contracts = cfg.ContractsB, HiVolMult = cfg.HiVolMultB, MaxContracts = cfg.MaxContractsB,
        StopPct = cfg.StopPctB, TargetPct = cfg.TargetPctB, PartialPct = cfg.PartialPctB,
        NearPct = cfg.NearPctB, MinRr = cfg.MinRrB, Mode = cfg.ModeB,
        RetestPct = cfg.RetestPct, EntryTickOffset = cfg.EntryTickOffsetB,
        OrderType = cfg.OrderTypeB,
        UseVwap = cfg.UseVwapB, UseOrbClose = cfg.UseOrbCloseB,
        CutoffHour = cfg.CutoffHourB, CutoffMinute = cfg.CutoffMinuteB,
        CloseAtRthClose = cfg.CloseAtRthCloseB, MaxTrades = cfg.MaxTradesB,
        MaxAdverseMinutes = cfg.MaxAdverseMinutesB,
        UsePartial = cfg.UsePartialB, UseBe = cfg.UseBeB,
        PartialCts = cfg.PartialCtsB, AllowRearmAfterBe = cfg.AllowRearmAfterBeB,
    };

    private StrategySetupConfig BuildSetupConfigC(StrategyConfig cfg) => new()
    {
        Name = "C", SetupId = SetupId.C, StrategyType = StrategyType.OrbFakeout,
        Enabled = cfg.EnableC,
        Ticker = cfg.EffectiveTickerC, PointValue = cfg.EffectivePointValueC,
        TickSize = cfg.EffectiveTickSizeC,
        Contracts = cfg.ContractsC, HiVolMult = cfg.HiVolMultC, MaxContracts = cfg.MaxContractsC,
        StopPct = cfg.StopPctC, TargetPct = cfg.TargetPctC, PartialPct = cfg.PartialPctC,
        NearPct = cfg.NearPctC, MinRr = cfg.MinRrC, Mode = "Conservative",
        EntryTickOffset = cfg.EntryTickOffsetC,
        OrderType = cfg.OrderTypeC,
        UseVwap = false, UseOrbClose = false,
        CutoffHour = cfg.CutoffHourC, CutoffMinute = cfg.CutoffMinuteC,
        CloseAtRthClose = cfg.CloseAtRthCloseC, MaxTrades = cfg.MaxTradesC,
        MaxAdverseMinutes = cfg.MaxAdverseMinutesC,
        UsePartial = cfg.UsePartialC, UseBe = cfg.UseBeC,
        PartialCts = cfg.PartialCtsC, AllowRearmAfterBe = cfg.AllowRearmAfterBeC,
    };

    // ── State snapshot builders ─────────────────────────────────
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
        SessionHigh:          _sessionEngine.SessionHigh,
        SessionLow:           _sessionEngine.SessionLow,
        AsiaHigh:             _sessionEngine.AsiaHigh,
        AsiaLow:              _sessionEngine.AsiaLow,
        AsiaCompressed:       _sessionEngine.AsiaCompressed,
        LondonHigh:           _sessionEngine.LondonHigh,
        LondonLow:            _sessionEngine.LondonLow,
        PDH:                  _sessionEngine.PDH,
        PDL:                  _sessionEngine.PDL,
        PWH:                  _sessionEngine.PWH,
        PWL:                  _sessionEngine.PWL,
        CurrentSession:       _sessionEngine.CurrentSession,
        LondonSweptAsiaHigh:  _sessionEngine.LondonSweptAsiaHigh,
        LondonSweptAsiaLow:   _sessionEngine.LondonSweptAsiaLow,
        NYBullExpansion:      _sessionEngine.NYBullExpansion,
        NYBearExpansion:      _sessionEngine.NYBearExpansion,
        ActiveSweeps:         _sweepDetector.ActiveSweeps,
        VwapState:            (int)_vwapModel.State,
        BullVwapReclaim:      _vwapModel.BullVWAPReclaim,
        BearVwapReject:       _vwapModel.BearVWAPReject,
        IsBullDrive:          _openingDrive.OpeningDriveBull,
        IsBearDrive:          _openingDrive.OpeningDriveBear,
        TrendDayBullScore:    _trendDay.BullScore,
        TrendDayBearScore:    _trendDay.BearScore,
        TrendDayBull:         _trendDay.TrendDayBull,
        TrendDayBear:         _trendDay.TrendDayBear,
        OrbFakeoutBull:       _falseBreakout.OrbTracker.IsActivated &&
                              _falseBreakout.OrbTracker.BreakoutDirection == Direction.Long,
        OrbFakeoutBear:       _falseBreakout.OrbTracker.IsActivated &&
                              _falseBreakout.OrbTracker.BreakoutDirection == Direction.Short,
        FakeoutPenetration:   _falseBreakout.OrbTracker.PenetrationDepth);

    /// <summary>
    /// Run the extracted strategy's OnBar for arm-state evaluation only.
    /// If the strategy transitions all the way to active (arm + entry on same bar),
    /// revert the entry so only the armed state survives.
    /// Called during warmup cooldown and ORB-just-formed bars.
    /// </summary>
    private void StrategyArmOnly(Bar bar)
    {
        var orbState = BuildOrbState();
        var indState = BuildIndicatorState();
        var modState = BuildModuleState();

        _setupA.OnBar(bar, orbState, indState, modState);
        if (_setupA.PendingEntry != null)
            _setupA.RevertEntry();      // undo entry, keep armed state
        else
            _setupA.ClearPendingSignals();

        _setupB.OnBar(bar, orbState, indState, modState);
        if (_setupB.PendingEntry != null)
            _setupB.RevertEntry();      // undo entry, keep armed state
        else
            _setupB.ClearPendingSignals();

        bool orbActivatedW = _falseBreakout.OrbTracker.IsActivated;
        bool wasArmedCW = _setupC.IsArmed || _setupC.IsActive;
        _setupC.OnBar(bar, orbState, indState, modState);
        if (orbActivatedW && !wasArmedCW && (_setupC.IsArmed || _setupC.IsActive))
            _falseBreakout.OrbTracker.ClearActivation();
        if (_setupC.PendingEntry != null)
            _setupC.RevertEntry();      // undo entry, keep armed state
        else
            _setupC.ClearPendingSignals();
    }

    /// <summary>
    /// Consume pending signals from an extracted strategy and route them to the
    /// executor + event sink — replicating the behaviour of the inline code paths.
    /// </summary>
    /// <param name="strategy">The strategy instance whose signals to dispatch.</param>
    /// <param name="time">Bar/tick time for signal timestamps.</param>
    /// <param name="preTrade">Snapshot of the active trade captured BEFORE OnBar/OnTick
    /// (needed to build TradeRecord after exit, since the strategy resets its state).</param>
    private async Task DispatchSignals(ISetupStrategy strategy, DateTime time,
        ActiveTradeView? preTrade)
    {
        var setup = strategy.SetupId;

        // ── Partial ──────────────────────────────────────────────
        if (strategy.PendingPartial is { } psig)
        {
            await _executor.OnPartialSignalAsync(psig);
            await _sink.OnPartialAsync(psig);
            AddAlert("PARTIAL", setup, $"Partial @ {psig.PartialPrice:F2}", "yellow");
        }

        // ── Break-even ──────────────────────────────────────────
        if (strategy.PendingBE is { } besig)
        {
            await _executor.OnBESignalAsync(besig);
            await _sink.OnBEMoveAsync(besig);
            AddAlert("MOVE_BE", setup, $"Stop → BE {besig.Entry:F2}", "yellow");
        }
        // Partial without BE: adjust broker qty
        else if (strategy.PendingPartial is { } psig2 && strategy.PendingBE is null)
        {
            var view = strategy.GetActiveTrade(_prices.GetLastPrice(_cfg.Ticker));
            if (view != null)
                await _executor.OnLevelsAdjustedAsync(setup, view.CurrentStop, view.Target,
                    view.RemainingContracts);
        }

        // ── Entry ───────────────────────────────────────────────
        if (strategy.PendingEntry is { } esig)
        {
            _enteredThisBar = true;
            if (!IsEntryPriceStale(setup, esig.Entry))
            {
                var fill = await _executor.OnEntrySignalAsync(esig);
                if (fill.HasValue && fill.Value != esig.Entry) strategy.ApplyFill(fill.Value);
                await _sink.OnEntryAsync(esig);
                bool isLong = esig.Direction == Direction.Long;
                AddAlert("ENTRY", setup,
                    $"{(isLong ? "LONG" : "SHORT")} {esig.Contracts}ct @ {esig.Entry:F2} | Stop {esig.Stop:F2} | Tgt {esig.Target:F2}",
                    isLong ? "green" : "red");
            }
        }

        // ── Exit ────────────────────────────────────────────────
        // If entry + exit happen on the same bar, preTrade is null because no trade
        // existed before OnBar. Synthesize preTrade from the entry signal.
        if (preTrade == null && strategy.PendingEntry is { } entSig && strategy.PendingExit != null)
        {
            preTrade = new ActiveTradeView
            {
                Setup = setup,
                Direction = entSig.Direction,
                Entry = entSig.Entry,
                CurrentStop = entSig.Stop,
                Target = entSig.Target,
                Partial = entSig.Partial,
                Contracts = entSig.Contracts,
                RemainingContracts = entSig.Contracts,
                PartialFilled = false,
                LastPrice = entSig.Entry,
                UnrealizedPnl = 0,
                EnteredAt = entSig.Time,
            };
        }
        if (strategy.PendingExit is { } xsig && preTrade != null)
        {
            bool isLong    = preTrade.Direction == Direction.Long;
            decimal grossPnl = preTrade.UnrealizedPnl; // PnL at exit (close enough for snap)
            // Recompute gross PnL precisely from exit price
            int cts = preTrade.Contracts;
            int remCts = xsig.Contracts;
            decimal initStop = preTrade.CurrentStop; // best available — pre-trade stop may have been moved to BE
            // Use the entry from preTrade to compute gross PnL for remaining contracts
            decimal pnl = (isLong ? xsig.ExitPrice - preTrade.Entry : preTrade.Entry - xsig.ExitPrice)
                          * PointValueFor(setup) * remCts;
            // If partial already hit, add partial PnL component
            if (preTrade.PartialFilled)
            {
                int partCts = cts - remCts;
                decimal partPnl = (isLong ? preTrade.Partial - preTrade.Entry : preTrade.Entry - preTrade.Partial)
                                  * PointValueFor(setup) * partCts;
                pnl += partPnl;
            }
            decimal comm  = cts * 2 * _cfg.CommissionPerSide;
            decimal net   = pnl - comm;
            // InitialStop: for accurate R-multiple we need the original stop, not the
            // potentially BE-moved stop. We'll approximate with the pre-trade stop.
            decimal risk  = Math.Abs(preTrade.Entry - preTrade.CurrentStop) * PointValueFor(setup) * cts;
            decimal rMult = risk > 0 ? pnl / risk : 0;

            var trade = new TradeRecord
            {
                Setup          = setup,
                Direction      = preTrade.Direction,
                Ticker         = _cfg.Ticker,
                Contracts      = cts,
                Entry          = preTrade.Entry,
                InitialStop    = preTrade.CurrentStop,
                Target         = preTrade.Target,
                Partial        = preTrade.Partial,
                Exit           = xsig.ExitPrice,
                ExitReason     = xsig.Reason,
                PartialFilled  = preTrade.PartialFilled || strategy.PendingPartial != null,
                PartialPrice   = preTrade.Partial,
                GrossPnl       = pnl,
                Commission     = comm,
                NetPnl         = net,
                RMultiple      = rMult,
                EnteredAt      = preTrade.EnteredAt,
                ExitedAt       = xsig.Time,
                SessionId      = string.IsNullOrEmpty(_activeSessionId) ? "NY" : _activeSessionId
            };

            _todayPnl   += net;
            _todayPeak   = Math.Max(_todayPeak, _todayPnl);
            _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
            CheckDailyLossLimit();
            if (net > 0) { _todayWins++;   _todayWinPnl  += net; }
            else         { _todayLosses++; _todayLossPnl += net; }
            // Per-setup stats
            switch (setup)
            {
                case SetupId.A:
                    if (net > 0) { _todayWinsA++;   _todayWinPnlA  += net; }
                    else         { _todayLossesA++; _todayLossPnlA += net; }
                    break;
                case SetupId.B:
                    if (net > 0) { _todayWinsB++;   _todayWinPnlB  += net; }
                    else         { _todayLossesB++; _todayLossPnlB += net; }
                    break;
                case SetupId.C:
                    if (net > 0) { _todayWinsC++;   _todayWinPnlC  += net; }
                    else         { _todayLossesC++; _todayLossPnlC += net; }
                    break;
            }

            _log.LogInformation("[Setup {S}] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}",
                setup, xsig.Reason, xsig.ExitPrice, net, rMult);

            await _executor.OnExitSignalAsync(xsig);
            await _sink.OnExitAsync(xsig, trade);
            AddAlert("EXIT", setup,
                $"{xsig.Reason} @ {xsig.ExitPrice:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
                xsig.Reason == ExitReason.Target ? "green" : "red");

            // Adverse-time exit: block re-arming for the rest of this session
            if (xsig.Reason == ExitReason.AdverseTime)
            {
                switch (setup)
                {
                    case SetupId.A: _pastCutoffA = true; break;
                    case SetupId.B: _pastCutoffB = true; break;
                    case SetupId.C: _pastCutoffC = true; break;
                }
            }
        }

        strategy.ClearPendingSignals();
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
            // SetupId.A: handled by _setupA.ApplyFill() in DispatchSignals
            // SetupId.B: handled by _setupB.ApplyFill() in DispatchSignals
            // SetupId.C: handled by _setupC.ApplyFill() in DispatchSignals
            case SetupId.D:
                delta = fillPrice - _entD;
                _log.LogInformation("[Setup D] Fill adjustment: theoretical={T:F2} actual={A:F2} delta={D:F2}",
                    _entD, fillPrice, delta);
                _entD      = fillPrice;
                _stopD     = LevelCalculator.RoundToTick(_stopD + delta, TickSizeFor(SetupId.D));
                _tgtD      = LevelCalculator.RoundToTick(_tgtD + delta, TickSizeFor(SetupId.D));
                _partialD  = LevelCalculator.RoundToTick(_partialD + delta, TickSizeFor(SetupId.D));
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
                if (_setupA.IsActive)
                {
                    var pre = _setupA.GetActiveTrade(price);
                    _setupA.ForceExit(price, utcTime);
                    await DispatchSignals(_setupA, utcTime, pre);
                }
                if (_setupB.IsActive)
                {
                    var preB = _setupB.GetActiveTrade(price);
                    _setupB.ForceExit(price, utcTime);
                    await DispatchSignals(_setupB, utcTime, preB);
                }
                if (_setupC.IsActive)
                {
                    var preC = _setupC.GetActiveTrade(price);
                    _setupC.ForceExit(price, utcTime);
                    await DispatchSignals(_setupC, utcTime, preC);
                }
                if (_activeD) await ForceExitD(bar);
                return;
            }
        }

        // ── Adverse excursion time check (tick mode) ───────────────
        var tickBar = new Bar(utcTime, price, price, price, price, 0);
        {
            var atv = _setupA.GetActiveTrade(price);
            if (atv != null && _cfg.MaxAdverseMinutesA > 0
                && (utcTime - atv.EnteredAt).TotalMinutes >= _cfg.MaxAdverseMinutesA
                && (atv.Direction == Direction.Long ? price < atv.Entry : price > atv.Entry))
            {
                _log.LogInformation("[A] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesA);
                AddAlert("ADVERSE", SetupId.A, $"Underwater >{_cfg.MaxAdverseMinutesA}min — exit", "orange");
                var pre = _setupA.GetActiveTrade(price);
                _setupA.ForceExit(price, utcTime, ExitReason.AdverseTime);
                await DispatchSignals(_setupA, utcTime, pre);
            }
        }
        {
            var atvB = _setupB.GetActiveTrade(price);
            if (atvB != null && _cfg.MaxAdverseMinutesB > 0
                && (utcTime - atvB.EnteredAt).TotalMinutes >= _cfg.MaxAdverseMinutesB
                && (atvB.Direction == Direction.Long ? price < atvB.Entry : price > atvB.Entry))
            {
                _log.LogInformation("[B] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesB);
                AddAlert("ADVERSE", SetupId.B, $"Underwater >{_cfg.MaxAdverseMinutesB}min — exit", "orange");
                var preB = _setupB.GetActiveTrade(price);
                _setupB.ForceExit(price, utcTime, ExitReason.AdverseTime);
                await DispatchSignals(_setupB, utcTime, preB);
            }
        }
        {
            var atvC = _setupC.GetActiveTrade(price);
            if (atvC != null && _cfg.MaxAdverseMinutesC > 0
                && (utcTime - atvC.EnteredAt).TotalMinutes >= _cfg.MaxAdverseMinutesC
                && (atvC.Direction == Direction.Long ? price < atvC.Entry : price > atvC.Entry))
            {
                _log.LogInformation("[C] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesC);
                AddAlert("ADVERSE", SetupId.C, $"Underwater >{_cfg.MaxAdverseMinutesC}min — exit", "orange");
                var preC = _setupC.GetActiveTrade(price);
                _setupC.ForceExit(price, utcTime, ExitReason.AdverseTime);
                await DispatchSignals(_setupC, utcTime, preC);
            }
        }
        if (IsAdverseTimeout(_activeD, _stD == 2, _entD, price, _entryTimeD, utcTime, _cfg.MaxAdverseMinutesD))
        {
            _log.LogInformation("[D] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesD);
            AddAlert("ADVERSE", SetupId.D, $"Underwater >{_cfg.MaxAdverseMinutesD}min — exit", "orange");
            await ForceExitD(tickBar, ExitReason.AdverseTime);
        }

        if (_cfg.EnableA && !_pastCutoffA)
        {
            var pre = _setupA.GetActiveTrade(price);
            _setupA.OnTick(price, utcTime, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
            await DispatchSignals(_setupA, utcTime, pre);
        }
        if (_cfg.EnableB && !_pastCutoffB)
        {
            var preB = _setupB.GetActiveTrade(price);
            _setupB.OnTick(price, utcTime, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
            await DispatchSignals(_setupB, utcTime, preB);
        }
        if (_cfg.EnableC && !_pastCutoffC)
        {
            var preC = _setupC.GetActiveTrade(price);
            _setupC.OnTick(price, utcTime, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
            await DispatchSignals(_setupC, utcTime, preC);
        }
        if (_cfg.EnableD && !_pastCutoffD) await EvalTickSetupD(price, utcTime);
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
            _setupA.Reset();
            _setupB.Reset();
            _setupC.Reset();
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
                _setupA.Reset();
                _setupB.Reset();
                _setupC.Reset();
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
        // Setup A/B/C sticky clearing is handled by _setupA/_setupB/_setupC.ClearPendingSignals() in DispatchSignals
        if (_exitBarIdxD != -1 && _barIndex > _exitBarIdxD)
            { _stickyTgtD = false; _stickyStpD = false; _exitBarIdxD = -1; }

        var sessStart   = new TimeOnly(_cfg.SessionStartHour, 0);

        // Per-setup cutoff — each setup can have its own cutoff time
        var cutoffA = new TimeOnly(_cfg.CutoffHourA, _cfg.CutoffMinuteA);
        if (_cfg.UseTimeFilter && localTime >= cutoffA && localTime < sessStart && !_pastCutoffA)
        {
            _pastCutoffA = true;
            // Disarm armed/waiting states — don't hold stale setups past cutoff
            if (_setupA.IsArmed) _setupA.Reset();
            _log.LogInformation("Cutoff A reached at {Time}", localTime);
            AddAlert("CUTOFF", SetupId.A, $"Cutoff A {cutoffA:HH:mm} — no new entries", "yellow");
        }

        var cutoffB = new TimeOnly(_cfg.CutoffHourB, _cfg.CutoffMinuteB);
        if (_cfg.UseTimeFilter && localTime >= cutoffB && localTime < sessStart && !_pastCutoffB)
        {
            _pastCutoffB = true;
            // Disarm armed/retest states — don't hold stale setups past cutoff
            if (_setupB.IsArmed) _setupB.Reset();
            _log.LogInformation("Cutoff B reached at {Time}", localTime);
            AddAlert("CUTOFF", SetupId.B, $"Cutoff B {cutoffB:HH:mm} — no new entries", "yellow");
        }

        var cutoffC = new TimeOnly(_cfg.CutoffHourC, _cfg.CutoffMinuteC);
        if (_cfg.UseTimeFilter && localTime >= cutoffC && localTime < sessStart && !_pastCutoffC)
        {
            _pastCutoffC = true;
            if (_setupC.IsArmed) _setupC.Reset();
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
            StrategyArmOnly(bar);
            EvaluateArmState(bar, localTime);
            await PublishSnapshot(bar); return;
        }

        // Warmup cooldown: arm evaluates but entry blocked (prevents instant trades
        // on engine restart mid-session)
        if (_warmupCooldown > 0)
        {
            _warmupCooldown--;
            _log.LogInformation("Warmup cooldown — arm OK, entry blocked ({N} bars remaining)", _warmupCooldown);
            StrategyArmOnly(bar);
            EvaluateArmState(bar, localTime);
            await PublishSnapshot(bar); return;
        }

        if (_cfg.EnableA && !_ddBreached && !_pastCutoffA)
        {
            var pre = _setupA.GetActiveTrade(_prices.GetLastPrice(_cfg.Ticker));
            _setupA.OnBar(bar, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
            await DispatchSignals(_setupA, bar.Time, pre);
        }

        if (_cfg.EnableB && !_ddBreached && !_pastCutoffB)
        {
            var preB = _setupB.GetActiveTrade(_prices.GetLastPrice(_cfg.Ticker));
            _setupB.OnBar(bar, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
            await DispatchSignals(_setupB, bar.Time, preB);
        }

        if (_cfg.EnableC && !_ddBreached && !_pastCutoffC)
        {
            bool orbWasActivated = _falseBreakout.OrbTracker.IsActivated;
            bool wasArmedC = _setupC.IsArmed || _setupC.IsActive;
            var preC = _setupC.GetActiveTrade(_prices.GetLastPrice(_cfg.Ticker));
            _setupC.OnBar(bar, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
            // Clear FalseBreakoutDetector activation after the strategy consumes it
            if (orbWasActivated && !wasArmedC && (_setupC.IsArmed || _setupC.IsActive))
                _falseBreakout.OrbTracker.ClearActivation();
            await DispatchSignals(_setupC, bar.Time, preC);
        }
        if (_cfg.EnableD && !_ddBreached && !_pastCutoffD)
            await ProcessSetupD(bar);

        // ── Adverse excursion time check (bar mode) ────────────────
        var barTime = BarExitTime(bar.Time);
        {
            var atv = _setupA.GetActiveTrade(bar.Close);
            if (atv != null && _cfg.MaxAdverseMinutesA > 0
                && (barTime - atv.EnteredAt).TotalMinutes >= _cfg.MaxAdverseMinutesA
                && (atv.Direction == Direction.Long ? bar.Close < atv.Entry : bar.Close > atv.Entry))
            {
                _log.LogInformation("[A] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesA);
                AddAlert("ADVERSE", SetupId.A, $"Underwater >{_cfg.MaxAdverseMinutesA}min — exit", "orange");
                var pre = _setupA.GetActiveTrade(bar.Close);
                _setupA.ForceExit(bar.Close, barTime, ExitReason.AdverseTime);
                await DispatchSignals(_setupA, barTime, pre);
            }
        }
        {
            var atvB = _setupB.GetActiveTrade(bar.Close);
            if (atvB != null && _cfg.MaxAdverseMinutesB > 0
                && (barTime - atvB.EnteredAt).TotalMinutes >= _cfg.MaxAdverseMinutesB
                && (atvB.Direction == Direction.Long ? bar.Close < atvB.Entry : bar.Close > atvB.Entry))
            {
                _log.LogInformation("[B] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesB);
                AddAlert("ADVERSE", SetupId.B, $"Underwater >{_cfg.MaxAdverseMinutesB}min — exit", "orange");
                var preB = _setupB.GetActiveTrade(bar.Close);
                _setupB.ForceExit(bar.Close, barTime, ExitReason.AdverseTime);
                await DispatchSignals(_setupB, barTime, preB);
            }
        }
        {
            var atvC = _setupC.GetActiveTrade(bar.Close);
            if (atvC != null && _cfg.MaxAdverseMinutesC > 0
                && (barTime - atvC.EnteredAt).TotalMinutes >= _cfg.MaxAdverseMinutesC
                && (atvC.Direction == Direction.Long ? bar.Close < atvC.Entry : bar.Close > atvC.Entry))
            {
                _log.LogInformation("[C] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesC);
                AddAlert("ADVERSE", SetupId.C, $"Underwater >{_cfg.MaxAdverseMinutesC}min — exit", "orange");
                var preC = _setupC.GetActiveTrade(bar.Close);
                _setupC.ForceExit(bar.Close, barTime, ExitReason.AdverseTime);
                await DispatchSignals(_setupC, barTime, preC);
            }
        }
        if (IsAdverseTimeout(_activeD, _stD == 2, _entD, bar.Close, _entryTimeD, barTime, _cfg.MaxAdverseMinutesD))
        {
            _log.LogInformation("[D] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesD);
            AddAlert("ADVERSE", SetupId.D, $"Underwater >{_cfg.MaxAdverseMinutesD}min — exit", "orange");
            await ForceExitD(bar, ExitReason.AdverseTime);
        }

        if (rthJustEndedA || _forceExitA)
        {
            _forceExitA = false;
            if (_setupA.IsActive)
            {
                var pre = _setupA.GetActiveTrade(bar.Close);
                _setupA.ForceExit(bar.Close, BarExitTime(bar.Time));
                await DispatchSignals(_setupA, BarExitTime(bar.Time), pre);
            }
        }
        if (rthJustEndedB || _forceExitB)
        {
            _forceExitB = false;
            if (_setupB.IsActive)
            {
                var preB = _setupB.GetActiveTrade(bar.Close);
                _setupB.ForceExit(bar.Close, BarExitTime(bar.Time));
                await DispatchSignals(_setupB, BarExitTime(bar.Time), preB);
            }
        }
        if (rthJustEndedC || _forceExitC)
        {
            _forceExitC = false;
            if (_setupC.IsActive)
            {
                var preC = _setupC.GetActiveTrade(bar.Close);
                _setupC.ForceExit(bar.Close, BarExitTime(bar.Time));
                await DispatchSignals(_setupC, BarExitTime(bar.Time), preC);
            }
        }
        if (rthJustEndedD || _forceExitD) { _forceExitD = false; await ForceExitD(bar); }

        await PublishSnapshot(bar);
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
                _cfg.UsePartialD, _cfg.UseBeD, PointValueFor(SetupId.D),
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
            ep, sl, tp, pp, _ctsD, bar.Time, _cfg.OrderTypeD, Ticker: _cfg.EffectiveTickerD);
        var fill = await _executor.OnEntrySignalAsync(sig);
        if (fill.HasValue && fill.Value != ep) await ApplyFillPrice(SetupId.D, fill.Value);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.D,
            $"{(isLong ? "LONG" : "SHORT")} {_ctsD}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task BookExitD(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _ctsD * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlD - comm;
        decimal risk = Math.Abs(_entD - _initStopD) * PointValueFor(SetupId.D) * _ctsD;
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
        var sig = new ExitSignal(SetupId.D, reason, exitPx, remCtsD, time, _cfg.EffectiveTickerD);
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

    private async Task ForceExitD(Bar bar, ExitReason reason = ExitReason.SessionEnd)
    {
        if (!_activeD) return;
        bool isLong = _stD == 2;
        _pnlD += ExitProcessor.ForcedExit(isLong, _entD, bar.Close,
            _ctsD, _partHitD, _cfg.UsePartialD, PointValueFor(SetupId.D), _cfg.PartialCtsD);
        await BookExitD(reason, bar.Close, BarExitTime(bar.Time), isLong);
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
                _pnlD += (long_ ? _partialD - _entD : _entD - _partialD) * PointValueFor(SetupId.D) * half;
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
        decimal pnl = _pnlD + (long_ ? exitPx - _entD : _entD - exitPx) * PointValueFor(SetupId.D) * remCts;
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
            ep, sl, tp, pp, _ctsD, utcTime, _cfg.OrderTypeD, Ticker: _cfg.EffectiveTickerD);
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
    /// Only used by Setup C/D entries — A and B operate independently (as before C/D integration).
    /// C/D share overlapping range logic, so opposing positions would net at the broker.
    /// </summary>
    private bool HasOpposingPosition(bool isLong)
    {
        if (_setupC.IsActive && (_setupC.GetSnapshot().State == 2) != isLong) return true;  // C: 2=long, -2=short
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
            if (_setupA.IsArmed) _setupA.Reset();
        }
        var cutoffB   = new TimeOnly(_cfg.CutoffHourB, _cfg.CutoffMinuteB);
        if (_cfg.UseTimeFilter && localTime >= cutoffB && localTime < sessStart)
        {
            _pastCutoffB = true;
            if (_setupB.IsArmed) _setupB.Reset();
        }
        var cutoffC   = new TimeOnly(_cfg.CutoffHourC, _cfg.CutoffMinuteC);
        if (_cfg.UseTimeFilter && localTime >= cutoffC && localTime < sessStart)
        {
            _pastCutoffC = true;
            if (_setupC.IsArmed) _setupC.Reset();
        }
        _pastCutoff = _pastCutoffA && _pastCutoffB;

        // Setup A/B/C arm evaluation is handled by StrategyArmOnly() at the cooldown call-site.
        // StrategyArmOnly calls _setupA/_setupB/_setupC.OnBar() with RevertEntry for entries.
    }

    private async Task PublishSnapshot(Bar bar)
    {
        _lastSnapshotUtc = DateTime.UtcNow;
        decimal lastPx = _prices.GetLastPrice(_cfg.Ticker);
        if (lastPx == 0) lastPx = bar.Close > 0 ? bar.Close : _lastBarClose;

        ActiveTradeView? viewA = _setupA.GetActiveTrade(lastPx);

        ActiveTradeView? viewB = _setupB.GetActiveTrade(lastPx);

        ActiveTradeView? viewC = _setupC.GetActiveTrade(lastPx);

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
                UnrealizedPnl = (isLong ? lastPx - _entD : _entD - lastPx) * PointValueFor(SetupId.D) * rem,
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

            TradeCountA = _setupA.GetSnapshot().TradeCount,
            MaxTradesA = _cfg.MaxTradesA,
            TradeCountB = _setupB.GetSnapshot().TradeCount, MaxTradesB = _cfg.MaxTradesB,
            TradeCountC = _setupC.GetSnapshot().TradeCount, MaxTradesC = _cfg.MaxTradesC,
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
            SetupAState = _setupA.GetSnapshot().State,
            SetupBState = _setupB.GetSnapshot().State,
            SetupCState = _setupC.GetSnapshot().State,
            SetupDState = _stD,

            StickyTgtA = _setupA.GetSnapshot().StickyTgt,
            StickyStpA = _setupA.GetSnapshot().StickyStp,
            StickyTgtB = _setupB.GetSnapshot().StickyTgt,
            StickyStpB = _setupB.GetSnapshot().StickyStp,
            StickyTgtC = _setupC.GetSnapshot().StickyTgt,
            StickyStpC = _setupC.GetSnapshot().StickyStp,
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
