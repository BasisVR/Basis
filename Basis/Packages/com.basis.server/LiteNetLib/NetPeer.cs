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

        //Merging
        private readonly NetPacket _mergeData;
        private int _mergePos;
        private int _mergeCount;

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
            Statistics = new NetStatistics();
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnqueueUnreliable(NetPacket packet)
        {
            _unreliableChannel.Enqueue(packet);
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

        private void ProcessMtuPacket(NetPacket packet)
        {
            //header + int
            if (packet.Size < NetConstants.PossibleMtu[0])
                return;

            //first stage check (mtu check and mtu ok)
            int receivedMtu = BitConverter.ToInt32(packet.RawData, 1);
            int endMtuCheck = BitConverter.ToInt32(packet.RawData, packet.Size - 4);
            if (receivedMtu != packet.Size || receivedMtu != endMtuCheck || receivedMtu > NetConstants.MaxPacketSize)
            {
                NetDebug.WriteError($"[MTU] Broken packet. RMTU {receivedMtu}, EMTU {endMtuCheck}, PSIZE {packet.Size}");
                return;
            }

            if (packet.Property == PacketProperty.MtuCheck)
            {
                _mtuCheckAttempts = 0;
                //NetDebug.Write("[MTU] check. send back: " + receivedMtu);
                packet.Property = PacketProperty.MtuOk;
                NetManager.SendRawAndRecycle(packet, this);
            }
            else if (receivedMtu > _mtu && !_finishMtu) //MtuOk
            {
                //invalid packet
                if (receivedMtu != NetConstants.PossibleMtu[_mtuIdx + 1] - NetManager.ExtraPacketSizeForLayer)
                    return;

                lock (_mtuMutex)
                {
                    SetMtu(_mtuIdx + 1);
                }
                //if maxed - finish.
                if (_mtuIdx == NetConstants.PossibleMtu.Length - 1)
                    _finishMtu = true;
                NetManager.PoolRecycle(packet);
                //NetDebug.Write("[MTU] ok. Increase to: " + _mtu);
            }
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
                            break;

                        pos += size;
                        ProcessPacket(mergedPacket);
                    }
                    NetManager.PoolRecycle(packet);
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
                    break;
            }
        }

        private void SendMerged()
        {
            if (_mergeCount == 0)
                return;
            int bytesSent;
            if (_mergeCount > 1)
            {
                //NetDebug.Write("[P]Send merged: " + _mergePos + ", count: " + _mergeCount);
                bytesSent = NetManager.SendRaw(_mergeData.RawData, 0, NetConstants.HeaderSize + _mergePos, this);
            }
            else
            {
                //Send without length information and merging
                bytesSent = NetManager.SendRaw(_mergeData.RawData, NetConstants.HeaderSize + 2, _mergePos - 2, this);
            }

            if (NetManager.EnableStatistics)
            {
                Statistics.IncrementPacketsSent();
                Statistics.AddBytesSent(bytesSent);
            }

            _mergePos = 0;
            _mergeCount = 0;
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
            int mergedPacketSize = NetConstants.HeaderSize + packet.Size + 2;
            const int sizeTreshold = 20;
            if (mergedPacketSize + sizeTreshold >= _mtu)
            {
                //NetDebug.Write(NetLogLevel.Trace, "[P]SendingPacket: " + packet.Property);
                int bytesSent = NetManager.SendRaw(packet, this);

                if (NetManager.EnableStatistics)
                {
                    Statistics.IncrementPacketsSent();
                    Statistics.AddBytesSent(bytesSent);
                }

                return;
            }
            if (_mergePos + mergedPacketSize > _mtu)
                SendMerged();

            FastBitConverter.GetBytes(_mergeData.RawData, _mergePos + NetConstants.HeaderSize, (ushort)packet.Size);
            Buffer.BlockCopy(packet.RawData, 0, _mergeData.RawData, _mergePos + NetConstants.HeaderSize + 2, packet.Size);
            _mergePos += packet.Size + 2;
            _mergeCount++;
            //DebugWriteForce("Merged: " + _mergePos + "/" + (_mtu - 2) + ", count: " + _mergeCount);
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

            while (_unreliableChannel.TryDequeue(out var packet))
            {
                SendUserData(packet);
                NetManager.PoolRecycle(packet);
            }

            SendMerged();
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
