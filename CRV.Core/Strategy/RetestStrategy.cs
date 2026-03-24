// CRV.Core/Strategy/RetestStrategy.cs
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

/// <summary>
/// Setup B — ORB Retest strategy extracted from OrbStrategyEngine.
/// Implements ISetupStrategy; produces pending signals consumed by the engine.
/// State machine: 0=idle, ±1=armed (breakout detected), ±2=retest zone (Conservative)
///                or confirmed-arm-enter-next-bar (SmartAggressive),
///                ±3=active trade (LONG=3, SHORT=-3).
/// </summary>
public class RetestStrategy : ISetupStrategy
{
    // ── Config ────────────────────────────────────────────────────
    private StrategySetupConfig _cfg;

    // ── State machine ─────────────────────────────────────────────
    // 0=idle, 1=armed LONG, -1=armed SHORT,
    // 2=retest LONG, -2=retest SHORT,
    // 3=active LONG, -3=active SHORT
    private int     _state       = 0;
    private decimal _armEntry    = 0;   // bar.Open at time of arming

    // ── Active trade ──────────────────────────────────────────────
    private decimal  _entry      = 0;
    private decimal  _stop       = 0;
    private decimal  _target     = 0;
    private decimal  _partial    = 0;
    private decimal  _initStop   = 0;
    private int      _contracts  = 0;
    private bool     _partHit    = false;
    private decimal  _pnl        = 0;
    private DateTime _entryTime  = DateTime.MinValue;

    // ── Sticky exit indicators ─────────────────────────────────────
    private bool _stickyTgt = false;
    private bool _stickyStop = false;

    // ── Re-arm guard (prevents re-entering same side until price leaves ORB zone) ──
    private bool _bullTraded = false;
    private bool _bearTraded = false;
    private bool _bullLeftZone = false;  // SmartAggressive: price has left the bull zone after a trade
    private bool _bearLeftZone = false;  // SmartAggressive: price has left the bear zone after a trade
    private bool _pastCutoff = false;
    private bool _retestLeftZone = false;  // Conservative: price must leave the ORB level zone before retest can fire

    // ── Counters ──────────────────────────────────────────────────
    private int     _tradeCount  = 0;

    // ── Win/loss stats ────────────────────────────────────────────
    private int     _wins        = 0;
    private int     _losses      = 0;
    private decimal _winPnl      = 0;
    private decimal _lossPnl     = 0;

    // ── Last ATR ratio (cached from OrbState.AtrRatio when trade enters) ──
    private decimal _lastAtrRatio = 0;

    // ── Pending signals ───────────────────────────────────────────
    private EntrySignal?   _pendingEntry   = null;
    private ExitSignal?    _pendingExit    = null;
    private PartialSignal? _pendingPartial = null;
    private BESignal?      _pendingBe      = null;
    private ActiveTradeView? _preExitTrade = null;

    public RetestStrategy(StrategySetupConfig cfg)
    {
        _cfg = cfg;
    }

    // ── ISetupStrategy identity ───────────────────────────────────
    public string       Id           => _cfg.Id;
    public SetupId      SetupId      => _cfg.SetupId;
    public StrategyType StrategyType => _cfg.StrategyType;
    public string       Name         => _cfg.Name;
    public string       Ticker       => _cfg.Ticker;
    public decimal      PointValue   => _cfg.PointValue;
    public bool         IsActive     => _state == 3 || _state == -3;
    public bool         IsArmed      => _state == 1 || _state == -1 || _state == 2 || _state == -2;
    public int          CutoffHour   => _cfg.CutoffHour;
    public int          CutoffMinute => _cfg.CutoffMinute;
    public (int Hour, int Minute) GetCutoffForSession(string s) => _cfg.GetCutoffForSession(s);
    public bool IsEnabledForSession(string s) => _cfg.IsEnabledForSession(s);

