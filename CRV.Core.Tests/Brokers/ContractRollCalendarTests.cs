using CRV.Live;
using Xunit;

namespace CRV.Core.Tests.Brokers;

public class ContractRollCalendarTests
{
    [Theory]
    [InlineData("NQ",  "2026-03-01", "NQH26")]   // before March roll
    [InlineData("NQ",  "2026-03-11", "NQH26")]   // day before roll
    [InlineData("NQ",  "2026-03-12", "NQM26")]   // roll day -> June
    [InlineData("NQ",  "2026-04-15", "NQM26")]   // well into June contract
    [InlineData("MNQ", "2026-03-15", "MNQM26")]  // micro follows same calendar
    [InlineData("ES",  "2026-03-15", "ESM26")]    // ES same dates
    [InlineData("NQ",  "2026-01-05", "NQH26")]   // January -> March contract
    [InlineData("NQ",  "2026-06-10", "NQM26")]   // before June 11 roll
    [InlineData("NQ",  "2026-06-11", "NQU26")]   // June roll -> September
    [InlineData("NQ",  "2026-12-09", "NQZ26")]   // before Dec 10 roll
    [InlineData("NQ",  "2026-12-10", "NQH27")]   // Dec roll -> March 2027
    public void ActiveContract_ReturnsCorrectFrontMonth(string root, string dateStr, string expected)
    {
        var date = DateTime.Parse(dateStr);
        Assert.Equal(expected, ContractRollCalendar.ActiveContract(root, date));
    }

    [Theory]
    [InlineData("NQH26", "2026-02-20", false)]  // 20 days before roll
    [InlineData("NQH26", "2026-02-26", true)]   // 14 days before roll
    [InlineData("NQH26", "2026-03-12", true)]   // roll day
    [InlineData("NQH26", "2026-03-15", true)]   // after roll
    public void IsNearRoll_ReturnsCorrectly(string ticker, string dateStr, bool expected)
    {
        var date = DateTime.Parse(dateStr);
        Assert.Equal(expected, ContractRollCalendar.IsNearRoll(ticker, date));
    }

    [Fact]
    public void RollDate_NQH26_ReturnsMarch12()
    {
        Assert.Equal(new DateTime(2026, 3, 12), ContractRollCalendar.RollDate("NQH26"));
    }

    [Fact]
    public void RollDate_AcceptsSlashFormat()
    {
        // Should normalize /NQH26 -> NQH26 and still work
        Assert.Equal(new DateTime(2026, 3, 12), ContractRollCalendar.RollDate("/NQH26"));
    }
}
