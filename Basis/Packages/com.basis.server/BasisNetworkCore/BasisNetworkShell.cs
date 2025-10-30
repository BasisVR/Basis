using System;
using System.Net;
using LiteNetLib;
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

	public struct DisconnectInfo
	{
		public DisconnectReason Reason;
		public System.Net.Sockets.SocketError SocketErrorCode;
		public NetPacketReader AdditionalData;

		internal DisconnectInfo(LiteNetLib.DisconnectInfo info) {
			NetPacketReader reader = new NetPacketReader(info.AdditionalData);

            // TODO: better enum conversion?
            Reason = (DisconnectReason)(int)info.Reason;
            SocketErrorCode = info.SocketErrorCode;
            AdditionalData = reader;
        }
	}


	public class EventBasedNetListener: LiteNetLib.INetEventListener
	{
		public delegate void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo);
		public delegate void OnNetworkError(IPEndPoint endPoint, SocketError socketError);
		public delegate void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod);
		public delegate void OnConnectionRequest(ConnectionRequest request);
		public delegate void OnPeerConnected(NetPeer peer);

		public event OnConnectionRequest ConnectionRequestEvent;
		public event OnPeerDisconnected PeerDisconnectedEvent;
		public event OnNetworkReceive NetworkReceiveEvent;
		public event OnNetworkError NetworkErrorEvent;
		public event OnPeerConnected PeerConnectedEvent;

		void INetEventListener.OnConnectionRequest(LiteNetLib.ConnectionRequest request)
		{
			ConnectionRequestEvent?.Invoke(new LNLConnectionRequest(request));
		}

		void INetEventListener.OnPeerDisconnected(LiteNetLib.NetPeer peer, LiteNetLib.DisconnectInfo disconnectInfo)
		{
			PeerDisconnectedEvent?.Invoke(new LNLNetPeer(peer), new DisconnectInfo(disconnectInfo));
		}

		void INetEventListener.OnPeerConnected(LiteNetLib.NetPeer peer)
		{
			PeerConnectedEvent?.Invoke(new LNLNetPeer(peer));
		}

		void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
		{
			NetworkErrorEvent?.Invoke(endPoint, socketError);
		}

		void INetEventListener.OnNetworkReceive(LiteNetLib.NetPeer peer, LiteNetLib.NetPacketReader reader, byte channelNumber, LiteNetLib.DeliveryMethod deliveryMethod)
		{
			NetPacketReader read = new NetPacketReader(reader);
            NetworkReceiveEvent?.Invoke(new LNLNetPeer(peer), read, channelNumber, (DeliveryMethod)(byte)deliveryMethod);
		}

		void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, LiteNetLib.NetPacketReader reader, UnconnectedMessageType messageType)
		{
			// unused
		}

		void INetEventListener.OnNetworkLatencyUpdate(LiteNetLib.NetPeer peer, int latency)
		{
			// unused
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
		public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod);
		public int Id { get; }
		public IPAddress Address { get; }
		public int RemoteId { get; }
		public int RoundTripTime { get; }
		public int Ping => RoundTripTime / 2;
		public float TimeSinceLastPacket { get; }
		public long RemoteTimeDelta { get; }
		public DateTime RemoteUtcTime => new DateTime(DateTime.UtcNow.Ticks + RemoteTimeDelta);

		// public readonly NetStatistics Statistics;
	}

	public interface NetManager
	{
		public void Start() {
			Start(0);
		}
		public void Start( int SetPort ) {
			Start(IPAddress.Any, IPAddress.IPv6Any, SetPort);
		}
		public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort);
		public void Stop();
		public Basis.Network.Core.NetPeer Connect(string sIP, int port, NetDataWriter Writer);

		public NetStatistics Statistics { get; }

		public int ConnectedPeersCount { get; }
	}

	public sealed class NetStatistics
	{
		public long PacketsSent;
		public long PacketsReceived;
		public long BytesSent;
		public long BytesReceived;
		public long PacketLoss;

		internal NetStatistics(LiteNetLib.NetStatistics stats) {
			PacketsSent = stats.PacketsSent;
			PacketsReceived = stats.PacketsReceived;
			BytesSent = stats.BytesSent;
			BytesReceived = stats.BytesReceived;
			PacketLoss = stats.PacketLoss;
		}
	}

	public class NetPacketReader : NetDataReader
	{
		Action RecycleInternal;

		internal NetPacketReader(LiteNetLib.NetPacketReader reader): base((LiteNetLib.Utils.NetDataReader)reader) {
			RecycleInternal = () => reader.Recycle();
		}

		public void Recycle() {
			RecycleInternal?.Invoke();
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


