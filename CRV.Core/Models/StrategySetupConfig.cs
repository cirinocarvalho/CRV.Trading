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

    // ── ORB window override (per-setup) ────────────────────────────
    /// <summary>When true, this setup uses its own ORB window instead of the global config window.
    /// Lets one ticker (e.g. NQ at 09:30–10:00) and another (e.g. GC at 08:20–09:50) coexist
    /// in the same basket without forcing a single shared window.</summary>
    public bool UseCustomOrbWindow { get; set; }
    /// <summary>Start of the ORB window when <see cref="UseCustomOrbWindow"/> is true.
    /// Effective value (already resolved against the global fallback at config-build time).</summary>
    public TimeOnly OrbStart { get; set; } = new(9, 30);
    /// <summary>End of the ORB window when <see cref="UseCustomOrbWindow"/> is true.
    /// Effective value (already resolved against the global fallback at config-build time).</summary>
    public TimeOnly OrbEnd { get; set; } = new(10, 0);

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
    /// <summary>Stop placement mode: "OrbPct" (default, % of ORB range), "BarHL" (high/low of bar before entry ± 1 tick), or "Vwap" (VWAP ± ticks).</summary>
    public string StopMode { get; set; } = "OrbPct";
    /// <summary>Number of ticks offset from VWAP for Vwap stop mode. Default 4.</summary>
    public int StopVwapTicks { get; set; } = 4;

    // ── EMA21 filter (usable by any strategy) ──────────────────────
    /// <summary>When true, only allow long if price > EMA21, short if price &lt; EMA21.</summary>
    public bool UseEmaFilter { get; set; }

    /// <summary>When true, this setup is exempt from the global chop-regime filter — entries fire even when chop is flagged.</summary>
    public bool BypassChopFilter { get; set; }

    /// <summary>(Setup D only) Which prior-session range to fade. Auto = default chain.</summary>
    public FakeoutSession FakeoutReferenceSession { get; set; } = FakeoutSession.Auto;

    // ── EMA21 strategy parameters (ignored by ORB strategies) ─────
    /// <summary>Lookback bars for EMA slope calculation. Default 5.</summary>
    public int SlopeLen { get; set; } = 5;
    /// <summary>ATR multiplier for EMA touch detection zone. Default 0.5.</summary>
    public decimal AtrTouchMult { get; set; } = 0.5m;
    /// <summary>Minimum slope as % of EMA price to filter flat EMA noise. Default 0.05.</summary>
    public decimal MinSlopePct { get; set; } = 0.05m;
    /// <summary>Max ticks the signal bar open may be from EMA. Default 4.</summary>
    public int OpenTicksToEma { get; set; } = 4;
    /// <summary>Require volume > 20-bar SMA on signal bar. Default false.</summary>
    public bool UseVolumeFilter { get; set; }
    /// <summary>ATR multiplier for partial target (TP1). Default 1.0.</summary>
    public decimal AtrTp1Mult { get; set; } = 1.0m;
    /// <summary>ATR multiplier for full target (TP2). Default 2.0.</summary>
    public decimal AtrTp2Mult { get; set; } = 2.0m;

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
    /// <summary>When false, long entries are disabled regardless of MaxLongTrades/MaxTrades.</summary>
    public bool AllowLong { get; set; } = true;
    /// <summary>When false, short entries are disabled regardless of MaxShortTrades/MaxTrades.</summary>
    public bool AllowShort { get; set; } = true;

    /// <summary>Effective max longs. 0 if AllowLong is false; otherwise MaxLongTrades or MaxTrades fallback.</summary>
    public int EffectiveMaxLong => !AllowLong ? 0 : (MaxLongTrades > 0 ? MaxLongTrades : MaxTrades);
    /// <summary>Effective max shorts. 0 if AllowShort is false; otherwise MaxShortTrades or MaxTrades fallback.</summary>
    public int EffectiveMaxShort => !AllowShort ? 0 : (MaxShortTrades > 0 ? MaxShortTrades : MaxTrades);

    public int MaxAdverseMinutes { get; set; }
    /// <summary>Max dollar risk per trade. 0 = no limit. Risk = |entry-stop| * pointValue * contracts.</summary>
    public decimal MaxTradeRisk { get; set; }

    // Exit
    public bool UsePartial { get; set; } = true;
    public bool UseBe { get; set; } = true;
    public int PartialCts { get; set; }
    public bool AllowRearmAfterBe { get; set; } = true;
    /// <summary>Conservative Retest only: after the first trade on a side stops out,
    /// skip the full retest cycle (breakout confirm → leave zone → return → close above ORB)
    /// and re-enter immediately when price reaches ORB High/Low again.
    /// Default false = always require full retest cycle.</summary>
    public bool QuickReentry { get; set; }

    // Auto Trail (per-ticker, copied from BasketEntry at setup construction)
    public AutoTrailConfig? AutoTrail { get; set; }

    // Session-specific cutoffs (from basket). When empty, uses CutoffHour/CutoffMinute for all sessions.
    public List<SessionSlot>? SessionSlots { get; set; }

    // Derived
    public bool IsAggressive => Mode == "Aggressive";
    public bool IsSmartAggressive => Mode == "SmartAggressive";
    /// <summary>Conservative Smart: deferred entry fires as Limit at first tick of next bar
    /// (zero slippage — only fills if price returns to that exact level).</summary>
    public bool IsConservativeSmart => Mode == "ConservativeSmart";

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
