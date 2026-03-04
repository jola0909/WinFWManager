using System.Collections;

namespace WinFWManager.Core.Collections;

public class RingBuffer<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public RingBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new T[capacity];
        _head = 0;
        _count = 0;
    }

    public int Count { get { lock (_lock) return _count; } }
    public int Capacity => _capacity;

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer, 0, _capacity);
            _head = 0;
            _count = 0;
        }
    }

    public List<T> ToList()
    {
        lock (_lock)
        {
            var list = new List<T>(_count);
            if (_count == 0) return list;

            int start = _count < _capacity ? 0 : _head;
            for (int i = 0; i < _count; i++)
                list.Add(_buffer[(start + i) % _capacity]);
            return list;
        }
    }

    public IEnumerator<T> GetEnumerator() => ToList().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
