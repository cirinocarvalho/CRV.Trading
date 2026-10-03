using CRV.Backtest.Engine;
using CRV.Core.Models;

namespace CRV.Backtest.Results;

public class BacktestResult
{
    public StrategyConfig      Config      { get; set; } = new();
    public BacktestConfig      BtConfig    { get; set; } = new();
    public List<TradeRecord>   Trades      { get; set; } = new();
    public PerformanceMetrics  Total       { get; set; } = new();

    /// <summary>Per-setup metrics keyed by string Id (e.g. "A", "B", "b-mnq-1").</summary>
    public Dictionary<string, PerformanceMetrics> PerSetup { get; set; } = new();

    /// <summary>
    /// Signals the risk budget refused at even one contract. Not trades, so not in
    /// <see cref="Trades"/> — but a run that refused a fifth of its signals measured
    /// a different sample from one that took them, and has to say so.
    /// </summary>
    public List<SizeRefusal> SizeRefusals { get; set; } = new();

    /// <summary>Signals skipped because their reward / risk was below the strategy's minimum.</summary>
    public List<SizeRefusal> MinRrSkips { get; set; } = new();

    /// <summary>Plain-language notes about the data the run used, e.g. an expired contract
    /// replaced by the next one. Shown with the result.</summary>
    public List<string> DataNotes { get; set; } = new();

    // Legacy accessors for backward compatibility with existing pages/tests
    public PerformanceMetrics  SetupA      => PerSetup.GetValueOrDefault("A", new());
    public PerformanceMetrics  SetupB      => PerSetup.GetValueOrDefault("B", new());
    public PerformanceMetrics  SetupC      => PerSetup.GetValueOrDefault("C", new());
    public PerformanceMetrics  SetupD      => PerSetup.GetValueOrDefault("D", new());
    public PerformanceMetrics  SetupF      => PerSetup.GetValueOrDefault("F", new());

    public List<EquityPoint>   EquityCurve { get; set; } = new();
    public DateTime            GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class PerformanceMetrics
{
    public int      TotalTrades     { get; set; }
    public int      Wins            { get; set; }
    public int      Losses          { get; set; }
    public decimal  WinRate         { get; set; }
    public decimal  NetPnl          { get; set; }
    public decimal  GrossPnl        { get; set; }
    public decimal  TotalCommission { get; set; }
    public decimal  ProfitFactor    { get; set; }
    public decimal  AvgWin          { get; set; }
    public decimal  AvgLoss         { get; set; }
    public decimal  AvgR            { get; set; }
    public decimal  MaxDrawdown     { get; set; }
    public int      MaxConsecWins   { get; set; }
    public int      MaxConsecLosses { get; set; }
    public TimeSpan AvgDuration     { get; set; }
    public int      LongTrades      { get; set; }
    public int      ShortTrades     { get; set; }
    public int      TargetExits     { get; set; }
    public int      StopExits       { get; set; }
    public int      SessionEndExits { get; set; }

    /// <summary>Signals refused by the risk budget. Zero unless a budget is set and bit.</summary>
    public int      SizeRefusals    { get; set; }

    /// <summary>Signals skipped by the reward / risk guard.</summary>
    public int      MinRrSkips      { get; set; }

    /// <summary>The setup's reward / risk guard in this run; null for the totals.</summary>
    public RrGuardState? RrGuard    { get; set; }

    // E = (WinRate% × AvgWin) + (LossRate% × AvgLoss)
    // AvgLoss is already negative, so adding it subtracts the loss contribution.
    public decimal Expectancy => TotalTrades > 0
        ? (WinRate / 100m) * AvgWin + ((100m - WinRate) / 100m) * AvgLoss
        : 0;
}

public record EquityPoint(DateTime Time, decimal Equity, decimal TradePnl);

/// <summary>Whether a setup enforced its minimum reward / risk in a run, and how.</summary>
public sealed record RrGuardState(bool Enforced, decimal MinRr, MinRrAction Action);

public static class BacktestResultCalculator
{
    public static BacktestResult Calculate(List<TradeRecord> trades, StrategyConfig cfg, BacktestConfig btCfg,
        List<SizeRefusal>? refusals = null, bool recordRrGuard = false)
    {
        refusals ??= new();
        var sizeRefusals = refusals.Where(r => r.Reason == RefusalReason.Size).ToList();
        var minRrSkips   = refusals.Where(r => r.Reason == RefusalReason.MinRr).ToList();

        // Group by SetupLabel (string Id), falling back to Setup enum name for legacy trades
        static string LabelOf(TradeRecord t) =>
            !string.IsNullOrEmpty(t.SetupLabel) ? t.SetupLabel : t.Setup.ToString();

        var refusedBySetup = sizeRefusals.GroupBy(r => r.SetupLabel).ToDictionary(g => g.Key, g => g.Count());
        var skippedBySetup = minRrSkips.GroupBy(r => r.SetupLabel).ToDictionary(g => g.Key, g => g.Count());
        // Only a backtest knows the config its trades ran under; stored live and paper trades may have run under another.
        var guards = !recordRrGuard ? new Dictionary<string, RrGuardState>() : cfg.ToSetupConfigs().GroupBy(s => s.Id)
            .ToDictionary(g => g.Key, g => new RrGuardState(g.First().EnforceMinRr, g.First().MinRr, g.First().MinRrAction));

        // A setup that was refused or skipped every time it fired has no trades and still needs a row.
        var labels = trades.Select(LabelOf).Concat(refusedBySetup.Keys).Concat(skippedBySetup.Keys).Distinct();
        var perSetup = labels.ToDictionary(
            label => label,
            label =>
            {
                var m = Calc(trades.Where(t => LabelOf(t) == label).ToList(), cfg,
                             refusedBySetup.GetValueOrDefault(label), skippedBySetup.GetValueOrDefault(label));
                m.RrGuard = guards.GetValueOrDefault(label);
                return m;
            });

        return new BacktestResult
        {
            Config       = cfg,
            BtConfig     = btCfg,
            Trades       = trades,
            Total        = Calc(trades, cfg, sizeRefusals.Count, minRrSkips.Count),
            PerSetup     = perSetup,
            SizeRefusals = sizeRefusals,
            MinRrSkips   = minRrSkips,
            EquityCurve  = BuildCurve(trades)
        };
    }

