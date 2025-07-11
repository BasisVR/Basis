using Basis.Network.Core;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkCore
{
    public static class BasisNetworkIDDatabase
    {
        public static ConcurrentDictionary<string, ushort> UshortNetworkDatabase = new ConcurrentDictionary<string, ushort>();
        private static int counter = -1; // Start at -1 so the first increment becomes 0
        public static void AddOrFindNetworkID(NetPeer NetPeer, string UniqueStringID)
        {
            if (UshortNetworkDatabase.TryGetValue(UniqueStringID, out ushort Value)) // This should basically never happen!
            {
                // We already know about it, let's just give it back to that player
                ServerNetIDMessage SNIM = new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage() { UniqueID = UniqueStringID },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage() { UniqueIDUshort = Value }
                };
                NetDataWriter Writer = new NetDataWriter(true);
                SNIM.Serialize(Writer);
                NetworkServer.SendOutValidated(NetPeer, Writer, BasisNetworkCommons.netIDAssignChannel, DeliveryMethod.ReliableOrdered);
                BNL.Log($"Sent existing NetID ({Value}) for {UniqueStringID} to peer {NetPeer.Address}");
            }
            else
            {
                // Log that we are assigning a new ID
                BNL.Log($"No existing ID found for {UniqueStringID}. Assigning a new ID.");

                // Check if we can assign a new ID
                if (counter >= ushort.MaxValue)
                {
                    // Log and throw an error
                    string errorMessage = $"Error: Cannot assign a new NetID for {UniqueStringID}. Maximum ID limit of {ushort.MaxValue} reached.";
                    BNL.Log(errorMessage);
                    throw new InvalidOperationException(errorMessage);
                }

                // Generate a new unique ushort ID
                ushort newID = (ushort)Interlocked.Increment(ref counter); // Thread-safe increment

                // Add to the database
                UshortNetworkDatabase[UniqueStringID] = newID;
                BNL.Log($"New ID {newID} assigned to {UniqueStringID}");

                // Notify the requesting peer and broadcast to others
                ServerNetIDMessage SUIMA = new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage() { UniqueID = UniqueStringID },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage() { UniqueIDUshort = newID }
                };
                NetDataWriter Writer = new NetDataWriter(true);
                SUIMA.Serialize(Writer);

                NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.netIDAssignChannel, BasisPlayerArray.GetSnapshot(), DeliveryMethod.ReliableOrdered);
                BNL.Log($"Broadcasted new ID ({newID}) for {UniqueStringID} to all connected peers.");
            }
        }

        public static bool GetAllNetworkID(out List<ServerNetIDMessage> ServerUniqueIDMessages)
        {
            ServerUniqueIDMessages = new List<ServerNetIDMessage>();
            foreach (KeyValuePair<string, ushort> pair in UshortNetworkDatabase)
            {
                ServerNetIDMessage SUIM = new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage() { UniqueID = pair.Key },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage() { UniqueIDUshort = pair.Value }
                };
                ServerUniqueIDMessages.Add(SUIM);
            }
            int Count = ServerUniqueIDMessages.Count;
            return Count != 0;
        }
        public static void RemoveUshortNetworkID(ushort netID)
        {
            BNL.Log($"Attempting to remove NetID: {netID}");
            // Remove based on value (ushort ID)
            var itemToRemove = UshortNetworkDatabase.FirstOrDefault(kvp => kvp.Value == netID);
            if (!string.IsNullOrEmpty(itemToRemove.Key))
            {
                if (UshortNetworkDatabase.TryRemove(itemToRemove.Key, out _))
                {
                    BNL.Log($"Successfully removed NetID: {netID} associated with UniqueStringID: {itemToRemove.Key}");
                }
                else
                {
                    BNL.Log($"Failed to remove NetID: {netID} (concurrent operation may have interfered)");
                }
            }
            else
            {
                BNL.Log($"NetID {netID} not found in the database.");
            }
        }

        public static void Reset()
        {
            BNL.Log("Resetting BasisNetworkIDDatabase...");
            UshortNetworkDatabase.Clear();
            Interlocked.Exchange(ref counter, -1);
            BNL.Log("Database reset complete. Counter set to -1.");
        }
    }
}
