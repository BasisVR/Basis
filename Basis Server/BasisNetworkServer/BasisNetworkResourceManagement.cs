using Basis.Network.Core;
using BasisPermissions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

public static class BasisNetworkResourceManagement
{
    public static ConcurrentDictionary<string, LocalLoadResource> UshortNetworkDatabase = new ConcurrentDictionary<string, LocalLoadResource>();
    public static void Reset()
    {
        LocalLoadResource[] resourceArray = UshortNetworkDatabase.Values.ToArray();
        int length = resourceArray.Length;

        for (int index = 0; index < length; index++)
        {
            LocalLoadResource llr = resourceArray[index];

            if (!llr.Persist)
            {
                // Prepare and send the unload resource message
                UnLoadResource unloadResource = new UnLoadResource
                {
                    Mode = llr.Mode,
                    LoadedNetID = llr.LoadedNetID
                };

                NetDataWriter writer = NetworkServer.RentWriter();
                unloadResource.Serialize(writer);
                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.UnloadResourceChannel,
                    NetworkServer.PeerSnapshot,
                    DeliveryMethod.ReliableOrdered
                );
                NetworkServer.ReturnWriter(writer);

                // Remove the non-persistent resource from the database
                UshortNetworkDatabase.Remove(llr.LoadedNetID,out LocalLoadResource Resource);
            }
        }
    }
    public static void RemovePeerResources(string uuid)
    {
        if (string.IsNullOrEmpty(uuid)) return;
        LocalLoadResource[] resourceArray = UshortNetworkDatabase.Values.ToArray();
        int length = resourceArray.Length;
        for (int index = 0; index < length; index++)
        {
            LocalLoadResource llr = resourceArray[index];
            if (llr.Persist || llr.UUIDOfCreator != uuid)
            {
                continue;
            }
            UnLoadResource unloadResource = new UnLoadResource
            {
                Mode = llr.Mode,
                LoadedNetID = llr.LoadedNetID
            };
            NetDataWriter writer = NetworkServer.RentWriter();
            unloadResource.Serialize(writer);
            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.UnloadResourceChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered
            );
            NetworkServer.ReturnWriter(writer);
            UshortNetworkDatabase.Remove(llr.LoadedNetID, out LocalLoadResource Resource);
        }
    }
    public static void SendOutAllResources(NetPeer NewConnection)
    {
        LocalLoadResource[] Resource = UshortNetworkDatabase.Values.ToArray();
        if (Resource != null)
        {
            int length = Resource.Length;
            NetDataWriter Writer = NetworkServer.RentWriter();
            for (int Index = 0; Index < length; Index++)
            {
                Writer.Reset();
                LocalLoadResource LLR = Resource[Index];

                // For synchronized resources (LoadStrategy == 2), check if the session
                // is still active. If it already completed, send as immediate (0) so
                // the late joiner spawns right away instead of waiting for a spawn
                // signal that will never come. If still active, add the late joiner
                // to the session so they participate in the synchronized load.
                if (LLR.LoadStrategy == 2)
                {
                    if (BasisNetworkPreloadResourceManagement.ActiveSessions.TryGetValue(LLR.LoadedNetID, out var session))
                    {
                        // Session still in progress - add late joiner to peer count
                        session.TotalPeerCount++;
                    }
                    else
                    {
                        // Session already completed - send as immediate load
                        LLR.LoadStrategy = 0;
                    }
                }

                LLR.Serialize(Writer);
                NetworkServer.TrySend(NewConnection, Writer, BasisNetworkCommons.LoadResourceChannel, DeliveryMethod.ReliableOrdered);
            }
            NetworkServer.ReturnWriter(Writer);
        }
    }
    // Predownload broadcast: tell every connected client to cache the bundle to disc now.
    // Deliberately NOT added to UshortNetworkDatabase - it is not a loaded resource, so it is
    // never replayed to late joiners by SendOutAllResources and never spawns anything.
    public static void PredownloadResource(LocalLoadResource LocalLoadResource)
    {
        NetDataWriter Writer = NetworkServer.RentWriter();
        LocalLoadResource.Serialize(Writer);
        BNL.Log("Broadcasting predownload for " + LocalLoadResource.CombinedURL);
        NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.LoadResourceChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(Writer);
    }
    public static void LoadResource(LocalLoadResource LocalLoadResource)
    {
        if (UshortNetworkDatabase.ContainsKey(LocalLoadResource.LoadedNetID) == false)
        {
            NetDataWriter Writer = NetworkServer.RentWriter();
            LocalLoadResource.Serialize(Writer);
            if (UshortNetworkDatabase.TryAdd(LocalLoadResource.LoadedNetID, LocalLoadResource))
            {
                BNL.Log("Adding Object " + LocalLoadResource.LoadedNetID);
                NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.LoadResourceChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                BNL.LogError("Try Add Failed Already have Object Loaded With " + LocalLoadResource.LoadedNetID);
            }
            NetworkServer.ReturnWriter(Writer);
        }
        else
        {
            BNL.LogError("Already have Object Loaded With " + LocalLoadResource.LoadedNetID);
        }
    }
    // Server-authoritative path — skips IsAdminLocked peer check because the caller
    // (REST API, etc.) is already authenticated at a higher level than any game peer.
    // Returns false if the resource was not found (TryRemove failed atomically).
    public static bool UnloadResource(UnLoadResource unLoadResource)
    {
        if (!UshortNetworkDatabase.TryRemove(unLoadResource.LoadedNetID, out _))
        {
            BNL.LogError($"[Server] Trying to unload an object that does not exist: {unLoadResource.LoadedNetID}");
            return false;
        }

        NetDataWriter writer = NetworkServer.RentWriter();
        unLoadResource.Serialize(writer);
        BNL.Log("Removing Object (server) " + unLoadResource.LoadedNetID);
        NetworkServer.BroadcastMessageToClients(writer, BasisNetworkCommons.UnloadResourceChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
        NetworkServer.ReturnWriter(writer);
        return true;
    }

    public static void UnloadResource(UnLoadResource unLoadResource, NetPeer peer)
    {
        if (!UshortNetworkDatabase.TryGetValue(unLoadResource.LoadedNetID, out LocalLoadResource resource))
        {
            BNL.LogError($"Trying to unload an object that does not exist! ID Provided was [{unLoadResource.LoadedNetID}]");
            return;
        }

        // Admin lock validation
        if (resource.IsAdminLocked && !PermissionIntegration.HasValidRequirement(peer, PermNodes.protection))
        {
            return;
        }

        // Creator-or-moderator, same rule SetStatic applies. The unload permission node is in the
        // default group, so without this any player can delete every other player's props.
        bool isModeratorUnload = PermissionIntegration.HasValidRequirement(peer, PermNodes.protection);
        bool isCreatorUnload = NetworkServer.AuthIdentity.NetIDToUUID(peer, out string unloadRequesterUuid)
            && !string.IsNullOrEmpty(resource.UUIDOfCreator)
            && unloadRequesterUuid == resource.UUIDOfCreator;
        if (!isCreatorUnload && !isModeratorUnload)
        {
            BNL.LogError($"Peer {peer.Id} tried to unload [{unLoadResource.LoadedNetID}] they did not create.");
            return;
        }

        // Only remove AFTER validation
        if (!UshortNetworkDatabase.TryRemove(unLoadResource.LoadedNetID, out _))
        {
            BNL.LogError($"Failed to remove object [{unLoadResource.LoadedNetID}] after validation.");
            return;
        }

        NetDataWriter writer = NetworkServer.RentWriter();
        unLoadResource.Serialize(writer);

        BNL.Log("Removing Object " + unLoadResource.LoadedNetID);

        NetworkServer.BroadcastMessageToClients(
            writer,
            BasisNetworkCommons.UnloadResourceChannel,
            NetworkServer.PeerSnapshot,
            DeliveryMethod.ReliableOrdered
        );
        NetworkServer.ReturnWriter(writer);
    }

    /// <summary>
    /// Toggle the server-authoritative "Static" flag on an already-spawned resource.
    /// Only the item's creator or a moderator (protection permission) may change it.
    /// On success the new state is stored and rebroadcast to every client (and replayed
    /// to late joiners via <see cref="SendOutAllResources"/>, which serializes the whole record).
    /// </summary>
    public static void SetStatic(ModifyResource modifyResource, NetPeer peer)
    {
        if (!UshortNetworkDatabase.TryGetValue(modifyResource.LoadedNetID, out LocalLoadResource resource))
        {
            BNL.LogError($"Trying to modify an object that does not exist! ID Provided was [{modifyResource.LoadedNetID}]");
            return;
        }

        // Admin-lock implies frozen — a request can't ask for "admin-locked but movable".
        bool targetAdminLocked = modifyResource.StaticAdminLocked;
        bool targetStatic = modifyResource.Static || targetAdminLocked;

        // Authorize. Any transition that touches the admin tier (entering OR leaving it) requires a
        // moderator — the item's creator can't set or clear an admin lock. Plain static toggles
        // (the non-admin tier) also allow the creator.
        bool involvesAdminTier = resource.StaticAdminLocked || targetAdminLocked;
        bool isModerator = PermissionIntegration.HasValidRequirement(peer, PermNodes.protection);
        bool isCreator = NetworkServer.AuthIdentity.NetIDToUUID(peer, out string requesterUuid)
            && !string.IsNullOrEmpty(resource.UUIDOfCreator)
            && requesterUuid == resource.UUIDOfCreator;
        bool allowed = involvesAdminTier ? isModerator : (isCreator || isModerator);
        if (!allowed)
        {
            return;
        }

        // No-op if nothing changes, to avoid spamming the network.
        if (resource.Static == targetStatic && resource.StaticAdminLocked == targetAdminLocked)
        {
            return;
        }

        // LocalLoadResource is a value type, so mutate a copy and write it back.
        resource.Static = targetStatic;
        resource.StaticAdminLocked = targetAdminLocked;
        UshortNetworkDatabase[modifyResource.LoadedNetID] = resource;

        // Normalize the broadcast so every client agrees on the resolved state + routing.
        modifyResource.Static = targetStatic;
        modifyResource.StaticAdminLocked = targetAdminLocked;
        modifyResource.Mode = resource.Mode;

        NetDataWriter writer = NetworkServer.RentWriter();
        modifyResource.Serialize(writer);
        BNL.Log($"Set Static={targetStatic} AdminLocked={targetAdminLocked} on Object {modifyResource.LoadedNetID}");
        NetworkServer.BroadcastMessageToClients(
            writer,
            BasisNetworkCommons.ModifyResourceChannel,
            NetworkServer.PeerSnapshot,
            DeliveryMethod.ReliableOrdered
        );
        NetworkServer.ReturnWriter(writer);
    }
}
