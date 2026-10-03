using CRV.Core.Interfaces;
using CRV.Core.Models;
using CRV.Web.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>What the live event sink leaves in the server log after a refused or skipped signal.</summary>
public class SignalREventSinkTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static readonly DateTime At = new(2026, 4, 15, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MinRrSkip_IsLoggedAsASkipWarning()
    {
        var log = new CapturingLogger<SignalREventSink>();
        using var sink = new SignalREventSink(null!, null!, log);
        var skip = new SizeRefusal(At, "retest-mnq", "MNQ", 0m, 0m, 0m, RefusalReason.MinRr, Rr: 1.2m, MinRr: 1.5m);

        await ((IStrategyEventSink)sink).OnMinRrSkippedAsync(skip);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("[SKIP] retest-mnq MNQ Skipped: 1.2R below 1.5R", entry.Message);
    }

    [Fact]
    public async Task SizeRefusal_IsLoggedAsARiskWarning()
    {
        var log = new CapturingLogger<SignalREventSink>();
        using var sink = new SignalREventSink(null!, null!, log);
        var refusal = new SizeRefusal(At, "retest-mnq", "MNQ", 20m, 40m, 30m);

        await sink.OnSizeRefusedAsync(refusal);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.StartsWith("[RISK] retest-mnq MNQ refused for size", entry.Message);
    }
}
