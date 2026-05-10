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

    /// <summary>
    /// Partial reset used for ordinary peer lifecycle.
    /// Keeps persistent resources alive while removing non-persistent ones.
    /// </summary>
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

    /// <summary>
    /// Full reset used on complete server teardown / rehost.
    /// Clears all resources, including persistent ones, so a new local host
    /// session does not replay stale scene state from the previous session.
    /// </summary>
    public static void ResetAll()
    {
        UshortNetworkDatabase.Clear();
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
}
