using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// The per-trade reward / risk guard. MNQ at $2 a point, long from 20000 with a 40-point stop,
/// minimum 1.5R: a dollar target under $120 a contract is below the minimum.
/// </summary>
public class MinRrGuardTests
{
    private static readonly DateTime T0 = new(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc);

    private static StrategySetupConfig Cfg(decimal dollars, bool enforce = true, MinRrAction action = MinRrAction.Skip) => new()
    {
        Id = "of-mnq", Ticker = "MNQZ26", PointValue = 2m, TickSize = 0.25m, PartialPct = 50,
        TargetMode = TargetMode.Dollars, TargetDollars = dollars, TargetDollarsBasis = TargetDollarsBasis.PerContract,
        MinRr = 1.5m, EnforceMinRr = enforce, MinRrAction = action,
    };

    private static LevelRequest Request(StrategySetupConfig cfg, decimal stop = 19960m)
        => LevelRequest.From(cfg, entry: 20000m, isLong: true, stop: stop, contracts: 1, rangeOrAtr: 0m);

    [Fact]
    public void BelowMinimum_Skip_SkipsAndSaysByHowMuch()
    {
        var cfg = Cfg(100m);                     // 50 pts / 40 = 1.25R

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.True(g.Skip);
        Assert.Equal(1.25m, g.Rr);
    }

    [Fact]
    public void AtMinimum_Takes()
    {
        var cfg = Cfg(120m);                     // 60 pts / 40 = 1.5R

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.False(g.Skip);
        Assert.Equal(20060m, g.Target);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_TakesAtTheMinimumWithThePartialRecomputed()
    {
        var cfg = Cfg(100m, action: MinRrAction.RaiseTarget);

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.False(g.Skip);
        Assert.Equal(20060m, g.Target);          // 1.5 x 40 pts
        Assert.Equal(20030m, g.Partial);         // 50% of the new 60 pts
        Assert.Equal(1.5m, g.Rr);
    }

    [Fact]
    public void BelowMinimum_RaiseTarget_LeavesTheStopAndTheContractCountAlone()
    {
        var cfg = Cfg(100m, action: MinRrAction.RaiseTarget);
        var request = Request(cfg);

        var g = MinRrGuard.Apply(cfg, request);

        // The raise reads the stop and the count; it hands back only target, partial and R.
        Assert.Equal(request, Request(cfg));
        Assert.Equal(1.5m, Math.Abs(g.Target - request.Entry) / Math.Abs(request.Entry - request.Stop));
        Assert.Equal(new[] { "Partial", "Rr", "Skip", "Target" },
            typeof(GuardedLevels).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public void GuardOff_TakesBelowMinimum()
    {
        var cfg = Cfg(100m, enforce: false);

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.False(g.Skip);
        Assert.Equal(20050m, g.Target);
        Assert.Equal(1.25m, g.Rr);
    }

    [Fact]
    public void NoReward_Skips_EvenWithGuardOff()
    {
        var cfg = Cfg(0m, enforce: false);

        Assert.True(MinRrGuard.Apply(cfg, Request(cfg)).Skip);
    }

    [Fact]
    public void NoRisk_Skips_EvenWithGuardOff()
    {
        var cfg = Cfg(400m, enforce: false);

        Assert.True(MinRrGuard.Apply(cfg, Request(cfg, stop: 20000m)).Skip);
    }

    [Fact]
    public void Gate_MinRrSkip_IsARefusalWithItsReason()
    {
        var gate = new SizeRefusalGate();

        var skip = gate.ReportMinRr(isLong: true, ep: 20000m, sl: 19960m, rr: 1.25m, Cfg(100m), T0);

        Assert.NotNull(skip);
        Assert.Equal(RefusalReason.MinRr, skip!.Reason);
        Assert.Equal(1.25m, skip.Rr);
        Assert.Equal(1.5m, skip.MinRr);
        Assert.Equal(40m, skip.StopDistance);
        Assert.Equal("Skipped: 1.25R below 1.5R", skip.Describe());
        Assert.Same(skip, gate.LastMinRrSkip);
    }

    [Fact]
    public void Gate_MinRrSkip_SameSignalAskedTwice_ReportsOnce()
    {
        var gate = new SizeRefusalGate();
        gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0);

        Assert.Null(gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0.AddSeconds(15)));
    }

    [Fact]
    public void Gate_Reset_ForgetsTheLastSkip()
    {
        var gate = new SizeRefusalGate();
        gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0);

        gate.Reset();

        Assert.Null(gate.LastMinRrSkip);
    }