    private static PerformanceMetrics Calc(List<TradeRecord> trades, StrategyConfig cfg, int sizeRefusals = 0, int minRrSkips = 0)
    {
        if (trades.Count == 0) return new() { SizeRefusals = sizeRefusals, MinRrSkips = minRrSkips };
        var wins   = trades.Where(t => t.IsWin).ToList();
        var losses = trades.Where(t => !t.IsWin).ToList();

        decimal grossW = wins.Sum(t => t.NetPnl);
        decimal grossL = Math.Abs(losses.Sum(t => t.NetPnl));

        // Drawdown
        decimal peak = 0, eq = 0, maxDd = 0;
        foreach (var t in trades.OrderBy(t => t.ExitedAt))
        { eq += t.NetPnl; if (eq > peak) peak = eq; var dd = peak - eq; if (dd > maxDd) maxDd = dd; }

        // Consec
        int mW = 0, mL = 0, cW = 0, cL = 0;
        foreach (var t in trades.OrderBy(t => t.ExitedAt))
        {
            if (t.IsWin) { cW++; cL = 0; mW = Math.Max(mW, cW); }
            else         { cL++; cW = 0; mL = Math.Max(mL, cL); }
        }

        return new PerformanceMetrics
        {
            TotalTrades     = trades.Count,
            Wins            = wins.Count,
            Losses          = losses.Count,
            WinRate         = trades.Count > 0 ? (decimal)wins.Count / trades.Count * 100 : 0,
            NetPnl          = trades.Sum(t => t.NetPnl),
            GrossPnl        = trades.Sum(t => t.GrossPnl),
            TotalCommission = trades.Sum(t => t.Commission),
            ProfitFactor    = grossL > 0 ? Math.Round(grossW / grossL, 2) : 0,
            AvgWin          = wins.Count   > 0 ? wins.Average(t => t.NetPnl)   : 0,
            AvgLoss         = losses.Count > 0 ? losses.Average(t => t.NetPnl) : 0,
            AvgR            = trades.Count > 0 ? (decimal)trades.Average(t => (double)t.RMultiple) : 0,
            MaxDrawdown     = maxDd,
            MaxConsecWins   = mW,
            MaxConsecLosses = mL,
            AvgDuration     = trades.Count > 0 ? TimeSpan.FromTicks((long)trades.Average(t => t.Duration.Ticks)) : TimeSpan.Zero,
            LongTrades      = trades.Count(t => t.EffectiveDirection == Direction.Long),
            ShortTrades     = trades.Count(t => t.EffectiveDirection == Direction.Short),
            TargetExits     = trades.Count(t => t.ExitReason == ExitReason.Target),
            StopExits       = trades.Count(t => t.ExitReason == ExitReason.Stop),
            SessionEndExits = trades.Count(t => t.ExitReason == ExitReason.SessionEnd),
            SizeRefusals    = sizeRefusals,
            MinRrSkips      = minRrSkips,
        };
    }

    private static List<EquityPoint> BuildCurve(List<TradeRecord> trades)
    {
        var curve = new List<EquityPoint>();
        decimal eq = 0;
        foreach (var t in trades.OrderBy(t => t.ExitedAt))
        { eq += t.NetPnl; curve.Add(new(t.ExitedAt, eq, t.NetPnl)); }
        return curve;
    }
}