    // ── Pending signals ───────────────────────────────────────────
    public EntrySignal?   PendingEntry   => _pendingEntry;
    public ExitSignal?    PendingExit    => _pendingExit;
    public PartialSignal? PendingPartial => _pendingPartial;
    public BESignal?      PendingBE      => _pendingBe;
    public ActiveTradeView? PreExitTrade => _preExitTrade;

    public void ClearPendingSignals()
    {
        _pendingEntry   = null;
        _pendingExit    = null;
        _pendingPartial = null;
        _pendingBe      = null;
        _preExitTrade   = null;
        _stickyTgt  = false;
        _stickyStop = false;
    }

    public void Reconfigure(StrategySetupConfig config) => _cfg = config;

    /// <summary>
    /// Revert an entry back to armed state. Used by the engine during cooldown
    /// when OnBar triggers arm+entry on the same bar but entry must be blocked.
    /// </summary>
    public void RevertEntry()
    {
        if (!IsActive) return;
        // Revert to retest state (±2), not armed (±1), since entry comes from retest
        int retestState = _state > 0 ? 2 : -2;
        _state    = retestState;
        _entry    = 0; _stop = 0; _target = 0; _partial = 0;
        _initStop = 0; _contracts = 0; _partHit = false; _pnl = 0;
        _entryTime = DateTime.MinValue;
        ClearPendingSignals();
    }

    public void Reset()
    {
        _state     = 0; _armEntry  = 0;
        _entry     = 0; _stop      = 0; _target    = 0; _partial   = 0;
        _initStop  = 0; _contracts = 0;
        _partHit   = false; _pnl   = 0; _entryTime = DateTime.MinValue;
        _stickyTgt = false; _stickyStop = false;
        _bullTraded = false; _bearTraded = false;
        _bullLeftZone = false; _bearLeftZone = false;
        _tradeCount = 0;
        _wins = 0; _losses = 0; _winPnl = 0; _lossPnl = 0;
        _lastAtrRatio = 0;
        ClearPendingSignals();
    }

    public void Disarm()
    {
        if (!IsActive) { _state = 0; _armEntry = 0; _pastCutoff = true; }
    }

    public void ResetCutoff() { _pastCutoff = false; }

    public void ResetSession()
    {
        _state     = 0; _armEntry  = 0;
        _entry     = 0; _stop      = 0; _target    = 0; _partial   = 0;
        _initStop  = 0; _contracts = 0;
        _partHit   = false; _pnl   = 0; _entryTime = DateTime.MinValue;
        _stickyTgt = false; _stickyStop = false;
        _bullTraded = false; _bearTraded = false;
        _bullLeftZone = false; _bearLeftZone = false;
        _pastCutoff = false; _retestLeftZone = false;
        _tradeCount = 0;
        _lastAtrRatio = 0;
        ClearPendingSignals();
    }

    public void ResetTradeCounters()
    {
        _tradeCount = 0;
        _wins = 0; _losses = 0; _winPnl = 0; _lossPnl = 0;
        _pastCutoff = false;
    }

    // ── OnBar ──────────────────────────────────────────────────────
    public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        if (!_cfg.Enabled) return;
        if (!orb.IsSet || orb.Range <= 0) return;

        _lastAtrRatio = orb.AtrRatio;

