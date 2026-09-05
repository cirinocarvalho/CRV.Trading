using System.Net.Http.Headers;
using CRV.Core.Options;

namespace CRV.Live.Brokers.Schwab;

/// <summary>
/// Fetches option chains from Schwab. All parsing lives in
/// <see cref="OptionChainParser"/> — this type only does auth and transport.
/// </summary>
public static class SchwabOptionChain
{
    /// <summary>
    /// Fetch a chain for <paramref name="underlying"/>.
    /// <para>Requests must be bounded. Measured against the live API, SPY with a
    /// 300-strike window over three weeks of expiries returned 3,610 contracts and
    /// 4.3 MB; the full chain is far larger. Always pass a date window and a
    /// <paramref name="strikeCount"/> rather than fetching everything.</para>
    /// </summary>
    public static async Task<OptionChain> FetchAsync(
        SchwabAuthService auth,
        string            underlying,
        DateOnly?         fromDate     = null,
        DateOnly?         toDate       = null,
        int               strikeCount  = 20,
        IHttpClientFactory? httpFactory = null,
        CancellationToken ct            = default)
    {
        var token = await auth.GetAccessTokenAsync();
        using var http = httpFactory?.CreateClient("Schwab") ?? new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var q = new List<string>
        {
            $"symbol={Uri.EscapeDataString(underlying)}",
            "contractType=ALL",
            "strategy=SINGLE",
            "includeUnderlyingQuote=true",
            $"strikeCount={strikeCount}",
        };
        if (fromDate is { } f) q.Add($"fromDate={f:yyyy-MM-dd}");
        if (toDate   is { } t) q.Add($"toDate={t:yyyy-MM-dd}");

        var url = $"{auth.ApiBaseUrl}/marketdata/v1/chains?{string.Join("&", q)}";
        var res = await http.GetAsync(url, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Schwab chains {(int)res.StatusCode} for {underlying}: {Truncate(body, 300)}");

        return OptionChainParser.Parse(body);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// Live market and contract detail for one option, from the quotes endpoint. Carries
    /// greeks, so open positions can be aggregated without pulling a whole chain per symbol.
    /// </summary>
    public record OptionQuote(
        string      Symbol,
        decimal     Bid,
        decimal     Ask,
        decimal     Strike,
        OptionRight Right,
        string      Underlying   = "",
        DateTime    Expiration   = default,
        int         Multiplier   = 100,
        decimal     Delta        = 0m,
        decimal     Gamma        = 0m,
        decimal     Theta        = 0m,
        decimal     Vega         = 0m,
        decimal     Volatility   = 0m,
        /// <summary>"A" American — can be assigned early; "E" European — cannot.</summary>
        string      ExerciseType = "")
    {
        public decimal Mid => (Bid + Ask) / 2m;
        public bool IsAmerican => string.Equals(ExerciseType, "A", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fetch current quotes for specific contracts. Used when closing a position, where
    /// the price comes from today's market rather than from the chain view on screen.
    /// </summary>
    public static async Task<Dictionary<string, OptionQuote>> FetchQuotesAsync(
        SchwabAuthService auth, IReadOnlyList<string> symbols,
        IHttpClientFactory? httpFactory = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, OptionQuote>(StringComparer.Ordinal);
        if (symbols.Count == 0) return result;

        var token = await auth.GetAccessTokenAsync();
        using var http = httpFactory?.CreateClient("Schwab") ?? new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var joined = string.Join(",", symbols.Select(Uri.EscapeDataString));
        var res  = await http.GetAsync($"{auth.ApiBaseUrl}/marketdata/v1/quotes?symbols={joined}", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Schwab quotes {(int)res.StatusCode}: {Truncate(body, 300)}");

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var e = entry.Value;
            if (!e.TryGetProperty("quote", out var q)) continue;

            decimal Dec(System.Text.Json.JsonElement el, string n)
                => el.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                   ? v.GetDecimal() : 0m;

            var     right       = OptionRight.Call;
            decimal strike      = 0m;
            string  underlying  = "";
            string  exercise    = "";
            int     multiplier  = 100;
            DateTime expiration = default;

            if (e.TryGetProperty("reference", out var r))
            {
                strike = Dec(r, "strikePrice");
                if (r.TryGetProperty("contractType", out var ctv) && ctv.GetString() is "P" or "PUT")
                    right = OptionRight.Put;

                underlying = r.TryGetProperty("underlying",   out var u)  ? u.GetString()  ?? "" : "";
                exercise   = r.TryGetProperty("exerciseType", out var ex) ? ex.GetString() ?? "" : "";

                var m = (int)Dec(r, "multiplier");
                if (m > 0) multiplier = m;

                int yy = (int)Dec(r, "expirationYear"),
                    mm = (int)Dec(r, "expirationMonth"),
                    dd = (int)Dec(r, "expirationDay");
                if (yy > 0 && mm is > 0 and <= 12 && dd is > 0 and <= 31)
                    expiration = new DateTime(yy, mm, dd);
            }

            result[entry.Name] = new OptionQuote(
                entry.Name, Dec(q, "bidPrice"), Dec(q, "askPrice"), strike, right,
                underlying, expiration, multiplier,
                Dec(q, "delta"), Dec(q, "gamma"), Dec(q, "theta"), Dec(q, "vega"),
                Dec(q, "volatility"), exercise);
        }
        return result;
    }
}
