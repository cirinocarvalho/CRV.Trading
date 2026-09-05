using CRV.Core.Modules;

namespace CRV.Core.Models;

/// <summary>
/// Global engine configuration (not per-setup). Extracted from StrategyConfig
/// to separate engine-level concerns from setup-level concerns.
/// </summary>
public class EngineConfig
{
    // ── Session Timing ──────────────────────────────────────────
    public int     SessionStartHour   { get; set; } = 18;
    public TimeOnly OrbStart          { get; set; } = new(9, 30);
    public TimeOnly OrbEnd            { get; set; } = new(10, 0);

    // ── RTH Timing ──────────────────────────────────────────────
    public TimeOnly RthStart          { get; set; } = new(9, 30);
    public TimeOnly RthEnd            { get; set; } = new(16, 0);
    public int     ExitMinutesBefore  { get; set; } = 0;

    // ── Timezone / Timeframe ────────────────────────────────────
    public string  Timezone           { get; set; } = "America/New_York";
    public int     ExecutionTFMinutes { get; set; } = 1;

    // ── Default Instrument (fallback) ───────────────────────────
    public string  Ticker             { get; set; } = "/NQH2026";
    public decimal PointValue         { get; set; } = 20m;
    public decimal TickSize           { get; set; } = 0.25m;

    // ── Risk ────────────────────────────────────────────────────
    public bool           UseDailyLossLimit  { get; set; } = true;
    public decimal        MaxDailyLoss       { get; set; } = 500m;

    /// <summary>Ceiling on dollar risk across all open and working positions. Zero disables it.</summary>
    public decimal MaxPortfolioRisk   { get; set; } = 0m;
    public DailyLossMode  DailyLossMode      { get; set; } = DailyLossMode.Floor;

    // ── ATR Filter ──────────────────────────────────────────────
    public decimal AtrFilterPct       { get; set; } = 0.50m;

    // ── Commission ──────────────────────────────────────────────
    public decimal CommissionPerSide  { get; set; } = 2.25m;

    // ── Cross-Setup Coordination ────────────────────────────────
    public bool    AllowBothSameBar   { get; set; } = false;

    // ── Chop Regime Filter ──────────────────────────────────────
    public bool          UseChopFilter             { get; set; } = false;
    public ChopBlockMode ChopBlockMode             { get; set; } = ChopBlockMode.UntilClear;
    public int           ChopMinVotes              { get; set; } = 2;
    public bool          ChopUseRangeCompression   { get; set; } = true;
    public decimal       ChopCompressionRatio      { get; set; } = 0.70m;
    public bool          ChopUseFlatVwap           { get; set; } = true;
    public decimal       ChopFlatSlopeThresholdPct { get; set; } = 0.05m;
    public bool          ChopUseWeakDrive          { get; set; } = true;
    public decimal       ChopMinDriveRatio         { get; set; } = 0.50m;
    public bool          ChopUseLowVolume          { get; set; } = true;
    public decimal       ChopMinVolumeRatio        { get; set; } = 1.00m;

    // ── Broker ──────────────────────────────────────────────────
    public string  Broker             { get; set; } = "Schwab";
    public string? ExecBroker         { get; set; }
    public string  AccountId          { get; set; } = "";
    public string  ExecAccountId      { get; set; } = "";

    // ── Replay ──────────────────────────────────────────────────
    public DateTime? ReplayDate       { get; set; }
    public int     ReplaySpeed        { get; set; } = 100;

    // ── Module Configuration ────────────────────────────────────
    public ModuleConfig Modules       { get; set; } = new();

    // ── False Breakout Module Params ────────────────────────────
    public int     FBMaxTimeOutsideMinutesOrb { get; set; } = 15;
    public int     FBMaxTimeOutsideMinutesSR  { get; set; } = 60;
    public decimal FBMaxPenetrationPctOrb     { get; set; } = 0.30m;
    public decimal FBMaxPenetrationPctSR      { get; set; } = 0.25m;
    public decimal FBMinRejectionBodyPct      { get; set; } = 0.50m;
    public int     FBMaxTrendDayScore         { get; set; } = 60;
}
