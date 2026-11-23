using Basis.Network.Core;
using BasisNetworkCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Basis.Network.Server.Generic
{
    /// <summary>
    /// Server-side rate control for image and animated-image traffic, in both directions.
    ///
    /// ── Why the server has to own this ───────────────────────────────────────────────────────
    ///
    /// The client already paces its own image uploads against a budget the server advertises
    /// (<see cref="Configuration.ImageShareEgressMegabitsPerSecond"/>). That is the right place for
    /// the *decision* — only the sharer knows how its fan-out splits between relayed and direct
    /// peers — but it is the wrong place for the *guarantee*, because a modified client simply
    /// ignores it and the original failure returns: one sharer saturating the server's egress and
    /// disconnecting everybody. Everything a client is asked to do politely needs a server-side
    /// floor under it, and <see cref="TryConsumeEgress"/> is that floor.
    ///
    /// The download direction has no client half at all. Cache replay is the server's own send to
    /// an arriving player who never requested it, so pacing it is not a backstop but the only
    /// control that exists.
    ///
    /// ── Why dropping is the right response to overrun ────────────────────────────────────────
    ///
    /// Image chunks are ReliableOrdered, so a dropped relay does not merely delay an image — it
    /// ends that transfer, and the recipients clean it up on their inbound timeout. That sounds
    /// harsh until you consider the alternative: queueing the overrun instead would grow the same
    /// server-side backlog this exists to prevent, turning an abusive client into an out-of-memory
    /// rather than a failed picture. A sender inside its budget is never dropped, so the cost falls
    /// exactly on the client that ignored the limit.
    /// </summary>
    public static class BasisImageBandwidthGovernor
    {
        /// <summary>Megabits per second to bytes per second.</summary>
        private const double MegabitsToBytes = 125_000.0;

        /// <summary>
        /// Seconds of budget a bucket may bank while idle.
        ///
        /// Image traffic is bursty by nature — a share is a wall of chunks and then nothing — so a
        /// bucket with no burst allowance would clip the front of every transfer even at a rate the
        /// operator intended to permit. Two seconds is long enough to cover a chunk train across a
        /// tick boundary and short enough that banked credit cannot fund a sustained flood.
        /// </summary>
        private const double BurstSeconds = 2.0;

        /// <summary>How often the replay pump wakes. Fine enough to pace smoothly, coarse enough to cost nothing.</summary>
        private const int PumpIntervalMs = 25;

        private const int MaxQueuedReplayPackets = 256;

        // ── Upload: per-sender egress buckets ────────────────────────────────────────────────

        private sealed class EgressBucket
        {
            public double Tokens;
            public long LastRefillTicks;
        }

        private static readonly ConcurrentDictionary<ushort, EgressBucket> _egress =
            new ConcurrentDictionary<ushort, EgressBucket>();

        private static long _droppedMessages;
        private static long _droppedBytes;

        /// <summary>Image relays refused because the sender was over its enforced budget.</summary>
        public static long DroppedMessages => Interlocked.Read(ref _droppedMessages);

        /// <summary>Bytes of server egress those refusals avoided, counting fan-out.</summary>
        public static long DroppedBytes => Interlocked.Read(ref _droppedBytes);

        private static double EnforcedEgressBytesPerSecond
        {
            get
            {
                Configuration configuration = NetworkServer.Configuration;
                if (configuration == null) return 0;

                int megabits = configuration.ImageShareEgressMegabitsPerSecond;
                if (megabits <= 0) return 0; // nothing advertised means nothing to enforce

                int percent = configuration.ImageShareEgressEnforcementPercent;
                if (percent < 100) percent = 100; // enforcing below what was advertised is never correct

                return megabits * MegabitsToBytes * (percent / 100.0);
            }
        }

        /// <summary>
        /// Charges one image relay against its sender's budget. Returns false when the sender is
        /// over, in which case the caller must not relay the message.
        /// </summary>
        /// <param name="senderId">Player id of the sharer.</param>
        /// <param name="bytes">Wire bytes multiplied by the number of peers this will be sent to.</param>
        public static bool TryConsumeEgress(ushort senderId, long bytes)
        {
            double ratePerSecond = EnforcedEgressBytesPerSecond;
            if (ratePerSecond <= 0 || bytes <= 0)
            {
                return true; // disabled
            }

            EgressBucket bucket = _egress.GetOrAdd(senderId, _ => new EgressBucket
            {
                Tokens = ratePerSecond * BurstSeconds,
                LastRefillTicks = DateTime.UtcNow.Ticks,
            });

            lock (bucket)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                double elapsedSeconds = (nowTicks - bucket.LastRefillTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSeconds > 0)
                {
                    bucket.LastRefillTicks = nowTicks;
                    double ceiling = ratePerSecond * BurstSeconds;
                    bucket.Tokens = Math.Min(ceiling, bucket.Tokens + ratePerSecond * elapsedSeconds);
                }

                // Gate on having something rather than on the whole charge fitting, and let the
                // bucket go negative. A single chunk multiplied by a wide fan-out can exceed the
                // entire burst capacity, and a bucket that demanded the charge fit would deadlock
                // that sender forever. Going negative repays itself at the configured rate, so the
                // long-run average is still exactly the budget.
                if (bucket.Tokens <= 0)
                {
                    Interlocked.Increment(ref _droppedMessages);
                    Interlocked.Add(ref _droppedBytes, bytes);
                    return false;
                }

                bucket.Tokens -= bytes;
                return true;
            }
        }

        // ── Download: paced cache replay ─────────────────────────────────────────────────────

        /// <summary>One arriving peer's outstanding replay, drained by the pump at the configured rate.</summary>
        private sealed class ReplayJob
        {
            public NetPeer Peer;
            public List<PendingPayload> Payloads;
            public int Cursor;
            public double Tokens;
            public long LastRefillTicks;

            /// <summary>
            /// Guards <see cref="Payloads"/> and <see cref="Cursor"/>. The cache appends to a live job
            /// from the tick thread while the pump is draining it, and a List being grown underneath an
            /// indexer is not something to leave to chance.
            /// </summary>
            public readonly object Sync = new object();

            /// <summary>
            /// Set under <see cref="Sync"/> when the pump has decided this job is finished, so an append
            /// racing the removal starts a fresh job instead of writing into one nobody will drain.
            /// </summary>
            public bool Retired;
        }

        /// <summary>A cached payload plus the owner it must be stamped with on the way out.</summary>
        public readonly struct PendingPayload
        {
            public readonly ushort OwnerId;
            public readonly byte[] Payload;

            public PendingPayload(ushort ownerId, byte[] payload)
            {
                OwnerId = ownerId;
                Payload = payload;
            }
        }

        private static readonly ConcurrentDictionary<int, ReplayJob> _replays =
            new ConcurrentDictionary<int, ReplayJob>();

        private static Thread _pump;
        private static volatile bool _pumpRunning;

        /// <summary>
        /// Whether enqueuing starts the background pump. Tests turn this off and drive
        /// <see cref="PumpOnceForTests"/> themselves — otherwise the pump thread drains the same
        /// queue underneath them and a rate assertion becomes a race against a 25 ms timer.
        /// </summary>
        internal static bool AutoPump = true;

        /// <summary>Sends one payload to one peer. Supplied by the cache, which owns the wire format.</summary>
        public static Action<NetPeer, ushort, byte[]> SendPayload;

        private static double ReplayBytesPerSecond
        {
            get
            {
                Configuration configuration = NetworkServer.Configuration;
                if (configuration == null) return 0;
                int megabits = configuration.ImageShareDownloadMegabitsPerSecond;
                return megabits <= 0 ? 0 : megabits * MegabitsToBytes;
            }
        }

        /// <summary>
        /// Queues a peer's cache replay to be delivered at the configured rate. Returns false when
        /// pacing is disabled, in which case the caller should send inline as it always did.
        /// </summary>
        public static bool EnqueueReplay(NetPeer peer, List<PendingPayload> payloads)
        {
            if (peer == null || payloads == null || payloads.Count == 0) return false;

            double ratePerSecond = ReplayBytesPerSecond;
            if (ratePerSecond <= 0) return false;

            // Append rather than replace. A peer picks up images repeatedly now - once on join and
            // again each time they walk into range of another card - and overwriting the job would
            // throw away whatever of the previous batch had not gone out yet.
            if (_replays.TryGetValue(peer.Id, out ReplayJob existing))
            {
                lock (existing.Sync)
                {
                    if (!existing.Retired)
                    {
                        existing.Payloads.AddRange(payloads);
                        existing.Peer = peer;
                        EnsurePump();
                        return true;
                    }
                }
            }

            var job = new ReplayJob
            {
                Peer = peer,
                Payloads = payloads,
                Cursor = 0,
                Tokens = ratePerSecond * BurstSeconds,
                LastRefillTicks = DateTime.UtcNow.Ticks,
            };

            _replays[peer.Id] = job;
            EnsurePump();
            return true;
        }

        private static void EnsurePump()
        {
            if (!AutoPump || _pumpRunning) return;
            lock (_replays)
            {
                if (_pumpRunning) return;
                _pumpRunning = true;
                _pump = new Thread(PumpLoop)
                {
                    Name = "BasisImageReplayPump",
                    IsBackground = true,
                };
                _pump.Start();
            }
        }

        private static void PumpLoop()
        {
            while (_pumpRunning)
            {
                try
                {
                    PumpOnce();
                }
                catch (Exception exception)
                {
                    // A replay is a nicety — a joiner that misses one re-requests nothing and simply
                    // does not see that image. Never let it take the pump thread down with it.
                    BNL.LogError($"[ImageReplay] pump error: {exception.Message}");
                }
                Thread.Sleep(PumpIntervalMs);
            }
        }

        private static void PumpOnce()
        {
            if (_replays.IsEmpty) return;

            double ratePerSecond = ReplayBytesPerSecond;
            Action<NetPeer, ushort, byte[]> send = SendPayload;

            foreach (KeyValuePair<int, ReplayJob> pair in _replays)
            {
                ReplayJob job = pair.Value;

                // A rate turned off under us drops the remaining work rather than holding the
                // payload list alive.
                //
                // Departure is handled by RemovePeer on the disconnect path rather than by polling
                // a roster here. That keeps the pump independent of server state — it is testable
                // on its own — and a send that races a disconnect is harmless anyway, because the
                // transport's TrySend is what it says it is.
                if (send == null || ratePerSecond <= 0 || job.Peer == null)
                {
                    _replays.TryRemove(pair.Key, out _);
                    continue;
                }

                long nowTicks = DateTime.UtcNow.Ticks;
                double elapsedSeconds = (nowTicks - job.LastRefillTicks) / (double)TimeSpan.TicksPerSecond;
                if (elapsedSeconds > 0)
                {
                    job.LastRefillTicks = nowTicks;
                    double ceiling = ratePerSecond * BurstSeconds;
                    job.Tokens = Math.Min(ceiling, job.Tokens + ratePerSecond * elapsedSeconds);
                }

                while (job.Tokens > 0)
                {
                    if (job.Peer.GetPacketsCountInQueue(BasisNetworkCommons.DirectSceneServerChannel, DeliveryMethod.ReliableOrdered) >= MaxQueuedReplayPackets)
                    {
                        break;
                    }
                    PendingPayload pending;
                    lock (job.Sync)
                    {
                        if (job.Cursor >= job.Payloads.Count) break;
                        pending = job.Payloads[job.Cursor];
                        job.Cursor++;
                    }

                    int size = pending.Payload?.Length ?? 0;
                    if (size == 0) continue;

                    job.Tokens -= size;
                    send(job.Peer, pending.OwnerId, pending.Payload);
                }

                bool retired;
                lock (job.Sync)
                {
                    retired = job.Cursor >= job.Payloads.Count;
                    job.Retired = retired;
                }
                if (retired)
                {
                    _replays.TryRemove(pair.Key, out _);
                }
            }
        }

        /// <summary>Forgets a peer's buckets and any queued replay. Called when they disconnect.</summary>
        public static void RemovePeer(int peerId)
        {
            _replays.TryRemove(peerId, out _);
            if (peerId >= 0 && peerId <= ushort.MaxValue)
            {
                _egress.TryRemove((ushort)peerId, out _);
            }
        }

        /// <summary>Drops all state. Called on server stop and from tests.</summary>
        public static void Reset()
        {
            _pumpRunning = false;
            _pump = null;
            AutoPump = true;
            _replays.Clear();
            _egress.Clear();
            Interlocked.Exchange(ref _droppedMessages, 0);
            Interlocked.Exchange(ref _droppedBytes, 0);
        }

        /// <summary>Test seam: runs one pump pass without the background thread.</summary>
        internal static void PumpOnceForTests() => PumpOnce();

        /// <summary>Test seam: whether a peer still has replay work outstanding.</summary>
        internal static bool HasPendingReplay(int peerId) => _replays.ContainsKey(peerId);
    }
}
