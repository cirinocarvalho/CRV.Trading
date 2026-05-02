namespace CRV.Web.Pages.Dashboard;

using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Live;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

public class ProspectusModel : PageModel
{
    private readonly StrategyConfigService _cfgSvc;
    private readonly LiveEngineOrchestrator _orchestrator;
    private readonly TradingDbContext _db;

    public ProspectusModel(StrategyConfigService cfgSvc, LiveEngineOrchestrator orchestrator, TradingDbContext db)
    {
        _cfgSvc = cfgSvc;
        _db = db;
        _orchestrator = orchestrator;
    }

    // ── View data ────────────────────────────────────────────────
    public List<ProspectusSession> Sessions { get; set; } = new();
    public decimal TotalProfitToday { get; set; }
    public decimal TotalRiskToday { get; set; }
    public decimal TotalProfitAvg { get; set; }
    public decimal TotalRiskAvg { get; set; }
    /// <summary>The date being viewed. null = today (live ORB from engine).</summary>
    public string SelectedDate { get; set; } = "";
    /// <summary>Display label for the selected date column header.</summary>
    public string SelectedDateLabel { get; set; } = "Today";
    /// <summary>True when using live engine snapshot (today, no date param).</summary>
    public bool IsLive { get; set; }

    public void OnGet(string? date)
    {
        var cfg = _cfgSvc.Current;
        var setups = cfg.ToSetupConfigs();

        // Parse selected date — null/empty = today (live)
        DateTime? selectedDateUtc = null;
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var parsed))
        {
            selectedDateUtc = parsed.Date;
            SelectedDate = parsed.ToString("yyyy-MM-dd");
            SelectedDateLabel = parsed.ToString("MMM d, yyyy");
            IsLive = false;
        }
        else
        {
            SelectedDate = DateTime.Now.ToString("yyyy-MM-dd");
            SelectedDateLabel = "Today (Live)";
            IsLive = true;
        }

        var snapshot = IsLive ? _orchestrator.LastSnapshot : null;
        var activeSessionId = snapshot?.ActiveSessionId ?? "";

        // Load ALL orb_cache entries once — used for both selected-date lookup and monthly avg
        var allOrbEntries = LoadAllOrbEntries();

        // Build ORB lookup for the selected date from cache.
        // For "today (live)" mode, we also load today's cache entries because the engine
        // only holds the ORB for the CURRENTLY ACTIVE session — earlier sessions (e.g.
        // Asia/London when NY is active) are only in the cache.
        var lookupDate = selectedDateUtc?.Date ?? DateTime.UtcNow.Date;
        var selectedDateOrb = allOrbEntries
            .Where(e => e.TradingDate.Date == lookupDate && (e.OrbHigh - e.OrbLow) > 0)
            .GroupBy(e => (FuturesSymbol.Normalize(e.Symbol), e.SessionId))
            .ToDictionary(g => g.Key, g => Math.Round(g.First().OrbHigh - g.First().OrbLow, 4));

        // Per-setup back-computed ranges from the Trades table for the selected date.
        // Key: (ticker, session, setupLabel) → range engine actually sized against.
        // Used by SessionFakeout (which sizes off the prior-session range, not the ORB)
        // and as the historical fallback for ORB-based rows when orb_cache is empty.
        var selectedDatePerSetup = new Dictionary<(string, string, string), decimal>();
        if (selectedDateUtc.HasValue)
        {
            var dayStart = selectedDateUtc.Value.Date;
            selectedDatePerSetup = ComputeRangeFromTradesPerSetup(dayStart, dayStart.AddDays(1), setups);
        }

        // Fallback: if orb_cache has no entries for the selected date, back-compute ORB
        // by averaging the per-setup map across setups in the same (ticker, session).
        if (!IsLive && selectedDateOrb.Count == 0 && selectedDatePerSetup.Count > 0)
        {
            selectedDateOrb = AggregateAcrossSetups(selectedDatePerSetup);
        }

        // Per-ticker daily session-reference ranges for the selected date, folded
        // across that day's Asia/London/NY orb_cache entries (each entry knows the
        // ranges that had already closed by save-time; folding takes the max so we
        // pick up whichever session captured each value first).
        var selectedDateSessionRanges = FoldSessionRanges(
            allOrbEntries.Where(e => e.TradingDate.Date == lookupDate));

        // Same fold for every day in the current month — used to back-compute the
        // monthly average prior-session range for SessionFakeout.
        var monthStartCache = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var monthlySessionRangesByDay = allOrbEntries
            .Where(e => e.TradingDate >= monthStartCache)
            .GroupBy(e => (e.TradingDate.Date, FuturesSymbol.Normalize(e.Symbol)))
            .ToDictionary(g => g.Key, g => FoldSessionRanges(g).Values.FirstOrDefault());

        // Build monthly average ORB ranges from orb_cache.json (ORB-based setups).
        var monthlyAvgOrb = ComputeMonthlyAverageOrb(allOrbEntries);

        // Build monthly average per-setup back-computed range from Trades for the
        // current calendar month — needed for SessionFakeout, whose prior-session
        // ranges are not stored in orb_cache.
        var monthStartUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var monthlyPerSetup = ComputeRangeFromTradesPerSetup(
            monthStartUtc, monthStartUtc.AddMonths(1), setups);

        // Identify active sessions from the config
        var sessionIds = new[] { "Asia", "London", "NY" };

        foreach (var sessionId in sessionIds)
        {
            var sessionSetups = new List<ProspectusRow>();

            foreach (var setup in setups)
            {
                if (!setup.Enabled) continue;

                // Check if this setup is enabled for this session
                bool enabledForSession = setup.IsEnabledForSession(sessionId);
                if (!enabledForSession) continue;

                var ticker = !string.IsNullOrEmpty(setup.Ticker) ? setup.Ticker : cfg.Ticker;
                var normTicker = FuturesSymbol.Normalize(ticker);
                var pointValue = setup.PointValue > 0 ? setup.PointValue : cfg.PointValue;
                var tickSize = setup.TickSize > 0 ? setup.TickSize : cfg.TickSize;

                // Selected-date ORB: prefer orb_cache per session+date, fall back to live
                // snapshot only for the currently active session (earlier sessions'
                // ORBs are only in the cache, not in the engine snapshot).
                decimal todayOrb = 0;
                if (selectedDateOrb.TryGetValue((normTicker, sessionId), out var cached))
                    todayOrb = cached;
                else if (selectedDateOrb.TryGetValue((normTicker, ""), out var cachedLegacy))
                    todayOrb = cachedLegacy;
                // Fall back to live engine snapshot for the active session if cache miss
                // (ORB may have just formed and not been saved to cache yet)
                if (todayOrb == 0 && IsLive
                    && string.Equals(sessionId, activeSessionId, StringComparison.OrdinalIgnoreCase)
                    && snapshot?.GroupSnapshots != null)
                {
                    var gs = snapshot.GroupSnapshots.Values.FirstOrDefault(g =>
                        FuturesSymbol.Normalize(g.Ticker) == normTicker);
                    if (gs != null && gs.OrbRange > 0)
                        todayOrb = gs.OrbRange;
                }

                // Monthly average ORB
                decimal avgOrb = monthlyAvgOrb.TryGetValue((normTicker, sessionId), out var avg) ? avg
                    : monthlyAvgOrb.TryGetValue((normTicker, ""), out var avgLegacy) ? avgLegacy : 0;

                // SessionFakeout sizes stops/targets off the PRIOR session's full range
                // (per FakeoutReferenceSession), not the ORB. Resolve via, in order:
                //   1. Live engine snapshot (today's view)
                //   2. orb_cache PrevDay/Asia/London ranges (historical view)
                //   3. Back-computed from this setup's past trades (legacy fallback,
                //      only works for days the setup actually traded)
                // Monthly avg likewise prefers cached ranges, falls back to trades.
                if (setup.StrategyType == StrategyType.SessionFakeout)
                {
                    var key = (normTicker, sessionId, setup.Id);
                    decimal sfToday = 0m;
                    if (IsLive)
                    {
                        sfToday = ResolveSessionFakeoutRange(setup, sessionId, normTicker, snapshot);
                    }
                    if (sfToday == 0 && selectedDateSessionRanges.TryGetValue(normTicker, out var sr))
                    {
                        sfToday = ResolveSessionFakeoutRangeFromCache(setup, sessionId, sr);
                    }
                    if (sfToday == 0 && selectedDatePerSetup.TryGetValue(key, out var sfHist))
                    {
                        sfToday = sfHist;
                    }
                    todayOrb = sfToday;

                    // Monthly avg: average the prior-session range across each day in
                    // the current month using the same resolver, then fall back to
                    // trade back-computation if no cached days are available.
                    var monthlyRanges = new List<decimal>();
                    foreach (var ((_, t), sr2) in monthlySessionRangesByDay)
                    {
                        if (t != normTicker || sr2 == null) continue;
                        var r = ResolveSessionFakeoutRangeFromCache(setup, sessionId, sr2);
                        if (r > 0) monthlyRanges.Add(r);
                    }
                    avgOrb = monthlyRanges.Count > 0
                        ? Math.Round(monthlyRanges.Average(), 4)
                        : (monthlyPerSetup.TryGetValue(key, out var sfAvg) ? sfAvg : 0m);
                }

                // Per-setup config values
                var stopPct = setup.StopPct;
                var targetPct = setup.TargetPct / 100m;   // int → decimal multiplier
                var partialPct = setup.PartialPct / 100m; // fraction of targetDist
                var usePartial = setup.UsePartial;

                // EntryTickOffset shifts the entry price after sl/tp/pp are computed
                // (see e.g. RetestStrategy.cs:507-515). Net effect on a positive offset:
                // stop distance grows by offset, target/partial distances shrink by offset.
                // Negative offsets reverse it. Mirror that here so the dollar columns
                // reflect what the engine actually sizes against.
                var entryOffsetPts = setup.EntryTickOffset * tickSize;

                // Compute P&L for a given ORB range
                ProspectusRow MakeRow(decimal orbRange)
                {
                    // Skip the offset adjustment when we have no range — otherwise an
                    // empty row would still register a phantom risk = offset*pv*cts.
                    var offset = orbRange > 0 ? entryOffsetPts : 0m;
                    var targetDist = Math.Max(0m, orbRange * targetPct - offset);
                    var partialDist = Math.Max(0m, orbRange * targetPct * partialPct - offset);
                    var stopDist = Math.Max(0m, orbRange * stopPct + offset);

                    // Route through AutoSizeByRiskCalculator so projections match runtime
                    // sizing. Synthetic (ep=stopDist, sl=0) — calculator only uses
                    // Math.Abs(ep-sl) × PointValue for riskPerCt.
                    // atrRatio=0 means "not high-vol regime" — projections show baseline
                    // sizing; live high-vol days will scale up via HiVolMult at runtime.
                    var (sizedCts, sizedPartial) = AutoSizeByRiskCalculator.Calc(
                        ep: stopDist, sl: 0m, cfg: setup, atrRatio: 0m);

                    // sizedCts == 0 signals "skip" (AutoSize ON + floor risk > budget).
                    // For projection display fall back to baseline so the row still shows
                    // what the trade WOULD look like absent the budget veto.
                    int contracts = sizedCts > 0 ? sizedCts : setup.Contracts;
                    int partialCts = !usePartial
                        ? 0
                        : (sizedPartial > 0
                            ? sizedPartial
                            : (setup.PartialCts > 0 ? setup.PartialCts : contracts / 2));
                    if (partialCts >= contracts) partialCts = contracts - 1;
                    var remainCts = contracts - partialCts;

                    var tgt1Usd = usePartial ? partialDist * pointValue * partialCts : 0;
                    var tgt2Usd = targetDist * pointValue * remainCts;
                    var profitUsd = tgt1Usd + tgt2Usd;
                    var riskUsd = stopDist * pointValue * contracts;

                    return new ProspectusRow
                    {
                        SetupLabel = setup.Name ?? setup.Id,
                        StrategyType = setup.StrategyType.ToString(),
                        Ticker = normTicker,
                        PointValue = pointValue,
                        Contracts = contracts,
                        PartialCts = partialCts,
                        RemainCts = remainCts,
                        UsePartial = usePartial,
                        TargetPct = targetPct,
                        PartialPct = partialPct,
                        StopPct = stopPct,
                        OrbRange = orbRange,
                        Tgt1Usd = Math.Round(tgt1Usd, 2),
                        Tgt2Usd = Math.Round(tgt2Usd, 2),
                        ProfitUsd = Math.Round(profitUsd, 2),
                        RiskUsd = Math.Round(riskUsd, 2),
                        Rr = riskUsd > 0 ? Math.Round(profitUsd / riskUsd, 2) : 0,
                    };
                }

                var rowToday = MakeRow(todayOrb);
                var rowAvg = MakeRow(avgOrb);

                sessionSetups.Add(new ProspectusRow
                {
                    SetupLabel = setup.Name ?? setup.Id,
                    StrategyType = setup.StrategyType.ToString(),
                    Ticker = normTicker,
                    PointValue = pointValue,
                    Contracts = rowToday.Contracts,
                    PartialCts = rowToday.PartialCts,
                    RemainCts = rowToday.RemainCts,
                    UsePartial = usePartial,
                    TargetPct = targetPct,
                    PartialPct = partialPct,
                    StopPct = stopPct,
                    // Today's ORB
                    OrbRange = todayOrb,
                    Tgt1Usd = rowToday.Tgt1Usd,
                    Tgt2Usd = rowToday.Tgt2Usd,
                    ProfitUsd = rowToday.ProfitUsd,
                    RiskUsd = rowToday.RiskUsd,
                    Rr = rowToday.Rr,
                    // Monthly average
                    AvgOrbRange = avgOrb,
                    AvgTgt1Usd = rowAvg.Tgt1Usd,
                    AvgTgt2Usd = rowAvg.Tgt2Usd,
                    AvgProfitUsd = rowAvg.ProfitUsd,
                    AvgRiskUsd = rowAvg.RiskUsd,
                    AvgRr = rowAvg.Rr,
                });
            }

            if (sessionSetups.Count > 0)
            {
                Sessions.Add(new ProspectusSession
                {
                    SessionId = sessionId,
                    Rows = sessionSetups,
                    TotalProfitToday = sessionSetups.Sum(r => r.ProfitUsd),
                    TotalRiskToday = sessionSetups.Sum(r => r.RiskUsd),
                    TotalProfitAvg = sessionSetups.Sum(r => r.AvgProfitUsd),
                    TotalRiskAvg = sessionSetups.Sum(r => r.AvgRiskUsd),
                });
            }
        }

        TotalProfitToday = Sessions.Sum(s => s.TotalProfitToday);
        TotalRiskToday = Sessions.Sum(s => s.TotalRiskToday);
        TotalProfitAvg = Sessions.Sum(s => s.TotalProfitAvg);
        TotalRiskAvg = Sessions.Sum(s => s.TotalRiskAvg);
    }

    /// <summary>Container for the four prior-session reference ranges on a given day.</summary>
    private sealed record DaySessionRanges(
        decimal PdH, decimal PdL,
        decimal AsiaHigh, decimal AsiaLow,
        decimal LondonHigh, decimal LondonLow);

    /// <summary>
    /// Fold a day's orb_cache entries (Asia/London/NY) into a single per-ticker
    /// view of all four reference ranges. Each entry only knows the ranges that
    /// had closed by save-time, so taking the max across entries pulls in
    /// whichever session captured each value first.
    /// </summary>
    private static Dictionary<string, DaySessionRanges> FoldSessionRanges(
        IEnumerable<OrbStateCache> entries)
    {
        var byTicker = entries
            .GroupBy(e => FuturesSymbol.Normalize(e.Symbol))
            .ToDictionary(g => g.Key, g => new DaySessionRanges(
                PdH:        g.Max(e => e.PrevDayHigh),
                PdL:        g.Max(e => e.PrevDayLow),
                AsiaHigh:   g.Max(e => e.AsiaHigh),
                AsiaLow:    g.Max(e => e.AsiaLow),
                LondonHigh: g.Max(e => e.LondonHigh),
                LondonLow:  g.Max(e => e.LondonLow)));
        return byTicker;
    }

    /// <summary>
    /// Same shape as <see cref="ResolveSessionFakeoutRange"/> but reads from a
    /// cached <see cref="DaySessionRanges"/> instead of the live snapshot.
    /// </summary>
    private static decimal ResolveSessionFakeoutRangeFromCache(
        StrategySetupConfig setup, string sessionId, DaySessionRanges r)
    {
        if (!Enum.TryParse<SessionId>(sessionId, ignoreCase: true, out var sid)) return 0m;
        var (high, low) = TickerGroup.ResolveFakeoutReference(
            sid, setup.FakeoutReferenceSession,
            r.PdH, r.PdL, r.AsiaHigh, r.AsiaLow, r.LondonHigh, r.LondonLow);
        return (high > 0 && low > 0 && high > low) ? Math.Round(high - low, 4) : 0m;
    }

    /// <summary>
    /// Resolve the prior-session range used by a SessionFakeout setup. Mirrors
    /// <see cref="TickerGroup.ResolveFakeoutReference"/> so the prospectus shows
    /// the same range the engine sizes against. Returns 0 when the snapshot is
    /// unavailable, the session id is unknown, or the chosen range hasn't formed.
    /// </summary>
    private static decimal ResolveSessionFakeoutRange(
        StrategySetupConfig setup, string sessionId, string normTicker, EngineSnapshot? snapshot)
    {
        if (snapshot?.GroupSnapshots == null) return 0m;
        if (!Enum.TryParse<SessionId>(sessionId, ignoreCase: true, out var sid)) return 0m;

        var gs = snapshot.GroupSnapshots.Values.FirstOrDefault(g =>
            FuturesSymbol.Normalize(g.Ticker) == normTicker);
        if (gs == null) return 0m;

        var (high, low) = TickerGroup.ResolveFakeoutReference(
            sid, setup.FakeoutReferenceSession,
            gs.PrevDayHigh, gs.PrevDayLow,
            gs.AsiaHigh, gs.AsiaLow,
            gs.LondonHigh, gs.LondonLow);

        return (high > 0 && low > 0 && high > low) ? Math.Round(high - low, 4) : 0m;
    }

    // ── ORB cache helpers ──────────────────────────────────────────

    private static List<OrbStateCache> LoadAllOrbEntries()
    {
        try
        {
            const string cacheFile = "orb_cache.json";
            if (!System.IO.File.Exists(cacheFile)) return new();
            var json = System.IO.File.ReadAllText(cacheFile);
            json = json.TrimStart();
            if (json.StartsWith('['))
                return System.Text.Json.JsonSerializer.Deserialize<List<OrbStateCache>>(json) ?? new();
            var single = System.Text.Json.JsonSerializer.Deserialize<OrbStateCache>(json);
            return single != null ? new() { single } : new();
        }
        catch { return new(); }
    }

    private static Dictionary<(string ticker, string sessionId), decimal> ComputeMonthlyAverageOrb(
        List<OrbStateCache> entries)
    {
        var result = new Dictionary<(string, string), decimal>();
        try
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1);

            var monthly = entries
                .Where(e => e.TradingDate >= monthStart && (e.OrbHigh - e.OrbLow) > 0)
                .GroupBy(e => (FuturesSymbol.Normalize(e.Symbol), e.SessionId));

            foreach (var g in monthly)
            {
                var avgRange = g.Average(e => e.OrbHigh - e.OrbLow);
                result[g.Key] = Math.Round(avgRange, 4);
            }
        }
        catch { /* best effort */ }
        return result;
    }

    /// <summary>
    /// Back-compute the range each setup actually sized against from the Trades table.
    /// Formula: range = |entry − initialStop| / stopPct (the engine's inverse — works for
    /// both ORB-based setups and SessionFakeout, since each writes its own initial stop
    /// using its own reference range).
    /// Returns per-setup averages keyed by (ticker, sessionId, setupLabel).
    /// </summary>
    private Dictionary<(string ticker, string sessionId, string setupLabel), decimal>
        ComputeRangeFromTradesPerSetup(DateTime startUtc, DateTime endUtc, List<StrategySetupConfig> setups)
    {
        var result = new Dictionary<(string, string, string), decimal>();
        try
        {
            var trades = _db.Set<TradeRecord>()
                .Where(t => t.Source == "live" && t.EnteredAt >= startUtc && t.EnteredAt < endUtc
                            && t.InitialStop > 0 && t.Entry > 0)
                .ToList();
            if (trades.Count == 0) return result;

            // StopPct lookup by setup id
            var stopPctByLabel = setups
                .Where(s => s.StopPct > 0)
                .ToDictionary(s => s.Id, s => s.StopPct, StringComparer.OrdinalIgnoreCase);

            var grouped = trades.GroupBy(t => (FuturesSymbol.Normalize(t.Ticker), t.SessionId, t.SetupLabel));
            foreach (var g in grouped)
            {
                var ranges = new List<decimal>();
                foreach (var t in g)
                {
                    var stopPct = stopPctByLabel.TryGetValue(t.SetupLabel, out var sp) ? sp : 0.5m;
                    if (stopPct <= 0) stopPct = 0.5m;
                    var stopDist = Math.Abs(t.Entry - t.InitialStop);
                    if (stopDist <= 0) continue;
                    ranges.Add(stopDist / stopPct);
                }
                if (ranges.Count > 0)
                    result[g.Key] = Math.Round(ranges.Average(), 4);
            }
        }
        catch { /* best effort */ }
        return result;
    }

    /// <summary>
    /// Aggregate a per-setup range map down to (ticker, session) by averaging across
    /// setups in the same group. Used as the historical fallback for ORB-based rows
    /// when orb_cache has no entry for the selected date — preserves the original
    /// (pre-per-setup) behavior of <c>ComputeOrbFromTrades</c>.
    /// </summary>
    private static Dictionary<(string ticker, string sessionId), decimal> AggregateAcrossSetups(
        Dictionary<(string ticker, string sessionId, string setupLabel), decimal> perSetup)
    {
        var byPair = perSetup
            .GroupBy(kv => (kv.Key.ticker, kv.Key.sessionId))
            .ToDictionary(g => g.Key, g => Math.Round(g.Average(kv => kv.Value), 4));
        return byPair;
    }
}

