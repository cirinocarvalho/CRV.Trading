// CRV.Core/Strategy/ISetupStrategy.cs
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

// ── Strategy type enum ──────────────────────────────────────────
public enum StrategyType { Pullback, Retest, OrbFakeout, SessionFakeout, Ema21 }

// ── Readonly state snapshots passed to strategies ───────────────
public readonly record struct OrbState(
    decimal High, decimal Low, decimal Mid, decimal Range,
    bool IsSet, bool BullClose, bool BearClose,
    decimal AtrRatio);

public readonly record struct IndicatorState(
    decimal Atr, decimal Vwap, decimal VwapUpper1, decimal VwapLower1,
    decimal VwapUpper2, decimal VwapLower2, decimal LastClose,
    decimal Ema21 = 0);

public readonly record struct ModuleState(
    // SessionEngine
    decimal SessionHigh, decimal SessionLow,
    decimal AsiaHigh, decimal AsiaLow, bool AsiaCompressed,
    decimal LondonHigh, decimal LondonLow,
    decimal PDH, decimal PDL, decimal PWH, decimal PWL,
    SessionType CurrentSession,
    bool LondonSweptAsiaHigh, bool LondonSweptAsiaLow,
    bool NYBullExpansion, bool NYBearExpansion,
    // SweepDetector
    IReadOnlyList<SweepEvent> ActiveSweeps,
    // VwapModel
    int VwapState,
    bool BullVwapReclaim, bool BearVwapReject,
    // OpeningDriveDetector
    bool IsBullDrive, bool IsBearDrive,
    // TrendDayFilter (directional pairs)
    int TrendDayBullScore, int TrendDayBearScore,
    bool TrendDayBull, bool TrendDayBear,
    // FalseBreakoutDetector — ORB range
    bool OrbFakeoutBull, bool OrbFakeoutBear,
    decimal FakeoutPenetration,
    // FalseBreakoutDetector — Session range
    bool SessionFakeoutBull, bool SessionFakeoutBear,
    decimal SessionRangeHigh, decimal SessionRangeLow);

// ── Per-setup snapshot for dashboard ────────────────────────────
public class SetupStateSnapshot
{
    public string Id { get; set; } = "";
    public SetupId SetupId { get; set; }
    public string Name { get; set; } = "";
    public int State { get; set; }           // state machine value
    public bool IsActive { get; set; }
    public bool IsArmed { get; set; }
    public bool PastCutoff { get; set; }
    public int TradeCount { get; set; }
    public int MaxTrades { get; set; }
    public bool StickyTgt { get; set; }
    public bool StickyStp { get; set; }
    public decimal Expectancy { get; set; }  // avg PnL per trade
    public bool Enabled { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal WinPnl { get; set; }
    public decimal LossPnl { get; set; }
}

// ── Strategy interface ──────────────────────────────────────────
public interface ISetupStrategy
{
    string Id { get; }
    SetupId SetupId { get; }
    StrategyType StrategyType { get; }
    string Name { get; }
    bool IsActive { get; }
    bool IsArmed { get; }

    /// <summary>Whether this setup has a broker-managed trade active (set by BrokerEventHandler).</summary>
    bool InTrade { get; }

    /// <summary>Called by BrokerEventHandler to signal trade entry/exit.</summary>
    void SetInTrade(bool active);

    /// <summary>Seed long/short trade counters from DB after engine restart so [X/Max] is correct.</summary>
    void SeedTradeCount(int longs, int shorts);

    int CutoffHour { get; }
    int CutoffMinute { get; }

    /// <summary>Get cutoff for a specific session (basket entries have per-session cutoffs).</summary>
    (int Hour, int Minute) GetCutoffForSession(string sessionName);

    /// <summary>Check if this setup is enabled for a specific session.</summary>
    bool IsEnabledForSession(string sessionName);

    /// <summary>The ticker symbol this setup trades (may differ from global config for multi-instrument).</summary>
    string Ticker { get; }

    /// <summary>The point value for this setup's instrument (e.g. 20 for NQ, 50 for ES).</summary>
    decimal PointValue { get; }

    /// <summary>Effective ORB-window start for this setup (already resolved against the global fallback).</summary>
    TimeOnly OrbStart { get; }

    /// <summary>Effective ORB-window end for this setup (already resolved against the global fallback).</summary>
    TimeOnly OrbEnd { get; }

    /// <summary>When true, long entries require price > EMA21, short entries require price &lt; EMA21.</summary>
    bool UseEmaFilter { get; }

    /// <summary>When true, this setup is exempt from the global chop-regime filter (entries fire even when chop is flagged).</summary>
    bool BypassChopFilter { get; }

    /// <summary>Process a confirmed bar. May produce pending signals.</summary>
    void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules);

    /// <summary>Process a price tick. May produce pending signals.</summary>
    void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules);

    /// <summary>Reconfigure for new session or settings change.</summary>
    void Reconfigure(StrategySetupConfig config);

    /// <summary>Reset all state for new trading day (clears trade counts, P&amp;L stats).</summary>
    void Reset();

    /// <summary>
    /// Reset for intra-day session transition.
    /// Clears trade state (arm, entry, stops) and per-session counters (trade count,
    /// direction-traded flags) but preserves daily P&amp;L stats (wins, losses, winPnl, lossPnl).
    /// </summary>
    void ResetSession();

    /// <summary>
    /// Reset only trade counters and P&amp;L stats accumulated during warmup.
    /// Preserves arm state, sticky signals, and other strategy logic state so the
    /// dashboard shows correct Armed/Idle badges immediately after warmup completes.
    /// </summary>
    void ResetTradeCounters();

    // ── Pending signals (consumed by engine after OnBar/OnTick) ──
    EntrySignal? PendingEntry { get; }

    /// <summary>Clear all pending signals after engine has processed them.</summary>
    void ClearPendingSignals();

    /// <summary>Revert an uncommitted entry (undo pending entry, keep armed state).</summary>
    void RevertEntry();

    /// <summary>Request force exit of active trade (resets state, no signal generation).</summary>
    void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd);

    /// <summary>
    /// Disarm the strategy (reset to idle) without affecting trade counters or P&amp;L.
    /// Used when per-setup cutoff is reached to clear stale armed/waiting states.
    /// </summary>
    void Disarm();

    /// <summary>Reset the pastCutoff flag (after warmup processed bars past cutoff time).</summary>
    void ResetCutoff();

    /// <summary>Snapshot for dashboard display.</summary>
    SetupStateSnapshot GetSnapshot();
}
