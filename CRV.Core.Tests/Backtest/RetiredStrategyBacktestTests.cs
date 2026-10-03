using System.Text.Json;
using CRV.Backtest.Results;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>Stored baskets and saved runs can still hold the retired EMA21 type (4).</summary>
public class RetiredStrategyBacktestTests
{
    private static StrategyConfig WithRetiredEntryOn()
    {
        var cfg = PullbackSessionFixture.Config();
        cfg.EmaBasketJson = BasketCodec.Serialize(new[]
        {
            new BasketEntry
            {
                Id = "ema21-mnq", Enabled = true, Label = "EMA21 [MNQ]", StrategyType = SetupValidation.RetiredEma21,
                Ticker = PullbackSessionFixture.Ticker, PointValue = 2m, TickSize = 0.25m,
            },
        });
        return cfg;
    }

    [Fact]
    public async Task Backtest_WithRetiredEntryOn_SkipsItAndTradesTheRest()
    {
        var baseline = await PullbackSessionFixture.Run();
        var result = await PullbackSessionFixture.Run(WithRetiredEntryOn());

        Assert.NotEmpty(baseline.Trades);
        Assert.Equal(baseline.Trades.Count, result.Trades.Count);
        Assert.Equal(baseline.Total.NetPnl, result.Total.NetPnl);
    }

    [Fact]
    public void SavedRunHoldingRetiredEntry_LoadsAndReadsItAsDisabled()
    {
        var json = JsonSerializer.Serialize(new BacktestResult { Config = WithRetiredEntryOn() });

        var loaded = JsonSerializer.Deserialize<BacktestResult>(json)!;

        Assert.Single(loaded.Config.ToSetupConfigs(), s => s.Id == "ema21-mnq");
        Assert.Equal("retired EMA21 strategy", Assert.Single(SetupValidation.DisabledSetups(loaded.Config)).Reason);
    }
}
