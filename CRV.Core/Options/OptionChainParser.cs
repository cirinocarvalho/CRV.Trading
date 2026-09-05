using System.Globalization;
using System.Text.Json;

namespace CRV.Core.Options;

/// <summary>
/// Parses a Schwab /marketdata/v1/chains response into <see cref="OptionChain"/>.
/// Pure function — no HTTP, no auth — so it is testable against saved responses.
/// </summary>
public static class OptionChainParser
{
    public static OptionChain Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var contracts = new List<OptionContract>();
        Collect(root, "callExpDateMap", OptionRight.Call, contracts);
        Collect(root, "putExpDateMap",  OptionRight.Put,  contracts);

        return new OptionChain(
            Str(root, "symbol"),
            Dec(root, "underlyingPrice"),
            contracts,
            Dec(root, "interestRate"));
    }

    /// <summary>
    /// Walks one side of the chain: expiry key → strike key → contract array.
    /// Expiry keys carry the date and days-to-expiration as "2026-08-28:2".
    /// </summary>
    private static void Collect(
        JsonElement root, string mapName, OptionRight right, List<OptionContract> into)
    {
        if (!root.TryGetProperty(mapName, out var map) || map.ValueKind != JsonValueKind.Object)
            return;

        foreach (var expiry in map.EnumerateObject())
        {
            var (expiration, dteFromKey) = ParseExpiryKey(expiry.Name);

            foreach (var strike in expiry.Value.EnumerateObject())
                foreach (var c in strike.Value.EnumerateArray())
                    into.Add(new OptionContract
                    {
                        Symbol            = Str(c, "symbol"),
                        Right             = right,
                        Strike            = Dec(c, "strikePrice"),
                        Expiration        = expiration,
                        DaysToExpiration  = dteFromKey ?? (int)Dec(c, "daysToExpiration"),
                        ExpiresAtUtc      = ExpiryInstant(c, expiration),
                        Bid               = Dec(c, "bid"),
                        Ask               = Dec(c, "ask"),
                        Mark              = Dec(c, "mark"),
                        Volume            = (long)Dec(c, "totalVolume"),
                        OpenInterest      = (long)Dec(c, "openInterest"),
                        Delta             = Dec(c, "delta"),
                        Gamma             = Dec(c, "gamma"),
                        Theta             = Dec(c, "theta"),
                        Vega              = Dec(c, "vega"),
                        ImpliedVolatility = Dec(c, "volatility"),
                        IntrinsicValue    = Dec(c, "intrinsicValue"),
                        ExtrinsicValue    = Dec(c, "extrinsicValue"),
                        Multiplier        = (int)Dec(c, "multiplier"),
                        InTheMoney        = Bool(c, "inTheMoney"),
                        NonStandard       = Bool(c, "nonStandard"),
                        ExerciseType      = Str(c, "exerciseType"),
                    });
        }
    }

    /// <summary>
    /// The contract's own expiry instant. Falls back to the end of the expiry date when the
    /// broker omits it — never earlier, so a contract is not hidden on a missing field.
    /// </summary>
    private static DateTime ExpiryInstant(System.Text.Json.JsonElement c, DateTime expirationDate)
    {
        if (c.TryGetProperty("expirationDate", out var v) &&
            v.ValueKind == System.Text.Json.JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out var dto))
            return dto.UtcDateTime;

        return expirationDate.Date.AddDays(1).AddTicks(-1);
    }

    private static (DateTime Expiration, int? Dte) ParseExpiryKey(string key)
    {
        var parts = key.Split(':');
        var date  = DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var d) ? d : default;
        return (date, parts.Length > 1 && int.TryParse(parts[1], out var dte) ? dte : null);
    }

    // ── tolerant readers ─────────────────────────────────────────
    // Schwab emits "NaN" as a *string* for greeks on some illiquid contracts, and
    // omits fields outright on others. Neither may abort the whole chain load.

    private static decimal Dec(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m,
        };
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
