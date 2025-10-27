using Basis.Network.Core;
using BasisNetworkCore;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

                NetDataWriter writer = new NetDataWriter(true);
                unloadResource.Serialize(writer);
                NetPeer[] peers = NetworkServer.AuthenticatedPeers.Values.ToArray();
                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.LoadResourceChannel,
                    peers,
                    DeliveryMethod.ReliableSequenced
                );

                // Remove the non-persistent resource from the database
                UshortNetworkDatabase.Remove(llr.LoadedNetID,out LocalLoadResource Resource);
            }
        }
    }
    public static void SendOutAllResources(NetPeer NewConnection)
    {
        LocalLoadResource[] Resource = UshortNetworkDatabase.Values.ToArray();
        if (Resource != null)
        {
            int length = Resource.Length;
            for (int Index = 0; Index < length; Index++)
            {
                LocalLoadResource LLR = Resource[Index];
                NetDataWriter Writer = new NetDataWriter(true);
                LLR.Serialize(Writer);
                NetworkServer.TrySend(NewConnection, Writer, BasisNetworkCommons.LoadResourceChannel, DeliveryMethod.ReliableOrdered);
            }
        }
    }
    public static void LoadResource(LocalLoadResource LocalLoadResource)
    {
        if (UshortNetworkDatabase.ContainsKey(LocalLoadResource.LoadedNetID) == false)
        {
            NetDataWriter Writer = new NetDataWriter(true);
            LocalLoadResource.Serialize(Writer);
            if (UshortNetworkDatabase.TryAdd(LocalLoadResource.LoadedNetID, LocalLoadResource))
            {
                BNL.Log("Adding Object " + LocalLoadResource.LoadedNetID);
                NetPeer[] peers = NetworkServer.AuthenticatedPeers.Values.ToArray();
                NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.LoadResourceChannel, peers, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                BNL.LogError("Try Add Failed Already have Object Loaded With " + LocalLoadResource.LoadedNetID);
            }
        }
        else
        {
            BNL.LogError("Already have Object Loaded With " + LocalLoadResource.LoadedNetID);
        }
    }
    public static void UnloadResource(UnLoadResource UnLoadResource)
    {
        if (UshortNetworkDatabase.TryRemove(UnLoadResource.LoadedNetID,out LocalLoadResource Resource))
        {
            NetDataWriter Writer = new NetDataWriter(true);
            UnLoadResource.Serialize(Writer);
            BNL.Log("Removing Object " + UnLoadResource.LoadedNetID);
            NetPeer[] peers = NetworkServer.AuthenticatedPeers.Values.ToArray();
            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.UnloadResourceChannel, peers, DeliveryMethod.ReliableOrdered);
        }
        else
        {
            BNL.LogError($"Trying to unload a object that does not exist! ID Proved was [{UnLoadResource.LoadedNetID}]");
        }
    }
}
