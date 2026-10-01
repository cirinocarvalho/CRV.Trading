using CRV.Core.Options;

namespace CRV.Live.Brokers.Schwab;

/// <summary>
/// The opening order the Options Explorer sends: entry, optional take-profit, optional stop.
/// Preview and placement both build it here, so what is confirmed is what is sent. They
/// used to build it separately, and placement dropped the stop the preview had shown.
/// </summary>
public static class OptionEntryOrder
{
    /// <summary>
    /// The stop's limit, set through the trigger so a triggered stop can actually fill.
    /// A stop-limit priced at the trigger frequently does not, which is the failure mode
    /// that leaves someone believing they were protected.
    /// </summary>
    public const decimal StopLimitSlipFraction = 0.10m;

    /// <summary>The stop to attach, or null when none was asked for or the structure is a spread
    /// (Schwab has no net-stop order type for a spread).</summary>
    /// <remarks>The limit goes through the trigger in the direction the stop trades. A bought
    /// option is stopped by SELLING as the price falls, so the limit sits below the trigger.
    /// A sold option is stopped by BUYING it back as the price rises, so the limit sits above;
    /// a buy limit below the trigger could never fill once the stop fired.</remarks>
    public static AttachedStop? Stop(decimal? trigger, IReadOnlyList<OptionLeg> legs)
    {
        if (trigger is not { } t || t <= 0m) return null;
        if (legs.Count != 1) return null;

        bool buysToClose = legs[0].Action == LegAction.Sell;
        decimal limit = buysToClose
            ? Math.Round(t * (1m + StopLimitSlipFraction), 2)
            : Math.Max(0.01m, Math.Round(t * (1m - StopLimitSlipFraction), 2));
        return new AttachedStop(t, limit);
    }

    /// <summary>Reads the ticket's duration; anything unrecognised is refused rather than guessed.</summary>
    public static bool TryParseDuration(string? value, out OrderDuration duration)
    {
        duration = OrderDuration.Day;
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Enum.TryParse(value, ignoreCase: true, out duration) && Enum.IsDefined(duration);
    }

    /// <summary>
    /// The most one unit of the structure can be worth (its value at the best expiry price),
    /// in the same per-unit terms as the order's price, or null when there is no such ceiling.
    /// A take-profit above it can never fill.
    /// <para>Null when it is unbounded, and for calendars and diagonals: with legs expiring on
    /// different days, the same-day payoff is no ceiling, because the later leg still has
    /// time value when the earlier one expires.</para>
    /// </summary>
    public static decimal? MaxStructureValue(IReadOnlyList<OptionLeg> legs)
    {
        if (legs.Select(l => l.Expiration.Date).Distinct().Count() > 1) return null;

        var free = PayoffCalculator.Analyze(legs, 0m);   // no commission, market prices
        if (free.ProfitUnbounded) return null;
        int multiplier = legs[0].Multiplier > 0 ? legs[0].Multiplier : 100;
        // The order is priced per reduced unit (3:6:3 is three 1:2:1 units), and so is
        // NetPrice; the payoff is for the legs as entered, so divide it by the same factor.
        decimal perUnitProfit = free.MaxProfit / SchwabOptionOrder.UnitFactor(legs);
        return (perUnitProfit + SchwabOptionOrder.NetPrice(legs) * multiplier) / multiplier;
    }

    /// <summary>True when the take-profit asks for more than the structure can ever be worth.</summary>
    public static bool ExitUnreachable(IReadOnlyList<OptionLeg> legs, decimal? exitPrice) =>
        exitPrice is { } x && MaxStructureValue(legs) is { } max && x > max;

    /// <summary>
    /// The Schwab payload. The entry uses <paramref name="duration"/>; an attached take-profit
    /// and stop stay good till cancelled, so a Day entry that fills is not left unprotected
    /// at the close.
    /// </summary>
    public static Dictionary<string, object> Payload(
        IReadOnlyList<OptionLeg> legs, int spreads, OrderDuration duration,
        decimal? limitPrice, decimal? exitPrice, decimal? stopTrigger) =>
        SchwabOptionOrder.BuildPayload(
            legs, spreads, duration, limitPrice,
            exitPrice is { } x ? new AttachedExit(x) : null,
            Stop(stopTrigger, legs));
}
