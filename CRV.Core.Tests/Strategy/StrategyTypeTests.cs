using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// Basket JSON stores the strategy type as a number, so the numbers are part of the stored data:
/// 0–3 must never move, and 4 (the retired EMA21) must never be reused.
/// </summary>
public class StrategyTypeTests
{
    [Theory]
    [InlineData(StrategyType.Pullback, 0)]
    [InlineData(StrategyType.Retest, 1)]
    [InlineData(StrategyType.OrbFakeout, 2)]
    [InlineData(StrategyType.SessionFakeout, 3)]
    [InlineData(StrategyType.Ema, 5)]
    public void StrategyType_HasItsStoredNumber(StrategyType type, int number)
        => Assert.Equal(number, (int)type);

    [Fact]
    public void StrategyType_KeepsFourForTheRetiredEma21()
    {
        Assert.Equal(4, (int)SetupValidation.RetiredEma21);
        Assert.False(Enum.IsDefined(SetupValidation.RetiredEma21));
    }

    [Fact]
    public void Basket_WithTypesZeroToThree_RoundTripsUnchanged()
    {
        const string stored = """
            [{"Id":"p","Enabled":true,"StrategyType":0},{"Id":"r","Enabled":true,"StrategyType":1},
             {"Id":"o","Enabled":true,"StrategyType":2},{"Id":"s","Enabled":true,"StrategyType":3}]
            """;

        var entries = BasketCodec.Parse(stored);
        var saved = BasketCodec.Serialize(entries);

        Assert.Equal(new[] { StrategyType.Pullback, StrategyType.Retest, StrategyType.OrbFakeout, StrategyType.SessionFakeout },
            entries.Select(e => e.StrategyType));
        Assert.Equal(saved, BasketCodec.Serialize(BasketCodec.Parse(saved)));
        Assert.Contains("\"StrategyType\":3", saved);
    }

    [Fact]
    public void Basket_WithRetiredEntry_KeepsTypeFourThroughASave()
    {
        const string stored = """[{"Id":"ema21-mnq","Enabled":true,"StrategyType":4,"Ticker":"/MNQZ26"}]""";

        var saved = BasketCodec.Serialize(BasketCodec.Parse(stored));

        Assert.Equal(SetupValidation.RetiredEma21, BasketCodec.Parse(saved).Single().StrategyType);
        Assert.Contains("\"StrategyType\":4", saved);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Factory_TreatsRetiredAndEmaAsUnknown(int type)
        => Assert.Throws<ArgumentException>(() =>
            StrategyFactory.Create(new StrategySetupConfig { StrategyType = (StrategyType)type }));
}
