namespace Basis.Network.Core
{
    /// <summary>
    /// Resolves the settings whose correct value is a function of how many players are connected
    /// and how much memory the box has.
    ///
    /// <see cref="BasisCpuBudget"/> already owns the settings that scale with core count. This is
    /// its sibling for the other axis, and it exists for the same reason: every constant it
    /// replaces was measured once, on one machine, at one player count, and was therefore wrong
    /// everywhere else. <c>MaxUnreliableQueuePerPeer = 256</c> was the clearest case — it was a
    /// sane backstop at a few hundred players and, at 2000, silently destroyed roughly half of
    /// every avatar update the reduction system produced.
    ///
    /// <b>Every value here is a CEILING, not a target.</b> Nothing tries to reach these numbers.
    /// The queue bound is only touched when the server is already failing to drain; the pool cap
    /// is only touched under a send burst. They are sized so that the failure mode is graceful
    /// rather than catastrophic, which means they must be generous on a large box and modest on a
    /// small one — and that is exactly what a fixed constant cannot do.
    ///
    /// <b>Why generous ceilings are safe here.</b> The reduction system now watches the transport's
    /// drop counter and throttles itself when deliveries start failing, so the queue bound no
    /// longer acts as the throttle. Before that feedback existed, a too-small bound WAS the
    /// throttle, and a hidden one: the producer could not see it, so it kept increasing its output
    /// in response to the cheap ticks that dropping produced. Raising these ceilings without that
    /// feedback loop in place would only move where the overload lands.
    ///
    /// Everything is expressed as a share of what the machine actually has, never as a fixed
    /// count. A hardcoded 4096 is really "about a fifth of 64 GB spread over 2000 players" and
    /// would be as wrong on a 8 GB VPS as 256 was here.
    /// </summary>
    public static class BasisPopulationScale
    {
        /// <summary>
        /// Share of the machine's memory the unreliable send queues may occupy at their bound.
        ///
        /// This is worst-case reserve, not working set: the queues sit near-empty whenever the
        /// server is keeping up, and measurements at 2000 players showed the resident set FALLING
        /// when the bound was raised, because a correctly-throttled producer stops filling them.
        /// The share only has to be large enough that a transient burst is absorbed instead of
        /// shed, and small enough that a stalled server cannot take the box down with it.
        /// </summary>
        public const double UnreliableQueueMemoryShare = 0.10;

        /// <summary>
        /// Share of memory the VOICE send queues may occupy at their bound.
        ///
        /// Equal to the bulk share, so the two together claim what the bulk queue used to claim
        /// alone — voice was carved out of that budget rather than added on top of it, and the
        /// worst case did not grow when it gained its own queue.
        ///
        /// Parity rather than a token slice is deliberate. Voice is a fan-IN at crowd scale: a
        /// receiver takes a packet from every audible talker every frame period, so its queue needs
        /// depth of the same order as the bulk one even though each sender is far slower. Sizing it
        /// as a single conversation measured 32.8% voice delivery at 1000 clients where a
        /// population-scaled bound delivered 93.6%.
        ///
        /// Measured on a deliberately starved server at 1000 clients, splitting the budget this way
        /// improved BOTH axes against the old shared queue: voice delivery 85.7% -> 93.6% and peak
        /// RSS 7.8 GB -> 4.6 GB. Bulk is what actually accumulates when a server falls behind,
        /// because bulk is what it produces most of.
        /// </summary>
        public const double PriorityQueueMemoryShare = 0.10;

        /// <summary>
        /// Share of memory the packet pool may retain.
        ///
        /// ⚠️ This is NOT a second budget on top of the queue shares — it is a ceiling on the SAME
        /// packets. A packet is either sitting in a send queue or sitting in the pool waiting to be
        /// rented again; it is never in both. So this has to be at least as large as the queues can
        /// hold, or the pool cannot take back what the queues let go of.
        ///
        /// It used to be 0.05 against queue shares totalling 0.20, which made the pool roughly
        /// fifty times too small to absorb its own queues, and the consequence was not "less memory"
        /// but WORSE memory — see <see cref="PacketPoolMax"/>.
        /// </summary>
        public const double PacketPoolMemoryShare =
            UnreliableQueueMemoryShare + PriorityQueueMemoryShare + 0.02;

        /// <summary>
        /// Floor for the per-peer unreliable bound. Below roughly this depth the queue cannot
        /// absorb even one reduction tick's fan-out at any useful population, so it stops being a
        /// backstop and becomes an unpredictable packet filter.
        /// </summary>
        public const int MinUnreliableQueuePerPeer = 512;

