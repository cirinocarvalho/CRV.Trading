using CRV.Core.Indicators;
using CRV.Core.Interfaces;
using CRV.Core.Models;
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

    // ── Session state ─────────────────────────────────────────
    private DateTime _lastDate       = DateTime.MinValue;
    private bool     _pastCutoff     = false;
    private bool     _rthEnded       = false;
    private bool     _orbLoggedFormed = false;
    private decimal  _todayPnl       = 0;
    private int      _todayWins      = 0;
    private int      _todayLosses    = 0;
    private decimal  _todayPeak      = 0;
    private decimal  _todayMaxDD     = 0;
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

    private readonly List<AlertEvent> _alertFeed = new();

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
    }

    /// <summary>Signal the engine to force-exit the Setup A position on the next bar.</summary>
    public void RequestForceExitA() => _forceExitA = true;

    /// <summary>Signal the engine to force-exit the Setup B position on the next bar.</summary>
    public void RequestForceExitB() => _forceExitB = true;

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

        var local     = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _tz);
        var localTime = TimeOnly.FromDateTime(local);
        bool canEnter = !_pastCutoff && !_rthEnded;

        if (_cfg.EnableA) await EvalTickSetupA(price, utcTime, canEnter);
        if (_cfg.EnableB) await EvalTickSetupB(price, utcTime, canEnter);
    }

    private async Task EvalTickSetupA(decimal price, DateTime utcTime, bool canEnter)
    {
        decimal orbRange = _orb.OrbRange;
        decimal tickTol  = _cfg.TickSize * 2;

        // ── Entry: armed but not yet active ─────────────────────
        if (!_activeA && canEnter && (_stA == 1 || _stA == -1))
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
        bool hitStop   = long_ ? price <= _stopA : price >= _stopA;
        bool hitTarget = long_ ? price >= _tgtA  : price <= _tgtA;
        if (!hitStop && !hitTarget) return;

        // Partial fill check (price-based)
        bool partJustHit = false;
        if (_cfg.UsePartialA && !_partHitA)
        {
            bool hitPartial = long_ ? price >= _partialA : price <= _partialA;
            if (hitPartial && !hitTarget) // partial without full close
            {
                partJustHit = true;
                _partHitA   = true;
                int half    = (int)Math.Floor(_cfg.Contracts * 0.5);
                var psig    = new PartialSignal(SetupId.A, long_ ? Direction.Long : Direction.Short,
                    _partialA, half, _cfg.Contracts - half, _entA, utcTime);
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
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _tgtA : _stopA;
        decimal    pnl    = (long_ ? exitPx - _entA : _entA - exitPx) * _cfg.PointValue * _cfg.Contracts;
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
            isLong ? "LONG" : "SHORT", _cfg.Contracts, ep, sl, tp, rr);
        var sig = new EntrySignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _cfg.Contracts, utcTime);
        await _executor.OnEntrySignalAsync(sig);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.A,
            $"{(isLong ? "LONG" : "SHORT")} {_cfg.Contracts}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task EvalTickSetupB(decimal price, DateTime utcTime, bool canEnter)
    {
        decimal orbRange = _orb.OrbRange;
        decimal tickTol  = _cfg.TickSize * 2;

        // ── Entry: armed/retest state ────────────────────────────
        // _stB: ±1=Armed (above/below ORB), ±2=Retest (price returned near orbHigh/orbLow)
        if (!_activeB && canEnter && (_stB == 1 || _stB == -1 || _stB == 2 || _stB == -2))
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
                // Stop = orbMid (from CalcLevelsB).  Entry ≠ Stop → non-zero risk → valid RR.
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
        bool hitStop   = longB ? price <= _stopB : price >= _stopB;
        bool hitTarget = longB ? price >= _tgtB  : price <= _tgtB;
        if (!hitStop && !hitTarget) return;

        // Partial fill check (price-based)
        bool partJustHit = false;
        if (_cfg.UsePartialB && !_partHitB)
        {
            bool hitPartial = longB ? price >= _partialB : price <= _partialB;
            if (hitPartial && !hitTarget) // partial without full close
            {
                partJustHit = true;
                _partHitB   = true;
                int half    = (int)Math.Floor(_cfg.Contracts * 0.5);
                var psig    = new PartialSignal(SetupId.B, longB ? Direction.Long : Direction.Short,
                    _partialB, half, _cfg.Contracts - half, _entB, utcTime);
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
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _tgtB : _stopB;
        decimal    pnl    = (longB ? exitPx - _entB : _entB - exitPx) * _cfg.PointValue * _cfg.Contracts;
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
            _cfg.TargetPctB, _cfg.PartialPctB, _orb.OrbRange, _orb.OrbMid, _cfg.TickSize);
        if (rr < _cfg.MinRrB) return;

        _entB = ep; _stopB = sl; _tgtB = tp; _partialB = pp;
        _initStopB = sl; _pnlB = 0; _activeB = true;
        _stB = isLong ? 3 : -3; _enteredThisBar = true;
        _entryTimeB = utcTime;

        _log.LogInformation("[Setup B TICK] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _cfg.Contracts, ep, sl, tp, rr);
        var sig = new EntrySignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _cfg.Contracts, utcTime);
        await _executor.OnEntrySignalAsync(sig);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.B,
            $"{(isLong ? "LONG" : "SHORT")} {_cfg.Contracts}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
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
            _rthEnded        = false;
            _orbLoggedFormed = false;
            _todayPnl    = 0;
            _todayWins   = 0;
            _todayLosses = 0;
            _todayPeak   = 0;
            _todayMaxDD  = 0;
            _ddBreached  = false;
            ResetSetupA();
            ResetSetupB();
            // Reset VWAP immediately at the session boundary so the dashboard
            // shows 0 right at 6 PM ET — not delayed until the first confirmed bar.
            _vwap.NewSession(tradingDate);
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

        // Warmup mode: indicators are built + arm state is evaluated so the dashboard
        // shows correct Armed/Idle badges when the engine starts mid-session.
        // No entries, exits, broker signals, trade counting, or snapshots.
        if (warmupOnly)
        {
            // Track session flags so stale alerts don't fire on first live bar
            if (_orb.IsSet && _orb.OrbRange > 0) _orbLoggedFormed = true;
            var wCutoff    = new TimeOnly(_cfg.CutoffHour, _cfg.CutoffMinute);
            var wSessStart = new TimeOnly(_cfg.SessionStartHour, 0);
            // Guard against overnight bars (19:00–23:59 ET) falsely triggering the cutoff.
            // Only set these flags for bars within the calendar trading day (before session restart).
            if (_cfg.UseTimeFilter && localTime >= wCutoff && localTime < wSessStart) _pastCutoff = true;
            if (_cfg.CloseAtRthClose && localTime >= _cfg.RthEnd && localTime < wSessStart) _rthEnded = true;
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

        var cutoff      = new TimeOnly(_cfg.CutoffHour, _cfg.CutoffMinute);
        var sessStart   = new TimeOnly(_cfg.SessionStartHour, 0);
        // Guard against overnight bars (19:00–23:59 ET) falsely triggering the cutoff.
        // Only apply when the bar's local time is within the calendar trading day (before session restart).
        if (_cfg.UseTimeFilter && localTime >= cutoff && localTime < sessStart)
        {
            if (!_pastCutoff)
            {
                _log.LogInformation("Cutoff reached at {Time} — no new entries allowed", localTime);
                AddAlert("CUTOFF", SetupId.A, $"Cutoff {cutoff:HH:mm} — no new entries", "yellow");
            }
            _pastCutoff = true;
        }

        bool rthJustEnded = _cfg.CloseAtRthClose && localTime >= _cfg.RthEnd && localTime < sessStart && !_rthEnded;
        if (rthJustEnded)
        {
            _log.LogInformation("Session ended at {Time} — forcing exits", localTime);
            AddAlert("SESSION", SetupId.A, $"Session ended {_cfg.RthEnd:HH:mm} — closing positions", "red");
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
            _log.LogInformation("ORB formed: High={H} Low={L} Range={R} at {Time}",
                _orb.OrbHigh, _orb.OrbLow, _orb.OrbRange, localTime);
            AddAlert("ORB", SetupId.A, $"ORB formed — H:{_orb.OrbHigh:F2} L:{_orb.OrbLow:F2} R:{_orb.OrbRange:F2}", "blue");
        }

        bool atrOk = !_atr.IsReady || (_orb.OrbRange >= _atr.Value * _cfg.AtrFilterPct);
        if (!atrOk)
        {
            _log.LogDebug("ATR filter blocked: OrbRange={Range:F2} ATR={Atr:F2} Threshold={Thr:F2}",
                _orb.OrbRange, _atr.Value, _atr.Value * _cfg.AtrFilterPct);
            await PublishSnapshot(bar); return;
        }

        bool aboveVwap  = !_cfg.UseVwap || (_vwap.IsReady && bar.Close > _vwap.Value);
        bool belowVwap  = !_cfg.UseVwap || (_vwap.IsReady && bar.Close < _vwap.Value);
        bool orbLongOk  = !_cfg.UseOrbClose || _orb.OrbBullClose;
        bool orbShortOk = !_cfg.UseOrbClose || _orb.OrbBearClose;

        _log.LogDebug("Bar {Time} O={O} H={H} L={L} C={C} | VWAP={Vwap:F2} AboveVwap={Av} BelowVwap={Bv} | ORB H={Oh:F2} L={Ol:F2} Range={Or:F2}",
            localTime, bar.Open, bar.High, bar.Low, bar.Close,
            _vwap.IsReady ? _vwap.Value : 0m, aboveVwap, belowVwap,
            _orb.OrbHigh, _orb.OrbLow, _orb.OrbRange);

        _enteredThisBar = false;
        bool canTrade   = !_ddBreached;

        if (_cfg.EnableA && canTrade)
            await ProcessSetupA(bar, aboveVwap, belowVwap, orbLongOk, orbShortOk);

        if (_cfg.EnableB && canTrade)
            await ProcessSetupB(bar, aboveVwap, belowVwap, orbLongOk, orbShortOk);

        if (rthJustEnded || _forceExitA) { _forceExitA = false; await ForceExitA(bar); }
        if (rthJustEnded || _forceExitB) { _forceExitB = false; await ForceExitB(bar); }

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
            if (_pastCutoff && (_stA == 1 || _stA == -1)) { _stA = 0; _armEntryA = 0; }
            else if ((_stA == 1 && bar.Close < orbLow) || (_stA == -1 && bar.Close > orbHigh))
                { _stA = 0; _armEntryA = 0; }
            else
            {
                bool isReady = _tradeCountA < _cfg.MaxTradesA;

                // Arm — only when not past the entry cutoff
                if (isReady && _stA == 0 && !_pastCutoff)
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
                _cfg.Contracts, _pnlA, _partHitA,
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
                int half = (int)Math.Floor(_cfg.Contracts * 0.5);
                var psig = new PartialSignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
                    _partialA, half, _cfg.Contracts - half, _entA, bar.Time);
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
            isLong ? "LONG" : "SHORT", _cfg.Contracts, ep, sl, tp, rr);
        var sig = new EntrySignal(SetupId.A, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _cfg.Contracts, bar.Time);
        await _executor.OnEntrySignalAsync(sig);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.A,
            $"{(isLong ? "LONG" : "SHORT")} {_cfg.Contracts}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task BookExitA(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _cfg.Contracts * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlA - comm;
        decimal risk = Math.Abs(_entA - _initStopA) * _cfg.PointValue * _cfg.Contracts;
        decimal rMult = risk > 0 ? _pnlA / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.A, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _cfg.Contracts,
            Entry = _entA, InitialStop = _initStopA, Target = _tgtA, Partial = _partialA,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitA, PartialPrice = _partialA,   // correctly true for same-bar partial too
            GrossPnl = _pnlA, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeA, ExitedAt = time
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        if (net > 0) _todayWins++; else _todayLosses++;

        _log.LogInformation("[Setup A] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        // Broker contract count:
        //   • Prior-bar partial:     partial order was already placed → only half remains → send half.
        //   • Same-bar partial+close: partial/BE signals were suppressed → full bracket still in place → send all.
        //   • No partial:            send all.
        int remCtsA = (_partHitA && _cfg.UsePartialA && !sameBarPartial)
            ? _cfg.Contracts - (int)Math.Floor(_cfg.Contracts * 0.5)
            : _cfg.Contracts;
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
        decimal orbMid   = _orb.OrbMid;
        decimal orbRange = _orb.OrbRange;

        // ── Entry (runs when not yet active) ──────────────────────
        if (!_activeB)
        {
            if (_pastCutoff && Math.Abs(_stB) <= 2) { _stB = 0; _armEntryB = 0; }
            else if ((_stB > 0 && _stB < 3 && bar.Close < orbLow) ||
                     (_stB < 0 && _stB > -3 && bar.Close > orbHigh))
                { _stB = 0; _armEntryB = 0; }
            else
            {
                bool isReady   = _tradeCountB < _cfg.MaxTradesB;
                bool canEnterB = _cfg.AllowBothSameBar || !_enteredThisBar;

                // Breakout detection — only when not past the entry cutoff
                if (isReady && _stB == 0 && !_pastCutoff)
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
                            await TryEntryB(bar, ep, true, orbRange, orbMid);
                        }
                        else if (_stB == -1 && canEnterB)
                        {
                            decimal ep = _armEntryB > 0 ? _armEntryB : orbLow;
                            await TryEntryB(bar, ep, false, orbRange, orbMid);
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
                            await TryEntryB(bar, orbHigh, true, orbRange, orbMid);
                        else if (_stB == -2 && bar.Close < orbLow && canEnterB)
                            await TryEntryB(bar, orbLow, false, orbRange, orbMid);
                    }

                    if (_stB == 2  && bar.Close < orbMid) _stB = 0;
                    if (_stB == -2 && bar.Close > orbMid) _stB = 0;
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
                _cfg.Contracts, _pnlB, _partHitB,
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
                int half = (int)Math.Floor(_cfg.Contracts * 0.5);
                var psig = new PartialSignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
                    _partialB, half, _cfg.Contracts - half, _entB, bar.Time);
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

    private async Task TryEntryB(Bar bar, decimal ep, bool isLong, decimal orbRange, decimal orbMid)
    {
        // Apply entry tick offset: Long → price + ticks, Short → price - ticks
        if (_cfg.EntryTickOffsetB != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffsetB * _cfg.TickSize;
            ep = isLong ? ep + offset : ep - offset;
            ep = LevelCalculator.RoundToTick(ep, _cfg.TickSize);
        }

        var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
            _cfg.TargetPctB, _cfg.PartialPctB, orbRange, orbMid, _cfg.TickSize);
        if (rr < _cfg.MinRrB) return;

        _entB = ep; _stopB = sl; _tgtB = tp; _partialB = pp;
        _initStopB = sl; _pnlB = 0; _activeB = true;
        _stB = isLong ? 3 : -3; _enteredThisBar = true;
        _entryTimeB = bar.Time;

        _log.LogInformation("[Setup B] Entry {Dir} {Ct}ct @ {Ep:F2} Stop={Sl:F2} Tgt={Tp:F2} RR={Rr:F2}",
            isLong ? "LONG" : "SHORT", _cfg.Contracts, ep, sl, tp, rr);
        var sig = new EntrySignal(SetupId.B, isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _cfg.Contracts, bar.Time);
        await _executor.OnEntrySignalAsync(sig);
        await _sink.OnEntryAsync(sig);
        AddAlert("ENTRY", SetupId.B,
            $"{(isLong ? "LONG" : "SHORT")} {_cfg.Contracts}ct @ {ep:F2} | Stop {sl:F2} | Tgt {tp:F2}",
            isLong ? "green" : "red");
    }

    private async Task BookExitB(ExitReason reason, decimal exitPx, DateTime time, bool isLong,
        bool sameBarPartial = false)
    {
        decimal comm = _cfg.Contracts * 2 * _cfg.CommissionPerSide;
        decimal net  = _pnlB - comm;
        decimal risk = Math.Abs(_entB - _initStopB) * _cfg.PointValue * _cfg.Contracts;
        decimal rMult = risk > 0 ? _pnlB / risk : 0;

        var trade = new TradeRecord
        {
            Setup = SetupId.B, Direction = isLong ? Direction.Long : Direction.Short,
            Ticker = _cfg.Ticker, Contracts = _cfg.Contracts,
            Entry = _entB, InitialStop = _initStopB, Target = _tgtB, Partial = _partialB,
            Exit = exitPx, ExitReason = reason,
            PartialFilled = _partHitB, PartialPrice = _partialB,   // correctly true for same-bar partial too
            GrossPnl = _pnlB, Commission = comm, NetPnl = net, RMultiple = rMult,
            EnteredAt = _entryTimeB, ExitedAt = time
        };

        _todayPnl   += net;
        _todayPeak   = Math.Max(_todayPeak, _todayPnl);
        _todayMaxDD  = Math.Max(_todayMaxDD, _todayPeak - _todayPnl);
        if (net > 0) _todayWins++; else _todayLosses++;

        _log.LogInformation("[Setup B] Exit {Reason} @ {Px:F2} Net={Net:C0} R={R:F2}", reason, exitPx, net, rMult);

        // Broker contract count:
        //   • Prior-bar partial:     partial order was already placed → only half remains → send half.
        //   • Same-bar partial+close: partial/BE signals were suppressed → full bracket still in place → send all.
        //   • No partial:            send all.
        int remCtsB = (_partHitB && _cfg.UsePartialB && !sameBarPartial)
            ? _cfg.Contracts - (int)Math.Floor(_cfg.Contracts * 0.5)
            : _cfg.Contracts;
        var sig = new ExitSignal(SetupId.B, reason, exitPx, remCtsB, time);
        await _executor.OnExitSignalAsync(sig);
        await _sink.OnExitAsync(sig, trade);
        AddAlert("EXIT", SetupId.B,
            $"{reason} @ {exitPx:F2} | {(net >= 0 ? "+" : "")}{net:C0} | {rMult:F1}R",
            reason == ExitReason.Target ? "green" : "red");

        _activeB = false; _stB = 0; _partHitB = false; _pnlB = 0;
    }

    private async Task ForceExitA(Bar bar)
    {
        if (!_activeA) return;
        bool isLong = _stA == 2;
        _pnlA += ExitProcessor.ForcedExit(isLong, _entA, bar.Close,
            _cfg.Contracts, _partHitA, _cfg.UsePartialA, _cfg.PointValue);
        await BookExitA(ExitReason.SessionEnd, bar.Close, bar.Time, isLong);
    }

    private async Task ForceExitB(Bar bar)
    {
        if (!_activeB) return;
        bool isLong = _stB == 3;
        _pnlB += ExitProcessor.ForcedExit(isLong, _entB, bar.Close,
            _cfg.Contracts, _partHitB, _cfg.UsePartialB, _cfg.PointValue);
        await BookExitB(ExitReason.SessionEnd, bar.Close, bar.Time, isLong);
    }

    /// <summary>
    /// Evaluates arm conditions for Setup A and B without placing entries/exits.
    /// Called during warmup so the engine starts with the correct armed state.
    /// </summary>
    private void EvaluateArmState(Bar bar, TimeOnly localTime)
    {
        // Track cutoff — armed state is cleared past cutoff.
        // Guard against overnight bars (>=SessionStartHour) falsely triggering the cutoff.
        var cutoff    = new TimeOnly(_cfg.CutoffHour, _cfg.CutoffMinute);
        var sessStart = new TimeOnly(_cfg.SessionStartHour, 0);
        if (_cfg.UseTimeFilter && localTime >= cutoff && localTime < sessStart) _pastCutoff = true;

        if (!_orb.IsSet || _orb.OrbRange <= 0) return;

        bool aboveVwap  = !_cfg.UseVwap || (_vwap.IsReady && bar.Close > _vwap.Value);
        bool belowVwap  = !_cfg.UseVwap || (_vwap.IsReady && bar.Close < _vwap.Value);
        bool orbLongOk  = !_cfg.UseOrbClose || _orb.OrbBullClose;
        bool orbShortOk = !_cfg.UseOrbClose || _orb.OrbBearClose;
        decimal orbHigh = _orb.OrbHigh, orbLow = _orb.OrbLow, orbRange = _orb.OrbRange;

        // ── Setup A — arm only (no entry/exit) ───────────────────
        if (_cfg.EnableA && !_activeA)
        {
            if (_pastCutoff && (_stA == 1 || _stA == -1))
                { _stA = 0; _armEntryA = 0; }
            else if ((_stA == 1 && bar.Close < orbLow) || (_stA == -1 && bar.Close > orbHigh))
                { _stA = 0; _armEntryA = 0; }
            else
            {
                bool isReady = _tradeCountA < _cfg.MaxTradesA;
                if (isReady && _stA == 0 && !_pastCutoff)
                {
                    decimal nearDist = orbRange * _cfg.NearPct;
                    if (bar.High >= orbHigh - nearDist && orbLongOk && aboveVwap)
                        { _stA = 1; _armEntryA = bar.Open; }
                    else if (bar.Low <= orbLow + nearDist && orbShortOk && belowVwap)
                        { _stA = -1; _armEntryA = bar.Open; }
                }
            }
        }

        // ── Setup B — arm + retest only (no entry/exit) ──────────
        if (_cfg.EnableB && !_activeB)
        {
            if (_pastCutoff && Math.Abs(_stB) <= 2)
                { _stB = 0; _armEntryB = 0; }
            else if ((_stB > 0 && _stB < 3 && bar.Close < orbLow) ||
                     (_stB < 0 && _stB > -3 && bar.Close > orbHigh))
                { _stB = 0; _armEntryB = 0; }
            else
            {
                bool isReady = _tradeCountB < _cfg.MaxTradesB;
                if (isReady && _stB == 0 && !_pastCutoff)
                {
                    if (bar.Close > orbHigh && orbLongOk && aboveVwap)
                        { _stB = 1; _armEntryB = bar.Open; }
                    else if (bar.Close < orbLow && orbShortOk && belowVwap)
                        { _stB = -1; _armEntryB = bar.Open; }
                }
                // Conservative retest zone detection
                if (!_cfg.IsAggressiveB)
                {
                    decimal retestW = orbRange * _cfg.RetestPct;
                    decimal orbMid  = _orb.OrbMid;
                    if (_stB == 1  && bar.Low  <= orbHigh + retestW && bar.High >= orbHigh - retestW) _stB = 2;
                    if (_stB == -1 && bar.High >= orbLow  - retestW && bar.Low  <= orbLow  + retestW) _stB = -2;
                    if (_stB == 2  && bar.Close < orbMid) _stB = 0;
                    if (_stB == -2 && bar.Close > orbMid) _stB = 0;
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
            int rem = _partHitA ? _cfg.Contracts - (int)Math.Floor(_cfg.Contracts * 0.5) : _cfg.Contracts;
            viewA = new ActiveTradeView
            {
                Setup = SetupId.A, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entA, CurrentStop = _stopA, Target = _tgtA, Partial = _partialA,
                Contracts = _cfg.Contracts, RemainingContracts = rem,
                PartialFilled = _partHitA, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entA : _entA - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeA
            };
        }

        ActiveTradeView? viewB = null;
        if (_activeB)
        {
            bool isLong = _stB == 3;
            int rem = _partHitB ? _cfg.Contracts - (int)Math.Floor(_cfg.Contracts * 0.5) : _cfg.Contracts;
            viewB = new ActiveTradeView
            {
                Setup = SetupId.B, Direction = isLong ? Direction.Long : Direction.Short,
                Entry = _entB, CurrentStop = _stopB, Target = _tgtB, Partial = _partialB,
                Contracts = _cfg.Contracts, RemainingContracts = rem,
                PartialFilled = _partHitB, LastPrice = lastPx,
                UnrealizedPnl = (isLong ? lastPx - _entB : _entB - lastPx) * _cfg.PointValue * rem,
                EnteredAt = _entryTimeB
            };
        }

        await _sink.OnSnapshotAsync(new EngineSnapshot
        {
            Time   = bar.Time, Ticker = _cfg.Ticker, IsLive = true,
            LastPrice  = lastPx,
            LastUpdate = DateTime.UtcNow,
            SetupA = viewA, SetupB = viewB,

            TodayPnl    = _todayPnl,
            TodayTrades = _todayWins + _todayLosses,
            TodayWins   = _todayWins,
            TodayLosses = _todayLosses,
            TodayMaxDD  = _todayMaxDD,

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

            OrbFormed    = _orb.IsSet,
            PastCutoff   = _pastCutoff,
            SessionEnded = _rthEnded,

            SetupAEnabled = _cfg.EnableA,
            SetupBEnabled = _cfg.EnableB,
            SetupAState = _stA,
            SetupBState = _stB,

            StickyTgtA = _stickyTgtA,
            StickyStpA = _stickyStpA,
            StickyTgtB = _stickyTgtB,
            StickyStpB = _stickyStpB,

            RecentAlerts = _alertFeed.TakeLast(20).ToList()
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

    private void AddAlert(string type, SetupId setup, string msg, string color)
    {
        _alertFeed.Add(new AlertEvent { Time = DateTime.UtcNow, Type = type, Setup = setup, Message = msg, Color = color });
        if (_alertFeed.Count > 100) _alertFeed.RemoveAt(0);
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