    [Fact]
    public void AlreadyAtMinimum_RaiseTarget_KeepsItsTarget()
    {
        var cfg = Cfg(200m, action: MinRrAction.RaiseTarget);   // 100 pts / 40 = 2.5R

        var g = MinRrGuard.Apply(cfg, Request(cfg));

        Assert.False(g.Skip);
        Assert.Equal(20100m, g.Target);
        Assert.Equal(2.5m, g.Rr);
    }

    [Fact]
    public void Gate_MinRrSkip_AtDifferentPricesInOneEpisode_ReportsOnce()
    {
        var gate = new SizeRefusalGate();
        Assert.NotNull(gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0));

        Assert.Null(gate.ReportMinRr(true, 20000.25m, 19960.25m, 1.25m, Cfg(100m), T0.AddSeconds(1)));
        Assert.Null(gate.ReportMinRr(true, 20001m, 19961m, 1.2m, Cfg(100m), T0.AddSeconds(2)));
    }

    [Fact]
    public void Gate_MinRrSkip_OtherDirection_ReportsAgain()
    {
        var gate = new SizeRefusalGate();
        gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0);

        Assert.NotNull(gate.ReportMinRr(false, 20000m, 20040m, 1.25m, Cfg(100m), T0.AddSeconds(1)));
    }

    [Fact]
    public void Gate_MinRrSkip_AfterReset_ReportsAgain()
    {
        var gate = new SizeRefusalGate();
        gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0);

        gate.Reset();

        Assert.NotNull(gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0.AddMinutes(1)));
    }

    [Fact]
    public void Gate_NoRewardOrRiskSkip_SaysSo_AndIsDedupedApartFromBelowMinimum()
    {
        var gate = new SizeRefusalGate();
        gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0);

        var skip = gate.ReportMinRr(true, 20000m, 19960m, 0m, Cfg(0m), T0.AddSeconds(1));

        Assert.NotNull(skip);
        Assert.Equal("Skipped: no reward or no risk", skip!.Describe());
        Assert.Null(gate.ReportMinRr(true, 20000.25m, 19960m, 0m, Cfg(0m), T0.AddSeconds(2)));
    }

    [Fact]
    public void Gate_SizeRefusalAndMinRrSkip_DoNotSuppressEachOther()
    {
        var gate = new SizeRefusalGate();
        var cfg = Cfg(100m);
        Assert.NotNull(gate.Report(true, 20000m, 19960m, cfg, T0));

        Assert.NotNull(gate.ReportMinRr(true, 20000m, 19960m, 1.25m, cfg, T0));
    }

    [Fact]
    public void Gate_EndEpisode_ReportsTheSameSkipAgain_AndKeepsTheLastSkip()
    {
        var gate = new SizeRefusalGate();
        var first = gate.ReportMinRr(true, 20000m, 19960m, 1.25m, Cfg(100m), T0);

        gate.EndEpisode();

        Assert.Same(first, gate.LastMinRrSkip);
        var second = gate.ReportMinRr(true, 20010m, 19970m, 1.25m, Cfg(100m), T0.AddMinutes(5));
        Assert.NotNull(second);
        Assert.Same(second, gate.LastMinRrSkip);
    }

    [Fact]
    public void SizeRefusal_KeepsItsWording()
    {
        var r = new SizeRefusal(T0, "of-mnq", "MNQZ26", 40m, 80m, 50m);

        Assert.Equal(RefusalReason.Size, r.Reason);
        Assert.StartsWith("refused for size", r.Describe());
    }
}
