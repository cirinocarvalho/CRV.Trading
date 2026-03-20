using CRV.Core.Interfaces;
using CRV.Core.Models;
using Microsoft.Extensions.Logging;

namespace CRV.Live.BarBuilders;

/// <summary>Aggregates real-time ticks into confirmed OHLCV bars.</summary>
public class RealTimeBarBuilder
{
    private readonly int               _tfMinutes;
    private readonly ILastPriceProvider _prices;
    private readonly ILogger           _log;
    private readonly string            _ticker;

    private Bar?     _current;
    private DateTime _barStart = DateTime.MinValue;

    public event Action<Bar>? BarUpdated;
    public event Action<Bar>? BarClosed;

    public RealTimeBarBuilder(string ticker, int tfMinutes, ILastPriceProvider prices, ILogger log)
    {
        _ticker    = ticker;
        _tfMinutes = tfMinutes;
        _prices    = prices;
        _log       = log;
    }

    public void OnTick(decimal price, long volume, DateTime utcTime)
    {
        _prices.UpdatePrice(_ticker, price);
        var barStart = GetBarStart(utcTime, _tfMinutes);

        if (barStart != _barStart)
        {
            if (_current != null)
            {
                var closed = _current with { IsConfirmed = true };
                _log.LogDebug("Bar closed {T} O={O} H={H} L={L} C={C}", closed.Time, closed.Open, closed.High, closed.Low, closed.Close);
                BarClosed?.Invoke(closed);
            }
            _barStart = barStart;
            _current  = new Bar(barStart, price, price, price, price, volume, false, _ticker);
        }
        else if (_current != null)
        {
            _current = _current with
            {
                High   = Math.Max(_current.High, price),
                Low    = Math.Min(_current.Low,  price),
                Close  = price,
                Volume = _current.Volume + volume
            };
        }
        BarUpdated?.Invoke(_current!);
    }

    public void ForceClose(DateTime utcTime)
    {
        if (_current == null) return;
        BarClosed?.Invoke(_current with { IsConfirmed = true });
        _current = null;
    }

    private static DateTime GetBarStart(DateTime t, int tf)
    {
        var mins = (t.Hour * 60 + t.Minute) / tf * tf;
        return new DateTime(t.Year, t.Month, t.Day, mins / 60, mins % 60, 0, DateTimeKind.Utc);
    }
}
