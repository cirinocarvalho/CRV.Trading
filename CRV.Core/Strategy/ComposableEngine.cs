using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Modules;

namespace CRV.Core.Strategy;

/// <summary>
/// Thin coordinator that manages multiple <see cref="TickerGroup"/> instances,
/// routes signals from strategies to broker (<see cref="IOrderExecutor"/>) and
/// events (<see cref="IStrategyEventSink"/>), manages global risk via
/// <see cref="RiskManager"/>, and builds snapshots via <see cref="SnapshotAggregator"/>.
///
/// Designed as a drop-in replacement for the monolithic OrbStrategyEngine.
/// </summary>
public class ComposableEngine
{
    private readonly IOrderExecutor _executor;
    private readonly IStrategyEventSink _sink;
    private readonly ILastPriceProvider _prices;
    private EngineConfig _config;

    private readonly Dictionary<string, TickerGroup> _groups = new();
    private readonly Dictionary<SetupId, ISetupStrategy> _strategies = new();
    private readonly Dictionary<SetupId, string> _setupToGroupKey = new();

    private bool _idle;
    private bool _tickModeEnabled;
    private string _activeSessionId = "";

    // ── Alert ring buffer ──────────────────────────────────────────
    private const int AlertCapacity = 100;
    private readonly AlertEvent[] _alertRing = new AlertEvent[AlertCapacity];
    private int _alertHead;
    private int _alertCount;

    /// <summary>Global risk manager — exposed for tests and orchestrator access.</summary>
    public RiskManager Risk { get; } = new();

    /// <summary>Current active session id (e.g. "NY", "London").</summary>
    public string ActiveSessionId => _activeSessionId;

    public ComposableEngine(
        IOrderExecutor executor,
        IStrategyEventSink sink,
        ILastPriceProvider prices,
        EngineConfig config)
    {
        _executor = executor;
        _sink = sink;
        _prices = prices;
        _config = config;
    }

    // ── Setup management ────────────────────────────────────────────

    /// <summary>
    /// Register a strategy setup. Creates the strategy via <see cref="StrategyFactory"/>
    /// and assigns it to the correct <see cref="TickerGroup"/> based on ticker grouping.
    /// </summary>
    public void AddSetup(StrategySetupConfig config)
    {
        var strategy = StrategyFactory.Create(config);
        var groupKey = TickerGroup.GetGroupKey(config.Ticker);

        if (!_groups.TryGetValue(groupKey, out var group))
        {
            // Create a StrategyConfig for TickerGroup construction
            var stratCfg = BuildStrategyConfigFromEngine(_config);
            group = new TickerGroup(groupKey, stratCfg);
            _groups[groupKey] = group;
        }

        group.AddStrategy(strategy);
        _strategies[config.SetupId] = strategy;
        _setupToGroupKey[config.SetupId] = groupKey;
    }

    // ── Bar/Tick processing ─────────────────────────────────────────

    /// <summary>
    /// Process a bar through the full strategy pipeline: indicators, strategy logic,
    /// signal routing to broker/sink, snapshot publish.
    /// </summary>
    public async Task ProcessBarAsync(Bar bar, string ticker, CancellationToken ct = default)
    {
        if (_idle) return;

        var groupKey = TickerGroup.GetGroupKey(ticker);
        if (!_groups.TryGetValue(groupKey, out var group)) return;

        await group.ProcessBarAsync(bar);

        if (bar.IsConfirmed)
        {
            var signals = group.CollectAndClearSignals();
            await RouteSignalsAsync(signals);
        }

        await PublishSnapshotInternal();
    }

    /// <summary>
    /// Process a historical bar for indicator warm-up only.
    /// Updates indicators — does NOT run signal routing or publish snapshots.
    /// </summary>
    public async Task WarmupBarAsync(Bar bar, string ticker, CancellationToken ct = default)
    {
        if (_idle) return;

        var groupKey = TickerGroup.GetGroupKey(ticker);
        if (!_groups.TryGetValue(groupKey, out var group)) return;

        await group.ProcessBarAsync(bar);
        // Warmup: collect and discard signals to prevent them from leaking
        if (bar.IsConfirmed)
            group.CollectAndClearSignals();
    }

