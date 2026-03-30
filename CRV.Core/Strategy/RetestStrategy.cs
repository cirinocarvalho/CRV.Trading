// CRV.Core/Strategy/RetestStrategy.cs
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

/// <summary>
/// Setup B — ORB Retest strategy extracted from OrbStrategyEngine.
/// Pure signal generator: emits EntrySignal only. Trade lifecycle managed by BrokerEventHandler.
/// State machine: 0=idle, ±1=armed (breakout detected), ±2=retest zone (Conservative)
///                or confirmed-arm-enter-next-bar (SmartAggressive).
/// </summary>
public class RetestStrategy : ISetupStrategy
{
    // ── Config ────────────────────────────────────────────────────
    private StrategySetupConfig _cfg;

    // ── State machine ─────────────────────────────────────────────
    // 0=idle, 1=armed LONG, -1=armed SHORT,
    // 2=retest LONG, -2=retest SHORT
    private int     _state       = 0;
    private decimal _armEntry    = 0;   // bar.Open at time of arming

    // ── Re-arm guard (prevents re-entering same side until price leaves ORB zone) ──
    private bool _bullTraded = false;
    private bool _bearTraded = false;
    private bool _bullLeftZone = false;  // SmartAggressive: price has left the bull zone after a trade
    private bool _bearLeftZone = false;  // SmartAggressive: price has left the bear zone after a trade
    private bool _pastCutoff = false;
    private bool _retestLeftZone = false;  // Conservative: price must leave the ORB level zone before retest can fire
    private bool _breakoutConfirmed = false; // Price must break through ORB level before retest can fire

    // ── Counters (per-direction to avoid longs eating short slots) ─
    private int     _longCount   = 0;
    private int     _shortCount  = 0;
    private int     _tradeCount  = 0;  // kept for snapshot compat

    // ── Win/loss stats ────────────────────────────────────────────
    private int     _wins        = 0;
    private int     _losses      = 0;
    private decimal _winPnl      = 0;
    private decimal _lossPnl     = 0;

    // ── Last ATR ratio (cached from OrbState.AtrRatio when trade enters) ──
    private decimal _lastAtrRatio = 0;

    // ── Pending signals ───────────────────────────────────────────
    private EntrySignal? _pendingEntry = null;

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
    public bool         IsActive     => _inTrade;
    public bool         IsArmed      => _state == 1 || _state == -1 || _state == 2 || _state == -2;
    private bool        _inTrade;
    public bool         InTrade      => _inTrade;
    public void SetInTrade(bool active)
    {
        _inTrade = active;
        if (!active) _state = 0;
    }
    public int          CutoffHour   => _cfg.CutoffHour;
    public int          CutoffMinute => _cfg.CutoffMinute;
    public (int Hour, int Minute) GetCutoffForSession(string s) => _cfg.GetCutoffForSession(s);
    public bool IsEnabledForSession(string s) => _cfg.IsEnabledForSession(s);

    // ── Pending signals ───────────────────────────────────────────
    public EntrySignal? PendingEntry => _pendingEntry;

    public void ClearPendingSignals()
    {
        _pendingEntry = null;
    }

    public void Reconfigure(StrategySetupConfig config)
    {
        _cfg = config;
    }

    /// <summary>
    /// Revert an entry back to armed state. Used by the engine when
    /// opposing position guard or cross-setup coordination blocks an entry.
    /// </summary>
    public void RevertEntry()
    {
        _pendingEntry = null;
        // TryEntry resets _state=0, entry is simply dropped.
    }

    public void Reset()
    {
        _state     = 0; _armEntry  = 0;
        _bullTraded = false; _bearTraded = false;
        _bullLeftZone = false; _bearLeftZone = false;
        _longCount = 0; _shortCount = 0; _tradeCount = 0;
        _wins = 0; _losses = 0; _winPnl = 0; _lossPnl = 0;
        _lastAtrRatio = 0;
        ClearPendingSignals();
    }

    public void Disarm()
    {
        if (!_inTrade) { _state = 0; _armEntry = 0; _pastCutoff = true; }
    }

    public void ResetCutoff() { _pastCutoff = false; }

