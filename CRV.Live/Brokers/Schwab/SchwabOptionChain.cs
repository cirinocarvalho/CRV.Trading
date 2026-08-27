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

    /// <summary>Live two-sided market for one option contract.</summary>
    public record OptionQuote(string Symbol, decimal Bid, decimal Ask, decimal Strike, OptionRight Right);

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

            var right = OptionRight.Call;
            decimal strike = 0m;
            if (e.TryGetProperty("reference", out var r))
            {
                strike = Dec(r, "strikePrice");
                if (r.TryGetProperty("contractType", out var ctv) && ctv.GetString() is "P" or "PUT")
                    right = OptionRight.Put;
            }

            result[entry.Name] = new OptionQuote(
                entry.Name, Dec(q, "bidPrice"), Dec(q, "askPrice"), strike, right);
        }
        return result;
    }
}
