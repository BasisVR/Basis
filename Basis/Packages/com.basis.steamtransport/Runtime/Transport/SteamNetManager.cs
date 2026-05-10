using Basis.Network.Core;
using Steamworks;
using Steamworks.Data;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Basis.Scripts.Networking.Steam
{
    internal enum SteamTransportPacketType : byte
    {
        Application = 0,
        ConnectRequest = 1,
        AssignPeer = 2,
    }

    internal sealed class SteamPendingConnection
    {
        public Connection Connection;
        public string Identity;
        public bool IsResolved;
        public SteamNetPeer Peer;
        public DateTime CreatedUtc;
        public DateTime LastActivityUtc;
    }

    internal sealed class SteamConnectionRequest : ConnectionRequest
    {
        private readonly SteamNetManager owner;
        private readonly SteamPendingConnection pendingConnection;
        private readonly NetDataReader data;

        public SteamConnectionRequest(SteamNetManager owner, SteamPendingConnection pendingConnection, byte[] connectPayload)
        {
            this.owner = owner;
            this.pendingConnection = pendingConnection;
            data = new NetDataReader(connectPayload);
        }

        public NetDataReader Data => data;

        public IPEndPoint RemoteEndPoint => new IPEndPoint(IPAddress.None, 0);

        public string Identity => pendingConnection.Identity;

        public NetPeer Accept()
        {
            return owner.AcceptPendingConnection(pendingConnection);
        }

        public void Reject(NetDataWriter w)
        {
            owner.RejectPendingConnection(pendingConnection, w);
        }
    }

    internal sealed class SteamNetPeer : NetPeer
    {
        private readonly SteamNetManager owner;
        private Connection connection;
        private string identity;
        private int id;
        private int remoteId;
        private DateTime lastPacketUtc = DateTime.UtcNow;

        public SteamNetPeer(SteamNetManager owner, Connection connection, int id, int remoteId, string identity)
        {
            this.owner = owner;
            this.connection = connection;
            this.id = id;
            this.remoteId = remoteId;
            this.identity = identity ?? string.Empty;
        }

        public void UpdateAssignedRemoteId(int assignedRemoteId)
        {
            remoteId = assignedRemoteId;
            if (id == 0)
            {
                id = assignedRemoteId;
            }
        }

        public void UpdateConnection(Connection updatedConnection, string updatedIdentity)
        {
            connection = updatedConnection;
            if (!string.IsNullOrWhiteSpace(updatedIdentity))
            {
                identity = updatedIdentity;
            }
        }

        public void MarkPacketReceived()
        {
            lastPacketUtc = DateTime.UtcNow;
        }

        public int Id => id;

        public IPAddress Address => IPAddress.None;

        public string Identity => identity;

        public int RemoteId => remoteId;

        public int RoundTripTime
        {
            get
            {
                try
                {
                    return connection.QuickStatus().Ping;
                }
                catch (Exception ex)
                {
                    BasisSteamTransportTrace.Error($"RoundTripTime failed connectionId={connection.Id} {ex}");
                    return 0;
                }
            }
        }

        public float TimeSinceLastPacket => (float)(DateTime.UtcNow - lastPacketUtc).TotalSeconds;

        public long RemoteTimeDelta => 0;

        public int Mtu => 1200;

        public void Disconnect()
        {
            connection.Close(false, 0, "Disconnected");
        }

        public void Disconnect(byte[] b)
        {
            connection.Close(false, 0, b == null || b.Length == 0 ? "Disconnected" : Encoding.UTF8.GetString(b));
        }

        public void DisconnectForce()
        {
            connection.Close(true, 0, "ForceDisconnect");
        }

        public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            owner.SendApplicationMessage(connection, data, 0, data.Length, channelNumber, deliveryMethod);
        }

        public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            owner.SendApplicationMessage(connection, data.Data, 0, data.Length, channelNumber, deliveryMethod);
        }

        public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0)
        {
            if (data == null || length <= 0)
            {
                BasisSteamTransportTrace.Error($"SendUnreliableRawMerge called with invalid data={data != null} length={length}");
                return;
            }

            if (patchOffset < 0 || patchOffset >= length)
            {
                owner.SendApplicationMessage(connection, data, offset, length, channelNumber, DeliveryMethod.Unreliable);
                return;
            }

            byte[] patchedData = ArrayPool<byte>.Shared.Rent(length > 0 ? length : 1);
            try
            {
                Buffer.BlockCopy(data, offset, patchedData, 0, length);
                patchedData[patchOffset] = patchValue;
                owner.SendApplicationMessage(connection, patchedData, 0, length, channelNumber, DeliveryMethod.Unreliable);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(patchedData);
            }
        }

        public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                ConnectionStatus status = connection.QuickStatus();
                return deliveryMethod == DeliveryMethod.Unreliable || deliveryMethod == DeliveryMethod.Sequenced
                    ? status.PendingUnreliable
                    : status.PendingReliable;
            }
            catch (Exception ex)
            {
                BasisSteamTransportTrace.Error($"GetPacketsCountInQueue failed connectionId={connection.Id} delivery={deliveryMethod} {ex}");
                return 0;
            }
        }

        public override bool Equals(object obj)
        {
            return obj is SteamNetPeer other && other.connection == connection;
        }

        public override int GetHashCode()
        {
            return connection.Id.GetHashCode();
        }
    }

    internal sealed class SteamServerSocketManager : SocketManager
    {
        public SteamNetManager Owner;

        public override void OnConnecting(Connection connection, ConnectionInfo info)
        {
            connection.Accept();
            Owner.RegisterPendingConnection(connection, info);
        }

        public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Owner.HandleServerMessage(connection, identity, data, size, channel);
        }

        public override void OnDisconnected(Connection connection, ConnectionInfo info)
        {
            Owner.HandleServerDisconnected(connection, info);
            connection.Close(false, 0, "Disconnected");
        }
    }

    internal sealed class SteamClientConnectionManager : ConnectionManager
    {
        public SteamNetManager Owner;
        public SteamNetPeer LocalPeer;
        public byte[] ConnectPayload;
        public bool HasAssignedPeerId;

        public override void OnConnected(ConnectionInfo info)
        {
            Owner.ConfigureConnectionLanes(Connection, "ClientConnected");
            if (ConnectPayload != null && ConnectPayload.Length > 0)
            {
                Owner.SendConnectRequest(Connection, ConnectPayload);
            }
        }

        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            Owner.HandleClientMessage(this, data, size, channel);
        }

        public override void OnDisconnected(ConnectionInfo info)
        {
            Owner.HandleClientDisconnected(LocalPeer, info);
        }
    }

    public class SteamNetManager : NetManager, IDisposable
    {
        private const int MaxAllocatedPeerId = ushort.MaxValue;
        private const byte SteamTransientLane = 0;
        private const byte SteamControlLane = 1;
        private const byte SteamResourceLane = 2;
        private const int MaxPendingConnections = 64;
        private const double PendingConnectionTimeoutSeconds = 10.0d;
        private const double PendingConnectionSweepIntervalSeconds = 1.0d;
        private const int ReceiveBatchSize = 64;
        private const int MaxMessagesPerPoll = 512;
        private const double MaxReceivePollMilliseconds = 2.0d;
        private static readonly double StopwatchTicksToMilliseconds = 1000d / Stopwatch.Frequency;
        private static readonly int[] LanePriorities = { 10, 10, 10 };
        private static readonly ushort[] LaneWeights = { 6, 3, 1 };
        private static readonly ArrayPool<byte> PacketBufferPool = ArrayPool<byte>.Shared;
        private static readonly List<SteamNetManager> activeManagers = new List<SteamNetManager>();
        private static readonly object activeManagersSync = new object();
        private static SteamNetManager[] activeManagersSnapshot = Array.Empty<SteamNetManager>();
        private readonly EventBasedNetListener listener;
        private readonly Configuration configuration;
        private readonly LNLNetManager fallbackManager;
        private readonly bool useFallback;
        private readonly NetStatistics statistics = new NetStatistics();
        private readonly Dictionary<uint, SteamPendingConnection> pendingConnections = new Dictionary<uint, SteamPendingConnection>();
        private readonly Dictionary<uint, SteamNetPeer> peersByConnection = new Dictionary<uint, SteamNetPeer>();
        private readonly Dictionary<int, SteamNetPeer> peersById = new Dictionary<int, SteamNetPeer>();
        private readonly List<SteamPendingConnection> stalePendingConnections = new List<SteamPendingConnection>();
        private Func<int, bool, int> serverReceiveDelegate;
        private Func<int, bool, int> clientReceiveDelegate;
        private SteamServerSocketManager serverSocketManager;
        private SteamClientConnectionManager clientConnectionManager;
        private bool serverReceiveEnabled = true;
        private bool clientReceiveEnabled = true;
        private int nextPeerId = 1;
        private DateTime nextPendingSweepUtc = DateTime.UtcNow;

        public SteamNetManager(EventBasedNetListener listener, Configuration configuration)
        {
            this.listener = listener;
            this.configuration = configuration;
            RegisterActiveManager(this);

            if (!configuration.UseSteamRelay)
            {
                useFallback = true;
                fallbackManager = new LNLNetManager(listener, configuration);
                BNL.LogWarning("Steam transport: UseSteamRelay is disabled, falling back to LiteNetLib.");
                return;
            }

            if (!SteamClient.IsValid)
            {
                useFallback = true;
                fallbackManager = new LNLNetManager(listener, configuration);
                BNL.LogWarning("Steam transport: SteamClient is not initialized, falling back to LiteNetLib.");
            }
            else
            {
                BasisSteamTransportTrace.Log($"SteamNetManager created. LobbyId={configuration.SteamLobbyId} HostSteamId={configuration.SteamHostSteamId} VirtualPort={configuration.SteamVirtualPort}");
            }
        }

        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            if (useFallback)
            {
                fallbackManager.Start(IPv4Address, IPv6Address, SetPort);
                return;
            }

            if (SetPort > 0)
            {
                serverSocketManager = SteamNetworkingSockets.CreateRelaySocket<SteamServerSocketManager>(configuration.SteamVirtualPort);
                serverSocketManager.Owner = this;
                serverReceiveDelegate = serverSocketManager.Receive;
                serverReceiveEnabled = serverSocketManager != null;
                BasisSteamTransportTrace.Log($"CreateRelaySocket virtualPort={configuration.SteamVirtualPort} setPort={SetPort}");
            }
        }

        public void Stop()
        {
            if (useFallback)
            {
                fallbackManager.Stop();
                ResetStatistics();
                UnregisterActiveManager(this);
                return;
            }

            BasisSteamTransportTrace.Log("SteamNetManager.Stop");

            if (clientConnectionManager != null)
            {
                clientConnectionManager.Close(false, 0, "Stop");
                clientConnectionManager = null;
                clientReceiveDelegate = null;
            }

            if (serverSocketManager != null)
            {
                serverSocketManager.Close();
                serverSocketManager = null;
                serverReceiveDelegate = null;
            }

            pendingConnections.Clear();
            peersByConnection.Clear();
            peersById.Clear();
            stalePendingConnections.Clear();
            ResetStatistics();
            BasisSteamTransportMetrics.RecordPendingConnections(0);
            serverReceiveEnabled = false;
            clientReceiveEnabled = false;
            nextPeerId = 1;
            UnregisterActiveManager(this);
        }

        public void Dispose()
        {
            Stop();
        }

        public NetPeer Connect(string sIP, int port, NetDataWriter writer)
        {
            if (useFallback)
            {
                return fallbackManager.Connect(sIP, port, writer);
            }

            if (configuration.SteamHostSteamId == 0)
            {
                BasisSteamTransportTrace.Error("ConnectRelay aborted: SteamHostSteamId was 0.");
                return null;
            }

            byte[] connectPayload = new byte[writer.Length];
            Buffer.BlockCopy(writer.Data, 0, connectPayload, 0, writer.Length);

            SteamNetPeer peer = new SteamNetPeer(this, default, 0, 0, configuration.SteamHostSteamId.ToString());
            clientConnectionManager = SteamNetworkingSockets.ConnectRelay<SteamClientConnectionManager>((SteamId)configuration.SteamHostSteamId, configuration.SteamVirtualPort);
            clientConnectionManager.Owner = this;
            clientConnectionManager.LocalPeer = peer;
            clientConnectionManager.ConnectPayload = connectPayload;
            clientReceiveDelegate = clientConnectionManager.Receive;
            clientReceiveEnabled = clientConnectionManager != null;
            BasisSteamTransportTrace.Log($"ConnectRelay hostSteamId={configuration.SteamHostSteamId} virtualPort={configuration.SteamVirtualPort} payloadBytes={connectPayload.Length}");
            return peer;
        }

        public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint)
        {
            if (useFallback)
            {
                return fallbackManager.SendUnconnectedMessage(writer, remoteEndPoint);
            }

            return false;
        }

        public void PollEvents()
        {
            if (useFallback)
            {
                fallbackManager.PollEvents();
                return;
            }

            SweepPendingConnectionsIfNeeded();

            if (serverReceiveEnabled && serverSocketManager != null && serverReceiveDelegate != null)
            {
                try
                {
                    DrainReceiveQueue(serverReceiveDelegate);
                }
                catch (Exception ex)
                {
                    serverReceiveEnabled = false;
                    BasisSteamTransportTrace.Error($"Server receive failed, disabling: {ex}");
                    try { serverSocketManager.Close(); }
                    catch (Exception closeEx) { BasisSteamTransportTrace.Error($"Server socket close also failed: {closeEx.Message}"); }
                    serverSocketManager = null;
                    serverReceiveDelegate = null;
                }
            }

            if (clientReceiveEnabled && clientConnectionManager != null && clientReceiveDelegate != null)
            {
                try
                {
                    DrainReceiveQueue(clientReceiveDelegate);
                }
                catch (Exception ex)
                {
                    clientReceiveEnabled = false;
                    BasisSteamTransportTrace.Error($"Client receive failed, disabling: {ex}");
                    try { clientConnectionManager.Close(false, 0, "ReceiveException"); }
                    catch (Exception closeEx) { BasisSteamTransportTrace.Error($"Client connection close also failed: {closeEx.Message}"); }
                    clientConnectionManager = null;
                    clientReceiveDelegate = null;
                }
            }
        }

        private static void VerifyReceiveSignature()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            System.Reflection.MethodInfo connectionReceive = typeof(ConnectionManager).GetMethod("Receive", new[] { typeof(int), typeof(bool) });
            System.Reflection.MethodInfo socketReceive = typeof(SocketManager).GetMethod("Receive", new[] { typeof(int), typeof(bool) });
            UnityEngine.Debug.Assert(connectionReceive != null && connectionReceive.ReturnType == typeof(int), "Facepunch ConnectionManager.Receive must return int for the bounded drain strategy.");
            UnityEngine.Debug.Assert(socketReceive != null && socketReceive.ReturnType == typeof(int), "Facepunch SocketManager.Receive must return int for the bounded drain strategy.");
