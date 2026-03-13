using CRV.Core.Models;

namespace CRV.Core.Modules;

public enum SessionType
{
    PreMarket,
    Asia,
    London,
    NYOpen,
    Midday,
    PowerHour
}

public class SessionEngine : IEngineModule
{
    private readonly ModuleConfig _cfg;
    private readonly TimeZoneInfo _tz;

    // Current session high/low (full day)
    public decimal SessionHigh { get; private set; }
    public decimal SessionLow  { get; private set; } = decimal.MaxValue;

    // Per-session tracking
    public decimal AsiaHigh   { get; private set; }
    public decimal AsiaLow    { get; private set; } = decimal.MaxValue;
    public decimal AsiaMid    => AsiaHigh > 0 && AsiaLow < decimal.MaxValue ? (AsiaHigh + AsiaLow) / 2m : 0m;
    public decimal AsiaRange  => AsiaHigh > 0 && AsiaLow < decimal.MaxValue ? AsiaHigh - AsiaLow : 0m;

    public decimal LondonHigh { get; private set; }
    public decimal LondonLow  { get; private set; } = decimal.MaxValue;

    public decimal NYHigh     { get; private set; }
    public decimal NYLow      { get; private set; } = decimal.MaxValue;

    // Previous day/week/month levels
    public decimal PDH { get; private set; }
    public decimal PDL { get; private set; }
    public decimal PWH { get; private set; }
    public decimal PWL { get; private set; }
    public decimal PMH { get; private set; }
    public decimal PML { get; private set; }

    // Cross-session signals
    public bool LondonSweptAsiaHigh { get; private set; }
    public bool LondonSweptAsiaLow  { get; private set; }
    public bool NYBullExpansion     { get; private set; }
    public bool NYBearExpansion     { get; private set; }

    // Asia compressed flag
    public bool AsiaCompressed { get; private set; }

    // Current session type
    public SessionType CurrentSession { get; private set; } = SessionType.PreMarket;

    // Set externally by engine before calling OnBar
    public decimal CurrentAtr  { get; set; }
    public decimal CurrentVwap { get; set; }

    public SessionEngine(ModuleConfig cfg)
    {
        _cfg = cfg;
        _tz  = FindTz(cfg.Timezone);
    }

