#if DEBUG
#define STATS_ENABLED
#endif
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    /// <summary>
    /// Peer connection state
    /// </summary>
    [Flags]
    public enum ConnectionState : byte
    {
        Outgoing = 1 << 1,
        Connected = 1 << 2,
        ShutdownRequested = 1 << 3,
        Disconnected = 1 << 4,
        EndPointChange = 1 << 5,
        Any = Outgoing | Connected | ShutdownRequested | EndPointChange
    }

    internal enum ConnectRequestResult
    {
        None,
        P2PLose, //when peer connecting
        Reconnection,  //when peer was connected
        NewConnection  //when peer was disconnected
    }

    internal enum DisconnectResult
    {
        None,
        Reject,
        Disconnect
    }

    internal enum ShutdownResult
    {
        None,
        Success,
        WasConnected
    }

    /// <summary>
    /// Network peer. Main purpose is sending messages to specific peer.
    /// </summary>
    public class NetPeer : IPEndPoint
    {
        //Ping and RTT
        private int _rtt;
        private int _avgRtt;
        private int _rttCount;
        private double _resendDelay = 27.0;
        private float _pingSendTimer;
        private float _rttResetTimer;
        private readonly Stopwatch _pingTimer = new Stopwatch();
        private float _timeSinceLastPacket;
        private long _remoteDelta;

        //Common
        private readonly object _shutdownLock = new object();

        internal volatile NetPeer NextPeer;
        internal NetPeer PrevPeer;

        internal byte ConnectionNum
        {
            get => _connectNum;
            private set
            {
                _connectNum = value;
                _mergeData.ConnectionNumber = value;
                _pingPacket.ConnectionNumber = value;
                _pongPacket.ConnectionNumber = value;
            }
        }

        //Channels
        private readonly ConcurrentQueue<NetPacket> _unreliableChannel = new ConcurrentQueue<NetPacket>();

        /// <summary>
        /// Latency-critical unreliable traffic, held apart from <see cref="_unreliableChannel"/> and
        /// drained ahead of it. See <see cref="NetManager.PriorityUnreliableChannels"/>.
        ///
        /// The bulk queue's whole drop policy rests on newer entries superseding older ones, which
        /// is true of state updates and false of a media stream: every voice packet is unique audio,
        /// so a dropped one is a permanent gap rather than a stale frame nobody would have rendered.
        /// Sharing one queue therefore shed voice at the bulk stream's drop rate — and because bulk
        /// traffic outnumbers voice by orders of magnitude, a backlog of position updates was enough
        /// to punch holes in every conversation on the instance. Anything that survived the trim was
        /// still stuck behind the backlog, arriving too late for the receiver's jitter buffer.
        /// </summary>
        private readonly ConcurrentQueue<NetPacket> _priorityUnreliableChannel = new ConcurrentQueue<NetPacket>();

        private readonly ConcurrentQueue<BaseChannel> _channelSendQueue;
        private readonly BaseChannel[] _channels;

        //MTU
        private int _mtu;
        private int _mtuIdx;
        private bool _finishMtu;
        private float _mtuCheckTimer;
        private int _mtuCheckAttempts;
        private const int MtuCheckDelay = 1000;
        private const int MaxMtuCheckAttempts = 4;
        private readonly object _mtuMutex = new object();

        //Fragment
        private class IncomingFragments
        {
            // Fragments with TotalFragments <= threshold use a fixed array (faster)
            // Larger sets use a dictionary to avoid large upfront allocations
            private const int ArrayThreshold = 64;

            private readonly NetPacket[] _array;
            private readonly Dictionary<ushort, NetPacket> _dict;

            public readonly ushort TotalFragments;
            public readonly byte ChannelId;
            public int ReceivedCount;
            public int TotalSize;
            /// <summary>Peer clock (ms) when a fragment last arrived for this set. See fragment sweep in Update.</summary>
            public double LastTouchedMs;

            public IncomingFragments(ushort totalFragments, byte channelId)
            {
                TotalFragments = totalFragments;
                ChannelId = channelId;
                if (totalFragments <= ArrayThreshold)
                    _array = new NetPacket[totalFragments];
                else
                    _dict = new Dictionary<ushort, NetPacket>();
            }

            public bool Contains(ushort part) =>
                _array != null ? _array[part] != null : _dict.ContainsKey(part);

            public bool TryGet(ushort part, out NetPacket packet)
            {
                if (_array != null)
                {
                    packet = _array[part];
                    return packet != null;
                }
                return _dict.TryGetValue(part, out packet);
            }

            public void Set(ushort part, NetPacket packet)
            {
                if (_array != null)
                    _array[part] = packet;
                else
                    _dict[part] = packet;
            }

            public void Remove(ushort part)
            {
                if (_array != null)
                    _array[part] = null;
                else
                    _dict.Remove(part);
            }

            public void RecycleAll(NetManager netManager)
            {
                if (_array != null)
                {
                    foreach (var p in _array)
                        if (p != null) netManager.PoolRecycle(p);
                }
                else
                {
                    foreach (var p in _dict.Values)
                        netManager.PoolRecycle(p);
                }
            }
        }
        private int _fragmentId;

        private readonly object _fragmentsLock = new object();
        private readonly Dictionary<ushort, IncomingFragments> _holdedFragments;
        private readonly Dictionary<ushort, ushort> _deliveredFragments;

        /// <summary>
        /// Reassembly sets an abandoned sender leaves behind used to live for the whole session —
        /// up to MaxFragmentsInWindow x channels sets, each holding full NetPackets, per peer.
        /// Fragments of a live transfer keep arriving (reliable resend refreshes the stamp), so a
        /// set nothing has touched for this long can only belong to a sender that stopped
        /// mid-message; recycle it. Sweep is amortized to once per FragmentSweepIntervalMs.
        /// </summary>
        private const double FragmentStaleMs = 30_000;
        private const double FragmentSweepIntervalMs = 5_000;
        private double _peerClockMs;
        private double _nextFragmentSweepMs = FragmentSweepIntervalMs;

        //Merging
        private readonly NetPacket _mergeData;
        private int _mergePos;
        private int _mergeCount;
        /// <summary>
        /// Framing of the entries currently held in <see cref="_mergeData"/>. A datagram carries
        /// one framing or the other and the reader picks its parser from the container property,
        /// so the buffer is flushed whenever this would change.
        /// </summary>
        private bool _mergeCompact;
        /// <summary>Time the current partial merge buffer has been waiting. See MergeHoldMs.</summary>
        private float _mergeHeldMs;

        //Connection
        private int _connectAttempts;
        private float _connectTimer;
        private long _connectTime;
        private byte _connectNum;
        private ConnectionState _connectionState;
        private NetPacket _shutdownPacket;
        private const int ShutdownDelay = 300;
        private float _shutdownTimer;
        private readonly NetPacket _pingPacket;
        private readonly NetPacket _pongPacket;
        private readonly NetPacket _connectRequestPacket;
        private readonly NetPacket _connectAcceptPacket;

        /// <summary>
        /// Peer parent NetManager
        /// </summary>
        public readonly NetManager NetManager;

        /// <summary>
        /// Current connection state
        /// </summary>
        public ConnectionState ConnectionState => _connectionState;

        /// <summary>
        /// Connection time for internal purposes
        /// </summary>
        internal long ConnectTime => _connectTime;

        /// <summary>
        /// Peer id can be used as key in your dictionary of peers
        /// </summary>
        public readonly int Id;

        /// <summary>
        /// Id assigned from server
        /// </summary>
        public int RemoteId { get; private set; }

        /// <summary>
        /// Current one-way ping (RTT/2) in milliseconds
        /// </summary>
        public int Ping => _avgRtt / 2;

        /// <summary>
        /// Round trip time in milliseconds
        /// </summary>
        public int RoundTripTime => _avgRtt;

        /// <summary>
        /// Current MTU - Maximum Transfer Unit ( maximum udp packet size without fragmentation )
        /// </summary>
        public int Mtu => _mtu;

        /// <summary>Whether unreliable traffic to this peer uses the compact merged framing.</summary>
        public bool CompactMergeActive => NetManager.CompactMergeEnabled;

        /// <summary>
        /// Delta with remote time in ticks (not accurate)
        /// positive - remote time > our time
        /// </summary>
        public long RemoteTimeDelta => _remoteDelta;

        /// <summary>
        /// Remote UTC time (not accurate)
        /// </summary>
        public DateTime RemoteUtcTime => new DateTime(DateTime.UtcNow.Ticks + _remoteDelta);

        /// <summary>
        /// Time since last packet received (including internal library packets) in milliseconds
        /// </summary>
        public float TimeSinceLastPacket => _timeSinceLastPacket;

        internal double ResendDelay => _resendDelay;

        /// <summary>
        /// Application defined object containing data about the connection
        /// </summary>
        public object Tag;

        /// <summary>
        /// Statistics of peer connection
        /// </summary>
        public readonly NetStatistics Statistics;

        private SocketAddress _cachedSocketAddr;
        private int _cachedHashCode;

        internal byte[] NativeAddress;

        /// <summary>
        /// IPEndPoint serialize
        /// </summary>
        /// <returns>SocketAddress</returns>
        public override SocketAddress Serialize()
        {
            return _cachedSocketAddr;
        }

        public override int GetHashCode()
        {
            //uses SocketAddress hash in NET8 and IPEndPoint hash for NativeSockets and previous NET versions
            return _cachedHashCode;
        }

        //incoming connection constructor
        internal NetPeer(NetManager netManager, IPEndPoint remoteEndPoint, int id) : base(remoteEndPoint.Address, remoteEndPoint.Port)
        {
            Id = id;
            // A peer's counters see a handful of threads, not the whole machine — see NetStatistics.
            Statistics = new NetStatistics(4);
            NetManager = netManager;

            _cachedSocketAddr = base.Serialize();
            if (NetManager.UseNativeSockets)
            {
                NativeAddress = new byte[_cachedSocketAddr.Size];
                for (int i = 0; i < _cachedSocketAddr.Size; i++)
                    NativeAddress[i] = _cachedSocketAddr[i];
            }
#if NET8_0_OR_GREATER
            _cachedHashCode = NetManager.UseNativeSockets ? base.GetHashCode() : _cachedSocketAddr.GetHashCode();
#else
            _cachedHashCode = base.GetHashCode();
#endif

            ResetMtu();

            _connectionState = ConnectionState.Connected;
            _mergeData = new NetPacket(PacketProperty.Merged, NetConstants.MaxPacketSize);
            _pongPacket = new NetPacket(PacketProperty.Pong, 0);
            _pingPacket = new NetPacket(PacketProperty.Ping, 0) { Sequence = 1 };

            _holdedFragments = new Dictionary<ushort, IncomingFragments>();
            _deliveredFragments = new Dictionary<ushort, ushort>();

            _channels = new BaseChannel[netManager.ChannelsCount * NetConstants.ChannelTypeCount];
            _channelSendQueue = new ConcurrentQueue<BaseChannel>();
        }

        internal void InitiateEndPointChange()
        {
            ResetMtu();
            _connectionState = ConnectionState.EndPointChange;
        }

        internal void FinishEndPointChange(IPEndPoint newEndPoint)
        {
            if (_connectionState != ConnectionState.EndPointChange)
                return;
            _connectionState = ConnectionState.Connected;

            Address = newEndPoint.Address;
            Port = newEndPoint.Port;

            _cachedSocketAddr = base.Serialize();
            if (NetManager.UseNativeSockets)
            {
                NativeAddress = new byte[_cachedSocketAddr.Size];
                for (int i = 0; i < _cachedSocketAddr.Size; i++)
                    NativeAddress[i] = _cachedSocketAddr[i];
            }
#if NET8_0_OR_GREATER
            _cachedHashCode = NetManager.UseNativeSockets ? base.GetHashCode() : _cachedSocketAddr.GetHashCode();
#else
            _cachedHashCode = base.GetHashCode();
#endif
        }

        internal void ResetMtu()
        {
            //finish if discovery disabled
            _finishMtu = !NetManager.MtuDiscovery;
            if (NetManager.MtuOverride > 0)
                OverrideMtu(NetManager.MtuOverride);
            else
                SetMtu(0);
        }

        private void SetMtu(int mtuIdx)
        {
            _mtuIdx = mtuIdx;
            _mtu = NetConstants.PossibleMtu[mtuIdx] - NetManager.ExtraPacketSizeForLayer;
        }

        private void OverrideMtu(int mtuValue)
        {
            _mtu = mtuValue;
            _finishMtu = true;
        }

        /// <summary>
        /// Returns packets count in queue for reliable channel
        /// </summary>
        /// <param name="channelNumber">number of channel 0-63</param>
        /// <param name="ordered">type of channel ReliableOrdered or ReliableUnordered</param>
        /// <returns>packets count in channel queue</returns>
        public int GetPacketsCountInReliableQueue(byte channelNumber, bool ordered)
        {
            int idx = channelNumber * NetConstants.ChannelTypeCount +
                       (byte)(ordered ? DeliveryMethod.ReliableOrdered : DeliveryMethod.ReliableUnordered);
            var channel = _channels[idx];
            return channel != null ? ((ReliableChannel)channel).PacketsInQueue : 0;
        }

        /// <summary>
        /// Create temporary packet (maximum size MTU - headerSize) to send later without additional copies
        /// </summary>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <returns>PooledPacket that you can use to write data starting from UserDataOffset</returns>
        public PooledPacket CreatePacketFromPool(DeliveryMethod deliveryMethod, byte channelNumber)
        {
            //multithreaded variable
            int mtu = _mtu;
            var packet = NetManager.PoolGetPacket(mtu);
            if (deliveryMethod == DeliveryMethod.Unreliable)
            {
                packet.Property = PacketProperty.Unreliable;
                packet.RawData[1] = channelNumber;
                return new PooledPacket(packet, mtu, 0);
            }
            else
            {
                packet.Property = PacketProperty.Channeled;
                return new PooledPacket(packet, mtu, (byte)(channelNumber * NetConstants.ChannelTypeCount + (byte)deliveryMethod));
            }
        }

        /// <summary>
        /// Sends pooled packet without data copy
        /// </summary>
        /// <param name="packet">packet to send</param>
        /// <param name="userDataSize">size of user data you want to send</param>
        public void SendPooledPacket(PooledPacket packet, int userDataSize)
        {
            if (_connectionState != ConnectionState.Connected)
                return;
            packet._packet.Size = packet.UserDataOffset + userDataSize;
            if (packet._packet.Property == PacketProperty.Channeled)
            {
                CreateChannel(packet._channelNumber).AddToQueue(packet._packet);
            }
            else
                EnqueueUnreliable(packet._packet);
        }

        /// <summary>
        /// Approximate depth of <see cref="_unreliableChannel"/>. Tracked separately because
        /// ConcurrentQueue.Count walks the segments, which is far too expensive to consult on
        /// every enqueue.
        /// </summary>
        private int _unreliableCount;

        /// <summary>Same, for <see cref="_priorityUnreliableChannel"/>.</summary>
        private int _priorityUnreliableCount;

        /// <summary>
        /// Whether this packet belongs in the priority queue. Reads the channel byte an unreliable
        /// packet always carries at offset 1, so no extra state has to be threaded down the send
        /// path. A null map means the host declared no priority channels — every packet is bulk,
        /// which is stock LiteNetLib behaviour.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsPriorityUnreliable(NetPacket packet)
        {
            bool[] map = NetManager.PriorityUnreliableChannels;
            if (map == null)
                return false;
            byte channel = packet.RawData[1];
            return channel < map.Length && map[channel];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnqueueUnreliable(NetPacket packet)
        {
            if (IsPriorityUnreliable(packet))
            {
                EnqueuePriorityUnreliable(packet);
                return;
            }

            _unreliableChannel.Enqueue(packet);

            // Counted unconditionally so the depth stays honest even while unbounded — the drain
            // decrements either way, and skipping the increment here would drive it negative and
            // silently disable the bound if the limit were ever raised at runtime.
            int depth = Interlocked.Increment(ref _unreliableCount);

            // Resolved from population and box size on the last join/leave, not the raw config
            // field — see NetManager.RecomputePoolCap.
            int limit = NetManager.EffectiveUnreliableQueuePerPeer;
            if (limit <= 0 || depth <= limit) return;

            // Over budget: the producer is outrunning the send loop. Drop from the front until we
            // are back inside it.
            //
            // This queue used to be unbounded, which turned a CPU overload into an out-of-memory:
            // at 2000 players the send loop enqueued faster than the logic pass could drain, and
            // the backlog reached ~40 GB before every peer timed out. Bounding it converts that
            // into what it always should have been — dropped frames on a server that is behind.
            //
            // Oldest-first, because these are position updates: the newest frame supersedes
            // everything queued behind it, so a stale one is exactly what you want to lose.
            while (Volatile.Read(ref _unreliableCount) > limit && _unreliableChannel.TryDequeue(out var stale))
            {
                Interlocked.Decrement(ref _unreliableCount);
                NetManager.NoteUnreliableDropped();
                NetManager.PoolRecycle(stale);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnqueuePriorityUnreliable(NetPacket packet)
        {
            _priorityUnreliableChannel.Enqueue(packet);
            int depth = Interlocked.Increment(ref _priorityUnreliableCount);

            // Bounded on its own budget, resolved from population like the bulk one.
            //
            // ⚠️ This is a fan-in, not a stream. A receiver in a crowd is fed by every audible talker
            // at once, so the depth needed here scales with population exactly as the bulk queue's
            // does — it is simply fed at a lower per-sender rate. Sizing it as "a couple of seconds
            // of one conversation" (a flat 256) measured 32.8% voice delivery at 1000 clients
            // against 93.6% once it scaled, because it was shedding continuously while the bulk
            // queue beside it, twenty times deeper, was barely shedding at all.
            int limit = NetManager.EffectivePriorityUnreliableQueuePerPeer;
            if (limit <= 0 || depth <= limit) return;

            // Oldest-first here too, but for a different reason than the bulk queue: nothing
            // supersedes a voice packet, so this is not "drop the stale one" — it is "the receiver
            // is far enough behind that the head of this queue is already unplayable". Counted
            // separately so it can never be mistaken for ordinary bulk shedding.
            while (Volatile.Read(ref _priorityUnreliableCount) > limit && _priorityUnreliableChannel.TryDequeue(out var stale))
            {
                Interlocked.Decrement(ref _priorityUnreliableCount);
                NetManager.NotePriorityUnreliableDropped();
                NetManager.PoolRecycle(stale);
            }
        }

        private BaseChannel CreateChannel(byte idx)
        {
            BaseChannel newChannel = _channels[idx];
            if (newChannel != null)
                return newChannel;
            switch ((DeliveryMethod)(idx % NetConstants.ChannelTypeCount))
            {
                case DeliveryMethod.ReliableUnordered:
                    newChannel = new ReliableChannel(this, false, idx);
                    break;
                case DeliveryMethod.Sequenced:
                    newChannel = new SequencedChannel(this, false, idx);
                    break;
                case DeliveryMethod.ReliableOrdered:
                    newChannel = new ReliableChannel(this, true, idx);
                    break;
                case DeliveryMethod.ReliableSequenced:
                    newChannel = new SequencedChannel(this, true, idx);
                    break;
            }
            BaseChannel prevChannel = Interlocked.CompareExchange(ref _channels[idx], newChannel, null);
            if (prevChannel != null)
                return prevChannel;

            return newChannel;
        }

        //"Connect to" constructor
        internal NetPeer(NetManager netManager, IPEndPoint remoteEndPoint, int id, byte connectNum, NetDataWriter connectData)
            : this(netManager, remoteEndPoint, id)
        {
            _connectTime = DateTime.UtcNow.Ticks;
            _connectionState = ConnectionState.Outgoing;
            ConnectionNum = connectNum;

            //Make initial packet
            _connectRequestPacket = NetConnectRequestPacket.Make(connectData, remoteEndPoint.Serialize(), _connectTime, id);
            _connectRequestPacket.ConnectionNumber = connectNum;

            //Send request
            NetManager.SendRaw(_connectRequestPacket, this);

            //  //NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {_connectTime}, ConnectNum: {connectNum}");
        }
        //"Connect to" constructor
        internal NetPeer(NetManager netManager, IPEndPoint remoteEndPoint, int id, byte connectNum, ReadOnlySpan<byte> connectData)
            : this(netManager, remoteEndPoint, id)
        {
            _connectTime = DateTime.UtcNow.Ticks;
            _connectionState = ConnectionState.Outgoing;
            ConnectionNum = connectNum;

            //Make initial packet
            _connectRequestPacket = NetConnectRequestPacket.Make(connectData, remoteEndPoint.Serialize(), _connectTime, id);
            _connectRequestPacket.ConnectionNumber = connectNum;

            //Send request
            NetManager.SendRaw(_connectRequestPacket, this);

            // //NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {_connectTime}, ConnectNum: {connectNum}");
        }
        //"Accept" incoming constructor
        internal NetPeer(NetManager netManager, ConnectionRequest request, int id)
            : this(netManager, request.RemoteEndPoint, id)
        {
            _connectTime = request.InternalPacket.ConnectionTime;
            ConnectionNum = request.InternalPacket.ConnectionNumber;
            RemoteId = request.InternalPacket.PeerId;

            //Make initial packet
            _connectAcceptPacket = NetConnectAcceptPacket.Make(_connectTime, ConnectionNum, id);

            //Make Connected
            _connectionState = ConnectionState.Connected;

            //Send
            NetManager.SendRaw(_connectAcceptPacket, this);

            //NetDebug.Write(NetLogLevel.Trace, $"[CC] ConnectId: {_connectTime}");
        }

        //Reject
        internal void Reject(NetConnectRequestPacket requestData, byte[] data, int start, int length)
        {
            _connectTime = requestData.ConnectionTime;
            _connectNum = requestData.ConnectionNumber;
            Shutdown(data, start, length, false);
        }

        internal bool ProcessConnectAccept(NetConnectAcceptPacket packet)
        {
            if (_connectionState != ConnectionState.Outgoing)
                return false;

            //check connection id
            if (packet.ConnectionTime != _connectTime)
            {
                //NetDebug.Write(NetLogLevel.Trace, $"[NC] Invalid connectId: {packet.ConnectionTime} != our({_connectTime})");
                return false;
            }
            //check connect num
            ConnectionNum = packet.ConnectionNumber;
            RemoteId = packet.PeerId;

            //NetDebug.Write(NetLogLevel.Trace, "[NC] Received connection accept");
            Interlocked.Exchange(ref _timeSinceLastPacket, 0);
            _connectionState = ConnectionState.Connected;
            return true;
        }

        /// <summary>
        /// Gets maximum size of packet that will be not fragmented.
        /// </summary>
        /// <param name="options">Type of packet that you want send</param>
        /// <returns>size in bytes</returns>
        public int GetMaxSinglePacketSize(DeliveryMethod options)
        {
            return _mtu - NetPacket.GetHeaderSize(options == DeliveryMethod.Unreliable ? PacketProperty.Unreliable : PacketProperty.Channeled);
        }

        /// <summary>
        /// Send data to peer with delivery event called
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="userData">User data that will be received in DeliveryEvent</param>
        /// <exception cref="ArgumentException">
        ///     If you trying to send unreliable packet type<para/>
        /// </exception>
        public void SendWithDeliveryEvent(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod, object userData)
        {
            if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                throw new ArgumentException("Delivery event will work only for ReliableOrdered/Unordered packets");
            SendInternal(data, 0, data.Length, channelNumber, deliveryMethod, userData);
        }

        /// <summary>
        /// Send data to peer with delivery event called
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="userData">User data that will be received in DeliveryEvent</param>
        /// <exception cref="ArgumentException">
        ///     If you trying to send unreliable packet type<para/>
        /// </exception>
        public void SendWithDeliveryEvent(byte[] data, int start, int length, byte channelNumber, DeliveryMethod deliveryMethod, object userData)
        {
            if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                throw new ArgumentException("Delivery event will work only for ReliableOrdered/Unordered packets");
            SendInternal(data, start, length, channelNumber, deliveryMethod, userData);
        }

        /// <summary>
        /// Send data to peer with delivery event called
        /// </summary>
        /// <param name="dataWriter">Data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="userData">User data that will be received in DeliveryEvent</param>
        /// <exception cref="ArgumentException">
        ///     If you trying to send unreliable packet type<para/>
        /// </exception>
        public void SendWithDeliveryEvent(NetDataWriter dataWriter, byte channelNumber, DeliveryMethod deliveryMethod, object userData)
        {
            if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                throw new ArgumentException("Delivery event will work only for ReliableOrdered/Unordered packets");
            SendInternal(dataWriter.Data, 0, dataWriter.Length, channelNumber, deliveryMethod, userData);
        }

        /// <summary>
        /// Send data to peer (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, DeliveryMethod deliveryMethod)
        {
            SendInternal(data, 0, data.Length, 0, deliveryMethod, null);
        }

        /// <summary>
        /// Send data to peer (channel - 0)
        /// </summary>
        /// <param name="dataWriter">DataWriter with data</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(NetDataWriter dataWriter, DeliveryMethod deliveryMethod)
        {
            SendInternal(dataWriter.Data, 0, dataWriter.Length, 0, deliveryMethod, null);
        }

        /// <summary>
        /// Send data to peer (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, int start, int length, DeliveryMethod options)
        {
            SendInternal(data, start, length, 0, options, null);
        }

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            SendInternal(data, 0, data.Length, channelNumber, deliveryMethod, null);
        }

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="dataWriter">DataWriter with data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(NetDataWriter dataWriter, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            SendInternal(dataWriter.Data, 0, dataWriter.Length, channelNumber, deliveryMethod, null);
        }

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, int start, int length, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            SendInternal(data, start, length, channelNumber, deliveryMethod, null);
        }

        private void SendInternal(
            byte[] data,
            int start,
            int length,
            byte channelNumber,
            DeliveryMethod deliveryMethod,
            object userData)
        {
            if (_connectionState != ConnectionState.Connected || channelNumber >= _channels.Length)
                return;

            //Select channel
            PacketProperty property;
            BaseChannel channel = null;

            if (deliveryMethod == DeliveryMethod.Unreliable)
            {
                property = PacketProperty.Unreliable;
            }
            else
            {
                property = PacketProperty.Channeled;
                channel = CreateChannel((byte)(channelNumber * NetConstants.ChannelTypeCount + (byte)deliveryMethod));
            }

            //Prepare
            //NetDebug.Write("[RS]Packet: " + property);

            //Check fragmentation
            int headerSize = NetPacket.GetHeaderSize(property);
            //Save mtu for multithread
            int mtu = _mtu;
            if (length + headerSize > mtu)
            {
                //if cannot be fragmented
                if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                    throw new TooBigPacketException($"Unreliable or ReliableSequenced packet size exceeded maximum of {mtu - headerSize} bytes, Check allowed size by GetMaxSinglePacketSize() {length + headerSize}");

                int packetFullSize = mtu - headerSize;
                int packetDataSize = packetFullSize - NetConstants.FragmentHeaderSize;
                int totalPackets = length / packetDataSize + (length % packetDataSize == 0 ? 0 : 1);

                //NetDebug.Write($@"FragmentSend:MTU: {mtu}headerSize: {headerSize}packetFullSize: {packetFullSize}packetDataSize: {packetDataSize}totalPackets: {totalPackets}");

                if (totalPackets > ushort.MaxValue)
                    throw new TooBigPacketException("Data was split in " + totalPackets + " fragments, which exceeds " + ushort.MaxValue);

                ushort currentFragmentId = (ushort)Interlocked.Increment(ref _fragmentId);

                for (ushort partIdx = 0; partIdx < totalPackets; partIdx++)
                {
                    int sendLength = length > packetDataSize ? packetDataSize : length;

                    NetPacket p = NetManager.PoolGetPacket(headerSize + sendLength + NetConstants.FragmentHeaderSize);
                    p.Property = property;
                    p.UserData = userData;
                    p.FragmentId = currentFragmentId;
                    p.FragmentPart = partIdx;
                    p.FragmentsTotal = (ushort)totalPackets;
                    p.MarkFragmented();

                    data.AsSpan(start + partIdx * packetDataSize, sendLength).CopyTo(p.RawData.AsSpan(NetConstants.FragmentedHeaderTotalSize));
                    channel.AddToQueue(p);

                    length -= sendLength;
                }
                return;
            }

            //Else just send
            NetPacket packet = NetManager.PoolGetPacket(headerSize + length);
            packet.Property = property;
            data.AsSpan(start, length).CopyTo(packet.RawData.AsSpan(headerSize));
            packet.UserData = userData;

            if (channel == null) //unreliable
            {
                packet.RawData[1] = channelNumber;
                EnqueueUnreliable(packet);
            }
            else
            {
                channel.AddToQueue(packet);
            }
        }
        /// <summary>
        /// Send data to peer with delivery event called
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Delivery method (reliable, unreliable, etc.)</param>
        /// <param name="userData">User data that will be received in DeliveryEvent</param>
        /// <exception cref="ArgumentException">
        ///     If you trying to send unreliable packet type<para/>
        /// </exception>
        public void SendWithDeliveryEvent(ReadOnlySpan<byte> data, byte channelNumber, DeliveryMethod deliveryMethod, object userData)
        {
            if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                throw new ArgumentException("Delivery event will work only for ReliableOrdered/Unordered packets");
            SendInternal(data, channelNumber, deliveryMethod, userData);
        }

        /// <summary>
        /// Send data to peer (channel - 0)
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(ReadOnlySpan<byte> data, DeliveryMethod deliveryMethod)
        {
            SendInternal(data, 0, deliveryMethod, null);
        }

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="channelNumber">Number of channel (from 0 to channelsCount - 1)</param>
        /// <param name="deliveryMethod">Send options (reliable, unreliable, etc.)</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(ReadOnlySpan<byte> data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            SendInternal(data, channelNumber, deliveryMethod, null);
        }

        private void SendInternal(
            ReadOnlySpan<byte> data,
            byte channelNumber,
            DeliveryMethod deliveryMethod,
            object userData)
        {
            if (_connectionState != ConnectionState.Connected || channelNumber >= _channels.Length)
                return;

            //Select channel
            PacketProperty property;
            BaseChannel channel = null;

            if (deliveryMethod == DeliveryMethod.Unreliable)
            {
                property = PacketProperty.Unreliable;
            }
            else
            {
                property = PacketProperty.Channeled;
                channel = CreateChannel((byte)(channelNumber * NetConstants.ChannelTypeCount + (byte)deliveryMethod));
            }

            //Prepare
            //NetDebug.Write("[RS]Packet: " + property);

            //Check fragmentation
            int headerSize = NetPacket.GetHeaderSize(property);
            //Save mtu for multithread
            int mtu = _mtu;
            int length = data.Length;
            if (length + headerSize > mtu)
            {
                //if cannot be fragmented
                if (deliveryMethod != DeliveryMethod.ReliableOrdered && deliveryMethod != DeliveryMethod.ReliableUnordered)
                    throw new TooBigPacketException($"Unreliable or ReliableSequenced packet size exceeded maximum of {mtu - headerSize} bytes, Check allowed size by GetMaxSinglePacketSize() data size was {length + headerSize}");

                int packetFullSize = mtu - headerSize;
                int packetDataSize = packetFullSize - NetConstants.FragmentHeaderSize;
                int totalPackets = length / packetDataSize + (length % packetDataSize == 0 ? 0 : 1);

                if (totalPackets > ushort.MaxValue)
                    throw new TooBigPacketException("Data was split in " + totalPackets + " fragments, which exceeds " + ushort.MaxValue);

                ushort currentFragmentId = (ushort)Interlocked.Increment(ref _fragmentId);

                for (ushort partIdx = 0; partIdx < totalPackets; partIdx++)
                {
                    int sendLength = length > packetDataSize ? packetDataSize : length;

                    NetPacket p = NetManager.PoolGetPacket(headerSize + sendLength + NetConstants.FragmentHeaderSize);
                    p.Property = property;
                    p.UserData = userData;
                    p.FragmentId = currentFragmentId;
                    p.FragmentPart = partIdx;
                    p.FragmentsTotal = (ushort)totalPackets;
                    p.MarkFragmented();

                    data.Slice(partIdx * packetDataSize, sendLength).CopyTo(p.RawData.AsSpan(NetConstants.FragmentedHeaderTotalSize));
                    channel.AddToQueue(p);

                    length -= sendLength;
                }
                return;
            }

            //Else just send
            NetPacket packet = NetManager.PoolGetPacket(headerSize + length);
            packet.Property = property;
            data.CopyTo(packet.RawData.AsSpan(headerSize));
            packet.UserData = userData;

            if (channel == null) //unreliable
            {
                packet.RawData[1] = channelNumber;
                EnqueueUnreliable(packet);
            }
            else
            {
                channel.AddToQueue(packet);
            }
        }
        public void Disconnect(byte[] data)
        {
            NetManager.DisconnectPeer(this, data);
        }

        public void Disconnect(NetDataWriter writer)
        {
            NetManager.DisconnectPeer(this, writer);
        }

        public void Disconnect(byte[] data, int start, int count)
        {
            NetManager.DisconnectPeer(this, data, start, count);
        }

        public void Disconnect()
        {
            NetManager.DisconnectPeer(this);
        }

        internal DisconnectResult ProcessDisconnect(NetPacket packet)
        {
            if ((_connectionState == ConnectionState.Connected || _connectionState == ConnectionState.Outgoing) &&
                packet.Size >= 9 &&
                BitConverter.ToInt64(packet.RawData, 1) == _connectTime &&
                packet.ConnectionNumber == _connectNum)
            {
                return _connectionState == ConnectionState.Connected
                    ? DisconnectResult.Disconnect
                    : DisconnectResult.Reject;
            }
            return DisconnectResult.None;
        }

        internal void AddToReliableChannelSendQueue(BaseChannel channel)
        {
            _channelSendQueue.Enqueue(channel);
        }

        internal ShutdownResult Shutdown(byte[] data, int start, int length, bool force)
        {
            lock (_shutdownLock)
            {
                //trying to shutdown already disconnected
                if (_connectionState == ConnectionState.Disconnected ||
                    _connectionState == ConnectionState.ShutdownRequested)
                {
                    return ShutdownResult.None;
                }

                var result = _connectionState == ConnectionState.Connected
                    ? ShutdownResult.WasConnected
                    : ShutdownResult.Success;

                //don't send anything
                if (force)
                {
                    _connectionState = ConnectionState.Disconnected;
                    return result;
                }

                //reset time for reconnect protection
                Interlocked.Exchange(ref _timeSinceLastPacket, 0);

                //send shutdown packet
                _shutdownPacket = new NetPacket(PacketProperty.Disconnect, length) { ConnectionNumber = _connectNum };
                FastBitConverter.GetBytes(_shutdownPacket.RawData, 1, _connectTime);
                if (_shutdownPacket.Size >= _mtu)
                {
                    //Drop additional data
                    NetDebug.WriteError("[Peer] Disconnect additional data size more than MTU - 8!");
                }
                else if (data != null && length > 0)
                {
                    Buffer.BlockCopy(data, start, _shutdownPacket.RawData, 9, length);
                }
                _connectionState = ConnectionState.ShutdownRequested;
                //NetDebug.Write("[Peer] Send disconnect");
                NetManager.SendRaw(_shutdownPacket, this);
                return result;
            }
        }

        private readonly object _rttLock = new object();

        private void UpdateRoundTripTime(int roundTripTime)
        {
            lock (_rttLock)
            {
                _rtt += roundTripTime;
                _rttCount++;
                _avgRtt = _rtt / _rttCount;
                _resendDelay = 25.0 + _avgRtt * 2.1;
            }
        }
        internal void AddReliablePacket(DeliveryMethod method, NetPacket p)
        {
            if (p.IsFragmented)
            {
                if (p.FragmentsTotal == 0 || p.FragmentsTotal > NetManager.MaxFragmentsCount)
                {
                    NetManager.PoolRecycle(p);
                    NetDebug.WriteError($"Invalid FragmentsTotal: {p.FragmentsTotal}");
                    return;
                }

                if (p.FragmentPart >= p.FragmentsTotal)
                {
                    NetManager.PoolRecycle(p);
                    NetDebug.WriteError($"FragmentPart {p.FragmentPart} >= FragmentsTotal {p.FragmentsTotal}");
                    return;
                }

                NetPacket resultingPacket = null;
                ushort packetFragId = p.FragmentId;
                byte packetChannelId = p.ChannelId;

                ushort totalFragments;
                int totalSize;
                NetPacket[] fragmentsCopy;

                lock (_fragmentsLock)
                {
                    if (!_holdedFragments.TryGetValue(packetFragId, out var incomingFragments))
                    {
                        if (_holdedFragments.Count >= NetConstants.MaxFragmentsInWindow * NetManager.ChannelsCount * NetConstants.FragmentedChannelsCount)
                        {
                            NetManager.PoolRecycle(p);
                            return;
                        }

                        incomingFragments = new IncomingFragments(p.FragmentsTotal, p.ChannelId);
                        _holdedFragments.Add(packetFragId, incomingFragments);
                    }
                    else if (p.FragmentsTotal != incomingFragments.TotalFragments || p.ChannelId != incomingFragments.ChannelId)
                    {
                        NetManager.PoolRecycle(p);
                        NetDebug.WriteError("Fragment metadata mismatch");
                        return;
                    }

                    if (incomingFragments.Contains(p.FragmentPart))
                    {
                        NetManager.PoolRecycle(p);
                        return;
                    }

                    incomingFragments.Set(p.FragmentPart, p);
                    incomingFragments.ReceivedCount++;
                    incomingFragments.TotalSize += p.Size - NetConstants.FragmentedHeaderTotalSize;
                    incomingFragments.LastTouchedMs = _peerClockMs;

                    if (incomingFragments.ReceivedCount != incomingFragments.TotalFragments)
                        return;

                    // All fragments received - copy references and remove from dictionary while under lock
                    totalFragments = incomingFragments.TotalFragments;
                    totalSize = incomingFragments.TotalSize;
                    fragmentsCopy = new NetPacket[totalFragments];
                    for (ushort i = 0; i < totalFragments; i++)
                    {
                        if (!incomingFragments.TryGet(i, out fragmentsCopy[i]))
                        {
                            // Should not happen since ReceivedCount == TotalFragments, but handle gracefully
                            _holdedFragments.Remove(packetFragId);
                            incomingFragments.RecycleAll(NetManager);
                            NetDebug.WriteError($"Fragment {i} missing during reassembly");
                            return;
                        }
                    }
                    _holdedFragments.Remove(packetFragId);
                }

                // Outside lock: do the actual data copy
                resultingPacket = NetManager.PoolGetPacket(totalSize);
                int pos = 0;
                for (ushort i = 0; i < totalFragments; i++)
                {
                    var fragment = fragmentsCopy[i];
                    int writtenSize = fragment.Size - NetConstants.FragmentedHeaderTotalSize;

                    if (pos + writtenSize > resultingPacket.RawData.Length)
                    {
                        for (ushort j = i; j < totalFragments; j++)
                            NetManager.PoolRecycle(fragmentsCopy[j]);
                        NetManager.PoolRecycle(resultingPacket);
                        NetDebug.WriteError($"Fragment error pos: {pos + writtenSize} >= resultPacketSize: {resultingPacket.RawData.Length}");
                        return;
                    }

                    if (fragment.Size > fragment.RawData.Length)
                    {
                        for (ushort j = i; j < totalFragments; j++)
                            NetManager.PoolRecycle(fragmentsCopy[j]);
                        NetManager.PoolRecycle(resultingPacket);
                        NetDebug.WriteError($"Fragment error size: {fragment.Size} > fragment.RawData.Length: {fragment.RawData.Length}");
                        return;
                    }

                    Buffer.BlockCopy(
                        fragment.RawData,
                        NetConstants.FragmentedHeaderTotalSize,
                        resultingPacket.RawData,
                        pos,
                        writtenSize);
                    pos += writtenSize;

                    NetManager.PoolRecycle(fragment);
                }

                // Outside lock: process reconstructed packet
                NetManager.CreateReceiveEvent(
                    resultingPacket,
                    method,
                    (byte)(packetChannelId / NetConstants.ChannelTypeCount),
                    0,
                    this);
            }
            else // Just simple packet
            {
                NetManager.CreateReceiveEvent(
                    p,
                    method,
                    (byte)(p.ChannelId / NetConstants.ChannelTypeCount),
                    NetConstants.ChanneledHeaderSize,
                    this);
            }
        }

        private void SweepStaleFragments()
        {
            lock (_fragmentsLock)
            {
                if (_holdedFragments.Count == 0) return;
                List<ushort> stale = null;
                foreach (var kv in _holdedFragments)
                {
                    if (_peerClockMs - kv.Value.LastTouchedMs > FragmentStaleMs)
                    {
                        if (stale == null)
                            stale = new List<ushort>();
                        stale.Add(kv.Key);
                    }
                }
                if (stale == null) return;
                foreach (ushort fragId in stale)
                {
                    _holdedFragments[fragId].RecycleAll(NetManager);
                    _holdedFragments.Remove(fragId);
                }
            }
        }

        /// <summary>
        /// Returns everything this peer still has queued to the packet pool. Called once when the
        /// peer is removed from the manager; without it every disconnect dropped up to a full
        /// unreliable queue plus any partial reassembly sets on the GC floor, and the shared pool
        /// replaced them with fresh allocations. Reliable-channel internals are deliberately left
        /// to the GC: their packets can still be referenced by an in-flight ack pass.
        /// </summary>
        internal void RecycleQueuedPackets()
        {
            while (_unreliableChannel.TryDequeue(out NetPacket queued))
            {
                Interlocked.Decrement(ref _unreliableCount);
                NetManager.PoolRecycle(queued);
            }
            while (_priorityUnreliableChannel.TryDequeue(out NetPacket priorityQueued))
            {
                Interlocked.Decrement(ref _priorityUnreliableCount);
                NetManager.PoolRecycle(priorityQueued);
            }
            lock (_fragmentsLock)
            {
                foreach (var frag in _holdedFragments.Values)
                    frag.RecycleAll(NetManager);
                _holdedFragments.Clear();
            }
        }

        private void ProcessMtuPacket(NetPacket packet)
        {
            //header + int
            if (packet.Size < NetConstants.PossibleMtu[0])
            {
                NetManager.PoolRecycle(packet);
                return;
            }

            //first stage check (mtu check and mtu ok)
            int receivedMtu = BitConverter.ToInt32(packet.RawData, 1);
            int endMtuCheck = BitConverter.ToInt32(packet.RawData, packet.Size - 4);
            if (receivedMtu != packet.Size || receivedMtu != endMtuCheck || receivedMtu > NetConstants.MaxPacketSize)
            {
                NetDebug.WriteError($"[MTU] Broken packet. RMTU {receivedMtu}, EMTU {endMtuCheck}, PSIZE {packet.Size}");
                NetManager.PoolRecycle(packet);
                return;
            }

            if (packet.Property == PacketProperty.MtuCheck)
            {
                _mtuCheckAttempts = 0;
                //NetDebug.Write("[MTU] check. send back: " + receivedMtu);
                packet.Property = PacketProperty.MtuOk;
                NetManager.SendRawAndRecycle(packet, this);
                return;
            }

            //MtuOk
            if (receivedMtu > _mtu && !_finishMtu)
            {
                //invalid packet
                if (receivedMtu == NetConstants.PossibleMtu[_mtuIdx + 1] - NetManager.ExtraPacketSizeForLayer)
                {
                    lock (_mtuMutex)
                    {
                        SetMtu(_mtuIdx + 1);
                    }
                    //if maxed - finish.
                    if (_mtuIdx == NetConstants.PossibleMtu.Length - 1)
                        _finishMtu = true;
                    //NetDebug.Write("[MTU] ok. Increase to: " + _mtu);
                }
            }
            NetManager.PoolRecycle(packet);
        }

        private void UpdateMtuLogic(float deltaTime)
        {
            if (_finishMtu)
                return;

            _mtuCheckTimer += deltaTime;
            if (_mtuCheckTimer < MtuCheckDelay)
                return;

            _mtuCheckTimer = 0;
            _mtuCheckAttempts++;
            if (_mtuCheckAttempts >= MaxMtuCheckAttempts)
            {
                _finishMtu = true;
                return;
            }

            lock (_mtuMutex)
            {
                if (_mtuIdx >= NetConstants.PossibleMtu.Length - 1)
                    return;

                //Send increased packet
                int newMtu = NetConstants.PossibleMtu[_mtuIdx + 1] - NetManager.ExtraPacketSizeForLayer;
                var p = NetManager.PoolGetPacket(newMtu);
                p.Property = PacketProperty.MtuCheck;
                FastBitConverter.GetBytes(p.RawData, 1, newMtu);         //place into start
                FastBitConverter.GetBytes(p.RawData, p.Size - 4, newMtu);//and end of packet

                //Must check result for MTU fix
                if (NetManager.SendRawAndRecycle(p, this) <= 0)
                    _finishMtu = true;
            }
        }

        internal ConnectRequestResult ProcessConnectRequest(NetConnectRequestPacket connRequest)
        {
            //current or new request
            switch (_connectionState)
            {
                //P2P case
                case ConnectionState.Outgoing:
                    //fast check
                    if (connRequest.ConnectionTime < _connectTime)
                    {
                        return ConnectRequestResult.P2PLose;
                    }
                    //slow rare case check
                    if (connRequest.ConnectionTime == _connectTime)
                    {
                        var localBytes = connRequest.TargetAddress;
                        for (int i = _cachedSocketAddr.Size - 1; i >= 0; i--)
                        {
                            byte rb = _cachedSocketAddr[i];
                            if (rb == localBytes[i])
                                continue;
                            if (rb < localBytes[i])
                                return ConnectRequestResult.P2PLose;
                        }
                    }
                    break;

                case ConnectionState.Connected:
                    //Old connect request
                    if (connRequest.ConnectionTime == _connectTime)
                    {
                        //just reply accept
                        NetManager.SendRaw(_connectAcceptPacket, this);
                    }
                    //New connect request
                    else if (connRequest.ConnectionTime > _connectTime)
                    {
                        return ConnectRequestResult.Reconnection;
                    }
                    break;

                case ConnectionState.Disconnected:
                case ConnectionState.ShutdownRequested:
                    if (connRequest.ConnectionTime >= _connectTime)
                        return ConnectRequestResult.NewConnection;
                    break;
            }
            return ConnectRequestResult.None;
        }

        //Process incoming packet
        internal void ProcessPacket(NetPacket packet)
        {
            //not initialized
            if (_connectionState == ConnectionState.Outgoing || _connectionState == ConnectionState.Disconnected)
            {
                NetManager.PoolRecycle(packet);
                return;
            }
            if (packet.Property == PacketProperty.ShutdownOk)
            {
                if (_connectionState == ConnectionState.ShutdownRequested)
                    _connectionState = ConnectionState.Disconnected;
                NetManager.PoolRecycle(packet);
                return;
            }
            if (packet.ConnectionNumber != _connectNum)
            {
                //NetDebug.Write(NetLogLevel.Trace, "[RR]Old packet");
                NetManager.PoolRecycle(packet);
                return;
            }
            Interlocked.Exchange(ref _timeSinceLastPacket, 0);

            //NetDebug.Write($"[RR]PacketProperty: {packet.Property}");
            switch (packet.Property)
            {
                case PacketProperty.Merged:
                    int pos = NetConstants.HeaderSize;
                    while (pos < packet.Size)
                    {
                        if (pos + 2 > packet.Size)
                            break;
                        ushort size = BitConverter.ToUInt16(packet.RawData, pos);
                        if (size == 0)
                            break;

                        pos += 2;
                        if (packet.Size - pos < size)
                            break;

                        NetPacket mergedPacket = NetManager.PoolGetPacket(size);
                        Buffer.BlockCopy(packet.RawData, pos, mergedPacket.RawData, 0, size);
                        mergedPacket.Size = size;

                        if (!mergedPacket.Verify())
                        {
                            NetManager.PoolRecycle(mergedPacket);
                            break;
                        }

                        pos += size;
                        ProcessPacket(mergedPacket);
                    }
                    NetManager.PoolRecycle(packet);
                    break;

                case PacketProperty.CompactMerged:
                    try
                    {
                        int compactPos = NetConstants.HeaderSize;
                        while (compactPos < packet.Size)
                        {
                            if (!CompactMerge.TryReadEntry(
                                    packet.RawData,
                                    packet.Size,
                                    ref compactPos,
                                    out bool compactRawPacket,
                                    out byte compactChannel,
                                    out int payloadLength))
                            {
                                break;
                            }

                            int payloadOffset = compactPos;
                            compactPos += payloadLength;

                            if (compactRawPacket)
                            {
                                NetPacket innerPacket = NetManager.PoolGetPacket(payloadLength);
                                Buffer.BlockCopy(packet.RawData, payloadOffset, innerPacket.RawData, 0, payloadLength);
                                innerPacket.Size = payloadLength;
                                if (!innerPacket.Verify())
                                {
                                    NetManager.PoolRecycle(innerPacket);
                                    break;
                                }

                                ProcessPacket(innerPacket);
                                continue;
                            }

                            NetPacket unreliable = NetManager.PoolGetPacket(NetConstants.UnreliableHeaderSize + payloadLength);
                            unreliable.RawData[0] = (byte)PacketProperty.Unreliable;
                            unreliable.ConnectionNumber = _connectNum;
                            unreliable.RawData[1] = compactChannel;
                            Buffer.BlockCopy(
                                packet.RawData,
                                payloadOffset,
                                unreliable.RawData,
                                NetConstants.UnreliableHeaderSize,
                                payloadLength);
                            unreliable.Size = NetConstants.UnreliableHeaderSize + payloadLength;

                            NetManager.CreateReceiveEvent(
                                unreliable,
                                DeliveryMethod.Unreliable,
                                compactChannel,
                                NetConstants.UnreliableHeaderSize,
                                this);
                        }
                    }
                    finally
                    {
                        NetManager.PoolRecycle(packet);
                    }
                    break;

                //If we get ping, send pong
                case PacketProperty.Ping:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _pongPacket.Sequence) > 0)
                    {
                        //NetDebug.Write("[PP]Ping receive, send pong");
                        FastBitConverter.GetBytes(_pongPacket.RawData, 3, DateTime.UtcNow.Ticks);
                        _pongPacket.Sequence = packet.Sequence;
                        NetManager.SendRaw(_pongPacket, this);
                    }
                    NetManager.PoolRecycle(packet);
                    break;

                //If we get pong, calculate ping time and rtt
                case PacketProperty.Pong:
                    if (packet.Sequence == _pingPacket.Sequence)
                    {
                        _pingTimer.Stop();
                        int elapsedMs = (int)_pingTimer.ElapsedMilliseconds;
                        _remoteDelta = BitConverter.ToInt64(packet.RawData, 3) + (elapsedMs * TimeSpan.TicksPerMillisecond) / 2 - DateTime.UtcNow.Ticks;
                        UpdateRoundTripTime(elapsedMs);
                        NetManager.ConnectionLatencyUpdated(this, elapsedMs / 2);
                        //NetDebug.Write($"[PP]Ping: {packet.Sequence} - {elapsedMs} - {_remoteDelta}");
                    }
                    NetManager.PoolRecycle(packet);
                    break;

                case PacketProperty.Ack:
                case PacketProperty.Channeled:
                    if (packet.ChannelId >= _channels.Length)
                    {
                        NetManager.PoolRecycle(packet);
                        break;
                    }
                    var channel = _channels[packet.ChannelId] ?? (packet.Property == PacketProperty.Ack ? null : CreateChannel(packet.ChannelId));
                    if (channel != null)
                    {
                        if (!channel.ProcessPacket(packet))
                            NetManager.PoolRecycle(packet);
                    }
                    else
                    {
                        NetManager.PoolRecycle(packet);
                    }
                    break;

                //Simple packet without acks
                case PacketProperty.Unreliable:
                    NetManager.CreateReceiveEvent(packet, DeliveryMethod.Unreliable, packet.RawData[1], NetConstants.UnreliableHeaderSize, this);
                    return;

                case PacketProperty.MtuCheck:
                case PacketProperty.MtuOk:
                    ProcessMtuPacket(packet);
                    break;

                default:
                    NetDebug.WriteError("Error! Unexpected packet type: " + packet.Property);
                    NetManager.PoolRecycle(packet);
                    break;
            }
        }

        private void SendMerged()
        {
            if (_mergeCount == 0)
                return;

            int bytesSent;

            // Batchable: this is bulk payload traffic and nothing reads the result beyond stats.
            // Outside a batching window this is exactly the old direct send.
            if (_mergeCount > 1)
            {
                bytesSent = NetManager.SendRawBatchable(
                    _mergeData.RawData,
                    0,
                    NetConstants.HeaderSize + _mergePos,
                    this);
            }
            else if (_mergeCompact)
            {
                int entryPos = NetConstants.HeaderSize;
                if (!CompactMerge.TryReadEntry(
                        _mergeData.RawData,
                        NetConstants.HeaderSize + _mergePos,
                        ref entryPos,
                        out bool isRawPacket,
                        out byte channel,
                        out int payloadLength))
                {
                    byte firstEntryByte0 = _mergePos > 0
                        ? _mergeData.RawData[NetConstants.HeaderSize]
                        : (byte)0;
                    byte firstEntryByte1 = _mergePos > 1
                        ? _mergeData.RawData[NetConstants.HeaderSize + 1]
                        : (byte)0;
                    NetDebug.WriteError(
                        $"[CompactMerged] Internal single-entry decode failure: count={_mergeCount}, " +
                        $"pos={_mergePos}, entry0=0x{firstEntryByte0:X2}, entry1=0x{firstEntryByte1:X2}. " +
                        "Dropping merge buffer.");
                    _mergePos = 0;
                    _mergeCount = 0;
                    _mergeHeldMs = 0f;
                    return;
                }

                if (isRawPacket)
                {
                    bytesSent = NetManager.SendRawBatchable(
                        _mergeData.RawData,
                        entryPos,
                        payloadLength,
                        this);
                }
                else
                {
                    // Rewrite the two bytes immediately before the payload as a normal Unreliable
                    // header/channel. This avoids moving the payload for either header length.
                    int sendOffset = entryPos - NetConstants.UnreliableHeaderSize;
                    _mergeData.RawData[sendOffset] =
                        (byte)((byte)PacketProperty.Unreliable | (_connectNum << 5));
                    _mergeData.RawData[sendOffset + 1] = channel;
                    bytesSent = NetManager.SendRawBatchable(
                        _mergeData.RawData,
                        sendOffset,
                        NetConstants.UnreliableHeaderSize + payloadLength,
                        this);
                }

            }
            else
            {
                // Legacy Merged stores the complete inner packet after a 16-bit length.
                bytesSent = NetManager.SendRawBatchable(
                    _mergeData.RawData,
                    NetConstants.HeaderSize + 2,
                    _mergePos - 2,
                    this);
            }


            // The manager already counts a batched datagram when it flushes; counting here too
            // would double it.
            if (NetManager.EnableStatistics && !NetManager.IsBatchingOnThisThread)
            {
                Statistics.IncrementPacketsSent();
                Statistics.AddBytesSent(bytesSent);
            }

            _mergePos = 0;
            _mergeCount = 0;
            _mergeHeldMs = 0f;
        }

        /// <summary>
        /// Builds an unreliable NetPacket from raw user bytes and enqueues it, skipping the
        /// intermediate NetDataWriter copy. Thread-safe: uses the same _unreliableChannel
        /// queue as Send(), so Update() merges it on the logic thread.
        /// Optionally patches a single byte at patchOffset (relative to user data start)
        /// after the copy — used for per-receiver fields like interval that differ per
        /// destination while the source array is shared.
        /// Pass patchOffset = -1 to skip patching.
        /// </summary>
        public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0)
        {
            if (_connectionState != ConnectionState.Connected || channelNumber >= _channels.Length)
                return;

            int headerSize = NetConstants.UnreliableHeaderSize;
            int packetSize = headerSize + length;

            NetPacket packet = NetManager.PoolGetPacket(packetSize);
            packet.Property = PacketProperty.Unreliable;
            packet.RawData[1] = channelNumber;
            Buffer.BlockCopy(data, offset, packet.RawData, headerSize, length);

            if (patchOffset >= 0 && patchOffset < length)
                packet.RawData[headerSize + patchOffset] = patchValue;

            EnqueueUnreliable(packet);
        }

        internal void SendUserData(NetPacket packet)
        {
            packet.ConnectionNumber = _connectNum;
            const int sizeThreshold = 20;

            bool isUnreliable = packet.Property == PacketProperty.Unreliable;
            bool isRawTransport = packet.Property == PacketProperty.Ack || packet.Property == PacketProperty.Channeled;
            int payloadSize = isUnreliable ? packet.Size - NetConstants.UnreliableHeaderSize : 0;


            // With compact framing enabled, an out-of-range unreliable channel cannot use the
            // six-bit channel field. Unreliable traffic is unordered, so bypass the accumulator
            // without flushing an otherwise useful pending CompactMerged datagram.
            if (isUnreliable && NetManager.CompactMergeEnabled && !CompactMerge.CanCarryChannel(packet.RawData[1]))
            {
                int bytesSent = NetManager.SendRaw(packet, this);
                if (NetManager.EnableStatistics)
                {
                    Statistics.IncrementPacketsSent();
                    Statistics.AddBytesSent(bytesSent);
                }
                return;
            }

            bool compact = NetManager.CompactMergeEnabled &&
                ((isUnreliable && !packet.IsFragmented) || isRawTransport);
            int compactPayloadSize = isRawTransport ? packet.Size : payloadSize;
            int entrySize = compact ? CompactMerge.EntrySize(compactPayloadSize) : packet.Size + 2;
            int mergedPacketSize = NetConstants.HeaderSize + entrySize;

            if (mergedPacketSize + sizeThreshold >= _mtu)
            {
                // Channeled transport has ordering semantics. If an earlier entry is held in the
                // accumulator, send it before a later Channeled packet that must bypass merging.
                // Unreliable traffic is unordered and intentionally does not evict the accumulator.
                if (!isUnreliable && _mergeCount > 0)
                    SendMerged();

                int bytesSent = NetManager.SendRaw(packet, this);
                if (NetManager.EnableStatistics)
                {
                    Statistics.IncrementPacketsSent();
                    Statistics.AddBytesSent(bytesSent);
                }
                return;
            }

            if (_mergeCount > 0 && compact != _mergeCompact)
                SendMerged();

            if (NetConstants.HeaderSize + _mergePos + entrySize > _mtu)
                SendMerged();

            if (_mergeCount == 0)
            {
                _mergeCompact = compact;
                _mergeData.Property = compact ? PacketProperty.CompactMerged : PacketProperty.Merged;
            }

            if (compact)
            {
                int entryOffset = NetConstants.HeaderSize + _mergePos;
                if (isRawTransport)
                {
                    _mergePos += CompactMerge.WriteRawEntry(
                        _mergeData.RawData,
                        entryOffset,
                        packet.RawData,
                        0,
                        packet.Size);
                }
                else
                {
                    _mergePos += CompactMerge.WriteUnreliableEntry(
                        _mergeData.RawData,
                        entryOffset,
                        packet.RawData[1],
                        packet.RawData,
                        NetConstants.UnreliableHeaderSize,
                        payloadSize);
                }
            }
            else
            {
                FastBitConverter.GetBytes(
                    _mergeData.RawData,
                    _mergePos + NetConstants.HeaderSize,
                    (ushort)packet.Size);
                Buffer.BlockCopy(
                    packet.RawData,
                    0,
                    _mergeData.RawData,
                    _mergePos + NetConstants.HeaderSize + 2,
                    packet.Size);
                _mergePos += packet.Size + 2;
            }

            _mergeCount++;
        }

        internal void Update(float deltaTime)
        {
            //atomic increment: CAS loop to avoid racing with Interlocked.Exchange on receive thread
            float original, updated;
            do
            {
                original = _timeSinceLastPacket;
                updated = original + deltaTime;
            } while (Interlocked.CompareExchange(ref _timeSinceLastPacket, updated, original) != original);

            _peerClockMs += deltaTime;
            if (_peerClockMs >= _nextFragmentSweepMs)
            {
                _nextFragmentSweepMs = _peerClockMs + FragmentSweepIntervalMs;
                SweepStaleFragments();
            }
            switch (_connectionState)
            {
                case ConnectionState.Connected:
                    if (_timeSinceLastPacket > NetManager.DisconnectTimeout)
                    {
                        //NetDebug.Write($"[UPDATE] Disconnect by timeout: {_timeSinceLastPacket} > {NetManager.DisconnectTimeout}");
                        NetManager.DisconnectPeerForce(this, DisconnectReason.Timeout, 0, null);
                        return;
                    }
                    break;

                case ConnectionState.ShutdownRequested:
                    if (_timeSinceLastPacket > NetManager.DisconnectTimeout)
                    {
                        _connectionState = ConnectionState.Disconnected;
                    }
                    else
                    {
                        _shutdownTimer += deltaTime;
                        if (_shutdownTimer >= ShutdownDelay)
                        {
                            _shutdownTimer = 0;
                            NetManager.SendRaw(_shutdownPacket, this);
                        }
                    }
                    return;

                case ConnectionState.Outgoing:
                    _connectTimer += deltaTime;
                    if (_connectTimer > NetManager.ReconnectDelay)
                    {
                        _connectTimer = 0;
                        _connectAttempts++;
                        if (_connectAttempts > NetManager.MaxConnectAttempts)
                        {
                            NetManager.DisconnectPeerForce(this, DisconnectReason.ConnectionFailed, 0, null);
                            return;
                        }

                        //else send connect again
                        NetManager.SendRaw(_connectRequestPacket, this);
                    }
                    return;

                case ConnectionState.Disconnected:
                    return;
            }

            //Send ping
            _pingSendTimer += deltaTime;
            if (_pingSendTimer >= NetManager.PingInterval)
            {
                //NetDebug.Write("[PP] Send ping...");
                //reset timer
                _pingSendTimer = 0;
                //send ping
                _pingPacket.Sequence++;
                //ping timeout
                if (_pingTimer.IsRunning)
                    UpdateRoundTripTime((int)_pingTimer.ElapsedMilliseconds);
                _pingTimer.Restart();
                NetManager.SendRaw(_pingPacket, this);
            }

            //RTT - round trip time
            _rttResetTimer += deltaTime;
            if (_rttResetTimer >= NetManager.PingInterval * 3)
            {
                _rttResetTimer = 0;
                _rtt = _avgRtt;
                _rttCount = 1;
            }

            UpdateMtuLogic(deltaTime);

            //Pending send
            int count = _channelSendQueue.Count;
            while (count-- > 0)
            {
                if (!_channelSendQueue.TryDequeue(out var channel))
                    break;
                if (channel.SendAndCheckQueue())
                {
                    // still has something to send, re-add it to the send queue
                    _channelSendQueue.Enqueue(channel);
                }
            }

            // Priority first, so latency-critical traffic lands in the earliest datagrams this pass
            // emits instead of behind however much bulk state the producer queued since the last
            // one. At a full bulk queue that ordering is the difference between voice arriving
            // inside the receiver's jitter window and arriving after it has already given up.
            while (_priorityUnreliableChannel.TryDequeue(out var priorityPacket))
            {
                Interlocked.Decrement(ref _priorityUnreliableCount);
                SendUserData(priorityPacket);
                NetManager.PoolRecycle(priorityPacket);
            }

            while (_unreliableChannel.TryDequeue(out var packet))
            {
                Interlocked.Decrement(ref _unreliableCount);
                SendUserData(packet);
                NetManager.PoolRecycle(packet);
            }

            // Hold a partly-filled buffer briefly so consecutive passes coalesce into one
            // datagram instead of each pass emitting its own half-empty one. SendUserData already
            // flushes the moment a packet would overflow the MTU, so a full buffer never waits
            // here — only small sends do, and only up to MergeHoldMs.
            float hold = NetManager.MergeHoldMs;
            if (hold <= 0f)
            {
                SendMerged();
            }
            else if (_mergeCount > 0)
            {
                _mergeHeldMs += deltaTime;
                if (_mergeHeldMs >= hold)
                {
                    SendMerged();
                }
            }
        }

        //For reliable channel
        internal void RecycleAndDeliver(NetPacket packet)
        {
            if (packet.UserData != null)
            {
                bool shouldDeliver = false;
                object userData = packet.UserData;

                if (packet.IsFragmented)
                {
                    lock (_fragmentsLock)
                    {
                        _deliveredFragments.TryGetValue(packet.FragmentId, out ushort fragCount);
                        fragCount++;
                        if (fragCount == packet.FragmentsTotal)
                        {
                            shouldDeliver = true;
                            _deliveredFragments.Remove(packet.FragmentId);
                        }
                        else
                        {
                            _deliveredFragments[packet.FragmentId] = fragCount;
                        }
                    }
                }
                else
                {
                    shouldDeliver = true;
                }

                if (shouldDeliver)
                {
                    NetManager.MessageDelivered(this, userData);
                }

                packet.UserData = null;
            }

            NetManager.PoolRecycle(packet);
        }

        /// <summary>
        /// Returns packets count in queue for reliable channel
        /// </summary>
        /// <param name="channelNumber">number of channel 0-63</param>
        /// <param name="deliveryMethod">type of channel ReliableOrdered or ReliableUnordered</param>
        /// <returns>packets count in channel queue</returns>
        public int GetPacketsCountInQueue(byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (deliveryMethod == DeliveryMethod.Unreliable)
                return 0;
            int idx = (byte)(channelNumber * NetConstants.ChannelTypeCount + (byte)deliveryMethod);
            if (idx >= _channels.Length)
                return 0;
            var channel = _channels[idx];
            return channel != null ? channel.PacketsInQueue : 0;
        }
    }
}
