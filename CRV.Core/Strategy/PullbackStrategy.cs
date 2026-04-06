// CRV.Core/Strategy/PullbackStrategy.cs
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

/// <summary>
/// Setup A — ORB Pullback strategy extracted from OrbStrategyEngine.
/// Pure signal generator: emits EntrySignal only. Trade lifecycle managed by BrokerEventHandler.
/// State machine: 0=idle, 1=armed LONG, -1=armed SHORT.
/// </summary>
public class PullbackStrategy : ISetupStrategy
{
    // ── Config ────────────────────────────────────────────────────
    private StrategySetupConfig _cfg;

    // ── State machine ─────────────────────────────────────────────
    // 0=idle, 1=armed LONG, -1=armed SHORT
    private int     _state       = 0;
    private decimal _armEntry    = 0;   // bar.Open at time of arming

    // ── Re-arm guard (prevents re-entering same side until price leaves ORB zone) ──
    private bool _bullTraded = false;
    private bool _bearTraded = false;
    private bool _pastCutoff = false;

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

    public PullbackStrategy(StrategySetupConfig cfg)
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
        // Revert entered state back to armed: state was set to 0 by TryEntry
        // Since TryEntry resets _state=0, and the entry is being reverted,
        // we can't recover the armed direction. The entry is simply dropped.
    }

    public void Reset()
    {
        _state     = 0; _armEntry  = 0;
        _bullTraded = false; _bearTraded = false;
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
        _pastCutoff = false;
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
        decimal orbRange = orb.Range;
        decimal tickTol  = _cfg.TickSize * 2;

        // Cancel arm if price leaves valid zone
        if ((_state == 1 && bar.Close < orbLow) || (_state == -1 && bar.Close > orbHigh))
        {
            _state = 0; _armEntry = 0;
        }

        bool longReady  = _longCount  < _cfg.EffectiveMaxLong;
        bool shortReady = _shortCount < _cfg.EffectiveMaxShort;
        bool isReady    = longReady || shortReady;

        // Arm logic (only when idle)
        if (isReady && _state == 0)
        {
            decimal nearDist = orbRange * _cfg.NearPct;

            // Clear directional lock once price leaves the arm zone
            if (_bullTraded && bar.High < orbHigh - nearDist) _bullTraded = false;
            if (_bearTraded && bar.Low  > orbLow  + nearDist) _bearTraded = false;

            bool vwapReady  = _cfg.UseVwap && ind.Vwap > 0;
            bool aboveVwap  = !vwapReady || bar.Close > ind.Vwap;
            bool belowVwap  = !vwapReady || bar.Close < ind.Vwap;
            bool orbLongOk  = !_cfg.UseOrbClose || orb.BullClose;
            bool orbShortOk = !_cfg.UseOrbClose || orb.BearClose;

            // Breakout detection: wick-based (default) or close-confirmation (fakeout filter)
            // Close confirmation requires the bar to CLOSE beyond the ORB boundary, not just wick.
            // A wick that closes back inside the range is a fakeout — don't arm it.
            bool longBreak  = _cfg.UseCloseConfirmation
                ? bar.Close >= orbHigh
                : bar.High  >= orbHigh - nearDist;
            bool shortBreak = _cfg.UseCloseConfirmation
                ? bar.Close <= orbLow
                : bar.Low   <= orbLow  + nearDist;

            if (longReady  && longBreak  && orbLongOk  && aboveVwap && !_bullTraded)
            {
                _state = 1; _armEntry = bar.Open;
            }
            else if (shortReady && shortBreak && orbShortOk && belowVwap && !_bearTraded)
            {
                _state = -1; _armEntry = bar.Open;
            }
        }

        // Bar-level entry (conservative or aggressive)
        if ((_state == 1 && longReady) || (_state == -1 && shortReady))
        {
            bool isLong = _state == 1;
            decimal pbPts   = orbRange * _cfg.PullbackPct;
            decimal longPb  = LevelCalculator.RoundToTick(orbHigh - pbPts, _cfg.TickSize);
            decimal shortPb = LevelCalculator.RoundToTick(orbLow  + pbPts, _cfg.TickSize);

            if (_cfg.IsAggressive)
            {
                TryEntry(_armEntry, isLong, orb, bar.Time);
            }
            else
            {
                if (isLong  && bar.Low  <= longPb  + tickTol)
                    TryEntry(longPb,  true,  orb, bar.Time);
                else if (!isLong && bar.High >= shortPb - tickTol)
                    TryEntry(shortPb, false, orb, bar.Time);
            }
        }
    }

    // ── OnTick ──────────────────────────────────────────────────────
    public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        if (!_cfg.Enabled) return;
        if (!orb.IsSet || orb.Range <= 0) return;

        decimal orbRange = orb.Range;
        decimal tickTol  = _cfg.TickSize * 2;

        // Entry: armed but not in trade
        bool isLong = _state == 1;
        bool tickReady = isLong ? _longCount < _cfg.EffectiveMaxLong : _shortCount < _cfg.EffectiveMaxShort;
        if (!_inTrade && IsArmed && tickReady)
        {
            // Pullback entry: price must be inside the ORB range
            if (price <= orb.Low || price >= orb.High) return;

            if (_cfg.IsAggressive)
            {
                TryEntry(_armEntry, isLong, orb, utc);
            }
            else
            {
                decimal pbPts   = orbRange * _cfg.PullbackPct;
                decimal longPb  = LevelCalculator.RoundToTick(orb.High - pbPts, _cfg.TickSize);
                decimal shortPb = LevelCalculator.RoundToTick(orb.Low  + pbPts, _cfg.TickSize);

                if (isLong  && price <= longPb  + tickTol)
                    TryEntry(longPb,  true,  orb, utc);
                else if (!isLong && price >= shortPb - tickTol)
                    TryEntry(shortPb, false, orb, utc);
            }
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
        if (_inTrade) return; // already in trade

        // Pullback entry must be inside the ORB range (between orbLow and orbHigh)
        if (isLong && (ep <= orb.Low || ep >= orb.High)) return;
        if (!isLong && (ep <= orb.Low || ep >= orb.High)) return;

        // Apply entry tick offset
        if (_cfg.EntryTickOffset != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffset * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + offset : ep - offset, _cfg.TickSize);
        }

        var (sl, tp, pp, rr) = LevelCalculator.CalcLevels(ep, isLong,
            _cfg.StopPct, _cfg.TargetPct, _cfg.PartialPct, orb.Range, _cfg.TickSize);

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
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe, Mode: _cfg.Mode,
            AutoTrailStopLoss: _cfg.AutoTrail?.Enabled == true ? LevelCalculator.RoundToTick(_cfg.AutoTrail.StopLoss * orb.Range, _cfg.TickSize) : null,
            AutoTrailTrigger:  _cfg.AutoTrail?.Enabled == true ? (_cfg.AutoTrail.Trigger.HasValue ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Trigger.Value * orb.Range, _cfg.TickSize) : null) : null,
            AutoTrailFreq:     _cfg.AutoTrail?.Enabled == true ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Freq * orb.Range, _cfg.TickSize) : null);

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
