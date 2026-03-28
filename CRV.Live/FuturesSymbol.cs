using System.Text.RegularExpressions;

namespace CRV.Live;

/// <summary>
/// Converts futures ticker symbols between the canonical stored format
/// and broker-specific formats.
///
/// Canonical (stored in StrategyConfig.Ticker): NQH26
///   – No leading slash, 2-digit year.
///   – This is what the JS instrument selector writes to the hidden input.
///
/// Schwab format:       /NQH26   (leading slash, 2-digit year)
/// TradeStation format:  NQH26   (same as canonical — no conversion needed)
///
/// Normalize() accepts any variant and returns the canonical form.
/// </summary>
public static class FuturesSymbol
{
    // Matches a trailing 4-digit year to convert to 2-digit (e.g. 2026 → 26)
    private static readonly Regex _year4 = new(@"(\d{4})$", RegexOptions.Compiled);
    // Matches Tradovate 1-digit year: letter (month code) + single digit at end (e.g. MNQM6)
    private static readonly Regex _year1 = new(@"(?<=[A-Z])(\d)$", RegexOptions.Compiled);

    /// <summary>
    /// Strips a leading slash if present and normalises to canonical 2-digit year.
    /// Handles all broker formats:
    ///
    /// "NQH2026"  → "NQH26"    (4-digit year)
    /// "/NQH2026" → "NQH26"    (Schwab + 4-digit)
    /// "/NQH26"   → "NQH26"    (Schwab)
    /// "NQH26"    → "NQH26"    (canonical — idempotent)
    /// "NQH6"     → "NQH26"    (Tradovate 1-digit year)
    /// </summary>
    public static string Normalize(string ticker)
    {
        var t = ticker.TrimStart('/');
        // 4-digit year → 2-digit (2026 → 26)
        t = _year4.Replace(t, m => m.Value.Length == 4 ? m.Value[2..] : m.Value);
        // 1-digit year → 2-digit (6 → 26, assumes 2020s decade)
        t = _year1.Replace(t, m => "2" + m.Value);
        return t;
    }

    /// <summary>
    /// Converts to Schwab format: /NQH26
    /// Idempotent — calling twice yields the same result.
    /// </summary>
    public static string ToSchwab(string ticker) => "/" + Normalize(ticker);

    /// <summary>
    /// Converts to TradeStation format: NQH26
    /// Same as Normalize() — included for symmetry.
    /// </summary>
    public static string ToTradeStation(string ticker) => Normalize(ticker);

    /// <summary>
    /// Converts to Tradovate format: NQH6 (1-digit year, no slash).
    /// NQH26 → NQH6, MESH26 → MESH6.
    /// </summary>
    public static string ToTradovate(string ticker)
    {
        var t = Normalize(ticker); // ensure no slash, 2-digit year
        // Replace trailing 2-digit year with 1-digit (drop leading decade digit)
        return Regex.Replace(t, @"(\d{2})$", m => m.Value[1..]);
    }

    // Known micro-contract base codes
    private static readonly HashSet<string> _microCodes = new(StringComparer.OrdinalIgnoreCase)
        { "MNQ", "MES", "MYM", "M2K", "MGC", "MCL", "MBT" };

    /// <summary>
    /// Returns true if the ticker is a Micro contract (MNQ, MES, MGC, MCL).
    /// Accepts any format: "MNQ", "MNQH26", "/MNQH2026", etc.
    /// </summary>
    public static bool IsMicro(string ticker)
    {
        var norm = Normalize(ticker);
        // Check each known micro prefix (3 chars)
        return _microCodes.Any(code => norm.StartsWith(code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the default commission per side for a broker + ticker combination.
    /// </summary>
    public static decimal DefaultCommission(string broker, string ticker)
    {
        bool micro = IsMicro(ticker);
        return broker switch
        {
            "TradeStation" => micro ? 0.75m : 2.00m,
            "Tradovate" or "TradovateReplay" => micro ? 0.59m : 1.59m,
            "Mock"         => 0m,
            _              => 2.25m, // Schwab / default
        };
    }

    /// <summary>
    /// Extracts the root symbol from a ticker: "NQH26" → "NQ", "/MESH2026" → "MES".
    /// </summary>
    public static string RootSymbol(string ticker)
    {
        var norm = Normalize(ticker);
        // Last 3 chars = month code + 2-digit year (e.g. H26)
        return norm.Length > 3 ? norm[..^3] : norm;
    }

    /// <summary>
    /// Returns the dollar-per-point multiplier for a futures symbol.
    /// Used to compute P&amp;L: (exit - entry) * PointValue * qty.
    /// </summary>
    public static decimal PointValue(string ticker)
    {
        var root = RootSymbol(ticker).ToUpperInvariant();
        return root switch
        {
            "ES"  => 50m,
            "NQ"  => 20m,
            "YM"  => 5m,
            "RTY" => 50m,
            "MES" => 5m,
            "MNQ" => 2m,
            "MYM" => 0.5m,
            "M2K" => 5m,
            "GC"  => 100m,
            "MGC" => 10m,
            "CL"  => 1000m,
            "MCL" => 100m,
            "SI"  => 5000m,
            "SIL" => 50m,
            "HG"  => 25000m,
            "MBT" => 5m,       // Micro Bitcoin
            _     => 1m,       // fallback — raw price diff
        };
    }

    /// <summary>
    /// Converts to the broker-specific format given a broker name string.
    /// Recognised names: "Schwab", "TradeStation", "Tradovate". All others: returns Normalize().
    /// </summary>
    public static string ForBroker(string ticker, string broker)
        => broker switch
        {
            "Schwab"       => ToSchwab(ticker),
            "TradeStation" => ToTradeStation(ticker),
            "Tradovate" or "TradovateReplay" => ToTradovate(ticker),
            _              => Normalize(ticker)
        };

}
