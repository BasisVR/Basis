using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Profiler;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Threading.Tasks;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static DarkRift.Basis_Common.Serializable.SerializableBasis;
using static SerializableBasis;
public static class BasisNetworkGenericMessages
{
    // Handler for server scene data messages
    public static void HandleServerSceneDataMessage(NetPacketReader reader, DeliveryMethod deliveryMethod)
    {
        ServerSceneDataMessage ServerSceneDataMessage = new ServerSceneDataMessage();
        ServerSceneDataMessage.Deserialize(reader);
        ushort playerID = ServerSceneDataMessage.playerIdMessage.playerID;
        RemoteSceneDataMessage sceneDataMessage = ServerSceneDataMessage.sceneDataMessage;
        BasisNetworkBehaviour.OnNetworkMessageReceived?.Invoke(playerID, sceneDataMessage.messageIndex, sceneDataMessage.payload,deliveryMethod);
    }
    public delegate void OnNetworkMessageReceiveOwnershipTransfer(string UniqueEntityID, ushort NetIdNewOwner, bool IsOwner);
    public delegate void OnNetworkMessageReceiveOwnershipRemoved(string UniqueEntityID);
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
        BasisNetworkManagement.OwnershipPairing.Remove(OwnershipTransferMessage.ownershipID);
        BasisNetworkPlayer.OnOwnershipReleased?.Invoke(OwnershipTransferMessage.ownershipID);
    }
    public static void HandleOwnership(OwnershipTransferMessage OwnershipTransferMessage)
    {
        if (BasisNetworkManagement.OwnershipPairing.ContainsKey(OwnershipTransferMessage.ownershipID))
        {
            BasisNetworkManagement.OwnershipPairing[OwnershipTransferMessage.ownershipID] = OwnershipTransferMessage.playerIdMessage.playerID;
        }
        else
        {
            BasisNetworkManagement.OwnershipPairing.TryAdd(OwnershipTransferMessage.ownershipID, OwnershipTransferMessage.playerIdMessage.playerID);
        }
        if (BasisNetworkManagement.TryGetLocalPlayerID(out ushort Id))
        {
            bool isLocalOwner = OwnershipTransferMessage.playerIdMessage.playerID == Id;

            BasisNetworkPlayer.OnOwnershipTransfer?.Invoke(OwnershipTransferMessage.ownershipID, OwnershipTransferMessage.playerIdMessage.playerID, isLocalOwner);
        }
    }
    // Handler for server avatar data messages
    public static void HandleServerAvatarDataMessage(LiteNetLib.NetPacketReader reader, LiteNetLib.DeliveryMethod Method)
    {
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAvatarData,reader.AvailableBytes);
        ServerAvatarDataMessage serverAvatarDataMessage = new ServerAvatarDataMessage();
        serverAvatarDataMessage.Deserialize(reader);
        ushort avatarLinkID = serverAvatarDataMessage.avatarDataMessage.PlayerIdMessage.playerID; // destination
        if (BasisNetworkManagement.Players.TryGetValue(avatarLinkID, out BasisNetworkPlayer player))
        {
            if (player.Player == null)
            {
                BasisDebug.LogError("Missing Player! " + avatarLinkID);
                return;
            }
            if (player.Player.BasisAvatar != null)
            {
                RemoteAvatarDataMessage output = serverAvatarDataMessage.avatarDataMessage;
                if (player.NetworkBehaviours.Length >= output.messageIndex)
                {
                    bool IsDifferentAvatar = output.AvatarLinkIndex != player.LastLinkedAvatarIndex;
                    if (output.messageIndex < player.NetworkBehaviourCount)
                    {
                        player.NetworkBehaviours[output.messageIndex].OnNetworkMessageReceived(serverAvatarDataMessage.playerIdMessage.playerID, output.payload, Method, IsDifferentAvatar);
                    }
                }
            }
            else
            {
                BasisDebug.LogError("Missing Avatar For Message " + serverAvatarDataMessage.playerIdMessage.playerID);
            }
        }
        else
        {
            BasisDebug.Log("Missing Player For Message " + serverAvatarDataMessage.playerIdMessage.playerID);
        }
    }
    // Sending message with different conditions
    public static void OnNetworkMessageSend(ushort messageIndex, byte[] buffer = null, DeliveryMethod deliveryMethod = DeliveryMethod.Unreliable, ushort[] recipients = null)
    {
        NetDataWriter netDataWriter = new NetDataWriter();
        //BasisDebug.Log("Sending with Recipients and buffer");
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
            BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.FallChannel, deliveryMethod);
        }
        else
        {
            sceneDataMessage.Serialize(netDataWriter);
            BasisNetworkManagement.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.SceneChannel, deliveryMethod);
        }
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SceneData, netDataWriter.Length);
    }
    public static void NetIDAssign(LiteNetLib.NetPacketReader reader, LiteNetLib.DeliveryMethod Method)
    {
        ServerNetIDMessage ServerNetIDMessage = new ServerNetIDMessage();
        ServerNetIDMessage.Deserialize(reader);
        BasisNetworkIdResolver.CompleteMessageDelegation(ServerNetIDMessage);
    }
    public static void MassNetIDAssign(LiteNetLib.NetPacketReader reader, LiteNetLib.DeliveryMethod Method)
    {
        ServerUniqueIDMessages ServerNetIDMessage = new ServerUniqueIDMessages();
        ServerNetIDMessage.Deserialize(reader);
        foreach(ServerNetIDMessage message in ServerNetIDMessage.Messages)
        {
            BasisNetworkIdResolver.CompleteMessageDelegation(message);
        }
    }
    public static async Task LoadResourceMessage(LiteNetLib.NetPacketReader reader, LiteNetLib.DeliveryMethod Method)
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
    public static void UnloadResourceMessage(LiteNetLib.NetPacketReader reader, LiteNetLib.DeliveryMethod Method)
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
