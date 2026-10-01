namespace CRV.Web.Pages;

/// <summary>
/// View model passed to _SetupConfigSection.cshtml partial.
/// Carries both the base config (all common fields) and the subclass-specific
/// optional fields so the partial stays strongly typed throughout.
/// </summary>
public sealed record SetupConfigSectionModel(
    int    SessionIndex,    // loop variable i from the session loop
    string SessionId,       // e.g. "NY", "London", "Asia"
    string SetupLetter,     // "A", "B", "C", "D"

    // ── Base fields (all setups) ──────────────────────────────────────────
    CRV.Core.Models.SetupConfigBase Setup,

    // ── Detail-row extended fields (subclass-specific) ────────────────────
    // These are null when the setup does not have the field.
    decimal NearPct,
    decimal StopPct,
    int     TargetPct,
    int     EntryTickOffset,

    // Mode: "Conservative"/"Aggressive"/"SmartAggressive" — only A and B
    string? Mode,

    // PullbackPct: only A
    decimal? PullbackPct,

    // RetestPct: only B
    decimal? RetestPct,

    // UseVwap: only A and B
    bool? UseVwap,

    // UseOrbClose: only A and B
    bool? UseOrbClose,

    // UseCloseConfirmation: only A
    bool? UseCloseConfirmation
);

/// <summary>Model for _LegacySetups.cshtml: setups A–D of one session.</summary>
public sealed record LegacySetupsModel(int SessionIndex, CRV.Core.Models.SessionConfig Session);
