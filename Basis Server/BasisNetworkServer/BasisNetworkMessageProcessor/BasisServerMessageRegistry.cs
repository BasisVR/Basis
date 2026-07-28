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
using System.Collections.Generic;
using static BasisNetworkCore.Serializable.SerializableBasis;
using static BasisPermissions.PermissionManager;

public delegate void BasisServerMessageHandler(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod);

/// <summary>
/// Table-driven inbound dispatch. Core messages bind to their dedicated channel (0-59);
/// multiplexed plugin messages bind to a ushort id read from the 61-63 channel payload.
/// Replaces the hardcoded switch so handlers can be added or removed without editing a
/// shared constant table.
/// </summary>
public static class BasisServerMessageRegistry
{
    private static readonly BasisServerMessageHandler[] CoreHandlers = new BasisServerMessageHandler[BasisNetworkCommons.TotalChannels];
    private static readonly ConcurrentDictionary<ushort, BasisServerMessageHandler> PluginHandlers = new();
    private static readonly ConcurrentDictionary<ushort, SerializableBasis.BasisMessageDescriptor> PluginDescriptors = new();
    private static readonly ConcurrentDictionary<int, HashSet<ushort>> Subscriptions = new();

    /// <summary>Plugin ids start above the core channel range (0-63) so they never collide with core ids in the flat manifest/subscription space.</summary>
    private const ushort PluginIdBase = 64;
    private static int _nextPluginId = PluginIdBase;
    private static readonly ConcurrentDictionary<string, ushort> PluginIdsByName = new();
    private static readonly object _pluginIdLock = new object();

    // The supplied manifest only changes when plugins (un)register, which is expected at startup.
    // Cache it as one atomically-swapped snapshot so per-connect SendSupplyTo is allocation-free.
    private sealed class SupplySnapshot
    {
        public readonly int Version;
        public readonly SerializableBasis.BasisMessageDescriptor[] Descriptors;
        public SupplySnapshot(int version, SerializableBasis.BasisMessageDescriptor[] descriptors)
        {
            Version = version;
            Descriptors = descriptors;
        }
    }
    private static volatile SupplySnapshot _supplySnapshot;
    private static int _supplyVersion;

    static BasisServerMessageRegistry()
    {
        RegisterCoreHandlers();
    }

    /// <summary>Force the static constructor to run (registers core handlers). Safe to call repeatedly.</summary>
    public static void EnsureInitialized() { }

    public static void RegisterCore(byte channel, BasisServerMessageHandler handler) => CoreHandlers[channel] = handler;

    public static BasisServerMessageHandler ResolveCore(byte channel) => CoreHandlers[channel];

    /// <summary>Bind a multiplexed plugin message id (carried on channels 61-63) to a handler. Not advertised in the manifest; prefer the descriptor overload.</summary>
    public static void RegisterPlugin(ushort id, BasisServerMessageHandler handler) => PluginHandlers[id] = handler;

    /// <summary>Bind a plugin message and advertise it in the supplied manifest so clients can subscribe by name.</summary>
    public static void RegisterPlugin(SerializableBasis.BasisMessageDescriptor descriptor, BasisServerMessageHandler handler)
    {
        PluginHandlers[descriptor.Id] = handler;
        PluginDescriptors[descriptor.Id] = descriptor;
        InvalidateSupply();
    }

    /// <summary>Remove a plugin message handler and its manifest descriptor. Returns true if a handler was bound.</summary>
    public static bool UnregisterPlugin(ushort id)
    {
        PluginDescriptors.TryRemove(id, out _);
        bool removed = PluginHandlers.TryRemove(id, out _);
        InvalidateSupply();
        return removed;
    }

