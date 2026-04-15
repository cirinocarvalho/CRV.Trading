// CRV.Core/Strategy/SessionFakeoutStrategy.cs
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

/// <summary>
/// Setup D — Session Range False Breakout strategy extracted from OrbStrategyEngine.
/// Pure signal generator: emits EntrySignal only. Trade lifecycle managed by BrokerEventHandler.
/// Arms when FalseBreakoutDetector's SessionRangeTracker activates
/// (via ModuleState.SessionFakeoutBull/Bear).
/// Direction is OPPOSITE of breakout: bull fakeout -> short, bear fakeout -> long.
/// Entry level uses session range high/low instead of ORB.
/// Range size for stop/target calculation = SessionRangeHigh - SessionRangeLow.
/// State machine: 0=idle, 1=armed LONG, -1=armed SHORT.
/// </summary>
public class SessionFakeoutStrategy : ISetupStrategy
{
    // ── Config ────────────────────────────────────────────────────
    private StrategySetupConfig _cfg;

    // ── State machine ─────────────────────────────────────────────
    // 0=idle, 1=armed LONG, -1=armed SHORT
    private int     _state       = 0;
    private decimal _armEntry    = 0;   // bar.Close at time of arming

    // ── Re-arm guard (prevents re-entering same side after stop) ──
    private bool _bullTraded = false;
    private bool _bearTraded = false;
    private bool _pastCutoff = false;

    // ── Counters ──────────────────────────────────────────────────
    private int     _tradeCount  = 0;

    // ── Win/loss stats ────────────────────────────────────────────
    private int     _wins        = 0;
    private int     _losses      = 0;
    private decimal _winPnl      = 0;
    private decimal _lossPnl     = 0;

    // ── Last ATR ratio (cached from OrbState.AtrRatio when trade enters) ──
    private decimal _lastAtrRatio = 0;

    // ── Stop-mode state ──────────────────────────────────────────
    private Bar?    _prevBar  = null;  // previous bar for BarHL stop mode
    private decimal _lastVwap = 0;     // latest VWAP for Vwap stop mode

    // ── Pending signals ───────────────────────────────────────────
    private EntrySignal? _pendingEntry = null;

    public SessionFakeoutStrategy(StrategySetupConfig cfg)
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
    public bool         IsArmed      => _state == 1 || _state == -1;
    private bool        _inTrade;
    public bool         InTrade      => _inTrade;
    public void SetInTrade(bool active)
    {
        _inTrade = active;
        if (!active) _state = 0;
    }
    public void SeedTradeCount(int longs, int shorts)
    {
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
        _tradeCount = 0;
        _wins = 0; _losses = 0; _winPnl = 0; _lossPnl = 0;
        _lastAtrRatio = 0;
        _prevBar = null; _lastVwap = 0;
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
        _pastCutoff = false;
        _tradeCount = 0;
        _lastAtrRatio = 0;
        _prevBar = null; _lastVwap = 0;
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
        // Setup D uses session range, not ORB — but we still need ORB to be set
        // so the engine is past ORB formation phase.
        if (!orb.IsSet || orb.Range <= 0) return;

        _lastAtrRatio = orb.AtrRatio;
        _lastVwap     = indicators.Vwap;
        // Set before ProcessArm so BarHL stops reference the SIGNAL bar
        // (the bar triggering the entry), not the bar preceding it.
        _prevBar      = bar;

        ProcessArm(bar, orb, indicators, modules);
    }

    private void ProcessArm(Bar bar, OrbState orb, IndicatorState ind, ModuleState modules)
    {
        if (_inTrade) return;

        bool isReady = _tradeCount < _cfg.MaxTrades;

        // Arm logic: check for session range fakeout activation
        if (isReady && _state == 0)
        {
            bool fakeoutBull = modules.SessionFakeoutBull;
            bool fakeoutBear = modules.SessionFakeoutBear;

            // Direction is OPPOSITE of breakout: bull fakeout -> arm SHORT, bear fakeout -> arm LONG
            if (fakeoutBull && !_bearTraded)
            {
                _state = -1;  // arm SHORT (fade the long breakout)
                _armEntry = bar.Close;
            }
            else if (fakeoutBear && !_bullTraded)
            {
                _state = 1;   // arm LONG (fade the short breakout)
                _armEntry = bar.Close;
            }
        }

        // Bar-level entry: armed strategies generate entry signal
        if (IsArmed)
        {
            bool isLong = _state == 1;
            TryEntry(isLong ? modules.SessionRangeLow : modules.SessionRangeHigh, isLong, orb, bar.Time, modules);
        }
    }

