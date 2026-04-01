namespace CRV.Live.Brokers.Tradovate;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CRV.Core.Data;
using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// IOrderExecutor for Tradovate — places OSO bracket orders via REST.
/// Uses POST /order/placeOSO for entry with embedded stop (bracket2) and target (bracket1).
/// Partial and BE-move operations cancel the relevant leg and place a replacement order.
/// Exit cancels all open legs then sends a market close.
///
/// NOTE: Tradovate uses account "name" (a string like "DEMO123") in order payloads,
/// not an integer account ID. The account name is resolved once and cached via
/// GetAccountAsync() → GET /account/list → first account.
/// </summary>
public class TradovateExecutor : IOrderExecutor, IGroupOrderExecutor
{
    private readonly TradovateAuthService  _auth;
    private readonly StrategyConfig        _cfg;
    private readonly ILogger               _log;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IHttpClientFactory?   _httpFactory;

    // Per-setup order leg tracking — supports all 5 setups (A/B/C/D/F)
    private class SetupState
    {
        public long?      EntryOrderId  { get; set; }
        public long?      StopOrderId   { get; set; }
        public long?      TargetOrderId { get; set; }
        public long?      PartialOrderId { get; set; }
        public string?    Ticker        { get; set; }
        public Direction? Direction     { get; set; }

        public void Clear()
        {
            EntryOrderId = StopOrderId = TargetOrderId = PartialOrderId = null;
            Direction = null;
            Ticker = null;
        }
    }

    private readonly Dictionary<string, SetupState> _states = new();
    private SetupState GetOrCreateState(string id)
    {
        if (!_states.TryGetValue(id, out var s)) { s = new SetupState(); _states[id] = s; }
        return s;
    }

    // Account cache — resolved once from GET /account/list
    private record AccountRef(string Name, long Id);
    private AccountRef? _cachedAccount;

    /// <summary>
    /// Optional WSS event stream for order registration.
    /// Set by the orchestrator after construction so WSS events can route to groups.
    /// </summary>
    public TradovateEventStream? EventStream { get; set; }

    public TradovateExecutor(
        TradovateAuthService auth,
        StrategyConfig cfg,
        ILogger<TradovateExecutor> log,
        IServiceScopeFactory? scopeFactory = null,
        IHttpClientFactory? httpFactory = null)
    {
        _auth         = auth;
        _cfg          = cfg;
        _log          = log;
        _scopeFactory = scopeFactory;
        _httpFactory  = httpFactory;
    }

    private HttpClient CreateClient() => _httpFactory?.CreateClient("Tradovate") ?? new HttpClient();

    // ── IOrderExecutor ────────────────────────────────────────────

    public async Task<decimal?> OnEntrySignalAsync(EntrySignal sig)
    {
        _log.LogInformation("[TV] ENTRY {D} {Q}x {S} @ {E} Stop={St} Tgt={T}",
            sig.Direction, sig.TotalContracts, sig.Setup, sig.Entry, sig.Stop, sig.Tg2Price);

        var rawTicker   = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker;
        var symbol      = FuturesSymbol.ToTradovate(rawTicker);
        var account     = await GetAccountAsync();
        var entryAction = sig.Direction == Direction.Long ? "Buy"  : "Sell";
        var exitAction  = sig.Direction == Direction.Long ? "Sell" : "Buy";

        // POST /order/placeOSO
        // bracket1 = Limit target leg (opposite direction)
        // bracket2 = Stop  stop  leg  (opposite direction)
        bool isLimit = sig.OrderType == "Limit";
        var body = new
        {
            accountSpec = account.Name,
            accountId   = account.Id,
            action      = entryAction,
            symbol      = symbol,
            orderQty    = sig.TotalContracts,
            orderType   = isLimit ? "Limit" : "Market",
            price       = isLimit ? (decimal?)sig.Entry : null,
            isAutomated = true,
            bracket1    = new
            {
                action    = exitAction,
                orderType = "Limit",
                price     = sig.Tg2Price,
                orderQty  = sig.TotalContracts
            },
            bracket2    = new
            {
                action    = exitAction,
                orderType = "Stop",
                stopPrice = sig.Stop,
                orderQty  = sig.TotalContracts
            }
        };

        var (entryId, targetId, stopId) = await PlaceOsoAsync(body);

        // Only commit state if the order was actually placed
        if (entryId is null)
        {
            _log.LogError("[TV] ENTRY OSO failed — no order IDs returned. " +
                          "Engine and broker state may be inconsistent; manual check required.");
            return null;
        }

        // Store direction AFTER confirmed placement
        var state = GetOrCreateState(sig.Setup.ToString());
        state.Direction     = sig.Direction;
        state.Ticker        = rawTicker;
        state.EntryOrderId  = entryId;
        state.TargetOrderId = targetId;
        state.StopOrderId   = stopId;
        _log.LogInformation("[TV] Setup {S} bracket — entry={E} target={T} stop={St}",
            sig.Setup, entryId, targetId, stopId);

        // Poll for fill price feedback
        var fillPrice = await GetFillPriceAsync(entryId.Value);
        if (fillPrice is not null)
            _log.LogInformation("[TV] Entry fill price for {S}: {P}", sig.Setup, fillPrice);
        else
            _log.LogWarning("[TV] Could not retrieve fill price for entry order {Id}", entryId);

        // Discover bracket leg IDs if placeOSO response didn't include them.
        // Tradovate creates bracket legs asynchronously — they may only be
        // discoverable after the entry fills.
        if (state.TargetOrderId is null || state.StopOrderId is null)
        {
            _log.LogInformation("[TV] Bracket leg IDs missing (target={T}, stop={S}) — polling order list to discover",
                state.TargetOrderId, state.StopOrderId);
            var (foundTarget, foundStop) = await FindBracketLegsAsync(entryId.Value, sig.Tg2Price, sig.Stop);
            state.TargetOrderId ??= foundTarget;
            state.StopOrderId   ??= foundStop;
            _log.LogInformation("[TV] Setup {S} bracket legs resolved — target={T} stop={St}",
                sig.Setup, state.TargetOrderId, state.StopOrderId);
        }

        return fillPrice;
    }

    public async Task OnLevelsAdjustedAsync(string setupId, decimal newStop, decimal newTarget, int contracts)
    {
        _log.LogInformation("[TV] LEVELS_ADJUSTED {S} → Stop={St} Target={T} Qty={Q}", setupId, newStop, newTarget, contracts);

        var state = GetOrCreateState(setupId);

        if (state.StopOrderId != null)
        {
            var ok = await ModifyOrderAsync(state.StopOrderId.Value, contracts, stopPrice: newStop);
            if (ok) _log.LogDebug("[TV] Setup {S} stop modified → {P}", setupId, newStop);
        }
        else
            _log.LogWarning("[TV] OnLevelsAdjusted {S}: StopOrderId not set", setupId);

        if (state.TargetOrderId != null)
        {
            var ok = await ModifyOrderAsync(state.TargetOrderId.Value, contracts, limitPrice: newTarget);
            if (ok) _log.LogDebug("[TV] Setup {S} target modified → {P}", setupId, newTarget);
        }
        else
            _log.LogWarning("[TV] OnLevelsAdjusted {S}: TargetOrderId not set", setupId);
    }

    // ── IGroupOrderExecutor ────────────────────────────────────────

