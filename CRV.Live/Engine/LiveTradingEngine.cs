namespace CRV.Live.Engine;

using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Core.Strategy;
using Microsoft.Extensions.Logging;

/// <summary>
/// Wraps OrbStrategyEngine for live use.
/// Owns the bar feed subscription loop.
/// </summary>
public class LiveTradingEngine
{
    private readonly OrbStrategyEngine _engine;
    private readonly IBarFeed          _feed;
    private readonly ILogger           _log;

    public LiveTradingEngine(
        OrbStrategyEngine engine, IBarFeed feed, ILogger<LiveTradingEngine> log)
    {
        _engine = engine;
        _feed   = feed;
        _log    = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Live trading engine started.");
        await foreach (var bar in _feed.StreamAsync(ct))
        {
            if (ct.IsCancellationRequested) break;
            await _engine.ProcessBarAsync(bar, ct);
        }
        _log.LogInformation("Live trading engine stopped.");
    }
}
