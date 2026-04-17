namespace CRV.Web.Pages.Dashboard;

using CRV.Core.Models;
using CRV.Live;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class ProspectusModel : PageModel
{
    private readonly StrategyConfigService _cfgSvc;
    private readonly LiveEngineOrchestrator _orchestrator;

    public ProspectusModel(StrategyConfigService cfgSvc, LiveEngineOrchestrator orchestrator)
    {
        _cfgSvc = cfgSvc;
        _orchestrator = orchestrator;
    }

    // ── View data ────────────────────────────────────────────────
    public List<ProspectusSession> Sessions { get; set; } = new();
    public decimal TotalProfitToday { get; set; }
    public decimal TotalRiskToday { get; set; }
    public decimal TotalProfitAvg { get; set; }
    public decimal TotalRiskAvg { get; set; }

    public void OnGet()
    {
        var cfg = _cfgSvc.Current;
        var setups = cfg.ToSetupConfigs();
        var snapshot = _orchestrator.LastSnapshot;

        // Build monthly average ORB ranges from orb_cache.json
        var monthlyAvgOrb = ComputeMonthlyAverageOrb();

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

                // Today's ORB from live engine snapshot
                decimal todayOrb = 0;
                if (snapshot?.GroupSnapshots != null)
                {
                    // Try exact ticker match, then normalized
                    var gs = snapshot.GroupSnapshots.Values.FirstOrDefault(g =>
                        FuturesSymbol.Normalize(g.Ticker) == normTicker);
                    if (gs != null && gs.OrbRange > 0)
                        todayOrb = gs.OrbRange;
                }

                // Monthly average ORB
                decimal avgOrb = monthlyAvgOrb.TryGetValue((normTicker, sessionId), out var avg) ? avg
                    : monthlyAvgOrb.TryGetValue((normTicker, ""), out var avgLegacy) ? avgLegacy : 0;

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

                // Compute P&L for a given ORB range
                ProspectusRow MakeRow(decimal orbRange)
                {
                    var targetDist = orbRange * targetPct;
                    var partialDist = targetDist * partialPct;
                    var stopDist = orbRange * stopPct;

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

    // ── Monthly average ORB from orb_cache.json ──────────────────
    private Dictionary<(string ticker, string sessionId), decimal> ComputeMonthlyAverageOrb()
    {
        var result = new Dictionary<(string, string), decimal>();
        try
        {
            var cacheFile = "orb_cache.json";
            if (!System.IO.File.Exists(cacheFile)) return result;
            var json = System.IO.File.ReadAllText(cacheFile);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<OrbStateCache>>(json) ?? new();

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
