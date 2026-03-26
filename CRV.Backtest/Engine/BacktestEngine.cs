using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using CRV.Backtest.Results;
using Microsoft.Extensions.Logging;

namespace CRV.Backtest.Engine;

public enum FillMode { AtClose, AtTouch, WithSlippage }

public class BacktestConfig
{
    public DateTime From             { get; set; } = DateTime.UtcNow.AddMonths(-6);
    public DateTime To               { get; set; } = DateTime.UtcNow;
    public FillMode FillMode         { get; set; } = FillMode.AtTouch;
    public int      SlippageTicks    { get; set; } = 1;
    public string   DataSource       { get; set; } = "CSV";
    public string?  CsvPath          { get; set; } = "";
    public int      WarmupBars       { get; set; } = 0;
    /// <summary>
    /// Overrides StrategyConfig.ExecutionTFMinutes for this run.
    /// CSV bars are resampled to this TF automatically.
    /// </summary>
    public int      ExecutionTFMinutes { get; set; } = 1;
    /// <summary>
    /// Which session(s) to include in the backtest.
    /// "All" runs all enabled sessions via SessionManager.
    /// "Asia", "London", or "NY" filters to that single session only.
    /// Default "NY" preserves backward-compatible single-session behaviour.
    /// </summary>
    public string   BacktestSession  { get; set; } = "NY";
}

/// <summary>Runs historical bars through ComposableEngine and collects trade results.</summary>
public class BacktestEngine
{
    private readonly StrategyConfig _cfg;
    private readonly BacktestConfig _btCfg;
    private readonly ILogger        _log;

    public BacktestEngine(StrategyConfig cfg, BacktestConfig btCfg, ILogger<BacktestEngine> log)
    { _cfg = cfg; _btCfg = btCfg; _log = log; }

