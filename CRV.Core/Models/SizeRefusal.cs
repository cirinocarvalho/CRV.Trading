using System.Globalization;

namespace CRV.Core.Models;

/// <summary>Why a signal the strategy wanted to take was not taken.</summary>
public enum RefusalReason
{
    /// <summary>The risk budget could not carry it at even one contract.</summary>
    Size,
    /// <summary>Its reward / risk was below the strategy's minimum, or it had no reward or no risk.</summary>
    MinRr,
}

/// <summary>
/// A signal the strategy wanted to take and did not: the risk budget could not carry it
/// at even one contract, or its reward / risk was below the minimum. Not a trade, so it never
/// reaches the trade record — which is why it is an event of its own: a study that measures
/// only the trades that were taken, while some signals were dropped before they could become
/// trades, is measuring a different sample and ought to say so.
/// </summary>
public sealed record SizeRefusal(
    DateTime Time,
    string   SetupLabel,
    string   Ticker,
    decimal  StopDistance,
    decimal  RiskPerContract,
    decimal  Budget,
    RefusalReason Reason = RefusalReason.Size,
    decimal  Rr    = 0m,
    decimal  MinRr = 0m)
{
    public string Describe() => Reason switch
    {
        RefusalReason.MinRr when Rr <= 0 => "Skipped: no reward or no risk",
        RefusalReason.MinRr              => string.Create(CultureInfo.InvariantCulture, $"Skipped: {Rr:0.0#}R below {MinRr:0.0#}R"),
        _                                => $"refused for size — stop {StopDistance:F2} pts = ${RiskPerContract:F2}/ct, budget ${Budget:F0}",
    };
}
