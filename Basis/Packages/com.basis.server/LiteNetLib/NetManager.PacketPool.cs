using System;
using System.Threading;

namespace LiteNetLib
{
    public partial class NetManager
    {
        private NetPacket _poolHead;
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

        internal NetPacket PoolGetPacket(int size)
        {
            if (size > NetConstants.MaxPacketSize)
                return new NetPacket(size);

            NetPacket packet;
            do
            {
                packet = _poolHead;
                if (packet == null)
                    return new NetPacket(size);
            } while (Interlocked.CompareExchange(ref _poolHead, packet.Next, packet) != packet);
            Interlocked.Decrement(ref _poolCount);

            packet.Next = null;
            packet.Size = size;
            if (packet.RawData.Length < size)
                packet.RawData = new byte[size];
            return packet;
        }

        internal void PoolRecycle(NetPacket packet)
        {
            if (packet.RawData.Length > NetConstants.MaxPacketSize ||
                Interlocked.CompareExchange(ref _poolCount, 0, 0) >= PacketPoolSize)
            {
                //Don't pool big packets. Save memory
                return;
            }

            //Clean fragmented flag
            packet.RawData[0] = 0;
            NetPacket oldHead;
            do
            {
                oldHead = _poolHead;
                packet.Next = oldHead;
            } while (Interlocked.CompareExchange(ref _poolHead, packet, oldHead) != oldHead);
            Interlocked.Increment(ref _poolCount);
        }
    }
}