using System.Collections.Generic;
using System.Diagnostics;

namespace Basis.Network.Core
{
    public sealed class BasisPeerIdAllocator
    {
        public const int DefaultReuseDelayMs = 5000;
        public const int MaxPlayerId = ushort.MaxValue;
        private readonly object _lock = new object();
        private readonly Queue<int> _ready = new Queue<int>();
        private readonly Queue<long> _quarantineReadyAt = new Queue<long>();
        private readonly Queue<int> _quarantineIds = new Queue<int>();
        private readonly HashSet<int> _live = new HashSet<int>();
        private readonly long _reuseDelayTicks;
        private int _next;

        public BasisPeerIdAllocator(int reuseDelayMs = DefaultReuseDelayMs)
        {
            _reuseDelayTicks = reuseDelayMs <= 0 ? 0 : reuseDelayMs * Stopwatch.Frequency / 1000;
        }

        public int Allocate()
        {
            lock (_lock)
            {
                long now = Stopwatch.GetTimestamp();
                while (_quarantineIds.Count > 0 && _quarantineReadyAt.Peek() <= now)
                {
                    _quarantineReadyAt.Dequeue();
                    _ready.Enqueue(_quarantineIds.Dequeue());
                }
                int id;
                if (_ready.Count > 0)
                {
                    id = _ready.Dequeue();
                }
                else if (_next <= MaxPlayerId || _quarantineIds.Count == 0)
                {
                    id = _next++;
                }
                else
                {
                    _quarantineReadyAt.Dequeue();
                    id = _quarantineIds.Dequeue();
                }
                _live.Add(id);
                return id;
            }
        }

        public bool Release(int id)
        {
            lock (_lock)
            {
                if (!_live.Remove(id))
                {
                    return false;
                }
                _quarantineIds.Enqueue(id);
                _quarantineReadyAt.Enqueue(Stopwatch.GetTimestamp() + _reuseDelayTicks);
                return true;
            }
        }

        public bool IsLive(int id)
        {
            lock (_lock)
            {
                return _live.Contains(id);
            }
        }

        public int LiveCount
        {
            get
            {
                lock (_lock)
                {
                    return _live.Count;
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _ready.Clear();
                _quarantineIds.Clear();
                _quarantineReadyAt.Clear();
                _live.Clear();
                _next = 0;
            }
        }
    }

    public interface IBasisSharedPeerIds
    {
        void UsePeerIdAllocator(BasisPeerIdAllocator allocator);
    }
}
