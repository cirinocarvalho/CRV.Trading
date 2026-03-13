namespace CRV.Core.Modules;

/// <summary>Pure evaluator — not an IEngineModule. Combines all module signals into actionable setups.</summary>
public class CompositeSetupEngine
{
    // Setup C: Sweep Reversal
    public bool SetupCBull { get; private set; }
    public bool SetupCBear { get; private set; }

    // Setup D: Drive + Pullback
    public bool SetupDBull { get; private set; }
    public bool SetupDBear { get; private set; }

    // Setup F: VWAP Reversion (midday mean-reversion)
    public bool SetupFBull { get; private set; }
    public bool SetupFBear { get; private set; }

    // Session Expansion
    public bool SessionExpansionBull { get; private set; }
    public bool SessionExpansionBear { get; private set; }

    public bool AnySetupActive =>
        SetupCBull || SetupCBear ||
        SetupDBull || SetupDBear ||
        SetupFBull || SetupFBear ||
        SessionExpansionBull || SessionExpansionBear;

    public void Evaluate(
        bool anyBullSweep, bool anyBearSweep,
        decimal close, decimal vwap,
        int bullScore, int bearScore,
        bool openingDriveBull, bool openingDriveBear,
        bool trendDayBull, bool trendDayBear,
        bool bullVwapPullback, bool bearVwapPullback,
        bool vwapReversionLong, bool vwapReversionShort,
        bool inMidday,
        bool londonSweptAsiaLow, bool londonSweptAsiaHigh,
        bool nyBullExpansion, bool nyBearExpansion)
    {
        // Setup C: Sweep Reversal
        SetupCBull = anyBullSweep && close > vwap && bullScore >= 2;
        SetupCBear = anyBearSweep && close < vwap && bearScore >= 2;

        // Setup D: Drive + Pullback
        SetupDBull = openingDriveBull && trendDayBull && bullVwapPullback;
        SetupDBear = openingDriveBear && trendDayBear && bearVwapPullback;

        // Setup F: VWAP Reversion (midday only, counter-trend)
        SetupFBull = inMidday && !trendDayBear && vwapReversionLong;
        SetupFBear = inMidday && !trendDayBull && vwapReversionShort;

        // Session Expansion: London sweeps Asia, NY expands
        SessionExpansionBull = londonSweptAsiaLow && nyBullExpansion;
        SessionExpansionBear = londonSweptAsiaHigh && nyBearExpansion;
    }

    public void Reset()
    {
        SetupCBull = false;
        SetupCBear = false;
        SetupDBull = false;
        SetupDBear = false;
        SetupFBull = false;
        SetupFBear = false;
        SessionExpansionBull = false;
        SessionExpansionBear = false;
    }
}
