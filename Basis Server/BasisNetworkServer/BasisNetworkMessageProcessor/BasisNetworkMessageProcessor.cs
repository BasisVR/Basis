using System;
using System.Diagnostics;
using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Basis.Network.Server.Ownership;
using BasisNetworkCore;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.Security;
using BasisServerHandle;
using LiteNetLib;
using LiteNetLib.Utils;
using static SerializableBasis;

public static class BasisNetworkMessageProcessor
{
    public static void Enqueue(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            ProcessMessage(peer, reader, channel, deliveryMethod);
        }
        catch (Exception ex)
        {
            BNL.LogError($"[Error] Exception in direct message processing:\n{ex}");
            reader?.Recycle();
        }
    }

    private static void ProcessMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            switch (channel)
            {
                case BasisNetworkCommons.FallChannel:
                    if (deliveryMethod == DeliveryMethod.Unreliable)
                    {
                        if (reader.TryGetByte(out byte newChannel))
                        {
                            ProcessMessage(peer, reader, newChannel, deliveryMethod);
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

                case BasisNetworkCommons.AuthIdentityChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.HandleAuth(reader, peer);
                    break;

                case BasisNetworkCommons.PlayerAvatarChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.HandleAvatarMovement(reader, peer);
                    break;

                case BasisNetworkCommons.VoiceChannel:
                    BasisServerHandleEvents.HandleVoiceMessage(reader, peer);
                    break;

                case BasisNetworkCommons.AvatarChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkingGeneric.HandleAvatar(reader, deliveryMethod, peer);
                    break;

                case BasisNetworkCommons.SceneChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkingGeneric.HandleScene(reader, deliveryMethod, peer);
                    break;

                case BasisNetworkCommons.AvatarChangeMessageChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.SendAvatarMessageToClients(reader, peer);
                    break;

                case BasisNetworkCommons.ChangeCurrentOwnerRequestChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkOwnership.OwnershipTransfer(reader, peer);
                    break;

                case BasisNetworkCommons.GetCurrentOwnerRequestChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkOwnership.OwnershipResponse(reader, peer);
                    break;

                case BasisNetworkCommons.RemoveCurrentOwnerRequestChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisNetworkOwnership.RemoveOwnership(reader, peer);
                    break;

                case BasisNetworkCommons.AudioRecipientsChannel:
                    BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer);
                    break;

                case BasisNetworkCommons.netIDAssignChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.NetIDAssign(reader, peer);
                    break;

                case BasisNetworkCommons.LoadResourceChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        if (NetworkServer.authIdentity.NetIDToUUID(peer, out string uuid))
                        {
                            if (NetworkServer.authIdentity.IsNetPeerAdmin(uuid))
                            {
                                BasisServerHandleEvents.LoadResource(reader, peer);
                            }
                            else
                            {
                                BNL.LogError("Admin was not found! for " + uuid);
                            }
                        }
                        else
                        {
                            BNL.LogError("User " + uuid + " does not exist!");
                        }
                    }
                    break;

                case BasisNetworkCommons.UnloadResourceChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        if (NetworkServer.authIdentity.NetIDToUUID(peer, out string uuid))
                        {
                            if (NetworkServer.authIdentity.IsNetPeerAdmin(uuid))
                            {
                                BasisServerHandleEvents.UnloadResource(reader, peer);
                            }
                            else
                            {
                                BNL.LogError("Admin was not found! for " + uuid);
                            }
                        }
                        else
                        {
                            BNL.LogError("User " + uuid + " does not exist!");
                        }
                    }
                    break;

                case BasisNetworkCommons.AdminChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisPlayerModeration.OnAdminMessage(peer, reader);
                    reader.Recycle();
                    break;

                case BasisNetworkCommons.AvatarCloneRequestChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        // Placeholder for AvatarCloneRequestMessage
                    }
                    reader.Recycle();
                    break;

                case BasisNetworkCommons.AvatarCloneResponseChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        // Placeholder for AvatarCloneResponseMessage
                    }
                    reader.Recycle();
                    break;

                case BasisNetworkCommons.ServerBoundChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                        BasisServerHandleEvents.OnServerReceived?.Invoke(peer, reader, deliveryMethod);
                    reader.Recycle();
                    break;

                case BasisNetworkCommons.StoreDatabaseChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        DatabasePrimativeMessage dataMessage = new();
                        dataMessage.Deserialize(reader);
                        BasisPersistentDatabase.AddOrUpdate(new BasisData(dataMessage.DatabaseID, dataMessage.jsonPayload));
                    }
                    reader.Recycle();
                    break;

                case BasisNetworkCommons.RequestStoreDatabaseChannel:
                    if (BasisServerHandleEvents.ValidateSize(reader, peer, channel))
                    {
                        DataBaseRequest dataRequest = new();
                        dataRequest.Deserialize(reader);

                        if (BasisPersistentDatabase.GetByName(dataRequest.DatabaseID, out BasisData db))
                        {
                            DatabasePrimativeMessage msg = new()
                            {
                                DatabaseID = dataRequest.DatabaseID,
                                jsonPayload = db.JsonPayload
                            };
                            NetDataWriter writer = new(true);
                            msg.Serialize(writer);
                            peer.Send(writer, BasisNetworkCommons.StoreDatabaseChannel, DeliveryMethod.ReliableOrdered);
                        }
                        else
                        {
                            BasisPersistentDatabase.AddOrUpdate(new BasisData(dataRequest.DatabaseID, new System.Collections.Concurrent.ConcurrentDictionary<string, object>()));
                            DatabasePrimativeMessage msg = new()
                            {
                                DatabaseID = dataRequest.DatabaseID,
                                jsonPayload = new System.Collections.Concurrent.ConcurrentDictionary<string, object>()
                            };
                            NetDataWriter writer = new(true);
                            msg.Serialize(writer);
                            peer.Send(writer, BasisNetworkCommons.StoreDatabaseChannel, DeliveryMethod.ReliableOrdered);
                        }
                    }
                    reader.Recycle();
                    break;

                default:
                    BNL.LogError($"Unknown channel: {channel} " + reader.AvailableBytes);
                    reader.Recycle();
                    break;
            }
        }
        catch (Exception ex)
        {
            BNL.LogError($"[Error] Exception in ProcessMessage\n" +
                         $"Peer: {peer.Address}, Channel: {channel}, Delivery: {deliveryMethod}\n" +
                         $"Message: {ex.Message}\nStackTrace: {ex.StackTrace}");
            reader?.Recycle();
        }
    }
}
