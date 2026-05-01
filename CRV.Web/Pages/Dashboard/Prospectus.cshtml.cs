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
                // (per FakeoutReferenceSession), not the ORB. Use the live-snapshot range
                // when available, falling back to back-computed range from this setup's
                // own trades — which gives the exact range the engine sized against.
                // Monthly avg comes from this setup's trades over the current month.
                if (setup.StrategyType == StrategyType.SessionFakeout)
                {
                    var key = (normTicker, sessionId, setup.Id);
                    if (IsLive)
                    {
                        decimal sfRange = ResolveSessionFakeoutRange(setup, sessionId, normTicker, snapshot);
                        // Today's reference range from live engine state. If it hasn't formed
                        // yet (e.g. viewing during Asia before Asia closes), the snapshot
                        // returns 0 — leave todayOrb at 0 so the row reads "—" rather than
                        // showing a misleading ORB-based number.
                        todayOrb = sfRange;
                    }
                    else
                    {
                        // Historical: back-compute from this setup's own trades that day.
                        todayOrb = selectedDatePerSetup.TryGetValue(key, out var sfHist) ? sfHist : 0m;
                    }
                    // Monthly avg: this setup's trades over the current calendar month.
                    avgOrb = monthlyPerSetup.TryGetValue(key, out var sfAvg) ? sfAvg : 0m;
                }

                // Per-setup config values
                var stopPct = setup.StopPct;
                var targetPct = setup.TargetPct / 100m;   // int → decimal multiplier
                var partialPct = setup.PartialPct / 100m; // fraction of targetDist
                var contracts = setup.Contracts;
                var usePartial = setup.UsePartial;
                var partialCts = usePartial
                    ? (setup.PartialCts > 0 ? setup.PartialCts : contracts / 2) : 0;
                if (partialCts >= contracts) partialCts = contracts - 1;
                var remainCts = contracts - partialCts;

                // EntryTickOffset shifts the entry price after sl/tp/pp are computed
                // (see e.g. RetestStrategy.cs:507-515). Net effect on a positive offset:
                // stop distance grows by offset, target/partial distances shrink by offset.
                // Negative offsets reverse it. Mirror that here so the dollar columns
                // reflect what the engine actually sizes against.
                var entryOffsetPts = setup.EntryTickOffset * tickSize;

                // Compute P&L for a given ORB range
                ProspectusRow MakeRow(decimal orbRange)
                {
                    var targetDist = Math.Max(0m, orbRange * targetPct - entryOffsetPts);
                    var partialDist = Math.Max(0m, orbRange * targetPct * partialPct - entryOffsetPts);
                    var stopDist = Math.Max(0m, orbRange * stopPct + entryOffsetPts);

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
                    Contracts = contracts,
                    PartialCts = partialCts,
                    RemainCts = remainCts,
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
