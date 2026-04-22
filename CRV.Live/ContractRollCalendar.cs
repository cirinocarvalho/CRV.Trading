namespace CRV.Live;

/// <summary>
/// Determines the active front-month CME futures contract based on product-specific
/// roll cycles and expiration rules.
///
/// Product families and their last-trading-day rule:
///   Equity indices (NQ, ES, YM, RTY): 3rd Friday of contract month
///   Gold (GC, MGC):                   3rd-last business day of the month PRIOR to contract month
///   Crude Oil (CL, MCL):              3 business days before the 25th of the month PRIOR to contract month
///   Bitcoin (BTC, MBT):               last Friday of contract month
///
/// The "active front month" rolls a small buffer BEFORE last-trading-day so the
/// engine switches to the next contract while liquidity still exists on both.
/// Buffer is 8 calendar days for equity indices (matches historical CME data-vendor
/// convention) and 5 calendar days for other families.
///
/// Business-day calculations treat Mon–Fri as business days. CME holidays are not
/// modeled — they can shift the "true" last-trading-day by at most one day. When
/// this matters, use the ContractOverride escape hatch instead of patching here.
/// </summary>
public static class ContractRollCalendar
{
    // ── Product family ──────────────────────────────────────────────
    private enum RollFamily { Quarterly, Gold, Crude, Bitcoin }

    // Calendar-day buffer applied before last-trading-day to pick the active contract.
    private const int EquityBufferDays = 8;
    private const int OtherBufferDays  = 5;

    // ── Month code ↔ calendar month ─────────────────────────────────
    private static readonly Dictionary<char, int> MonthCodeToMonth = new()
    {
        ['F'] = 1,  ['G'] = 2,  ['H'] = 3,  ['J'] = 4,
        ['K'] = 5,  ['M'] = 6,  ['N'] = 7,  ['Q'] = 8,
        ['U'] = 9,  ['V'] = 10, ['X'] = 11, ['Z'] = 12,
    };

    private static readonly Dictionary<int, char> MonthToCode =
        MonthCodeToMonth.ToDictionary(kv => kv.Value, kv => kv.Key);

    // ── Roll cycles per product family ──────────────────────────────
    private static readonly int[] Quarterly = [3, 6, 9, 12];
    private static readonly int[] BiMonthly = [2, 4, 6, 8, 10, 12];
    private static readonly int[] Monthly   = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>True for equity-index roots (quarterly H/M/U/Z cycle).</summary>
    public static bool IsQuarterly(string rootSymbol) => GetFamily(rootSymbol) == RollFamily.Quarterly;

    /// <summary>
    /// Returns the active front-month contract ticker, e.g. <c>ActiveContract("NQ")</c> → <c>"NQH26"</c>.
    /// Rolls to the next contract in the cycle once the current contract is within its roll buffer.
    /// </summary>
    public static string ActiveContract(string rootSymbol, DateTime? asOf = null)
    {
        var date   = asOf ?? DateTime.Today;
        var family = GetFamily(rootSymbol);
        var cycle  = GetCycleForFamily(family);

        var (month, year) = GetCurrentCycleMonth(date, cycle);

        // Walk the cycle until we find the first contract whose roll date is in the future.
        for (int i = 0; i < cycle.Length * 2; i++)
        {
            var rollDate = ComputeRollDate(month, year, family);
            if (date < rollDate)
            {
                char code = MonthToCode[month];
                return $"{rootSymbol}{code}{year % 100:D2}";
            }
            (month, year) = NextInCycle(month, year, cycle);
        }

        throw new InvalidOperationException($"Could not determine active contract for {rootSymbol}");
    }

    /// <summary>True within 14 calendar days of the contract's roll date (including past).</summary>
    public static bool IsNearRoll(string ticker, DateTime? asOf = null)
    {
        var date = asOf ?? DateTime.Today;
        var rollDate = RollDate(ticker);
        return date >= rollDate.AddDays(-14);
    }

    /// <summary>Roll date for a specific contract ticker (e.g. <c>RollDate("NQH26")</c> → March 12, 2026).</summary>
    public static DateTime RollDate(string ticker)
    {
        var norm = FuturesSymbol.Normalize(ticker);
        var suffix = norm[^3..];
        var root   = norm[..^3];
        char monthCode = char.ToUpper(suffix[0]);
        int year2 = int.Parse(suffix[1..]);
        int year  = 2000 + year2;

        if (!MonthCodeToMonth.TryGetValue(monthCode, out int month))
            throw new ArgumentException($"Invalid month code '{monthCode}' in ticker '{ticker}'");

        return ComputeRollDate(month, year, GetFamily(root));
    }

