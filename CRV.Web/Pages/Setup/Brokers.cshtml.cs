using System.Net.Http.Headers;
using System.Text.Json;
using CRV.Core.Models;
using CRV.Live;
using CRV.Live.Brokers.Schwab;
using CRV.Live.Brokers.TradeStation;
using CRV.Live.Brokers.Tradovate;
using CRV.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CRV.Web.Pages.Setup;

/// <summary>
/// Broker connections plus where data comes from and where orders go.
/// Saving writes only the fields this page shows; everything else keeps its value.
/// </summary>
public class BrokersModel : PageModel
{
    public static readonly (string Value, string Label)[] BrokerChoices =
    {
        ("Schwab", "Schwab"), ("TradeStation", "TradeStation"), ("Tradovate", "Tradovate"),
        ("TradovateReplay", "Tradovate Replay"), ("Mock", "Mock (paper)"),
    };

    private readonly StrategyConfigService   _cfgSvc;
    private readonly LiveEngineOrchestrator  _engine;
    private readonly SchwabAuthService       _schwab;
    private readonly TradeStationAuthService _ts;
    private readonly TradovateAuthService    _tv;
    private readonly IHttpClientFactory      _http;
    private readonly IConfiguration          _config;
    private readonly ILogger<BrokersModel>   _log;

    public BrokersModel(StrategyConfigService cfgSvc, LiveEngineOrchestrator engine,
                        SchwabAuthService schwab, TradeStationAuthService ts, TradovateAuthService tv,
                        IHttpClientFactory http, IConfiguration config, ILogger<BrokersModel> log)
    {
        _cfgSvc = cfgSvc; _engine = engine; _schwab = schwab; _ts = ts; _tv = tv;
        _http = http; _config = config; _log = log;
    }

    public StrategyConfig Config { get; private set; } = new();
    public AccountMode Mode => AccountMode.For(Config.EffectiveExecBroker, _tv.AuthBaseUrl);
    public bool TradovateIsDemo => AccountMode.For("Tradovate", _tv.AuthBaseUrl).Kind == AccountKind.Demo;
    public bool EngineRunning => _engine.IsRunning;

    public bool SchwabConnected => _schwab.IsAuthenticated;
    public bool TsConnected     => _ts.IsAuthenticated;
    public bool TvConnected     => _tv.IsAuthenticated;

    public string? Message { get; private set; }
    public string? Error   { get; private set; }
    public List<string> Warnings { get; } = new();

    /// <summary>Schwab account numbers and the hash each one is configured by (loaded on request).</summary>
    public List<(string AccountNumber, string Hash, string AccountType)>? SchwabAccounts { get; private set; }

    /// <summary>The account the engine sends orders to (the same rule it uses at start).</summary>
    public string OrderAccount => OrderAccounts.Exec(Config, key => _config[key]);

    /// <summary>The account a broker's card shows: the order account for the broker orders go to,
    /// else the one configured for that broker in app settings.</summary>
    public (string? Id, bool IsOrderAccount) AccountFor(string broker)
    {
        var exec = Config.EffectiveExecBroker == "TradovateReplay" ? "Tradovate" : Config.EffectiveExecBroker;
        return exec == broker
            ? (OrderAccount is { Length: > 0 } a ? a : null, true)
            : (OrderAccounts.Configured(broker, key => _config[key]) is { Length: > 0 } c ? c : null, false);
    }

    public DateTime PreviousTradingDay
    {
        get
        {
            var d = DateTime.Today.AddDays(-1);
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
            return d;
        }
    }