    /// <summary>
    /// Single-ticker backward-compatible overload.
    /// Routes all bars through the global ticker.
    /// </summary>
    public Task<BacktestResult> RunAsync(IAsyncEnumerable<Bar> bars, CancellationToken ct = default)
    {
        // Wrap as tagged stream using the global ticker
        return RunAsync(TagBars(bars, _cfg.Ticker), ct);

        static async IAsyncEnumerable<(string Ticker, Bar Bar)> TagBars(
            IAsyncEnumerable<Bar> bars, string ticker,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var b in bars.WithCancellation(ct))
                yield return (ticker, b);
        }
    }

    /// <summary>
    /// Multi-ticker overload. Accepts a pre-merged chronological stream of (ticker, bar) pairs.
    /// Each setup keeps its own per-instrument ticker; bars route to the correct TickerGroup.
    /// <para>
    /// Tick simulation: for each input bar the engine receives four synthetic price ticks
    /// (Open, High, Low, Close) so that entry and exit levels are evaluated at every
    /// bar's intra-period prices — not just at the execution-TF bar's close.
    /// Arm-state updates (ORB, ATR, VWAP, arm/de-arm) still happen at execution-TF
    /// bar close, giving "armed by execution TF, filled by minute" semantics.
    /// </para>
    /// </summary>
    public async Task<BacktestResult> RunAsync(IAsyncEnumerable<(string Ticker, Bar Bar)> taggedBars, CancellationToken ct = default)
    {
        var trades   = new List<TradeRecord>();
        var sink     = new BacktestSink();
        var prices   = new InMemoryPriceProvider();

        // WSS-style fill simulation
        var groupExec = new BacktestGroupOrderExecutor(_btCfg, _cfg);
        var handler = new BrokerEventHandler(groupExec);

        // Synchronous event delivery: executor → handler (Func<OrderEvent, Task>)
        groupExec.OnEvent = evt => handler.HandleEventAsync(evt);

        // Capture completed trades with commission
        handler.OnTradeCompleted += (group, trade) =>
        {
            trade.Commission = trade.Contracts * 2 * _cfg.CommissionPerSide;
            trade.NetPnl = trade.GrossPnl - trade.Commission;
            trades.Add(trade);
        };

        var noopExecutor = new NoopExecutor();
        var engineConfig = _cfg.ToEngineConfig();
        var engine   = new ComposableEngine(noopExecutor, sink, prices, engineConfig, handler);

        // Only register enabled setups.
        // Collect distinct tickers for multi-ticker bar loading.
        var setupTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var setupCfg in _cfg.ToSetupConfigs())
        {
            if (setupCfg.Enabled)
            {
                engine.AddSetup(setupCfg);
                setupTickers.Add(setupCfg.Ticker);
            }
        }

        // Enable tick mode: bar-level entry/exit skipped in ProcessBarAsync;
        // instead, four OHLC ticks per input bar drive entries and exits.
        engine.EnableTickMode();

        // ── Multi-session setup ──────────────────────────────────────────
        var sessions = _cfg.Sessions ?? SessionConfig.CreateDefaults(_cfg);
        var backtestSession = _btCfg.BacktestSession ?? "NY";
        if (!string.Equals(backtestSession, "All", StringComparison.OrdinalIgnoreCase))
        {
            var target = Enum.Parse<SessionId>(backtestSession, ignoreCase: true);
            sessions = sessions
                .Where(s => s.SessionId == target)
                .Select(s => { s.Enabled = true; return s; })
                .ToList();
        }
        var sessionMgr   = new SessionManager(sessions);
        var tz           = TimeZoneInfo.FindSystemTimeZoneById(_cfg.Timezone);
        DateTime lastDailyReset = DateTime.MinValue;
        bool betweenSessions = true;  // Start in gap; first SessionStarted will clear it

        int tfMinutes = Math.Max(1, _btCfg.ExecutionTFMinutes);

        // ── Per-ticker execution-TF bucket state ─────────────────────
        var buckets = new Dictionary<string, BucketState>(StringComparer.OrdinalIgnoreCase);
        int tfBarsOut = 0;   // completed TF bars emitted (global warmup counter)

        await foreach (var (ticker, bar) in taggedBars.WithCancellation(ct))
        {
            // ── Session transitions (based on bar time, ticker-independent) ──
            var local     = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, tz);
            var localTime = TimeOnly.FromDateTime(local);
            var tradingDate = _cfg.TradingDate(local);

            if (tradingDate != lastDailyReset)
            {
                lastDailyReset = tradingDate;
                engine.ResetDaily();
            }

            var (transition, session) = sessionMgr.CheckTransition(localTime);
            switch (transition)
            {
                case TransitionType.SessionStarted:
                    engine.Reconfigure(session!.ToLegacyConfig(_cfg), session.SessionId);
                    betweenSessions = false;
                    break;
                case TransitionType.SessionEnded:
                    await engine.ForceExitAllAsync(bar.Time);
                    engine.SetIdle();
                    betweenSessions = true;
                    break;
            }

            // ── Per-ticker bucket aggregation ────────────────────────────
            if (!buckets.TryGetValue(ticker, out var bkt))
            {
                bkt = new BucketState();
                buckets[ticker] = bkt;
            }

            var key      = BucketStart(bar.Time, tfMinutes);
            bool newBkt  = bkt.BucketKey == null || key != bkt.BucketKey;

            if (newBkt && bkt.BucketKey != null)
            {
                // ── Emit the completed TF bucket for this ticker ─────
                await EmitBucket(engine, prices, bkt, ticker, tfBarsOut, betweenSessions, groupExec, ct);
                tfBarsOut++;

                // Start new bucket
                bkt.BucketKey = key;
                bkt.Open = bar.Open; bkt.High = bar.High; bkt.Low = bar.Low; bkt.Close = bar.Close; bkt.Volume = bar.Volume;
                bkt.Pending.Clear();
            }
            else if (bkt.BucketKey == null)
            {
                // First bar for this ticker
                bkt.BucketKey = key;
                bkt.Open = bar.Open; bkt.High = bar.High; bkt.Low = bar.Low; bkt.Close = bar.Close; bkt.Volume = bar.Volume;
            }
            else
            {
                // Extend current bucket
                if (bar.High > bkt.High) bkt.High = bar.High;
                if (bar.Low  < bkt.Low)  bkt.Low  = bar.Low;
                bkt.Close   = bar.Close;
                bkt.Volume += bar.Volume;
            }

            // Queue tick data for live-phase buckets only
            if (tfBarsOut >= _btCfg.WarmupBars)
                bkt.Pending.Add((bar.Open, bar.High, bar.Low, bar.Close, bar.Time));
        }

        // ── Emit final (possibly incomplete) TF buckets for each ticker ──
        foreach (var (ticker, bkt) in buckets)
        {
            if (bkt.BucketKey != null)
            {
                await EmitBucket(engine, prices, bkt, ticker, tfBarsOut, betweenSessions, groupExec, ct);
                tfBarsOut++;
            }
        }

        _log.LogInformation("Backtest complete. {TfBars} TF bars processed, {Trades} trades.", tfBarsOut, trades.Count);
        return BacktestResultCalculator.Calculate(trades, _cfg, _btCfg);
    }

    /// <summary>Emit a completed execution-TF bucket: fire OHLC ticks then process bar.</summary>
    private async Task EmitBucket(
        ComposableEngine engine, InMemoryPriceProvider prices,
        BucketState bkt, string ticker,
        int tfBarsOut, bool betweenSessions,
        BacktestGroupOrderExecutor groupExec,
        CancellationToken ct)
    {
        var tfBar    = new Bar(bkt.BucketKey!.Value, bkt.Open, bkt.High, bkt.Low, bkt.Close, bkt.Volume);
        bool isWarmup = tfBarsOut < _btCfg.WarmupBars;

        if (isWarmup || betweenSessions)
        {
            if (betweenSessions) engine.ClearIdle();
            await engine.WarmupBarAsync(tfBar, ticker, ct);
            if (betweenSessions) engine.SetIdle();
        }
        else
        {
            // 1. Fire accumulated 1-min OHLC ticks for entry/exit evaluation.
            foreach (var (o, h, l, c, t) in bkt.Pending)
            {
                prices.UpdatePrice(ticker, o);
                await engine.ProcessPriceTickAsync(o, t, ticker);
                await groupExec.EvaluateFillsAsync(o, t, ticker);
                if (c >= o)
                {   // Bullish: O → L → H → C
                    prices.UpdatePrice(ticker, l);
                    await engine.ProcessPriceTickAsync(l, t.AddSeconds(15), ticker);
                    await groupExec.EvaluateFillsAsync(l, t.AddSeconds(15), ticker);
                    prices.UpdatePrice(ticker, h);
                    await engine.ProcessPriceTickAsync(h, t.AddSeconds(30), ticker);
                    await groupExec.EvaluateFillsAsync(h, t.AddSeconds(30), ticker);
                }
                else
                {   // Bearish: O → H → L → C
                    prices.UpdatePrice(ticker, h);
                    await engine.ProcessPriceTickAsync(h, t.AddSeconds(15), ticker);
                    await groupExec.EvaluateFillsAsync(h, t.AddSeconds(15), ticker);
                    prices.UpdatePrice(ticker, l);
                    await engine.ProcessPriceTickAsync(l, t.AddSeconds(30), ticker);
                    await groupExec.EvaluateFillsAsync(l, t.AddSeconds(30), ticker);
                }
                prices.UpdatePrice(ticker, c);
                await engine.ProcessPriceTickAsync(c, t.AddSeconds(45), ticker);
                await groupExec.EvaluateFillsAsync(c, t.AddSeconds(45), ticker);
            }
            // 2. Process the completed TF bar to update indicators and arm state.
            //    Strategies armed by the bar will enter on the NEXT bucket's first
            //    1-min tick Open — matching live behavior (first available price).
            //    No post-bar tick: entries defer to next real price, same as live.
            prices.UpdatePrice(ticker, bkt.Close);
            await engine.ProcessBarAsync(tfBar, ticker, ct);
        }
    }

    /// <summary>Returns the start DateTime of the N-minute execution-TF bucket containing <paramref name="t"/>.</summary>
    private static DateTime BucketStart(DateTime t, int tfMinutes)
    {
        int totalMins  = t.Hour * 60 + t.Minute;
        int bucketMins = totalMins / tfMinutes * tfMinutes;
        return t.Date.AddMinutes(bucketMins);
    }

    /// <summary>Per-ticker bucket state for execution-TF aggregation.</summary>
    private class BucketState
    {
        public DateTime? BucketKey;
        public decimal Open, High, Low, Close;
        public long Volume;
        public readonly List<(decimal O, decimal H, decimal L, decimal C, DateTime T)> Pending = new();
    }
}

