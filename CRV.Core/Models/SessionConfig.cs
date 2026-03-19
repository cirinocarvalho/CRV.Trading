namespace CRV.Core.Models;

// ── Session identity ────────────────────────────────────────────────────────

public enum SessionId { Asia, London, NY }

// ── Per-setup base (fields common to ALL setups A/B/C/D/F) ─────────────────

public abstract class SetupConfigBase
{
    public bool    Enabled            { get; set; } = false;
    public int     Contracts          { get; set; } = 2;
    public int     PartialCts         { get; set; } = 0;
    public int     PartialPct         { get; set; } = 50;
    public int     CutoffHour         { get; set; } = 14;
    public int     CutoffMinute       { get; set; } = 30;
    public int     MaxTrades          { get; set; } = 3;
    public string  OrderType          { get; set; } = "Market";
    public decimal MinRr              { get; set; } = 1.5m;
    public bool    CloseAtRthClose    { get; set; } = true;
    public bool    UsePartial         { get; set; } = true;
    public bool    UseBe              { get; set; } = true;
    public bool    AllowRearmAfterBE  { get; set; } = true;
    public int     MaxAdverseMinutes  { get; set; } = 0;
    public decimal HiVolMult          { get; set; } = 1.0m;
    public int     MaxContracts       { get; set; } = 2;
}

// ── Setup A — Pullback ──────────────────────────────────────────────────────

public class SetupConfigA : SetupConfigBase
{
    public SetupConfigA() { MaxTrades = 5; }

    public string  Mode               { get; set; } = "Conservative";
    public decimal NearPct            { get; set; } = 0.15m;
    public decimal PullbackPct        { get; set; } = 0.50m;
    public decimal StopPct            { get; set; } = 0.10m;
    public int     TargetPct          { get; set; } = 100;
    public int     EntryTickOffset    { get; set; } = 0;
    public bool    UseVwap            { get; set; } = true;
    public bool    UseOrbClose        { get; set; } = false;
}

// ── Setup B — Breakout Retest ───────────────────────────────────────────────

public class SetupConfigB : SetupConfigBase
{
    public SetupConfigB() { MaxTrades = 5; }

    public string  Mode               { get; set; } = "Conservative";
    public decimal NearPct            { get; set; } = 0.15m;
    public decimal RetestPct          { get; set; } = 0.05m;
    public decimal StopPct            { get; set; } = 0.50m;
    public int     TargetPct          { get; set; } = 100;
    public int     EntryTickOffset    { get; set; } = 0;
    public bool    UseVwap            { get; set; } = true;
    public bool    UseOrbClose        { get; set; } = false;
}

// ── Setup C — ORB False Breakout ─────────────────────────────────────────────

public class SetupConfigC : SetupConfigBase
{
    public decimal NearPct          { get; set; } = 0.15m;
    public decimal StopPct          { get; set; } = 0.10m;
    public int     TargetPct        { get; set; } = 100;
    public int     EntryTickOffset  { get; set; } = 0;
}

// ── Setup D — Session Range False Breakout ───────────────────────────────────

public class SetupConfigD : SetupConfigBase
{
    public decimal NearPct          { get; set; } = 0.15m;
    public decimal StopPct          { get; set; } = 0.10m;
    public int     TargetPct        { get; set; } = 100;
    public int     EntryTickOffset  { get; set; } = 0;
}

// ── Session-level config ────────────────────────────────────────────────────

public class SessionConfig
{
    public SessionId SessionId        { get; set; }
    public bool      Enabled          { get; set; } = false;

    // Session window
    public TimeOnly  OrbStart         { get; set; } = new(9, 30);
    public TimeOnly  OrbEnd           { get; set; } = new(10, 0);
    public TimeOnly  RthStart         { get; set; } = new(9, 30);
    public TimeOnly  RthEnd           { get; set; } = new(16, 0);
    public int       ExitMinutesBefore { get; set; } = 0;

    // Per-setup configs
    public SetupConfigA SetupA        { get; set; } = new();
    public SetupConfigB SetupB        { get; set; } = new();
    public SetupConfigC SetupC        { get; set; } = new();
    public SetupConfigD SetupD        { get; set; } = new();

