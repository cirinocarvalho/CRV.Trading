using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

/// <summary>
/// EMA21 cross/touch strategy. Self-contained: owns its own EMA(21), ATR(14),
/// and VolumeSMA(20) indicators. Ignores OrbState/IndicatorState/ModuleState.
/// Emits EntrySignal with ATR-based targets and EMA-at-entry as stop.
/// </summary>
public sealed class Ema21Strategy : ISetupStrategy
{
    private StrategySetupConfig _cfg;

    // ── Internal indicators ──────────────────────────────────────
    private const int EmaPeriod = 21;
    private const int AtrPeriod = 14;
    private const int VolSmaPeriod = 20;

    // EMA state
    private readonly decimal[] _emaHistory;   // ring buffer for slope lookback
    private int _emaHistIdx;
    private int _emaHistCount;
    private decimal _ema;
    private decimal _emaSum;                  // for SMA seed
    private int _emaBarCount;
    private bool _emaReady;

    // ATR state (Wilder)
    private decimal _atr;
    private decimal _prevClose;
    private int _atrBarCount;
    private decimal _atrSum;                  // for seed
    private bool _atrReady;
    private bool _atrSeeded;

    // Volume SMA state
    private readonly long[] _volHistory;      // ring buffer
    private int _volHistIdx;
    private int _volHistCount;
    private long _volSum;

    // Previous bar data (for cross detection + touch candle checks)
    private decimal _prevBarClose;
    private decimal _prevBarHigh;
    private decimal _prevBarLow;
    private decimal _prevEma;
    private bool _hasPrevBar;

    // ── State machine ────────────────────────────────────────────
    // 0=flat, 1=armed LONG, -1=armed SHORT
    private int _state;
    private bool _bullTraded;
    private bool _bearTraded;
    private bool _pastCutoff;
    private int _tradeCount;
    private int _longCount;
    private int _shortCount;

    // ── Win/loss stats ───────────────────────────────────────────
    private int _wins;
    private int _losses;
    private decimal _winPnl;
    private decimal _lossPnl;

    // ── Trade state ──────────────────────────────────────────────
    private bool _inTrade;
    private EntrySignal? _pendingEntry;

    public Ema21Strategy(StrategySetupConfig cfg)
    {
        _cfg = cfg;
        _emaHistory = new decimal[Math.Max(cfg.SlopeLen + 1, 2)];
        _volHistory = new long[VolSmaPeriod];
    }

    // ── ISetupStrategy identity ──────────────────────────────────
    public string       Id           => _cfg.Id;
    public SetupId      SetupId      => _cfg.SetupId;
    public StrategyType StrategyType => _cfg.StrategyType;
    public string       Name         => _cfg.Name;
    public string       Ticker       => _cfg.Ticker;
    public decimal      PointValue   => _cfg.PointValue;
    public bool         UseEmaFilter => _cfg.UseEmaFilter;
    public bool         IsActive     => _inTrade;
    public bool         IsArmed      => _state != 0;
    public bool         InTrade      => _inTrade;
    public void SetInTrade(bool active)
    {
        _inTrade = active;
        if (!active) _state = 0;
    }
    public void SeedTradeCount(int longs, int shorts)
    {
        _longCount = longs;
        _shortCount = shorts;
        _tradeCount = longs + shorts;
    }
    public int CutoffHour   => _cfg.CutoffHour;
    public int CutoffMinute => _cfg.CutoffMinute;
    public (int Hour, int Minute) GetCutoffForSession(string s) => _cfg.GetCutoffForSession(s);
    public bool IsEnabledForSession(string s) => _cfg.IsEnabledForSession(s);

    public EntrySignal? PendingEntry => _pendingEntry;
    public void ClearPendingSignals() => _pendingEntry = null;

    public void Reconfigure(StrategySetupConfig config) => _cfg = config;

    public void RevertEntry()
    {
        _pendingEntry = null;
    }

