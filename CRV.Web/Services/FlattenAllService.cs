using CRV.Live;

namespace CRV.Web.Services;

public sealed record FlattenPreview(string Account, string Broker, bool RealMoney, bool EngineRunning, FlattenPlan Plan, string? Problem);
public sealed record FlattenResult(bool Ok, List<string> Done, List<string> Failed, List<string> LeftAlone);

/// <summary>
/// Flatten all: close every position, cancel every working order, and stop the engine so
/// nothing re-arms. Order of work:
/// <list type="number">
/// <item>Engine-tracked groups through the engine (works for every broker, Mock included).</item>
/// <item>Broker futures positions the engine isn't tracking: cancel that instrument's orders, close at market.</item>
/// <item>Working futures orders on instruments with no position: cancel.</item>
/// <item>Stop the engine.</item>
/// </list>
/// The plan is rebuilt on the server when executed; the client's preview is never trusted.
/// </summary>
public sealed class FlattenAllService
{
    private readonly BrokerAccountService       _account;
    private readonly LiveEngineOrchestrator     _engine;
    private readonly ILogger<FlattenAllService> _log;
    private readonly SemaphoreSlim              _gate = new(1, 1);

    /// <summary>How long to wait for the engine's exits to fill before stopping it.</summary>
    public static TimeSpan ExitConfirmWait { get; set; } = TimeSpan.FromSeconds(10);

    public FlattenAllService(BrokerAccountService account, LiveEngineOrchestrator engine, ILogger<FlattenAllService> log)
    {
        _account = account; _engine = engine; _log = log;
    }

    public async Task<FlattenPreview> PreviewAsync()
    {
        var groups = _engine.GetActiveGroups();
        var positions = new List<PositionView>();
        var orders = new List<OrderView>();
        string? problem = null;

        if (_account.HasBrokerAccount)
        {
            try
            {
                positions = await _account.GetPositionsAsync();
                orders    = await _account.GetOrdersAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Flatten all: could not read the {Broker} account", _account.Broker);
                problem = $"Couldn't read your {_account.Account.BrokerLabel} positions and orders ({ex.Message}). " +
                          "Engine positions are still closed, but check the broker afterwards.";
            }
        }

        var acct = _account.Account;
        return new FlattenPreview(acct.Label, acct.BrokerLabel, acct.IsRealMoney, _engine.IsRunning,
            FlattenPlanner.Build(groups, positions, orders), problem);
    }

    public async Task<FlattenResult> ExecuteAsync(string requestedBy)
    {
        if (!await _gate.WaitAsync(0))
            return new FlattenResult(false, new(), new() { "Flatten all is already running." }, new());

        try
        {
            var preview = await PreviewAsync();
            var plan = preview.Plan;
            var done = new List<string>();
            var failed = new List<string>();
            if (preview.Problem != null) failed.Add(preview.Problem);

            _log.LogWarning("FLATTEN ALL requested by {User} on {Account} {Broker}: {Groups} group(s), {Positions} position(s), {Orders} order instrument(s)",
                requestedBy, preview.Account, preview.Broker, plan.Groups.Count, plan.Positions.Count, plan.OrderSymbols.Count);

            foreach (var g in plan.Groups)
            {
                try
                {
                    var (ok, msg) = await _engine.FlatGroupAsync(g.GroupOrderId);
                    (ok ? done : failed).Add(ok ? msg : $"{g.Setup} {g.Ticker}: {msg}");
                }
                catch (Exception ex) { failed.Add($"{g.Setup} {g.Ticker}: {ex.Message}"); }
            }

            foreach (var p in plan.Positions)
            {
                try { done.AddRange(await _account.ClosePositionAsync(p.Symbol, p.Quantity, p.IsLong)); }
                catch (Exception ex) { failed.Add($"{p.Symbol}: {ex.Message}"); }
            }

            foreach (var s in plan.OrderSymbols)
            {
                try { done.AddRange(await _account.CancelAllAsync(s)); }
                catch (Exception ex) { failed.Add($"Cancel orders on {s}: {ex.Message}"); }
            }

            if (_engine.IsRunning)
            {
                // Give the engine's exits a moment to fill, so they are recorded as trades before
                // the engine stops. Anything a setup opens in the meantime is closed too.
                var handled = plan.Groups.Select(g => g.GroupOrderId).ToHashSet();
                var deadline = DateTime.UtcNow + ExitConfirmWait;
                while (DateTime.UtcNow < deadline)
                {
                    var open = _engine.GetActiveGroups()
                        .Where(g => g.Status is Core.Models.GroupOrderStatus.Pending or Core.Models.GroupOrderStatus.Active or Core.Models.GroupOrderStatus.PartialFilled)
                        .ToList();
                    if (open.Count == 0) break;
                    foreach (var g in open.Where(g => handled.Add(g.GroupOrderId)))
                    {
                        try
                        {
                            var (ok, msg) = await _engine.FlatGroupAsync(g.GroupOrderId);
                            (ok ? done : failed).Add(ok ? "Opened while flattening, closed: " + msg : $"{g.SetupId} {g.Ticker}: {msg}");
                        }
                        catch (Exception ex) { failed.Add($"{g.SetupId} {g.Ticker}: {ex.Message}"); }
                    }
                    await Task.Delay(500);
                }
                if (_engine.GetActiveGroups().Any(g => g.Status is Core.Models.GroupOrderStatus.Active or Core.Models.GroupOrderStatus.PartialFilled))
                    failed.Add("Some exits hadn't confirmed when the engine stopped. Check Positions & orders and the broker.");

                _engine.StopEngine();
                done.Add("Engine stopped. Start it again from the engine bar when you're ready.");
            }

            foreach (var line in done)   _log.LogWarning("FLATTEN ALL done: {Line}", line);
            foreach (var line in failed) _log.LogError("FLATTEN ALL failed: {Line}", line);

            return new FlattenResult(failed.Count == 0, done, failed, plan.LeftAlone.ToList());
        }
        finally
        {
            _gate.Release();
        }
    }
}
