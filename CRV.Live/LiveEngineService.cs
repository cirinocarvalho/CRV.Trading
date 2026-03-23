using CRV.Core.Interfaces;
using CRV.Core.Strategy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CRV.Live;

/// <summary>
/// Background service owning the live trading loop.
/// Start/stop controlled from Settings page via LiveEngineOrchestrator.
/// </summary>
public class LiveEngineService : BackgroundService
{
    private readonly ILogger _log;
    private bool    _running = false;
    private string  _status  = "Stopped";

    public string Status  => _status;
    public bool   IsLive  => _running;

    public LiveEngineService(ILogger<LiveEngineService> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _status = "Idle — start from Settings";
        while (!ct.IsCancellationRequested)
            await Task.Delay(500, ct);
    }

    public async Task RunAsync(IBarFeed feed, OrbStrategyEngine engine, CancellationToken ct)
    {
        if (_running) { _log.LogWarning("Engine already running."); return; }
        _running = true; _status = "Live";
        _log.LogInformation("Live trading started.");

        try
        {
            await foreach (var bar in feed.StreamAsync(ct))
            {
                if (!_running) break;
                await engine.ProcessBarAsync(bar, ct);
            }
        }
        catch (OperationCanceledException) { _log.LogInformation("Live trading stopped."); }
        catch (Exception ex)               { _log.LogError(ex, "Live engine error."); _status = $"Error: {ex.Message}"; }
        finally { _running = false; _status = "Stopped"; }
    }

    public void Stop() { _running = false; _status = "Stopped"; }
}
