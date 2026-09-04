using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Sync;
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
    private static readonly List<DeferredMessage> _deferredDirectMessages = new();
    private static readonly Dictionary<ushort, Action<ushort, byte[], DeliveryMethod>> _directHandlers = new();
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

    public static void RegisterDirectHandler(ushort messageIndex, Action<ushort, byte[], DeliveryMethod> handler)
    {
        _directHandlers[messageIndex] = handler;
        TryDeliverDeferredDirectMessages();
    }

    public static void UnregisterDirectHandler(ushort messageIndex)
    {
        _directHandlers.Remove(messageIndex);
    }

    /// <summary>
    /// Drops every scene-data registration and everything still queued for one. Called when a connection ends,
    /// because a message index means nothing outside the connection that issued it: the server assigns indices
    /// from a counter that restarts on an empty instance, so the next server hands the same numbers to
    /// different objects. Anything kept across a disconnect is therefore actively wrong - a stale registration
    /// points a live index at the wrong subsystem, and a payload nobody claimed on the last server is replayed
    /// into whoever claims that index on the next one, carrying a player id from a room that no longer exists.
    ///
    /// Everything dropped here re-registers itself on the next connection: <c>BasisNetworkBehaviour</c> from
    /// <c>Start</c> on the world and prop objects the teardown rebuilds, and the static services from their own
    /// join hooks. Batch demux is the one exception - it is session-wide policy rather than per connection, so
    /// it is re-armed here instead of waiting for a <c>SetEnabled</c> call that will never come again.
    /// </summary>
    public static void ReleaseConnectionRegistrations()
    {
        _handlers.Clear();
        _directHandlers.Clear();
        _deferredMessages.Clear();
        _deferredDirectMessages.Clear();

        if (BasisSyncBatchCollector.Enabled)
        {
            RegisterBatchHandler();
        }
    }

    public static void DispatchServerSceneDataMessage(ServerSceneDataMessage serverSceneDataMessage, DeliveryMethod deliveryMethod, bool direct)
    {
        ushort playerID = serverSceneDataMessage.playerIdMessage.playerID;
        var sceneDataMessage = serverSceneDataMessage.sceneDataMessage;
        if (DispatchSceneData(playerID, sceneDataMessage.messageIndex, sceneDataMessage.payload, deliveryMethod, direct))
        {
            serverSceneDataMessage.sceneDataMessage.Release();//dont need todo this but not doing it will create more gc then necessary
        }
    }

    public static void HandleDirectP2PSceneMessage(ushort senderPlayerId, ushort messageIndex, byte[] payload, DeliveryMethod deliveryMethod)
    {
        DispatchSceneData(senderPlayerId, messageIndex, payload, deliveryMethod, true);
    }

    private static bool DispatchSceneData(ushort playerID, ushort messageIndex, byte[] payload, DeliveryMethod deliveryMethod, bool direct)
    {
        var handlers = direct ? _directHandlers : _handlers;
        if (handlers.TryGetValue(messageIndex, out var handler))
        {
            handler.Invoke(playerID, payload, deliveryMethod);
            return true;
        }

        var deferred = direct ? _deferredDirectMessages : _deferredMessages;
        if (deferred.Count >= MaxDeferredMessages)
        {
            // Dropping the oldest keeps the queue bounded but takes the front of whatever sequence is
            // waiting — the header that opens a chunked transfer, typically — so the replay delivers a body
            // with nothing to attach it to. Worth a line, because the failure surfaces much later and
            // somewhere else entirely.
            BasisDebug.LogWarningOnce(
                direct ? "BasisNetwork.DeferredDirectOverflow" : "BasisNetwork.DeferredOverflow",
                $"Deferred {(direct ? "direct " : string.Empty)}scene-data queue hit its {MaxDeferredMessages} message cap; "
                    + $"dropping the oldest to admit message index {messageIndex} from {playerID}. Anything sent as a "
                    + "sequence before its handler registered may now be incomplete.",
                BasisDebug.LogTag.Networking
            );
            deferred.RemoveAt(0);
        }
        deferred.Add(new DeferredMessage(playerID, messageIndex, payload, deliveryMethod));
        return false;
    }

    // ── Optional batched scene data (see BasisSyncBatchCollector). One packet under the reserved index carries
    //    many objects' payloads; demux to each object's normal handler. Registered only while batching is on. ──
    public static void RegisterBatchHandler() => RegisterHandler(BasisSyncBatchCollector.BatchMessageIndex, HandleBatch);
    public static void UnregisterBatchHandler() => UnregisterHandler(BasisSyncBatchCollector.BatchMessageIndex);

    private static void HandleBatch(ushort playerID, byte[] payload, DeliveryMethod deliveryMethod)
    {
        if (payload == null) return;
        var reader = new BasisSyncBatchReader(payload, payload.Length);
        while (reader.TryRead(out ushort id, out int offset, out int length))
        {
            if (_handlers.TryGetValue(id, out var handler))
            {
                byte[] sub = new byte[length];
                System.Array.Copy(payload, offset, sub, 0, length);
                handler.Invoke(playerID, sub, deliveryMethod);
            }
        }
    }

    private static void TryDeliverDeferredMessages() => DeliverDeferred(_deferredMessages, _handlers);

    private static void TryDeliverDeferredDirectMessages() => DeliverDeferred(_deferredDirectMessages, _directHandlers);

    /// <summary>
    /// Replays messages that arrived before their handler existed, in the order they arrived.
    ///
    /// This used to walk the list backwards — safe for removal during iteration, but it delivered newest
    /// first, which silently reverses the wire order the transport went to the trouble of guaranteeing. For
    /// anything sent as a sequence that is fatal rather than untidy: an image pickup joining an instance
    /// received every chunk of a transfer before the spawn header that opens it, so the chunks were dropped
    /// as belonging to no transfer, the header then raised a card with nothing left to fill it, and the card
    /// was removed thirty seconds later when the transfer timed out. Handlers registering during a replay is
    /// also legal, so the list is settled before anything is invoked.
    /// </summary>
    private static void DeliverDeferred(List<DeferredMessage> deferred, Dictionary<ushort, Action<ushort, byte[], DeliveryMethod>> handlers)
    {
        if (deferred.Count == 0)
        {
            return;
        }

        List<DeferredMessage> ready = null;
        List<DeferredMessage> stillWaiting = null;
        for (int Index = 0; Index < deferred.Count; Index++)
        {
            DeferredMessage msg = deferred[Index];
            if (handlers.ContainsKey(msg.MessageIndex))
            {
                (ready ??= new List<DeferredMessage>()).Add(msg);
            }
            else
            {
                (stillWaiting ??= new List<DeferredMessage>()).Add(msg);
            }
        }
        if (ready == null)
        {
            return;
        }

        deferred.Clear();
        if (stillWaiting != null)
        {
            deferred.AddRange(stillWaiting);
        }

        int readyCount = ready.Count;
        for (int Index = 0; Index < readyCount; Index++)
        {
            DeferredMessage msg = ready[Index];
            if (handlers.TryGetValue(msg.MessageIndex, out var handler))
            {
                handler.Invoke(msg.PlayerId, msg.Payload, msg.DeliveryMethod);
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
        BasisNetworkPlayers.OwnershipPairing.Remove(OwnershipTransferMessage.ownershipID, out ushort OldPlayerID);
        BasisNetworkPlayer.OnOwnershipReleased?.Invoke(OwnershipTransferMessage.ownershipID);
    }
    public static void HandleOwnership(OwnershipTransferMessage OwnershipTransferMessage)
    {
        BasisNetworkPlayers.OwnershipPairing[OwnershipTransferMessage.ownershipID] = OwnershipTransferMessage.playerIdMessage.playerID;
        if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort Id))
        {
            bool isLocalOwner = OwnershipTransferMessage.playerIdMessage.playerID == Id;

            BasisNetworkPlayer.OnOwnershipTransfer?.Invoke(OwnershipTransferMessage.ownershipID, OwnershipTransferMessage.playerIdMessage.playerID, isLocalOwner);
        }
        else
        {
            BasisDebug.LogError("NO Local PLayer ID Found");
        }
    }
    // Handler for server avatar data messages.
    // Persistent envelope so RemoteAvatarDataMessage's exact-size payload reuse path actually
    // engages — a fresh envelope arrived with payload null and allocated every message. Safe
    // because both call sites run on the polled connection's thread, every dispatch consumes the
    // payload synchronously, and the deferred branch below clones before storing.
    private static ServerAvatarDataMessage sServerAvatarData;
    public static void HandleServerAvatarDataMessage(NetPacketReader reader, DeliveryMethod Method, bool direct = false)
    {
        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.ServerAvatarData, reader.AvailableBytes);
        sServerAvatarData.Deserialize(reader);
        DispatchAvatarData(sServerAvatarData, Method, direct);
    }

    public static void HandleDirectP2PAvatarMessage(ushort senderPlayerId, byte messageIndex, byte avatarLinkIndex, byte[] payload, DeliveryMethod Method)
    {
        ServerAvatarDataMessage SADM = new ServerAvatarDataMessage
        {
            avatarDataMessage = new RemoteAvatarDataMessage
            {
                messageIndex = messageIndex,
                AvatarLinkIndex = avatarLinkIndex,
                payload = payload,
                PlayerIdMessage = new PlayerIdMessage { playerID = senderPlayerId },
            },
            playerIdMessage = new PlayerIdMessage { playerID = senderPlayerId },
        };
        DispatchAvatarData(SADM, Method, true);
    }

    public static void DispatchAvatarData(ServerAvatarDataMessage SADM, DeliveryMethod Method, bool direct)
    {
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

                // Null between NotifyNetworkBehavioursTerminated and the GetComponentsInChildren that
                // refills it: BasisAvatar is assigned by the factory well before AvatarLoadComplete
                // runs, and the install is budgeted across frames, so every first load and every
                // avatar swap has a window where the avatar is live and this array is not.
                if (player.NetworkBehaviours != null && player.NetworkBehaviours.Length > output.messageIndex)
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
                            System.Threading.Interlocked.Increment(ref Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDeferred);
                            // Store the message for delayed playback. The reused envelope's payload
                            // buffer is overwritten by the next deserialize, so the stored copy has
                            // to own its bytes.
                            ServerAvatarDataMessage stored = SADM;
                            if (stored.avatarDataMessage.payload != null)
                            {
                                stored.avatarDataMessage.payload = (byte[])stored.avatarDataMessage.payload.Clone();
                            }
                            player.NextMessages[output.messageIndex] = new BasisNetworkPlayer.ServerAvatarDataMessageQueue()
                            {
                                Method = Method,
                                ServerAvatarDataMessage = stored,
                                Direct = direct
                            };
                        }
                        else
                        {
                            System.Threading.Interlocked.Increment(ref Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDropped);
                        }
                    }
                    else
                    {
                        if (output.messageIndex < player.NetworkBehaviourCount)
                        {
                            System.Threading.Interlocked.Increment(ref Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDispatched);
                            Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDebugCapture.RecordReceivedAvatarChannel(SADM.playerIdMessage.playerID, output.messageIndex, output.payload);
                            if (direct)
                            {
                                player.NetworkBehaviours[output.messageIndex].OnDirectNetworkMessageReceived(SADM.playerIdMessage.playerID, output.payload, Method);
                            }
                            else
                            {
                                player.NetworkBehaviours[output.messageIndex].OnNetworkMessageReceived(SADM.playerIdMessage.playerID, output.payload, Method);
                            }
                        }
                        else
                        {
                            System.Threading.Interlocked.Increment(ref Basis.Scripts.Networking.NetworkedAvatar.BasisAdditionalDataDiagnostics.ReceiverAvatarChannelDropped);
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
        else if (!BasisNetworkPlayers.JoiningPlayers.ContainsKey(SADM.playerIdMessage.playerID))
        {
            // Joining players race their own creation — silent. Anything else is worth a line.
            BasisDebug.Log("Missing Player For Message " + SADM.playerIdMessage.playerID);
        }
    }
    /// <summary>
    /// Bytes the scene-data framing adds around a payload, so a caller can check its packet against
    /// <see cref="BasisNetworkCommons.MaxUnfragmentedPayload"/> before handing it over. Worst case of
    /// the two paths a send can take: the relay path carries messageIndex + recipientsSize + the
    /// recipient ids, the P2P-direct path only messageIndex, and a broadcast can split across both.
    /// </summary>
    public static int SceneDataFramingBytes(ushort[] recipients) =>
        sizeof(ushort) * 2 + (recipients != null ? recipients.Length * sizeof(ushort) : 0);

    public static void OnNetworkMessageSend(ushort messageIndex, byte[] buffer = null, DeliveryMethod deliveryMethod = DeliveryMethod.Unreliable, ushort[] recipients = null)
    {
        NetDataWriter netDataWriter = threadLocalWriter.Value;
        netDataWriter.Reset(); // clear previous data

        SceneDataMessage sceneDataMessage = new SceneDataMessage
        {
            messageIndex = messageIndex,
            payload = buffer,
            recipients = recipients
        };

        sceneDataMessage.Serialize(netDataWriter);
        BasisNetworkConnection.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.SceneChannel, deliveryMethod);

        BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SceneData, netDataWriter.Length);
    }
    public static void OnNetworkMessageSendDirect(ushort messageIndex, byte[] buffer = null, DeliveryMethod deliveryMethod = DeliveryMethod.Unreliable, ushort[] recipients = null, bool allowServerFallback = true)
    {
        BasisP2PManager.PartitionRecipients(recipients, out List<ushort> directIds, out List<ushort> relayIds);

        if (directIds != null && directIds.Count > 0)
        {
            NetDataWriter p2pWriter = threadLocalWriter.Value;
            p2pWriter.Reset();
            p2pWriter.Put(messageIndex);
            if (buffer != null)
            {
                p2pWriter.Put(buffer);
            }
            for (int Index = 0; Index < directIds.Count; Index++)
            {
                BasisP2PManager.SendDirectTo(directIds[Index], p2pWriter, BasisNetworkCommons.DirectSceneChannel, deliveryMethod);
            }
        }

        if (allowServerFallback && relayIds != null && relayIds.Count > 0)
        {
            NetDataWriter netDataWriter = threadLocalWriter.Value;
            netDataWriter.Reset();
            SceneDataMessage sceneDataMessage = new SceneDataMessage
            {
                messageIndex = messageIndex,
                payload = buffer,
                recipients = relayIds.ToArray()
            };
            sceneDataMessage.Serialize(netDataWriter);
            BasisNetworkConnection.LocalPlayerPeer.Send(netDataWriter, BasisNetworkCommons.DirectSceneServerChannel, deliveryMethod);
            BasisNetworkProfiler.AddToCounter(BasisNetworkProfilerCounter.SceneData, netDataWriter.Length);
        }
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

        try
        {
            // Check the load strategy before spawning
            switch (LocalLoadResource.LoadStrategy)
            {
                case 2: // Synchronized - download, report readiness, wait for spawn signal
                    await BasisNetworkPreloadManager.HandleSynchronizedPreload(LocalLoadResource);
                    return;
                case 3: // Predownload only - cache to disc, never spawn, never report readiness
                    await BasisNetworkPreloadManager.HandlePredownload(LocalLoadResource);
                    return;
            }

            // LoadStrategy 0 (Immediate) - existing behavior
            switch (LocalLoadResource.Mode)
            {
                case 0:
                    await BasisNetworkSpawnItem.SpawnGameObject(LocalLoadResource, BundledContentHolder.Selector.Prop);
                    break;
                case 1:
                    await BasisNetworkSpawnItem.SpawnScene(LocalLoadResource);
                    break;
                case 2:
                    await BasisNetworkSpawnItem.SpawnGameObject(LocalLoadResource, BundledContentHolder.Selector.Avatar);
                    break;
                default:
                    BNL.LogError($"tried to Load Mode {LocalLoadResource.Mode}");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            BasisDebug.Log($"Load cancelled for {LocalLoadResource.LoadedNetID} (disconnected)", BasisDebug.LogTag.Networking);
        }
    }

    /// <summary>
    /// Handles the SpawnPreloaded message from the server, signaling that a
    /// previously preloaded synchronized resource should now be spawned.
    /// </summary>
    public static async Task SpawnPreloadedMessage(NetPacketReader reader, DeliveryMethod Method)
    {
        SpawnPreloadedMessage spawnMsg = new SpawnPreloadedMessage();
        spawnMsg.Deserialize(reader);
        try
        {
            await BasisNetworkPreloadManager.HandleSpawnPreloaded(spawnMsg);
        }
        catch (OperationCanceledException)
        {
            BasisDebug.Log($"Spawn cancelled for preloaded {spawnMsg.LoadedNetID} (disconnected)", BasisDebug.LogTag.Networking);
        }
    }
    public static async Task UnloadResourceMessage(NetPacketReader reader, DeliveryMethod Method)
    {
        UnLoadResource UnLoadResource = new UnLoadResource();
        UnLoadResource.Deserialize(reader);
        switch (UnLoadResource.Mode)
        {
            case 0:
                await BasisNetworkSpawnItem.DestroyGameobject(UnLoadResource);
                break;
            case 1:
                await BasisNetworkSpawnItem.DestroyScene(UnLoadResource);
                break;
            case 02:
              await  BasisNetworkSpawnItem.DestroyGameobject(UnLoadResource);
                break;
            default:
                BNL.LogError($"tried to removed Mode {UnLoadResource.Mode}");
                break;
        }
       // Basis.BasisRuntimeSpawnRegistry.RemoveByLoadedNetId(UnLoadResource.LoadedNetID, out var data);
    }
    public static Task ModifyResourceMessage(NetPacketReader reader, DeliveryMethod Method)
    {
        ModifyResource modifyResource = new ModifyResource();
        modifyResource.Deserialize(reader);
        // Apply the server-authoritative static/locked state to the registry record + live object.
        Basis.BasisRuntimeSpawnRegistry.SetStaticByLoadedNetId(modifyResource.LoadedNetID, modifyResource.Static, modifyResource.StaticAdminLocked);
        return Task.CompletedTask;
    }
}
