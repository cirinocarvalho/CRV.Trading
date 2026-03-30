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

    /// <summary>Whether this setup is active. Disabled setups stay in the basket but are skipped by the engine.</summary>
    public bool Enabled { get; set; } = true;

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

    /// <summary>Per-session enablement and cutoff times.</summary>
    public List<SessionSlot> Sessions { get; set; } = new()
    {
        new() { SessionId = "Asia",   Enabled = false, CutoffHour = 1,  CutoffMinute = 30 },
        new() { SessionId = "London", Enabled = false, CutoffHour = 8,  CutoffMinute = 0  },
        new() { SessionId = "NY",     Enabled = true,  CutoffHour = 14, CutoffMinute = 30 },
    };

    /// <summary>Full per-setup configuration.</summary>
    public StrategySetupConfig Config { get; set; } = new();
}

/// <summary>Per-session enablement and cutoff for a basket entry.</summary>
public class SessionSlot
{
    public string SessionId   { get; set; } = "";   // "Asia", "London", "NY"
    public bool   Enabled     { get; set; }
    public int    CutoffHour  { get; set; } = 14;
    public int    CutoffMinute { get; set; } = 30;
}
