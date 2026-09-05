namespace CRV.Core.Options;

/// <summary>
/// Black-Scholes valuation, used to answer what a structure is worth <em>before</em>
/// expiration.
/// <para>Expiration payoff answers "if it settles there"; most option positions close well
/// before that, and the difference is the remaining time value. This is what closes that
/// gap.</para>
/// <para>Assumes European exercise. Equity and ETF options are American, but early exercise
/// is only rational for a deep in-the-money put or a call before a dividend, so the model
/// understates those two cases and is otherwise the standard approximation.</para>
/// </summary>
public static class BlackScholes
{
    /// <param name="spot">Underlying price.</param>
    /// <param name="strike">Strike.</param>
    /// <param name="years">Time to expiry in years. Zero or less returns intrinsic value.</param>
    /// <param name="rate">Continuously compounded risk-free rate, as a decimal (0.04 = 4%).</param>
    /// <param name="vol">Implied volatility as a decimal (0.20 = 20%).</param>
    public static double Price(
        OptionRight right, double spot, double strike, double years, double rate, double vol)
    {
        // At or past expiry there is no optionality left — only what it is worth exercised.
        if (years <= 0d || vol <= 0d)
        {
            double discounted = strike * Math.Exp(-rate * Math.Max(years, 0d));
            return right == OptionRight.Call
                ? Math.Max(spot - discounted, 0d)
                : Math.Max(discounted - spot, 0d);
        }
        if (spot <= 0d) return right == OptionRight.Call ? 0d : strike * Math.Exp(-rate * years);

        double sqrtT = Math.Sqrt(years);
        double d1 = (Math.Log(spot / strike) + (rate + 0.5d * vol * vol) * years) / (vol * sqrtT);
        double d2 = d1 - vol * sqrtT;
        double df = Math.Exp(-rate * years);

        return right == OptionRight.Call
            ? spot * Cdf(d1) - strike * df * Cdf(d2)
            : strike * df * Cdf(-d2) - spot * Cdf(-d1);
    }

    /// <summary>
    /// Standard normal CDF via Abramowitz &amp; Stegun 26.2.17 — accurate to about 7.5e-8,
    /// which is far below the noise in any implied volatility fed into it.
    /// </summary>
    private static double Cdf(double x)
    {
        const double a1 = 0.319381530, a2 = -0.356563782, a3 = 1.781477937,
                     a4 = -1.821255978, a5 = 1.330274429, p = 0.2316419;

        bool negative = x < 0d;
        x = Math.Abs(x);

        double t = 1d / (1d + p * x);
        double poly = t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5))));
        double pdf = Math.Exp(-0.5d * x * x) / Math.Sqrt(2d * Math.PI);

        double upper = 1d - pdf * poly;          // Φ(|x|)
        return negative ? 1d - upper : upper;    // Φ(−x) = 1 − Φ(x)
    }
}