    /// <summary>
    /// Place an entry via Tradovate's native bracket strategy (orderStrategyTypeId=2).
    /// Single API call — Tradovate manages the entire bracket lifecycle server-side:
    /// stop auto-cancels targets, targets auto-cancel stop, BE moves, partial fills.
    /// Returns a GroupOrder with all leg IDs populated, or null on failure.
    /// </summary>
    async Task<GroupOrder?> IGroupOrderExecutor.OnEntrySignalAsync(EntrySignal sig)
    {
        var rawTicker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : _cfg.Ticker;
        var symbol = FuturesSymbol.ToTradovate(rawTicker);
        var account = await GetAccountAsync();
        var entryAction = sig.Direction == Direction.Long ? "Buy" : "Sell";
        bool isLimit = sig.OrderType == "Limit";

        var groupId = Guid.NewGuid().ToString("N")[..8];
        var usePartial = sig.UsePartial;
        var partialCts = usePartial
            ? (sig.PartialContracts > 0 ? sig.PartialContracts : sig.TotalContracts / 2)
            : 0;
        var remainCts = sig.TotalContracts - partialCts;

        // Compute signed point offsets from entry price
        // Both profitTarget and stopLoss are directional: + = above entry, − = below entry
        var tg1Offset = sig.Tg1Price - sig.Entry;                  // + for long, − for short
        var tg2Offset = sig.Tg2Price - sig.Entry;                  // + for long, − for short
        var stopOffset = sig.Stop - sig.Entry;                      // − for long (below), + for short (above)
        bool hasAutoTrail = sig.AutoTrailStopLoss.HasValue;
        // When auto-trail is active, skip breakEven — trail replaces it
        var beOffset = (!hasAutoTrail && sig.UseBe) ? Math.Abs(tg1Offset) : 0m;

        // Build bracket array — each bracket gets its own qty, target, and stop
        // Use Dictionary<string,object> for reliable JSON serialization
        var brackets = new List<Dictionary<string, object>>();
        if (usePartial && partialCts > 0)
        {
            // Bracket 1: partial target (Tg1), no BE on this bracket
            brackets.Add(new Dictionary<string, object>
            {
                ["qty"] = partialCts, ["profitTarget"] = tg1Offset,
                ["stopLoss"] = stopOffset, ["trailingStop"] = false
            });
            // Bracket 2: full target (Tg2), with BE after Tg1-level profit
            var b2 = new Dictionary<string, object>
            {
                ["qty"] = remainCts, ["profitTarget"] = tg2Offset,
                ["stopLoss"] = stopOffset, ["trailingStop"] = false
            };
            if (beOffset > 0) b2["breakEven"] = beOffset;
            if (hasAutoTrail)
            {
                var trailTrigger = sig.AutoTrailTrigger ?? Math.Abs(tg1Offset);
                b2["autoTrail"] = new Dictionary<string, object>
                {
                    ["stopLoss"] = sig.AutoTrailStopLoss!.Value,
                    ["trigger"] = trailTrigger,
                    ["freq"] = sig.AutoTrailFreq!.Value
                };
            }
            brackets.Add(b2);
        }
        else
        {
            // Single bracket: full qty, one target + stop
            var singleBracket = new Dictionary<string, object>
            {
                ["qty"] = sig.TotalContracts, ["profitTarget"] = tg2Offset,
                ["stopLoss"] = stopOffset, ["trailingStop"] = false
            };
            if (hasAutoTrail)
            {
                singleBracket["autoTrail"] = new Dictionary<string, object>
                {
                    ["stopLoss"] = sig.AutoTrailStopLoss!.Value,
                    ["trigger"] = sig.AutoTrailTrigger!.Value,
                    ["freq"] = sig.AutoTrailFreq!.Value
                };
            }
            brackets.Add(singleBracket);
        }

        // Build entry params — params must be a JSON string, not a nested object
        // Use Dictionary to avoid System.Text.Json issues with anonymous types cast to object
        var entryVersion = new Dictionary<string, object>
        {
            ["orderQty"] = sig.TotalContracts,
            ["orderType"] = isLimit ? "Limit" : "Market",
            ["timeInForce"] = "GTC"
        };
        if (isLimit) entryVersion["price"] = sig.Entry;

        var paramsObj = new Dictionary<string, object>
        {
            ["entryVersion"] = entryVersion,
            ["brackets"] = brackets
        };
        var paramsJson = JsonSerializer.Serialize(paramsObj);
        _log.LogDebug("[TV-GRP] Bracket params JSON: {Params}", paramsJson);

        var body = new
        {
            accountId = account.Id,
            accountSpec = account.Name,
            symbol,
            orderStrategyTypeId = 2,
            action = entryAction,
            @params = paramsJson
        };

        // Single API call — Tradovate handles the entire bracket lifecycle
        var (strategyId, entryOrderId) = await StartOrderStrategyAsync(body);
        if (strategyId is null)
        {
            _log.LogError("[TV-GRP] startOrderStrategy failed — no strategyId returned (entryOrderId={E})", entryOrderId);
            return null;
        }
        if (entryOrderId is null)
            _log.LogInformation("[TV-GRP] entryOrderId not in initial response — will discover via REST polling");

        // Discover all bracket legs via REST polling (not WSS — unreliable connection)
        // Tradovate computes bracket prices from the fill price using offsets,
        // so we match by strategy ID sequence, action, and order type.
        var exitAction = sig.Direction == Direction.Long ? "Sell" : "Buy";
        var disc = await DiscoverBracketLegsAsync(
            strategyId.Value, symbol, entryAction, exitAction, usePartial);

        // Entry rejected (e.g. market closed) — no trade placed
        if (disc.Entry is not null && disc.Entry.Status is "Rejected" or "Canceled" or "Cancelled")
        {
            _log.LogWarning("[TV-GRP] Entry {E} was {S} — order not placed", disc.Entry.Id, disc.Entry.Status);
            return null;
        }

        // For single bracket, map Tg1 discovery to Tg2
        var tg2Disc = disc.Tg2 ?? (usePartial ? null : disc.Tg1);

        // Register all legs with WSS (best-effort — REST is primary)
        if (disc.Entry is not null) EventStream?.RegisterOrder(disc.Entry.Id, groupId, LegType.Entry);
        if (disc.Tg1 is not null && usePartial) EventStream?.RegisterOrder(disc.Tg1.Id, groupId, LegType.Tg1);
        if (tg2Disc is not null) EventStream?.RegisterOrder(tg2Disc.Id, groupId, LegType.Tg2);
        if (disc.Stop is not null) EventStream?.RegisterOrder(disc.Stop.Id, groupId, LegType.Stop);
        if (disc.Stop2 is not null) EventStream?.RegisterOrder(disc.Stop2.Id, groupId, LegType.Stop);

        var setupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString();
        var buyAction = entryAction == "Buy" ? "BUY" : "SELL";
        var sellAction = entryAction == "Buy" ? "SELL" : "BUY";

        // Helper to map Tradovate status to OrderLegStatus
        static OrderLegStatus MapStatus(string s) => s switch
        {
            "Filled" => OrderLegStatus.Filled,
            "Canceled" or "Cancelled" => OrderLegStatus.Canceled,
            "Rejected" => OrderLegStatus.Rejected,
            "Suspended" => OrderLegStatus.Working, // Bracket legs start Suspended, activate on entry fill
            _ => OrderLegStatus.Working
        };

        var group = new GroupOrder
        {
            GroupOrderId = groupId,
            SetupId = setupId,
            Ticker = rawTicker,
            Direction = sig.Direction,
            TotalContracts = sig.TotalContracts,
            PartialContracts = partialCts,
            UseBe = sig.UseBe,
            Status = GroupOrderStatus.Pending,
            Broker = "Tradovate",
            BrokerStrategyId = strategyId?.ToString(),
        };

        // Build legs from REST-discovered data (real prices from Tradovate, not our requested prices)
        if (disc.Entry is not null)
        {
            var leg = new OrderLeg { GroupOrderId = groupId, OrderId = disc.Entry.Id.ToString(), LegType = LegType.Entry, OrderType = sig.OrderType, Action = buyAction, Quantity = disc.Entry.Qty > 0 ? disc.Entry.Qty : sig.TotalContracts, Price = disc.Entry.Price, Status = MapStatus(disc.Entry.Status) };
            if (disc.Entry.FillPrice.HasValue) leg.FillPrice = disc.Entry.FillPrice;
            if (disc.Entry.Status == "Filled") leg.FillTime = DateTime.UtcNow;
            group.Legs.Add(leg);

            // If entry already filled, set group Active + EntryPrice
            if (disc.Entry.Status == "Filled")
            {
                group.Status = GroupOrderStatus.Active;
                group.EntryPrice = disc.Entry.FillPrice ?? disc.Entry.Price;
            }
        }
        if (disc.Tg1 is not null && usePartial)
            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = disc.Tg1.Id.ToString(), LegType = LegType.Tg1, OrderType = "Limit", Action = sellAction, Quantity = disc.Tg1.Qty > 0 ? disc.Tg1.Qty : partialCts, Price = disc.Tg1.Price, Status = MapStatus(disc.Tg1.Status) });
        if (tg2Disc is not null)
            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = tg2Disc.Id.ToString(), LegType = LegType.Tg2, OrderType = "Limit", Action = sellAction, Quantity = tg2Disc.Qty > 0 ? tg2Disc.Qty : remainCts, Price = tg2Disc.Price, Status = MapStatus(tg2Disc.Status) });
        if (disc.Stop is not null)
            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = disc.Stop.Id.ToString(), LegType = LegType.Stop, OrderType = "Stop", Action = sellAction, Quantity = disc.Stop.Qty > 0 ? disc.Stop.Qty : sig.TotalContracts, Price = disc.Stop.Price, Status = MapStatus(disc.Stop.Status) });
        // Track the second stop (from Tg2 bracket) for safety-cancel on Tg1 fill
        if (disc.Stop2 is not null && disc.Stop2.Status is not "Canceled" and not "Cancelled")
            group.Stop2OrderId = disc.Stop2.Id.ToString();

        _log.LogInformation("[TV-GRP] Bracket strategy {Strat} group {G}: entry={E}({ES}) tg1={T1} tg2={T2} stop={S} stop2={S2}",
            strategyId, groupId, disc.Entry?.Id, disc.Entry?.Status, disc.Tg1?.Id, tg2Disc?.Id, disc.Stop?.Id, disc.Stop2?.Id);

        // If bracket legs are missing (entry not filled yet), discover them in background
        // when the entry eventually fills. Poll until bracket legs appear.
        if (disc.Tg1 is null && disc.Tg2 is null && disc.Stop is null)
        {
            _ = Task.Run(async () =>
            {
                _log.LogDebug("[TV-GRP] Background bracket leg discovery started for group {G}", groupId);
                // Wait for entry to fill — poll every 2s for up to 5 minutes
                for (int i = 0; i < 150; i++)
                {
                    await Task.Delay(2000);
                    var fullDisc = await DiscoverBracketLegsAsync(
                        strategyId!.Value, symbol, entryAction, exitAction, usePartial);

                    if (fullDisc.Tg1 is not null || fullDisc.Tg2 is not null || fullDisc.Stop is not null)
                    {
                        // Update GroupOrder legs in place
                        var tg2d = fullDisc.Tg2 ?? (usePartial ? null : fullDisc.Tg1);

                        if (fullDisc.Tg1 is not null && usePartial)
                        {
                            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = fullDisc.Tg1.Id.ToString(), LegType = LegType.Tg1, OrderType = "Limit", Action = sellAction, Quantity = fullDisc.Tg1.Qty > 0 ? fullDisc.Tg1.Qty : partialCts, Price = fullDisc.Tg1.Price, Status = MapStatus(fullDisc.Tg1.Status) });
                            EventStream?.RegisterOrder(fullDisc.Tg1.Id, groupId, LegType.Tg1);
                        }
                        if (tg2d is not null)
                        {
                            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = tg2d.Id.ToString(), LegType = LegType.Tg2, OrderType = "Limit", Action = sellAction, Quantity = tg2d.Qty > 0 ? tg2d.Qty : remainCts, Price = tg2d.Price, Status = MapStatus(tg2d.Status) });
                            EventStream?.RegisterOrder(tg2d.Id, groupId, LegType.Tg2);
                        }
                        if (fullDisc.Stop is not null)
                        {
                            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = fullDisc.Stop.Id.ToString(), LegType = LegType.Stop, OrderType = "Stop", Action = sellAction, Quantity = fullDisc.Stop.Qty > 0 ? fullDisc.Stop.Qty : sig.TotalContracts, Price = fullDisc.Stop.Price, Status = MapStatus(fullDisc.Stop.Status) });
                            EventStream?.RegisterOrder(fullDisc.Stop.Id, groupId, LegType.Stop);
                        }
                        if (fullDisc.Stop2 is not null)
                            EventStream?.RegisterOrder(fullDisc.Stop2.Id, groupId, LegType.Stop);

                        // Update entry status if it filled during background polling
                        if (fullDisc.Entry is not null && fullDisc.Entry.Status == "Filled" && group.Status == GroupOrderStatus.Pending)
                        {
                            var entryLeg = group.GetLeg(LegType.Entry);
                            if (entryLeg != null)
                            {
                                entryLeg.Status = OrderLegStatus.Filled;
                                entryLeg.FillPrice = fullDisc.Entry.FillPrice;
                                entryLeg.FillTime = DateTime.UtcNow;
                            }
                            group.Status = GroupOrderStatus.Active;
                            group.EntryPrice = fullDisc.Entry.FillPrice ?? fullDisc.Entry.Price;
                        }

                        _log.LogInformation("[TV-GRP] Background discovery completed for group {G}: tg1={T1} tg2={T2} stop={S}",
                            groupId, fullDisc.Tg1?.Id, tg2d?.Id, fullDisc.Stop?.Id);
                        return;
                    }
                }
                _log.LogWarning("[TV-GRP] Background bracket leg discovery timed out for group {G}", groupId);
            });
        }

        return group;
    }

    /// <summary>
    /// Recover an orphaned Tradovate strategy by re-discovering all bracket legs.
    /// Creates a fully populated GroupOrder from the broker's current state.
    /// </summary>
    public async Task<GroupOrder?> RecoverStrategyAsync(long strategyId, string ticker, Direction direction,
        int totalContracts, int partialContracts, bool useBe, string setupId)
    {
        var entryAction = direction == Direction.Long ? "Buy" : "Sell";
        var exitAction  = direction == Direction.Long ? "Sell" : "Buy";
        var usePartial  = partialContracts > 0;
        var remainCts   = totalContracts - partialContracts;
        var symbol      = FuturesSymbol.ToTradovate(ticker);

        _log.LogInformation("[TV-RECOVER] Recovering strategy {S} for {Ticker} {Dir}", strategyId, ticker, direction);

        var disc = await DiscoverBracketLegsAsync(strategyId, symbol, entryAction, exitAction, usePartial);
        if (disc.Entry is null && disc.Tg1 is null && disc.Stop is null)
        {
            _log.LogError("[TV-RECOVER] No legs found for strategy {S}", strategyId);
            return null;
        }

        var groupId    = Guid.NewGuid().ToString("N")[..8];
        var buyAction  = entryAction == "Buy" ? "BUY" : "SELL";
        var sellAction = entryAction == "Buy" ? "SELL" : "BUY";

        static OrderLegStatus MapStatus(string s) => s switch
        {
            "Filled" => OrderLegStatus.Filled,
            "Canceled" or "Cancelled" => OrderLegStatus.Canceled,
            "Rejected" => OrderLegStatus.Rejected,
            "Suspended" => OrderLegStatus.Working,
            _ => OrderLegStatus.Working
        };

        var group = new GroupOrder
        {
            GroupOrderId    = groupId,
            SetupId         = setupId,
            Ticker          = ticker,
            Direction       = direction,
            TotalContracts  = totalContracts,
            PartialContracts = partialContracts,
            UseBe           = useBe,
            Status          = GroupOrderStatus.Pending,
            Broker          = "Tradovate",
            BrokerStrategyId = strategyId.ToString(),
        };

        // Build legs from discovered data
        if (disc.Entry is not null)
        {
            var leg = new OrderLeg
            {
                GroupOrderId = groupId, OrderId = disc.Entry.Id.ToString(),
                LegType = LegType.Entry, OrderType = disc.Entry.OrderType == "Stop" ? "Stop" : "Limit",
                Action = buyAction, Quantity = disc.Entry.Qty > 0 ? disc.Entry.Qty : totalContracts,
                Price = disc.Entry.Price, Status = MapStatus(disc.Entry.Status)
            };
            if (disc.Entry.FillPrice.HasValue) leg.FillPrice = disc.Entry.FillPrice;
            if (disc.Entry.Status == "Filled") leg.FillTime = DateTime.UtcNow;
            group.Legs.Add(leg);

            if (disc.Entry.Status == "Filled")
            {
                group.Status = GroupOrderStatus.Active;
                group.EntryPrice = disc.Entry.FillPrice ?? disc.Entry.Price;
            }
        }

        var tg2Disc = disc.Tg2 ?? (usePartial ? null : disc.Tg1);

        if (disc.Tg1 is not null && usePartial)
        {
            var tg1Leg = new OrderLeg { GroupOrderId = groupId, OrderId = disc.Tg1.Id.ToString(), LegType = LegType.Tg1, OrderType = "Limit", Action = sellAction, Quantity = disc.Tg1.Qty > 0 ? disc.Tg1.Qty : partialContracts, Price = disc.Tg1.Price, Status = MapStatus(disc.Tg1.Status) };
            if (disc.Tg1.FillPrice.HasValue) tg1Leg.FillPrice = disc.Tg1.FillPrice;
            if (disc.Tg1.Status == "Filled") tg1Leg.FillTime = DateTime.UtcNow;
            group.Legs.Add(tg1Leg);

            // If Tg1 already filled, set group to PartialFilled and accrue P&L
            if (disc.Tg1.Status == "Filled" && group.Status == GroupOrderStatus.Active && group.EntryPrice.HasValue)
            {
                group.Status = GroupOrderStatus.PartialFilled;
                bool isLong = direction == Direction.Long;
                var tg1Fill = disc.Tg1.FillPrice ?? disc.Tg1.Price;
                group.AccruedPartialPnl = isLong
                    ? (tg1Fill - group.EntryPrice.Value) * group.PointValue * partialContracts
                    : (group.EntryPrice.Value - tg1Fill) * group.PointValue * partialContracts;
            }
        }
        if (tg2Disc is not null)
        {
            var tg2Qty = tg2Disc.Qty > 0 ? tg2Disc.Qty : remainCts;
            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = tg2Disc.Id.ToString(), LegType = LegType.Tg2, OrderType = "Limit", Action = sellAction, Quantity = tg2Qty, Price = tg2Disc.Price, Status = MapStatus(tg2Disc.Status) });
        }

        // Use the ACTIVE stop leg: if Tg1 filled, Stop1 is Canceled → use Stop2
        var activeStop = disc.Stop;
        if (activeStop is not null && activeStop.Status is "Canceled" or "Cancelled" && disc.Stop2 is not null)
            activeStop = disc.Stop2;
        if (activeStop is not null)
        {
            var stopQty = group.Status == GroupOrderStatus.PartialFilled
                ? totalContracts - partialContracts
                : (activeStop.Qty > 0 ? activeStop.Qty : totalContracts);
            group.Legs.Add(new OrderLeg { GroupOrderId = groupId, OrderId = activeStop.Id.ToString(), LegType = LegType.Stop, OrderType = "Stop", Action = sellAction, Quantity = stopQty, Price = activeStop.Price, Status = MapStatus(activeStop.Status) });
        }

        // Track the other stop for safety-cancel on Tg1 fill
        var otherStop = activeStop == disc.Stop ? disc.Stop2 : disc.Stop;
        if (otherStop is not null && otherStop.Status is not "Canceled" and not "Cancelled")
            group.Stop2OrderId = otherStop.Id.ToString();

        // Register all legs with WSS for live tracking
        if (disc.Entry is not null) EventStream?.RegisterOrder(disc.Entry.Id, groupId, LegType.Entry);
        if (disc.Tg1 is not null && usePartial) EventStream?.RegisterOrder(disc.Tg1.Id, groupId, LegType.Tg1);
        if (tg2Disc is not null) EventStream?.RegisterOrder(tg2Disc.Id, groupId, LegType.Tg2);
        if (activeStop is not null) EventStream?.RegisterOrder(activeStop.Id, groupId, LegType.Stop);
        // Also register the other stop if still active
        if (!string.IsNullOrEmpty(group.Stop2OrderId))
            EventStream?.RegisterOrder(otherStop!.Id, groupId, LegType.Stop);

        // Detect if trade is already completed (stop or Tg2 already filled)
        var stopLeg = group.GetLeg(LegType.Stop);
        var tg2Leg = group.GetLeg(LegType.Tg2);
        if (stopLeg?.Status == OrderLegStatus.Filled)
        {
            group.Status = GroupOrderStatus.Completed;
            group.CompletedAt = DateTime.UtcNow;
            _log.LogInformation("[TV-RECOVER] Strategy {S} already completed (stop filled @ {P})",
                strategyId, stopLeg.FillPrice ?? activeStop?.FillPrice);
        }
        else if (tg2Leg?.Status == OrderLegStatus.Filled)
        {
            group.Status = GroupOrderStatus.Completed;
            group.CompletedAt = DateTime.UtcNow;
            _log.LogInformation("[TV-RECOVER] Strategy {S} already completed (target filled @ {P})",
                strategyId, tg2Leg.FillPrice ?? tg2Disc?.FillPrice);
        }

        _log.LogInformation("[TV-RECOVER] Recovered group {G}: entry={E}({ES}) tg1={T1} tg2={T2} stop={S} status={St}",
            groupId, disc.Entry?.Id, disc.Entry?.Status, disc.Tg1?.Id, tg2Disc?.Id, activeStop?.Id, group.Status);

        return group;
    }

    /// <summary>
    /// Poll Tradovate REST /order/list for current status of all legs in a group.
    /// Compares with GroupOrder leg status and returns OrderEvents for changed legs.
    /// </summary>
    async Task<List<OrderEvent>> IGroupOrderExecutor.PollOrderStatusesAsync(GroupOrder group)
    {
        var events = new List<OrderEvent>();
        using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = pollCts.Token;
        try
        {
            // ── Discover missing bracket legs via strategy link ──
            bool hasBracketLegs = group.Legs.Any(l => l.LegType is LegType.Tg1 or LegType.Tg2 or LegType.Stop);

            // No strategy ID and no bracket legs → unrecoverable, mark canceled
            if (!hasBracketLegs && string.IsNullOrEmpty(group.BrokerStrategyId))
            {
                _log.LogWarning("[TV-POLL] Group {G} has no BrokerStrategyId and no bracket legs — marking Canceled",
                    group.GroupOrderId);
                group.Status = GroupOrderStatus.Canceled;
                group.CompletedAt = DateTime.UtcNow;
                return events;
            }

            if (!hasBracketLegs && !string.IsNullOrEmpty(group.BrokerStrategyId)
                && long.TryParse(group.BrokerStrategyId, out var stratId))
            {
                _log.LogDebug("[TV-POLL] Group {G} missing bracket legs — discovering via strategy {S}",
                    group.GroupOrderId, stratId);

                var entryAction = group.Direction == Direction.Long ? "Buy" : "Sell";
                var exitAction = group.Direction == Direction.Long ? "Sell" : "Buy";
                bool usePartial = group.PartialContracts > 0 && group.PartialContracts < group.TotalContracts;

                var disc = await DiscoverBracketLegsAsync(stratId, group.Ticker, entryAction, exitAction, usePartial);

                // Entry was rejected/canceled — mark group as canceled, stop polling
                if (disc.Entry is not null && disc.Entry.Status is "Rejected" or "Canceled" or "Cancelled")
                {
                    _log.LogWarning("[TV-POLL] Entry {E} was {S} — marking group {G} as Canceled",
                        disc.Entry.Id, disc.Entry.Status, group.GroupOrderId);
                    group.Status = GroupOrderStatus.Canceled;
                    group.CompletedAt = DateTime.UtcNow;
                    return events;
                }

                var sellAction = group.Direction == Direction.Long ? "SELL" : "BUY";
                static OrderLegStatus MapSt(string s) => s switch
                {
                    "Filled" => OrderLegStatus.Filled,
                    "Canceled" or "Cancelled" => OrderLegStatus.Canceled,
                    "Rejected" => OrderLegStatus.Rejected,
                    _ => OrderLegStatus.Working
                };

                var tg2Disc = disc.Tg2 ?? (usePartial ? null : disc.Tg1);

                if (disc.Tg1 is not null && usePartial && group.GetLeg(LegType.Tg1) is null)
                    group.Legs.Add(new OrderLeg { GroupOrderId = group.GroupOrderId, OrderId = disc.Tg1.Id.ToString(),
                        LegType = LegType.Tg1, OrderType = "Limit", Action = sellAction,
                        Quantity = disc.Tg1.Qty, Price = disc.Tg1.Price, Status = MapSt(disc.Tg1.Status) });

                if (tg2Disc is not null && group.GetLeg(LegType.Tg2) is null)
                    group.Legs.Add(new OrderLeg { GroupOrderId = group.GroupOrderId, OrderId = tg2Disc.Id.ToString(),
                        LegType = LegType.Tg2, OrderType = "Limit", Action = sellAction,
                        Quantity = tg2Disc.Qty, Price = tg2Disc.Price, Status = MapSt(tg2Disc.Status) });

                if (disc.Stop is not null && group.GetLeg(LegType.Stop) is null)
                    group.Legs.Add(new OrderLeg { GroupOrderId = group.GroupOrderId, OrderId = disc.Stop.Id.ToString(),
                        LegType = LegType.Stop, OrderType = "Stop", Action = sellAction,
                        Quantity = disc.Stop.Qty, Price = disc.Stop.Price, Status = MapSt(disc.Stop.Status) });

                if (group.Legs.Count > 1)
                    _log.LogInformation("[TV-POLL] Discovered bracket legs for group {G}: {Count} legs total",
                        group.GroupOrderId, group.Legs.Count);
            }

            // ── Poll existing legs for status changes ──
            var legIds = string.Join(",", group.Legs.Select(l => l.OrderId));
            if (string.IsNullOrEmpty(legIds)) return events;

            var json = await GetAsync($"/order/items?ids={legIds}", ct);
            using var doc = JsonDocument.Parse(json);

            var brokerOrders = new Dictionary<string, (string status, decimal? fillPrice, int? fillQty)>();
            foreach (var order in doc.RootElement.EnumerateArray())
            {
                var id = order.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number
                    ? idp.GetInt64().ToString() : null;
                if (id is null) continue;

                var status = order.TryGetProperty("ordStatus", out var sp) ? sp.GetString() : null;
                var fillPrice = order.TryGetProperty("avgFillPrice", out var fp) && fp.ValueKind == JsonValueKind.Number
                    ? (decimal?)fp.GetDecimal() : null;
                var fillQty = order.TryGetProperty("filledQty", out var fq) && fq.ValueKind == JsonValueKind.Number
                    ? (int?)fq.GetInt32() : null;

                if (status is not null)
                    brokerOrders[id] = (status, fillPrice, fillQty);
            }

            // Also fetch current prices from /orderVersion/items (stop may have been modified by BE)
            var versionJson = await GetAsync($"/orderVersion/items?ids={legIds}", ct);
            using var verDoc = JsonDocument.Parse(versionJson);
            var currentPrices = new Dictionary<string, decimal>();
            foreach (var ver in verDoc.RootElement.EnumerateArray())
            {
                var oid = ver.TryGetProperty("orderId", out var voip) && voip.ValueKind == JsonValueKind.Number
                    ? voip.GetInt64().ToString()
                    : ver.TryGetProperty("id", out var vidp) && vidp.ValueKind == JsonValueKind.Number
                        ? vidp.GetInt64().ToString() : null;
                if (oid is null) continue;
                var price = ver.TryGetProperty("price", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetDecimal() : 0;
                if (price == 0)
                    price = ver.TryGetProperty("stopPrice", out var spp) && spp.ValueKind == JsonValueKind.Number ? spp.GetDecimal() : 0;
                if (price > 0)
                    currentPrices[oid] = price;
            }

            foreach (var leg in group.Legs)
            {
                // Update leg price if broker modified it (e.g. BE stop move)
                if (currentPrices.TryGetValue(leg.OrderId, out var curPrice) && curPrice != leg.Price && leg.Status == OrderLegStatus.Working)
                {
                    _log.LogDebug("[TV-POLL] Leg {Leg} {OrderId} price updated: {Old} → {New}",
                        leg.LegType, leg.OrderId, leg.Price, curPrice);
                    leg.Price = curPrice;
                }

                if (!brokerOrders.TryGetValue(leg.OrderId, out var broker)) continue;

                var brokerStatus = broker.status switch
                {
                    "Filled" => OrderLegStatus.Filled,
                    "Canceled" or "Cancelled" => OrderLegStatus.Canceled,
                    "Rejected" => OrderLegStatus.Rejected,
                    "Working" => OrderLegStatus.Working,
                    _ => (OrderLegStatus?)null
                };

                if (brokerStatus is not null && brokerStatus != leg.Status &&
                    brokerStatus is OrderLegStatus.Filled or OrderLegStatus.Canceled or OrderLegStatus.Rejected or OrderLegStatus.Working)
                {
                    events.Add(new OrderEvent(
                        group.GroupOrderId, leg.OrderId, leg.LegType,
                        brokerStatus.Value, broker.fillPrice, broker.fillQty,
                        null, null, DateTime.UtcNow));

                    _log.LogInformation("[TV-POLL] Leg {Leg} {OrderId} status changed: {Old} → {New} @ {Price}",
                        leg.LegType, leg.OrderId, leg.Status, brokerStatus, broker.fillPrice);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV-POLL] PollOrderStatusesAsync failed for group {G}", group.GroupOrderId);
        }
        return events;
    }

    async Task IGroupOrderExecutor.ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
    {
        if (!long.TryParse(orderId, out var id)) return;
        // Determine order type from price context: stopPrice if modifying stop, limitPrice for targets
        await ModifyOrderAsync(id, newQty ?? 0, stopPrice: newPrice);
    }

    async Task IGroupOrderExecutor.CancelOrderAsync(string orderId)
    {
        if (!long.TryParse(orderId, out var id)) return;
        await CancelOrderAsync(id);
    }

    async Task<decimal> IGroupOrderExecutor.PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
    {
        var account = await GetAccountAsync();
        var symbol = FuturesSymbol.ToTradovate(ticker);
        var action = direction == Direction.Long ? "Sell" : "Buy";
        var body = new
        {
            accountSpec = account.Name, accountId = account.Id,
            action, symbol, orderQty = qty,
            orderType = "Market", isAutomated = true
        };
        var orderId = await PlaceSingleAsync(body);
        _log.LogInformation("[TV-GRP] Market close placed, ID={Id}", orderId);

        if (orderId is null) return 0m;

        // Poll /fill/deps?masterid={orderId} to get the actual fill price.
        // Market orders fill almost instantly — a few retries is sufficient.
        var fillPrice = await GetFillPriceByDepsAsync(orderId.Value);
        if (fillPrice is not null)
        {
            _log.LogInformation("[TV-GRP] Market close fill price: {P} (order {Id})", fillPrice, orderId);
            return fillPrice.Value;
        }

        _log.LogWarning("[TV-GRP] Could not retrieve fill price for market close order {Id}", orderId);
        return 0m;
    }

    /// <summary>
    /// Fetch fill price for a market order via GET /fill/deps?masterid={orderId}.
    /// Retries up to 10 times at 300ms. Returns null on timeout or error.
    /// </summary>
    private async Task<decimal?> GetFillPriceByDepsAsync(long orderId)
    {
        const int maxRetries = 10;
        const int delayMs    = 300;
        try
        {
            for (int i = 0; i < maxRetries; i++)
            {
                var json = await GetAsync($"/fill/deps?masterid={orderId}");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fill in doc.RootElement.EnumerateArray())
                    {
                        if (fill.TryGetProperty("price", out var p) &&
                            p.ValueKind == JsonValueKind.Number)
                            return p.GetDecimal();
                    }
                }
                _log.LogDebug("[TV] GetFillPriceByDeps poll {I}/{Max} — no fill yet for order {Id}",
                    i + 1, maxRetries, orderId);
                await Task.Delay(delayMs);
            }
            _log.LogWarning("[TV] GetFillPriceByDeps timed out after {Max} retries for order {Id}",
                maxRetries, orderId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV] GetFillPriceByDepsAsync failed for order {Id}", orderId);
        }
        return null;
    }

    // ── Private helpers ───────────────────────────────────────────

    /// <summary>
    /// Recovers the entry direction from the Orders DB — used when the app restarts
    /// mid-trade and in-memory direction is lost.
    /// </summary>
    private async Task<Direction?> RecoverDirectionAsync(SetupId setup)
    {
        if (_scopeFactory is null) return null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var lastEntry = await db.Orders
                .Where(o => o.Status == "FILLED" && o.Direction != null)
                .OrderByDescending(o => o.FilledAt)
                .FirstOrDefaultAsync();

            if (lastEntry?.Direction is not null &&
                Enum.TryParse<Direction>(lastEntry.Direction, out var dir))
                return dir;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV] Failed to recover direction from DB for setup {S}", setup);
        }
        return null;
    }

    /// <summary>
    /// Fetches fill price for any order by string ID.
    /// Tries /fill/deps first (reliable for bracket stops), falls back to /order/item.
    /// </summary>
    public async Task<decimal?> GetOrderFillPriceAsync(string orderId)
    {
        if (!long.TryParse(orderId, out var id)) return null;
        // /fill/deps is more reliable — Tradovate often omits avgFillPrice for bracket stops
        var price = await GetFillPriceByDepsAsync(id);
        if (price is > 0) return price;
        return await GetFillPriceAsync(id);
    }

    /// <summary>
    /// Fetch all order strategies from Tradovate with their linked orders.
    /// Returns a list of strategy summaries including order IDs, statuses, and fill prices.
    /// </summary>
    public async Task<List<BrokerStrategyView>> GetAllStrategiesAsync()
    {
        var result = new List<BrokerStrategyView>();
        try
        {
            // Step 1: Get all strategies
            var stratJson = await GetAsync("/orderStrategy/list");
            using var stratDoc = JsonDocument.Parse(stratJson);

            var strategies = new List<(long id, string action, int contractId, string paramsJson, string status, DateTime timestamp)>();
            foreach (var s in stratDoc.RootElement.EnumerateArray())
            {
                var id = s.TryGetProperty("id", out var sid) && sid.ValueKind == JsonValueKind.Number ? sid.GetInt64() : 0;
                if (id == 0) continue;
                var action = s.TryGetProperty("action", out var ap) ? ap.GetString() ?? "" : "";
                var contractId = s.TryGetProperty("contractId", out var cid) && cid.ValueKind == JsonValueKind.Number ? cid.GetInt32() : 0;
                var prms = s.TryGetProperty("params", out var pp) ? pp.GetString() ?? "" : "";
                var status = s.TryGetProperty("status", out var sp) ? sp.GetString() ?? "" : "";
                var ts = s.TryGetProperty("timestamp", out var tp) && tp.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.TryParse(tp.GetString(), out var dto) ? dto.UtcDateTime : DateTime.MinValue : DateTime.MinValue;
                strategies.Add((id, action, contractId, prms, status, ts));
            }

            // Step 2: For each strategy, get linked orders
            foreach (var (stratId, action, contractId, paramsJson, status, timestamp) in strategies)
            {
                var view = new BrokerStrategyView
                {
                    StrategyId = stratId,
                    Action = action,
                    ContractId = contractId,
                    Status = status,
                    Timestamp = timestamp,
                };

                // Parse entry price from params
                try
                {
                    using var pDoc = JsonDocument.Parse(paramsJson);
                    if (pDoc.RootElement.TryGetProperty("entryVersion", out var ev))
                    {
                        view.EntryPrice = ev.TryGetProperty("price", out var ep) && ep.ValueKind == JsonValueKind.Number ? ep.GetDecimal() : 0;
                        view.EntryQty = ev.TryGetProperty("orderQty", out var eq) && eq.ValueKind == JsonValueKind.Number ? eq.GetInt32() : 0;
                        view.OrderType = ev.TryGetProperty("orderType", out var ot) ? ot.GetString() ?? "Limit" : "Limit";
                    }
                }
                catch { /* params parse failure — non-critical */ }

                // Get linked orders
                try
                {
                    var linksJson = await GetAsync($"/orderStrategyLink/deps?masterid={stratId}");
                    using var linksDoc = JsonDocument.Parse(linksJson);

                    var orderIds = new List<long>();
                    foreach (var link in linksDoc.RootElement.EnumerateArray())
                    {
                        var oid = link.TryGetProperty("orderId", out var oip) && oip.ValueKind == JsonValueKind.Number ? oip.GetInt64() : 0;
                        var label = link.TryGetProperty("label", out var lp) ? lp.GetString() ?? "" : "";
                        if (oid > 0)
                        {
                            orderIds.Add(oid);
                            view.OrderLinks.Add(new BrokerOrderLink { OrderId = oid, Label = label });
                        }
                    }

                    // Batch fetch order details
                    if (orderIds.Count > 0)
                    {
                        var ids = string.Join(",", orderIds);
                        var ordersJson = await GetAsync($"/order/items?ids={ids}");
                        using var ordersDoc = JsonDocument.Parse(ordersJson);
                        foreach (var o in ordersDoc.RootElement.EnumerateArray())
                        {
                            var oid = o.TryGetProperty("id", out var oip2) && oip2.ValueKind == JsonValueKind.Number ? oip2.GetInt64() : 0;
                            var link = view.OrderLinks.FirstOrDefault(l => l.OrderId == oid);
                            if (link == null) continue;

                            link.Status = o.TryGetProperty("ordStatus", out var osp) ? osp.GetString() ?? "" : "";
                            link.Action = o.TryGetProperty("action", out var oap) ? oap.GetString() ?? "" : "";
                            link.FillPrice = o.TryGetProperty("avgFillPrice", out var afp) && afp.ValueKind == JsonValueKind.Number ? afp.GetDecimal() : null;
                        }

                        // Fetch order versions for type/price info
                        var versionsJson = await GetAsync($"/orderVersion/items?ids={ids}");
                        using var versionsDoc = JsonDocument.Parse(versionsJson);
                        foreach (var v in versionsDoc.RootElement.EnumerateArray())
                        {
                            var oid = v.TryGetProperty("orderId", out var voip) && voip.ValueKind == JsonValueKind.Number ? voip.GetInt64() : 0;
                            var link = view.OrderLinks.FirstOrDefault(l => l.OrderId == oid);
                            if (link == null) continue;

                            link.OrderType = v.TryGetProperty("orderType", out var otp) ? otp.GetString() ?? "" : "";
                            link.Price = v.TryGetProperty("price", out var pp2) && pp2.ValueKind == JsonValueKind.Number ? pp2.GetDecimal() : 0;
                            if (link.Price == 0)
                                link.Price = v.TryGetProperty("stopPrice", out var spp) && spp.ValueKind == JsonValueKind.Number ? spp.GetDecimal() : 0;
                            link.Qty = v.TryGetProperty("orderQty", out var oqp) && oqp.ValueKind == JsonValueKind.Number ? oqp.GetInt32() : 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[TV] Failed to fetch order links for strategy {S}", stratId);
                }

                result.Add(view);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] GetAllStrategiesAsync failed");
        }
        return result;
    }

    /// <summary>View model for a broker strategy with its linked orders.</summary>
    public class BrokerStrategyView
    {
        public long StrategyId { get; set; }
        public string Action { get; set; } = "";
        public int ContractId { get; set; }
        public string Status { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public decimal EntryPrice { get; set; }
        public int EntryQty { get; set; }
        public string OrderType { get; set; } = "Limit";
        public string? SetupId { get; set; } // filled from StrategyLog
        public List<BrokerOrderLink> OrderLinks { get; set; } = new();
    }

    public class BrokerOrderLink
    {
        public long OrderId { get; set; }
        public string Label { get; set; } = ""; // "256"=entry, "0"=bracket0, "1"=bracket1
        public string OrderType { get; set; } = "";
        public string Action { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Price { get; set; }
        public int Qty { get; set; }
        public decimal? FillPrice { get; set; }
    }

    /// <summary>Fetch order IDs linked to a strategy from Tradovate REST.</summary>
    public async Task<Dictionary<string, long>> GetStrategyOrderIdsAsync(long strategyId)
    {
        var result = new Dictionary<string, long>();
        try
        {
            var linksJson = await GetAsync($"/orderStrategyLink/deps?masterid={strategyId}");
            using var linksDoc = JsonDocument.Parse(linksJson);

            foreach (var link in linksDoc.RootElement.EnumerateArray())
            {
                var orderId = link.TryGetProperty("orderId", out var oid) && oid.ValueKind == JsonValueKind.Number
                    ? oid.GetInt64() : 0;
                var label = link.TryGetProperty("label", out var lp) ? lp.GetString() ?? "" : "";
                if (orderId > 0)
                    result[label] = orderId;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV] GetStrategyOrderIdsAsync failed for {S}", strategyId);
        }
        return result;
    }

    private async Task<decimal?> GetFillPriceAsync(long orderId)
    {
        const int maxRetries   = 15;
        const int delayMs      = 200;

        try
        {
            for (int i = 0; i < maxRetries; i++)
            {
                await Task.Delay(delayMs);
                var json = await GetAsync($"/order/item?id={orderId}");
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("avgFillPrice", out var fp) &&
                    fp.ValueKind == JsonValueKind.Number)
                {
                    return fp.GetDecimal();
                }

                _log.LogDebug("[TV] GetFillPrice poll {I}/{Max} — avgFillPrice not yet available for order {Id}",
                    i + 1, maxRetries, orderId);
            }

            _log.LogWarning("[TV] GetFillPrice timed out after {Max} retries for order {Id}", maxRetries, orderId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV] GetFillPriceAsync failed for order {Id}", orderId);
        }

        return null;
    }

    /// <summary>
    /// Get account cash balance snapshot from Tradovate for daily P&L verification.
    /// Returns (realizedPnl, unrealizedPnl, totalCashValue) or null on error.
    /// </summary>
    public async Task<(decimal realized, decimal unrealized, decimal cashValue)?> GetCashBalanceAsync()
    {
        try
        {
            var account = await GetAccountAsync();
            var json = await GetAsync($"/cashBalance/getCashBalanceSnapshot?accountId={account.Id}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var realized = root.TryGetProperty("realizedPnl", out var rp) && rp.ValueKind == JsonValueKind.Number
                ? rp.GetDecimal() : 0m;
            var unrealized = root.TryGetProperty("openPnl", out var up) && up.ValueKind == JsonValueKind.Number
                ? up.GetDecimal() : 0m;
            var cashValue = root.TryGetProperty("totalCashValue", out var cv) && cv.ValueKind == JsonValueKind.Number
                ? cv.GetDecimal() : 0m;

            return (realized, unrealized, cashValue);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[TV] GetCashBalanceAsync failed");
            return null;
        }
    }

    /// <summary>
    /// Resolves and caches the Tradovate account from GET /account/list.
    /// Uses the first account in the list.
    /// </summary>
    private async Task<AccountRef> GetAccountAsync()
    {
        if (_cachedAccount is not null) return _cachedAccount;

        var json = await GetAsync("/account/list");
        using var doc = JsonDocument.Parse(json);

        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Tradovate: /account/list returned empty array — no accounts found.");

        var name = first.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        var id   = first.TryGetProperty("id",   out var ip) ? ip.GetInt64()      : 0;

        _cachedAccount = new AccountRef(name, id);
        _log.LogInformation("[TV] Resolved account: name={Name} id={Id}", name, id);
        return _cachedAccount;
    }

    /// <summary>
    /// POST /order/placeOSO — places a market entry with two bracket legs.
    /// Returns (entryOrderId, targetOrderId, stopOrderId) parsed from the response.
    /// Tradovate returns a single orderId for the entry; the bracket leg IDs are
    /// available inside the response's "bracket1" / "bracket2" objects.
    /// </summary>
    private async Task<(long? entry, long? target, long? stop)> PlaceOsoAsync(object body)
    {
        try
        {
            var url  = $"{_auth.ApiBaseUrl}/order/placeOSO";
            var resp = await PostAsync(url, body);

            _log.LogDebug("[TV] PlaceOSO raw response: {Resp}", resp);

            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;

            long? GetId(JsonElement el)
                => el.TryGetProperty("orderId", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetInt64()
                    // Also try "id" as fallback property name
                    : el.TryGetProperty("id", out var p2) && p2.ValueKind == JsonValueKind.Number
                        ? p2.GetInt64()
                        : null;

            var entryId  = GetId(root);

            // Try bracket1/bracket2 properties (documented response format)
            var targetId = root.TryGetProperty("bracket1", out var b1) ? GetId(b1) : null;
            var stopId   = root.TryGetProperty("bracket2", out var b2) ? GetId(b2) : null;

            // Fallback: try "oso1Id" / "oso2Id" or "bracket1OrdId" / "bracket2OrdId"
            // (Tradovate response format varies by API version)
            if (targetId is null && root.TryGetProperty("oso1Id", out var o1) && o1.ValueKind == JsonValueKind.Number)
                targetId = o1.GetInt64();
            if (stopId is null && root.TryGetProperty("oso2Id", out var o2) && o2.ValueKind == JsonValueKind.Number)
                stopId = o2.GetInt64();

            _log.LogInformation("[TV] PlaceOSO parsed — entry={E} target={T} stop={S}",
                entryId, targetId, stopId);

            return (entryId, targetId, stopId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] PlaceOsoAsync failed");
            return (null, null, null);
        }
    }

    /// <summary>Places a single order (limit, stop, or market) at the given URL.
    /// Returns the Tradovate orderId from the response.</summary>
    private async Task<long?> PlaceSingleAsync(object body)
    {
        try
        {
            var url  = $"{_auth.ApiBaseUrl}/order/placeOrder";
            var resp = await PostAsync(url, body);

            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("orderId", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number)
                return idProp.GetInt64();

            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] PlaceSingleAsync failed");
            return null;
        }
    }

    /// <summary>
    /// POST /orderStrategy/startOrderStrategy — places a bracket strategy.
    /// Tradovate manages the entire lifecycle: entry, brackets, BE, trailing stop.
    /// Returns (strategyId, entryOrderId).
    /// </summary>
    private async Task<(long? strategyId, long? entryOrderId)> StartOrderStrategyAsync(object body)
    {
        try
        {
            var url = $"{_auth.ApiBaseUrl}/orderStrategy/startOrderStrategy";
            var resp = await PostAsync(url, body);
            _log.LogInformation("[TV] startOrderStrategy raw response: {Resp}", resp);

            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;

            long? strategyId = null;
            long? entryOrderId = null;

            // Try to find strategy ID from all known response shapes
            // Shape 1: {"orderStrategy": {"id": 123, ...}}
            // Shape 2: {"id": 123, ...}  (flat)
            // Shape 3: {"orderStrategyId": 123, ...}
            var stratEl = root.TryGetProperty("orderStrategy", out var nested) ? nested : root;

            // Try "id" on strategy element
            if (stratEl.TryGetProperty("id", out var sid) && sid.ValueKind == JsonValueKind.Number)
                strategyId = sid.GetInt64();
            // Try "orderStrategyId" as fallback
            if (strategyId is null && stratEl.TryGetProperty("orderStrategyId", out var sid2) && sid2.ValueKind == JsonValueKind.Number)
                strategyId = sid2.GetInt64();
            // Try root "id" if we used a nested element
            if (strategyId is null && nested.ValueKind != JsonValueKind.Undefined && root.TryGetProperty("id", out var sid3) && sid3.ValueKind == JsonValueKind.Number)
                strategyId = sid3.GetInt64();

            // Deep scan: if still null, walk all top-level number properties looking for a plausible ID
            if (strategyId is null)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.GetInt64() > 400_000_000_000)
                    {
                        strategyId = prop.Value.GetInt64();
                        _log.LogWarning("[TV] Found strategyId via deep scan in property '{P}': {V}", prop.Name, strategyId);
                        break;
                    }
                }
            }

            // Entry order ID — may be at root, in orderStrategy, or absent (discovered later)
            foreach (var el in new[] { root, stratEl })
            {
                if (el.TryGetProperty("orderId", out var eid) && eid.ValueKind == JsonValueKind.Number)
                    { entryOrderId = eid.GetInt64(); break; }
                if (el.TryGetProperty("entryOrder", out var eo))
                {
                    if (eo.TryGetProperty("id", out var eoid) && eoid.ValueKind == JsonValueKind.Number)
                        { entryOrderId = eoid.GetInt64(); break; }
                }
            }

            _log.LogInformation("[TV] startOrderStrategy — strategyId={S} entryOrderId={E}",
                strategyId, entryOrderId);
            return (strategyId, entryOrderId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] StartOrderStrategyAsync failed");
            return (null, null);
        }
    }

    /// <summary>Info about a discovered order leg from REST.</summary>
    private record DiscoveredLeg(long Id, string OrderType, string Action, decimal Price, int Qty, string Status, decimal? FillPrice);

    /// <summary>Result of bracket leg discovery — includes full order info for REST-based state sync.</summary>
    private record BracketDiscovery(
        DiscoveredLeg? Entry, DiscoveredLeg? Tg1, DiscoveredLeg? Tg2,
        DiscoveredLeg? Stop, DiscoveredLeg? Stop2);

    /// <summary>
    /// Discovers bracket leg orders via /orderStrategyLink/deps + /order/items.
    /// Step 1: Get order IDs linked to strategy (labels: 256=entry, 0=bracket0, 1=bracket1)
    /// Step 2: Fetch full order details by IDs
    /// Step 3: Classify by label + orderType (Limit=target, Stop=stop)
    /// </summary>
    private async Task<BracketDiscovery> DiscoverBracketLegsAsync(
        long strategyId, string symbol, string entryAction, string exitAction, bool usePartial)
    {
        const int maxAttempts = 15;
        const int delayMs = 500;

        try
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(delayMs);

                // Step 1: Get linked order IDs from strategy
                var linksJson = await GetAsync($"/orderStrategyLink/deps?masterid={strategyId}");
                using var linksDoc = JsonDocument.Parse(linksJson);

                var orderIdsByLabel = new Dictionary<long, string>(); // orderId → label
                foreach (var link in linksDoc.RootElement.EnumerateArray())
                {
                    var orderId = link.TryGetProperty("orderId", out var oid) && oid.ValueKind == JsonValueKind.Number
                        ? oid.GetInt64() : 0;
                    var label = link.TryGetProperty("label", out var lp) ? lp.GetString() ?? "" : "";
                    if (orderId > 0)
                        orderIdsByLabel[orderId] = label;
                }

                if (orderIdsByLabel.Count == 0)
                {
                    if (attempt % 5 == 0)
                        _log.LogInformation("[TV] DiscoverBracketLegs attempt {A}/{Max}: no links yet for strategy {S}",
                            attempt + 1, maxAttempts, strategyId);
                    continue;
                }

                // Step 2: Fetch order details from two endpoints
                // /orderVersion/items — always has orderType, price, stopPrice, qty
                // /order/items — has ordStatus, action, avgFillPrice, filledQty
                var ids = string.Join(",", orderIdsByLabel.Keys);
                var versionJson = await GetAsync($"/orderVersion/items?ids={ids}");
                var statusJson = await GetAsync($"/order/items?ids={ids}");
                using var versionDoc = JsonDocument.Parse(versionJson);
                using var statusDoc = JsonDocument.Parse(statusJson);

                // Build status lookup from /order/items
                var statusLookup = new Dictionary<long, (string status, string? action, decimal? fillPrice, int? fillQty, decimal? price, decimal? stopPrice, int? qty)>();
                foreach (var order in statusDoc.RootElement.EnumerateArray())
                {
                    var oid = order.TryGetProperty("id", out var idp2) && idp2.ValueKind == JsonValueKind.Number
                        ? idp2.GetInt64() : 0;
                    if (oid == 0) continue;
                    var st = order.TryGetProperty("ordStatus", out var sp2) ? sp2.GetString() : null;
                    var act = order.TryGetProperty("action", out var ap2) ? ap2.GetString() : null;
                    var fp2 = order.TryGetProperty("avgFillPrice", out var fpp) && fpp.ValueKind == JsonValueKind.Number
                        ? (decimal?)fpp.GetDecimal() : null;
                    var fq2 = order.TryGetProperty("filledQty", out var fqp) && fqp.ValueKind == JsonValueKind.Number
                        ? (int?)fqp.GetInt32() : null;
                    // /order/items has current prices (reflects broker modifications)
                    var curPrice = order.TryGetProperty("price", out var cpp) && cpp.ValueKind == JsonValueKind.Number
                        ? (decimal?)cpp.GetDecimal() : null;
                    var curStopPx = order.TryGetProperty("stopPrice", out var cspp) && cspp.ValueKind == JsonValueKind.Number
                        ? (decimal?)cspp.GetDecimal() : null;
                    var curQty = order.TryGetProperty("orderQty", out var cqp) && cqp.ValueKind == JsonValueKind.Number
                        ? (int?)cqp.GetInt32() : null;
                    statusLookup[oid] = (st ?? "Unknown", act, fp2, fq2, curPrice, curStopPx, curQty);
                }

                DiscoveredLeg? entryLeg = null;
                var bracketLegs = new Dictionary<string, (DiscoveredLeg? limit, DiscoveredLeg? stop)>();

                // Merge version (type/price) + status (status/action/fill) per order
                foreach (var ver in versionDoc.RootElement.EnumerateArray())
                {
                    long orderId = ver.TryGetProperty("orderId", out var oidp) && oidp.ValueKind == JsonValueKind.Number
                        ? oidp.GetInt64()
                        : ver.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number
                            ? idp.GetInt64() : 0;
                    if (orderId == 0 || !orderIdsByLabel.TryGetValue(orderId, out var label)) continue;

                    var orderType = ver.TryGetProperty("orderType", out var otp) ? otp.GetString() : null;
                    var price = ver.TryGetProperty("price", out var pp) && pp.ValueKind == JsonValueKind.Number
                        ? pp.GetDecimal() : 0m;
                    var stopPx = ver.TryGetProperty("stopPrice", out var spp) && spp.ValueKind == JsonValueKind.Number
                        ? spp.GetDecimal() : 0m;
                    var qty = ver.TryGetProperty("orderQty", out var qp) && qp.ValueKind == JsonValueKind.Number
                        ? qp.GetInt32() : 0;

                    var status = "Unknown";
                    string? action = null;
                    decimal? fillPrice = null;
                    if (statusLookup.TryGetValue(orderId, out var st))
                    {
                        status = st.status;
                        action = st.action;
                        fillPrice = st.fillPrice;
                        // Prefer current prices from /order/items (reflects broker modifications)
                        if (st.stopPrice is > 0) stopPx = st.stopPrice.Value;
                        if (st.price is > 0 && orderType != "Stop") price = st.price.Value;
                        if (st.qty is > 0) qty = st.qty.Value;
                    }
                    if (status is "Expired") continue;

                    var leg = new DiscoveredLeg(orderId, orderType ?? "", action ?? "",
                        orderType == "Stop" ? stopPx : price, qty, status, fillPrice);

                    // Entry order — Market type or same action as entry
                    if (orderType == "Market" || label == "256")
                    {
                        entryLeg = leg;
                        continue;
                    }

                    // Bracket legs — classify by orderType from /orderVersion
                    if (!bracketLegs.ContainsKey(label))
                        bracketLegs[label] = (null, null);

                    var bracket = bracketLegs[label];
                    if (orderType == "Limit")
                        bracketLegs[label] = (leg, bracket.stop);
                    else if (orderType == "Stop")
                        bracketLegs[label] = (bracket.limit, leg);
                }

                if (attempt == 0 || bracketLegs.Count > 0)
                    _log.LogDebug("[TV] DiscoverBracketLegs attempt {A}: entry={E}({ES}) brackets={B}",
                        attempt + 1, entryLeg?.Id, entryLeg?.Status, bracketLegs.Count);

                // Map bracket labels to Tg1/Tg2/Stop based on bracket index
                var sortedLabels = bracketLegs.Keys.OrderBy(k => k).ToList();
                int expectedBrackets = usePartial ? 2 : 1;

                if (sortedLabels.Count >= expectedBrackets)
                {
                    DiscoveredLeg? tg1 = null, tg2 = null, stop1 = null, stop2 = null;

                    if (usePartial && sortedLabels.Count >= 2)
                    {
                        // Bracket "0" = partial (Tg1 + Stop1), bracket "1" = remainder (Tg2 + Stop2)
                        tg1 = bracketLegs[sortedLabels[0]].limit;
                        stop1 = bracketLegs[sortedLabels[0]].stop;
                        tg2 = bracketLegs[sortedLabels[1]].limit;
                        stop2 = bracketLegs[sortedLabels[1]].stop;
                    }
                    else
                    {
                        // Single bracket — Tg2 + Stop
                        tg2 = bracketLegs[sortedLabels[0]].limit;
                        stop1 = bracketLegs[sortedLabels[0]].stop;
                    }

                    _log.LogInformation(
                        "[TV] Bracket legs discovered on attempt {A}: entry={E}({ES}) tg1={T1}({T1S}) tg2={T2}({T2S}) stop={S}({SS}) stop2={S2}",
                        attempt + 1,
                        entryLeg?.Id, entryLeg?.Status,
                        tg1?.Id, tg1?.Status,
                        tg2?.Id, tg2?.Status,
                        stop1?.Id, stop1?.Status,
                        stop2?.Id);

                    return new BracketDiscovery(entryLeg, tg1, tg2, stop1, stop2);
                }

                // Entry found but bracket legs not yet visible — give a few attempts
                if (entryLeg is not null && attempt >= 3 && bracketLegs.Count == 0)
                {
                    _log.LogInformation("[TV] Entry {E} ({S}) found but no bracket legs after {A} attempts — returning entry-only",
                        entryLeg.Id, entryLeg.Status, attempt + 1);
                    return new BracketDiscovery(entryLeg, null, null, null, null);
                }
            }

            _log.LogWarning("[TV] DiscoverBracketLegsAsync timed out after {Max} attempts", maxAttempts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] DiscoverBracketLegsAsync failed");
        }

        return new BracketDiscovery(null, null, null, null, null);
    }

    /// <summary>Cancels an order via POST /order/cancelOrder.</summary>
    private async Task CancelOrderAsync(long orderId)
    {
        try
        {
            var url  = $"{_auth.ApiBaseUrl}/order/cancelOrder";
            var body = new { orderId };
            var resp = await PostAsync(url, body);
            _log.LogDebug("[TV] CancelOrder {Id} response: {Resp}", orderId, resp);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] CancelOrderAsync {Id} failed", orderId);
        }
    }

    /// <summary>Modifies an existing order via POST /order/modifyorder (stop and/or limit price).</summary>
    private async Task<bool> ModifyOrderAsync(long orderId, int qty, decimal? stopPrice = null, decimal? limitPrice = null)
    {
        try
        {
            var url  = $"{_auth.ApiBaseUrl}/order/modifyorder";
            var body = new
            {
                orderId,
                orderQty    = qty,
                orderType   = stopPrice.HasValue ? "Stop" : "Limit",
                stopPrice   = stopPrice,
                price       = limitPrice,
                isAutomated = true
            };
            var resp = await PostAsync(url, body);
            _log.LogDebug("[TV] ModifyOrder {Id} response: {Resp}", orderId, resp);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] ModifyOrderAsync {Id} failed", orderId);
            return false;
        }
    }

    /// <summary>
    /// Discovers bracket leg order IDs by querying open orders after the entry fills.
    /// Tradovate's placeOSO response may not include bracket leg IDs — the child orders
    /// are created asynchronously when the entry fills. This method polls GET /order/list
    /// and matches by price and order type.
    /// </summary>
    private async Task<(long? targetId, long? stopId)> FindBracketLegsAsync(
        long entryOrderId, decimal targetPrice, decimal stopPrice)
    {
        const int maxAttempts = 10;
        const int delayMs     = 300;

        try
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(delayMs);

                var json = await GetAsync("/order/list");
                using var doc = JsonDocument.Parse(json);

                long? targetId = null, stopId = null;

                foreach (var order in doc.RootElement.EnumerateArray())
                {
                    // Only look at working/accepted orders
                    var status = order.TryGetProperty("ordStatus", out var sp) ? sp.GetString() : null;
                    if (status is not ("Working" or "Accepted" or "Queued")) continue;

                    var orderType = order.TryGetProperty("orderType", out var otp) ? otp.GetString() : null;
                    long orderId  = order.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number
                        ? idp.GetInt64() : 0;

                    if (orderId == 0 || orderId == entryOrderId) continue;

                    // Match target: Limit order at our target price
                    if (orderType == "Limit" && targetId is null)
                    {
                        if (order.TryGetProperty("price", out var pp) &&
                            pp.ValueKind == JsonValueKind.Number &&
                            pp.GetDecimal() == targetPrice)
                        {
                            targetId = orderId;
                        }
                    }
                    // Match stop: Stop order at our stop price
                    else if (orderType == "Stop" && stopId is null)
                    {
                        if (order.TryGetProperty("stopPrice", out var spp) &&
                            spp.ValueKind == JsonValueKind.Number &&
                            spp.GetDecimal() == stopPrice)
                        {
                            stopId = orderId;
                        }
                    }

                    if (targetId is not null && stopId is not null)
                        return (targetId, stopId);
                }

                if (targetId is not null && stopId is not null)
                    return (targetId, stopId);

                _log.LogDebug("[TV] FindBracketLegs attempt {I}/{Max} — target={T} stop={S}",
                    attempt + 1, maxAttempts, targetId, stopId);
            }

            _log.LogWarning("[TV] FindBracketLegsAsync timed out after {Max} attempts", maxAttempts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[TV] FindBracketLegsAsync failed");
        }

        return (null, null);
    }

    /// <summary>GET helper — returns response body as string.</summary>
    private async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync();
        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var url  = path.StartsWith("http") ? path : $"{_auth.ApiBaseUrl}{path}";
        var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Tradovate GET {path} failed ({(int)resp.StatusCode}): {body}");

        return body;
    }

    /// <summary>POST helper — serialises body as JSON, returns response body string.</summary>
    private async Task<string> PostAsync(string url, object body)
    {
        var token = await _auth.GetAccessTokenAsync();
        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var json    = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });

        using var resp = await http.PostAsync(url,
            new StringContent(json, Encoding.UTF8, "application/json"));

        var respBody = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("[TV] POST {Url} rejected: {Status} {Body}",
                url, (int)resp.StatusCode, respBody);
            throw new InvalidOperationException(
                $"Tradovate POST {url} failed ({(int)resp.StatusCode}): {respBody}");
        }

        return respBody;
    }
}
