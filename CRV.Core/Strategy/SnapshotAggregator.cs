using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

/// <summary>
/// Builds an <see cref="EngineSnapshot"/> from individual strategy snapshots,
/// risk state, indicator state, and module state. Produces output identical to
/// the existing OrbStrategyEngine.PublishSnapshot method.
/// </summary>
public static class SnapshotAggregator
{
    /// <summary>All inputs needed to produce an EngineSnapshot.</summary>
    public class Inputs
    {
        // ── Strategies ──────────────────────────────────────────
        public IReadOnlyList<ISetupStrategy> Strategies { get; init; } = Array.Empty<ISetupStrategy>();

        // ── Risk ────────────────────────────────────────────────
        public RiskManager Risk { get; init; } = new();

        // ── Indicator state ─────────────────────────────────────
        public OrbState Orb { get; init; }
        public IndicatorState Indicators { get; init; }
        public decimal OrbAtrRatio { get; init; }

        // ── Per-setup ORB state (each ticker group has its own ORB) ──
        public Dictionary<SetupId, OrbState> PerSetupOrb { get; init; } = new();

        // ── Session / time state ────────────────────────────────
        public DateTime BarTime { get; init; }
        public string Ticker { get; init; } = "";
        public bool IsLive { get; init; }
        public decimal LastPrice { get; init; }
        public bool PastCutoff { get; init; }
        public bool SessionEnded { get; init; }
        public string ActiveSessionId { get; init; } = "";
        public string OrbWindowStart { get; init; } = "";
        public string OrbWindowEnd { get; init; } = "";

        // ── Price provider (for per-setup last price) ────────────
        public ILastPriceProvider? Prices { get; init; }

        // ── Config values ───────────────────────────────────────
        public decimal DailyLossLimit { get; init; }

        // ── FalseBreakout module state ──────────────────────────
        public bool FBOrbBreakoutActive { get; init; }
        public bool FBSessionBreakoutActive { get; init; }
        public int FBOrbBarsInBreakout { get; init; }
        public int FBSessionBarsInBreakout { get; init; }
        public decimal FBOrbPenetrationDepth { get; init; }
        public decimal FBSessionPenetrationDepth { get; init; }
        public bool FBOrbActivated { get; init; }
        public bool FBSessionActivated { get; init; }
        public bool IsCompoundFakeout { get; init; }

        // ── Module outputs ──────────────────────────────────────
        public string CurrentSession { get; init; } = "";
        public decimal SessionHigh { get; init; }
        public decimal SessionLow { get; init; }
        public decimal PrevDayHigh { get; init; }
        public decimal PrevDayLow { get; init; }
        public bool AsiaCompressed { get; init; }
        public string LastSweep { get; init; } = "";

        // VWAP model
        public decimal VwapUpper1 { get; init; }
        public decimal VwapUpper2 { get; init; }
        public decimal VwapLower1 { get; init; }
        public decimal VwapLower2 { get; init; }
        public int VwapState { get; init; }

        // Opening Drive
        public bool OpeningDriveBull { get; init; }
        public bool OpeningDriveBear { get; init; }

        // Trend Day
        public int TrendScoreBull { get; init; }
        public int TrendScoreBear { get; init; }

        // Signal Strength scores (pre-computed)
        public decimal DriveScore { get; init; }
        public decimal SweepScore { get; init; }
        public decimal VwapDevScore { get; init; }
        public decimal SignalStrength { get; init; }

        // Alerts
        public List<AlertEvent> RecentAlerts { get; init; } = new();

        // Per-ticker-group snapshots for dashboard selector
        public Dictionary<string, TickerGroupSnapshot> GroupSnapshots { get; init; } = new();
    }

