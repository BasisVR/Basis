using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Profiler;
using BasisNetworkClient;
using BasisNetworkServer.BasisNetworking;
using BasisPermissions;
using System;
using static SerializableBasis;
public static class BasisNetworkEvents
{
    public static async void NetworkReceiveEvent(NetPeer peer, NetPacketReader Reader, byte channel, DeliveryMethod deliveryMethod)
    {
        switch (channel)
        {
            case BasisNetworkCommons.ShoutVoiceChannel:
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ShoutVoice, Reader.AvailableBytes);
#if UNITY_SERVER
                Reader.Recycle();
#else
                //released inside
                await BasisNetworkHandleVoice.HandleShoutAudioUpdate(Reader);
#endif
                break;
            case BasisNetworkCommons.AuthIdentityChannel:
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Authentication, Reader.AvailableBytes);
                AuthIdentityMessage(peer, Reader, channel);
                break;
            case BasisNetworkCommons.DisconnectionChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Disconnection, Reader.AvailableBytes);
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.GetOwnership, Reader.AvailableBytes);
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ChangeOwnership, Reader.AvailableBytes);
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.RemoveOwnership, Reader.AvailableBytes);
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
                await BasisNetworkHandleVoice.HandleAudioUpdate(Reader, false);
#endif
                break;
            case BasisNetworkCommons.VoiceLargeChannel:
#if UNITY_SERVER
                Reader.Recycle();
#else
                //released inside
                await BasisNetworkHandleVoice.HandleAudioUpdate(Reader, true);
#endif
                break;
            case BasisNetworkCommons.PlayerAvatarVeryLowChannel:
            case BasisNetworkCommons.PlayerAvatarVeryLowAdditionalChannel:
            case BasisNetworkCommons.PlayerAvatarLowChannel:
            case BasisNetworkCommons.PlayerAvatarLowAdditionalChannel:
            case BasisNetworkCommons.PlayerAvatarMediumChannel:
            case BasisNetworkCommons.PlayerAvatarMediumAdditionalChannel:
            case BasisNetworkCommons.PlayerAvatarHighChannel:
            case BasisNetworkCommons.PlayerAvatarHighAdditionalChannel:
            case BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel:
            case BasisNetworkCommons.PlayerAvatarVeryLowAdditionalLargeChannel:
            case BasisNetworkCommons.PlayerAvatarLowLargeChannel:
            case BasisNetworkCommons.PlayerAvatarLowAdditionalLargeChannel:
            case BasisNetworkCommons.PlayerAvatarMediumLargeChannel:
            case BasisNetworkCommons.PlayerAvatarMediumAdditionalLargeChannel:
            case BasisNetworkCommons.PlayerAvatarHighLargeChannel:
            case BasisNetworkCommons.PlayerAvatarHighAdditionalLargeChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.PlayerAvatar, Reader.AvailableBytes);
                BasisNetworkHandleAvatar.HandleAvatarUpdate(Reader, channel);
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
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.NetIDAssigns, Reader.AvailableBytes);
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
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.NetIDAssign, Reader.AvailableBytes);
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
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LoadResource, Reader.AvailableBytes);
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
                BasisDeviceManagement.EnqueueOnMainThread(async () =>
                {
                   BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.UnloadResource, Reader.AvailableBytes);
                   await BasisNetworkGenericMessages.UnloadResourceMessage(Reader, deliveryMethod);
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
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Admin, Reader.AvailableBytes);
                    BasisNetworkModeration.AdminMessage(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.ContentShareChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ContentShare, Reader.AvailableBytes);
                    BasisContentShareManager.HandleContentShareMessage(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.ContentShareCleanupChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ContentShareCleanup, Reader.AvailableBytes);
                    BasisContentShareManager.HandleContentShareCleanup(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.ChatChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Chat, Reader.AvailableBytes);
                    BasisNetworkHandleChat.HandleServerChatMessage(Reader);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.metaDataChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.PlayerMetaData, Reader.AvailableBytes);
                ServerMetaDataMessage SMDM = new ServerMetaDataMessage();
                SMDM.Deserialize(Reader);
                Reader.Recycle();

                BasisLocalPlayer.Instance.UUID = SMDM.ClientMetaDataMessage.playerUUID;
                BasisLocalPlayer.Instance.DisplayName = SMDM.ClientMetaDataMessage.playerDisplayName;
                BasisNetworkManagement.ServerMetaDataMessage = SMDM;
                BasisNetworkManagement.LocalPermissions = SMDM.GetPermissions();
                BasisNetworkManagement.OnlocalPermissionsChanged?.Invoke();
                break;
            case BasisNetworkCommons.StoreDatabaseChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.StoreDatabase, Reader.AvailableBytes);
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerStatistics, Reader.AvailableBytes);
                IncomingData(Reader);
                Reader.Recycle();
                break;
            case BasisNetworkCommons.CameraPIPStateChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.CameraPIPState, Reader.AvailableBytes);
                BasisDeviceManagement.EnqueueOnMainThread(() =>
                {
                    CameraPIPStateMessage pipState = new CameraPIPStateMessage();
                    pipState.Deserialize(Reader);
                    Reader.Recycle();
                    BasisNetworkPIPCameraDriver.OnRemotePIPState(pipState);
                });
                break;
            case BasisNetworkCommons.CameraPIPPositionChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.CameraPIPPosition, Reader.AvailableBytes);
                    CameraPIPPositionMessage pipPos = new CameraPIPPositionMessage();
                    pipPos.Deserialize(Reader);
                    Reader.Recycle();
                    BasisDeviceManagement.EnqueueOnMainThread(() =>
                    {
                        BasisNetworkPIPCameraDriver.OnRemotePIPPosition(pipPos);
                    });
                }
                break;
            case BasisNetworkCommons.SpawnPreloadedChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                BasisDeviceManagement.EnqueueOnMainThread(async () =>
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SpawnPreloaded, Reader.AvailableBytes);
                    await BasisNetworkGenericMessages.SpawnPreloadedMessage(Reader, deliveryMethod);
                    Reader.Recycle();
                });
                break;
            case BasisNetworkCommons.EventsChannel:
                if (ValidateSize(Reader, peer, channel) == false)
                {
                    Reader.Recycle();
                    return;
                }
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Events, Reader.AvailableBytes);
                    byte eventType = Reader.GetByte();
                    switch (eventType)
                    {
                        case BasisNetworkCommons.EventType_CameraShutterSound:
                            CameraShutterSoundMessage shutterMsg = new CameraShutterSoundMessage();
                            shutterMsg.Deserialize(Reader);
                            Reader.Recycle();
                            BasisDeviceManagement.EnqueueOnMainThread(() =>
                            {
                                BasisNetworkPIPCameraDriver.OnRemoteShutterSound(shutterMsg);
                            });
                            break;
                        case BasisNetworkCommons.EventType_CameraCountdown:
                            CameraCountdownMessage countdownMsg = new CameraCountdownMessage();
                            countdownMsg.Deserialize(Reader);
                            Reader.Recycle();
                            BasisDeviceManagement.EnqueueOnMainThread(() =>
                            {
                                BasisNetworkPIPCameraDriver.OnRemoteCountdown(countdownMsg);
                            });
                            break;
                        default:
                            BNL.LogError($"Unknown EventsChannel event type: {eventType}");
                            Reader.Recycle();
                            break;
                    }
                }
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
        BasisNetworkConnection.LocalPlayerPeer.Send(Writer, BasisNetworkCommons.ServerStatisticsChannel, Basis.Network.Core.DeliveryMethod.ReliableOrdered);
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAvatarData, Writer.Length);
        BasisDebug.Log("RequestStatFrames");
    }

    public static void StopStatFrames()
    {
        NetDataWriter Writer = new NetDataWriter();
        Writer.Put(false);
        BasisNetworkConnection.LocalPlayerPeer?.Send(Writer, BasisNetworkCommons.ServerStatisticsChannel, Basis.Network.Core.DeliveryMethod.ReliableOrdered);
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAvatarData, Writer.Length);
        BasisDebug.Log("StopStatFrames");
    }
    public static void AuthIdentityMessage(Basis.Network.Core.NetPeer peer, Basis.Network.Core.NetPacketReader Reader, byte channel)
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
            var info = new DisconnectInfo
            {
                Reason = DisconnectReason.ConnectionRejected,
                SocketErrorCode = System.Net.Sockets.SocketError.AccessDenied,
                AdditionalData = null
            };
            BasisNetworkConnection.HandleDisconnection(peer, info);
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
#if UNITY_SERVER
        bool canShowMenu = !UnityEngine.Application.isBatchMode;
