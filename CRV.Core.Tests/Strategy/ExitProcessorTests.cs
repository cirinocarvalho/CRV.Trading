using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

public class ExitProcessorTests
{
    private const decimal PointValue = 20m;

    private static ExitResult Process(
        bool isLong, decimal entry, decimal stop, decimal target, decimal partial,
        int contracts, decimal currentPnl, bool partialHit,
        bool usePartial, bool useBE,
        decimal barHigh, decimal barLow)
        => ExitProcessor.ProcessBar(
            active: true, isLong, entry, stop, target, partial,
            contracts, currentPnl, partialHit,
            usePartial, useBE, PointValue, barHigh, barLow);

    // ── Stop hits ─────────────────────────────────────────────

    [Fact]
    public void Long_StopHit_WhenBarLowBelowStop()
    {
        var result = Process(
            isLong: true, entry: 1000m, stop: 990m, target: 1100m, partial: 1050m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: false, useBE: false,
            barHigh: 995m, barLow: 988m);

        Assert.True(result.HitStop);
        Assert.False(result.HitTarget);
        Assert.False(result.StillActive);
        // PnL: (990-1000)*20*2 = -400
        Assert.Equal(-400m, result.NewPnl);
    }

    [Fact]
    public void Short_StopHit_WhenBarHighAboveStop()
    {
        var result = Process(
            isLong: false, entry: 1000m, stop: 1010m, target: 900m, partial: 950m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: false, useBE: false,
            barHigh: 1012m, barLow: 995m);

        Assert.True(result.HitStop);
        Assert.False(result.HitTarget);
        // PnL: (1000-1010)*20*2 = -400
        Assert.Equal(-400m, result.NewPnl);
    }

    // ── Target hits ───────────────────────────────────────────

    [Fact]
    public void Long_TargetHit_WhenBarHighAboveTarget()
    {
        var result = Process(
            isLong: true, entry: 1000m, stop: 990m, target: 1100m, partial: 1050m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: false, useBE: false,
            barHigh: 1105m, barLow: 1000m);

        Assert.True(result.HitTarget);
        Assert.False(result.HitStop);
        // PnL: (1100-1000)*20*2 = 4000
        Assert.Equal(4000m, result.NewPnl);
    }

    [Fact]
    public void Short_TargetHit_WhenBarLowBelowTarget()
    {
        var result = Process(
            isLong: false, entry: 1000m, stop: 1010m, target: 900m, partial: 950m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: false, useBE: false,
            barHigh: 1000m, barLow: 895m);

        Assert.True(result.HitTarget);
        // PnL: (1000-900)*20*2 = 4000
        Assert.Equal(4000m, result.NewPnl);
    }

    // ── Partial fill + BE move ─────────────────────────────────

    [Fact]
    public void Long_PartialHit_AddsHalfPnlAndSetsBE()
    {
        // barLow=1005 stays safely above entry (BE stop=1000) so the trade stays open
        var result = Process(
            isLong: true, entry: 1000m, stop: 990m, target: 1100m, partial: 1050m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: true, useBE: true,
            barHigh: 1055m, barLow: 1005m); // bar reaches partial but not target

        Assert.True(result.PartialHit);
        Assert.True(result.StillActive);
        Assert.False(result.HitTarget);
        // Partial PnL: (1050-1000)*20*1 = 1000 (half contract = 1)
        Assert.Equal(1000m, result.NewPnl);
        // BE move: stop should move to entry
        Assert.Equal(1000m, result.NewStop);
    }

    [Fact]
    public void PartialAlreadyHit_NotProcessedAgain()
    {
        // partialHit=true means we already booked the partial; barLow=1005 stays above BE stop
        var result = Process(
            isLong: true, entry: 1000m, stop: 1000m, target: 1100m, partial: 1050m,
            contracts: 2, currentPnl: 1000m, partialHit: true,
            usePartial: true, useBE: true,
            barHigh: 1055m, barLow: 1005m);

        // Partial already hit — should not add more PnL, partial stays true
        Assert.Equal(1000m, result.NewPnl); // unchanged
        Assert.True(result.StillActive);
    }

    // ── Same-bar partial + stop (the "entry=exit price yet profit" scenario) ──

