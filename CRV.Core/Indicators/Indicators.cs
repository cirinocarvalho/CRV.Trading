namespace CRV.Core.Indicators;

using CRV.Core.Models;
using Microsoft.Extensions.Logging;

// ── ATR — Wilder smoothing ────────────────────────────────────
public class AtrIndicator
{
    private readonly int _period;
    private readonly Queue<decimal> _trs = new();
    private decimal _prevClose;
    private decimal _atr;
    private bool _seeded;

    public AtrIndicator(int period = 14) => _period = period;
    /// <summary>Returns running average of TRs during warmup, then Wilder ATR once seeded.</summary>
    public decimal Value   => _trs.Count > 0 && _trs.Count < _period ? _trs.Average() : _atr;
    public bool    IsReady => _trs.Count >= _period;
    /// <summary>True after at least one bar (returns partial ATR from running TR average).</summary>
    public bool    HasValue => _trs.Count > 0;

    public decimal Update(Bar bar)
    {
        decimal tr = _seeded
            ? Math.Max(bar.High - bar.Low,
              Math.Max(Math.Abs(bar.High - _prevClose),
                       Math.Abs(bar.Low  - _prevClose)))
            : bar.High - bar.Low;

        _prevClose = bar.Close;
        _seeded    = true;
        _trs.Enqueue(tr);

        if (_trs.Count < _period) return Value;
        if (_trs.Count == _period) { _atr = _trs.Average(); return _atr; }
        _atr = (_atr * (_period - 1) + tr) / _period;
        _trs.Dequeue();
        return _atr;
    }

    public void Reset() { _trs.Clear(); _seeded = false; _atr = 0; }
}

// ── EMA(21) — incremental with SMA seed ─────────────────────
public class Ema21Indicator
{
    private const int Period = 21;
    private decimal _ema;
    private decimal _sum;
    private int _count;

    public decimal Value   => _count > 0 && _count < Period ? _sum / _count : _ema;
    public bool    IsReady => _count >= Period;

    /// <summary>True after at least one bar has been fed (returns running SMA before full EMA).</summary>
    public bool    HasValue => _count > 0;

    public decimal Update(decimal close)
    {
        _count++;
        if (_count <= Period)
        {
            _sum += close;
            if (_count == Period)
                _ema = _sum / Period;
            return Value;
        }
        decimal k = 2m / (Period + 1);
        _ema = (close - _ema) * k + _ema;
        return _ema;
    }

    public void Reset() { _ema = 0; _sum = 0; _count = 0; }
}

// ── VWAP — resets per trading session ─────────────────────────
public class VwapIndicator
{
    private decimal _cumTPV;
    private decimal _cumVol;
    private DateTime _lastTradingDate = DateTime.MinValue;

    public decimal Value   => _cumVol > 0 ? _cumTPV / _cumVol : 0;
    public bool    IsReady => _cumVol > 0;

    /// <summary>
    /// Update VWAP with a new bar. Pass the session-aware trading date
    /// (from <see cref="StrategyConfig.TradingDate"/>) so VWAP resets
    /// at futures session boundaries (e.g. 6 PM ET) instead of midnight.
    /// </summary>
    public decimal Update(Bar bar, DateTime tradingDate)
    {
        if (tradingDate != _lastTradingDate) { _cumTPV = 0; _cumVol = 0; _lastTradingDate = tradingDate; }
        _cumTPV += bar.HLC3 * bar.Volume;
        _cumVol += bar.Volume;
        return Value;
    }

    /// <summary>Legacy overload — uses calendar date. Kept for backtest compatibility.</summary>
    public decimal Update(Bar bar, TimeZoneInfo tz)
    {
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, tz).Date;
        return Update(bar, localDate);
    }

    /// <summary>
    /// Signals the start of a new trading session without adding bar data.
    /// Resets accumulation AND updates _lastTradingDate so the next Update()
    /// call does not double-reset. Call this when a new session is detected on
    /// an unconfirmed bar (L1 tick) so the dashboard shows VWAP = 0 immediately
    /// at the session boundary rather than waiting for the first confirmed bar.
    /// </summary>
    public void NewSession(DateTime tradingDate) { _cumTPV = 0; _cumVol = 0; _lastTradingDate = tradingDate; }

    public void Reset() { _cumTPV = 0; _cumVol = 0; }
}

// ── ORB — captures high/low of opening range ─────────────────
public class OrbCalculator
{
    private TimeOnly _orbStart;
    private TimeOnly _orbEnd;
    private readonly string   _timezone;
    private readonly int      _tfMinutes;   // execution TF — used for overlap detection

    private decimal  _high;
    private decimal  _low = decimal.MaxValue;
    private bool     _active;
    private bool     _isSet;
    private DateTime _lastTradingDate = DateTime.MinValue;
    private decimal  _closeRelPct;