// ── Noop Executor (legacy IOrderExecutor — signals go through BrokerEventHandler) ──
internal class NoopExecutor : IOrderExecutor
{
    public Task<decimal?> OnEntrySignalAsync(EntrySignal sig) => Task.FromResult<decimal?>(null);
}

// ── Group Order Executor (WSS-style fill simulation for backtest) ──
internal class BacktestGroupOrderExecutor : IGroupOrderExecutor
{
    private readonly BacktestConfig _btCfg;
    private readonly StrategyConfig _cfg;

    /// <summary>Synchronous event delivery callback (replaces async Channel).</summary>
    public Func<OrderEvent, Task>? OnEvent { get; set; }

    // Internal order book: groupId → orderId → leg state
    private readonly Dictionary<string, Dictionary<string, LegState>> _ordersByGroup = new();
    // groupId → ticker (for filtering EvaluateFills by instrument)
    private readonly Dictionary<string, string> _groupTickers = new(StringComparer.OrdinalIgnoreCase);

    private class LegState
    {
        public string OrderId = "";
        public string GroupOrderId = "";
        public LegType LegType;
        public string Action = ""; // BUY | SELL
        public int Quantity;
        public decimal? LimitPrice;
        public decimal? StopPrice;
        public string Status = "WORKING";
    }

