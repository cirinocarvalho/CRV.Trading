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

    /// <summary>
    /// When the contract stops trading, in UTC, taken from the broker's own
    /// <c>expirationDate</c>. Using the reported instant rather than a hard-coded
    /// 4pm rule keeps index options right — SPXW trades past the equity close — and
    /// handles early-close days without a calendar.
    /// </summary>
    public required DateTime     ExpiresAtUtc      { get; init; }
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

    /// <summary>
    /// "A" American — assignable at any time; "E" European — only at expiration.
    /// Index options are European, which is why a short index leg cannot be taken away early.
    /// </summary>
    public required string      ExerciseType      { get; init; }

    /// <summary>Midpoint of the quoted market.</summary>
    public decimal Mid => (Bid + Ask) / 2m;

    /// <summary>Bid/ask spread as a percentage of mid. <see cref="decimal.MaxValue"/> when there is no market.</summary>
    public decimal SpreadPct => Mid <= 0m ? decimal.MaxValue : (Ask - Bid) / Mid * 100m;

    /// <summary>
    /// True when this contract can be assigned before expiration. Only matters for legs you
    /// are short: a long option is a right you choose to exercise, never an obligation.
    /// </summary>
    public bool IsAmerican => string.Equals(ExerciseType, "A", StringComparison.OrdinalIgnoreCase);

    /// <summary>True once the contract can no longer be traded.</summary>
    public bool HasExpired => ExpiresAtUtc <= DateTime.UtcNow;

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

    /// <summary>
    /// The market's implied move by an expiration, taken as the at-the-money straddle price.
    /// <para>This is what the options are charging for the move, so it is the yardstick a
    /// price target should be judged against: a target inside the expected move is already
    /// paid for, one well outside it needs the market to be wrong.</para>
    /// <para>Null when either side of the at-the-money strike is missing a market.</para>
    /// </summary>
    public decimal? ExpectedMove(DateTime expiration)
    {
        var calls = For(expiration, OptionRight.Call);
        var puts  = For(expiration, OptionRight.Put);
        if (calls.Count == 0 || puts.Count == 0 || UnderlyingPrice <= 0m) return null;

        // The at-the-money strike is the one nearest spot that has BOTH rights quoted —
        // pairing a call and a put from different strikes is not a straddle.
        var putByStrike = puts.Where(p => p.HasBid).ToDictionary(p => p.Strike);

        var atmCall = calls
            .Where(c => c.HasBid && putByStrike.ContainsKey(c.Strike))
            .MinBy(c => Math.Abs(c.Strike - UnderlyingPrice));

        // A missing bid on either leg makes the straddle unpriceable, not cheap.
        if (atmCall is null) return null;

        return atmCall.Mid + putByStrike[atmCall.Strike].Mid;
    }

    /// <summary>Contracts for one expiration and right, ordered by strike.</summary>
    public IReadOnlyList<OptionContract> For(DateTime expiration, OptionRight right)
        => Contracts
            .Where(c => c.Expiration.Date == expiration.Date && c.Right == right)
            .OrderBy(c => c.Strike)
            .ToList();
}
