using System.Text.Json;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Models;

public class BasketCodecTests
{
    // Shaped like the live basket: written by the old JS editor (ints as strings in places,
    // "HH:mm" times, a key-less Enabled, fields the engine no longer reads).
    private const string JsBasket = """
    [
      {"Id":"retest-mnq","Enabled":true,"Label":"retest-mnq","StrategyType":1,"Ticker":"MNQZ26","PointValue":2,"TickSize":0.25,
       "ExecutionTFMinutes":"5",
       "Sessions":[{"SessionId":"NY","Enabled":true,"CutoffHour":"14","CutoffMinute":30}],
       "Config":{"MaxTrades":3,"Contracts":2,"MaxContracts":3,"StopPct":0.5,"TargetPct":100,"PartialPct":50,"MinRr":1.5,
                 "Mode":"Conservative","OrderType":"Market","UsePartial":true,"UseBe":true,"PartialCts":1,"RetestPct":0.05,
                 "UseCustomOrbWindow":true,"OrbStart":"09:30","OrbEnd":"09:45","AllowLong":false,"QuickReentry":true},
       "AutoTrail":{"Enabled":true,"StopLoss":12,"Freq":2,"Trigger":null}},
      {"Id":"pullback-mgc","Label":"pullback-mgc","StrategyType":0,"Ticker":"MGCZ26","PointValue":10,"TickSize":0.1,
       "Config":{"MaxTrades":2,"Contracts":1,"MaxContracts":1,"StopPct":0.35,"TargetPct":65,"PullbackPct":0.5}}
    ]
    """;

    private static string SetupsJson(StrategyConfig c) => JsonSerializer.Serialize(c.ToSetupConfigs());

    [Fact]
    public void RoundTrip_LeavesWhatTheEngineTradesUnchanged()
    {
        var before = new StrategyConfig { BasketJson = JsBasket };
        var entries = BasketCodec.Parse(JsBasket);
        var after = new StrategyConfig { BasketJson = BasketCodec.Serialize(entries) };

        Assert.Equal(2, entries.Count);
        Assert.Equal(SetupsJson(before), SetupsJson(after));
        Assert.Equal(BasketCodec.Serialize(entries), BasketCodec.Serialize(BasketCodec.Parse(BasketCodec.Serialize(entries))));
    }

    [Fact]
    public void KeyLessEnabled_StaysOff()
    {
        var e = BasketCodec.Parse(JsBasket).Single(x => x.Id == "pullback-mgc");
        Assert.False(e.Enabled);
        Assert.False(BasketCodec.Parse(BasketCodec.Serialize(new[] { e }))[0].Enabled);
    }

    [Fact]
    public void Empty_IsBlank_AndMalformed_Throws()
    {
        Assert.Empty(BasketCodec.Parse(""));
        Assert.Empty(BasketCodec.Parse(null));
        Assert.Equal("", BasketCodec.Serialize(Array.Empty<BasketEntry>()));
        Assert.ThrowsAny<JsonException>(() => BasketCodec.Parse("[{\"Id\":"));
    }
}