    // ── ToLegacyConfig ─────────────────────────────────────────────────────
    /// <summary>
    /// Produces a flat <see cref="StrategyConfig"/> for this session by cloning
    /// <paramref name="global"/> (preserving Ticker, Broker, PointValue, etc.)
    /// and overwriting all session-specific and per-setup fields.
    /// </summary>
    public StrategyConfig ToLegacyConfig(StrategyConfig global)
    {
        var c = global.Clone();

        // Session times
        c.OrbStart          = OrbStart;
        c.OrbEnd            = OrbEnd;
        c.RthStart          = RthStart;
        c.RthEnd            = RthEnd;
        c.ExitMinutesBefore = ExitMinutesBefore;

        // ── Setup A ────────────────────────────────────────────────────────
        c.EnableA           = SetupA.Enabled;
        c.ContractsA        = SetupA.Contracts;
        c.PartialCtsA       = SetupA.PartialCts;
        c.PartialPctA       = SetupA.PartialPct;
        c.CutoffHourA       = SetupA.CutoffHour;
        c.CutoffMinuteA     = SetupA.CutoffMinute;
        c.MaxTradesA        = SetupA.MaxTrades;
        c.OrderTypeA        = SetupA.OrderType;
        c.MinRrA            = SetupA.MinRr;
        c.CloseAtRthCloseA  = SetupA.CloseAtRthClose;
        c.UsePartialA       = SetupA.UsePartial;
        c.UseBeA            = SetupA.UseBe;
        c.AllowRearmAfterBeA = SetupA.AllowRearmAfterBE;
        c.MaxAdverseMinutesA = SetupA.MaxAdverseMinutes;
        c.HiVolMultA        = SetupA.HiVolMult;
        c.MaxContractsA     = SetupA.MaxContracts;
        // A-specific
        c.ModeA             = SetupA.Mode;
        c.NearPctA          = SetupA.NearPct;
        c.NearPct           = SetupA.NearPct;   // keep legacy alias in sync
        c.PullbackPct       = SetupA.PullbackPct;
        c.StopPctA          = SetupA.StopPct;
        c.TargetPctA        = SetupA.TargetPct;
        c.EntryTickOffsetA  = SetupA.EntryTickOffset;
        c.UseVwapA          = SetupA.UseVwap;
        c.UseOrbCloseA      = SetupA.UseOrbClose;

        // ── Setup B ────────────────────────────────────────────────────────
        c.EnableB           = SetupB.Enabled;
        c.ContractsB        = SetupB.Contracts;
        c.PartialCtsB       = SetupB.PartialCts;
        c.PartialPctB       = SetupB.PartialPct;
        c.CutoffHourB       = SetupB.CutoffHour;
        c.CutoffMinuteB     = SetupB.CutoffMinute;
        c.MaxTradesB        = SetupB.MaxTrades;
        c.OrderTypeB        = SetupB.OrderType;
        c.MinRrB            = SetupB.MinRr;
        c.CloseAtRthCloseB  = SetupB.CloseAtRthClose;
        c.UsePartialB       = SetupB.UsePartial;
        c.UseBeB            = SetupB.UseBe;
        c.AllowRearmAfterBeB = SetupB.AllowRearmAfterBE;
        c.MaxAdverseMinutesB = SetupB.MaxAdverseMinutes;
        c.HiVolMultB        = SetupB.HiVolMult;
        c.MaxContractsB     = SetupB.MaxContracts;
        // B-specific
        c.ModeB             = SetupB.Mode;
        c.NearPctB          = SetupB.NearPct;
        c.RetestPct         = SetupB.RetestPct;
        c.StopPctB          = SetupB.StopPct;
        c.TargetPctB        = SetupB.TargetPct;
        c.EntryTickOffsetB  = SetupB.EntryTickOffset;
        c.UseVwapB          = SetupB.UseVwap;
        c.UseOrbCloseB      = SetupB.UseOrbClose;

        // ── Setup C ────────────────────────────────────────────────────────
        c.EnableC           = SetupC.Enabled;
        c.ContractsC        = SetupC.Contracts;
        c.PartialCtsC       = SetupC.PartialCts;
        c.PartialPctC       = SetupC.PartialPct;
        c.CutoffHourC       = SetupC.CutoffHour;
        c.CutoffMinuteC     = SetupC.CutoffMinute;
        c.MaxTradesC        = SetupC.MaxTrades;
        c.OrderTypeC        = SetupC.OrderType;
        c.MinRrC            = SetupC.MinRr;
        c.CloseAtRthCloseC  = SetupC.CloseAtRthClose;
        c.UsePartialC       = SetupC.UsePartial;
        c.UseBeC            = SetupC.UseBe;
        c.AllowRearmAfterBeC = SetupC.AllowRearmAfterBE;
        c.MaxAdverseMinutesC = SetupC.MaxAdverseMinutes;
        c.HiVolMultC        = SetupC.HiVolMult;
        c.MaxContractsC     = SetupC.MaxContracts;
        c.NearPctC          = SetupC.NearPct;
        c.StopPctC          = SetupC.StopPct;
        c.TargetPctC        = SetupC.TargetPct;
        c.EntryTickOffsetC  = SetupC.EntryTickOffset;

        // ── Setup D ────────────────────────────────────────────────────────
        c.EnableD           = SetupD.Enabled;
        c.ContractsD        = SetupD.Contracts;
        c.PartialCtsD       = SetupD.PartialCts;
        c.PartialPctD       = SetupD.PartialPct;
        c.CutoffHourD       = SetupD.CutoffHour;
        c.CutoffMinuteD     = SetupD.CutoffMinute;
        c.MaxTradesD        = SetupD.MaxTrades;
        c.OrderTypeD        = SetupD.OrderType;
        c.MinRrD            = SetupD.MinRr;
        c.CloseAtRthCloseD  = SetupD.CloseAtRthClose;
        c.UsePartialD       = SetupD.UsePartial;
        c.UseBeD            = SetupD.UseBe;
        c.AllowRearmAfterBeD = SetupD.AllowRearmAfterBE;
        c.MaxAdverseMinutesD = SetupD.MaxAdverseMinutes;
        c.HiVolMultD        = SetupD.HiVolMult;
        c.MaxContractsD     = SetupD.MaxContracts;
        c.NearPctD          = SetupD.NearPct;
        c.StopPctD          = SetupD.StopPct;
        c.TargetPctD        = SetupD.TargetPct;
        c.EntryTickOffsetD  = SetupD.EntryTickOffset;

        return c;
    }

