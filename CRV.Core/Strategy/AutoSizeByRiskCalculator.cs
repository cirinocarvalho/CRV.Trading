using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Shared sizing helper used by every strategy. Computes (contracts, partialContracts)
/// honoring AutoSizeByRisk + MaxTradeRisk + HiVolMult + MaxContracts. Returning
/// contracts == 0 means "skip" (risk floor exceeds the budget).
/// </summary>
internal static class AutoSizeByRiskCalculator
{
    public static (int contracts, int partial) Calc(
        decimal ep, decimal sl, StrategySetupConfig cfg, decimal atrRatio)
    {
        int contracts;

        if (cfg.AutoSizeByRisk && cfg.MaxTradeRisk > 0)
        {
            decimal riskPerCt = System.Math.Abs(ep - sl) * cfg.PointValue;
            if (riskPerCt <= 0) return (0, 0);

            int budgetCts = (int)System.Math.Floor(cfg.MaxTradeRisk / riskPerCt);
            if (budgetCts < cfg.Contracts) return (0, 0);  // signals caller to skip

            contracts = System.Math.Min(budgetCts, cfg.MaxContracts);
        }
        else
        {
            bool isHighVol = atrRatio >= 1.0m;
            int cts = isHighVol
                ? (int)System.Math.Round(cfg.Contracts * cfg.HiVolMult)
                : cfg.Contracts;
            contracts = System.Math.Min(cts, cfg.MaxContracts);
        }

        int partial = cfg.PartialCts;
        if (cfg.AutoSizeByRisk && partial > 0 && contracts > cfg.Contracts && cfg.Contracts > 0)
        {
            partial = (int)System.Math.Round(
                (decimal)cfg.PartialCts * contracts / cfg.Contracts,
                MidpointRounding.AwayFromZero);
            if (partial < 1) partial = 1;
            if (partial > contracts - 1) partial = contracts - 1;
        }

        return (contracts, partial);
    }
}
