using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisPermissions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

/// <summary>
/// Server-side management of content share spheres.
/// Tracks all active spheres and handles broadcasting to clients.
/// </summary>
public static class BasisNetworkContentShare
{
    public sealed class ActiveSphere
    {
        public readonly ServerContentShareMessage Message;
        public readonly long DroppedAt, SharerLeftAt;
        public readonly bool SharerLeft;
        public ActiveSphere(ServerContentShareMessage message, long droppedAt, bool sharerLeft = false, long sharerLeftAt = 0)
        {
            Message = message;
            DroppedAt = droppedAt;
            SharerLeft = sharerLeft;
            SharerLeftAt = sharerLeftAt;
        }
        public ushort SharerId => Message.playerIdMessage.playerID;
    }

    /// <summary>
    /// All active content share spheres keyed by SphereNetID.
    /// Value is the full message including creator player ID, plus when it was dropped and when its sharer left.
    /// </summary>
    public static ConcurrentDictionary<string, ActiveSphere> ActiveSpheres =
        new ConcurrentDictionary<string, ActiveSphere>();

    private const int ExpiryIntervalMs = 1000;
    private static Timer expiryTimer;
    private static int expiring;

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
            case ContentShareType.DollyTrack:
                // ContentURL carries the track itself as JSON. There is no bundle to gate and no
                // global lock of its own: a track is a shape somebody drew, so the create
                // permission, the per-player sphere cap and the payload ceiling below are the
                // whole of what limits it.
                contentName = "Dolly track";
                break;
            default:
                BNL.LogError($"Unknown content share type {(byte)msg.ContentType} from peer {peer.Id}");
                return;
        }
        if (ContentSharePayload.IsPayloadType(msg.ContentType)
            && (msg.ContentURL == null || msg.ContentURL.Length > ContentSharePayload.MaxLength))
        {
            BNL.LogError($"Content share payload from peer {peer.Id} is {msg.ContentURL?.Length ?? -1} characters; the ceiling is {ContentSharePayload.MaxLength}.");
            return;
        }

        if (blocked)
        {
            BNL.Log($"{contentName} content sharing is globally disabled. Rejected from peer {peer.Id}");
            BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(peer, $"{contentName} loading is currently disabled by an admin.");
            return;
        }

        if (ActiveSpheres.Count(kvp => !kvp.Value.SharerLeft && kvp.Value.SharerId == (ushort)peer.Id) >= BasisNetworkServer.Security.BasisResourceLimitManager.MaxContentSpheresPerPlayer)
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

        if (ActiveSpheres.TryAdd(msg.SphereNetID, new ActiveSphere(serverMsg, Stopwatch.GetTimestamp())))
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

        ushort requesterId = (ushort)peer.Id;
        if (!ActiveSpheres.TryGetValue(msg.SphereNetID, out ActiveSphere existing))
        {
            BNL.LogError($"Trying to remove content sphere that does not exist: {msg.SphereNetID}");
            SendCleanup(peer, msg.SphereNetID, requesterId);
            return;
        }
        if (!PermissionIntegration.HasValidRequirement(peer, PermNodes.ContentShareDelete))
        {
            BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(peer, "You do not have permission to remove shared content.");
            return;
        }
        // ContentShareDelete is default-granted, so the sharer check is what stops one player
        // deleting everyone else's orbs.
        if ((existing.SharerLeft || existing.SharerId != requesterId)
            && !(NetworkServer.AuthIdentity.NetIDToUUID(peer, out string requesterUuid) && PermissionIntegration.HasValidRequirement(requesterUuid, PermNodes.protection)))
        {
            BNL.LogWarning($"Peer {peer.Id} tried to remove content sphere {msg.SphereNetID} they did not share.");
            BasisNetworkServer.Security.BasisPlayerModeration.SendBackMessage(peer, "Only the player who shared this content can remove it.");
            return;
        }
        if (ActiveSpheres.TryRemove(msg.SphereNetID, out _))
        {
            BNL.Log($"Content sphere removed: {msg.SphereNetID}");
            BroadcastCleanup(msg.SphereNetID, requesterId);
        }
    }

    private static void WriteCleanup(NetDataWriter writer, string sphereId, ushort playerId)
    {
        ServerContentShareCleanupMessage serverMsg = new ServerContentShareCleanupMessage
        {
            playerIdMessage = new PlayerIdMessage { playerID = playerId },
            contentShareCleanupMessage = new ContentShareCleanupMessage { SphereNetID = sphereId }
        };
        writer.Put(BasisNetworkCommons.ContentShareSub_Cleanup);
        serverMsg.Serialize(writer);
    }

    private static void BroadcastCleanup(string sphereId, ushort playerId)
    {
        NetDataWriter writer = NetworkServer.RentWriter();
        WriteCleanup(writer, sphereId, playerId);
        NetworkServer.BroadcastMessageToClients(
            writer,
            BasisNetworkCommons.ContentShareChannel,
            NetworkServer.PeerSnapshot,
            DeliveryMethod.ReliableOrdered
        );
        NetworkServer.ReturnWriter(writer);
    }

    private static void SendCleanup(NetPeer peer, string sphereId, ushort playerId)
    {
        NetDataWriter writer = NetworkServer.RentWriter();
        WriteCleanup(writer, sphereId, playerId);
        NetworkServer.TrySend(peer, writer, BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>
    /// Sends all active content share spheres to a newly connected peer.
    /// </summary>
    public static void SendAllSpheresToPeer(NetPeer newConnection)
    {
        ActiveSphere[] spheres = ActiveSpheres.Values.ToArray();
        if (spheres.Length == 0) return;

        NetDataWriter writer = NetworkServer.RentWriter();
        for (int i = 0; i < spheres.Length; i++)
        {
            writer.Reset();
            writer.Put(BasisNetworkCommons.ContentShareSub_Drop);
            spheres[i].Message.Serialize(writer);
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
    /// Starts the leave timeout on every sphere a disconnecting player shared, or removes them
    /// straight away when the timeout is 0.
    /// </summary>
    public static void RemovePlayerSpheres(int peerId)
    {
        ushort playerId = (ushort)peerId;
        int leaveTimeoutSeconds = BasisNetworkServer.Security.BasisResourceLimitManager.ContentSphereLeaveTimeoutSeconds;
        long now = Stopwatch.GetTimestamp();
        var shared = ActiveSpheres.Where(kvp => !kvp.Value.SharerLeft && kvp.Value.SharerId == playerId)
                                  .ToArray();

        foreach (KeyValuePair<string, ActiveSphere> kvp in shared)
        {
            if (leaveTimeoutSeconds > 0)
            {
                if (ActiveSpheres.TryUpdate(kvp.Key, new ActiveSphere(kvp.Value.Message, kvp.Value.DroppedAt, true, now), kvp.Value))
                {
                    BNL.Log($"Content sphere {kvp.Key} stays {leaveTimeoutSeconds}s after its sharer {playerId} left.");
                }
            }
            else if (ActiveSpheres.TryRemove(kvp.Key, out _))
            {
                BroadcastCleanup(kvp.Key, playerId);
            }
        }
    }

    public static void StartExpiry()
    {
        Timer timer = new Timer(_ => ExpireTick(), null, ExpiryIntervalMs, ExpiryIntervalMs);
        Interlocked.Exchange(ref expiryTimer, timer)?.Dispose();
    }

    public static void StopExpiry()
    {
        Interlocked.Exchange(ref expiryTimer, null)?.Dispose();
    }

    private static void ExpireTick()
    {
        if (Interlocked.Exchange(ref expiring, 1) == 1) return;
        try
        {
            ExpireSpheres(Stopwatch.GetTimestamp());
        }
        catch (Exception e)
        {
            BNL.LogError($"Content sphere expiry failed: {e.Message} {e.StackTrace}");
        }
        finally
        {
            Volatile.Write(ref expiring, 0);
        }
    }

    internal static int ExpireSpheres(long now)
    {
        long leaveTimeout = BasisNetworkServer.Security.BasisResourceLimitManager.ContentSphereLeaveTimeoutSeconds * Stopwatch.Frequency;
        long deletionTimer = BasisNetworkServer.Security.BasisResourceLimitManager.ContentSphereDeletionTimerSeconds * Stopwatch.Frequency;
        int expired = 0;
        foreach (KeyValuePair<string, ActiveSphere> kvp in ActiveSpheres)
        {
            ActiveSphere sphere = kvp.Value;
            bool due = (sphere.SharerLeft && now - sphere.SharerLeftAt >= leaveTimeout) || (deletionTimer > 0 && now - sphere.DroppedAt >= deletionTimer);
            if (due && ((ICollection<KeyValuePair<string, ActiveSphere>>)ActiveSpheres).Remove(kvp))
            {
                BNL.Log($"Content sphere expired: {kvp.Key}");
                BroadcastCleanup(kvp.Key, sphere.SharerId);
                expired++;
            }
        }
        return expired;
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
