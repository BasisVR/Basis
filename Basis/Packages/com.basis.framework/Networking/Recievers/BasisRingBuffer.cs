using System;
using UnityEngine;


/// <summary>
/// Main-thread-only bounded FIFO. When full, Enqueue overwrites the oldest.
/// </summary>
public sealed class BasisRingBuffer<T>
{
    private readonly T[] _data;
    private int _head; // index of oldest
    private int _tail; // index after newest
    private int _count;

    public int Count => _count;
    public int Capacity => _data.Length;

    public BasisRingBuffer(int capacity)
    {
        _data = new T[Mathf.Max(1, capacity)];
        _head = 0;
        _tail = 0;
        _count = 0;
    }

    /// <summary>
    /// Enqueues item. If full, overwrites the oldest element and calls onOverwrite(oldest).
    /// </summary>
    public void EnqueueOverwriteOldest(T item, Action<T> onOverwrite = null)
    {
        if (_count == _data.Length)
        {
            // Full: overwrite at tail (which equals head when full in this layout after wrap),
            // then advance head as well (oldest is dropped).
            onOverwrite?.Invoke(_data[_tail]);

            _data[_tail] = item;
            _tail = (_tail + 1) % _data.Length;
            _head = _tail; // oldest moved forward by one
        }
        else
        {
            _data[_tail] = item;
            _tail = (_tail + 1) % _data.Length;
            _count++;
        }
    }

    public bool TryDequeueOldest(out T item)
    {
        if (_count == 0)
        {
            item = default;
            return false;
        }

        item = _data[_head];
        _data[_head] = default;

        _head = (_head + 1) % _data.Length;
        _count--;
        return true;
    }
}
