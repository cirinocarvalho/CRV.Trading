using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Lightweight ISetupStrategy stub for manual trades placed from the Manual Trade page.
/// Only provides identity + PointValue for BrokerEventHandler registration;
/// no bar/tick processing — those are strategy-engine concerns.
/// </summary>
public class ManualStrategy : ISetupStrategy
{
    private readonly EntrySignal _signal;
    private bool _inTrade;

    public ManualStrategy(EntrySignal signal)
    {
        _signal = signal;
        Id = !string.IsNullOrEmpty(signal.SetupLabel) ? signal.SetupLabel : $"Manual-{Guid.NewGuid().ToString("N")[..6]}";
    }

    public string Id { get; }
    public SetupId SetupId => _signal.Setup;
    public StrategyType StrategyType => StrategyType.Pullback;
    public string Name => "Manual";
    public bool IsActive => _inTrade;
    public bool IsArmed => false;
    public bool InTrade => _inTrade;
    public void SetInTrade(bool active) => _inTrade = active;
    public void SeedTradeCount(int longs, int shorts) { } // no-op for manual trades

    public int CutoffHour => 23;
    public int CutoffMinute => 59;
    public (int Hour, int Minute) GetCutoffForSession(string sessionName) => (23, 59);
    public bool IsEnabledForSession(string sessionName) => true;

    public string Ticker => _signal.Ticker;
    public decimal PointValue { get; set; }
    public bool    UseEmaFilter => false;

    // No-op: manual trades don't process bars/ticks
    public void OnBar(Bar bar, OrbState orb, IndicatorState indicators, ModuleState modules) { }
    public void OnTick(decimal price, DateTime utc, OrbState orb, IndicatorState indicators, ModuleState modules) { }
    public void Reconfigure(StrategySetupConfig config) { }
    public void Reset() { }
    public void ResetSession() { }
    public void ResetTradeCounters() { }

    public EntrySignal? PendingEntry => null;
    public void ClearPendingSignals() { }
    public void RevertEntry() { }
    public void ForceExit(decimal currentPrice, DateTime utcTime, ExitReason reason = ExitReason.SessionEnd) { }
    public void Disarm() { }
    public void ResetCutoff() { }
    public SetupStateSnapshot GetSnapshot() => new() { Id = Id, Name = "Manual", Enabled = true };
}