// ── View models ──────────────────────────────────────────────────

public class ProspectusSession
{
    public string SessionId { get; set; } = "";
    public List<ProspectusRow> Rows { get; set; } = new();
    public decimal TotalProfitToday { get; set; }
    public decimal TotalRiskToday { get; set; }
    public decimal TotalProfitAvg { get; set; }
    public decimal TotalRiskAvg { get; set; }
}

public class ProspectusRow
{
    public string SetupLabel { get; set; } = "";
    public string StrategyType { get; set; } = "";
    public string Ticker { get; set; } = "";
    public decimal PointValue { get; set; }
    public int Contracts { get; set; }
    public int PartialCts { get; set; }
    public int RemainCts { get; set; }
    public bool UsePartial { get; set; }
    public decimal TargetPct { get; set; }
    public decimal PartialPct { get; set; }
    public decimal StopPct { get; set; }

    // Today's ORB
    public decimal OrbRange { get; set; }
    public decimal Tgt1Usd { get; set; }
    public decimal Tgt2Usd { get; set; }
    public decimal ProfitUsd { get; set; }
    public decimal RiskUsd { get; set; }
    public decimal Rr { get; set; }

    // Monthly average
    public decimal AvgOrbRange { get; set; }
    public decimal AvgTgt1Usd { get; set; }
    public decimal AvgTgt2Usd { get; set; }
    public decimal AvgProfitUsd { get; set; }
    public decimal AvgRiskUsd { get; set; }
    public decimal AvgRr { get; set; }
}
