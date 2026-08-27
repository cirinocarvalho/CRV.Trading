using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;

namespace CRV.Web.Services;

/// <summary>
/// Keeps broker refresh tokens alive while the app is running.
/// <para>Schwab issues a NEW refresh token on every grant and expires the old one after
/// seven days of disuse, so the clock only resets when something actually calls the
/// token endpoint. A page opened a couple of times a week would otherwise find its
/// authorization dead and send the user back through the OAuth flow.</para>
/// <para>This periodically requests an access token, which triggers a refresh once the
/// short-lived access token has aged out, rotating and re-saving the refresh token.
/// It cannot help while the app is stopped — nothing can — but it removes the case
/// where the app is up and the token still lapses.</para>
/// </summary>
public class BrokerTokenKeepAlive : BackgroundService
{
    private readonly SchwabAuthService       _schwab;
    private readonly TradeStationAuthService _tradeStation;
    private readonly ILogger<BrokerTokenKeepAlive> _log;

    /// <summary>Comfortably inside the seven-day window, and past the ~30 min access-token life.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    public BrokerTokenKeepAlive(
        SchwabAuthService schwab,
        TradeStationAuthService tradeStation,
        ILogger<BrokerTokenKeepAlive> log)
    {
        _schwab       = schwab;
        _tradeStation = tradeStation;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let startup settle before the first touch.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            await TouchAsync("Schwab",       () => _schwab.GetAccessTokenAsync());
            await TouchAsync("TradeStation", () => _tradeStation.GetAccessTokenAsync());

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// A failure here is expected and benign — the user may simply never have connected
    /// this broker. It must never take the host down.
    /// </summary>
    private async Task TouchAsync(string broker, Func<Task<string>> getToken)
    {
        try
        {
            await getToken();
            _log.LogDebug("{Broker}: refresh token renewed", broker);
        }
        catch (Exception ex)
        {
            _log.LogInformation("{Broker}: token keep-alive skipped — {Reason}", broker, ex.Message);
        }
    }
}
