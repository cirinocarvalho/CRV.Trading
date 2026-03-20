using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class RiskManagerTests
{
    // ──────────────────────────────────────────────
    // 1. RecordTrade updates PnL, win/loss counts
    // ──────────────────────────────────────────────

    [Fact]
    public void RecordTrade_WinningTrade_IncrementsWinsAndWinPnl()
    {
        var rm = new RiskManager();
        rm.RecordTrade(100m);

        Assert.Equal(100m,  rm.TodayPnl);
        Assert.Equal(1,     rm.TodayWins);
        Assert.Equal(0,     rm.TodayLosses);
        Assert.Equal(100m,  rm.TodayWinPnl);
        Assert.Equal(0m,    rm.TodayLossPnl);
    }

    [Fact]
    public void RecordTrade_LosingTrade_IncrementsLossesAndLossPnl()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-50m);

        Assert.Equal(-50m,  rm.TodayPnl);
        Assert.Equal(0,     rm.TodayWins);
        Assert.Equal(1,     rm.TodayLosses);
        Assert.Equal(0m,    rm.TodayWinPnl);
        Assert.Equal(-50m,  rm.TodayLossPnl);
    }

    [Fact]
    public void RecordTrade_ZeroPnl_CountsAsLoss()
    {
        var rm = new RiskManager();
        rm.RecordTrade(0m);

        Assert.Equal(0,  rm.TodayWins);
        Assert.Equal(1,  rm.TodayLosses);
    }

    // ──────────────────────────────────────────────
    // 2. RecordTrade tracks peak and drawdown
    // ──────────────────────────────────────────────

    [Fact]
    public void RecordTrade_TracksPeakAndDrawdown()
    {
        var rm = new RiskManager();
        rm.RecordTrade(200m);   // PnL=200, Peak=200, DD=0
        rm.RecordTrade(-80m);   // PnL=120, Peak=200, DD=80

        Assert.Equal(120m,  rm.TodayPnl);
        Assert.Equal(200m,  rm.TodayPeak);
        Assert.Equal(80m,   rm.TodayMaxDD);
    }

    [Fact]
    public void RecordTrade_PeakNeverDecreases()
    {
        var rm = new RiskManager();
        rm.RecordTrade(300m);   // Peak=300
        rm.RecordTrade(-200m);  // Peak stays 300
        rm.RecordTrade(50m);    // PnL=150, Peak still 300

        Assert.Equal(300m, rm.TodayPeak);
        Assert.Equal(200m, rm.TodayMaxDD);  // peak 300 − low 100 = 200 (recorded after second trade)
    }

    [Fact]
    public void RecordTrade_NewPeakUpdatesMaxDD()
    {
        var rm = new RiskManager();
        rm.RecordTrade(100m);   // Peak=100, DD=0
        rm.RecordTrade(-30m);   // Peak=100, DD=30
        rm.RecordTrade(150m);   // PnL=220, Peak=220, DD=30 (unchanged — new peak, no new DD)

        Assert.Equal(220m, rm.TodayPeak);
        Assert.Equal(30m,  rm.TodayMaxDD);
    }

    [Fact]
    public void RecordTrade_OnlyLosses_PeakStaysZero()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-100m);  // PnL=-100, Peak=0, DD=100

        Assert.Equal(0m,    rm.TodayPeak);
        Assert.Equal(100m,  rm.TodayMaxDD);
    }

    // ──────────────────────────────────────────────
    // 3. CanTrade returns false when loss limit breached
    // ──────────────────────────────────────────────

    [Fact]
    public void CanTrade_PnlExceedsLossLimit_ReturnsFalseAndSetsDdBreached()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-500m);  // TodayPnl = -500

        var result = rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m);

        Assert.False(result);
        Assert.True(rm.DdBreached);
    }

    [Fact]
    public void CanTrade_PnlExactlyAtNegativeLimit_ReturnsFalse()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-300m);

        // TodayPnl == -300, maxDailyLoss == 300  →  TodayPnl <= -maxDailyLoss
        var result = rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 300m);

        Assert.False(result);
        Assert.True(rm.DdBreached);
    }

    // ──────────────────────────────────────────────
    // 4. CanTrade returns true when under limit
    // ──────────────────────────────────────────────

    [Fact]
    public void CanTrade_PnlBelowLimit_ReturnsTrue()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-200m);

        var result = rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m);

        Assert.True(result);
        Assert.False(rm.DdBreached);
    }

    // ──────────────────────────────────────────────
    // 5. CanTrade with useDailyLossLimit=false always returns true (unless DdBreached)
    // ──────────────────────────────────────────────

    [Fact]
    public void CanTrade_LimitDisabled_ReturnsTrueEvenWithHeavyLoss()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-10_000m);

        var result = rm.CanTrade(useDailyLossLimit: false, maxDailyLoss: 0m);

        Assert.True(result);
        Assert.False(rm.DdBreached);
    }

    [Fact]
    public void CanTrade_LimitDisabledButDdAlreadyBreached_ReturnsFalse()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-500m);
        rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m); // breach it

        // Now disable limit — DdBreached flag should still block trading
        var result = rm.CanTrade(useDailyLossLimit: false, maxDailyLoss: 0m);

        Assert.False(result);
    }

    // ──────────────────────────────────────────────
    // 6. DdBreached stays true even if PnL recovers
    // ──────────────────────────────────────────────

    [Fact]
    public void DdBreached_StaysTrueAfterPnlRecovers()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-500m);
        rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m); // set breach

        Assert.True(rm.DdBreached);

        rm.RecordTrade(1_000m); // PnL is now positive

        // Breach flag must not reset due to PnL recovery
        Assert.True(rm.DdBreached);
        var result = rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m);
        Assert.False(result);
    }

    // ──────────────────────────────────────────────
    // 7. ResetDay clears everything
    // ──────────────────────────────────────────────

    [Fact]
    public void ResetDay_ClearsAllFields()
    {
        var rm = new RiskManager();
        rm.RecordTrade(200m);
        rm.RecordTrade(-800m);
        rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m); // breach
        rm.RecordTrade(100m);

        rm.ResetDay();

        Assert.Equal(0m,    rm.TodayPnl);
        Assert.Equal(0m,    rm.TodayPeak);
        Assert.Equal(0m,    rm.TodayMaxDD);
        Assert.False(rm.DdBreached);
        Assert.Equal(0,     rm.TodayWins);
        Assert.Equal(0,     rm.TodayLosses);
        Assert.Equal(0m,    rm.TodayWinPnl);
        Assert.Equal(0m,    rm.TodayLossPnl);
    }

    [Fact]
    public void ResetDay_AllowsTradingAgainAfterBreach()
    {
        var rm = new RiskManager();
        rm.RecordTrade(-500m);
        rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m); // breach

        rm.ResetDay();

        var result = rm.CanTrade(useDailyLossLimit: true, maxDailyLoss: 500m);
        Assert.True(result);
    }

    // ──────────────────────────────────────────────
    // 8. Multiple trades accumulate correctly
    // ──────────────────────────────────────────────

    [Fact]
    public void MultipleTrades_AccumulateCorrectly()
    {
        var rm = new RiskManager();
        rm.RecordTrade(100m);   // W
        rm.RecordTrade(-40m);   // L
        rm.RecordTrade(60m);    // W
        rm.RecordTrade(-20m);   // L
        rm.RecordTrade(80m);    // W

        Assert.Equal(180m,  rm.TodayPnl);       // 100-40+60-20+80
        Assert.Equal(3,     rm.TodayWins);
        Assert.Equal(2,     rm.TodayLosses);
        Assert.Equal(240m,  rm.TodayWinPnl);    // 100+60+80
        Assert.Equal(-60m,  rm.TodayLossPnl);   // -40-20
    }

    [Fact]
    public void MultipleTrades_DrawdownIsHighWaterMarkBased()
    {
        var rm = new RiskManager();
        rm.RecordTrade(100m);   // PnL=100, Peak=100, DD=0
        rm.RecordTrade(-70m);   // PnL=30,  Peak=100, DD=70
        rm.RecordTrade(120m);   // PnL=150, Peak=150, DD=70 (no new DD)
        rm.RecordTrade(-90m);   // PnL=60,  Peak=150, DD=90  ← new max DD

        Assert.Equal(60m,  rm.TodayPnl);
        Assert.Equal(150m, rm.TodayPeak);
        Assert.Equal(90m,  rm.TodayMaxDD);
    }
}
