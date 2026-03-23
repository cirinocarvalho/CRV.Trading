// CRV.Core/Strategy/ISetupStrategy.cs
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

// ── Strategy type enum ──────────────────────────────────────────
public enum StrategyType { Pullback, Retest, OrbFakeout, SessionFakeout }

// ── Readonly state snapshots passed to strategies ───────────────
public readonly record struct OrbState(
    decimal High, decimal Low, decimal Mid, decimal Range,
    bool IsSet, bool BullClose, bool BearClose,
    decimal AtrRatio);

public readonly record struct IndicatorState(
    decimal Atr, decimal Vwap, decimal VwapUpper1, decimal VwapLower1,
    decimal VwapUpper2, decimal VwapLower2, decimal LastClose);

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
    SetupId SetupId { get; }
    StrategyType StrategyType { get; }
    string Name { get; }
    bool IsActive { get; }
    bool IsArmed { get; }
    int CutoffHour { get; }
    int CutoffMinute { get; }

    /// <summary>The ticker symbol this setup trades (may differ from global config for multi-instrument).</summary>
    string Ticker { get; }

    /// <summary>The point value for this setup's instrument (e.g. 20 for NQ, 50 for ES).</summary>
    decimal PointValue { get; }

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
    ExitSignal? PendingExit { get; }
    PartialSignal? PendingPartial { get; }
    BESignal? PendingBE { get; }

    /// <summary>
    /// Snapshot of the active trade captured just before BookExit resets state.
    /// Used by RouteSignalsAsync to build the TradeRecord, since GetActiveTrade
    /// returns null after the strategy has already transitioned to idle.
    /// Cleared by ClearPendingSignals.
    /// </summary>
    ActiveTradeView? PreExitTrade { get; }

    /// <summary>Adjust levels after broker reports actual fill price.</summary>
    void ApplyFill(decimal actualFillPrice);

    /// <summary>Clear all pending signals after engine has processed them.</summary>
    void ClearPendingSignals();

    /// <summary>Revert an uncommitted entry (undo pending entry, keep armed state).</summary>
    void RevertEntry();

    /// <summary>Request force exit of active trade.</summary>
    void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd);

    /// <summary>
    /// Disarm the strategy (reset to idle) without affecting trade counters or P&amp;L.
    /// Used when per-setup cutoff is reached to clear stale armed/waiting states.
    /// </summary>
    void Disarm();

    /// <summary>Snapshot for dashboard display.</summary>
    SetupStateSnapshot GetSnapshot();

    /// <summary>Active trade view with unrealized PnL, or null if no trade.</summary>
    ActiveTradeView? GetActiveTrade(decimal lastPrice);
}
