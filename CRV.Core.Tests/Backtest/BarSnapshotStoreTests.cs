using CRV.Backtest.DataLoaders;
using CRV.Backtest.Engine;
using CRV.Core.Models;
using Xunit;

namespace CRV.Core.Tests.Backtest;

/// <summary>
/// Backtest runs 920-922 used identical configuration and produced net results
/// 24.6% apart, because every run refetched bars from the broker and quietly
/// tolerated whatever came back. A backtest that cannot reproduce itself cannot
/// validate anything, so the bars that fed a run are snapshotted and replayed.
/// </summary>
public class BarSnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crv-snap-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private BarSnapshotStore Store() => new(_dir);

    private static BacktestConfig Cfg(string source = "Schwab") => new()
    {
        From = new DateTime(2026, 3, 30, 0, 0, 0, DateTimeKind.Utc),
        To   = new DateTime(2026, 4, 23, 0, 0, 0, DateTimeKind.Utc),
        DataSource = source,
        ExecutionTFMinutes = 1,
    };

    private static async IAsyncEnumerable<(string Ticker, Bar Bar)> Sample()
    {
        yield return ("MNQ", new Bar(new DateTime(2026, 3, 30, 13, 30, 0, DateTimeKind.Utc), 18000.25m, 18010.50m, 17995.75m, 18005m, 1234));
        yield return ("MES", new Bar(new DateTime(2026, 3, 30, 13, 30, 0, DateTimeKind.Utc), 5300.25m, 5302m, 5299.50m, 5301.75m, 900));
        yield return ("MNQ", new Bar(new DateTime(2026, 3, 30, 13, 31, 0, DateTimeKind.Utc), 18005m, 18008m, 18001m, 18002.25m, 400));
        await Task.CompletedTask;
    }

    private static async Task<List<(string, Bar)>> Drain(IAsyncEnumerable<(string Ticker, Bar Bar)> src)
    {
        var outp = new List<(string, Bar)>();
        await foreach (var x in src) outp.Add((x.Ticker, x.Bar));
        return outp;
    }

    [Fact]
    public void KeyIsStableAcrossCallsForTheSameInputs()
    {
        var a = BarSnapshotStore.KeyFor(Cfg(), new[] { "MNQ", "MES" });
        var b = BarSnapshotStore.KeyFor(Cfg(), new[] { "MES", "MNQ" }); // order must not matter
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("Tradovate", "MNQ")]
    [InlineData("Schwab",    "MYM")]
    public void KeyChangesWhenTheRunInputsChange(string source, string ticker)
    {
        var baseline = BarSnapshotStore.KeyFor(Cfg(), new[] { "MNQ" });
        Assert.NotEqual(baseline, BarSnapshotStore.KeyFor(Cfg(source), new[] { ticker }));
    }

    [Fact]
    public async Task CaptureThenReplayReturnsTheIdenticalSequence()
    {
        var store = Store();
        var key   = BarSnapshotStore.KeyFor(Cfg(), new[] { "MNQ", "MES" });

        var captured = await Drain(store.Capture(key, Sample()));
        Assert.True(store.Has(key));

        var replayed = await Drain(store.Replay(key));
        Assert.Equal(captured, replayed);
        Assert.Equal(3, replayed.Count);
    }

    [Fact]
    public async Task ReplayPreservesPricesToTheTickAndBarOrder()
    {
        var store = Store();
        var key   = BarSnapshotStore.KeyFor(Cfg(), new[] { "MNQ" });
        await Drain(store.Capture(key, Sample()));

        var replayed = await Drain(store.Replay(key));
        Assert.Equal(new[] { "MNQ", "MES", "MNQ" }, replayed.Select(r => r.Item1));
        Assert.Equal(17995.75m, replayed[0].Item2.Low);
        Assert.Equal(5301.75m,  replayed[1].Item2.Close);
        Assert.Equal(1234L,     replayed[0].Item2.Volume);
        Assert.Equal(new DateTime(2026, 3, 30, 13, 31, 0, DateTimeKind.Utc), replayed[2].Item2.Time);
    }

    [Fact]
    public async Task AnInterruptedCaptureIsNotLeftBehindAsAValidSnapshot()
    {
        // A snapshot only counts once the stream completes. A run killed halfway
        // must not leave a truncated file that the next run replays as if whole.
        var store = Store();
        var key   = BarSnapshotStore.KeyFor(Cfg(), new[] { "MNQ" });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in store.Capture(key, Failing())) { }
        });

        Assert.False(store.Has(key));

        static async IAsyncEnumerable<(string Ticker, Bar Bar)> Failing()
        {
            yield return ("MNQ", new Bar(DateTime.UtcNow, 1, 2, 0, 1, 1));
            await Task.CompletedTask;
            throw new InvalidOperationException("broker went away mid-fetch");
        }
    }

    [Fact]
    public async Task StaleTemporaryFilesAreSweptUpOnTheNextCapture()
    {
        // A process killed outright cannot run its cleanup, so a .partial survives.
        // It is never mistaken for a snapshot, but it should not pile up either.
        var store = Store();
        Directory.CreateDirectory(_dir);
        var orphan = Path.Combine(_dir, "deadbeef.csv.abcd1234.partial");
        await File.WriteAllTextAsync(orphan, "ticker,time,open,high,low,close,volume\n");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));

        await Drain(store.Capture(BarSnapshotStore.KeyFor(Cfg(), new[] { "MNQ" }), Sample()));

        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task ATemporaryFileFromARunStillInFlightIsLeftAlone()
    {
        var store = Store();
        Directory.CreateDirectory(_dir);
        var live = Path.Combine(_dir, "cafebabe.csv.99887766.partial");
        await File.WriteAllTextAsync(live, "in progress");

        await Drain(store.Capture(BarSnapshotStore.KeyFor(Cfg(), new[] { "MES" }), Sample()));

        Assert.True(File.Exists(live));
    }

    [Fact]
    public async Task HasIsFalseForAKeyThatWasNeverCaptured()
    {
        Assert.False(Store().Has(BarSnapshotStore.KeyFor(Cfg(), new[] { "MCL" })));
        await Task.CompletedTask;
    }
}
