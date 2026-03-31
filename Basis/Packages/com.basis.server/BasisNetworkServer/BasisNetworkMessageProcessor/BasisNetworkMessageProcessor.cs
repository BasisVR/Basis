using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisPermissions;
using BasisServerHandle;
using System;
using System.Collections.Concurrent;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;

public static class BasisNetworkMessageProcessor
{
    private const int MaxErrorsBeforeWarning = 50;
    private static readonly ConcurrentDictionary<int, int> _peerErrorCounts = new();

    public static void ClearPeerErrors(int peerId) => _peerErrorCounts.TryRemove(peerId, out _);

    public static void ProcessMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        BasisNetworkStatistics.RecordInbound(channel, reader.AvailableBytes);
        try
        {
            switch (channel)
            {
                case BasisNetworkCommons.ShoutVoiceChannel:
                    BasisServerHandleEvents.HandleShoutVoiceMessage(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.AuthIdentityChannel:
                    BasisServerHandleEvents.HandleAuth(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.PlayerAvatarHighChannel:
                case BasisNetworkCommons.PlayerAvatarHighAdditionalChannel:
                    BasisServerReductionSystemEvents.HandleAvatarMovement(reader, peer, channel); // recycles inside
                    break;

                case BasisNetworkCommons.VoiceChannel:
                    BasisServerHandleEvents.HandleVoiceMessage(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.AvatarChannel:
                    BasisNetworkingGeneric.HandleAvatar(reader, deliveryMethod, peer); // recycles inside
                    break;

                case BasisNetworkCommons.SceneChannel:
                    BasisNetworkingGeneric.HandleScene(reader, deliveryMethod, peer); // recycles inside
                    break;

                case BasisNetworkCommons.AvatarChangeMessageChannel:
                    BasisServerHandleEvents.SendAvatarMessageToClients(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.ChangeCurrentOwnerRequestChannel:
                    HandlePermitted(peer, reader, PermNodes.OwnershipTransfer, () =>
                    {
                        BasisNetworkOwnership.OwnershipTransfer(reader, peer); // recycles inside
                    });
                    break;

                case BasisNetworkCommons.GetCurrentOwnerRequestChannel:
                    HandlePermitted(peer, reader, PermNodes.OwnershipGet, () =>
                    {
                        BasisNetworkOwnership.OwnershipResponse(reader, peer); // recycles inside
                    });
                    break;

                case BasisNetworkCommons.RemoveCurrentOwnerRequestChannel:
                    HandlePermitted(peer, reader, PermNodes.OwnershipRemove, () =>
                    {
                        BasisNetworkOwnership.RemoveOwnership(reader, peer); // recycles inside
                    });
                    break;

                case BasisNetworkCommons.AudioRecipientsChannel:
                    BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer, false); // byte count, recycles inside
                    break;

                case BasisNetworkCommons.AudioRecipientsLargeChannel:
                    BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer, true); // ushort count, recycles inside
                    break;

                case BasisNetworkCommons.AudioRecipientsInvertedChannel:
                    BasisServerHandleEvents.UpdateVoiceReceiversInverted(reader, peer, false); // byte count excluded, recycles inside
                    break;

                case BasisNetworkCommons.AudioRecipientsInvertedLargeChannel:
                    BasisServerHandleEvents.UpdateVoiceReceiversInverted(reader, peer, true); // ushort count excluded, recycles inside
                    break;

                case BasisNetworkCommons.AudioRecipientsBitfieldChannel:
                    BasisServerHandleEvents.UpdateVoiceReceiversBitfield(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.netIDAssignChannel:
                    BasisServerHandleEvents.NetIDAssign(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.LoadResourceChannel:
                    if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string LRuuid))
                    {
                        BasisServerHandleEvents.LoadResource(reader, peer, LRuuid);
                        break;
                    }
                    BNL.LogError($"User UUID not found for peer: {peer}");
                    reader.Recycle();
                    return;

                case BasisNetworkCommons.UnloadResourceChannel:
                    BasisServerHandleEvents.UnloadResource(reader, peer);
                    reader.Recycle();
                    return;

                case BasisNetworkCommons.AdminChannel:
                    BasisPlayerModeration.OnAdminMessage(peer, reader); // recycles inside
                    break;

                case BasisNetworkCommons.ContentShareChannel:
                    BasisNetworkContentShare.HandleContentShareDrop(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.ContentShareCleanupChannel:
                    BasisNetworkContentShare.HandleContentShareCleanup(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.ServerBoundChannel:
                    BasisServerHandleEvents.OnServerReceived?.Invoke(peer, reader, deliveryMethod);
                    reader.Recycle(); // recycles here
                    break;

                case BasisNetworkCommons.StoreDatabaseChannel:
                    BasisServerHandleEvents.HandleStoreDatabase(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.RequestStoreDatabaseChannel:
                    BasisServerHandleEvents.HandleRequestStoreDatabase(reader, peer); // recycles inside
                    break;

              //  case BasisNetworkCommons.ServerIsAdminChannel:
                ///    BasisPlayerModeration.CheckHasPermission(reader,peer);
                ///    reader.Recycle(); // recycles here
                //    break;

                case BasisNetworkCommons.ServerStatisticsChannel:
                    {
                        // Permission-gated stats
                        if (!TryWithPermission(peer, reader, PermNodes.ServerStats, out _))
                        {
                            return;
                        }

                        if (reader.GetBool())
                        {
                            BNL.Log("requested Server StatisticsChannel");
                            BasisNetworkStatistics.IsRecordingData = true;

                            ServerStatisticMessage serverStatistic = new ServerStatisticMessage
                            {
                                Data = BasisNetworkStatistics.Snapshot.SnapshotResetEncode(true, 6)
                            };

                            reader.Recycle();

                            NetDataWriter writer = NetworkServer.RentWriter();
                            serverStatistic.Serialize(writer);
                            BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.ServerStatisticsChannel, writer.Length);
                            peer.Send(writer, BasisNetworkCommons.ServerStatisticsChannel, DeliveryMethod.ReliableOrdered);
                            NetworkServer.ReturnWriter(writer);
                        }
                        else
                        {
                            BasisNetworkStatistics.IsRecordingData = false;
                            reader.Recycle();
                        }
                        break;
                    }

                case BasisNetworkCommons.ChatChannel:
                    BasisNetworkChat.HandleChatMessage(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.CameraPIPStateChannel:
                    BasisNetworkPIPCamera.HandlePIPStateChange(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.CameraPIPPositionChannel:
                    BasisNetworkPIPCamera.HandlePIPPositionUpdate(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.PreloadReadyChannel:
                    BasisServerHandleEvents.HandlePreloadReady(reader, peer); // recycles inside
                    break;

                case BasisNetworkCommons.EventsChannel:
                    BasisNetworkEvents.HandleEvent(reader, peer); // reads event type byte, routes, recycles inside
                    break;

                default:
                    int errorCount = _peerErrorCounts.AddOrUpdate(peer.Id, 1, (_, c) => c + 1);
                    if (errorCount <= 5 || errorCount % 100 == 0)
                    {
                        BNL.LogError($"Unknown channel: {channel} ({reader.AvailableBytes} bytes remaining) from peer {peer.Id} (error #{errorCount})");
                    }
                    reader.Recycle();
                    if (errorCount >= MaxErrorsBeforeWarning)
                    {
                        BNL.LogError($"Peer {peer.Id} ({peer.Address}) has reached {errorCount} protocol errors. The server has detected an issue with this client or its connection.");
                        BasisPlayerModeration.SendBackMessage(peer, "The server has detected an issue with your client or connection. You may experience problems.");
                        _peerErrorCounts.TryRemove(peer.Id, out _);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            int errorCount = _peerErrorCounts.AddOrUpdate(peer.Id, 1, (_, c) => c + 1);
            if (errorCount <= 5 || errorCount % 100 == 0)
            {
                BNL.LogError(
                    $"[Error] Exception in ProcessMessage (error #{errorCount})\nPeer: {peer.Address}, Channel: {channel}, Delivery: {deliveryMethod}\nMessage: {ex.Message}\nStackTrace: {ex.StackTrace}"
                );
            }
            reader.Recycle();
            if (errorCount >= MaxErrorsBeforeWarning)
            {
                BNL.LogError($"Peer {peer.Id} ({peer.Address}) has reached {errorCount} protocol errors. The server has detected an issue with this client or its connection.");
                BasisPlayerModeration.SendBackMessage(peer, "The server has detected an issue with your client or connection. You may experience problems.");
                _peerErrorCounts.TryRemove(peer.Id, out _);
            }
        }
    }
    private static bool TryWithPermission(NetPeer peer, NetPacketReader reader, string permNode, out string uuid)
    {
        if (!NetworkServer.AuthIdentity.NetIDToUUID(peer, out uuid))
        {
            BNL.LogError($"User UUID not found for peer: {peer}");
            reader.Recycle();
            return false;
        }

        // Allow if they have the specific node, or admin, or global wildcard
        if (PermissionIntegration.HasValidRequirement(uuid, permNode))
        {
            return true;
        }

        BNL.LogError($"Unauthorized access attempt by UUID: {uuid} for {permNode}");
        reader.Recycle();
        return false;
    }

    private static void HandlePermitted(NetPeer peer, NetPacketReader reader, string permNode, Action action)
    {
        if (TryWithPermission(peer, reader, permNode, out _))
        {
            action();
        }
    }
}