    // ── Reset (new trading day) — clears everything including indicators ──
    public void Reset()
    {
        _state = 0; _bullTraded = false; _bearTraded = false;
        _pastCutoff = false;
        _tradeCount = 0; _longCount = 0; _shortCount = 0;
        _wins = 0; _losses = 0; _winPnl = 0; _lossPnl = 0;
        _inTrade = false;
        _pendingEntry = null;

        // Reset indicators
        _ema = 0; _emaSum = 0; _emaBarCount = 0; _emaReady = false;
        _emaHistIdx = 0; _emaHistCount = 0;
        Array.Clear(_emaHistory);

        _atr = 0; _prevClose = 0; _atrBarCount = 0; _atrSum = 0;
        _atrReady = false; _atrSeeded = false;
        Array.Clear(_volHistory);
        _volHistIdx = 0; _volHistCount = 0; _volSum = 0;

        _prevBarClose = 0; _prevBarHigh = 0; _prevBarLow = 0;
        _prevEma = 0; _hasPrevBar = false;
    }

    // ── ResetSession — clears trade state, preserves indicators ──
    public void ResetSession()
    {
        _state = 0; _bullTraded = false; _bearTraded = false;
        _pastCutoff = false;
        _tradeCount = 0; _longCount = 0; _shortCount = 0;
        _inTrade = false;
        _pendingEntry = null;
        // Note: indicators, _wins/_losses/_winPnl/_lossPnl PRESERVED
    }

    public void ResetTradeCounters()
    {
        _tradeCount = 0; _longCount = 0; _shortCount = 0;
        _wins = 0; _losses = 0; _winPnl = 0; _lossPnl = 0;
        _pastCutoff = false;
    }

    public void Disarm()
    {
        if (!_inTrade) { _state = 0; _pastCutoff = true; }
    }

    public void ResetCutoff() => _pastCutoff = false;

