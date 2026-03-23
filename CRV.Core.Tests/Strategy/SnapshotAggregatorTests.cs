using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class SnapshotAggregatorTests
{
    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>Minimal ISetupStrategy stub for testing.</summary>
    private class StubStrategy : ISetupStrategy
    {
        public SetupId SetupId { get; init; }
        public StrategyType StrategyType { get; init; } = StrategyType.Pullback;
        public string Name { get; init; } = "Stub";
        public string Ticker { get; init; } = "/NQH2026";
        public decimal PointValue { get; init; } = 20m;
        public bool IsActive { get; init; }
        public bool IsArmed { get; init; }
        public int CutoffHour { get; set; } = 23;
        public int CutoffMinute { get; set; } = 59;

        private SetupStateSnapshot _snapshot = new();
        private ActiveTradeView? _activeTrade;

        public void SetSnapshot(SetupStateSnapshot ss) => _snapshot = ss;
        public void SetActiveTrade(ActiveTradeView? t) => _activeTrade = t;

        public SetupStateSnapshot GetSnapshot() => _snapshot;
        public ActiveTradeView? GetActiveTrade(decimal lastPrice) => _activeTrade;

        // Unused interface members
        public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules) { }
        public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules) { }
        public void Reconfigure(StrategySetupConfig config) { }
        public void Reset() { }
        public void ResetSession() { }
        public void ResetTradeCounters() { }
        public void Disarm() { }
        public EntrySignal? PendingEntry => null;
        public ExitSignal? PendingExit => null;
        public PartialSignal? PendingPartial => null;
        public BESignal? PendingBE => null;
        public ActiveTradeView? PreExitTrade => null;
        public void ApplyFill(decimal actualFillPrice) { }
        public void ClearPendingSignals() { }
        public void RevertEntry() { }
        public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd) { }
    }

    private static StubStrategy MakeStub(SetupId id, SetupStateSnapshot? ss = null, ActiveTradeView? trade = null)
    {
        var stub = new StubStrategy { SetupId = id };
        if (ss != null) stub.SetSnapshot(ss);
        if (trade != null) stub.SetActiveTrade(trade);
        return stub;
    }

    private static SnapshotAggregator.Inputs DefaultInputs(params ISetupStrategy[] strategies) => new()
    {
        Strategies = strategies,
        Risk = new RiskManager(),
        Orb = default,
        Indicators = default,
        BarTime = new DateTime(2026, 3, 20, 14, 30, 0),
        Ticker = "MESM6",
        IsLive = true,
        LastPrice = 5000m,
    };

    // ── Setup mapping tests ─────────────────────────────────────

    [Fact]
    public void SetupA_SnapshotMapsToCorrectFields()
    {
        var ss = new SetupStateSnapshot
        {
            SetupId = SetupId.A, State = 1, TradeCount = 2, MaxTrades = 3,
            Enabled = true, PastCutoff = true, StickyTgt = true, StickyStp = false,
            Wins = 3, Losses = 1, WinPnl = 300m, LossPnl = -50m
        };
        var trade = new ActiveTradeView
        {
            Setup = SetupId.A, Direction = Direction.Long,
            Entry = 4990m, Target = 5010m, CurrentStop = 4980m,
            Contracts = 2, RemainingContracts = 1, LastPrice = 5000m,
            UnrealizedPnl = 200m
        };

        var inputs = DefaultInputs(MakeStub(SetupId.A, ss, trade));
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Equal(trade, snap.SetupA);
        Assert.Equal(2, snap.TradeCountA);
        Assert.Equal(3, snap.MaxTradesA);
        Assert.Equal(1, snap.SetupAState);
        Assert.True(snap.SetupAEnabled);
        Assert.True(snap.PastCutoffA);
        Assert.True(snap.StickyTgtA);
        Assert.False(snap.StickyStpA);
        Assert.Equal(3, snap.TodayWinsA);
        Assert.Equal(1, snap.TodayLossesA);
        Assert.Equal(300m, snap.TodayWinPnlA);
        Assert.Equal(-50m, snap.TodayLossPnlA);
        // ExpectancyA: winRate=0.75, lossRate=0.25, avgWin=100, avgLoss=-50
        // 0.75*100 - 0.25*(-50) = 75 + 12.5 = 87.5
        Assert.Equal(87.5m, snap.ExpectancyA);
    }

    [Fact]
    public void SetupB_SnapshotMapsToCorrectFields()
    {
        var ss = new SetupStateSnapshot
        {
            SetupId = SetupId.B, State = -2, TradeCount = 1, MaxTrades = 2,
            Enabled = true, PastCutoff = false, StickyTgt = false, StickyStp = true,
            Wins = 1, Losses = 0, WinPnl = 100m, LossPnl = 0m
        };

        var inputs = DefaultInputs(MakeStub(SetupId.B, ss));
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Null(snap.SetupB);   // no active trade
        Assert.Equal(1, snap.TradeCountB);
        Assert.Equal(2, snap.MaxTradesB);
        Assert.Equal(-2, snap.SetupBState);
        Assert.True(snap.SetupBEnabled);
        Assert.False(snap.PastCutoffB);
        Assert.False(snap.StickyTgtB);
        Assert.True(snap.StickyStpB);
        Assert.Equal(100m, snap.ExpectancyB);  // 1 win, 0 losses → 100
    }

    [Fact]
    public void SetupC_SnapshotMapsToCorrectFields()
    {
        var ss = new SetupStateSnapshot
        {
            SetupId = SetupId.C, State = 3, TradeCount = 0, MaxTrades = 1,
            Enabled = false, Wins = 0, Losses = 0
        };

        var inputs = DefaultInputs(MakeStub(SetupId.C, ss));
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Null(snap.SetupC);
        Assert.Equal(0, snap.TradeCountC);
        Assert.Equal(1, snap.MaxTradesC);
        Assert.Equal(3, snap.SetupCState);
        Assert.False(snap.SetupCEnabled);
        Assert.Equal(0m, snap.ExpectancyC);
    }

    [Fact]
    public void SetupD_SnapshotMapsToCorrectFields()
    {
        var ss = new SetupStateSnapshot
        {
            SetupId = SetupId.D, State = -1, TradeCount = 5, MaxTrades = 5,
            Enabled = true, StickyTgt = true, StickyStp = false,
            Wins = 2, Losses = 2, WinPnl = 200m, LossPnl = -200m
        };

        var inputs = DefaultInputs(MakeStub(SetupId.D, ss));
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Equal(5, snap.TradeCountD);
        Assert.Equal(5, snap.MaxTradesD);
        Assert.Equal(-1, snap.SetupDState);
        Assert.True(snap.SetupDEnabled);
        Assert.True(snap.StickyTgtD);
        // Expectancy: winRate=0.5, avgWin=100, lossRate=0.5, avgLoss=-100
        // 0.5*100 - 0.5*(-100) = 50 + 50 = 100
        Assert.Equal(100m, snap.ExpectancyD);
    }

    // ── Risk fields ─────────────────────────────────────────────

    [Fact]
    public void RiskFieldsMapCorrectly()
    {
        var risk = new RiskManager();
        risk.RecordTrade(150m);   // win
        risk.RecordTrade(-50m);   // loss
        risk.RecordTrade(200m);   // win

        var inputs = DefaultInputs();
        inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = risk,
            BarTime = inputs.BarTime,
            Ticker = inputs.Ticker,
            LastPrice = inputs.LastPrice,
            DailyLossLimit = 500m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Equal(300m, snap.TodayPnl);       // 150 - 50 + 200
        Assert.Equal(3, snap.TodayTrades);
        Assert.Equal(2, snap.TodayWins);
        Assert.Equal(1, snap.TodayLosses);
        Assert.Equal(500m, snap.DailyLossLimit);
        Assert.Equal(0m, snap.DailyLossUsed);     // PnL is positive → 0
        Assert.False(snap.TradingHalted);
    }

    [Fact]
    public void DailyLossUsed_WhenPnlNegative()
    {
        var risk = new RiskManager();
        risk.RecordTrade(-200m);

        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = risk,
            DailyLossLimit = 500m,
            LastPrice = 5000m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Equal(-200m, snap.TodayPnl);
        Assert.Equal(200m, snap.DailyLossUsed);
    }

    [Fact]
    public void TradingHalted_WhenDdBreached()
    {
        var risk = new RiskManager();
        risk.RecordTrade(-500m);
        risk.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m);

        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = risk,
            LastPrice = 5000m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.True(snap.TradingHalted);
    }

    // ── ActiveTradeView from strategy ───────────────────────────

    [Fact]
    public void ActiveTrade_MapsToSetupField()
    {
        var tradeA = new ActiveTradeView
        {
            Setup = SetupId.A, Direction = Direction.Long,
            Entry = 5000m, Target = 5020m, CurrentStop = 4990m,
            Contracts = 3, RemainingContracts = 2, PartialFilled = true,
            LastPrice = 5010m, UnrealizedPnl = 400m,
            EnteredAt = new DateTime(2026, 3, 20, 14, 0, 0)
        };
        var tradeC = new ActiveTradeView
        {
            Setup = SetupId.C, Direction = Direction.Short,
            Entry = 5010m, Target = 4990m, CurrentStop = 5020m,
            Contracts = 1, RemainingContracts = 1
        };

        var stubA = MakeStub(SetupId.A, new SetupStateSnapshot { SetupId = SetupId.A }, tradeA);
        var stubC = MakeStub(SetupId.C, new SetupStateSnapshot { SetupId = SetupId.C }, tradeC);

        var snap = SnapshotAggregator.Build(DefaultInputs(stubA, stubC));

        Assert.NotNull(snap.SetupA);
        Assert.Equal(Direction.Long, snap.SetupA!.Direction);
        Assert.Equal(5000m, snap.SetupA.Entry);

        Assert.Null(snap.SetupB);  // not provided

        Assert.NotNull(snap.SetupC);
        Assert.Equal(Direction.Short, snap.SetupC!.Direction);

        Assert.Null(snap.SetupD);  // not provided
    }

    // ── Missing setups produce defaults ─────────────────────────

    [Fact]
    public void MissingSetups_ProduceDefaultZeroValues()
    {
        // Only setup A provided; B, C, D should all be defaults
        var ss = new SetupStateSnapshot
        {
            SetupId = SetupId.A, TradeCount = 1, MaxTrades = 2,
            Enabled = true, Wins = 1, WinPnl = 50m
        };
        var snap = SnapshotAggregator.Build(DefaultInputs(MakeStub(SetupId.A, ss)));

        // B
        Assert.Null(snap.SetupB);
        Assert.Equal(0, snap.TradeCountB);
        Assert.Equal(0, snap.MaxTradesB);
        Assert.Equal(0, snap.SetupBState);
        Assert.False(snap.SetupBEnabled);
        Assert.Equal(0m, snap.ExpectancyB);

        // C
        Assert.Null(snap.SetupC);
        Assert.Equal(0, snap.TradeCountC);
        Assert.Equal(0m, snap.ExpectancyC);

        // D
        Assert.Null(snap.SetupD);
        Assert.Equal(0, snap.TradeCountD);
        Assert.Equal(0m, snap.ExpectancyD);
    }

    // ── OrbState + Indicator mapping ────────────────────────────

    [Fact]
    public void OrbAndIndicatorFields_MapCorrectly()
    {
        var orb = new OrbState(
            High: 5020m, Low: 4980m, Mid: 5000m, Range: 40m,
            IsSet: true, BullClose: true, BearClose: false, AtrRatio: 0.8m);
        var ind = new IndicatorState(
            Atr: 50m, Vwap: 5005m,
            VwapUpper1: 5050m, VwapLower1: 4960m,
            VwapUpper2: 5100m, VwapLower2: 4910m,
            LastClose: 5010m);

        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = new RiskManager(),
            Orb = orb,
            Indicators = ind,
            OrbAtrRatio = 0.8m,
            LastPrice = 5010m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Equal(5020m, snap.OrbHigh);
        Assert.Equal(4980m, snap.OrbLow);
        Assert.Equal(5000m, snap.OrbMid);
        Assert.Equal(40m, snap.OrbRange);
        Assert.True(snap.OrbBullClose);
        Assert.False(snap.OrbBearClose);
        Assert.Equal(0.8m, snap.OrbAtrRatio);
        Assert.True(snap.OrbFormed);

        Assert.Equal(50m, snap.Atr);
        Assert.Equal(5005m, snap.Vwap);
    }

    // ── Session / module state ──────────────────────────────────

    [Fact]
    public void SessionAndModuleFields_MapCorrectly()
    {
        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = new RiskManager(),
            PastCutoff = true,
            SessionEnded = false,
            ActiveSessionId = "NY",
            OrbWindowStart = "09:30",
            OrbWindowEnd = "10:00",
            CurrentSession = "NY",
            SessionHigh = 5050m,
            SessionLow = 4950m,
            PrevDayHigh = 5100m,
            PrevDayLow = 4900m,
            AsiaCompressed = true,
            LastSweep = "PDH Bull",
            VwapUpper1 = 5060m,
            VwapState = 1,
            OpeningDriveBull = true,
            TrendScoreBull = 3,
            TrendScoreBear = 1,
            LastPrice = 5000m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.True(snap.PastCutoff);
        Assert.False(snap.SessionEnded);
        Assert.Equal("NY", snap.ActiveSessionId);
        Assert.Equal("09:30", snap.OrbWindowStart);
        Assert.Equal("10:00", snap.OrbWindowEnd);
        Assert.Equal("NY", snap.CurrentSession);
        Assert.Equal(5050m, snap.SessionHigh);
        Assert.Equal(4950m, snap.SessionLow);
        Assert.Equal(5100m, snap.PrevDayHigh);
        Assert.Equal(4900m, snap.PrevDayLow);
        Assert.True(snap.AsiaCompressed);
        Assert.Equal("PDH Bull", snap.LastSweep);
        Assert.Equal(5060m, snap.VwapUpper1);
        Assert.Equal(1, snap.VwapState);
        Assert.True(snap.OpeningDriveBull);
        Assert.Equal(3, snap.TrendScoreBull);
        Assert.Equal(1, snap.TrendScoreBear);
    }

    // ── FalseBreakout fields ────────────────────────────────────

    [Fact]
    public void FalseBreakoutFields_MapCorrectly()
    {
        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = new RiskManager(),
            FBOrbBreakoutActive = true,
            FBSessionBreakoutActive = false,
            FBOrbBarsInBreakout = 3,
            FBOrbPenetrationDepth = 0.15m,
            FBOrbActivated = true,
            IsCompoundFakeout = false,
            LastPrice = 5000m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.True(snap.FBOrbBreakoutActive);
        Assert.False(snap.FBSessionBreakoutActive);
        Assert.Equal(3, snap.FBOrbBarsInBreakout);
        Assert.Equal(0.15m, snap.FBOrbPenetrationDepth);
        Assert.True(snap.FBOrbActivated);
        Assert.False(snap.IsCompoundFakeout);
    }

    // ── Signal strength scores ──────────────────────────────────

    [Fact]
    public void SignalStrengthScores_MapCorrectly()
    {
        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = new RiskManager(),
            DriveScore = 3.5m,
            SweepScore = 2.5m,
            VwapDevScore = 5.0m,
            SignalStrength = 3.7m,
            LastPrice = 5000m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Equal(3.5m, snap.DriveScore);
        Assert.Equal(2.5m, snap.SweepScore);
        Assert.Equal(5.0m, snap.VwapDevScore);
        Assert.Equal(3.7m, snap.SignalStrength);
    }

    // ── Overall expectancy ──────────────────────────────────────

    [Fact]
    public void OverallExpectancy_UsesRiskManagerTotals()
    {
        var risk = new RiskManager();
        risk.RecordTrade(100m);  // win
        risk.RecordTrade(-60m);  // loss

        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = risk,
            LastPrice = 5000m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        // winRate=0.5, avgWin=100, lossRate=0.5, avgLoss=-60
        // 0.5*100 - 0.5*(-60) = 50 + 30 = 80
        Assert.Equal(80m, snap.Expectancy);
    }

    // ── CalcExpectancy edge cases ───────────────────────────────

    [Fact]
    public void CalcExpectancy_ZeroTrades_ReturnsZero()
    {
        Assert.Equal(0m, SnapshotAggregator.CalcExpectancy(0, 0, 0m, 0m));
    }

    [Fact]
    public void CalcExpectancy_OnlyWins()
    {
        // 2 wins totaling $300 → avgWin=150, winRate=1.0 → 150
        Assert.Equal(150m, SnapshotAggregator.CalcExpectancy(2, 0, 300m, 0m));
    }

    [Fact]
    public void CalcExpectancy_OnlyLosses()
    {
        // 2 losses totaling -$200 → avgLoss=-100, lossRate=1.0 → 0 - 1*(-100) = 100
        Assert.Equal(100m, SnapshotAggregator.CalcExpectancy(0, 2, 0m, -200m));
    }

    // ── All four setups together ────────────────────────────────

    [Fact]
    public void AllFourSetups_MappedCorrectly()
    {
        var strategies = new ISetupStrategy[]
        {
            MakeStub(SetupId.A, new SetupStateSnapshot { SetupId = SetupId.A, TradeCount = 1, Enabled = true }),
            MakeStub(SetupId.B, new SetupStateSnapshot { SetupId = SetupId.B, TradeCount = 2, Enabled = true }),
            MakeStub(SetupId.C, new SetupStateSnapshot { SetupId = SetupId.C, TradeCount = 3, Enabled = false }),
            MakeStub(SetupId.D, new SetupStateSnapshot { SetupId = SetupId.D, TradeCount = 4, Enabled = true }),
        };

        var snap = SnapshotAggregator.Build(DefaultInputs(strategies));

        Assert.Equal(1, snap.TradeCountA);
        Assert.Equal(2, snap.TradeCountB);
        Assert.Equal(3, snap.TradeCountC);
        Assert.Equal(4, snap.TradeCountD);

        Assert.True(snap.SetupAEnabled);
        Assert.True(snap.SetupBEnabled);
        Assert.False(snap.SetupCEnabled);
        Assert.True(snap.SetupDEnabled);
    }

    // ── RecentAlerts passthrough ────────────────────────────────

    [Fact]
    public void RecentAlerts_PassedThrough()
    {
        var alerts = new List<AlertEvent>
        {
            new() { Time = DateTime.UtcNow, Type = "Entry", Setup = SetupId.A, Message = "Long entry" }
        };

        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = new RiskManager(),
            RecentAlerts = alerts,
            LastPrice = 5000m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Single(snap.RecentAlerts);
        Assert.Equal("Entry", snap.RecentAlerts[0].Type);
    }

    // ── Ticker / Time / IsLive passthrough ──────────────────────

    [Fact]
    public void BasicFields_MapCorrectly()
    {
        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = Array.Empty<ISetupStrategy>(),
            Risk = new RiskManager(),
            BarTime = new DateTime(2026, 3, 20, 15, 0, 0),
            Ticker = "NQM6",
            IsLive = false,
            LastPrice = 18000m,
        };
        var snap = SnapshotAggregator.Build(inputs);

        Assert.Equal(new DateTime(2026, 3, 20, 15, 0, 0), snap.Time);
        Assert.Equal("NQM6", snap.Ticker);
        Assert.False(snap.IsLive);
        Assert.Equal(18000m, snap.LastPrice);
    }
}
