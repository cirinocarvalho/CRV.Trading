namespace CRV.Core.Models;

/// <summary>All strategy inputs — shared by live engine and backtest engine.</summary>
public class StrategyConfig
{
    public int     Id              { get; set; }
    public string  Name            { get; set; } = "Default";
    public DateTime UpdatedAt      { get; set; } = DateTime.UtcNow;

    // ── Instrument ──────────────────────────────────────────
    public string  Ticker          { get; set; } = "/NQH2026";
    public string  Exchange        { get; set; } = "CME";
    public decimal PointValue      { get; set; } = 20m;
    public decimal TickSize        { get; set; } = 0.25m;

    // ── Timeframe / Sessions ─────────────────────────────────
    public int     ExecutionTFMinutes { get; set; } = 1;
    public string  Timezone        { get; set; } = "America/New_York";
    public TimeOnly OrbStart       { get; set; } = new(9, 30);
    public TimeOnly OrbEnd         { get; set; } = new(10, 0);
    public TimeOnly RthStart       { get; set; } = new(9, 30);
    public TimeOnly RthEnd         { get; set; } = new(16, 0);

    /// <summary>
    /// Minutes before RthEnd to force-close all active trades (default 0 = close at RthEnd).
    /// Example: 15 means exit all positions at 15:45 when RthEnd is 16:00.
    /// </summary>
    public int     ExitMinutesBefore { get; set; } = 0;

    /// <summary>
    /// Hour (local) when the futures session starts (default 18 = 6 PM ET for CME).
    /// Bars at or after this hour belong to the NEXT trading day.
    /// Used to reset ORB, VWAP, daily stats, and setup state.
    /// </summary>
    public int     SessionStartHour { get; set; } = 18;

    // ── Filters ──────────────────────────────────────────────
    public decimal AtrFilterPct    { get; set; } = 0.50m;
    public bool    UseTimeFilter   { get; set; } = true;
    public int     CutoffHour      { get; set; } = 14;
    public int     CutoffMinute    { get; set; } = 30;
    public bool    UseDailyLossLimit { get; set; } = true;
    public decimal MaxDailyLoss    { get; set; } = 500m;
    public bool    UseVwap         { get; set; } = true;
    public bool    UseOrbClose     { get; set; } = false;
    public bool    AllowBothSameBar{ get; set; } = false;

    // ── Position Sizing ───────────────────────────────────────
    public int     Contracts       { get; set; } = 2;
    public decimal HiVolMult       { get; set; } = 1.0m;
    public int     MaxContracts    { get; set; } = 2;

    // ── Broker ────────────────────────────────────────────────
    public string  Broker          { get; set; } = "Schwab"; // Schwab | TradeStation | Tradovate | Mock
    public string? ExecBroker      { get; set; }             // null = same as Broker
    public string  AccountId       { get; set; } = "";
    public string  ExecAccountId   { get; set; } = "";       // Exec broker account; empty = use AccountId
    public decimal CommissionPerSide { get; set; } = 2.25m;

    // ── Replay ───────────────────────────────────────────────
    public DateTime? ReplayDate        { get; set; }
    public int       ReplaySpeed       { get; set; } = 100;
    public int       ReplayBalance     { get; set; } = 50000;
    public bool      SaveReplayTrades  { get; set; } = false;

    // ── Multi-Session ──────────────────────────────────────────
    [System.Text.Json.Serialization.JsonIgnore]
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<SessionConfig>? Sessions { get; set; }

    // ── Computed helpers (not persisted) ─────────────────────────────────────
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveExecBroker =>
        string.IsNullOrWhiteSpace(ExecBroker) ? Broker : ExecBroker;
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveExecAccountId =>
        string.IsNullOrWhiteSpace(ExecAccountId) ? AccountId : ExecAccountId;

    // ── Setup A — Pullback ────────────────────────────────────
    public bool    EnableA         { get; set; } = true;
    public string  ModeA           { get; set; } = "Conservative";
    public int     MaxTradesA      { get; set; } = 5;
    public decimal NearPct         { get; set; } = 0.15m;   // legacy — alias for NearPctA (EF column kept for compat)
    public decimal NearPctA        { get; set; } = 0.15m;
    public decimal PullbackPct     { get; set; } = 0.50m;
    public decimal StopPctA        { get; set; } = 0.10m;
    public int     TargetPctA      { get; set; } = 100;
    public int     PartialPctA     { get; set; } = 50;
    public decimal MinRrA          { get; set; } = 1.5m;
    public bool    UsePartialA     { get; set; } = true;
    public bool    UseBeA          { get; set; } = true;
    public bool    AllowRearmAfterBeA { get; set; } = true;

