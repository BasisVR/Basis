using Basis.Network.Core;
using Basis.Network.Core.Compression;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using static SerializableBasis;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        public static void HandleAvatarMovement(NetPacketReader reader, NetPeer fromPeer, byte channel)
        {
            // Read the application-level sequence byte prepended by the client
            if (!reader.TryGetByte(out byte sequence))
            {
                reader.Recycle();
                return;
            }

            // Quality and additional-data presence are derived from the channel.
            byte quality = BasisNetworkCommons.GetQualityFromChannel(channel);
            bool hasAdditional = BasisNetworkCommons.ChannelHasAdditionalData(channel);

            // Rent BEFORE deserialize so the pooled byte[] is reused (avoids alloc per frame per player).
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            message.AvatarMessage.Deserialize(reader, quality, hasAdditional);
            reader.Recycle();

            if (message.AvatarMessage.array == null)
            {
                BNL.LogError($"[HandleAvatarMovement] Deserialized avatar message has null array from peer {fromPeer.Id}");
                QueuedMessagePool.Return(message);
                return;
            }

            // Every full High frame doubles as the sender's uplink delta baseline (v42).
            if (quality == 3)
            {
                UplinkCaptureBaseline(fromPeer.Id, message.AvatarMessage.array, sequence);
            }

            // Overwrite any pending message for this peer.
            // Uses indexer instead of AddOrUpdate to avoid closure allocation on every call.
            // Do NOT return prev to the pool — the drain phase may have captured it.
            // The orphaned prev (if any) is collected by the GC; this only occurs when two
            // messages for the same peer arrive within the same tick — negligible cost.
            currentMessages[fromPeer.Id] = message;

            // Wake the loop only while it is parked (empty server). Once a player is
            // registered the loop is running, so this read short-circuits with no syscall.
            if (Volatile.Read(ref _activePlayerCount) == 0) _tickWake.Set();
        }

        // ── Uplink avatar deltas (v42) ──
        // Per-sender baseline of the last full keyframe the client uploaded on the High channels.
        // Owned by the network receive thread (capture + delta apply both run there); removed on
        // the tick thread via ProcessPendingRemovals (ConcurrentDictionary makes that safe).
        private sealed class UplinkDeltaState
        {
            public byte[] Baseline;
            public byte BaselineSeq;
            public bool Has;
            public long LastNackTicks;
        }
        private static readonly ConcurrentDictionary<int, UplinkDeltaState> _uplinkStates = new();
        private static readonly long NackMinIntervalTicks = Stopwatch.Frequency; // 1/s per sender

        private static void UplinkCaptureBaseline(int peerId, byte[] payload, byte sequence)
        {
            int size = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);
            if (payload == null || payload.Length < size) return;
            UplinkDeltaState st = _uplinkStates.GetOrAdd(peerId, static _ => new UplinkDeltaState());
            if (st.Baseline == null || st.Baseline.Length < size) st.Baseline = new byte[size];
            Buffer.BlockCopy(payload, 0, st.Baseline, 0, size);
            st.BaselineSeq = sequence;
            st.Has = true;
        }

        private static void NackUplink(NetPeer peer, UplinkDeltaState st)
        {
            long now = Stopwatch.GetTimestamp();
            if (st != null)
            {
                if (now - st.LastNackTicks < NackMinIntervalTicks) return;
                st.LastNackTicks = now;
            }
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                writer.Put(BasisNetworkCommons.DeltaControlUplinkKeyframeRequest);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.DeltaAvatarChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        public static void HandleDeltaChannelInbound(NetPacketReader reader, NetPeer fromPeer)
        {
            if (!reader.TryGetByte(out byte header))
            {
                reader.Recycle();
                return;
            }

            if (BasisNetworkCommons.IsDeltaControlHeader(header))
            {
                if (header == BasisNetworkCommons.DeltaControlKeyframeRequest && reader.TryGetUShort(out ushort senderId))
                {
                    RequestKeyframe(senderId, fromPeer.Id);
                }
                reader.Recycle();
                return;
            }

            if (BasisNetworkCommons.DeltaHeaderQuality(header) != 3)
            {
                // Clients only ever upload High.
                reader.Recycle();
                return;
            }
            bool hasAdditional = BasisNetworkCommons.DeltaHeaderHasAdditionalData(header);
            if (!reader.TryGetByte(out byte sequence))
            {
                reader.Recycle();
                return;
            }
            if (!reader.TryGetByte(out byte baseSeq))
            {
                reader.Recycle();
                return;
            }

            _uplinkStates.TryGetValue(fromPeer.Id, out UplinkDeltaState st);
            if (st == null || !st.Has || st.BaselineSeq != baseSeq)
            {
                // Missing/stale baseline (lost keyframe or reorder) — ask for a fresh keyframe.
                NackUplink(fromPeer, st);
                reader.Recycle();
                return;
            }

            int bodyLen = BasisAvatarDeltaCompression.DeltaBodyLength(reader.RawData, reader.Position, reader.AvailableBytes, BitQuality.High);
            if (bodyLen < 0 || bodyLen > reader.AvailableBytes)
            {
                reader.Recycle();
                return;
            }

            int payloadSize = BasisAvatarDeltaCompression.PayloadSize(BitQuality.High);
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            if (message.AvatarMessage.array == null || message.AvatarMessage.array.Length != payloadSize)
            {
                message.AvatarMessage.array = new byte[payloadSize];
            }

            bool ok = BasisAvatarDeltaCompression.TryApplyDelta(st.Baseline, reader.RawData, reader.Position, bodyLen, BitQuality.High, message.AvatarMessage.array);
            if (!ok)
            {
                QueuedMessagePool.Return(message);
                reader.Recycle();
                return;
            }
            reader.SkipBytes(bodyLen);

            message.AvatarMessage.DataQualityLevel = 3;
            message.AvatarMessage.AdditionalAvatarDataSize = 0;
            message.AvatarMessage.AdditionalAvatarDatas = null;
            if (hasAdditional)
            {
                message.AvatarMessage.DeserializeAdditionalData(reader);
            }
            reader.Recycle();

            // Same ingest as HandleAvatarMovement — ProcessMessage deep-copies and repacks.
            currentMessages[fromPeer.Id] = message;
            if (Volatile.Read(ref _activePlayerCount) == 0) _tickWake.Set();
        }

        public static void AddMessage(NetPeer fromPeer, LocalAvatarSyncMessage localMessage, byte sequence)
        {
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.Sequence = sequence;
            message.AvatarMessage = localMessage;

            // Same as HandleAvatarMovement — indexer avoids closure allocation.
            currentMessages[fromPeer.Id] = message;

            if (Volatile.Read(ref _activePlayerCount) == 0) _tickWake.Set();
        }
    }
}
