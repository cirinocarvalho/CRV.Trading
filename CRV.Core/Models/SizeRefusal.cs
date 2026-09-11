namespace CRV.Core.Models;

/// <summary>
/// A signal the strategy wanted to take and the risk budget could not carry at
/// even one contract. Not a trade, so it never reaches the trade record — which is
/// why it is an event of its own: a study that measures only the trades that were
/// taken, while the widest-stop signals were dropped before they could become
/// trades, is measuring a different sample and ought to say so.
/// </summary>
public sealed record SizeRefusal(
    DateTime Time,
    string   SetupLabel,
    string   Ticker,
    decimal  StopDistance,
    decimal  RiskPerContract,
    decimal  Budget)
{
    public string Describe() =>
        $"refused for size — stop {StopDistance:F2} pts = ${RiskPerContract:F2}/ct, budget ${Budget:F0}";
}
