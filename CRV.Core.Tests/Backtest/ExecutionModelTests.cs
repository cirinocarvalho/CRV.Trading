using CRV.Backtest.Engine;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// The backtest booked every exit at its exact order price: a stop at 18000 filled
/// at 18000, always. Live, 20 of 123 stop-outs (16%) exceeded 1R and the worst
/// reached -4.32R, because a stop becomes a market order in a market that is already
/// moving. Modelling stops as free is what let a -0.003R system look tradeable.
/// </summary>
public class ExecutionModelTests
{
    private const decimal Tick = 0.25m;

    private static ExecutionModel Model(FillMode mode = FillMode.WithSlippage,
        int entryTicks = 1, int stopTicks = 4) =>
        new(new BacktestConfig { FillMode = mode, SlippageTicks = entryTicks, StopSlippageTicks = stopTicks }, Tick);

    // ── Stops slip against you ────────────────────────────────────

    [Fact]
    public void ALongsStopSellsBelowTheStopPrice()
    {
        // 4 ticks x 0.25 = 1.00 worse than the 18000 stop.
        Assert.Equal(17999m, Model().ExitFill(LegType.Stop, isBuy: false, orderPrice: 18000m));
    }

    [Fact]
    public void AShortsStopBuysAboveTheStopPrice()
        => Assert.Equal(18001m, Model().ExitFill(LegType.Stop, isBuy: true, orderPrice: 18000m));

    [Theory]
    [InlineData(0, 18000)]
    [InlineData(1, 17999.75)]
    [InlineData(8, 17998)]
    public void StopSlippageScalesWithTheConfiguredTicks(int ticks, decimal expected)
        => Assert.Equal(expected, Model(stopTicks: ticks).ExitFill(LegType.Stop, isBuy: false, orderPrice: 18000m));

    // ── Limit exits do not ────────────────────────────────────────

    [Theory]
    [InlineData(LegType.Tg1)]
    [InlineData(LegType.Tg2)]
    [InlineData(LegType.Tg3)]
    [InlineData(LegType.Tg4)]
    public void TargetsFillAtTheirLimitBecauseALimitNeverFillsWorse(LegType leg)
        => Assert.Equal(18030m, Model().ExitFill(leg, isBuy: false, orderPrice: 18030m));

    // ── Entries ───────────────────────────────────────────────────

    [Fact]
    public void AMarketEntryToBuyPaysUp()
        => Assert.Equal(18010.25m, Model().EntryFill(18010m, isBuy: true, isLimit: false));

    [Fact]
    public void AMarketEntryToSellGetsLess()
        => Assert.Equal(18009.75m, Model().EntryFill(18010m, isBuy: false, isLimit: false));

    [Fact]
    public void ALimitEntryFillsAtItsLimit()
        => Assert.Equal(18010m, Model().EntryFill(18010m, isBuy: true, isLimit: true));

    // ── The other fill modes stay exactly as they were ────────────

    [Theory]
    [InlineData(FillMode.AtTouch)]
    [InlineData(FillMode.AtClose)]
    public void WithoutTheSlippageModeNothingSlips(FillMode mode)
    {
        var m = Model(mode);
        Assert.Equal(18000m, m.ExitFill(LegType.Stop, isBuy: false, orderPrice: 18000m));
        Assert.Equal(18010m, m.EntryFill(18010m, isBuy: true, isLimit: false));
    }

    // ── The consequence the review measured ───────────────────────

    [Fact]
    public void FourTicksOfStopSlippageTurnsAOneRLossIntoMoreThanOneR()
    {
        // MNQ long: entry 18010, stop 18000, so 1R = 10 points of risk.
        const decimal entry = 18010m, stop = 18000m;
        var fill = Model(stopTicks: 4).ExitFill(LegType.Stop, isBuy: false, orderPrice: stop);
        decimal realisedR = (fill - entry) / (entry - stop);

        Assert.Equal(-1.10m, realisedR);
        Assert.True(realisedR < -1m, "a stop that costs exactly 1R is the thing this model exists to stop claiming");
    }
}

/// <summary>
/// Slippage is priced in ticks, and the instruments in one basket do not share a
/// tick: MCL's is 0.01 and MNQ's is 0.25. Charging every instrument the global tick
/// would overstate MCL's stop cost twenty-five-fold.
/// </summary>
public class PerInstrumentSlippageTests
{
    private static StrategyConfig Basket()
    {
        var entries = new List<BasketEntry>
        {
            new() { Id = "retest-mnq", Ticker = "MNQM26", TickSize = 0.25m, PointValue = 2m },
            new() { Id = "retest-mcl", Ticker = "MCLM26", TickSize = 0.01m, PointValue = 100m },
            new() { Id = "retest-mgc", Ticker = "MGCM26", TickSize = 0.10m, PointValue = 10m },
        };
        return new StrategyConfig
        {
            TickSize = 0.25m,
            BasketJson = System.Text.Json.JsonSerializer.Serialize(entries),
        };
    }

    [Theory]
    [InlineData("MNQM26", 0.25)]
    [InlineData("MCLM26", 0.01)]
    [InlineData("MGCM26", 0.10)]
    public void TickSizeComesFromTheBasketEntry(string ticker, decimal expected)
        => Assert.Equal(expected, Basket().TickSizeFor(ticker));

    [Theory]
    [InlineData("")]
    [InlineData("MESM26")]   // not in the basket
    public void UnknownTickersFallBackToTheGlobalTick(string ticker)
        => Assert.Equal(0.25m, Basket().TickSizeFor(ticker));

    [Fact]
    public void AStopOnCrudeIsNotChargedTheNasdaqTick()
    {
        var model = new ExecutionModel(
            new BacktestConfig { FillMode = FillMode.WithSlippage, StopSlippageTicks = 4 },
            Basket().TickSizeFor);

        Assert.Equal(74.96m,   model.ExitFill(LegType.Stop, isBuy: false, 75m,    "MCLM26"));
        Assert.Equal(17999m,   model.ExitFill(LegType.Stop, isBuy: false, 18000m, "MNQM26"));
        Assert.Equal(2399.60m, model.ExitFill(LegType.Stop, isBuy: false, 2400m,  "MGCM26"));
    }
}
