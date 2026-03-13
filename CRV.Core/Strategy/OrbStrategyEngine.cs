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
    private readonly StrategyConfig     _cfg;
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
    private readonly CompositeSetupEngine   _compositeSetups;

    // ── Session state ─────────────────────────────────────────
    private DateTime _lastDate       = DateTime.MinValue;
    private bool     _pastCutoff     = false;
    private bool     _pastCutoffA    = false;
    private bool     _pastCutoffB    = false;
    // C/D/F cutoff flags are stored on the setup field blocks above
    private bool     _rthEnded       = false;
    private bool     _orbLoggedFormed = false;
    private decimal  _orbAtrRatio    = 0;
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
    private volatile bool _forceExitC = false;
    private volatile bool _forceExitD = false;
    private volatile bool _forceExitF = false;

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

    // ── Setup C — Sweep Reversal ────────────────────────────────
    private int      _stC          = 0;   // 0=Idle, ±1=Armed, ±2=Active
    private decimal  _entC         = 0;
    private decimal  _stopC        = 0;
    private decimal  _tgtC         = 0;
    private decimal  _partialC     = 0;
    private decimal  _initStopC    = 0;
    private bool     _activeC      = false;
    private int      _tradeCountC  = 0;
    private bool     _partHitC     = false;
    private decimal  _pnlC         = 0;
    private DateTime _entryTimeC   = DateTime.MinValue;
    private bool     _stickyTgtC   = false;
    private bool     _stickyStpC   = false;
    private int      _exitBarIdxC  = -1;
    private bool     _pastCutoffC  = false;
    private int      _todayWinsC   = 0;
    private int      _todayLossesC = 0;
    private decimal  _todayWinPnlC = 0;
    private decimal  _todayLossPnlC = 0;
    // C-specific arm tracking
    private decimal  _sweepExtremeC      = 0;   // sweep low (bull) or high (bear)
    private decimal  _sweepBarHighC      = 0;
    private decimal  _sweepBarLowC       = 0;
    private decimal  _confirmBarHighC    = 0;
    private decimal  _confirmBarLowC     = 0;
    private bool     _awaitingConfirmC   = false;

    // ── Setup D — Opening Drive Pullback ────────────────────────
    private int      _stD          = 0;   // 0=Idle, ±1=Armed, ±2=Active
    private decimal  _entD         = 0;
    private decimal  _stopD        = 0;
    private decimal  _tgtD         = 0;
    private decimal  _partialD     = 0;
    private decimal  _initStopD    = 0;
    private bool     _activeD      = false;
    private int      _tradeCountD  = 0;
    private bool     _partHitD     = false;
    private decimal  _pnlD         = 0;
    private DateTime _entryTimeD   = DateTime.MinValue;
    private bool     _stickyTgtD   = false;
    private bool     _stickyStpD   = false;
    private int      _exitBarIdxD  = -1;
    private bool     _pastCutoffD  = false;
    private int      _todayWinsD   = 0;
    private int      _todayLossesD = 0;
    private decimal  _todayWinPnlD = 0;
    private decimal  _todayLossPnlD = 0;
    // D-specific arm tracking
    private decimal  _pullbackSwingLowD  = 0;
    private decimal  _pullbackSwingHighD = 0;
    private decimal  _armBarHighD        = 0;
    private decimal  _armBarLowD         = 0;

    // ── Setup F — Midday VWAP Reversion ─────────────────────────
    private int      _stF          = 0;   // 0=Idle, ±1=Armed, ±2=Active
    private decimal  _entF         = 0;
    private decimal  _stopF        = 0;
    private decimal  _tgtF         = 0;
    private decimal  _partialF     = 0;
    private decimal  _initStopF    = 0;
    private bool     _activeF      = false;
    private int      _tradeCountF  = 0;
    private bool     _partHitF     = false;
    private decimal  _pnlF         = 0;
    private DateTime _entryTimeF   = DateTime.MinValue;
    private bool     _stickyTgtF   = false;
    private bool     _stickyStpF   = false;
    private int      _exitBarIdxF  = -1;
    private bool     _pastCutoffF  = false;
    private int      _todayWinsF   = 0;
    private int      _todayLossesF = 0;
    private decimal  _todayWinPnlF = 0;
    private decimal  _todayLossPnlF = 0;
    // F-specific arm tracking
    private decimal  _rejectionBarHighF = 0;
    private decimal  _rejectionBarLowF  = 0;
    private decimal  _sessionExtremeF   = 0;   // session high (short) or low (long)

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
        _orb      = new OrbCalculator(cfg.OrbStart, cfg.OrbEnd, cfg.Timezone, cfg.ExecutionTFMinutes);
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
        _compositeSetups = new CompositeSetupEngine();
    }

    /// <summary>Signal the engine to force-exit the Setup A position on the next bar.</summary>
    public void RequestForceExitA() => _forceExitA = true;

    /// <summary>Signal the engine to force-exit the Setup B position on the next bar.</summary>
    public void RequestForceExitB() => _forceExitB = true;

    /// <summary>Signal the engine to force-exit the Setup C position on the next bar.</summary>
    public void RequestForceExitC() => _forceExitC = true;

    /// <summary>Signal the engine to force-exit the Setup D position on the next bar.</summary>
    public void RequestForceExitD() => _forceExitD = true;

    /// <summary>Signal the engine to force-exit the Setup F position on the next bar.</summary>
    public void RequestForceExitF() => _forceExitF = true;

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

    public void SeedModuleHistory(IReadOnlyList<Bar> dailyBars)
    {
        _sessionEngine.SeedHistory(dailyBars);
    }

    // ── Tick-mode flag ────────────────────────────────────────────
    private bool _tickModeEnabled = false;

    /// <summary>
    /// Enable tick-based entry/exit evaluation.
    /// When enabled, <see cref="ProcessPriceTickAsync"/> evaluates entries/exits on each price tick
    /// and <see cref="ProcessBarAsync"/> only updates indicators and arm state (skips bar-level
    /// entry/exit).  Used for both live trading (L1 ticks) and backtest tick simulation
    /// (1-min OHLC prices fired as four sequential ticks per bar).
    /// </summary>
    public void EnableTickMode() => _tickModeEnabled = true;

    /// <summary>
    /// Evaluate entry and exit conditions against a realtime L1 price tick.
    /// Only active after EnableTickMode() has been called.
    /// WARNING: NOT thread-safe. Shares mutable state with ProcessBarAsync.
    /// The caller MUST serialize all calls to this method and ProcessBarAsync
    /// (e.g., via SemaphoreSlim(1,1)) to prevent race conditions.
    /// </summary>
    public async Task ProcessPriceTickAsync(decimal price, DateTime utcTime)
    {
        if (!_tickModeEnabled) return;
        if (price <= 0) return;
        if (!_orb.IsSet || _orb.OrbRange <= 0) return;
        if (_ddBreached) return;

        if (_rthEnded) return;

        _sessionEngine.OnTick(price, utcTime);

        if (_cfg.EnableA && !_pastCutoffA) await EvalTickSetupA(price, utcTime);
        if (_cfg.EnableB && !_pastCutoffB) await EvalTickSetupB(price, utcTime);
        if (_cfg.EnableC && _activeC) await EvalTickExitCDF(SetupId.C, price, utcTime);
        if (_cfg.EnableD && _activeD) await EvalTickExitCDF(SetupId.D, price, utcTime);
        if (_cfg.EnableF && _activeF) await EvalTickExitCDF(SetupId.F, price, utcTime);
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
            partJustHit = true;
            _partHitA   = true;
            int half    = (int)Math.Floor(_cfg.ContractsA * 0.5);
            var psig    = new PartialSignal(SetupId.A, long_ ? Direction.Long : Direction.Short,
                _partialA, half, _cfg.ContractsA - half, _entA, utcTime);
            await _executor.OnPartialSignalAsync(psig);
            await _sink.OnPartialAsync(psig);
            AddAlert("PARTIAL", SetupId.A, $"Partial @ {_partialA:F2}", "yellow");
            if (_cfg.UseBeA)
            {
                var besig = new BESignal(SetupId.A, long_ ? Direction.Long : Direction.Short,
                    _entA, _entA, psig.ContractsRemaining, utcTime);
                _stopA = _entA;
                await _executor.OnBESignalAsync(besig);
                await _sink.OnBEMoveAsync(besig);
                AddAlert("MOVE_BE", SetupId.A, $"Stop → BE {_entA:F2}", "yellow");
            }
            if (!hitTarget && !hitStop) return; // partial only, trade still open
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _tgtA : _stopA;
        decimal    pnl    = (long_ ? exitPx - _entA : _entA - exitPx) * _cfg.PointValue * _cfg.ContractsA;
        _pnlA    = pnl;
        _activeA = false;
        _stA     = 0;
        _tradeCountA++;
        if (hitTarget) { _stickyTgtA = true; _exitBarIdxA = _barIndex; }
        else           { _stickyStpA = true; _exitBarIdxA = _barIndex; }
        await BookExitA(reason, exitPx, utcTime, long_, sameBarPartial: partJustHit);
    }

    private async Task TryEntryAFromTick(decimal ep, bool isLong, decimal orbRange, DateTime utcTime)
    {
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
        _stA = isLong ? 2 : -2; _enteredThisBar = true;
        _entryTimeA = utcTime;

        _log.LogInformation("[Setup A TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _cfg.ContractsA, ep, sl, tp, rr);
        var sig = new EntrySignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _cfg.ContractsA, utcTime);
        await _executor.OnEntrySignalAsync(sig);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.A,
            $"{(isLong ? "LONG" : "SHORT")} {_cfg.ContractsA}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
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
            partJustHit = true;
            _partHitB   = true;
            int half    = (int)Math.Floor(_cfg.ContractsB * 0.5);
            var psig    = new PartialSignal(SetupId.B, longB ? Direction.Long : Direction.Short,
                _partialB, half, _cfg.ContractsB - half, _entB, utcTime);
            await _executor.OnPartialSignalAsync(psig);
            await _sink.OnPartialAsync(psig);
            AddAlert("PARTIAL", SetupId.B, $"Partial @ {_partialB:F2}", "yellow");
            if (_cfg.UseBeB)
            {
                var besig = new BESignal(SetupId.B, longB ? Direction.Long : Direction.Short,
                    _entB, _entB, psig.ContractsRemaining, utcTime);
                _stopB = _entB;
                await _executor.OnBESignalAsync(besig);
                await _sink.OnBEMoveAsync(besig);
                AddAlert("MOVE_BE", SetupId.B, $"Stop → BE {_entB:F2}", "yellow");
            }
            if (!hitTarget && !hitStop) return; // partial only, trade still open
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _tgtB : _stopB;
        decimal    pnl    = (longB ? exitPx - _entB : _entB - exitPx) * _cfg.PointValue * _cfg.ContractsB;
        _pnlB    = pnl;
        _activeB = false;
        _stB     = 0;
        _tradeCountB++;
        if (hitTarget) { _stickyTgtB = true; _exitBarIdxB = _barIndex; }
        else           { _stickyStpB = true; _exitBarIdxB = _barIndex; }
        await BookExitB(reason, exitPx, utcTime, longB, sameBarPartial: partJustHit);
    }

    private async Task TryEntryBFromTick(decimal ep, bool isLong, DateTime utcTime)
    {
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
        _stB = isLong ? 3 : -3; _enteredThisBar = true;
        _entryTimeB = utcTime;

        _log.LogInformation("[Setup B TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _cfg.ContractsB, ep, sl, tp, rr);
        var sig = new EntrySignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _cfg.ContractsB, utcTime);
        await _executor.OnEntrySignalAsync(sig);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.B,
            $"{(isLong ? "LONG" : "SHORT")} {_cfg.ContractsB}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task ProcessBarInternalAsync(Bar bar, bool warmupOnly, CancellationToken ct)
    {
        var local     = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, _tz);
        var tradingDate = _cfg.TradingDate(local);
        var localTime = TimeOnly.FromDateTime(local);

        bool newDay = tradingDate != _lastDate;
        if (newDay)
        {
            _log.LogInformation("New trading session {Date} for {Ticker} (local: {LocalTime})",
                tradingDate.ToString("yyyy-MM-dd"), _cfg.Ticker, local.ToString("HH:mm"));
            _lastDate    = tradingDate;
            _pastCutoff      = false;
            _pastCutoffA     = false;
            _pastCutoffB     = false;
            _pastCutoffC     = false;
            _pastCutoffD     = false;
            _pastCutoffF     = false;
            _rthEnded        = false;
            _orbLoggedFormed = false;
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
            _todayWinsF  = 0; _todayLossesF = 0; _todayWinPnlF = 0; _todayLossPnlF = 0;
            _ddBreached  = false;
            ResetSetupA();
            ResetSetupB();
            ResetSetupC();
            ResetSetupD();
            ResetSetupF();
            // Reset VWAP immediately at the session boundary so the dashboard
            // shows 0 right at 6 PM ET — not delayed until the first confirmed bar.
            _vwap.NewSession(tradingDate);
            _sessionEngine.NewSession(tradingDate);
            _sweepDetector.NewSession(tradingDate);
            _vwapModel.NewSession(tradingDate);
            _openingDrive.NewSession(tradingDate);
            _trendDay.NewSession(tradingDate);
            _compositeSetups.Reset();
        }

        // ORB tracks the developing bar in real time (included before confirmed guard)
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

        // Warmup mode: indicators are built + arm state is evaluated so the dashboard
        // shows correct Armed/Idle badges when the engine starts mid-session.
        // No entries, exits, broker signals, trade counting, or snapshots.
        if (warmupOnly)
        {
            // Track session flags so stale alerts don't fire on first live bar
            if (_orb.IsSet && _orb.OrbRange > 0) _orbLoggedFormed = true;
            var wSessStart = new TimeOnly(_cfg.SessionStartHour, 0);
            // Guard against overnight bars (19:00–23:59 ET) falsely triggering the cutoff.
            // Only set these flags for bars within the calendar trading day (before session restart).
            var wCutoffA = new TimeOnly(_cfg.CutoffHourA, _cfg.CutoffMinuteA);
            if (_cfg.UseTimeFilter && localTime >= wCutoffA && localTime < wSessStart) _pastCutoffA = true;
            var wCutoffB = new TimeOnly(_cfg.CutoffHourB, _cfg.CutoffMinuteB);
            if (_cfg.UseTimeFilter && localTime >= wCutoffB && localTime < wSessStart) _pastCutoffB = true;
            if (_cfg.EnableC) { var wCutC = new TimeOnly(_cfg.CutoffHourC, _cfg.CutoffMinuteC); if (_cfg.UseTimeFilter && localTime >= wCutC && localTime < wSessStart) _pastCutoffC = true; }
            if (_cfg.EnableD) { var wCutD = new TimeOnly(_cfg.CutoffHourD, _cfg.CutoffMinuteD); if (_cfg.UseTimeFilter && localTime >= wCutD && localTime < wSessStart) _pastCutoffD = true; }
            if (_cfg.EnableF) { var wCutF = new TimeOnly(_cfg.CutoffHourF, _cfg.CutoffMinuteF); if (_cfg.UseTimeFilter && localTime >= wCutF && localTime < wSessStart) _pastCutoffF = true; }
            _pastCutoff = _pastCutoffA && _pastCutoffB;
            if (localTime >= _cfg.RthEnd && localTime < wSessStart) _rthEnded = true;
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
        if (_exitBarIdxF != -1 && _barIndex > _exitBarIdxF)
            { _stickyTgtF = false; _stickyStpF = false; _exitBarIdxF = -1; }

        var sessStart   = new TimeOnly(_cfg.SessionStartHour, 0);

        // Per-setup cutoff — each setup can have its own cutoff time
        var cutoffA = new TimeOnly(_cfg.CutoffHourA, _cfg.CutoffMinuteA);
        if (_cfg.UseTimeFilter && localTime >= cutoffA && localTime < sessStart && !_pastCutoffA)
        {
            _pastCutoffA = true;
            _log.LogInformation("Cutoff A reached at {Time}", localTime);
            AddAlert("CUTOFF", SetupId.A, $"Cutoff A {cutoffA:HH:mm} — no new entries", "yellow");
        }

        var cutoffB = new TimeOnly(_cfg.CutoffHourB, _cfg.CutoffMinuteB);
        if (_cfg.UseTimeFilter && localTime >= cutoffB && localTime < sessStart && !_pastCutoffB)
        {
            _pastCutoffB = true;
            _log.LogInformation("Cutoff B reached at {Time}", localTime);
            AddAlert("CUTOFF", SetupId.B, $"Cutoff B {cutoffB:HH:mm} — no new entries", "yellow");
        }

        if (_cfg.EnableC && !_pastCutoffC)
        {
            var cutoffC = new TimeOnly(_cfg.CutoffHourC, _cfg.CutoffMinuteC);
            if (_cfg.UseTimeFilter && localTime >= cutoffC && localTime < sessStart)
            {
                _pastCutoffC = true;
                _log.LogInformation("Cutoff C reached at {Time}", localTime);
                AddAlert("CUTOFF", SetupId.C, $"Cutoff C {cutoffC:HH:mm} — no new entries", "yellow");
            }
        }

        if (_cfg.EnableD && !_pastCutoffD)
        {
            var cutoffD = new TimeOnly(_cfg.CutoffHourD, _cfg.CutoffMinuteD);
            if (_cfg.UseTimeFilter && localTime >= cutoffD && localTime < sessStart)
            {
                _pastCutoffD = true;
                _log.LogInformation("Cutoff D reached at {Time}", localTime);
                AddAlert("CUTOFF", SetupId.D, $"Cutoff D {cutoffD:HH:mm} — no new entries", "yellow");
            }
        }

        if (_cfg.EnableF && !_pastCutoffF)
        {
            var cutoffF = new TimeOnly(_cfg.CutoffHourF, _cfg.CutoffMinuteF);
            if (_cfg.UseTimeFilter && localTime >= cutoffF && localTime < sessStart)
            {
                _pastCutoffF = true;
                _log.LogInformation("Cutoff F reached at {Time}", localTime);
                AddAlert("CUTOFF", SetupId.F, $"Cutoff F {cutoffF:HH:mm} — no new entries", "yellow");
            }
        }

        _pastCutoff = _pastCutoffA && _pastCutoffB;

        bool rthJustEndedA = _cfg.CloseAtRthCloseA && localTime >= _cfg.RthEnd && localTime < sessStart && !_rthEnded;
        bool rthJustEndedB = _cfg.CloseAtRthCloseB && localTime >= _cfg.RthEnd && localTime < sessStart && !_rthEnded;
        bool rthJustEndedC = _cfg.CloseAtRthCloseC && localTime >= _cfg.RthEnd && localTime < sessStart && !_rthEnded;
        bool rthJustEndedD = _cfg.CloseAtRthCloseD && localTime >= _cfg.RthEnd && localTime < sessStart && !_rthEnded;
        bool rthJustEndedF = _cfg.CloseAtRthCloseF && localTime >= _cfg.RthEnd && localTime < sessStart && !_rthEnded;
        if (localTime >= _cfg.RthEnd && localTime < sessStart && !_rthEnded)
        {
            _log.LogInformation("Session ended at {Time}", localTime);
            AddAlert("SESSION", SetupId.A, $"Session ended {_cfg.RthEnd:HH:mm}", "red");
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
            _openingDrive.FreezeAtOrbClose(_orb.OrbRange);
            _orbAtrRatio = _atr.IsReady && _atr.Value > 0 ? _orb.OrbRange / _atr.Value : 0;
            _log.LogInformation("ORB formed: High={H} Low={L} Range={R} ATR/ORB={Ratio:F2} at {Time}",
                _orb.OrbHigh, _orb.OrbLow, _orb.OrbRange, _orbAtrRatio, localTime);
            AddAlert("ORB", SetupId.A, $"ORB formed — H:{_orb.OrbHigh:F2} L:{_orb.OrbLow:F2} R:{_orb.OrbRange:F2}", "blue");
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

            _compositeSetups.Evaluate(
                anyBullSweep: _sweepDetector.AnyBullSweep,
                anyBearSweep: _sweepDetector.AnyBearSweep,
                close: bar.Close, vwap: _vwap.Value,
                bullScore: _trendDay.BullScore, bearScore: _trendDay.BearScore,
                openingDriveBull: _openingDrive.OpeningDriveBull,
                openingDriveBear: _openingDrive.OpeningDriveBear,
                trendDayBull: _trendDay.TrendDayBull,
                trendDayBear: _trendDay.TrendDayBear,
                bullVwapPullback: _vwapModel.BullVWAPPullback,
                bearVwapPullback: _vwapModel.BearVWAPPullback,
                vwapReversionLong: _vwapModel.VWAPReversionLong,
                vwapReversionShort: _vwapModel.VWAPReversionShort,
                inMidday: _sessionEngine.CurrentSession == SessionType.Midday,
                londonSweptAsiaLow: _sessionEngine.LondonSweptAsiaLow,
                londonSweptAsiaHigh: _sessionEngine.LondonSweptAsiaHigh,
                nyBullExpansion: _sessionEngine.NYBullExpansion,
                nyBearExpansion: _sessionEngine.NYBearExpansion
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

        if (_cfg.EnableA && !_ddBreached && !_pastCutoffA)
            await ProcessSetupA(bar, aboveVwapA, belowVwapA, orbLongOkA, orbShortOkA);

        if (_cfg.EnableB && !_ddBreached && !_pastCutoffB)
            await ProcessSetupB(bar, aboveVwapB, belowVwapB, orbLongOkB, orbShortOkB);

        // C/D/F: D first (highest priority), then C, then F
        if (_cfg.EnableD && !_ddBreached && !_pastCutoffD)
            await ProcessSetupD(bar, local);
        if (_cfg.EnableC && !_ddBreached && !_pastCutoffC)
            await ProcessSetupC(bar, local);
        if (_cfg.EnableF && !_ddBreached && !_pastCutoffF)
            await ProcessSetupF(bar, local);

        if (rthJustEndedA || _forceExitA) { _forceExitA = false; await ForceExitA(bar); }
        if (rthJustEndedB || _forceExitB) { _forceExitB = false; await ForceExitB(bar); }
        if (rthJustEndedC || _forceExitC) { _forceExitC = false; await ForceExitC(bar); }
        if (rthJustEndedD || _forceExitD) { _forceExitD = false; await ForceExitD(bar); }
        if (rthJustEndedF || _forceExitF) { _forceExitF = false; await ForceExitF(bar); }

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

                // Arm
                if (isReady && _stA == 0)
                {
                    decimal nearDist = orbRange * _cfg.NearPct;
                    if (bar.High >= orbHigh - nearDist && orbLongOk && aboveVwap)
                    {
                        _stA = 1; _armEntryA = bar.Open;
                        _log.LogDebug("Setup A armed LONG: OrbHigh={H:F2} NearDist={N:F2}", orbHigh, nearDist);
                        AddAlert("ARMED", SetupId.A, "A LONG armed", "gray");
                    }
                    else if (bar.Low <= orbLow + nearDist && orbShortOk && belowVwap)
                    {
                        _stA = -1; _armEntryA = bar.Open;
                        _log.LogDebug("Setup A armed SHORT: OrbLow={L:F2} NearDist={N:F2}", orbLow, nearDist);
                        AddAlert("ARMED", SetupId.A, "A SHORT armed", "gray");
                    }
                }

                // Bar-level entry — skipped in tick mode (handled by ProcessPriceTickAsync)
                if (!_tickModeEnabled)
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

        // ── Exit (bar mode only; tick mode uses ProcessPriceTickAsync) ──────
        if (_activeA && !_tickModeEnabled)
        {
            bool isLong   = _stA == 2;
            bool prevPart = _partHitA;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entA, _stopA, _tgtA, _partialA,
                _cfg.ContractsA, _pnlA, _partHitA,
                _cfg.UsePartialA, _cfg.UseBeA, _cfg.PointValue,
                bar.High, bar.Low);

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
                int half = (int)Math.Floor(_cfg.ContractsA * 0.5);
                var psig = new PartialSignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
                    _partialA, half, _cfg.ContractsA - half, _entA, bar.Time);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.A, $"Partial @ {_partialA:F2}", "yellow");

                if (_cfg.UseBeA)
                {
                    var besig = new BESignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
                        _entA, _entA, psig.ContractsRemaining, bar.Time);
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.A, $"Stop → BE {_entA:F2}", "yellow");
                }
            }

            if (closingNow)
            {
                _stA = 0; _tradeCountA++;
                if (result.HitTarget) { _stickyTgtA = true; _exitBarIdxA = _barIndex; }
                else                  { _stickyStpA = true; _exitBarIdxA = _barIndex; }
                await BookExitA(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    result.HitTarget ? _tgtA : _stopA, bar.Time, isLong,
                    sameBarPartial: partJustHit);
            }
        }
    }

    private async Task TryEntryA(Bar bar, decimal ep, bool isLong, decimal orbRange)
    {
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
        _stA = isLong ? 2 : -2; _enteredThisBar = true;
        _entryTimeA = bar.Time;

        _log.LogInformation("[Setup A] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _cfg.ContractsA, ep, sl, tp, rr);
        var sig = new EntrySignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _cfg.ContractsA, bar.Time);
        await _executor.OnEntrySignalAsync(sig);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.A,
            $"{(isLong ? "LONG" : "SHORT")} {_cfg.ContractsA}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task BookExitA(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _cfg.ContractsA * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlA - comm;
        decimal risk = Math.Abs(_entA - _initStopA) * _cfg.PointValue * _cfg.ContractsA;
        decimal rMult = risk > 0 ? _pnlA / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.A, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _cfg.ContractsA,
            Entry = _entA, InitialStop = _initStopA, Target = _tgtA, Partial = _partialA,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitA, PartialPrice = _partialA,   // correctly true for same-bar partial too
            GrossPnl = _pnlA, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeA, ExitedAt = time
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsA++;   _todayWinPnlA  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesA++; _todayLossPnlA += net; }

        _log.LogInformation("[Setup A] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        // Broker contract count:
        //   • Prior-bar partial:     partial order was already placed → only half remains → send half.
        //   • Same-bar partial+close: partial/BE signals were suppressed → full bracket still in place → send all.
        //   • No partial:            send all.
        int remCtsA = (_partHitA && _cfg.UsePartialA && !sameBarPartial)
            ? _cfg.ContractsA - (int)Math.Floor(_cfg.ContractsA * 0.5)
            : _cfg.ContractsA;
        var sig = new ExitSignal(SetupId.A, reason, exitPx, remCtsA, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.A,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeA = false; _stA = 0; _partHitA = false; _pnlA = 0;
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

                // Breakout detection
                if (isReady && _stB == 0)
                {
                    if (bar.Close > orbHigh && orbLongOk && aboveVwap)
                    { _stB = 1; _armEntryB = bar.Open; AddAlert("ARMED", SetupId.B, "B Bull break", "gray"); }
                    else if (bar.Close < orbLow && orbShortOk && belowVwap)
                    { _stB = -1; _armEntryB = bar.Open; AddAlert("ARMED", SetupId.B, "B Bear break", "gray"); }
                }

                if (_cfg.IsAggressiveB)
                {
                    // Bar-level entry — skipped in tick mode (handled by ProcessPriceTickAsync)
                    if (!_tickModeEnabled)
                    {
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
                }
                else
                {
                    // Conservative — retest-zone state transitions always run (arm → retest → de-arm)
                    decimal retestW = orbRange * _cfg.RetestPct;
                    if (_stB == 1 && bar.Low <= orbHigh + retestW && bar.High >= orbHigh - retestW) _stB = 2;
                    if (_stB == -1 && bar.High >= orbLow - retestW && bar.Low <= orbLow + retestW) _stB = -2;

                    // Bar-level entry — skipped in tick mode (handled by ProcessPriceTickAsync)
                    if (!_tickModeEnabled)
                    {
                        if (_stB == 2 && bar.Close > orbHigh && canEnterB)
                            await TryEntryB(bar, orbHigh, true, orbRange);
                        else if (_stB == -2 && bar.Close < orbLow && canEnterB)
                            await TryEntryB(bar, orbLow, false, orbRange);
                    }

                    if (_stB == 2  && bar.Close < _orb.OrbMid) _stB = 0;
                    if (_stB == -2 && bar.Close > _orb.OrbMid) _stB = 0;
                }
            }
        }

        // ── Exit (bar mode only; tick mode uses ProcessPriceTickAsync) ──────
        if (_activeB && !_tickModeEnabled)
        {
            bool isLong   = _stB == 3;
            bool prevPart = _partHitB;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entB, _stopB, _tgtB, _partialB,
                _cfg.ContractsB, _pnlB, _partHitB,
                _cfg.UsePartialB, _cfg.UseBeB, _cfg.PointValue,
                bar.High, bar.Low);

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
                int half = (int)Math.Floor(_cfg.ContractsB * 0.5);
                var psig = new PartialSignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
                    _partialB, half, _cfg.ContractsB - half, _entB, bar.Time);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.B, $"Partial @ {_partialB:F2}", "yellow");

                if (_cfg.UseBeB)
                {
                    var besig = new BESignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
                        _entB, _entB, psig.ContractsRemaining, bar.Time);
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.B, $"Stop → BE {_entB:F2}", "yellow");
                }
            }

            if (closingNow)
            {
                _stB = 0; _tradeCountB++;
                if (result.HitTarget) { _stickyTgtB = true; _exitBarIdxB = _barIndex; }
                else                  { _stickyStpB = true; _exitBarIdxB = _barIndex; }
                await BookExitB(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    result.HitTarget ? _tgtB : _stopB, bar.Time, isLong,
                    sameBarPartial: partJustHit);
            }
        }
    }

    private async Task TryEntryB(Bar bar, decimal ep, bool isLong, decimal orbRange)
    {
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
        _stB = isLong ? 3 : -3; _enteredThisBar = true;
        _entryTimeB = bar.Time;

        _log.LogInformation("[Setup B] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _cfg.ContractsB, ep, sl, tp, rr);
        var sig = new EntrySignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _cfg.ContractsB, bar.Time);
        await _executor.OnEntrySignalAsync(sig);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.B,
            $"{(isLong ? "LONG" : "SHORT")} {_cfg.ContractsB}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task BookExitB(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _cfg.ContractsB * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlB - comm;
        decimal risk = Math.Abs(_entB - _initStopB) * _cfg.PointValue * _cfg.ContractsB;
        decimal rMult = risk > 0 ? _pnlB / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.B, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _cfg.ContractsB,
            Entry = _entB, InitialStop = _initStopB, Target = _tgtB, Partial = _partialB,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitB, PartialPrice = _partialB,   // correctly true for same-bar partial too
            GrossPnl = _pnlB, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeB, ExitedAt = time
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsB++;   _todayWinPnlB  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesB++; _todayLossPnlB += net; }

        _log.LogInformation("[Setup B] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        // Broker contract count:
        //   • Prior-bar partial:     partial order was already placed → only half remains → send half.
        //   • Same-bar partial+close: partial/BE signals were suppressed → full bracket still in place → send all.
        //   • No partial:            send all.
        int remCtsB = (_partHitB && _cfg.UsePartialB && !sameBarPartial)
            ? _cfg.ContractsB - (int)Math.Floor(_cfg.ContractsB * 0.5)
            : _cfg.ContractsB;
        var sig = new ExitSignal(SetupId.B, reason, exitPx, remCtsB, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.B,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeB = false; _stB = 0; _partHitB = false; _pnlB = 0;
    }

    // ── C/D/F Priority Helpers ─────────────────────────────────
    private bool AnyCDFActive() => _activeC || _activeD || _activeF;
    private bool AnyCDFArmed() => _stC != 0 || _stD != 0 || _stF != 0;

    // ── Setup C — Sweep Reversal ─────────────────────────────────
    private async Task ProcessSetupC(Bar bar, DateTime localTime)
    {
        // ── Exit (bar mode) ──────────────────────────────────────
        if (_activeC && !_tickModeEnabled)
        {
            bool isLong   = _stC == 2;
            bool prevPart = _partHitC;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entC, _stopC, _tgtC, _partialC,
                _cfg.ContractsC, _pnlC, _partHitC,
                _cfg.UsePartialC, _cfg.UseBeC, _cfg.PointValue,
                bar.High, bar.Low);

            _pnlC     = result.NewPnl;
            _activeC  = result.StillActive;
            _partHitC = result.PartialHit;
            _stopC    = result.NewStop;

            bool partJustHit = _partHitC && !prevPart;
            bool closingNow  = result.HitTarget || result.HitStop;

            if (partJustHit && !closingNow)
            {
                int half = (int)Math.Floor(_cfg.ContractsC * 0.5);
                var psig = new PartialSignal(SetupId.C, isLong ? Direction.Long : Direction.Short,
                    _partialC, half, _cfg.ContractsC - half, _entC, bar.Time);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.C, $"Partial @ {_partialC:F2}", "yellow");
                if (_cfg.UseBeC)
                {
                    var besig = new BESignal(SetupId.C, isLong ? Direction.Long : Direction.Short,
                        _entC, _entC, psig.ContractsRemaining, bar.Time);
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.C, $"Stop → BE {_entC:F2}", "yellow");
                }
            }

            if (closingNow)
            {
                _stC = 0; _tradeCountC++;
                if (result.HitTarget) { _stickyTgtC = true; _exitBarIdxC = _barIndex; }
                else                  { _stickyStpC = true; _exitBarIdxC = _barIndex; }
                await BookExitC(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    result.HitTarget ? _tgtC : _stopC, bar.Time, isLong,
                    sameBarPartial: partJustHit);
            }
            return; // exit processing done — don't re-arm while active
        }

        // ── Entry logic ──────────────────────────────────────────
        if (_activeC || _pastCutoffC || _tradeCountC >= _cfg.MaxTradesC) return;

        // DETECT: sweep occurs (idle, no C/D/F armed or active)
        if (_stC == 0 && !_awaitingConfirmC)
        {
            if (AnyCDFActive() || AnyCDFArmed()) return;

            // Bull sweep: price swept below a level then closed above VWAP
            if (_sweepDetector.AnyBullSweep && bar.Close > _vwapModel.Vwap)
            {
                _sweepExtremeC   = bar.Low;
                _sweepBarHighC   = bar.High;
                _sweepBarLowC    = bar.Low;
                _awaitingConfirmC = true;
                _log.LogDebug("[Setup C] Bull sweep detected, awaiting confirm. SweepLow={Low:F2} BarHigh={High:F2}",
                    bar.Low, bar.High);
            }
            // Bear sweep: price swept above a level then closed below VWAP
            else if (_sweepDetector.AnyBearSweep && bar.Close < _vwapModel.Vwap)
            {
                _sweepExtremeC    = bar.High;
                _sweepBarHighC    = bar.High;
                _sweepBarLowC     = bar.Low;
                _awaitingConfirmC = true;
                _log.LogDebug("[Setup C] Bear sweep detected, awaiting confirm. SweepHigh={High:F2} BarLow={Low:F2}",
                    bar.High, bar.Low);
            }
            return;
        }

        // CONFIRM: waiting for confirmation bar
        if (_awaitingConfirmC && _stC == 0)
        {
            bool isBullSweep = _sweepExtremeC == _sweepBarLowC; // sweep low = bull
            if (isBullSweep)
            {
                if (bar.High > _sweepBarHighC)
                {
                    _stC = 1; // armed long
                    _confirmBarHighC  = bar.High;
                    _awaitingConfirmC = false;
                    AddAlert("ARMED", SetupId.C, "C LONG armed (sweep confirm)", "gray");
                }
                else if (bar.Low < _sweepExtremeC)
                {
                    _awaitingConfirmC = false; // cancel — went against
                }
            }
            else // bear sweep
            {
                if (bar.Low < _sweepBarLowC)
                {
                    _stC = -1; // armed short
                    _confirmBarLowC   = bar.Low;
                    _awaitingConfirmC = false;
                    AddAlert("ARMED", SetupId.C, "C SHORT armed (sweep confirm)", "gray");
                }
                else if (bar.High > _sweepExtremeC)
                {
                    _awaitingConfirmC = false; // cancel — went against
                }
            }
            return;
        }

        // ENTRY: armed
        if (!_tickModeEnabled && (_stC == 1 || _stC == -1))
        {
            bool isLong = _stC == 1;
            if (isLong && bar.High > _confirmBarHighC)
            {
                decimal ep   = LevelCalculator.RoundToTick(_confirmBarHighC, _cfg.TickSize);
                decimal stop = LevelCalculator.RoundToTick(_sweepExtremeC - 2 * _cfg.TickSize, _cfg.TickSize);
                decimal risk = ep - stop;
                if (risk <= 0) { _stC = 0; return; }

                // Target: VWAP primary, fall back to ORB mid if VWAP too close, then PDH
                decimal target = _vwapModel.Vwap;
                if (target <= ep) target = _orb.OrbMid;
                if (target <= ep && _sessionEngine.PDH > ep) target = _sessionEngine.PDH;
                if (target <= ep) { _stC = 0; return; }
                target = LevelCalculator.RoundToTick(target, _cfg.TickSize);

                decimal rr = (target - ep) / risk;
                if (rr < _cfg.MinRrC)
                {
                    // Try PDH
                    if (_sessionEngine.PDH > ep)
                    {
                        target = LevelCalculator.RoundToTick(_sessionEngine.PDH, _cfg.TickSize);
                        rr = (target - ep) / risk;
                    }
                    if (rr < _cfg.MinRrC) { _stC = 0; return; }
                }

                decimal partialDist = (target - ep) * (_cfg.PartialPctC / 100m);
                decimal partial = LevelCalculator.RoundToTick(ep + partialDist, _cfg.TickSize);

                _entC = ep; _stopC = stop; _tgtC = target; _partialC = partial;
                _initStopC = stop; _pnlC = 0; _activeC = true;
                _stC = 2; _enteredThisBar = true; _tradeCountC++;
                _entryTimeC = bar.Time;

                _log.LogInformation("[Setup C] Entry LONG {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
                    _cfg.ContractsC, ep, stop, target, rr);
                var sig = new EntrySignal(SetupId.C, Direction.Long, ep, stop, target, partial, _cfg.ContractsC, bar.Time);
                await _executor.OnEntrySignalAsync(sig);
                await _sink.OnEntryAsync(sig);
                AddAlert("ENTRY", SetupId.C, $"LONG {_cfg.ContractsC}ct @ {ep:F2} | Stop {stop:F2} | Tgt {target:F2}", "green");
            }
            else if (!isLong && bar.Low < _confirmBarLowC)
            {
                decimal ep   = LevelCalculator.RoundToTick(_confirmBarLowC, _cfg.TickSize);
                decimal stop = LevelCalculator.RoundToTick(_sweepExtremeC + 2 * _cfg.TickSize, _cfg.TickSize);
                decimal risk = stop - ep;
                if (risk <= 0) { _stC = 0; return; }

                // Target: VWAP primary, fall back to ORB mid if VWAP too close, then PDL
                decimal target = _vwapModel.Vwap;
                if (target >= ep) target = _orb.OrbMid;
                if (target >= ep && _sessionEngine.PDL > 0 && _sessionEngine.PDL < ep) target = _sessionEngine.PDL;
                if (target >= ep) { _stC = 0; return; }
                target = LevelCalculator.RoundToTick(target, _cfg.TickSize);

                decimal rr = (ep - target) / risk;
                if (rr < _cfg.MinRrC)
                {
                    if (_sessionEngine.PDL > 0 && _sessionEngine.PDL < ep)
                    {
                        target = LevelCalculator.RoundToTick(_sessionEngine.PDL, _cfg.TickSize);
                        rr = (ep - target) / risk;
                    }
                    if (rr < _cfg.MinRrC) { _stC = 0; return; }
                }

                decimal partialDist = (ep - target) * (_cfg.PartialPctC / 100m);
                decimal partial = LevelCalculator.RoundToTick(ep - partialDist, _cfg.TickSize);

                _entC = ep; _stopC = stop; _tgtC = target; _partialC = partial;
                _initStopC = stop; _pnlC = 0; _activeC = true;
                _stC = -2; _enteredThisBar = true; _tradeCountC++;
                _entryTimeC = bar.Time;

                _log.LogInformation("[Setup C] Entry SHORT {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
                    _cfg.ContractsC, ep, stop, target, rr);
                var sig = new EntrySignal(SetupId.C, Direction.Short, ep, stop, target, partial, _cfg.ContractsC, bar.Time);
                await _executor.OnEntrySignalAsync(sig);
                await _sink.OnEntryAsync(sig);
                AddAlert("ENTRY", SetupId.C, $"SHORT {_cfg.ContractsC}ct @ {ep:F2} | Stop {stop:F2} | Tgt {target:F2}", "red");
            }
        }
    }

    // ── Setup D — Opening Drive Pullback ─────────────────────────
    private async Task ProcessSetupD(Bar bar, DateTime localTime)
    {
        // ── Exit (bar mode) ──────────────────────────────────────
        if (_activeD && !_tickModeEnabled)
        {
            bool isLong   = _stD == 2;
            bool prevPart = _partHitD;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entD, _stopD, _tgtD, _partialD,
                _cfg.ContractsD, _pnlD, _partHitD,
                _cfg.UsePartialD, _cfg.UseBeD, _cfg.PointValue,
                bar.High, bar.Low);

            _pnlD     = result.NewPnl;
            _activeD  = result.StillActive;
            _partHitD = result.PartialHit;
            _stopD    = result.NewStop;

            bool partJustHit = _partHitD && !prevPart;
            bool closingNow  = result.HitTarget || result.HitStop;

            if (partJustHit && !closingNow)
            {
                int half = (int)Math.Floor(_cfg.ContractsD * 0.5);
                var psig = new PartialSignal(SetupId.D, isLong ? Direction.Long : Direction.Short,
                    _partialD, half, _cfg.ContractsD - half, _entD, bar.Time);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.D, $"Partial @ {_partialD:F2}", "yellow");
                if (_cfg.UseBeD)
                {
                    var besig = new BESignal(SetupId.D, isLong ? Direction.Long : Direction.Short,
                        _entD, _entD, psig.ContractsRemaining, bar.Time);
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.D, $"Stop → BE {_entD:F2}", "yellow");
                }
            }

            if (closingNow)
            {
                _stD = 0; _tradeCountD++;
                if (result.HitTarget) { _stickyTgtD = true; _exitBarIdxD = _barIndex; }
                else                  { _stickyStpD = true; _exitBarIdxD = _barIndex; }
                await BookExitD(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    result.HitTarget ? _tgtD : _stopD, bar.Time, isLong,
                    sameBarPartial: partJustHit);
            }
            return;
        }

        // ── Arm / Entry logic ────────────────────────────────────
        if (_activeD || _pastCutoffD || _tradeCountD >= _cfg.MaxTradesD) return;
        if (AnyCDFActive() || AnyCDFArmed()) return;

        // ARM: check for opening drive + pullback to VWAP
        if (_stD == 0)
        {
            // Bull side
            if (_openingDrive.OpeningDriveBull && _trendDay.TrendDayBull)
            {
                // Pullback: bar touches VWAP area
                if (bar.Low <= _vwapModel.Vwap * 1.005m)
                {
                    _stD = 1;
                    _armBarHighD = bar.High;
                    _pullbackSwingLowD = bar.Low;
                    _log.LogDebug("[Setup D] Armed LONG: DriveHigh + PB to VWAP. ArmHigh={H:F2} SwingLow={L:F2}",
                        bar.High, bar.Low);
                    AddAlert("ARMED", SetupId.D, "D LONG armed (drive pullback)", "gray");
                }
            }
            // Bear side
            else if (_openingDrive.OpeningDriveBear && _trendDay.TrendDayBear)
            {
                if (bar.High >= _vwapModel.Vwap * 0.995m)
                {
                    _stD = -1;
                    _armBarLowD = bar.Low;
                    _pullbackSwingHighD = bar.High;
                    _log.LogDebug("[Setup D] Armed SHORT: DriveLow + PB to VWAP. ArmLow={L:F2} SwingHigh={H:F2}",
                        bar.Low, bar.High);
                    AddAlert("ARMED", SetupId.D, "D SHORT armed (drive pullback)", "gray");
                }
            }
        }

        // DE-ARM: pullback too deep
        if (_stD == 1 && bar.Low < _vwapModel.Vwap - _orb.OrbRange * 0.5m)
            _stD = 0;
        if (_stD == -1 && bar.High > _vwapModel.Vwap + _orb.OrbRange * 0.5m)
            _stD = 0;

        // Update swing extremes while armed
        if (_stD == 1) _pullbackSwingLowD = Math.Min(_pullbackSwingLowD, bar.Low);
        if (_stD == -1) _pullbackSwingHighD = Math.Max(_pullbackSwingHighD, bar.High);

        // ENTRY (bar mode)
        if (!_tickModeEnabled && (_stD == 1 || _stD == -1))
        {
            if (_stD == 1 && bar.High > _armBarHighD)
            {
                decimal ep   = LevelCalculator.RoundToTick(_armBarHighD, _cfg.TickSize);
                decimal stop = LevelCalculator.RoundToTick(
                    Math.Min(_pullbackSwingLowD, _vwapModel.Vwap - 2 * _cfg.TickSize), _cfg.TickSize);
                decimal risk = ep - stop;
                if (risk <= 0) { _stD = 0; return; }

                decimal target = _orb.OrbHigh + _orb.OrbRange;
                if (_sessionEngine.PDH > 0 && _sessionEngine.PDH > target)
                    target = Math.Max(target, _sessionEngine.PDH);
                target = LevelCalculator.RoundToTick(target, _cfg.TickSize);

                decimal rr = (target - ep) / risk;
                if (rr < _cfg.MinRrD) { _stD = 0; return; }

                decimal partialDist = (target - ep) * (_cfg.PartialPctD / 100m);
                decimal partial = LevelCalculator.RoundToTick(ep + partialDist, _cfg.TickSize);

                _entD = ep; _stopD = stop; _tgtD = target; _partialD = partial;
                _initStopD = stop; _pnlD = 0; _activeD = true;
                _stD = 2; _enteredThisBar = true; _tradeCountD++;
                _entryTimeD = bar.Time;

                _log.LogInformation("[Setup D] Entry LONG {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
                    _cfg.ContractsD, ep, stop, target, rr);
                var sig = new EntrySignal(SetupId.D, Direction.Long, ep, stop, target, partial, _cfg.ContractsD, bar.Time);
                await _executor.OnEntrySignalAsync(sig);
                await _sink.OnEntryAsync(sig);
                AddAlert("ENTRY", SetupId.D, $"LONG {_cfg.ContractsD}ct @ {ep:F2} | Stop {stop:F2} | Tgt {target:F2}", "green");
            }
            else if (_stD == -1 && bar.Low < _armBarLowD)
            {
                decimal ep   = LevelCalculator.RoundToTick(_armBarLowD, _cfg.TickSize);
                decimal stop = LevelCalculator.RoundToTick(
                    Math.Max(_pullbackSwingHighD, _vwapModel.Vwap + 2 * _cfg.TickSize), _cfg.TickSize);
                decimal risk = stop - ep;
                if (risk <= 0) { _stD = 0; return; }

                decimal target = _orb.OrbLow - _orb.OrbRange;
                if (_sessionEngine.PDL > 0 && _sessionEngine.PDL < target)
                    target = Math.Min(target, _sessionEngine.PDL);
                target = LevelCalculator.RoundToTick(target, _cfg.TickSize);

                decimal rr = (ep - target) / risk;
                if (rr < _cfg.MinRrD) { _stD = 0; return; }

                decimal partialDist = (ep - target) * (_cfg.PartialPctD / 100m);
                decimal partial = LevelCalculator.RoundToTick(ep - partialDist, _cfg.TickSize);

                _entD = ep; _stopD = stop; _tgtD = target; _partialD = partial;
                _initStopD = stop; _pnlD = 0; _activeD = true;
                _stD = -2; _enteredThisBar = true; _tradeCountD++;
                _entryTimeD = bar.Time;

                _log.LogInformation("[Setup D] Entry SHORT {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
                    _cfg.ContractsD, ep, stop, target, rr);
                var sig = new EntrySignal(SetupId.D, Direction.Short, ep, stop, target, partial, _cfg.ContractsD, bar.Time);
                await _executor.OnEntrySignalAsync(sig);
                await _sink.OnEntryAsync(sig);
                AddAlert("ENTRY", SetupId.D, $"SHORT {_cfg.ContractsD}ct @ {ep:F2} | Stop {stop:F2} | Tgt {target:F2}", "red");
            }
        }
    }

    // ── Setup F — Midday VWAP Reversion ──────────────────────────
    private async Task ProcessSetupF(Bar bar, DateTime localTime)
    {
        // ── Exit (bar mode) ──────────────────────────────────────
        if (_activeF && !_tickModeEnabled)
        {
            bool isLong   = _stF == 2;
            bool prevPart = _partHitF;
            var result = ExitProcessor.ProcessBar(
                true, isLong, _entF, _stopF, _tgtF, _partialF,
                _cfg.ContractsF, _pnlF, _partHitF,
                _cfg.UsePartialF, _cfg.UseBeF, _cfg.PointValue,
                bar.High, bar.Low);

            _pnlF     = result.NewPnl;
            _activeF  = result.StillActive;
            _partHitF = result.PartialHit;
            _stopF    = result.NewStop;

            bool partJustHit = _partHitF && !prevPart;
            bool closingNow  = result.HitTarget || result.HitStop;

            if (partJustHit && !closingNow)
            {
                int half = (int)Math.Floor(_cfg.ContractsF * 0.5);
                var psig = new PartialSignal(SetupId.F, isLong ? Direction.Long : Direction.Short,
                    _partialF, half, _cfg.ContractsF - half, _entF, bar.Time);
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", SetupId.F, $"Partial @ {_partialF:F2}", "yellow");
                if (_cfg.UseBeF)
                {
                    var besig = new BESignal(SetupId.F, isLong ? Direction.Long : Direction.Short,
                        _entF, _entF, psig.ContractsRemaining, bar.Time);
                    await _executor.OnBESignalAsync(besig);
                    await _sink.OnBEMoveAsync(besig);
                    AddAlert("MOVE_BE", SetupId.F, $"Stop → BE {_entF:F2}", "yellow");
                }
            }

            if (closingNow)
            {
                _stF = 0; _tradeCountF++;
                if (result.HitTarget) { _stickyTgtF = true; _exitBarIdxF = _barIndex; }
                else                  { _stickyStpF = true; _exitBarIdxF = _barIndex; }
                await BookExitF(result.HitTarget ? ExitReason.Target : ExitReason.Stop,
                    result.HitTarget ? _tgtF : _stopF, bar.Time, isLong,
                    sameBarPartial: partJustHit);
            }
            return;
        }

        // ── Arm / Entry logic ────────────────────────────────────
        if (_activeF || _pastCutoffF || _tradeCountF >= _cfg.MaxTradesF) return;
        if (AnyCDFActive() || AnyCDFArmed()) return;

        // Must be midday session
        if (_sessionEngine.CurrentSession != SessionType.Midday) return;

        // NOT a trend day
        if (_trendDay.TrendDayBull || _trendDay.TrendDayBear) return;

        // ARM
        if (_stF == 0)
        {
            // Bear setup: extended above 2σ with bearish rejection candle
            if (bar.Close >= _vwapModel.Upper2 && bar.Close < bar.Open)
            {
                _stF = -1;
                _rejectionBarLowF  = bar.Low;
                _rejectionBarHighF = bar.High;
                _sessionExtremeF   = _sessionEngine.SessionHigh;
                _log.LogDebug("[Setup F] Armed SHORT: VwapUpper2 rejection. RejLow={L:F2} SessHigh={H:F2}",
                    bar.Low, _sessionExtremeF);
                AddAlert("ARMED", SetupId.F, "F SHORT armed (VWAP reversion)", "gray");
            }
            // Bull setup: extended below 2σ with bullish rejection candle
            else if (bar.Close <= _vwapModel.Lower2 && bar.Close > bar.Open)
            {
                _stF = 1;
                _rejectionBarHighF = bar.High;
                _rejectionBarLowF  = bar.Low;
                decimal sessLow = _sessionEngine.SessionLow < decimal.MaxValue ? _sessionEngine.SessionLow : bar.Low;
                _sessionExtremeF   = sessLow;
                _log.LogDebug("[Setup F] Armed LONG: VwapLower2 rejection. RejHigh={H:F2} SessLow={L:F2}",
                    bar.High, _sessionExtremeF);
                AddAlert("ARMED", SetupId.F, "F LONG armed (VWAP reversion)", "gray");
            }
        }

        // ENTRY (bar mode)
        if (!_tickModeEnabled && (_stF == 1 || _stF == -1))
        {
            if (_stF == -1 && bar.Low < _rejectionBarLowF)
            {
                decimal ep   = LevelCalculator.RoundToTick(_rejectionBarLowF, _cfg.TickSize);
                decimal stop = LevelCalculator.RoundToTick(_sessionExtremeF + 2 * _cfg.TickSize, _cfg.TickSize);
                decimal risk = stop - ep;
                if (risk <= 0) { _stF = 0; return; }

                decimal target = LevelCalculator.RoundToTick(_vwapModel.Vwap, _cfg.TickSize);
                if (target >= ep) { _stF = 0; return; } // target must be below entry for short

                decimal rr = (ep - target) / risk;
                if (rr < _cfg.MinRrF) { _stF = 0; return; }

                decimal partialDist = (ep - target) * (_cfg.PartialPctF / 100m);
                decimal partial = LevelCalculator.RoundToTick(ep - partialDist, _cfg.TickSize);

                _entF = ep; _stopF = stop; _tgtF = target; _partialF = partial;
                _initStopF = stop; _pnlF = 0; _activeF = true;
                _stF = -2; _enteredThisBar = true; _tradeCountF++;
                _entryTimeF = bar.Time;

                _log.LogInformation("[Setup F] Entry SHORT {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
                    _cfg.ContractsF, ep, stop, target, rr);
                var sig = new EntrySignal(SetupId.F, Direction.Short, ep, stop, target, partial, _cfg.ContractsF, bar.Time);
                await _executor.OnEntrySignalAsync(sig);
                await _sink.OnEntryAsync(sig);
                AddAlert("ENTRY", SetupId.F, $"SHORT {_cfg.ContractsF}ct @ {ep:F2} | Stop {stop:F2} | Tgt {target:F2}", "red");
            }
            else if (_stF == 1 && bar.High > _rejectionBarHighF)
            {
                decimal ep   = LevelCalculator.RoundToTick(_rejectionBarHighF, _cfg.TickSize);
                decimal stop = LevelCalculator.RoundToTick(_sessionExtremeF - 2 * _cfg.TickSize, _cfg.TickSize);
                decimal risk = ep - stop;
                if (risk <= 0) { _stF = 0; return; }

                decimal target = LevelCalculator.RoundToTick(_vwapModel.Vwap, _cfg.TickSize);
                if (target <= ep) { _stF = 0; return; } // target must be above entry for long

                decimal rr = (target - ep) / risk;
                if (rr < _cfg.MinRrF) { _stF = 0; return; }

                decimal partialDist = (target - ep) * (_cfg.PartialPctF / 100m);
                decimal partial = LevelCalculator.RoundToTick(ep + partialDist, _cfg.TickSize);

                _entF = ep; _stopF = stop; _tgtF = target; _partialF = partial;
                _initStopF = stop; _pnlF = 0; _activeF = true;
                _stF = 2; _enteredThisBar = true; _tradeCountF++;
                _entryTimeF = bar.Time;

                _log.LogInformation("[Setup F] Entry LONG {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
                    _cfg.ContractsF, ep, stop, target, rr);
                var sig = new EntrySignal(SetupId.F, Direction.Long, ep, stop, target, partial, _cfg.ContractsF, bar.Time);
                await _executor.OnEntrySignalAsync(sig);
                await _sink.OnEntryAsync(sig);
                AddAlert("ENTRY", SetupId.F, $"LONG {_cfg.ContractsF}ct @ {ep:F2} | Stop {stop:F2} | Tgt {target:F2}", "green");
            }
        }
    }

    // ── BookExit C/D/F ───────────────────────────────────────────
    private async Task BookExitC(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _cfg.ContractsC * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlC - comm;
        decimal risk = Math.Abs(_entC - _initStopC) * _cfg.PointValue * _cfg.ContractsC;
        decimal rMult = risk > 0 ? _pnlC / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.C, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _cfg.ContractsC,
            Entry = _entC, InitialStop = _initStopC, Target = _tgtC, Partial = _partialC,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitC, PartialPrice = _partialC,
            GrossPnl = _pnlC, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeC, ExitedAt = time
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsC++;   _todayWinPnlC  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesC++; _todayLossPnlC += net; }

        _log.LogInformation("[Setup C] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        int remCts = (_partHitC && _cfg.UsePartialC && !sameBarPartial)
            ? _cfg.ContractsC - (int)Math.Floor(_cfg.ContractsC * 0.5)
            : _cfg.ContractsC;
        var sig = new ExitSignal(SetupId.C, reason, exitPx, remCts, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.C,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeC = false; _stC = 0; _partHitC = false; _pnlC = 0;
    }

    private async Task BookExitD(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _cfg.ContractsD * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlD - comm;
        decimal risk = Math.Abs(_entD - _initStopD) * _cfg.PointValue * _cfg.ContractsD;
        decimal rMult = risk > 0 ? _pnlD / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.D, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _cfg.ContractsD,
            Entry = _entD, InitialStop = _initStopD, Target = _tgtD, Partial = _partialD,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitD, PartialPrice = _partialD,
            GrossPnl = _pnlD, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeD, ExitedAt = time
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsD++;   _todayWinPnlD  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesD++; _todayLossPnlD += net; }

        _log.LogInformation("[Setup D] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        int remCts = (_partHitD && _cfg.UsePartialD && !sameBarPartial)
            ? _cfg.ContractsD - (int)Math.Floor(_cfg.ContractsD * 0.5)
            : _cfg.ContractsD;
        var sig = new ExitSignal(SetupId.D, reason, exitPx, remCts, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.D,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeD = false; _stD = 0; _partHitD = false; _pnlD = 0;
    }

    private async Task BookExitF(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _cfg.ContractsF * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlF - comm;
        decimal risk = Math.Abs(_entF - _initStopF) * _cfg.PointValue * _cfg.ContractsF;
        decimal rMult = risk > 0 ? _pnlF / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.F, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _cfg.ContractsF,
            Entry = _entF, InitialStop = _initStopF, Target = _tgtF, Partial = _partialF,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitF, PartialPrice = _partialF,
            GrossPnl = _pnlF, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeF, ExitedAt = time
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        if (net > 0) { _todayWins++;   _todayWinPnl  += net; _todayWinsF++;   _todayWinPnlF  += net; }
        else         { _todayLosses++; _todayLossPnl += net; _todayLossesF++; _todayLossPnlF += net; }

        _log.LogInformation("[Setup F] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        int remCts = (_partHitF && _cfg.UsePartialF && !sameBarPartial)
            ? _cfg.ContractsF - (int)Math.Floor(_cfg.ContractsF * 0.5)
            : _cfg.ContractsF;
        var sig = new ExitSignal(SetupId.F, reason, exitPx, remCts, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.F,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeF = false; _stF = 0; _partHitF = false; _pnlF = 0;
    }

    // ── Tick-mode exit evaluation for C/D/F ─────────────────────
    private async Task EvalTickExitCDF(SetupId setup, decimal price, DateTime utcTime)
    {
        // Get the right fields based on setup
        bool active; bool isLong; decimal entry, stop, target, partial;
        int contracts; decimal pnl; bool partHit; bool usePartial, useBE;

        switch (setup)
        {
            case SetupId.C:
                active = _activeC; isLong = _stC == 2;
                entry = _entC; stop = _stopC; target = _tgtC; partial = _partialC;
                contracts = _cfg.ContractsC; pnl = _pnlC; partHit = _partHitC;
                usePartial = _cfg.UsePartialC; useBE = _cfg.UseBeC;
                break;
            case SetupId.D:
                active = _activeD; isLong = _stD == 2;
                entry = _entD; stop = _stopD; target = _tgtD; partial = _partialD;
                contracts = _cfg.ContractsD; pnl = _pnlD; partHit = _partHitD;
                usePartial = _cfg.UsePartialD; useBE = _cfg.UseBeD;
                break;
            case SetupId.F:
                active = _activeF; isLong = _stF == 2;
                entry = _entF; stop = _stopF; target = _tgtF; partial = _partialF;
                contracts = _cfg.ContractsF; pnl = _pnlF; partHit = _partHitF;
                usePartial = _cfg.UsePartialF; useBE = _cfg.UseBeF;
                break;
            default: return;
        }

        if (!active) return;

        bool hitStop      = isLong ? price <= stop    : price >= stop;
        bool hitTarget    = isLong ? price >= target  : price <= target;
        bool hitPartialPx = usePartial && !partHit &&
                            (isLong ? price >= partial : price <= partial);
        if (!hitStop && !hitTarget && !hitPartialPx) return;

        // Partial fill check
        bool partJustHit = false;
        if (hitPartialPx && !hitTarget)
        {
            partJustHit = true;
            int half = (int)Math.Floor(contracts * 0.5);
            var psig = new PartialSignal(setup, isLong ? Direction.Long : Direction.Short,
                partial, half, contracts - half, entry, utcTime);
            await _executor.OnPartialSignalAsync(psig);
            await _sink.OnPartialAsync(psig);
            AddAlert("PARTIAL", setup, $"Partial @ {partial:F2}", "yellow");

            // Update partial hit and potentially move to BE
            switch (setup)
            {
                case SetupId.C: _partHitC = true; if (useBE) { _stopC = entry; } break;
                case SetupId.D: _partHitD = true; if (useBE) { _stopD = entry; } break;
                case SetupId.F: _partHitF = true; if (useBE) { _stopF = entry; } break;
            }

            if (useBE)
            {
                var besig = new BESignal(setup, isLong ? Direction.Long : Direction.Short,
                    entry, entry, psig.ContractsRemaining, utcTime);
                await _executor.OnBESignalAsync(besig);
                await _sink.OnBEMoveAsync(besig);
                AddAlert("MOVE_BE", setup, $"Stop → BE {entry:F2}", "yellow");
                // Re-read stop after BE move for stop check below
                stop = entry;
            }

            // Re-check stop after BE
            hitStop = isLong ? price <= stop : price >= stop;
            if (!hitTarget && !hitStop) return;
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? target : stop;
        decimal    exitPnl = (isLong ? exitPx - entry : entry - exitPx) * _cfg.PointValue * contracts;

        switch (setup)
        {
            case SetupId.C:
                _pnlC = exitPnl; _activeC = false; _stC = 0; _tradeCountC++;
                if (hitTarget) { _stickyTgtC = true; _exitBarIdxC = _barIndex; }
                else           { _stickyStpC = true; _exitBarIdxC = _barIndex; }
                await BookExitC(reason, exitPx, utcTime, isLong, sameBarPartial: partJustHit);
                break;
            case SetupId.D:
                _pnlD = exitPnl; _activeD = false; _stD = 0; _tradeCountD++;
                if (hitTarget) { _stickyTgtD = true; _exitBarIdxD = _barIndex; }
                else           { _stickyStpD = true; _exitBarIdxD = _barIndex; }
                await BookExitD(reason, exitPx, utcTime, isLong, sameBarPartial: partJustHit);
                break;
            case SetupId.F:
                _pnlF = exitPnl; _activeF = false; _stF = 0; _tradeCountF++;
                if (hitTarget) { _stickyTgtF = true; _exitBarIdxF = _barIndex; }
                else           { _stickyStpF = true; _exitBarIdxF = _barIndex; }
                await BookExitF(reason, exitPx, utcTime, isLong, sameBarPartial: partJustHit);
                break;
        }
    }

    private async Task ForceExitA(Bar bar)
    {
        if (!_activeA) return;
        bool isLong = _stA == 2;
        _pnlA += ExitProcessor.ForcedExit(isLong, _entA, bar.Close,
            _cfg.ContractsA, _partHitA, _cfg.UsePartialA, _cfg.PointValue);
        await BookExitA(ExitReason.SessionEnd, bar.Close, bar.Time, isLong);
    }

    private async Task ForceExitB(Bar bar)
    {
        if (!_activeB) return;
        bool isLong = _stB == 3;
        _pnlB += ExitProcessor.ForcedExit(isLong, _entB, bar.Close,
            _cfg.ContractsB, _partHitB, _cfg.UsePartialB, _cfg.PointValue);
        await BookExitB(ExitReason.SessionEnd, bar.Close, bar.Time, isLong);
    }

    private async Task ForceExitC(Bar bar)
    {
        if (!_activeC) return;
        bool isLong = _stC == 2;
        _pnlC += ExitProcessor.ForcedExit(isLong, _entC, bar.Close,
            _cfg.ContractsC, _partHitC, _cfg.UsePartialC, _cfg.PointValue);
        await BookExitC(ExitReason.SessionEnd, bar.Close, bar.Time, isLong);
    }

    private async Task ForceExitD(Bar bar)
    {
        if (!_activeD) return;
        bool isLong = _stD == 2;
        _pnlD += ExitProcessor.ForcedExit(isLong, _entD, bar.Close,
            _cfg.ContractsD, _partHitD, _cfg.UsePartialD, _cfg.PointValue);
        await BookExitD(ExitReason.SessionEnd, bar.Close, bar.Time, isLong);
    }

    private async Task ForceExitF(Bar bar)
    {
        if (!_activeF) return;
        bool isLong = _stF == 2;
        _pnlF += ExitProcessor.ForcedExit(isLong, _entF, bar.Close,
            _cfg.ContractsF, _partHitF, _cfg.UsePartialF, _cfg.PointValue);
        await BookExitF(ExitReason.SessionEnd, bar.Close, bar.Time, isLong);
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
        if (_cfg.UseTimeFilter && localTime >= cutoffA && localTime < sessStart) _pastCutoffA = true;
        var cutoffB   = new TimeOnly(_cfg.CutoffHourB, _cfg.CutoffMinuteB);
        if (_cfg.UseTimeFilter && localTime >= cutoffB && localTime < sessStart) _pastCutoffB = true;
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
                    decimal nearDist = orbRange * _cfg.NearPct;
                    if (bar.High >= orbHigh - nearDist && orbLongOkA && aboveVwapA)
                        { _stA = 1; _armEntryA = bar.Open; }
                    else if (bar.Low <= orbLow + nearDist && orbShortOkA && belowVwapA)
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
                    if (bar.Close > orbHigh && orbLongOkB && aboveVwapB)
                        { _stB = 1; _armEntryB = bar.Open; }
                    else if (bar.Close < orbLow && orbShortOkB && belowVwapB)
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
            int rem = _partHitA ? _cfg.ContractsA - (int)Math.Floor(_cfg.ContractsA * 0.5) : _cfg.ContractsA;
            viewA = new ActiveTradeView
            {
                Setup = SetupId.A, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entA, CurrentStop = _stopA, Target = _tgtA, Partial = _partialA,
                Contracts = _cfg.ContractsA, RemainingContracts = rem,
                PartialFilled = _partHitA, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entA : _entA - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeA
            };
        }

        ActiveTradeView? viewB = null;
        if (_activeB)
        {
            bool isLong = _stB == 3;
            int rem = _partHitB ? _cfg.ContractsB - (int)Math.Floor(_cfg.ContractsB * 0.5) : _cfg.ContractsB;
            viewB = new ActiveTradeView
            {
                Setup = SetupId.B, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entB, CurrentStop = _stopB, Target = _tgtB, Partial = _partialB,
                Contracts = _cfg.ContractsB, RemainingContracts = rem,
                PartialFilled = _partHitB, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entB : _entB - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeB
            };
        }

        ActiveTradeView? viewC = null;
        if (_activeC)
        {
            bool isLong = _stC == 2;
            int rem = _partHitC ? _cfg.ContractsC - (int)Math.Floor(_cfg.ContractsC * 0.5) : _cfg.ContractsC;
            viewC = new ActiveTradeView
            {
                Setup = SetupId.C, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entC, CurrentStop = _stopC, Target = _tgtC, Partial = _partialC,
                Contracts = _cfg.ContractsC, RemainingContracts = rem,
                PartialFilled = _partHitC, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entC : _entC - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeC
            };
        }

        ActiveTradeView? viewD = null;
        if (_activeD)
        {
            bool isLong = _stD == 2;
            int rem = _partHitD ? _cfg.ContractsD - (int)Math.Floor(_cfg.ContractsD * 0.5) : _cfg.ContractsD;
            viewD = new ActiveTradeView
            {
                Setup = SetupId.D, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entD, CurrentStop = _stopD, Target = _tgtD, Partial = _partialD,
                Contracts = _cfg.ContractsD, RemainingContracts = rem,
                PartialFilled = _partHitD, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entD : _entD - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeD
            };
        }

        ActiveTradeView? viewF = null;
        if (_activeF)
        {
            bool isLong = _stF == 2;
            int rem = _partHitF ? _cfg.ContractsF - (int)Math.Floor(_cfg.ContractsF * 0.5) : _cfg.ContractsF;
            viewF = new ActiveTradeView
            {
                Setup = SetupId.F, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entF, CurrentStop = _stopF, Target = _tgtF, Partial = _partialF,
                Contracts = _cfg.ContractsF, RemainingContracts = rem,
                PartialFilled = _partHitF, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entF : _entF - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeF
            };
        }

        await _sink.OnSnapshotAsync(new EngineSnapshot
        {
            Time   = bar.Time, Ticker = _cfg.Ticker, IsLive = true,
            LastPrice  = lastPx,
            LastUpdate = DateTime.UtcNow,
            SetupA = viewA, SetupB = viewB,
            SetupC = viewC, SetupD = viewD, SetupF = viewF,

            TodayPnl    = _todayPnl,
            TodayTrades = _todayWins + _todayLosses,
            TodayWins   = _todayWins,
            TodayLosses = _todayLosses,
            TodayMaxDD  = _todayMaxDD,

            Expectancy  = CalcExpectancy(_todayWins, _todayLosses, _todayWinPnl, _todayLossPnl),
            ExpectancyA = CalcExpectancy(_todayWinsA, _todayLossesA, _todayWinPnlA, _todayLossPnlA),
            ExpectancyB = CalcExpectancy(_todayWinsB, _todayLossesB, _todayWinPnlB, _todayLossPnlB),
            ExpectancyC = CalcExpectancy(_todayWinsC, _todayLossesC, _todayWinPnlC, _todayLossPnlC),
            ExpectancyD = CalcExpectancy(_todayWinsD, _todayLossesD, _todayWinPnlD, _todayLossPnlD),
            ExpectancyF = CalcExpectancy(_todayWinsF, _todayLossesF, _todayWinPnlF, _todayLossPnlF),

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
            PastCutoffF  = _pastCutoffF,
            SessionEnded = _rthEnded,

            SetupAEnabled = _cfg.EnableA,
            SetupBEnabled = _cfg.EnableB,
            SetupCEnabled = _cfg.EnableC,
            SetupDEnabled = _cfg.EnableD,
            SetupFEnabled = _cfg.EnableF,
            SetupAState = _stA,
            SetupBState = _stB,
            SetupCState = _stC,
            SetupDState = _stD,
            SetupFState = _stF,

            StickyTgtA = _stickyTgtA,
            StickyStpA = _stickyStpA,
            StickyTgtB = _stickyTgtB,
            StickyStpB = _stickyStpB,
            StickyTgtC = _stickyTgtC,
            StickyStpC = _stickyStpC,
            StickyTgtD = _stickyTgtD,
            StickyStpD = _stickyStpD,
            StickyTgtF = _stickyTgtF,
            StickyStpF = _stickyStpF,

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
            SetupCBull      = _compositeSetups.SetupCBull,
            SetupCBear      = _compositeSetups.SetupCBear,
            SetupDBull      = _compositeSetups.SetupDBull,
            SetupDBear      = _compositeSetups.SetupDBear,
            SetupFBull      = _compositeSetups.SetupFBull,
            SetupFBear      = _compositeSetups.SetupFBear,

            RecentAlerts = GetRecentAlerts(20)
        });
    }

    private void ResetSetupA()
    {
        _stA = 0; _entA = 0; _stopA = 0; _tgtA = 0; _partialA = 0;
        _activeA = false; _tradeCountA = 0; _partHitA = false;
        _pnlA = 0; _armEntryA = 0; _entryTimeA = DateTime.MinValue;
        _stickyTgtA = false; _stickyStpA = false; _exitBarIdxA = -1;
    }

    private void ResetSetupB()
    {
        _stB = 0; _entB = 0; _stopB = 0; _tgtB = 0; _partialB = 0;
        _activeB = false; _tradeCountB = 0; _partHitB = false;
        _pnlB = 0; _armEntryB = 0; _entryTimeB = DateTime.MinValue;
        _stickyTgtB = false; _stickyStpB = false; _exitBarIdxB = -1;
    }

    private void ResetSetupC()
    {
        _stC = 0; _entC = 0; _stopC = 0; _tgtC = 0; _partialC = 0;
        _activeC = false; _tradeCountC = 0; _partHitC = false;
        _pnlC = 0; _entryTimeC = DateTime.MinValue;
        _stickyTgtC = false; _stickyStpC = false; _exitBarIdxC = -1;
        _awaitingConfirmC = false;
        _sweepExtremeC = 0; _sweepBarHighC = 0; _sweepBarLowC = 0;
        _confirmBarHighC = 0; _confirmBarLowC = 0;
    }

    private void ResetSetupD()
    {
        _stD = 0; _entD = 0; _stopD = 0; _tgtD = 0; _partialD = 0;
        _activeD = false; _tradeCountD = 0; _partHitD = false;
        _pnlD = 0; _entryTimeD = DateTime.MinValue;
        _stickyTgtD = false; _stickyStpD = false; _exitBarIdxD = -1;
        _pullbackSwingLowD = 0; _pullbackSwingHighD = 0;
        _armBarHighD = 0; _armBarLowD = 0;
    }

    private void ResetSetupF()
    {
        _stF = 0; _entF = 0; _stopF = 0; _tgtF = 0; _partialF = 0;
        _activeF = false; _tradeCountF = 0; _partHitF = false;
        _pnlF = 0; _entryTimeF = DateTime.MinValue;
        _stickyTgtF = false; _stickyStpF = false; _exitBarIdxF = -1;
        _rejectionBarHighF = 0; _rejectionBarLowF = 0; _sessionExtremeF = 0;
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
