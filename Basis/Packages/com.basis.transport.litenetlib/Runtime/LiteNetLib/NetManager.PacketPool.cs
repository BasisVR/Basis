using System;
using System.Collections.Concurrent;
using System.Threading;

namespace LiteNetLib
{
    public partial class NetManager
    {
        /// <summary>
        /// The shared pool, split across independent queues.
        ///
        /// <para>One queue was the second-largest cost on a busy server: measured at 2000 players
        /// with a 16-wide send pool, <c>PoolGetPacket</c> plus the
        /// <c>ConcurrentQueueSegment.TryDequeue</c> beneath it was <b>21.6% of all server CPU</b>,
        /// and it grew with worker count (1.5% at 1000 players on 5 workers). Every renting thread
        /// was competing for the same segment head, so the harder the send pool was pushed the more
        /// of that extra width went into cache-line ping-pong instead of sending.</para>
        ///
        /// <para><b>The per-thread cache in front of this does not help the renting side.</b> It was
        /// built for threads that rent and recycle in a loop, and the send path is not one: BSR
        /// workers only ever rent, hand the packet to a peer's queue, and a different thread sends
        /// and recycles it. <c>PoolRecycle</c> profiles at 0.00% on those threads, so their local
        /// list is empty on every get and every get reaches this.</para>
        ///
        /// <para>Stripe count comes from the machine rather than a constant, because the whole
        /// failure being fixed is contention that scales with thread count — a number fitted to one
        /// host would be wrong on a 24-core box and wronger on a 128-core one. Power of two so the
        /// index is a mask.</para>
        /// </summary>
        private readonly ConcurrentQueue<NetPacket>[] _pool = CreatePoolStripes();

        /// <summary>
        /// Per-stripe counts, spaced a cache line apart. Packed into one array they would sit 16 to
        /// a line and every recycle would invalidate the line for every other stripe — reintroducing
        /// exactly the sharing the stripes exist to remove.
        /// </summary>
        private readonly int[] _poolCounts = new int[PoolStripeCount * CountStride];

        private const int CountStride = 16;   // 64-byte line / sizeof(int)

        /// <summary>Independent queues in the shared pool. Power of two, from the host's core count.</summary>
        internal static readonly int PoolStripeCount = ComputePoolStripes();
        private static readonly int PoolStripeMask = PoolStripeCount - 1;

        private static int ComputePoolStripes()
        {
            // Round DOWN to a power of two so the mask is exact, and cap it: past a few dozen the
            // stripes stop removing contention and start costing a longer steal scan when one runs
            // dry. Floor of 1 keeps single-core and unit-test hosts on the original behaviour.
            int cores = Environment.ProcessorCount;
            int stripes = 1;
            while (stripes * 2 <= cores && stripes < 64)
                stripes *= 2;
            return stripes;
        }

        private static ConcurrentQueue<NetPacket>[] CreatePoolStripes()
        {
            var stripes = new ConcurrentQueue<NetPacket>[PoolStripeCount];
            for (int i = 0; i < stripes.Length; i++)
                stripes[i] = new ConcurrentQueue<NetPacket>();
            return stripes;
        }

        /// <summary>
        /// This thread's home stripe, handed out round-robin on first use.
        ///
        /// Deliberately not <c>ManagedThreadId % stripes</c>: thread ids are not dense and collide
        /// in clumps, which would leave some stripes serving several threads while others idle.
        /// </summary>
        [ThreadStatic] private static int t_poolStripe;
        [ThreadStatic] private static bool t_poolStripeAssigned;
        private static int s_poolStripeCursor = -1;

        private static int HomeStripe()
        {
            if (!t_poolStripeAssigned)
            {
                t_poolStripe = Interlocked.Increment(ref s_poolStripeCursor) & PoolStripeMask;
                t_poolStripeAssigned = true;
            }
            return t_poolStripe;
        }

