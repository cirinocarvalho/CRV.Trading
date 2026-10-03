using System.Text.Json;
using CRV.Backtest.Engine;
using CRV.Backtest.Results;
using CRV.Core.Data;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CRV.Web.A11yTests;

/// <summary>
/// Gives every page real content to scan: a Mock-broker config with one strategy of each
/// type, closed live and paper trades over two days, and one saved backtest run.
/// </summary>
public static class A11ySeed
{
    public const string RetestId = "a11y-retest";
    /// <summary>A stored entry of the retired EMA21 type (4), switched on, as old live configs hold it.</summary>
    public const string RetiredId = "a11y-ema21";
    public const string DollarsId  = "a11y-dollars";
    public const string GuardOffId = "a11y-guard-off";

    public static string OrbBasketJson { get; } = BasketCodec.Serialize(new[]
    {
        Entry("a11y-pullback",       StrategyType.Pullback,       "Pullback [MNQ]"),
        Entry(RetestId,              StrategyType.Retest,         "Retest [MNQ]"),
        Entry("a11y-orbfakeout",     StrategyType.OrbFakeout,     "ORB fakeout [MES]"),
        Entry("a11y-sessionfakeout", StrategyType.SessionFakeout, "Session fakeout [MES]"),
        DollarEntry(DollarsId,  StrategyType.OrbFakeout, "ORB fakeout $ [MES]", enabled: true,
                    TargetDollarsBasis.WholePosition, 150m, minRr: 1.5m, MinRrAction.Skip),
        DollarEntry(GuardOffId, StrategyType.Pullback,   "Pullback $ [MNQ]",    enabled: false,
                    TargetDollarsBasis.PerContract,   400m, minRr: 2.5m, MinRrAction.RaiseTarget),
    });

    private static readonly string RetiredBasketJson = BasketCodec.Serialize(new[]
    {
        Entry(RetiredId, SetupValidation.RetiredEma21, "EMA21 [MNQ]"),
    });

    public static void Apply(IServiceProvider services)
    {
        var configs = services.GetRequiredService<StrategyConfigService>();
        var cfg = configs.Current;
        cfg.Broker          = "Mock";
        cfg.ExecBroker      = null;
        cfg.BasketJson      = OrbBasketJson;
        cfg.EmaBasketJson = RetiredBasketJson;
        configs.Update(cfg);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var yesterday = DateTime.UtcNow.Date.AddDays(-1);
        var trades = new List<TradeRecord>
        {
            Trade("live",  yesterday.AddHours(14),             Direction.Long,  21000m, 20980m, 21040m, ExitReason.Target),
            Trade("live",  yesterday.AddHours(15),             Direction.Short, 21050m, 21070m, 21070m, ExitReason.Stop),
            Trade("live",  yesterday.AddDays(-1).AddHours(14), Direction.Long,  20900m, 20880m, 20930m, ExitReason.SessionEnd),
            Trade("paper", yesterday.AddHours(14),             Direction.Long,  21000m, 20980m, 21040m, ExitReason.Target),
            Trade("paper", yesterday.AddDays(-1).AddHours(15), Direction.Short, 20950m, 20970m, 20910m, ExitReason.Target),
        };
        db.Trades.AddRange(trades);

        var btCfg = new BacktestConfig { From = yesterday.AddDays(-1), To = yesterday.AddDays(1) };
        var result = BacktestResultCalculator.Calculate(trades.Where(t => t.Source == "live").ToList(), cfg, btCfg);
        db.BacktestRuns.Add(new BacktestRunRow
        {
            Ticker       = "/MNQZ26",
            ConfigName   = "a11y",
            From         = btCfg.From,
            To           = btCfg.To,
            DataSource   = "csv",
            FillMode     = btCfg.FillMode.ToString(),
            TotalTrades  = result.Total.TotalTrades,
            NetPnl       = result.Total.NetPnl,
            WinRate      = result.Total.WinRate,
            ProfitFactor = result.Total.ProfitFactor,
            MaxDrawdown  = result.Total.MaxDrawdown,
            RunAt        = DateTime.UtcNow,
            ResultJson   = JsonSerializer.Serialize(result),
        });

        db.SaveChanges();
    }

    /// <summary>Replaces the ORB basket; an empty string makes the Risk page show Setups A–D.</summary>
    public static void SetOrbBasket(IServiceProvider services, string json)
    {
        var configs = services.GetRequiredService<StrategyConfigService>();
        var cfg = configs.Current;
        cfg.BasketJson = json;
        configs.Update(cfg);
    }

    private static BasketEntry Entry(string id, StrategyType type, string label) => new()
    {
        Id           = id,
        Enabled      = true,
        Label        = label,
        StrategyType = type,
        Ticker       = label.Contains("MES") ? "/MESZ26" : "/MNQZ26",
        PointValue   = label.Contains("MES") ? 5m : 2m,
        TickSize     = 0.25m,
    };

    /// <summary>A dollar-target entry with the reward / risk guard off.</summary>
    private static BasketEntry DollarEntry(string id, StrategyType type, string label, bool enabled,
        TargetDollarsBasis basis, decimal dollars, decimal minRr, MinRrAction action)
    {
        var e = Entry(id, type, label);
        e.Enabled = enabled;
        e.Config = new StrategySetupConfig
        {
            TargetMode = TargetMode.Dollars, TargetDollars = dollars, TargetDollarsBasis = basis,
            EnforceMinRr = false, MinRr = minRr, MinRrAction = action,
        };
        return e;
    }

    private static TradeRecord Trade(string source, DateTime enteredAt, Direction dir,
        decimal entry, decimal stop, decimal exit, ExitReason reason)
    {
        var points = dir == Direction.Long ? exit - entry : entry - exit;
        var risk   = Math.Abs(entry - stop);
        var gross  = points * 2m;
        return new TradeRecord
        {
            SessionId   = "NY",
            Source      = source,
            Setup       = SetupId.B,
            SetupLabel  = "Retest [MNQ]",
            Direction   = dir,
            Ticker      = "/MNQZ26",
            Contracts   = 1,
            Entry       = entry,
            InitialStop = stop,
            Target      = dir == Direction.Long ? entry + 2 * risk : entry - 2 * risk,
            Exit        = exit,
            ExitReason  = reason,
            GrossPnl    = gross,
            Commission  = 1.24m,
            NetPnl      = gross - 1.24m,
            RMultiple   = points / risk,
            EnteredAt   = enteredAt,
            ExitedAt    = enteredAt.AddMinutes(35),
        };
    }
}
