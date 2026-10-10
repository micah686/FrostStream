namespace YtDlpSharpLib.Internal;

/// <summary>
/// Thread-safe fixed-capacity ring buffer used to retain the most recent N items.
/// Used to keep the last few lines of stderr for error reporting without unbounded growth.
/// </summary>
internal sealed class RingBuffer<T>
{
    private readonly T[] _buffer;
    private readonly Lock _gate = new();
    private int _count;
    private int _writeIndex;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buffer = new T[capacity];
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            _buffer[_writeIndex] = item;
            _writeIndex = (_writeIndex + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            var result = new T[_count];
            var startIndex = _count == _buffer.Length ? _writeIndex : 0;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _buffer[(startIndex + i) % _buffer.Length];
            }

            return result;
        }
    }
}