    public void OnBar(Bar bar, DateTime tradingDate)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, _tz);
        var time  = TimeOnly.FromDateTime(local);

        CurrentSession = DetectSession(time);

        // Update full-day high/low
        UpdateSessionHighLow(bar.High, bar.Low);

        // Update per-session high/low
        switch (CurrentSession)
        {
            case SessionType.Asia:
                if (bar.High > AsiaHigh) AsiaHigh = bar.High;
                if (bar.Low < AsiaLow) AsiaLow = bar.Low;
                // Check compressed after update
                if (CurrentAtr > 0)
                    AsiaCompressed = AsiaRange < CurrentAtr * 1.2m;
                break;

            case SessionType.London:
                if (bar.High > LondonHigh) LondonHigh = bar.High;
                if (bar.Low < LondonLow) LondonLow = bar.Low;
                // Cross-session: London swept Asia levels
                if (AsiaHigh > 0 && bar.High > AsiaHigh)
                    LondonSweptAsiaHigh = true;
                if (AsiaLow < decimal.MaxValue && bar.Low < AsiaLow)
                    LondonSweptAsiaLow = true;
                break;

            case SessionType.NYOpen:
                if (bar.High > NYHigh) NYHigh = bar.High;
                if (bar.Low < NYLow) NYLow = bar.Low;
                // Cross-session: NY expansion beyond London
                if (LondonHigh > 0 && bar.High > LondonHigh)
                    NYBullExpansion = true;
                if (LondonLow < decimal.MaxValue && bar.Low < LondonLow)
                    NYBearExpansion = true;
                break;
        }
    }

    public void OnTick(decimal price, DateTime utcTime)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _tz);
        var time  = TimeOnly.FromDateTime(local);
        CurrentSession = DetectSession(time);

        // Update session high/low in real-time
        if (price > SessionHigh) SessionHigh = price;
        if (price < SessionLow)  SessionLow  = price;

        // Update per-session high/low
        switch (CurrentSession)
        {
            case SessionType.Asia:
                if (price > AsiaHigh) AsiaHigh = price;
                if (price < AsiaLow) AsiaLow = price;
                break;
            case SessionType.London:
                if (price > LondonHigh) LondonHigh = price;
                if (price < LondonLow) LondonLow = price;
                if (AsiaHigh > 0 && price > AsiaHigh) LondonSweptAsiaHigh = true;
                if (AsiaLow < decimal.MaxValue && price < AsiaLow) LondonSweptAsiaLow = true;
                break;
            case SessionType.NYOpen:
                if (price > NYHigh) NYHigh = price;
                if (price < NYLow) NYLow = price;
                if (LondonHigh > 0 && price > LondonHigh) NYBullExpansion = true;
                if (LondonLow < decimal.MaxValue && price < LondonLow) NYBearExpansion = true;
                break;
        }
    }

    public void NewSession(DateTime tradingDate)
    {
        // Roll current into previous is handled by SeedHistory;
        // NewSession just resets intraday state
        SessionHigh = 0;
        SessionLow  = decimal.MaxValue;

        AsiaHigh = 0;
        AsiaLow  = decimal.MaxValue;

        LondonHigh = 0;
        LondonLow  = decimal.MaxValue;

        NYHigh = 0;
        NYLow  = decimal.MaxValue;

        LondonSweptAsiaHigh = false;
        LondonSweptAsiaLow  = false;
        NYBullExpansion     = false;
        NYBearExpansion     = false;
        AsiaCompressed      = false;

        CurrentSession = SessionType.PreMarket;
    }

    /// <summary>
    /// Seed historical levels from daily bars. Expects bars sorted ascending by date.
    /// Computes PDH/PDL from last bar, PWH/PWL from last 5 bars, PMH/PML from last 20 bars.
    /// </summary>
    public void SeedHistory(IReadOnlyList<Bar> dailyBars)
    {
        if (dailyBars.Count == 0) return;

        // Previous day
        var last = dailyBars[^1];
        PDH = last.High;
        PDL = last.Low;

        // Previous week (last 5 bars)
        int weekCount = Math.Min(5, dailyBars.Count);
        PWH = decimal.MinValue;
        PWL = decimal.MaxValue;
        for (int i = dailyBars.Count - weekCount; i < dailyBars.Count; i++)
        {
            if (dailyBars[i].High > PWH) PWH = dailyBars[i].High;
            if (dailyBars[i].Low < PWL)  PWL = dailyBars[i].Low;
        }

        // Previous month (last 20 bars)
        int monthCount = Math.Min(20, dailyBars.Count);
        PMH = decimal.MinValue;
        PML = decimal.MaxValue;
        for (int i = dailyBars.Count - monthCount; i < dailyBars.Count; i++)
        {
            if (dailyBars[i].High > PMH) PMH = dailyBars[i].High;
            if (dailyBars[i].Low < PML)  PML = dailyBars[i].Low;
        }
    }

    public SessionType DetectSession(TimeOnly time)
    {
        // Asia wraps midnight: 18:00-00:00
        if (time >= _cfg.AsiaStart || time < _cfg.AsiaEnd)
            return SessionType.Asia;
        if (time >= _cfg.LondonStart && time < _cfg.LondonEnd)
            return SessionType.London;
        if (time >= _cfg.NYOpenStart && time < _cfg.NYOpenEnd)
            return SessionType.NYOpen;
        if (time >= _cfg.MiddayStart && time < _cfg.MiddayEnd)
            return SessionType.Midday;
        if (time >= _cfg.PowerStart && time < _cfg.PowerEnd)
            return SessionType.PowerHour;

        return SessionType.PreMarket;
    }

    private void UpdateSessionHighLow(decimal high, decimal low)
    {
        if (high > SessionHigh) SessionHigh = high;
        if (low < SessionLow)   SessionLow  = low;
    }

    private static TimeZoneInfo FindTz(string tz)
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
