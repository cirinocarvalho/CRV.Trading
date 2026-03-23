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
    string    OrderType = "Market",   // "Market" or "Limit"
    string    SessionId = "NY",       // "Asia" | "London" | "NY"
    string    Ticker    = "");        // Per-setup ticker override; empty = use global

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
    public ActiveTradeView? SetupA   { get; set; }
    public ActiveTradeView? SetupB   { get; set; }

    // Daily P&L stats
    public decimal    TodayPnl        { get; set; }
    public int        TodayTrades     { get; set; }
    public int        TodayWins       { get; set; }
    public int        TodayLosses     { get; set; }
    public decimal    TodayMaxDD      { get; set; }

    // Per-setup trade counts
    public int        TradeCountA     { get; set; }
    public int        MaxTradesA      { get; set; }
    public int        TradeCountB     { get; set; }
    public int        MaxTradesB      { get; set; }

    // Expectancy (total + per setup)
    public decimal    Expectancy      { get; set; }
    public decimal    ExpectancyA     { get; set; }
    public decimal    ExpectancyB     { get; set; }
    public decimal    ExpectancyC     { get; set; }
    public decimal    ExpectancyD     { get; set; }

    // Daily loss limit
    public decimal    DailyLossLimit { get; set; }
    public decimal    DailyLossUsed  { get; set; }
    public bool       TradingHalted  { get; set; }

    // Stream health — lets dashboard show that data is flowing
    public decimal    LastPrice      { get; set; }
    public DateTime   LastUpdate     { get; set; }

    // Per-setup last price, ticker, and point value (each setup may trade a different instrument)
    public decimal    LastPriceA     { get; set; }
    public decimal    LastPriceB     { get; set; }
    public decimal    LastPriceC     { get; set; }
    public decimal    LastPriceD     { get; set; }
    public string     TickerA        { get; set; } = "";
    public string     TickerB        { get; set; } = "";
    public string     TickerC        { get; set; } = "";
    public string     TickerD        { get; set; } = "";
    public decimal    PointValueA    { get; set; }
    public decimal    PointValueB    { get; set; }
    public decimal    PointValueC    { get; set; }
    public decimal    PointValueD    { get; set; }

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

    // Per-setup ORB state (each ticker group has its own ORB)
    public decimal    OrbHighA       { get; set; }
    public decimal    OrbLowA        { get; set; }
    public decimal    OrbMidA        { get; set; }
    public decimal    OrbRangeA      { get; set; }
    public bool       OrbBullCloseA  { get; set; }
    public bool       OrbBearCloseA  { get; set; }
    public decimal    OrbAtrRatioA   { get; set; }
    public bool       OrbFormedA     { get; set; }

    public decimal    OrbHighB       { get; set; }
    public decimal    OrbLowB        { get; set; }
    public decimal    OrbMidB        { get; set; }
    public decimal    OrbRangeB      { get; set; }
    public bool       OrbBullCloseB  { get; set; }
    public bool       OrbBearCloseB  { get; set; }
    public decimal    OrbAtrRatioB   { get; set; }
    public bool       OrbFormedB     { get; set; }

    public decimal    OrbHighC       { get; set; }
    public decimal    OrbLowC        { get; set; }
    public decimal    OrbMidC        { get; set; }
    public decimal    OrbRangeC      { get; set; }
    public bool       OrbBullCloseC  { get; set; }
    public bool       OrbBearCloseC  { get; set; }
    public decimal    OrbAtrRatioC   { get; set; }
    public bool       OrbFormedC     { get; set; }

    public decimal    OrbHighD       { get; set; }
    public decimal    OrbLowD        { get; set; }
    public decimal    OrbMidD        { get; set; }
    public decimal    OrbRangeD      { get; set; }
    public bool       OrbBullCloseD  { get; set; }
    public bool       OrbBearCloseD  { get; set; }
    public decimal    OrbAtrRatioD   { get; set; }
    public bool       OrbFormedD     { get; set; }

    // Session state
    public bool       OrbFormed      { get; set; }
    public bool       PastCutoff     { get; set; }
    public bool       PastCutoffA    { get; set; }
    public bool       PastCutoffB    { get; set; }
    public bool       SessionEnded   { get; set; }
    public string     ActiveSessionId { get; set; } = "";

    // Active session ORB window (dynamic — changes per session)
    public string  OrbWindowStart  { get; set; } = "";
    public string  OrbWindowEnd    { get; set; } = "";

    // Setup enabled flags (from StrategyConfig.EnableA / EnableB)
    public bool SetupAEnabled { get; set; } = true;
    public bool SetupBEnabled { get; set; } = true;

    // Setup state machine values (from OrbStrategyEngine._stA / _stB)
    // A: 0=Idle  ±1=Armed  ±2=Active
    // B: 0=Idle  ±1=Armed  ±2=Retest  ±3=Active
    public int  SetupAState { get; set; }
    public int  SetupBState { get; set; }

    // Sticky exit chart markers (true on exit bar only)
    public bool StickyTgtA { get; set; }
    public bool StickyStpA { get; set; }
    public bool StickyTgtB { get; set; }
    public bool StickyStpB { get; set; }

    // ── Setup C/D ─────────────────────────────────────────
    public ActiveTradeView? SetupC   { get; set; }
    public ActiveTradeView? SetupD   { get; set; }
    public int  TradeCountC   { get; set; }
    public int  TradeCountD   { get; set; }
    public int  MaxTradesC    { get; set; }
    public int  MaxTradesD    { get; set; }
    public int  SetupCState   { get; set; }
    public int  SetupDState   { get; set; }
    public bool SetupCEnabled { get; set; }
    public bool SetupDEnabled { get; set; }
    public bool PastCutoffC   { get; set; }
    public bool PastCutoffD   { get; set; }
    public bool StickyTgtC    { get; set; }
    public bool StickyStpC    { get; set; }
    public bool StickyTgtD    { get; set; }
    public bool StickyStpD    { get; set; }

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

    // Per-setup daily stats C/D
    public int     TodayWinsC    { get; set; }
    public int     TodayLossesC  { get; set; }
    public decimal TodayWinPnlC  { get; set; }
    public decimal TodayLossPnlC { get; set; }
    public int     TodayWinsD    { get; set; }
    public int     TodayLossesD  { get; set; }
    public decimal TodayWinPnlD  { get; set; }
    public decimal TodayLossPnlD { get; set; }

    // ── Per-setup daily stats A/B ─────────────────────────
    public int     TodayWinsA    { get; set; }
    public int     TodayLossesA  { get; set; }
    public decimal TodayWinPnlA  { get; set; }
    public decimal TodayLossPnlA { get; set; }
    public int     TodayWinsB    { get; set; }
    public int     TodayLossesB  { get; set; }
    public decimal TodayWinPnlB  { get; set; }
    public decimal TodayLossPnlB { get; set; }

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
    public string   Message { get; set; } = "";
    public string   Color   { get; set; } = "gray";
}
