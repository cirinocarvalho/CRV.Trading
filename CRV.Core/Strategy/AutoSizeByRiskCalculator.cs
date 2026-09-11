using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Shared sizing helper used by every strategy. Computes (contracts, partialContracts)
/// honoring AutoSizeByRisk + MaxTradeRisk + HiVolMult + MaxContracts. Returning
/// contracts == 0 means "refuse": the trade does not fit the risk budget at even one
/// contract. This is the only place that decision is made — a strategy that gets a
/// positive count has a trade inside its budget.
/// <para>
/// With AutoSizeByRisk on, <c>Contracts</c> is a starting size, not a floor. It used to
/// be a floor: a budget that could not carry the configured count refused the trade
/// outright, even when it could plainly carry one contract. On the live book that
/// refused every stop between 52.5 and 105 MNQ points — a two-fold band, entered on
/// exactly the wide-range days the setup is built for — and the refusal wrote nothing
/// anywhere.
/// </para>
/// </summary>
public static class AutoSizeByRiskCalculator
{
    public static (int contracts, int partial) Calc(
        decimal ep, decimal sl, StrategySetupConfig cfg, decimal atrRatio)
    {
        decimal riskPerCt = System.Math.Abs(ep - sl) * cfg.PointValue;
        int contracts;

        if (cfg.AutoSizeByRisk && cfg.MaxTradeRisk > 0)
        {
            if (riskPerCt <= 0) return (0, 0);

            int budgetCts = (int)System.Math.Floor(cfg.MaxTradeRisk / riskPerCt);
            if (budgetCts < 1) return (0, 0);

            contracts = System.Math.Min(budgetCts, cfg.MaxContracts);
        }
        else
        {
            bool isHighVol = atrRatio >= 1.0m;
            int cts = isHighVol
                ? (int)System.Math.Round(cfg.Contracts * cfg.HiVolMult)
                : cfg.Contracts;
            contracts = System.Math.Min(cts, cfg.MaxContracts);

            // The per-trade cap as a plain veto when sizing is fixed (0 = disabled).
            if (cfg.MaxTradeRisk > 0 && riskPerCt * contracts > cfg.MaxTradeRisk)
                return (0, 0);
        }

        // Partial sizing rule:
        //  • AutoSize ON  + PartialCts > 0 → reserve exactly 1 runner; partial = contracts − 1.
        //  • AutoSize ON  + PartialCts = 0 → leave 0 (auto/50% sentinel — same as AutoSize-OFF).
        //  • AutoSize OFF                   → honor cfg.PartialCts literally, clamped to contracts − 1.
        int partial = (cfg.AutoSizeByRisk && cfg.PartialCts > 0)
            ? (contracts > 1 ? contracts - 1 : 0)
            : cfg.PartialCts;
        if (partial > contracts - 1) partial = contracts - 1;
        if (partial < 0) partial = 0;

        return (contracts, partial);
    }

    /// <summary>What a refusal was, in the terms the budget is set in.</summary>
    public static SizeRefusal Refusal(decimal ep, decimal sl, StrategySetupConfig cfg, DateTime time)
    {
        decimal stopDistance = System.Math.Abs(ep - sl);
        return new SizeRefusal(
            Time:            time,
            SetupLabel:      cfg.Id,
            Ticker:          cfg.Ticker,
            StopDistance:    stopDistance,
            RiskPerContract: stopDistance * cfg.PointValue,
            Budget:          cfg.MaxTradeRisk);
    }
}
