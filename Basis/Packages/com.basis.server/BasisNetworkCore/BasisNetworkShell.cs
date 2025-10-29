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

		public static explicit operator DisconnectInfo(LiteNetLib.DisconnectInfo info) {
			return new DisconnectInfo
			{
				// TODO: better enum conversion?
				Reason = (DisconnectReason)(int)info.Reason,
				SocketErrorCode = info.SocketErrorCode,
				AdditionalData = null, // TODO: convert to common netpacketreader
			};
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
			PeerDisconnectedEvent?.Invoke(new LNLNetPeer(peer), (DisconnectInfo)disconnectInfo);
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
			// TODO: convert netpacketreader to common
			NetworkReceiveEvent?.Invoke(new LNLNetPeer(peer), null, channelNumber, (DeliveryMethod)(byte)deliveryMethod);
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

	// TODO: consider interface instead of abstract class
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

		public static explicit operator NetStatistics(LiteNetLib.NetStatistics stats) {
			return new NetStatistics()
			{
				PacketsSent = stats.PacketsSent,
				PacketsReceived = stats.PacketsReceived,
				BytesSent = stats.BytesSent,
				BytesReceived = stats.BytesReceived,
				PacketLoss = stats.PacketLoss,
			};
		}
	}

	public class NetPacketReader : NetDataReader
	{
		 public void Recycle() { }
	}

	public class NetDataReader
	{
		public bool TryGetUShort(out ushort result) { result = 0; return false; }
		public bool TryGetByte(out byte result) { result = 0; return false; }
		public string GetString() { return ""; }
		public ushort GetUShort() { return 0; }
		public byte GetByte() { return 0; }
		public sbyte GetSByte() { return 0; }
		public char GetChar() { return '\0'; }
		public bool GetBool() { return false; }
		public float GetFloat() { return 0.0f; }
		public int GetInt() { return 0; }
		public double GetDouble() { return 0; }
		public long GetLong() { return 0; }
		public short GetShort() { return 0; }
		public ulong GetULong() { return 0; }
		public bool Get(out string result) { result = ""; return false; }
		public bool Get(out byte result) { result = 0; return false; }
		public bool Get(out int result) { result = 0; return false; }
		public bool Get(out ushort result) { result = 0; return false; }
		public bool Get(out float result) { result = 0.0f; return false; }
		public void GetBytes(byte[] destination, int start, int count) { }
		public void GetBytes(byte[] destination, int count) { }
		public byte[] GetBytesWithLength() { return new byte[0]; }
		public byte[] GetRemainingBytes() { return new byte[0]; }
		public bool TryGetString(out string result) { result = null; return false; }
		public bool TryGetBytesWithLength(out byte[] result){ result = null; return false; }
		public ArraySegment<byte> GetRemainingBytesSegment() { return null; }

		public bool EndOfData;
		public int AvailableBytes;
	}

	public class NetDataWriter
	{

		public NetDataWriter() : this(true, defaultInitialSize)
		{
		}

		public NetDataWriter(bool autoResize) : this(autoResize, defaultInitialSize)
		{
		}

		public NetDataWriter(bool autoResize, int initialSize)
		{
			_data = new byte[initialSize];
			_autoResize = autoResize;
		}

		public void Put(float value) { }
		public void Put(double value) { }
		public void Put(long value) { }
		public void Put(ulong value) { }
		public void Put(int value) { }
		public void Put(uint value) { }
		public void Put(ushort value) { }
		public void Put(char value) { }
		public void Put(string value) { }
		public void Put(bool value) { }
		public void Put(byte[] data, int offset, int length) { }
		public void Put(byte[] data) { }
		public void Reset() { }

		public int Length;

		const int defaultInitialSize = 64;

		bool _autoResize;
		byte [] _data;
		int _position;
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


