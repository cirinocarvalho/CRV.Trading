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

    /// <summary>Reset all state for new trading day/session.</summary>
    void Reset();

    // ── Pending signals (consumed by engine after OnBar/OnTick) ──
    EntrySignal? PendingEntry { get; }
    ExitSignal? PendingExit { get; }
    PartialSignal? PendingPartial { get; }
    BESignal? PendingBE { get; }

    /// <summary>Adjust levels after broker reports actual fill price.</summary>
    void ApplyFill(decimal actualFillPrice);

    /// <summary>Clear all pending signals after engine has processed them.</summary>
    void ClearPendingSignals();

    /// <summary>Revert an uncommitted entry (undo pending entry, keep armed state).</summary>
    void RevertEntry();

    /// <summary>Request force exit of active trade.</summary>
    void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd);

    /// <summary>Snapshot for dashboard display.</summary>
    SetupStateSnapshot GetSnapshot();

    /// <summary>Active trade view with unrealized PnL, or null if no trade.</summary>
    ActiveTradeView? GetActiveTrade(decimal lastPrice);
}
