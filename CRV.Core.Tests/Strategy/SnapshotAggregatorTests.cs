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
        public string Id { get; init; } = "";
        public SetupId SetupId { get; init; }
        public StrategyType StrategyType { get; init; } = StrategyType.Pullback;
        public string Name { get; init; } = "Stub";
        public string Ticker { get; init; } = "/NQH2026";
        public decimal PointValue { get; init; } = 20m;
        public bool IsActive { get; init; }
        public bool IsArmed { get; init; }
        public bool InTrade { get; set; }
        public void SetInTrade(bool active) => InTrade = active;
        public int CutoffHour { get; set; } = 23;
        public int CutoffMinute { get; set; } = 59;

        private SetupStateSnapshot _snapshot = new();

        public void SetSnapshot(SetupStateSnapshot ss) => _snapshot = ss;

        public SetupStateSnapshot GetSnapshot() => _snapshot;

        // Unused interface members
        public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules) { }
        public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules) { }
        public void Reconfigure(StrategySetupConfig config) { }
        public void Reset() { }
        public void ResetSession() { }
        public void ResetTradeCounters() { }
        public void Disarm() { }
        public void ResetCutoff() { }
        public (int Hour, int Minute) GetCutoffForSession(string s) => (CutoffHour, CutoffMinute);
        public bool IsEnabledForSession(string s) => true;
        public EntrySignal? PendingEntry => null;
        public void ClearPendingSignals() { }
        public void RevertEntry() { }
        public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd) { }
    }

    private static StubStrategy MakeStub(SetupId id, SetupStateSnapshot? ss = null)
    {
        var stub = new StubStrategy { Id = id.ToString(), SetupId = id };
        if (ss != null) stub.SetSnapshot(ss);
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

    /// <summary>Helper to find a setup snapshot by Id letter.</summary>
    private static SetupSnapshot FindSetup(EngineSnapshot snap, string id) =>
        snap.Setups.First(s => s.Id == id);

    private static SetupSnapshot? FindSetupOrNull(EngineSnapshot snap, string id) =>
        snap.Setups.FirstOrDefault(s => s.Id == id);

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

        var inputs = DefaultInputs(MakeStub(SetupId.A, ss));
        var snap = SnapshotAggregator.Build(inputs);

        var a = FindSetup(snap, "A");
        Assert.Null(a.Trade); // Trade is now populated from BrokerEventHandler, not strategy
        Assert.Equal(2, a.TradeCount);
        Assert.Equal(3, a.MaxTrades);
        Assert.Equal(1, a.State);
        Assert.True(a.Enabled);
        Assert.True(a.PastCutoff);
        Assert.True(a.StickyTgt);
        Assert.False(a.StickyStp);
        Assert.Equal(3, a.Wins);
        Assert.Equal(1, a.Losses);
        Assert.Equal(300m, a.WinPnl);
        Assert.Equal(-50m, a.LossPnl);
        // ExpectancyA: winRate=0.75, lossRate=0.25, avgWin=100, avgLoss=-50
        // 0.75*100 + 0.25*(-50) = 75 - 12.5 = 62.5
        Assert.Equal(62.5m, a.Expectancy);
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

        var b = FindSetup(snap, "B");
        Assert.Null(b.Trade);   // no active trade
        Assert.Equal(1, b.TradeCount);
        Assert.Equal(2, b.MaxTrades);
        Assert.Equal(-2, b.State);
        Assert.True(b.Enabled);
        Assert.False(b.PastCutoff);
        Assert.False(b.StickyTgt);
        Assert.True(b.StickyStp);
        Assert.Equal(100m, b.Expectancy);  // 1 win, 0 losses -> 100
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

        var c = FindSetup(snap, "C");
        Assert.Null(c.Trade);
        Assert.Equal(0, c.TradeCount);
        Assert.Equal(1, c.MaxTrades);
        Assert.Equal(3, c.State);
        Assert.False(c.Enabled);
        Assert.Equal(0m, c.Expectancy);
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

        var d = FindSetup(snap, "D");
        Assert.Equal(5, d.TradeCount);
        Assert.Equal(5, d.MaxTrades);
        Assert.Equal(-1, d.State);
        Assert.True(d.Enabled);
        Assert.True(d.StickyTgt);
        // Expectancy: winRate=0.5, avgWin=100, lossRate=0.5, avgLoss=-100
        // 0.5*100 + 0.5*(-100) = 50 - 50 = 0
        Assert.Equal(0m, d.Expectancy);
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
        Assert.Equal(0m, snap.DailyLossUsed);     // PnL is positive -> 0
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

    // ── ActiveTradeView from BrokerEventHandler ─────────────────
    // NOTE: ActiveTradeView is now populated from BrokerEventHandler, not from strategy.
    // Without a broker handler, Trade is always null. Broker-level trade tests are in
    // BrokerEventHandlerTests.

    // ── Missing setups produce defaults ─────────────────────────

    [Fact]
    public void MissingSetups_ProduceDefaultZeroValues()
    {
        // Only setup A provided; B, C, D should not be in Setups[]
        var ss = new SetupStateSnapshot
        {
            SetupId = SetupId.A, TradeCount = 1, MaxTrades = 2,
            Enabled = true, Wins = 1, WinPnl = 50m
        };
        var snap = SnapshotAggregator.Build(DefaultInputs(MakeStub(SetupId.A, ss)));

        // Only A should be in Setups[]
        Assert.Single(snap.Setups);
        Assert.Equal("A", snap.Setups[0].Id);

        // B, C, D not present
        Assert.Null(FindSetupOrNull(snap, "B"));
        Assert.Null(FindSetupOrNull(snap, "C"));
        Assert.Null(FindSetupOrNull(snap, "D"));
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
        // 0.5*100 + 0.5*(-60) = 50 - 30 = 20
        Assert.Equal(20m, snap.Expectancy);
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
        // 2 wins totaling $300 -> avgWin=150, winRate=1.0 -> 150
        Assert.Equal(150m, SnapshotAggregator.CalcExpectancy(2, 0, 300m, 0m));
    }

    [Fact]
    public void CalcExpectancy_OnlyLosses()
    {
        // 2 losses totaling -$200 -> avgLoss=-100, lossRate=1.0 -> 0 + 1*(-100) = -100
        Assert.Equal(-100m, SnapshotAggregator.CalcExpectancy(0, 2, 0m, -200m));
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

        Assert.Equal(1, FindSetup(snap, "A").TradeCount);
        Assert.Equal(2, FindSetup(snap, "B").TradeCount);
        Assert.Equal(3, FindSetup(snap, "C").TradeCount);
        Assert.Equal(4, FindSetup(snap, "D").TradeCount);

        Assert.True(FindSetup(snap, "A").Enabled);
        Assert.True(FindSetup(snap, "B").Enabled);
        Assert.False(FindSetup(snap, "C").Enabled);
        Assert.True(FindSetup(snap, "D").Enabled);
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

    // ── Setups[] array population ────────────────────────────────

    [Fact]
    public void Build_PopulatesSetupsList()
    {
        var ssA = new SetupStateSnapshot
        {
            SetupId = SetupId.A, State = 1, TradeCount = 2, MaxTrades = 5,
            Enabled = true, Wins = 1, Losses = 0, WinPnl = 50m, LossPnl = 0m
        };
        var ssB = new SetupStateSnapshot
        {
            SetupId = SetupId.B, State = -1, TradeCount = 1, MaxTrades = 3,
            Enabled = true, PastCutoff = true
        };
        var inputs = DefaultInputs(
            MakeStub(SetupId.A, ssA),
            MakeStub(SetupId.B, ssB)
        );
        var snap = SnapshotAggregator.Build(inputs);

        Assert.NotNull(snap.Setups);
        Assert.Equal(2, snap.Setups.Count);

        var a = snap.Setups[0];
        Assert.Equal("A", a.Id);
        Assert.Equal(1, a.State);
        Assert.Equal(2, a.TradeCount);
        Assert.Equal(5, a.MaxTrades);
        Assert.True(a.Enabled);

        var b = snap.Setups[1];
        Assert.Equal("B", b.Id);
        Assert.Equal(-1, b.State);
        Assert.True(b.PastCutoff);
    }
}
