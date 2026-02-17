using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkCore;
using BasisNetworkCore.Pooling;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static BasisNetworkCore.Serializable.SerializableBasis;
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

                NetworkServer.AuthIdentity.RemoveConnection(id);
                BasisNetworkOwnership.RemovePlayerOwnership(id);
                BasisSavedState.RemovePlayer(id);
                BasisServerReductionSystemEvents.RemovePlayer(id);

                if (NetworkServer.AuthenticatedPeers.TryRemove(id, out _))
                {
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
                }

                NetDataWriter writer = new NetDataWriter(true, sizeof(ushort));
                writer.Put((ushort)id);
                if (NetworkServer.CheckValidated(writer))
                {
                    NetPeer[] Peers = NetworkServer.AuthenticatedPeers.Values.ToArray();
                    foreach (var client in Peers)
                    {
                        if (client.Id != id)
                        {
                            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.DisconnectionChannel, writer.Length);
                            client.Send(writer, BasisNetworkCommons.DisconnectionChannel, DeliveryMethod.ReliableOrdered);
                        }
                    }
                }
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
            NetDataWriter writer = new NetDataWriter(true, 2);
            writer.Put(reason);
            request.Reject(writer);
            BNL.LogError($"Rejected for reason: {reason}");
        }
        public static void RejectWithReason(NetPeer request, string reason)
        {
            ushort Id =(ushort)request.Id;
            NetDataWriter writer = new NetDataWriter(true, 2);
            writer.Put(reason);
            NetworkServer.AuthenticatedPeers.TryRemove(Id, out _);
            request.Disconnect();
            BNL.LogError($"Rejected after accept with reason: {reason}");
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
                BNL.Log($"Peer connected: {newPeer.Id}");
                //never ever assume the UUID provided by the user is good always recalc on the server.
                //this means that as long as they pass auth but locally have a bad UUID that only they locally are effected.
                //there is no way to force a user locally to be a certain UUID, thats not how the internet works.
                //instead we can make sure all additional clients have them correct.
                //this only occurs if the server is doing Auth checks.
                ReadyMessage.playerMetaDataMessage.playerUUID = UUID;

               Configuration Config = NetworkServer.Configuration;
                //lets dump to the local client there data after the server has had its way
                ServerMetaDataMessage ServerMetaDataMessage = new ServerMetaDataMessage
                {
                    ClientMetaDataMessage = ReadyMessage.playerMetaDataMessage,
                    SyncInterval = Config.BSRSMillisecondDefaultInterval,
                    BaseMultiplier = Config.BSRBaseMultiplier,
                    IncreaseRate = Config.BSRSIncreaseRate,
                    SlowestSendRate = Config.BSRSlowestSendRate,
                };

                NetDataWriter Writer = new NetDataWriter(true, 4);
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

                SendRemoteSpawnMessage(newPeer, ReadyMessage);

                BasisNetworkResourceManagement.SendOutAllResources(newPeer);
                BasisNetworkOwnership.SendOutOwnershipInformation(newPeer);
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
            NetDataWriter Writer = new NetDataWriter(true, 4);
            serverAvatarChangeMessage.Serialize(Writer);

            NetPeer[] allPeers = NetworkServer.AuthenticatedPeers.Values.ToArray();
            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AvatarChangeMessageChannel, Peer, allPeers, DeliveryMethod.ReliableOrdered);
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

            SendVoiceMessageToClients(serverAudio, BasisNetworkCommons.VoiceChannel, peer, DeliveryMethod.Sequenced);

            ThreadSafeMessagePool<AudioSegmentDataMessage>.Return(audioSegment);
        }

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
                AdditionalData = 0
            };

            var writer = new NetDataWriter(true, 3);
            audioSegment.Serialize(writer);

            NetworkServer.BroadcastMessageToClients(writer, channel, ref targetPeers, method,1024);
        }

        private static List<NetPeer> GetTargetPeers(VoiceReceiversMessage Message)
        {
            List<NetPeer> peers = new List<NetPeer>(Message.Users.Length);
            foreach (ushort userId in Message.Users)
            {
                if (NetworkServer.AuthenticatedPeers.TryGetValue(userId, out NetPeer found))
                {
                    peers.Add(found);
                }
                else
                {
                    BNL.LogError($"[VoiceMessage] Could not find peer with ID: {userId}");
                }
            }

            return peers;
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
            BasisServerReductionSystemEvents.AddMessage(authClient, readyMessage.localAvatarSyncMessage);
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
            NetDataWriter Writer = new NetDataWriter(true);
            serverSideSyncPlayerMessage.Serialize(Writer);
            NetPeer[] peers = NetworkServer.AuthenticatedPeers.Values.ToArray();
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
        }
        /// <summary>
        /// send everyone to the new client
        /// </summary>
        /// <param name="authClient"></param>
        public static void SendClientListToNewClient(NetPeer authClient)
        {
            try
            {
                // Fetch all peers into an array (up to 1024)
                NetPeer[] peers = NetworkServer.AuthenticatedPeers.Values.ToArray();
                NetDataWriter writer = new NetDataWriter(true, 2);
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
                        playerUUID = string.Empty
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
            BasisNetworkIDDatabase.AddOrFindNetworkID(Peer, ServerUniqueIDMessage.UniqueID);
            //we need to convert the string int a  ushort.
        }
        public static void LoadResource(NetPacketReader Reader, NetPeer Peer)
        {
            LocalLoadResource LocalLoadResource = new LocalLoadResource();
            LocalLoadResource.Deserialize(Reader);
            Reader.Recycle();
            //returns a message with the ushort back to the client, or it sends it to everyone if its new.
            BasisNetworkResourceManagement.LoadResource(LocalLoadResource);
            //we need to convert the string int a  ushort.
        }
        public static void UnloadResource(NetPacketReader Reader, NetPeer Peer)
        {
            UnLoadResource UnLoadResource = new UnLoadResource();
            UnLoadResource.Deserialize(Reader);
            Reader.Recycle();
            //returns a message with the ushort back to the client, or it sends it to everyone if its new.
            BasisNetworkResourceManagement.UnloadResource(UnLoadResource);
            //we need to convert the string int a  ushort.
        }
        #endregion
        public static void HandleStoreDatabase(NetPacketReader reader, NetPeer peer)
        {
            if (NetworkServer.Configuration.DisableReadUnlessAdminPersistentFlag)
            {
                if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) == false)
                {
                    BNL.LogError($"User UUID not found for peer: {peer}");
                    return;
                }

                if (NetworkServer.AuthIdentity.IsNetPeerAdmin(uuid) == false)
                {
                    BNL.LogError($"Unauthorized admin access attempt by UUID: {uuid}");
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
            if(NetworkServer.Configuration.DisableWriteUnlessAdminPersistentFlag)
            {
                if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid) == false)
                {
                    BNL.LogError($"User UUID not found for peer: {peer}");
                    return;
                }

                if (NetworkServer.AuthIdentity.IsNetPeerAdmin(uuid) == false)
                {
                    BNL.LogError($"Unauthorized admin access attempt by UUID: {uuid}");
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

            var writer = new NetDataWriter(true);
            msg.Serialize(writer);
            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.StoreDatabaseChannel, writer.Length);
            peer.Send(writer, BasisNetworkCommons.StoreDatabaseChannel, DeliveryMethod.ReliableOrdered);
        }
    }
}