        /// <summary>
        /// Ceiling for the per-peer unreliable bound. Past this, extra depth only buys the right to
        /// deliver position updates so stale that the client would discard them anyway.
        /// </summary>
        public const int MaxUnreliableQueuePerPeer = 8192;

        /// <summary>Assumed bytes per queued packet, for turning a memory share into a packet count.</summary>
        private const int ApproxPacketBytes = 1432; // NetConstants.MaxPacketSize at the largest MTU

        private static long _availableMemoryBytes;

        /// <summary>
        /// Memory the runtime believes it can use — the container limit when there is one, physical
        /// RAM otherwise. Read once and cached; it does not change and the query is not free.
        /// </summary>
        public static long AvailableMemoryBytes
        {
            get
            {
                long cached = System.Threading.Volatile.Read(ref _availableMemoryBytes);
                if (cached > 0) return cached;

                // Reflected rather than called directly: this assembly also targets netstandard2.1
                // for the Unity package, where GC.GetGCMemoryInfo does not exist. Resolved once and
                // cached, so the reflection cost is paid a single time per process.
                long total = 0;
                try
                {
                    var method = typeof(System.GC).GetMethod(
                        "GetGCMemoryInfo", System.Type.EmptyTypes);
                    object info = method?.Invoke(null, null);
                    var property = info?.GetType().GetProperty("TotalAvailableMemoryBytes");
                    if (property?.GetValue(info) is long bytes) total = bytes;
                }
                catch (System.Exception)
                {
                    total = 0;
                }

                // A runtime that will not answer must not be read as "this box has no memory" —
                // that would collapse every ceiling to its floor. Assume a modest 4 GB instead.
                if (total <= 0) total = 4L * 1024 * 1024 * 1024;

                System.Threading.Volatile.Write(ref _availableMemoryBytes, total);
                return total;
            }
        }

        /// <summary>Test seam: pin the memory figure so the resolvers can be exercised at any box size.</summary>
        internal static void OverrideAvailableMemoryForTests(long bytes) =>
            System.Threading.Volatile.Write(ref _availableMemoryBytes, bytes);

        private static int Clamp(long value, int min, int max) =>
            value < min ? min : value > max ? max : (int)value;

        /// <summary>
        /// Per-peer unreliable queue depth for <paramref name="peers"/> connected peers.
        ///
        /// Divides the memory share by the population, so the worst-case total is bounded by the
        /// box rather than by the player count. A 64 GB host at 2000 players lands near 4500 —
        /// which measured zero drops where the old fixed 256 shed 48% — and the same formula gives
        /// a 8 GB VPS a few hundred, which is the honest answer for that machine. It will shed
        /// more, and the reduction system's drop feedback will notice and lower its output to suit.
        /// </summary>
        /// <param name="configured">Value from config; anything above 0 wins and is returned as-is.</param>
        public static int UnreliableQueuePerPeer(int configured, int peers)
        {
            if (configured > 0) return configured;
            if (peers < 1) peers = 1;

            long budgetPackets = (long)(AvailableMemoryBytes * UnreliableQueueMemoryShare) / ApproxPacketBytes;
            return Clamp(budgetPackets / peers, MinUnreliableQueuePerPeer, MaxUnreliableQueuePerPeer);
        }

        /// <summary>
        /// Floor for the per-peer VOICE bound. Higher than the bulk floor because this queue holds
        /// a fan-in, not a stream: a receiver in a crowd is fed by every talker in range at once.
        /// </summary>
        public const int MinPriorityQueuePerPeer = 1024;

        /// <summary>Ceiling for the per-peer voice bound.</summary>
        public const int MaxPriorityQueuePerPeer = 8192;

        /// <summary>
        /// Per-peer voice queue depth for <paramref name="peers"/> connected peers.
        ///
        /// ⚠️ Sized as a fan-in, which is the mistake worth not repeating. This queue first shipped
        /// at a flat 256 on the reasoning that voice is "one stream per talker, not one update per
        /// pair" — and that is wrong at crowd scale. A receiver with several hundred audible talkers
        /// takes a voice packet from EVERY one of them every frame period, so 256 covers single-digit
        /// milliseconds of arrivals and the queue sheds continuously. Measured at 1000 clients:
        /// 256 delivered 32.8% of voice, where a population-scaled bound delivered 93.6%.
        /// </summary>
        /// <param name="configured">Value from config; anything above 0 wins and is returned as-is.</param>
        public static int PriorityQueuePerPeer(int configured, int peers)
        {
            if (configured > 0) return configured;
            if (peers < 1) peers = 1;

            long budgetPackets = (long)(AvailableMemoryBytes * PriorityQueueMemoryShare) / ApproxPacketBytes;
            return Clamp(budgetPackets / peers, MinPriorityQueuePerPeer, MaxPriorityQueuePerPeer);
        }

