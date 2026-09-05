using CRV.Core.Options;
using Xunit;

namespace CRV.Core.Tests.Options;

public class BlackScholesTests
{
    private static double Call(double s, double k, double t, double r, double v)
        => BlackScholes.Price(OptionRight.Call, s, k, t, r, v);
    private static double Put(double s, double k, double t, double r, double v)
        => BlackScholes.Price(OptionRight.Put, s, k, t, r, v);

    [Fact]
    public void MatchesAKnownTextbookValue()
    {
        // S=100 K=100 T=1 r=5% vol=20% → 10.4506 for the call, 5.5735 for the put.
        Assert.Equal(10.4506, Call(100, 100, 1, 0.05, 0.20), 3);
        Assert.Equal(5.5735,  Put (100, 100, 1, 0.05, 0.20), 3);
    }

    [Fact]
    public void SatisfiesPutCallParity()
    {
        // C − P = S − K·e^(−rT). An implementation can be wrong in ways that still look
        // plausible; parity is the invariant that catches most of them.
        const double s = 123.45, k = 110, t = 0.42, r = 0.043, v = 0.31;
        double lhs = Call(s, k, t, r, v) - Put(s, k, t, r, v);
        double rhs = s - k * Math.Exp(-r * t);
        Assert.Equal(rhs, lhs, 8);
    }

    [Fact]
    public void AtExpiry_IsIntrinsicValue()
    {
        Assert.Equal(10d, Call(110, 100, 0, 0.05, 0.20), 8);
        Assert.Equal(0d,  Call( 90, 100, 0, 0.05, 0.20), 8);
        Assert.Equal(10d, Put ( 90, 100, 0, 0.05, 0.20), 8);
    }

    [Fact]
    public void ZeroVolatility_IsDiscountedIntrinsic()
    {
        // With no uncertainty the option is worth exactly what exercising it will be worth.
        Assert.Equal(110 - 100 * Math.Exp(-0.05), Call(110, 100, 1, 0.05, 0d), 8);
    }

    [Fact]
    public void DeepOutOfTheMoney_ApproachesZero()
        => Assert.True(Call(50, 200, 0.25, 0.04, 0.20) < 0.001);

    [Fact]
    public void DeepInTheMoney_ApproachesDiscountedIntrinsic()
    {
        double expected = 200 - 50 * Math.Exp(-0.04 * 0.25);
        Assert.Equal(expected, Call(200, 50, 0.25, 0.04, 0.20), 4);
    }

    [Fact]
    public void MoreTime_IsWorthMore()
    {
        // Optionality has positive time value; a longer-dated option cannot be worth less.
        Assert.True(Call(100, 100, 0.5, 0.04, 0.20) > Call(100, 100, 0.1, 0.04, 0.20));
    }

    [Fact]
    public void HigherVolatility_IsWorthMore()
        => Assert.True(Call(100, 100, 0.25, 0.04, 0.40) > Call(100, 100, 0.25, 0.04, 0.10));

    [Fact]
    public void TimeValueIsWhatSeparatesThisFromExpirationPayoff()
    {
        // The whole reason this exists: at the money, expiration payoff is zero while the
        // option still carries real value. Anything reading the expiry number as "what I
        // get if it trades there" is off by exactly this.
        double now = Call(100, 100, 0.25, 0.04, 0.30);
        Assert.True(now > 5d, $"at-the-money quarter-year option should carry time value, got {now}");
        Assert.Equal(0d, Call(100, 100, 0, 0.04, 0.30), 8);
    }

    [Fact]
    public void TheNormalCdfIsCorrectlyCentred()
    {
        // Parity alone does not catch a CDF that is wrong by a constant factor — it
        // constrains the difference between call and put, and a shared error cancels.
        // An at-the-money call with no rate and no drift must be worth about 0.4·σ·√T·S.
        double atm = Call(100, 100, 1, 0d, 0.20);
        Assert.InRange(atm, 7.5, 8.5);
    }

    [Fact]
    public void SymmetricStrikesAroundSpot_ArePricedSymmetrically()
    {
        // With no rate, a call K% above spot and a put the same distance below should be
        // close in value. A mis-centred CDF breaks this badly.
        double c = Call(100, 110, 0.5, 0d, 0.25);
        double p = Put (100,  90, 0.5, 0d, 0.25);
        Assert.Equal(c, p, 0);
    }
}
