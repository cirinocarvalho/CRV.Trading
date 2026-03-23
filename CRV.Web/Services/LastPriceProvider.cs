using CRV.Core.Interfaces;
using System.Collections.Concurrent;

namespace CRV.Web.Services;

public class LastPriceProvider : ILastPriceProvider
{
    private readonly ConcurrentDictionary<string, decimal> _prices = new();

    public decimal GetLastPrice(string ticker) =>
        _prices.TryGetValue(Normalize(ticker), out var p) ? p : 0;

    public void UpdatePrice(string ticker, decimal price) =>
        _prices[Normalize(ticker)] = price;

    /// <summary>
    /// Normalize ticker keys so broker-format (e.g. Schwab "/NQH26") and
    /// canonical format ("NQH26") resolve to the same dictionary entry.
    /// </summary>
    private static string Normalize(string ticker) => ticker.TrimStart('/');
}
