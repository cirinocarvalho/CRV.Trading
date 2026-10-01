namespace CRV.Core.Tests.Brokers;

using CRV.Live;
using Xunit;

public class AccountModeTests
{
    private const string TvLive = "https://live.tradovateapi.com/v1";
    private const string TvDemo = "https://demo.tradovateapi.com/v1";

    [Theory]
    [InlineData("Mock",            TvLive, AccountKind.Paper)]
    [InlineData("TradovateReplay", TvLive, AccountKind.Replay)]
    [InlineData("Tradovate",       TvDemo, AccountKind.Demo)]
    [InlineData("Tradovate",       TvLive, AccountKind.Live)]
    [InlineData("Schwab",          TvDemo, AccountKind.Live)]   // demo host only matters for Tradovate
    [InlineData("TradeStation",    null,   AccountKind.Live)]
    [InlineData("",                TvLive, AccountKind.Paper)]
    public void For_ClassifiesExecutionBroker(string broker, string? tvUrl, AccountKind expected)
    {
        Assert.Equal(expected, AccountMode.For(broker, tvUrl).Kind);
    }

    [Fact]
    public void OnlyLiveIsRealMoney()
    {
        Assert.True(AccountMode.For("Tradovate", TvLive).IsRealMoney);
        Assert.False(AccountMode.For("Tradovate", TvDemo).IsRealMoney);
        Assert.False(AccountMode.For("Mock", TvLive).IsRealMoney);
    }

    [Fact]
    public void Labels()
    {
        var m = AccountMode.For("TradovateReplay", TvLive);
        Assert.Equal("REPLAY", m.Label);
        Assert.Equal("Tradovate Replay", m.BrokerLabel);
    }
}
