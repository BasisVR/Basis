using LiteNetLib.Layers;
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace Basis.Network.Core
{

    public partial class EventBasedNetListener : LiteNetLib.INetEventListener
    {
        void LiteNetLib.INetEventListener.OnConnectionRequest(LiteNetLib.ConnectionRequest request)
        {
            ConnectionRequestEvent?.Invoke(new LNLConnectionRequest(request));
        }

        void LiteNetLib.INetEventListener.OnPeerDisconnected(LiteNetLib.NetPeer peer, LiteNetLib.DisconnectInfo disconnectInfo)
        {
            PeerDisconnectedEvent?.Invoke(new LNLNetPeer(peer), new DisconnectInfo(disconnectInfo));
        }

        void LiteNetLib.INetEventListener.OnPeerConnected(LiteNetLib.NetPeer peer)
        {
            PeerConnectedEvent?.Invoke(new LNLNetPeer(peer));
        }

        void LiteNetLib.INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            NetworkErrorEvent?.Invoke(endPoint, socketError);
        }

        void LiteNetLib.INetEventListener.OnNetworkReceive(LiteNetLib.NetPeer peer, LiteNetLib.NetPacketReader reader, byte channelNumber, LiteNetLib.DeliveryMethod deliveryMethod)
        {
            NetPacketReader read = new NetPacketReader(reader);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            read.channel = channelNumber;
            read.method = (DeliveryMethod)(byte)deliveryMethod;
#endif

            NetworkReceiveEvent?.Invoke(new LNLNetPeer(peer), read, channelNumber, (DeliveryMethod)(byte)deliveryMethod);
        }

        void LiteNetLib.INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, LiteNetLib.NetPacketReader reader, LiteNetLib.UnconnectedMessageType messageType)
        {
            // unused
        }

        void LiteNetLib.INetEventListener.OnNetworkLatencyUpdate(LiteNetLib.NetPeer peer, int latency)
        {
            // unused
        }
    }

    public partial struct DisconnectInfo
    {
        internal DisconnectInfo(LiteNetLib.DisconnectInfo info)
        {
            NetPacketReader reader = new NetPacketReader(info.AdditionalData);

            // TODO: better enum conversion?
            Reason = (DisconnectReason)(int)info.Reason;
            SocketErrorCode = info.SocketErrorCode;
            AdditionalData = reader;
        }
    }

    public sealed partial class NetStatistics
    {
        internal NetStatistics(LiteNetLib.NetStatistics stats)
        {
            PacketsSent = stats.PacketsSent;
            PacketsReceived = stats.PacketsReceived;
            BytesSent = stats.BytesSent;
            BytesReceived = stats.BytesReceived;
            PacketLoss = stats.PacketLoss;
        }
    }

    public partial class NetPacketReader
    {
        internal NetPacketReader(LiteNetLib.NetPacketReader reader) : base((LiteNetLib.Utils.NetDataReader)reader)
        {
            RecycleInternal = () => reader.Recycle();
        }
    }

    public class LNLConnectionRequest : ConnectionRequest
    {
        readonly LiteNetLib.ConnectionRequest request;
        readonly NetDataReader data;

        internal LNLConnectionRequest(LiteNetLib.ConnectionRequest request)
        {
            this.request = request;
            data = new NetDataReader(request.Data);
        }

        public NetDataReader Data => data;

        public IPEndPoint RemoteEndPoint => request.RemoteEndPoint;

        NetPeer ConnectionRequest.Accept()
        {
            return new LNLNetPeer(request.Accept());
        }

        void ConnectionRequest.Reject(NetDataWriter w)
        {
            request.Reject(w.Data, 0, w.Length, false);
        }
    }

    public class LNLNetPeer : NetPeer
    {
        private readonly LiteNetLib.NetPeer peer;

        internal LNLNetPeer(LiteNetLib.NetPeer lnlPeer)
        {
            peer = lnlPeer;
        }

        int NetPeer.Id => peer.Id;

        IPAddress NetPeer.Address => peer.Address;

        int NetPeer.RemoteId => peer.RemoteId;

        int NetPeer.RoundTripTime => peer.RoundTripTime;

        float NetPeer.TimeSinceLastPacket => peer.TimeSinceLastPacket;

        long NetPeer.RemoteTimeDelta => peer.RemoteTimeDelta;

        void NetPeer.Disconnect()
        {
            peer.Disconnect();
        }

        void NetPeer.Disconnect(byte[] b)
        {
            peer.Disconnect(b);
        }

        void NetPeer.DisconnectForce()
        {
            peer.NetManager.DisconnectPeerForce(peer);
        }

        int NetPeer.GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod)
        {
            return peer.GetPacketsCountInQueue(channel, (LiteNetLib.DeliveryMethod)(byte)deliveryMethod);
        }

        void NetPeer.Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            peer.Send(data, channelNumber, (LiteNetLib.DeliveryMethod)(byte)deliveryMethod);
        }

        void NetPeer.Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            peer.Send(data.Data, 0, data.Length, channelNumber, (LiteNetLib.DeliveryMethod)(byte)deliveryMethod);
        }

        public override bool Equals(object obj)
        {
            if (obj == null || !(obj is LNLNetPeer))
            {
                return false;
            }
            else
            {
                return peer.Equals(((LNLNetPeer)obj).peer);
            }
        }

        public override int GetHashCode()
        {
            return peer.GetHashCode();
        }
    }

    public class LNLNetManager : NetManager
    {
        public LiteNetLib.NetManager manager;

        public LNLNetManager(EventBasedNetListener listener, Configuration configuration)
        {
            if(configuration.UseNetworkFinalCompression)
            {
                Compressor = new CompressionPacketLayer();//we dont want to use this normally
                //when there is a few people this might make sense to help reduced network data.
                //client has to also have this on.
            }
            else
            {
                Compressor = null;
            }
            manager = new LiteNetLib.NetManager(listener, Compressor)
            {
                AutoRecycle = false,
                UnconnectedMessagesEnabled = false,
                NatPunchEnabled = configuration.NatPunchEnabled,
                AllowPeerAddressChange = configuration.AllowPeerAddressChange,
                BroadcastReceiveEnabled = false,
                UseNativeSockets = configuration.UseNativeSockets,
                ChannelsCount = BasisNetworkCommons.TotalChannels,
                EnableStatistics = configuration.EnableStatistics,
                IPv6Enabled = configuration.IPv6Enabled,
                UpdateTime = BasisNetworkCommons.NetworkIntervalPoll,
                PingInterval = configuration.PingInterval,
                DisconnectTimeout = configuration.DisconnectTimeout,
                UnsyncedEvents = true,
                ReceivePollingTime = BasisNetworkCommons.ReceivePollingTime,
                PacketPoolSize = BasisNetworkCommons.PacketPoolSize,
                SimulateLatency = configuration.SimulateLatency,
                SimulatePacketLoss = configuration.SimulatePacketLoss,
                SimulationMaxLatency = configuration.SimulationMaxLatency,
                SimulationMinLatency = configuration.SimulationMinLatency,
                SimulationPacketLossChance = configuration.SimulationPacketLossChance,
                MtuDiscovery = configuration.MtuDiscovery,
                MtuOverride = configuration.MtuOverride
            };
        }
        public static CompressionPacketLayer Compressor = null;
public class CompressionPacketLayer : PacketLayerBase
    {
        // 1 byte flag + 4 bytes original length
        private const int HeaderSize = 5;
        private const byte FlagRaw = 0;
        private const byte FlagDeflate = 1;

        // Tune these:
        private const int MinCompressBytes = 300;
        private const CompressionLevel Level = CompressionLevel.Fastest; // often better for real-time networking

        public CompressionPacketLayer() : base(64) { } // margin; header + a bit of slop

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            // If you truly want to bypass, still mark it raw so inbound never tries to deflate.
            if (length < MinCompressBytes)
            {
                WriteRawHeader(ref data, ref offset, ref length);
                return;
            }

            int originalLen = length;

            // Ensure we have the raw bytes contiguous at data[0..originalLen)
            if (offset != 0)
            {
                Buffer.BlockCopy(data, offset, data, 0, originalLen);
                offset = 0;
            }

            // Pre-size output to avoid growth reallocations.
            // Deflate rarely expands much, but worst-case can be slightly larger; this is a practical estimate.
            int initialCapacity = originalLen + 64;

            using var output = new MemoryStream(initialCapacity);
            output.Position = HeaderSize; // reserve header

            using (var dstream = new DeflateStream(output, Level, leaveOpen: true))
            {
                dstream.Write(data, 0, originalLen);
            }

            int compressedPayloadLen = (int)output.Length - HeaderSize;

            // If it didn't shrink, send raw (no pointless work + avoids expansion risk).
            // Also note: header adds 5 bytes, so require actual win vs original.
            if (compressedPayloadLen + HeaderSize >= originalLen + HeaderSize)
            {
                WriteRawHeader(ref data, ref offset, ref length); // keeps original bytes, just prefixes header
                return;
            }

            // Write header (flag + original length)
            WriteDeflateHeader(output.GetBuffer(), originalLen);

            // Copy stream buffer into packet buffer without ToArray()
            if (!output.TryGetBuffer(out ArraySegment<byte> seg))
                {
                    seg = new ArraySegment<byte>(output.ToArray()); // fallback (rare)
                }

                int totalLen = HeaderSize + compressedPayloadLen;

            EnsureCapacity(ref data, totalLen);
            Buffer.BlockCopy(seg.Array!, seg.Offset, data, 0, totalLen);

            offset = 0;
            length = totalLen;
        }

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            if (length < HeaderSize) return; // malformed / too small

            byte flag = data[0];

            if (flag == FlagRaw)
            {
                // Strip header in-place (shift left)
                int payloadLen = length - HeaderSize;
                Buffer.BlockCopy(data, HeaderSize, data, 0, payloadLen);
                length = payloadLen;
                return;
            }

            if (flag != FlagDeflate)
            {
                // Unknown flag: ignore or treat as fatal depending on your protocol
                return;
            }

            int originalLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1, 4));
            if (originalLen <= 0) return;

            try
            {
                using var input = new MemoryStream(data, HeaderSize, length - HeaderSize, writable: false);
                using var dstream = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);

                // Pre-size to expected output to avoid MemoryStream growth
                using var output = new MemoryStream(originalLen);
                dstream.CopyTo(output);

                int decompressedLen = (int)output.Length;
                if (decompressedLen != originalLen)
                {
                    // Optional sanity check; sometimes streams can differ if sender lied.
                    // You can choose to drop packet here.
                }

                EnsureCapacity(ref data, decompressedLen);
                Buffer.BlockCopy(output.GetBuffer(), 0, data, 0, decompressedLen);
                length = decompressedLen;
            }
            catch
            {
                // If you want: drop packet / mark error / stats.
            }
        }

        private static void WriteDeflateHeader(byte[] buffer, int originalLen)
        {
            buffer[0] = FlagDeflate;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), originalLen);
        }

        private static void WriteRawHeader(ref byte[] data, ref int offset, ref int length)
        {
            // Make contiguous
            if (offset != 0)
            {
                Buffer.BlockCopy(data, offset, data, 0, length);
                offset = 0;
            }

            int payloadLen = length;
            int totalLen = HeaderSize + payloadLen;

            EnsureCapacity(ref data, totalLen);

            // Shift payload right to make room for header
            Buffer.BlockCopy(data, 0, data, HeaderSize, payloadLen);

            data[0] = FlagRaw;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1, 4), payloadLen);

            offset = 0;
            length = totalLen;
        }

        private static void EnsureCapacity(ref byte[] buffer, int required)
        {
            if (buffer.Length >= required) return;
            Array.Resize(ref buffer, required);
        }
}

        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            manager.Start(IPv4Address, IPv6Address, SetPort);
        }

        public void Stop()
        {
            manager.Stop();
        }

        public Basis.Network.Core.NetPeer Connect(string sIP, int port, NetDataWriter Writer)
        {

            LiteNetLib.NetPeer peer = manager.Connect(LiteNetLib.NetUtils.MakeEndPoint(sIP, port), Writer.AsReadOnlySpan());
            return new LNLNetPeer(peer);
        }

        public int ConnectedPeersCount => manager.ConnectedPeersCount;

        public NetStatistics Statistics => new NetStatistics(manager.Statistics);
    }
}