    // ── FromExistingConfig ─────────────────────────────────────────────────
    /// <summary>
    /// Builds an NY <see cref="SessionConfig"/> from an existing flat config
    /// (round-trip: <c>FromExistingConfig(cfg).ToLegacyConfig(cfg)</c> should
    /// reproduce the same per-setup values).
    /// </summary>
    public static SessionConfig FromExistingConfig(StrategyConfig cfg) => new()
    {
        SessionId         = SessionId.NY,
        Enabled           = true,
        OrbStart          = cfg.OrbStart,
        OrbEnd            = cfg.OrbEnd,
        RthStart          = cfg.RthStart,
        RthEnd            = cfg.RthEnd,
        ExitMinutesBefore = cfg.ExitMinutesBefore,

        SetupA = new SetupConfigA
        {
            Enabled           = cfg.EnableA,
            Contracts         = cfg.ContractsA,
            PartialCts        = cfg.PartialCtsA,
            PartialPct        = cfg.PartialPctA,
            CutoffHour        = cfg.CutoffHourA,
            CutoffMinute      = cfg.CutoffMinuteA,
            MaxTrades         = cfg.MaxTradesA,
            OrderType         = cfg.OrderTypeA,
            MinRr             = cfg.MinRrA,
            CloseAtRthClose   = cfg.CloseAtRthCloseA,
            UsePartial        = cfg.UsePartialA,
            UseBe             = cfg.UseBeA,
            AllowRearmAfterBE = cfg.AllowRearmAfterBeA,
            MaxAdverseMinutes = cfg.MaxAdverseMinutesA,
            HiVolMult         = cfg.HiVolMultA,
            MaxContracts      = cfg.MaxContractsA,
            Mode              = cfg.ModeA,
            NearPct           = cfg.NearPctA,
            PullbackPct       = cfg.PullbackPct,
            StopPct           = cfg.StopPctA,
            TargetPct         = cfg.TargetPctA,
            EntryTickOffset   = cfg.EntryTickOffsetA,
            UseVwap           = cfg.UseVwapA,
            UseOrbClose       = cfg.UseOrbCloseA,
        },

        SetupB = new SetupConfigB
        {
            Enabled           = cfg.EnableB,
            Contracts         = cfg.ContractsB,
            PartialCts        = cfg.PartialCtsB,
            PartialPct        = cfg.PartialPctB,
            CutoffHour        = cfg.CutoffHourB,
            CutoffMinute      = cfg.CutoffMinuteB,
            MaxTrades         = cfg.MaxTradesB,
            OrderType         = cfg.OrderTypeB,
            MinRr             = cfg.MinRrB,
            CloseAtRthClose   = cfg.CloseAtRthCloseB,
            UsePartial        = cfg.UsePartialB,
            UseBe             = cfg.UseBeB,
            AllowRearmAfterBE = cfg.AllowRearmAfterBeB,
            MaxAdverseMinutes = cfg.MaxAdverseMinutesB,
            HiVolMult         = cfg.HiVolMultB,
            MaxContracts      = cfg.MaxContractsB,
            Mode              = cfg.ModeB,
            NearPct           = cfg.NearPctB,
            RetestPct         = cfg.RetestPct,
            StopPct           = cfg.StopPctB,
            TargetPct         = cfg.TargetPctB,
            EntryTickOffset   = cfg.EntryTickOffsetB,
            UseVwap           = cfg.UseVwapB,
            UseOrbClose       = cfg.UseOrbCloseB,
        },

        SetupC = new SetupConfigC
        {
            Enabled           = cfg.EnableC,
            Contracts         = cfg.ContractsC,
            PartialCts        = cfg.PartialCtsC,
            PartialPct        = cfg.PartialPctC,
            CutoffHour        = cfg.CutoffHourC,
            CutoffMinute      = cfg.CutoffMinuteC,
            MaxTrades         = cfg.MaxTradesC,
            OrderType         = cfg.OrderTypeC,
            MinRr             = cfg.MinRrC,
            CloseAtRthClose   = cfg.CloseAtRthCloseC,
            UsePartial        = cfg.UsePartialC,
            UseBe             = cfg.UseBeC,
            AllowRearmAfterBE = cfg.AllowRearmAfterBeC,
            MaxAdverseMinutes = cfg.MaxAdverseMinutesC,
            HiVolMult         = cfg.HiVolMultC,
            MaxContracts      = cfg.MaxContractsC,
            NearPct           = cfg.NearPctC,
            StopPct           = cfg.StopPctC,
            TargetPct         = cfg.TargetPctC,
            EntryTickOffset   = cfg.EntryTickOffsetC,
        },

        SetupD = new SetupConfigD
        {
            Enabled           = cfg.EnableD,
            Contracts         = cfg.ContractsD,
            PartialCts        = cfg.PartialCtsD,
            PartialPct        = cfg.PartialPctD,
            CutoffHour        = cfg.CutoffHourD,
            CutoffMinute      = cfg.CutoffMinuteD,
            MaxTrades         = cfg.MaxTradesD,
            OrderType         = cfg.OrderTypeD,
            MinRr             = cfg.MinRrD,
            CloseAtRthClose   = cfg.CloseAtRthCloseD,
            UsePartial        = cfg.UsePartialD,
            UseBe             = cfg.UseBeD,
            AllowRearmAfterBE = cfg.AllowRearmAfterBeD,
            MaxAdverseMinutes = cfg.MaxAdverseMinutesD,
            HiVolMult         = cfg.HiVolMultD,
            MaxContracts      = cfg.MaxContractsD,
            NearPct           = cfg.NearPctD,
            StopPct           = cfg.StopPctD,
            TargetPct         = cfg.TargetPctD,
            EntryTickOffset   = cfg.EntryTickOffsetD,
        },
    };

    // ── CreateDefaults ─────────────────────────────────────────────────────
    /// <summary>
    /// Returns a list of three sessions: Asia (disabled, 19:00–23:59), London
    /// (disabled, 3:00–8:00), and NY (from existing flat config, enabled).
    /// </summary>
    public static List<SessionConfig> CreateDefaults(StrategyConfig cfg)
    {
        var asia = new SessionConfig
        {
            SessionId  = SessionId.Asia,
            Enabled    = false,
            OrbStart   = new TimeOnly(19, 0),
            OrbEnd     = new TimeOnly(19, 30),
            RthStart   = new TimeOnly(19, 0),
            RthEnd     = new TimeOnly(23, 59),
        };

        var london = new SessionConfig
        {
            SessionId  = SessionId.London,
            Enabled    = false,
            OrbStart   = new TimeOnly(3, 0),
            OrbEnd     = new TimeOnly(3, 30),
            RthStart   = new TimeOnly(3, 0),
            RthEnd     = new TimeOnly(8, 0),
        };

        var ny = FromExistingConfig(cfg);

        return [asia, london, ny];
    }
}
