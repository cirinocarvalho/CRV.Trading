using System.Text.Json;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Models;

/// <summary>
/// <c>BasketEntry.Enabled</c> defaulted to true, so an entry whose JSON omitted the
/// key was armed. In the live config every entry carried <c>"Enabled": false</c>
/// except one — sessionfakeout-mnq, which omitted it — and that entry traded. It was
/// the only setup running, and its record is a single trade for -$277.80. An absent
/// switch has to mean off.
/// </summary>
public class BasketEnabledDefaultTests
{
    private static StrategyConfig WithBasket(string json) => new()
    {
        Ticker = "MNQM26", BasketJson = json,
    };

    private const string Omitted = """
        [{"Id":"sessionfakeout-mnq","Label":"Session Fakeout [MNQ]","StrategyType":3,
          "Ticker":"MNQM26","PointValue":2,"TickSize":0.25,
          "Config":{"Name":"sessionfakeout-mnq","Contracts":2}}]
        """;

    private const string ExplicitlyTrue = """
        [{"Id":"retest-mnq","Enabled":true,"Label":"Retest [MNQ]","StrategyType":1,
          "Ticker":"MNQM26","PointValue":2,"TickSize":0.25,
          "Config":{"Name":"retest-mnq","Contracts":2}}]
        """;

    private const string ExplicitlyFalse = """
        [{"Id":"retest-mes","Enabled":false,"Label":"Retest [MES]","StrategyType":1,
          "Ticker":"MESM26","PointValue":5,"TickSize":0.25,
          "Config":{"Name":"retest-mes","Contracts":2}}]
        """;

    [Fact]
    public void AnEntryWithNoEnabledKeyIsDisarmed()
        => Assert.False(JsonSerializer.Deserialize<List<BasketEntry>>(Omitted,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })![0].Enabled);

    [Fact]
    public void ANewEntryStartsDisarmed()
        => Assert.False(new BasketEntry().Enabled);

    [Fact]
    public void AnOmittedKeyDoesNotReachTheEngineAsAnEnabledSetup()
    {
        // The path that actually matters: BacktestEngine and ComposableEngine both
        // register a setup only when Enabled is true.
        Assert.All(WithBasket(Omitted).ToSetupConfigs(), s => Assert.False(s.Enabled));
    }

    [Fact]
    public void AnExplicitTrueIsStillHonoured()
        => Assert.All(WithBasket(ExplicitlyTrue).ToSetupConfigs(), s => Assert.True(s.Enabled));

    [Fact]
    public void AnExplicitFalseIsStillHonoured()
        => Assert.All(WithBasket(ExplicitlyFalse).ToSetupConfigs(), s => Assert.False(s.Enabled));

    [Fact]
    public void RoundTrippingWritesTheKeyBackExplicitly()
    {
        // Once a basket has been through the app the ambiguity is gone: every entry
        // carries the key either way, so the default stops mattering for that config.
        var cfg = WithBasket(Omitted);
        cfg.MapBasketEntries(_ => { });

        Assert.Contains("\"Enabled\":false", cfg.BasketJson.Replace(" ", ""));
    }

    [Fact]
    public void ABasketWhereNothingIsArmedIsReportedRatherThanSilent()
    {
        var cfg = WithBasket(Omitted);
        Assert.Equal(0, cfg.EnabledSetupCount());
        Assert.True(cfg.HasNoArmedSetups());
    }

    [Fact]
    public void ABasketWithAnArmedEntryIsNotFlagged()
    {
        var cfg = WithBasket(ExplicitlyTrue);
        Assert.Equal(1, cfg.EnabledSetupCount());
        Assert.False(cfg.HasNoArmedSetups());
    }
}