    public async Task OnGetAsync(bool schwabAccounts = false)
    {
        Config = _cfgSvc.Current.Clone();
        Message = TempData["brokers_msg"] as string;
        Error   = TempData["brokers_err"] as string;
        if (TempData["brokers_warnings"] is string w) Warnings.AddRange(w.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (schwabAccounts && _schwab.IsAuthenticated) SchwabAccounts = await LoadSchwabAccountsAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var before = _cfgSvc.Current;
        Config = before.Clone();
        var ok = await TryUpdateModelAsync(Config, "Config",
            c => c.Broker, c => c.ExecBroker, c => c.CommissionPerSide,
            c => c.ReplayDate, c => c.ReplaySpeed, c => c.ReplayBalance, c => c.SaveReplayTrades);
        if (!ok || !ModelState.IsValid)
        {
            TempData["brokers_err"] = "Not saved: " + string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToPage();
        }
        if (string.IsNullOrWhiteSpace(Config.ExecBroker)) Config.ExecBroker = null;
        if (!BrokerChoices.Any(b => b.Value == Config.Broker) ||
            (Config.ExecBroker != null && !BrokerChoices.Any(b => b.Value == Config.ExecBroker)))
        {
            TempData["brokers_err"] = "Not saved: choose a broker from the list.";
            return RedirectToPage();
        }

        _log.LogInformation("Brokers saved by {Who}: data {D0}→{D1}, orders {E0}→{E1}, commission {C0}→{C1}",
            Who, before.Broker, Config.Broker, before.EffectiveExecBroker, Config.EffectiveExecBroker,
            before.CommissionPerSide, Config.CommissionPerSide);
        _cfgSvc.Update(Config);
        _engine.ApplyRuntimeSettings(Config);

        var brokerChanged = before.Broker != Config.Broker || before.EffectiveExecBroker != Config.EffectiveExecBroker;
        TempData["brokers_msg"] = brokerChanged && _engine.IsRunning
            ? "Saved. The running engine keeps its current brokers until you stop and start it."
            : "Saved.";
        var problems = Config.Validate();
        if (problems.Count > 0) TempData["brokers_warnings"] = string.Join("\n", problems);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTradovateConnectAsync()
    {
        try
        {
            await _tv.AuthenticateAsync();
            TempData["brokers_msg"] = "Connected to Tradovate.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tradovate authentication failed");
            TempData["brokers_err"] = $"Tradovate didn't connect: {ex.Message}";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTradovateRenewAsync()
    {
        try
        {
            await _tv.RenewTokenAsync();
            TempData["brokers_msg"] = "Tradovate login renewed.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tradovate token renewal failed");
            TempData["brokers_err"] = $"Tradovate login wasn't renewed: {ex.Message}";
        }
        return RedirectToPage();
    }

    private string Who => User?.Identity?.Name ?? Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault() ?? "unknown";

    private async Task<List<(string, string, string)>?> LoadSchwabAccountsAsync()
    {
        try
        {
            var token = await _schwab.GetAccessTokenAsync();
            using var http = _http.CreateClient("Schwab");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Account number → hash. Response: [ { "accountNumber": "...", "hashValue": "..." } ]
            var numbersResp = await http.GetAsync($"{_schwab.ApiBaseUrl}/trader/v1/accounts/accountNumbers");
            if (!numbersResp.IsSuccessStatusCode) return null;
            using var numbersDoc = JsonDocument.Parse(await numbersResp.Content.ReadAsStringAsync());
            var hashMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in numbersDoc.RootElement.EnumerateArray())
            {
                var num  = item.TryGetProperty("accountNumber", out var n) ? n.GetString() ?? "" : "";
                var hash = item.TryGetProperty("hashValue",     out var h) ? h.GetString() ?? "" : "";
                if (num != "" && hash != "") hashMap[num] = hash;
            }

            // Account types (MARGIN, CASH, FUTURES, ...).
            var typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var accountsResp = await http.GetAsync($"{_schwab.ApiBaseUrl}/trader/v1/accounts");
            if (accountsResp.IsSuccessStatusCode)
            {
                using var accountsDoc = JsonDocument.Parse(await accountsResp.Content.ReadAsStringAsync());
                foreach (var item in accountsDoc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("securitiesAccount", out var sa)) continue;
                    var num  = sa.TryGetProperty("accountNumber", out var n) ? n.GetString() ?? "" : "";
                    var type = sa.TryGetProperty("type",          out var t) ? t.GetString() ?? "" : "";
                    if (num != "") typeMap[num] = type;
                }
            }

            return hashMap.Select(kv => (kv.Key, kv.Value, typeMap.GetValueOrDefault(kv.Key) ?? "")).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not fetch Schwab account numbers");
            return null;
        }
    }
}
