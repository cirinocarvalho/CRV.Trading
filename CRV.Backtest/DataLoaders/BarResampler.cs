using System.Runtime.CompilerServices;
using CRV.Core.Models;

namespace CRV.Backtest.DataLoaders;

/// <summary>
/// Aggregates fine-grained bars (e.g. 1-minute CSV) into coarser execution-TF bars
/// using standard OHLCV rules:  Open=first, High=max, Low=min, Close=last, Volume=sum.
/// When tfMinutes &lt;= 1 the source bars are returned as-is.
/// Buckets are computed per calendar day so bars from different days are never merged.
/// </summary>
public static class BarResampler
{
    public static async IAsyncEnumerable<Bar> ResampleAsync(
        IAsyncEnumerable<Bar> source,
        int tfMinutes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Nothing to aggregate — yield all source bars unchanged
        if (tfMinutes <= 1)
        {
            await foreach (var b in source.WithCancellation(ct))
                yield return b;
            yield break;
        }

        DateTime? bucketKey  = null;
        decimal   open       = 0;
        decimal   high       = 0;
        decimal   low        = 0;
        decimal   close      = 0;
        long      volume     = 0;

        await foreach (var bar in source.WithCancellation(ct))
        {
            var key = BucketStart(bar.Time, tfMinutes);

            if (bucketKey == null || key != bucketKey)
            {
                // Emit the completed bucket before starting a new one
                if (bucketKey != null)
                    yield return new Bar(bucketKey.Value, open, high, low, close, volume);

                bucketKey  = key;
                open       = bar.Open;
                high       = bar.High;
                low        = bar.Low;
                close      = bar.Close;
                volume     = bar.Volume;
            }
            else
            {
                // Extend the current bucket
                if (bar.High > high) high = bar.High;
                if (bar.Low  < low)  low  = bar.Low;
                close   = bar.Close;
                volume += bar.Volume;
            }
        }

        // Emit the final (possibly incomplete) bucket
        if (bucketKey != null)
            yield return new Bar(bucketKey.Value, open, high, low, close, volume);
    }

    /// <summary>
    /// Returns the start DateTime of the N-minute bucket that contains <paramref name="t"/>.
    /// E.g. 09:33 with tf=5 → 09:30;  09:37 with tf=5 → 09:35.
    /// </summary>
    private static DateTime BucketStart(DateTime t, int tfMinutes)
    {
        int totalMins  = t.Hour * 60 + t.Minute;
        int bucketMins = totalMins / tfMinutes * tfMinutes;
        return t.Date.AddMinutes(bucketMins);
    }
}
