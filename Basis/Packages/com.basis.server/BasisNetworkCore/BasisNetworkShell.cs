using UnityEngine;
using System;
using System.Net;

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
	}


	public class EventBasedNetListener
	{
		public delegate void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo);
		public delegate void OnNetworkError(DisconnectInfo reason);
		public delegate void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod);
		public delegate void OnConnectionRequest(ConnectionRequest request);
		public delegate void OnPeerConnected(NetPeer peer);

		public event OnConnectionRequest ConnectionRequestEvent;
		public event OnPeerDisconnected PeerDisconnectedEvent;
		public event OnNetworkReceive NetworkReceiveEvent;
		public event OnNetworkError NetworkErrorEvent;
		public event OnPeerConnected PeerConnectedEvent;
	}

	public class ConnectionRequest
	{
		public void Reject( NetDataWriter w ) { }
		public NetPeer Accept() { return null; }
		public NetDataReader Data;
		public readonly IPEndPoint RemoteEndPoint;
	}

	public class NetPeer : IPEndPoint
	{
		public NetPeer(NetManager netManager, IPEndPoint remoteEndPoint, int id) : base(remoteEndPoint.Address, remoteEndPoint.Port)
		{
		}

		public void Disconnect() { }
		public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod ) { }
		public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod ) { }
		public void DisconnectPeerForce(NetPeer peer) { }
		public int GetPacketsCountInQueue( int channel, DeliveryMethod deliveryMethod ) { return 0; }
		public int Id;
		public string Address;
		public int RemoteId;
		private int _avgRtt;
		public int RoundTripTime => _avgRtt;
		public int Ping => _avgRtt / 2;
		private volatile float _timeSinceLastPacket;
		private long _remoteDelta;
		public float TimeSinceLastPacket => _timeSinceLastPacket;
		public long RemoteTimeDelta => _remoteDelta;
		public DateTime RemoteUtcTime => new DateTime(DateTime.UtcNow.Ticks + _remoteDelta);

		public readonly NetStatistics Statistics;
	}

	public class NetManager
	{
		public NetManager(EventBasedNetListener e) { }
		public void Start() { }
		public void Start( string IPv4Address, string IPv6Address, int SetPort) { }
		public void Start( int SetPort ) { }
		public void Stop() { }
		public Basis.Network.Core.NetPeer Connect( string sIP, int port, NetDataWriter Writer) { return null; }
		public void Disconnect() { }
		public void DisconnectPeer( NetPeer p, byte[] b ) { return; }

		public readonly NetStatistics Statistics;

		// Need to update these (These are required)
		public int ConnectedPeersCount = 0;
		public int ChannelsCount = 0;

		public int ReceivePollingTime = 0;
		public int PacketPoolSize = 0;
		public bool AllowPeerAddressChange = false;
		public bool UnconnectedMessagesEnabled = false;
		public bool NatPunchEnabled = false;
		public int UpdateTime = 15;
		public int PingInterval = 1000;
		public int DisconnectTimeout = 5000;
		public bool SimulatePacketLoss = false;
		public bool SimulateLatency = false;
		public int SimulationPacketLossChance = 10;
		public int SimulationMinLatency = 30;
		public int SimulationMaxLatency = 100;
		public bool UnsyncedEvents = false;
		public bool UnsyncedReceiveEvent = false;
		public bool UnsyncedDeliveryEvent = false;
		public bool BroadcastReceiveEnabled = false;
		public int ReconnectDelay = 500;
		public int MaxConnectAttempts = 10;
		public bool ReuseAddress = false;
		public bool DontRoute = false;
		//public readonly NetStatistics Statistics = new NetStatistics();
		public bool EnableStatistics = false;
		//public readonly NatPunchModule NatPunchModule;
		public bool IsRunning = false;
		//public int LocalPort { get; private set; }
		public bool AutoRecycle = true;
		public bool IPv6Enabled = true;
		public int MtuOverride = 0;
		public bool MtuDiscovery = false;
		//public NetPeer FirstPeer => _headPeer;
		public bool UseNativeSockets = false;
		public bool DisconnectOnUnreachable = false;
	}

	public sealed class NetStatistics
	{
		public long PacketsSent;
		public long PacketsReceived;
		public long BytesSent;
		public long BytesReceived;
		public long PacketLoss;
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


