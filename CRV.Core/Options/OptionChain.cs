namespace CRV.Core.Options;

/// <summary>
/// One option contract as returned by the broker's chain endpoint.
/// <para><see cref="Symbol"/> is the OSI string carried through verbatim — it is the
/// only value ever sent back when placing an order. Never rebuild it.</para>
/// <para><see cref="Multiplier"/> comes from the response rather than a constant:
/// adjusted and non-standard contracts do not deliver 100 shares.</para>
/// </summary>
public sealed record OptionContract
{
    public required string      Symbol            { get; init; }
    public required OptionRight Right             { get; init; }
    public required decimal     Strike            { get; init; }
    public required DateTime    Expiration        { get; init; }
    public required int         DaysToExpiration  { get; init; }
    public required decimal     Bid               { get; init; }
    public required decimal     Ask               { get; init; }
    public required decimal     Mark              { get; init; }
    public required long        Volume            { get; init; }
    public required long        OpenInterest      { get; init; }
    public required decimal     Delta             { get; init; }
    public required decimal     Gamma             { get; init; }
    public required decimal     Theta             { get; init; }
    public required decimal     Vega              { get; init; }
    public required decimal     ImpliedVolatility { get; init; }
    public required decimal     IntrinsicValue    { get; init; }
    public required decimal     ExtrinsicValue    { get; init; }
    public required int         Multiplier        { get; init; }
    public required bool        InTheMoney        { get; init; }
    public required bool        NonStandard       { get; init; }

    /// <summary>Midpoint of the quoted market.</summary>
    public decimal Mid => (Bid + Ask) / 2m;

    /// <summary>Bid/ask spread as a percentage of mid. <see cref="decimal.MaxValue"/> when there is no market.</summary>
    public decimal SpreadPct => Mid <= 0m ? decimal.MaxValue : (Ask - Bid) / Mid * 100m;

    /// <summary>False when nobody is bidding — the position could not be exited at any price.</summary>
    public bool HasBid => Bid > 0m;

    /// <summary>Commission as a percentage of the premium paid at the ask.</summary>
    public decimal CommissionPctOfPremium(decimal perContract)
    {
        decimal premium = Ask * Multiplier;
        return premium <= 0m ? decimal.MaxValue : perContract / premium * 100m;
    }
}

/// <summary>
/// Admission test for tradeability.
/// <para>Open interest is deliberately NOT the primary gate: measured on a live SPY
/// chain, 610 contracts quoted at or below $0.02 carried open interest as high as
/// 6,981 while every one of them had a spread of 200% of mid and 62% had no bid at
/// all. Spread and the presence of a bid are what separate tradeable from not.</para>
/// </summary>
public sealed record LiquidityGate(
    decimal MaxSpreadPct    = 10m,
    long    MinOpenInterest = 0,
    long    MinVolume       = 0)
{
    public bool Admits(OptionContract c)
        => c.HasBid
        && c.SpreadPct    <= MaxSpreadPct
        && c.OpenInterest >= MinOpenInterest
        && c.Volume       >= MinVolume;
}

/// <summary>A parsed option chain for a single underlying.</summary>
public sealed record OptionChain(
    string  Underlying,
    decimal UnderlyingPrice,
    IReadOnlyList<OptionContract> Contracts)
{
    /// <summary>Distinct expirations present in the chain, ascending.</summary>
    public IReadOnlyList<DateTime> Expirations
        => Contracts.Select(c => c.Expiration.Date).Distinct().OrderBy(d => d).ToList();

    /// <summary>Contracts for one expiration and right, ordered by strike.</summary>
    public IReadOnlyList<OptionContract> For(DateTime expiration, OptionRight right)
        => Contracts
            .Where(c => c.Expiration.Date == expiration.Date && c.Right == right)
            .OrderBy(c => c.Strike)
            .ToList();
}