    /// <summary>
    /// Entry price tick offset for Setup A. Default 0 (no offset).
    /// Long: entry + (offset * TickSize), Short: entry - (offset * TickSize).
    /// Positive values push the entry in the trade direction (improves fill probability).
    /// </summary>
    public int     EntryTickOffsetA { get; set; } = 0;

    /// <summary>Order type for Setup A entries: "Market" or "Limit". Default "Market".</summary>
    public string  OrderTypeA      { get; set; } = "Market";

    /// <summary>Fixed partial exit contracts for Setup A. 0 = auto (50% floor).</summary>
    public int     PartialCtsA      { get; set; } = 0;
    /// <summary>Exit if trade is underwater after N minutes. 0 = disabled.</summary>
    public int     MaxAdverseMinutesA { get; set; } = 0;

    // Per-setup filters / sizing (A)
    public int     ContractsA       { get; set; } = 2;
    public decimal HiVolMultA       { get; set; } = 1.0m;
    public int     MaxContractsA    { get; set; } = 2;
    public bool    UseVwapA         { get; set; } = true;
    public bool    UseOrbCloseA     { get; set; } = false;
    public int     CutoffHourA      { get; set; } = 14;
    public int     CutoffMinuteA    { get; set; } = 30;
    public bool    CloseAtRthCloseA { get; set; } = true;

    // ── Setup B — Breakout Retest ─────────────────────────────
    public bool    EnableB         { get; set; } = true;
    public string  ModeB           { get; set; } = "Conservative";
    public int     MaxTradesB      { get; set; } = 5;
    public decimal NearPctB        { get; set; } = 0.15m;
    public decimal RetestPct       { get; set; } = 0.05m;
    public int     TargetPctB      { get; set; } = 100;
    public int     PartialPctB     { get; set; } = 50;
    public decimal MinRrB          { get; set; } = 1.5m;
    public bool    UsePartialB     { get; set; } = true;
    public bool    UseBeB          { get; set; } = true;
    public bool    AllowRearmAfterBeB { get; set; } = true;

    /// <summary>
    /// Entry price tick offset for Setup B. Default 0 (no offset).
    /// Long: entry + (offset * TickSize), Short: entry - (offset * TickSize).
    /// Positive values push the entry in the trade direction (improves fill probability).
    /// </summary>
    public int     EntryTickOffsetB { get; set; } = 0;

    /// <summary>Order type for Setup B entries: "Market" or "Limit". Default "Market".</summary>
    public string  OrderTypeB      { get; set; } = "Market";

    /// <summary>Fixed partial exit contracts for Setup B. 0 = auto (50% floor).</summary>
    public int     PartialCtsB      { get; set; } = 0;
    /// <summary>Exit if trade is underwater after N minutes. 0 = disabled.</summary>
    public int     MaxAdverseMinutesB { get; set; } = 0;

    /// <summary>
    /// Stop distance for Setup B as a fraction of ORB range (default 0.50 = 50%).
    /// Long: stop = entry - orbRange * StopPctB.
    /// Short: stop = entry + orbRange * StopPctB.
    /// Default 0.50 is identical to the legacy orbMid stop for a symmetric ORB.
    /// </summary>
    public decimal StopPctB        { get; set; } = 0.50m;

    // Per-setup filters / sizing (B)
    public int     ContractsB       { get; set; } = 2;
    public decimal HiVolMultB       { get; set; } = 1.0m;
    public int     MaxContractsB    { get; set; } = 2;
    public bool    UseVwapB         { get; set; } = true;
    public bool    UseOrbCloseB     { get; set; } = false;
    public int     CutoffHourB      { get; set; } = 14;
    public int     CutoffMinuteB    { get; set; } = 30;
    public bool    CloseAtRthCloseB { get; set; } = true;