    [Fact]
    public void Short_SameBar_PartialThenBEStop_PartialHitPreserved()
    {
        // Mirrors the ESH26 3/3 11:00 trade:
        //   Short entry 1000, partial 974 (~50% of target dist), target 948, initial stop 1026
        //   Bar: low=973 (crosses partial 974) → partial fires, stop moves to BE=1000
        //         high=1001 (crosses BE stop 1000) → stops out at breakeven
        // Expected: HitStop=true, PartialHit=true (was incorrectly false before fix),
        //           PnL = partial gain + $0 breakeven = (1000-974)*20*1 = 520
        var result = Process(
            isLong: false, entry: 1000m, stop: 1026m, target: 948m, partial: 974m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: true, useBE: true,
            barHigh: 1001m, barLow: 973m);

        Assert.True(result.HitStop);
        Assert.False(result.HitTarget);
        Assert.False(result.StillActive);
        Assert.True(result.PartialHit);   // ← was always false before this fix
        // Partial PnL: (1000-974)*20*1 = 520   (1 of 2 contracts, half = floor(2*0.5) = 1)
        // BE stop PnL: (1000-1000)*20*1 = 0
        Assert.Equal(520m, result.NewPnl);
        // NewStop should be entry (BE level)
        Assert.Equal(1000m, result.NewStop);
    }

    [Fact]
    public void Long_SameBar_PartialThenBEStop_PartialHitPreserved()
    {
        // Long 1000, partial 1026, target 1052, bar: high=1027 (partial fires → BE=1000),
        // then low=999 (crosses BE stop)
        var result = Process(
            isLong: true, entry: 1000m, stop: 974m, target: 1052m, partial: 1026m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: true, useBE: true,
            barHigh: 1027m, barLow: 999m);

        Assert.True(result.HitStop);
        Assert.False(result.HitTarget);
        Assert.False(result.StillActive);
        Assert.True(result.PartialHit);
        // Partial PnL: (1026-1000)*20*1 = 520
        // BE stop PnL: (1000-1000)*20*1 = 0
        Assert.Equal(520m, result.NewPnl);
        Assert.Equal(1000m, result.NewStop);
    }

    [Fact]
    public void Short_SameBar_PartialThenTarget_PartialSkippedByTargetHitGuard()
    {
        // Short 1000, partial 974, target 948.
        // barLow=945 drops through BOTH partial (974) and target (948) in one bar.
        // ExitProcessor evaluates targetHit FIRST; the partial block is guarded by !targetHit,
        // so when targetHit=true the partial is skipped. Only remCts=1 closes at target.
        // (This is a known simulation simplification: partial is not retroactively booked when
        //  the bar overshoots straight to target.)
        var result = Process(
            isLong: false, entry: 1000m, stop: 1026m, target: 948m, partial: 974m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: true, useBE: true,
            barHigh: 1001m, barLow: 945m);

        Assert.True(result.HitTarget);
        Assert.False(result.PartialHit);  // partial was skipped because targetHit blocked it
        // PnL: (1000-948)*20 * remCts(1) = 1040   (usePartial → remCts = contracts - half = 1)
        Assert.Equal(1040m, result.NewPnl);
    }

    // ── No hit ───────────────────────────────────────────────

    [Fact]
    public void NoHit_StillActive_PnlUnchanged()
    {
        var result = Process(
            isLong: true, entry: 1000m, stop: 990m, target: 1100m, partial: 1050m,
            contracts: 2, currentPnl: 0, partialHit: false,
            usePartial: false, useBE: false,
            barHigh: 1020m, barLow: 995m); // between stop and target

        Assert.False(result.HitStop);
        Assert.False(result.HitTarget);
        Assert.True(result.StillActive);
        Assert.Equal(0m, result.NewPnl);
    }

    // ── Forced exit ───────────────────────────────────────────

    [Fact]
    public void ForcedExit_Long_NoPreviousPartial()
    {
        decimal pnl = ExitProcessor.ForcedExit(
            isLong: true, entry: 1000m, closePrice: 1020m,
            contracts: 2, partialHit: false, usePartial: false, pointValue: PointValue);
        // (1020-1000)*20*2 = 800
        Assert.Equal(800m, pnl);
    }

    [Fact]
    public void ForcedExit_Long_WithPartial_UsesRemainingContracts()
    {
        decimal pnl = ExitProcessor.ForcedExit(
            isLong: true, entry: 1000m, closePrice: 1020m,
            contracts: 2, partialHit: true, usePartial: true, pointValue: PointValue);
        // remaining = 2 - floor(2*0.5) = 2 - 1 = 1
        // (1020-1000)*20*1 = 400
        Assert.Equal(400m, pnl);
    }

    [Fact]
    public void ForcedExit_Short_Loss()
    {
        decimal pnl = ExitProcessor.ForcedExit(
            isLong: false, entry: 1000m, closePrice: 1010m,
            contracts: 2, partialHit: false, usePartial: false, pointValue: PointValue);
        // (1000-1010)*20*2 = -400
        Assert.Equal(-400m, pnl);
    }
}
