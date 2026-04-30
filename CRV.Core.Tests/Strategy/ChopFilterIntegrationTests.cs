using CRV.Core.Models;
using CRV.Core.Strategy;
using Xunit;

namespace CRV.Core.Tests.Strategy;

/// <summary>
/// End-to-end wiring tests: drive a real <see cref="TickerGroup"/> with bars that
/// trigger the chop detector, then verify that pending entries are blocked or allowed
/// per <see cref="StrategyConfig.UseChopFilter"/> and <see cref="StrategyConfig.ChopBlockMode"/>.
/// </summary>
public class ChopFilterIntegrationTests
{
    private static readonly TimeZoneInfo EasternTz =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static StrategyConfig MakeConfig(bool useFilter, ChopBlockMode mode = ChopBlockMode.UntilClear) => new()
    {
        Ticker = "/NQH2026",
        PointValue = 20m,
        TickSize = 0.25m,
        Timezone = "America/New_York",
        OrbStart = new TimeOnly(9, 30),
        OrbEnd = new TimeOnly(10, 0),
        ExecutionTFMinutes = 1,
        AllowBothSameBar = false,
        UseChopFilter = useFilter,
        ChopBlockMode = mode,
    };

    private static Bar MakeBar(DateTime utc, decimal price, long volume = 1000)
        => new(utc, price, price, price, price, volume);

    private static EntrySignal MakeSignal(DateTime time) => new(
        Setup: SetupId.A, Direction: Direction.Long,
        Entry: 5200m, Stop: 5195m, Tg2Price: 5210m, Tg1Price: 5205m,
        TotalContracts: 1, Time: time);

    /// <summary>
    /// Drives 22 flat-volume bars + 1 low-volume "trigger" bar through the group.
    /// Pre-OR bars only — F1/F3 stay inactive, F2 (flat VWAP) and F4 (low volume) vote on the trigger.
    /// Returns the UTC time of the trigger bar.
    /// </summary>
    private static async Task<DateTime> WarmUpToChopAsync(TickerGroup group, int day = 10)
    {
        var startEt  = new DateTime(2026, 3, day, 8, 0, 0, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startEt, EasternTz);

        // 22 flat warmup bars at vol=1000 — fills volume SMA (period 20) and VWAP slope buffer (21+1).
        for (int i = 0; i < 22; i++)
            await group.ProcessBarAsync(MakeBar(startUtc.AddMinutes(i), 5200m, volume: 1000));

        // Trigger: same flat price, low volume → F4 votes (ratio 0.5), F2 still flat → 2 votes ≥ default MinVotes=2.
        var triggerUtc = startUtc.AddMinutes(22);
        await group.ProcessBarAsync(MakeBar(triggerUtc, 5200m, volume: 500));
        return triggerUtc;
    }

    private static (TickerGroup group, FakeChopStrategy fake) BuildGroup(StrategyConfig cfg)
    {
        var group = new TickerGroup("NQ", cfg);
        var fake  = new FakeChopStrategy();
        group.AddStrategy(fake);
        return (group, fake);
    }

    /// <summary>Minimal ISetupStrategy stub. PendingEntry is set externally by tests; OnBar is a no-op.</summary>
    private sealed class FakeChopStrategy : ISetupStrategy
    {
        public string Id { get; set; } = "A";
        public SetupId SetupId { get; set; } = SetupId.A;
        public StrategyType StrategyType => StrategyType.Pullback;
        public string Name => "FakeA";
        public string Ticker { get; set; } = "/NQH2026";
        public decimal PointValue { get; set; } = 20m;
        public TimeOnly OrbStart { get; set; } = new(9, 30);
        public TimeOnly OrbEnd   { get; set; } = new(10, 0);
        public bool UseEmaFilter => false;
        public bool BypassChopFilter { get; set; } = false;
        public bool IsActive { get; set; }
        public bool IsArmed { get; set; }
        public bool InTrade { get; set; }
        public int CutoffHour { get; set; } = 23;
        public int CutoffMinute { get; set; } = 59;
        public Direction TradeDirection { get; set; } = Direction.Long;
        public EntrySignal? PendingEntry { get; set; }