    public void ResetSession()
    {
        _state     = 0; _armEntry  = 0;
        _bullTraded = false; _bearTraded = false;
        _bullLeftZone = false; _bearLeftZone = false;
        _pastCutoff = false; _retestLeftZone = false; _breakoutConfirmed = false;
        _longCount = 0; _shortCount = 0; _tradeCount = 0;
        _lastAtrRatio = 0;
        ClearPendingSignals();
    }

    public void ResetTradeCounters()
    {
        _longCount = 0; _shortCount = 0; _tradeCount = 0;
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
    }

    private void ProcessArm(Bar bar, OrbState orb, IndicatorState ind)
    {
        if (_inTrade) return;

        decimal orbHigh  = orb.High;
        decimal orbLow   = orb.Low;
        decimal orbMid   = orb.Mid;
        decimal orbRange = orb.Range;

        // Cancel arm if price leaves valid zone
        if ((_state > 0 && bar.Close < orbLow) ||
            (_state < 0 && bar.Close > orbHigh))
        {
            _state = 0; _armEntry = 0; _retestLeftZone = false; _breakoutConfirmed = false;
        }

        bool longReady  = _longCount  < _cfg.EffectiveMaxLong;
        bool shortReady = _shortCount < _cfg.EffectiveMaxShort;
        bool isReady    = longReady || shortReady;

        // Arm logic (only when idle)
        if (isReady && _state == 0)
        {
            decimal nearDist = orbRange * _cfg.NearPct;

            // Clear directional lock once price leaves the arm zone
            if (_cfg.IsSmartAggressive)
            {
                if (_bullTraded && !_bullLeftZone && bar.Close < orbHigh - nearDist)
                    _bullLeftZone = true;
                if (_bearTraded && !_bearLeftZone && bar.Close > orbLow + nearDist)
                    _bearLeftZone = true;

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

            // Arm requires a bar CLOSE outside the ORB range (confirmed breakout)
            if (longReady && bar.Close > orbHigh && orbLongOk && aboveVwap && !_bullTraded)
            {
                _state = 1; _armEntry = bar.Open; _retestLeftZone = false; _breakoutConfirmed = true;
            }
            else if (shortReady && bar.Close < orbLow && orbShortOk && belowVwap && !_bearTraded)
            {
                _state = -1; _armEntry = bar.Open; _retestLeftZone = false; _breakoutConfirmed = true;
            }
        }

        // Aggressive mode: arm and compute entry on same bar
        if (_cfg.IsAggressive)
        {
            if (longReady && _state == 1)
                TryEntry(orbHigh, true, orb, bar.Time);
            else if (shortReady && _state == -1)
                TryEntry(orbLow, false, orb, bar.Time);
        }
        else if (_cfg.IsSmartAggressive)
        {
            // SmartAggressive: arm on bar N (state ±1), enter on bar N+1.
            // Only enter if price has actually crossed the ORB boundary.
            if (longReady && _state == 2 && bar.Open > orbHigh)
                TryEntry(bar.Open, true, orb, bar.Time);
            else if (shortReady && _state == -2 && bar.Open < orbLow)
                TryEntry(bar.Open, false, orb, bar.Time);

            // Promote ±1 → ±2 (will enter on next ProcessArm call, i.e. next bar)
            if (_state == 1)  _state = 2;
            if (_state == -1) _state = -2;

            // De-arm if price fully crosses to opposite ORB boundary (setup invalidated)
            // Using orbLow/orbHigh instead of orbMid — allows normal pullbacks within ORB range
            if (_state == 2  && bar.Close < orbLow)  _state = 0;
            if (_state == -2 && bar.Close > orbHigh) _state = 0;
        }
        else
        {
            // Conservative — retest-zone state transitions
            decimal retestW = orbRange * _cfg.RetestPct;

            // Step 0: breakout confirmation — price must actually break through the ORB level
            // Long: bar must trade above ORB High. Short: bar must trade below ORB Low.
            if (_state == 1 && !_breakoutConfirmed && bar.High > orbHigh)
                _breakoutConfirmed = true;
            if (_state == -1 && !_breakoutConfirmed && bar.Low < orbLow)
                _breakoutConfirmed = true;

            // Step 1: detect when price leaves the zone after arming (AND breakout confirmed)
            if (_state == 1 && _breakoutConfirmed && !_retestLeftZone && bar.Low < orbHigh - retestW)
                _retestLeftZone = true;
            if (_state == -1 && _breakoutConfirmed && !_retestLeftZone && bar.High > orbLow + retestW)
                _retestLeftZone = true;

            // Step 2: retest fires only after breakout + left zone + returned to zone
            bool zoneOk = _breakoutConfirmed && _retestLeftZone;
            if (zoneOk)
            {
                if (_state == 1 && bar.Low <= orbHigh + retestW && bar.High >= orbHigh - retestW)
                    _state = 2;
                if (_state == -1 && bar.High >= orbLow - retestW && bar.Low <= orbLow + retestW)
                    _state = -2;
            }

            // Entry from retest state
            if (isReady && _state == 2 && bar.Close > orbHigh)
                TryEntry(orbHigh, true, orb, bar.Time);
            else if (isReady && _state == -2 && bar.Close < orbLow)
                TryEntry(orbLow, false, orb, bar.Time);

            // De-arm if price crosses OrbMid (retest failed)
            if (_state == 2  && bar.Close < orbMid) _state = 0;
            if (_state == -2 && bar.Close > orbMid) _state = 0;
        }
    }

    // ── OnTick ──────────────────────────────────────────────────────
    public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        if (!_cfg.Enabled) return;
        if (!orb.IsSet || orb.Range <= 0) return;

        decimal orbRange = orb.Range;
        decimal tickTol  = _cfg.TickSize * 2;

        // Conservative tick entry: only fire after full retest cycle (state ±2)
        // State ±1 = armed but hasn't left/returned to zone yet — must NOT enter.
        bool isLongArm = _state > 0;
        bool tickReady = isLongArm ? _longCount < _cfg.EffectiveMaxLong : _shortCount < _cfg.EffectiveMaxShort;
        bool retestConfirmed = _state == 2 || _state == -2;
        if (!_inTrade && retestConfirmed && tickReady && !_cfg.IsAggressive && !_cfg.IsSmartAggressive)
        {
            decimal retestDist = orbRange * _cfg.RetestPct;
            decimal orbHigh    = orb.High;
            decimal orbLow     = orb.Low;
            // Price must be OUTSIDE ORB but near the boundary to trigger entry
            // Long: price >= orbHigh (outside) and within retestDist above
            // Short: price <= orbLow (outside) and within retestDist below
            if (isLongArm && price >= orbHigh && price <= orbHigh + retestDist + tickTol)
                TryEntry(orbHigh, true, orb, utc);
            else if (!isLongArm && price <= orbLow && price >= orbLow - retestDist - tickTol)
                TryEntry(orbLow, false, orb, utc);
        }
    }

