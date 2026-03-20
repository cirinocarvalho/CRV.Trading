using CRV.Core.Strategy;

namespace CRV.Core.Models;

/// <summary>
/// Per-setup instance configuration. Named to avoid collision with
/// existing SetupConfigBase/SetupConfigA/B/C/D hierarchy in SessionConfig.cs.
/// </summary>
public class StrategySetupConfig
{
    public string Name { get; set; } = "";               // "A", "B", "C", "D"
    public SetupId SetupId { get; set; }
    public StrategyType StrategyType { get; set; }
    public bool Enabled { get; set; }

    // Instrument (resolved effective values — fallback already applied)
    public string Ticker { get; set; } = "";
    public decimal PointValue { get; set; } = 20m;
    public decimal TickSize { get; set; } = 0.25m;

    // Sizing
    public int Contracts { get; set; } = 2;
    public decimal HiVolMult { get; set; } = 1.0m;
    public int MaxContracts { get; set; } = 2;

    // Entry
    public decimal StopPct { get; set; } = 0.10m;
    public int TargetPct { get; set; } = 100;
    public int PartialPct { get; set; } = 50;
    public decimal NearPct { get; set; } = 0.15m;
    public decimal MinRr { get; set; } = 1.5m;
    public string Mode { get; set; } = "Conservative";
    public decimal PullbackPct { get; set; } = 0.50m;    // A only
    public decimal RetestPct { get; set; } = 0.05m;      // B only
    public int EntryTickOffset { get; set; }
    public string OrderType { get; set; } = "Market";

    // Filters
    public bool UseVwap { get; set; } = true;
    public bool UseOrbClose { get; set; }
    public int CutoffHour { get; set; } = 14;
    public int CutoffMinute { get; set; } = 30;
    public bool CloseAtRthClose { get; set; } = true;
    public int MaxTrades { get; set; } = 5;
    public int MaxAdverseMinutes { get; set; }

    // Exit
    public bool UsePartial { get; set; } = true;
    public bool UseBe { get; set; } = true;
    public int PartialCts { get; set; }
    public bool AllowRearmAfterBe { get; set; } = true;

    // Derived
    public bool IsAggressive => Mode == "Aggressive";
}
