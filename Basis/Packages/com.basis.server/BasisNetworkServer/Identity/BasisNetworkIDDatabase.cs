using Basis.Network.Core;
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
        private static int exhaustedLogged;

        // How many ids each connected peer has been assigned this session. The shared ushort space is
        // only reclaimed when the instance empties, so without a per-peer ceiling one client can
        // register 65,536 distinct strings and permanently lock everyone else out of registering any
        // networked object (plus a reliable broadcast + 3 log lines per assignment). Entries are never
        // removed individually so this count only grows during a peer's session and is dropped on
        // disconnect — it cannot drift.
        private static readonly ConcurrentDictionary<int, int> PerPeerAssignedCount = new ConcurrentDictionary<int, int>();
        // Peers we have already warned about hitting the cap, so a client that keeps requesting after
        // the limit cannot turn one reject into a log flood (the flood this cap exists to stop).
        private static readonly ConcurrentDictionary<int, byte> PerPeerCapWarned = new ConcurrentDictionary<int, byte>();
        private const int DefaultMaxNetworkIdsPerPlayer = 32768;

        private static int ResolveMaxIdsPerPlayer()
        {
            int configured = NetworkServer.Configuration?.MaxNetworkIdsPerPlayer ?? 0;
            return configured > 0 ? configured : DefaultMaxNetworkIdsPerPlayer;
        }

        /// <summary>Drops a departed peer's per-session assignment count. The ids themselves persist
        /// until the instance empties (Reset); this only frees the throttling counter.</summary>
        public static void RemovePeer(int peerId)
        {
            PerPeerAssignedCount.TryRemove(peerId, out _);
            PerPeerCapWarned.TryRemove(peerId, out _);
        }
        private static readonly object AssignLock = new object();
        public static void AddOrFindNetworkID(NetPeer NetPeer, string UniqueStringID)
        {
            if (UshortNetworkDatabase.TryGetValue(UniqueStringID, out ushort Value))
            {
                SendExistingNetworkID(NetPeer, UniqueStringID, Value);
                return;
            }
            lock (AssignLock)
            {
                if (UshortNetworkDatabase.TryGetValue(UniqueStringID, out Value))
                {
                    SendExistingNetworkID(NetPeer, UniqueStringID, Value);
                    return;
                }

                // Per-peer cap: stop one client consuming the shared id space and locking everyone
                // else out. The count only grows during a session and is cleared on disconnect, so it
                // cannot drift into a false reject.
                int perPeerCap = ResolveMaxIdsPerPlayer();
                if (PerPeerAssignedCount.TryGetValue(NetPeer.Id, out int assigned) && assigned >= perPeerCap)
                {
                    if (PerPeerCapWarned.TryAdd(NetPeer.Id, 0))
                    {
                        BNL.LogError($"Peer {NetPeer.Id} reached the per-player network-id limit ({perPeerCap}); dropping registration for {UniqueStringID} and further ids this session.");
                    }
                    return;
                }

                // Log that we are assigning a new ID
                BNL.Log($"No existing ID found for {UniqueStringID}. Assigning a new ID.");

                // Generate a new unique ushort ID (thread-safe increment)
                int newCounter = Interlocked.Increment(ref counter);

                // Check if we exceeded the ushort range
                if (newCounter > ushort.MaxValue)
                {
                    Interlocked.Decrement(ref counter); // Roll back
                    // Log-and-drop, never throw: ids arrive per client message, so at the ceiling a
                    // throw per request became an exception storm (stack trace string per message)
                    // through the message processor. The requester simply gets no assignment.
                    if (Interlocked.Exchange(ref exhaustedLogged, 1) == 0)
                    {
                        BNL.LogError($"NetID space exhausted ({ushort.MaxValue} ids assigned since the server was last empty); dropping request for {UniqueStringID}.");
                    }
                    return;
                }

                ushort newID = (ushort)newCounter;

                // Add to the database
                UshortNetworkDatabase[UniqueStringID] = newID;
                PerPeerAssignedCount.AddOrUpdate(NetPeer.Id, 1, (_, c) => c + 1);
                BNL.Log($"New ID {newID} assigned to {UniqueStringID}");

                // Notify the requesting peer and broadcast to others
                ServerNetIDMessage SUIMA = new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage() { playerID = UniqueStringID },
                    UshortUniqueIDMessage = new UshortUniqueIDMessage() { UniqueIDUshort = newID }
                };
                NetDataWriter Writer = NetworkServer.RentWriter();
                SUIMA.Serialize(Writer);

                NetworkServer.BroadcastMessageToClients(Writer, BasisNetworkCommons.netIDAssignChannel, NetworkServer.PeerSnapshot, DeliveryMethod.ReliableOrdered);
                NetworkServer.ReturnWriter(Writer);
                BNL.Log($"Broadcasted new ID ({newID}) for {UniqueStringID} to all connected peers.");
            }
        }
        private static void SendExistingNetworkID(NetPeer NetPeer, string UniqueStringID, ushort Value)
        {
            // We already know about it, let's just give it back to that player
            ServerNetIDMessage SNIM = new ServerNetIDMessage
            {
                NetIDMessage = new NetIDMessage() { playerID = UniqueStringID },
                UshortUniqueIDMessage = new UshortUniqueIDMessage() { UniqueIDUshort = Value }
            };
            NetDataWriter Writer = NetworkServer.RentWriter();
            SNIM.Serialize(Writer);
            NetworkServer.TrySend(NetPeer, Writer, BasisNetworkCommons.netIDAssignChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(Writer);
            BNL.Log($"Sent existing NetID ({Value}) for {UniqueStringID} to peer {NetPeer.Id}");
        }

        public static bool GetAllNetworkID(out List<ServerNetIDMessage> ServerUniqueIDMessages)
        {
            ServerUniqueIDMessages = new List<ServerNetIDMessage>();
            foreach (KeyValuePair<string, ushort> pair in UshortNetworkDatabase)
            {
                ServerNetIDMessage SUIM = new ServerNetIDMessage
                {
                    NetIDMessage = new NetIDMessage() { playerID = pair.Key },
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
            PerPeerAssignedCount.Clear();
            PerPeerCapWarned.Clear();
            Interlocked.Exchange(ref counter, -1);
            Interlocked.Exchange(ref exhaustedLogged, 0);
            BNL.Log("Database reset complete. Counter set to -1.");
        }
    }
}
