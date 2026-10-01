using CRV.Live;
using CRV.Live.Brokers;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;

namespace CRV.Web.Services;

/// <summary>
/// The order-destination account: its positions and working orders, and the manual actions on
/// them. Every call is one of the existing <see cref="ManualBrokerOps"/> functions, chosen by the
/// effective execution broker the same way the Manual and Orders pages choose them.
/// </summary>
public sealed class BrokerAccountService
{
    private readonly StrategyConfigService   _cfgSvc;
    private readonly SchwabAuthService       _schwab;
    private readonly TradeStationAuthService _ts;
    private readonly TradovateAuthService    _tv;
    private readonly MockBrokerExecutor      _mockExec;
    private readonly LiveEngineOrchestrator  _orchestrator;
    private readonly IConfiguration          _config;

    public BrokerAccountService(StrategyConfigService cfgSvc, SchwabAuthService schwab, TradeStationAuthService ts,
        TradovateAuthService tv, MockBrokerExecutor mockExec, LiveEngineOrchestrator orchestrator, IConfiguration config)
    {
        _cfgSvc = cfgSvc; _schwab = schwab; _ts = ts; _tv = tv; _mockExec = mockExec; _orchestrator = orchestrator; _config = config;
    }

    public string Broker => _cfgSvc.Current.EffectiveExecBroker;
    public AccountMode Account => AccountMode.For(Broker, _tv.AuthBaseUrl);

    /// <summary>Brokers whose account can be read and acted on directly. Mock and Replay positions exist only as engine groups.</summary>
    public bool HasBrokerAccount => Broker is "Schwab" or "TradeStation" or "Tradovate";

    private string AccountId
    {
        get
        {
            var raw = Broker switch
            {
                "TradeStation" => _config["TradeStation:AccountId"],
                "Schwab"       => _config["Schwab:AccountId"],
                "Tradovate"    => _config["Tradovate:AccountId"],
                _              => null,
            };
            return !string.IsNullOrEmpty(raw) ? raw : _cfgSvc.Current.AccountId;
        }
    }

    public Task<List<PositionView>> GetPositionsAsync() => Broker switch
    {
        "TradeStation" => ManualBrokerOps.GetPositionsTradeStationAsync(_ts, AccountId),
        "Schwab"       => ManualBrokerOps.GetPositionsSchwabAsync(_schwab, AccountId),
        "Tradovate"    => ManualBrokerOps.GetPositionsTradovateAsync(_tv, AccountId),
        _              => Task.FromResult(ManualBrokerOps.GetMockPositionsFromGroups(_orchestrator.GetActiveGroups())),
    };

    /// <summary>Orders from the last week, so resting GTC orders placed on earlier days are included.</summary>
    public async Task<List<OrderView>> GetOrdersAsync()
    {
        var from = DateTime.Today.AddDays(-7);
        var to   = DateTime.Today;
        return Broker switch
        {
            "TradeStation" => await ManualBrokerOps.GetOrdersTradeStationAsync(_ts, AccountId, "ALL", from, to),
            "Schwab"       => await ManualBrokerOps.GetOrdersSchwabAsync(_schwab, AccountId, "ALL", from, to),
            "Tradovate"    => await ManualBrokerOps.GetOrdersTradovateAsync(_tv, AccountId, "ALL", from, to),
            _              => new List<OrderView>(),
        };
    }

    public Task<string> CancelOrderAsync(string orderId) => Broker switch
    {
        "TradeStation" => ManualBrokerOps.CancelOrderTradeStationAsync(_ts, orderId),
        "Schwab"       => ManualBrokerOps.CancelOrderSchwabAsync(_schwab, AccountId, orderId),
        "Tradovate"    => ManualBrokerOps.CancelOrderTradovateAsync(_tv, orderId),
        _              => ManualBrokerOps.CancelOrderMockAsync(orderId, _mockExec),
    };

    /// <summary>Cancel every working order on <paramref name="symbol"/> (broker format, as the broker returned it).</summary>
    public Task<List<string>> CancelAllAsync(string symbol) => Broker switch
    {
        "TradeStation" => ManualBrokerOps.CancelAllTradeStationAsync(_ts, AccountId, symbol),
        "Schwab"       => ManualBrokerOps.CancelAllSchwabAsync(_schwab, AccountId, symbol),
        "Tradovate"    => ManualBrokerOps.CancelAllTradovateAsync(_tv, AccountId, symbol),
        _              => ManualBrokerOps.CancelAllMockAsync(symbol),
    };

    /// <summary>Cancel the instrument's working orders, then close the position at market — the Manual page's row Flat.</summary>
    public async Task<List<string>> ClosePositionAsync(string symbol, int contracts, bool isLong)
    {
        var done = await CancelAllAsync(symbol);
        done.Add(Broker switch
        {
            "TradeStation" => await ManualBrokerOps.FlatAtMarketTradeStationAsync(_ts, AccountId, symbol, contracts, isLong),
            "Schwab"       => await ManualBrokerOps.FlatAtMarketSchwabAsync(_schwab, AccountId, symbol, contracts, isLong),
            "Tradovate"    => await ManualBrokerOps.FlatAtMarketTradovateAsync(_tv, AccountId, symbol, contracts, isLong),
            _              => await ManualBrokerOps.FlatAtMarketMockAsync(symbol, contracts, isLong),
        });
        return done;
    }
}