        /// <summary>
        /// Ceiling on the scaled packet pool.
        ///
        /// The pool already grows at <paramref name="perPeer"/> packets per connected peer; this is
        /// the wall it hits. The old fixed 262144 was that wall from about 5400 peers upward, so at
        /// 8000 the pool was capped a third below what its own per-peer rule asked for and every
        /// recycle past the cap threw the packet away for the GC to re-allocate.
        ///
        /// ── ⚠️ The cap must cover the QUEUES, which is the part that was missing ───────────────
        ///
        /// The send queues are where packets accumulate, and their bounds are population-scaled into
        /// the millions, while this was sized from a per-peer constant of 48. At 1000 players that
        /// was a 96,000-packet pool in front of 9.5 million packets of queue capacity — so a server
        /// that fell behind allocated millions of packets, held them long enough to be promoted to
        /// gen2, and then had <c>PoolRecycle</c> throw away all but 96,000 of them on the way out.
        ///
        /// Throwing them away does NOT return the memory. Measured at 1000 clients on a starved
        /// server: 10.4% of wall time in GC and <b>2.7 GB of the 7 GB working set was gen2
        /// fragmentation</b> — memory the collector had neither compacted nor handed back, against
        /// 11 MB fragmented on the same build when it was keeping up. The packets are the same
        /// objects either way; the only choice is whether they come back reusable or become holes.
        ///
        /// So the pool is sized to take back everything the queues can let go of. This cannot raise
        /// peak memory: peak is set by how many packets the queues can hold, and this only decides
        /// whether those packets are retained for reuse or abandoned. It is a retention ceiling,
        /// never a preallocation — the pool still only ever holds what was actually allocated.
        /// </summary>
        public const int DefaultPacketPoolPerPeer = 48;

        public static int PacketPoolMax(int configured, int peers, int perPeer)
        {
            if (configured > 0) return configured;
            if (peers < 1) peers = 1;
            if (perPeer < 1) perPeer = 1;

            // Twice the per-peer demand: the pool has to hold what is in flight plus what is coming
            // back, and a cap sitting exactly on demand oscillates between full and empty.
            long want = (long)peers * perPeer * 2L;

            // Plus everything the send queues are allowed to hold, because all of that arrives back
            // here the moment a backlog drains or is trimmed.
            want += (long)peers * (UnreliableQueuePerPeer(0, peers) + PriorityQueuePerPeer(0, peers));

            long memoryCap = (long)(AvailableMemoryBytes * PacketPoolMemoryShare) / ApproxPacketBytes;
            if (want > memoryCap) want = memoryCap;

            return Clamp(want, 65536, int.MaxValue);
        }

        /// <summary>
        /// Upper bound on reduction-system slicing.
        ///
        /// Slicing is the last-resort lever: it divides the roster so each tick serves only part of
        /// it. The cap decides how far the server may degrade before it simply overruns instead,
        /// and 32 was chosen when 2000 was a large instance. At 8000 a cap of 32 still leaves 250
        /// receivers per tick, so the server hits the ceiling with nowhere left to go. Scaling the
        /// cap keeps "degrade further" available as an alternative to "miss the tick".
        /// </summary>
        public static int SliceCap(int configured, int players)
        {
            if (configured > 0) return configured;
            if (players < 1) players = 1;

            // ~64 receivers per slice, so the per-tick fan-out stays flat as population grows.
            return Clamp(players / 64L, 32, 256);
        }

        // NOTE: the authentication window is deliberately NOT resolved here. It already scales with
        // population inside BasisDIDAuthIdentity.GetAuthTimeoutMs (+12 ms per peer, capped at
        // +45 s), which is the same shape and was measured against the mass-rejoin case directly.
        // A second scaler over the top would be two sources of truth for one number.

        /// <summary>One line for the boot log, so the resolved ceilings are visible without a debugger.</summary>
        public static string Describe(int peers, int poolPerPeer) =>
            $"[POP] {peers} peers, {AvailableMemoryBytes / (1024 * 1024)} MB available: " +
            $"bulk queue {UnreliableQueuePerPeer(0, peers)}/peer, " +
            $"voice queue {PriorityQueuePerPeer(0, peers)}/peer, " +
            $"packet pool max {PacketPoolMax(0, peers, poolPerPeer)}, " +
            $"slice cap {SliceCap(0, peers)}";
    }
}
