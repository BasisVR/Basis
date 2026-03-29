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
            ushort Id =(ushort)request.Id;
            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(reason);
            NetworkServer.AuthenticatedPeers.TryRemove(Id, out _);
            request.Disconnect();
            NetworkServer.ReturnWriter(writer);
            BNL.LogError($"Rejected after accept with reason: {reason}");
        }
        #endregion

        #region Connection Handling
        public static void HandleConnectionRequest(ConnectionRequest ConReq)
        {
            try
            {
                if (BasisPlayerModeration.IsIpBanned(ConReq.Identity))
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
                NetPeer newPeer = ConReq.Accept();//can do both way Communication from here on

                if (NetworkServer.Configuration.UseAuthIdentity)
                {
                    NetworkServer.AuthIdentity.ProcessConnection(NetworkServer.Configuration, ConReq, newPeer);
                }
                else
                {
                    ReadyMessage readyMessage = new ReadyMessage();
                    readyMessage.Deserialize(ConReq.Data);

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

            SendVoiceMessageToClients(serverAudio, BasisNetworkCommons.VoiceChannel, peer, DeliveryMethod.Unreliable);

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

            var writer = NetworkServer.RentWriter();
            serverAudio.Serialize(writer);

            NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.ShoutVoiceChannel, peer, NetworkServer.PeerSnapshot, DeliveryMethod.Unreliable);

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

        [ThreadStatic] private static List<NetPeer> _voicePeerList;

        public static void SendVoiceMessageToClients(ServerAudioSegmentMessage audioSegment, byte channel, NetPeer sender, DeliveryMethod method)
        {
            if (BasisSavedState.GetLastVoiceReceivers(sender, out VoiceReceiversMessage receivers))
            {
            }
            else
            {
                BNL.Log($"[VoiceMessage] No receivers found for sender {sender.Id}.");
            }
            if (receivers.Users == null || receivers.Users.Length == 0)
            {
                BNL.Log($"[VoiceMessage] No users found for {sender.Id}.");
                return;
            }

            var targetPeers = GetTargetPeers(receivers);
            if (targetPeers.Count == 0)
            {
                BNL.Log($"[VoiceMessage] No valid peer matches found for sender {sender.Id}.");
                return;
            }

            audioSegment.playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)sender.Id,
            };

            var writer = NetworkServer.RentWriter();

            audioSegment.Serialize(writer);

            NetworkServer.BroadcastMessageToClients(writer, channel, ref targetPeers, method, 1024);

            NetworkServer.ReturnWriter(writer);
        }

        private static List<NetPeer> GetTargetPeers(VoiceReceiversMessage Message)
        {
            if (_voicePeerList == null)
                _voicePeerList = new List<NetPeer>(64);
            else
                _voicePeerList.Clear();

            foreach (ushort userId in Message.Users)
            {
                if (NetworkServer.AuthenticatedPeers.TryGetValue(userId, out NetPeer found))
                {
                    _voicePeerList.Add(found);
                }
                else
                {
                    BNL.LogError($"[VoiceMessage] Could not find peer with ID: {userId}");
                }
            }

            return _voicePeerList;
        }
        public static void UpdateVoiceReceivers(NetPacketReader Reader, NetPeer Peer)
        {
            VoiceReceiversMessage VoiceReceiversMessage = new VoiceReceiversMessage();
            VoiceReceiversMessage.Deserialize(Reader);
            Reader.Recycle();
            BasisSavedState.AddLastData(Peer, VoiceReceiversMessage);
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
                    changeState = new ClientAvatarChangeMessage();
                    BNL.LogError("Unable to get avatar Change Request!");
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

            switch (LocalLoadResource.Mode)
            {
                case 0:
                    if (PermissionIntegration.HasValidRequirement(UUID, PermNodes.ResourceLoadProp) == false)
                    {
                        BNL.LogError($"Invalid Request To Load Gameobject From {UUID}");
                        return;
                    }
                    break;
                case 1:
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
