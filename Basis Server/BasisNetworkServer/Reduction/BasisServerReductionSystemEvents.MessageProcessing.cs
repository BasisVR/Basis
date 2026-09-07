using Basis.Network.Core;
using Basis.Network.Core.Compression;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using static SerializableBasis;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        private static double KeyframePhaseFraction(int id)
        {
            uint scrambled = unchecked((uint)id * 2654435761u);
            return scrambled / (double)uint.MaxValue;
        }

        internal static void PropagateAdditionalData(
            in LocalAvatarSyncMessage high,
            ref LocalAvatarSyncMessage medium,
            ref LocalAvatarSyncMessage low,
            ref LocalAvatarSyncMessage veryLow)
        {
            medium.AdditionalAvatarDatas = high.AdditionalAvatarDatas;
            medium.AdditionalAvatarDataSize = high.AdditionalAvatarDataSize;
            medium.LinkedAvatarIndex = high.LinkedAvatarIndex;

            bool strip = StripAdditionalDataAtLowQuality;
            low.AdditionalAvatarDatas = strip ? null : high.AdditionalAvatarDatas;
            low.AdditionalAvatarDataSize = strip ? (byte)0 : high.AdditionalAvatarDataSize;
            low.LinkedAvatarIndex = high.LinkedAvatarIndex;

            veryLow.AdditionalAvatarDatas = strip ? null : high.AdditionalAvatarDatas;
            veryLow.AdditionalAvatarDataSize = strip ? (byte)0 : high.AdditionalAvatarDataSize;
            veryLow.LinkedAvatarIndex = high.LinkedAvatarIndex;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyPositionToLowerQualities(
            byte[] highArray,
            ref LocalAvatarSyncMessage medium,
            ref LocalAvatarSyncMessage low,
            ref LocalAvatarSyncMessage veryLow)
        {
            int posBytes = BasisAvatarBitPacking.WritePosition;
            if (medium.array != null) Buffer.BlockCopy(highArray, 0, medium.array, 0, posBytes);
            if (low.array != null) Buffer.BlockCopy(highArray, 0, low.array, 0, posBytes);
            if (veryLow.array != null) Buffer.BlockCopy(highArray, 0, veryLow.array, 0, posBytes);
        }

        private static void ProcessMessage(QueuedMessage message)
        {
            if (message.FromPeer == null)
            {
                QueuedMessagePool.Return(message);
                return;
            }

            int id = message.FromPeer.Id;
            byte inboundSeq = message.Sequence;

            var poolMsg = message.AvatarMessage;

            if (poolMsg.array == null)
            {
                BNL.LogError($"[ProcessMessage] Avatar array is null for peer {id}");
                QueuedMessagePool.Return(message);
                return;
            }

            if (BasisNetworkServer.Security.BasisGlobalLockManager.AdditionalAvatarDataLock)
            {
                poolMsg.AdditionalAvatarDatas = null;
                poolMsg.AdditionalAvatarDataSize = 0;
            }

            var incomingQuality = (BitQuality)poolMsg.DataQualityLevel;
            bool isHighQuality = incomingQuality == BitQuality.High;

            if (!BasisAvatarBitPacking.IsValidQuality(incomingQuality))
            {
                QueuedMessagePool.Return(message);
                return;
            }

            int expectedPayloadSize = BasisAvatarBitPacking.ConvertToSize(incomingQuality);
            if (poolMsg.array.Length < expectedPayloadSize)
            {
                QueuedMessagePool.Return(message);
                return;
            }

            var pos = BasisNetworkCompressionExtensions.ReadPosition(ref poolMsg.array);

            // A message can outlive its sender: removals drain at MaxRemovalsPerTick, and a stale
            // frame drained after its player's removal used to recreate PlayerState around the dead
            // NetPeer — pinning the peer (channels, merge buffer) plus a fresh 64 KB tracking array
            // until the id happened to be reused. Only ever create state for the peer that
            // currently owns the id.
            if (!playerStates.TryGetValue(id, out _) &&
                (!NetworkServer.AuthenticatedPeers.TryGetValue(id, out NetPeer livePeer) || !Equals(livePeer, message.FromPeer)))
            {
                QueuedMessagePool.Return(message);
                return;
            }

            // Deep-copy the avatar payload so state.AvatarHigh owns its own buffer.
            // Without this copy, QueuedMessagePool.Return() preserves the byte[] and
            // re-rents it for other peers — silently overwriting state.AvatarHigh.array.
            // Uses ArrayPool to avoid per-message heap allocation (~208 bytes * 11K/sec).
            byte[] rentedArray = ArrayPool<byte>.Shared.Rent(expectedPayloadSize);
            Buffer.BlockCopy(poolMsg.array, 0, rentedArray, 0, expectedPayloadSize);
            var high = new LocalAvatarSyncMessage
            {
                DataQualityLevel = poolMsg.DataQualityLevel,
                AdditionalAvatarDatas = poolMsg.AdditionalAvatarDatas,
                AdditionalAvatarDataSize = poolMsg.AdditionalAvatarDataSize,
                LinkedAvatarIndex = poolMsg.LinkedAvatarIndex,
                array = rentedArray,
            };

            if (!playerStates.TryGetValue(id, out var state))
            {
                state = new PlayerState
                {
                    Peer = message.FromPeer,
                    IsActive = true,
                    Position = pos,
                    SyncMessage = new ServerSideSyncPlayerMessage
                    {
                        playerIdMessage = new PlayerIdMessage { playerID = (ushort)id },
                        avatarSerialization = high
                    },
                    AvatarHigh = high,
                    HighArrayActualSize = expectedPayloadSize,
                    PeerTracking = new PeerTrackingData[InitialPlayerArrayCapacity],
                    PeerLastSeenGeneration = new uint[InitialPlayerArrayCapacity],
                    // Stagger the periodic keyframe phase per player. A keyframe tick serialises all
                    // four qualities instead of just the used ones (~4x the work) and puts a much
                    // larger payload on the wire. Seeding every player from their first frame makes a
                    // mass join produce a synchronized herd: a large slice of the instance emits
                    // keyframes on the same tick, every interval, forever. Offsetting the phase by a
                    // hash of the player id spreads that cost flat, without changing the per-player
                    // cadence. Hashed rather than random so it is deterministic and allocation-free
                    // (Random.Shared does not exist on the netstandard2.1 target this also builds for).
                    LastKeyframeTimeTicks = Stopwatch.GetTimestamp() - (long)(KeyframePhaseFraction(id)
                        * AvatarDeltaKeyframeIntervalMs * MsToTick),
                    DataGeneration = 1,
                    LastInboundSequence = inboundSeq,
                    HasReceivedFirst = true,
                    OutboundSequence = 0,
                    SmallId = id <= byte.MaxValue,
                    BypassReduction = _bypassReductionIds.ContainsKey(id),
                };

                if (isHighQuality)
                {
                    try
                    {
                        AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"[ProcessMessage] Repack failed: {ex}");
                        // Don't alias high into lower slots — that sends High-packed muscle
                        // data on lower-quality channels, causing bit-width mismatches.
                        // Null the arrays so PreSerializeAll skips them; the repacker's
                        // EnsureBuffer and the position-only fast path both handle null safely.
                        state.AvatarMedium.array = null;
                        state.AvatarLow.array = null;
                        state.AvatarVeryLow.array = null;
                    }
                }
                else
                {
                    // Non-High quality: can't repack downward. Null lower slots to
                    // avoid sending mismatched quality data on wrong channels.
                    state.AvatarMedium.array = null;
                    state.AvatarLow.array = null;
                    state.AvatarVeryLow.array = null;
                }

                // Propagate additional avatar data (e.g. blendshapes) to quality variants.
                // BuildAllLowerFromHighInto only handles muscle/position payload;
                // additional data must be copied separately.
                PropagateAdditionalData(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                state.HasAdditionalData = high.AdditionalAvatarDatas != null && high.AdditionalAvatarDatas.Length > 0;

                // First frame: always a keyframe (generation 1).
                PreSerializeFrame(state, 1, forceKeyframe: true);

                playerStates[id] = state;

                // Add to active players list
                lock (_activePlayersLock)
                {
                    _activePlayers.Add((id, state));
                    _activePlayersDirty = true;
                    Interlocked.Increment(ref _activePlayerCount);
                }
            }
            else
            {
                // Peer-slot reuse: LiteNetLib recycles NetPeer ids after disconnect.
                // If the incoming peer is a different instance, the stored Peer is the
                // old disconnected one — sends to it silently no-op, so the new player
                // would never receive avatar data. Refresh the Peer ref and treat the
                // next frame as the first frame so the sequence-delta check doesn't
                // drop it against the previous player's last sequence.
                if (!Equals(state.Peer, message.FromPeer))
                {
                    state.Peer = message.FromPeer;
                    state.HasReceivedFirst = false;
                    state.SmallId = id <= byte.MaxValue;
                    state.BypassReduction = _bypassReductionIds.ContainsKey(id);
                }

                // Drop stale inbound packets (unreliable can deliver out of order)
                if (state.HasReceivedFirst)
                {
                    byte delta = unchecked((byte)(inboundSeq - state.LastInboundSequence));
                    if (delta == 0 || delta >= 128)
                    {
                        // Duplicate or stale — discard. Return the just-rented array.
                        ArrayPool<byte>.Shared.Return(rentedArray);
                        QueuedMessagePool.Return(message);
                        return;
                    }
                }
                state.LastInboundSequence = inboundSeq;
                state.HasReceivedFirst = true;

                if (!state.IsActive)
                {
                    state.IsActive = true;
                }

                state.Position = pos;

                // Increment outbound sequence for this sender's new update
                unchecked { state.OutboundSequence++; }

                byte[] prevArray = state.AvatarHigh.array;
                int prevActualSize = state.HighArrayActualSize;
                state.AvatarHigh = high;
                state.HighArrayActualSize = expectedPayloadSize;

                if (isHighQuality)
                {
                    // Check if muscles+tail changed (skip expensive bit repacking if only position moved).
                    // Uses HighArrayActualSize instead of .Length since ArrayPool may return larger arrays.
                    int muscleAndTailBytes = HighMuscleAndTailBytes;
                    bool musclesOrTailChanged = prevArray == null
                        || ReferenceEquals(prevArray, high.array)
                        || prevActualSize != expectedPayloadSize
                        || !high.array.AsSpan(WritePosition, muscleAndTailBytes)
                            .SequenceEqual(prevArray.AsSpan(WritePosition, muscleAndTailBytes));

                    // Force a full repack when any lower quality array is null (e.g. after a
                    // previous repack failure).  Without this, the position-only fast path would
                    // skip the null arrays indefinitely and far receivers would never see the player.
                    bool needsRecovery = state.AvatarMedium.array == null
                        || state.AvatarLow.array == null
                        || state.AvatarVeryLow.array == null;

                    if (musclesOrTailChanged || needsRecovery)
                    {
                        try
                        {
                            AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                        }
                        catch (Exception ex)
                        {
                            BNL.LogError($"[ProcessMessage] Repack failed: {ex}");
                            state.AvatarMedium.array = null;
                            state.AvatarLow.array = null;
                            state.AvatarVeryLow.array = null;
                        }
                    }
                    else
                    {
                        // Position-only fast path: carry position to all lower qualities without
                        // re-packing bones (float32 in High, int24-mm in the lower tiers).
                        CopyPositionToLowerQualities(high.array, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                    }
                }
                else
                {
                    // Non-High quality: can't repack downward safely.
                    state.AvatarMedium.array = null;
                    state.AvatarLow.array = null;
                    state.AvatarVeryLow.array = null;
                }

                // Propagate additional avatar data to quality variants
                PropagateAdditionalData(high, ref state.AvatarMedium, ref state.AvatarLow, ref state.AvatarVeryLow);
                state.HasAdditionalData = high.AdditionalAvatarDatas != null && high.AdditionalAvatarDatas.Length > 0;

                // Keep SyncMessage in sync (shell)
                state.SyncMessage.avatarSerialization = high;

                // publishGen = the DataGeneration value this frame becomes after the increment below.
                PreSerializeFrame(state, state.DataGeneration + 1, forceKeyframe: false);

                // Return the previous tick's array to the pool now that muscle comparison is done.
                if (prevArray != null)
                {
                    ArrayPool<byte>.Shared.Return(prevArray);
                }

                // Single atomic increment replaces O(N) CAS bit-setting across all other players.
                // Receivers detect new data by comparing this generation against their LastSeenGeneration.
                Interlocked.Increment(ref state.DataGeneration);
            }

            QueuedMessagePool.Return(message);
        }
    }
}
