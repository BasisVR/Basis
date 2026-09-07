using System;
using System.Buffers;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        private const int MaxRemovalsPerTick = 8;

        private static void ProcessPendingRemovals()
        {
            int removalsThisTick = 0;
            Span<int> removedIds = stackalloc int[MaxRemovalsPerTick];
            int removedCount = 0;
            while (removalsThisTick < MaxRemovalsPerTick && playersToRemove.TryDequeue(out int id))
            {
                removalsThisTick++;
                _uplinkStates.TryRemove(id, out _);
                // Admin bypass is per-player-session; LiteNetLib recycles ids, so a stale entry
                // would silently grant the next player on this id full-quality broadcast.
                _bypassReductionIds.TryRemove(id, out _);
                if (playerStates.TryRemove(id, out var removedState))
                {
                    removedState.IsActive = false;

                    // Return pooled arrays to ArrayPool
                    if (removedState.AvatarHigh.array != null)
                    {
                        ArrayPool<byte>.Shared.Return(removedState.AvatarHigh.array);
                        removedState.AvatarHigh.array = null;
                    }
                    if (removedState.BundleRawScratch != null)
                    {
                        ArrayPool<byte>.Shared.Return(removedState.BundleRawScratch);
                        removedState.BundleRawScratch = null;
                    }
                    if (removedState.BundleCompressedScratch != null)
                    {
                        ArrayPool<byte>.Shared.Return(removedState.BundleCompressedScratch);
                        removedState.BundleCompressedScratch = null;
                    }

                    // Remove from active players list
                    lock (_activePlayersLock)
                    {
                        for (int i = _activePlayers.Count - 1; i >= 0; i--)
                        {
                            if (_activePlayers[i].id == id)
                            {
                                _activePlayers.RemoveAt(i);
                                _activePlayersDirty = true;
                                Interlocked.Decrement(ref _activePlayerCount);
                                break;
                            }
                        }
                    }

                    removedIds[removedCount++] = id;
                    BNL.Log($"Player {id} removed and cleaned up.");
                }
                else
                {
                    BNL.LogError("Missing Player From Index, Normally Quick Disconnect after Connect " + id);
                }
            }

            if (removedCount == 0)
            {
                return;
            }

            // Clear stale per-player tracking data for the removed IDs across all remaining players.
            // Without this, when a new player reuses an ID, other players LastSeenGeneration
            // would still hold the old (high) generation value, causing the new-data check
            // (senderGen > seenGens[jId]) to fail -- no data would be sent for the new player.
            // Must enumerate the authoritative dictionary, NOT _activePlayersSnapshot: the
            // snapshot is only rebuilt when dirty, so a player added earlier this tick may be
            // absent from it and would keep a stale generation for this id — which is exactly
            // the reuse bug described above. One pass clears the whole tick's batch, so a churn
            // tick costs one dictionary enumeration instead of one per removal.
            foreach (var kvp in playerStates)
            {
                ClearDepartedPeerSlots(kvp.Value, removedIds.Slice(0, removedCount));
            }
        }

        /// <summary>
        /// Forgets everything one receiver knew about a batch of departed senders.
        ///
        /// ⚠️ BOTH halves of the per-peer table. The generation lives in its own array for cache
        /// reasons, and it is the field this whole pass exists for: clearing only the record would leave
        /// the next player given this id facing the previous occupant's high generation, and the send
        /// loop's new-data gate would reject every frame they ever send until their own counter climbed
        /// past it. Nothing else in the system would report anything wrong.
        ///
        /// Split out of the drain so it can be tested against a receiver the test owns, rather than
        /// against the shared player table that the tick thread and every other fixture also write.
        /// </summary>
        internal static void ClearDepartedPeerSlots(PlayerState state, ReadOnlySpan<int> removedIds)
        {
            var tracking = state.PeerTracking;
            if (tracking == null)
            {
                return;
            }
            var lastSeen = state.PeerLastSeenGeneration;
            for (int r = 0; r < removedIds.Length; r++)
            {
                int removedId = removedIds[r];
                if (removedId < tracking.Length)
                {
                    tracking[removedId] = default;
                }
                if (lastSeen != null && removedId < lastSeen.Length)
                {
                    lastSeen[removedId] = 0;
                }
            }
        }

        public static void Shutdown()
        {
            cts.Cancel();
            _tickWake.Set();
        }

        public static void SetBypassReduction(int id, bool enable)
        {
            if (enable) _bypassReductionIds[id] = true;
            else _bypassReductionIds.TryRemove(id, out _);

            if (playerStates.TryGetValue(id, out var state))
                state.BypassReduction = enable;
        }

        public static void RemovePlayer(int id)
        {
            playersToRemove.Enqueue(id);
        }
    }
}
