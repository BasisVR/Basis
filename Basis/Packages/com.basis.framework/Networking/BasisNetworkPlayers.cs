using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Receivers;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Thread-safe registry for players/receivers, plus avatar/player conversion helpers.
    /// </summary>
    public static class BasisNetworkPlayers
    {
        // --- Collections (thread-safe) -------------------------------------
        public static readonly ConcurrentDictionary<ushort, BasisNetworkPlayer> Players = new();
        public static readonly ConcurrentDictionary<ushort, BasisNetworkReceiver> RemotePlayerReceivers = new();
        public static readonly ConcurrentDictionary<ushort, BasisRemotePlayer> RemotePlayers = new();
        public static readonly ConcurrentDictionary<ushort, byte> JoiningPlayers = new(); // used as a concurrent set
        public static readonly ConcurrentDictionary<string, ushort> OwnershipPairing = new();

        // Receiver snapshot for multi-threaded compute/apply phases.
        // Reusable buffer — grows on demand, never shrinks (avoids per-frame allocation).
        public static BasisNetworkReceiver[] ReceiversSnapshot = Array.Empty<BasisNetworkReceiver>();
        public static int ReceiverCount;
        public static uint SnapshotVersion;
        public static ushort LargestNetworkReceiverID;
        private static BasisNetworkReceiver[] _snapshotBuffer = Array.Empty<BasisNetworkReceiver>();

        // Dirty flag: only re-enumerate ConcurrentDictionary on player join/leave.
        // ConcurrentDictionary enumeration acquires bucket locks and walks all nodes — too expensive per frame.
        private static volatile bool _snapshotDirty = true;

        // --- Lifecycle helpers ---------------------------------------------
        public static void ClearAllRegistries()
        {
            foreach (KeyValuePair<ushort, BasisNetworkPlayer> BasisNetworkPlayer in Players)
            {
                try
                {
                    BasisNetworkHandleRemoval.HandleDisconnectIdImmediate(BasisNetworkPlayer.Key);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"ClearAllRegistries teardown failed for {BasisNetworkPlayer.Key}: {ex}");
                }
            }
            Players.Clear();
            RemotePlayerReceivers.Clear();
            RemotePlayers.Clear();
            JoiningPlayers.Clear();
            OwnershipPairing.Clear();
            _snapshotDirty = true;
        }

        /// <summary>
        /// Copies remote players into a reusable array. Only re-enumerates the
        /// ConcurrentDictionary when the player list has actually changed (dirty flag).
        /// In steady state this is effectively free.
        /// </summary>
        public static void PublishReceiversSnapshot()
        {
            if (!_snapshotDirty) return;
            SnapshotVersion++;

            int count = RemotePlayerReceivers.Count;
            if (count == 0)
            {
                // Null out stale references so GC can collect departed receivers
                for (int j = 0; j < ReceiverCount && j < _snapshotBuffer.Length; j++)
                {
                    _snapshotBuffer[j] = default;
                }
                ReceiverCount = 0;
                _snapshotDirty = false;
                return;
            }

            // Grow-only buffer: power-of-2 sizing avoids realloc churn on join/leave
            if (_snapshotBuffer.Length < count)
            {
                int newSize = 16;
                while (newSize < count) newSize <<= 1;
                _snapshotBuffer = new BasisNetworkReceiver[newSize];
            }

            // Enumerate directly (struct enumerator) — no Values/ToArray allocation
            int i = 0;
            foreach (var kvp in RemotePlayerReceivers)
            {
                if (i >= _snapshotBuffer.Length) break;
                _snapshotBuffer[i++] = kvp.Value;
            }

            // Null out stale trailing references from a previous larger snapshot
            int prevCount = ReceiverCount;
            for (int j = i; j < prevCount && j < _snapshotBuffer.Length; j++)
            {
                _snapshotBuffer[j] = default;
            }

            ReceiversSnapshot = _snapshotBuffer;
            ReceiverCount = i;
            _snapshotDirty = false;
        }

        // --- Registry APIs --------------------------------------------------
        public static bool AddPlayer(BasisNetworkPlayer netPlayer)
        {
            if (!BasisNetworkManagement.IsInitialized)
            {
                BasisDebug.LogError("No network Instance existed!");
                return false;
            }

            if (netPlayer == null || netPlayer.Player == null)
            {
                BasisDebug.LogError("NetPlayer or NetPlayer.Player was null!");
                return false;
            }

            if (Players.ContainsKey(netPlayer.playerId))
            {
                BasisDebug.LogWarning($"Player {netPlayer.playerId} already exists. Removing old entry before adding new one.");
                BasisNetworkHandleRemoval.HandleDisconnectIdImmediate(netPlayer.playerId);
            }

            if (!Players.TryAdd(netPlayer.playerId, netPlayer))
            {
                BasisDebug.LogError($"Failed to add player {netPlayer.playerId} to Players.");
                return false;
            }

            if (!netPlayer.Player.IsLocal)
            {
                if (RemotePlayerReceivers.ContainsKey(netPlayer.playerId))
                {
                    BasisDebug.LogWarning($"Remote player {netPlayer.playerId} already exists. Removing old entry before adding new one.");
                    BasisNetworkHandleRemoval.HandleDisconnectIdImmediate(netPlayer.playerId);
                }

                if (!RemotePlayerReceivers.TryAdd(netPlayer.playerId, (BasisNetworkReceiver)netPlayer))
                {
                    Players.TryRemove(netPlayer.playerId, out _);
                    BasisDebug.LogError($"Failed to add remote player {netPlayer.playerId} to RemotePlayers. Rolled back from Players.");
                    return false;
                }
                RemotePlayers[netPlayer.playerId] = (BasisRemotePlayer)netPlayer.Player;
                _snapshotDirty = true;
            }

            return true;
        }

        public static bool RemovePlayer(ushort netId, out BasisNetworkPlayer player)
        {
            player = null;
            if (!BasisNetworkManagement.IsInitialized)
            {
                BasisDebug.LogError("No network Instance existed!");
                return false;
            }

            bool removed = Players.TryRemove(netId, out player);
            RemotePlayerReceivers.TryRemove(netId, out _);
            RemotePlayers.TryRemove(netId, out _);
            _snapshotDirty = true;
            return removed;
        }

        public static bool GetPlayerById(ushort playerId, out BasisNetworkPlayer player) => Players.TryGetValue(playerId, out player);

        // --- Conversions (Avatar/Player) -----------------------------------
        public static bool AvatarToPlayer(BasisAvatar avatar, out IBasisPlayer basisPlayer, out BasisNetworkPlayer networkedPlayer)
        {
            basisPlayer = null;
            networkedPlayer = null;

            if (avatar == null)
            {
                BasisDebug.LogError("Missing Avatar! Make sure you're not sending in a null item");
                return false;
            }

            if (avatar.TryGetLinkedPlayer(out ushort id))
            {
                if (Players.TryGetValue(id, out var output))
                {
                    networkedPlayer = output;
                    basisPlayer = output.Player;
                    return true;
                }
                BasisDebug.LogError("Player was not found. This also includes joining list; something is very wrong!");
                return false;
            }

            BasisDebug.LogError("The player was not assigned at this time!");
            return false;
        }

        public static bool AvatarToPlayer(BasisAvatar avatar, out IBasisPlayer basisPlayer)
        {
            basisPlayer = null;

            if (avatar == null)
            {
                BasisDebug.LogError("Missing Avatar! Make sure you're not sending in a null item");
                return false;
            }

            if (!avatar.TryGetLinkedPlayer(out ushort id))
            {
                BasisDebug.LogError("The player was not assigned at this time!");
                return false;
            }

            if (GetPlayerById(id, out var player))
            {
                basisPlayer = player.Player;
                return true;
            }

            if (JoiningPlayers.ContainsKey(id))
                BasisDebug.LogError("Player was still connecting when this was called!");
            else
                BasisDebug.LogError("Player was not found, including joining list; something is very wrong!");

            return false;
        }

        public static bool PlayerToNetworkedPlayer(IBasisPlayer basisPlayer, out BasisNetworkPlayer networkedPlayer)
        {
            networkedPlayer = null;
            if (basisPlayer == null)
            {
                BasisDebug.LogError("Missing Player! Make sure you're not sending in a null item");
                return false;
            }

            // Match by reference: a player is the same object on both sides. The remote
            // player is a plain managed object, so the old GetEntityId() identity check
            // (a UnityEngine.Object method) no longer applies.
            foreach (var nPlayer in Players.Values)
            {
                if (nPlayer?.Player == null) continue;
                if (ReferenceEquals(nPlayer.Player, basisPlayer))
                {
                    networkedPlayer = nPlayer;
                    return true;
                }
            }
            return false;
        }
        public static bool TryGetPlayerByUUID(string uuid, out BasisNetworkPlayer player)
        {
            player = null;

            if (string.IsNullOrEmpty(uuid))
                return false;

            foreach (var entry in Players)
            {
                var networkPlayer = entry.Value;
                if (networkPlayer?.Player == null)
                    continue;

                if (string.Equals(networkPlayer.Player.UUID, uuid, StringComparison.Ordinal))
                {
                    player = networkPlayer;
                    return true;
                }
            }

            return false;
        }
        public static bool TryGetPlayerByUUID(string uuid, out IBasisPlayer player)
        {
            player = null;

            if (string.IsNullOrEmpty(uuid))
            {
                return false;
            }

            foreach (var entry in Players)
            {
                var basisPlayer = entry.Value?.Player;
                if (basisPlayer == null)
                {
                    continue;
                }

                if (string.Equals(basisPlayer.UUID, uuid, StringComparison.Ordinal))
                {
                    player = basisPlayer;
                    return true;
                }
            }

            return false;
        }
    }
}
