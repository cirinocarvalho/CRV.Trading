namespace CRV.Live.BarBuilders;

using CRV.Core.Models;

/// <summary>
/// Aggregates tick-level data into OHLCV bars of N minutes.
/// BarCompleted fires when a bar closes (minute boundary crossed).
/// </summary>
public class BarAggregator
{
    private readonly int _minutes;
    private decimal? _open, _high, _low;
    private decimal  _close;
    private long     _volume;
    private DateTime _barStart;
    private bool     _active;

    public event Action<Bar>? BarCompleted;

    public BarAggregator(int minutes = 1) => _minutes = minutes;

    public void OnTick(decimal price, long size, DateTime utcTime)
    {
        var barStart = SnapToBar(utcTime);

        if (_active && barStart > _barStart)
        {
            BarCompleted?.Invoke(new Bar(_barStart, _open!.Value, _high!.Value, _low!.Value, _close, _volume));
            _active = false;
        }

        if (!_active)
        {
            _barStart = barStart;
            _open = _high = _low = price;
            _volume = 0;
            _active = true;
        }

        _high   = Math.Max(_high!.Value, price);
        _low    = Math.Min(_low!.Value,  price);
        _close  = price;
        _volume += size;
    }

    private DateTime SnapToBar(DateTime t)
    {
        var mins = (t.Hour * 60 + t.Minute) / _minutes * _minutes;
        return new DateTime(t.Year, t.Month, t.Day, mins / 60, mins % 60, 0, DateTimeKind.Utc);
    }
}