    /// <summary>
    /// Builds a complete <see cref="EngineSnapshot"/> from the given inputs.
    /// Each setup strategy is mapped to the correct EngineSnapshot fields
    /// by its <see cref="ISetupStrategy.SetupId"/>.
    /// </summary>
    public static EngineSnapshot Build(Inputs inputs)
    {
        var snap = new EngineSnapshot
        {
            Time           = inputs.BarTime,
            Ticker         = inputs.Ticker,
            IsLive         = inputs.IsLive,
            LastPrice      = inputs.LastPrice,
            LastUpdate     = DateTime.UtcNow,

            // Risk
            TodayPnl       = inputs.Risk.TodayPnl,
            TodayTrades    = inputs.Risk.TodayWins + inputs.Risk.TodayLosses,
            TodayWins      = inputs.Risk.TodayWins,
            TodayLosses    = inputs.Risk.TodayLosses,
            TodayMaxDD     = inputs.Risk.TodayMaxDD,
            DailyLossLimit = inputs.DailyLossLimit,
            DailyLossUsed  = Math.Abs(Math.Min(0, inputs.Risk.TodayPnl)),
            TradingHalted  = inputs.Risk.DdBreached,

            // Indicators
            Vwap = inputs.Indicators.Vwap,
            Atr  = inputs.Indicators.Atr,

            // ORB levels
            OrbHigh      = inputs.Orb.High,
            OrbLow       = inputs.Orb.Low,
            OrbMid       = inputs.Orb.Mid,
            OrbRange     = inputs.Orb.Range,
            OrbBullClose = inputs.Orb.BullClose,
            OrbBearClose = inputs.Orb.BearClose,
            OrbAtrRatio  = inputs.OrbAtrRatio,

            // Session state
            OrbFormed       = inputs.Orb.IsSet,
            PastCutoff      = inputs.PastCutoff,
            SessionEnded    = inputs.SessionEnded,
            ActiveSessionId = inputs.ActiveSessionId,
            OrbWindowStart  = inputs.OrbWindowStart,
            OrbWindowEnd    = inputs.OrbWindowEnd,

            // FalseBreakout
            FBOrbBreakoutActive       = inputs.FBOrbBreakoutActive,
            FBSessionBreakoutActive   = inputs.FBSessionBreakoutActive,
            FBOrbBarsInBreakout       = inputs.FBOrbBarsInBreakout,
            FBSessionBarsInBreakout   = inputs.FBSessionBarsInBreakout,
            FBOrbPenetrationDepth     = inputs.FBOrbPenetrationDepth,
            FBSessionPenetrationDepth = inputs.FBSessionPenetrationDepth,
            FBOrbActivated            = inputs.FBOrbActivated,
            FBSessionActivated        = inputs.FBSessionActivated,
            IsCompoundFakeout         = inputs.IsCompoundFakeout,

            // Module outputs
            CurrentSession   = inputs.CurrentSession,
            SessionHigh      = inputs.SessionHigh,
            SessionLow       = inputs.SessionLow,
            PrevDayHigh      = inputs.PrevDayHigh,
            PrevDayLow       = inputs.PrevDayLow,
            AsiaCompressed   = inputs.AsiaCompressed,
            LastSweep        = inputs.LastSweep,
            VwapUpper1       = inputs.VwapUpper1,
            VwapUpper2       = inputs.VwapUpper2,
            VwapLower1       = inputs.VwapLower1,
            VwapLower2       = inputs.VwapLower2,
            VwapState        = inputs.VwapState,
            OpeningDriveBull = inputs.OpeningDriveBull,
            OpeningDriveBear = inputs.OpeningDriveBear,
            TrendScoreBull   = inputs.TrendScoreBull,
            TrendScoreBear   = inputs.TrendScoreBear,

            // Signal Strength
            DriveScore     = inputs.DriveScore,
            SweepScore     = inputs.SweepScore,
            VwapDevScore   = inputs.VwapDevScore,
            SignalStrength = inputs.SignalStrength,

            RecentAlerts = inputs.RecentAlerts,
            GroupSnapshots = inputs.GroupSnapshots,

            // Default enabled to false; strategies will override
            SetupAEnabled = false,
            SetupBEnabled = false,
            SetupCEnabled = false,
            SetupDEnabled = false,
        };

        // Map each strategy to per-setup fields by SetupId
        foreach (var strategy in inputs.Strategies)
        {
            // Per-setup last price from the strategy's own ticker
            var setupLastPrice = inputs.Prices != null && !string.IsNullOrEmpty(strategy.Ticker)
                ? inputs.Prices.GetLastPrice(strategy.Ticker)
                : inputs.LastPrice;
            if (setupLastPrice <= 0) setupLastPrice = inputs.LastPrice;

            var ss = strategy.GetSnapshot();
            var trade = strategy.GetActiveTrade(setupLastPrice);

            // Resolve per-setup ORB for both switch block and Setups[] population
            OrbState perSetupOrb = default;
            bool hasPerSetupOrb = inputs.PerSetupOrb?.TryGetValue(strategy.SetupId, out perSetupOrb) ?? false;

            switch (strategy.SetupId)
            {
                case SetupId.A:
                    snap.SetupA        = trade;
                    snap.TradeCountA   = ss.TradeCount;
                    snap.MaxTradesA    = ss.MaxTrades;
                    snap.SetupAState   = ss.State;
                    snap.SetupAEnabled = ss.Enabled;
                    snap.PastCutoffA   = ss.PastCutoff;
                    snap.StickyTgtA    = ss.StickyTgt;
                    snap.StickyStpA    = ss.StickyStp;
                    snap.ExpectancyA   = CalcExpectancy(ss.Wins, ss.Losses, ss.WinPnl, ss.LossPnl);
                    snap.TodayWinsA    = ss.Wins;
                    snap.TodayLossesA  = ss.Losses;
                    snap.TodayWinPnlA  = ss.WinPnl;
                    snap.TodayLossPnlA = ss.LossPnl;
                    snap.LastPriceA    = setupLastPrice;
                    snap.TickerA       = strategy.Ticker?.TrimStart('/') ?? "";
                    snap.PointValueA   = strategy.PointValue;
                    if (inputs.PerSetupOrb.TryGetValue(SetupId.A, out var orbA))
                    {
                        snap.OrbHighA = orbA.High; snap.OrbLowA = orbA.Low; snap.OrbMidA = orbA.Mid;
                        snap.OrbRangeA = orbA.Range; snap.OrbBullCloseA = orbA.BullClose;
                        snap.OrbBearCloseA = orbA.BearClose; snap.OrbAtrRatioA = orbA.AtrRatio;
                        snap.OrbFormedA = orbA.IsSet;
                    }
                    break;

                case SetupId.B:
                    snap.SetupB        = trade;
                    snap.TradeCountB   = ss.TradeCount;
                    snap.MaxTradesB    = ss.MaxTrades;
                    snap.SetupBState   = ss.State;
                    snap.SetupBEnabled = ss.Enabled;
                    snap.PastCutoffB   = ss.PastCutoff;
                    snap.StickyTgtB    = ss.StickyTgt;
                    snap.StickyStpB    = ss.StickyStp;
                    snap.ExpectancyB   = CalcExpectancy(ss.Wins, ss.Losses, ss.WinPnl, ss.LossPnl);
                    snap.TodayWinsB    = ss.Wins;
                    snap.TodayLossesB  = ss.Losses;
                    snap.TodayWinPnlB  = ss.WinPnl;
                    snap.TodayLossPnlB = ss.LossPnl;
                    snap.LastPriceB    = setupLastPrice;
                    snap.TickerB       = strategy.Ticker?.TrimStart('/') ?? "";
                    snap.PointValueB   = strategy.PointValue;
                    if (inputs.PerSetupOrb.TryGetValue(SetupId.B, out var orbB))
                    {
                        snap.OrbHighB = orbB.High; snap.OrbLowB = orbB.Low; snap.OrbMidB = orbB.Mid;
                        snap.OrbRangeB = orbB.Range; snap.OrbBullCloseB = orbB.BullClose;
                        snap.OrbBearCloseB = orbB.BearClose; snap.OrbAtrRatioB = orbB.AtrRatio;
                        snap.OrbFormedB = orbB.IsSet;
                    }
                    break;

                case SetupId.C:
                    snap.SetupC        = trade;
                    snap.TradeCountC   = ss.TradeCount;
                    snap.MaxTradesC    = ss.MaxTrades;
                    snap.SetupCState   = ss.State;
                    snap.SetupCEnabled = ss.Enabled;
                    snap.PastCutoffC   = ss.PastCutoff;
                    snap.StickyTgtC    = ss.StickyTgt;
                    snap.StickyStpC    = ss.StickyStp;
                    snap.ExpectancyC   = CalcExpectancy(ss.Wins, ss.Losses, ss.WinPnl, ss.LossPnl);
                    snap.TodayWinsC    = ss.Wins;
                    snap.TodayLossesC  = ss.Losses;
                    snap.TodayWinPnlC  = ss.WinPnl;
                    snap.TodayLossPnlC = ss.LossPnl;
                    snap.LastPriceC    = setupLastPrice;
                    snap.TickerC       = strategy.Ticker?.TrimStart('/') ?? "";
                    snap.PointValueC   = strategy.PointValue;
                    if (inputs.PerSetupOrb.TryGetValue(SetupId.C, out var orbC))
                    {
                        snap.OrbHighC = orbC.High; snap.OrbLowC = orbC.Low; snap.OrbMidC = orbC.Mid;
                        snap.OrbRangeC = orbC.Range; snap.OrbBullCloseC = orbC.BullClose;
                        snap.OrbBearCloseC = orbC.BearClose; snap.OrbAtrRatioC = orbC.AtrRatio;
                        snap.OrbFormedC = orbC.IsSet;
                    }
                    break;

                case SetupId.D:
                    snap.SetupD        = trade;
                    snap.TradeCountD   = ss.TradeCount;
                    snap.MaxTradesD    = ss.MaxTrades;
                    snap.SetupDState   = ss.State;
                    snap.SetupDEnabled = ss.Enabled;
                    snap.PastCutoffD   = ss.PastCutoff;
                    snap.StickyTgtD    = ss.StickyTgt;
                    snap.StickyStpD    = ss.StickyStp;
                    snap.ExpectancyD   = CalcExpectancy(ss.Wins, ss.Losses, ss.WinPnl, ss.LossPnl);
                    snap.TodayWinsD    = ss.Wins;
                    snap.TodayLossesD  = ss.Losses;
                    snap.TodayWinPnlD  = ss.WinPnl;
                    snap.TodayLossPnlD = ss.LossPnl;
                    snap.LastPriceD    = setupLastPrice;
                    snap.TickerD       = strategy.Ticker?.TrimStart('/') ?? "";
                    snap.PointValueD   = strategy.PointValue;
                    if (inputs.PerSetupOrb.TryGetValue(SetupId.D, out var orbD))
                    {
                        snap.OrbHighD = orbD.High; snap.OrbLowD = orbD.Low; snap.OrbMidD = orbD.Mid;
                        snap.OrbRangeD = orbD.Range; snap.OrbBullCloseD = orbD.BullClose;
                        snap.OrbBearCloseD = orbD.BearClose; snap.OrbAtrRatioD = orbD.AtrRatio;
                        snap.OrbFormedD = orbD.IsSet;
                    }
                    break;
            }

            // Populate dynamic Setups[] array (coexists with flat fields during transition)
            snap.Setups.Add(new SetupSnapshot
            {
                Id           = strategy.SetupId.ToString(),
                Label        = $"{strategy.SetupId} — {strategy.Name}",
                StrategyType = strategy.StrategyType.ToString(),
                Ticker       = strategy.Ticker?.TrimStart('/') ?? "",
                PointValue   = strategy.PointValue,
                LastPrice    = setupLastPrice,
                Enabled      = ss.Enabled,
                State        = ss.State,
                PastCutoff   = ss.PastCutoff,
                Trade        = trade,
                TradeCount   = ss.TradeCount,
                MaxTrades    = ss.MaxTrades,
                StickyTgt    = ss.StickyTgt,
                StickyStp    = ss.StickyStp,
                Wins         = ss.Wins,
                Losses       = ss.Losses,
                WinPnl       = ss.WinPnl,
                LossPnl      = ss.LossPnl,
                Expectancy   = CalcExpectancy(ss.Wins, ss.Losses, ss.WinPnl, ss.LossPnl),
                OrbHigh      = hasPerSetupOrb ? perSetupOrb.High : 0,
                OrbLow       = hasPerSetupOrb ? perSetupOrb.Low : 0,
                OrbMid       = hasPerSetupOrb ? perSetupOrb.Mid : 0,
                OrbRange     = hasPerSetupOrb ? perSetupOrb.Range : 0,
                OrbBullClose = hasPerSetupOrb && perSetupOrb.BullClose,
                OrbBearClose = hasPerSetupOrb && perSetupOrb.BearClose,
                OrbAtrRatio  = hasPerSetupOrb ? perSetupOrb.AtrRatio : 0,
                OrbFormed    = hasPerSetupOrb && perSetupOrb.IsSet,
            });
        }

        // Overall expectancy from risk manager totals
        snap.Expectancy = CalcExpectancy(
            inputs.Risk.TodayWins, inputs.Risk.TodayLosses,
            inputs.Risk.TodayWinPnl, inputs.Risk.TodayLossPnl);

        return snap;
    }

    /// <summary>
    /// Expectancy = winRate * avgWin - lossRate * avgLoss.
    /// Matches OrbStrategyEngine.CalcExpectancy exactly.
    /// </summary>
    internal static decimal CalcExpectancy(int wins, int losses, decimal winPnl, decimal lossPnl)
    {
        int total = wins + losses;
        if (total == 0) return 0;
        decimal winRate  = (decimal)wins   / total;
        decimal lossRate = (decimal)losses / total;
        decimal avgWin   = wins   > 0 ? winPnl  / wins   : 0;
        decimal avgLoss  = losses > 0 ? lossPnl / losses : 0;
        return winRate * avgWin - lossRate * avgLoss;
    }
}
