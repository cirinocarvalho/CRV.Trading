using CRV.Core.Modules;
using Xunit;

namespace CRV.Core.Tests.Modules;

public class CompositeSetupEngineTests
{
    private static CompositeSetupEngine Create() => new();

    // ── 1. Arms Setup C — Sweep Reversal Long ──────────────────────

    [Fact]
    public void ArmsSetupC_SweepReversal_Long()
    {
        var engine = Create();

        engine.Evaluate(
            anyBullSweep: true, anyBearSweep: false,
            close: 5050m, vwap: 5040m,
            bullScore: 3, bearScore: 0,
            openingDriveBull: false, openingDriveBear: false,
            trendDayBull: false, trendDayBear: false,
            bullVwapPullback: false, bearVwapPullback: false,
            vwapReversionLong: false, vwapReversionShort: false,
            inMidday: false,
            londonSweptAsiaLow: false, londonSweptAsiaHigh: false,
            nyBullExpansion: false, nyBearExpansion: false);

        Assert.True(engine.SetupCBull);
        Assert.False(engine.SetupCBear);
        Assert.True(engine.AnySetupActive);
    }

    // ── 2. Arms Setup D — Drive + Pullback Long ────────────────────

    [Fact]
    public void ArmsSetupD_DrivePullback_Long()
    {
        var engine = Create();

        engine.Evaluate(
            anyBullSweep: false, anyBearSweep: false,
            close: 5050m, vwap: 5040m,
            bullScore: 0, bearScore: 0,
            openingDriveBull: true, openingDriveBear: false,
            trendDayBull: true, trendDayBear: false,
            bullVwapPullback: true, bearVwapPullback: false,
            vwapReversionLong: false, vwapReversionShort: false,
            inMidday: false,
            londonSweptAsiaLow: false, londonSweptAsiaHigh: false,
            nyBullExpansion: false, nyBearExpansion: false);

        Assert.True(engine.SetupDBull);
        Assert.False(engine.SetupDBear);
        Assert.True(engine.AnySetupActive);
    }

    // ── 3. Arms Setup F — VWAP Reversion Long ──────────────────────

    [Fact]
    public void ArmsSetupF_VwapReversion_Long()
    {
        var engine = Create();

        engine.Evaluate(
            anyBullSweep: false, anyBearSweep: false,
            close: 5050m, vwap: 5040m,
            bullScore: 0, bearScore: 0,
            openingDriveBull: false, openingDriveBear: false,
            trendDayBull: false, trendDayBear: false,
            bullVwapPullback: false, bearVwapPullback: false,
            vwapReversionLong: true, vwapReversionShort: false,
            inMidday: true,
            londonSweptAsiaLow: false, londonSweptAsiaHigh: false,
            nyBullExpansion: false, nyBearExpansion: false);

        Assert.True(engine.SetupFBull);
        Assert.False(engine.SetupFBear);
        Assert.True(engine.AnySetupActive);
    }

    // ── 4. Arms Session Expansion Long ─────────────────────────────

    [Fact]
    public void ArmsSessionExpansion_Long()
    {
        var engine = Create();

        engine.Evaluate(
            anyBullSweep: false, anyBearSweep: false,
            close: 5050m, vwap: 5040m,
            bullScore: 0, bearScore: 0,
            openingDriveBull: false, openingDriveBear: false,
            trendDayBull: false, trendDayBear: false,
            bullVwapPullback: false, bearVwapPullback: false,
            vwapReversionLong: false, vwapReversionShort: false,
            inMidday: false,
            londonSweptAsiaLow: true, londonSweptAsiaHigh: false,
            nyBullExpansion: true, nyBearExpansion: false);

        Assert.True(engine.SessionExpansionBull);
        Assert.False(engine.SessionExpansionBear);
        Assert.True(engine.AnySetupActive);
    }

    // ── 5. No setup when conditions not met ─────────────────────────

    [Fact]
    public void NoSetupWhenConditionsNotMet()
    {
        var engine = Create();

        engine.Evaluate(
            anyBullSweep: false, anyBearSweep: false,
            close: 5050m, vwap: 5040m,
            bullScore: 0, bearScore: 0,
            openingDriveBull: false, openingDriveBear: false,
            trendDayBull: false, trendDayBear: false,
            bullVwapPullback: false, bearVwapPullback: false,
            vwapReversionLong: false, vwapReversionShort: false,
            inMidday: false,
            londonSweptAsiaLow: false, londonSweptAsiaHigh: false,
            nyBullExpansion: false, nyBearExpansion: false);

        Assert.False(engine.AnySetupActive);
        Assert.False(engine.SetupCBull);
        Assert.False(engine.SetupCBear);
        Assert.False(engine.SetupDBull);
        Assert.False(engine.SetupDBear);
        Assert.False(engine.SetupFBull);
        Assert.False(engine.SetupFBear);
        Assert.False(engine.SessionExpansionBull);
        Assert.False(engine.SessionExpansionBear);
    }
}
