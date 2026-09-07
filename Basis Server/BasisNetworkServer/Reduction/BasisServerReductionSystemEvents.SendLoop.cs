using Basis.Network.Core;
using BasisNetworkServer.BasisNetworking;
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        private static void UpdateCommunicationAndDistances(long nowTicks)
        {
            // Double-buffered snapshot: only rebuild when dirty
            if (_activePlayersDirty)
            {
                lock (_activePlayersLock)
                {
                    if (_activePlayersDirty)
                    {
                        _activePlayersSnapshot = BuildActiveRoster();
                        _activePlayersDirty = false;
                    }
                }
            }
            var activeCopy = _activePlayersSnapshot;

            int playerCount = activeCopy.Length;
            if (playerCount == 0)
            {
                return;
            }

            // Retune workers to the current population before the phase that uses them.
            TuneParallelism(playerCount);

            // Advance the sender visit order so queue trims do not always fall on the same players.
            _senderRotation++;

            // Snapshot generation counters only (positions handled by slow distance cache).
            int maxId = 0;
            for (int i = 0; i < playerCount; i++)
            {
                if (activeCopy[i].id > maxId) maxId = activeCopy[i].id;
            }
            int snapshotLen = maxId + 1;
            if (_generationSnapshot.Length < snapshotLen)
            {
                _generationSnapshot = new long[Math.Max(snapshotLen, _generationSnapshot.Length * 2)];
            }
            for (int i = 0; i < playerCount; i++)
            {
                int id = activeCopy[i].id;
                _generationSnapshot[id] = Interlocked.Read(ref activeCopy[i].state.DataGeneration);
            }

            // Fallback interval for pairs not yet in the distance cache (new players).
            long minIntervalTicks = (long)(BSRSMillisecondDefaultInterval * BSRBaseMultiplier * MsToTick);
            // Hoisted once per pass: the send loop is the only reader that can run before the distance
            // sweep has ever built it, so it is the one that has to guarantee it exists.
            int[] intervalTickTable = EnsureIntervalTickTable();

            // Floor on the interval we advertise this tick: a receiver is only visited every
            // _sliceCount ticks, so nothing can actually arrive faster than that regardless of what
            // distance says. Encoded once here rather than per pair.
            int deliverableIntervalMs = (int)(intervalMs * Math.Max(1, _sliceCount));
            byte degradedIntervalByte = BasisNetworkCommons.EncodeAvatarIntervalByte(
                deliverableIntervalMs, BSRSMillisecondDefaultInterval);

            // Tick slicing: only process a slice of receivers per tick
            int sliceSize = (playerCount + _sliceCount - 1) / _sliceCount;
            int start = _sliceIndex * sliceSize;
            int end = Math.Min(start + sliceSize, playerCount);
            _sliceIndex = (_sliceIndex + 1) % _sliceCount;

            if (start >= playerCount)
            {
                return;
            }

            // Sender/receiver pairs this pass will consider — the unit the send phase's cost
            // actually scales in. Receivers alone would be the wrong unit: the per-receiver cost is
            // proportional to the roster, so a population change mid-measurement would look like a
            // change in how well the pool parallelises.
            _lastSendPairs = (long)(end - start) * playerCount;

            // Workers this pass can actually put to work. Parallel.For over a range never uses more
            // of them than the range has items, so on a small slice the configured degree overstates
            // the width — and a rate divided by a width that never ran reads as a slow host.
            _lastSendWorkers = Math.Min(parallelOptions.MaxDegreeOfParallelism, end - start);

            bool bundlingEnabled = EnableAvatarBundleCompression;

            Parallel.For(start, end, parallelOptions, i =>
            {
                var (id, state) = activeCopy[i];
                var stateI = state;
                var peer = stateI.Peer;

                var tracking = stateI.PeerTracking;
                if (tracking == null)
                {
                    return;
                }
                var lastSeen = stateI.PeerLastSeenGeneration;
                if (lastSeen == null || lastSeen.Length < tracking.Length)
                {
                    // Repaired rather than skipped: returning here would drop the receiver from the
                    // pass without a word.
                    tracking = GrowPeerTracking(stateI, tracking.Length - 1);
                    lastSeen = stateI.PeerLastSeenGeneration;
                }

                // Per-receiver pending buffer: collect what would be sent this tick
                // and decide compress-or-individual at the end. Lazily grown.
                var pending = stateI.PendingSends;
                if (pending == null)
                {
                    pending = new PendingAvatarSend[64];
                    stateI.PendingSends = pending;
                }
                int pendingCount = 0;

                // Thread-local send counter — no Interlocked in the hot loop
                long localSends = 0;

                // Senders are visited from a rotating offset, not always from index 0.
                //
                // The order senders are visited in is the order their packets enter the receiver's
                // send queue, and when that queue is over budget it discards from the front — the
                // oldest, which is whatever went in earliest. With a fixed starting index and a
                // stable roster that is the same handful of players on every tick of every
                // receiver, so an overloaded server did not degrade everyone slightly: it stopped
                // sending a specific subset of people almost entirely, and they froze in place for
                // everyone else. Rotating the start spreads that cost across the population.
                //
                // Offset by the receiver as well as by the tick. A single global start meant every
                // receiver was cutting the same senders on the same tick, so whoever lost the trim
                // lost it for the whole server at once and visibly froze; staggering by id makes a
                // trimmed sender late for one viewer and on time for the next.
                int rotation = playerCount > 0 ? (int)(((uint)_senderRotation + (uint)id) % (uint)playerCount) : 0;

                for (int step = 0; step < playerCount; step++)
                {
                    int index = step + rotation;
                    if (index >= playerCount) index -= playerCount;

                    int jId = activeCopy[index].id;
                    if (id == jId)
                    {
                        continue;
                    }

                    // Bounds check — grow both arrays if needed (rare, only when IDs exceed capacity)
                    if (jId >= tracking.Length)
                    {
                        tracking = GrowPeerTracking(stateI, jId);
                        lastSeen = stateI.PeerLastSeenGeneration;
                    }

                    // 1. New data check — plain array read, no pointer chase. This is the cheapest
                    // test in the loop and rejects most pairs (senders publish well below tick rate),
                    // so nothing more expensive may run above it. The P2P check in particular used to
                    // sit higher up, paying a ConcurrentDictionary lookup on every pair in the matrix
                    // instead of only the ones that survive this gate.
                    // Truncated to 32 bits on both sides: the snapshot is a counter of avatar updates
                    // and the low half takes over a year to wrap at full rate. See
                    // PlayerState.PeerLastSeenGeneration for why this one field is not in the record.
                    uint senderGen = (uint)_generationSnapshot[jId];
                    if (senderGen <= lastSeen[jId])
                    {
                        continue;
                    }

                    // Their avatar data goes peer-to-peer, so the server must not also relay it.
                    // Guarded by a plain counter so the common case (no P2P sessions at all) costs a
                    // register compare rather than a hash lookup.
                    if (BasisServerP2PBroker.HasOffloadedPairs &&
                        BasisNetworkServer.BasisServerP2PBroker.IsP2POffloaded(jId, id))
                    {
                        continue;
                    }

                    PlayerState stateJ = activeCopy[index].state;

                    // Full-quality broadcast bypasses the distance throttle + quality reduction; the new-data gate still bounds it to the source rate.
                    bool bypassReduction = stateJ.BypassReduction;

                    // Quality tier for this pair, from the distance cache. Needed before the
                    // interval check because load shedding scales the interval by it.
                    int qi = bypassReduction ? 3 : tracking[jId].CachedQualityIndex;

                    // 2. Interval check using cached distance results (no float math).
                    //    Load shedding is applied here as an interval MULTIPLIER rather than as a
                    //    drop, so an overloaded server slows distant players down instead of freezing
                    //    them. A player 100 m away updating at 1 Hz reads as "far away"; the same
                    //    player receiving nothing reads as a broken client, which is exactly what
                    //    dropping the tier outright looked like in testing. Each tier below the shed
                    //    threshold doubles the interval, so degradation is graded by distance.
                    if (!bypassReduction)
                    {
                        // Unsigned subtraction: LastSentTime is the low 32 bits of the tick counter and
                        // wrapping is the intended arithmetic, not an overflow. See PeerTrackingData.
                        uint elapsed = (uint)nowTicks - tracking[jId].LastSentTime;
                        long required = tracking[jId].HasDistanceCache
                            ? intervalTickTable[tracking[jId].CachedIntervalByte]
                            : minIntervalTicks;

                        int shedSteps = _loadShedTier - qi;
                        if (shedSteps > 0)
                        {
                            required <<= Math.Min(shedSteps, MaxShedIntervalDoublings);
                        }

                        if (elapsed < required)
                        {
                            continue;
                        }
                    }

                    // Why shed by distance at all: tick slicing gets the priority backwards. Slicing
                    // visits a receiver once every _sliceCount ticks, capping its effective rate at
                    // tickRate/_sliceCount. A DISTANT sender is already on a ~500 ms interval so
                    // slicing costs it nothing; a NEARBY sender wants ~20 Hz and gets cut hard. So
                    // slicing hurts precisely the players you can see. Stretching intervals by tier is
                    // O(1) per pair, needs no sorting, and degrades the least visible pairs first.
                    // Report the cadence we will ACTUALLY deliver at, not the one distance asked for.
                    // The client decodes this byte into the interpolation window it plays the pose
                    // over, so a server that promises 50 ms while slicing delivers every 128 ms leaves
                    // every client extrapolating past the end of its window — visible as remote-player
                    // stutter that looks like a client bug. degradedIntervalByte is computed once per
                    // tick from the current slice factor and period; the encoding is monotonic in ms,
                    // so taking the larger byte is the same as taking the longer interval.
                    byte startAtZeroInterval;
                    if (bypassReduction)
                    {
                        startAtZeroInterval = 0;
                    }
                    else
                    {
                        // Include the shed stretch: if this pair is being slowed to a quarter rate the
                        // client must interpolate over that longer window, or it runs off the end of
                        // its buffer and stutters exactly like an under-delivering server.
                        int shedSteps = _loadShedTier - qi;
                        byte pairInterval = tracking[jId].CachedIntervalByte;
                        if (shedSteps > 0)
                        {
                            int stretched = BasisNetworkCommons.DecodeAvatarIntervalMs(pairInterval, BSRSMillisecondDefaultInterval)
                                            << Math.Min(shedSteps, MaxShedIntervalDoublings);
                            pairInterval = BasisNetworkCommons.EncodeAvatarIntervalByte(stretched, BSRSMillisecondDefaultInterval);
                        }
                        startAtZeroInterval = Math.Max(pairInterval, degradedIntervalByte);
                    }

                    // Delta vs keyframe: send a delta only when the current frame is a delta, the
                    // receiver already holds the current keyframe at this quality, and the delta is
                    // serialized. Otherwise send — and (re)baseline the receiver to — the keyframe.
                    bool sendDelta = EnableAvatarDeltaCompression
                        && !bypassReduction
                        && !stateJ.CurrentIsKeyframe
                        && tracking[jId].BaselineKeyframeGen == (uint)stateJ.KeyframeGen
                        && tracking[jId].BaselineQuality == qi
                        && stateJ.SerializedDeltaLength[qi] > 0
                        && stateJ.SerializedDelta[qi] != null;

                    byte[] srcArr;
                    int srcLen;
                    byte avatarChannel;
                    byte intervalOffset;
                    if (sendDelta)
                    {
                        srcArr = stateJ.SerializedDelta[qi];
                        srcLen = stateJ.SerializedDeltaLength[qi];
                        avatarChannel = BasisNetworkCommons.DeltaAvatarChannel;
                        // delta frame layout: [header:1][playerId:1|2][interval:1]...
                        intervalOffset = (byte)(stateJ.SmallId ? 2 : 3);
                    }
                    else
                    {
                        // Keyframe path (also the fallback when the receiver lacks the current baseline).
                        srcLen = stateJ.SerializedKeyframeLength[qi];
                        srcArr = stateJ.SerializedKeyframe[qi];
                        if (srcLen == 0 || srcArr == null)
                        {
                            MarkQualityUsed(ref stateJ.UsedQualities, qi);
                            continue;
                        }
                        avatarChannel = stateJ.SmallId
                            ? BasisNetworkCommons.GetPlayerAvatarChannelForQuality(qi, stateJ.SerializedHasAdditional[qi])
                            : BasisNetworkCommons.GetPlayerAvatarLargeChannelForQuality(qi, stateJ.SerializedHasAdditional[qi]);
                        // keyframe frame layout: [playerId:1|2][interval:1]...
                        intervalOffset = (byte)(stateJ.SmallId ? 1 : 2);
                        // Receiver now holds this keyframe generation + quality; subsequent deltas apply.
                        tracking[jId].BaselineKeyframeGen = (uint)stateJ.KeyframeGen;
                        tracking[jId].BaselineQuality = (byte)qi;
                    }

                    // Defer the send. Cheaper per-pair than SendUnreliableRawMerge:
                    // a single struct write vs pool-rent + BlockCopy + enqueue.
                    if (pendingCount == pending.Length)
                    {
                        Array.Resize(ref pending, pending.Length * 2);
                        stateI.PendingSends = pending;
                    }
                    ref PendingAvatarSend p = ref pending[pendingCount++];
                    p.Source = srcArr;
                    p.Length = srcLen;
                    p.Channel = avatarChannel;
                    p.Interval = startAtZeroInterval;
                    p.IntervalOffset = intervalOffset;

                    MarkQualityUsed(ref stateJ.UsedQualities, qi);

                    tracking[jId].LastSentTime = (uint)nowTicks;
                    lastSeen[jId] = senderGen;

                    localSends++;
                }

                stateI.PendingCount = pendingCount;
                if (pendingCount > 0)
                {
                    FlushPendingForReceiver(stateI, peer, bundlingEnabled);
                }

                // Per-thread block, folded in once per window — no atomic at all on this path.
                if (localSends > 0 && BSRProfiler.Enabled)
                {
                    BSRProfiler.Local.Sends += localSends;
                }
            });
        }

        private struct TailStats
        {
            private const int Capacity = 8;

            private byte _c0, _c1, _c2, _c3, _c4, _c5, _c6, _c7;
            private long _n0, _n1, _n2, _n3, _n4, _n5, _n6, _n7;
            private long _b0, _b1, _b2, _b3, _b4, _b5, _b6, _b7;
            private int _used;

            public void Add(byte channel, int length)
            {
                for (int i = 0; i < _used; i++)
                {
                    if (ChannelAt(i) == channel)
                    {
                        AddAt(i, length);
                        return;
                    }
                }

                if (_used < Capacity)
                {
                    SetChannelAt(_used, channel);
                    _used++;
                    AddAt(_used - 1, length);
                    return;
                }

                // More distinct channels in one flush than expected: record directly rather than
                // dropping the sample. Correct, just not batched.
                BasisNetworkStatistics.RecordOutboundBatch(channel, 1, length);
            }

            public void Flush()
            {
                for (int i = 0; i < _used; i++)
                {
                    BasisNetworkStatistics.RecordOutboundBatch(ChannelAt(i), CountAt(i), BytesAt(i));
                }
                _used = 0;
            }

            private byte ChannelAt(int i) => i switch
            {
                0 => _c0, 1 => _c1, 2 => _c2, 3 => _c3, 4 => _c4, 5 => _c5, 6 => _c6, _ => _c7,
            };

            private void SetChannelAt(int i, byte v)
            {
                switch (i)
                {
                    case 0: _c0 = v; _n0 = 0; _b0 = 0; break;
                    case 1: _c1 = v; _n1 = 0; _b1 = 0; break;
                    case 2: _c2 = v; _n2 = 0; _b2 = 0; break;
                    case 3: _c3 = v; _n3 = 0; _b3 = 0; break;
                    case 4: _c4 = v; _n4 = 0; _b4 = 0; break;
                    case 5: _c5 = v; _n5 = 0; _b5 = 0; break;
                    case 6: _c6 = v; _n6 = 0; _b6 = 0; break;
                    default: _c7 = v; _n7 = 0; _b7 = 0; break;
                }
            }

            private void AddAt(int i, int length)
            {
                switch (i)
                {
                    case 0: _n0++; _b0 += length; break;
                    case 1: _n1++; _b1 += length; break;
                    case 2: _n2++; _b2 += length; break;
                    case 3: _n3++; _b3 += length; break;
                    case 4: _n4++; _b4 += length; break;
                    case 5: _n5++; _b5 += length; break;
                    case 6: _n6++; _b6 += length; break;
                    default: _n7++; _b7 += length; break;
                }
            }

            private long CountAt(int i) => i switch
            {
                0 => _n0, 1 => _n1, 2 => _n2, 3 => _n3, 4 => _n4, 5 => _n5, 6 => _n6, _ => _n7,
            };

            private long BytesAt(int i) => i switch
            {
                0 => _b0, 1 => _b1, 2 => _b2, 3 => _b3, 4 => _b4, 5 => _b5, 6 => _b6, _ => _b7,
            };
        }

        private static void FlushPendingForReceiver(PlayerState stateI, NetPeer peer, bool bundlingEnabled)
        {
            int count = stateI.PendingCount;
            if (count <= 0) return;
            var pending = stateI.PendingSends;

            // Per-receiver-tick stats accumulators: fold per-send Interlocked into one
            // RecordOutboundBatch per channel at flush.
            //
            // This used to be two `stackalloc long[256]`. Stack allocation is not free — with
            // localsinit on (no [SkipLocalsInit] anywhere in this tree) both spans are zeroed on
            // every call, so at 1000 receivers x 250Hz that was ~1 GB/s of memset to track the
            // handful of channels the avatar path actually uses, followed by a 256-slot scan to
            // drain them. A small linear-probe accumulator covers the real channel count with no
            // zeroing and no scan; it falls back to direct recording if a run somehow uses more.
            TailStats tail = default;
            long bundleCount = 0;
            long bundleBytes = 0;

            // Bundling is only worth its CPU if the payload actually compresses. When a receiver's
            // observed ratio says otherwise we stop deflating for it and re-probe occasionally,
            // rather than paying full deflate cost every tick for a few percent.
            bool bundleThisFlush = bundlingEnabled && count >= AvatarBundleMinMessages;
            if (bundleThisFlush && stateI.BundleSkipCountdown > 0)
            {
                stateI.BundleSkipCountdown--;
                bundleThisFlush = false;
            }

            int cursor = 0;
            if (bundleThisFlush)
            {
                // Group the receiver's messages by channel before chunking, so each bundle carries
                // a handful of long runs rather than an interleaved one-channel-byte-per-entry
                // stream. Only worth the pass when we are actually about to bundle.
                SortPendingByChannel(stateI, pending, count);
                cursor = EmitGreedyBundles(stateI, peer, pending, count, ref bundleCount, ref bundleBytes);

                // LastBundleRatio is an EMA over what we just emitted, so this reflects real
                // observed behaviour for this receiver rather than a one-off bad chunk.
                if (cursor > 0 && stateI.LastBundleRatio > AvatarBundleMaxRatio)
                {
                    stateI.BundleSkipCountdown = AvatarBundleReprobeFlushes;
                }
            }

            // Send anything not packed into a bundle (the tail < min, or all of pending
            // when bundling is disabled / pathological no-fit). Equivalent to the
            // pre-bundling path; LiteNetLib's merge buffer still packs these into UDP packets.
            int tailSent = 0;
            for (int i = cursor; i < count; i++)
            {
                ref PendingAvatarSend p = ref pending[i];
                if (p.Length <= p.IntervalOffset) continue;
                peer.SendUnreliableRawMerge(p.Source, 0, p.Length, p.Channel, p.IntervalOffset, p.Interval);
                tail.Add(p.Channel, p.Length);
                tailSent++;
            }

            // Flush accumulated stats in one Interlocked.Add per (channel, metric).
            if (BasisNetworkStatistics.IsRecordingData)
            {
                if (bundleCount > 0)
                {
                    BasisNetworkStatistics.RecordOutboundBatch(BasisNetworkCommons.CompressedAvatarBundleChannel, bundleCount, bundleBytes);
                }
                if (tailSent > 0)
                {
                    tail.Flush();
                }
            }
            // Profiler attribution: distinguish "tail of bundled receiver" (cursor > 0) from
            // "fallback because bundling produced nothing" (cursor == 0 with bundling enabled).
            if (BSRProfiler.Enabled && tailSent > 0)
            {
                var counters = BSRProfiler.Local;
                counters.BundleTailUncompressed += tailSent;
                if (bundlingEnabled && cursor == 0 && count >= AvatarBundleMinMessages)
                {
                    counters.BundleFallbacks++;
                }
            }
            // Clear the payload references, not just the count. Leaving the Source pointers behind
            // keeps ~1M dead references reachable at 1000 players — every GC has to trace them, and
            // they pin the serialized payloads of players who may already have disconnected.
            for (int i = 0; i < count; i++)
            {
                pending[i].Source = null;
            }
            stateI.PendingCount = 0;

            // Give back a buffer that a busy spell grew and quiet ticks no longer justify.
            //
            // This array only ever grew before, sized by the worst tick a receiver had ever seen
            // and kept at that size forever — 223 MB across 4000 players, most of it untouched.
            // The peak is tracked over a window rather than reacting to one tick, so a receiver
            // that is periodically busy does not thrash between sizes.
            stateI.PendingPeak = Math.Max(stateI.PendingPeak, count);
            if (++stateI.PendingPeakTicks >= PendingShrinkWindowTicks)
            {
                stateI.PendingPeakTicks = 0;
                int want = Math.Max(PendingMinCapacity, stateI.PendingPeak * 2);
                if (pending.Length > want * 2)
                {
                    stateI.PendingSends = new PendingAvatarSend[want];
                }
                stateI.PendingPeak = 0;
            }

            // Keep modest scratch buffers between ticks; only hand back the oversized ones.
            //
            // These used to be returned unconditionally, which meant a rent and a return per
            // receiver per tick — at 1000 players that is ~330K operations a second against
            // ArrayPool.Shared, and its bucket contention showed up in the profile as spin-waiting
            // inside this method. Retaining the common small case removes nearly all of it.
            //
            // The cap is what keeps the original concern honest: a receiver that needed a huge
            // buffer for one tick gives it straight back, so nothing pins LOH-sized arrays per
            // player. Steady state is a few KB each, and disconnect returns whatever is held.
            if (stateI.BundleRawScratch != null && stateI.BundleRawScratch.Length > RetainedScratchBytes)
            {
                ArrayPool<byte>.Shared.Return(stateI.BundleRawScratch);
                stateI.BundleRawScratch = null;
            }
            if (stateI.BundleCompressedScratch != null && stateI.BundleCompressedScratch.Length > RetainedScratchBytes)
            {
                ArrayPool<byte>.Shared.Return(stateI.BundleCompressedScratch);
                stateI.BundleCompressedScratch = null;
            }
        }
    }
}
