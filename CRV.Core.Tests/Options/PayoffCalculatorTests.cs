using CRV.Core.Options;
using Xunit;

namespace CRV.Core.Tests.Options;

public class PayoffCalculatorTests
{
    // Canonical single leg: long 1 call, strike 100, premium 2.50, multiplier 100.
    // Cost to open = 2.50 * 100 * 1 = $250.
    private static OptionLeg LongCall100() =>
        new(OptionRight.Call, LegAction.Buy, Strike: 100m, Premium: 2.50m);

    [Fact]
    public void LongCall_AboveStrike_PayoffIsIntrinsicMinusPremium()
    {
        // Settles at 110: intrinsic = 10 → (10 - 2.50) * 100 = 750
        var pnl = PayoffCalculator.PayoffAt(new[] { LongCall100() }, underlying: 110m);
        Assert.Equal(750m, pnl);
    }

    [Fact]
    public void LongCall_BelowStrike_LosesExactlyThePremium()
    {
        // Settles at 90: worthless → -2.50 * 100 = -250
        var pnl = PayoffCalculator.PayoffAt(new[] { LongCall100() }, underlying: 90m);
        Assert.Equal(-250m, pnl);
    }

    [Fact]
    public void LongCall_NetDebitIsPremiumPaid()
    {
        var a = PayoffCalculator.Analyze(new[] { LongCall100() });
        Assert.Equal(250m, a.NetDebit);
    }

    [Fact]
    public void LongCall_MaxLossIsPremiumPaid()
    {
        var a = PayoffCalculator.Analyze(new[] { LongCall100() });
        Assert.Equal(250m, a.MaxLoss);
        Assert.False(a.LossUnbounded);
    }

    [Fact]
    public void LongCall_ProfitIsUnbounded()
    {
        var a = PayoffCalculator.Analyze(new[] { LongCall100() });
        Assert.True(a.ProfitUnbounded);
    }

    [Fact]
    public void LongCall_BreakevenIsStrikePlusPremium()
    {
        var a = PayoffCalculator.Analyze(new[] { LongCall100() });
        Assert.Equal(102.50m, Assert.Single(a.Breakevens));
    }

    [Fact]
    public void Commission_IncreasesNetDebitPerContract()
    {
        // 1 contract * $0.65 commission → debit 250 + 0.65
        var a = PayoffCalculator.Analyze(new[] { LongCall100() }, commissionPerContract: 0.65m);
        Assert.Equal(250.65m, a.NetDebit);
    }

    // ── Bull call spread: long 100c @ 5.00, short 105c @ 2.00 ──────
    // Debit 3.00 → $300. Width 5.00 → max profit (5 - 3) * 100 = $200.
    private static OptionLeg[] BullCallSpread() =>
    [
        new(OptionRight.Call, LegAction.Buy,  Strike: 100m, Premium: 5.00m),
        new(OptionRight.Call, LegAction.Sell, Strike: 105m, Premium: 2.00m),
    ];

    [Fact]
    public void BullCallSpread_NetDebitIsDifferenceOfPremiums()
        => Assert.Equal(300m, PayoffCalculator.Analyze(BullCallSpread()).NetDebit);

    [Fact]
    public void BullCallSpread_MaxProfitIsWidthMinusDebit()
    {
        var a = PayoffCalculator.Analyze(BullCallSpread());
        Assert.Equal(200m, a.MaxProfit);
        Assert.False(a.ProfitUnbounded);
    }

    [Fact]
    public void BullCallSpread_MaxLossIsTheDebit()
        => Assert.Equal(300m, PayoffCalculator.Analyze(BullCallSpread()).MaxLoss);

    [Fact]
    public void BullCallSpread_BreakevenIsLongStrikePlusDebit()
        => Assert.Equal(103m, Assert.Single(PayoffCalculator.Analyze(BullCallSpread()).Breakevens));

    // ── Long butterfly: +95c @7.00, -2x 100c @3.50, +105c @1.50 ────
    // Debit = (7.00 - 7.00 + 1.50) * 100 = $150, all of it at risk.
    // Peak at the body: (5.00 - 1.50) * 100 = $350.
    private static OptionLeg[] LongButterfly() =>
    [
        new(OptionRight.Call, LegAction.Buy,  Strike:  95m, Premium: 7.00m),
        new(OptionRight.Call, LegAction.Sell, Strike: 100m, Premium: 3.50m, Quantity: 2),
        new(OptionRight.Call, LegAction.Buy,  Strike: 105m, Premium: 1.50m),
    ];

