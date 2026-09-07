using Basis.Network.Core;
using Basis.Network.Core.Compression;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using static SerializableBasis;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        private static void PreSerializeAll(PlayerState state)
        {
            ushort playerId = state.SyncMessage.playerIdMessage.playerID;

            // Read accumulated quality bits. Bits are sticky — set by MarkQualityUsed in the
            // send loop and never reset. With tick slicing (32 slices), each slice's receivers
            // contribute their needed quality bits over successive ticks. NOT resetting prevents
            // oscillation where each tick only has bits from 1/32 of receivers.
            int mask = Volatile.Read(ref state.UsedQualities);
            if (mask == 0) mask = 0xF; // new player or no sends yet: serialize all

            LocalAvatarSyncMessage msg;
            for (int qi = 0; qi < 4; qi++)
            {
                if ((mask & (1 << qi)) != 0)
                {
                    msg = qi switch
                    {
                        0 => state.AvatarVeryLow,
                        1 => state.AvatarLow,
                        2 => state.AvatarMedium,
                        _ => state.AvatarHigh,
                    };
                    PreSerializeKeyframe(state, qi, msg, playerId);
                    BSRProfiler.IncrementPreSerializations();
                }
                else
                {
                    // Mark as not available  send loop will skip and request it for next tick.
                    state.SerializedKeyframeLength[qi] = 0;
                    BSRProfiler.IncrementPreSerializationsSkipped();
                }
            }
        }

        internal static void PreSerializeKeyframe(PlayerState state, int qi, LocalAvatarSyncMessage msg, ushort playerId)
        {
            if (msg.array == null)
            {
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            var quality = (BitQuality)msg.DataQualityLevel;

            // Guard: the message's quality must match the quality slot index.
            // AvatarHigh may contain non-High quality data if the client sent a
            // lower quality. Without this check, the payload would be sent on the
            // wrong channel, causing size mismatches on the receiver (e.g. "Need 169, have 99").
            if ((int)quality != qi)
            {
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            int expectedPayload = BasisAvatarBitPacking.ConvertToSize(quality);

            // Skip if the array is undersized (e.g. client sent wrong quality level)
            if (msg.array.Length < expectedPayload)
            {
                BNL.LogError($"[PreSerializeKeyframe] Array undersized for quality {quality}: got {msg.array.Length}, need {expectedPayload}. Skipping.");
                state.SerializedKeyframeLength[qi] = 0;
                return;
            }

            // Byte-ID:   [PlayerID:1][interval:1][sequence:1][array:N][additional...]
            // Ushort-ID: [PlayerID:2][interval:1][sequence:1][array:N][additional...]
            // Quality and additional-data presence are derived from the channel number.
            bool hasAdditional = state.HasAdditionalData
                && msg.AdditionalAvatarDatas != null
                && msg.AdditionalAvatarDatas.Length > 0
                && msg.AdditionalAvatarDatas.Length <= 255;
            state.SerializedHasAdditional[qi] = hasAdditional;

            int additionalSize = 0;
            if (hasAdditional)
            {
                additionalSize = 1 + 1; // AdditionalSize + LinkedAvatarIndex
                for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                {
                    additionalSize += 1 + 1 + (msg.AdditionalAvatarDatas[i].array?.Length ?? 0); // PayloadSize + messageIndex + data
                }
            }

            int idSize = state.SmallId ? 1 : 2;
            int totalSize = idSize + 1 + 1 + expectedPayload + additionalSize;

            if (state.SerializedKeyframe[qi] == null || state.SerializedKeyframe[qi].Length < totalSize)
            {
                state.SerializedKeyframe[qi] = new byte[totalSize];
            }

            // Write directly to SerializedKeyframe — avoids the intermediate NetDataWriter
            // buffer and the final BlockCopy (~40MB/sec saved at 200K+ pre-serializations/5s).
            byte[] dst = state.SerializedKeyframe[qi];
            int offset = 0;

            if (state.SmallId)
            {
                dst[offset++] = (byte)playerId;
            }
            else
            {
                dst[offset++] = (byte)(playerId & 0xFF);
                dst[offset++] = (byte)((playerId >> 8) & 0xFF);
            }

            dst[offset++] = 0; // interval placeholder (patched per-receiver in send loop)
            dst[offset++] = state.OutboundSequence;

            Buffer.BlockCopy(msg.array, 0, dst, offset, expectedPayload);
            offset += expectedPayload;

            if (hasAdditional)
            {
                dst[offset++] = (byte)msg.AdditionalAvatarDatas.Length;
                dst[offset++] = msg.LinkedAvatarIndex;
                for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                {
                    var ad = msg.AdditionalAvatarDatas[i];
                    // Every entry writes the full [size:1][messageIndex:1] header (size 0 for
                    // null/oversized payloads) — a bare size byte would desync the entries after
                    // it. Must match AdditionalAvatarData.Serialize/Deserialize exactly.
                    if (ad.array == null || ad.array.Length > 255)
                    {
                        dst[offset++] = 0;
                        dst[offset++] = ad.messageIndex;
                    }
                    else
                    {
                        byte payloadSize = (byte)ad.array.Length;
                        dst[offset++] = payloadSize;
                        dst[offset++] = ad.messageIndex;
                        if (payloadSize > 0)
                        {
                            Buffer.BlockCopy(ad.array, 0, dst, offset, payloadSize);
                            offset += payloadSize;
                        }
                    }
                }
            }

            state.SerializedKeyframeLength[qi] = offset;
        }

        private static void PreSerializeFrame(PlayerState state, long publishGen, bool forceKeyframe)
        {
            if (!EnableAvatarDeltaCompression)
            {
                PreSerializeAll(state);
                state.CurrentIsKeyframe = true;
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long keyframeIntervalTicks = (long)(EffectiveKeyframeIntervalMs(state.KeyframeStretchShift) * MsToTick);

            bool isKeyframe = forceKeyframe
                || state.BypassReduction
                || state.KeyframePayload[3] == null
                || state.KeyframePayloadLength[3] == 0
                || (now - state.LastKeyframeTimeTicks) >= keyframeIntervalTicks;

            // Promotion: if the High delta isn't actually smaller than a High keyframe (fully-moving
            // avatar), just send a keyframe — never pay delta overhead to be larger than a keyframe.
            if (!isKeyframe)
            {
                LocalAvatarSyncMessage highMsg = state.AvatarHigh;
                int highPayload = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
                if (highMsg.array == null || (int)highMsg.DataQualityLevel != 3 || highMsg.array.Length < highPayload)
                {
                    isKeyframe = true;
                }
                else
                {
                    int probeCap = BasisAvatarDeltaCompression.MaxDeltaSize(BitQuality.High);
                    if (state.DeltaProbeScratch == null || state.DeltaProbeScratch.Length < probeCap)
                        state.DeltaProbeScratch = new byte[probeCap];
                    int dl = BasisAvatarDeltaCompression.BuildDelta(state.KeyframePayload[3], highMsg.array, BitQuality.High, state.DeltaProbeScratch, 0);
                    if (dl < 0 || dl >= highPayload)
                    {
                        isKeyframe = true;
                        state.KeyframeStretchShift = 0;
                        state.SmallDeltaStreak = 0;
                    }
                    else
                    {
                        UpdateKeyframeStretch(state, dl);
                    }
                }
            }

            ushort playerId = state.SyncMessage.playerIdMessage.playerID;

            if (isKeyframe)
            {
                state.KeyframeGen = publishGen;
                state.KeyframeSequence = state.OutboundSequence;
                state.LastKeyframeTimeTicks = now;
                state.CurrentIsKeyframe = true;

                // Serialize all four quality keyframes (that have valid arrays) and snapshot their
                // payloads as the delta baseline, so a receiver at ANY quality can rebaseline off the
                // last keyframe without waiting for the next keyframe interval.
                for (int qi = 0; qi < 4; qi++)
                {
                    LocalAvatarSyncMessage msg = QualityMsg(state, qi);
                    if (msg.array == null || (int)msg.DataQualityLevel != qi)
                    {
                        state.SerializedKeyframeLength[qi] = 0;
                        state.KeyframePayloadLength[qi] = 0;
                        state.SerializedDeltaLength[qi] = 0;
                        BSRProfiler.IncrementPreSerializationsSkipped();
                        continue;
                    }
                    int payload = BasisAvatarBitPacking.ConvertToSize((BitQuality)qi);
                    if (msg.array.Length < payload)
                    {
                        state.SerializedKeyframeLength[qi] = 0;
                        state.KeyframePayloadLength[qi] = 0;
                        state.SerializedDeltaLength[qi] = 0;
                        continue;
                    }
                    if (state.KeyframePayload[qi] == null || state.KeyframePayload[qi].Length < payload)
                        state.KeyframePayload[qi] = new byte[payload];
                    Buffer.BlockCopy(msg.array, 0, state.KeyframePayload[qi], 0, payload);
                    state.KeyframePayloadLength[qi] = payload;

                    PreSerializeKeyframe(state, qi, msg, playerId);
                    state.SerializedDeltaLength[qi] = 0; // no delta on a keyframe generation
                    BSRProfiler.IncrementPreSerializations();
                }
            }
            else
            {
                state.CurrentIsKeyframe = false;
                // Build deltas only for qualities that had receivers (sticky UsedQualities). Keyframe
                // buffers are left intact from the last keyframe tick so a lagging receiver can rebaseline.
                int mask = Volatile.Read(ref state.UsedQualities);
                if (mask == 0) mask = 0xF;
                for (int qi = 0; qi < 4; qi++)
                {
                    if ((mask & (1 << qi)) == 0) { state.SerializedDeltaLength[qi] = 0; continue; }
                    PreSerializeDelta(state, qi, QualityMsg(state, qi), playerId);
                }
            }
        }

        internal static int EffectiveKeyframeIntervalMs(int stretchShift)
        {
            int baseMs = AvatarDeltaKeyframeIntervalMs;
            int maxMs = AvatarDeltaKeyframeMaxIntervalMs;
            if (maxMs <= baseMs || stretchShift <= 0) return baseMs;
            long stretched = (long)baseMs << (stretchShift > 8 ? 8 : stretchShift);
            return stretched >= maxMs ? maxMs : (int)stretched;
        }

        internal static void UpdateKeyframeStretch(PlayerState state, int highDeltaLength)
        {
            if (highDeltaLength > SmallHighDeltaBytes)
            {
                state.KeyframeStretchShift = 0;
                state.SmallDeltaStreak = 0;
                return;
            }
            if (EffectiveKeyframeIntervalMs(state.KeyframeStretchShift + 1) == EffectiveKeyframeIntervalMs(state.KeyframeStretchShift)) return;
            if (++state.SmallDeltaStreak >= SmallDeltaStreakToStretch)
            {
                state.SmallDeltaStreak = 0;
                state.KeyframeStretchShift++;
            }
        }

        public static void RequestKeyframe(int senderId, int receiverId)
        {
            _pendingKeyframeRequests.Enqueue((senderId, receiverId));
            _tickWake.Set();
        }

        private static void ProcessPendingKeyframeRequests()
        {
            while (_pendingKeyframeRequests.TryDequeue(out var pair))
            {
                // PeerTracking lives on the RECEIVER, indexed by sender id.
                if (!playerStates.TryGetValue(pair.receiverId, out PlayerState receiver)) continue;
                var tracking = receiver.PeerTracking;
                if (tracking == null || pair.senderId < 0 || pair.senderId >= tracking.Length) continue;
                var lastSeen = receiver.PeerLastSeenGeneration;
                if (lastSeen == null || lastSeen.Length < tracking.Length)
                {
                    tracking = GrowPeerTracking(receiver, tracking.Length - 1);
                    lastSeen = receiver.PeerLastSeenGeneration;
                }
                ref PeerTrackingData t = ref tracking[pair.senderId];
                t.BaselineKeyframeGen = PeerTrackingData.NoBaseline;
                // Reopen the new-data and interval gates: a fully idle sender publishes no new
                // generation, so without this the requested keyframe would wait for motion.
                lastSeen[pair.senderId] = 0;
                // Backdated rather than zeroed. LastSentTime is now the low 32 bits of the tick counter
                // and the gate is a wrapping subtraction, so a literal zero would read as "sent when the
                // counter last wrapped" - which, depending on where in the wrap the server happens to
                // be, can look recent. Backdating past the longest interval any pair can be given makes
                // the gate open immediately whatever the counter reads.
                t.LastSentTime = unchecked((uint)Stopwatch.GetTimestamp() - ForceSendBackdateTicks);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LocalAvatarSyncMessage QualityMsg(PlayerState state, int qi) => qi switch
        {
            0 => state.AvatarVeryLow,
            1 => state.AvatarLow,
            2 => state.AvatarMedium,
            _ => state.AvatarHigh,
        };

        internal static void PreSerializeDelta(PlayerState state, int qi, LocalAvatarSyncMessage msg, ushort playerId)
        {
            var q = (BitQuality)qi;
            if (msg.array == null || (int)msg.DataQualityLevel != qi || state.KeyframePayload[qi] == null)
            {
                state.SerializedDeltaLength[qi] = 0;
                return;
            }
            int payload = BasisAvatarBitPacking.ConvertToSize(q);
            if (msg.array.Length < payload || state.KeyframePayloadLength[qi] < payload)
            {
                state.SerializedDeltaLength[qi] = 0;
                return;
            }

            int additionalSize = AdditionalDataSize(state, msg, out bool hasAdditional);
            int idSize = state.SmallId ? 1 : 2;
            int headerBytes = 1 + idSize + 1 + 1 + 1; // header + playerId + interval + sequence + baseSeq
            int cap = headerBytes + BasisAvatarDeltaCompression.MaxDeltaSize(q) + additionalSize;

            if (state.SerializedDelta[qi] == null || state.SerializedDelta[qi].Length < cap)
                state.SerializedDelta[qi] = new byte[cap];

            byte[] dst = state.SerializedDelta[qi];
            int o = 0;
            dst[o++] = BasisNetworkCommons.BuildDeltaHeader(qi, hasAdditional, !state.SmallId);
            if (state.SmallId)
            {
                dst[o++] = (byte)playerId;
            }
            else
            {
                dst[o++] = (byte)(playerId & 0xFF);
                dst[o++] = (byte)((playerId >> 8) & 0xFF);
            }
            dst[o++] = 0; // interval placeholder (patched per-receiver in the send loop)
            dst[o++] = state.OutboundSequence;
            dst[o++] = state.KeyframeSequence; // baseSeq — the keyframe this delta reconstructs against

            int bodyLen = BasisAvatarDeltaCompression.BuildDelta(state.KeyframePayload[qi], msg.array, q, dst, o);
            if (bodyLen < 0)
            {
                state.SerializedDeltaLength[qi] = 0;
                return;
            }
            o += bodyLen;

            if (hasAdditional) o = WriteAdditionalData(dst, o, msg);

            state.SerializedDeltaLength[qi] = o;
            BSRProfiler.IncrementPreSerializations();
        }

        private static int AdditionalDataSize(PlayerState state, LocalAvatarSyncMessage msg, out bool hasAdditional)
        {
            hasAdditional = state.HasAdditionalData
                && msg.AdditionalAvatarDatas != null
                && msg.AdditionalAvatarDatas.Length > 0
                && msg.AdditionalAvatarDatas.Length <= 255;
            if (!hasAdditional) return 0;
            int sz = 1 + 1; // AdditionalSize + LinkedAvatarIndex
            for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
                sz += 1 + 1 + (msg.AdditionalAvatarDatas[i].array?.Length ?? 0);
            return sz;
        }

        private static int WriteAdditionalData(byte[] dst, int offset, LocalAvatarSyncMessage msg)
        {
            dst[offset++] = (byte)msg.AdditionalAvatarDatas.Length;
            dst[offset++] = msg.LinkedAvatarIndex;
            for (int i = 0; i < msg.AdditionalAvatarDatas.Length; i++)
            {
                var ad = msg.AdditionalAvatarDatas[i];
                // Full [size:1][messageIndex:1] header per entry, size 0 for null/oversized —
                // must match AdditionalAvatarData.Serialize/Deserialize exactly (see PreSerializeKeyframe).
                if (ad.array == null || ad.array.Length > 255)
                {
                    dst[offset++] = 0;
                    dst[offset++] = ad.messageIndex;
                }
                else
                {
                    byte payloadSize = (byte)ad.array.Length;
                    dst[offset++] = payloadSize;
                    dst[offset++] = ad.messageIndex;
                    if (payloadSize > 0)
                    {
                        Buffer.BlockCopy(ad.array, 0, dst, offset, payloadSize);
                        offset += payloadSize;
                    }
                }
            }
            return offset;
        }
    }
}
