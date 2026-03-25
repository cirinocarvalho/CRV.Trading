namespace CRV.Core.Models;

// ── Enums ────────────────────────────────────────────────────
public enum SetupId    { A, B, C, D, F }
public enum Direction  { Long, Short }
public enum ExitReason { Target, Stop, SessionEnd, Manual, AdverseTime }

// ── Live signals fired by the strategy engine ─────────────────
public record EntrySignal(
    SetupId   Setup,
    Direction Direction,
    decimal   Entry,
    decimal   Stop,
    decimal   Target,
    decimal   Partial,
    int       Contracts,
    DateTime  Time,
    string    OrderType  = "Market",   // "Market" or "Limit"
    string    SessionId  = "NY",       // "Asia" | "London" | "NY"
    string    Ticker     = "",         // Per-setup ticker override; empty = use global
    string    SetupLabel = "");        // Basket entry ID (e.g. "retest-mnq"); empty = use Setup enum

// ── Bridge extensions for WSS order management transition ───
// Allow BrokerEventHandler to read EntrySignal using new field names
// while strategies still construct using old names (Target, Partial, Contracts).
public static class EntrySignalExtensions
{
    public static decimal Tg1Price(this EntrySignal s) => s.Partial;
    public static decimal Tg2Price(this EntrySignal s) => s.Target;
    public static int TotalContracts(this EntrySignal s) => s.Contracts;
}

public record ExitSignal(
    SetupId    Setup,
    ExitReason Reason,
    decimal    ExitPrice,
    int        Contracts,
    DateTime   Time,
    string     Ticker = "");          // Per-setup ticker override; empty = use global

public record PartialSignal(
    SetupId   Setup,
    Direction Direction,
    decimal   PartialPrice,
    int       ContractsExited,
    int       ContractsRemaining,
    decimal   Entry,
    DateTime  Time);

public record BESignal(
    SetupId   Setup,
    Direction Direction,
    decimal   NewStop,
    decimal   Entry,
    int       ContractsRemaining,
    DateTime  Time);

// ── Completed trade — persisted to SQLite ────────────────────
public class TradeRecord
{
    public int        Id             { get; set; }
    public string     SessionId      { get; set; } = "";
    public string     Source         { get; set; } = "live"; // "live" | "backtest"
    public SetupId    Setup          { get; set; }
    /// <summary>String ID for basket entries (e.g. "b-mnq-1"). Falls back to Setup.ToString() for legacy.</summary>
    public string     SetupLabel     { get; set; } = "";
    public Direction  Direction      { get; set; }
    public string     Ticker         { get; set; } = "";
    public int        Contracts      { get; set; }
    public decimal    Entry          { get; set; }
    public decimal    InitialStop    { get; set; }
    public decimal    Target         { get; set; }
    public decimal    Partial        { get; set; }
    public decimal    Exit           { get; set; }
    public ExitReason ExitReason     { get; set; }
    public bool       PartialFilled  { get; set; }
    public decimal    PartialPrice   { get; set; }
    public decimal    GrossPnl       { get; set; }
    public decimal    Commission     { get; set; }
    public decimal    NetPnl         { get; set; }
    public decimal    RMultiple      { get; set; }
    public DateTime   EnteredAt      { get; set; }
    public DateTime   ExitedAt       { get; set; }
    public TimeSpan   Duration          => ExitedAt - EnteredAt;
    public bool       IsWin             => NetPnl > 0;
    /// <summary>
    /// Direction derived from InitialStop vs Entry — always correct regardless of
    /// how the Direction field was stored. Long = Stop below Entry, Short = Stop above Entry.
    /// Falls back to Target vs Entry if InitialStop is not set.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Direction EffectiveDirection =>
        InitialStop != 0 && InitialStop != Entry
            ? (InitialStop < Entry ? Direction.Long : Direction.Short)
            : (Target > Entry      ? Direction.Long : Direction.Short);
}

// ── In-flight trade — in-memory only ─────────────────────────
public class ActiveTradeState
{
    public SetupId    Setup              { get; set; }
    public Direction  Direction          { get; set; }
    public string     Ticker             { get; set; } = "";
    public int        Contracts          { get; set; }
    public int        RemainingContracts { get; set; }
    public decimal    Entry              { get; set; }
    public decimal    Stop               { get; set; }
    public decimal    InitialStop        { get; set; }
    public decimal    Target             { get; set; }
    public decimal    Partial            { get; set; }
    public bool       PartialFilled      { get; set; }
    public DateTime   EnteredAt          { get; set; }
    public decimal    AccruedPnl         { get; set; }
}

// ── Snapshot pushed to dashboard via SignalR ─────────────────
public class EngineSnapshot
{
    public DateTime   Time           { get; set; }
    public string     Ticker         { get; set; } = "";
    public bool       IsLive         { get; set; }