    /// <summary>
    /// Process a price tick (L1 data) for real-time entry/exit evaluation.
    /// Only active after <see cref="EnableTickMode"/> has been called.
    /// </summary>
    public async Task ProcessPriceTickAsync(decimal price, DateTime utcTime, string ticker)
    {
        if (_idle) return;
        if (!_tickModeEnabled) return;
        if (price <= 0) return;
        if (Risk.DdBreached) return;

        var groupKey = TickerGroup.GetGroupKey(ticker);
        if (!_groups.TryGetValue(groupKey, out var group)) return;

        await group.ProcessTickAsync(price, utcTime);

        var signals = group.CollectAndClearSignals();
        await RouteSignalsAsync(signals);
    }

    // ── Signal routing ──────────────────────────────────────────────

    /// <summary>
    /// Route signals from strategies to broker executor and event sink.
    /// Public for testing; normally called internally after bar/tick processing.
    /// </summary>
    public async Task RouteSignalsAsync(List<StrategySignals> signals)
    {
        foreach (var sig in signals)
        {
            var setup = sig.Strategy.SetupId;

            // ── Partial ──
            if (sig.Partial is { } psig)
            {
                await _executor.OnPartialSignalAsync(psig);
                await _sink.OnPartialAsync(psig);
                AddAlert("PARTIAL", setup, $"Partial @ {psig.PartialPrice:F2}", "yellow");
            }

            // ── Break-even ──
            if (sig.BE is { } besig)
            {
                await _executor.OnBESignalAsync(besig);
                await _sink.OnBEMoveAsync(besig);
                AddAlert("MOVE_BE", setup, $"Stop -> BE {besig.Entry:F2}", "yellow");
            }

            // ── Entry (risk check) ──
            if (sig.Entry is { } esig)
            {
                if (Risk.CanTrade(_config.UseDailyLossLimit, _config.MaxDailyLoss))
                {
                    var fill = await _executor.OnEntrySignalAsync(esig);
                    if (fill.HasValue && fill.Value != esig.Entry)
                        sig.Strategy.ApplyFill(fill.Value);
                    await _sink.OnEntryAsync(esig);
                    bool isLong = esig.Direction == Direction.Long;
                    AddAlert("ENTRY", setup,
                        $"{(isLong ? "LONG" : "SHORT")} {esig.Contracts}ct @ {esig.Entry:F2} | Stop {esig.Stop:F2} | Tgt {esig.Target:F2}",
                        isLong ? "green" : "red");
                }
            }

            // ── Exit ──
            if (sig.Exit is { } xsig)
            {
                // Build TradeRecord from strategy's pre-exit state
                var preTrade = sig.Strategy.GetActiveTrade(xsig.ExitPrice);
                var trade = BuildTradeRecord(sig.Strategy, xsig, preTrade);

                if (trade != null)
                {
                    Risk.RecordTrade(trade.NetPnl);
                    Risk.CanTrade(_config.UseDailyLossLimit, _config.MaxDailyLoss);
                }

                await _executor.OnExitSignalAsync(xsig);
                await _sink.OnExitAsync(xsig, trade ?? new TradeRecord { Setup = setup, Exit = xsig.ExitPrice });

                AddAlert("EXIT", setup,
                    $"{xsig.Reason} @ {xsig.ExitPrice:F2}",
                    xsig.Reason == ExitReason.Target ? "green" : "red");
            }
        }
    }

    // ── Lifecycle ───────────────────────────────────────────────────

    /// <summary>Put engine into idle mode — all processing calls become no-ops.</summary>
    public void SetIdle()
    {
        _idle = true;
        _activeSessionId = "";
    }

    /// <summary>Clear idle flag so warmup bars can flow through.</summary>
    public void ClearIdle() => _idle = false;

    /// <summary>Enable tick-based entry/exit evaluation.</summary>
    public void EnableTickMode() => _tickModeEnabled = true;

