using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public sealed class BasisWebSocketPeer : NetPeer
    {
        private readonly BasisWebSocketLink _link;
        private readonly int _id;

        internal BasisWebSocketPeer(BasisWebSocketLink link, int id)
        {
            _link = link;
            _id = id;
        }

        internal BasisWebSocketLink Link => _link;

        public int Id => _id;

        public IPAddress Address => _link.RemoteEndPoint?.Address ?? IPAddress.None;

        public IPEndPoint RemoteEndPoint => _link.RemoteEndPoint;

        public int RemoteId => _link.RemoteId;

        public int RoundTripTime => _link.RoundTripMs;

        public float TimeSinceLastPacket => _link.MillisecondsSinceLastReceive;

        public long RemoteTimeDelta => _link.RemoteTimeDelta;

        public int Mtu => _link.Mtu;

        public object Tag { get; set; }

        public string StackId => BasisNetworkStackRegistry.WebSocketId;

        public bool SupportsDirectConnect => false;

        public bool IsConnected => _link.IsConnected;

        public void Disconnect()
        {
            _link.Disconnect(null, 0, 0, false);
        }

        public void Disconnect(byte[] b)
        {
            _link.Disconnect(b, 0, b == null ? 0 : b.Length, false);
        }

        public void DisconnectForce()
        {
            _link.Disconnect(null, 0, 0, true);
        }

        public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (data == null) return;
            _link.SendData(channelNumber, deliveryMethod, data, 0, data.Length, -1, 0);
        }

        public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (data == null) return;
            _link.SendData(channelNumber, deliveryMethod, data.Data, 0, data.Length, -1, 0);
        }

        public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0)
        {
            if (data == null) return;
            _link.SendData(channelNumber, DeliveryMethod.Unreliable, data, offset, length, patchOffset, patchValue);
        }

        public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod)
        {
            return BasisWebSocketProtocol.IsDroppable(deliveryMethod) ? _link.Outbound.QueuedCount(channel) : 0;
        }

        public override string ToString()
        {
            return $"WebSocket peer {_id} ({_link.RemoteEndPoint})";
        }
    }

    public abstract class BasisWebSocketLink
    {
        private static readonly WaitCallback FlushCallback = state => _ = ((BasisWebSocketLink)state).FlushLoopAsync();
        internal readonly BasisWebSocketNetManager Manager;
        internal readonly BasisWebSocketOutbound Outbound;
        internal readonly int Mtu;
        internal IPEndPoint RemoteEndPoint;
        internal int RemoteId = -1;
        private long _lastReceive;
        private long _lastPingSent;
        private int _roundTripMs;
        private long _remoteTimeDelta;
        private int _disconnectRaised;

        protected BasisWebSocketLink(BasisWebSocketNetManager manager, BasisWebSocketTransportConfig config)
        {
            Manager = manager;
            Outbound = new BasisWebSocketOutbound(config.MaxUnreliableBytesPerPeer, config.MaxVoiceBytesPerPeer, config.MaxPendingBytesPerPeer);
            Mtu = config.VirtualMtu > 0 ? config.VirtualMtu : 1432;
            long now = Stopwatch.GetTimestamp();
            _lastReceive = now;
            _lastPingSent = now;
        }

        internal BasisWebSocketPeer Peer { get; set; }

        internal abstract bool IsConnected { get; }

        internal int RoundTripMs => Volatile.Read(ref _roundTripMs);

        internal long RemoteTimeDelta => Interlocked.Read(ref _remoteTimeDelta);

        internal long LastReceiveTimestamp => Interlocked.Read(ref _lastReceive);

        internal float MillisecondsSinceLastReceive => (float)((Stopwatch.GetTimestamp() - LastReceiveTimestamp) * 1000.0 / Stopwatch.Frequency);

        protected virtual bool PollsForFlush => false;

        internal void MarkReceived()
        {
            Interlocked.Exchange(ref _lastReceive, Stopwatch.GetTimestamp());
        }

        internal void SendData(byte channel, DeliveryMethod method, byte[] data, int offset, int length, int patchOffset, byte patchValue)
        {
            if (!IsConnected) return;
            BasisWebSocketOutbound.AppendResult result = Outbound.AppendData(channel, method, data, offset, length, patchOffset, patchValue);
            switch (result)
            {
                case BasisWebSocketOutbound.AppendResult.QueuedStartFlush:
                    ScheduleFlush();
                    break;
                case BasisWebSocketOutbound.AppendResult.Dropped:
                    Manager.NoteDropped(channel);
                    break;
                case BasisWebSocketOutbound.AppendResult.Overflow:
                    OnSendBacklogExceeded();
                    break;
            }
        }

        internal bool SendControl(byte kind, byte[] body, int offset, int length)
        {
            BasisWebSocketOutbound.AppendResult result = Outbound.AppendControl(kind, body, offset, length);
            if (result == BasisWebSocketOutbound.AppendResult.QueuedStartFlush)
            {
                ScheduleFlush();
            }
            return result == BasisWebSocketOutbound.AppendResult.Queued || result == BasisWebSocketOutbound.AppendResult.QueuedStartFlush;
        }

        protected void RequestCloseAfterFlush()
        {
            if (Outbound.RequestCloseAfterFlush())
            {
                ScheduleFlush();
            }
        }

        protected virtual void OnSendBacklogExceeded()
        {
            BNL.LogWarning($"[WebSocket] {Peer} fell {Outbound.PendingBytes} bytes behind on reliable data; disconnecting it.");
            Disconnect(null, 0, 0, true);
        }

        internal void ScheduleFlush()
        {
            if (PollsForFlush) return;
            ThreadPool.UnsafeQueueUserWorkItem(FlushCallback, this);
        }

        private async Task FlushLoopAsync()
        {
            try
            {
                while (true)
                {
                    if (!Outbound.TryTakeBatch(out byte[] batch, out int length, out bool closeNow))
                    {
                        if (closeNow)
                        {
                            await CloseTransportAsync().ConfigureAwait(false);
                        }
                        return;
                    }
                    await SendBatchAsync(batch, length).ConfigureAwait(false);
                    Manager.NoteSent(length);
                    Outbound.ReturnBatch(batch);
                }
            }
            catch (Exception ex)
            {
                OnTransportFailed(ex);
            }
        }

        internal void FlushSynchronously()
        {
            try
            {
                while (true)
                {
                    if (!Outbound.TryTakeBatch(out byte[] batch, out int length, out bool closeNow))
                    {
                        if (closeNow)
                        {
                            CloseTransportAsync();
                        }
                        return;
                    }
                    Task send = SendBatchAsync(batch, length);
                    if (send.IsFaulted)
                    {
                        OnTransportFailed(send.Exception?.InnerException ?? send.Exception);
                        return;
                    }
                    Manager.NoteSent(length);
                    Outbound.ReturnBatch(batch);
                }
            }
            catch (Exception ex)
            {
                OnTransportFailed(ex);
            }
        }

        protected abstract Task SendBatchAsync(byte[] batch, int length);

        protected abstract Task CloseTransportAsync();

        protected abstract void OnTransportFailed(Exception ex);

        internal abstract void Disconnect(byte[] data, int offset, int length, bool force);

        protected bool TryClaimDisconnectEvent()
        {
            return Interlocked.Exchange(ref _disconnectRaised, 1) == 0;
        }

        protected bool DisconnectEventClaimed => Volatile.Read(ref _disconnectRaised) != 0;

        internal void ServicePing(long now, long pingIntervalTicks)
        {
            if (now - Interlocked.Read(ref _lastPingSent) < pingIntervalTicks) return;
            Interlocked.Exchange(ref _lastPingSent, now);
            byte[] body = new byte[BasisWebSocketProtocol.PingBodyBytes];
            BasisWebSocketProtocol.WriteInt64(body, 0, now);
            SendControl(BasisWebSocketProtocol.KindPing, body, 0, body.Length);
        }

        internal void HandlePing(byte[] buffer, int bodyOffset, int bodyLength)
        {
            if (bodyLength < BasisWebSocketProtocol.PingBodyBytes) return;
            byte[] body = new byte[BasisWebSocketProtocol.PongBodyBytes];
            Buffer.BlockCopy(buffer, bodyOffset, body, 0, 8);
            BasisWebSocketProtocol.WriteInt64(body, 8, DateTime.UtcNow.Ticks);
            SendControl(BasisWebSocketProtocol.KindPong, body, 0, body.Length);
        }

        internal void HandlePong(byte[] buffer, int bodyOffset, int bodyLength)
        {
            if (bodyLength < BasisWebSocketProtocol.PingBodyBytes) return;
            long now = Stopwatch.GetTimestamp();
            long sent = BasisWebSocketProtocol.ReadInt64(buffer, bodyOffset);
            long elapsed = now - sent;
            if (elapsed < 0 || elapsed > Stopwatch.Frequency * 120) return;
            int sample = (int)(elapsed * 1000 / Stopwatch.Frequency);
            int previous = Volatile.Read(ref _roundTripMs);
            Volatile.Write(ref _roundTripMs, previous <= 0 ? Math.Max(1, sample) : Math.Max(1, (previous * 3 + sample) / 4));
            if (bodyLength >= BasisWebSocketProtocol.PongBodyBytes)
            {
                long remoteUtc = BasisWebSocketProtocol.ReadInt64(buffer, bodyOffset + 8);
                if (remoteUtc > 0)
                {
                    long halfTrip = TimeSpan.TicksPerSecond * elapsed / Stopwatch.Frequency / 2;
                    Interlocked.Exchange(ref _remoteTimeDelta, remoteUtc + halfTrip - DateTime.UtcNow.Ticks);
                }
            }
        }

        internal void SetRemoteTime(long remoteUtcTicks)
        {
            if (remoteUtcTicks > 0)
            {
                Interlocked.Exchange(ref _remoteTimeDelta, remoteUtcTicks - DateTime.UtcNow.Ticks);
            }
        }

        internal static NetPacketReader CopyToReader(byte[] buffer, int offset, int length, byte channel, DeliveryMethod method)
        {
            if (length <= 0)
            {
                NetPacketReader empty = new NetPacketReader(Array.Empty<byte>(), 0, 0, null);
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
                empty.channel = channel;
                empty.method = method;
#endif
                return empty;
            }
            byte[] copy = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(buffer, offset, copy, 0, length);
            NetPacketReader reader = new NetPacketReader(copy, 0, length, () => System.Buffers.ArrayPool<byte>.Shared.Return(copy));
#if UNITY_EDITOR || UNITY_INCLUDE_INSTRUMENTATION
            reader.channel = channel;
            reader.method = method;
#endif
            return reader;
        }

        internal static NetPacketReader EmptyReader()
        {
            return new NetPacketReader(Array.Empty<byte>(), 0, 0, null);
        }
    }
}