    // Daily P&L stats
    public decimal    TodayPnl        { get; set; }
    public int        TodayTrades     { get; set; }
    public int        TodayWins       { get; set; }
    public int        TodayLosses     { get; set; }
    public decimal    TodayMaxDD      { get; set; }

    // Expectancy (total)
    public decimal    Expectancy      { get; set; }

    // Daily loss limit
    public decimal    DailyLossLimit { get; set; }
    public decimal    DailyLossUsed  { get; set; }
    public bool       TradingHalted  { get; set; }

    // Stream health — lets dashboard show that data is flowing
    public decimal    LastPrice      { get; set; }
    public DateTime   LastUpdate     { get; set; }

    // Indicators
    public decimal    Vwap           { get; set; }
    public decimal    Atr            { get; set; }

    // ORB levels
    public decimal    OrbHigh        { get; set; }
    public decimal    OrbLow         { get; set; }
    public decimal    OrbMid         { get; set; }
    public decimal    OrbRange       { get; set; }
    public bool       OrbBullClose   { get; set; }
    public bool       OrbBearClose   { get; set; }
    public decimal    OrbAtrRatio    { get; set; }  // frozen at ORB formation (primary group)

    // Session state
    public bool       OrbFormed      { get; set; }
    public bool       PastCutoff     { get; set; }
    public bool       SessionEnded   { get; set; }
    public string     ActiveSessionId { get; set; } = "";

    // Active session ORB window (dynamic — changes per session)
    public string  OrbWindowStart  { get; set; } = "";
    public string  OrbWindowEnd    { get; set; } = "";

    // ── FalseBreakout module context ──────────────────────
    public bool    FBOrbBreakoutActive      { get; set; }
    public bool    FBSessionBreakoutActive  { get; set; }
    public int     FBOrbBarsInBreakout      { get; set; }
    public int     FBSessionBarsInBreakout  { get; set; }
    public decimal FBOrbPenetrationDepth    { get; set; }
    public decimal FBSessionPenetrationDepth { get; set; }
    public bool    FBOrbActivated           { get; set; }
    public bool    FBSessionActivated       { get; set; }
    public bool    IsCompoundFakeout        { get; set; }

    // ── Module outputs ───────────────────────────────────────
    // Session
    public string  CurrentSession  { get; set; } = "";
    public decimal SessionHigh     { get; set; }
    public decimal SessionLow      { get; set; }
    public decimal PrevDayHigh     { get; set; }
    public decimal PrevDayLow      { get; set; }
    public bool    AsiaCompressed  { get; set; }

    // Sweep
    public string  LastSweep       { get; set; } = "";

    // VWAP Model
    public decimal VwapUpper1      { get; set; }
    public decimal VwapUpper2      { get; set; }
    public decimal VwapLower1      { get; set; }
    public decimal VwapLower2      { get; set; }
    public int     VwapState       { get; set; }

    // Opening Drive
    public bool    OpeningDriveBull { get; set; }
    public bool    OpeningDriveBear { get; set; }

    // Trend Day
    public int     TrendScoreBull  { get; set; }
    public int     TrendScoreBear  { get; set; }

    // Composite Setups

    // Signal Strength (each 0–5, composite = average)
    public decimal DriveScore      { get; set; }
    public decimal SweepScore      { get; set; }
    public decimal VwapDevScore    { get; set; }
    public decimal SignalStrength  { get; set; }

    public List<AlertEvent> RecentAlerts { get; set; } = new();

    /// <summary>Dynamic setup snapshots (replaces flat SetupA/B/C/D fields).</summary>
    public List<SetupSnapshot> Setups { get; set; } = new();

    /// <summary>
    /// Per-ticker-group module snapshots. Key = group key (e.g. "NQ", "ES", "GC", "CL").
    /// Dashboard uses this to switch the Market Context / False Breakout / ORB / VWAP / ATR panels.
    /// </summary>
    public Dictionary<string, TickerGroupSnapshot> GroupSnapshots { get; set; } = new();
}

public class ActiveTradeView
{
    public SetupId    Setup              { get; set; }
    public Direction  Direction          { get; set; }
    public decimal    Entry              { get; set; }
    public decimal    InitialStop        { get; set; }
    public decimal    CurrentStop        { get; set; }
    public decimal    Target             { get; set; }
    public decimal    Partial            { get; set; }
    public int        Contracts          { get; set; }
    public int        RemainingContracts { get; set; }
    public bool       PartialFilled      { get; set; }
    public decimal    LastPrice          { get; set; }
    public decimal    UnrealizedPnl      { get; set; }
    public DateTime   EnteredAt          { get; set; }
    public string     Ticker             { get; set; } = "";
    public decimal    PointValue         { get; set; }

    /// <summary>WSS group order status (null when using legacy path).</summary>
    public string?    GroupStatus        { get; set; }
}

