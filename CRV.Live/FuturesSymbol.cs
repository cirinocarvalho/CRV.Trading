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

    /// <summary>
    /// Strips a leading slash if present and converts a 4-digit year to 2-digit.
    /// Returns the canonical format: no slash, 2-digit year.
    ///
    /// "NQH2026"  → "NQH26"
    /// "/NQH2026" → "NQH26"
    /// "NQH26"    → "NQH26"   (idempotent)
    /// "/NQH26"   → "NQH26"   (idempotent)
    /// </summary>
    public static string Normalize(string ticker)
    {
        var t = ticker.TrimStart('/');
        return _year4.Replace(t, m => m.Value.Length == 4 ? m.Value[2..] : m.Value);
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
