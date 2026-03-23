using CRV.Core.Strategy;

namespace CRV.Core.Models;

/// <summary>
/// A single entry in the user's setup basket. Each entry defines a strategy type,
/// instrument, and full per-setup configuration.
/// </summary>
public class BasketEntry
{
    /// <summary>Unique ID within the basket (e.g. "b-mnq-1", "a-mcl-1").</summary>
    public string Id { get; set; } = "";

    /// <summary>Display label (e.g. "B — Retest [MNQ]").</summary>
    public string Label { get; set; } = "";

    /// <summary>Strategy type to instantiate.</summary>
    public StrategyType StrategyType { get; set; }

    /// <summary>Broker ticker symbol (e.g. "/MNQM26").</summary>
    public string Ticker { get; set; } = "";

    /// <summary>Point value for this instrument.</summary>
    public decimal PointValue { get; set; } = 20m;

    /// <summary>Tick size for this instrument.</summary>
    public decimal TickSize { get; set; } = 0.25m;

    /// <summary>Full per-setup configuration.</summary>
    public StrategySetupConfig Config { get; set; } = new();
}
