namespace CRV.Core.Tests.Brokers;

using CRV.Core.Models;
using CRV.Live;
using Xunit;

public class OrderAccountsTests
{
    private static Func<string, string?> Settings(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void TheBrokersAppSettingWins()
    {
        var cfg = new StrategyConfig { Broker = "Schwab", AccountId = "saved" };
        Assert.Equal("app", OrderAccounts.Exec(cfg, Settings(("Schwab:AccountId", "app"))));
    }

    [Fact]
    public void WithoutAnAppSettingTheSavedAccountIsUsed()
    {
        var cfg = new StrategyConfig { Broker = "TradeStation", AccountId = "saved" };
        Assert.Equal("saved", OrderAccounts.Exec(cfg, Settings()));
    }

    [Fact]
    public void ASavedExecAccountOverridesEverything()
    {
        var cfg = new StrategyConfig { Broker = "Schwab", ExecBroker = "Tradovate", AccountId = "saved", ExecAccountId = "exec" };
        Assert.Equal("exec", OrderAccounts.Exec(cfg, Settings(("Tradovate:AccountId", "app"))));
    }

    [Fact]
    public void OrdersOnAnotherBrokerFallBackToTheDataAccount()
    {
        // The engine's rule: no app setting for the exec broker falls back to the data account.
        var cfg = new StrategyConfig { Broker = "Schwab", ExecBroker = "TradeStation", AccountId = "saved" };
        var s = Settings(("Schwab:AccountId", "schwab-app"));
        Assert.Equal("schwab-app", OrderAccounts.Data(cfg, s));
        Assert.Equal("schwab-app", OrderAccounts.Exec(cfg, s));
    }

    [Fact]
    public void ReplayUsesTheTradovateAccount()
    {
        var cfg = new StrategyConfig { Broker = "TradovateReplay", AccountId = "saved" };
        Assert.Equal("tv", OrderAccounts.Exec(cfg, Settings(("Tradovate:AccountId", "tv"))));
    }

    [Fact]
    public void BlankExecAccountDoesNotOverride()
    {
        var cfg = new StrategyConfig { Broker = "Schwab", AccountId = "saved", ExecAccountId = "  " };
        Assert.Equal("saved", OrderAccounts.Exec(cfg, Settings()));
    }
}
