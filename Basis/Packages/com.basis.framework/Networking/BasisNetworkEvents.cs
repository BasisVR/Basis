using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Profiler;
using BasisNetworkClient;
using BasisNetworkServer.BasisNetworking;
using BasisPermissions;
using K4os.Compression.LZ4;
using System;
using System.Buffers;
using static SerializableBasis;
public static class BasisNetworkEvents
{
    private static bool _coreHandlersRegistered;

    static BasisNetworkEvents()
    {
        EnsureInitialized();
    }

    /// <summary>Registers the core channel handlers if not already done. Safe to call repeatedly.</summary>
    public static void EnsureInitialized()
    {
        if (_coreHandlersRegistered)
        {
            return;
        }
        _coreHandlersRegistered = true;
        RegisterCoreHandlers();
    }

    public static void NetworkReceiveEvent(NetPeer peer, NetPacketReader Reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            BasisClientMessageHandler handler = BasisClientMessageRegistry.ResolveCore(channel);
            if (handler != null)
            {
                handler(peer, Reader, channel, deliveryMethod);
            }
            else if (BasisNetworkCommons.IsPluginChannel(channel))
            {
                if (!BasisClientMessageRegistry.DispatchPlugin(peer, Reader, channel, deliveryMethod))
                {
                    BNL.LogError($"Unknown plugin id on channel {channel}");
                    Reader.Recycle();
                }
            }
            else
            {
                BNL.LogError($"this Channel was not been implemented {channel}");
                Reader.Recycle();
            }
        }
        catch (Exception ex)
        {
            BNL.LogError($"Dropping malformed message on channel {channel}: {ex.Message}");
            Reader.Recycle(true);
        }
    }

    private static void RegisterCoreHandlers()
    {
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.AnnounceVoiceChannel, async (peer, Reader, channel, deliveryMethod) =>
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.AnnounceVoice, Reader.AvailableBytes);
#if UNITY_SERVER
            Reader.Recycle(true);
#else
            //released inside
            await BasisNetworkHandleVoice.HandleAnnounceAudioUpdate(Reader);
