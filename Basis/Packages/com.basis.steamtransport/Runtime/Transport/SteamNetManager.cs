using Basis.Network.Core;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
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
                catch
                {
                    return 0;
                }
            }
        }

        public float TimeSinceLastPacket => (float)(DateTime.UtcNow - lastPacketUtc).TotalSeconds;

        public long RemoteTimeDelta => 0;

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

        public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                ConnectionStatus status = connection.QuickStatus();
                return deliveryMethod == DeliveryMethod.Unreliable || deliveryMethod == DeliveryMethod.Sequenced
                    ? status.PendingUnreliable
                    : status.PendingReliable;
            }
            catch
            {
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

    public class SteamNetManager : NetManager
    {
        private static readonly List<SteamNetManager> activeManagers = new List<SteamNetManager>();
        private static readonly object activeManagersSync = new object();
        private readonly EventBasedNetListener listener;
        private readonly Configuration configuration;
        private readonly LNLNetManager fallbackManager;
        private readonly bool useFallback;
        private readonly NetStatistics statistics = new NetStatistics();
        private readonly Dictionary<uint, SteamPendingConnection> pendingConnections = new Dictionary<uint, SteamPendingConnection>();
        private readonly Dictionary<uint, SteamNetPeer> peersByConnection = new Dictionary<uint, SteamNetPeer>();
        private readonly Dictionary<int, SteamNetPeer> peersById = new Dictionary<int, SteamNetPeer>();
        private SteamServerSocketManager serverSocketManager;
        private SteamClientConnectionManager clientConnectionManager;
        private bool serverReceiveEnabled = true;
        private bool clientReceiveEnabled = true;
        private int nextPeerId = 1;

        public SteamNetManager(EventBasedNetListener listener, Configuration configuration)
        {
            this.listener = listener;
            this.configuration = configuration;
            RegisterActiveManager(this);

            if (!configuration.UseSteamRelay)
            {
                useFallback = true;
                fallbackManager = new LNLNetManager(listener, configuration);
                BNL.LogWarning("Steam transport currently supports relay mode first. Falling back to LiteNetLib because UseSteamRelay is disabled.");
                BasisSteamTransportTrace.Warn("UseSteamRelay=false. Falling back to LiteNetLib.");
                return;
            }

            if (!SteamClient.IsValid)
            {
                useFallback = true;
                fallbackManager = new LNLNetManager(listener, configuration);
                BNL.LogWarning("Steam transport requested while SteamClient is not initialized. Falling back to LiteNetLib.");
                BasisSteamTransportTrace.Warn("SteamClient is not initialized. Falling back to LiteNetLib.");
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
                serverReceiveEnabled = serverSocketManager != null;
                BasisSteamTransportTrace.Log($"CreateRelaySocket virtualPort={configuration.SteamVirtualPort} setPort={SetPort}");
            }
        }

        public void Stop()
        {
            if (useFallback)
            {
                fallbackManager.Stop();
                UnregisterActiveManager(this);
                return;
            }

            BasisSteamTransportTrace.Log("SteamNetManager.Stop");

            if (clientConnectionManager != null)
            {
                clientConnectionManager.Close(false, 0, "Stop");
                clientConnectionManager = null;
            }

            if (serverSocketManager != null)
            {
                serverSocketManager.Close();
                serverSocketManager = null;
            }

            pendingConnections.Clear();
            peersByConnection.Clear();
            peersById.Clear();
            serverReceiveEnabled = false;
            clientReceiveEnabled = false;
            UnregisterActiveManager(this);
        }

        public NetPeer Connect(string sIP, int port, NetDataWriter writer)
        {
            if (useFallback)
            {
                return fallbackManager.Connect(sIP, port, writer);
            }

            if (configuration.SteamHostSteamId == 0)
            {
                BNL.LogError("Steam transport connect failed because SteamHostSteamId was not set.");
                BasisSteamTransportTrace.Error("ConnectRelay aborted because SteamHostSteamId was 0.");
                return null;
            }

            byte[] connectPayload = new byte[writer.Length];
            Buffer.BlockCopy(writer.Data, 0, connectPayload, 0, writer.Length);

            SteamNetPeer peer = new SteamNetPeer(this, default, 0, 0, configuration.SteamHostSteamId.ToString());
            clientConnectionManager = SteamNetworkingSockets.ConnectRelay<SteamClientConnectionManager>((SteamId)configuration.SteamHostSteamId, configuration.SteamVirtualPort);
            clientConnectionManager.Owner = this;
            clientConnectionManager.LocalPeer = peer;
            clientConnectionManager.ConnectPayload = connectPayload;
            clientReceiveEnabled = clientConnectionManager != null;
            BasisSteamTransportTrace.Log($"ConnectRelay hostSteamId={configuration.SteamHostSteamId} virtualPort={configuration.SteamVirtualPort} payloadBytes={connectPayload.Length}");
            return peer;
        }

        public void PollEvents()
        {
            if (useFallback)
            {
                fallbackManager.PollEvents();
                return;
            }

            if (serverReceiveEnabled && serverSocketManager != null)
            {
                try
                {
                    serverSocketManager.Receive(32);
                }
                catch (Exception ex)
                {
                    serverReceiveEnabled = false;
                    BasisSteamTransportTrace.Error($"Server Receive exception: {ex}");
                    try
                    {
                        serverSocketManager.Close();
                    }
                    catch
                    {
                    }

                    serverSocketManager = null;
                }
            }

            if (clientReceiveEnabled && clientConnectionManager != null)
            {
                try
                {
                    clientConnectionManager.Receive(32);
                }
                catch (Exception ex)
                {
                    clientReceiveEnabled = false;
                    BasisSteamTransportTrace.Error($"Client Receive exception: {ex}");
                    try
                    {
                        clientConnectionManager.Close(false, 0, "ReceiveException");
                    }
                    catch
                    {
                    }

                    clientConnectionManager = null;
                }
            }
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
            BasisSteamTransportTrace.Log($"RegisterPendingConnection connectionId={connection.Id} identity={info.Identity} state={info.State} endReason={info.EndReason}");
            pendingConnections[connection.Id] = new SteamPendingConnection
            {
                Connection = connection,
                Identity = info.Identity.ToString(),
                IsResolved = false
            };
        }

        internal void HandleServerMessage(Connection connection, NetIdentity identity, IntPtr data, int size, int channel)
        {
            byte[] managedData = new byte[size];
            Marshal.Copy(data, managedData, 0, size);
            statistics.BytesReceived += size;
            statistics.PacketsReceived++;

            if (pendingConnections.TryGetValue(connection.Id, out SteamPendingConnection pending) && !pending.IsResolved)
            {
                HandlePendingConnectMessage(pending, managedData);
                return;
            }

            if (!peersByConnection.TryGetValue(connection.Id, out SteamNetPeer peer))
            {
                connection.Close(true, 0, "UnknownPeer");
                return;
            }

            peer.MarkPacketReceived();
            HandleApplicationMessage(peer, managedData, channel);
        }

        internal void HandleServerDisconnected(Connection connection, ConnectionInfo info)
        {
            BasisSteamTransportTrace.Warn($"HandleServerDisconnected connectionId={connection.Id} state={info.State} endReason={info.EndReason}");
            if (pendingConnections.Remove(connection.Id))
            {
                return;
            }

            if (!peersByConnection.TryGetValue(connection.Id, out SteamNetPeer peer))
            {
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
            BasisSteamTransportTrace.Warn($"RejectPendingConnection connectionId={pendingConnection.Connection.Id}");
            pendingConnection.Connection.Close(false, 0, "Rejected");
        }

        internal void HandleClientMessage(SteamClientConnectionManager manager, IntPtr data, int size, int channel)
        {
            byte[] managedData = new byte[size];
            Marshal.Copy(data, managedData, 0, size);
            statistics.BytesReceived += size;
            statistics.PacketsReceived++;

            if (managedData.Length == 0)
            {
                return;
            }

            switch ((SteamTransportPacketType)managedData[0])
            {
                case SteamTransportPacketType.AssignPeer:
                    if (managedData.Length < 3)
                    {
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
                    HandleApplicationMessage(manager.LocalPeer, managedData, channel);
                    break;
            }
        }

        internal void HandleClientDisconnected(SteamNetPeer peer, ConnectionInfo info)
        {
            BasisSteamTransportTrace.Warn($"HandleClientDisconnected state={info.State} endReason={info.EndReason}");
            listener.RaisePeerDisconnected(peer, MapDisconnectInfo(info));
        }

        internal void SendConnectRequest(Connection connection, byte[] connectPayload)
        {
            byte[] packet = new byte[connectPayload.Length + 1];
            packet[0] = (byte)SteamTransportPacketType.ConnectRequest;
            Buffer.BlockCopy(connectPayload, 0, packet, 1, connectPayload.Length);
            BasisSteamTransportTrace.Log($"SendConnectRequest payloadBytes={connectPayload.Length}");
            SendPacket(connection, packet, 0, DeliveryMethod.ReliableOrdered);
        }

        internal void SendApplicationMessage(Connection connection, byte[] data, int offset, int length, byte channel, DeliveryMethod deliveryMethod)
        {
            byte[] packet = new byte[length + 3];
            packet[0] = (byte)SteamTransportPacketType.Application;
            packet[1] = (byte)deliveryMethod;
            packet[2] = channel;
            Buffer.BlockCopy(data, offset, packet, 3, length);
            SendPacket(connection, packet, 0, deliveryMethod);
        }

        private void HandlePendingConnectMessage(SteamPendingConnection pendingConnection, byte[] managedData)
        {
            if (managedData.Length < 2 || (SteamTransportPacketType)managedData[0] != SteamTransportPacketType.ConnectRequest)
            {
                BasisSteamTransportTrace.Error($"Invalid connect request packet. size={managedData.Length}");
                pendingConnection.Connection.Close(true, 0, "InvalidConnectRequest");
                pendingConnections.Remove(pendingConnection.Connection.Id);
                return;
            }

            byte[] payload = new byte[managedData.Length - 1];
            Buffer.BlockCopy(managedData, 1, payload, 0, payload.Length);
            BasisSteamTransportTrace.Log($"HandlePendingConnectMessage connectionId={pendingConnection.Connection.Id} payloadBytes={payload.Length}");
            listener.RaiseConnectionRequest(new SteamConnectionRequest(this, pendingConnection, payload));
        }

        private void HandleApplicationMessage(SteamNetPeer peer, byte[] managedData, int channel)
        {
            if (managedData.Length < 3 || (SteamTransportPacketType)managedData[0] != SteamTransportPacketType.Application)
            {
                return;
            }

            DeliveryMethod deliveryMethod = (DeliveryMethod)managedData[1];
            byte basisChannel = managedData[2];
            NetPacketReader reader = NetPacketReader.Create(managedData, 3, managedData.Length);
            listener.RaiseNetworkReceive(peer, reader, basisChannel, deliveryMethod);
        }

        private void SendAssignPeerId(Connection connection, int assignedPeerId)
        {
            byte[] packet = new byte[3];
            packet[0] = (byte)SteamTransportPacketType.AssignPeer;
            byte[] idBytes = BitConverter.GetBytes((ushort)assignedPeerId);
            Buffer.BlockCopy(idBytes, 0, packet, 1, 2);
            BasisSteamTransportTrace.Log($"SendAssignPeerId assignedPeerId={assignedPeerId}");
            SendPacket(connection, packet, 0, DeliveryMethod.ReliableOrdered);
        }

        private void SendPacket(Connection connection, byte[] packet, byte channel, DeliveryMethod deliveryMethod)
        {
            Result result = connection.SendMessage(packet, MapSendType(deliveryMethod), channel);
            if (result == Result.OK)
            {
                statistics.BytesSent += packet.Length;
                statistics.PacketsSent++;
            }
            else
            {
                BasisSteamTransportTrace.Error($"SendPacket failed result={result} connectionId={connection.Id} packetBytes={packet.Length} channel={channel} delivery={deliveryMethod}");
            }
        }

        private static SendType MapSendType(DeliveryMethod deliveryMethod)
        {
            return deliveryMethod switch
            {
                DeliveryMethod.Unreliable => SendType.Unreliable,
                DeliveryMethod.Sequenced => SendType.Unreliable,
                DeliveryMethod.ReliableUnordered => SendType.Reliable,
                DeliveryMethod.ReliableOrdered => SendType.Reliable,
                DeliveryMethod.ReliableSequenced => SendType.Reliable,
                _ => SendType.Reliable
            };
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
            while (peersById.ContainsKey(nextPeerId))
            {
                nextPeerId++;
            }

            return nextPeerId++;
        }

        private static void RegisterActiveManager(SteamNetManager manager)
        {
            lock (activeManagersSync)
            {
                if (!activeManagers.Contains(manager))
                {
                    activeManagers.Add(manager);
                }
            }
        }

        private static void UnregisterActiveManager(SteamNetManager manager)
        {
            lock (activeManagersSync)
            {
                activeManagers.Remove(manager);
            }
        }

        public static void PollActiveManagers()
        {
            SteamNetManager[] managers;
            lock (activeManagersSync)
            {
                managers = activeManagers.ToArray();
            }

            for (int index = 0; index < managers.Length; index++)
            {
                managers[index].PollEvents();
            }
        }
    }
}
