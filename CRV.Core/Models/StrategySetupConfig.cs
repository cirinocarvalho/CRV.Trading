using CRV.Core.Strategy;

namespace CRV.Core.Models;

/// <summary>
/// Per-setup instance configuration. Named to avoid collision with
/// existing SetupConfigBase/SetupConfigA/B/C/D hierarchy in SessionConfig.cs.
/// </summary>
public class StrategySetupConfig
{
    public string Id { get; set; } = "";                 // unique key: "A", "B", "C", "D" (later: "b-mnq-1")
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
    public decimal MaxEntrySlippage { get; set; } = 0;  // max % of ORB range; 0 = no limit (use NearPct as natural bound)
    public bool UseTickConfirmation { get; set; } = true;  // false = bar-level entry (backtest)
    public string OrderType { get; set; } = "Market";

    // Filters
    public bool UseVwap { get; set; } = true;
    public bool UseOrbClose { get; set; }
    /// <summary>
    /// When true, a bar must CLOSE beyond the ORB boundary (not just wick through it)
    /// before the pullback setup arms. Filters fakeout breakouts that only wick above/below
    /// the ORB level and immediately reverse back inside the range.
    /// Default false to preserve legacy behaviour.
    /// </summary>
    public bool UseCloseConfirmation { get; set; } = false;
    public int CutoffHour { get; set; } = 14;
    public int CutoffMinute { get; set; } = 30;
    public bool CloseAtRthClose { get; set; } = true;
    public int MaxTrades { get; set; } = 5;
    /// <summary>Max long entries per session. 0 = use MaxTrades.</summary>
    public int MaxLongTrades { get; set; } = 0;
    /// <summary>Max short entries per session. 0 = use MaxTrades.</summary>
    public int MaxShortTrades { get; set; } = 0;

    /// <summary>Effective max longs (falls back to MaxTrades if 0).</summary>
    public int EffectiveMaxLong => MaxLongTrades > 0 ? MaxLongTrades : MaxTrades;
    /// <summary>Effective max shorts (falls back to MaxTrades if 0).</summary>
    public int EffectiveMaxShort => MaxShortTrades > 0 ? MaxShortTrades : MaxTrades;

    public int MaxAdverseMinutes { get; set; }

    // Exit
    public bool UsePartial { get; set; } = true;
    public bool UseBe { get; set; } = true;
    public int PartialCts { get; set; }
    public bool AllowRearmAfterBe { get; set; } = true;

    // Session-specific cutoffs (from basket). When empty, uses CutoffHour/CutoffMinute for all sessions.
    public List<SessionSlot>? SessionSlots { get; set; }

    // Derived
    public bool IsAggressive => Mode == "Aggressive";
    public bool IsSmartAggressive => Mode == "SmartAggressive";

    /// <summary>Get the cutoff for a specific session, falling back to the global cutoff.</summary>
    public (int Hour, int Minute) GetCutoffForSession(string sessionName)
    {
        if (SessionSlots != null)
        {
            var slot = SessionSlots.Find(s => s.SessionId.Equals(sessionName, StringComparison.OrdinalIgnoreCase));
            if (slot != null) return (slot.CutoffHour, slot.CutoffMinute);
        }
        return (CutoffHour, CutoffMinute);
    }

    /// <summary>Check if this setup is enabled for a specific session.</summary>
    public bool IsEnabledForSession(string sessionName)
    {
        if (SessionSlots == null || SessionSlots.Count == 0) return true; // legacy: runs in all sessions
        return SessionSlots.Any(s => s.Enabled && s.SessionId.Equals(sessionName, StringComparison.OrdinalIgnoreCase));
    }
}