    [Fact]
    public void LongButterfly_NetDebitIsWingsMinusBody()
        => Assert.Equal(150m, PayoffCalculator.Analyze(LongButterfly()).NetDebit);

    [Fact]
    public void LongButterfly_PeaksAtTheBodyStrike()
        => Assert.Equal(350m, PayoffCalculator.PayoffAt(LongButterfly(), underlying: 100m));

    [Fact]
    public void LongButterfly_MaxLossIsTheDebitAndBothTailsAreBounded()
    {
        var a = PayoffCalculator.Analyze(LongButterfly());
        Assert.Equal(150m, a.MaxLoss);
        Assert.False(a.LossUnbounded);
        Assert.False(a.ProfitUnbounded);
    }

    [Fact]
    public void LongButterfly_HasTwoBreakevensStraddlingTheBody()
        => Assert.Equal([96.5m, 103.5m], PayoffCalculator.Analyze(LongButterfly()).Breakevens);

    // ── Short put: sell 95p @ 2.00 ─────────────────────────────────
    [Fact]
    public void ShortPut_NetDebitIsNegativeForACredit()
        => Assert.Equal(-200m, PayoffCalculator.Analyze(
            [new(OptionRight.Put, LegAction.Sell, Strike: 95m, Premium: 2.00m)]).NetDebit);

    [Fact]
    public void ShortPut_MaxLossIsCappedAtZeroUnderlying()
    {
        // Underlying cannot go below 0, so the worst case is (95 - 2) * 100 = $9,300.
        var a = PayoffCalculator.Analyze(
            [new(OptionRight.Put, LegAction.Sell, Strike: 95m, Premium: 2.00m)]);
        Assert.Equal(9_300m, a.MaxLoss);
        Assert.False(a.LossUnbounded);
    }

    [Fact]
    public void NakedShortCall_ReportsUnboundedLoss()
    {
        var a = PayoffCalculator.Analyze(
            [new(OptionRight.Call, LegAction.Sell, Strike: 100m, Premium: 2.50m)]);
        Assert.True(a.LossUnbounded);
        Assert.Equal(decimal.MaxValue, a.MaxLoss);
    }

    // ── Iron condor: -95p @1.00, +90p @0.50, -105c @1.00, +110c @0.50
    // Credit 1.00 → $100. Wing width 5.00 → max loss 500 - 100 = $400.
    private static OptionLeg[] IronCondor() =>
    [
        new(OptionRight.Put,  LegAction.Sell, Strike:  95m, Premium: 1.00m),
        new(OptionRight.Put,  LegAction.Buy,  Strike:  90m, Premium: 0.50m),
        new(OptionRight.Call, LegAction.Sell, Strike: 105m, Premium: 1.00m),
        new(OptionRight.Call, LegAction.Buy,  Strike: 110m, Premium: 0.50m),
    ];

    [Fact]
    public void IronCondor_MaxProfitIsTheCreditReceived()
    {
        var a = PayoffCalculator.Analyze(IronCondor());
        Assert.Equal(-100m, a.NetDebit);
        Assert.Equal(100m, a.MaxProfit);
    }

    [Fact]
    public void IronCondor_MaxLossIsWingWidthMinusCredit()
    {
        var a = PayoffCalculator.Analyze(IronCondor());
        Assert.Equal(400m, a.MaxLoss);
        Assert.False(a.LossUnbounded);
    }

    [Fact]
    public void IronCondor_HasTwoBreakevensOutsideTheShortStrikes()
        => Assert.Equal([94m, 106m], PayoffCalculator.Analyze(IronCondor()).Breakevens);

    // ── Sizing ─────────────────────────────────────────────────────
    [Fact]
    public void Quantity_ScalesPayoffLinearly()
    {
        var three = new OptionLeg(OptionRight.Call, LegAction.Buy, 100m, 2.50m, Quantity: 3);
        Assert.Equal(2_250m, PayoffCalculator.PayoffAt([three], underlying: 110m));
    }

