using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CRV.Backtest.Engine;
using CRV.Core.Models;

namespace CRV.Backtest.DataLoaders;

/// <summary>
/// Raised when a historical bar request cannot be satisfied in full.
/// <para>
/// The loaders fetch history in chunks. Swallowing a failed chunk leaves a hole in
/// the middle of the series and lets the run finish and report a result as though
/// the data were complete — which is how three runs of one configuration landed
/// 24.6% apart. Losing bars must end the run, not shrink it quietly.
/// </para>
/// </summary>
public class BarLoadException : Exception
{
    public BarLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Persists the exact bar stream that fed a backtest, and replays it on a re-run.
/// <para>
/// Without this, every run refetches from the broker and is therefore measuring a
/// slightly different market each time. The snapshot makes a run an experiment you
/// can repeat: same key in, byte-identical bars out, regardless of what the API
/// feels like returning today.
/// </para>
/// <para>
/// The file is written to a temporary path and moved into place only once the source
/// stream completes, so an interrupted run cannot leave a truncated snapshot behind
/// to be replayed later as if it were whole.
/// </para>
/// </summary>
public sealed class BarSnapshotStore
{
    private readonly string _root;

    public BarSnapshotStore(string rootDirectory) => _root = rootDirectory;

    /// <summary>Default location: <c>&lt;content root&gt;/Data/bar-snapshots</c>.</summary>
    public static BarSnapshotStore Default(string contentRoot) =>
        new(Path.Combine(contentRoot, "Data", "bar-snapshots"));

    /// <summary>
    /// A content hash of everything that determines which bars a run should see.
    /// Ticker order is normalised so that the same basket listed differently is the
    /// same key.
    /// </summary>
    public static string KeyFor(BacktestConfig cfg, IEnumerable<string> tickers)
    {
        var parts = string.Join('|',
            cfg.DataSource,
            cfg.From.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cfg.To.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            cfg.ExecutionTFMinutes.ToString(CultureInfo.InvariantCulture),
            string.Join(',', tickers.Select(t => t.ToUpperInvariant()).OrderBy(t => t, StringComparer.Ordinal)));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts)))[..16].ToLowerInvariant();
    }

    private string PathFor(string key) => Path.Combine(_root, key + ".csv");

    public bool Has(string key) => File.Exists(PathFor(key));

    public string Describe(string key) => PathFor(key);

    /// <summary>
    /// Passes <paramref name="source"/> through unchanged while writing each bar to
    /// the snapshot. The caller still consumes one stream; the capture is a side effect.
    /// </summary>
    public async IAsyncEnumerable<(string Ticker, Bar Bar)> Capture(
        string key, IAsyncEnumerable<(string Ticker, Bar Bar)> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var final = PathFor(key);
        var temp  = final + "." + Guid.NewGuid().ToString("N")[..8] + ".partial";

        var writer = new StreamWriter(temp, append: false);
        try
        {
            await writer.WriteLineAsync("ticker,time,open,high,low,close,volume");
            await foreach (var (ticker, bar) in source.WithCancellation(ct))
            {
                await writer.WriteLineAsync(Format(ticker, bar));
                yield return (ticker, bar);
            }
            await writer.FlushAsync(ct);
            await writer.DisposeAsync();
            File.Move(temp, final, overwrite: true);
        }
        finally
        {
            await writer.DisposeAsync();
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Replays a previously captured stream.</summary>
    public async IAsyncEnumerable<(string Ticker, Bar Bar)> Replay(
        string key, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            throw new BarLoadException($"No bar snapshot for key {key} at {path}.");

        using var reader = new StreamReader(path);
        await reader.ReadLineAsync(ct);   // header

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var p = line.Split(',');
            if (p.Length < 7)
                throw new BarLoadException($"Corrupt bar snapshot {path}: expected 7 fields, got {p.Length}.");

            yield return (p[0], new Bar(
                DateTime.ParseExact(p[1], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                Dec(p[2]), Dec(p[3]), Dec(p[4]), Dec(p[5]),
                long.Parse(p[6], CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Replays when a snapshot exists for the key, otherwise captures.</summary>
    public IAsyncEnumerable<(string Ticker, Bar Bar)> ReplayOrCapture(
        string key, Func<IAsyncEnumerable<(string Ticker, Bar Bar)>> source,
        CancellationToken ct = default)
        => Has(key) ? Replay(key, ct) : Capture(key, source(), ct);

    // Round-trip exactly: "O" for the timestamp, invariant decimal for prices.
    private static string Format(string ticker, Bar b) =>
        string.Join(',', ticker,
            b.Time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            b.Open.ToString(CultureInfo.InvariantCulture),
            b.High.ToString(CultureInfo.InvariantCulture),
            b.Low.ToString(CultureInfo.InvariantCulture),
            b.Close.ToString(CultureInfo.InvariantCulture),
            b.Volume.ToString(CultureInfo.InvariantCulture));

    private static decimal Dec(string s) => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
}
