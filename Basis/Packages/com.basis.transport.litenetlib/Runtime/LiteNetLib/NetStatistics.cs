using System;
using System.Threading;

namespace LiteNetLib
{
    /// <summary>
    /// Wire counters. Every field used to be a single <c>Interlocked</c> target, which meant one
    /// cache line ping-ponging between every core that sent a packet — at 500 players the send
    /// loop drives ~160K sends/s from ~30 threads, so the counters cost far more than the thing
    /// they were counting.
    ///
    /// Counters are striped across cache lines and each thread sticks to one stripe. The
    /// interlocked op stays (two threads can still hash to the same stripe, and a lost update
    /// would silently corrupt the totals the health endpoint reports) but it is now almost always
    /// uncontended, which is the part that was expensive.
    /// </summary>
    public sealed class NetStatistics
    {
        // Sized from the machine, not assumed. A literal 64 was "comfortably above the core count"
        // only on the box it was written on; on a 256-core host it is a quarter of the threads and
        // the collisions it exists to prevent come straight back. Power of two: the index is a
        // mask, not a modulo.
        //
        // Two per core, floored so a tiny container still gets a usable table and capped so a very
        // large host does not allocate a pointless one.
        private static readonly int MachineStripeCount = ConcurrencyWidth(perCore: 2, min: 16, max: 1024);

        // Stripe width is per-INSTANCE: the manager-level object is hammered by every sending
        // thread and gets the machine-wide table, but a per-peer object is only ever touched by
        // the handful of threads that own that peer's send/receive path. Giving each of thousands
        // of peers a machine-wide table cost 20 KB+ per peer on a 32-core host (5 arrays x
        // stripes x cache line) and hundreds of KB on very wide ones, all churned on every
        // connect/disconnect.
        private readonly int _stripeCount;
        private readonly int _stripeMask;

        /// <summary>
        /// Local copy of the shared sizing helper — LiteNetLib sits below Basis.Network.Core and
        /// cannot reference it. Keep the two in step.
        /// </summary>
        private static int ConcurrencyWidth(int perCore, int min, int max)
        {
            long wanted = (long)Environment.ProcessorCount * (perCore < 1 ? 1 : perCore);
            if (wanted < min) wanted = min;
            if (wanted > max) wanted = max;

            int pow2 = 1;
            while (pow2 < wanted) pow2 <<= 1;
            return pow2;
        }

        // 8 longs = one 64-byte cache line per stripe per counter, so neighbouring stripes never
        // share a line. Index i lives at i * Stride.
        private const int Stride = 8;

        private readonly long[] _packetsSent;
        private readonly long[] _packetsReceived;
        private readonly long[] _bytesSent;
        private readonly long[] _bytesReceived;
        private readonly long[] _packetLoss;

        /// <summary>Machine-wide table, for the manager-level instance every sending thread shares.</summary>
        public NetStatistics() : this(MachineStripeCount)
        {
        }

        /// <summary>Narrow table for lightly-contended instances (one per peer).</summary>
        public NetStatistics(int stripesWanted)
        {
            int pow2 = 1;
            while (pow2 < stripesWanted && pow2 < 1024) pow2 <<= 1;
            _stripeCount = pow2;
            _stripeMask = pow2 - 1;
            _packetsSent = new long[_stripeCount * Stride];
            _packetsReceived = new long[_stripeCount * Stride];
            _bytesSent = new long[_stripeCount * Stride];
            _bytesReceived = new long[_stripeCount * Stride];
            _packetLoss = new long[_stripeCount * Stride];
        }

        // Managed thread ids are dense and stable for a thread's lifetime, so this pins a thread
        // to one stripe without a ThreadStatic registry to maintain.
        private int StripeIndex => (Environment.CurrentManagedThreadId & _stripeMask) * Stride;

        private long Sum(long[] stripes)
        {
            long total = 0;
            for (int i = 0; i < _stripeCount; i++)
                total += Interlocked.Read(ref stripes[i * Stride]);
            return total;
        }

        private void Clear(long[] stripes)
        {
            for (int i = 0; i < _stripeCount; i++)
                Interlocked.Exchange(ref stripes[i * Stride], 0);
        }

        public long PacketsSent => Sum(_packetsSent);
        public long PacketsReceived => Sum(_packetsReceived);
        public long BytesSent => Sum(_bytesSent);
        public long BytesReceived => Sum(_bytesReceived);
        public long PacketLoss => Sum(_packetLoss);

        public long PacketLossPercent
        {
            get
            {
                long sent = PacketsSent, loss = PacketLoss;

                return sent == 0 ? 0 : loss * 100 / sent;
            }
        }

        public void Reset()
        {
            Clear(_packetsSent);
            Clear(_packetsReceived);
            Clear(_bytesSent);
            Clear(_bytesReceived);
            Clear(_packetLoss);
        }

        public void IncrementPacketsSent()
        {
            Interlocked.Increment(ref _packetsSent[StripeIndex]);
        }

        public void IncrementPacketsReceived()
        {
            Interlocked.Increment(ref _packetsReceived[StripeIndex]);
        }

        public void AddBytesSent(long bytesSent)
        {
            Interlocked.Add(ref _bytesSent[StripeIndex], bytesSent);
        }

        public void AddBytesReceived(long bytesReceived)
        {
            Interlocked.Add(ref _bytesReceived[StripeIndex], bytesReceived);
        }

        /// <summary>
        /// Records a whole batch of sends with one pair of atomics instead of one pair per
        /// datagram. Used by the batched send path, where the caller already knows both totals.
        /// </summary>
        public void AddSentBatch(long packets, long bytes)
        {
            int stripe = StripeIndex;
            Interlocked.Add(ref _packetsSent[stripe], packets);
            Interlocked.Add(ref _bytesSent[stripe], bytes);
        }

        public void IncrementPacketLoss()
        {
            Interlocked.Increment(ref _packetLoss[StripeIndex]);
        }

        public void AddPacketLoss(long packetLoss)
        {
            Interlocked.Add(ref _packetLoss[StripeIndex], packetLoss);
        }

        public override string ToString()
        {
            return
                string.Format(
                    "BytesReceived: {0}\nPacketsReceived: {1}\nBytesSent: {2}\nPacketsSent: {3}\nPacketLoss: {4}\nPacketLossPercent: {5}\n",
                    BytesReceived,
                    PacketsReceived,
                    BytesSent,
                    PacketsSent,
                    PacketLoss,
                    PacketLossPercent);
        }
    }
}
