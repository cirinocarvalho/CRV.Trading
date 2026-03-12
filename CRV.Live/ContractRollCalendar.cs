namespace CRV.Live;

/// <summary>
/// Determines the active front-month CME equity index futures contract
/// using quarterly roll logic (H=Mar, M=Jun, U=Sep, Z=Dec).
/// Roll date = 8 calendar days before the 3rd Friday of the contract month.
/// </summary>
public static class ContractRollCalendar
{
    private static readonly char[] QuarterCodes = ['H', 'M', 'U', 'Z'];
    private static readonly int[]  QuarterMonths = [3, 6, 9, 12];

    /// <summary>
    /// Returns the active front-month contract ticker, e.g. ActiveContract("NQ", march1) -> "NQH26".
    /// After the roll date (3rd Friday - 8 days), switches to next quarter.
    /// </summary>
    public static string ActiveContract(string rootSymbol, DateTime? asOf = null)
    {
        var date = asOf ?? DateTime.Today;
        // Start from the quarterly contract whose month contains (or just passed) the date
        var (month, year) = GetQuarter(date, 0);

        // Check up to 5 quarters ahead (covers edge cases)
        for (int i = 0; i < 5; i++)
        {
            var rollDate = ComputeRollDate(month, year);
            if (date < rollDate)
            {
                // This contract's roll hasn't happened yet — it's the front month
                char code = QuarterCodes[Array.IndexOf(QuarterMonths, month)];
                return $"{rootSymbol}{code}{year % 100:D2}";
            }

            // Roll already happened, advance to next quarter
            (month, year) = NextQuarter(month, year);
        }

        // Fallback (should never reach here)
        throw new InvalidOperationException("Could not determine active contract");
    }

    /// <summary>
    /// Returns true when within 14 calendar days before the roll date, or after the roll date.
    /// Useful for alerting that a contract is near expiration.
    /// </summary>
    public static bool IsNearRoll(string ticker, DateTime? asOf = null)
    {
        var date = asOf ?? DateTime.Today;
        var rollDate = RollDate(ticker);
        // Within 14 days before roll, or on/after roll date
        return date >= rollDate.AddDays(-14);
    }

    /// <summary>
    /// Returns the roll date for a specific contract ticker (e.g. "NQH26" -> March 12, 2026).
    /// </summary>
    public static DateTime RollDate(string ticker)
    {
        var norm = FuturesSymbol.Normalize(ticker);
        // Last 3 chars: month code + 2-digit year (e.g. H26)
        var suffix = norm[^3..];
        char monthCode = suffix[0];
        int year2 = int.Parse(suffix[1..]);
        int year = 2000 + year2;

        int monthIdx = Array.IndexOf(QuarterCodes, char.ToUpper(monthCode));
        if (monthIdx < 0)
            throw new ArgumentException($"Invalid month code '{monthCode}' in ticker '{ticker}'");

        int month = QuarterMonths[monthIdx];
        return ComputeRollDate(month, year);
    }

    private static DateTime ThirdFriday(int month, int year)
    {
        var first = new DateTime(year, month, 1);
        // Find first Friday
        int daysUntilFriday = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        var firstFriday = first.AddDays(daysUntilFriday);
        // Third Friday = first Friday + 14 days
        return firstFriday.AddDays(14);
    }

    private static DateTime ComputeRollDate(int month, int year)
    {
        return ThirdFriday(month, year).AddDays(-8);
    }

    private static (int month, int year) GetQuarter(DateTime date, int offset)
    {
        // Map date to its quarterly bucket
        int qIdx = date.Month switch
        {
            >= 1 and <= 3  => 0, // H (March)
            >= 4 and <= 6  => 1, // M (June)
            >= 7 and <= 9  => 2, // U (September)
            _              => 3, // Z (December)
        };

        qIdx += offset;
        int yearOffset = 0;
        while (qIdx < 0) { qIdx += 4; yearOffset--; }
        while (qIdx >= 4) { qIdx -= 4; yearOffset++; }

        return (QuarterMonths[qIdx], date.Year + yearOffset);
    }

    private static (int month, int year) NextQuarter(int month, int year)
    {
        int idx = Array.IndexOf(QuarterMonths, month);
        if (idx == 3) // December -> March next year
            return (QuarterMonths[0], year + 1);
        return (QuarterMonths[idx + 1], year);
    }
}
