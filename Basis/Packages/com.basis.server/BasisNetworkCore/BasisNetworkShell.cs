using System;
using System.Net;
using System.Net.Sockets;

namespace Basis.Network.Core
{
    public enum DisconnectReason
    {
        ConnectionFailed,
        Timeout,
        HostUnreachable,
        NetworkUnreachable,
        RemoteConnectionClose,
        DisconnectPeerCalled,
        ConnectionRejected,
        InvalidProtocol,
        UnknownHost,
        Reconnect,
        PeerToPeerConnection,
        PeerNotFound
    }

    public partial struct DisconnectInfo
    {
        public DisconnectReason Reason;
        public System.Net.Sockets.SocketError SocketErrorCode;
        public NetPacketReader AdditionalData;
    }


    public partial class EventBasedNetListener
    {
        public delegate void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo);
        public delegate void OnNetworkError(IPEndPoint endPoint, SocketError socketError);
        public delegate void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod);
        public delegate void OnConnectionRequest(ConnectionRequest request);
        public delegate void OnPeerConnected(NetPeer peer);
        public delegate void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader);

        public event OnConnectionRequest ConnectionRequestEvent;
        public event OnPeerDisconnected PeerDisconnectedEvent;
        public event OnNetworkReceive NetworkReceiveEvent;
        public event OnNetworkError NetworkErrorEvent;
        public event OnPeerConnected PeerConnectedEvent;
        public event OnNetworkReceiveUnconnected NetworkReceiveUnconnectedEvent;

        public void RaiseConnectionRequest(ConnectionRequest request)
        {
            ConnectionRequestEvent?.Invoke(request);
        }

        public void RaisePeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            PeerDisconnectedEvent?.Invoke(peer, disconnectInfo);
        }

        public void RaiseNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            reader.channel = channel;
            reader.method = deliveryMethod;
#endif
            NetworkReceiveEvent?.Invoke(peer, reader, channel, deliveryMethod);
        }

        public void RaiseNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            NetworkErrorEvent?.Invoke(endPoint, socketError);
        }

        public void RaisePeerConnected(NetPeer peer)
        {
            PeerConnectedEvent?.Invoke(peer);
        }
    }

    public interface ConnectionRequest
    {
        public void Reject(NetDataWriter w);
        public NetPeer Accept();
        public NetDataReader Data { get; }
        public IPEndPoint RemoteEndPoint { get; }
    }

    public interface NetPeer
    {
        public void Disconnect();
        public void Disconnect(byte[] b);
        public void DisconnectForce();
        public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod);
        public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod);
        public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0);
        public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod);
        public int Id { get; }
        public IPAddress Address { get; }
        public int RemoteId { get; }
        public int RoundTripTime { get; }
        public int Ping => RoundTripTime / 2;
        public float TimeSinceLastPacket { get; }
        public long RemoteTimeDelta { get; }
        public DateTime RemoteUtcTime => new DateTime(DateTime.UtcNow.Ticks + RemoteTimeDelta);
        // Maximum UDP payload (no fragmentation) negotiated for this peer. Used by the
        // avatar bundle compressor to size compressed payloads to fit one datagram.
        public int Mtu { get; }

        // public readonly NetStatistics Statistics;
    }

    public interface NetManager
    {
        public void Start()
        {
            Start(0);
        }
        public void Start(int SetPort)
        {
            Start(IPAddress.Any, IPAddress.IPv6Any, SetPort);
        }
        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort);
        public void Stop();
        public Basis.Network.Core.NetPeer Connect(string sIP, int port, NetDataWriter Writer);
        public void PollEvents()
        {
        }
        public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint);

        public NetStatistics Statistics { get; }

        public int ConnectedPeersCount { get; }
    }

    public sealed partial class NetStatistics
    {
        public NetStatistics()
        {
        }

        public long PacketsSent;
        public long PacketsReceived;
        public long BytesSent;
        public long BytesReceived;
        public long PacketLoss;
    }

    public partial class NetPacketReader : NetDataReader
    {
        Action RecycleInternal;

        public NetPacketReader()
        {
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
		internal byte channel;
		internal DeliveryMethod method;
#endif

        public void Recycle(bool IsOkTOHaveEmptyData = false)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
			if (IsOkTOHaveEmptyData == false)
			{
				if (!EndOfData && AvailableBytes > 0)
				{
					BNL.LogWarning($"Message on channel {channel} with delivery method {method} had {AvailableBytes} bytes remaining when recycling! Is this a parsing bug?");
					// TODO: consider printing the bytes of the message.
				}
			}
#endif

            RecycleInternal?.Invoke();
        }

        public static NetPacketReader Create(byte[] source, int offset, int maxSize, Action recycle = null)
        {
            var reader = new NetPacketReader();
            reader.SetSource(source, offset, maxSize);
            reader.RecycleInternal = recycle;
            return reader;
        }
    }

    // Lifted straight from litenetlib


    public enum NetLogLevel
    {
        Warning,
        Error,
        Trace,
        Info
    }
    public interface INetLogger
    {
        void WriteNet(NetLogLevel level, string str, params object[] args);
    }

    public class NetDebug
    {
        public static INetLogger Logger;
    }

    /// <summary>
    /// Sending method type
    /// </summary>
    public enum DeliveryMethod : byte
    {
        /// <summary>
        /// Unreliable. Packets can be dropped, can be duplicated, can arrive without order.
        /// </summary>
        Unreliable = 4,

        /// <summary>
        /// Reliable. Packets won't be dropped, won't be duplicated, can arrive without order.
        /// </summary>
        ReliableUnordered = 0,

        /// <summary>
        /// Unreliable. Packets can be dropped, won't be duplicated, will arrive in order.
        /// </summary>
        Sequenced = 1,

        /// <summary>
        /// Reliable and ordered. Packets won't be dropped, won't be duplicated, will arrive in order.
        /// </summary>
        ReliableOrdered = 2,

        /// <summary>
        /// Reliable only last packet. Packets can be dropped (except the last one), won't be duplicated, will arrive in order.
        /// Cannot be fragmented
        /// </summary>
        ReliableSequenced = 3
    }

}


