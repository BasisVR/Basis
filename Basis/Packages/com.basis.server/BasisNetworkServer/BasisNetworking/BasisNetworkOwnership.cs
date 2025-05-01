using Basis.Network.Core;
using BasisNetworkCore;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Concurrent;
using System.Collections.Generic;
using static DarkRift.Basis_Common.Serializable.SerializableBasis;
namespace Basis.Network.Server.Ownership
{
    public static class BasisNetworkOwnership
    {
        // A dictionary for easy lookup by object ID (Object unique string ID -> Ownership ID)
        public static ConcurrentDictionary<string, ushort> ownershipByObjectId = new ConcurrentDictionary<string, ushort>();

        public static readonly object LockObject = new object();  // For synchronized multi-step operations
        public static void OwnershipResponse(NetPacketReader Reader, NetPeer Peer)
        {
            OwnershipTransferMessage ownershipTransferMessage = new OwnershipTransferMessage();
            ownershipTransferMessage.Deserialize(Reader);
            Reader.Recycle();
            //if we are not aware of this ownershipID lets only give back to that client that its been assigned to them
            //the goal here is to make it so ownership understanding has to be requested.
            //once a ownership has been requested there good for life or when a ownership switch happens.
            NetworkRequestNewOrExisting(ownershipTransferMessage, out ushort currentOwner);
            NetDataWriter Writer = new NetDataWriter(true, 2);
            ownershipTransferMessage.playerIdMessage.playerID = currentOwner;
            ownershipTransferMessage.Serialize(Writer);
            BNL.Log("OwnershipResponse " + currentOwner + " for " + ownershipTransferMessage.playerIdMessage.playerID);
            NetworkServer.SendOutValidated(Peer, Writer, BasisNetworkCommons.GetCurrentOwnerRequest, DeliveryMethod.ReliableOrdered);
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
                    if (PlayerId == ownershipTransferMessage.playerIdMessage.playerID)
                    {
                        if (RemoveObject(ownershipTransferMessage.ownershipID))
                        {
                            NetDataWriter Writer = new NetDataWriter(true);
                            ownershipTransferMessage.Serialize(Writer);
                            NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.RemoveCurrentOwnerRequest, BasisPlayerArray.GetSnapshot(), DeliveryMethod.ReliableSequenced);
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
            NetDataWriter Writer = new NetDataWriter(true, 2);
            //all clients need to know about a ownership switch
            if (SwitchOwnership(ownershipTransferMessage.ownershipID, ClientId))
            {
                ownershipTransferMessage.playerIdMessage.playerID = ClientId;
                ownershipTransferMessage.Serialize(Writer);

                BNL.Log("OwnershipResponse " + ownershipTransferMessage.ownershipID + " for " + ownershipTransferMessage.playerIdMessage);
                NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.ChangeCurrentOwnerRequest, BasisPlayerArray.GetSnapshot(), DeliveryMethod.ReliableSequenced);
            }
            else
            {
                //if we are not aware of this ownershipID lets only give back to that client that its been assigned to them
                //the goal here is to make it so ownership understanding has to be requested.
                //once a ownership has been requested there good for life or when a ownership switch happens.
                NetworkRequestNewOrExisting(ownershipTransferMessage, out ushort currentOwner);
                ownershipTransferMessage.Serialize(Writer);
                NetworkServer.SendOutValidated(Peer, Writer, BasisNetworkCommons.ChangeCurrentOwnerRequest, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }
        /// <summary>
        /// Requests either new or existing ownership with thread safety and rollback.
        /// </summary>
        public static bool NetworkRequestNewOrExisting(OwnershipTransferMessage ownershipInitializeMessage, out ushort ownershipInfo)
        {
            if (GetOwnershipInformation(ownershipInitializeMessage.ownershipID, out ownershipInfo))
            {
                // Ownership already exists, no need to add
                return false;
            }
            else
            {
                if (!AddOwnership(ownershipInitializeMessage.ownershipID, ownershipInitializeMessage.playerIdMessage.playerID))
                {
                    BNL.LogError($"Error while adding ownership for: {ownershipInitializeMessage.ownershipID}");
                    return false;
                }
                else
                {
                    ownershipInfo = ownershipInitializeMessage.playerIdMessage.playerID;
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
                    AddOwnership(objectId, newOwnerId);
                    return true;
                    //BNL.LogError($"Ownership failed to switch ObjectId " + objectId + " is not in dictionary");
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
        /// Removes all ownership of a specific player and notifies all clients.
        /// </summary>
        public static void RemovePlayerOwnership(ushort playerId)
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
                foreach (string client in objectsToRemove)
                {
                    ownershipByObjectId.TryRemove(client, out ushort user);
                }
                BNL.Log($"Player {playerId}'s ownership removed from {objectsToRemove.Count} objects.");
            }
        }
    }
}
