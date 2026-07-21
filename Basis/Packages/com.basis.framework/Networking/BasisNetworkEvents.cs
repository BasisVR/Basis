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

    private static void RegisterCoreHandlers()
    {
        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.ShoutVoiceChannel, async (peer, Reader, channel, deliveryMethod) =>
        {
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ShoutVoice, Reader.AvailableBytes);
#if UNITY_SERVER
            Reader.Recycle(true);
#else
            //released inside
            await BasisNetworkHandleVoice.HandleShoutAudioUpdate(Reader);
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
                BasisNetworkHandleAvatar.HandleAvatarChangeMessage(Reader);
                Reader.Recycle();
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.CreateRemotePlayerChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            // Deserialize on the main thread (preserves existing thread affinity),
            // recycle the reader immediately, then enqueue the heavy
            // CreateRemotePlayer work into the budgeted lifecycle queue.
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, Reader.AvailableBytes);
                    ServerReadyMessage srm = new ServerReadyMessage();
                    srm.Deserialize(Reader);
                    BasisNetworkHandleRemoval.LifecycleQueue.Enqueue(() =>
                    {
                        BasisRemotePlayerFactory.CreateRemotePlayer(srm, BasisNetworkManagement.instantiationParameters);
                    });
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
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            //same as remote player but just used at the start — arrives as a compressed batch of players
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                try
                {
                    BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerSideSyncPlayer, Reader.AvailableBytes);

                    ServerReadyBatchMessage batch = new ServerReadyBatchMessage();
                    batch.Deserialize(Reader);

                    NetDataReader batchReader = new NetDataReader(batch.Payload);
                    for (int i = 0; i < batch.Count; i++)
                    {
                        // One corrupt entry must not cost the whole batch: every player after it in this
                        // packet would otherwise never spawn. Stop at the bad record, keep the good ones.
                        ServerReadyMessage srm = new ServerReadyMessage();
                        try
                        {
                            srm.Deserialize(batchReader);
                        }
                        catch (Exception ex)
                        {
                            BNL.LogError($"Dropping remote-player spawn batch at entry {i}/{batch.Count}: {ex.Message}");
                            break;
                        }
                        BasisNetworkHandleRemoval.LifecycleQueue.Enqueue(() =>
                        {
                            BasisRemotePlayerFactory.CreateRemotePlayer(srm, BasisNetworkManagement.instantiationParameters);
                        });
                    }
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
                BasisNetworkGenericMessages.HandleOwnershipResponse(Reader);
                Reader.Recycle();
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
                BasisNetworkGenericMessages.HandleOwnershipTransfer(Reader);
                Reader.Recycle();
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
                BasisNetworkGenericMessages.HandleOwnershipRemove(Reader);
                Reader.Recycle();
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
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                BasisNetworkGenericMessages.HandleServerSceneDataMessage(Reader, deliveryMethod);
                Reader.Recycle();
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
                BasisNetworkGenericMessages.HandleServerAvatarDataMessage(Reader, deliveryMethod);
                Reader.Recycle();
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.DirectSceneServerChannel, (peer, Reader, channel, deliveryMethod) =>
        {
            if (ValidateSize(Reader, peer, channel) == false)
            {
                Reader.Recycle();
                return;
            }
            BasisDeviceManagement.EnqueueOnMainThread(() =>
            {
                BasisNetworkGenericMessages.HandleDirectServerSceneDataMessage(Reader, deliveryMethod);
                Reader.Recycle();
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
                BasisNetworkGenericMessages.HandleServerAvatarDataMessage(Reader, deliveryMethod, true);
                Reader.Recycle();
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.NetIDAssigns, Reader.AvailableBytes);
                BasisNetworkGenericMessages.MassNetIDAssign(Reader, deliveryMethod);
                Reader.Recycle();
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.NetIDAssign, Reader.AvailableBytes);
                BasisNetworkGenericMessages.NetIDAssign(Reader, deliveryMethod);
                Reader.Recycle();
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.LoadResource, Reader.AvailableBytes);
                await BasisNetworkGenericMessages.LoadResourceMessage(Reader, deliveryMethod);
                Reader.Recycle();
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
               BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.UnloadResource, Reader.AvailableBytes);
               await BasisNetworkGenericMessages.UnloadResourceMessage(Reader, deliveryMethod);
                Reader.Recycle();
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
                await BasisNetworkGenericMessages.ModifyResourceMessage(Reader, deliveryMethod);
                Reader.Recycle();
            });
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.ServerLibraryChannel, (peer, Reader, channel, deliveryMethod) =>
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
                    HandleServerLibraryReceive(Reader);
                }
                finally
                {
                    Reader.Recycle();
                }
            });
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Admin, Reader.AvailableBytes);
                BasisNetworkModeration.AdminMessage(Reader);
                Reader.Recycle();
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
                Reader.Recycle();
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.Chat, Reader.AvailableBytes);
                BasisNetworkHandleChat.HandleServerChatMessage(Reader);
                Reader.Recycle();
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
            BasisNetworkManagement.LocalPermissions = SMDM.GetPermissions();
            BasisNetworkManagement.OnlocalPermissionsChanged?.Invoke();
            if (BasisNetworkConnection.LocalPlayerIsConnected == false)
            {
                BasisNetworkConnection.SetupLocalPlayer(peer);
            }
        });

        BasisClientMessageRegistry.RegisterCore(BasisNetworkCommons.StoreDatabaseChannel, (peer, Reader, channel, deliveryMethod) =>
        {
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
                pipState.Deserialize(Reader);
                Reader.Recycle();
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
                BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SpawnPreloaded, Reader.AvailableBytes);
                await BasisNetworkGenericMessages.SpawnPreloadedMessage(Reader, deliveryMethod);
                Reader.Recycle();
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
                    default:
                        BNL.LogError($"Unknown EventsChannel event type: {eventType}");
                        Reader.Recycle();
                        break;
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
    // Reused on the main thread (every ServerLibraryChannel receive enqueues onto
    // it) — keeps the per-join NetDataReader allocation out of GC. Library messages
    // arrive sequentially, so a single instance is enough.
    private static NetDataReader _libraryPayloadReader;

    private static void HandleServerLibraryReceive(NetPacketReader reader)
    {
        // Wire format from BasisNetworkServerLibrary:
        //   [u16 rawLen][u16 compressedLen][bytes payload]
        // compressedLen == 0 means the payload is the raw message bytes.
        ushort rawLen = reader.GetUShort();
        ushort compressedLen = reader.GetUShort();
        if (rawLen == 0) return;

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
                        return;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                }
            }

            NetDataReader payloadReader = _libraryPayloadReader ??= new NetDataReader();
            payloadReader.SetSource(payload, 0, rawLen);
            ServerLibraryMessage libraryMessage = new ServerLibraryMessage();
            libraryMessage.Deserialize(payloadReader);
            // Items array becomes BasisServerProvidedItems' source of truth — fine
            // to release the byte buffer once Deserialize has copied strings out.
            BasisServerProvidedItems.SetFromServer(libraryMessage.Items);
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
            BasisConnectionService.ClearPendingLanPasswordAttempt();
            BasisDebug.Log($"Disconnected locally [{disconnectInfo.Reason}]", BasisDebug.LogTag.Networking);
            return;
        }
