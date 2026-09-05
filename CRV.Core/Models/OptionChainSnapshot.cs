namespace CRV.Core.Models;

/// <summary>
/// A daily reading of one expiry's implied volatility for one underlying.
/// <para>Exists purely because implied volatility history cannot be bought back. The chain
/// reports what IV is now and nothing about what it has been, so "is this expensive?" is
/// unanswerable until enough of these have accumulated. One row is a few dozen bytes; a
/// year of them for a handful of symbols is trivial, and not having them costs a year.</para>
/// </summary>
public class OptionChainSnapshot
{
    public int      Id              { get; set; }

    /// <summary>Session date the reading belongs to, in Eastern time.</summary>
    public DateOnly TradeDate       { get; set; }

    public string   Underlying      { get; set; } = "";

    // Stored as double, not decimal, throughout this table. EF Core maps decimal to TEXT on
    // SQLite, so MIN/MAX/ORDER BY compare lexicographically — "10.5" sorts below "9.7". For a
    // table whose entire purpose is statistical aggregation that is silently wrong, and these
    // are measurements rather than money, so binary floating point costs nothing.
    public double   UnderlyingPrice { get; set; }

    public DateOnly Expiration      { get; set; }
    public int      DaysToExpiration{ get; set; }

    /// <summary>Strike used for the at-the-money readings — nearest spot with both rights quoted.</summary>
    public double   AtmStrike       { get; set; }

    /// <summary>Mean of the at-the-money call and put implied volatilities.</summary>
    public double   AtmImpliedVol   { get; set; }

    /// <summary>At-the-money straddle price: what the market charged for the move to this expiry.</summary>
    public double?  ExpectedMove    { get; set; }

    public DateTime CapturedAtUtc   { get; set; } = DateTime.UtcNow;
}
