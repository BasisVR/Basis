using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisServerHandle;
using LiteNetLib;
using System;
public static class BasisNetworkMessageProcessor
{
    public static void ProcessMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            if (TryRedirectFallChannel(peer,reader, ref channel, deliveryMethod))
                return;

            switch (channel)
            {
                case BasisNetworkCommons.AuthIdentityChannel:
                    BasisServerHandleEvents.HandleAuth(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.PlayerAvatarChannel:
                    BasisServerReductionSystemEvents.HandleAvatarMovement(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.VoiceChannel:
                    BasisServerHandleEvents.HandleVoiceMessage(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.AvatarChannel:
                    BasisNetworkingGeneric.HandleAvatar(reader, deliveryMethod, peer);//recycles inside
                    break;

                case BasisNetworkCommons.SceneChannel:
                    BasisNetworkingGeneric.HandleScene(reader, deliveryMethod, peer);//recycles inside
                    break;

                case BasisNetworkCommons.AvatarChangeMessageChannel:
                    BasisServerHandleEvents.SendAvatarMessageToClients(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.ChangeCurrentOwnerRequestChannel:
                    BasisNetworkOwnership.OwnershipTransfer(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.GetCurrentOwnerRequestChannel:
                    BasisNetworkOwnership.OwnershipResponse(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.RemoveCurrentOwnerRequestChannel:
                    BasisNetworkOwnership.RemoveOwnership(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.AudioRecipientsChannel:
                    BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.netIDAssignChannel:
                    BasisServerHandleEvents.NetIDAssign(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.LoadResourceChannel:
                    HandleAdminResourceAction(peer, reader, BasisServerHandleEvents.LoadResource);//recycles inside
                    break;

                case BasisNetworkCommons.UnloadResourceChannel:
                    HandleAdminResourceAction(peer, reader, BasisServerHandleEvents.UnloadResource);//recycles inside
                    break;

                case BasisNetworkCommons.AdminChannel:
                    BasisPlayerModeration.OnAdminMessage(peer, reader);//recycles inside
                    break;

                case BasisNetworkCommons.AvatarCloneRequestChannel:
                case BasisNetworkCommons.AvatarCloneResponseChannel:
                    // Placeholder for AvatarCloneMessage handlers
                    reader.Recycle();//recycles here
                    break;

                case BasisNetworkCommons.ServerBoundChannel:
                    BasisServerHandleEvents.OnServerReceived?.Invoke(peer, reader, deliveryMethod);
                    reader.Recycle();//recycles here
                    break;

                case BasisNetworkCommons.StoreDatabaseChannel:
                    BasisServerHandleEvents.HandleStoreDatabase(reader,peer);//recycles inside
                    break;

                case BasisNetworkCommons.RequestStoreDatabaseChannel:
                    BasisServerHandleEvents.HandleRequestStoreDatabase(reader, peer);//recycles inside
                    break;

                default:
                    BNL.LogError($"Unknown channel: {channel} ({reader.AvailableBytes} bytes remaining)");
                    break;
            }
        }
        catch (Exception ex)
        {

            BNL.LogError($"[Error] Exception in ProcessMessage\nPeer: {peer.Address}, Channel: {channel}, Delivery: {deliveryMethod}\nMessage: {ex.Message}\nStackTrace: {ex.StackTrace}");
            reader.Recycle();
        }
    }

    private static bool TryRedirectFallChannel(NetPeer Peer,NetPacketReader reader, ref byte channel, DeliveryMethod deliveryMethod)
    {
        if (channel == BasisNetworkCommons.FallChannel && deliveryMethod == DeliveryMethod.Unreliable)
        {
            if (reader.TryGetByte(out byte newChannel))
            {
                ProcessMessage(Peer, reader, newChannel, deliveryMethod);
            }
            else
            {
                BNL.LogError($"FallChannel redirection failed, no data remains: {reader.AvailableBytes}");
            }

            reader.Recycle();
            return true;
        }

        return false;
    }
    private static void HandleAdminResourceAction(NetPeer peer, NetPacketReader reader, Action<NetPacketReader, NetPeer> action)
    {
        if (!NetworkServer.AuthIdentity.NetIDToUUID(peer, out string uuid))
        {
            BNL.LogError($"User UUID not found for peer: {peer}");
            return;
        }

        if (!NetworkServer.AuthIdentity.IsNetPeerAdmin(uuid))
        {
            BNL.LogError($"Unauthorized admin access attempt by UUID: {uuid}");
            return;
        }

        action(reader, peer);
    }
}