        ProcessArm(bar, orb, indicators);
        ProcessBarExit(bar, orb, indicators);
    }

    private void ProcessArm(Bar bar, OrbState orb, IndicatorState ind)
    {
        if (IsActive) return;

        decimal orbHigh  = orb.High;
        decimal orbLow   = orb.Low;
        decimal orbMid   = orb.Mid;
        decimal orbRange = orb.Range;

        // Cancel arm if price leaves valid zone
        // For retest: armed (±1) or retest (±2) states are cancelled
        if ((_state > 0 && _state < 3 && bar.Close < orbLow) ||
            (_state < 0 && _state > -3 && bar.Close > orbHigh))
        {
            _state = 0; _armEntry = 0; _retestLeftZone = false;
        }

        bool isReady = _tradeCount < _cfg.MaxTrades;

        // Arm logic (only when idle)
        if (isReady && _state == 0)
        {
            decimal nearDist = orbRange * _cfg.NearPct;

            // Clear directional lock once price leaves the arm zone
            if (_cfg.IsSmartAggressive)
            {
                // SmartAggressive re-arm: price must LEAVE the zone first, then RETURN.
                // Step 1: track when price leaves the zone
                if (_bullTraded && !_bullLeftZone && bar.Close < orbHigh - nearDist)
                    _bullLeftZone = true;
                if (_bearTraded && !_bearLeftZone && bar.Close > orbLow + nearDist)
                    _bearLeftZone = true;

                // Step 2: clear lock only when price has left AND returned to the zone
                if (_bullTraded && _bullLeftZone && bar.Close >= orbHigh - nearDist)
                    { _bullTraded = false; _bullLeftZone = false; }
                if (_bearTraded && _bearLeftZone && bar.Close <= orbLow + nearDist)
                    { _bearTraded = false; _bearLeftZone = false; }
            }
            else
            {
                if (_bullTraded && bar.Close < orbHigh - nearDist) _bullTraded = false;
                if (_bearTraded && bar.Close > orbLow  + nearDist) _bearTraded = false;
            }

            bool aboveVwap  = !_cfg.UseVwap || bar.Close > ind.Vwap;
            bool belowVwap  = !_cfg.UseVwap || bar.Close < ind.Vwap;
            bool orbLongOk  = !_cfg.UseOrbClose || orb.BullClose;
            bool orbShortOk = !_cfg.UseOrbClose || orb.BearClose;

            if (bar.Close >= orbHigh - nearDist && orbLongOk && aboveVwap && !_bullTraded)
            {
                _state = 1; _armEntry = bar.Open; _retestLeftZone = false;
            }
            else if (bar.Close <= orbLow + nearDist && orbShortOk && belowVwap && !_bearTraded)
            {
                _state = -1; _armEntry = bar.Open; _retestLeftZone = false;
            }
        }

        // Aggressive mode: enter on arm bar directly
        if (_cfg.IsAggressive)
        {
            if (isReady && _state == 1)
            {
                decimal ep = _armEntry > 0 ? _armEntry : orbHigh;
                TryEntry(ep, true, orb, bar.Time);
            }
            else if (isReady && _state == -1)
            {
                decimal ep = _armEntry > 0 ? _armEntry : orbLow;
                TryEntry(ep, false, orb, bar.Time);
            }
        }
        else if (_cfg.IsSmartAggressive)
        {
            // SmartAggressive: arm on bar N (state ±1), enter on bar N+1 at Open.
            // State ±2 = "confirmed arm, enter next bar". After exit, _bullTraded/_bearTraded
            // block re-arm until price leaves the zone AND retests the level.

            // Transition: armed (±1) → confirmed (±2) on the NEXT bar
            if (isReady && _state == 2)
            {
                TryEntry(bar.Open, true, orb, bar.Time);
            }
            else if (isReady && _state == -2)
            {
                TryEntry(bar.Open, false, orb, bar.Time);
            }

            // Promote ±1 → ±2 (will enter on next ProcessArm call, i.e. next bar)
            if (_state == 1)  _state = 2;
            if (_state == -1) _state = -2;

            // De-arm if price crosses OrbMid (setup invalidated)
            if (_state == 2  && bar.Close < orbMid) _state = 0;
            if (_state == -2 && bar.Close > orbMid) _state = 0;
        }
        else
        {
            // Conservative — retest-zone state transitions
            // Price must LEAVE the ORB level zone first, then RETURN for a valid retest.
            // This prevents the arm bar (which is already near the level) from instantly
            // satisfying the retest condition.
            decimal retestW = orbRange * _cfg.RetestPct;

            // Step 1: detect when price leaves the zone after arming
            if (_state == 1 && !_retestLeftZone && bar.Low < orbHigh - retestW)
                _retestLeftZone = true;
            if (_state == -1 && !_retestLeftZone && bar.High > orbLow + retestW)
                _retestLeftZone = true;

            // Step 2: retest fires only after price has left AND returned to the zone
            if (_retestLeftZone)
            {
                if (_state == 1 && bar.Low <= orbHigh + retestW && bar.High >= orbHigh - retestW)
                    _state = 2;
                if (_state == -1 && bar.High >= orbLow - retestW && bar.Low <= orbLow + retestW)
                    _state = -2;
            }

            // Entry from retest state when price breaks back through
            if (isReady && _state == 2 && bar.Close > orbHigh)
                TryEntry(orbHigh, true, orb, bar.Time);
            else if (isReady && _state == -2 && bar.Close < orbLow)
                TryEntry(orbLow, false, orb, bar.Time);

            // De-arm if price crosses OrbMid (retest failed)
            if (_state == 2  && bar.Close < orbMid) _state = 0;
            if (_state == -2 && bar.Close > orbMid) _state = 0;
        }
    }

    private void ProcessBarExit(Bar bar, OrbState orb, IndicatorState ind)
    {
        if (!IsActive) return;

        bool isLong   = _state == 3;
        bool prevPart = _partHit;

        var result = ExitProcessor.ProcessBar(
            true, isLong, _entry, _stop, _target, _partial,
            _contracts, _pnl, _partHit,
            _cfg.UsePartial, _cfg.UseBe, _cfg.PointValue,
            bar.High, bar.Low, _cfg.PartialCts);

        _pnl     = result.NewPnl;
        _stop    = result.NewStop;
        _partHit = result.PartialHit;

        bool partJustHit = _partHit && !prevPart;
        bool closingNow  = result.HitTarget || result.HitStop;

        // Fire partial/BE signals (only when trade stays open)
        if (partJustHit && !closingNow)
        {
            int half      = CalcPartialCts(_contracts, _cfg.PartialCts);
            int remaining = _contracts - half;
            if (half > 0)
            {
                _pendingPartial = new PartialSignal(
                    _cfg.SetupId,
                    isLong ? Direction.Long : Direction.Short,
                    _partial, half, remaining, _entry, bar.Time);

                if (_cfg.UseBe)
                {
                    _pendingBe = new BESignal(
                        _cfg.SetupId,
                        isLong ? Direction.Long : Direction.Short,
                        _entry, _entry, remaining, bar.Time);
                }
            }
        }

        if (closingNow)
        {
            decimal exitPx = result.HitTarget ? _target : _stop;
            var reason     = result.HitTarget ? ExitReason.Target : ExitReason.Stop;
            bool isBE      = _cfg.AllowRearmAfterBe && !result.HitTarget && exitPx == _entry;
            if (!isBE) { if (isLong) _bullTraded = true; else _bearTraded = true; }
            if (result.HitTarget) _stickyTgt  = true;
            else                  _stickyStop = true;

            BookExit(reason, exitPx, bar.Time, isLong);
        }
    }

    // ── OnTick ──────────────────────────────────────────────────────
    public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        if (!_cfg.Enabled) return;
        if (!orb.IsSet || orb.Range <= 0) return;

        decimal orbRange = orb.Range;
        decimal tickTol  = _cfg.TickSize * 2;

        // Entry: armed or retest state
        if (!IsActive && IsArmed)
        {
            bool isLong = _state > 0;
            if (_cfg.IsAggressive)
            {
                TryEntry(_armEntry, isLong, orb, utc);
            }
            else if (_cfg.IsSmartAggressive)
            {
                // SmartAggressive tick entry: state ±2 means confirmed (next bar).
                // Enter at current price on tick.
                if (_state == 2 || _state == -2)
                    TryEntry(price, isLong, orb, utc);
            }
            else
            {
                // Conservative: enter when price retests the ORB breakout level
                decimal retestDist = orbRange * _cfg.RetestPct;
                decimal orbHigh    = orb.High;
                decimal orbLow     = orb.Low;
                if (isLong  && price <= orbHigh + retestDist + tickTol)
                    TryEntry(orbHigh, true, orb, utc);
                else if (!isLong && price >= orbLow - retestDist - tickTol)
                    TryEntry(orbLow, false, orb, utc);
            }
            return; // don't check exit on same tick as entry attempt
        }

        // Exit: active position
        if (!IsActive) return;
        bool long_ = _state == 3;
        bool hitStop   = long_ ? price <= _stop   : price >= _stop;
        bool hitTarget = long_ ? price >= _target  : price <= _target;
        bool hitPart   = _cfg.UsePartial && !_partHit &&
                         (long_ ? price >= _partial : price <= _partial);

        if (!hitStop && !hitTarget && !hitPart) return;

        // Partial fill check (price-based)
        bool partJustHit = false;
        if (hitPart && !hitTarget)
        {
            int half      = CalcPartialCts(_contracts, _cfg.PartialCts);
            int remaining = _contracts - half;
            if (half > 0)
            {
                partJustHit = true;
                _partHit    = true;
                _pnl += (long_ ? _partial - _entry : _entry - _partial) * _cfg.PointValue * half;

                _pendingPartial = new PartialSignal(
                    _cfg.SetupId,
                    long_ ? Direction.Long : Direction.Short,
                    _partial, half, remaining, _entry, utc);

                if (_cfg.UseBe)
                {
                    _stop   = _entry; // move stop to breakeven
                    _pendingBe = new BESignal(
                        _cfg.SetupId,
                        long_ ? Direction.Long : Direction.Short,
                        _entry, _entry, remaining, utc);
                }
            }
            if (!hitTarget && !hitStop) return; // partial only, trade still open
        }

        ExitReason reason = hitTarget ? ExitReason.Target : ExitReason.Stop;
        decimal    exitPx = hitTarget ? _target : _stop;

        int remCts = (_partHit && _cfg.UsePartial)
            ? _contracts - CalcPartialCts(_contracts, _cfg.PartialCts) : _contracts;
        _pnl += (long_ ? exitPx - _entry : _entry - exitPx) * _cfg.PointValue * remCts;

        bool isBE = _cfg.AllowRearmAfterBe && reason == ExitReason.Stop && exitPx == _entry;
        if (!isBE) { if (long_) _bullTraded = true; else _bearTraded = true; }
        if (hitTarget) _stickyTgt  = true;
        else           _stickyStop = true;

        BookExit(reason, exitPx, utc, long_);
    }

    // ── ForceExit ─────────────────────────────────────────────────
    public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd)
    {
        if (!IsActive) return;
        bool isLong = _state == 3;
        _pnl += ExitProcessor.ForcedExit(isLong, _entry, currentPrice,
            _contracts, _partHit, _cfg.UsePartial, _cfg.PointValue, _cfg.PartialCts);
        BookExit(reason, currentPrice, utcTime, isLong);
    }

    // ── ApplyFill ─────────────────────────────────────────────────
    public void ApplyFill(decimal actualFillPrice)
    {
        if (!IsActive) return;
        decimal slippage = actualFillPrice - _entry;
        _entry   = actualFillPrice;
        _stop   += slippage;
        _target += slippage;
        _partial += slippage;
        _initStop += slippage;
        // Re-emit updated entry signal with corrected levels
        _pendingEntry = _pendingEntry is null ? null :
            _pendingEntry with { Entry = _entry, Stop = _stop, Target = _target, Partial = _partial };
    }

    // ── GetSnapshot ───────────────────────────────────────────────
    public SetupStateSnapshot GetSnapshot() => new()
    {
        Id          = _cfg.Id,
        SetupId     = _cfg.SetupId,
        Name        = _cfg.Name,
        State       = _state,
        IsActive    = IsActive,
        IsArmed     = IsArmed,
        PastCutoff  = _pastCutoff,
        TradeCount  = _tradeCount,
        MaxTrades   = _cfg.MaxTrades,
        StickyTgt   = _stickyTgt,
        StickyStp   = _stickyStop,
        Enabled     = _cfg.Enabled,
        Wins        = _wins,
        Losses      = _losses,
        WinPnl      = _winPnl,
        LossPnl     = _lossPnl,
        Expectancy  = (_wins + _losses) > 0
            ? (_winPnl + _lossPnl) / (_wins + _losses) : 0m,
    };

    // ── GetActiveTrade ────────────────────────────────────────────
    public ActiveTradeView? GetActiveTrade(decimal lastPrice)
    {
        if (!IsActive) return null;
        bool isLong      = _state == 3;
        int  half        = CalcPartialCts(_contracts, _cfg.PartialCts);
        int  remaining   = (_cfg.UsePartial && _partHit && half > 0) ? _contracts - half : _contracts;
        decimal unrealized = (isLong ? lastPrice - _entry : _entry - lastPrice) * _cfg.PointValue * remaining;

        return new ActiveTradeView
        {
            Setup              = _cfg.SetupId,
            Direction          = isLong ? Direction.Long : Direction.Short,
            Entry              = _entry,
            InitialStop        = _initStop,
            CurrentStop        = _stop,
            Target             = _target,
            Partial            = _partial,
            Contracts          = _contracts,
            RemainingContracts = remaining,
            PartialFilled      = _partHit,
            LastPrice          = lastPrice,
            UnrealizedPnl      = unrealized + _pnl,
            EnteredAt          = _entryTime,
            Ticker             = _cfg.Ticker,
            PointValue         = _cfg.PointValue,
        };
    }

    // ── Private helpers ───────────────────────────────────────────

    private void TryEntry(decimal ep, bool isLong, OrbState orb, DateTime time)
    {
        if (IsActive) return; // already in trade

        // Apply entry tick offset
        if (_cfg.EntryTickOffset != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffset * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + offset : ep - offset, _cfg.TickSize);
        }

        // Setup B uses CalcLevelsB (entry-anchored stop)
        var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
            _cfg.TargetPct, _cfg.PartialPct, orb.Range, _cfg.StopPct, _cfg.TickSize);

        if (rr < _cfg.MinRr) return;

        _entry     = ep; _stop = sl; _target = tp; _partial = pp;
        _initStop  = sl; _pnl  = 0;
        _contracts = CalcContracts();
        _partHit   = false;
        _state     = isLong ? 3 : -3;
        _entryTime = time;

        _pendingEntry = new EntrySignal(
            _cfg.SetupId,
            isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, _contracts, time,
            _cfg.OrderType, Ticker: _cfg.Ticker);
    }

    private void BookExit(ExitReason reason, decimal exitPx, DateTime time, bool isLong)
    {
        // Capture trade snapshot BEFORE resetting state — RouteSignalsAsync needs it
        _preExitTrade = GetActiveTrade(exitPx);

        int remCts = (_partHit && _cfg.UsePartial)
            ? _contracts - CalcPartialCts(_contracts, _cfg.PartialCts) : _contracts;

        _pendingExit = new ExitSignal(_cfg.SetupId, reason, exitPx, remCts, time, _cfg.Ticker);

        if (reason != ExitReason.AdverseTime)
            _tradeCount++;

        if (_pnl > 0) { _wins++;   _winPnl  += _pnl; }
        else          { _losses++; _lossPnl += _pnl; }

        _state    = 0;
        _entry    = 0; _stop = 0; _target = 0; _partial = 0; _initStop = 0;
        _contracts = 0; _partHit = false; _pnl = 0;
    }

    private int CalcContracts()
    {
        bool isHighVol = _lastAtrRatio >= 1.0m;
        int  cts       = isHighVol
            ? (int)Math.Round(_cfg.Contracts * _cfg.HiVolMult)
            : _cfg.Contracts;
        return Math.Min(cts, _cfg.MaxContracts);
    }

    private static int CalcPartialCts(int totalCts, int fixedCts)
    {
        if (fixedCts > 0)
            return Math.Min(fixedCts, totalCts - 1);
        return (int)Math.Floor(totalCts * 0.5);
    }
}