    public BacktestGroupOrderExecutor(BacktestConfig btCfg, StrategyConfig cfg)
    { _btCfg = btCfg; _cfg = cfg; }

    public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
    {
        var groupId = Guid.NewGuid().ToString("N")[..8];
        bool isLong = sig.Direction == Direction.Long;
        var exitAction = isLong ? "SELL" : "BUY";
        var entryAction = isLong ? "BUY" : "SELL";
        var ticker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : sig.Setup.ToString();
        var setupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString();
        var usePartial = sig.UsePartial;
        var partialCts = usePartial
            ? (sig.PartialContracts > 0 ? sig.PartialContracts : sig.TotalContracts / 2)
            : 0;
        var remainCts = sig.TotalContracts - partialCts;

        var group = new GroupOrder
        {
            GroupOrderId = groupId,
            SetupId = setupId,
            Ticker = ticker,
            Direction = sig.Direction,
            TotalContracts = sig.TotalContracts,
            PartialContracts = partialCts,
            PointValue = _cfg.PointValue,
            UseBe = sig.UseBe,
            Status = GroupOrderStatus.Pending,
            Broker = "Backtest",
            CreatedAt = sig.Time,
            SessionId = sig.SessionId,
        };

        var entryLeg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-e", LegType = LegType.Entry, OrderType = sig.OrderType, Action = entryAction, Quantity = sig.TotalContracts, Price = sig.Entry };
        var tg2Leg   = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-t2", LegType = LegType.Tg2, OrderType = "Limit", Action = exitAction, Quantity = remainCts, Price = sig.Tg2Price };
        var stopLeg  = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-s", LegType = LegType.Stop, OrderType = "Stop", Action = exitAction, Quantity = sig.TotalContracts, Price = sig.Stop };
        group.Legs.AddRange(new[] { entryLeg, tg2Leg, stopLeg });

        // Register in internal order book
        var legs = new Dictionary<string, LegState>
        {
            [entryLeg.OrderId] = new() { OrderId = entryLeg.OrderId, GroupOrderId = groupId, LegType = LegType.Entry, Action = entryAction, Quantity = sig.TotalContracts, LimitPrice = sig.OrderType == "Limit" ? sig.Entry : null },
            [tg2Leg.OrderId]   = new() { OrderId = tg2Leg.OrderId, GroupOrderId = groupId, LegType = LegType.Tg2, Action = exitAction, Quantity = remainCts, LimitPrice = sig.Tg2Price },
            [stopLeg.OrderId]  = new() { OrderId = stopLeg.OrderId, GroupOrderId = groupId, LegType = LegType.Stop, Action = exitAction, Quantity = sig.TotalContracts, StopPrice = sig.Stop },
        };

        // Only add tg1 leg when UsePartial is enabled
        if (usePartial)
        {
            var tg1Leg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-t1", LegType = LegType.Tg1, OrderType = "Limit", Action = exitAction, Quantity = partialCts, Price = sig.Tg1Price };
            group.Legs.Add(tg1Leg);
            legs[tg1Leg.OrderId] = new() { OrderId = tg1Leg.OrderId, GroupOrderId = groupId, LegType = LegType.Tg1, Action = exitAction, Quantity = partialCts, LimitPrice = sig.Tg1Price };
        }
        _ordersByGroup[groupId] = legs;
        _groupTickers[groupId] = ticker;

        // Apply slippage to entry fill price
        var fillPrice = ApplySlip(sig.Entry, isLong);

        // Market entry: fill immediately by setting group state directly.
        // No event fired — PlaceEntryAsync activates after RegisterGroup.
        // (Firing an event here would fail: group not yet in _active dictionary.)
        if (sig.OrderType != "Limit")
        {
            legs[entryLeg.OrderId].Status = "FILLED";
            entryLeg.Status = OrderLegStatus.Filled;
            entryLeg.FillPrice = fillPrice;
            entryLeg.FillTime = sig.Time;
            group.Status = GroupOrderStatus.Active;
            group.EntryPrice = fillPrice;
        }

        return Task.FromResult<GroupOrder?>(group);
    }

