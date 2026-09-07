using Basis.Network.Core;
using BasisNetworkCore;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static DarkRift.Basis_Common.Serializable.SerializableBasis;
namespace Basis.Network.Server.Ownership
{
    public static class BasisNetworkOwnership
    {
        // A dictionary for easy lookup by object ID (Object unique string ID -> Ownership ID)
        public static ConcurrentDictionary<string, ushort> ownershipByObjectId = new ConcurrentDictionary<string, ushort>();

        public static readonly object LockObject = new object();  // For synchronized multi-step operations
        public static void SendOutOwnershipInformation(NetPeer Peer)
        {
            NetDataWriter Writer = NetworkServer.RentWriter();
            OwnershipTransferMessage ownershipTransferMessage = new OwnershipTransferMessage();
            lock (LockObject)
            {
                foreach (KeyValuePair<string, ushort> Ownership in ownershipByObjectId)
                {
                    ownershipTransferMessage.playerIdMessage.playerID = Ownership.Value;
                    ownershipTransferMessage.ownershipID = Ownership.Key;
                    ownershipTransferMessage.Serialize(Writer);
                    NetworkServer.TrySend(Peer, Writer, BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, DeliveryMethod.ReliableOrdered);
                    Writer.Reset();
                }
            }
            NetworkServer.ReturnWriter(Writer);
        }
        public static void OwnershipResponse(NetPacketReader Reader, NetPeer Peer)
        {
            OwnershipTransferMessage ownershipTransferMessage = new OwnershipTransferMessage();
            ownershipTransferMessage.Deserialize(Reader);
            Reader.Recycle();
            //if we are not aware of this ownershipID lets only give back to that client that its been assigned to them
            //the goal here is to make it so ownership understanding has to be requested.
            //once a ownership has been requested there good for life or when a ownership switch happens.
            NetDataWriter Writer = NetworkServer.RentWriter();
            lock (LockObject)
            {
                NetworkRequestNewOrExisting(ownershipTransferMessage, (ushort)Peer.Id, out ushort currentOwner);
                ownershipTransferMessage.playerIdMessage.playerID = currentOwner;
                ownershipTransferMessage.Serialize(Writer);
                BNL.Log("OwnershipResponse " + currentOwner + " for " + ownershipTransferMessage.ownershipID);
                NetworkServer.TrySend(Peer, Writer, BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, DeliveryMethod.ReliableOrdered);
            }
            NetworkServer.ReturnWriter(Writer);
        }
        /// <summary>
        /// this api removes a owner from the object,
        /// example dropping a pickup tells the server that no one owns it anymore.
        /// </summary>
        /// <param name="Reader"></param>
        /// <param name="Peer"></param>
        public static void RemoveOwnership(NetPacketReader Reader, NetPeer Peer)
        {
            OwnershipTransferMessage ownershipTransferMessage = new OwnershipTransferMessage();
            ownershipTransferMessage.Deserialize(Reader);
            Reader.Recycle();
            lock (LockObject)
            {
                if (ownershipByObjectId.TryGetValue(ownershipTransferMessage.ownershipID, out ushort PlayerId))
                {
                    // Authorize against the sending peer, not the id in the packet: the client
                    // fills that field in itself, so trusting it lets any peer release any
                    // object by naming its owner.
                    if (PlayerId == (ushort)Peer.Id)
                    {
                        ownershipTransferMessage.playerIdMessage.playerID = PlayerId;
                        if (RemoveObject(ownershipTransferMessage.ownershipID))
                        {
                            NetDataWriter Writer = NetworkServer.RentWriter();
                            ownershipTransferMessage.Serialize(Writer);
                            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                            NetworkServer.ReturnWriter(Writer);
                        }
                        else
                        {
                            BNL.LogError(ownershipTransferMessage.ownershipID + " failure to remove!");
                        }
                    }
                    else
                    {
                        BNL.LogError("the player that requested this did not own the object");
                    }
                }
                else
                {
                    BNL.LogError("Ownership was not found for " + ownershipTransferMessage.ownershipID);
                }
            }
        }
        /// <summary>
        /// Handles the ownership transfer for all clients with proper error handling.
        /// </summary>
        public static void OwnershipTransfer(NetPacketReader Reader, NetPeer Peer)
        {
            OwnershipTransferMessage ownershipTransferMessage = new OwnershipTransferMessage();
            ownershipTransferMessage.Deserialize(Reader);
            Reader.Recycle();

            ushort ClientId = (ushort)Peer.Id;
            NetDataWriter Writer = NetworkServer.RentWriter();
            //all clients need to know about a ownership switch
            lock (LockObject)
            {
                if (SwitchOwnership(ownershipTransferMessage.ownershipID, ClientId))
                {
                    ownershipTransferMessage.playerIdMessage.playerID = ClientId;
                }
                else
                {
                    //if we are not aware of this ownershipID lets only give back to that client that its been assigned to them
                    //the goal here is to make it so ownership understanding has to be requested.
                    //once a ownership has been requested there good for life or when a ownership switch happens.
                    NetworkRequestNewOrExisting(ownershipTransferMessage, ClientId, out ushort currentOwner);
                    ownershipTransferMessage.playerIdMessage.playerID = currentOwner;
                }
                ownershipTransferMessage.Serialize(Writer);
                BNL.Log("OwnershipResponse " + ownershipTransferMessage.ownershipID + " for " + ownershipTransferMessage.playerIdMessage.playerID);
                NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.ChangeCurrentOwnerRequestChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
            }
            NetworkServer.ReturnWriter(Writer);
        }
        /// <summary>
        /// Requests either new or existing ownership with thread safety and rollback.
        /// </summary>
        public static bool NetworkRequestNewOrExisting(OwnershipTransferMessage ownershipInitializeMessage, ushort requesterId, out ushort ownershipInfo)
        {
            if (GetOwnershipInformation(ownershipInitializeMessage.ownershipID, out ownershipInfo))
            {
                // Ownership already exists, no need to add
                return false;
            }
            else
            {
                if (!AddOwnership(ownershipInitializeMessage.ownershipID, requesterId))
                {
                    BNL.LogError($"Error while adding ownership for: {ownershipInitializeMessage.ownershipID}");
                    return false;
                }
                else
                {
                    ownershipInfo = requesterId;
                }
            }
            return true;
        }
        /// <summary>
        /// Adds an object with ownership information to the database in a thread-safe manner.
        /// </summary>
        public static bool AddOwnership(string objectId, ushort ownerId)
        {
            if (ownershipByObjectId.TryAdd(objectId, ownerId))
            {
                BNL.Log($"Object {objectId} added with owner {ownerId}");
                return true;
            }
            else
            {
                BNL.LogError($"Failed to add Object {objectId} to object ownership lookup.");
                return false;
            }
        }
        /// <summary>
        /// Removes an object and its ownership information from the database in a thread-safe and consistent manner.
        /// </summary>
        public static bool RemoveObject(string objectId)
        {
            lock (LockObject)
            {
                if (ownershipByObjectId.TryRemove(objectId, out ushort ownershipInformation))
                {
                    BNL.Log($"Object {objectId} owned by {ownershipInformation} removed from database.");
                    return true;
                }
                else
                {
                    BNL.LogError($"Failed to remove object with ID {objectId}.");
                    return false;
                }
            }
        }
        /// <summary>
        /// Switches the ownership of an object in a thread-safe manner.
        /// </summary>
        public static bool SwitchOwnership(string objectId, ushort newOwnerId)
        {
            lock (LockObject)
            {
                if (ownershipByObjectId.TryGetValue(objectId, out ushort currentOwnerId))
                {
                    // Update ownership only if the current owner matches
                    if (ownershipByObjectId.TryUpdate(objectId, newOwnerId, currentOwnerId))
                    {
                        BNL.Log($"Ownership of object {objectId} switched from {currentOwnerId} to {newOwnerId}.");
                        return true;
                    }
                }
                else
                {
                    return AddOwnership(objectId, newOwnerId);
                }

                BNL.LogError($"Object with ID {objectId} does not exist or ownership change failed.");
                return false;
            }
        }
        /// <summary>
        /// Checks if an object exists in the database.
        /// </summary>
        public static bool DoesObjectExistInDatabase(string objectId)
        {
            return ownershipByObjectId.ContainsKey(objectId); // Thread-safe lookup without extra locking
        }
        /// <summary>
        /// Retrieves ownership information for a specific object ID in a thread-safe manner.
        /// </summary>
        public static bool GetOwnershipInformation(string objectId, out ushort ownershipInfo)
        {
            if (ownershipByObjectId.TryGetValue(objectId, out ownershipInfo))
            {
                return true;
            }

            ownershipInfo = 0;
            return false;
        }
        /// <summary>
        /// Prints current ownership database for debugging purposes with thread safety.
        /// </summary>
        public static void PrintOwnershipDatabase()
        {
            BNL.Log("Current Ownership Database:");

            lock (LockObject)
            {
                foreach (var entry in ownershipByObjectId)
                {
                    BNL.Log($"Ownership ID: {entry.Key}, Owner ID: {entry.Value}");
                }
            }
        }
        /// <summary>
        /// Hands a departing player's objects to the longest-connected remaining peer (drops them when nobody remains) and notifies all clients.
        /// </summary>
        public static void RemovePlayerOwnership(int playerId)
        {
            lock (LockObject)
            {
                List<string> objectsToRemove = new List<string>();

                // Collect all object IDs owned by the player
                foreach (KeyValuePair<string, ushort> entry in ownershipByObjectId)
                {
                    if (entry.Value == playerId)
                    {
                        objectsToRemove.Add(entry.Key);
                    }
                }
                if (objectsToRemove.Count == 0)
                {
                    return;
                }
                bool migrate = TrySelectSuccessor(playerId, out ushort successor, out List<NetPeer> recipients);
                OwnershipTransferMessage ownershipTransferMessage = new OwnershipTransferMessage();
                NetDataWriter Writer = NetworkServer.RentWriter();
                int handled = 0;
                foreach (string OwnershipId in objectsToRemove)
                {
                    if (migrate)
                    {
                        if (!ownershipByObjectId.TryUpdate(OwnershipId, successor, (ushort)playerId))
                        {
                            continue;
                        }
                    }
                    else if (!ownershipByObjectId.TryRemove(OwnershipId, out _))
                    {
                        continue;
                    }
                    handled++;
                    if (recipients.Count == 0)
                    {
                        continue;
                    }
                    Writer.Reset();
                    ownershipTransferMessage.playerIdMessage = new SerializableBasis.PlayerIdMessage();
                    ownershipTransferMessage.playerIdMessage.playerID = migrate ? successor : (ushort)playerId;
                    ownershipTransferMessage.ownershipID = OwnershipId;
                    ownershipTransferMessage.Serialize(Writer);
                    NetworkServer.BroadcastMessageToClients(Writer, migrate ? BasisNetworkCommons.ChangeCurrentOwnerRequestChannel : BasisNetworkCommons.RemoveCurrentOwnerRequestChannel, ref recipients, DeliveryMethod.ReliableOrdered);
                }
                NetworkServer.ReturnWriter(Writer);
                if (migrate)
                {
                    BNL.Log($"Player {playerId}'s ownership of {handled} objects migrated to player {successor}.");
                }
                else
                {
                    BNL.Log($"Player {playerId}'s ownership removed from {handled} objects.");
                }
            }
        }
        public static bool TrySelectSuccessor(int departingId, out ushort successor, out List<NetPeer> recipients)
        {
            successor = 0;
            recipients = new List<NetPeer>();
            bool found = false;
            long bestSeq = long.MaxValue;
            int bestId = int.MaxValue;
            foreach (KeyValuePair<int, NetPeer> entry in NetworkServer.AuthenticatedPeers)
            {
                int id = entry.Key;
                if (id == departingId || entry.Value == null || id < 0 || id > ushort.MaxValue)
                {
                    continue;
                }
                recipients.Add(entry.Value);
                long seq = BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.TryGetSeq(id, out long s) ? s : long.MaxValue;
                if (!found || seq < bestSeq || (seq == bestSeq && id < bestId))
                {
                    found = true;
                    bestSeq = seq;
                    bestId = id;
                    successor = (ushort)id;
                }
            }
            return found;
        }
    }
}