    /// <summary>
    /// Register a plugin message by name with an auto-assigned id, advertise it in the manifest,
    /// and bind its handler. Returns the assigned id. Ids are assigned in registration order from
    /// PluginIdBase; register plugins in a deterministic order for stable ids across restarts.
    /// </summary>
    public static ushort RegisterServerPlugin(string name, DeliveryMethod delivery, BasisServerMessageHandler handler, byte version = 1, SerializableBasis.BasisMessageFlags extraFlags = SerializableBasis.BasisMessageFlags.None)
    {
        ushort id;
        lock (_pluginIdLock)
        {
            if (!PluginIdsByName.TryGetValue(name, out id))
            {
                id = (ushort)_nextPluginId++;
                PluginIdsByName[name] = id;
            }
        }
        SerializableBasis.BasisMessageDescriptor descriptor = new SerializableBasis.BasisMessageDescriptor
        {
            Id = id,
            Version = version,
            Channel = BasisNetworkCommons.GetPluginChannelForDelivery(delivery),
            Flags = (byte)(SerializableBasis.BasisMessageFlags.Multiplexed | extraFlags),
            Name = name,
        };
        PluginHandlers[id] = handler;
        PluginDescriptors[id] = descriptor;
        InvalidateSupply();
        return id;
    }

    /// <summary>Look up a plugin's assigned message id by name.</summary>
    public static bool TryGetPluginId(string name, out ushort id) => PluginIdsByName.TryGetValue(name, out id);

    /// <summary>
    /// Send a plugin message to a peer by name: prepends the id and uses the descriptor's channel.
    /// Skips peers that did not subscribe to the id. Returns false if the plugin is unknown or skipped.
    /// </summary>
    public static bool SendToPeer(NetPeer peer, string name, Action<NetDataWriter> writePayload)
    {
        if (!PluginIdsByName.TryGetValue(name, out ushort id) || !PluginDescriptors.TryGetValue(id, out SerializableBasis.BasisMessageDescriptor descriptor))
        {
            return false;
        }
        if (!IsSubscribed(peer.Id, id))
        {
            return false;
        }
        NetDataWriter writer = NetworkServer.RentWriter();
        writer.Put(id);
        writePayload?.Invoke(writer);
        BasisNetworkStatistics.RecordOutbound(descriptor.Channel, writer.Length);
        NetworkServer.TrySend(peer, writer, descriptor.Channel, BasisNetworkCommons.GetDeliveryForPluginChannel(descriptor.Channel));
        NetworkServer.ReturnWriter(writer);
        return true;
    }

    /// <summary>Core catalog plus any registered plugin descriptors — the manifest supplied to each client. Cached until a plugin (un)registers.</summary>
    public static SerializableBasis.BasisMessageDescriptor[] BuildSupply()
    {
        int version = _supplyVersion;
        SupplySnapshot snapshot = _supplySnapshot;
        if (snapshot != null && snapshot.Version == version)
        {
            return snapshot.Descriptors;
        }

        SerializableBasis.BasisMessageDescriptor[] core = SerializableBasis.BasisMessageCatalog.BuildCore();
        SerializableBasis.BasisMessageDescriptor[] result;
        if (PluginDescriptors.IsEmpty)
        {
            result = core;
        }
        else
        {
            List<SerializableBasis.BasisMessageDescriptor> combined = new List<SerializableBasis.BasisMessageDescriptor>(core.Length + PluginDescriptors.Count);
            combined.AddRange(core);
            foreach (KeyValuePair<ushort, SerializableBasis.BasisMessageDescriptor> kvp in PluginDescriptors)
            {
                combined.Add(kvp.Value);
            }
            result = combined.ToArray();
        }

        _supplySnapshot = new SupplySnapshot(version, result);
        return result;
    }

    /// <summary>Invalidate the cached manifest after a plugin (un)registers.</summary>
    private static void InvalidateSupply() => System.Threading.Interlocked.Increment(ref _supplyVersion);

