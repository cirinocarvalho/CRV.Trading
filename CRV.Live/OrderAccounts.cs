using CRV.Core.Models;

namespace CRV.Live;

/// <summary>
/// Which broker account the engine uses, so every page shows and queries the same one.
/// The rule: the broker's <c>{Broker}:AccountId</c> app setting, else the saved
/// <see cref="StrategyConfig.AccountId"/>; for orders, a saved
/// <see cref="StrategyConfig.ExecAccountId"/> overrides both.
/// </summary>
public static class OrderAccounts
{
    /// <summary>The account configured in app settings / user-secrets for a broker, or null.</summary>
    /// <param name="setting">Reads a configuration key, e.g. <c>key => configuration[key]</c>.</param>
    public static string? Configured(string? broker, Func<string, string?> setting) => broker switch
    {
        "TradeStation"                   => setting("TradeStation:AccountId"),
        "Schwab"                         => setting("Schwab:AccountId"),
        "Tradovate" or "TradovateReplay" => setting("Tradovate:AccountId"),
        _                                => null,
    };

    /// <summary>The account market data is read from.</summary>
    public static string Data(StrategyConfig cfg, Func<string, string?> setting) =>
        Configured(cfg.Broker, setting) ?? cfg.AccountId;

    /// <summary>The account orders are sent to.</summary>
    public static string Exec(StrategyConfig cfg, Func<string, string?> setting)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ExecAccountId)) return cfg.ExecAccountId;
        return Configured(cfg.EffectiveExecBroker, setting) ?? Data(cfg, setting);
    }
}