    // ── Setup C — ORB False Breakout ──────────────────────────────────────
    public bool    EnableC            { get; set; } = false;
    public int     MaxTradesC         { get; set; } = 3;
    public decimal NearPctC           { get; set; } = 0.15m;
    public decimal StopPctC           { get; set; } = 0.10m;
    public int     TargetPctC         { get; set; } = 100;
    public int     PartialPctC        { get; set; } = 50;
    public decimal MinRrC             { get; set; } = 1.5m;
    public bool    UsePartialC        { get; set; } = true;
    public bool    UseBeC             { get; set; } = true;
    public bool    AllowRearmAfterBeC { get; set; } = true;
    public int     EntryTickOffsetC   { get; set; } = 0;
    public string  OrderTypeC         { get; set; } = "Market";
    public int     PartialCtsC        { get; set; } = 0;
    public int     MaxAdverseMinutesC { get; set; } = 0;
    public int     ContractsC         { get; set; } = 2;
    public decimal HiVolMultC         { get; set; } = 1.0m;
    public int     MaxContractsC      { get; set; } = 2;
    public int     CutoffHourC        { get; set; } = 14;
    public int     CutoffMinuteC      { get; set; } = 30;
    public bool    CloseAtRthCloseC   { get; set; } = true;

    // ── Setup D — Session Range False Breakout ──────────────────────────
    public bool    EnableD            { get; set; } = false;
    public int     MaxTradesD         { get; set; } = 3;
    public decimal NearPctD           { get; set; } = 0.15m;
    public decimal StopPctD           { get; set; } = 0.10m;
    public int     TargetPctD         { get; set; } = 100;
    public int     PartialPctD        { get; set; } = 50;
    public decimal MinRrD             { get; set; } = 1.5m;
    public bool    UsePartialD        { get; set; } = true;
    public bool    UseBeD             { get; set; } = true;
    public bool    AllowRearmAfterBeD { get; set; } = true;
    public int     EntryTickOffsetD   { get; set; } = 0;
    public string  OrderTypeD         { get; set; } = "Market";
    public int     PartialCtsD        { get; set; } = 0;
    public int     MaxAdverseMinutesD { get; set; } = 0;
    public int     ContractsD         { get; set; } = 2;
    public decimal HiVolMultD         { get; set; } = 1.0m;
    public int     MaxContractsD      { get; set; } = 2;
    public int     CutoffHourD        { get; set; } = 14;
    public int     CutoffMinuteD      { get; set; } = 30;
    public bool    CloseAtRthCloseD   { get; set; } = true;

    // ── False Breakout Module Params ──────────────────────────────────────
    public int     FBMaxTimeOutsideMinutesOrb { get; set; } = 15;
    public int     FBMaxTimeOutsideMinutesSR  { get; set; } = 60;
    public decimal FBMaxPenetrationPctOrb     { get; set; } = 0.30m;
    public decimal FBMaxPenetrationPctSR      { get; set; } = 0.25m;
    public decimal FBMinRejectionBodyPct      { get; set; } = 0.50m;
    public int     FBMaxTrendDayScore         { get; set; } = 60;

    // ── Module params (kept for market context modules) ──────────
    public decimal SweepMinPenetration  { get; set; } = 0.50m;
    public decimal SweepMinBodyReject   { get; set; } = 1.00m;
    public decimal SweepEqualTolerance  { get; set; } = 2.00m;
    public int     SweepConfirmBars     { get; set; } = 1;
    public decimal DriveRangeAtrMult    { get; set; } = 0.80m;
    public decimal DriveMaxPullback     { get; set; } = 0.35m;
    public int     DriveBullBearRatio   { get; set; } = 2;
    public int     TrendDayThreshold    { get; set; } = 4;
    public decimal ShallowPullbackMax   { get; set; } = 0.35m;
    public int     VwapDevPeriod        { get; set; } = 20;

    // ── Forced Exit ───────────────────────────────────────────
    public bool    CloseAtRthClose { get; set; } = true;

    // ── Computed helpers ─────────────────────────────────────
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAggressiveA  => ModeA == "Aggressive";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAggressiveB  => ModeB == "Aggressive";
    [System.Text.Json.Serialization.JsonIgnore]
    public int  PullbackPctInt => (int)(PullbackPct * 100);

    /// <summary>
    /// Returns the "trading date" for a given local time based on <see cref="SessionStartHour"/>.
    /// Futures sessions span midnight — bars at or after SessionStartHour (e.g. 18:00)
    /// belong to the NEXT calendar day's trading session.
    /// Example with SessionStartHour=18:
    ///   17:59 on Mar 9 → trading date Mar 9  (still previous session)
    ///   18:00 on Mar 9 → trading date Mar 10 (new session started)
    ///   09:30 on Mar 10 → trading date Mar 10
    /// </summary>
    public DateTime TradingDate(DateTime localTime)
    {
        if (localTime.Hour >= SessionStartHour)
            return localTime.Date.AddDays(1);
        return localTime.Date;
    }

    public StrategyConfig Clone() => (StrategyConfig)MemberwiseClone();

