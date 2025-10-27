using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static DarkRift.Basis_Common.Serializable.SerializableBasis;
using static SerializableBasis;
public static class BasisNetworkGenericMessages
{
    public class DeferredMessage
    {
        public ushort PlayerId { get; }
        public ushort MessageIndex { get; }
        public byte[] Payload { get; }
        public DeliveryMethod DeliveryMethod { get; }

        public DeferredMessage(ushort playerId, ushort messageIndex, byte[] payload, DeliveryMethod deliveryMethod)
        {
            PlayerId = playerId;
            MessageIndex = messageIndex;
            Payload = payload;
            DeliveryMethod = deliveryMethod;
        }
    }
    private static readonly List<DeferredMessage> _deferredMessages = new();
    private static readonly Dictionary<ushort, Action<ushort, byte[], DeliveryMethod>> _handlers = new();
    private const int MaxDeferredMessages = 1000; // Set your limit here
    public delegate void OnNetworkMessageReceiveOwnershipTransfer(string UniqueEntityID, ushort NetIdNewOwner, bool IsOwner);
    public delegate void OnNetworkMessageReceiveOwnershipRemoved(string UniqueEntityID);
    // Sending message with different conditions
    private static readonly ThreadLocal<NetDataWriter> threadLocalWriter = new ThreadLocal<NetDataWriter>(() => new NetDataWriter());
    public static void RegisterHandler(ushort messageIndex, Action<ushort, byte[], DeliveryMethod> handler)
    {
        _handlers[messageIndex] = handler;
        TryDeliverDeferredMessages();
    }

    public static void UnregisterHandler(ushort messageIndex)
    {
        _handlers.Remove(messageIndex);
    }

    public static void HandleServerSceneDataMessage(NetPacketReader reader, DeliveryMethod deliveryMethod)
    {
        var serverSceneDataMessage = new ServerSceneDataMessage();
        serverSceneDataMessage.Deserialize(reader);

        ushort playerID = serverSceneDataMessage.playerIdMessage.playerID;
        var sceneDataMessage = serverSceneDataMessage.sceneDataMessage;
        ushort messageIndex = sceneDataMessage.messageIndex;

        if (_handlers.TryGetValue(messageIndex, out var handler))
        {
            handler.Invoke(playerID, sceneDataMessage.payload, deliveryMethod);
            serverSceneDataMessage.sceneDataMessage.Release();//dont need todo this but not doing it will create more gc then necessary
        }
        else
        {
            // Check capacity before adding
            if (_deferredMessages.Count >= MaxDeferredMessages)
            {
                // Remove the oldest message (FIFO)
                _deferredMessages.RemoveAt(0);
            }

            _deferredMessages.Add(new DeferredMessage(playerID, messageIndex, sceneDataMessage.payload, deliveryMethod));
        }
    }

