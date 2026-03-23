using CRV.Backtest.Engine;
using CRV.Core.Models;

namespace CRV.Backtest.Results;

public class BacktestResult
{
    public StrategyConfig      Config      { get; set; } = new();
    public BacktestConfig      BtConfig    { get; set; } = new();
    public List<TradeRecord>   Trades      { get; set; } = new();
    public PerformanceMetrics  Total       { get; set; } = new();
    public PerformanceMetrics  SetupA      { get; set; } = new();
    public PerformanceMetrics  SetupB      { get; set; } = new();
    public PerformanceMetrics  SetupC      { get; set; } = new();
    public PerformanceMetrics  SetupD      { get; set; } = new();
    public PerformanceMetrics  SetupF      { get; set; } = new();
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

    // E = (WinRate% × AvgWin) − (LossRate% × AvgLoss)
    // AvgLoss is negative, so subtracting it adds the loss contribution.
    public decimal Expectancy => TotalTrades > 0
        ? (WinRate / 100m) * AvgWin - ((100m - WinRate) / 100m) * AvgLoss
        : 0;
}

public record EquityPoint(DateTime Time, decimal Equity, decimal TradePnl);

public static class BacktestResultCalculator
{
    public static BacktestResult Calculate(List<TradeRecord> trades, StrategyConfig cfg, BacktestConfig btCfg)
    {
        return new BacktestResult
        {
            Config      = cfg,
            BtConfig    = btCfg,
            Trades      = trades,
            Total       = Calc(trades, cfg),
            SetupA      = Calc(trades.Where(t => t.Setup == SetupId.A).ToList(), cfg),
            SetupB      = Calc(trades.Where(t => t.Setup == SetupId.B).ToList(), cfg),
            SetupC      = Calc(trades.Where(t => t.Setup == SetupId.C).ToList(), cfg),
            SetupD      = Calc(trades.Where(t => t.Setup == SetupId.D).ToList(), cfg),
            SetupF      = Calc(trades.Where(t => t.Setup == SetupId.F).ToList(), cfg),
            EquityCurve = BuildCurve(trades)
        };
    }

    private static PerformanceMetrics Calc(List<TradeRecord> trades, StrategyConfig cfg)
    {
        if (trades.Count == 0) return new();
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
