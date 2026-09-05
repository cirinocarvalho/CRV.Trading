using CRV.Core.Statistics;
using Xunit;

namespace CRV.Core.Tests.Statistics;

/// <summary>
/// Two-sided 95% critical values, checked against a printed t-table. These matter
/// most where the samples are smallest: the promising cells in the live book run
/// 13-16 trades, and using 1.96 there would understate the interval by a fifth.
/// </summary>
public class StudentTTests
{
    [Theory]
    [InlineData(1,  12.706)]
    [InlineData(2,   4.303)]
    [InlineData(3,   3.182)]
    [InlineData(4,   2.776)]
    [InlineData(5,   2.571)]
    [InlineData(10,  2.228)]
    [InlineData(12,  2.179)]   // n = 13, the retest-mgc Asia cell
    [InlineData(13,  2.160)]   // n = 14, the retest-mnq NY cell
    [InlineData(15,  2.131)]
    [InlineData(20,  2.086)]
    [InlineData(30,  2.042)]
    [InlineData(60,  2.000)]
    [InlineData(120, 1.980)]
    [InlineData(175, 1.974)]   // n = 176, the whole live book
    public void CriticalValueMatchesTheTable(int df, double expected)
        => Assert.Equal(expected, StudentT.TwoSided95(df), 3);

    [Fact]
    public void LargeSamplesConvergeOnTheNormalValue()
        => Assert.Equal(1.960, StudentT.TwoSided95(100_000), 3);

    [Fact]
    public void ZeroDegreesOfFreedomHasNoInterval()
        => Assert.True(double.IsPositiveInfinity(StudentT.TwoSided95(0)));
}
