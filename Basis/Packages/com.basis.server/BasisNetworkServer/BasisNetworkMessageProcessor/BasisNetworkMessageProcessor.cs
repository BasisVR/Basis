using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisServerHandle;
using System;
using static BasisNetworkCore.Serializable.SerializableBasis;
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
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.AuthIdentityChannel, reader.AvailableBytes);
                    BasisServerHandleEvents.HandleAuth(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.PlayerAvatarChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.PlayerAvatarChannel, reader.AvailableBytes);
                    BasisServerReductionSystemEvents.HandleAvatarMovement(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.VoiceChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.VoiceChannel, reader.AvailableBytes);
                    BasisServerHandleEvents.HandleVoiceMessage(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.AvatarChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.AvatarChannel, reader.AvailableBytes);
                    BasisNetworkingGeneric.HandleAvatar(reader, deliveryMethod, peer);//recycles inside
                    break;

                case BasisNetworkCommons.SceneChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.SceneChannel, reader.AvailableBytes);
                    BasisNetworkingGeneric.HandleScene(reader, deliveryMethod, peer);//recycles inside
                    break;

                case BasisNetworkCommons.AvatarChangeMessageChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.AvatarChangeMessageChannel, reader.AvailableBytes);
                    BasisServerHandleEvents.SendAvatarMessageToClients(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.ChangeCurrentOwnerRequestChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, reader.AvailableBytes);
                    BasisNetworkOwnership.OwnershipTransfer(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.GetCurrentOwnerRequestChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.GetCurrentOwnerRequestChannel, reader.AvailableBytes);
                    BasisNetworkOwnership.OwnershipResponse(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.RemoveCurrentOwnerRequestChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, reader.AvailableBytes);
                    BasisNetworkOwnership.RemoveOwnership(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.AudioRecipientsChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.AudioRecipientsChannel, reader.AvailableBytes);
                    BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.netIDAssignChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.netIDAssignChannel, reader.AvailableBytes);
                    BasisServerHandleEvents.NetIDAssign(reader, peer);//recycles inside
                    break;

                case BasisNetworkCommons.LoadResourceChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.LoadResourceChannel, reader.AvailableBytes);
                    HandleAdminResourceAction(peer, reader, BasisServerHandleEvents.LoadResource);//recycles inside
                    break;

                case BasisNetworkCommons.UnloadResourceChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.UnloadResourceChannel, reader.AvailableBytes);
                    HandleAdminResourceAction(peer, reader, BasisServerHandleEvents.UnloadResource);//recycles inside
                    break;

                case BasisNetworkCommons.AdminChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.AdminChannel, reader.AvailableBytes);
                    BasisPlayerModeration.OnAdminMessage(peer, reader);//recycles inside
                    break;

                case BasisNetworkCommons.AvatarCloneRequestChannel:
                case BasisNetworkCommons.AvatarCloneResponseChannel:
                    // Placeholder for AvatarCloneMessage handlers
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.AvatarCloneResponseChannel, reader.AvailableBytes);
                    reader.Recycle();//recycles here
                    break;

                case BasisNetworkCommons.ServerBoundChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.ServerBoundChannel, reader.AvailableBytes);
                    BasisServerHandleEvents.OnServerReceived?.Invoke(peer, reader, deliveryMethod);
                    reader.Recycle();//recycles here
                    break;

                case BasisNetworkCommons.StoreDatabaseChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.StoreDatabaseChannel, reader.AvailableBytes);
                    BasisServerHandleEvents.HandleStoreDatabase(reader,peer);//recycles inside
                    break;

                case BasisNetworkCommons.RequestStoreDatabaseChannel:
                    BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.RequestStoreDatabaseChannel, reader.AvailableBytes);
                    BasisServerHandleEvents.HandleRequestStoreDatabase(reader, peer);//recycles inside
                    break;


                case BasisNetworkCommons.ServerStatisticsChannel:

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

                    if (reader.GetBool())
                    {
                        BNL.Log("requested Server StatisticsChannel");
                        BasisNetworkStatistics.IsRecordingData = true;
                        BasisNetworkStatistics.RecordInbound(BasisNetworkCommons.ServerStatisticsChannel, reader.AvailableBytes);
                        ServerStatisticMessage ServerStatistic = new ServerStatisticMessage
                        {
                            Data = BasisNetworkStatistics.Snapshot.SnapshotResetEncode(true, 6)
                        };
                        reader.Recycle();

                        NetDataWriter writer = new NetDataWriter(true);
                        ServerStatistic.Serialize(writer);
                        BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.ServerStatisticsChannel, writer.Length);
                        peer.Send(writer, BasisNetworkCommons.ServerStatisticsChannel, DeliveryMethod.ReliableOrdered);

                    }
                    else
                    {
                        BasisNetworkStatistics.IsRecordingData = false;
                    }
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
