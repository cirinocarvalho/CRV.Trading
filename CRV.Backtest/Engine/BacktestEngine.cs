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
        var sink     = new BacktestSink(trades);
        var prices   = new InMemoryPriceProvider();
        var executor = new BacktestExecutor(_btCfg, _cfg, sink);
        var engineConfig = _cfg.ToEngineConfig();
        var engine   = new ComposableEngine(executor, sink, prices, engineConfig);

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
                await EmitBucket(engine, prices, bkt, ticker, tfBarsOut, betweenSessions, ct);
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
                await EmitBucket(engine, prices, bkt, ticker, tfBarsOut, betweenSessions, ct);
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
                if (c >= o)
                {   // Bullish: O → L → H → C
                    prices.UpdatePrice(ticker, l);
                    await engine.ProcessPriceTickAsync(l, t.AddSeconds(15), ticker);
                    prices.UpdatePrice(ticker, h);
                    await engine.ProcessPriceTickAsync(h, t.AddSeconds(30), ticker);
                }
                else
                {   // Bearish: O → H → L → C
                    prices.UpdatePrice(ticker, h);
                    await engine.ProcessPriceTickAsync(h, t.AddSeconds(15), ticker);
                    prices.UpdatePrice(ticker, l);
                    await engine.ProcessPriceTickAsync(l, t.AddSeconds(30), ticker);
                }
                prices.UpdatePrice(ticker, c);
                await engine.ProcessPriceTickAsync(c, t.AddSeconds(45), ticker);
            }
            // 2. Process the completed TF bar to update indicators and arm state
            prices.UpdatePrice(ticker, bkt.Close);
            await engine.ProcessBarAsync(tfBar, ticker, ct);

            // 3. Fire a post-bar tick at bar.Close so strategies armed by the bar
            //    can enter immediately at the confirmed close price.
            await engine.ProcessPriceTickAsync(bkt.Close, tfBar.Time.AddSeconds(1), ticker);
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

// ── Fill Simulator ────────────────────────────────────────────
internal class BacktestExecutor : IOrderExecutor
{
    private readonly BacktestConfig _btCfg;
    private readonly StrategyConfig _cfg;
    private readonly BacktestSink   _sink;

    public BacktestExecutor(BacktestConfig btCfg, StrategyConfig cfg, BacktestSink sink)
    { _btCfg = btCfg; _cfg = cfg; _sink = sink; }

    public Task<decimal?> OnEntrySignalAsync(EntrySignal sig)
    {
        var filled = sig with { Entry = ApplySlip(sig.Entry, sig.Direction == Direction.Long) };
        _sink.RecordEntry(filled);
        return Task.FromResult<decimal?>(null);
    }
    public Task OnPartialSignalAsync(PartialSignal sig) => Task.CompletedTask;
    public Task OnBESignalAsync(BESignal sig)           => Task.CompletedTask;
    public Task OnExitSignalAsync(ExitSignal sig)
    {
        var filled = sig with { ExitPrice = sig.Reason == ExitReason.Stop
            ? ApplySlip(sig.ExitPrice, false)
            : sig.ExitPrice };
        _sink.RecordExit(filled);
        return Task.CompletedTask;
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
    private readonly List<TradeRecord> _trades;
    private readonly Dictionary<string, EntrySignal> _open = new();

    public BacktestSink(List<TradeRecord> trades) => _trades = trades;

    public void RecordEntry(EntrySignal sig) => _open[sig.Setup.ToString()] = sig;

    public void RecordExit(ExitSignal sig)
    {
        var key = sig.Setup.ToString();
        if (!_open.TryGetValue(key, out var entry)) return;
        _open.Remove(key);
        // Trade record built in OnExitAsync below
    }

    public Task OnEntryAsync(EntrySignal s)  { _open[s.Setup.ToString()] = s; return Task.CompletedTask; }
    public Task OnPartialAsync(PartialSignal s) => Task.CompletedTask;
    public Task OnBEMoveAsync(BESignal s)       => Task.CompletedTask;

    public Task OnExitAsync(ExitSignal sig, TradeRecord completed)
    {
        _trades.Add(completed);
        return Task.CompletedTask;
    }

    public Task OnSnapshotAsync(EngineSnapshot snap) => Task.CompletedTask;
}

// ── Price Provider ────────────────────────────────────────────
internal class InMemoryPriceProvider : ILastPriceProvider
{
    private readonly Dictionary<string, decimal> _p = new();
    public decimal GetLastPrice(string t) => _p.TryGetValue(t, out var v) ? v : 0;
    public void UpdatePrice(string t, decimal v) => _p[t] = v;
}
