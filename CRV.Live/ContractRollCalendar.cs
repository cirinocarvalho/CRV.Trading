namespace CRV.Live;

/// <summary>
/// Determines the active front-month CME futures contract based on product-specific
/// roll cycles.
///
/// Equity indices (NQ, ES, YM, RTY, MNQ, MES, MYM, M2K): Quarterly H/M/U/Z
/// Gold (GC, MGC): Bi-monthly G/J/M/Q/V/Z
/// Crude Oil (CL, MCL): Monthly F/G/H/J/K/M/N/Q/U/V/X/Z
/// Bitcoin (BTC, MBT): Monthly F/G/H/J/K/M/N/Q/U/V/X/Z
///
/// Roll date = 8 calendar days before the 3rd Friday of the contract month.
/// </summary>
public static class ContractRollCalendar
{
    // ── Month code → calendar month mapping ──────────────────────────
    private static readonly Dictionary<char, int> MonthCodeToMonth = new()
    {
        ['F'] = 1,  ['G'] = 2,  ['H'] = 3,  ['J'] = 4,
        ['K'] = 5,  ['M'] = 6,  ['N'] = 7,  ['Q'] = 8,
        ['U'] = 9,  ['V'] = 10, ['X'] = 11, ['Z'] = 12,
    };

    private static readonly Dictionary<int, char> MonthToCode =
        MonthCodeToMonth.ToDictionary(kv => kv.Value, kv => kv.Key);

    // ── Roll cycles per product family ───────────────────────────────
    // Quarterly: H(3), M(6), U(9), Z(12)
    private static readonly int[] Quarterly = [3, 6, 9, 12];

    // Bi-monthly (Gold): G(2), J(4), M(6), Q(8), V(10), Z(12)
    private static readonly int[] BiMonthly = [2, 4, 6, 8, 10, 12];

    // Monthly: every month
    private static readonly int[] Monthly = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    /// <summary>
    /// Returns true if the root symbol uses a quarterly roll cycle.
    /// Used to decide whether contract mismatch warnings are meaningful.
    /// </summary>
    public static bool IsQuarterly(string rootSymbol)
        => GetCycleForRoot(rootSymbol) == Quarterly;

    /// <summary>
    /// Returns the active front-month contract ticker, e.g. ActiveContract("NQ") -> "NQH26".
    /// After the roll date (3rd Friday - 8 days), switches to the next contract in the cycle.
    /// </summary>
    public static string ActiveContract(string rootSymbol, DateTime? asOf = null)
    {
        var date = asOf ?? DateTime.Today;
        var cycle = GetCycleForRoot(rootSymbol);

        // Find the cycle month that contains (or is nearest after) the current date
        var (month, year) = GetCurrentCycleMonth(date, cycle);

        // Check forward through the cycle to find the first contract whose roll hasn't happened
        for (int i = 0; i < cycle.Length * 2; i++)
        {
            var rollDate = ComputeRollDate(month, year);
            if (date < rollDate)
            {
                // This contract's roll hasn't happened yet — it's the front month
                char code = MonthToCode[month];
                return $"{rootSymbol}{code}{year % 100:D2}";
            }

            // Roll already happened, advance to next contract in cycle
            (month, year) = NextInCycle(month, year, cycle);
        }

        throw new InvalidOperationException($"Could not determine active contract for {rootSymbol}");
    }

    /// <summary>
    /// Returns true when within 14 calendar days before the roll date, or after the roll date.
    /// Useful for alerting that a contract is near expiration.
    /// </summary>
    public static bool IsNearRoll(string ticker, DateTime? asOf = null)
    {
        var date = asOf ?? DateTime.Today;
        var rollDate = RollDate(ticker);
        return date >= rollDate.AddDays(-14);
    }

    /// <summary>
    /// Returns the roll date for a specific contract ticker (e.g. "NQH26" -> March 12, 2026).
    /// </summary>
    public static DateTime RollDate(string ticker)
    {
        var norm = FuturesSymbol.Normalize(ticker);
        var suffix = norm[^3..];
        char monthCode = char.ToUpper(suffix[0]);
        int year2 = int.Parse(suffix[1..]);
        int year = 2000 + year2;

        if (!MonthCodeToMonth.TryGetValue(monthCode, out int month))
            throw new ArgumentException($"Invalid month code '{monthCode}' in ticker '{ticker}'");

        return ComputeRollDate(month, year);
    }

    /// <summary>
    /// Splits a date range into per-contract segments, each with the correct ticker.
    /// E.g. (NQ, Dec 1 2025, Mar 30 2026) → [("NQZ25", Dec1..Mar5), ("NQH26", Mar5..Mar30)]
    /// </summary>
    public static List<(string Ticker, DateTime From, DateTime To)> SplitByContract(
        string rootSymbol, DateTime from, DateTime to)
    {
        var segments = new List<(string Ticker, DateTime From, DateTime To)>();
        var cursor = from;

        while (cursor < to)
        {
            var ticker = ActiveContract(rootSymbol, cursor);
            var roll = RollDate(ticker);

            var segEnd = roll < to ? roll : to;
            if (segEnd <= cursor)
            {
                cursor = roll;
                continue;
            }

            segments.Add((ticker, cursor, segEnd));
            cursor = segEnd;
        }

        return segments;
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static DateTime ThirdFriday(int month, int year)
    {
        var first = new DateTime(year, month, 1);
        int daysUntilFriday = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        var firstFriday = first.AddDays(daysUntilFriday);
        return firstFriday.AddDays(14);
    }

    private static DateTime ComputeRollDate(int month, int year)
        => ThirdFriday(month, year).AddDays(-8);

    /// <summary>
    /// Returns the roll cycle for a given root symbol.
    /// </summary>
    private static int[] GetCycleForRoot(string rootSymbol)
    {
        var r = rootSymbol.ToUpperInvariant();
        // Gold: GC, MGC
        if (r is "GC" or "MGC") return BiMonthly;
        // Crude Oil: CL, MCL  |  Bitcoin: BTC, MBT
        if (r is "CL" or "MCL" or "BTC" or "MBT") return Monthly;
        // Everything else (NQ, ES, YM, RTY, MNQ, MES, MYM, M2K): quarterly
        return Quarterly;
    }

    /// <summary>
    /// Finds the cycle month that contains or is the nearest cycle month at/before the given date.
    /// </summary>
    private static (int month, int year) GetCurrentCycleMonth(DateTime date, int[] cycle)
    {
        // Find the latest cycle month that is <= date.Month
        for (int i = cycle.Length - 1; i >= 0; i--)
        {
            if (cycle[i] <= date.Month)
                return (cycle[i], date.Year);
        }
        // date.Month is before the first cycle month this year → use last cycle month of previous year
        return (cycle[^1], date.Year - 1);
    }

    /// <summary>
    /// Advance to the next contract month in the cycle.
    /// </summary>
    private static (int month, int year) NextInCycle(int month, int year, int[] cycle)
    {
        int idx = Array.IndexOf(cycle, month);
        if (idx < 0 || idx == cycle.Length - 1)
            return (cycle[0], year + 1);
        return (cycle[idx + 1], year);
    }
}