    /// <summary>
    /// Swap config for a new session. Resets session-scoped state but preserves daily-scoped state.
    /// </summary>
    public void Reconfigure(StrategyConfig cfg, SessionId sessionId)
    {
        _config = BuildEngineConfigFromStrategy(cfg);
        _activeSessionId = sessionId.ToString();

        foreach (var group in _groups.Values)
        {
            group.ReconfigureOrb(cfg.OrbStart, cfg.OrbEnd);
            group.Reset();
        }

        foreach (var (setupId, strategy) in _strategies)
            strategy.Reset();

        _idle = false;
    }

    /// <summary>
    /// Reconfigure with new global config and setup configs.
    /// </summary>
    public void Reconfigure(EngineConfig globalConfig, List<StrategySetupConfig> setupConfigs)
    {
        _config = globalConfig;

        // Reconfigure existing strategies
        foreach (var setupCfg in setupConfigs)
        {
            if (_strategies.TryGetValue(setupCfg.SetupId, out var strategy))
                strategy.Reconfigure(setupCfg);
        }
    }

    /// <summary>
    /// Daily-scope reset — clears risk, indicators, session levels.
    /// Called by SessionManager at the start of each trading day.
    /// </summary>
    public void ResetDaily()
    {
        Risk.ResetDay();

        foreach (var group in _groups.Values)
            group.Reset();

        foreach (var strategy in _strategies.Values)
            strategy.Reset();
    }

    /// <summary>Force-exit a specific setup immediately at the current market price.</summary>
    public async Task ForceExitSetupAsync(SetupId setupId)
    {
        if (!_strategies.TryGetValue(setupId, out var strategy)) return;
        if (!_setupToGroupKey.TryGetValue(setupId, out var groupKey)) return;

        var ticker = _config.Ticker;
        var px = _prices.GetLastPrice(ticker);
        if (px <= 0) return;

        if (strategy.IsActive)
        {
            var preTrade = strategy.GetActiveTrade(px);
            strategy.ForceExit(px, DateTime.UtcNow);

            // Collect and route the exit signal
            if (strategy.PendingExit is { } xsig)
            {
                var trade = BuildTradeRecord(strategy, xsig, preTrade);
                if (trade != null) Risk.RecordTrade(trade.NetPnl);
                await _executor.OnExitSignalAsync(xsig);
                await _sink.OnExitAsync(xsig, trade ?? new TradeRecord { Setup = setupId, Exit = xsig.ExitPrice });
            }
            strategy.ClearPendingSignals();
        }

        AddAlert("EXIT", setupId, $"Force exit @ {px:F2}", "orange");
        await PublishSnapshotInternal();
    }

    /// <summary>Force-exit all active trades.</summary>
    public async Task ForceExitAllAsync(DateTime? utcTime = null)
    {
        var ts = utcTime ?? DateTime.UtcNow;
        var ticker = _config.Ticker;
        var px = _prices.GetLastPrice(ticker);
        if (px <= 0) return;

        foreach (var (setupId, strategy) in _strategies)
        {
            if (!strategy.IsActive) continue;

            var preTrade = strategy.GetActiveTrade(px);
            strategy.ForceExit(px, ts);

            if (strategy.PendingExit is { } xsig)
            {
                var trade = BuildTradeRecord(strategy, xsig, preTrade);
                if (trade != null) Risk.RecordTrade(trade.NetPnl);
                await _executor.OnExitSignalAsync(xsig);
                await _sink.OnExitAsync(xsig, trade ?? new TradeRecord { Setup = setupId, Exit = xsig.ExitPrice });
            }
            strategy.ClearPendingSignals();
        }
    }

    // ── State ───────────────────────────────────────────────────────

