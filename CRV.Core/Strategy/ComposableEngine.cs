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
    private readonly Dictionary<string, ISetupStrategy> _strategies = new();
    private readonly Dictionary<string, string> _setupToGroupKey = new();

    private bool _idle;
    private bool _tickModeEnabled;
    private string _activeSessionId = "";

    // ── WSS order management (optional — null when using legacy path) ──
    private readonly BrokerEventHandler? _brokerHandler;

    // ── Alert ring buffer ──────────────────────────────────────────
    private const int AlertCapacity = 100;
    private readonly AlertEvent[] _alertRing = new AlertEvent[AlertCapacity];
    private int _alertHead;
    private int _alertCount;

    /// <summary>Global risk manager — exposed for tests and orchestrator access.</summary>
    public RiskManager Risk { get; } = new();

    /// <summary>Current active session id (e.g. "NY", "London").</summary>
    public string ActiveSessionId => _activeSessionId;

    /// <summary>BrokerEventHandler for WSS order management (null when using legacy path).</summary>
    public BrokerEventHandler? BrokerHandler => _brokerHandler;

    public ComposableEngine(
        IOrderExecutor executor,
        IStrategyEventSink sink,
        ILastPriceProvider prices,
        EngineConfig config,
        BrokerEventHandler? brokerHandler = null)
    {
        _executor = executor;
        _sink = sink;
        _prices = prices;
        _config = config;
        _brokerHandler = brokerHandler;
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
            group = new TickerGroup(groupKey, stratCfg, _brokerHandler);
            _groups[groupKey] = group;
        }

        group.AddStrategy(strategy);
        _strategies[config.Id] = strategy;
        _setupToGroupKey[config.Id] = groupKey;
    }

    // ── Bar/Tick processing ─────────────────────────────────────────

    /// <summary>
    /// Process a bar through the full strategy pipeline: indicators, strategy logic,
    /// signal routing to broker/sink, snapshot publish.
    /// </summary>
    public async Task ProcessBarAsync(Bar bar, string ticker, CancellationToken ct = default)
    {
        if (_idle)
        {
            // Still publish snapshot so dashboard shows live prices during idle
            await PublishSnapshotInternal();
            return;
        }

        var groupKey = TickerGroup.GetGroupKey(ticker);
        if (!_groups.TryGetValue(groupKey, out var group)) return;

        await group.ProcessBarAsync(bar);

        if (bar.IsConfirmed)
        {
            SaveOrbCacheIfFormed(group);
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
        {
            SaveOrbCacheIfFormed(group);
            group.CollectAndClearSignals();
        }
    }

    /// <summary>
    /// Process a price tick (L1 data) for real-time entry/exit evaluation.
    /// Only active after <see cref="EnableTickMode"/> has been called.
    /// </summary>
    public async Task ProcessPriceTickAsync(decimal price, DateTime utcTime, string ticker)
    {
        if (_idle) return;  // prices still update via ILastPriceProvider (bar feed level)
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
    /// Route entry signals from strategies to broker executor and event sink.
    /// Public for testing; normally called internally after bar/tick processing.
    /// </summary>
    public async Task RouteSignalsAsync(List<StrategySignals> signals)
    {
        foreach (var sig in signals)
        {
            if (sig.Entry is not { } esig) continue;

            var setup = sig.Strategy.SetupId;
            var label = sig.Strategy.Id;

            if (string.IsNullOrEmpty(esig.SetupLabel) && !string.IsNullOrEmpty(label))
                esig = esig with { SetupLabel = label };

            if (!Risk.CanTrade(_config.UseDailyLossLimit, _config.MaxDailyLoss))
                continue;

            if (_brokerHandler != null)
                await _brokerHandler.PlaceEntryAsync(esig, sig.Strategy);
            else
                await _executor.OnEntrySignalAsync(esig);

            await _sink.OnEntryAsync(esig);

            bool isLong = esig.Direction == Direction.Long;
            AddAlert("ENTRY", setup,
                $"{(isLong ? "LONG" : "SHORT")} {esig.Contracts}ct @ {esig.Entry:F2} | Stop {esig.Stop:F2} | Tgt {esig.Target:F2}",
                isLong ? "green" : "red", label);
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

    /// <summary>
    /// Pre-set the active session ID so ORB cache writes during backfill
    /// use the correct session key (matches the key used on restore after session transition).
    /// </summary>
    public void SetActiveSessionId(string sessionId) => _activeSessionId = sessionId;

    /// <summary>
    /// Reset only trade counters on all strategies. Call after warmup completes
    /// so per-setup [N/Max] counts reflect the current session, not accumulated
    /// warmup trades from prior sessions. Preserves arm state and sticky signals.
    /// </summary>
    public void ResetWarmupCounters()
    {
        foreach (var strategy in _strategies.Values)
        {
            // Silently clear any active trades from warmup — those entries were
            // discarded (no broker order placed), so the strategy must not think
            // it has a real position. Disarm() resets _state to 0 (idle) without
            // producing exit signals or recording phantom trades.
            if (strategy.IsActive)
            {
                strategy.ClearPendingSignals();
                strategy.ResetSession();  // full reset: clears entry/stop/target/state
            }
            else
            {
                strategy.ResetTradeCounters();
            }
            strategy.ResetCutoff();
        }
        Risk.ResetDay();
    }

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
            // Snapshot prior session H/L into FalseBreakoutDetector BEFORE reset
            // clears session data. This enables SessionFakeout (Setup D) to detect
            // false breakouts of the prior session's range.
            group.OnSessionBoundary(sessionId);
            group.ReconfigureOrb(cfg.OrbStart, cfg.OrbEnd);
            group.Reset();
        }

        foreach (var (_, strategy) in _strategies)
            strategy.ResetSession();

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
            if (_strategies.TryGetValue(setupCfg.Id, out var strategy))
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
    public async Task ForceExitSetupAsync(string setupId)
    {
        if (!_strategies.TryGetValue(setupId, out var strategy)) return;

        if (_brokerHandler != null && _brokerHandler.HasActiveGroup(setupId))
            await _brokerHandler.ExitGroupAsync(setupId);

        strategy.ForceExit(_prices.GetLastPrice(strategy.Ticker), DateTime.UtcNow, ExitReason.Manual);

        AddAlert("EXIT", strategy.SetupId, $"Force exit", "orange");
        await PublishSnapshotInternal();
    }

    /// <summary>Force-exit all active trades.</summary>
    public async Task ForceExitAllAsync(DateTime? utcTime = null)
    {
        if (_brokerHandler != null)
            await _brokerHandler.ExitAllAsync();

        foreach (var (_, strategy) in _strategies)
            strategy.ResetSession();
    }

    // ── State ───────────────────────────────────────────────────────

    /// <summary>Build and return the current engine snapshot.</summary>
    public EngineSnapshot GetSnapshot()
    {
        var allStrategies = _strategies.Values.ToList();

        // Get state from the primary group (matching the global ticker)
        OrbState orbState = default;
        IndicatorState indState = default;
        ModuleState modState = default;
        decimal lastPrice = _prices.GetLastPrice(_config.Ticker);

        var primaryGroupKey = TickerGroup.GetGroupKey(_config.Ticker);
        var primaryGroup = _groups.TryGetValue(primaryGroupKey, out var pg) ? pg
                         : _groups.Values.FirstOrDefault();
        if (primaryGroup != null)
        {
            orbState = primaryGroup.GetOrbState();
            indState = primaryGroup.GetIndicatorState();
            modState = primaryGroup.GetModuleState();
        }

        // Build per-setup ORB state by looking up each strategy's ticker group
        var perSetupOrb = new Dictionary<string, OrbState>();
        foreach (var (id, strategy) in _strategies)
        {
            if (_setupToGroupKey.TryGetValue(id, out var gk) &&
                _groups.TryGetValue(gk, out var g))
            {
                perSetupOrb[id] = g.GetOrbState();
            }
        }

        // Build per-group snapshots for the dashboard ticker-group selector
        var groupSnapshots = new Dictionary<string, TickerGroupSnapshot>();
        foreach (var (key, group) in _groups)
        {
            // Find the representative ticker to get live price
            string? repTicker = null;
            foreach (var (id, gk) in _setupToGroupKey)
            {
                if (gk == key && _strategies.TryGetValue(id, out var strat)
                    && !string.IsNullOrEmpty(strat.Ticker))
                {
                    repTicker = strat.Ticker;
                    break;
                }
            }
            var livePrice = repTicker != null ? _prices.GetLastPrice(repTicker) : 0m;
            var gs = group.BuildGroupSnapshot(livePrice);
            if (repTicker != null) gs.Ticker = repTicker.TrimStart('/');
            groupSnapshots[key] = gs;
        }

        // Primary group FB data for flat snapshot fields (backward compat)
        TickerGroupSnapshot? primaryGS = null;
        if (primaryGroupKey != null)
            groupSnapshots.TryGetValue(primaryGroupKey, out primaryGS);

        var inputs = new SnapshotAggregator.Inputs
        {
            Strategies = allStrategies,
            Risk = Risk,
            Prices = _prices,
            BrokerHandler = _brokerHandler,
            Orb = orbState,
            Indicators = indState,
            OrbAtrRatio = orbState.AtrRatio,
            PerSetupOrb = perSetupOrb,
            BarTime = DateTime.UtcNow,
            Ticker = _config.Ticker,
            IsLive = true,
            LastPrice = lastPrice,
            SessionEnded = _idle,
            PastCutoff = _idle,  // when engine is idle, all setups are past cutoff
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
            // FalseBreakout from primary group
            FBOrbBreakoutActive = primaryGS?.FBOrbBreakoutActive ?? false,
            FBSessionBreakoutActive = primaryGS?.FBSessionBreakoutActive ?? false,
            FBOrbBarsInBreakout = primaryGS?.FBOrbBarsInBreakout ?? 0,
            FBSessionBarsInBreakout = primaryGS?.FBSessionBarsInBreakout ?? 0,
            FBOrbPenetrationDepth = primaryGS?.FBOrbPenetrationDepth ?? 0,
            FBSessionPenetrationDepth = primaryGS?.FBSessionPenetrationDepth ?? 0,
            FBOrbActivated = primaryGS?.FBOrbActivated ?? false,
            FBSessionActivated = primaryGS?.FBSessionActivated ?? false,
            IsCompoundFakeout = primaryGS?.IsCompoundFakeout ?? false,
            GroupSnapshots = groupSnapshots,
            RecentAlerts = GetRecentAlerts(),
        };

        var snapshot = SnapshotAggregator.Build(inputs);

        // Overlay WSS group order data onto snapshots when BrokerEventHandler is active
        if (_brokerHandler != null)
        {
            foreach (var setup in snapshot.Setups)
            {
                var group = _brokerHandler.GetGroupState(setup.Id);
                if (group == null) continue;

                var setupPrice = _strategies.TryGetValue(setup.Id, out var strat)
                    ? _prices.GetLastPrice(strat.Ticker)
                    : lastPrice;

                // If strategy doesn't have trade state (WSS owns it), build ActiveTradeView from GroupOrder
                if (setup.Trade == null && group.EntryPrice.HasValue)
                {
                    var stopLeg = group.GetLeg(LegType.Stop);
                    var tg1Leg = group.GetLeg(LegType.Tg1);
                    var tg2Leg = group.GetLeg(LegType.Tg2);
                    var remaining = group.Status == GroupOrderStatus.PartialFilled
                        ? group.TotalContracts - group.PartialContracts
                        : group.TotalContracts;

                    setup.Trade = new ActiveTradeView
                    {
                        Setup = strat?.SetupId ?? SetupId.A,
                        Direction = group.Direction,
                        Entry = group.EntryPrice.Value,
                        InitialStop = stopLeg?.Price ?? 0m,
                        CurrentStop = stopLeg?.Price ?? 0m,
                        Target = tg2Leg?.Price ?? 0m,
                        Partial = tg1Leg?.Price ?? 0m,
                        Contracts = group.TotalContracts,
                        RemainingContracts = remaining,
                        PartialFilled = group.Status == GroupOrderStatus.PartialFilled,
                        LastPrice = setupPrice,
                        UnrealizedPnl = _brokerHandler.GetUnrealizedPnl(setup.Id, setupPrice),
                        EnteredAt = group.CreatedAt,
                        Ticker = group.Ticker.TrimStart('/'),
                        PointValue = group.PointValue,
                    };
                }

                if (setup.Trade != null)
                {
                    setup.Trade.GroupStatus = group.Status.ToString();
                    // Always use BrokerEventHandler's P&L (includes accrued partials)
                    setup.Trade.UnrealizedPnl = _brokerHandler.GetUnrealizedPnl(setup.Id, setupPrice);
                }
            }
        }

        return snapshot;
    }

    /// <summary>Get bar history for a specific ticker group (for dashboard chart).</summary>
    public List<Bar> GetBarHistory(string groupKey)
    {
        if (_groups.TryGetValue(groupKey, out var group))
            return group.GetBarHistory();
        return new List<Bar>();
    }

    /// <summary>Get bar history with VWAP for a specific ticker group.</summary>
    public List<(Bar Bar, decimal Vwap)> GetBarHistoryWithVwap(string groupKey)
    {
        if (_groups.TryGetValue(groupKey, out var group))
            return group.GetBarHistoryWithVwap();
        return new List<(Bar, decimal)>();
    }

    /// <summary>Get active trade view for a specific setup, or null.</summary>
    public ActiveTradeView? GetActiveTrade(string setupId)
    {
        if (_brokerHandler == null) return null;
        var group = _brokerHandler.GetGroupState(setupId);
        if (group?.EntryPrice == null) return null;

        var stopLeg = group.GetLeg(LegType.Stop);
        var tg1Leg = group.GetLeg(LegType.Tg1);
        var tg2Leg = group.GetLeg(LegType.Tg2);

        return new ActiveTradeView
        {
            Direction = group.Direction,
            Contracts = group.TotalContracts,
            Entry = group.EntryPrice.Value,
            CurrentStop = stopLeg?.Price ?? 0m,
            InitialStop = stopLeg?.Price ?? 0m,
            Target = tg2Leg?.Price ?? 0m,
            Partial = tg1Leg?.Price ?? 0m,
            PartialFilled = group.Status == GroupOrderStatus.PartialFilled,
            EnteredAt = group.CreatedAt,
        };
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

    /// <summary>Restore ORB state from a cached snapshot for the matching ticker group.</summary>
    public void RestoreOrb(OrbStateCache cache)
    {
        var groupKey = TickerGroup.GetGroupKey(cache.Symbol);
        if (_groups.TryGetValue(groupKey, out var group))
            group.RestoreOrb(cache.OrbHigh, cache.OrbLow, cache.CloseRelPct, cache.TradingDate, cache.OrbAtrRatio,
                cache.OpeningDriveBull, cache.OpeningDriveBear, cache.DriveRangePctATR);
        else
        {
            // Fallback: apply to all groups (backward compat — single-ticker setups)
            foreach (var g in _groups.Values)
                g.RestoreOrb(cache.OrbHigh, cache.OrbLow, cache.CloseRelPct, cache.TradingDate, cache.OrbAtrRatio,
                    cache.OpeningDriveBull, cache.OpeningDriveBear, cache.DriveRangePctATR);
        }
    }

    /// <summary>Seed ORB floor from cache for the matching ticker group.</summary>
    public void SeedOrbFloor(OrbStateCache cache)
    {
        var groupKey = TickerGroup.GetGroupKey(cache.Symbol);
        if (_groups.TryGetValue(groupKey, out var group))
            group.SeedOrbFloor(cache.OrbHigh, cache.OrbLow, cache.TradingDate);
        else
        {
            foreach (var g in _groups.Values)
                g.SeedOrbFloor(cache.OrbHigh, cache.OrbLow, cache.TradingDate);
        }
    }

    /// <summary>
    /// If a TickerGroup's ORB just formed, persist to cache (fire-and-forget).
    /// The session ID is stamped so multi-session restarts restore the correct ORB.
    /// </summary>
    private void SaveOrbCacheIfFormed(TickerGroup group)
    {
        if (group.ConsumeOrbFormed(out var cache) && cache != null)
        {
            cache.SessionId = _activeSessionId;
            OrbStateCacheService.Save(cache);
        }
    }

    /// <summary>Seed module history from daily bars.</summary>
    public void SeedModuleHistory(IReadOnlyList<Bar> dailyBars)
    {
        foreach (var group in _groups.Values)
            group.SeedModuleHistory(dailyBars);
    }

    /// <summary>
    /// Re-seed session range reference on each TickerGroup after backfill.
    /// During pre-init, OnSessionBoundary runs before backfill so session-engine
    /// levels (London H/L, Asia H/L) are still 0. After backfill populates them,
    /// call this to set the FalseBreakout SessionRangeTracker reference range.
    /// </summary>
    public void ReseedSessionRange(SessionId sessionId)
    {
        foreach (var group in _groups.Values)
            group.OnSessionBoundary(sessionId);
    }

    // ── Private helpers ─────────────────────────────────────────────

    private async Task PublishSnapshotInternal()
    {
        var snap = GetSnapshot();
        await _sink.OnSnapshotAsync(snap);
    }

    private void AddAlert(string type, SetupId setup, string message, string color, string setupLabel = "")
    {
        _alertRing[_alertHead] = new AlertEvent
        {
            Time = DateTime.UtcNow,
            Type = type,
            Setup = setup,
            SetupLabel = setupLabel,
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