    /// <summary>Send the registry manifest to a peer (RegistryControlChannel, RegistrySub_Supply). Called once per connect.</summary>
    public static void SendSupplyTo(NetPeer peer)
    {
        SerializableBasis.BasisMessageSupply supply = new SerializableBasis.BasisMessageSupply { Descriptors = BuildSupply() };
        NetDataWriter writer = NetworkServer.RentWriter();
        writer.Put(BasisNetworkCommons.RegistrySub_Supply);
        supply.Serialize(writer);
        BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.RegistryControlChannel, writer.Length);
        NetworkServer.TrySend(peer, writer, BasisNetworkCommons.RegistryControlChannel, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>Record the message ids a peer reported it can handle (from RegistrySub_Subscribe).</summary>
    public static void SetSubscription(int peerId, ushort[] ids)
    {
        Subscriptions[peerId] = (ids == null || ids.Length == 0) ? new HashSet<ushort>() : new HashSet<ushort>(ids);
    }

    /// <summary>True if the peer subscribed to this message id. Also true when the peer never sent a subscription (no filtering until it does).</summary>
    public static bool IsSubscribed(int peerId, ushort id)
    {
        return !Subscriptions.TryGetValue(peerId, out HashSet<ushort> set) || set.Contains(id);
    }

    /// <summary>Drop a peer's subscription record on disconnect.</summary>
    public static void ClearSubscription(int peerId) => Subscriptions.TryRemove(peerId, out _);

    /// <summary>
    /// Reads the leading ushort message id from a plugin channel payload and dispatches it.
    /// Returns false (leaving the caller to recycle and error-count) when the id is unknown
    /// or the payload is too short to carry an id.
    /// </summary>
    public static bool DispatchPlugin(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (!reader.TryGetUShort(out ushort id))
        {
            return false;
        }
        if (PluginHandlers.TryGetValue(id, out BasisServerMessageHandler handler))
        {
            handler(peer, reader, channel, deliveryMethod);
            return true;
        }
        return false;
    }

    private static void RegisterCoreHandlers()
    {
        RegisterCore(BasisNetworkCommons.ShoutVoiceChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleShoutVoiceMessage(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.AuthIdentityChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleAuth(reader, peer)); // recycles inside

        BasisServerMessageHandler avatarMovement = (peer, reader, channel, dm) =>
            BasisServerReductionSystemEvents.HandleAvatarMovement(reader, peer, channel); // recycles inside
        RegisterCore(BasisNetworkCommons.PlayerAvatarHighChannel, avatarMovement);
        RegisterCore(BasisNetworkCommons.PlayerAvatarHighAdditionalChannel, avatarMovement);

        RegisterCore(BasisNetworkCommons.DeltaAvatarChannel, (peer, reader, channel, dm) =>
            BasisServerReductionSystemEvents.HandleDeltaChannelInbound(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.VoiceChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleVoiceMessage(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.AvatarChannel, (peer, reader, channel, dm) =>
            BasisNetworkingGeneric.HandleAvatar(reader, dm, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.SceneChannel, (peer, reader, channel, dm) =>
            BasisNetworkingGeneric.HandleScene(reader, dm, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.DirectAvatarServerChannel, (peer, reader, channel, dm) =>
            BasisNetworkingGeneric.HandleAvatar(reader, dm, peer, BasisNetworkCommons.DirectAvatarServerChannel)); // recycles inside

        RegisterCore(BasisNetworkCommons.DirectSceneServerChannel, (peer, reader, channel, dm) =>
            BasisNetworkingGeneric.HandleScene(reader, dm, peer, BasisNetworkCommons.DirectSceneServerChannel)); // recycles inside

        RegisterCore(BasisNetworkCommons.AvatarChangeMessageChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.SendAvatarMessageToClients(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, (peer, reader, channel, dm) =>
            HandlePermitted(peer, reader, PermNodes.OwnershipTransfer, () =>
                BasisNetworkOwnership.OwnershipTransfer(reader, peer))); // recycles inside

        RegisterCore(BasisNetworkCommons.GetCurrentOwnerRequestChannel, (peer, reader, channel, dm) =>
            HandlePermitted(peer, reader, PermNodes.OwnershipGet, () =>
                BasisNetworkOwnership.OwnershipResponse(reader, peer))); // recycles inside

        RegisterCore(BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, (peer, reader, channel, dm) =>
            HandlePermitted(peer, reader, PermNodes.OwnershipRemove, () =>
                BasisNetworkOwnership.RemoveOwnership(reader, peer))); // recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer, false)); // byte count, recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsLargeChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceivers(reader, peer, true)); // ushort count, recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsInvertedChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceiversInverted(reader, peer, false)); // byte count excluded, recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsInvertedLargeChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceiversInverted(reader, peer, true)); // ushort count excluded, recycles inside

        RegisterCore(BasisNetworkCommons.AudioRecipientsBitfieldChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.UpdateVoiceReceiversBitfield(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.netIDAssignChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.NetIDAssign(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.LoadResourceChannel, (peer, reader, channel, dm) =>
        {
            if (NetworkServer.AuthIdentity.NetIDToUUID(peer, out string LRuuid))
            {
                BasisServerHandleEvents.LoadResource(reader, peer, LRuuid);
                return;
            }
            BNL.LogError($"User UUID not found for peer: {peer}");
            reader.Recycle();
        });

        RegisterCore(BasisNetworkCommons.UnloadResourceChannel, (peer, reader, channel, dm) =>
        {
            BasisServerHandleEvents.UnloadResource(reader, peer);
            reader.Recycle();
        });

        RegisterCore(BasisNetworkCommons.ModifyResourceChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleModifyResource(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.AdminChannel, (peer, reader, channel, dm) =>
            BasisPlayerModeration.OnAdminMessage(peer, reader)); // recycles inside

        RegisterCore(BasisNetworkCommons.ContentShareChannel, (peer, reader, channel, dm) =>
        {
            // Multiplexed: first byte selects drop vs cleanup. Handlers recycle the reader.
            if (!reader.TryGetByte(out byte sub)) { reader.Recycle(); return; }
            if (sub == BasisNetworkCommons.ContentShareSub_Cleanup)
                BasisNetworkContentShare.HandleContentShareCleanup(reader, peer);
            else
                BasisNetworkContentShare.HandleContentShareDrop(reader, peer);
        });

        RegisterCore(BasisNetworkCommons.ServerBoundChannel, (peer, reader, channel, dm) =>
        {
            BasisServerHandleEvents.OnServerReceived?.Invoke(peer, reader, dm);
            reader.Recycle(); // recycles here
        });

        RegisterCore(BasisNetworkCommons.StoreDatabaseChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleStoreDatabase(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.RequestStoreDatabaseChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandleRequestStoreDatabase(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.ServerStatisticsChannel, (peer, reader, channel, dm) =>
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
        });

        RegisterCore(BasisNetworkCommons.ChatChannel, (peer, reader, channel, dm) =>
            BasisNetworkChat.HandleChatMessage(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.CameraPIPStateChannel, (peer, reader, channel, dm) =>
            BasisNetworkPIPCamera.HandlePIPStateChange(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.CameraPIPPositionChannel, (peer, reader, channel, dm) =>
            BasisNetworkPIPCamera.HandlePIPPositionUpdate(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.PreloadReadyChannel, (peer, reader, channel, dm) =>
            BasisServerHandleEvents.HandlePreloadReady(reader, peer)); // recycles inside

        RegisterCore(BasisNetworkCommons.EventsChannel, (peer, reader, channel, dm) =>
            BasisServerEventsRouter.HandleEvent(reader, peer)); // reads event type byte, routes, recycles inside

        RegisterCore(BasisNetworkCommons.P2PChannel, (peer, reader, channel, dm) =>
            BasisServerP2PBroker.HandleP2PMessage(reader, peer)); // reads sub-type byte, routes, recycles inside

        RegisterCore(BasisNetworkCommons.RegistryControlChannel, (peer, reader, channel, dm) =>
        {
            if (reader.TryGetByte(out byte sub) && sub == BasisNetworkCommons.RegistrySub_Subscribe)
            {
                SerializableBasis.BasisMessageSubscribe subscribe = new SerializableBasis.BasisMessageSubscribe();
                subscribe.Deserialize(reader);
                SetSubscription(peer.Id, subscribe.Ids);
            }
            reader.Recycle();
        });
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
