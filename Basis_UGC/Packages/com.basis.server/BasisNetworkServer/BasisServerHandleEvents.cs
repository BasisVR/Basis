using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkCore;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkMessageProcessor;
using BasisNetworkServer.Security;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
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
            NetworkServer.listener.ConnectionRequestEvent += HandleConnectionRequest;
            NetworkServer.listener.PeerDisconnectedEvent += HandlePeerDisconnected;
            NetworkServer.listener.NetworkReceiveEvent += BasisNetworkMessageProcessor.Enqueue;
            NetworkServer.listener.NetworkErrorEvent += OnNetworkError;
        }

        public static void UnsubscribeServerEvents()
        {
            NetworkServer.listener.ConnectionRequestEvent -= HandleConnectionRequest;
            NetworkServer.listener.PeerDisconnectedEvent -= HandlePeerDisconnected;
            NetworkServer.listener.NetworkReceiveEvent -= BasisNetworkMessageProcessor.Enqueue;
            NetworkServer.listener.NetworkErrorEvent -= OnNetworkError;
        }

        public static void StopWorker()
        {
            NetworkServer.server?.Stop();
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
                ushort id = (ushort)peer.Id;
                ClientDisconnect(id);

                BasisPlayerArray.RemovePlayer(peer);
                if (NetworkServer.Peers.TryRemove(id, out _))
                {
                    BNL.Log($"Peer removed: {id}");
                }
                else
                {
                    BNL.LogError($"Failed to remove peer: {id}");
                }
                NetworkServer.authIdentity.RemoveConnection(peer);
                NetworkServer.chunkedNetPeerArray.SetPeer(id, null);
                CleanupPlayerData(id, peer);
            }
            catch (Exception e)
            {
                BNL.LogError(e.Message + " " + e.StackTrace);
            }
        }

        public static void CleanupPlayerData(ushort id, NetPeer peer)
        {
            BasisNetworkOwnership.RemovePlayerOwnership(id);
            BasisSavedState.RemovePlayer(peer);
            BasisServerReductionSystem.RemovePlayer(peer);
            if (NetworkServer.Peers.IsEmpty)
            {
                BasisNetworkIDDatabase.Reset();
                BasisNetworkResourceManagement.Reset();
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
            NetDataWriter writer = new NetDataWriter(true, 2);
            writer.Put(reason);
            NetworkServer.Peers.TryRemove((ushort)request.Id, out _);
            BasisPlayerArray.RemovePlayer(request);
            request.Disconnect();
            BNL.LogError($"Rejected after accept with reason: {reason}");
        }
        public static void ClientDisconnect(ushort leaving)
        {
            NetDataWriter writer = new NetDataWriter(true, sizeof(ushort));
            writer.Put(leaving);

            if (NetworkServer.CheckValidated(writer))
            {
                ReadOnlySpan<NetPeer> Peers = BasisPlayerArray.GetSnapshot();
                foreach (var client in Peers)
                {
                    if (client.Id != leaving)
                    {
                        client.Send(writer, BasisNetworkCommons.Disconnection, DeliveryMethod.ReliableOrdered);
                    }
                }
            }
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
                BNL.Log("Processing Connection Request");
                int ServerCount = NetworkServer.server.ConnectedPeersCount;

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
                    authMessage.Deserialize(ConReq.Data,out byte[] AuthBytes);
                    if (NetworkServer.auth.IsAuthenticated(AuthBytes) == false)
                    {
                        RejectWithReason(ConReq, "Authentication failed, Auth rejected");
                        return;
                    }
                }
                else
                {
                    //we still want to read the data to move the needle along
                    BytesMessage authMessage = new BytesMessage();
                    authMessage.Deserialize(ConReq.Data,out byte[] UnusedBytes);
                }
                NetPeer newPeer = ConReq.Accept();//can do both way Communication from here on

                if (NetworkServer.Configuration.UseAuthIdentity)
                {
                    NetworkServer.authIdentity.ProcessConnection(NetworkServer.Configuration, ConReq, newPeer);
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
            if (NetworkServer.Peers.TryAdd(PeerId, newPeer))
            {
                NetworkServer.chunkedNetPeerArray.SetPeer(PeerId, newPeer);
                BasisPlayerArray.AddPlayer(newPeer);
                BNL.Log($"Peer connected: {newPeer.Id}");
                //never ever assume the UUID provided by the user is good always recalc on the server.
                //this means that as long as they pass auth but locally have a bad UUID that only they locally are effected.
                //there is no way to force a user locally to be a certain UUID, thats not how the internet works.
                //instead we can make sure all additional clients have them correct.
                //this only occurs if the server is doing Auth checks.
                ReadyMessage.playerMetaDataMessage.playerUUID = UUID;

                if (BasisNetworkIDDatabase.GetAllNetworkID(out List<ServerNetIDMessage> ServerNetIDMessages))
                {
                    ServerUniqueIDMessages ServerUniqueIDMessageArray = new ServerUniqueIDMessages
                    {
                        Messages = ServerNetIDMessages.ToArray(),
                    };

                    NetDataWriter Writer = new NetDataWriter(true, 4);
                    ServerUniqueIDMessageArray.Serialize(Writer);
                    BNL.Log($"Sending out Network Id Count " + ServerUniqueIDMessageArray.Messages.Length);
                    NetworkServer.SendOutValidated(newPeer, Writer, BasisNetworkCommons.NetIDAssigns, LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
                else
                {
                    BNL.Log($"No Network Ids Not Sending out");
                }

                SendRemoteSpawnMessage(newPeer, ReadyMessage);

                BasisNetworkResourceManagement.SendOutAllResources(newPeer);
            }
            else
            {
                RejectWithReason(newPeer, "Peer already exists.");
            }
        }
        #endregion

        #region Network Receive Handlers
        public static bool ValidateSize(NetPacketReader reader, NetPeer peer,byte channel )
        {
            if (reader.AvailableBytes == 0)
            {
                BNL.LogError($"Missing Data from peer! {peer.Id} with channel ID {channel}");
                reader.Recycle();
                return false;
            }
            return true;
        }
        #endregion
        // Define the delegate type
        public delegate void AuthEventHandler(NetPacketReader reader, NetPeer peer);

        // Declare an event of the delegate type
        public static event AuthEventHandler OnAuthReceived;
        public static void HandleAuth(NetPacketReader Reader, NetPeer Peer)
        {
            OnAuthReceived?.Invoke(Reader, Peer);
        }
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
            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AvatarChangeMessage, Peer, BasisPlayerArray.GetSnapshot());
        }
        public static void HandleAvatarMovement(NetPacketReader Reader, NetPeer Peer)
        {
            LocalAvatarSyncMessage LocalAvatarSyncMessage = new LocalAvatarSyncMessage();
            LocalAvatarSyncMessage.Deserialize(Reader);
            Reader.Recycle();
            BasisSavedState.AddLastData(Peer, LocalAvatarSyncMessage);
            ReadOnlySpan<NetPeer> Peers = BasisPlayerArray.GetSnapshot();
            foreach (NetPeer client in Peers)
            {
                if (client.Id == Peer.Id)
                {
                    continue;
                }
                ServerSideSyncPlayerMessage ssspm = CreateServerSideSyncPlayerMessage(LocalAvatarSyncMessage, (ushort)Peer.Id);
                BasisServerReductionSystem.AddOrUpdatePlayer(client, ssspm, Peer);
            }
        }

        public static ServerSideSyncPlayerMessage CreateServerSideSyncPlayerMessage(LocalAvatarSyncMessage local, ushort clientId)
        {
            return new ServerSideSyncPlayerMessage
            {
                playerIdMessage = new PlayerIdMessage { playerID = clientId },
                avatarSerialization = local
            };
        }

        public static void HandleVoiceMessage(NetPacketReader Reader, NetPeer peer)
        {
            /*
            byte sequenceNumber = Reader.GetByte();
            if (sequenceNumber > 63)
            {
                BNL.LogError("Sequence Number was greater the 63!");
                sequenceNumber = 0;
            }
            */
            AudioSegmentDataMessage audioSegment = ThreadSafeMessagePool<AudioSegmentDataMessage>.Rent();
            audioSegment.Deserialize(Reader);
            Reader.Recycle();
            ServerAudioSegmentMessage ServerAudio = new ServerAudioSegmentMessage
            {
                audioSegmentData = audioSegment
            };
            SendVoiceMessageToClients(ServerAudio, BasisNetworkCommons.VoiceChannel, peer);
            ThreadSafeMessagePool<AudioSegmentDataMessage>.Return(audioSegment);
        }
        public static void SendVoiceMessageToClients(ServerAudioSegmentMessage audioSegment, byte channel, NetPeer sender)//byte sequenceNumber
        {
            if (BasisSavedState.GetLastVoiceReceivers(sender, out VoiceReceiversMessage data))
            {
                // If no users are found or the array is empty, return early
                if (data.users == null || data.users.Length == 0)
                {
                    return;
                }
                int length = data.users.Length;
                // Get the current snapshot of all peers
                ReadOnlySpan<NetPeer> AllPeers = BasisPlayerArray.GetSnapshot();
                int AllPeersLength = AllPeers.Length;
                // Select valid clients based on the users list and corresponding NetPeer
                List<NetPeer> endPoints = new List<NetPeer>(length);

                for (int DataIndex = 0; DataIndex < length; DataIndex++)
                {
                    // Find the NetPeer corresponding to the user
                    NetPeer matchingPeer = null;

                    for (int PeerIndex = 0; PeerIndex < AllPeersLength; PeerIndex++)
                    {
                        if (AllPeers[PeerIndex].Id == data.users[DataIndex])
                        {
                            matchingPeer = AllPeers[PeerIndex];
                            break;  // Found the peer, exit inner loop
                        }
                    }

                    // If a matching peer was found, add it to the endPoints list
                    if (matchingPeer != null)
                    {
                        endPoints.Add(matchingPeer);
                    }
                }

                // If no valid endpoints were found, return early
                if (endPoints.Count == 0)
                {
                    return;
                }

                // Add player ID to the audio segment message
                audioSegment.playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)sender.Id,
                    AdditionalData = 0,
                };

                // Serialize the audio segment message
                NetDataWriter NetDataWriter = new NetDataWriter(true, 2);
                audioSegment.Serialize(NetDataWriter);

                // Broadcast the message to the clients
                NetworkServer.BroadcastMessageToClients(NetDataWriter, channel, ref endPoints, DeliveryMethod.Sequenced);
            }
            else
            {
                // Log error if unable to find the sender in the data store
                BNL.Log("Error unable to find " + sender.Id + " in the data store!");
            }
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
            ReadOnlySpan<NetPeer> Peers = BasisPlayerArray.GetSnapshot();
            //  BNL.LogError("Writing Data with size Size " + Writer.Length);
            if (NetworkServer.CheckValidated(Writer))
            {
                foreach (NetPeer client in Peers)
                {
                    if (client != authClient)
                    {
                        client.Send(Writer, BasisNetworkCommons.CreateRemotePlayer, DeliveryMethod.ReliableOrdered);
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
                ReadOnlySpan<NetPeer> peers = BasisPlayerArray.GetSnapshot();
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
                        NetworkServer.SendOutValidated(authClient, writer, BasisNetworkCommons.CreateRemotePlayersForNewPeer, LiteNetLib.DeliveryMethod.ReliableOrdered);
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
                if (!BasisSavedState.GetLastAvatarChangeState(peer, out var changeState))
                {
                    changeState = new ClientAvatarChangeMessage();
                    BNL.LogError("Unable to get avatar Change Request!");
                }

                if (!BasisSavedState.GetLastAvatarSyncState(peer, out var syncState))
                {
                    syncState = new LocalAvatarSyncMessage() { array = new byte[386], AdditionalAvatarDatas = null };
                    BNL.LogError("Unable to get Last Player Avatar Data! Using Error Fallback");
                }

                if (!BasisSavedState.GetLastPlayerMetaData(peer, out var metaData))
                {
                    metaData = new PlayerMetaDataMessage() { playerDisplayName = "Error", playerUUID = string.Empty };
                    BNL.LogError("Unable to get Last Player Meta Data! Using Error Fallback");
                }
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
                    },
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
        public static void netIDAssign(NetPacketReader Reader, NetPeer Peer)
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
    }
}