    /// <summary>Last trading day for a specific contract ticker per CME rules.</summary>
    public static DateTime LastTradingDay(string ticker)
    {
        var norm = FuturesSymbol.Normalize(ticker);
        var suffix = norm[^3..];
        var root   = norm[..^3];
        char monthCode = char.ToUpper(suffix[0]);
        int year2 = int.Parse(suffix[1..]);
        int year  = 2000 + year2;

        if (!MonthCodeToMonth.TryGetValue(monthCode, out int month))
            throw new ArgumentException($"Invalid month code '{monthCode}' in ticker '{ticker}'");

        return ComputeLastTradingDay(month, year, GetFamily(root));
    }

    /// <summary>
    /// Splits a date range into per-contract segments, each with the correct ticker.
    /// e.g. <c>(NQ, Dec 1 2025, Mar 30 2026)</c> → <c>[("NQZ25", Dec1..Mar5), ("NQH26", Mar5..Mar30)]</c>.
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

    // ── Private: family lookup ──────────────────────────────────────

    private static RollFamily GetFamily(string rootSymbol)
    {
        var r = rootSymbol.ToUpperInvariant();
        if (r is "GC"  or "MGC") return RollFamily.Gold;
        if (r is "CL"  or "MCL") return RollFamily.Crude;
        if (r is "BTC" or "MBT") return RollFamily.Bitcoin;
        return RollFamily.Quarterly;
    }

    private static int[] GetCycleForFamily(RollFamily family) => family switch
    {
        RollFamily.Gold    => BiMonthly,
        RollFamily.Crude   => Monthly,
        RollFamily.Bitcoin => Monthly,
        _                  => Quarterly,
    };

    // ── Private: last-trading-day + roll-date per family ────────────

    private static DateTime ComputeLastTradingDay(int contractMonth, int contractYear, RollFamily family)
        => family switch
        {
            RollFamily.Quarterly => ThirdFriday(contractMonth, contractYear),
            RollFamily.Gold      => ThirdLastBusinessDay(PriorMonth(contractMonth, contractYear)),
            RollFamily.Crude     => CrudeLastTradingDay(contractMonth, contractYear),
            RollFamily.Bitcoin   => LastFridayOfMonth(contractMonth, contractYear),
            _ => throw new InvalidOperationException($"Unknown family {family}"),
        };

    private static DateTime ComputeRollDate(int contractMonth, int contractYear, RollFamily family)
    {
        var ltd = ComputeLastTradingDay(contractMonth, contractYear, family);
        int buffer = family == RollFamily.Quarterly ? EquityBufferDays : OtherBufferDays;
        return ltd.AddDays(-buffer);
    }

    // ── Private: calendar helpers ───────────────────────────────────

    private static DateTime ThirdFriday(int month, int year)
    {
        var first = new DateTime(year, month, 1);
        int daysUntilFriday = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        var firstFriday = first.AddDays(daysUntilFriday);
        return firstFriday.AddDays(14);
    }

    private static DateTime LastFridayOfMonth(int month, int year)
    {
        var lastDay = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        int daysBack = ((int)lastDay.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7;
        return lastDay.AddDays(-daysBack);
    }

    /// <summary>Third-to-last Mon–Fri of the given month/year.</summary>
    private static DateTime ThirdLastBusinessDay((int Month, int Year) m)
    {
        var d = new DateTime(m.Year, m.Month, DateTime.DaysInMonth(m.Year, m.Month));
        int bdCount = 0;
        while (true)
        {
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
            {
                bdCount++;
                if (bdCount == 3) return d;
            }
            d = d.AddDays(-1);
        }
    }

    /// <summary>CL: 3 Mon–Fri business days before the 25th of the month PRIOR to contract month.</summary>
    private static DateTime CrudeLastTradingDay(int contractMonth, int contractYear)
    {
        var (pm, py) = PriorMonth(contractMonth, contractYear);
        var d = new DateTime(py, pm, 25);
        int bd = 0;
        while (bd < 3)
        {
            d = d.AddDays(-1);
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) bd++;
        }
        return d;
    }

    private static (int Month, int Year) PriorMonth(int month, int year)
        => month == 1 ? (12, year - 1) : (month - 1, year);

    // ── Private: cycle navigation ───────────────────────────────────

    private static (int month, int year) GetCurrentCycleMonth(DateTime date, int[] cycle)
    {
        for (int i = cycle.Length - 1; i >= 0; i--)
        {
            if (cycle[i] <= date.Month)
                return (cycle[i], date.Year);
        }
        return (cycle[^1], date.Year - 1);
    }

    private static (int month, int year) NextInCycle(int month, int year, int[] cycle)
    {
        int idx = Array.IndexOf(cycle, month);
        if (idx < 0 || idx == cycle.Length - 1)
            return (cycle[0], year + 1);
        return (cycle[idx + 1], year);
    }
}
