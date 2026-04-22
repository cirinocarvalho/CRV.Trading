using CRV.Live;
using Xunit;

namespace CRV.Core.Tests.Brokers;

public class ContractRollCalendarTests
{
    // ── Equity indices (NQ/ES/YM/RTY/MNQ…) — quarterly H/M/U/Z ──
    // Expiration = 3rd Friday of contract month; roll = expiration − 8 days.
    [Theory]
    [InlineData("NQ",  "2026-03-01", "NQH26")]   // before March roll
    [InlineData("NQ",  "2026-03-11", "NQH26")]   // day before roll
    [InlineData("NQ",  "2026-03-12", "NQM26")]   // roll day -> June
    [InlineData("NQ",  "2026-04-15", "NQM26")]   // well into June contract
    [InlineData("MNQ", "2026-03-15", "MNQM26")]  // micro follows same calendar
    [InlineData("ES",  "2026-03-15", "ESM26")]   // ES same dates
    [InlineData("NQ",  "2026-01-05", "NQH26")]   // January -> March contract
    [InlineData("NQ",  "2026-06-10", "NQM26")]   // before June 11 roll
    [InlineData("NQ",  "2026-06-11", "NQU26")]   // June roll -> September
    [InlineData("NQ",  "2026-12-09", "NQZ26")]   // before Dec 10 roll
    [InlineData("NQ",  "2026-12-10", "NQH27")]   // Dec roll -> March 2027
    public void ActiveContract_Equity_ReturnsCorrectFrontMonth(string root, string dateStr, string expected)
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

    // ── Gold (GC/MGC) — bi-monthly cycle: G/J/M/Q/V/Z ──
    // Expiration = 3rd-last business day of month PRIOR to contract; roll = expiration − 5 days.
    // GCJ26 (April delivery): last BD of March = Tue Mar 31; 3rd-last = Fri Mar 27; roll = Mar 22.
    [Theory]
    [InlineData("GC",  "2026-03-20", "GCJ26")]    // before Mar 22 roll
    [InlineData("GC",  "2026-03-22", "GCM26")]    // roll day → June contract
    [InlineData("MGC", "2026-03-20", "MGCJ26")]   // Micro Gold same cycle
    [InlineData("GC",  "2026-01-15", "GCG26")]    // GCG26 (Feb): roll = Jan 23
    [InlineData("GC",  "2026-04-15", "GCM26")]    // GCM26 (June): roll = May 22
    public void ActiveContract_Gold_CorrectCycle(string root, string dateStr, string expected)
    {
        var date = DateTime.Parse(dateStr);
        Assert.Equal(expected, ContractRollCalendar.ActiveContract(root, date));
    }

    // ── Crude Oil (CL/MCL) — monthly cycle ──
    // Expiration = 3 business days before 25th of month PRIOR to contract; roll = expiration − 5 days.
    // CLJ26 (April delivery): 25th Mar = Wed; back 3 BDs = Fri Mar 20; roll = Mar 15.
    // CLK26 (May delivery):   25th Apr = Sat; back 3 BDs = Wed Apr 22; roll = Apr 17.
    // CLM26 (June delivery):  25th May = Mon; back 3 BDs = Wed May 20; roll = May 15.
    [Theory]
    [InlineData("CL",  "2026-03-15", "CLK26")]    // on CLJ26 roll day → CLK26
    [InlineData("CL",  "2026-03-14", "CLJ26")]    // day before CLJ26 roll
    [InlineData("CL",  "2026-04-16", "CLK26")]    // day before CLK26 roll
    [InlineData("CL",  "2026-04-17", "CLM26")]    // CLK26 roll day → CLM26
    [InlineData("CL",  "2026-04-22", "CLM26")]    // CLK26 last trading day — must be on CLM26
    [InlineData("CL",  "2026-05-01", "CLM26")]    // comfortably on CLM26
    [InlineData("MCL", "2026-04-22", "MCLM26")]   // Micro follows same calendar
    public void ActiveContract_CrudeOil_CorrectCycle(string root, string dateStr, string expected)
    {
        var date = DateTime.Parse(dateStr);
        Assert.Equal(expected, ContractRollCalendar.ActiveContract(root, date));
    }

