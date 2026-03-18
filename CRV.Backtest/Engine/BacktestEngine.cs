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

/// <summary>Runs historical bars through OrbStrategyEngine and collects trade results.</summary>
public class BacktestEngine
{
    private readonly StrategyConfig _cfg;
    private readonly BacktestConfig _btCfg;
    private readonly ILogger        _log;

    public BacktestEngine(StrategyConfig cfg, BacktestConfig btCfg, ILogger<BacktestEngine> log)
    { _cfg = cfg; _btCfg = btCfg; _log = log; }

    /// <summary>
    /// Run the backtest over <paramref name="bars"/>, which may be raw 1-minute bars (CSV)
    /// or pre-resampled execution-TF bars (API sources).
    /// <para>
    /// Tick simulation: for each input bar the engine receives four synthetic price ticks
    /// (Open, High, Low, Close) so that entry and exit levels are evaluated at every
    /// bar's intra-period prices — not just at the execution-TF bar's close.
    /// Arm-state updates (ORB, ATR, VWAP, arm/de-arm) still happen at execution-TF
    /// bar close, giving "armed by execution TF, filled by minute" semantics.
    /// </para>
    /// </summary>
    public async Task<BacktestResult> RunAsync(IAsyncEnumerable<Bar> bars, CancellationToken ct = default)
    {
        var trades   = new List<TradeRecord>();
        var sink     = new BacktestSink(trades);
        var prices   = new InMemoryPriceProvider();
        var executor = new BacktestExecutor(_btCfg, _cfg, sink);
        var engine   = new OrbStrategyEngine(_cfg, executor, sink, prices,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

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

        int tfMinutes = Math.Max(1, _btCfg.ExecutionTFMinutes);

        // ── Inline execution-TF bucket state ──────────────────────
        DateTime? bucketKey = null;
        decimal   bOpen = 0, bHigh = 0, bLow = 0, bClose = 0;
        long      bVolume   = 0;
        int       tfBarsOut = 0;   // completed TF bars emitted so far

        // Accumulate per-bucket 1-min bar tick data; fired when the bucket closes
        var pending = new List<(decimal O, decimal H, decimal L, decimal C, DateTime T)>();

        await foreach (var bar in bars.WithCancellation(ct))
        {
            // ── Session transitions ────────────────────────────────────
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
                    break;
                case TransitionType.SessionEnded:
                    await engine.ForceExitAllAsync();
                    engine.SetIdle();
                    break;
            }
            // ──────────────────────────────────────────────────────────

            var key      = BucketStart(bar.Time, tfMinutes);
            bool newBkt  = bucketKey == null || key != bucketKey;

            if (newBkt && bucketKey != null)
            {
                // ── Emit the completed TF bucket ───────────────────
                var tfBar    = new Bar(bucketKey.Value, bOpen, bHigh, bLow, bClose, bVolume);
                bool isWarmup = tfBarsOut < _btCfg.WarmupBars;

                if (isWarmup)
                {
                    await engine.WarmupBarAsync(tfBar, ct);
                }
                else
                {
                    // 1. Fire accumulated 1-min OHLC ticks for entry/exit evaluation.
                    //    Each OHLC tick gets a distinct sub-second offset within the bar's minute
                    //    so entry and exit timestamps are distinguishable.
                    //    Tick order depends on bar direction:
                    //      Bullish (C>=O): O→L→H→C  (price dips first, then rallies)
                    //      Bearish (C<O):  O→H→L→C  (price rallies first, then sells off)
                    foreach (var (o, h, l, c, t) in pending)
                    {
                        prices.UpdatePrice(_cfg.Ticker, o);
                        await engine.ProcessPriceTickAsync(o, t);                       // Open  +0s
                        if (c >= o)
                        {   // Bullish: O → L → H → C
                            await engine.ProcessPriceTickAsync(l, t.AddSeconds(15));
                            await engine.ProcessPriceTickAsync(h, t.AddSeconds(30));
                        }
                        else
                        {   // Bearish: O → H → L → C
                            await engine.ProcessPriceTickAsync(h, t.AddSeconds(15));
                            await engine.ProcessPriceTickAsync(l, t.AddSeconds(30));
                        }
                        prices.UpdatePrice(_cfg.Ticker, c);
                        await engine.ProcessPriceTickAsync(c, t.AddSeconds(45));        // Close +45s
                    }
                    // 2. Process the completed TF bar to update indicators and arm state
                    prices.UpdatePrice(_cfg.Ticker, bClose);
                    await engine.ProcessBarAsync(tfBar, ct);
                }

                tfBarsOut++;
                pending.Clear();

                // Start new bucket
                bucketKey = key;
                bOpen = bar.Open; bHigh = bar.High; bLow = bar.Low; bClose = bar.Close; bVolume = bar.Volume;
            }
            else if (bucketKey == null)
            {
                // First bar ever
                bucketKey = key;
                bOpen = bar.Open; bHigh = bar.High; bLow = bar.Low; bClose = bar.Close; bVolume = bar.Volume;
            }
            else
            {
                // Extend current bucket with this bar's OHLCV
                if (bar.High > bHigh) bHigh = bar.High;
                if (bar.Low  < bLow)  bLow  = bar.Low;
                bClose   = bar.Close;
                bVolume += bar.Volume;
            }

            // Queue tick data for live-phase buckets only (warmup bars fire no ticks)
            if (tfBarsOut >= _btCfg.WarmupBars)
                pending.Add((bar.Open, bar.High, bar.Low, bar.Close, bar.Time));
        }

        // ── Emit final (possibly incomplete) TF bucket ────────────
        if (bucketKey != null)
        {
            var tfBar    = new Bar(bucketKey.Value, bOpen, bHigh, bLow, bClose, bVolume);
            bool isWarmup = tfBarsOut < _btCfg.WarmupBars;

            if (isWarmup)
            {
                await engine.WarmupBarAsync(tfBar, ct);
            }
            else
            {
                foreach (var (o, h, l, c, t) in pending)
                {
                    prices.UpdatePrice(_cfg.Ticker, o);
                    await engine.ProcessPriceTickAsync(o, t);
                    if (c >= o)
                    {   await engine.ProcessPriceTickAsync(l, t.AddSeconds(15));
                        await engine.ProcessPriceTickAsync(h, t.AddSeconds(30)); }
                    else
                    {   await engine.ProcessPriceTickAsync(h, t.AddSeconds(15));
                        await engine.ProcessPriceTickAsync(l, t.AddSeconds(30)); }
                    prices.UpdatePrice(_cfg.Ticker, c);
                    await engine.ProcessPriceTickAsync(c, t.AddSeconds(45));
                }
                prices.UpdatePrice(_cfg.Ticker, bClose);
                await engine.ProcessBarAsync(tfBar, ct);
            }
            tfBarsOut++;
        }

        _log.LogInformation("Backtest complete. {TfBars} TF bars processed, {Trades} trades.", tfBarsOut, trades.Count);
        return BacktestResultCalculator.Calculate(trades, _cfg, _btCfg);
    }

    /// <summary>Returns the start DateTime of the N-minute execution-TF bucket containing <paramref name="t"/>.</summary>
    private static DateTime BucketStart(DateTime t, int tfMinutes)
    {
        int totalMins  = t.Hour * 60 + t.Minute;
        int bucketMins = totalMins / tfMinutes * tfMinutes;
        return t.Date.AddMinutes(bucketMins);
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
    private readonly Dictionary<SetupId, EntrySignal> _open = new();

    public BacktestSink(List<TradeRecord> trades) => _trades = trades;

    public void RecordEntry(EntrySignal sig) => _open[sig.Setup] = sig;

    public void RecordExit(ExitSignal sig)
    {
        if (!_open.TryGetValue(sig.Setup, out var entry)) return;
        _open.Remove(sig.Setup);
        // Trade record built in OnExitAsync below
    }

    public Task OnEntryAsync(EntrySignal s)  { _open[s.Setup] = s; return Task.CompletedTask; }
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
