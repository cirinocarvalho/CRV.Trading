using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class AutoSizeByRiskTests
{
    // Helper: directly exercise the sizing math via a strategy's CalcContracts.
    // We use PullbackStrategy as the canonical implementation; identical logic
    // is duplicated across the other 4 strategies and is covered by their own tests.
    private static StrategySetupConfig BaseCfg() => new()
    {
        Id = "A", Name = "A", SetupId = SetupId.A,
        StrategyType = StrategyType.Pullback,
        Ticker = "NQH26", PointValue = 20m, TickSize = 0.25m,
        Contracts = 2, MaxContracts = 6, HiVolMult = 1.0m,
        MaxTradeRisk = 500m, AutoSizeByRisk = true,
        PartialCts = 1,
    };

    [Fact]
    public void AutoSize_WithinBudget_ScalesUpToBudgetCap()
    {
        // riskPerCt = |100 - 95| * 20 = 100. Budget = 500/100 = 5. Cap = MaxContracts (6).
        // Expect 5 contracts. AutoSize ON + PartialCts > 0 → runner=1, partial=cts-1=4.
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(5, cts);
        Assert.Equal(4, partial);
    }

    [Fact]
    public void AutoSize_BudgetCtsBelowFloor_ReturnsZeroToSignalSkip()
    {
        // riskPerCt = |100 - 80| * 20 = 400. Budget = 500/400 = 1. Floor = Contracts (2).
        // budgetCts (1) < Contracts (2) ⇒ skip.
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 80m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(0, cts);
    }

    [Fact]
    public void AutoSize_BudgetExceedsMaxContracts_ClampsToMaxContracts()
    {
        // riskPerCt = |100 - 99.5| * 20 = 10. Budget = 500/10 = 50. Cap to MaxContracts (6).
        // AutoSize ON + PartialCts > 0 → runner=1, partial=cts-1=5.
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 99.5m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(6, cts);
        Assert.Equal(5, partial);
    }

    [Fact]
    public void AutoSizeOff_FallsBackToHiVolMultThenMaxContractsClamp()
    {
        var cfg = BaseCfg();
        cfg.AutoSizeByRisk = false;
        cfg.HiVolMult = 2.0m;
        // High vol: 2 * 2.0 = 4. Cap to MaxContracts (6) ⇒ 4. PartialCts unchanged (1).
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: cfg, atrRatio: 1.0m);
        Assert.Equal(4, cts);
        Assert.Equal(1, partial);
    }

    [Fact]
    public void AutoSizeOff_ClampsToMaxContracts()
    {
        var cfg = BaseCfg();
        cfg.AutoSizeByRisk = false;
        cfg.HiVolMult = 5.0m;
        cfg.MaxContracts = 3;
        // High vol: 2 * 5.0 = 10, clamped to MaxContracts (3).
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: cfg, atrRatio: 1.0m);
        Assert.Equal(3, cts);
    }

    [Fact]
    public void AutoSize_PartialCtsZero_StaysZeroForAutoMode()
    {
        // PartialCts=0 is the "auto/50% — handled downstream" sentinel. AutoSize must
        // NOT override it: leave partial=0 so downstream logic continues to work.
        var cfg = BaseCfg();
        cfg.PartialCts = 0;
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: cfg, atrRatio: 0m);
        Assert.Equal(5, cts);
        Assert.Equal(0, partial);
    }

    [Fact]
    public void AutoSize_DisabledWhenMaxTradeRiskZero()
    {
        var cfg = BaseCfg();
        cfg.MaxTradeRisk = 0m;       // disabled — autosize must no-op
        cfg.HiVolMult = 1.0m;
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 95m, cfg: cfg, atrRatio: 0m);
        Assert.Equal(2, cts);        // falls back to plain Contracts
    }

    [Fact]
    public void AutoSize_EpEqualsStopLoss_ReturnsZeroToSignalSkip()
    {
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 100m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(0, cts);
    }
}
