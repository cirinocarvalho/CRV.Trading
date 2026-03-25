using CRV.Core.Interfaces;
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

    public void Stop() { _running = false; _status = "Stopped"; }
}
