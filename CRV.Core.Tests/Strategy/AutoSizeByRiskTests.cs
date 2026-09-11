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
    public void AutoSize_BudgetAffordsFewerThanConfigured_SizesDownRatherThanSkipping()
    {
        // riskPerCt = |100 - 80| * 20 = 400. Budget = 500/400 = 1. Contracts says 2.
        // The budget can carry this trade at one contract, so it trades at one.
        // Refusing it outright was the dead band: the widest-stop signals, the ones
        // the setup fires on its most distinctive days, were the only ones dropped.
        var (cts, partial) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 80m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(1, cts);
        Assert.Equal(0, partial);   // one contract is a runner, not a partial
    }

    [Fact]
    public void AutoSize_SingleContractExceedsBudget_Refuses()
    {
        // riskPerCt = |100 - 70| * 20 = 600 > budget 500. Not even one fits.
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 70m, cfg: BaseCfg(), atrRatio: 0m);
        Assert.Equal(0, cts);
    }

    [Fact]
    public void AutoSizeOff_ConfiguredSizeExceedsBudget_Refuses()
    {
        // The legacy veto, now inside Calc rather than duplicated after it in
        // every strategy: 2 contracts x |100 - 85| x 20 = 600 > 500.
        var cfg = BaseCfg();
        cfg.AutoSizeByRisk = false;
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 85m, cfg: cfg, atrRatio: 0m);
        Assert.Equal(0, cts);
    }

    [Fact]
    public void AutoSizeOff_ConfiguredSizeWithinBudget_Trades()
    {
        // 2 x |100 - 90| x 20 = 400 <= 500.
        var cfg = BaseCfg();
        cfg.AutoSizeByRisk = false;
        var (cts, _) = AutoSizeByRiskCalculator.Calc(
            ep: 100m, sl: 90m, cfg: cfg, atrRatio: 0m);
        Assert.Equal(2, cts);
    }

    [Fact]
    public void Refusal_DescribesStopRiskAndBudgetInDollars()
    {
        var when = new DateTime(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc);
        var r = AutoSizeByRiskCalculator.Refusal(ep: 100m, sl: 70m, cfg: BaseCfg(), time: when);

        Assert.Equal(when, r.Time);
        Assert.Equal("A", r.SetupLabel);
        Assert.Equal("NQH26", r.Ticker);
        Assert.Equal(30m, r.StopDistance);
        Assert.Equal(600m, r.RiskPerContract);
        Assert.Equal(500m, r.Budget);
        Assert.Equal("refused for size — stop 30.00 pts = $600.00/ct, budget $500", r.Describe());
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

    // ── One refusal per signal, however many ticks re-ask ────────────
    // A strategy that is refused stays armed and asks again on the next tick, as it
    // always did. The trading behaviour is unchanged; only the first ask is reported.

    private static readonly DateTime T0 = new(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Gate_SameSignalAskedTwice_ReportsOnce()
    {
        var gate = new SizeRefusalGate();

        var first  = gate.Report(isLong: true, ep: 100m, sl: 70m, BaseCfg(), T0);
        var second = gate.Report(isLong: true, ep: 100m, sl: 70m, BaseCfg(), T0.AddSeconds(15));

        Assert.NotNull(first);
        Assert.Equal(T0, first!.Time);
        Assert.Null(second);
    }

    [Fact]
    public void Gate_DifferentStop_IsADifferentSignal()
    {
        var gate = new SizeRefusalGate();
        gate.Report(isLong: true, ep: 100m, sl: 70m, BaseCfg(), T0);

        var moved = gate.Report(isLong: true, ep: 100m, sl: 65m, BaseCfg(), T0.AddMinutes(1));

        Assert.NotNull(moved);
        Assert.Equal(35m, moved!.StopDistance);
    }

    [Fact]
    public void Gate_OppositeDirectionAtSameLevels_IsADifferentSignal()
    {
        var gate = new SizeRefusalGate();
        gate.Report(isLong: true, ep: 100m, sl: 70m, BaseCfg(), T0);

        Assert.NotNull(gate.Report(isLong: false, ep: 100m, sl: 70m, BaseCfg(), T0.AddMinutes(1)));
    }

    [Fact]
    public void Gate_AfterReset_ReportsTheSameSignalAgain()
    {
        var gate = new SizeRefusalGate();
        gate.Report(isLong: true, ep: 100m, sl: 70m, BaseCfg(), T0);

        gate.Reset();

        Assert.NotNull(gate.Report(isLong: true, ep: 100m, sl: 70m, BaseCfg(), T0.AddDays(1)));
    }
}
