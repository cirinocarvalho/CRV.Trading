using CRV.Core.Options;
using Xunit;

namespace CRV.Core.Tests.Options;

public class StructureFinderTests
{
    private static readonly DateTime Exp = new(2026, 9, 18);
    private const decimal Spot = 100m;

    /// <summary>
    /// Synthetic chain, strikes 80..120. Prices are intrinsic plus a simple time value so
    /// spreads and flies come out with sane, orderly economics.
    /// </summary>
    private static OptionChain Chain()
    {
        var contracts = new List<OptionContract>();
        for (decimal k = 80m; k <= 120m; k += 1m)
        {
            foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
            {
                decimal intrinsic = right == OptionRight.Call
                    ? Math.Max(Spot - k, 0m) : Math.Max(k - Spot, 0m);
                decimal timeValue = Math.Max(0.20m, 5m - Math.Abs(Spot - k) * 0.20m);
                decimal mid = intrinsic + timeValue;

                contracts.Add(new OptionContract
                {
                    Symbol            = $"TST   260918{(right == OptionRight.Call ? 'C' : 'P')}{(int)(k * 1000):D8}",
                    Right             = right,
                    Strike            = k,
                    Expiration        = Exp,
                    DaysToExpiration  = 14,
                    ExpiresAtUtc      = Exp.AddHours(20),
                    Bid               = mid - 0.02m,
                    Ask               = mid + 0.02m,
                    Mark              = mid,
                    Volume            = 500,
                    OpenInterest      = 2_000,
                    Delta             = right == OptionRight.Call ? 0.5m : -0.5m,
                    Gamma             = 0.05m,
                    Theta             = -0.03m,
                    Vega              = 0.10m,
                    ImpliedVolatility = 20m,
                    IntrinsicValue    = intrinsic,
                    ExtrinsicValue    = timeValue,
                    Multiplier        = 100,
                    InTheMoney        = intrinsic > 0m,
                    NonStandard       = false,
                });
            }
        }
        return new OptionChain("TST", Spot, contracts);
    }

    private static IReadOnlyList<StructureCandidate> Bullish(decimal target = 110m)
        => StructureFinder.Find(Chain(), Exp, target, commissionPerContract: 0.65m);

    [Fact]
    public void Find_ReturnsCandidates()
        => Assert.NotEmpty(Bullish());

    [Fact]
    public void Find_RanksByProfitAtTheTarget()
    {
        var c = Bullish();
        Assert.Equal(c.OrderByDescending(x => x.PnlAtTarget), c);
    }

    [Fact]
    public void BullishTarget_OffersUpsideStructuresNotDownsideOnes()
    {
        var names = Bullish().Select(c => c.Name).ToList();
        Assert.Contains(names, n => n.Contains("call", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Bear", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BearishTarget_OffersDownsideStructures()
    {
        var names = StructureFinder.Find(Chain(), Exp, target: 90m).Select(c => c.Name).ToList();
        Assert.Contains(names, n => n.Contains("put", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Bull", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Butterfly_IsCentredOnTheTarget()
    {
        var fly = Bullish(110m).First(c => c.Name.Contains("utterfly"));
        var body = fly.Legs.Single(l => l.Quantity == 2);
        Assert.Equal(110m, body.Strike);
    }

    [Fact]
    public void EverySensitivityRowBracketsTheTarget()
    {
        foreach (var c in Bullish(110m))
        {
            Assert.Equal(3, c.Sensitivity.Count);
            Assert.True(c.Sensitivity[0].Underlying < 110m);
            Assert.Equal(110m, c.Sensitivity[1].Underlying);
            Assert.True(c.Sensitivity[2].Underlying > 110m);
        }
    }

    [Fact]
    public void SensitivityMiddlePointAgreesWithTheRankingKey()
    {
        foreach (var c in Bullish())
            Assert.Equal(c.PnlAtTarget, c.Sensitivity[1].Pnl);
    }

    [Fact]
    public void Candidates_AreBuiltAtTheSideYouWouldActuallyTrade()
    {
        // Bought legs lift the ask, sold legs hit the bid. Pricing at mid flatters everything.
        var chain = Chain();
        foreach (var c in StructureFinder.Find(chain, Exp, 110m))
            foreach (var leg in c.Legs)
            {
                var q = chain.Contracts.Single(x => x.Symbol == leg.Symbol);
                Assert.Equal(leg.Action == LegAction.Buy ? q.Ask : q.Bid, leg.Premium);
            }
    }

    [Fact]
    public void Candidates_OnlyUseContractsThatPassTheLiquidityGate()
    {
        // Nothing untradeable should ever be proposed.
        var gate  = new LiquidityGate(MaxSpreadPct: 1m);
        var chain = Chain();
        foreach (var c in StructureFinder.Find(chain, Exp, 110m, gate: gate))
            foreach (var leg in c.Legs)
                Assert.True(gate.Admits(chain.Contracts.Single(x => x.Symbol == leg.Symbol)));
    }

    [Fact]
    public void WorstSpreadPct_ReportsTheWidestLegNotTheAverage()
    {
        // A structure is only as executable as its worst leg.
        var chain = Chain();
        foreach (var c in StructureFinder.Find(chain, Exp, 110m))
        {
            var widest = c.Legs.Max(l => chain.Contracts.Single(x => x.Symbol == l.Symbol).SpreadPct);
            Assert.Equal(widest, c.WorstSpreadPct);
        }
    }

    [Fact]
    public void LongCall_ProfitsWhenTheTargetIsAboveItsStrike()
        => Assert.True(Bullish(115m).First(c => c.Name.Contains("Long call")).PnlAtTarget > 0m);

    [Fact]
    public void ReturnOnRisk_IsNullWhenTheDownsideIsUnbounded()
    {
        var naked = new StructureCandidate("x", [], 0m, null, null, [], 100m, [], 1m);
        Assert.Null(naked.ReturnOnRisk);
    }
}