    private static void TryDeliverDeferredMessages()
    {
        for (int Index = _deferredMessages.Count - 1; Index >= 0; Index--)
        {
            var msg = _deferredMessages[Index];
            if (_handlers.TryGetValue(msg.MessageIndex, out var handler))
            {
                handler.Invoke(msg.PlayerId, msg.Payload, msg.DeliveryMethod);
                _deferredMessages.RemoveAt(Index);
            }
        }
    }
    public static void HandleOwnershipTransfer(NetPacketReader reader)
    {
        OwnershipTransferMessage OwnershipTransferMessage = new OwnershipTransferMessage();
        OwnershipTransferMessage.Deserialize(reader);
        HandleOwnership(OwnershipTransferMessage);
    }
    public static void HandleOwnershipResponse(NetPacketReader reader)
    {
        OwnershipTransferMessage ownershipTransferMessage = new OwnershipTransferMessage();
        ownershipTransferMessage.Deserialize(reader);
        HandleOwnership(ownershipTransferMessage);
    }
    public static void HandleOwnershipRemove(NetPacketReader reader)
    {
        OwnershipTransferMessage OwnershipTransferMessage = new OwnershipTransferMessage();
        OwnershipTransferMessage.Deserialize(reader);
        BasisNetworkPlayers.OwnershipPairing.Remove(OwnershipTransferMessage.ownershipID,out ushort OldPlayerID);
        BasisNetworkPlayer.OnOwnershipReleased?.Invoke(OwnershipTransferMessage.ownershipID);
    }
    public static void HandleOwnership(OwnershipTransferMessage OwnershipTransferMessage)
    {
        if (BasisNetworkPlayers.OwnershipPairing.ContainsKey(OwnershipTransferMessage.ownershipID))
        {
            BasisNetworkPlayers.OwnershipPairing[OwnershipTransferMessage.ownershipID] = OwnershipTransferMessage.playerIdMessage.playerID;
        }
        else
        {
            BasisNetworkPlayers.OwnershipPairing.TryAdd(OwnershipTransferMessage.ownershipID, OwnershipTransferMessage.playerIdMessage.playerID);
        }
        if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort Id))
        {
            bool isLocalOwner = OwnershipTransferMessage.playerIdMessage.playerID == Id;

            BasisNetworkPlayer.OnOwnershipTransfer?.Invoke(OwnershipTransferMessage.ownershipID, OwnershipTransferMessage.playerIdMessage.playerID, isLocalOwner);
        }
    }
    // Handler for server avatar data messages
    public static void HandleServerAvatarDataMessage(NetPacketReader reader, DeliveryMethod Method)
    {
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAvatarData, reader.AvailableBytes);
        ServerAvatarDataMessage SADM = new ServerAvatarDataMessage();
        SADM.Deserialize(reader);

        ushort playerID = SADM.avatarDataMessage.PlayerIdMessage.playerID; // destination
        if (BasisNetworkPlayers.Players.TryGetValue(playerID, out BasisNetworkPlayer player))
        {
            if (player.Player == null)
            {
                BasisDebug.LogError("Missing Player! " + playerID);
                return;
            }

            if (player.Player.BasisAvatar != null)
            {
                RemoteAvatarDataMessage output = SADM.avatarDataMessage;

                if (player.NetworkBehaviours.Length >= output.messageIndex)
                {
                    bool isDifferentAvatar = output.AvatarLinkIndex != player.LastLinkedAvatarIndex;

                    if (isDifferentAvatar)
                    {
                        // Check if the AvatarLinkIndex is within the next 4 slots ahead (modulo 256)
                        bool withinNextFour = false;
                        for (int Index = 1; Index <= 4; Index++)
                        {
                            byte nextIndex = (byte)((player.LastLinkedAvatarIndex + Index) % (byte.MaxValue + 1));
                            if (nextIndex == output.AvatarLinkIndex)
                            {
                                withinNextFour = true;
                                break;
                            }
                        }

                        if (withinNextFour)
                        {
                            // Store the message for delayed playback
                            player.NextMessages[output.messageIndex] = new BasisNetworkPlayer.ServerAvatarDataMessageQueue()
                            {
                                Method = Method,
                                ServerAvatarDataMessage = SADM
                            };
                        }
                    }
                    else
                    {
                        if (output.messageIndex < player.NetworkBehaviourCount)
                        {
                            player.NetworkBehaviours[output.messageIndex].OnNetworkMessageReceived(SADM.playerIdMessage.playerID, output.payload, Method);
                        }
                        else
                        {
                            BasisDebug.LogError($"this Should never occur Message Index did not exist {output.messageIndex}");
                        }
                    }
                }
            }
            else
            {
                BasisDebug.LogError("Missing Avatar For Message " + SADM.playerIdMessage.playerID);
            }
        }
        else
        {
            BasisDebug.Log("Missing Player For Message " + SADM.playerIdMessage.playerID);
        }
    }
    public static void OnNetworkMessageSend(ushort messageIndex,byte[] buffer = null,DeliveryMethod deliveryMethod = DeliveryMethod.Unreliable,ushort[] recipients = null)
    {
        NetDataWriter netDataWriter = threadLocalWriter.Value;
        netDataWriter.Reset(); // clear previous data

        SceneDataMessage sceneDataMessage = new SceneDataMessage
        {
            messageIndex = messageIndex,
            payload = buffer,
            recipients = recipients
        };

        if (deliveryMethod == DeliveryMethod.Unreliable)
        {
            netDataWriter.Put(BasisNetworkCommons.SceneChannel);
            sceneDataMessage.Serialize(netDataWriter);
            BasisNetworkConnection.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.FallChannel, deliveryMethod);
        }
        else
        {
            sceneDataMessage.Serialize(netDataWriter);
            BasisNetworkConnection.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.SceneChannel, deliveryMethod);
        }

        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SceneData, netDataWriter.Length);
    }
    public static void NetIDAssign(NetPacketReader reader, DeliveryMethod Method)
    {
        ServerNetIDMessage ServerNetIDMessage = new ServerNetIDMessage();
        ServerNetIDMessage.Deserialize(reader);
        BasisNetworkIdResolver.CompleteMessageDelegation(ServerNetIDMessage);
    }
    public static void MassNetIDAssign(NetPacketReader reader, DeliveryMethod Method)
    {
        ServerUniqueIDMessages ServerNetIDMessage = new ServerUniqueIDMessages();
        ServerNetIDMessage.Deserialize(reader);
        foreach (ServerNetIDMessage message in ServerNetIDMessage.Messages)
        {
            BasisNetworkIdResolver.CompleteMessageDelegation(message);
        }
    }
    public static async Task LoadResourceMessage(NetPacketReader reader, DeliveryMethod Method)
    {
        LocalLoadResource LocalLoadResource = new LocalLoadResource();
        LocalLoadResource.Deserialize(reader);
        switch (LocalLoadResource.Mode)
        {
            case 0:
                await BasisNetworkSpawnItem.SpawnGameObject(LocalLoadResource, BundledContentHolder.Selector.Prop);
                break;
            case 1:
                await BasisNetworkSpawnItem.SpawnScene(LocalLoadResource);
                break;
            default:
                BNL.LogError($"tried to Load Mode {LocalLoadResource.Mode}");
                break;
        }
    }
    public static void UnloadResourceMessage(NetPacketReader reader, DeliveryMethod Method)
    {
        UnLoadResource UnLoadResource = new UnLoadResource();
        UnLoadResource.Deserialize(reader);
        switch (UnLoadResource.Mode)
        {
            case 0:
                BasisNetworkSpawnItem.DestroyGameobject(UnLoadResource);
                break;
            case 1:
                BasisNetworkSpawnItem.DestroyScene(UnLoadResource);
                break;
            default:
                BNL.LogError($"tried to removed Mode {UnLoadResource.Mode}");
                break;
        }
    }
}