#endif
        }

        private static void DrainReceiveQueue(Func<int, bool, int> receive)
        {
            long startTimestamp = Stopwatch.GetTimestamp();
            int processedTotal = 0;
            bool hitMessageBudget = false;
            bool hitTimeBudget = false;

            while (processedTotal < MaxMessagesPerPoll)
            {
                int remainingBudget = MaxMessagesPerPoll - processedTotal;
                int batchSize = remainingBudget < ReceiveBatchSize ? remainingBudget : ReceiveBatchSize;
                int processed = receive(batchSize, false);

                if (processed <= 0)
                {
                    break;
                }

                processedTotal += processed;

                if (processed < batchSize)
                {
                    break;
                }

                if (HasExceededReceiveBudget(startTimestamp))
                {
                    hitTimeBudget = true;
                    break;
                }
            }

            if (processedTotal >= MaxMessagesPerPoll)
            {
                hitMessageBudget = true;
            }

            BasisSteamTransportMetrics.RecordReceivePoll(processedTotal, MaxMessagesPerPoll, hitMessageBudget, hitTimeBudget);
        }

        private static bool HasExceededReceiveBudget(long startTimestamp)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
            double elapsedMilliseconds = elapsedTicks * StopwatchTicksToMilliseconds;
            return elapsedMilliseconds >= MaxReceivePollMilliseconds;
        }

        public NetStatistics Statistics
        {
            get
            {
                if (useFallback)
                {
                    return fallbackManager.Statistics;
                }

                return statistics;
            }
        }

        public int ConnectedPeersCount
        {
            get
            {
                if (useFallback)
                {
                    return fallbackManager.ConnectedPeersCount;
                }

                return peersById.Count;
            }
        }

        internal void RegisterPendingConnection(Connection connection, ConnectionInfo info)
        {
            if (pendingConnections.Count >= MaxPendingConnections)
            {
                BasisSteamTransportTrace.Warn($"Pending connection limit reached. connectionId={connection.Id} limit={MaxPendingConnections}");
                connection.Close(false, 0, "PendingLimitReached");
                return;
            }

            ConfigureConnectionLanes(connection, "ServerPendingConnection");
            DateTime now = DateTime.UtcNow;
            BasisSteamTransportTrace.Log($"RegisterPendingConnection connectionId={connection.Id} identity={info.Identity} state={info.State} endReason={info.EndReason}");
            pendingConnections[connection.Id] = new SteamPendingConnection
            {
                Connection = connection,
                Identity = info.Identity.ToString(),
                IsResolved = false,
                CreatedUtc = now,
                LastActivityUtc = now,
            };
            BasisSteamTransportMetrics.RecordPendingConnections(pendingConnections.Count);
        }

        internal void HandleServerMessage(Connection connection, NetIdentity identity, IntPtr data, int size, int channel)
        {
            byte[] managedData = CopyToPooledBuffer(data, size);
            statistics.BytesReceived += size;
            statistics.PacketsReceived++;
            BasisSteamTransportMetrics.RecordReceiveSuccess(channel, size);
            bool returnBuffer = true;

            try
            {
                if (pendingConnections.TryGetValue(connection.Id, out SteamPendingConnection pending) && !pending.IsResolved)
                {
                    pending.LastActivityUtc = DateTime.UtcNow;
                    HandlePendingConnectMessage(pending, managedData, size);
                    return;
                }

                if (!peersByConnection.TryGetValue(connection.Id, out SteamNetPeer peer))
                {
                    BasisSteamTransportTrace.Error($"HandleServerMessage from unknown connectionId={connection.Id}, closing");
                    connection.Close(true, 0, "UnknownPeer");
                    return;
                }

                peer.MarkPacketReceived();
                returnBuffer = false;
                HandleApplicationMessage(peer, managedData, size, channel, true);
            }
            finally
            {
                if (returnBuffer)
                {
                    ReturnPacketBuffer(managedData);
                }
            }
        }

        internal void HandleServerDisconnected(Connection connection, ConnectionInfo info)
        {
            BasisSteamTransportTrace.Warn($"HandleServerDisconnected connectionId={connection.Id} state={info.State} endReason={info.EndReason}");
            if (pendingConnections.Remove(connection.Id))
            {
                BasisSteamTransportMetrics.RecordPendingConnections(pendingConnections.Count);
                return;
            }

            if (!peersByConnection.TryGetValue(connection.Id, out SteamNetPeer peer))
            {
                BasisSteamTransportTrace.Error($"HandleServerDisconnected: unknown connectionId={connection.Id}, not in pending or active peers");
                return;
            }

            peersByConnection.Remove(connection.Id);
            peersById.Remove(peer.Id);
            listener.RaisePeerDisconnected(peer, MapDisconnectInfo(info));
        }

        internal NetPeer AcceptPendingConnection(SteamPendingConnection pendingConnection)
        {
            if (pendingConnection.IsResolved && pendingConnection.Peer != null)
            {
                return pendingConnection.Peer;
            }

            int assignedPeerId = AllocatePeerId();
            SteamNetPeer peer = new SteamNetPeer(this, pendingConnection.Connection, assignedPeerId, assignedPeerId, pendingConnection.Identity);
            pendingConnection.IsResolved = true;
            pendingConnection.Peer = peer;
            pendingConnections.Remove(pendingConnection.Connection.Id);
            BasisSteamTransportMetrics.RecordPendingConnections(pendingConnections.Count);
            peersByConnection[pendingConnection.Connection.Id] = peer;
            peersById[assignedPeerId] = peer;

            BasisSteamTransportTrace.Log($"AcceptPendingConnection connectionId={pendingConnection.Connection.Id} assignedPeerId={assignedPeerId} identity={pendingConnection.Identity}");
            SendAssignPeerId(pendingConnection.Connection, assignedPeerId);
            listener.RaisePeerConnected(peer);
            return peer;
        }

        internal void RejectPendingConnection(SteamPendingConnection pendingConnection, NetDataWriter writer)
        {
            pendingConnection.IsResolved = true;
            pendingConnections.Remove(pendingConnection.Connection.Id);
            BasisSteamTransportMetrics.RecordPendingConnections(pendingConnections.Count);
            BasisSteamTransportTrace.Warn($"RejectPendingConnection connectionId={pendingConnection.Connection.Id}");
            pendingConnection.Connection.Close(false, 0, "Rejected");
        }

        internal void HandleClientMessage(SteamClientConnectionManager manager, IntPtr data, int size, int channel)
        {
            byte[] managedData = CopyToPooledBuffer(data, size);
            statistics.BytesReceived += size;
            statistics.PacketsReceived++;
            BasisSteamTransportMetrics.RecordReceiveSuccess(channel, size);
            bool returnBuffer = true;

            if (size == 0)
            {
                BasisSteamTransportTrace.Error("HandleClientMessage received empty packet");
                ReturnPacketBuffer(managedData);
                return;
            }

            try
            {
                switch ((SteamTransportPacketType)managedData[0])
                {
                    case SteamTransportPacketType.AssignPeer:
                        if (size < 3)
                        {
                            BasisSteamTransportTrace.Error($"HandleClientMessage AssignPeer packet too small size={size}");
                            return;
                        }

                        ushort assignedPeerId = BitConverter.ToUInt16(managedData, 1);
                        manager.LocalPeer.UpdateConnection(manager.Connection, configuration.SteamHostSteamId.ToString());
                        manager.LocalPeer.UpdateAssignedRemoteId(assignedPeerId);
                        manager.LocalPeer.MarkPacketReceived();
                        BasisSteamTransportTrace.Log($"Client received AssignPeer assignedPeerId={assignedPeerId}");

                        if (!manager.HasAssignedPeerId)
                        {
                            manager.HasAssignedPeerId = true;
                            listener.RaisePeerConnected(manager.LocalPeer);
                        }
                        break;

                    case SteamTransportPacketType.Application:
                        manager.LocalPeer.MarkPacketReceived();
                        returnBuffer = false;
                        HandleApplicationMessage(manager.LocalPeer, managedData, size, channel, true);
                        break;
                }
            }
            finally
            {
                if (returnBuffer)
                {
                    ReturnPacketBuffer(managedData);
                }
            }
        }

        internal void HandleClientDisconnected(SteamNetPeer peer, ConnectionInfo info)
        {
            BasisSteamTransportTrace.Warn($"HandleClientDisconnected state={info.State} endReason={info.EndReason}");
            listener.RaisePeerDisconnected(peer, MapDisconnectInfo(info));
        }

        internal void SendConnectRequest(Connection connection, byte[] connectPayload)
        {
            int packetLength = connectPayload.Length + 1;
            byte[] packet = RentPacketBuffer(packetLength);
            packet[0] = (byte)SteamTransportPacketType.ConnectRequest;
            Buffer.BlockCopy(connectPayload, 0, packet, 1, connectPayload.Length);
            BasisSteamTransportTrace.Log($"SendConnectRequest payloadBytes={connectPayload.Length}");
            SendPacket(connection, packet, 0, packetLength, SteamControlLane, DeliveryMethod.ReliableOrdered, true);
        }

        internal void SendApplicationMessage(Connection connection, byte[] data, int offset, int length, byte channel, DeliveryMethod deliveryMethod)
        {
            int packetLength = length + 3;
            byte[] packet = RentPacketBuffer(packetLength);
            packet[0] = (byte)SteamTransportPacketType.Application;
            packet[1] = (byte)deliveryMethod;
            packet[2] = channel;
            Buffer.BlockCopy(data, offset, packet, 3, length);
            SendPacket(connection, packet, 0, packetLength, GetSteamLane(channel), deliveryMethod, true);
        }

        private void HandlePendingConnectMessage(SteamPendingConnection pendingConnection, byte[] managedData, int dataSize)
        {
            if (dataSize < 2 || (SteamTransportPacketType)managedData[0] != SteamTransportPacketType.ConnectRequest)
            {
                BasisSteamTransportTrace.Error($"Invalid connect request packet. size={dataSize}");
                pendingConnection.Connection.Close(true, 0, "InvalidConnectRequest");
                pendingConnections.Remove(pendingConnection.Connection.Id);
                return;
            }

            byte[] payload = new byte[dataSize - 1];
            Buffer.BlockCopy(managedData, 1, payload, 0, payload.Length);
            BasisSteamTransportTrace.Log($"HandlePendingConnectMessage connectionId={pendingConnection.Connection.Id} payloadBytes={payload.Length}");
            listener.RaiseConnectionRequest(new SteamConnectionRequest(this, pendingConnection, payload));
        }

        private void HandleApplicationMessage(SteamNetPeer peer, byte[] managedData, int dataSize, int channel, bool pooledBuffer)
        {
            if (dataSize < 3 || (SteamTransportPacketType)managedData[0] != SteamTransportPacketType.Application)
            {
                BasisSteamTransportTrace.Error($"HandleApplicationMessage invalid packet size={dataSize} type={(dataSize > 0 ? managedData[0] : -1)}");
                if (pooledBuffer)
                {
                    ReturnPacketBuffer(managedData);
                }
                return;
            }

            DeliveryMethod deliveryMethod = (DeliveryMethod)managedData[1];
            byte basisChannel = managedData[2];
            Action recycle = pooledBuffer ? () => ReturnPacketBuffer(managedData) : null;
            NetPacketReader reader = NetPacketReader.Create(managedData, 3, dataSize, recycle);
            try
            {
                listener.RaiseNetworkReceive(peer, reader, basisChannel, deliveryMethod);
            }
            catch (Exception ex)
            {
                BasisSteamTransportTrace.Error($"HandleApplicationMessage dispatch failed channel={basisChannel} delivery={deliveryMethod} {ex}");
                reader.Recycle(true);
                throw;
            }
        }

        private void SendAssignPeerId(Connection connection, int assignedPeerId)
        {
            byte[] packet = new byte[3];
            packet[0] = (byte)SteamTransportPacketType.AssignPeer;
            packet[1] = (byte)assignedPeerId;
            packet[2] = (byte)(assignedPeerId >> 8);
            BasisSteamTransportTrace.Log($"SendAssignPeerId assignedPeerId={assignedPeerId}");
            SendPacket(connection, packet, 0, packet.Length, SteamControlLane, DeliveryMethod.ReliableOrdered);
        }

        private void SendPacket(Connection connection, byte[] packet, int offset, int length, byte steamLane, DeliveryMethod deliveryMethod, bool returnToPool = false)
        {
            try
            {
                Result result = connection.SendMessage(packet, offset, length, MapSendType(deliveryMethod, steamLane), steamLane);
                if (result == Result.OK)
                {
                    statistics.BytesSent += length;
                    statistics.PacketsSent++;
                    BasisSteamTransportMetrics.RecordSendSuccess(steamLane, length);
                }
                else
                {
                    BasisSteamTransportMetrics.RecordSendFailure();
                    BasisSteamTransportTrace.Error($"SendPacket failed result={result} connectionId={connection.Id} packetBytes={length} steamLane={steamLane} delivery={deliveryMethod}");
                }
            }
            finally
            {
                if (returnToPool)
                {
                    ReturnPacketBuffer(packet);
                }
            }
        }

        private static byte[] RentPacketBuffer(int size)
        {
            return PacketBufferPool.Rent(size > 0 ? size : 1);
        }

        private static byte[] CopyToPooledBuffer(IntPtr data, int size)
        {
            byte[] buffer = RentPacketBuffer(size);
            if (size > 0)
            {
                Marshal.Copy(data, buffer, 0, size);
            }

            return buffer;
        }

        private static void ReturnPacketBuffer(byte[] buffer)
        {
            if (buffer != null)
            {
                PacketBufferPool.Return(buffer);
            }
        }

        private void SweepPendingConnectionsIfNeeded()
        {
            if (pendingConnections.Count == 0)
            {
                nextPendingSweepUtc = DateTime.UtcNow.AddSeconds(PendingConnectionSweepIntervalSeconds);
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (now < nextPendingSweepUtc)
            {
                return;
            }

            nextPendingSweepUtc = now.AddSeconds(PendingConnectionSweepIntervalSeconds);
            stalePendingConnections.Clear();

            foreach (SteamPendingConnection pendingConnection in pendingConnections.Values)
            {
                if (pendingConnection.IsResolved)
                {
                    continue;
                }

                if ((now - pendingConnection.LastActivityUtc).TotalSeconds >= PendingConnectionTimeoutSeconds)
                {
                    stalePendingConnections.Add(pendingConnection);
                }
            }

            for (int index = 0; index < stalePendingConnections.Count; index++)
            {
                SteamPendingConnection pendingConnection = stalePendingConnections[index];
                pendingConnections.Remove(pendingConnection.Connection.Id);
                BasisSteamTransportTrace.Warn($"Pending connection timed out. connectionId={pendingConnection.Connection.Id} identity={pendingConnection.Identity} timeoutSeconds={PendingConnectionTimeoutSeconds}");
                pendingConnection.Connection.Close(false, 0, "PendingTimeout");
            }

            stalePendingConnections.Clear();
            BasisSteamTransportMetrics.RecordPendingConnections(pendingConnections.Count);
        }

        internal void ConfigureConnectionLanes(Connection connection, string context)
        {
            Result result = connection.ConfigureConnectionLanes(LanePriorities, LaneWeights);
            if (result == Result.OK)
            {
                BasisSteamTransportTrace.Log($"ConfigureConnectionLanes context={context} connectionId={connection.Id} lanes=3");
            }
            else
            {
                BasisSteamTransportTrace.Error($"ConfigureConnectionLanes failed context={context} connectionId={connection.Id} result={result}");
            }
        }

        private static byte GetSteamLane(byte basisChannel)
        {
            if (IsTransientBasisChannel(basisChannel))
            {
                return SteamTransientLane;
            }

            if (IsResourceBasisChannel(basisChannel))
            {
                return SteamResourceLane;
            }

            return SteamControlLane;
        }

        private static bool IsTransientBasisChannel(byte basisChannel)
        {
            return basisChannel == BasisNetworkCommons.VoiceChannel
                || basisChannel == BasisNetworkCommons.ShoutVoiceChannel
                || basisChannel == BasisNetworkCommons.AvatarChannel
                || basisChannel == BasisNetworkCommons.CameraPIPPositionChannel
                || (basisChannel >= BasisNetworkCommons.PlayerAvatarVeryLowChannel && basisChannel <= BasisNetworkCommons.PlayerAvatarHighAdditionalChannel);
        }

        private static bool IsResourceBasisChannel(byte basisChannel)
        {
            return basisChannel == BasisNetworkCommons.SceneChannel
                || basisChannel == BasisNetworkCommons.LoadResourceChannel
                || basisChannel == BasisNetworkCommons.UnloadResourceChannel
                || basisChannel == BasisNetworkCommons.ContentShareChannel
                || basisChannel == BasisNetworkCommons.ContentShareCleanupChannel;
        }

        private static SendType MapSendType(DeliveryMethod deliveryMethod, byte steamLane)
        {
            SendType sendType = deliveryMethod switch
            {
                DeliveryMethod.Unreliable => SendType.Unreliable,
                DeliveryMethod.Sequenced => SendType.Unreliable,
                DeliveryMethod.ReliableUnordered => SendType.Reliable,
                DeliveryMethod.ReliableOrdered => SendType.Reliable,
                DeliveryMethod.ReliableSequenced => SendType.Reliable,
                _ => SendType.Reliable
            };

            if ((deliveryMethod == DeliveryMethod.Unreliable || deliveryMethod == DeliveryMethod.Sequenced) && steamLane == SteamTransientLane)
            {
                sendType |= SendType.NoNagle;
            }

            return sendType;
        }

        private DisconnectInfo MapDisconnectInfo(ConnectionInfo info)
        {
            DisconnectReason reason = info.State switch
            {
                ConnectionState.Connected => DisconnectReason.RemoteConnectionClose,
                ConnectionState.ClosedByPeer => DisconnectReason.RemoteConnectionClose,
                ConnectionState.ProblemDetectedLocally => DisconnectReason.ConnectionFailed,
                _ => DisconnectReason.ConnectionFailed
            };

            switch (info.EndReason)
            {
                case NetConnectionEnd.Remote_Timeout:
                case NetConnectionEnd.Misc_Timeout:
                    reason = DisconnectReason.Timeout;
                    break;
                case NetConnectionEnd.Misc_NoRelaySessionsToClient:
                case NetConnectionEnd.Misc_SteamConnectivity:
                    reason = DisconnectReason.HostUnreachable;
                    break;
            }

            return new DisconnectInfo
            {
                Reason = reason,
                SocketErrorCode = 0,
                AdditionalData = null
            };
        }

        private int AllocatePeerId()
        {
            if (nextPeerId < 1 || nextPeerId > MaxAllocatedPeerId)
            {
                nextPeerId = 1;
            }

            int startPeerId = nextPeerId;
            int candidatePeerId = nextPeerId;

            do
            {
                if (!peersById.ContainsKey(candidatePeerId))
                {
                    nextPeerId = candidatePeerId >= MaxAllocatedPeerId ? 1 : candidatePeerId + 1;
                    return candidatePeerId;
                }

                candidatePeerId = candidatePeerId >= MaxAllocatedPeerId ? 1 : candidatePeerId + 1;
            }
            while (candidatePeerId != startPeerId);

            throw new InvalidOperationException("Peer id space exhausted.");
        }

        private void ResetStatistics()
        {
            statistics.PacketsSent = 0;
            statistics.PacketsReceived = 0;
            statistics.BytesSent = 0;
            statistics.BytesReceived = 0;
            statistics.PacketLoss = 0;
        }

        private static void RegisterActiveManager(SteamNetManager manager)
        {
            VerifyReceiveSignature();
            lock (activeManagersSync)
            {
                if (!activeManagers.Contains(manager))
                {
                    activeManagers.Add(manager);
                    activeManagersSnapshot = activeManagers.ToArray();
                }
            }
        }

        private static void UnregisterActiveManager(SteamNetManager manager)
        {
            lock (activeManagersSync)
            {
                if (activeManagers.Remove(manager))
                {
                    activeManagersSnapshot = activeManagers.ToArray();
                }
            }
        }

        public static void PollActiveManagers()
        {
            SteamNetManager[] managers = activeManagersSnapshot;
            for (int index = 0; index < managers.Length; index++)
            {
                managers[index].PollEvents();
            }
        }
    }
}