    /// <summary>Build and return the current engine snapshot.</summary>
    public EngineSnapshot GetSnapshot()
    {
        var allStrategies = _strategies.Values.ToList();

        // Get state from the first (primary) group
        OrbState orbState = default;
        IndicatorState indState = default;
        ModuleState modState = default;
        decimal lastPrice = _prices.GetLastPrice(_config.Ticker);

        var primaryGroup = _groups.Values.FirstOrDefault();
        if (primaryGroup != null)
        {
            orbState = primaryGroup.GetOrbState();
            indState = primaryGroup.GetIndicatorState();
            modState = primaryGroup.GetModuleState();
        }

        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = allStrategies,
            Risk = Risk,
            Orb = orbState,
            Indicators = indState,
            OrbAtrRatio = orbState.AtrRatio,
            BarTime = DateTime.UtcNow,
            Ticker = _config.Ticker,
            IsLive = true,
            LastPrice = lastPrice,
            ActiveSessionId = _activeSessionId,
            OrbWindowStart = _config.OrbStart.ToString("HH:mm"),
            OrbWindowEnd = _config.OrbEnd.ToString("HH:mm"),
            DailyLossLimit = _config.MaxDailyLoss,
            CurrentSession = modState.CurrentSession.ToString(),
            SessionHigh = modState.SessionHigh,
            SessionLow = modState.SessionLow,
            PrevDayHigh = modState.PDH,
            PrevDayLow = modState.PDL,
            AsiaCompressed = modState.AsiaCompressed,
            VwapUpper1 = indState.VwapUpper1,
            VwapUpper2 = indState.VwapUpper2,
            VwapLower1 = indState.VwapLower1,
            VwapLower2 = indState.VwapLower2,
            VwapState = modState.VwapState,
            OpeningDriveBull = modState.IsBullDrive,
            OpeningDriveBear = modState.IsBearDrive,
            TrendScoreBull = modState.TrendDayBullScore,
            TrendScoreBear = modState.TrendDayBearScore,
            RecentAlerts = GetRecentAlerts(),
        };

