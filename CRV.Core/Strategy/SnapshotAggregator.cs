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
        public Dictionary<string, OrbState> PerSetupOrb { get; init; } = new();

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

        // ── BrokerEventHandler (for active trade views) ──────────
        public BrokerEventHandler? BrokerHandler { get; init; }

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
            DailyLossUsed  = inputs.Risk.DailyLossUsed,
            TradingHalted  = inputs.Risk.DdBreached,

            // Indicators
            Vwap = inputs.Indicators.Vwap,
            Atr  = inputs.Indicators.Atr,
            Ema21 = inputs.GroupSnapshots.Values.FirstOrDefault()?.Ema21 ?? 0,

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
        };

        var brokerHandler = inputs.BrokerHandler;

        // Map each strategy to per-setup fields by SetupId
        foreach (var strategy in inputs.Strategies)
        {
            // Per-setup last price from the strategy's own ticker. Do NOT fall back to
            // inputs.LastPrice on miss — a silent fallback masks ticker-routing bugs
            // (e.g. stored setup ticker stale after rollover so its own price never
            // arrives). The UI renders 0 as "—", making the missing price visible.
            var setupLastPrice = inputs.Prices != null && !string.IsNullOrEmpty(strategy.Ticker)
                ? inputs.Prices.GetLastPrice(strategy.Ticker)
                : 0m;

            var ss = strategy.GetSnapshot();

            // Build ActiveTradeView from BrokerEventHandler if available
            ActiveTradeView? trade = null;
            if (brokerHandler != null)
            {
                var group = brokerHandler.GetGroupState(strategy.Id);
                if (group == null) group = brokerHandler.GetGroupState(strategy.SetupId.ToString());
                if (group != null)
                {
                    // Use fill price if available, else entry leg limit price for pending entries
                    var entryLeg = group.GetLeg(LegType.Entry);
                    var entryPrice = group.EntryPrice ?? entryLeg?.Price ?? 0m;
                    if (entryPrice > 0)
                    {
                        var stopLeg = group.GetLeg(LegType.Stop);
                        var tg1Leg = group.GetLeg(LegType.Tg1);
                        var tg2Leg = group.GetLeg(LegType.Tg2);
                        // Calculate unrealized P&L inline (avoid re-lookup by setupId which may not match)
                        bool isPartial = group.Status == GroupOrderStatus.PartialFilled;
                        int remQty = isPartial ? group.RemainingContracts : group.TotalContracts;
                        bool isLong = group.Direction == Direction.Long;
                        // Use entryPrice (which falls back to limit price) so unrealized P&L
                        // is correct even when the WSS fill event didn't include a fill price
                        decimal unrealPnl = entryPrice > 0 && setupLastPrice > 0 && group.PointValue > 0
                                            && group.Status != GroupOrderStatus.Pending
                            ? (isLong
                                ? (setupLastPrice - entryPrice) * group.PointValue * remQty
                                : (entryPrice - setupLastPrice) * group.PointValue * remQty)
                              + group.AccruedPartialPnl
                            : 0m;

                        trade = new ActiveTradeView
                        {
                            Direction = group.Direction,
                            Contracts = group.TotalContracts,
                            PartialContracts = group.PartialContracts,
                            RemainingContracts = group.RemainingContracts,
                            Entry = entryPrice,
                            // When partial filled + BE, the broker moved stop to entry — use that
                            CurrentStop = (isPartial && group.UseBe && group.EntryPrice.HasValue)
                                ? group.EntryPrice.Value
                                : (stopLeg?.Price ?? 0m),
                            InitialStop = group.InitialStopPrice > 0 ? group.InitialStopPrice : (stopLeg?.Price ?? 0m),
                            Target = tg2Leg?.Price ?? 0m,
                            Partial = tg1Leg?.Price ?? 0m,
                            PartialFilled = isPartial,
                            PointValue = group.PointValue,
                            LastPrice = setupLastPrice,
                            UnrealizedPnl = unrealPnl,
                            EnteredAt = group.CreatedAt,
                            GroupStatus = group.Status.ToString(),
                        };
                    }
                }
            }

            // Resolve per-setup ORB for Setups[] population
            OrbState perSetupOrb = default;
            bool hasPerSetupOrb = inputs.PerSetupOrb?.TryGetValue(strategy.Id, out perSetupOrb) ?? false;

            // Populate dynamic Setups[] array
            snap.Setups.Add(new SetupSnapshot
            {
                Id           = strategy.Id,
                Label        = strategy.Name,
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
    /// Expectancy = winRate * avgWin + lossRate * avgLoss.
    /// avgLoss is already negative, so adding it subtracts the loss contribution.
    /// </summary>
    internal static decimal CalcExpectancy(int wins, int losses, decimal winPnl, decimal lossPnl)
    {
        int total = wins + losses;
        if (total == 0) return 0;
        decimal winRate  = (decimal)wins   / total;
        decimal lossRate = (decimal)losses / total;
        decimal avgWin   = wins   > 0 ? winPnl  / wins   : 0;
        decimal avgLoss  = losses > 0 ? lossPnl / losses : 0;
        return winRate * avgWin + lossRate * avgLoss;
    }
}
