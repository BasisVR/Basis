using Basis.Network.Core;
using Basis.Network.Core.Compression;
using K4os.Compression.LZ4;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        private static int EmitGreedyBundles(PlayerState stateI, NetPeer peer, PendingAvatarSend[] pending, int count, ref long bundleCount, ref long bundleBytes)
        {
            int budget = peer.Mtu - BundleMtuHeadroom - BundleHeaderSize;
            if (budget <= 0) return 0;

            // Hoisted per flush — enablement, dictionary presence and shed tier are all
            // tick-scoped, so re-testing them per chunk would buy nothing.
            bool zstdPath = ZstdPathAvailable();

            float fillMargin = stateI.BundleFillMargin;
            if (fillMargin < MinBundleFillMargin || fillMargin > MaxBundleFillMargin) fillMargin = MaxBundleFillMargin;

            int cursor = 0;
            // AvatarBundleMinMessages gates *starting* to bundle (caller already checked it for
            // the first chunk). Inside the loop, individual chunks are sized by what fits in MTU;
            // a chunk of only 1-2 large messages is still worthwhile if rawLen ≥ AvatarBundleMinBytes
            // so the deflate header pays back. The outer condition just keeps the receiver tail of
            // < min uncompressed (since uncompressed sends merge fine for tiny remainders).
            while (count - cursor >= AvatarBundleMinMessages)
            {
                // Codec has to be picked BEFORE the chunk is sized, because the size prediction
                // needs the ratio EMA belonging to the codec that will actually run. Predict it
                // from the run starting at cursor: pending is channel-sorted, so that run is
                // what the chunk mostly consists of.
                bool useZstd = zstdPath
                               && (AvatarBundleZstdDeltaBundles
                                   || pending[cursor].Channel != BasisNetworkCommons.DeltaAvatarChannel);

                // Initial ratio guess. Measured at 1000 players the LZ4 path sits around 0.87, not
                // the 0.6 this used to assume — quantized bone rotations are close to
                // incompressible. Guessing too optimistic makes the very first chunk overshoot MTU
                // and burn a whole extra deflate on the retry path, which was costing ~7-8% of all
                // bundles. Stays in [0.05, 0.95] so prediction never picks zero or full-budget chunks.
                float ratio = useZstd ? stateI.LastBundleZstdRatio : stateI.LastBundleRatio;
                if (ratio < 0.05f || ratio > 0.95f) ratio = useZstd ? InitialBundleZstdRatioGuess : InitialBundleRatioGuess;

                // Predict raw chunk size that would compress to ~budget * fillMargin (safety margin
                // so we don't waste a retry on near-MTU overshoots). Then walk pending
                // accumulating sizes until we hit that target or run out of messages.
                int targetRaw = (int)((budget * fillMargin) / ratio);
                int chunkEnd = PickChunkEnd(pending, cursor, count, targetRaw);
                if (chunkEnd <= cursor) break;

                // Now the chosen range is known, so settle the class properly. A chunk that starts
                // on a quality channel can still run entirely into delta entries; routing that to
                // Zstd would be the case the traffic-class finding says to avoid. The prediction
                // above may then have used the wrong EMA, which costs at worst one mis-sized chunk.
                if (useZstd && !AvatarBundleZstdDeltaBundles && ChunkIsDeltaOnly(pending, cursor, chunkEnd)) useZstd = false;

                int rawLen = BuildRawForRange(stateI, pending, cursor, chunkEnd);
                if (rawLen < AvatarBundleMinBytes) break;

                // Bound once the codec is final; every EMA update below feeds the codec that ran.
                ref float ema = ref (useZstd ? ref stateI.LastBundleZstdRatio : ref stateI.LastBundleRatio);

                if (TryDeflateAndEmit(stateI, peer, cursor, chunkEnd, rawLen, budget, useZstd, ref bundleCount, ref bundleBytes, out int compressedLen))
                {
                    UpdateRatioEMA(ref ema, compressedLen, rawLen, weightOnObserved: 0.3f);
                    cursor = chunkEnd;
                    if (fillMargin < MaxBundleFillMargin) fillMargin = Math.Min(MaxBundleFillMargin, fillMargin + BundleFillMarginRecover);
                    continue;
                }

                // Overshoot — recompute target using the actual ratio we just observed and
                // retry with a smaller chunk. Heavier weight on the observed value: this
                // receiver's payload likely just compresses worse than predicted.
                UpdateRatioEMA(ref ema, compressedLen, rawLen, weightOnObserved: 0.7f);
                fillMargin = Math.Max(MinBundleFillMargin, fillMargin - BundleFillMarginBackoff);
                float observed = (float)compressedLen / rawLen;
                if (observed < 0.05f) observed = 0.05f;
                if (observed > 0.99f) observed = 0.99f;

                int retryTargetRaw = (int)((budget * 0.92f) / observed);
                int retryEnd = PickChunkEnd(pending, cursor, chunkEnd, retryTargetRaw);
                if (retryEnd >= chunkEnd) retryEnd = cursor + Math.Max(1, (chunkEnd - cursor) * 3 / 4);
                if (retryEnd <= cursor) break;

                // Shrinking the chunk can drop the entries that made it a keyframe chunk, so the
                // class is re-derived for the retry rather than inherited.
                bool retryUseZstd = useZstd
                                    && (AvatarBundleZstdDeltaBundles || !ChunkIsDeltaOnly(pending, cursor, retryEnd));

                int retryRawLen = BuildRawForRange(stateI, pending, cursor, retryEnd);
                if (retryRawLen < AvatarBundleMinBytes) break;

                if (BSRProfiler.Enabled) BSRProfiler.Local.BundleRetries++;
                if (!TryDeflateAndEmit(stateI, peer, cursor, retryEnd, retryRawLen, budget, retryUseZstd, ref bundleCount, ref bundleBytes, out int retryCompressed))
                {
                    // Two failures in a row — give up on bundling for this receiver this tick;
                    // caller replays cursor..count uncompressed.
                    break;
                }

                ref float retryEma = ref (retryUseZstd ? ref stateI.LastBundleZstdRatio : ref stateI.LastBundleRatio);
                UpdateRatioEMA(ref retryEma, retryCompressed, retryRawLen, weightOnObserved: 0.5f);
                cursor = retryEnd;
            }
            stateI.BundleFillMargin = fillMargin;
            return cursor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PickChunkEnd(PendingAvatarSend[] pending, int cursor, int hardEnd, int targetRaw)
        {
            int chunkEnd = cursor;
            int rawAccum = 0;
            while (chunkEnd < hardEnd)
            {
                // v50 grouped layout: [len:2][bytes] per entry, plus a [chan:1][n:1] header each
                // time the channel changes. Pending is channel-sorted, so on a sorted array this
                // charges the header once per run; on an unsorted one it degrades to the old
                // 4-bytes-per-entry and merely picks smaller chunks.
                int entrySize = 2 + pending[chunkEnd].Length;
                if (chunkEnd == cursor || pending[chunkEnd].Channel != pending[chunkEnd - 1].Channel) entrySize += 2;
                // Always include at least one entry so the chunk grows; only break once
                // adding the next would exceed the predicted budget.
                if (chunkEnd > cursor && rawAccum + entrySize > targetRaw) break;
                rawAccum += entrySize;
                chunkEnd++;
            }
            return chunkEnd;
        }

        private static int BuildRawForRange(PlayerState stateI, PendingAvatarSend[] pending, int start, int end)
        {
            // 4 not 3: worst case is one group per entry ([ch][n]) plus its [len:2].
            int upperBound = 0;
            for (int i = start; i < end; i++) upperBound += 4 + pending[i].Length;

            byte[] raw = stateI.BundleRawScratch;
            if (raw == null || raw.Length < upperBound)
            {
                if (raw != null) ArrayPool<byte>.Shared.Return(raw);
                raw = ArrayPool<byte>.Shared.Rent(Math.Max(upperBound, 4096));
                stateI.BundleRawScratch = raw;
            }

            int rawPos = 0;
            int i2 = start;
            while (i2 < end)
            {
                byte channel = pending[i2].Channel;

                // Extend the run while the channel holds, counting only entries that will actually
                // be emitted. n is a byte on the wire, so a run is capped at 255 and simply
                // continues as a second group with the same channel.
                int runEnd = i2;
                int n = 0;
                while (runEnd < end && pending[runEnd].Channel == channel && n < byte.MaxValue)
                {
                    if (pending[runEnd].Length > pending[runEnd].IntervalOffset) n++;
                    runEnd++;
                }
                if (n == 0) { i2 = runEnd; continue; }

                raw[rawPos++] = channel;
                raw[rawPos++] = (byte)n;

                for (int k = i2; k < runEnd; k++)
                {
                    ref PendingAvatarSend p = ref pending[k];
                    if (p.Length <= p.IntervalOffset) continue;
                    BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(rawPos, 2), (ushort)p.Length);
                    rawPos += 2;
                }

                if (channel != BasisNetworkCommons.DeltaAvatarChannel)
                {
                    for (int k = i2; k < runEnd; k++)
                    {
                        ref PendingAvatarSend p = ref pending[k];
                        if (p.Length <= p.IntervalOffset) continue;
                        Buffer.BlockCopy(p.Source, 0, raw, rawPos, p.Length);
                        // Patch the per-receiver interval byte in our copy (source is shared).
                        raw[rawPos + p.IntervalOffset] = p.Interval;
                        rawPos += p.Length;
                    }
                }
                else
                {
                    int maxLen = 0;
                    for (int k = i2; k < runEnd; k++)
                    {
                        ref PendingAvatarSend p = ref pending[k];
                        if (p.Length > p.IntervalOffset && p.Length > maxLen) maxLen = p.Length;
                    }
                    // The interval patch is applied as the column is written — the byte the
                    // receiver must see at logical offset IntervalOffset is this receiver's
                    // interval, not whatever the shared Source holds.
                    for (int j = 0; j < maxLen; j++)
                    {
                        for (int k = i2; k < runEnd; k++)
                        {
                            ref PendingAvatarSend p = ref pending[k];
                            if (p.Length <= p.IntervalOffset || j >= p.Length) continue;
                            raw[rawPos++] = j == p.IntervalOffset ? p.Interval : p.Source[j];
                        }
                    }
                }
                i2 = runEnd;
            }
            return rawPos;
        }

        private const int ChannelHistogramSize = 64;

        /// <summary>
        /// The order channel groups are written to the peer in, most expendable first.
        ///
        /// <para>⚠️ This ordering is a delivery guarantee, not a cosmetic detail. The receiver's
        /// unreliable queue discards from the FRONT when it is over budget, so whatever is written
        /// LAST is what survives an overloaded tick. Ascending channel order — which is what a plain
        /// counting sort produces — is exactly backwards twice over: it wrote VeryLow first, so the
        /// distant senders whose frame is their only update for most of a second were dropped in
        /// favour of near ones that get another frame 50 ms later; and it wrote every small-id
        /// channel (6-13) before every large-id one (41-48), so under pressure the first 256 players
        /// to join froze for the entire server while later joiners kept moving. Neither is something
        /// <c>_senderRotation</c> can fix: the sort is stable, so rotation only permutes WITHIN a
        /// group.</para>
        ///
        /// <para>Groups therefore go High → delta → Medium → Low → VeryLow, so the trim eats the
        /// frames that are about to be superseded anyway, and the two id widths of a tier swap places
        /// each tick so neither is permanently the one cut into.</para>
        ///
        /// <para>⚠️ The delta channel is the one placement that is a judgement rather than a
        /// consequence: it is quality-agnostic, so a delta frame's cadence is not visible here the
        /// way a keyframe channel's is. It sits just behind High because a delta is only sent to a
        /// receiver that already holds the sender's CURRENT keyframe generation, and a pair on a
        /// second-long interval has usually missed one and is being sent a keyframe instead — so
        /// deltas skew towards the frequently-served pairs that lose least by waiting a tick.</para>
        /// </summary>
        private static readonly byte[][] ChannelFlushOrder =
        {
            BuildChannelFlushOrder(largeIdFirst: false),
            BuildChannelFlushOrder(largeIdFirst: true),
        };

        private static byte[] BuildChannelFlushOrder(bool largeIdFirst)
        {
            byte[] avatar = new byte[17];
            int written = 0;
            for (int quality = 3; quality >= 0; quality--)
            {
                byte small = (byte)(BasisNetworkCommons.PlayerAvatarVeryLowChannel + (quality * 2));
                byte large = (byte)(BasisNetworkCommons.PlayerAvatarVeryLowLargeChannel + (quality * 2));
                byte first = largeIdFirst ? large : small;
                byte second = largeIdFirst ? small : large;
                avatar[written++] = first;
                avatar[written++] = (byte)(first + 1);
                avatar[written++] = second;
                avatar[written++] = (byte)(second + 1);
                if (quality == 3) avatar[written++] = BasisNetworkCommons.DeltaAvatarChannel;
            }

            bool[] isAvatar = new bool[ChannelHistogramSize];
            foreach (byte channel in avatar) isAvatar[channel] = true;

            byte[] order = new byte[ChannelHistogramSize];
            int position = 0;
            for (int channel = 0; channel < ChannelHistogramSize; channel++)
            {
                if (!isAvatar[channel]) order[position++] = (byte)channel;
            }
            foreach (byte channel in avatar) order[position++] = channel;
            return order;
        }

        /// <summary>
        /// Groups a receiver's pending sends by channel so each bundle carries a few long runs
        /// instead of an interleaved stream. Sorts in place from the caller's point of view.
        ///
        /// <para>⚠️ The scatter-then-copy-back looks like an obvious thing to optimise into a
        /// buffer swap, and a profile will actively encourage you: <c>Array.Copy</c> here shows as
        /// ~15% of server CPU at 1000 players. <b>That attribution is false and the swap was tried
        /// and reverted.</b> The sampler charges GC-poll time to whichever method happens to hold
        /// the poll point, and this copy was merely where threads parked; removing it moved exactly
        /// the same time onto <see cref="EmitGreedyBundles"/> and changed nothing else. Measured in
        /// isolation the whole sort is 286 ns per flush (7.3 ns/entry) and the copy is ~8 ns of it,
        /// so a swap buys 3% of an operation that is 0.02 cores in total. Not worth handing the
        /// caller back a different array than it passed in.</para>
        /// </summary>
        private static void SortPendingByChannel(PlayerState stateI, PendingAvatarSend[] pending, int count)
        {
            if (count < 2) return;

            Span<int> offsets = stackalloc int[ChannelHistogramSize];
            offsets.Clear();
            for (int i = 0; i < count; i++)
            {
                byte c = pending[i].Channel;
                if (c >= ChannelHistogramSize) return;   // not an avatar channel — leave as-is
                offsets[c]++;
            }

            byte[] flushOrder = ChannelFlushOrder[_senderRotation & 1];
            int running = 0;
            for (int k = 0; k < ChannelHistogramSize; k++)
            {
                int c = flushOrder[k];
                int n = offsets[c];
                offsets[c] = running;
                running += n;
            }

            PendingAvatarSend[] dst = stateI.PendingSortScratch;
            if (dst == null || dst.Length < count)
            {
                dst = new PendingAvatarSend[Math.Max(count, 64)];
                stateI.PendingSortScratch = dst;
            }

            for (int i = 0; i < count; i++) dst[offsets[pending[i].Channel]++] = pending[i];
            Array.Copy(dst, pending, count);
            // Drop the scratch's copy of the Source references; see the field's doc comment.
            Array.Clear(dst, 0, count);
        }

        private static bool TryDeflateAndEmit(PlayerState stateI, NetPeer peer, int chunkStart, int chunkEnd, int rawLen, int budget, bool useZstd, ref long bundleCount, ref long bundleBytes, out int compressedLen)
        {
            // rawLen rides the bundle header as a ushort, so an oversized chunk cannot be framed
            // at all. Reported as a full-size 'compression' so the caller's retry shrinks the chunk
            // hard rather than reading a zero as a spectacular ratio.
            if (rawLen > ushort.MaxValue)
            {
                compressedLen = rawLen;
                return false;
            }
            compressedLen = 0;
            byte[] raw = stateI.BundleRawScratch;
            byte[] compressed = stateI.BundleCompressedScratch;
            // LZ4 worst case is rawLen + (rawLen / 255) + 16; Zstd's bound is a little larger.
            // Size for whichever codec could run so a receiver that switches class mid-stream
            // never has to re-rent, and so an incompressible chunk cannot overrun the scratch.
            int compCapacityNeeded = BundleHeaderSize + Math.Max(
                LZ4Codec.MaximumOutputSize(rawLen),
                useZstd ? BasisAvatarBundleZstd.MaximumOutputSize(rawLen) : 0);
            if (compressed == null || compressed.Length < compCapacityNeeded)
            {
                if (compressed != null) ArrayPool<byte>.Shared.Return(compressed);
                compressed = ArrayPool<byte>.Shared.Rent(Math.Max(compCapacityNeeded, 4096));
                stateI.BundleCompressedScratch = compressed;
            }

            bool profiling = BSRProfiler.Enabled;
            long deflateStart = profiling ? Stopwatch.GetTimestamp() : 0;

            // Encode directly into the wire packet's payload region. Either codec reporting
            // failure is treated as an overshoot and the caller retries with a smaller chunk.
            byte codec;
            Span<byte> payload = compressed.AsSpan(BundleHeaderSize, compressed.Length - BundleHeaderSize);
            if (useZstd && BasisAvatarBundleZstd.TryCompress(raw.AsSpan(0, rawLen), payload, out compressedLen))
            {
                codec = BasisAvatarBundleZstd.CodecZstdDict;
            }
            else
            {
                // Covers both "this chunk is delta-only" and "Zstd declined" — LZ4 is always a
                // valid encoding of any bundle body, so there is no failure path to handle here
                // beyond the shared overshoot check below.
                codec = BasisAvatarBundleZstd.CodecLz4;
                compressedLen = LZ4Codec.Encode(raw.AsSpan(0, rawLen), payload, LZ4Level.L00_FAST);
            }

            long deflateTicks = profiling ? Stopwatch.GetTimestamp() - deflateStart : 0;
            if (profiling) BSRProfiler.Local.BundleDeflateTicks += deflateTicks;

            if (compressedLen <= 0 || compressedLen > budget)
            {
                return false;
            }

            int wireLen = BundleHeaderSize + compressedLen;
            int chunkCount = chunkEnd - chunkStart;
            compressed[0] = BasisAvatarBundleZstd.PackFlags(
                codec,
                codec == BasisAvatarBundleZstd.CodecZstdDict ? BasisAvatarBundleZstd.DictionaryGeneration : (byte)0);
            BinaryPrimitives.WriteUInt16LittleEndian(compressed.AsSpan(1, 2), (ushort)rawLen);

            peer.SendUnreliableRawMerge(compressed, 0, wireLen, BasisNetworkCommons.CompressedAvatarBundleChannel);
            bundleCount++;
            bundleBytes += wireLen;

            if (profiling)
            {
                var counters = BSRProfiler.Local;
                counters.BundlesEmitted++;
                counters.BundleMessages += chunkCount;
                counters.BundleRawBytes += rawLen;
                counters.BundleCompressedBytes += compressedLen;
                // Split out so the health endpoint can show what each codec is actually costing
                // and returning in production, rather than only the blended average.
                if (codec == BasisAvatarBundleZstd.CodecZstdDict)
                {
                    counters.BundleZstdEmitted++;
                    counters.BundleZstdRawBytes += rawLen;
                    counters.BundleZstdCompressedBytes += compressedLen;
                    counters.BundleZstdTicks += deflateTicks;
                }
            }
            return true;
        }

        private static bool ChunkIsDeltaOnly(PendingAvatarSend[] pending, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                if (pending[i].Channel != BasisNetworkCommons.DeltaAvatarChannel) return false;
            }
            return true;
        }

        private static bool ZstdPathAvailable()
            => EnableAvatarBundleZstd
               && BasisAvatarBundleZstd.Available
               && Volatile.Read(ref _loadShedTier) <= AvatarBundleZstdMaxShedTier;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateRatioEMA(ref float ema, int compressed, int raw, float weightOnObserved)
        {
            if (raw <= 0) return;
            float observed = (float)compressed / raw;
            if (observed < 0.05f) observed = 0.05f;
            if (observed > 0.99f) observed = 0.99f;
            float prev = ema;
            if (prev < 0.05f || prev > 0.95f) prev = observed; // unseeded → adopt
            ema = prev * (1f - weightOnObserved) + observed * weightOnObserved;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkQualityUsed(ref int usedQualities, int qi)
        {
            int bit = 1 << qi;
            // Bits are sticky (only set, never cleared in the send loop), so a plain read
            // showing "set" is always correct. Avoids the Volatile.Read barrier in the
            // common case after the first few ticks when all 4 bits converge.
            if ((usedQualities & bit) != 0) return;

            int cur = Volatile.Read(ref usedQualities);
            while (true)
            {
                if ((cur & bit) != 0) return;
                int updated = cur | bit;
                int was = Interlocked.CompareExchange(ref usedQualities, updated, cur);
                if (was == cur) return;
                cur = was;
            }
        }
    }
}
