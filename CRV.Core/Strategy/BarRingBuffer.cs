using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Thread-safe fixed-capacity ring buffer for storing recent bars
/// with parallel VWAP and EMA21 values. Used by TickerGroup for the dashboard chart.
/// </summary>
public class BarRingBuffer
{
    private readonly Bar[] _buffer;
    private readonly decimal[] _vwaps;   // parallel VWAP value per bar
    private readonly decimal[] _ema21s;  // parallel EMA21 value per bar
    private readonly object _lock = new();
    private int _head;   // next write position
    private int _count;

    public BarRingBuffer(int capacity)
    {
        _buffer  = new Bar[capacity];
        _vwaps   = new decimal[capacity];
        _ema21s  = new decimal[capacity];
    }

    public int Count { get { lock (_lock) return _count; } }

    /// <summary>
    /// Add a confirmed bar. If the latest bar has the same timestamp,
    /// update it in place (prevents duplicates from backfill/replay).
    /// </summary>
    public void Add(Bar bar)
    {
        lock (_lock)
        {
            // Check if latest bar has the same timestamp → update in place
            if (_count > 0)
            {
                int lastIdx = (_head - 1 + _buffer.Length) % _buffer.Length;
                if (_buffer[lastIdx].Time == bar.Time)
                {
                    _buffer[lastIdx] = bar;
                    return;
                }
            }

            _buffer[_head] = bar;
            _vwaps[_head]  = 0;
            _ema21s[_head] = 0;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    /// <summary>Set the VWAP value for the most recently added bar.</summary>
    public void SetVwap(decimal vwap)
    {
        lock (_lock)
        {
            if (_count == 0) return;
            int lastIdx = (_head - 1 + _buffer.Length) % _buffer.Length;
            _vwaps[lastIdx] = vwap;
        }
    }

    /// <summary>Set the EMA21 value for the most recently added bar.</summary>
    public void SetEma21(decimal ema21)
    {
        lock (_lock)
        {
            if (_count == 0) return;
            int lastIdx = (_head - 1 + _buffer.Length) % _buffer.Length;
            _ema21s[lastIdx] = ema21;
        }
    }

    /// <summary>Return all bars oldest→newest as a list snapshot.</summary>
    public List<Bar> ToList()
    {
        lock (_lock)
        {
            var result = new List<Bar>(_count);
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - _count + i + _buffer.Length) % _buffer.Length;
                result.Add(_buffer[idx]);
            }
            return result;
        }
    }

    /// <summary>Return all (bar, vwap) pairs oldest→newest.</summary>
    public List<(Bar Bar, decimal Vwap)> ToListWithVwap()
    {
        lock (_lock)
        {
            var result = new List<(Bar, decimal)>(_count);
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - _count + i + _buffer.Length) % _buffer.Length;
                result.Add((_buffer[idx], _vwaps[idx]));
            }
            return result;
        }
    }

    /// <summary>Return all (bar, vwap, ema21) tuples oldest→newest.</summary>
    public List<(Bar Bar, decimal Vwap, decimal Ema21)> ToListWithIndicators()
    {
        lock (_lock)
        {
            var result = new List<(Bar, decimal, decimal)>(_count);
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - _count + i + _buffer.Length) % _buffer.Length;
                result.Add((_buffer[idx], _vwaps[idx], _ema21s[idx]));
            }
            return result;
        }
    }

    /// <summary>Return the most recent bar, or null if empty.</summary>
    public Bar? Latest()
    {
        lock (_lock)
        {
            if (_count == 0) return null;
            int idx = (_head - 1 + _buffer.Length) % _buffer.Length;
            return _buffer[idx];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
        }
    }
}
