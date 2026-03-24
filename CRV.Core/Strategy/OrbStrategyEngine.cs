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
    private ISetupStrategy _setupA;
    private ISetupStrategy _setupB;
    private ISetupStrategy _setupC;
    private ISetupStrategy _setupD;

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

    // ── Manual force-exit flags C/D ──────────────────────────────
    private volatile bool _forceExitC = false;
    private volatile bool _forceExitD = false;

    // ── Setup C/D cutoff (engine-managed, strategy doesn't own cutoff) ──
    private bool    _pastCutoffC  = false;
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
            VwapDevPeriod        = cfg.VwapDevPeriod,
            ExecutionTFMinutes          = cfg.ExecutionTFMinutes,
            FBMaxTimeOutsideMinutesOrb  = cfg.FBMaxTimeOutsideMinutesOrb,
            FBMaxTimeOutsideMinutesSR   = cfg.FBMaxTimeOutsideMinutesSR,
            FBMaxPenetrationPctOrb      = cfg.FBMaxPenetrationPctOrb,
            FBMaxPenetrationPctSR       = cfg.FBMaxPenetrationPctSR,
            FBMinRejectionBodyPct       = cfg.FBMinRejectionBodyPct,
            FBMaxTrendDayScore          = cfg.FBMaxTrendDayScore,
        };
        _sessionEngine   = new SessionEngine(modCfg);
        _sweepDetector   = new SweepDetector(modCfg);
        _vwapModel       = new VwapModel(modCfg);
        _openingDrive    = new OpeningDriveDetector(modCfg);
        _trendDay        = new TrendDayFilter(modCfg);
        _falseBreakout   = new FalseBreakoutDetector(modCfg);
        _setupA          = StrategyFactory.Create(BuildSetupConfigA(cfg));
        _setupB          = StrategyFactory.Create(BuildSetupConfigB(cfg));
        _setupC          = StrategyFactory.Create(BuildSetupConfigC(cfg));
        _setupD          = StrategyFactory.Create(BuildSetupConfigD(cfg));
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
        if (_setupD.IsActive)
        {
            var pre = _setupD.GetActiveTrade(px);
            _setupD.ForceExit(px, DateTime.UtcNow);
            await DispatchSignals(_setupD, DateTime.UtcNow, pre);
        }
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
            ExecutionTFMinutes          = cfg.ExecutionTFMinutes,
            FBMaxTimeOutsideMinutesOrb  = cfg.FBMaxTimeOutsideMinutesOrb,
            FBMaxTimeOutsideMinutesSR   = cfg.FBMaxTimeOutsideMinutesSR,
            FBMaxPenetrationPctOrb      = cfg.FBMaxPenetrationPctOrb,
            FBMaxPenetrationPctSR       = cfg.FBMaxPenetrationPctSR,
            FBMinRejectionBodyPct       = cfg.FBMinRejectionBodyPct,
            FBMaxTrendDayScore          = cfg.FBMaxTrendDayScore,
        };
        // Reconfigure modules with updated setup-specific parameters.
        // NOTE: Do NOT reconfigure _sessionEngine here — it tracks daily levels
        // (PDH/PDL, Asia/London highs) across all sessions. Only reset at daily boundary.
        _sweepDetector.Reconfigure(modCfg);
        _vwapModel.Reconfigure(modCfg);
        _openingDrive.Reconfigure(modCfg);
        _trendDay.Reconfigure(modCfg);
        _falseBreakout.Reconfigure(modCfg);
        _setupA.Reconfigure(BuildSetupConfigA(cfg));
        _setupB.Reconfigure(BuildSetupConfigB(cfg));
        _setupC.Reconfigure(BuildSetupConfigC(cfg));
        _setupD.Reconfigure(BuildSetupConfigD(cfg));

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
        _pastCutoffD = false;
        _todayWinsD = 0; _todayLossesD = 0; _todayWinPnlD = 0; _todayLossPnlD = 0;
        _lastTickTime = DateTime.MinValue;

        // Reset per-setup trade state
        _setupA.Reset();
        _setupB.Reset();
        _setupC.Reset();
        _setupD.Reset();

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
        if (_setupD.IsActive)
        {
            var preD = _setupD.GetActiveTrade(px);
            _setupD.ForceExit(px, ts);
            await DispatchSignals(_setupD, ts, preD);
        }
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
        _setupC.Reset(); _setupD.Reset();
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
        _openingDrive.Restore(cache.OpeningDriveBull, cache.OpeningDriveBear, cache.DriveRangePctATR);
        _log.LogInformation("ORB restored from cache — H:{H:F2} L:{L:F2} R:{R:F2} ATR%:{A:F3} Drive:{D}",
            cache.OrbHigh, cache.OrbLow, _orb.OrbRange, cache.OrbAtrRatio,
            cache.OpeningDriveBull ? "Bull" : cache.OpeningDriveBear ? "Bear" : "None");
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

    private StrategySetupConfig BuildSetupConfigD(StrategyConfig cfg) => new()
    {
        Name = "D", SetupId = SetupId.D, StrategyType = StrategyType.SessionFakeout,
        Enabled = cfg.EnableD,
        Ticker = cfg.EffectiveTickerD, PointValue = cfg.EffectivePointValueD,
        TickSize = cfg.EffectiveTickSizeD,
        Contracts = cfg.ContractsD, HiVolMult = cfg.HiVolMultD, MaxContracts = cfg.MaxContractsD,
        StopPct = cfg.StopPctD, TargetPct = cfg.TargetPctD, PartialPct = cfg.PartialPctD,
        NearPct = cfg.NearPctD, MinRr = cfg.MinRrD, Mode = "Conservative",
        EntryTickOffset = cfg.EntryTickOffsetD,
        OrderType = cfg.OrderTypeD,
        UseVwap = false, UseOrbClose = false,
        CutoffHour = cfg.CutoffHourD, CutoffMinute = cfg.CutoffMinuteD,
        CloseAtRthClose = cfg.CloseAtRthCloseD, MaxTrades = cfg.MaxTradesD,
        MaxAdverseMinutes = cfg.MaxAdverseMinutesD,
        UsePartial = cfg.UsePartialD, UseBe = cfg.UseBeD,
        PartialCts = cfg.PartialCtsD, AllowRearmAfterBe = cfg.AllowRearmAfterBeD,
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
        FakeoutPenetration:   _falseBreakout.OrbTracker.PenetrationDepth,
        SessionFakeoutBull:   _falseBreakout.SessionRangeTracker.IsActivated &&
                              _falseBreakout.SessionRangeTracker.BreakoutDirection == Direction.Long,
        SessionFakeoutBear:   _falseBreakout.SessionRangeTracker.IsActivated &&
                              _falseBreakout.SessionRangeTracker.BreakoutDirection == Direction.Short,
        SessionRangeHigh:     _falseBreakout.SessionRangeTracker.RangeHigh,
        SessionRangeLow:      _falseBreakout.SessionRangeTracker.RangeLow);

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

        bool srActivatedW = _falseBreakout.SessionRangeTracker.IsActivated;
        bool wasArmedDW = _setupD.IsArmed || _setupD.IsActive;
        _setupD.OnBar(bar, orbState, indState, modState);
        if (srActivatedW && !wasArmedDW && (_setupD.IsArmed || _setupD.IsActive))
            _falseBreakout.SessionRangeTracker.ClearActivation();
        if (_setupD.PendingEntry != null)
            _setupD.RevertEntry();      // undo entry, keep armed state
        else
            _setupD.ClearPendingSignals();
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
                await _executor.OnLevelsAdjustedAsync(setup.ToString(), view.CurrentStop, view.Target,
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
            decimal initStop = preTrade.InitialStop != 0 ? preTrade.InitialStop : preTrade.CurrentStop;
            decimal risk  = Math.Abs(preTrade.Entry - initStop) * PointValueFor(setup) * cts;
            decimal rMult = risk > 0 ? pnl / risk : 0;

            var trade = new TradeRecord
            {
                Setup          = setup,
                Direction      = preTrade.Direction,
                Ticker         = _cfg.Ticker,
                Contracts      = cts,
                Entry          = preTrade.Entry,
                InitialStop    = initStop,
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
                case SetupId.D:
                    if (net > 0) { _todayWinsD++;   _todayWinPnlD  += net; }
                    else         { _todayLossesD++; _todayLossPnlD += net; }
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
                    case SetupId.D: _pastCutoffD = true; break;
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
    // ApplyFillPrice for all setups (A/B/C/D) is now handled by
    // strategy.ApplyFill() inside DispatchSignals.

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
                if (_setupD.IsActive)
                {
                    var preD = _setupD.GetActiveTrade(price);
                    _setupD.ForceExit(price, utcTime);
                    await DispatchSignals(_setupD, utcTime, preD);
                }
                return;
            }
        }

        // ── Adverse excursion time check (tick mode) ───────────────
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
        {
            var atvD = _setupD.GetActiveTrade(price);
            if (atvD != null && _cfg.MaxAdverseMinutesD > 0
                && (utcTime - atvD.EnteredAt).TotalMinutes >= _cfg.MaxAdverseMinutesD
                && (atvD.Direction == Direction.Long ? price < atvD.Entry : price > atvD.Entry))
            {
                _log.LogInformation("[D] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesD);
                AddAlert("ADVERSE", SetupId.D, $"Underwater >{_cfg.MaxAdverseMinutesD}min — exit", "orange");
                var preD = _setupD.GetActiveTrade(price);
                _setupD.ForceExit(price, utcTime, ExitReason.AdverseTime);
                await DispatchSignals(_setupD, utcTime, preD);
            }
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
        if (_cfg.EnableD && !_pastCutoffD)
        {
            var preD = _setupD.GetActiveTrade(price);
            bool srWasActivated = _falseBreakout.SessionRangeTracker.IsActivated;
            bool wasArmedD = _setupD.IsArmed || _setupD.IsActive;
            _setupD.OnTick(price, utcTime, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
            if (srWasActivated && !wasArmedD && (_setupD.IsArmed || _setupD.IsActive))
                _falseBreakout.SessionRangeTracker.ClearActivation();
            await DispatchSignals(_setupD, utcTime, preD);
        }
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
            _setupD.Reset();
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
                _setupD.Reset();
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

        // Sticky exit signals for all setups (A/B/C/D) are now managed by the strategy instances
        // via ClearPendingSignals() in DispatchSignals.

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
            if (_setupD.IsArmed) _setupD.Reset();
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
                Symbol      = _cfg.Ticker?.TrimStart('/') ?? "",
                SessionId   = _activeSessionId,
                OrbHigh     = _orb.OrbHigh,
                OrbLow      = _orb.OrbLow,
                CloseRelPct = _orb.CloseRelPct,
                OrbAtrRatio = _orbAtrRatio,
                OpeningDriveBull  = _openingDrive.OpeningDriveBull,
                OpeningDriveBear  = _openingDrive.OpeningDriveBear,
                DriveRangePctATR  = _openingDrive.DriveRangePctATR,
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
        {
            bool srWasActivated = _falseBreakout.SessionRangeTracker.IsActivated;
            bool wasArmedD = _setupD.IsArmed || _setupD.IsActive;
            var preD = _setupD.GetActiveTrade(_prices.GetLastPrice(_cfg.Ticker));
            _setupD.OnBar(bar, BuildOrbState(), BuildIndicatorState(), BuildModuleState());
            // Clear FalseBreakoutDetector session range activation after the strategy consumes it
            if (srWasActivated && !wasArmedD && (_setupD.IsArmed || _setupD.IsActive))
                _falseBreakout.SessionRangeTracker.ClearActivation();
            await DispatchSignals(_setupD, bar.Time, preD);
        }

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
        {
            var atvD = _setupD.GetActiveTrade(bar.Close);
            if (atvD != null && _cfg.MaxAdverseMinutesD > 0
                && (barTime - atvD.EnteredAt).TotalMinutes >= _cfg.MaxAdverseMinutesD
                && (atvD.Direction == Direction.Long ? bar.Close < atvD.Entry : bar.Close > atvD.Entry))
            {
                _log.LogInformation("[D] Adverse timeout: underwater after {Min}min — force exit", _cfg.MaxAdverseMinutesD);
                AddAlert("ADVERSE", SetupId.D, $"Underwater >{_cfg.MaxAdverseMinutesD}min — exit", "orange");
                var preDAdv = _setupD.GetActiveTrade(bar.Close);
                _setupD.ForceExit(bar.Close, barTime, ExitReason.AdverseTime);
                await DispatchSignals(_setupD, barTime, preDAdv);
            }
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
        if (rthJustEndedD || _forceExitD)
        {
            _forceExitD = false;
            if (_setupD.IsActive)
            {
                var preDRth = _setupD.GetActiveTrade(bar.Close);
                _setupD.ForceExit(bar.Close, BarExitTime(bar.Time));
                await DispatchSignals(_setupD, BarExitTime(bar.Time), preDRth);
            }
        }

        await PublishSnapshot(bar);
    }

    // Setup D is fully handled by _setupD (SessionFakeoutStrategy instance)

    // ── C/D Priority Helpers ─────────────────────────────────

    /// <summary>
    /// Returns true if any setup has an active trade in the opposite direction.
    /// Only used by Setup C/D entries — A and B operate independently (as before C/D integration).
    /// C/D share overlapping range logic, so opposing positions would net at the broker.
    /// </summary>
    private bool HasOpposingPosition(bool isLong)
    {
        if (_setupC.IsActive && (_setupC.GetSnapshot().State == 2) != isLong) return true;  // C: 2=long, -2=short
        if (_setupD.IsActive && (_setupD.GetSnapshot().State == 2) != isLong) return true;  // D: 2=long, -2=short
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
        var cutoffDw  = new TimeOnly(_cfg.CutoffHourD, _cfg.CutoffMinuteD);
        if (_cfg.UseTimeFilter && localTime >= cutoffDw && localTime < sessStart)
        {
            _pastCutoffD = true;
            if (_setupD.IsArmed) _setupD.Reset();
        }
        _pastCutoff = _pastCutoffA && _pastCutoffB;

        // Setup A/B/C/D arm evaluation is handled by StrategyArmOnly() at the cooldown call-site.
        // StrategyArmOnly calls _setupA/_setupB/_setupC/_setupD.OnBar() with RevertEntry for entries.
    }

    private async Task PublishSnapshot(Bar bar)
    {
        _lastSnapshotUtc = DateTime.UtcNow;
        decimal lastPx = _prices.GetLastPrice(_cfg.Ticker);
        if (lastPx == 0) lastPx = bar.Close > 0 ? bar.Close : _lastBarClose;

        await _sink.OnSnapshotAsync(new EngineSnapshot
        {
            Time   = bar.Time, Ticker = _cfg.Ticker, IsLive = true,
            LastPrice  = lastPx,
            LastUpdate = DateTime.UtcNow,
            TodayPnl    = _todayPnl,
            TodayTrades = _todayWins + _todayLosses,
            TodayWins   = _todayWins,
            TodayLosses = _todayLosses,
            TodayMaxDD  = _todayMaxDD,

            Expectancy  = CalcExpectancy(_todayWins, _todayLosses, _todayWinPnl, _todayLossPnl),

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
            SessionEnded = _rthEnded || IsWeekendClosed(),
            ActiveSessionId = _activeSessionId,
            OrbWindowStart  = _cfg.OrbStart.ToString("HH:mm"),
            OrbWindowEnd    = _cfg.OrbEnd.ToString("HH:mm"),

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
