namespace CRV.Core.Statistics;

/// <summary>
/// Two-sided 95% critical values of Student's t.
/// <para>
/// Needed because the interesting subsets of the live book are small — the cells
/// that looked promising hold 13 to 16 trades — and at that size the normal 1.96
/// understates the interval by around a fifth, which is the difference between
/// "no evidence" and "edge".
/// </para>
/// </summary>
public static class StudentT
{
    // Low df sits far enough into the tail that the series expansion below drifts
    // past the third decimal; these are the exact tabulated values.
    private static readonly double[] Exact =
        { 12.7062, 4.3027, 3.1824, 2.7764, 2.5706, 2.4469, 2.3646, 2.3060 };

    private const double Z = 1.959963985;   // the standard normal 97.5th percentile

    /// <summary>Critical value for a two-sided 95% interval on <paramref name="df"/> degrees of freedom.</summary>
    public static double TwoSided95(int df)
    {
        if (df <= 0) return double.PositiveInfinity;   // a single observation has no interval
        if (df <= Exact.Length) return Exact[df - 1];

        // Abramowitz & Stegun 26.7.5 — the same family of expansion used for the
        // normal CDF in BlackScholes. Accurate to ~1e-3 from df = 5 upward.
        double z = Z, z2 = z * z, z3 = z2 * z, z5 = z3 * z2, z7 = z5 * z2, z9 = z7 * z2;
        double v = df, v2 = v * v, v3 = v2 * v, v4 = v3 * v;

        return z
             + (z3 + z) / (4 * v)
             + (5 * z5 + 16 * z3 + 3 * z) / (96 * v2)
             + (3 * z7 + 19 * z5 + 17 * z3 - 15 * z) / (384 * v3)
             + (79 * z9 + 776 * z7 + 1482 * z5 - 1920 * z3 - 945 * z) / (92160 * v4);
    }
}
