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
    public static AttachedStop? Stop(decimal? trigger, int legCount)
    {
        if (trigger is not { } t || t <= 0m) return null;
        if (legCount != 1) return null;

        decimal limit = Math.Max(0.01m, Math.Round(t * (1m - StopLimitSlipFraction), 2));
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
    /// The most the structure can be worth per unit (its value at the best expiry price), or
    /// null when that is unbounded. A take-profit above it can never fill.
    /// </summary>
    public static decimal? MaxStructureValue(IReadOnlyList<OptionLeg> legs)
    {
        var free = PayoffCalculator.Analyze(legs, 0m);   // no commission, market prices
        if (free.ProfitUnbounded) return null;
        int multiplier = legs[0].Multiplier > 0 ? legs[0].Multiplier : 100;
        return (free.MaxProfit + SchwabOptionOrder.NetPrice(legs) * multiplier) / multiplier;
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
            Stop(stopTrigger, legs.Count));
}