    public OrbCalculator(TimeOnly orbStart, TimeOnly orbEnd, string timezone, int tfMinutes = 1, ILogger? log = null)
    {
        _orbStart  = orbStart;
        _orbEnd    = orbEnd;
        _timezone  = timezone;
        _tfMinutes = Math.Max(1, tfMinutes);
    }

    public decimal OrbHigh     => _high;
    public decimal OrbLow      => _low == decimal.MaxValue ? 0 : _low;
    public decimal OrbMid      => (_high + OrbLow) / 2m;
    public decimal OrbRange    => _high - OrbLow;
    public bool    IsSet       => _isSet;
    public bool    OrbBullClose => _closeRelPct >= 0.70m;
    public bool    OrbBearClose => _closeRelPct <= 0.30m;
    public decimal CloseRelPct  => _closeRelPct;

    /// <summary>
    /// Restore ORB state from cache (used after engine restart to avoid
    /// re-deriving from potentially incorrect REST backfill data).
    /// </summary>
    public void Restore(decimal high, decimal low, decimal closeRelPct, DateTime tradingDate)
    {
        _high             = high;
        _low              = low;
        _closeRelPct      = closeRelPct;
        _isSet            = true;
        _active           = false;
        _lastTradingDate  = tradingDate;   // prevent session-reset from wiping restored state
    }

    /// <summary>
    /// Seed ORB high/low as a floor when restarting inside the ORB window.
    /// Keeps the ORB in "forming" state — subsequent bars can only expand the range.
    /// Must set _lastTradingDate to prevent the first bar's date-change guard from wiping the seed.
    /// </summary>
    public void SeedFloor(decimal high, decimal low, DateTime tradingDate)
    {
        _high   = Math.Max(_high, high);
        _low    = (_low == decimal.MaxValue) ? low : Math.Min(_low, low);
        _active = true;   // still forming
        _isSet  = false;  // not finalized yet
        _lastTradingDate = tradingDate;
    }

    /// <summary>Update the ORB window times for a new session.</summary>
    public void Reconfigure(TimeOnly orbStart, TimeOnly orbEnd)
    {
        _orbStart = orbStart;
        _orbEnd   = orbEnd;
    }

    /// <summary>Clear all ORB state (high/low/formed). Does NOT affect ATR or other indicators.</summary>
    public void Reset()
    {
        _high             = 0;
        _low              = decimal.MaxValue;
        _active           = false;
        _isSet            = false;
        _closeRelPct      = 0;
        _lastTradingDate  = DateTime.MinValue;
    }

    /// <summary>
    /// Update ORB with a new bar. Pass the session-aware trading date
    /// (from <see cref="StrategyConfig.TradingDate"/>) so ORB resets
    /// at futures session boundaries (e.g. 6 PM ET) instead of midnight.
    /// </summary>
    public void Update(Bar bar, DateTime tradingDate)
    {
        var tz    = GetTz(_timezone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, tz);
        var time  = TimeOnly.FromDateTime(local);

        if (tradingDate != _lastTradingDate)
        {
            _lastTradingDate = tradingDate;
            _high = 0; _low = decimal.MaxValue;
            _active = false; _isSet = false; _closeRelPct = 0;
        }

        // A bar's period is [time, time + tfMinutes).
        // It overlaps the ORB window [OrbStart, OrbEnd) when:
        //   barOpen < OrbEnd  AND  barClose > OrbStart
        // This correctly handles large TF bars (e.g. 60-min bar opening at 9:00
        // when ORB=9:30–10:00) that span the ORB window even though barOpen < OrbStart.
        var barEnd = time.AddMinutes(_tfMinutes);
        bool overlapsOrb = time < _orbEnd && barEnd > _orbStart;

        if (overlapsOrb)
        {
            // De-freeze: if _isSet was prematurely set (e.g. bar ordering race)
            // but a bar still overlaps the ORB window, revert to accumulating.
            if (_isSet) { _isSet = false; _active = true; }

            _active = true;
            if (_high == 0) { _high = bar.High; _low = bar.Low; }
            else { _high = Math.Max(_high, bar.High); _low = Math.Min(_low, bar.Low); }
        }
        else if (_active && !_isSet && time >= _orbEnd)
        {
            // First bar fully after ORB closed — finalize.
            // The time >= _orbEnd guard prevents premature freeze when bars
            // arrive out of order (e.g. unconfirmed next bar before confirmed current bar).
            _isSet = true;
            _active = false;
            if (OrbRange > 0)
                _closeRelPct = (bar.Close - OrbLow) / OrbRange;
        }
    }

    /// <summary>Legacy overload — uses calendar date. Kept for backtest compatibility.</summary>
    public void Update(Bar bar)
    {
        var tz    = GetTz(_timezone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, tz);
        Update(bar, local.Date);
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
