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
    private bool _retestCloseConfirmed = false; // Conservative: bar must CLOSE above/below ORB before tick entry is allowed
    private bool _quickReentryNeedsLeave = false; // QuickReentry: price must leave ORB zone before next re-entry
    private bool _enterNextBarOpen = false; // Conservative Market: confirmed, enter at next bar's Open
    private bool _enterNextBarDirection = false; // true = long, false = short

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

    // ── Stop-mode state ──────────────────────────────────────────
    private Bar?    _prevBar  = null;  // current bar (set at OnBar start) — for same-bar BarHL stop
    private Bar?    _signalBar = null; // bar that fired the arm/confirmation — for deferred-entry BarHL stop
    private decimal _lastVwap = 0;     // latest VWAP for Vwap stop mode

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
    public bool         UseEmaFilter => _cfg.UseEmaFilter;
    public bool         IsActive     => _inTrade;
    public bool         IsArmed      => _state == 1 || _state == -1 || _state == 2 || _state == -2;
    private bool        _inTrade;
    public bool         InTrade      => _inTrade;
    public void SetInTrade(bool active)
    {
        _inTrade = active;
        if (!active)
        {
            _state = 0;
            // QuickReentry: auto-clear the directional trade guard so the
            // strategy can re-arm without waiting for the full nearDist bounce.
            // The arm logic + leave/return requirement (below) still ensures
            // each entry is a distinct retest, not a repeated entry on hovering price.
            if (_cfg.QuickReentry)
            {
                _bullTraded = false;
                _bearTraded = false;
                // Require price to leave the zone and return before next entry.
                // This prevents back-to-back entries on the same bar when price
                // is hovering near the ORB level.
                _quickReentryNeedsLeave = true;
            }
        }
    }
    public void SeedTradeCount(int longs, int shorts)
    {
        _longCount  = longs;
        _shortCount = shorts;
        _tradeCount = longs + shorts;
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
        _prevBar = null; _signalBar = null; _lastVwap = 0;
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
        _pastCutoff = false; _retestLeftZone = false; _breakoutConfirmed = false; _retestCloseConfirmed = false; _quickReentryNeedsLeave = false; _enterNextBarOpen = false;
        _longCount = 0; _shortCount = 0; _tradeCount = 0;
        _lastAtrRatio = 0;
        _prevBar = null; _signalBar = null; _lastVwap = 0;
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
        _lastVwap     = indicators.Vwap;
        // Set before ProcessArm so BarHL stops reference the SIGNAL bar
        // (the bar triggering the entry), not the bar preceding it.
        _prevBar      = bar;

        ProcessArm(bar, orb, indicators);
    }

    private void ProcessArm(Bar bar, OrbState orb, IndicatorState ind)
    {
        if (_inTrade) return;

        // Deferred entry: Conservative confirmed on the previous bar,
        // now enter at THIS bar's Open for a realistic fill price.
        if (_enterNextBarOpen)
        {
            _enterNextBarOpen = false;
            bool isLong = _enterNextBarDirection;
            bool ready = isLong
                ? _longCount < _cfg.EffectiveMaxLong
                : _shortCount < _cfg.EffectiveMaxShort;
            if (ready && _tradeCount < _cfg.MaxTrades)
                TryEntry(bar.Open, isLong, orb, bar.Time);
        }

        decimal orbHigh  = orb.High;
        decimal orbLow   = orb.Low;
        decimal orbMid   = orb.Mid;
        decimal orbRange = orb.Range;

        // Cancel arm if price crosses ORB mid (invalidation)
        if ((_state > 0 && bar.Close < orbMid) ||
            (_state < 0 && bar.Close > orbMid))
        {
            _state = 0; _armEntry = 0; _retestLeftZone = false; _breakoutConfirmed = false; _retestCloseConfirmed = false;
        }

        bool longReady  = _longCount  < _cfg.EffectiveMaxLong;
        bool shortReady = _shortCount < _cfg.EffectiveMaxShort;
        // Enforce the combined MaxTrades cap on top of per-direction limits.
        // MaxTrades=1 with MaxLong=0/MaxShort=0 means 1 trade TOTAL, not 1 per side.
        if (_tradeCount >= _cfg.MaxTrades) { longReady = false; shortReady = false; }
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

            // SmartAggressive: arm requires close ABOVE orbHigh / BELOW orbLow (confirmed breakout).
            // Aggressive/Conservative: arm when NEAR the ORB boundary (within nearDist).
            bool longArm  = _cfg.IsSmartAggressive
                ? bar.Close > orbHigh
                : bar.Close >= orbHigh - nearDist;
            bool shortArm = _cfg.IsSmartAggressive
                ? bar.Close < orbLow
                : bar.Close <= orbLow + nearDist;

            if (longReady && longArm && orbLongOk && aboveVwap && !_bullTraded)
            {
                _state = 1; _armEntry = bar.Open; _retestLeftZone = false; _retestCloseConfirmed = false;
                _breakoutConfirmed = _cfg.IsAggressive || _cfg.IsSmartAggressive;
            }
            else if (shortReady && shortArm && orbShortOk && belowVwap && !_bearTraded)
            {
                _state = -1; _armEntry = bar.Open; _retestLeftZone = false; _retestCloseConfirmed = false;
                _breakoutConfirmed = _cfg.IsAggressive || _cfg.IsSmartAggressive;
            }
        }

        // Aggressive mode: arm and compute entry on same bar
        // Limit → entry at ORB boundary; Market → entry at bar.Open (realistic fill)
        if (_cfg.IsAggressive)
        {
            bool isLimit = _cfg.OrderType == "Limit";
            if (longReady && _state == 1)
                TryEntry(isLimit ? orbHigh : bar.Open, true, orb, bar.Time);
            else if (shortReady && _state == -1)
                TryEntry(isLimit ? orbLow : bar.Open, false, orb, bar.Time);
        }
        else if (_cfg.IsSmartAggressive)
        {
            // SmartAggressive: arm on bar N (state ±1), enter on bar N+1 ONLY.
            // If bar N+1 doesn't qualify, the entry expires — must wait for ORB retest.
            if (longReady && _state == 2 && bar.Open > orbHigh)
                TryEntry(bar.Open, true, orb, bar.Time);
            else if (shortReady && _state == -2 && bar.Open < orbLow)
                TryEntry(bar.Open, false, orb, bar.Time);
            else if (_state == 2 || _state == -2)
                _state = 0; // entry window expired — missed bar N+1

            // Promote ±1 → ±2 (will enter on next ProcessArm call, i.e. next bar)
            // Snapshot this (signal) bar so next-bar BarHL stop references it, not bar N+1.
            if (_state == 1)  { _state = 2;  _signalBar = bar; }
            if (_state == -1) { _state = -2; _signalBar = bar; }

            // De-arm if price crosses ORB mid (setup invalidated)
            if (_state == 2  && bar.Close < orbMid)  _state = 0;
            if (_state == -2 && bar.Close > orbMid) _state = 0;
        }
        else
        {
            // Conservative — retest-zone state transitions
            decimal retestW = orbRange * _cfg.RetestPct;

            // QuickReentry: after a previous trade on this side, skip breakout
            // confirmation and close confirmation, but still require price to
            // LEAVE the ORB zone and RETURN — ensuring each entry is a distinct
            // retest, not a repeated entry while price hovers at the level.
            bool quickLong  = _cfg.QuickReentry && _longCount > 0;
            bool quickShort = _cfg.QuickReentry && _shortCount > 0;

            // Detect "left zone" for quick re-entry: price moved away from ORB level
            if (_quickReentryNeedsLeave)
            {
                decimal retestDist = orbRange * _cfg.RetestPct;
                // Long: price dropped below orbHigh - retestDist
                if (_state == 1 && bar.Close < orbHigh - retestDist)
                    _quickReentryNeedsLeave = false;
                // Short: price rose above orbLow + retestDist
                if (_state == -1 && bar.Close > orbLow + retestDist)
                    _quickReentryNeedsLeave = false;
                // Not armed yet: check raw price vs ORB zone
                if (_state == 0)
                {
                    if (bar.Close < orbHigh - retestDist && bar.Close > orbLow + retestDist)
                        _quickReentryNeedsLeave = false;
                }
            }

            if (quickLong && longReady && _state == 1 && !_quickReentryNeedsLeave)
            {
                TryEntry(orbHigh, true, orb, bar.Time);
            }
            else if (quickShort && shortReady && _state == -1 && !_quickReentryNeedsLeave)
            {
                TryEntry(orbLow, false, orb, bar.Time);
            }
            else
            {
                // Full Conservative retest cycle (first trade, or QuickReentry off)

                // Step 0: breakout confirmation — price must actually break through the ORB level
                // Long: bar must trade above ORB High. Short: bar must trade below ORB Low.
                if (_state == 1 && !_breakoutConfirmed && bar.High > orbHigh)
                    _breakoutConfirmed = true;
                if (_state == -1 && !_breakoutConfirmed && bar.Low < orbLow)
                    _breakoutConfirmed = true;

                // Step 1: detect when price leaves the zone after arming (AND breakout confirmed)
                if (_state == 1 && _breakoutConfirmed && !_retestLeftZone && bar.Low <= orbHigh - retestW)
                    _retestLeftZone = true;
                if (_state == -1 && _breakoutConfirmed && !_retestLeftZone && bar.High >= orbLow + retestW)
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

                // Close confirmation: a bar CLOSED above/below ORB boundary.
                // This is a market fact — set unconditionally so the tick path
                // can fire even if the bar-level entry was blocked by readiness.
                if (_state == 2 && bar.Close > orbHigh)
                    _retestCloseConfirmed = true;
                if (_state == -2 && bar.Close < orbLow)
                    _retestCloseConfirmed = true;

                // Entry from retest state (bar-level)
                if (isReady && _state == 2 && _retestCloseConfirmed)
                {
                    if (_cfg.OrderType == "Market")
                    {
                        // Defer entry to the next bar's Open for a realistic fill.
                        // Limit orders still enter at orbHigh (fill via EvaluateFills).
                        _enterNextBarOpen = true;
                        _enterNextBarDirection = true;
                        _signalBar = bar;  // snapshot signal bar for deferred BarHL stop
                    }
                    else
                        TryEntry(orbHigh, true, orb, bar.Time);
                }
                else if (isReady && _state == -2 && _retestCloseConfirmed)
                {
                    if (_cfg.OrderType == "Market")
                    {
                        _enterNextBarOpen = true;
                        _enterNextBarDirection = false;
                        _signalBar = bar;  // snapshot signal bar for deferred BarHL stop
                    }
                    else
                        TryEntry(orbLow, false, orb, bar.Time);
                }
            }

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
        // AND after a bar has CLOSED above/below ORB High/Low (_retestCloseConfirmed).
        // State ±1 = armed but hasn't left/returned to zone yet — must NOT enter.
        // Without _retestCloseConfirmed, a wick that briefly touches ORB High could
        // trigger entry even though no bar confirmed the close — producing false
        // entries in downtrends.
        bool isLongArm = _state > 0;
        bool tickReady = isLongArm ? _longCount < _cfg.EffectiveMaxLong : _shortCount < _cfg.EffectiveMaxShort;
        bool retestConfirmed = _state == 2 || _state == -2;
        if (!_inTrade && retestConfirmed && _retestCloseConfirmed && tickReady
            && !_cfg.IsAggressive && !_cfg.IsSmartAggressive)
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

        // Calculate levels from ORIGINAL entry (before offset) so partial/target are based
        // on the true signal price, not the artificially nudged entry.
        var (sl, tp, pp, rr) = LevelCalculator.CalcLevelsB(ep, isLong,
            _cfg.TargetPct, _cfg.PartialPct, orb.Range, _cfg.StopPct, _cfg.TickSize);

        // Apply entry tick offset to entry price only
        if (_cfg.EntryTickOffset != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffset * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + offset : ep - offset, _cfg.TickSize);
        }

        // Hard guard: retest entry must not be inside the ORB range
        // (prevents entries that haven't crossed the boundary, regardless of mode/offset)
        if (isLong && ep < orb.High) return;
        if (!isLong && ep > orb.Low) return;

        // MaxEntrySlippage: reject if entry price is too far past the ORB boundary
        // (e.g. gap-open beyond the retest zone). Measured as % of ORB range.
        if (_cfg.MaxEntrySlippage > 0 && orb.Range > 0)
        {
            decimal maxSlip = orb.Range * _cfg.MaxEntrySlippage;
            decimal slip = isLong ? ep - orb.High : orb.Low - ep;
            if (slip > maxSlip) return;
        }

        // Keep OrbPct stop as an upper-risk cap for alternative stop modes.
        decimal orbPctSl   = sl;
        decimal orbPctRisk = Math.Abs(ep - orbPctSl);

        // BarHL stop mode: use high/low of the SIGNAL bar ± 1 tick.
        // For same-bar entries (Aggressive, Limit), _signalBar is null and _prevBar = current bar.
        // For deferred entries (Conservative Market, SmartAggressive), _signalBar holds the arm/confirm bar
        // (not the entry bar) so the stop reflects the bar that triggered the setup.
        var stopBar = _signalBar ?? _prevBar;
        if (_cfg.StopMode == "BarHL" && stopBar != null)
        {
            sl = isLong
                ? LevelCalculator.RoundToTick(stopBar.Low  - _cfg.TickSize, _cfg.TickSize)
                : LevelCalculator.RoundToTick(stopBar.High + _cfg.TickSize, _cfg.TickSize);
            // Sanity: stop must be on the protective side of entry.
            // Long → sl < ep, Short → sl > ep. Otherwise fall back to OrbPct.
            if ((isLong && sl >= ep) || (!isLong && sl <= ep)) sl = orbPctSl;
            // Cap: if BarHL would be wider than OrbPct, fall back to OrbPct
            else if (Math.Abs(ep - sl) > orbPctRisk) sl = orbPctSl;
            decimal risk   = Math.Abs(ep - sl);
            decimal reward = Math.Abs(tp - ep);
            rr = risk > 0 ? reward / risk : 0;
        }
        // Vwap stop mode: stop at VWAP ± N ticks
        else if (_cfg.StopMode == "Vwap" && _lastVwap > 0)
        {
            sl = isLong
                ? LevelCalculator.RoundToTick(_lastVwap - _cfg.StopVwapTicks * _cfg.TickSize, _cfg.TickSize)
                : LevelCalculator.RoundToTick(_lastVwap + _cfg.StopVwapTicks * _cfg.TickSize, _cfg.TickSize);
            // Sanity: stop must be on the protective side of entry.
            if ((isLong && sl >= ep) || (!isLong && sl <= ep)) sl = orbPctSl;
            // Cap: if Vwap would be wider than OrbPct, fall back to OrbPct
            else if (Math.Abs(ep - sl) > orbPctRisk) sl = orbPctSl;
            decimal risk   = Math.Abs(ep - sl);
            decimal reward = Math.Abs(tp - ep);
            rr = risk > 0 ? reward / risk : 0;
        }

        if (rr < _cfg.MinRr) return;

        int contracts = CalcContracts();

        // Max trade risk filter: skip if dollar risk exceeds limit (0 = disabled)
        if (_cfg.MaxTradeRisk > 0)
        {
            decimal risk = Math.Abs(ep - sl) * _cfg.PointValue * contracts;
            if (risk > _cfg.MaxTradeRisk) return;
        }

        _pendingEntry = new EntrySignal(
            _cfg.SetupId,
            isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, contracts, time,
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe, Mode: _cfg.Mode,
            AutoTrailStopLoss: _cfg.AutoTrail?.Enabled == true ? LevelCalculator.RoundToTick(_cfg.AutoTrail.StopLoss * orb.Range, _cfg.TickSize) : null,
            AutoTrailTrigger:  _cfg.AutoTrail?.Enabled == true ? (_cfg.AutoTrail.Trigger.HasValue ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Trigger.Value * orb.Range, _cfg.TickSize) : null) : null,
            AutoTrailFreq:     _cfg.AutoTrail?.Enabled == true ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Freq * orb.Range, _cfg.TickSize) : null);

        if (isLong) { _longCount++; _bullTraded = true; }
        else        { _shortCount++; _bearTraded = true; }
        _tradeCount = _longCount + _shortCount;
        _state = 0;
        _signalBar = null;  // consumed — next trade sets its own signal bar
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
