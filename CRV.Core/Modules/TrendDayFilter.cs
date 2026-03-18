using CRV.Core.Models;

namespace CRV.Core.Modules;

public class TrendDayFilter : IEngineModule
{
    private ModuleConfig _cfg;

    public int BullScore { get; private set; }
    public int BearScore { get; private set; }
    public bool TrendDayBull => BullScore >= _cfg.TrendDayThreshold;
    public bool TrendDayBear => BearScore >= _cfg.TrendDayThreshold;

    public TrendDayFilter(ModuleConfig cfg)
    {
        _cfg = cfg;
    }

    /// <summary>Update module parameters for a new session config.</summary>
    public void Reconfigure(ModuleConfig cfg) => _cfg = cfg;

    public void Update(
        bool openingDriveBull, bool openingDriveBear,
        decimal close, decimal vwap,
        decimal orbHigh, decimal orbLow, decimal orbMid,
        decimal sessionHigh, decimal sessionLow, decimal sessionOpen)
    {
        BullScore = 0;
        BearScore = 0;

        // Bull scoring
        if (openingDriveBull) BullScore++;
        if (close > orbHigh && sessionLow > orbMid) BullScore++;
        if (close > vwap) BullScore++;

        // Shallow pullback (bull): price hasn't pulled back much from session high
        decimal moveUp = sessionHigh - sessionOpen;
        if (moveUp > 0 && (sessionHigh - close) / moveUp < _cfg.ShallowPullbackMax)
            BullScore++;

        if (sessionHigh > orbHigh) BullScore++;

        // Bear scoring
        if (openingDriveBear) BearScore++;
        if (close < orbLow && sessionHigh < orbMid) BearScore++;
        if (close < vwap) BearScore++;

        // Shallow pullback (bear): price hasn't pulled back much from session low
        decimal moveDown = sessionOpen - sessionLow;
        if (moveDown > 0 && (close - sessionLow) / moveDown < _cfg.ShallowPullbackMax)
            BearScore++;

        if (sessionLow < orbLow) BearScore++;
    }

    public void OnBar(Bar bar, DateTime tradingDate) { }
    public void OnTick(decimal price, DateTime utcTime) { }

    public void NewSession(DateTime tradingDate)
    {
        BullScore = 0;
        BearScore = 0;
    }
}
