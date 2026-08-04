using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisPermissions;
using System.Collections.Concurrent;
using System.Linq;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

/// <summary>
/// Server-side management of content share spheres.
/// Tracks all active spheres and handles broadcasting to clients.
/// </summary>
public static class BasisNetworkContentShare
{
    /// <summary>
    /// All active content share spheres keyed by SphereNetID.
    /// Value is the full message including creator player ID.
    /// </summary>
    public static ConcurrentDictionary<string, ServerContentShareMessage> ActiveSpheres =
        new ConcurrentDictionary<string, ServerContentShareMessage>();

    /// <summary>
    /// Handles a content share drop from a client.
    /// Stores the sphere and broadcasts to all clients.
    /// </summary>
    public static void HandleContentShareDrop(NetPacketReader reader, NetPeer peer)
    {
        ContentShareMessage msg = new ContentShareMessage();
        msg.Deserialize(reader);
        reader.Recycle();

        if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.ContentShareCreate))
        {
            return;
        }

        // Global lock check based on content type: blocked when the content's
        // lock is on AND the peer lacks the matching lockbypass permission.
        bool blocked = false;
        string contentName = "";
        switch (msg.ContentType)
        {
            case ContentShareType.Avatar:
                blocked = BasisNetworkServer.Security.BasisGlobalLockManager.AvatarsLocked &&
                    !PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceLockBypassAvatar);
                contentName = "Avatar";
                break;
            case ContentShareType.Prop:
                blocked = BasisNetworkServer.Security.BasisGlobalLockManager.PropsLocked &&
                    !PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceLockBypassProp);
                contentName = "Prop";
                break;
            case ContentShareType.World:
                blocked = BasisNetworkServer.Security.BasisGlobalLockManager.WorldsLocked &&
                    !PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceLockBypassWorld);
                contentName = "World";
                break;
            case ContentShareType.Server:
                // ContentURL carries the connection string (address[:port][#password]).
                // UnlockPassword is intentionally unused — receivers parse the URL directly.
                blocked = BasisNetworkServer.Security.BasisGlobalLockManager.ServersLocked &&
                    !PermissionIntegration.HasValidRequirement(peer, PermNodes.ResourceLockBypassServer);
                contentName = "Server share";
                break;
            default:
                BNL.LogError($"Unknown content share type {(byte)msg.ContentType} from peer {peer.Id}");
                return;
        }
        if (blocked)
        {
            BNL.Log($"{contentName} content sharing is globally disabled. Rejected from peer {peer.Id}");
            BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(peer, $"{contentName} loading is currently disabled by an admin.");
            return;
        }

        if (ActiveSpheres.Count(kvp => kvp.Value.playerIdMessage.playerID == (ushort)peer.Id) >= BasisNetworkServer.Security.BasisResourceLimitManager.MaxContentSpheresPerPlayer)
        {
            BNL.LogError($"Peer {peer.Id} reached content sphere limit.");
            return;
        }

        string sharerUUID = string.Empty;
        string sharerDisplayName = string.Empty;
        if (BasisSavedState.GetLastPlayerMetaData(peer, out ClientMetaDataMessage sharerMeta))
        {
            sharerUUID = sharerMeta.playerUUID ?? string.Empty;
            sharerDisplayName = sharerMeta.playerDisplayName ?? string.Empty;
        }

        ServerContentShareMessage serverMsg = new ServerContentShareMessage
        {
            playerIdMessage = new PlayerIdMessage
            {
                playerID = (ushort)peer.Id
            },
            SharerUUID = sharerUUID,
            SharerDisplayName = sharerDisplayName,
            contentShareMessage = msg
        };

        if (ActiveSpheres.TryAdd(msg.SphereNetID, serverMsg))
        {
            BNL.Log($"Content sphere dropped: {msg.SphereNetID} type={msg.ContentType}");

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(BasisNetworkCommons.ContentShareSub_Drop);
            serverMsg.Serialize(writer);

            // Broadcast to all clients including sender
            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.ContentShareChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered
            );
            NetworkServer.ReturnWriter(writer);
        }
        else
        {
            BNL.LogError($"Content sphere already exists: {msg.SphereNetID}");
        }
    }

    /// <summary>
    /// Handles a content share cleanup from a client.
    /// Removes the sphere and broadcasts removal to all clients.
    /// </summary>
    public static void HandleContentShareCleanup(NetPacketReader reader, NetPeer peer)
    {
        ContentShareCleanupMessage msg = new ContentShareCleanupMessage();
        msg.Deserialize(reader);
        reader.Recycle();

        if (!ActiveSpheres.TryGetValue(msg.SphereNetID, out ServerContentShareMessage existing))
        {
            BNL.LogError($"Trying to remove content sphere that does not exist: {msg.SphereNetID}");
            return;
        }
        if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.ContentShareDelete))
        {
            return;
        }
        // ContentShareDelete is default-granted, so the sharer check is what stops one player
        // deleting everyone else's orbs.
        if (existing.playerIdMessage.playerID != (ushort)peer.Id
            && !PermissionIntegration.HasValidRequirement(peer, PermNodes.protection))
        {
            BNL.LogError($"Peer {peer.Id} tried to remove content sphere {msg.SphereNetID} they did not share.");
            return;
        }
        if (ActiveSpheres.TryRemove(msg.SphereNetID, out _))
        {
            BNL.Log($"Content sphere removed: {msg.SphereNetID}");

            ServerContentShareCleanupMessage serverMsg = new ServerContentShareCleanupMessage
            {
                playerIdMessage = new PlayerIdMessage
                {
                    playerID = (ushort)peer.Id
                },
                contentShareCleanupMessage = msg
            };

            NetDataWriter writer = NetworkServer.RentWriter();
            writer.Put(BasisNetworkCommons.ContentShareSub_Cleanup);
            serverMsg.Serialize(writer);

            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.ContentShareChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered
            );
            NetworkServer.ReturnWriter(writer);
        }
    }

    /// <summary>
    /// Sends all active content share spheres to a newly connected peer.
    /// </summary>
    public static void SendAllSpheresToPeer(NetPeer newConnection)
    {
        ServerContentShareMessage[] spheres = ActiveSpheres.Values.ToArray();
        if (spheres.Length == 0) return;

        NetDataWriter writer = NetworkServer.RentWriter();
        for (int i = 0; i < spheres.Length; i++)
        {
            writer.Reset();
            writer.Put(BasisNetworkCommons.ContentShareSub_Drop);
            spheres[i].Serialize(writer);
            NetworkServer.TrySend(
                newConnection,
                writer,
                BasisNetworkCommons.ContentShareChannel,
                DeliveryMethod.ReliableOrdered
            );
        }
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>
    /// Removes all spheres created by a disconnecting player.
    /// </summary>
    public static void RemovePlayerSpheres(int peerId)
    {
        ushort playerId = (ushort)peerId;
        var toRemove = ActiveSpheres.Where(kvp => kvp.Value.playerIdMessage.playerID == playerId)
                                    .Select(kvp => kvp.Key)
                                    .ToArray();

        foreach (string sphereId in toRemove)
        {
            if (ActiveSpheres.TryRemove(sphereId, out _))
            {
                ContentShareCleanupMessage cleanup = new ContentShareCleanupMessage
                {
                    SphereNetID = sphereId
                };

                ServerContentShareCleanupMessage serverMsg = new ServerContentShareCleanupMessage
                {
                    playerIdMessage = new PlayerIdMessage { playerID = playerId },
                    contentShareCleanupMessage = cleanup
                };

                NetDataWriter writer = NetworkServer.RentWriter();
                writer.Put(BasisNetworkCommons.ContentShareSub_Cleanup);
                serverMsg.Serialize(writer);

                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.ContentShareChannel,
                    NetworkServer.PeerSnapshot,
                    DeliveryMethod.ReliableOrdered
                );
                NetworkServer.ReturnWriter(writer);
            }
        }
    }

    /// <summary>
    /// Clears all non-persistent spheres (called when server empties).
    /// </summary>
    public static void Reset()
    {
        string[] keys = ActiveSpheres.Keys.ToArray();
        foreach (string key in keys)
        {
            ActiveSpheres.TryRemove(key, out _);
        }
    }
}
