using CRV.Core.Models;

namespace CRV.Core.Strategy;

/// <summary>
/// Thread-safe fixed-capacity ring buffer for storing recent bars.
/// Used by TickerGroup to maintain chart history for the dashboard.
/// </summary>
public class BarRingBuffer
{
    private readonly Bar[] _buffer;
    private readonly object _lock = new();
    private int _head;   // next write position
    private int _count;

    public BarRingBuffer(int capacity)
    {
        _buffer = new Bar[capacity];
    }

    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Add a confirmed bar to the buffer.</summary>
    public void Add(Bar bar)
    {
        lock (_lock)
        {
            _buffer[_head] = bar;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
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
