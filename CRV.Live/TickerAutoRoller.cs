namespace CRV.Live;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

/// <summary>
/// Rewrites stale futures contract tickers to the current active front-month
/// based on <see cref="ContractRollCalendar"/>.
///
/// Applies to both the top-level <c>Ticker</c> string and the <c>Ticker</c>
/// field inside each entry of <c>BasketJson</c> / <c>Ema21BasketJson</c>.
/// Uses <see cref="JsonNode"/> for basket rewrites so extra JSON fields are
/// preserved verbatim — avoids the default-stripping trap that bit us with
/// Python-based JSON edits.
/// </summary>
public static class TickerAutoRoller
{
    /// <summary>A single rewrite — returned so the caller can log/display.</summary>
    public record Rewrite(string Where, string Old, string New);

    /// <summary>
    /// Returns the active contract for <paramref name="ticker"/>'s root if different
    /// from the normalized input. Null if already current or root is unrecognized.
    /// </summary>
    public static string? NewerContractFor(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return null;
        string norm;
        string root;
        try
        {
            norm = FuturesSymbol.Normalize(ticker);
            root = FuturesSymbol.RootSymbol(norm);
            if (string.IsNullOrEmpty(root)) return null;
        }
        catch { return null; }

        try
        {
            var active = ContractRollCalendar.ActiveContract(root);
            return string.Equals(norm, active, StringComparison.OrdinalIgnoreCase) ? null : active;
        }
        catch { return null; }
    }

    /// <summary>
    /// Rewrites the <c>Ticker</c> field in every element of a JSON array, preserving
    /// all other fields unchanged. Returns the updated JSON string and a list of
    /// rewrites applied. Returns <c>(input, empty)</c> if no rewrites were needed or
    /// the input is not a JSON array.
    /// </summary>
    public static (string Json, List<Rewrite> Rewrites) RewriteBasket(string? basketJson, string label)
    {
        var rewrites = new List<Rewrite>();
        if (string.IsNullOrWhiteSpace(basketJson)) return (basketJson ?? "", rewrites);

        JsonNode? root;
        try { root = JsonNode.Parse(basketJson); }
        catch { return (basketJson, rewrites); }

        if (root is not JsonArray arr) return (basketJson, rewrites);

        foreach (var entry in arr)
        {
            if (entry is not JsonObject obj) continue;
            // BasketEntry serialization is PascalCase by default; be case-insensitive
            // to survive camelCase payloads from JS builds.
            var key = obj.ContainsKey("Ticker") ? "Ticker"
                    : obj.ContainsKey("ticker") ? "ticker"
                    : null;
            if (key == null) continue;

            var old = obj[key]?.GetValue<string>();
            if (string.IsNullOrEmpty(old)) continue;

            var fresh = NewerContractFor(old);
            if (fresh == null) continue;

            obj[key] = fresh;
            rewrites.Add(new Rewrite($"{label}[{obj["Id"]?.GetValue<string>() ?? "?"}]", old, fresh));
        }

        return rewrites.Count == 0 ? (basketJson, rewrites) : (arr.ToJsonString(), rewrites);
    }
}
