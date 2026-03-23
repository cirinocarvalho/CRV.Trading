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

    // ── Gold (GC/MGC) — bi-monthly cycle: G/J/M/Q/V/Z ──────────
    [Theory]
    [InlineData("GC",  "2026-03-20", "GCJ26")]    // March → April contract
    [InlineData("MGC", "2026-03-20", "MGCJ26")]   // Micro Gold same cycle
    [InlineData("GC",  "2026-01-15", "GCG26")]    // January → February contract
    [InlineData("GC",  "2026-04-15", "GCM26")]    // April → past J roll → June
    public void ActiveContract_Gold_CorrectCycle(string root, string dateStr, string expected)
    {
        var date = DateTime.Parse(dateStr);
        Assert.Equal(expected, ContractRollCalendar.ActiveContract(root, date));
    }

    // ── Crude Oil (CL/MCL) — monthly cycle ───────────────────────
    [Theory]
    [InlineData("CL",  "2026-03-20", "CLJ26")]    // March → past H roll → April
    [InlineData("MCL", "2026-03-20", "MCLJ26")]   // Micro CL same cycle
    [InlineData("CL",  "2026-05-01", "CLK26")]    // May → May contract (before roll)
    public void ActiveContract_CrudeOil_CorrectCycle(string root, string dateStr, string expected)
    {
        var date = DateTime.Parse(dateStr);
        Assert.Equal(expected, ContractRollCalendar.ActiveContract(root, date));
    }

    [Fact]
    public void RollDate_AcceptsNonQuarterlyMonthCodes()
    {
        // J = April, K = May — these should not throw
        var rollJ = ContractRollCalendar.RollDate("MGCJ26");
        var rollK = ContractRollCalendar.RollDate("MCLK26");
        Assert.Equal(4, rollJ.Month);
        Assert.Equal(5, rollK.Month);
    }

    [Fact]
    public void IsQuarterly_ReturnsTrueForEquities()
    {
        Assert.True(ContractRollCalendar.IsQuarterly("NQ"));
        Assert.True(ContractRollCalendar.IsQuarterly("MES"));
        Assert.False(ContractRollCalendar.IsQuarterly("GC"));
        Assert.False(ContractRollCalendar.IsQuarterly("CL"));
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