    // ── Bitcoin (BTC/MBT) — monthly cycle ──
    // Expiration = last Friday of contract month; roll = expiration − 5 days.
    // BTCF26 (Jan delivery): last Fri Jan = Jan 30; roll = Jan 25.
    // BTCG26 (Feb delivery): last Fri Feb = Feb 27; roll = Feb 22.
    [Theory]
    [InlineData("BTC", "2026-01-24", "BTCF26")]   // day before BTCF26 roll
    [InlineData("BTC", "2026-01-25", "BTCG26")]   // BTCF26 roll day → BTCG26
    [InlineData("BTC", "2026-01-30", "BTCG26")]   // BTCF26 last trade — on BTCG26
    [InlineData("MBT", "2026-01-25", "MBTG26")]   // Micro follows same calendar
    public void ActiveContract_Bitcoin_CorrectCycle(string root, string dateStr, string expected)
    {
        var date = DateTime.Parse(dateStr);
        Assert.Equal(expected, ContractRollCalendar.ActiveContract(root, date));
    }

    // ── Roll-date spot checks ──
    [Fact]
    public void RollDate_NQH26_ReturnsMarch12() =>
        Assert.Equal(new DateTime(2026, 3, 12), ContractRollCalendar.RollDate("NQH26"));

    [Fact]
    public void RollDate_AcceptsSlashFormat() =>
        Assert.Equal(new DateTime(2026, 3, 12), ContractRollCalendar.RollDate("/NQH26"));

    [Fact]
    public void RollDate_CLK26_UsesPriorMonth25thRule()
    {
        // CLK26 last trade = Apr 22, 2026; roll = Apr 17.
        Assert.Equal(new DateTime(2026, 4, 17), ContractRollCalendar.RollDate("CLK26"));
    }

    [Fact]
    public void RollDate_GCJ26_UsesPriorMonthBusinessDayRule()
    {
        // GCJ26 last trade = Mar 27, 2026 (3rd-last BD of March); roll = Mar 22.
        Assert.Equal(new DateTime(2026, 3, 22), ContractRollCalendar.RollDate("GCJ26"));
    }

    [Fact]
    public void RollDate_BTCF26_UsesLastFridayOfMonth()
    {
        // BTCF26 last trade = Jan 30, 2026 (last Friday); roll = Jan 25.
        Assert.Equal(new DateTime(2026, 1, 25), ContractRollCalendar.RollDate("BTCF26"));
    }

    [Fact]
    public void LastTradingDay_MatchesCmeRulesPerFamily()
    {
        // Equity index = 3rd Friday
        Assert.Equal(new DateTime(2026, 3, 20), ContractRollCalendar.LastTradingDay("NQH26"));
        // Crude = 3 BD before 25th of prior month
        Assert.Equal(new DateTime(2026, 4, 22), ContractRollCalendar.LastTradingDay("CLK26"));
        // Gold = 3rd-last BD of prior month
        Assert.Equal(new DateTime(2026, 3, 27), ContractRollCalendar.LastTradingDay("GCJ26"));
        // Bitcoin = last Friday of contract month
        Assert.Equal(new DateTime(2026, 1, 30), ContractRollCalendar.LastTradingDay("BTCF26"));
    }

    [Fact]
    public void RollDate_AcceptsNonQuarterlyMonthCodes()
    {
        // Just verify the call doesn't throw for J/K month codes; specific dates are
        // covered by the RollDate_* tests above.
        _ = ContractRollCalendar.RollDate("MGCJ26");
        _ = ContractRollCalendar.RollDate("MCLK26");
    }

    [Fact]
    public void IsQuarterly_ReturnsTrueForEquities()
    {
        Assert.True(ContractRollCalendar.IsQuarterly("NQ"));
        Assert.True(ContractRollCalendar.IsQuarterly("MES"));
        Assert.False(ContractRollCalendar.IsQuarterly("GC"));
        Assert.False(ContractRollCalendar.IsQuarterly("CL"));
        Assert.False(ContractRollCalendar.IsQuarterly("BTC"));
    }
}