    public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd)
    {
        _pendingEntry = null;
        _state = 0;
    }

    public SetupStateSnapshot GetSnapshot() => new()
    {
        Id         = _cfg.Id,
        SetupId    = _cfg.SetupId,
        Name       = _cfg.Name,
        State      = _state,
        IsActive   = IsActive,
        IsArmed    = IsArmed,
        PastCutoff = _pastCutoff,
        TradeCount = _tradeCount,
        MaxTrades  = _cfg.MaxTrades,
        StickyTgt  = false,
        StickyStp  = false,
        Enabled    = _cfg.Enabled,
        Wins       = _wins,
        Losses     = _losses,
        WinPnl     = _winPnl,
        LossPnl    = _lossPnl,
        Expectancy = (_wins + _losses) > 0
            ? (_winPnl + _lossPnl) / (_wins + _losses) : 0m,
    };

    // ── OnTick — no tick-level logic for EMA21 (entry is bar-level) ──
    public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        // EMA21 entries fire on bar open (via OnBar), not on tick
    }

    // ── OnBar — main processing ──────────────────────────────────
    public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules)
    {
        if (!_cfg.Enabled || !bar.IsConfirmed) return;

        // 1. Update indicators
        UpdateEma(bar.Close);
        UpdateAtr(bar);
        UpdateVolSma(bar.Volume);

        // 2. If armed from previous bar, try entry at this bar's open
        if (_state != 0 && !_inTrade)
        {
            TryEntry(bar);
            if (_pendingEntry != null)
                Console.Error.WriteLine($"[EMA21] ENTRY {_pendingEntry.Direction} @ {bar.Open} stop={_pendingEntry.Stop} tp2={_pendingEntry.Tg2Price}");
        }

        // 3. Detect new signals (only when indicators are ready and flat)
        if (_emaReady && _atrReady && _hasPrevBar && _state == 0 && !_inTrade)
        {
            DetectSignals(bar);
            if (_state != 0)
                Console.Error.WriteLine($"[EMA21] ARMED {(_state > 0 ? "LONG" : "SHORT")} bar={bar.Time:HH:mm} ema={_ema:F2} atr={_atr:F2} close={bar.Close}");
        }

        // 4. Save current bar data for next bar's cross/touch detection
        _prevBarClose = bar.Close;
        _prevBarHigh = bar.High;
        _prevBarLow = bar.Low;
        _prevEma = _ema;
        _hasPrevBar = true;
    }

    // ── Indicator updates ────────────────────────────────────────

    private void UpdateEma(decimal close)
    {
        _emaBarCount++;
        if (_emaBarCount <= EmaPeriod)
        {
            _emaSum += close;
            if (_emaBarCount == EmaPeriod)
            {
                _ema = _emaSum / EmaPeriod;
                _emaReady = true;
                PushEmaHistory(_ema);
            }
        }
        else
        {
            decimal k = 2m / (EmaPeriod + 1);
            _ema = (close - _ema) * k + _ema;
            PushEmaHistory(_ema);
        }
    }

    private void PushEmaHistory(decimal ema)
    {
        _emaHistory[_emaHistIdx] = ema;
        _emaHistIdx = (_emaHistIdx + 1) % _emaHistory.Length;
        if (_emaHistCount < _emaHistory.Length) _emaHistCount++;
    }

    private decimal GetEmaAgo(int barsAgo)
    {
        if (barsAgo >= _emaHistCount) return _ema;
        int idx = (_emaHistIdx - 1 - barsAgo + _emaHistory.Length * 2) % _emaHistory.Length;
        return _emaHistory[idx];
    }

    private void UpdateAtr(Bar bar)
    {
        decimal tr;
        if (!_atrSeeded)
        {
            tr = bar.High - bar.Low;
            _prevClose = bar.Close;
            _atrSeeded = true;
        }
        else
        {
            tr = Math.Max(bar.High - bar.Low,
                 Math.Max(Math.Abs(bar.High - _prevClose),
                          Math.Abs(bar.Low - _prevClose)));
            _prevClose = bar.Close;
        }

        _atrBarCount++;
        if (_atrBarCount <= AtrPeriod)
        {
            _atrSum += tr;
            if (_atrBarCount == AtrPeriod)
            {
                _atr = _atrSum / AtrPeriod;
                _atrReady = true;
            }
        }
        else
        {
            _atr = (_atr * (AtrPeriod - 1) + tr) / AtrPeriod;
        }
    }

    private void UpdateVolSma(long volume)
    {
        if (_volHistCount == VolSmaPeriod)
            _volSum -= _volHistory[_volHistIdx];

        _volHistory[_volHistIdx] = volume;
        _volSum += volume;
        _volHistIdx = (_volHistIdx + 1) % VolSmaPeriod;
        if (_volHistCount < VolSmaPeriod) _volHistCount++;
    }

    private bool VolumeOk(long volume)
    {
        if (!_cfg.UseVolumeFilter) return true;
        if (_volHistCount == 0) return false;
        decimal avgVol = (decimal)_volSum / _volHistCount;
        return volume > avgVol;
    }

    // ── Signal detection (runs on bar close) ─────────────────────

    private void DetectSignals(Bar bar)
    {
        if (!VolumeOk(bar.Volume)) return;

        decimal tickZone = _cfg.TickSize * _cfg.OpenTicksToEma;

        // Check max trades (directional)
        bool canLong = _longCount < _cfg.EffectiveMaxLong && _tradeCount < _cfg.MaxTrades;
        bool canShort = _shortCount < _cfg.EffectiveMaxShort && _tradeCount < _cfg.MaxTrades;

        // Cross signals
        if (canLong && _prevBarClose < _prevEma && bar.Close > _ema
            && bar.Close + tickZone > _ema)
        {
            _state = 1; // arm LONG
            return;
        }
        if (canShort && _prevBarClose > _prevEma && bar.Close < _ema
            && bar.Close - tickZone < _ema)
        {
            _state = -1; // arm SHORT
            return;
        }

        // Slope check for touch signals
        bool slopeUp = false, slopeDown = false;
        if (_emaHistCount > _cfg.SlopeLen)
        {
            decimal emaAgo = GetEmaAgo(_cfg.SlopeLen);
            decimal rawSlope = _ema - emaAgo;
            decimal slopePct = _ema > 0 ? Math.Abs(rawSlope) / _ema * 100m : 0m;
            slopeUp = rawSlope > 0 && slopePct >= _cfg.MinSlopePct;
            slopeDown = rawSlope < 0 && slopePct >= _cfg.MinSlopePct;
        }

        decimal touchUpper = _ema + _atr * _cfg.AtrTouchMult;
        decimal touchLower = _ema - _atr * _cfg.AtrTouchMult;

        // TouchBull: uptrend, bar touches EMA zone, bullish candle
        if (canLong && slopeUp && bar.Close > _ema
            && bar.Low <= touchUpper && bar.High >= touchLower
            && bar.Open > _ema - tickZone
            && bar.Close > bar.Open && bar.Close > _prevBarHigh)
        {
            _state = 1; // arm LONG
            return;
        }

        // TouchBear: downtrend, bar touches EMA zone, bearish candle
        if (canShort && slopeDown && bar.Close < _ema
            && bar.High >= touchLower && bar.Low <= touchUpper
            && bar.Open < _ema + tickZone
            && bar.Close < bar.Open && bar.Close < _prevBarLow)
        {
            _state = -1; // arm SHORT
            return;
        }
    }

    // ── Entry generation (fires on bar after signal) ─────────────

    private void TryEntry(Bar bar)
    {
        bool isLong = _state == 1;
        decimal ep = bar.Open;

        // Apply entry tick offset
        if (_cfg.EntryTickOffset != 0 && _cfg.TickSize > 0)
        {
            decimal offset = _cfg.EntryTickOffset * _cfg.TickSize;
            ep = LevelCalculator.RoundToTick(isLong ? ep + offset : ep - offset, _cfg.TickSize);
        }

        // Stop = EMA21 at entry bar
        decimal sl = LevelCalculator.RoundToTick(_ema, _cfg.TickSize);

        // Targets = ATR-based
        decimal tp1 = LevelCalculator.RoundToTick(
            isLong ? ep + _atr * _cfg.AtrTp1Mult : ep - _atr * _cfg.AtrTp1Mult,
            _cfg.TickSize);
        decimal tp2 = LevelCalculator.RoundToTick(
            isLong ? ep + _atr * _cfg.AtrTp2Mult : ep - _atr * _cfg.AtrTp2Mult,
            _cfg.TickSize);

        // R:R check
        decimal risk = Math.Abs(ep - sl);
        decimal reward = Math.Abs(tp2 - ep);
        if (risk <= 0 || (reward / risk) < _cfg.MinRr)
        {
            _state = 0;
            return;
        }

        // Contract sizing
        int contracts = Math.Min(_cfg.Contracts, _cfg.MaxContracts);

        // Max trade risk filter
        if (_cfg.MaxTradeRisk > 0)
        {
            decimal tradeRisk = risk * _cfg.PointValue * contracts;
            if (tradeRisk > _cfg.MaxTradeRisk) { _state = 0; return; }
        }

        _pendingEntry = new EntrySignal(
            _cfg.SetupId,
            isLong ? Direction.Long : Direction.Short,
            ep, sl, tp2, tp1, contracts, bar.Time,
            _cfg.OrderType, Ticker: _cfg.Ticker,
            PartialContracts: _cfg.PartialCts, PointValue: _cfg.PointValue,
            UsePartial: _cfg.UsePartial, UseBe: _cfg.UseBe,
            AutoTrailStopLoss: _cfg.AutoTrail?.Enabled == true
                ? LevelCalculator.RoundToTick(_cfg.AutoTrail.StopLoss * _atr, _cfg.TickSize) : null,
            AutoTrailTrigger: _cfg.AutoTrail?.Enabled == true && _cfg.AutoTrail.Trigger.HasValue
                ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Trigger.Value * _atr, _cfg.TickSize) : null,
            AutoTrailFreq: _cfg.AutoTrail?.Enabled == true
                ? LevelCalculator.RoundToTick(_cfg.AutoTrail.Freq * _atr, _cfg.TickSize) : null);

        _tradeCount++;
        if (isLong) { _longCount++; _bullTraded = true; }
        else { _shortCount++; _bearTraded = true; }
        _state = 0;
    }
}