    [Fact]
    public void Commission_IsChargedPerContractNotPerLeg()
    {
        // 1 + 2 + 1 = 4 contracts * $0.65 = $2.60 on top of the $150 debit.
        var a = PayoffCalculator.Analyze(LongButterfly(), commissionPerContract: 0.65m);
        Assert.Equal(152.60m, a.NetDebit);
        Assert.Equal(152.60m, a.MaxLoss);
    }

    // ── Payoff curve ───────────────────────────────────────────────

    [Fact]
    public void Curve_IncludesTheExactStrikeEvenWhenTheGridStepsOverIt()
    {
        // 80..120 in 7 steps lands on 80, 85.71, 91.43, 97.14, 102.86, … — never 100.
        // The butterfly's entire peak lives at 100, so the grid alone would draw a
        // blunted tent that understates max profit by a wide margin.
        var curve = PayoffCalculator.Curve(LongButterfly(), from: 80m, to: 120m, steps: 7);
        Assert.Contains(curve, p => p.Underlying == 100m && p.Pnl == 350m);
    }

    [Fact]
    public void Curve_IncludesBreakevens()
    {
        var curve = PayoffCalculator.Curve(LongButterfly(), from: 80m, to: 120m, steps: 7);
        Assert.Contains(curve, p => p.Underlying == 96.5m);
        Assert.Contains(curve, p => p.Underlying == 103.5m);
    }

    [Fact]
    public void Curve_IsSortedAndSpansTheRequestedRange()
    {
        var curve = PayoffCalculator.Curve(LongButterfly(), from: 80m, to: 120m, steps: 7);
        Assert.Equal(80m,  curve[0].Underlying);
        Assert.Equal(120m, curve[^1].Underlying);
        Assert.Equal(curve.OrderBy(p => p.Underlying), curve);
    }

    [Fact]
    public void Curve_HasNoDuplicateUnderlyings()
    {
        // 100 is both a strike and a grid point when the grid divides evenly.
        var curve = PayoffCalculator.Curve(LongButterfly(), from: 90m, to: 110m, steps: 20);
        Assert.Equal(curve.Select(p => p.Underlying).Distinct().Count(), curve.Count);
    }

    [Fact]
    public void Curve_AppliesCommission()
    {
        var plain = PayoffCalculator.Curve(LongButterfly(), 80m, 120m, 7);
        var withC = PayoffCalculator.Curve(LongButterfly(), 80m, 120m, 7, commissionPerContract: 0.65m);
        var a = plain.Single(p => p.Underlying == 100m).Pnl;
        var b = withC.Single(p => p.Underlying == 100m).Pnl;
        Assert.Equal(2.60m, a - b);   // 4 contracts * 0.65
    }

    // ── Where the extremes actually occur ──────────────────────────

    [Fact]
    public void LongPut_BestCaseIsAtAnUnderlyingOfZero()
    {
        // The headline figure for a long put is its value if the stock goes to zero.
        // Correct, and useless unless the display says so.
        var a = PayoffCalculator.Analyze(
            [new(OptionRight.Put, LegAction.Buy, Strike: 755m, Premium: 0.11m)]);

        Assert.Equal(75_489m, a.MaxProfit);
        Assert.Equal(0m, a.MaxProfitAt);
    }

    [Fact]
    public void LongButterfly_ReportsTheBodyStrikeAsWhereItPeaks()
    {
        var a = PayoffCalculator.Analyze(LongButterfly());
        Assert.Equal(100m, a.MaxProfitAt);
    }

    [Fact]
    public void LongCall_HasNoMaxProfitPriceBecauseProfitIsUnbounded()
    {
        var a = PayoffCalculator.Analyze(new[] { LongCall100() });
        Assert.True(a.ProfitUnbounded);
        Assert.Null(a.MaxProfitAt);
    }

    [Fact]
    public void ShortPut_WorstCaseIsAtAnUnderlyingOfZero()
        => Assert.Equal(0m, PayoffCalculator.Analyze(
            [new(OptionRight.Put, LegAction.Sell, Strike: 95m, Premium: 2.00m)]).MaxLossAt);

    [Fact]
    public void NakedShortCall_HasNoMaxLossPriceBecauseLossIsUnbounded()
        => Assert.Null(PayoffCalculator.Analyze(
            [new(OptionRight.Call, LegAction.Sell, Strike: 100m, Premium: 2.50m)]).MaxLossAt);

