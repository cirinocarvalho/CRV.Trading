namespace CRV.Core.Modules;

/// <summary>
/// Per-filter diagnostic. <see cref="Active"/> means the filter was enabled
/// AND had enough data to evaluate. <see cref="Voted"/> means the filter
/// voted "chop" on this evaluation.
/// </summary>
public readonly record struct ChopFilterDiagnostic(
    string  Name,
    bool    Active,
    bool    Voted,
    decimal Value,        // raw measurement (compression ratio, slope%, drive ratio, vol ratio)
    decimal Threshold);

/// <summary>
/// Aggregate result of a chop regime evaluation. Immutable snapshot tied to
/// the bar time that produced it.
/// </summary>
public readonly record struct ChopRegimeResult(
    DateTime             BarTime,
    bool                 IsChop,
    int                  Votes,
    int                  VotesRequired,
    int                  EnabledFilterCount,
    ChopFilterDiagnostic RangeCompression,
    ChopFilterDiagnostic FlatVwap,
    ChopFilterDiagnostic WeakDrive,
    ChopFilterDiagnostic LowVolume)
{
    /// <summary>A neutral "no decision yet" result — used before the OR locks or before warmup completes.</summary>
    public static ChopRegimeResult Pending(DateTime barTime) => new(
        BarTime:            barTime,
        IsChop:             false,
        Votes:              0,
        VotesRequired:      0,
        EnabledFilterCount: 0,
        RangeCompression:   new("RangeCompression", Active: false, Voted: false, Value: 0m, Threshold: 0m),
        FlatVwap:           new("FlatVwap",         Active: false, Voted: false, Value: 0m, Threshold: 0m),
        WeakDrive:          new("WeakDrive",        Active: false, Voted: false, Value: 0m, Threshold: 0m),
        LowVolume:          new("LowVolume",        Active: false, Voted: false, Value: 0m, Threshold: 0m));
}
