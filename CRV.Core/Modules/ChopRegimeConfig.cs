namespace CRV.Core.Modules;

/// <summary>
/// Configuration for <see cref="ChopRegimeDetector"/>. Each sub-filter can be
/// independently enabled/disabled. The detector flags a chop regime when the
/// number of enabled filters that vote "chop" meets or exceeds <see cref="MinVotesForChop"/>.
///
/// Maps 1:1 to the Pine Script CRV_ChopRegime_Detector.pine.
/// </summary>
public sealed record ChopRegimeConfig
{
    public bool Enabled { get; init; } = true;

    public bool    F1_RangeCompressionEnabled { get; init; } = true;
    public decimal F1_CompressionRatio        { get; init; } = 0.70m;   // OR / ATR threshold

    public bool    F2_FlatVwapEnabled         { get; init; } = true;
    public int     F2_SlopeLookbackBars       { get; init; } = 20;
    public decimal F2_FlatSlopeThresholdPct   { get; init; } = 0.05m;   // % of price

    public bool    F3_WeakDriveEnabled        { get; init; } = true;
    public decimal F3_MinDriveRatio           { get; init; } = 0.50m;   // |OR open→close| / OR height

    public bool    F4_LowVolumeEnabled        { get; init; } = true;
    public int     F4_VolumeMaLength          { get; init; } = 20;
    public decimal F4_MinVolumeRatio          { get; init; } = 1.00m;   // bar volume / avg

    public int MinVotesForChop { get; init; } = 2;

    /// <summary>Validates the config and returns a normalized copy. Clamps MinVotesForChop to enabled-filter count.</summary>
    public ChopRegimeConfig Normalize()
    {
        var enabledCount =
            (F1_RangeCompressionEnabled ? 1 : 0) +
            (F2_FlatVwapEnabled         ? 1 : 0) +
            (F3_WeakDriveEnabled        ? 1 : 0) +
            (F4_LowVolumeEnabled        ? 1 : 0);

        var min = Math.Clamp(MinVotesForChop, 1, Math.Max(enabledCount, 1));
        return this with { MinVotesForChop = min };
    }
}
