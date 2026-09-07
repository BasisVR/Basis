using System;
using System.Collections.Concurrent;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public sealed class ShardedConcurrentDictionary<TValue> : System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, TValue>>
    {
        private readonly ConcurrentDictionary<int, TValue>[] _shards;
        private readonly int _mask;

        public ShardedConcurrentDictionary()
            : this(NextPowerOfTwo(Math.Max(1, Environment.ProcessorCount))) { }

        public ShardedConcurrentDictionary(int shardCount)
        {
            if (shardCount <= 0 || (shardCount & (shardCount - 1)) != 0)
                throw new ArgumentException("shardCount must be a positive power of two", nameof(shardCount));
            _shards = new ConcurrentDictionary<int, TValue>[shardCount];
            _mask = shardCount - 1;
            for (int i = 0; i < shardCount; i++) _shards[i] = new ConcurrentDictionary<int, TValue>();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private ConcurrentDictionary<int, TValue> ShardOf(int key) => _shards[Scramble(key) & _mask];

        // 32-bit integer hash mix (Murmur3-style). Player ids are dense small ints assigned by
        // LiteNetLib; without scrambling, ids 0..N-1 would all hash to shard 0 under low-bit
        // masking, completely defeating the shard split.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int Scramble(int key)
        {
            unchecked
            {
                uint x = (uint)key;
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
                return (int)x;
            }
        }

        public bool TryGetValue(int key, out TValue value) => ShardOf(key).TryGetValue(key, out value);
        public bool TryRemove(int key, out TValue value) => ShardOf(key).TryRemove(key, out value);

        public TValue this[int key]
        {
            get => ShardOf(key)[key];
            set => ShardOf(key)[key] = value;
        }

        public void Clear()
        {
            for (int i = 0; i < _shards.Length; i++) _shards[i].Clear();
        }

        public void DrainInto(System.Collections.Generic.List<TValue> destination)
        {
            for (int i = 0; i < _shards.Length; i++)
            {
                var shard = _shards[i];
                if (shard.IsEmpty) continue;
                foreach (var kvp in shard)
                {
                    if (shard.TryRemove(kvp.Key, out var value))
                    {
                        destination.Add(value);
                    }
                }
            }
        }

        public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<int, TValue>> GetEnumerator()
        {
            for (int i = 0; i < _shards.Length; i++)
            {
                foreach (var kvp in _shards[i]) yield return kvp;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private static int NextPowerOfTwo(int x)
        {
            if (x <= 1) return 1;
            int p = 1;
            while (p < x) p <<= 1;
            return p;
        }
    }
}