/// <summary>
/// Per-setup snapshot for the dashboard. Replaces the flat SetupA/B/C/D fields.
/// </summary>
public class SetupSnapshot
{
    public string   Id             { get; set; } = "";    // "A", "B", "C", "D" (later: "b-mnq-1")
    public string   Label          { get; set; } = "";    // "B — Breakout [BONGA]"
    public string   StrategyType   { get; set; } = "";    // "Pullback", "Retest", etc.
    public string   Ticker         { get; set; } = "";
    public decimal  PointValue     { get; set; }
    public decimal  LastPrice      { get; set; }
    public bool     Enabled        { get; set; }
    public int      State          { get; set; }          // state machine value
    public bool     PastCutoff     { get; set; }

    // Trade
    public ActiveTradeView? Trade  { get; set; }
    public int      TradeCount     { get; set; }
    public int      MaxTrades      { get; set; }
    public bool     StickyTgt      { get; set; }
    public bool     StickyStp      { get; set; }

    // Daily stats
    public int      Wins           { get; set; }
    public int      Losses         { get; set; }
    public decimal  WinPnl         { get; set; }
    public decimal  LossPnl        { get; set; }
    public decimal  Expectancy     { get; set; }

    // Per-setup ORB
    public decimal  OrbHigh        { get; set; }
    public decimal  OrbLow         { get; set; }
    public decimal  OrbMid         { get; set; }
    public decimal  OrbRange       { get; set; }
    public bool     OrbBullClose   { get; set; }
    public bool     OrbBearClose   { get; set; }
    public decimal  OrbAtrRatio    { get; set; }
    public bool     OrbFormed      { get; set; }
}

/// <summary>
/// Per-ticker-group snapshot of module/indicator state for the dashboard selector.
/// Each group (NQ, ES, GC, CL) has its own session, ORB, VWAP, trend, and fakeout data.
/// </summary>
public class TickerGroupSnapshot
{
    public string GroupKey { get; set; } = "";

    /// <summary>Representative broker ticker for this group (e.g. "MNQM26").</summary>
    public string Ticker { get; set; } = "";

    // Session
    public string  CurrentSession  { get; set; } = "";
    public decimal SessionHigh     { get; set; }
    public decimal SessionLow      { get; set; }
    public decimal PrevDayHigh     { get; set; }
    public decimal PrevDayLow      { get; set; }
    public bool    AsiaCompressed  { get; set; }

    // Sweep
    public string  LastSweep       { get; set; } = "";

    // VWAP
    public decimal Vwap            { get; set; }
    public decimal VwapUpper1      { get; set; }
    public decimal VwapUpper2      { get; set; }
    public decimal VwapLower1      { get; set; }
    public decimal VwapLower2      { get; set; }
    public int     VwapState       { get; set; }

    // Opening Drive
    public bool    OpeningDriveBull { get; set; }
    public bool    OpeningDriveBear { get; set; }

    // Trend Day
    public int     TrendScoreBull  { get; set; }
    public int     TrendScoreBear  { get; set; }

    // ORB
    public decimal OrbHigh         { get; set; }
    public decimal OrbLow          { get; set; }
    public decimal OrbMid          { get; set; }
    public decimal OrbRange        { get; set; }
    public bool    OrbBullClose    { get; set; }
    public bool    OrbBearClose    { get; set; }
    public decimal OrbAtrRatio     { get; set; }
    public bool    OrbFormed       { get; set; }

    // ATR
    public decimal Atr             { get; set; }

    // Current bar (for live chart updates)
    public long    BarTime         { get; set; }  // UTC unix seconds
    public decimal BarOpen         { get; set; }
    public decimal BarHigh         { get; set; }
    public decimal BarLow          { get; set; }
    public decimal BarClose        { get; set; }
    public long    BarVolume       { get; set; }

    // False Breakout
    public bool    FBOrbBreakoutActive       { get; set; }
    public bool    FBSessionBreakoutActive   { get; set; }
    public int     FBOrbBarsInBreakout       { get; set; }
    public int     FBSessionBarsInBreakout   { get; set; }
    public decimal FBOrbPenetrationDepth     { get; set; }
    public decimal FBSessionPenetrationDepth { get; set; }
    public bool    FBOrbActivated            { get; set; }
    public bool    FBSessionActivated        { get; set; }
    public bool    IsCompoundFakeout         { get; set; }

    // Signal Strength
    public decimal DriveScore      { get; set; }
    public decimal SweepScore      { get; set; }
    public decimal VwapDevScore    { get; set; }
    public decimal SignalStrength  { get; set; }
}

public class AlertEvent
{
    public DateTime Time    { get; set; }
    public string   Type    { get; set; } = "";
    public SetupId  Setup   { get; set; }
    public string   SetupLabel { get; set; } = "";
    public string   Message { get; set; } = "";
    public string   Color   { get; set; } = "gray";
}