#endif
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.AuthIdentityChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Authentication, Reader.AvailableBytes);
            AuthIdentityMessage(peer, Reader, channel);
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.DisconnectionChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Disconnection, Reader.AvailableBytes);
            BasisNetworkHandleRemoval.HandleDisconnection(Reader);
            Reader.Recycle();
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.AvatarChangeMessageChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try { BasisNetworkHandleAvatar.HandleAvatarChangeMessage(Reader); }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.CreateRemotePlayerChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            // This body used to be wrapped in EnqueueOnMainThread, which put the DECODE (Deflate,
            // strings, avatar blob, pose unpack) on the frame thread rather than just the spawn.
            // Copy the bytes out, free the pooled reader immediately, and let the avatar load
            // thread decode; it re-enters through the budgeted lifecycle queue with a record the
            // spawn step consumes without parsing anything. Decoding inline here is not an option
            // — this is LiteNetLib's receive thread and blocking it stalls every other channel.
            //
            // JoiningPlayers is marked from the leading ushort of the record rather than after the
            // decode, so per-player traffic arriving behind this packet still finds the id present
            // — sooner than it did when the mark waited on an EnqueueOnMainThread hop.
            try
            {
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, Reader.AvailableBytes);
                BasisNetworkPlayers.JoiningPlayers.TryAdd(Reader.PeekUShort(), 0);
                BasisAvatarLoadThread.SubmitSpawn(Reader.GetRemainingBytes());
            }
            catch (Exception ex)
            {
                BNL.LogError($"Dropping corrupt remote-player spawn packet: {ex.Message}");
            }
            finally
            {
                Reader.Recycle();
            }
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            // Same as remote player but just used at the start — arrives as a compressed batch of
            // players. This is the single heaviest decode the client ever does: a Deflate inflate
            // of up to 32 KB per batch plus one full record decode per player already present, and
            // it was running on the frame thread. Unlike the single-spawn channel the ids sit
            // inside the compressed payload, so JoiningPlayers is marked by the load thread as
            // each record comes out; a pose or voice packet that beats it there costs a log line,
            // never a dropped player.
            try
            {
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, Reader.AvailableBytes);
                BasisAvatarLoadThread.SubmitSpawnBatch(Reader.GetRemainingBytes());
            }
            catch (Exception ex)
            {
                BNL.LogError($"Dropping corrupt remote-player spawn packet: {ex.Message}");
            }
            finally
            {
                Reader.Recycle();
            }
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.GetCurrentOwnerRequestChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.GetOwnership, Reader.AvailableBytes);
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try { BasisNetworkGenericMessages.HandleOwnershipResponse(Reader); }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ChangeOwnership, Reader.AvailableBytes);
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try { BasisNetworkGenericMessages.HandleOwnershipTransfer(Reader); }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.RemoveOwnership, Reader.AvailableBytes);
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try { BasisNetworkGenericMessages.HandleOwnershipRemove(Reader); }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.VoiceChannel, async (peer, Reader, channel, deliveryMethod) =>
        {
#if UNITY_SERVER
            Reader.Recycle(true);
#else
            //released inside
            await BasisNetworkHandleVoice.HandleAudioUpdate(Reader, false);
#endif
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.VoiceLargeChannel, async (peer, Reader, channel, deliveryMethod) =>
        {
#if UNITY_SERVER
            Reader.Recycle(true);
#else
            //released inside
            await BasisNetworkHandleVoice.HandleAudioUpdate(Reader, true);
#endif
        });

        BasisClientMessageHandler avatarUpdate = (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.PlayerAvatar, Reader.AvailableBytes);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, Reader.AvailableBytes);
            BasisNetworkHandleAvatar.HandleAvatarUpdate(Reader, channel);
            Reader.Recycle();
        };
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarVeryLowChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarVeryLowAdditionalChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarLowChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarLowAdditionalChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarMediumChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarMediumAdditionalChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarHighChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarHighAdditionalChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarVeryLowAdditionalLargeChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarLowLargeChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarLowAdditionalLargeChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarMediumLargeChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarMediumAdditionalLargeChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarHighLargeChannel, avatarUpdate);
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.PlayerAvatarHighAdditionalLargeChannel, avatarUpdate);

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.CompressedAvatarBundleChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.PlayerAvatar, Reader.AvailableBytes);
            BasisNetworkHandleCompressedBundle.Handle(Reader);
            Reader.Recycle();
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.DeltaAvatarChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.PlayerAvatar, Reader.AvailableBytes);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, Reader.AvailableBytes);
            // A dropped delta (no receiver yet, no baseline yet) deliberately leaves its body unread —
            // both are routine at join. Tell Recycle so the parse-bug warning stays meaningful.
            bool leftoverIsExpected = BasisNetworkHandleAvatarDelta.Handle(Reader);
            Reader.Recycle(leftoverIsExpected);
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.SceneChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            var serverSceneDataMessage = new SerializableBasis.ServerSceneDataMessage();
            serverSceneDataMessage.Deserialize(Reader);
            Reader.Recycle();
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                BasisNetworkGenericMessages.DispatchServerSceneDataMessage(serverSceneDataMessage, deliveryMethod, false);
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.AvatarChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try { BasisNetworkGenericMessages.HandleServerAvatarDataMessage(Reader, deliveryMethod); }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.DirectSceneServerChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            var serverSceneDataMessage = new SerializableBasis.ServerSceneDataMessage();
            serverSceneDataMessage.Deserialize(Reader);
            Reader.Recycle();
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                BasisNetworkGenericMessages.DispatchServerSceneDataMessage(serverSceneDataMessage, deliveryMethod, true);
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.DirectAvatarServerChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try { BasisNetworkGenericMessages.HandleServerAvatarDataMessage(Reader, deliveryMethod, true); }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.NetIDAssignsChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.NetIDAssigns, Reader.AvailableBytes);
                    BasisNetworkGenericMessages.MassNetIDAssign(Reader, deliveryMethod);
                }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.netIDAssignChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.NetIDAssign, Reader.AvailableBytes);
                    BasisNetworkGenericMessages.NetIDAssign(Reader, deliveryMethod);
                }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.LoadResourceChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(async () =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LoadResource, Reader.AvailableBytes);
                    await BasisNetworkGenericMessages.LoadResourceMessage(Reader, deliveryMethod);
                }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.UnloadResourceChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(async () =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.UnloadResource, Reader.AvailableBytes);
                    await BasisNetworkGenericMessages.UnloadResourceMessage(Reader, deliveryMethod);
                }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.ModifyResourceChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(async () =>
            {
                try { await BasisNetworkGenericMessages.ModifyResourceMessage(Reader, deliveryMethod); }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.ServerLibraryChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            // Decompress + deserialize on the receive thread (same shape as the
            // ServerSideSyncPlayer channel above); only the publish, which raises
            // main-thread UI events, hops to the frame thread.
            try
            {
                if (TryDecodeServerLibrary(Reader, out ServerLibraryMessage libraryMessage))
                {
                    BasisDeviceManagement.EnqueueOnMainThread(() =>
                    {
                        BasisServerProvidedItems.SetFromServer(libraryMessage.Items);
                    });
                }
            }
            finally
            {
                Reader.Recycle();
            }
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.AdminChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Admin, Reader.AvailableBytes);
                    BasisNetworkModeration.AdminMessage(Reader);
                }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.ContentShareChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try
                {
                    // Multiplexed: first byte selects drop vs cleanup (ContentShareSub_*).
                    if (Reader.TryGetByte(out byte sub))
                    {
                        if (sub == BasisNetworkCommons.ContentShareSub_Cleanup)
                        {
                            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ContentShareCleanup, Reader.AvailableBytes);
                            BasisContentShareManager.HandleContentShareCleanup(Reader);
                        }
                        else
                        {
                            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ContentShare, Reader.AvailableBytes);
                            BasisContentShareManager.HandleContentShareMessage(Reader);
                        }
                    }
                }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.ChatChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Chat, Reader.AvailableBytes);
                    BasisNetworkHandleChat.HandleServerChatMessage(Reader);
                }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.metaDataChannel, (peer, Reader, channel, deliveryMethod) =>
        {
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
#if UNITY_SERVER
            BasisVerticalSyncModule.ApplyHeadlessFrameRate();
#endif
            BasisNetworkManagement.LocalPermissions = SMDM.GetPermissions();
            BasisNetworkManagement.OnlocalPermissionsChanged?.Invoke();
            if (BasisNetworkConnection.LocalPlayerIsConnected == false)
            {
                BasisNetworkConnection.SetupLocalPlayer(peer);
            }
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.ServerStatisticsChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerStatistics, Reader.AvailableBytes);
            IncomingData(Reader);
            Reader.Recycle();
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.CameraPIPStateChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.CameraPIPState, Reader.AvailableBytes);
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                CameraPIPStateMessage pipState = new CameraPIPStateMessage();
                try { pipState.Deserialize(Reader); }
                finally { Reader.Recycle(); }
                BasisNetworkPIPCameraDriver.OnRemotePIPState(pipState);
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.CameraPIPPositionChannel, (peer, Reader, channel, deliveryMethod) =>
        {
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
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.SpawnPreloadedChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(async () =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SpawnPreloaded, Reader.AvailableBytes);
                    await BasisNetworkGenericMessages.SpawnPreloadedMessage(Reader, deliveryMethod);
                }
                finally { Reader.Recycle(); }
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.P2PChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisP2PManager.HandleServerMessage(Reader);
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.EventsChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            try
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
                    case BasisNetworkCommons.EventType_PlayerTempBlock:
                        ushort tempBlockSenderId = Reader.GetUShort();
                        bool tempBlockIsBlocked = Reader.GetBool();
                        Reader.Recycle();
                        BasisDeviceManagement.EnqueueOnMainThread(() =>
                        {
                            BasisNetworkHandleTempBlock.OnRemoteTempBlockReceived(tempBlockSenderId, tempBlockIsBlocked);
                        });
                        break;
                    case BasisNetworkCommons.EventType_AvatarRateChange:
                        ushort rateSenderId = Reader.GetUShort();
                        ushort rateIntervalMs = Reader.GetUShort();
                        Reader.Recycle();
                        Basis.Scripts.Networking.BasisAvatarRateRegistry.UpdateRemoteRate(rateSenderId, rateIntervalMs);
                        break;
                    case BasisNetworkCommons.EventType_PlayerChatTyping:
                        ushort typingSenderId = Reader.GetUShort();
                        bool isTyping = Reader.GetBool();
                        Reader.Recycle();
                        BasisDeviceManagement.EnqueueOnMainThread(() =>
                        {
                            BasisNetworkHandleChatTyping.OnRemoteTypingStateReceived(typingSenderId, isTyping);
                        });
                        break;
                    case BasisNetworkCommons.EventType_TalkModeChanged:
                        ushort talkModeSenderId = Reader.GetUShort();
                        byte talkModeValue = Reader.GetByte();
                        Reader.Recycle();
                        BasisDeviceManagement.EnqueueOnMainThread(() =>
                        {
                            Basis.Scripts.Networking.BasisTalkModeManager.OnRemoteTalkModeReceived(talkModeSenderId, talkModeValue);
                        });
                        break;
                    case BasisNetworkCommons.EventType_MuteStateChanged:
                        ushort muteSenderId = Reader.GetUShort();
                        byte muteValue = Reader.GetByte();
                        Reader.Recycle();
                        BasisDeviceManagement.EnqueueOnMainThread(() =>
                        {
                            Basis.Scripts.Networking.BasisTalkModeManager.OnRemoteMuteReceived(muteSenderId, muteValue != 0);
                        });
                        break;
                    case BasisNetworkCommons.EventType_VoiceRecordRequest:
                        ushort voiceRecRequesterId = Reader.GetUShort();
                        byte voiceRecReqPurpose = Reader.GetByte();
                        Reader.Recycle();
                        BasisDeviceManagement.EnqueueOnMainThread(() =>
                        {
                            BasisNetworkHandleVoiceRecord.OnRemoteRecordRequestReceived(voiceRecRequesterId, voiceRecReqPurpose);
                        });
                        break;
                    case BasisNetworkCommons.EventType_VoiceRecordConsent:
                        ushort voiceRecResponderId = Reader.GetUShort();
                        byte voiceRecState = Reader.GetByte();
                        byte voiceRecConsentPurpose = Reader.GetByte();
                        Reader.Recycle();
                        BasisDeviceManagement.EnqueueOnMainThread(() =>
                        {
                            BasisNetworkHandleVoiceRecord.OnRemoteConsentReceived(voiceRecResponderId, voiceRecState, voiceRecConsentPurpose);
                        });
                        break;
                    case BasisNetworkCommons.EventType_JiggleGrab:
                        byte jiggleGrabOp = Reader.GetByte();
                        ushort jiggleGrabSenderId = Reader.GetUShort();
                        ushort jiggleGrabPayloadId = Reader.GetUShort();
                        byte jiggleGrabRigIndex = 0;
                        ushort jiggleGrabPointIndex = 0;
                        byte jiggleGrabHand = 0;
                        uint jiggleGrabBoneHash = 0;
                        UnityEngine.Vector3 jiggleGrabOffset = default;
                        if (jiggleGrabOp != BasisNetworkCommons.JiggleGrabOp_Deny)
                        {
                            jiggleGrabRigIndex = Reader.GetByte();
                            jiggleGrabPointIndex = Reader.GetUShort();
                        }
                        if (jiggleGrabOp == BasisNetworkCommons.JiggleGrabOp_Start)
                        {
                            jiggleGrabHand = Reader.GetByte();
                            jiggleGrabBoneHash = Reader.GetUInt();
                            jiggleGrabOffset = new UnityEngine.Vector3(
                                UnityEngine.Mathf.HalfToFloat(Reader.GetUShort()),
                                UnityEngine.Mathf.HalfToFloat(Reader.GetUShort()),
                                UnityEngine.Mathf.HalfToFloat(Reader.GetUShort()));
                        }
                        Reader.Recycle();
                        BasisDeviceManagement.EnqueueOnMainThread(() =>
                        {
                            BasisNetworkHandleJiggleGrab.OnRemoteJiggleGrabReceived(jiggleGrabOp, jiggleGrabSenderId, jiggleGrabPayloadId,
                                jiggleGrabRigIndex, jiggleGrabPointIndex, jiggleGrabHand, jiggleGrabBoneHash, jiggleGrabOffset);
                        });
                        break;
                    default:
                        BNL.LogError($"Unknown EventsChannel event type: {eventType}");
                        Reader.Recycle();
                        break;
                }
            }
            catch (Exception ex)
            {
                BNL.LogError($"Malformed EventsChannel message from peer {peer.Id}: {ex.Message}");
                if (Reader.IsNull == false)
                {
                    Reader.Recycle();
                }
            }
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.RegistryControlChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (Reader.TryGetByte(out byte sub) && sub == BasisNetworkCommons.RegistrySub_Supply)
            {
                BasisMessageSupply supply = new BasisMessageSupply();
                supply.Deserialize(Reader);
                Reader.Recycle();
                BasisClientMessageRegistry.ApplySupply(supply, peer);
            }
            else
            {
                Reader.Recycle();
            }
        });
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
    // Reused across both stat-frame calls: RequestStatFrames fires on a 0.1s timer while the
    // stats view is open, so a fresh writer per call was garbage every tick for one bool.
    private static readonly NetDataWriter StatFrameWriter = new NetDataWriter();

    public static void RequestStatFrames()
    {
        NetDataWriter Writer = StatFrameWriter;
        Writer.Reset();
        Writer.Put(true);
        BasisNetworkConnection.LocalPlayerPeer.Send(Writer, BasisNetworkCommons.ServerStatisticsChannel, Basis.Network.Core.DeliveryMethod.ReliableOrdered);
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAvatarData, Writer.Length);
        BasisDebug.Log("RequestStatFrames");
    }

    public static void StopStatFrames()
    {
        NetDataWriter Writer = StatFrameWriter;
        Writer.Reset();
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
    // Reused on the LiteNetLib receive thread (every ServerLibraryChannel receive
    // decodes on it) — keeps the per-join NetDataReader allocation out of GC.
    // Library messages arrive sequentially on that one thread, so a single
    // instance is enough.
    private static NetDataReader _libraryPayloadReader;

    private static bool TryDecodeServerLibrary(NetPacketReader reader, out ServerLibraryMessage libraryMessage)
    {
        libraryMessage = default;
        // Wire format from BasisNetworkServerLibrary:
        //   [u16 rawLen][u16 compressedLen][bytes payload]
        // compressedLen == 0 means the payload is the raw message bytes.
        ushort rawLen = reader.GetUShort();
        ushort compressedLen = reader.GetUShort();
        if (rawLen == 0) return false;

        byte[] payload = ArrayPool<byte>.Shared.Rent(rawLen);
        try
        {
            if (compressedLen == 0)
            {
                reader.GetBytes(payload, rawLen);
            }
            else
            {
                byte[] compressed = ArrayPool<byte>.Shared.Rent(compressedLen);
                try
                {
                    reader.GetBytes(compressed, compressedLen);
                    int decoded = LZ4Codec.Decode(
                        compressed, 0, compressedLen,
                        payload, 0, rawLen);
                    if (decoded != rawLen)
                    {
                        BasisDebug.LogError(
                            $"Server library decompression mismatch: expected {rawLen} bytes, got {decoded}");
                        return false;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                }
            }

            NetDataReader payloadReader = _libraryPayloadReader ??= new NetDataReader();
            payloadReader.SetSource(payload, 0, rawLen);
            libraryMessage = new ServerLibraryMessage();
            libraryMessage.Deserialize(payloadReader);
            // Items array becomes BasisServerProvidedItems' source of truth — fine
            // to release the byte buffer once Deserialize has copied strings out.
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
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
    /// <summary>
    /// Reads a structured, kind-tagged reject payload (see BasisNetworkCommons.RejectKind_*) that a
    /// current server attaches to a rejected connection. Non-consuming on failure so the caller can
    /// fall back to PeekString for older servers that send a bare reason string.
    /// </summary>
    private static bool TryReadStructuredReject(NetDataReader data, out byte kind, out ushort aux0, out ushort aux1, out string message)
    {
        kind = 0; aux0 = 0; aux1 = 0; message = null;
        if (data == null) return false;
        byte[] raw = data.RawData;
        int p = data.Position;
        if (raw == null || data.AvailableBytes < 9) return false; // magic(4)+kind(1)+aux0(2)+aux1(2)
        uint magic = (uint)(raw[p] | (raw[p + 1] << 8) | (raw[p + 2] << 16) | (raw[p + 3] << 24));
        if (magic != BasisNetworkCommons.RejectMagic) return false;
        try
        {
            data.GetUInt();            // magic
            kind = data.GetByte();
            aux0 = data.GetUShort();
            aux1 = data.GetUShort();
            message = data.AvailableBytes > 0 ? data.GetString() : null;
            return true;
        }
        catch
        {
            message = null;
            return false;
        }
    }

    public static void HandleDisconnectionReason(DisconnectInfo disconnectInfo)
    {
        if (disconnectInfo.Reason == DisconnectReason.DisconnectPeerCalled)
        {
            BasisDebug.Log($"Disconnected locally [{disconnectInfo.Reason}]", BasisDebug.LogTag.Networking);
            return;
        }
        if (BasisNetworkConnectionWatchdog.SuppressDisconnectDialogue)
        {
            BasisDebug.Log($"Disconnected [{disconnectInfo.Reason}]; the connection watchdog owns the notification.", BasisDebug.LogTag.Networking);
            return;
        }
#if UNITY_SERVER
        bool canShowMenu = !UnityEngine.Application.isBatchMode;
#endif

        if (disconnectInfo.Reason == DisconnectReason.RemoteConnectionClose)
        {
#if UNITY_SERVER
            // PeekString is now defensive — a malformed additional-data payload
            // returns "" instead of throwing. Read once and re-use.
            string reason = disconnectInfo.AdditionalData?.PeekString();
            if (!string.IsNullOrEmpty(reason))
            {
                if (canShowMenu)
                {
                    BasisMainMenu.Open();
                    if (BasisMainMenu.Instance != null)
                    {
                        BasisMainMenu.Instance.OpenDialogue(BasisLocalization.Get("menu.servers.connection.title"), reason, BasisLocalization.Get("ui.ok"), value =>
                        {
                        }, category: BasisNotificationCategory.Network);
                    }
                }
                BasisDebug.LogError(reason);
            }
            else
            {
                BasisDebug.Log($"Unexpected Failure Of Reason {disconnectInfo.Reason}");
            }
#else
            // Read the reason once (PeekString is now defensive against
            // malformed additional-data, but reading it twice still wastes a
            // GetString call).
            string Reason = disconnectInfo.AdditionalData?.PeekString();
            if (!string.IsNullOrEmpty(Reason))
            {
                BasisMainMenu.Open();
                if (BasisMainMenu.Instance != null)
                {
                    BasisMainMenu.Instance.OpenDialogue(BasisLocalization.Get("menu.servers.connection.title"), Reason, BasisLocalization.Get("ui.ok"), value =>
                    {
                    }, category: BasisNotificationCategory.Network);
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
            bool rejected = disconnectInfo.Reason == DisconnectReason.ConnectionRejected;
            NetDataReader extra = disconnectInfo.AdditionalData;

            string title;
            string body;

            // A current server attaches a structured, kind-tagged reject payload (version mismatch,
            // server full, ...). Older servers send a bare reason string, handled by the else path.
            if (rejected && TryReadStructuredReject(extra, out byte kind, out ushort aux0, out _, out string structuredMsg))
            {
                switch (kind)
                {
                    case BasisNetworkCommons.RejectKind_VersionMismatch:
                    {
                        ushort localVersion = BasisNetworkVersion.ServerVersion;
                        if (aux0 > localVersion)
                        {
                            title = BasisLocalization.Get("menu.servers.reject.updateClient.title");
                            body = BasisLocalization.Get("menu.servers.reject.updateClient.body", aux0, localVersion);
                        }
                        else
                        {
                            title = BasisLocalization.Get("menu.servers.reject.updateServer.title");
                            body = BasisLocalization.Get("menu.servers.reject.updateServer.body", aux0, localVersion);
                        }
                        break;
                    }
                    case BasisNetworkCommons.RejectKind_ServerFull:
                        title = BasisLocalization.Get("menu.servers.reject.serverFull.title");
                        body = !string.IsNullOrEmpty(structuredMsg) ? structuredMsg : BasisLocalization.Get("menu.servers.reject.serverFull.body");
                        break;
                    default:
                        title = BasisLocalization.Get("menu.servers.reject.title");
                        body = !string.IsNullOrEmpty(structuredMsg) ? structuredMsg : BasisLocalization.Get("menu.servers.reject.body");
                        break;
                }
            }
            else
            {
                // Legacy bare-string reject, or a non-rejection disconnect (timeout, etc.). PeekString
                // is defensive: an empty/malformed payload yields "".
                string reason = extra?.PeekString();
                title = rejected
                    ? BasisLocalization.Get("menu.servers.reject.title")
                    : BasisLocalization.Get("menu.servers.disconnected.title");
                body = !string.IsNullOrEmpty(reason)
                    ? reason
                    : (rejected
                        ? BasisLocalization.Get("menu.servers.reject.bodyUnknown")
                        : disconnectInfo.Reason.ToString());
            }

#if UNITY_SERVER
            if (canShowMenu)
#endif
            {
                BasisMainMenu.Open();
                if (BasisMainMenu.Instance != null)
                {
                    BasisMainMenu.Instance.OpenDialogue(title, body, BasisLocalization.Get("ui.ok"), value =>
                    {
                    }, category: BasisNotificationCategory.Network);
                }
            }

            BasisDebug.LogError($"{title}: {body}");
        }
    }
}
