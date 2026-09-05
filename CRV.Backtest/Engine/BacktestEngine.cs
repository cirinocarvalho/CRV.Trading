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
    /// <summary>
    /// Defaults to <see cref="FillMode.WithSlippage"/>. <see cref="FillMode.AtTouch"/>
    /// books exact-price fills with no cost on the exit side at all, which is what let
    /// the backtest disagree with the live book by roughly $380 on execution alone.
    /// The frictionless modes remain available for comparing one run against another.
    /// </summary>
    public FillMode FillMode         { get; set; } = FillMode.WithSlippage;

    /// <summary>Ticks of adverse slippage on a market entry.</summary>
    public int      SlippageTicks    { get; set; } = 1;

    /// <summary>
    /// Ticks of adverse slippage when a stop is hit. Larger than entry slippage
    /// because a stop fires into a market that is already moving against the position:
    /// live, 16% of stop-outs cost more than the 1R they were sized for.
    /// </summary>
    public int      StopSlippageTicks { get; set; } = 4;
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
        var handler = new BrokerEventHandler(groupExec, _log) { IsBacktest = true };

        // Synchronous event delivery: executor → handler (Func<OrderEvent, Task>)
        groupExec.OnEvent = evt => handler.HandleEventAsync(evt);

        var noopExecutor = new NoopExecutor();
        var engineConfig = _cfg.ToEngineConfig();
        var engine   = new ComposableEngine(noopExecutor, sink, prices, engineConfig, handler);

        // Capture completed trades with commission and feed RiskManager
        handler.OnTradeCompleted += (group, trade) =>
        {
            trade.Commission = trade.Contracts * 2 * _cfg.CommissionPerSide;
            trade.NetPnl = trade.GrossPnl - trade.Commission;
            trades.Add(trade);
            engine.Risk.RecordTrade(trade.NetPnl);
        };

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

        int runTfFallback = Math.Max(1, _btCfg.ExecutionTFMinutes);
        // Per-ticker TF: basket override wins, else the backtest run TF.
        var tickerTf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int TfFor(string t)
        {
            if (!tickerTf.TryGetValue(t, out var tf))
            {
                tf = _cfg.TfMinutesFor(t, runTfFallback);
                tickerTf[t] = tf;
            }
            return tf;
        }

        // ── Per-ticker execution-TF bucket state ─────────────────────
        var buckets = new Dictionary<string, BucketState>(StringComparer.OrdinalIgnoreCase);
        int tfBarsOut = 0;   // completed TF bars emitted (global warmup counter)

        await foreach (var (ticker, bar) in taggedBars.WithCancellation(ct))
        {
            var local     = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, tz);
            var localTime = TimeOnly.FromDateTime(local);
            var tradingDate = _cfg.TradingDate(local);

            if (tradingDate != lastDailyReset)
            {
                lastDailyReset = tradingDate;
                engine.ResetDaily();
            }

            // ── Per-ticker bucket aggregation ────────────────────────────
            // Emit completed buckets BEFORE session transitions so that
            // the last bucket of a session processes its ticks while the
            // session is still active (betweenSessions == false).
            if (!buckets.TryGetValue(ticker, out var bkt))
            {
                bkt = new BucketState();
                buckets[ticker] = bkt;
            }

            var key      = BucketStart(bar.Time, TfFor(ticker));
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

            // ── Session transitions (after bucket emission) ──────────────
            var (transition, session) = sessionMgr.CheckTransition(localTime);
            switch (transition)
            {
                case TransitionType.SessionStarted:
                    engine.Reconfigure(session!.ToLegacyConfig(_cfg), session.SessionId);
                    betweenSessions = false;
                    break;
                case TransitionType.SessionEnded:
                    // Flush ALL pending buckets before the session ends so their
                    // ticks process while betweenSessions is still false.
                    // This ensures cutoff/exit logic fires for all tickers, not
                    // just whichever ticker's bar happened to arrive first at the
                    // session boundary.
                    foreach (var (flushTicker, flushBkt) in buckets)
                    {
                        if (flushBkt.BucketKey != null && flushBkt.Pending.Count > 0)
                        {
                            await EmitBucket(engine, prices, flushBkt, flushTicker,
                                tfBarsOut, false, groupExec, ct);
                            tfBarsOut++;
                            flushBkt.Pending.Clear();
                            flushBkt.BucketKey = null;
                        }
                    }
                    await engine.ForceExitAllAsync(bar.Time);
                    engine.SetIdle();
                    betweenSessions = true;
                    break;
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
    private readonly ExecutionModel _exec;

    /// <summary>Synchronous event delivery callback (replaces async Channel).</summary>
    public Func<OrderEvent, Task>? OnEvent { get; set; }

    // Internal order book: groupId → orderId → leg state
    private readonly Dictionary<string, Dictionary<string, LegState>> _ordersByGroup = new();
    // groupId → ticker (for filtering EvaluateFills by instrument)
    private readonly Dictionary<string, string> _groupTickers = new(StringComparer.OrdinalIgnoreCase);
    // Auto-trail simulation state per group
    private readonly Dictionary<string, TrailSimState> _trailState = new();

    private class TrailSimState
    {
        public bool Enabled { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Trigger { get; set; }
        public decimal Freq { get; set; }
        public decimal EntryPrice { get; set; }
        public bool Activated { get; set; }
        public decimal HighWater { get; set; }
    }

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
    { _btCfg = btCfg; _cfg = cfg; _exec = new ExecutionModel(btCfg, cfg.TickSizeFor); }

    public Task<GroupOrder?> OnEntrySignalAsync(EntrySignal sig)
    {
        var groupId = Guid.NewGuid().ToString("N")[..8];
        bool isLong = sig.Direction == Direction.Long;
        var exitAction = isLong ? "SELL" : "BUY";
        var entryAction = isLong ? "BUY" : "SELL";
        var ticker = !string.IsNullOrEmpty(sig.Ticker) ? sig.Ticker : sig.Setup.ToString();
        var setupId = !string.IsNullOrEmpty(sig.SetupLabel) ? sig.SetupLabel : sig.Setup.ToString();

        // Resolve effective N-bracket list (legacy Tg1/Tg2 auto-build applies for null Brackets)
        var bracketList = sig.ResolveBrackets();
        if (bracketList.Count == 0)
            bracketList = new[] { new BracketLeg(sig.Tg2Price, sig.TotalContracts, MoveBe: false) };
        if (bracketList.Count > 4) bracketList = bracketList.Take(4).ToList();

        bool usePartial = bracketList.Count >= 2;
        var partialCts = usePartial ? bracketList[0].Qty : 0;
        var remainCts  = sig.TotalContracts - partialCts;

        var group = new GroupOrder
        {
            GroupOrderId = groupId,
            SetupId = setupId,
            Ticker = ticker,
            Direction = sig.Direction,
            TotalContracts = sig.TotalContracts,
            PartialContracts = partialCts,
            PointValue = sig.PointValue > 0 ? sig.PointValue : _cfg.PointValue,
            UseBe = sig.UseBe,
            InitialStopPrice = sig.Stop,
            Status = GroupOrderStatus.Pending,
            Broker = "Backtest",
            CreatedAt = sig.Time,
            SessionId = sig.SessionId,
        };

        static LegType TargetLegTypeForIndex(int i) => i switch
        {
            0 => LegType.Tg1,
            1 => LegType.Tg2,
            2 => LegType.Tg3,
            3 => LegType.Tg4,
            _ => LegType.Tg4,
        };

        var entryLeg = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-e", LegType = LegType.Entry, OrderType = sig.OrderType, Action = entryAction, Quantity = sig.TotalContracts, Price = sig.Entry };
        var stopLeg  = new OrderLeg { GroupOrderId = groupId, OrderId = $"{groupId}-s", LegType = LegType.Stop,  OrderType = "Stop",   Action = exitAction, Quantity = sig.TotalContracts, Price = sig.Stop };
        group.Legs.Add(entryLeg);

        var legs = new Dictionary<string, LegState>
        {
            [entryLeg.OrderId] = new() { OrderId = entryLeg.OrderId, GroupOrderId = groupId, LegType = LegType.Entry, Action = entryAction, Quantity = sig.TotalContracts, LimitPrice = sig.OrderType == "Limit" ? sig.Entry : null },
            [stopLeg.OrderId]  = new() { OrderId = stopLeg.OrderId,  GroupOrderId = groupId, LegType = LegType.Stop,  Action = exitAction, Quantity = sig.TotalContracts, StopPrice = sig.Stop },
        };

        // Add a target leg per bracket
        for (int i = 0; i < bracketList.Count; i++)
        {
            var bl = bracketList[i];
            if (bl.Qty <= 0) continue;
            var legType = TargetLegTypeForIndex(i);
            var legId = $"{groupId}-t{i + 1}";
            var tgLeg = new OrderLeg
            {
                GroupOrderId = groupId, OrderId = legId, LegType = legType,
                OrderType = "Limit", Action = exitAction,
                Quantity = bl.Qty, Price = bl.TargetPrice
            };
            group.Legs.Add(tgLeg);
            legs[legId] = new()
            {
                OrderId = legId, GroupOrderId = groupId, LegType = legType,
                Action = exitAction, Quantity = bl.Qty, LimitPrice = bl.TargetPrice
            };
        }

        group.Legs.Add(stopLeg);
        _ordersByGroup[groupId] = legs;
        _groupTickers[groupId] = ticker;

        // Only the market branch below uses this: a limit entry fills at its limit
        // when EvaluateFills sees the price, and never worse.
        var fillPrice = _exec.EntryFill(sig.Entry, isBuy: isLong, isLimit: false, ticker);

        // Market: fill immediately at sig.Entry.
        // Limit (all modes including Conservative): stay pending until
        // EvaluateFills detects price touching the entry level on a
        // subsequent tick. This is more realistic — with large TF bars
        // (15/30 min), the bar close can be well past the entry level,
        // and in live the limit would only fill if price returns to it.
        bool fillNow = sig.OrderType != "Limit";
        if (fillNow)
        {
            legs[entryLeg.OrderId].Status = "FILLED";
            entryLeg.Status = OrderLegStatus.Filled;
            entryLeg.FillPrice = fillPrice;
            entryLeg.FillTime = sig.Time;
            group.Status = GroupOrderStatus.Active;
            group.EntryPrice = fillPrice;
        }

        // Copy auto-trail config for backtest simulation
        if (sig.AutoTrailStopLoss.HasValue)
        {
            group.AutoTrailStopLoss = sig.AutoTrailStopLoss;
            group.AutoTrailFreq = sig.AutoTrailFreq;
            group.AutoTrailTrigger = sig.AutoTrailTrigger
                ?? (sig.UsePartial ? Math.Abs(sig.Tg1Price - sig.Entry) : 0m);

            _trailState[groupId] = new TrailSimState
            {
                Enabled = true,
                StopLoss = sig.AutoTrailStopLoss.Value,
                Trigger = sig.AutoTrailTrigger ?? Math.Abs(sig.Tg1Price - sig.Entry),
                Freq = sig.AutoTrailFreq!.Value,
                EntryPrice = fillPrice,
            };
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

    public Task<decimal> PlaceMarketCloseAsync(string ticker, Direction direction, int qty)
    {
        return Task.FromResult(0m); // No-op in backtest
    }

    /// <summary>Evaluate fills for WORKING orders on the given ticker against current price.</summary>
    public async Task EvaluateFillsAsync(decimal price, DateTime utcNow, string ticker)
    {
        if (price <= 0) return;

        // Evaluate legs in priority order: Entry → Tg1 → Tg2 → Tg3 → Stop → Tg4.
        // Process one fill per group per tick so that earlier partials always
        // resolve before later targets when multiple levels cross in the same bar.
        // Stop sits between the penultimate and final target — the safety net below
        // still forces earlier targets to fill first if the tick skips them.
        var legOrder = new[]
        {
            LegType.Entry, LegType.Tg1, LegType.Tg2, LegType.Tg3, LegType.Stop, LegType.Tg4
        };

        foreach (var (groupId, legs) in _ordersByGroup)
        {
            // Only evaluate orders for the matching ticker
            if (!_groupTickers.TryGetValue(groupId, out var grpTicker)
                || !string.Equals(grpTicker, ticker, StringComparison.OrdinalIgnoreCase))
                continue;

            // Don't evaluate exit legs (Tg1/Stop/Tg2) until Entry has filled.
            // With limit entries, the price may reach a target level before the
            // entry limit is hit — filling exits before entry is nonsensical.
            var entryState = legs.Values.FirstOrDefault(l => l.LegType == LegType.Entry);
            bool entryFilled = entryState == null || entryState.Status == "FILLED";

            foreach (var lt in legOrder)
            {
                if (!entryFilled && lt != LegType.Entry) continue;

                var leg = legs.Values.FirstOrDefault(l => l.LegType == lt && l.Status == "WORKING");
                if (leg == null) continue;

                bool fills = leg.Action == "BUY"
                    ? (leg.StopPrice.HasValue && price >= leg.StopPrice)
                   || (leg.LimitPrice.HasValue && price <= leg.LimitPrice)
                    : (leg.StopPrice.HasValue && price <= leg.StopPrice)
                   || (leg.LimitPrice.HasValue && price >= leg.LimitPrice);

                if (!fills) continue;

                leg.Status = "FILLED";
                // Fill at the order level rather than the tick that triggered it, then
                // charge the stop its slippage: a touched stop is a market order.
                var orderLevel = leg.LimitPrice ?? leg.StopPrice ?? price;
                var fillPrice  = leg.StopPrice.HasValue && !leg.LimitPrice.HasValue
                    ? _exec.ExitFill(leg.LegType, leg.Action == "BUY", orderLevel, ticker)
                    : orderLevel;

                // Safety net: if a later target is about to fill but an earlier
                // target in the sequence is still WORKING, force-fill the earlier
                // target first so partial P&L accrues correctly.
                if (GroupOrder.IsTargetLeg(lt))
                {
                    var earlierTargets = new[] { LegType.Tg1, LegType.Tg2, LegType.Tg3 }
                        .TakeWhile(e => e != lt);
                    foreach (var earlier in earlierTargets)
                    {
                        var eLeg = legs.Values.FirstOrDefault(l => l.LegType == earlier && l.Status == "WORKING");
                        if (eLeg == null) continue;
                        Console.WriteLine($"[BT-SAFETY] {lt} fill for grp={groupId} but {earlier} still WORKING (limit={eLeg.LimitPrice}, price={price}) — forcing {earlier} fill first");
                        eLeg.Status = "FILLED";
                        var eFill = eLeg.LimitPrice ?? price;
                        if (OnEvent != null)
                            await OnEvent(new OrderEvent(groupId, eLeg.OrderId, earlier,
                                OrderLegStatus.Filled, eFill, eLeg.Quantity, null, null, utcNow));
                    }
                }

                if (OnEvent != null)
                    await OnEvent(new OrderEvent(groupId, leg.OrderId, leg.LegType,
                        OrderLegStatus.Filled, fillPrice, leg.Quantity, null, null, utcNow));
                break; // one fill per group per tick — let event handler process before next
            }

            // ── Auto-trail simulation ──────────────────────────────────
            if (!entryFilled) continue;

            var stopLegForTrail = legs.Values.FirstOrDefault(l => l.LegType == LegType.Stop && l.Status == "WORKING");
            if (stopLegForTrail == null) continue;

            if (!_trailState.TryGetValue(groupId, out var trail) || !trail.Enabled) continue;

            // Auto-trail applies to ALL brackets (Tg1, Tg2, ... TgN).
            // On Tradovate each bracket gets its own independent autoTrail dict.
            // In the backtest we simulate this with a single trailing stop for all
            // contracts — the trail arms when profitDistance >= trigger and
            // ratchets the stop for the entire position.

            bool trailIsLong = stopLegForTrail.Action == "SELL"; // Stop sells for long positions

            decimal profitDistance = trailIsLong
                ? price - trail.EntryPrice
                : trail.EntryPrice - price;

            if (!trail.Activated && profitDistance >= trail.Trigger)
            {
                trail.Activated = true;
                trail.HighWater = price;
            }

            if (trail.Activated)
            {
                // Update high water
                if ((trailIsLong && price > trail.HighWater) || (!trailIsLong && price < trail.HighWater))
                    trail.HighWater = price;

                // Compute new trailing stop
                decimal rawStop = trailIsLong
                    ? trail.HighWater - trail.StopLoss
                    : trail.HighWater + trail.StopLoss;

                // Snap to freq grid (if Freq is 0, skip snapping — use raw stop)
                decimal snappedStop;
                if (trail.Freq > 0)
                {
                    snappedStop = trailIsLong
                        ? Math.Floor(rawStop / trail.Freq) * trail.Freq
                        : Math.Ceiling(rawStop / trail.Freq) * trail.Freq;
                }
                else
                {
                    snappedStop = rawStop;
                }

                // Ratchet: only move stop in favorable direction
                decimal currentStop = stopLegForTrail.StopPrice ?? 0m;
                bool isBetter = trailIsLong ? snappedStop > currentStop : snappedStop < currentStop;

                if (isBetter)
                {
                    stopLegForTrail.StopPrice = snappedStop;
                }
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
            _trailState.Remove(gid);
        }
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
