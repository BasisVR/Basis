using System;
using System.Collections.Concurrent;
using System.Threading;

namespace LiteNetLib
{
    public partial class NetManager
    {
        private readonly ConcurrentQueue<NetPacket> _pool = new ConcurrentQueue<NetPacket>();
        private int _poolCount;

        /// <summary>
        /// Maximum packet pool size (increase if you have tons of packets sending)
        /// </summary>
        public int PacketPoolSize = 1000;

        public int PoolCount => _poolCount;

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

        /// <summary><see cref="PacketPoolSize"/> is the floor; peer count raises it up to <see cref="PacketPoolSizeMax"/>.</summary>
        private int EffectivePoolCap()
        {
            if (PacketPoolSizePerPeer <= 0)
                return PacketPoolSize;

            long scaled = (long)ConnectedPeersCount * PacketPoolSizePerPeer;
            if (scaled < PacketPoolSize)
                scaled = PacketPoolSize;
            if (PacketPoolSizeMax > 0 && scaled > PacketPoolSizeMax)
                scaled = PacketPoolSizeMax;
            return (int)scaled;
        }

        internal NetPacket PoolGetPacket(int size)
        {
            if (size > NetConstants.MaxPacketSize)
                return new NetPacket(size);

            if (!_pool.TryDequeue(out NetPacket packet))
                return new NetPacket(size);
            Interlocked.Decrement(ref _poolCount);

            packet.Next = null;
            packet.Size = size;
            if (packet.RawData.Length < size)
                packet.RawData = new byte[size];
            return packet;
        }

        internal void PoolRecycle(NetPacket packet)
        {
            //Don't pool big packets. Save memory
            if (packet.RawData.Length > NetConstants.MaxPacketSize)
                return;

            // Plain read: _poolCount is advisory here, and this runs on every recycle, so the
            // read-modify-write the interlocked form implies is pure overhead at that rate.
            if (Volatile.Read(ref _poolCount) >= EffectivePoolCap())
                return;

            //Clean fragmented flag
            packet.RawData[0] = 0;
            packet.Next = null;
            _pool.Enqueue(packet);
            Interlocked.Increment(ref _poolCount);
        }
    }
}