    public Task ModifyOrderAsync(string orderId, decimal? newPrice, int? newQty)
    {
        // No event fired — BrokerEventHandler itself called this, so re-entering
        // HandleEventAsync would deadlock on the per-group semaphore.
        foreach (var (_, legs) in _ordersByGroup)
        {
            if (legs.TryGetValue(orderId, out var leg))
            {
                if (newPrice.HasValue)
                {
                    if (leg.StopPrice.HasValue) leg.StopPrice = newPrice;
                    else leg.LimitPrice = newPrice;
                }
                if (newQty.HasValue) leg.Quantity = newQty.Value;
                return Task.CompletedTask;
            }
        }
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(string orderId)
    {
        // No event fired — same re-entrancy reason as ModifyOrderAsync.
        foreach (var (_, legs) in _ordersByGroup)
        {
            if (legs.TryGetValue(orderId, out var leg) && leg.Status == "WORKING")
            {
                leg.Status = "CANCELED";
                return Task.CompletedTask;
            }
        }
        return Task.CompletedTask;
    }

    public Task PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
    {
        return Task.CompletedTask; // No-op in backtest
    }

    /// <summary>Evaluate fills for WORKING orders on the given ticker against current price.</summary>
    public async Task EvaluateFillsAsync(decimal price, DateTime utcNow, string ticker)
    {
        if (price <= 0) return;
        foreach (var (groupId, legs) in _ordersByGroup)
        {
            // Only evaluate orders for the matching ticker
            if (!_groupTickers.TryGetValue(groupId, out var grpTicker)
                || !string.Equals(grpTicker, ticker, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var leg in legs.Values.Where(l => l.Status == "WORKING").ToList())
            {
                bool fills = leg.Action == "BUY"
                    ? (leg.StopPrice.HasValue && price >= leg.StopPrice)
                   || (leg.LimitPrice.HasValue && price <= leg.LimitPrice)
                    : (leg.StopPrice.HasValue && price <= leg.StopPrice)
                   || (leg.LimitPrice.HasValue && price >= leg.LimitPrice);

                if (!fills) continue;

                leg.Status = "FILLED";
                if (OnEvent != null)
                    await OnEvent(new OrderEvent(groupId, leg.OrderId, leg.LegType,
                        OrderLegStatus.Filled, price, leg.Quantity, null, null, utcNow));
            }
        }

        // Prune completed groups (all legs filled or canceled)
        var completedGroups = _ordersByGroup
            .Where(kv => kv.Value.Values.All(l => l.Status != "WORKING"))
            .Select(kv => kv.Key).ToList();
        foreach (var gid in completedGroups)
        {
            _ordersByGroup.Remove(gid);
            _groupTickers.Remove(gid);
        }
    }

    private decimal ApplySlip(decimal price, bool isBuy)
    {
        if (_btCfg.FillMode != FillMode.WithSlippage) return price;
        decimal slip = _btCfg.SlippageTicks * _cfg.TickSize;
        return isBuy ? price + slip : price - slip;
    }
}

// ── Event Sink ────────────────────────────────────────────────
internal class BacktestSink : IStrategyEventSink
{
    public Task OnEntryAsync(EntrySignal s) => Task.CompletedTask;
    public Task OnExitAsync(TradeRecord t) => Task.CompletedTask; // Trades collected via BrokerEventHandler.OnTradeCompleted
    public Task OnSnapshotAsync(EngineSnapshot snap) => Task.CompletedTask;
}

// ── Price Provider ────────────────────────────────────────────
internal class InMemoryPriceProvider : ILastPriceProvider
{
    private readonly Dictionary<string, decimal> _p = new();
    public decimal GetLastPrice(string t) => _p.TryGetValue(t, out var v) ? v : 0;
    public void UpdatePrice(string t, decimal v) => _p[t] = v;
}
