using System.Collections.Generic;
using System.Threading;

namespace LiteNetLib
{
    internal abstract class BaseChannel
    {
        protected readonly NetPeer Peer;
        protected readonly Queue<NetPacket> OutgoingQueue = new Queue<NetPacket>(NetConstants.DefaultWindowSize);
        private int _isAddedToPeerChannelSendQueue;

        public int PacketsInQueue => OutgoingQueue.Count;

        protected BaseChannel(NetPeer peer)
        {
            Peer = peer;
        }

        public void AddToQueue(NetPacket packet)
        {
            lock (OutgoingQueue)
            {
                OutgoingQueue.Enqueue(packet);
            }
            Peer.NoteChannelQueued(packet.Size);
            AddToPeerChannelSendQueue();
        }

        protected NetPacket DequeueOutgoing()
        {
            NetPacket packet = OutgoingQueue.Dequeue();
            Peer.NoteChannelQueued(-packet.Size);
            return packet;
        }

        protected void AddToPeerChannelSendQueue()
        {
            if (Interlocked.CompareExchange(ref _isAddedToPeerChannelSendQueue, 1, 0) == 0)
            {
                Peer.AddToReliableChannelSendQueue(this);
            }
        }

        public bool SendAndCheckQueue()
        {
            bool hasPacketsToSend = SendNextPackets();
            if (!hasPacketsToSend)
                Interlocked.Exchange(ref _isAddedToPeerChannelSendQueue, 0);

            return hasPacketsToSend;
        }

        protected abstract bool SendNextPackets();
        public abstract bool ProcessPacket(NetPacket packet);
    }
}