    // ── OnTick ──────────────────────────────────────────────────────
    public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        if (!_cfg.Enabled) return;
        if (!orb.IsSet || orb.Range <= 0) return;

        // Entry: armed but not in trade
        if (!_inTrade && IsArmed)
        {
            bool isLong = _state == 1;
            bool tickReady = _tradeCount < _cfg.MaxTrades;
            if (!tickReady) return;
            var srLow  = modules.SessionRangeLow;
            var srHigh = modules.SessionRangeHigh;
            // Long: price crosses above session range low (after false break below)
            // Short: price crosses below session range high (after false break above)
            if (isLong && price >= srLow)
                TryEntry(price, true, orb, utc, modules);
            else if (!isLong && price <= srHigh)
                TryEntry(price, false, orb, utc, modules);
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

    private void TryEntry(decimal ep, bool isLong, OrbState orb, DateTime time, ModuleState modules)
    {
        if (_inTrade) return; // already in trade

        decimal rangeSize = modules.SessionRangeHigh - modules.SessionRangeLow;
        if (rangeSize <= 0) rangeSize = orb.Range;

        // Apply entry tick offset
        if (_cfg.EntryTickOffset != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffset * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + offset : ep - offset, _cfg.TickSize);
        }

        var (sl, tp, pp, _) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPct, _cfg.TargetPct, _cfg.PartialPct, rangeSize, _cfg.TickSize);

        // Keep OrbPct (session-range-pct) stop as an upper-risk cap for alternative modes.
        decimal orbPctSl   = sl;
        decimal orbPctRisk = Math.Abs(ep - orbPctSl);

        // BarHL stop mode: use high/low of bar before entry ± 1 tick
        if (_cfg.StopMode == "BarHL" && _prevBar != null)
        {
            sl = isLong
                ? LevelCalculator.RoundToTick(_prevBar.Low  - _cfg.TickSize, _cfg.TickSize)
                : LevelCalculator.RoundToTick(_prevBar.High + _cfg.TickSize, _cfg.TickSize);
            // Sanity: stop must be on the protective side of entry.
            // Long → sl < ep, Short → sl > ep. Otherwise fall back to OrbPct.
            if ((isLong && sl >= ep) || (!isLong && sl <= ep)) sl = orbPctSl;
            // Cap: if BarHL would be wider than StopPct * sessionRange, fall back
            else if (Math.Abs(ep - sl) > orbPctRisk) sl = orbPctSl;
        }
        // Vwap stop mode: stop at VWAP ± N ticks
        else if (_cfg.StopMode == "Vwap" && _lastVwap > 0)
        {
            sl = isLong
                ? LevelCalculator.RoundToTick(_lastVwap - _cfg.StopVwapTicks * _cfg.TickSize, _cfg.TickSize)
                : LevelCalculator.RoundToTick(_lastVwap + _cfg.StopVwapTicks * _cfg.TickSize, _cfg.TickSize);
            // Sanity: stop must be on the protective side of entry.
            if ((isLong && sl >= ep) || (!isLong && sl <= ep)) sl = orbPctSl;
            // Cap: if Vwap would be wider than StopPct * sessionRange, fall back
            else if (Math.Abs(ep - sl) > orbPctRisk) sl = orbPctSl;
        }

        decimal risk   = Math.Abs(ep - sl);
        decimal reward = Math.Abs(tp - ep);
        decimal rr     = risk > 0 ? reward / risk : 0;
        if (rr < _cfg.MinRr) return;

        int contracts = CalcContracts();

        // Max trade risk filter: skip if dollar risk exceeds limit (0 = disabled)
        if (_cfg.MaxTradeRisk > 0)
        {
            decimal tradeRisk = Math.Abs(ep - sl) * _cfg.PointValue * contracts;
            if (tradeRisk > _cfg.MaxTradeRisk) return;
        }

        _pendingEntry = new EntrySignal(
            _cfg.SetupId,
            isLong ? Direction.Long : Direction.Short,
            ep, sl, tp, pp, contracts, time,
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe,
            AutoTrailStopLoss: _cfg.AutoTrail?.Enabled == true ? LevelCalculator.RoundToTick(_cfg.AutoTrail.StopLoss * orb.Range, _cfg.TickSize) : null,
            AutoTrailTrigger:  _cfg.AutoTrail?.Enabled == true ? (_cfg.AutoTrail.Trigger.HasValue ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Trigger.Value * orb.Range, _cfg.TickSize) : null) : null,
            AutoTrailFreq:     _cfg.AutoTrail?.Enabled == true ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Freq * orb.Range, _cfg.TickSize) : null);

        _tradeCount++;
        if (isLong) _bullTraded = true; else _bearTraded = true;
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
