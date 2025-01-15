using System;
using System.Net.Sockets;
using System.Net;
using LiteNetLib;
using BasisNetworkCore;
using LiteNetLib.Utils;
using Basis.Network.Core;
using static Basis.Network.Core.Serializable.SerializableBasis;
using Basis.Network.Server.Ownership;
using Basis.Network.Server.Generic;
using static SerializableBasis;
using System.Collections.Generic;
using System.Threading.Tasks;
using static BasisNetworkServer;

namespace BasisServerHandle
{
    public static class BasisServerHandleEvents 
    {
        #region Server Events Setup
        public static void SubscribeServerEvents()
        {
            BasisNetworkServer.listener.ConnectionRequestEvent += HandleConnectionRequest;
            BasisNetworkServer.listener.PeerDisconnectedEvent += HandlePeerDisconnected;
            BasisNetworkServer.listener.NetworkReceiveEvent += HandleNetworkReceiveEvent;
            BasisNetworkServer.listener.NetworkErrorEvent += OnNetworkError;
        }

        public static void UnsubscribeServerEvents()
        {
            BasisNetworkServer.listener.ConnectionRequestEvent -= HandleConnectionRequest;
            BasisNetworkServer.listener.PeerDisconnectedEvent -= HandlePeerDisconnected;
            BasisNetworkServer.listener.NetworkReceiveEvent -= HandleNetworkReceiveEvent;
            BasisNetworkServer.listener.NetworkErrorEvent -= OnNetworkError;
        }

        public static void StopWorker()
        {
            BasisNetworkServer.server?.Stop();
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
                if (BasisNetworkServer.Peers.TryRemove(id, out _))
                {
                    BNL.Log($"Peer removed: {id}");
                }
                else
                {
                    BNL.LogError($"Failed to remove peer: {id}");
                }
                chunkedNetPeerArray.SetPeer(id,null);
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
        }
        #endregion

        #region Utility Methods
        private static void RejectWithReason(ConnectionRequest request, string reason)
        {
            NetDataWriter writer = new NetDataWriter(true, 2);
            writer.Put(reason);
            request.Reject(writer);
            BNL.LogError($"Rejected: {reason}");
        }

        public static void ClientDisconnect(ushort leaving)
        {
            NetDataWriter writer = new NetDataWriter(true,sizeof(ushort));
            writer.Put(leaving);

           ReadOnlySpan<NetPeer> Peers = BasisPlayerArray.GetSnapshot();
            foreach (var client in Peers)
            {
                if (client.Id != leaving)
                {
                    client.Send(writer, BasisNetworkCommons.Disconnection, DeliveryMethod.ReliableOrdered);
                }
            }
        }
        #endregion

        #region Connection Handling
        public static void HandleConnectionRequest(ConnectionRequest request)
        {
            try
            {
                BNL.Log("Processing Connection Request");
                int ServerCount = BasisNetworkServer.server.ConnectedPeersCount;

                if (ServerCount >= BasisNetworkServer.Configuration.PeerLimit)
                {
                    RejectWithReason(request, "Server is full! Rejected.");
                    return;
                }

                if (!request.Data.TryGetUShort(out ushort ClientVersion))
                {
                    RejectWithReason(request, "Invalid client data.");
                    return;
                }

                if (ClientVersion < BasisNetworkVersion.ServerVersion)
                {
                    RejectWithReason(request, "Outdated client version.");
                    return;
                }

                AuthenticationMessage authMessage = new AuthenticationMessage();
                authMessage.Deserialize(request.Data);

                if (BasisNetworkServer.auth.IsAuthenticated(authMessage) == false)
                {
                    RejectWithReason(request, "Authentication failed, password rejected");
                    return;
                }

                BNL.Log("Player approved. Current count: " + ServerCount);

                NetPeer newPeer = request.Accept();
                ushort PeerId = (ushort)newPeer.Id;
                if (BasisNetworkServer.Peers.TryAdd(PeerId, newPeer))
                {
                    chunkedNetPeerArray.SetPeer(PeerId, newPeer);
                    BasisPlayerArray.AddPlayer(newPeer);
                    BNL.Log($"Peer connected: {newPeer.Id}");
                    ReadyMessage readyMessage = new ReadyMessage();
                    readyMessage.Deserialize(request.Data,false);
                    if (readyMessage.WasDeserializedCorrectly())
                    {
                        SendRemoteSpawnMessage(newPeer, readyMessage);
                    }
                    else
                    {
                        BasisNetworkServer.Peers.Remove(PeerId, out _);
                        BasisPlayerArray.RemovePlayer(newPeer);
                        RejectWithReason(request, "Payload Provided was invalid!");
                    }
                }
                else
                {
                    RejectWithReason(request, "Peer already exists.");
                }
            }
            catch (Exception e)
            {
                RejectWithReason(request, "Fatal Connection Issue stacktrace on server " + e.Message);
                BNL.LogError(e.StackTrace);
            }
        }
        #endregion