    // ── Value before expiration ────────────────────────────────────

    private static readonly DateTime Expiry = new(2026, 12, 18);

    private static OptionLeg Dated(OptionRight right, LegAction action, decimal k, decimal prem,
                                   decimal iv = 20m, int qty = 1)
        => new(right, action, k, prem, qty, 100, $"T {k}", iv, Expiry);

    [Fact]
    public void ValueAt_ConvergesOnTheExpirationPayoff()
    {
        // The invariant that ties the two together: with no time left, an option is worth
        // its intrinsic value, so this must agree with PayoffAt to the cent.
        var legs = new[] { Dated(OptionRight.Call, LegAction.Buy, 100m, 2.50m) };

        foreach (var price in new[] { 80m, 100m, 110m, 130m })
        {
            var atExpiry = PayoffCalculator.PayoffAt(legs, price);
            var valued   = PayoffCalculator.ValueAt(legs, price, Expiry);
            Assert.Equal((double)atExpiry, (double)valued!.Value, 2);
        }
    }

    [Fact]
    public void BeforeExpiry_AnAtTheMoneyLongIsWorthMoreThanItsExpirationPayoff()
    {
        // At the money the expiration payoff is just the premium lost. Reading that as
        // "what I get if it trades there" is exactly the error this method exists to fix.
        var legs = new[] { Dated(OptionRight.Call, LegAction.Buy, 100m, 2.50m) };

        var atExpiry = PayoffCalculator.PayoffAt(legs, 100m);
        var earlier  = PayoffCalculator.ValueAt(legs, 100m, Expiry.AddMonths(-3))!.Value;

        Assert.Equal(-250m, atExpiry);
        Assert.True(earlier > atExpiry, $"time value should soften the loss: {earlier} vs {atExpiry}");
    }

    [Fact]
    public void RaisingVolatility_HelpsALongAndHurtsAShort()
    {
        var asOf = Expiry.AddMonths(-3);
        var lng  = new[] { Dated(OptionRight.Call, LegAction.Buy,  100m, 2.50m) };
        var shrt = new[] { Dated(OptionRight.Call, LegAction.Sell, 100m, 2.50m) };

        Assert.True(PayoffCalculator.ValueAt(lng,  100m, asOf, ivShiftPoints: 10m)
                  > PayoffCalculator.ValueAt(lng,  100m, asOf));
        Assert.True(PayoffCalculator.ValueAt(shrt, 100m, asOf, ivShiftPoints: 10m)
                  < PayoffCalculator.ValueAt(shrt, 100m, asOf));
    }

    [Fact]
    public void ValueAt_IsNullWhenALegCannotBeValued()
    {
        // A leg with no volatility or no expiry cannot be priced. Returning zero would be
        // indistinguishable from "worth nothing".
        Assert.Null(PayoffCalculator.ValueAt(
            [new(OptionRight.Call, LegAction.Buy, 100m, 2.50m)], 110m, DateTime.UtcNow));

        Assert.Null(PayoffCalculator.ValueAt(
            [Dated(OptionRight.Call, LegAction.Buy, 100m, 2.50m, iv: 0m)], 110m, Expiry.AddMonths(-1)));
    }

    [Fact]
    public void ButterflyBeforeExpiry_IsFlatterThanAtExpiry()
    {
        // A butterfly's peak only materialises as time runs out; early on it is muted.
        // Anyone sizing on the expiration peak is assuming they hold to settlement.
        OptionLeg[] fly =
        [
            Dated(OptionRight.Call, LegAction.Buy,   95m, 7.00m),
            Dated(OptionRight.Call, LegAction.Sell, 100m, 3.50m, qty: 2),
            Dated(OptionRight.Call, LegAction.Buy,  105m, 1.50m),
        ];

        var peakAtExpiry = PayoffCalculator.PayoffAt(fly, 100m);
        var peakEarlier  = PayoffCalculator.ValueAt(fly, 100m, Expiry.AddMonths(-3))!.Value;

        Assert.True(peakEarlier < peakAtExpiry,
            $"early peak {peakEarlier} should be below the expiration peak {peakAtExpiry}");
    }
}
