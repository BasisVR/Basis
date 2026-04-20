using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkCore;
using BasisNetworkCore.Pooling;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisPermissions;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerHandle
{
    public static class BasisServerHandleEvents
    {
        [ThreadStatic] private static HashSet<int> _excludedSet;

        #region Server Events Setup
        public static void SubscribeServerEvents()
        {
            NetworkServer.Listener.ConnectionRequestEvent += HandleConnectionRequest;
            NetworkServer.Listener.PeerDisconnectedEvent += HandlePeerDisconnected;
            NetworkServer.Listener.NetworkReceiveEvent += BasisNetworkMessageProcessor.ProcessMessage;
            NetworkServer.Listener.NetworkErrorEvent += OnNetworkError;
        }

        public static void UnsubscribeServerEvents()
        {
            NetworkServer.Listener.ConnectionRequestEvent -= HandleConnectionRequest;
            NetworkServer.Listener.PeerDisconnectedEvent -= HandlePeerDisconnected;
            NetworkServer.Listener.NetworkReceiveEvent -= BasisNetworkMessageProcessor.ProcessMessage;
            NetworkServer.Listener.NetworkErrorEvent -= OnNetworkError;
        }

        public static void StopWorker()
        {
            NetworkServer.Server?.Stop();
            BasisServerHandleEvents.UnsubscribeServerEvents();
        }
        #endregion

        #region Network Event Handlers

        public static void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            BNL.LogError($"Endpoint {endPoint.ToString()} was reported with error {socketError}");
        }
        #endregion

        #region Peer Connection and Disconnection
        public static void HandlePeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            try
            {
                if(peer == null)
                {
                    BNL.LogError("Missing Peer this is a mistake!");
                    return;
                }
                int id = peer.Id;

                // Clean up stored metadata before the UUID mapping is removed
                if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string disconnectedUuid))
                {
                    PermissionIntegration.RemovePlayerMeta(disconnectedUuid);
                }

                NetworkServer.AuthIdentity.RemoveConnection(id);
                BasisNetworkOwnership.RemovePlayerOwnership(id);
                BasisSavedState.RemovePlayer(id);
                BasisServerReductionSystemEvents.RemovePlayer(id);
                BasisNetworkPIPCamera.RemovePlayer(id);
                BasisNetworkContentShare.RemovePlayerSpheres(id);
                BasisNetworkPreloadResourceManagement.RemovePeer(id);

                if (NetworkServer.AuthenticatedPeers.TryRemove(id, out _))
                {
                    NetworkServer.RebuildPeerSnapshot();
                    BNL.Log($"Peer removed: {id}");
                }
                else
                {
                    BNL.LogError($"Failed to remove peer: {id}");
                }

                if (NetworkServer.AuthenticatedPeers.IsEmpty)
                {
                    BasisNetworkIDDatabase.Reset();
                    BasisNetworkResourceManagement.Reset();
                    BasisNetworkContentShare.Reset();
                }

                NetDataWriter writer = NetworkServer.RentWriter();
                writer.Put((ushort)id);
                if (NetworkServer.CheckValidated(writer))
                {
                    NetPeer[] Peers = NetworkServer.PeerSnapshot;
                    foreach (var client in Peers)
                    {
                        if (client.Id != id)
                        {
                            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.DisconnectionChannel, writer.Length);
                            client.Send(writer, BasisNetworkCommons.DisconnectionChannel, DeliveryMethod.ReliableOrdered);
                        }
                    }
                }
                NetworkServer.ReturnWriter(writer);
            }
            catch (Exception e)
            {
                BNL.LogError($"{e.Message} {e.StackTrace}");
            }
        }
        #endregion

        #region Utility Methods
        public static void RejectWithReason(ConnectionRequest request, string reason)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(reason);
            request.Reject(writer);
            NetworkServer.ReturnWriter(writer);
            BNL.LogError($"Rejected for reason: {reason}");
        }
        public static void RejectWithReason(NetPeer request, string reason)
        {
            ushort id = (ushort)request.Id;
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(reason ?? string.Empty);
            byte[] reasonBytes = writer.CopyData();
            NetworkServer.ReturnWriter(writer);
            if (NetworkServer.AuthenticatedPeers.TryRemove(id, out _))
            {
                NetworkServer.RebuildPeerSnapshot();
            }
            request.Disconnect(reasonBytes);
            BNL.LogError($"Rejected after accept with reason: {reason}");
        }

        public static bool IsHeadlessDisallowed(ClientMetaDataMessage metaData, out string reason)
        {
            if (!BasisHeadlessConnectionPolicyManager.HeadlessDisallowed ||
                !BasisHeadlessConnectionPolicyManager.IsHeadlessClient(metaData))
            {
                reason = null;
                return false;
            }

            reason = BasisHeadlessConnectionPolicyManager.DisallowedReason;
            return true;
        }
        #endregion

        #region Connection Handling
        public static void HandleConnectionRequest(ConnectionRequest ConReq)
        {
            try
            {
                if (BasisPlayerModeration.IsIpBanned(ConReq.RemoteEndPoint.Address.ToString()))
                {
                    RejectWithReason(ConReq, "Banned IP");
                    return;
                }
              //  BNL.Log("Processing Connection Request");
                int ServerCount = NetworkServer.Server.ConnectedPeersCount;

                if (ServerCount >= NetworkServer.Configuration.PeerLimit)
                {
                    RejectWithReason(ConReq, "Server is full! Rejected.");
                    return;
                }

                if (!ConReq.Data.TryGetUShort(out ushort ClientVersion))
                {
                    RejectWithReason(ConReq, "Invalid client data.");
                    return;
                }

                if (ClientVersion < BasisNetworkVersion.ServerVersion)
                {
                    RejectWithReason(ConReq, "Outdated client version.");
                    return;
                }
                if (NetworkServer.Configuration.UseAuth)
                {
                    BytesMessage authMessage = new BytesMessage();
                    authMessage.Deserialize(ConReq.Data, out byte[] AuthBytes);
                    if (NetworkServer.Auth.IsAuthenticated(AuthBytes) == false)
                    {
                        RejectWithReason(ConReq, "Authentication failed, Auth rejected");
                        return;
                    }
                }
                else
                {
                    //we still want to read the data to move the needle along
                    BytesMessage authMessage = new BytesMessage();
                    authMessage.Deserialize(ConReq.Data, out byte[] UnusedBytes);
                }
                if (NetworkServer.Configuration.UseAuthIdentity)
                {
                    NetPeer newPeer = ConReq.Accept();//can do both way Communication from here on
                    NetworkServer.AuthIdentity.ProcessConnection(NetworkServer.Configuration, ConReq, newPeer);
                }
                else
                {
                    ReadyMessage readyMessage = new ReadyMessage();
                    readyMessage.Deserialize(ConReq.Data);

                    if (readyMessage.WasDeserializedCorrectly())
                    {
                        if (IsHeadlessDisallowed(readyMessage.playerMetaDataMessage, out string reason))
                        {
                            RejectWithReason(ConReq, reason);
                            return;
                        }
                    }

                    NetPeer newPeer = ConReq.Accept();//can do both way Communication from here on

                    if (readyMessage.WasDeserializedCorrectly())
                    {
                        OnNetworkAccepted(newPeer, readyMessage, readyMessage.playerMetaDataMessage.playerUUID);
                    }
                }
            }
            catch (Exception e)
            {
                RejectWithReason(ConReq, "Fatal Connection Issue stacktrace on server " + e.Message);
                BNL.LogError(e.StackTrace);
            }
        }
        public static void OnNetworkAccepted(NetPeer newPeer, ReadyMessage ReadyMessage, string UUID)
        {
            ushort PeerId = (ushort)newPeer.Id;
            if (NetworkServer.AuthenticatedPeers.TryAdd(PeerId, newPeer))
            {
                NetworkServer.RebuildPeerSnapshot();
                BNL.Log($"Peer connected: {newPeer.Id}");
                //never ever assume the UUID provided by the user is good always recalc on the server.
                //this means that as long as they pass auth but locally have a bad UUID that only they locally are effected.
                //there is no way to force a user locally to be a certain UUID, thats not how the internet works.
                //instead we can make sure all additional clients have them correct.
                //this only occurs if the server is doing Auth checks.
                ReadyMessage.playerMetaDataMessage.playerUUID = UUID;
                PermissionIntegration.StorePlayerMeta(UUID, ReadyMessage.playerMetaDataMessage);

               Configuration Config = NetworkServer.Configuration;
                //lets dump to the local client there data after the server has had its way
                ServerMetaDataMessage ServerMetaDataMessage = new ServerMetaDataMessage
                {
                    ClientMetaDataMessage = ReadyMessage.playerMetaDataMessage,
                    SyncInterval = Config.BSRSMillisecondDefaultInterval,
                    BaseMultiplier = Config.BSRBaseMultiplier,
                    IncreaseRate = Config.BSRSIncreaseRate,
                    SlowestSendRate = Config.BSRSlowestSendRate,
                    PeerLimit = Config.PeerLimit,

                };
                ServerMetaDataMessage.SetPermissions(PermissionIntegration.Manager.GetAllAllowedRules(UUID), PermissionIntegration.Manager.GetAllDeniedRules(UUID));
                NetDataWriter Writer = NetworkServer.RentWriter();
                ServerMetaDataMessage.Serialize(Writer);
                NetworkServer.TrySend(newPeer, Writer, BasisNetworkCommons.metaDataChannel, DeliveryMethod.ReliableOrdered);

                if (BasisNetworkIDDatabase.GetAllNetworkID(out List<ServerNetIDMessage> ServerNetIDMessages))
                {
                    ServerUniqueIDMessages ServerUniqueIDMessageArray = new ServerUniqueIDMessages
                    {
                        Messages = ServerNetIDMessages.ToArray(),
                    };

                    Writer.Reset();
                    ServerUniqueIDMessageArray.Serialize(Writer);
                    //BNL.Log($"Sending out Network Id Count " + ServerUniqueIDMessageArray.Messages.Length);
                    NetworkServer.TrySend(newPeer, Writer, BasisNetworkCommons.NetIDAssignsChannel, DeliveryMethod.ReliableOrdered);
                }
                else
                {
                    BNL.Log($"No Network Ids Not Sending out");
                }

                NetworkServer.ReturnWriter(Writer);

                SendRemoteSpawnMessage(newPeer, ReadyMessage);

                BasisNetworkResourceManagement.SendOutAllResources(newPeer);
                BasisNetworkOwnership.SendOutOwnershipInformation(newPeer);
                BasisNetworkPIPCamera.SendPIPStateToPeer(newPeer);
                BasisNetworkContentShare.SendAllSpheresToPeer(newPeer);
                BasisNetworkServer.Security.BasisGlobalLockManager.SendLockStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisHeadlessAudioStateManager.SendStateToPeer(newPeer);
                BasisNetworkServer.Security.BasisHeadlessConnectionPolicyManager.SendStateToPeer(newPeer);
                SendShoutStateToPeer(newPeer);
            }
            else
            {
                RejectWithReason(newPeer, "Peer already exists.");
            }
        }
        #endregion
        // Define the delegate type
        public delegate void AuthEventHandler(NetPacketReader reader, NetPeer peer);

        // Declare an event of the delegate type
        public static event AuthEventHandler OnAuthReceived;
        public static void HandleAuth(NetPacketReader Reader, NetPeer Peer)
        {
            OnAuthReceived?.Invoke(Reader, Peer);
            Reader.Recycle();
        }
        public static ServerEventHandler OnServerReceived;
        public delegate void ServerEventHandler(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod);
        #region Avatar and Voice Handling
        public static void SendAvatarMessageToClients(NetPacketReader Reader, NetPeer Peer)
        {
            ClientAvatarChangeMessage ClientAvatarChangeMessage = new ClientAvatarChangeMessage();
            ClientAvatarChangeMessage.Deserialize(Reader);
            Reader.Recycle();

            // Global avatar lock: reject network broadcast but still save state locally
            if (BasisNetworkServer.Security.BasisGlobalLockManager.AvatarsLocked)
            {
                bool isAdmin = false;
                if (NetworkServer.AuthIdentity.NetIDToUUID(Peer, out string uuid))
                {
                    isAdmin = PermissionIntegration.HasValidRequirement(uuid, PermNodes.All);
                }

                if (!isAdmin)
                {
                    BNL.Log($"Avatar loading is globally disabled. Rejected avatar change from peer {Peer.Id}");
                    BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "Avatar loading is currently disabled by an admin.");
                    return;
                }
            }

            ServerAvatarChangeMessage serverAvatarChangeMessage = new ServerAvatarChangeMessage
            {
                clientAvatarChangeMessage = ClientAvatarChangeMessage,
                uShortPlayerId = new PlayerIdMessage
                {
                    playerID = (ushort)Peer.Id
                }
            };
            BasisSavedState.AddLastData(Peer, ClientAvatarChangeMessage);
            NetDataWriter Writer = NetworkServer.RentWriter();
            serverAvatarChangeMessage.Serialize(Writer);

            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AvatarChangeMessageChannel, Peer, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(Writer);
        }

        public static void HandleVoiceMessage(NetPacketReader reader, NetPeer peer)
        {
            AudioSegmentDataMessage audioSegment = ThreadSafeMessagePool<AudioSegmentDataMessage>.Rent();
            audioSegment.Deserialize(reader);
            reader.Recycle();

            ServerAudioSegmentMessage serverAudio = new ServerAudioSegmentMessage
            {
                audioSegmentData = audioSegment,
            };

            SendVoiceMessageToClients(serverAudio, peer, DeliveryMethod.Unreliable);

            ThreadSafeMessagePool<AudioSegmentDataMessage>.Return(audioSegment);
        }

        /// <summary>
        /// Handles shout voice sent by a client on ShoutVoiceChannel (channel 0).
        /// Only processes if the sender is authorized for shout mode.
        /// Broadcasts to ALL connected peers.
        /// </summary>
        public static void HandleShoutVoiceMessage(NetPacketReader reader, NetPeer peer)
        {
            if (!BasisSavedState.IsInShoutMode(peer.Id))
            {
                BNL.LogError($"Peer {peer.Id} sent shout voice but is not in shout mode. Ignoring.");
                reader.Recycle();
                return;
            }

            AudioSegmentDataMessage audioSegment = ThreadSafeMessagePool<AudioSegmentDataMessage>.Rent();
            audioSegment.Deserialize(reader);
            reader.Recycle();

            ServerAudioSegmentMessage serverAudio = new ServerAudioSegmentMessage
            {
                audioSegmentData = audioSegment,
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)peer.Id,
                },
            };

            // Serialize once, then send raw to each peer — skips N writer→packet copies.
            var writer = NetworkServer.RentWriter();
            serverAudio.Serialize(writer);
            int len = writer.Length;
            byte[] data = writer.Data;
            byte channel = BasisNetworkCommons.ShoutVoiceChannel;
            int senderId = peer.Id;

            var clients = NetworkServer.PeerSnapshot;
            for (int i = 0; i < clients.Length; i++)
            {
                NetPeer client = clients[i];
                if (client.Id != senderId)
                {
                    client.SendUnreliableRawMerge(data, 0, len, channel);
                    BasisNetworkStatistics.RecordOutbound(channel, len);
                }
            }

            NetworkServer.ReturnWriter(writer);
            ThreadSafeMessagePool<AudioSegmentDataMessage>.Return(audioSegment);
        }

        /// <summary>
        /// Broadcasts a shout mode state change to all clients via the AdminChannel.
        /// </summary>
        public static void BroadcastShoutModeState(ushort targetPlayerId, bool enabled)
        {
            var writer = NetworkServer.RentWriter();
            AdminRequestMode mode = enabled ? AdminRequestMode.EnableShoutMode : AdminRequestMode.DisableShoutMode;
            new AdminRequest().Serialize(writer, mode);
            writer.Put(targetPlayerId);

            NetPeer[] peers = NetworkServer.PeerSnapshot;
            foreach (var client in peers)
            {
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.AdminChannel, writer.Length);
                client.Send(writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }

            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Sends current shout mode states to a newly connected peer.
        /// </summary>
        public static void SendShoutStateToPeer(NetPeer newPeer)
        {
            int[] shoutPlayers = BasisSavedState.GetAllShoutModePlayers();
            if (shoutPlayers.Length == 0) return;

            var writer = NetworkServer.RentWriter();
            foreach (int peerId in shoutPlayers)
            {
                writer.Reset();
                new AdminRequest().Serialize(writer, AdminRequestMode.EnableShoutMode);
                writer.Put((ushort)peerId);
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.AdminChannel, writer.Length);
                newPeer.Send(writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            NetworkServer.ReturnWriter(writer);
        }

        public static void SendVoiceMessageToClients(ServerAudioSegmentMessage audioSegment, NetPeer sender, DeliveryMethod method)
        {
            if (!BasisSavedState.GetResolvedVoicePeers(sender, out List<NetPeer> targetPeers) || targetPeers.Count == 0)
            {
                return;
            }

            audioSegment.playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)sender.Id,
            };

            bool largeId = sender.Id > byte.MaxValue;
            byte channel = largeId ? BasisNetworkCommons.VoiceLargeChannel : BasisNetworkCommons.VoiceChannel;

            // Serialize once into a byte[], then send raw to each peer — skips N writer→packet copies.
            var writer = NetworkServer.RentWriter();
            audioSegment.Serialize(writer, largeId);
            int len = writer.Length;
            byte[] data = writer.Data;

            int count = targetPeers.Count;
            for (int i = 0; i < count; i++)
            {
                NetPeer client = targetPeers[i];
                client.SendUnreliableRawMerge(data, 0, len, channel);
                BasisNetworkStatistics.RecordOutbound(channel, len);
            }

            NetworkServer.ReturnWriter(writer);
        }
        public static void UpdateVoiceReceivers(NetPacketReader Reader, NetPeer Peer, bool largeCount)
        {
            VoiceReceiversMessage VoiceReceiversMessage = new VoiceReceiversMessage();
            VoiceReceiversMessage.Deserialize(Reader, largeCount);
            Reader.Recycle();
            BasisSavedState.AddLastData(Peer, VoiceReceiversMessage);
        }

        /// <summary>
        /// Inverted mode: the message contains IDs to EXCLUDE. Everyone else is a recipient.
        /// </summary>
        public static void UpdateVoiceReceiversInverted(NetPacketReader Reader, NetPeer Peer, bool largeCount)
        {
            VoiceReceiversMessage excluded = new VoiceReceiversMessage();
            excluded.Deserialize(Reader, largeCount);
            Reader.Recycle();

            int senderId = Peer.Id;
            var peers = BasisSavedState.GetOrCreateResolvedList(senderId);

            if (excluded.Users == null || excluded.UsersLength == 0)
            {
                // No exclusions: everyone except sender is a recipient
                foreach (var kvp in NetworkServer.AuthenticatedPeers)
                {
                    if (kvp.Key != senderId)
                        peers.Add(kvp.Value);
                }
            }
            else
            {
                // Reuse thread-local set to avoid allocation
                if (_excludedSet == null)
                    _excludedSet = new HashSet<int>(64);
                else
                    _excludedSet.Clear();
                for (int i = 0; i < excluded.UsersLength; i++)
                    _excludedSet.Add(excluded.Users[i]);

                foreach (var kvp in NetworkServer.AuthenticatedPeers)
                {
                    if (kvp.Key != senderId && !_excludedSet.Contains(kvp.Key))
                        peers.Add(kvp.Value);
                }
            }

            excluded.ReturnPool();
        }

        /// <summary>
        /// Bitfield mode: each set bit at position N means playerID N is a recipient.
        /// Wire format: [byteCount: ushort][bitfield bytes]
        /// </summary>
        public static void UpdateVoiceReceiversBitfield(NetPacketReader Reader, NetPeer Peer)
        {
            int senderId = Peer.Id;

            if (Reader.AvailableBytes < sizeof(ushort))
            {
                Reader.Recycle();
                return;
            }

            ushort byteCount = Reader.GetUShort();

            if (byteCount == 0 || Reader.AvailableBytes < byteCount)
            {
                Reader.Recycle();
                return;
            }

            var peers = BasisSavedState.GetOrCreateResolvedList(senderId);

            for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
            {
                byte b = Reader.GetByte();
                if (b == 0) continue;

                int baseId = byteIdx * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((b & (1 << bit)) != 0)
                    {
                        int playerId = baseId + bit;
                        if (playerId != senderId && NetworkServer.AuthenticatedPeers.TryGetValue(playerId, out NetPeer found))
                        {
                            peers.Add(found);
                        }
                    }
                }
            }

            Reader.Recycle();
        }
        #endregion

        #region Spawn and Client List Handling
        public static void SendRemoteSpawnMessage(NetPeer authClient, ReadyMessage readyMessage)
        {
            ServerReadyMessage serverReadyMessage = LoadInitialState(authClient, readyMessage);
            NotifyExistingClients(serverReadyMessage, authClient);
            SendClientListToNewClient(authClient);
        }

        public static ServerReadyMessage LoadInitialState(NetPeer authClient, ReadyMessage readyMessage)
        {
            ServerReadyMessage serverReadyMessage = new ServerReadyMessage
            {
                localReadyMessage = readyMessage,
                playerIdMessage = new PlayerIdMessage()
                {
                    playerID = (ushort)authClient.Id
                }
            };
            BasisServerReductionSystemEvents.AddMessage(authClient, readyMessage.localAvatarSyncMessage, 0);
            BasisSavedState.AddLastData(authClient, readyMessage);
            return serverReadyMessage;
        }
        /// <summary>
        /// notify existing clients about a new player
        /// </summary>
        /// <param name="serverSideSyncPlayerMessage"></param>
        /// <param name="authClient"></param>
        public static void NotifyExistingClients(ServerReadyMessage serverSideSyncPlayerMessage, NetPeer authClient)
        {
            NetDataWriter Writer = NetworkServer.RentWriter();
            serverSideSyncPlayerMessage.Serialize(Writer);
            NetPeer[] peers = NetworkServer.PeerSnapshot;
            //  BNL.LogError("Writing Data with size Size " + Writer.Length);
            if (NetworkServer.CheckValidated(Writer))
            {
                foreach (NetPeer client in peers)
                {
                    if (client != authClient)
                    {
                        client.Send(Writer, BasisNetworkCommons.CreateRemotePlayerChannel, DeliveryMethod.ReliableOrdered);
                        BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.CreateRemotePlayerChannel, Writer.Length);
                    }
                }
            }
            NetworkServer.ReturnWriter(Writer);
        }
        /// <summary>
        /// send everyone to the new client
        /// </summary>
        /// <param name="authClient"></param>
        public static void SendClientListToNewClient(NetPeer authClient)
        {
            try
            {
                NetPeer[] peers = NetworkServer.PeerSnapshot;
                NetDataWriter writer = NetworkServer.RentWriter();
                foreach (var peer in peers)
                {
                    if (peer == authClient)
                    {
                        continue;
                    }
                    writer.Reset();
                    if (CreateServerReadyMessageForPeer(peer, out ServerReadyMessage Message))
                    {
                        Message.Serialize(writer);
                        //  BNL.Log($"Writing Data with size {writer.Length}");
                        NetworkServer.TrySend(authClient, writer, BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, DeliveryMethod.ReliableOrdered);
                    }
                }
                NetworkServer.ReturnWriter(writer);
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to send client list: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private static bool CreateServerReadyMessageForPeer(NetPeer peer, out ServerReadyMessage ServerReadyMessage)
        {
            try
            {
                // Avatar Change State
                if (!BasisSavedState.GetLastAvatarChangeState(peer, out var changeState))
                {
                    BNL.LogError($"Unable to get avatar change state for peer {peer.Id}; skipping in client list.");
                    ServerReadyMessage = default;
                    return false;
                }
                if (changeState.byteArray == null)
                {
                    BNL.LogError($"Avatar state for peer {peer.Id} has null byteArray; skipping in client list.");
                    ServerReadyMessage = default;
                    return false;
                }

                int id = peer.Id;
                LocalAvatarSyncMessage syncState;
                if (BasisServerReductionSystemEvents.playerStates.TryGetValue(id, out PlayerState state))
                {
                    syncState = state.SyncMessage.avatarSerialization;
                }
                else
                {
                    syncState = new LocalAvatarSyncMessage
                    {
                        DataQualityLevel = (byte)Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality.High,
                        array = new byte[NetworkServer.HighQualityLength],
                        AdditionalAvatarDatas = null,
                        AdditionalAvatarDataSize = 0,
                        LinkedAvatarIndex = 0
                    };
                    // Optionally log fallback
                    // BNL.LogError("Unable to get Last Player Avatar Data! Using Error Fallback");
                }
                // Meta Data
                if (!BasisSavedState.GetLastPlayerMetaData(peer, out var metaData))
                {
                    metaData = new ClientMetaDataMessage
                    {
                        playerDisplayName = "Error",
                        playerUUID = string.Empty,
                        playerPlatform = string.Empty
                    };
                    BNL.LogError("Unable to get Last Player Meta Data! Using Error Fallback");
                }

                // Construct ServerReadyMessage
                ServerReadyMessage = new ServerReadyMessage
                {
                    localReadyMessage = new ReadyMessage
                    {
                        localAvatarSyncMessage = syncState,
                        clientAvatarChangeMessage = changeState,
                        playerMetaDataMessage = metaData
                    },
                    playerIdMessage = new PlayerIdMessage
                    {
                        playerID = (ushort)peer.Id
                    }
                };

                return true;
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to create ServerReadyMessage for peer {peer.Id}: {ex.Message}");
                ServerReadyMessage = new ServerReadyMessage();
                return false;
            }
        }
        #endregion
        #region Network ID Generation
        public static void NetIDAssign(NetPacketReader Reader, NetPeer Peer)
        {
            NetIDMessage ServerUniqueIDMessage = new NetIDMessage();
            ServerUniqueIDMessage.Deserialize(Reader);
            Reader.Recycle();
            //returns a message with the ushort back to the client, or it sends it to everyone if its new.
            BasisNetworkIDDatabase.AddOrFindNetworkID(Peer, ServerUniqueIDMessage.playerID);
            //we need to convert the string int a  ushort.
        }
        public static void LoadResource(NetPacketReader Reader, NetPeer Peer,string UUID)
        {
            LocalLoadResource LocalLoadResource = new LocalLoadResource();

            if (NetworkServer.AuthIdentity.NetIDToUUID(Peer, out string uuid) == false)
            {
                BNL.LogError($"User UUID not found for peer: {Peer}");
                return;
            }
            LocalLoadResource.Deserialize(Reader);
            LocalLoadResource.IsAdminLocked = PermissionIntegration.HasValidRequirement(Peer, PermNodes.protection);
            LocalLoadResource.UUIDOfCreator = UUID;
            Reader.Recycle();

            bool isAdmin = PermissionIntegration.HasValidRequirement(UUID, PermNodes.All);

            switch (LocalLoadResource.Mode)
            {
                case 0:
                    if (!isAdmin && BasisNetworkServer.Security.BasisGlobalLockManager.PropsLocked)
                    {
                        BNL.Log($"Prop loading is globally disabled. Rejected request from {UUID}");
                        BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "Prop loading is currently disabled by an admin.");
                        return;
                    }
                    if (PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLoadProp) == false)
                    {
                        BNL.LogError($"Invalid Request To Load Gameobject From {UUID}");
                        return;
                    }
                    break;
                case 1:
                    if (!isAdmin && BasisNetworkServer.Security.BasisGlobalLockManager.WorldsLocked)
                    {
                        BNL.Log($"World loading is globally disabled. Rejected request from {UUID}");
                        BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(Peer, "World loading is currently disabled by an admin.");
                        return;
                    }
                    if (PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLoadWorld) == false)
                    {
                        BNL.LogError($"Invalid Request To Load Scene From {UUID}");
                        return;
                    }
                    break;
                default:
                    BNL.LogError($"Missing Mode {LocalLoadResource.Mode}");
                    break;
            }
            // Route based on load strategy
            switch (LocalLoadResource.LoadStrategy)
            {
                case 0:
                    BasisNetworkResourceManagement.LoadResource(LocalLoadResource);
                    break;
                case 2: // Synchronized
                    BasisNetworkPreloadResourceManagement.StartSynchronizedLoad(LocalLoadResource);
                    break;
                default:
                    BNL.LogError("Falling Back to Resource Load, Unsupport Load Strategy");
                    BasisNetworkResourceManagement.LoadResource(LocalLoadResource);
                    break;
            }
        }
        public static void HandlePreloadReady(NetPacketReader Reader, NetPeer Peer)
        {
            PreloadReadyMessage readyMsg = new PreloadReadyMessage();
            readyMsg.Deserialize(Reader);
            Reader.Recycle();
            BasisNetworkPreloadResourceManagement.HandleClientReady(readyMsg.LoadedNetID, Peer.Id, readyMsg.IsReady);
        }
        public static void UnloadResource(NetPacketReader Reader, NetPeer Peer)
        {
            UnLoadResource UnLoadResource = new UnLoadResource();
            UnLoadResource.Deserialize(Reader);
            Reader.Recycle();

            switch (UnLoadResource.Mode)
            {
                case 0:
                    if (PermissionIntegration.HasValidRequirement(Peer, PermNodes.ResourceUnloadProp) == false)
                    {
                        return;
                    }
                    break;
                case 1:
                    if (PermissionIntegration.HasValidRequirement(Peer, PermNodes.ResourceUnloadWorld) == false)
                    {
                        return;
                    }
                    break;
                default:
                    BNL.LogError($"Missing Mode {UnLoadResource.Mode}");
                    break;
            }

            //returns a message with the ushort back to the client, or it sends it to everyone if its new.
            BasisNetworkResourceManagement.UnloadResource(UnLoadResource, Peer);
            //we need to convert the string int a  ushort.
        }
        #endregion
        public static void HandleStoreDatabase(NetPacketReader reader, NetPeer peer)
        {
            if (NetworkServer.Configuration.DisableWriteUnlessAdminPersistentFlag)
            {
                if (PermissionIntegration.HasValidRequirement(peer, PermNodes.ConfigurationEditor))
                {
                    return;
                }
            }
            var dataMessage = new DatabasePrimativeMessage();
            dataMessage.Deserialize(reader);
            reader.Recycle();

            var basisData = new BasisData(dataMessage.Name, dataMessage.jsonPayload);
            BasisPersistentDatabase.AddOrUpdate(basisData);
        }

        public static void HandleRequestStoreDatabase(NetPacketReader reader, NetPeer peer)
        {
            if(NetworkServer.Configuration.DisableReadUnlessAdminPersistentFlag)
            {
                if(!PermissionIntegration.HasValidRequirement(peer, PermNodes.ConfigurationEditor))
                {
                    return;
                }
            }
            var dataRequest = new DataBaseRequest();
            dataRequest.Deserialize(reader);
            reader.Recycle();
            if (!BasisPersistentDatabase.GetByName(dataRequest.DatabaseID, out var db))
            {
                db = new BasisData(dataRequest.DatabaseID, new System.Collections.Concurrent.ConcurrentDictionary<string, object>());
                BasisPersistentDatabase.AddOrUpdate(db);
            }

            var msg = new DatabasePrimativeMessage
            {
                Name = db.Name,
                jsonPayload = db.JsonPayload
            };

            var writer = NetworkServer.RentWriter();
            msg.Serialize(writer);
            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.StoreDatabaseChannel, writer.Length);
            peer.Send(writer, BasisNetworkCommons.StoreDatabaseChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }
    }
}