#if UNITY_SERVER
        bool canShowMenu = !UnityEngine.Application.isBatchMode;
#endif

        if (disconnectInfo.Reason == DisconnectReason.RemoteConnectionClose)
        {
            BasisConnectionService.ClearPendingLanPasswordAttempt();
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
            // Read the reason once (PeekString is now defensive against
            // malformed additional-data, but reading it twice still wastes a
            // GetString call).
            string Reason = disconnectInfo.AdditionalData?.PeekString();
            if (!string.IsNullOrEmpty(Reason))
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
            bool rejected = disconnectInfo.Reason == DisconnectReason.ConnectionRejected;
            NetDataReader extra = disconnectInfo.AdditionalData;

            string title;
            string body;

            // A current server attaches a structured, kind-tagged reject payload (version mismatch,
            // server full, ...). Older servers send a bare reason string, handled by the else path.
            if (rejected && TryReadStructuredReject(extra, out byte kind, out ushort aux0, out ushort aux1, out string structuredMsg))
            {
                switch (kind)
                {
                    case BasisNetworkCommons.RejectKind_VersionMismatch:
                        title = "Update Required";
                        body = !string.IsNullOrEmpty(structuredMsg)
                            ? structuredMsg
                            : $"This server requires Basis protocol v{aux0}; your client is v{aux1}. Please update.";
                        break;
                    case BasisNetworkCommons.RejectKind_ServerFull:
                        title = "Server Full";
                        body = !string.IsNullOrEmpty(structuredMsg) ? structuredMsg : "This server is full. Please try again later.";
                        break;
                    default:
                        title = "Connection Rejected";
                        body = !string.IsNullOrEmpty(structuredMsg) ? structuredMsg : "The server rejected the connection.";
                        break;
                }
            }
            else
            {
                // Bare-string reject, or a non-rejection disconnect (timeout, etc.). PeekString
                // is defensive: an empty/malformed payload yields "".
                string reason = extra?.PeekString();
                if (rejected
                    && string.Equals(reason, BasisNetworkCommons.AuthenticationRejectedReason, StringComparison.Ordinal)
                    && BasisConnectionService.TryHandleLanAuthenticationRejected())
                {
                    BasisDebug.LogWarning("LAN server rejected authentication; requesting a password from the user.", BasisDebug.LogTag.Networking);
                    return;
                }
                title = rejected ? "Connection Rejected" : "Server Disconnected";
                body = !string.IsNullOrEmpty(reason)
                    ? reason
                    : (rejected
                        ? "The server rejected the connection. It may be full, running a different Basis version, or you may not be authorized."
                        : disconnectInfo.Reason.ToString());
            }

            BasisConnectionService.ClearPendingLanPasswordAttempt();

#if UNITY_SERVER
            if (canShowMenu)
#endif
            {
                BasisMainMenu.Open();
                if (BasisMainMenu.Instance != null)
                {
                    BasisMainMenu.Instance.OpenDialogue(title, body, "ok", value =>
                    {
                    });
                }
            }

            BasisDebug.LogError($"{title}: {body}");
        }
    }
}