        return SnapshotAggregator.Build(inputs);
    }

    /// <summary>Get active trade view for a specific setup, or null.</summary>
    public ActiveTradeView? GetActiveTrade(SetupId setupId)
    {
        if (!_strategies.TryGetValue(setupId, out var strategy)) return null;
        var px = _prices.GetLastPrice(_config.Ticker);
        return strategy.GetActiveTrade(px);
    }

    /// <summary>
    /// Publishes the current engine state to the event sink.
    /// Call after backfill so the dashboard shows warmed-up indicator values.
    /// </summary>
    public async Task PublishCurrentStateAsync()
    {
        var snap = GetSnapshot();
        await _sink.OnSnapshotAsync(snap);
    }

    // ── ORB cache support ───────────────────────────────────────────

    /// <summary>Restore ORB state from a cached snapshot.</summary>
    public void RestoreOrb(OrbStateCache cache)
    {
        foreach (var group in _groups.Values)
            group.RestoreOrb(cache.OrbHigh, cache.OrbLow, cache.CloseRelPct, cache.TradingDate, cache.OrbAtrRatio);
    }

    /// <summary>Seed ORB floor from cache when restarting inside ORB window.</summary>
    public void SeedOrbFloor(OrbStateCache cache)
    {
        foreach (var group in _groups.Values)
            group.SeedOrbFloor(cache.OrbHigh, cache.OrbLow);
    }

    /// <summary>Seed module history from daily bars.</summary>
    public void SeedModuleHistory(IReadOnlyList<Bar> dailyBars)
    {
        foreach (var group in _groups.Values)
            group.SeedModuleHistory(dailyBars);
    }

    // ── Private helpers ─────────────────────────────────────────────

    private async Task PublishSnapshotInternal()
    {
        var snap = GetSnapshot();
        await _sink.OnSnapshotAsync(snap);
    }

    private TradeRecord? BuildTradeRecord(ISetupStrategy strategy, ExitSignal xsig, ActiveTradeView? preTrade)
    {
        if (preTrade == null) return null;

        bool isLong = preTrade.Direction == Direction.Long;
        int cts = preTrade.Contracts;
        int remCts = xsig.Contracts;

        decimal pnl = (isLong ? xsig.ExitPrice - preTrade.Entry : preTrade.Entry - xsig.ExitPrice)
                      * _config.PointValue * remCts;

        if (preTrade.PartialFilled)
        {
            int partCts = cts - remCts;
            decimal partPnl = (isLong ? preTrade.Partial - preTrade.Entry : preTrade.Entry - preTrade.Partial)
                              * _config.PointValue * partCts;
            pnl += partPnl;
        }

        decimal comm = cts * 2 * _config.CommissionPerSide;
        decimal net = pnl - comm;
        decimal risk = Math.Abs(preTrade.Entry - preTrade.CurrentStop) * _config.PointValue * cts;
        decimal rMult = risk > 0 ? pnl / risk : 0;

        return new TradeRecord
        {
            Setup = strategy.SetupId,
            Direction = preTrade.Direction,
            Ticker = _config.Ticker,
            Contracts = cts,
            Entry = preTrade.Entry,
            InitialStop = preTrade.CurrentStop,
            Target = preTrade.Target,
            Partial = preTrade.Partial,
            Exit = xsig.ExitPrice,
            ExitReason = xsig.Reason,
            PartialFilled = preTrade.PartialFilled,
            PartialPrice = preTrade.Partial,
            GrossPnl = pnl,
            Commission = comm,
            NetPnl = net,
            RMultiple = rMult,
            EnteredAt = preTrade.EnteredAt,
            ExitedAt = xsig.Time,
            SessionId = string.IsNullOrEmpty(_activeSessionId) ? "NY" : _activeSessionId,
        };
    }

    private void AddAlert(string type, SetupId setup, string message, string color)
    {
        _alertRing[_alertHead] = new AlertEvent
        {
            Time = DateTime.UtcNow,
            Type = type,
            Setup = setup,
            Message = message,
            Color = color,
        };
        _alertHead = (_alertHead + 1) % AlertCapacity;
        if (_alertCount < AlertCapacity) _alertCount++;
    }

    private List<AlertEvent> GetRecentAlerts()
    {
        var alerts = new List<AlertEvent>(_alertCount);
        for (int i = 0; i < _alertCount; i++)
        {
            int idx = (_alertHead - _alertCount + i + AlertCapacity) % AlertCapacity;
            alerts.Add(_alertRing[idx]);
        }
        return alerts;
    }

    /// <summary>
    /// Build a StrategyConfig from EngineConfig for TickerGroup construction.
    /// TickerGroup needs a StrategyConfig for shared indicator/module initialization.
    /// </summary>
    private static StrategyConfig BuildStrategyConfigFromEngine(EngineConfig cfg) => new()
    {
        Ticker = cfg.Ticker,
        PointValue = cfg.PointValue,
        TickSize = cfg.TickSize,
        Timezone = cfg.Timezone,
        OrbStart = cfg.OrbStart,
        OrbEnd = cfg.OrbEnd,
        ExecutionTFMinutes = cfg.ExecutionTFMinutes,
        AllowBothSameBar = cfg.AllowBothSameBar,
        CommissionPerSide = cfg.CommissionPerSide,
        UseDailyLossLimit = cfg.UseDailyLossLimit,
        MaxDailyLoss = cfg.MaxDailyLoss,
    };

    /// <summary>Build an EngineConfig from a StrategyConfig (for Reconfigure path).</summary>
    private static EngineConfig BuildEngineConfigFromStrategy(StrategyConfig cfg) => new()
    {
        Ticker = cfg.Ticker,
        PointValue = cfg.PointValue,
        TickSize = cfg.TickSize,
        Timezone = cfg.Timezone,
        OrbStart = cfg.OrbStart,
        OrbEnd = cfg.OrbEnd,
        ExecutionTFMinutes = cfg.ExecutionTFMinutes,
        AllowBothSameBar = cfg.AllowBothSameBar,
        CommissionPerSide = cfg.CommissionPerSide,
        UseDailyLossLimit = cfg.UseDailyLossLimit,
        MaxDailyLoss = cfg.MaxDailyLoss,
        SessionStartHour = cfg.SessionStartHour,
        RthStart = cfg.RthStart,
        RthEnd = cfg.RthEnd,
        ExitMinutesBefore = cfg.ExitMinutesBefore,
    };
}