        /// <summary>
        /// Per-thread packet cache, in front of the shared pool.
        ///
        /// Every get and recycle used to touch the shared queue plus an interlocked counter, and at
        /// a few hundred thousand packets a second from every sending thread that contention was
        /// measurable: 7.9% of server CPU at 2000 players sat in PoolRecycle alone. A thread that
        /// recycles a packet is very likely to want one immediately afterwards, so almost all of
        /// that traffic can be served without leaving the thread.
        ///
        /// Static rather than per-manager on purpose. A NetPacket is a plain buffer with no
        /// affinity to whichever manager pooled it, and the load tester runs thousands of managers
        /// across a few dozen threads — one hot cache per thread beats one cold queue per manager.
        /// The shared pool stays as the balancing layer, which is what makes the asymmetry work:
        /// packets are often rented on a receive thread and recycled on a send thread, so a purely
        /// thread-local scheme would drain one side and overflow the other.
        ///
        /// Uses <see cref="NetPacket.Next"/>, which is already the pool's link field, so the list
        /// itself costs no allocation.
        /// </summary>
        [ThreadStatic] private static NetPacket t_freeList;
        [ThreadStatic] private static int t_freeCount;

        /// <summary>
        /// Packets a single thread keeps to itself. Bounds the memory this adds to
        /// threads x this x MaxPacketSize — a few MB at any realistic thread count — while being
        /// deep enough to absorb a tick's worth of get/recycle churn without reaching the shared pool.
        /// </summary>
        private const int ThreadLocalPoolCap = 128;

        /// <summary>
        /// Maximum packet pool size (increase if you have tons of packets sending)
        /// </summary>
        public int PacketPoolSize = 1000;