        #region Network Receive Handlers
        private static void HandleNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                var task = Task.Run(() =>
                {
                    try
                    {
                        switch (channel)
                        {
                            case BasisNetworkCommons.FallChannel:
                                if (deliveryMethod == DeliveryMethod.Unreliable)
                                {
                                    if (reader.TryGetByte(out byte Byte))
                                    {
                                        HandleNetworkReceiveEvent(peer, reader, Byte, deliveryMethod);
                                    }
                                    else
                                    {
                                        BNL.LogError($"Unknown channel no data remains: {channel} " + reader.AvailableBytes);
                                        reader.Recycle();
                                    }
                                }
                                else
                                {
                                    BNL.LogError($"Unknown channel: {channel} " + reader.AvailableBytes);
                                    reader.Recycle();
                                }
                                break;
                            case BasisNetworkCommons.MovementChannel:
                                HandleAvatarMovement(reader, peer);
                                break;
                            case BasisNetworkCommons.VoiceChannel:
                                HandleVoiceMessage(reader, peer);
                                break;
                            case BasisNetworkCommons.AvatarChannel:
                                BasisNetworkingGeneric.HandleAvatar(reader, deliveryMethod, peer);
                                break;
                            case BasisNetworkCommons.SceneChannel:
                                BasisNetworkingGeneric.HandleScene(reader, deliveryMethod, peer);
                                break;
                            case BasisNetworkCommons.AvatarChangeMessage:
                                SendAvatarMessageToClients(reader, peer);
                                break;
                            case BasisNetworkCommons.OwnershipTransfer:
                                BasisNetworkOwnership.OwnershipTransfer(reader, peer);
                                break;
                            case BasisNetworkCommons.OwnershipResponse:
                                BasisNetworkOwnership.OwnershipResponse(reader, peer);
                                break;
                            case BasisNetworkCommons.AudioRecipients:
                                UpdateVoiceReceivers(reader, peer);
                                break;
                            default:
                                BNL.LogError($"Unknown channel: {channel} " + reader.AvailableBytes);
                                reader.Recycle();
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        BNL.LogError($"{e.Message} : {e.StackTrace}");
                        reader?.Recycle();
                    }
                });
            }
            catch (Exception e)
            {
                BNL.LogError($"{e.Message} : {e.StackTrace}");
                reader?.Recycle();
            }
        }
        #endregion

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
            BasisNetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.AvatarChangeMessage, Peer, BasisPlayerArray.GetSnapshot());
        }
        public static void HandleAvatarMovement(NetPacketReader Reader, NetPeer Peer)
        {
            LocalAvatarSyncMessage LocalAvatarSyncMessage = new LocalAvatarSyncMessage();
            LocalAvatarSyncMessage.Deserialize(Reader, true);
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
            AudioSegmentDataMessage audioSegment = new AudioSegmentDataMessage();
            audioSegment.Deserialize(Reader);
            Reader.Recycle();
            ServerAudioSegmentMessage ServerAudio = new ServerAudioSegmentMessage
            {
                audioSegmentData = audioSegment
            };
            SendVoiceMessageToClients(ServerAudio, BasisNetworkCommons.VoiceChannel, peer);
        }

        public static void SendVoiceMessageToClients(ServerAudioSegmentMessage audioSegment, byte channel, NetPeer sender)
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

                for (int i = 0; i < length; i++)
                {
                    // Find the NetPeer corresponding to the user
                    NetPeer matchingPeer = null;

                    for (int j = 0; j < AllPeersLength; j++)
                    {
                        if (AllPeers[j].Id == data.users[i])
                        {
                            matchingPeer = AllPeers[j];
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
                    playerID = (ushort)sender.Id
                };

                // Serialize the audio segment message
                NetDataWriter NetDataWriter = new NetDataWriter(true, 2);
                audioSegment.Serialize(NetDataWriter);

                // Broadcast the message to the clients
                BasisNetworkServer.BroadcastMessageToClients(NetDataWriter, channel, ref endPoints, DeliveryMethod.Sequenced);
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
                playerIdMessage = new PlayerIdMessage() { playerID = (ushort)authClient.Id }
            };
            BasisSavedState.AddLastData(authClient, readyMessage);
            return serverReadyMessage;
        }

        public static void NotifyExistingClients(ServerReadyMessage serverSideSyncPlayerMessage, NetPeer authClient)
        {
            NetDataWriter Writer = new NetDataWriter(true, 2);
            serverSideSyncPlayerMessage.Serialize(Writer, false);
            ReadOnlySpan<NetPeer> Peers = BasisPlayerArray.GetSnapshot();

            foreach (NetPeer client in Peers)
            {
                if (client != authClient)
                {
                    client.Send(Writer, BasisNetworkCommons.CreateRemotePlayer, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        public static void SendClientListToNewClient(NetPeer authClient)
        {
            try
            {
                // Fetch all peers into an array (up to 1024)
                ReadOnlySpan<NetPeer> peers = BasisPlayerArray.GetSnapshot();
                int peerCount = peers.Length;

                if (peerCount == 0)
                {
                    BNL.Log("No peers to notify.");
                    return;
                }

                if (peerCount > 1024)
                {
                    BNL.LogError("Peer count exceeds the maximum allowed limit (1024).");
                    return;
                }

                // Pre-allocate list with a known capacity
                List<ServerReadyMessage> serverReadyMessages = new List<ServerReadyMessage>(peerCount);

                foreach (var peer in peers)
                {
                    if (peer == authClient)
                    {
                        continue;
                    }

                    if (CreateServerReadyMessageForPeer(peer, out ServerReadyMessage Message))
                    {
                        serverReadyMessages.Add(Message);
                    }
                }

                // If no messages were created, return early
                if (serverReadyMessages.Count == 0)
                {
                    BNL.Log("No valid peers to notify.");
                    return;
                }

                // Create a batched message and send it to the new client
                var remoteMessages = new CreateAllRemoteMessage
                {
                    serverSidePlayer = serverReadyMessages.ToArray()
                };

                NetDataWriter writer = new NetDataWriter(true, 2);
                remoteMessages.Serialize(writer, false);
                authClient.Send(writer, BasisNetworkCommons.CreateRemotePlayers, DeliveryMethod.ReliableOrdered);
                BNL.Log($"Sent client list ({serverReadyMessages.Count} clients) to new peer {authClient.Id}.");
            }
            catch (Exception ex)
            {
                BNL.LogError($"Failed to send client list: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private static bool CreateServerReadyMessageForPeer(NetPeer peer,out ServerReadyMessage ServerReadyMessage)
        {
            try
            {
                if (!BasisSavedState.GetLastAvatarChangeState(peer, out var changeState))
                {
                    changeState = new ClientAvatarChangeMessage();
                }

                if (!BasisSavedState.GetLastAvatarSyncState(peer, out var syncState))
                {
                    syncState = new LocalAvatarSyncMessage() { array = new byte[386], hasAdditionalAvatarData = false, AdditionalAvatarDatas = null };
                }

                if (!BasisSavedState.GetLastPlayerMetaData(peer, out var metaData))
                {
                    metaData = new PlayerMetaDataMessage() { playerDisplayName = "Error", playerUUID = string.Empty };
                }
                ServerReadyMessage = new ServerReadyMessage
                {
                    localReadyMessage = new ReadyMessage
                    {
                        localAvatarSyncMessage = syncState,
                        clientAvatarChangeMessage = changeState,
                        playerMetaDataMessage = metaData
                    },
                    playerIdMessage = new PlayerIdMessage { playerID = (ushort)peer.Id }
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
    }
}
