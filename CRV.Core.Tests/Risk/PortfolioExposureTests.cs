using CRV.Core.Models;
using CRV.Core.Risk;
using Xunit;

namespace CRV.Core.Tests.Risk;

/// <summary>
/// The system enforced a per-trade risk cap and a daily loss limit, and nothing in
/// between. Five setups could be in the market at once, each individually within its
/// budget, and MNQ and MES are not independent risks — one bad opening drive takes
/// both. Concurrent exposure has to have a ceiling of its own.
/// </summary>
public class PortfolioExposureTests
{
    private static GroupOrder Open(string setup, string ticker, decimal entry, decimal stop,
        int contracts, decimal pointValue, GroupOrderStatus status = GroupOrderStatus.Active) => new()
    {
        GroupOrderId = setup + "-g", SetupId = setup, Ticker = ticker,
        Direction = Direction.Long, TotalContracts = contracts,
        EntryPrice = status == GroupOrderStatus.Active ? entry : null,
        InitialStopPrice = stop, PointValue = pointValue, Status = status,
        Legs = { new OrderLeg { LegType = LegType.Entry, Price = entry, Quantity = contracts } },
    };

    // ── Measuring what is committed ───────────────────────────────

    [Fact]
    public void OpenRiskIsStopDistanceTimesContractsTimesPointValue()
    {
        // MNQ 2 contracts, 20 points of stop, $2 a point = $80.
        var risk = PortfolioExposure.OpenRisk(new[] { Open("retest-mnq", "MNQM26", 18020m, 18000m, 2, 2m) });
        Assert.Equal(80m, risk);
    }

    [Fact]
    public void ExposureSumsAcrossEveryOpenPosition()
    {
        var risk = PortfolioExposure.OpenRisk(new[]
        {
            Open("retest-mnq", "MNQM26", 18020m, 18000m, 2, 2m),    // $80
            Open("retest-mes", "MESM26", 5310m,  5300m,  2, 5m),    // $100
            Open("retest-mgc", "MGCM26", 2410m,  2400m,  2, 10m),   // $200
        });
        Assert.Equal(380m, risk);
    }

    [Fact]
    public void AShortsRiskIsMeasuredTheSameWay()
    {
        var g = Open("retest-mnq", "MNQM26", 18000m, 18020m, 2, 2m);
        g.Direction = Direction.Short;
        Assert.Equal(80m, PortfolioExposure.OpenRisk(new[] { g }));
    }

    [Fact]
    public void AWorkingEntryCountsBecauseItCanFillAtAnyMoment()
    {
        // Counting only filled positions is how five resting orders all fill on one
        // move and blow a limit that was never checked against them.
        var pending = Open("retest-mes", "MESM26", 5310m, 5300m, 2, 5m, GroupOrderStatus.Pending);
        Assert.Equal(100m, PortfolioExposure.OpenRisk(new[] { pending }));
    }

    [Fact]
    public void ACompletedGroupNoLongerCounts()
    {
        var done = Open("retest-mes", "MESM26", 5310m, 5300m, 2, 5m, GroupOrderStatus.Completed);
        Assert.Equal(0m, PortfolioExposure.OpenRisk(new[] { done }));
    }

    [Fact]
    public void AGroupWithNoStopRecordedIsSkippedRatherThanCountedAsFree()
    {
        var noStop = Open("x", "MNQM26", 18000m, 0m, 2, 2m);
        Assert.Equal(0m, PortfolioExposure.OpenRisk(new[] { noStop }));
    }

    [Fact]
    public void NothingOpenIsNoExposure()
        => Assert.Equal(0m, PortfolioExposure.OpenRisk(Array.Empty<GroupOrder>()));

    // ── The gate ──────────────────────────────────────────────────

    [Fact]
    public void ACandidateThatFitsUnderTheCeilingIsAdmitted()
    {
        var open = new[] { Open("retest-mnq", "MNQM26", 18020m, 18000m, 2, 2m) };   // $80
        Assert.True(PortfolioExposure.Admits(open, candidateRisk: 100m, maxPortfolioRisk: 400m));
    }

    [Fact]
    public void ACandidateThatWouldBreachTheCeilingIsRefused()
    {
        var open = new[]
        {
            Open("retest-mnq", "MNQM26", 18020m, 18000m, 2, 2m),    // $80
            Open("retest-mgc", "MGCM26", 2410m,  2400m,  2, 10m),   // $200
        };
        Assert.False(PortfolioExposure.Admits(open, candidateRisk: 150m, maxPortfolioRisk: 400m));
    }

    [Fact]
    public void LandingExactlyOnTheCeilingIsAllowed()
    {
        var open = new[] { Open("retest-mnq", "MNQM26", 18020m, 18000m, 2, 2m) };   // $80
        Assert.True(PortfolioExposure.Admits(open, candidateRisk: 320m, maxPortfolioRisk: 400m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ALimitOfZeroOrLessMeansNoLimit(decimal limit)
    {
        var open = new[] { Open("retest-mgc", "MGCM26", 2410m, 2400m, 20, 10m) };   // $2,000
        Assert.True(PortfolioExposure.Admits(open, candidateRisk: 9_999m, maxPortfolioRisk: limit));
    }

    [Fact]
    public void ACandidateBiggerThanTheWholeCeilingIsRefusedEvenWithNothingOpen()
    {
        Assert.False(PortfolioExposure.Admits(
            Array.Empty<GroupOrder>(), candidateRisk: 500m, maxPortfolioRisk: 400m));
    }

    // ── Sizing a candidate ────────────────────────────────────────

    [Fact]
    public void CandidateRiskIsMeasuredFromTheSignalNotAFilledPosition()
    {
        var sig = new EntrySignal(
            SetupId.A, Direction.Long, Entry: 18020m, Stop: 18000m,
            Tg2Price: 18060m, Tg1Price: 18040m, TotalContracts: 3,
            Time: DateTime.UtcNow, OrderType: "Limit", Ticker: "MNQM26",
            SetupLabel: "retest-mnq", PartialContracts: 1, PointValue: 2m,
            UsePartial: false, UseBe: false);

        Assert.Equal(120m, PortfolioExposure.CandidateRisk(sig));
    }

    [Fact]
    public void ASignalWithNoStopHasNoMeasurableRiskAndIsNotSilentlyFree()
    {
        var sig = new EntrySignal(
            SetupId.A, Direction.Long, Entry: 18020m, Stop: 18020m,
            Tg2Price: 18060m, Tg1Price: 18040m, TotalContracts: 3,
            Time: DateTime.UtcNow, OrderType: "Limit", Ticker: "MNQM26",
            SetupLabel: "retest-mnq", PartialContracts: 1, PointValue: 2m,
            UsePartial: false, UseBe: false);

        // Refused rather than admitted as zero-risk: a signal whose stop equals its
        // entry is malformed, and letting it through unmeasured defeats the gate.
        Assert.Equal(0m, PortfolioExposure.CandidateRisk(sig));
        Assert.False(PortfolioExposure.Admits(Array.Empty<GroupOrder>(), 0m, 400m));
    }
}