#endif

        if (disconnectInfo.Reason == DisconnectReason.RemoteConnectionClose)
        {
#if UNITY_SERVER
            string reason = null;
            if (disconnectInfo.AdditionalData != null &&
                disconnectInfo.AdditionalData.TryGetString(out string parsedReason))
            {
                reason = parsedReason;
            }

            if (!string.IsNullOrEmpty(reason))
            {
                if (canShowMenu)
                {
                    BasisMainMenu.Open();
                    if (BasisMainMenu.Instance != null)
                    {
                        BasisMainMenu.Instance.OpenDialogue("Server Connection", reason, "ok", value =>
                        {
                        });
                    }
                }
                BasisDebug.LogError(reason);
            }
            else
            {
                BasisDebug.Log($"Unexpected Failure Of Reason {disconnectInfo.Reason}");
            }
#else
            if (disconnectInfo.AdditionalData != null && disconnectInfo.AdditionalData.TryGetString(out string Reason))
            {
                BasisMainMenu.Open();
                if (BasisMainMenu.Instance != null)
                {
                    BasisMainMenu.Instance.OpenDialogue("Server Connection", Reason, "ok", value =>
                    {
                    });
                }
                BasisDebug.LogError(Reason);
            }
            else
            {
                BasisDebug.Log($"Unexpected Failure Of Reason {disconnectInfo.Reason}");
            }
#endif
        }
        else
        {
#if UNITY_SERVER
            if (canShowMenu)
            {
                BasisMainMenu.Open();
                if (BasisMainMenu.Instance != null)
                {
                    BasisMainMenu.Instance.OpenDialogue("Server Disconnected", disconnectInfo.Reason.ToString(), "ok", value =>
                    {
                    });
                }
            }

            BasisDebug.LogError(disconnectInfo.Reason.ToString());
#else
            BasisMainMenu.Open();
            if (BasisMainMenu.Instance != null)
            {
                BasisMainMenu.Instance.OpenDialogue("Server Disconnected", disconnectInfo.Reason.ToString(), "ok", value =>
                {
                });
            }

            BasisDebug.LogError(disconnectInfo.Reason.ToString());
#endif
        }
    }
}
