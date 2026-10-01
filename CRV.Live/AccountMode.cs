namespace CRV.Live;

public enum AccountKind { Live, Demo, Paper, Replay }

/// <summary>Where orders actually go, as shown on the engine bar and in confirmations.</summary>
public sealed record AccountMode(AccountKind Kind, string Broker)
{
    public bool IsRealMoney => Kind == AccountKind.Live;

    public string Label => Kind switch
    {
        AccountKind.Live   => "LIVE",
        AccountKind.Demo   => "DEMO",
        AccountKind.Paper  => "PAPER",
        _                  => "REPLAY",
    };

    public string BrokerLabel => Broker switch
    {
        "Mock"            => "Mock",
        "TradovateReplay" => "Tradovate Replay",
        _                 => Broker,
    };

    /// <param name="execBroker">The effective execution broker (<c>StrategyConfig.EffectiveExecBroker</c>).</param>
    /// <param name="tradovateAuthBaseUrl">Configured Tradovate REST base URL; a <c>demo.</c> host is a simulated account.</param>
    public static AccountMode For(string? execBroker, string? tradovateAuthBaseUrl)
    {
        var broker = string.IsNullOrWhiteSpace(execBroker) ? "Mock" : execBroker.Trim();
        var kind = broker switch
        {
            "Mock"            => AccountKind.Paper,
            "TradovateReplay" => AccountKind.Replay,
            "Tradovate" when IsDemoHost(tradovateAuthBaseUrl) => AccountKind.Demo,
            _                 => AccountKind.Live,
        };
        return new AccountMode(kind, broker);
    }

    private static bool IsDemoHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Host.StartsWith("demo.", StringComparison.OrdinalIgnoreCase);
}