        /// <summary>Packets held across every stripe. Advisory — the stripes are read unsynchronised.</summary>
        public int PoolCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < PoolStripeCount; i++)
                    total += Volatile.Read(ref _poolCounts[i * CountStride]);
                return total;
            }
        }

        private NetPacket PoolGetWithData(PacketProperty property, byte[] data, int start, int length)
        {
            int headerSize = NetPacket.GetHeaderSize(property);
            NetPacket packet = PoolGetPacket(length + headerSize);
            packet.Property = property;
            Buffer.BlockCopy(data, start, packet.RawData, headerSize, length);
            return packet;
        }

        //Get packet with size
        private NetPacket PoolGetWithProperty(PacketProperty property, int size)
        {
            NetPacket packet = PoolGetPacket(size + NetPacket.GetHeaderSize(property));
            packet.Property = property;
            return packet;
        }

        private NetPacket PoolGetWithProperty(PacketProperty property)
        {
            NetPacket packet = PoolGetPacket(NetPacket.GetHeaderSize(property));
            packet.Property = property;
            return packet;
        }

        /// <summary>
        /// Packets kept per connected peer. The pool's job is to absorb the gap between a burst of
        /// sends draining it and those packets coming back, and that gap grows with the peer count —
        /// a fixed ceiling that is generous at 100 players is a wall at 3,000, where the pool swings
        /// between full (recycled packets thrown away) and empty (every get allocating). 0 disables
        /// scaling and uses <see cref="PacketPoolSize"/> alone.
        /// </summary>
        public int PacketPoolSizePerPeer = 0;

        /// <summary>Upper bound on the scaled pool so peer count can't translate into unbounded memory. 0 = no cap.</summary>
        public int PacketPoolSizeMax = 0;

        /// <summary>
        /// Cached result of <see cref="RecomputePoolCap"/>.
        ///
        /// A plain field, read once per recycle. It used to be recomputed there, which meant an
        /// interlocked read of the peer count on a cache line every sending thread was touching —
        /// several hundred thousand times a second — to obtain a number that only changes when
        /// somebody joins or leaves. Refreshed from those two events instead.
        /// </summary>
        private int _effectivePoolCap = 1000;

        /// <summary>
        /// Host hook: given the connected peer count, returns the packet pool ceiling to use.
        ///
        /// Null by default, which keeps <see cref="PacketPoolSizeMax"/> governing exactly as it did
        /// — LiteNetLib on its own has no opinion about how big the host's box is. Basis injects a
        /// resolver that sizes this against available memory instead of a shipped constant.
        /// </summary>
        public System.Func<int, int> ResolvePacketPoolMax;

        /// <summary>
        /// Host hook: given the connected peer count, returns the per-peer unreliable queue bound.
        /// Null keeps <see cref="MaxUnreliableQueuePerPeer"/>, the standalone behaviour.
        /// </summary>
        public System.Func<int, int> ResolveUnreliableQueuePerPeer;

        /// <summary>
        /// Host hook: given the connected peer count, returns the per-peer priority queue bound.
        /// Null keeps <see cref="MaxPriorityUnreliableQueuePerPeer"/>, the standalone behaviour.
        /// </summary>
        public System.Func<int, int> ResolvePriorityUnreliableQueuePerPeer;

        /// <summary>
        /// Resolved per-peer unreliable bound, refreshed by <see cref="RecomputePoolCap"/>.
        ///
        /// Cached as a plain field for the same reason <see cref="_effectivePoolCap"/> is: the
        /// enqueue path reads it on every unreliable send — millions of times a second at a few
        /// thousand players — and must not pay for a delegate call or a peer-count read there.
        /// </summary>
        public int EffectiveUnreliableQueuePerPeer = 256;

        /// <summary>Resolved per-peer priority bound, refreshed by <see cref="RecomputePoolCap"/>.</summary>
        public int EffectivePriorityUnreliableQueuePerPeer = 1024;

        /// <summary><see cref="PacketPoolSize"/> is the floor; peer count raises it up to the resolved ceiling.</summary>
        internal void RecomputePoolCap()
        {
            int peers = ConnectedPeersCount;

            // Both ceilings move with population, and this is the one place that already runs on
            // every join and leave — so they are refreshed together rather than each growing its
            // own trigger.
            var queueResolver = ResolveUnreliableQueuePerPeer;
            EffectiveUnreliableQueuePerPeer = queueResolver != null
                ? queueResolver(peers)
                : MaxUnreliableQueuePerPeer;

            var priorityResolver = ResolvePriorityUnreliableQueuePerPeer;
            EffectivePriorityUnreliableQueuePerPeer = priorityResolver != null
                ? priorityResolver(peers)
                : MaxPriorityUnreliableQueuePerPeer;

            var poolResolver = ResolvePacketPoolMax;
            int poolMax = poolResolver != null ? poolResolver(peers) : PacketPoolSizeMax;

            if (PacketPoolSizePerPeer <= 0)
            {
                _effectivePoolCap = PacketPoolSize;
                return;
            }

            // Per peer, the pool has to cover that peer's working set AND whatever its send queues
            // are allowed to hold — because every packet a queue lets go of, by draining or by
            // trimming at the bound, arrives here to be recycled.
            //
            // Leaving the queue terms out is what made this a 96,000-packet pool standing in front
            // of 9.5 million packets of queue capacity at 1000 players. The surplus did not vanish;
            // PoolRecycle dropped it, and because those packets had lived in a queue long enough to
            // reach gen2, the collector could neither compact nor return the space. Measured: 2.7 GB
            // of a 7 GB working set was gen2 fragmentation, against 11 MB on the same build while it
            // was keeping up.
            //
            // This raises a RETENTION ceiling, not an allocation. Peak memory is decided by how many
            // packets the queues can hold; all this decides is whether those packets come back
            // reusable or turn into holes.
            long perPeerTotal = (long)PacketPoolSizePerPeer
                                + EffectiveUnreliableQueuePerPeer
                                + EffectivePriorityUnreliableQueuePerPeer;

            long scaled = (long)peers * perPeerTotal;
            if (scaled < PacketPoolSize)
                scaled = PacketPoolSize;
            if (poolMax > 0 && scaled > poolMax)
                scaled = poolMax;
            _effectivePoolCap = (int)Math.Min(scaled, int.MaxValue);
        }

        internal NetPacket PoolGetPacket(int size)
        {
            if (size > NetConstants.MaxPacketSize)
                return new NetPacket(size);

            // This thread's own cache first — no atomics, no shared cache lines.
            NetPacket packet = t_freeList;
            if (packet != null)
            {
                t_freeList = packet.Next;
                t_freeCount--;
            }
            else
            {
                // Empty: this thread rents more than it recycles (the BSR send path does exactly
                // that). Fall through to the shared pool, which is where the other side's surplus
                // ends up. Home stripe first; if it is dry, walk the others before allocating,
                // because the recycling threads have their own homes and a renter's stripe can be
                // empty while the pool as a whole is full.
                int stripe = HomeStripe();
                if (!_pool[stripe].TryDequeue(out packet))
                {
                    stripe = -1;
                    for (int i = 1; i < PoolStripeCount; i++)
                    {
                        int probe = (t_poolStripe + i) & PoolStripeMask;
                        if (_pool[probe].TryDequeue(out packet)) { stripe = probe; break; }
                    }
                    if (stripe < 0)
                        return new NetPacket(size);
                }
                Interlocked.Decrement(ref _poolCounts[stripe * CountStride]);
            }

            packet.Next = null;
            packet.Size = size;
            if (packet.RawData.Length < size)
            {
                int capacity = 64;
                while (capacity < size)
                    capacity <<= 1;
                if (capacity > NetConstants.MaxPacketSize)
                    capacity = NetConstants.MaxPacketSize;
                packet.RawData = new byte[capacity];
            }
            return packet;
        }

        internal void PoolRecycle(NetPacket packet)
        {
            //Don't pool big packets. Save memory
            if (packet.RawData.Length > NetConstants.MaxPacketSize)
                return;

            //Clean fragmented flag
            packet.RawData[0] = 0;

            // Keep it on this thread if there is room. This is the common case by a wide margin —
            // a thread that recycles a packet almost always wants one again shortly.
            if (t_freeCount < ThreadLocalPoolCap)
            {
                packet.Next = t_freeList;
                t_freeList = packet;
                t_freeCount++;
                return;
            }

            // Local cache full: this thread recycles more than it rents, so hand the surplus to the
            // shared pool for the threads that are short.
            //
            // Plain reads: both are advisory here, and this runs on every recycle, so the
            // read-modify-write the interlocked form implies is pure overhead at that rate.
            // Cap is per stripe, so the total retained is unchanged by how many stripes exist.
            int perStripeCap = _effectivePoolCap / PoolStripeCount;
            if (perStripeCap < 1) perStripeCap = 1;

            // Home stripe first, then spill into the others before giving up.
            //
            // ⚠️ The spill is not an optimisation, it is what makes the cap mean what it says. Far
            // fewer threads recycle than rent — the send path hands packets to a peer queue and a
            // transport thread returns them — so without it only the recyclers' own stripes ever
            // fill. The rest would sit empty while those few hit their share of the cap and started
            // dropping packets, cutting the pool's effective depth to (recycling threads / stripes)
            // of what was configured and pushing the difference onto the allocator.
            int stripe = HomeStripe();
            for (int i = 0; i < PoolStripeCount; i++)
            {
                int probe = (stripe + i) & PoolStripeMask;
                if (Volatile.Read(ref _poolCounts[probe * CountStride]) >= perStripeCap)
                    continue;

                packet.Next = null;
                _pool[probe].Enqueue(packet);
                Interlocked.Increment(ref _poolCounts[probe * CountStride]);
                return;
            }
            // Every stripe is at its share: the pool is genuinely full, so this packet is surplus.
        }
    }
}
