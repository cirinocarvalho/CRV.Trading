using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Creates ISetupStrategy instances from per-setup configuration.
/// Decouples the engine from concrete strategy types.
/// </summary>
public static class StrategyFactory
{
    public static ISetupStrategy Create(StrategySetupConfig config) => config.StrategyType switch
    {
        StrategyType.Pullback       => new PullbackStrategy(config),
        StrategyType.Retest         => new RetestStrategy(config),
        StrategyType.OrbFakeout     => new OrbFakeoutStrategy(config),
        StrategyType.SessionFakeout => new SessionFakeoutStrategy(config),
        _ => throw new ArgumentException($"Unknown strategy type: {config.StrategyType}")
    };
}