    // ── ForceExit ─────────────────────────────────────────────────
    public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd)
    {
        _pendingEntry = null;
        _state = 0;
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
        StickyTgt   = false,
        StickyStp   = false,
        Enabled     = _cfg.Enabled,
        Wins        = _wins,
        Losses      = _losses,
        WinPnl      = _winPnl,
        LossPnl     = _lossPnl,
        Expectancy  = (_wins + _losses) > 0
            ? (_winPnl + _lossPnl) / (_wins + _losses) : 0m,
    };

    // ── Private helpers ───────────────────────────────────────────

    private void TryEntry(decimal ep, bool isLong, OrbState orb, DateTime time)
    {
        if (_inTrade) return;

        // Apply entry tick offset
        if (_cfg.EntryTickOffset != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffset * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + offset : ep - offset, _cfg.TickSize);
        }

        // Hard guard: retest entry must not be inside the ORB range
        // (prevents entries that haven't crossed the boundary, regardless of mode/offset)
        if (isLong && ep < orb.High) return;
        if (!isLong && ep > orb.Low) return;

        var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
            _cfg.TargetPct, _cfg.PartialPct, orb.Range, _cfg.StopPct, _cfg.TickSize);

        if (rr < _cfg.MinRr) return;

        int contracts = CalcContracts();

        _pendingEntry = new EntrySignal(
            _cfg.SetupId,
            isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, contracts, time,
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe, Mode: _cfg.Mode);

        if (isLong) { _longCount++; _bullTraded = true; }
        else        { _shortCount++; _bearTraded = true; }
        _tradeCount = _longCount + _shortCount;
        _state = 0;
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
