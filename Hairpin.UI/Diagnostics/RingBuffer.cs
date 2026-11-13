namespace Hairpin.UI.Diagnostics;

/// <summary>
/// A fixed-size, thread-safe buffer that keeps the newest items. Once it's full, each new item replaces the oldest, so
/// its memory never grows past <see cref="Capacity"/> items however long the app runs.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly Lock _lock = new();
    private int _next;
    private int _count;

    /// <summary>Creates an empty buffer that holds at most <paramref name="capacity"/> items.</summary>
    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    /// <summary>The most items the buffer holds.</summary>
    public int Capacity => _items.Length;

    /// <summary>How many items the buffer holds now.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>Adds an item, dropping the oldest when the buffer is full.</summary>
    public void Add(T item)
    {
        lock (_lock)
        {
            _items[_next] = item;
            _next = (_next + 1) % _items.Length;
            _count = Math.Min(_count + 1, _items.Length);
        }
    }

    /// <summary>Copies the items, oldest first.</summary>
    public T[] ToArray()
    {
        lock (_lock)
        {
            var result = new T[_count];
            var start = (_next - _count + _items.Length) % _items.Length;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _items[(start + i) % _items.Length];
            }

            return result;
        }
    }

    /// <summary>Removes every item.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_items);
            _next = 0;
            _count = 0;
        }
    }
}
