namespace CRV.Core.Tests.Brokers;

using CRV.Core.Models;
using CRV.Live;
using Xunit;

public class ManualOrderBuilderTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 14, 7, 30, DateTimeKind.Utc);

    private static ManualOrder Base(string dir = "Long") => new()
    {
        Ticker = "NQZ26", Direction = dir, OrderType = "Market", EntryPrice = 20400m, Contracts = 2, PointValue = 20m,
        InputMode = "Points", StopPoints = 20m, TargetPoints = 40m,
    };

    [Fact]
    public void Points_Long_BuildsBracketAroundEntry()
    {
        var (sig, errors) = ManualOrderBuilder.Build(Base(), Now);
        Assert.Empty(errors);
        Assert.NotNull(sig);
        Assert.Equal(Direction.Long, sig!.Direction);
        Assert.Equal(20380m, sig.Stop);
        Assert.Equal(20440m, sig.Tg2Price);
        Assert.Equal(20420m, sig.Tg1Price);          // no partial: Tg1 defaults to half the target distance
        Assert.Equal(2, sig.TotalContracts);
        Assert.Equal("Manual-140730", sig.SetupLabel);
        Assert.False(sig.UsePartial);
    }

    [Fact]
    public void Points_Short_MirrorsTheBracket()
    {
        var (sig, _) = ManualOrderBuilder.Build(Base("Short"), Now);
        Assert.Equal(20420m, sig!.Stop);
        Assert.Equal(20360m, sig.Tg2Price);
    }

    [Fact]
    public void Dollars_ConvertsUsingPointValueAndContracts()
    {
        var o = Base();
        o.InputMode = "Dollars"; o.StopDollars = 800m; o.TargetDollars = 1600m;   // 800 / (20 × 2) = 20 pts
        var (sig, errors) = ManualOrderBuilder.Build(o, Now);
        Assert.Empty(errors);
        Assert.Equal(20380m, sig!.Stop);
        Assert.Equal(20440m, sig.Tg2Price);
    }

    [Fact]
    public void Dollars_WithoutPointValue_IsRejected()
    {
        var o = Base(); o.InputMode = "Dollars"; o.PointValue = 0; o.StopDollars = 800m; o.TargetDollars = 1600m;
        var (sig, errors) = ManualOrderBuilder.Build(o, Now);
        Assert.Null(sig);
        Assert.Contains(errors, e => e.Contains("Point value"));
    }

    [Fact]
    public void Price_StopOnTheWrongSide_IsRejected()
    {
        var o = Base(); o.InputMode = "Price"; o.StopPrice = 20410m; o.TargetPrice = 20450m;   // stop above a long entry
        var (sig, errors) = ManualOrderBuilder.Build(o, Now);
        Assert.Null(sig);
        Assert.Contains(errors, e => e.Contains("Stop distance"));
    }

    [Fact]
    public void Partial_NeedsTwoContractsAndAShorterDistance()
    {
        var o = Base(); o.Contracts = 1; o.UsePartial = true; o.PartialContracts = 1; o.PartialPoints = 10m;
        Assert.Contains(ManualOrderBuilder.Build(o, Now).Errors, e => e.Contains("at least 2 contracts"));

        o = Base(); o.UsePartial = true; o.PartialContracts = 1; o.PartialPoints = 50m;   // beyond the 40-pt target
        Assert.Contains(ManualOrderBuilder.Build(o, Now).Errors, e => e.Contains("Partial distance"));

        o = Base(); o.UsePartial = true; o.PartialContracts = 1; o.PartialPoints = 15m;
        var (sig, errors) = ManualOrderBuilder.Build(o, Now);
        Assert.Empty(errors);
        Assert.Equal(20415m, sig!.Tg1Price);
        Assert.Equal(1, sig.PartialContracts);
    }

    [Fact]
    public void AutoTrail_WithoutPartial_NeedsATrigger()
    {
        var o = Base(); o.UseAutoTrail = true; o.AutoTrailStopLoss = 10m; o.AutoTrailFreq = 2m;
        Assert.Contains(ManualOrderBuilder.Build(o, Now).Errors, e => e.Contains("Trigger"));

        o.AutoTrailTrigger = 15m;
        var (sig, errors) = ManualOrderBuilder.Build(o, Now);
        Assert.Empty(errors);
        Assert.Equal(10m, sig!.AutoTrailStopLoss);
        Assert.Equal(15m, sig.AutoTrailTrigger);
    }

    [Fact]
    public void MultiBracket_QuantitiesMustAddUp_AndTargetsBeOnTheProfitSide()
    {
        var o = Base(); o.UseMultiBracket = true; o.StopPrice = 20380m; o.Contracts = 3;
        o.Brackets = new() { new() { Target = 20420m, Qty = 1, MoveBe = true }, new() { Target = 20440m, Qty = 1 } };
        Assert.Contains(ManualOrderBuilder.Build(o, Now).Errors, e => e.Contains("must equal total Contracts"));

        o.Brackets.Add(new() { Target = 20390m, Qty = 1 });   // below a long entry
        Assert.Contains(ManualOrderBuilder.Build(o, Now).Errors, e => e.Contains("profit side"));

        o.Brackets[2].Target = 20470m;
        var (sig, errors) = ManualOrderBuilder.Build(o, Now);
        Assert.Empty(errors);
        Assert.Equal(3, sig!.Brackets!.Count);
        Assert.Equal(20420m, sig.Tg1Price);
        Assert.Equal(20470m, sig.Tg2Price);
        Assert.True(sig.UsePartial);
        Assert.True(sig.Brackets[0].MoveBe);
    }
}
