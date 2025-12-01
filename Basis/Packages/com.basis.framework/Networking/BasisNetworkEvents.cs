using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Profiler;
using Basis.Scripts.UI.UI_Panels;
using BasisNetworkClient;
using BasisNetworkServer.BasisNetworking;
using System;
using UnityEngine;
using static SerializableBasis;
public static class BasisNetworkEvents
{
    public static async void NetworkReceiveEvent(NetPeer peer, NetPacketReader Reader, byte channel, DeliveryMethod deliveryMethod)
    {
        switch (channel)
        {
            case BasisNetworkCommons.FallChannel:
                if (deliveryMethod == DeliveryMethod.Unreliable)
                {
                    if (Reader.TryGetByte(out byte Byte))
                    {
                        NetworkReceiveEvent(peer, Reader, Byte, deliveryMethod);
                    }
                    else
                    {
                        BNL.LogError($"Unknown channel no data remains: {channel} " + Reader.AvailableBytes);
                        Reader.Recycle();
                    }
                }
                else
                {
                    BNL.LogError($"Unknown channel: {channel} " + Reader.AvailableBytes);
                    Reader.Recycle();
                }
                break;
            case BasisNetworkCommons.AuthIdentityChannel:
                AuthIdentityMessage(peer, Reader, channel);
                break;
            case BasisNetworkCommons.DisconnectionChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisNetworkHandleRemoval.HandleDisconnection(Reader);
                Reader.Recycle();
                break;
            case BasisNetworkCommons.AvatarChangeMessageChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkHandleAvatar.HandleAvatarChangeMessage(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.CreateRemotePlayerChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisRemotePlayerFactory.HandleCreateRemotePlayer(Reader, BasisNetworkManagement.instantiationParameters);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                //same as remote player but just used at the start
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    //this one is called first and is also generally where the issues are.
                    BasisRemotePlayerFactory.HandleCreateRemotePlayer(Reader, BasisNetworkManagement.instantiationParameters);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.GetCurrentOwnerRequestChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkGenericMessages.HandleOwnershipResponse(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.ChangeCurrentOwnerRequestChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkGenericMessages.HandleOwnershipTransfer(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.RemoveCurrentOwnerRequestChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkGenericMessages.HandleOwnershipRemove(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.VoiceChannel:
#if UNITY_SERVER
                Reader.Recycle();
#else
                //released inside
                await BasisNetworkHandleVoice.HandleAudioUpdate(Reader);
#endif
                break;
            case BasisNetworkCommons.PlayerAvatarChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisNetworkHandleAvatar.HandleAvatarUpdate(Reader,deliveryMethod);
                Reader.Recycle();
                break;
            case BasisNetworkCommons.SceneChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkGenericMessages.HandleServerSceneDataMessage(Reader, deliveryMethod);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.AvatarChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkGenericMessages.HandleServerAvatarDataMessage(Reader, deliveryMethod);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.NetIDAssignsChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkGenericMessages.MassNetIDAssign(Reader, deliveryMethod);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.netIDAssignChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkGenericMessages.NetIDAssign(Reader, deliveryMethod);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.LoadResourceChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(async () =>
                {
                    await BasisNetworkGenericMessages.LoadResourceMessage(Reader, deliveryMethod);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.UnloadResourceChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkGenericMessages.UnloadResourceMessage(Reader, deliveryMethod);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.AdminChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkModeration.AdminMessage(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.metaDataChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                ServerMetaDataMessage SMDM = new ServerMetaDataMessage();
                SMDM.Deserialize(Reader);
                Reader.Recycle();

                BasisLocalPlayer.Instance.UUID = SMDM.ClientMetaDataMessage.playerUUID;
                BasisLocalPlayer.Instance.DisplayName = SMDM.ClientMetaDataMessage.playerDisplayName;
                BasisNetworkManagement.ServerMetaDataMessage = SMDM;

                break;
            case BasisNetworkCommons.StoreDatabaseChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                DatabasePrimativeMessage DatabasePrimativeMessage = new DatabasePrimativeMessage();
                DatabasePrimativeMessage.Deserialize(Reader);
                Reader.Recycle();
                BasisNetworkManagement.OnRequestServerSideDatabaseItem?.Invoke(DatabasePrimativeMessage);
                break;
            case BasisNetworkCommons.ServerStatisticsChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                IncomingData(Reader);
                Reader.Recycle();
                break;
            default:
                BNL.LogError($"this Channel was not been implemented {channel}");
                Reader.Recycle();
                break;
        }
    }
    public static Action<BasisNetworkStatistics.Snapshot> Snapshotdata;
    public static void IncomingData(NetPacketReader Reader)
    {
        BasisNetworkStatistics.Snapshot Snapshot = BasisNetworkStatistics.Snapshot.Decode(Reader.GetRemainingBytesSegment(), true);
        BasisDeviceManagement.EnqueueOnMainThread(() =>
        {
            Snapshotdata?.Invoke(Snapshot);
        });
    }
    public static void RequestStatFrames()
    {
        NetDataWriter Writer = new NetDataWriter();
        Writer.Put(true);
        BasisNetworkConnection.LocalPlayerPeer.Send(Writer, BasisNetworkCommons.ServerStatisticsChannel, DeliveryMethod.ReliableOrdered);
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAvatarData, Writer.Length);
        BasisDebug.Log("RequestStatFrames");
    }

    public static void StopStatFrames()
    {
        NetDataWriter Writer = new NetDataWriter();
        Writer.Put(false);
        BasisNetworkConnection.LocalPlayerPeer?.Send(Writer, BasisNetworkCommons.ServerStatisticsChannel, DeliveryMethod.ReliableOrdered);
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAvatarData, Writer.Length);
        BasisDebug.Log("StopStatFrames");
    }
    public static void AuthIdentityMessage(NetPeer peer, NetPacketReader Reader, byte channel)
    {
        BasisDebug.Log("Auth is being requested by server!");
        if (ValidateSize(Reader, peer, channel) == false)
        {
            BasisDebug.Log("Auth Failed");
            Reader.Recycle();
            return;
        }
        BasisDebug.Log("Validated Size " + Reader.AvailableBytes);
        if (BasisDIDAuthIdentityClient.IdentityMessage(peer, Reader, out NetDataWriter Writer))
        {
            BasisDebug.Log("Sent Identity To Server!");
            BasisNetworkConnection.LocalPlayerPeer.Send(Writer, BasisNetworkCommons.AuthIdentityChannel, DeliveryMethod.ReliableOrdered);
            Reader.Recycle();
        }
        else
        {
            BasisDebug.LogError("Failed Identity Message!");
            Reader.Recycle();
            DisconnectInfo info = new DisconnectInfo
            {
                Reason = DisconnectReason.ConnectionRejected,
                SocketErrorCode = System.Net.Sockets.SocketError.AccessDenied,
                AdditionalData = null
            };
            PeerDisconnectedEvent(peer, info);
        }
        BasisDebug.Log("Completed");
    }
    public static bool ValidateSize(NetPacketReader reader, NetPeer peer, byte channel)
    {
        if (reader.AvailableBytes == 0)
        {
            BasisDebug.LogError($"Missing Data from peer! {peer.Id} with channel ID {channel}");
            return false;
        }
        return true;
    }
    public static void HandleDisconnectionReason(DisconnectInfo disconnectInfo)
    {
        if (disconnectInfo.Reason == DisconnectReason.RemoteConnectionClose)
        {
            if (disconnectInfo.AdditionalData.TryGetString(out string Reason))
            {
                BasisUINotification.OpenNotification(Reason, false, Vector3.zero);
                BasisDebug.LogError(Reason);
            }
        }
        else
        {
            BasisUINotification.OpenNotification(disconnectInfo.Reason.ToString(), false, Vector3.zero);
            BasisDebug.LogError(disconnectInfo.Reason.ToString());
        }
        if (BasisSetUserName.Instance != null && BasisSetUserName.Instance.Ready != null)
        {
            BasisSetUserName.Instance.Ready.interactable = true;
        }
    }
    public static void PeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        BasisDebug.Log($"Client disconnected from server [{peer.Id}]");
        if (peer == BasisNetworkConnection.LocalPlayerPeer)
        {
            BasisNetworkConnection.HandleDisconnection(peer, disconnectInfo);
        }
        if (BasisSetUserName.Instance != null && BasisSetUserName.Instance.Ready != null)
        {
            BasisSetUserName.Instance.Ready.interactable = true;
        }
    }
}