        public void SetInTrade(bool active) => InTrade = active;
        public void SeedTradeCount(int l, int s) { }
        public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules) { }
        public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules) { }
        public void Reconfigure(StrategySetupConfig config) { }
        public void Reset() { }
        public void ResetTradeCounters() { }
        public void Disarm() { }
        public void ResetCutoff() { }
        public (int Hour, int Minute) GetCutoffForSession(string s) => (CutoffHour, CutoffMinute);
        public bool IsEnabledForSession(string s) => true;
        public void ResetSession() { IsActive = false; IsArmed = false; PendingEntry = null; }
        public void ClearPendingSignals() { PendingEntry = null; }
        public void RevertEntry() { PendingEntry = null; IsActive = false; }
        public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd) { }
        public SetupStateSnapshot GetSnapshot() => new() { SetupId = SetupId, Name = Name };
    }

    // ─── Filter disabled (current default) ────────────────────────────

    [Fact]
    public async Task FilterOff_PendingEntryNeverReverted_EvenInChopConditions()
    {
        var (group, fake) = BuildGroup(MakeConfig(useFilter: false));
        var triggerUtc = await WarmUpToChopAsync(group);

        var signal = MakeSignal(triggerUtc.AddMinutes(1));
        fake.PendingEntry = signal;
        await group.ProcessBarAsync(MakeBar(triggerUtc.AddMinutes(1), 5200m, volume: 500));

        Assert.NotNull(fake.PendingEntry);
        Assert.Same(signal, fake.PendingEntry);
    }

    // ─── UntilClear mode ──────────────────────────────────────────────

    [Fact]
    public async Task UntilClear_BlocksPendingEntry_WhileChopActive()
    {
        var (group, fake) = BuildGroup(MakeConfig(useFilter: true, ChopBlockMode.UntilClear));
        var triggerUtc = await WarmUpToChopAsync(group);

        // Re-arm pending entry on the next bar, keeping chop conditions active (low vol, flat).
        fake.PendingEntry = MakeSignal(triggerUtc.AddMinutes(1));
        await group.ProcessBarAsync(MakeBar(triggerUtc.AddMinutes(1), 5200m, volume: 500));

        Assert.Null(fake.PendingEntry);
    }

    [Fact]
    public async Task Reconfigure_FlipsFilterOn_WithoutRestart()
    {
        var (group, fake) = BuildGroup(MakeConfig(useFilter: false));
        var triggerUtc = await WarmUpToChopAsync(group);

        // With filter off, entry survives even with chop conditions.
        fake.PendingEntry = MakeSignal(triggerUtc.AddMinutes(1));
        await group.ProcessBarAsync(MakeBar(triggerUtc.AddMinutes(1), 5200m, volume: 500));
        Assert.NotNull(fake.PendingEntry);

        // Hot-reload: flip the master switch ON without restarting the engine.
        var newCfg = MakeConfig(useFilter: true, ChopBlockMode.UntilClear);
        group.Reconfigure(newCfg);

        // Same chop conditions on the next bar — should now be blocked.
        fake.PendingEntry = MakeSignal(triggerUtc.AddMinutes(2));
        await group.ProcessBarAsync(MakeBar(triggerUtc.AddMinutes(2), 5200m, volume: 500));
        Assert.Null(fake.PendingEntry);
    }

    [Fact]
    public async Task BypassChopFilter_AllowsEntry_EvenWhileChopActive()
    {
        var (group, fake) = BuildGroup(MakeConfig(useFilter: true, ChopBlockMode.UntilClear));
        fake.BypassChopFilter = true;   // strategy opts out of the global filter

        var triggerUtc = await WarmUpToChopAsync(group);

        var signal = MakeSignal(triggerUtc.AddMinutes(1));
        fake.PendingEntry = signal;
        await group.ProcessBarAsync(MakeBar(triggerUtc.AddMinutes(1), 5200m, volume: 500));

        Assert.NotNull(fake.PendingEntry);
        Assert.Same(signal, fake.PendingEntry);
    }

    [Fact]
    public async Task UntilClear_AllowsPendingEntry_AfterChopClears()
    {
        var (group, fake) = BuildGroup(MakeConfig(useFilter: true, ChopBlockMode.UntilClear));
        var triggerUtc = await WarmUpToChopAsync(group);

        // Clearing bar: large price jump + high volume → F2 slope > threshold AND F4 ratio > 1.0
        var clearUtc = triggerUtc.AddMinutes(1);
        var signal   = MakeSignal(clearUtc);
        fake.PendingEntry = signal;
        await group.ProcessBarAsync(MakeBar(clearUtc, 5300m, volume: 1500));

        Assert.NotNull(fake.PendingEntry);
        Assert.Same(signal, fake.PendingEntry);
    }

    // ─── RestOfDay mode ───────────────────────────────────────────────

    [Fact]
    public async Task RestOfDay_StaysBlocked_EvenAfterChopClears()
    {
        var (group, fake) = BuildGroup(MakeConfig(useFilter: true, ChopBlockMode.RestOfDay));
        var triggerUtc = await WarmUpToChopAsync(group);

        // Clearing bar (same as UntilClear test) — but RestOfDay should still block.
        var clearUtc = triggerUtc.AddMinutes(1);
        fake.PendingEntry = MakeSignal(clearUtc);
        await group.ProcessBarAsync(MakeBar(clearUtc, 5300m, volume: 1500));

        Assert.Null(fake.PendingEntry);
    }

    [Fact]
    public async Task RestOfDay_ResetsOnNewSession()
    {
        var (group, fake) = BuildGroup(MakeConfig(useFilter: true, ChopBlockMode.RestOfDay));
        await WarmUpToChopAsync(group);

        // 18:00 ET on March 10 → trading date March 11 → triggers NewSession on all modules
        // and clears _chopBlockedToday. Use a price/volume that does not trigger chop on day 2.
        var nextSessionEt  = new DateTime(2026, 3, 10, 18, 0, 0, DateTimeKind.Unspecified);
        var nextSessionUtc = TimeZoneInfo.ConvertTimeToUtc(nextSessionEt, EasternTz);

        var signal = MakeSignal(nextSessionUtc);
        fake.PendingEntry = signal;
        await group.ProcessBarAsync(MakeBar(nextSessionUtc, 5300m, volume: 2000));

        Assert.NotNull(fake.PendingEntry);
        Assert.Same(signal, fake.PendingEntry);
    }
}