    /// <summary>Validates all parameters. Returns a list of error messages (empty = valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Ticker))
            errors.Add("Ticker is required.");
        if (PointValue <= 0)
            errors.Add("PointValue must be positive.");
        if (TickSize <= 0)
            errors.Add("TickSize must be positive.");
        if (ExecutionTFMinutes <= 0)
            errors.Add("ExecutionTFMinutes must be positive.");
        if (OrbEnd <= OrbStart)
            errors.Add("OrbEnd must be after OrbStart.");
        if (RthEnd <= RthStart)
            errors.Add("RthEnd must be after RthStart.");
        if (AtrFilterPct < 0)
            errors.Add("AtrFilterPct must be non-negative.");
        if (UseDailyLossLimit && MaxDailyLoss <= 0)
            errors.Add("MaxDailyLoss must be positive when daily loss limit is enabled.");
        if (Contracts <= 0)
            errors.Add("Contracts must be positive.");
        if (MaxContracts < Contracts)
            errors.Add("MaxContracts must be >= Contracts.");
        if (CommissionPerSide < 0)
            errors.Add("CommissionPerSide must be non-negative.");
        var validBrokers = new[] { "Schwab", "TradeStation", "Tradovate", "TradovateReplay", "Mock" };
        if (!string.IsNullOrWhiteSpace(ExecBroker) && !validBrokers.Contains(ExecBroker))
            errors.Add($"ExecBroker must be one of: {string.Join(", ", validBrokers)}.");
        if (CutoffHour < 0 || CutoffHour > 23)
            errors.Add("CutoffHour must be 0-23.");
        if (CutoffMinute < 0 || CutoffMinute > 59)
            errors.Add("CutoffMinute must be 0-59.");
        if (SessionStartHour < 0 || SessionStartHour > 23)
            errors.Add("SessionStartHour must be 0-23.");

        if (EnableA)
        {
            if (StopPctA <= 0)
                errors.Add("StopPctA must be positive.");
            if (NearPctA <= 0 || NearPctA > 1)
                errors.Add("NearPctA must be between 0 and 1.");
            if (PullbackPct <= 0 || PullbackPct > 1)
                errors.Add("PullbackPct must be between 0 and 1.");
            if (MinRrA <= 0)
                errors.Add("MinRrA must be positive.");
            if (MaxTradesA < 0)
                errors.Add("MaxTradesA must be non-negative.");
            if (TargetPctA <= 0)
                errors.Add("TargetPctA must be positive.");
            if (PartialPctA <= 0 || PartialPctA >= 100)
                errors.Add("PartialPctA must be between 0 and 100.");
            if (EntryTickOffsetA < 0)
                errors.Add("EntryTickOffsetA must be non-negative.");
            if (ContractsA <= 0)
                errors.Add("ContractsA must be positive.");
            if (PartialCtsA > 0 && PartialCtsA >= ContractsA)
                errors.Add("PartialCtsA must be less than ContractsA.");
            if (CutoffHourA < 0 || CutoffHourA > 23)
                errors.Add("CutoffHourA must be 0-23.");
            if (CutoffMinuteA < 0 || CutoffMinuteA > 59)
                errors.Add("CutoffMinuteA must be 0-59.");
        }

        if (EnableB)
        {
            if (NearPctB <= 0 || NearPctB > 1)
                errors.Add("NearPctB must be between 0 and 1.");
            if (RetestPct <= 0 || RetestPct > 1)
                errors.Add("RetestPct must be between 0 and 1.");
            if (MinRrB <= 0)
                errors.Add("MinRrB must be positive.");
            if (MaxTradesB < 0)
                errors.Add("MaxTradesB must be non-negative.");
            if (TargetPctB <= 0)
                errors.Add("TargetPctB must be positive.");
            if (PartialPctB <= 0 || PartialPctB >= 100)
                errors.Add("PartialPctB must be between 0 and 100.");
            if (EntryTickOffsetB < 0)
                errors.Add("EntryTickOffsetB must be non-negative.");
            if (StopPctB <= 0)
                errors.Add("StopPctB must be positive.");
            if (ContractsB <= 0)
                errors.Add("ContractsB must be positive.");
            if (PartialCtsB > 0 && PartialCtsB >= ContractsB)
                errors.Add("PartialCtsB must be less than ContractsB.");
            if (CutoffHourB < 0 || CutoffHourB > 23)
                errors.Add("CutoffHourB must be 0-23.");
            if (CutoffMinuteB < 0 || CutoffMinuteB > 59)
                errors.Add("CutoffMinuteB must be 0-59.");
        }

        return errors;
    }
}
