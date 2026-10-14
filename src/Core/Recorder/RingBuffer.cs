using System;

namespace Vigil.Core
{
    /// <summary>Fixed-size history: once full, each new entry replaces the oldest.</summary>
    public sealed class RingBuffer<T>
    {
        private readonly T[] _items;
        private int _next;
        private int _count;

        public RingBuffer(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new T[capacity];
        }

        public int Count => _count;
        public int Capacity => _items.Length;

        public void Add(T item)
        {
            _items[_next] = item;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }

        /// <summary>Everything held, oldest first.</summary>
        public T[] ToArray() => Last(_count);

        /// <summary>The newest <paramref name="n"/> entries (or fewer), oldest first.</summary>
        public T[] Last(int n)
        {
            n = Math.Max(0, Math.Min(n, _count));
            var result = new T[n];
            int start = (_next - n + _items.Length) % _items.Length;
            for (int i = 0; i < n; i++) result[i] = _items[(start + i) % _items.Length];
            return result;
        }

        public bool TryNewest(out T item)
        {
            if (_count == 0) { item = default(T); return false; }
            item = _items[(_next - 1 + _items.Length) % _items.Length];
            return true;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _next = 0; _count = 0;
        }
    }
}
