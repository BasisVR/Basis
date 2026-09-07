namespace Basis.Network.Core
{
    /// <summary>
    /// One subsystem's standing claim on the machine's cores.
    ///
    /// A subsystem does not decide its own width. It states three things it actually knows about
    /// itself — the floor below which it cannot function, the ceiling past which more cores stop
    /// helping it, and how busy it currently is — and reads back whatever the allocator granted.
    /// Nothing else in the server is allowed to size a pool against <c>ProcessorCount</c> directly,
    /// because every pool that does that is sizing against the whole machine while sharing it with
    /// the others.
    ///
    /// <see cref="MaxCores"/> is the load-bearing field and the one that is easy to get wrong. It is
    /// not "how many cores would I like"; it is "how many can I convert into throughput". Demand may
    /// move a lease anywhere inside its own [Min, Max]; it can never argue its way past that
    /// ceiling. It is a delegate rather than a number because ceilings move at runtime — adding a
    /// send socket raises the send phase's immediately.
    ///
    /// <b>But nobody actually knows that number.</b> "Eight workers per send socket" is a real
    /// measurement, taken once, on one machine, at one player count — and the hosts this runs on
    /// range from a few container vCPUs to hundreds of weak cores, where the true figure is
    /// certainly something else. A declared ceiling is a hypothesis, so it is treated as one: it is
    /// the upper bound discovery starts from, and <see cref="AddWork"/> is how a lease earns the
    /// right to be measured instead of trusted. A lease that reports throughput has its real
    /// ceiling found by experiment (see BasisCpuBudget's discovery pass) and
    /// <see cref="EffectiveMax"/> drops to whatever that turns out to be. A lease that reports
    /// nothing keeps its declared number, because there is nothing to measure it against.
    /// </summary>
    public sealed class BasisCoreLease
    {
        internal BasisCoreLease(string name, int minCores, System.Func<int> maxCores, double weight)
        {
            Name = name;
            MinCores = minCores < 1 ? 1 : minCores;
            MaxCores = maxCores;
            Weight = weight <= 0 ? 1.0 : weight;
            _granted = MinCores;
        }

        /// <summary>Subsystem name, for the allocator's diagnostic line.</summary>
        public string Name { get; }

        /// <summary>Workers below which this subsystem cannot do its job at all.</summary>
        public int MinCores { get; }

        /// <summary>Workers past which this subsystem cannot convert cores into throughput.</summary>
        public System.Func<int> MaxCores { get; }

        /// <summary>Resting share of the pool when no lease reports pressure.</summary>
        public double Weight { get; }

        private int _granted;
        private double _demand;

        /// <summary>Cores the allocator has granted. Read this to size a parallel region.</summary>
        public int Granted => System.Threading.Volatile.Read(ref _granted);

        /// <summary>Most recent demand this lease reported, 0..1.</summary>
        public double Demand => System.Threading.Volatile.Read(ref _demand);

        /// <summary>
        /// How saturated this subsystem is right now, 0 (idle) to 1 (cannot keep up). Usually a
        /// pass duration over its budget. Cheap enough to call every pass; the allocator only
        /// reads it on its own cadence.
        /// </summary>
        public void ReportDemand(double demand01)
        {
            if (demand01 < 0) demand01 = 0;
            if (demand01 > 1) demand01 = 1;
            System.Threading.Volatile.Write(ref _demand, demand01);
        }

        internal void Grant(int cores) => System.Threading.Volatile.Write(ref _granted, cores);

        /// <summary>The ceiling this lease declared, floored at <see cref="MinCores"/>.</summary>
        internal int DeclaredMax()
        {
            int max;
            try { max = MaxCores(); }
            catch (System.Exception) { max = MinCores; }
            return max < MinCores ? MinCores : max;
        }

        private long _work;
        private long _busyMicros;
        private int _everReportedWork;

        /// <summary>
        /// Reports a completed pass: how much work it did, and how long it was busy doing it.
        ///
        /// Units are whatever this subsystem counts in — receivers served, peers updated — and
        /// never have to agree between leases, because only the ratio is ever used.
        ///
        /// <b>Both halves are required, and the second is the one that makes this measurable.</b>
        /// Work per second of wall time is not a core-scaling signal: a pass that runs on a fixed
        /// cadence delivers the same amount per second however many workers it has, so a search
        /// steering on it would narrow every pool to its floor and call that the ceiling. Work per
        /// millisecond <em>of the pool's own busy time</em> asks the question that actually matters
        /// — how much can this pool chew through while it is running — and that number does rise
        /// with cores, right up until the point where it stops. Finding that point is the whole
        /// exercise. It also divides out load and cadence, so a probe is not invalidated by the
        /// population changing or the tick re-slicing underneath it.
        ///
        /// Calling this is what opts a lease into having its ceiling measured rather than believed.
        /// A lease that never calls it keeps the ceiling it declared, because there is nothing to
        /// check that number against.
        /// </summary>
        /// <param name="units">Work items this pass completed.</param>
        /// <param name="busyMs">Wall time the pass spent working, excluding any wait before it.</param>
        public void AddWork(long units, double busyMs)
        {
            if (units <= 0 || busyMs <= 0) return;
            System.Threading.Interlocked.Add(ref _work, units);
            System.Threading.Interlocked.Add(ref _busyMicros, (long)(busyMs * 1000.0));
            if (_everReportedWork == 0) System.Threading.Volatile.Write(ref _everReportedWork, 1);
        }

        internal long WorkTotal => System.Threading.Interlocked.Read(ref _work);
        internal long BusyMicrosTotal => System.Threading.Interlocked.Read(ref _busyMicros);
        internal bool ReportsWork => System.Threading.Volatile.Read(ref _everReportedWork) != 0;

        // ── Discovery state, driven by BasisCpuBudget ───────────────────────────
        /// <summary>
        /// Ceiling found by experiment. <c>int.MaxValue</c> until something has been measured, so
        /// the declared ceiling governs from a cold start.
        /// </summary>
        internal int DiscoveredMax = int.MaxValue;

        /// <summary>Grant the discovery pass is holding this lease at, or 0 when it is not.</summary>
        internal int ForcedGrant;

        internal int ProbePhase;          // 0 = idle, 1 = measuring baseline, 2 = measuring narrowed
        internal int ProbeBaselineGrant;
        internal double ProbeBaselineRate;
        internal long ProbeWindowStartTicks;
        internal long ProbeWindowStartWork;
        internal long ProbeWindowStartBusyMicros;
        internal int ProbeCooldownSteps;

        /// <summary>
        /// Demand at the moment a ceiling was accepted. A ceiling is only true for the load it was
        /// measured under, and this is what lets the allocator notice the load has moved on.
        /// </summary>
        internal double DemandAtSettle;

        /// <summary>
        /// The measured ceiling if there is one, otherwise the declared one. Never below
        /// <see cref="MinCores"/>.
        /// </summary>
        public int EffectiveMax()
        {
            int declared = DeclaredMax();
            int discovered = System.Threading.Volatile.Read(ref DiscoveredMax);
            int max = discovered < declared ? discovered : declared;
            return max < MinCores ? MinCores : max;
        }

        /// <summary>
        /// Throws away what was measured, because the thing that set the ceiling has changed —
        /// a send socket added, the population stepped up. The next discovery pass starts again
        /// from the declared bound rather than staying pinned to a number that described a machine
        /// in a different configuration.
        /// </summary>
        public void InvalidateDiscovery()
        {
            System.Threading.Volatile.Write(ref DiscoveredMax, int.MaxValue);
            ProbePhase = 0;
            ForcedGrant = 0;
            ProbeCooldownSteps = 0;
        }

        /// <summary>True once a ceiling has actually been measured for this lease.</summary>
        public bool HasMeasuredCeiling => System.Threading.Volatile.Read(ref DiscoveredMax) != int.MaxValue;
    }

    /// <summary>
    /// Divides the machine's cores between the server's parallel pools.
    ///
    /// The pools do not run in isolation: the reduction system's send phase and the transport's
    /// per-peer pass overlap in time, because the send phase ends by waking the transport while
    /// the next tick is already starting. Each sizing itself against the whole machine therefore
    /// oversubscribes it. Measured at 4000 players on a 32-thread box, both pools sized to the
    /// core count cost 23.6 cores for 634 MB/s with a 153 ms worst pass; holding the send pool to
    /// a quarter and giving the rest to the per-peer pass cost 18.0 cores for 644 MB/s with a
    /// 108 ms worst pass — less CPU, more throughput, lower latency. At 2000 players the same
    /// change was 11.0 cores against 7.8.
    ///
    /// Shares rather than fixed worker counts, so this scales with whatever it is deployed on
    /// instead of encoding one test machine's core count.
    ///
    /// It is a pool, not a split. Subsystems <see cref="Register"/> a <see cref="BasisCoreLease"/>
    /// and read <see cref="BasisCoreLease.Granted"/>; the allocator holds the invariant nobody can
    /// hold alone — <b>the sum of every grant stays inside the machine</b>. That is the whole point.
    /// Two pools each sizing themselves against <c>ProcessorCount</c> are each individually correct
    /// and together oversubscribe the host by 2x, which is the bug this class exists to make
    /// unrepresentable. Adding a third consumer later is a registration, not a re-derivation of
    /// everyone else's arithmetic.
    ///
    /// Grants move with load: each lease reports how saturated it is, and cores flow toward the
    /// subsystem that is behind and away from the one that is coasting, bounded at both ends by
    /// what that subsystem itself declared it can use. Movement is damped, so the split follows a
    /// real load change in about a second without chasing single noisy samples — retuning thread
    /// counts costs more than most misallocations it would be correcting.
    ///
    /// Why the two standing leases get the resting split they do:
    ///  - The send phase is <b>throughput-bound</b> and already rate-limited by the tick budget
    ///    (it sheds by slicing), so extra workers cannot make it deliver sooner — they only cost
    ///    more to schedule. It takes the smaller share.
    ///  - The per-peer pass is <b>latency-bound</b>: its interval is the floor on reliable
    ///    delivery, and a direct-connect handshake has to complete several round trips inside the
    ///    client's 4 s budget. It takes the larger share.
    ///
    /// Not everything here is under the allocator, deliberately. The dedicated single threads
    /// (tick loop, logic loop, receive, stats) are one apiece and not worth modelling, and the
    /// receive path measured at ~2% of server CPU at 4000 players, so sizing it would be policy
    /// without evidence.
    /// </summary>
    public static class BasisCpuBudget
    {
        /// <summary>
        /// Floor for any pool. Below roughly this a parallel region costs more to dispatch than it
        /// saves — but never more workers than the machine has cores, or a small host ends up
        /// oversubscribed by the floors alone before any real work is scheduled.
        /// </summary>
        public static int MinWorkersPerPool => System.Math.Min(4, System.Math.Max(1, TotalCores));

        /// <summary>
        /// Sizes a per-thread-contention structure — stripe tables, shard counts, pools whose only
        /// job is to keep concurrent threads off each other's cache lines.
        ///
        /// Always derived, never a literal. Hosts this runs on range from a couple of container
        /// vCPUs to hundreds of cores, and a constant picked on one of them is wrong everywhere
        /// else in a way that never shows up as an error — just contention nobody attributes. The
        /// result is rounded up to a power of two so callers can index it with a mask.
        /// </summary>
        /// <param name="perCore">Stripes wanted per core.</param>
        /// <param name="min">Floor, so a single-core host still gets a usable table.</param>
        /// <param name="max">Ceiling, so a very large host does not allocate an absurd table.</param>
        public static int ConcurrencyWidth(int perCore = 2, int min = 16, int max = 1024)
        {
            long wanted = (long)TotalCores * (perCore < 1 ? 1 : perCore);
            if (wanted < min) wanted = min;
            if (wanted > max) wanted = max;

            int pow2 = 1;
            while (pow2 < wanted) pow2 <<= 1;
            return pow2;
        }

        /// <summary>
        /// Resting share for the reduction system's send/process/distance phases — where the split
        /// sits when neither pool reports pressure.
        /// </summary>
        public const double ReductionSendWeight = 1.0;

        /// <summary>Resting share for the transport's per-peer update pass.</summary>
        public const double PeerUpdateWeight = 3.0;

        /// <summary>
        /// Hard ceiling on the send pool regardless of how many cores the machine has.
        ///
        /// This pool does not scale with the machine, because what limits it is not cores — it is
        /// the send syscall on a single socket, which is serialised. Measured at 500 players with
        /// identical throughput throughout: 4 workers 6.4 cores, 8 workers 6.6, 16 workers 8.6,
        /// 32 workers 11.0. Past a handful of threads the extra ones queue on the socket and cost
        /// scheduling for nothing.
        ///
        /// So its width is an absolute number, not a share. A share was wrong the moment the
        /// hardware changed: a quarter of a 64-core host is 16 workers, which the curve above puts
        /// about 30% worse than 8. Everything the share model is good for — scaling with the
        /// machine — applies to the other pool, which is genuinely parallel.
        ///
        /// It does scale with one thing, just not cores: the number of sockets. Each additional
        /// socket is another independent send path, so N sockets can absorb N times the concurrent
        /// senders. <see cref="SendSocketCount"/> is set from MultiSocketCount at startup.
        /// </summary>
        public static int MaxReductionSendWorkers =>
            System.Math.Min(TotalCores, WorkersPerSendSocket * System.Math.Max(1, SendSocketCount));

        /// <summary>Threads that can usefully contend on one UDP socket before they just queue.</summary>
        private const int WorkersPerSendSocket = 8;

        /// <summary>
        /// Sockets the send path has available. Set from MultiSocketCount once the transport knows
        /// how many it actually bound — the request can be refused, and asking for eight sockets
        /// while running on one would size the pool for capacity that is not there.
        /// </summary>
        public static int SendSocketCount { get; private set; } = 1;

        /// <summary>
        /// Records how many send sockets were actually bound.
        ///
        /// A change here invalidates anything measured about the send pool: its ceiling is a fact
        /// about how many independent send paths exist, so a socket appearing means the old number
        /// described a machine that no longer exists.
        /// </summary>
        public static void SetSendSocketCount(int bound)
        {
            int next = bound > 0 ? bound : 1;
            if (next == SendSocketCount) return;
            SendSocketCount = next;
            _reductionLease?.InvalidateDiscovery();
        }

        /// <summary>
        /// Ceiling on runtime-added send sockets when the operator has not set one.
        ///
        /// Half the cores. A socket is not free — it is a receive thread that spends its life in a
        /// syscall, so the ceiling has to leave room for the pools that do the actual work; and it
        /// is not expensive either, which is why the answer is a share of the machine rather than a
        /// number. Field measurement on a 128-core host with weak cores found real gains all the
        /// way up to 64 sockets, which is what this returns there. Growth still has to earn each
        /// one against the drop counter; this only bounds how far that can go.
        ///
        /// Floored at four so a small host can still spread across a few flows, but never above the
        /// core count — on a 2-core container, four receive threads is the whole machine spinning
        /// in recvfrom.
        /// </summary>
        public static int AutoMaxSendSockets
        {
            get
            {
                int cores = TotalCores;
                int floor = System.Math.Min(4, cores);
                int wanted = cores / 2;
                if (wanted < floor) wanted = floor;
                if (wanted > cores) wanted = cores;
                return wanted;
            }
        }

        /// <summary>
        /// How hard measured pressure may bend the resting split.
        ///
        /// Pressure is added to the resting weight rather than replacing it, so with no signal the
        /// allocator returns to the measured 1:3 default instead of inventing a split. At gain 4:
        /// both pools idle holds 25/75; the send loop saturated alone moves to 62/38; the peer pass
        /// saturated alone moves to 13/87. Enough authority to matter, not enough to strand a pool
        /// that happens to be briefly quiet.
        /// </summary>
        private const double PressureGain = 4.0;

        /// <summary>
        /// Fraction of the distance to the target moved per rebalance. Rebalancing is frequent, so
        /// each step is small: the split follows a real load change within about a second but does
        /// not chase a single noisy sample. Thrashing thread counts costs more than the
        /// misallocation being corrected.
        /// </summary>
        private const double Damping = 0.25;

        /// <summary>How often live pool load is sampled for the diagnostic line.</summary>
        public const int RebalanceIntervalMs = 100;

        /// <summary>Cores the allocator is dividing up.</summary>
        public static int TotalCores => System.Environment.ProcessorCount;

        private static readonly System.Diagnostics.Process _process =
            System.Diagnostics.Process.GetCurrentProcess();
        private static System.TimeSpan _lastCpuTime;
        private static long _lastCpuSampleTicks;
        private static double _utilization;

        /// <summary>
        /// Fraction of the whole machine this process is using, 0..1, over the last sample.
        ///
        /// This is the input every attempt at automatic sizing was missing. "The pass is slow" does
        /// not mean "give it more workers" — on a host that is already full, widening a pool only
        /// moves contention around, and measured at 2000 players on a saturated 32-thread box it
        /// cost 16.2 to 25.2 cores while leaving the pass just as slow. On a host with cores to
        /// spare the same widening is close to free. Utilisation is what separates those two, and
        /// it is the difference between a 32-thread box at 2000 players and a 128-core box at 4000.
        /// </summary>
        public static double Utilization => _utilization;

        /// <summary>Re-samples <see cref="Utilization"/>. Cheap enough for a ~100 ms cadence.</summary>
        public static double SampleUtilization()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            System.TimeSpan cpu;
            try
            {
                _process.Refresh();
                cpu = _process.TotalProcessorTime;
            }
            catch (System.Exception)
            {
                return _utilization;   // container or platform that will not report it
            }

            if (_lastCpuSampleTicks != 0)
            {
                double wallMs = (now - _lastCpuSampleTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double cpuMs = (cpu - _lastCpuTime).TotalMilliseconds;
                if (wallMs > 0)
                {
                    double u = cpuMs / (wallMs * TotalCores);
                    if (u < 0) u = 0;
                    if (u > 1) u = 1;
                    // Smoothed: a single sample straddling a GC pause is not a busy machine.
                    _utilization = _utilization <= 0 ? u : _utilization * 0.7 + u * 0.3;
                }
            }

            _lastCpuTime = cpu;
            _lastCpuSampleTicks = now;
            return _utilization;
        }

        /// <summary>
        /// Cores held back from the pool for threads that are not pools at all.
        ///
        /// The tick loop, logic loop and stats thread are one apiece; the receive threads are one
        /// per bound socket and are the only ones that reliably pin a core, which is exactly why
        /// adding sockets on a many-weak-core host helps — it moves drain work onto dedicated
        /// threads. Those threads exist whether or not the allocator accounts for them, so handing
        /// their cores out again is how a "correctly sized" pool still ends up fighting for CPU.
        ///
        /// Capped at half the machine so a large socket count cannot starve the pools it was grown
        /// to feed.
        /// </summary>
        private static int ReservedCores
        {
            get
            {
                int cores = TotalCores;
                int reserved = 1 + System.Math.Max(1, SendSocketCount);
                int ceiling = System.Math.Max(1, cores / 2);
                return reserved > ceiling ? ceiling : reserved;
            }
        }

        private static BasisCoreLease[] _leases = System.Array.Empty<BasisCoreLease>();
        private static readonly object _registryLock = new object();

        /// <summary>
        /// Claims a share of the machine for a subsystem. Call once at startup and keep the lease;
        /// read <see cref="BasisCoreLease.Granted"/> wherever a worker count is needed.
        /// </summary>
        /// <param name="name">Subsystem name for diagnostics.</param>
        /// <param name="minCores">Floor below which the subsystem cannot function.</param>
        /// <param name="maxCores">Ceiling past which it cannot convert cores into throughput.</param>
        /// <param name="weight">Resting share, relative to other leases, when nothing is under load.</param>
        public static BasisCoreLease Register(string name, int minCores, System.Func<int> maxCores, double weight)
        {
            var lease = new BasisCoreLease(name, minCores, maxCores, weight);
            lock (_registryLock)
            {
                var next = new BasisCoreLease[_leases.Length + 1];
                System.Array.Copy(_leases, next, _leases.Length);
                next[_leases.Length] = lease;
                _leases = next;
            }
            Rebalance();
            return lease;
        }

        /// <summary>
        /// Gives a lease's cores back to the pool. A subsystem that has stopped should not keep
        /// holding a share of the machine.
        /// </summary>
        public static void Unregister(BasisCoreLease lease)
        {
            if (lease == null) return;
            lock (_registryLock)
            {
                int index = System.Array.IndexOf(_leases, lease);
                if (index < 0) return;

                var next = new BasisCoreLease[_leases.Length - 1];
                System.Array.Copy(_leases, 0, next, 0, index);
                System.Array.Copy(_leases, index + 1, next, index, _leases.Length - index - 1);
                _leases = next;
            }
            // A lease removed mid-probe would otherwise hold the single-prober slot forever.
            if (_probing == lease) _probing = null;
            Rebalance();
        }

        /// <summary>Every registered lease, for diagnostics.</summary>
        public static BasisCoreLease[] Leases => System.Threading.Volatile.Read(ref _leases);

        // ── Ceiling discovery ───────────────────────────────────────────────────

        /// <summary>
        /// How long one grant is held before its throughput is believed.
        ///
        /// Settable only as a test seam (InternalsVisibleTo BasisServerTests) — a narrowing search
        /// takes one window per step, so a test that ran at the shipped length would spend a minute
        /// proving a controller that converges in seconds.
        /// </summary>
        internal static double ProbeWindowMs = 2000;

        /// <summary>
        /// How far throughput may fall before a narrower grant counts as too narrow.
        ///
        /// Measured against the rate at the <em>start</em> of the narrowing sequence, not against
        /// the previous step. Re-baselining every step would let a 2% loss per step compound its
        /// way down to nothing, each individual step looking acceptable.
        /// </summary>
        private const double ProbeTolerance = 0.02;

        /// <summary>
        /// Demand below which a lease is not worth measuring. A pool that is keeping up produces
        /// exactly as much as it is fed no matter how many cores it has, so narrowing it always
        /// looks free — and would latch a ceiling that describes the current load rather than the
        /// machine. Only a subsystem that is actually behind can answer "would more cores help".
        /// </summary>
        private const double ProbeMinDemand = 0.5;

        /// <summary>Rebalance steps between re-measurements. ~60s at a 100ms cadence.</summary>
        private const int ProbeCooldownSteps = 600;

        /// <summary>
        /// How much demand has to climb above where a ceiling was measured before that ceiling is
        /// treated as describing a load the server has left behind.
        /// </summary>
        private const double DemandReopenMargin = 0.15;

        private static BasisCoreLease _probing;

        /// <summary>
        /// Finds each lease's real ceiling by experiment, because no subsystem actually knows its
        /// own. "Eight send workers per socket" is a true measurement of one machine at one moment;
        /// on a host with sixty-four weak cores it is a guess.
        ///
        /// Discovery narrows rather than widens. Taking a core away and checking whether throughput
        /// held is a test that is always affordable — it frees a core instead of needing a spare
        /// one — and it converges on the ceiling from above, so the lease is never parked below its
        /// real width while the search runs. Widening to find the top would need headroom the host
        /// may not have, and would spend the search sitting under-provisioned.
        ///
        /// One lease at a time: two simultaneous probes each change the conditions the other is
        /// measuring under.
        /// </summary>
        private static void DriveDiscovery(BasisCoreLease[] leases)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

            for (int i = 0; i < leases.Length; i++)
            {
                BasisCoreLease lease = leases[i];

                // A ceiling describes the load it was measured under, not the machine. When demand
                // climbs well past that point the old number is stale, and waiting out the cooldown
                // to creep back up one core at a time would take minutes — long enough for a
                // population surge to be strangled by a ceiling measured when the server was half
                // empty. Reopen generously and let the search narrow back down; being briefly too
                // wide costs some scheduling, being too narrow costs delivery.
                if (lease.HasMeasuredCeiling && lease.Demand > lease.DemandAtSettle + DemandReopenMargin)
                {
                    int measured = System.Threading.Volatile.Read(ref lease.DiscoveredMax);
                    System.Threading.Volatile.Write(ref lease.DiscoveredMax, measured + (measured / 2) + 1);
                    lease.DemandAtSettle = lease.Demand;
                    lease.ProbeCooldownSteps = 0;
                }

                if (lease.ProbeCooldownSteps > 0)
                {
                    if (--lease.ProbeCooldownSteps == 0 && lease.HasMeasuredCeiling)
                    {
                        // Routine re-measurement. Nudge the ceiling up a step first, so a settled
                        // lease can rediscover room that opened up without waiting for demand to
                        // spike — otherwise the first narrow result would be permanent.
                        int reopened = System.Threading.Volatile.Read(ref lease.DiscoveredMax);
                        int step = reopened / 8;
                        if (step < 1) step = 1;
                        System.Threading.Volatile.Write(ref lease.DiscoveredMax, reopened + step);
                    }
                    continue;
                }

                if (!lease.ReportsWork) continue;

                if (lease.Demand < ProbeMinDemand)
                {
                    // Went quiet mid-probe: the measurement is no longer about cores. Abandon it
                    // rather than drawing a conclusion from it.
                    if (lease.ProbePhase != 0)
                    {
                        lease.ProbePhase = 0;
                        lease.ForcedGrant = 0;
                        if (_probing == lease) _probing = null;
                    }
                    continue;
                }

                if (_probing != null && _probing != lease) continue;

                switch (lease.ProbePhase)
                {
                    case 0:
                        if (lease.Granted <= lease.MinCores) continue;   // nothing to give back
                        _probing = lease;
                        lease.ProbePhase = 1;
                        StartWindow(lease, now);
                        break;

                    case 1:
                    {
                        if ((now - lease.ProbeWindowStartTicks) / ticksPerMs < ProbeWindowMs) break;

                        lease.ProbeBaselineRate = RateOver(lease);
                        lease.ProbeBaselineGrant = lease.Granted;
                        System.Threading.Volatile.Write(ref lease.DiscoveredMax, lease.ProbeBaselineGrant);

                        if (!TryNarrow(lease, lease.ProbeBaselineGrant, now)) SettleProbe(lease);
                        break;
                    }

                    case 2:
                    {
                        if ((now - lease.ProbeWindowStartTicks) / ticksPerMs < ProbeWindowMs) break;

                        double rate = RateOver(lease);
                        bool held = lease.ProbeBaselineRate <= 0 ||
                                    rate >= lease.ProbeBaselineRate * (1.0 - ProbeTolerance);

                        if (held)
                        {
                            // This width is enough. Record it and keep taking cores away until it
                            // is not — the point of the search is the smallest width that still
                            // delivers, so the rest can go to a lease that will use them.
                            System.Threading.Volatile.Write(ref lease.DiscoveredMax, lease.ForcedGrant);
                            if (!TryNarrow(lease, lease.ForcedGrant, now)) SettleProbe(lease);
                        }
                        else
                        {
                            // Too far. DiscoveredMax still holds the last width that delivered.
                            SettleProbe(lease);
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Work per millisecond the pool was actually busy — not per millisecond of wall clock.
        /// See <see cref="BasisCoreLease.AddWork"/> for why the distinction is the whole signal.
        /// </summary>
        private static double RateOver(BasisCoreLease lease)
        {
            double busyMs = (lease.BusyMicrosTotal - lease.ProbeWindowStartBusyMicros) / 1000.0;
            if (busyMs <= 0) return 0;
            return (lease.WorkTotal - lease.ProbeWindowStartWork) / busyMs;
        }

        /// <summary>Holds the lease one step narrower and starts a fresh measurement window.</summary>
        private static bool TryNarrow(BasisCoreLease lease, int from, long now)
        {
            int step = from / 8;
            if (step < 1) step = 1;
            int narrowed = from - step;
            if (narrowed < lease.MinCores) return false;

            lease.ForcedGrant = narrowed;
            lease.ProbePhase = 2;
            StartWindow(lease, now);
            return true;
        }

        private static void StartWindow(BasisCoreLease lease, long now)
        {
            lease.ProbeWindowStartTicks = now;
            lease.ProbeWindowStartWork = lease.WorkTotal;
            lease.ProbeWindowStartBusyMicros = lease.BusyMicrosTotal;
        }

        private static void SettleProbe(BasisCoreLease lease)
        {
            lease.ProbePhase = 0;
            lease.ForcedGrant = 0;
            lease.ProbeCooldownSteps = ProbeCooldownSteps;
            lease.DemandAtSettle = lease.Demand;
            if (_probing == lease) _probing = null;
        }

        /// <summary>
        /// Hands the machine out to the registered leases. Called on the
        /// <see cref="RebalanceIntervalMs"/> cadence.
        ///
        /// Floors first, then the remainder by weighted demand, then whatever a lease could not
        /// accept because it hit its own ceiling is offered back to the leases that still have
        /// room. That last pass is what makes this a pool rather than a set of fixed shares: the
        /// send phase capping out at eight workers per socket does not leave those cores idle, it
        /// leaves them for the per-peer pass, which is genuinely parallel and will use them.
        /// </summary>
        public static void Rebalance()
        {
            BasisCoreLease[] leases = System.Threading.Volatile.Read(ref _leases);
            int count = leases.Length;
            if (count == 0) return;

            DriveDiscovery(leases);

            int pool = TotalCores - ReservedCores;
            if (pool < count) pool = count;   // every lease gets at least one core

            var want = new int[count];
            var max = new int[count];
            var weight = new double[count];

            int assigned = 0;
            for (int i = 0; i < count; i++)
            {
                // A lease under measurement is pinned to the width being tested — the whole
                // experiment is that it runs at exactly that width for a full window.
                bool forced = leases[i].ForcedGrant > 0;
                max[i] = forced ? leases[i].ForcedGrant : leases[i].EffectiveMax();

                // A lease under measurement is pinned to exactly the width being tested — the
                // experiment is that it runs at that width for a whole window, so it claims its
                // share up front rather than converging on it.
                want[i] = forced ? max[i] : leases[i].MinCores;
                if (want[i] > max[i]) want[i] = max[i];
                assigned += want[i];

                // Demand is added to the resting weight rather than replacing it, so with no signal
                // the allocator returns to the measured split instead of inventing one.
                weight[i] = leases[i].Weight * (1.0 + PressureGain * leases[i].Demand);
            }

            // Floors alone can exceed a small host. Trim the largest claims until they fit rather
            // than letting the sum run over — on a 2-core box the invariant matters most.
            while (assigned > pool)
            {
                int biggest = 0;
                for (int i = 1; i < count; i++)
                    if (want[i] > want[biggest]) biggest = i;
                if (want[biggest] <= 1) break;
                want[biggest]--;
                assigned--;
            }

            int remaining = pool - assigned;

            // Repeat because clamping at a ceiling frees cores that the earlier passes already
            // apportioned; each pass redistributes only what the previous one could not place.
            for (int pass = 0; pass < 8 && remaining > 0; pass++)
            {
                double activeWeight = 0;
                for (int i = 0; i < count; i++)
                    if (want[i] < max[i]) activeWeight += weight[i];
                if (activeWeight <= 0) break;

                int handedOut = 0;
                for (int i = 0; i < count; i++)
                {
                    int room = max[i] - want[i];
                    if (room <= 0) continue;
                    int give = (int)(remaining * (weight[i] / activeWeight));
                    if (give > room) give = room;
                    if (give <= 0) continue;
                    want[i] += give;
                    handedOut += give;
                }

                if (handedOut == 0)
                {
                    // Integer truncation stalled the split with cores still unplaced. Give the
                    // remainder one at a time to the hungriest lease that can still take it, so a
                    // small pool never leaves cores permanently unassigned.
                    int best = -1;
                    for (int i = 0; i < count; i++)
                    {
                        if (want[i] >= max[i]) continue;
                        if (best < 0 || weight[i] > weight[best]) best = i;
                    }
                    if (best < 0) break;
                    want[best]++;
                    handedOut = 1;
                }

                remaining -= handedOut;
            }

            for (int i = 0; i < count; i++)
            {
                int target = want[i];

                // Measurement takes effect at once. Easing into the width under test would spend
                // the first part of the window running at the old one and measure a blend of both.
                if (leases[i].ForcedGrant > 0)
                {
                    leases[i].Grant(target < 1 ? 1 : target);
                    continue;
                }

                int current = leases[i].Granted;
                int next = current + (int)System.Math.Round((target - current) * Damping);

                // Damping must not round to a standstill, or a lease one core away from its target
                // never arrives.
                if (next == current && target != current) next += target > current ? 1 : -1;
                if (next < 1) next = 1;
                if (next > max[i]) next = max[i];
                leases[i].Grant(next);
            }
        }

        /// <summary>
        /// The two standing leases. Registered here rather than by their owners because they are
        /// referenced through <see cref="ReductionSendCap"/> and <see cref="PeerUpdateCap"/> from
        /// code that runs before either subsystem has started.
        /// </summary>
        private static readonly BasisCoreLease _reductionLease =
            Register("reduction-send", MinWorkersPerPool, () => MaxReductionSendWorkers, ReductionSendWeight);

        private static readonly BasisCoreLease _peerUpdateLease =
            Register("peer-update", MinWorkersPerPool, () => TotalCores, PeerUpdateWeight);

        /// <summary>
        /// Reports how far behind each pool is, 0..1. Feeds the next <see cref="Rebalance"/>.
        ///
        /// An earlier version of this steered on load alone and was measured to make things worse:
        /// it read the send phase as saturated and widened it from 8 workers to 13, when the reason
        /// that phase looks busy is that it is bottlenecked on a serialised send syscall. Same
        /// throughput throughout, measured at 500 players: 8 workers 6.6 cores, 16 workers 8.6, 32
        /// workers 11.0. Load says "this pool is busy"; the question is "would this pool go faster
        /// with more cores", and for that pool the answer was no.
        ///
        /// What makes demand safe to act on now is that it can no longer answer that question by
        /// itself. Each lease declares the ceiling past which cores stop helping it — for the send
        /// phase, eight per socket — and demand only moves a lease inside its own bounds. A pool
        /// that is busy for reasons cores cannot fix stays where its ceiling puts it, and the cores
        /// it cannot use go to a lease that can.
        /// </summary>
        public static void ReportPressure(double reductionPressure, double peerUpdatePressure)
        {
            _reductionLease.ReportDemand(reductionPressure);
            _peerUpdateLease.ReportDemand(peerUpdatePressure);
        }

        /// <summary>Worker ceiling for the reduction system's parallel phases.</summary>
        public static int ReductionSendCap => _reductionLease.Granted;

        /// <summary>Worker ceiling for the transport's per-peer update pass.</summary>
        public static int PeerUpdateCap => _peerUpdateLease.Granted;

        /// <summary>The reduction system's lease. Report work to it so its ceiling gets measured.</summary>
        public static BasisCoreLease ReductionSendLease => _reductionLease;

        /// <summary>The transport's per-peer lease. Report work to it so its ceiling gets measured.</summary>
        public static BasisCoreLease PeerUpdateLease => _peerUpdateLease;

        /// <summary>Current grants plus the demand that produced them, for diagnostics.</summary>
        // ASCII only: this goes to the log file, which is not written as UTF-8 everywhere.
        public static string DescribeLive()
        {
            BasisCoreLease[] leases = Leases;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < leases.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(leases[i].Name).Append(' ').Append(leases[i].Granted)
                  .Append(" (load ").Append(leases[i].Demand.ToString("F2")).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>One line describing the pool, for the boot log.</summary>
        // ASCII only: this goes to the log file, which is not written as UTF-8 everywhere.
        public static string Describe()
        {
            BasisCoreLease[] leases = Leases;
            var sb = new System.Text.StringBuilder();
            sb.Append(TotalCores).Append(" cores, ").Append(ReservedCores)
              .Append(" reserved for dedicated threads: ");
            for (int i = 0; i < leases.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(leases[i].Name).Append(' ').Append(leases[i].Granted)
                  .Append('/').Append(leases[i].EffectiveMax());
                // Say which ceilings were measured and which are still the shipped guess — that is
                // the difference between a number that describes this host and one that describes
                // the machine it was tuned on.
                sb.Append(leases[i].HasMeasuredCeiling ? " (measured)" : " (declared)");
            }
            sb.Append(" (floor ").Append(MinWorkersPerPool)
              .Append(" each; grants move with load and pools grow with population up to their grant)");
            return sb.ToString();
        }
    }

    public static class BasisNetworkCommons
    {
        /// <summary>
        /// this is the maximum Connections that can occur under the hood.
        /// </summary>
        public const int MaxConnections = ushort.MaxValue;

        public const int NetworkIntervalPoll = 2;
        public const int PingInterval = 1500;
        public const int ReceivePollingTime = 50000;
        /// <summary>
        /// LiteNetLib packet pool FLOOR. The effective cap scales with population
        /// (PacketPoolSizePerPeer, default 48/peer, capped by PacketPoolSizeMax); this floor only
        /// rules below ~170 players. It used to be 65536, which meant every server — including an
        /// idle or 20-player one — slowly ratcheted its pool up to 65536 x ~1.4 KB ≈ 96 MB of
        /// permanently retained full-MTU buffers (receive rents always upsize a pooled buffer to
        /// MaxPacketSize, and nothing ever trims the pool). 8192 (~12 MB worst case) covers
        /// small-population bursts; the per-peer term takes over before the old figure mattered:
        /// 1000 players still get 48,000 packets.
        /// </summary>
        public const int PacketPoolSize = 8192;

        // ── Single-datagram budget ───────────────────────────────────────────
        /// <summary>
        /// Largest payload a caller may hand to a send that the transport cannot fragment
        /// (see <see cref="CanFragment"/>). Over this, the send THROWS rather than truncating or
        /// dropping, so anything sizing a packet has to check first.
        ///
        /// Deliberately derived from the smallest MTU any peer can be at, not the negotiated one.
        /// A peer starts at its initial MTU and only grows through discovery, and one broadcast
        /// fans out to peers at different points in that — including P2P links that negotiated
        /// separately from the server link. Sizing against a live <c>peer.Mtu</c> would produce a
        /// payload that fits the peer it was measured on and throws on the one still probing.
        /// The slack absorbs the packet header plus any per-message framing above it.
        /// </summary>
        public const int MinimumPeerMtu = 1024;
        /// <summary>Headroom held back from <see cref="MinimumPeerMtu"/> for transport headers and framing.</summary>
        public const int UnfragmentedHeadroom = 36;
        /// <summary>Payload ceiling for one unfragmentable datagram, framing included.</summary>
        public const int MaxUnfragmentedPayload = MinimumPeerMtu - UnfragmentedHeadroom;

        /// <summary>
        /// True if the transport will split an over-MTU payload across datagrams for this delivery
        /// method. Only the two reliable non-sequenced methods can: sequencing is a per-datagram
        /// property, so a fragmented sequenced packet has no coherent meaning and the transport
        /// throws instead.
        /// </summary>
        public static bool CanFragment(DeliveryMethod method) =>
            method == DeliveryMethod.ReliableOrdered || method == DeliveryMethod.ReliableUnordered;

        /// <summary>
        /// when adding a new message we need to increase this
        /// will function up to 64
        /// </summary>
        public const byte TotalChannels = 64;

        // ── Avatar send-interval byte ────────────────────────────────────────
        // The per-receiver interval byte in avatar keyframe/delta frames encodes the send
        // cadence relative to the server's base interval. 0..199 map 1:1 (base+b ms, the
        // pre-v42 range); 200..255 step 12 ms each (base+200 .. base+860 ms) so very distant
        // receivers can drop below the old 3.3 Hz floor while staying inside the receiver's
        // 1 s interpolation-window clamp.
        public const byte AvatarIntervalExtendedStart = 200;
        public const int AvatarIntervalExtendedStepMs = 12;

        public static byte EncodeAvatarIntervalByte(int intervalMs, int baseIntervalMs)
        {
            int rel = intervalMs - baseIntervalMs;
            if (rel <= 0) return 0;
            if (rel < AvatarIntervalExtendedStart) return (byte)rel;
            int steps = (rel - AvatarIntervalExtendedStart + (AvatarIntervalExtendedStepMs >> 1)) / AvatarIntervalExtendedStepMs;
            int maxSteps = byte.MaxValue - AvatarIntervalExtendedStart;
            if (steps > maxSteps) steps = maxSteps;
            return (byte)(AvatarIntervalExtendedStart + steps);
        }

        public static int DecodeAvatarIntervalMs(byte encoded, int baseIntervalMs)
        {
            if (encoded < AvatarIntervalExtendedStart) return baseIntervalMs + encoded;
            return baseIntervalMs + AvatarIntervalExtendedStart
                 + (encoded - AvatarIntervalExtendedStart) * AvatarIntervalExtendedStepMs;
        }

        // ── Connection lifecycle ─────────────────────────────────────────────
        /// <summary>Auth Identity Message</summary>
        public const byte AuthIdentityChannel = 0;
        /// <summary>Player metadata (UUID, display name, permissions)</summary>
        public const byte metaDataChannel = 1;
        /// <summary>Removes a player entity</summary>
        public const byte DisconnectionChannel = 2;

        // ── Voice ────────────────────────────────────────────────────────────
        /// <summary>Spatialized voice data</summary>
        public const byte VoiceChannel = 3;
        /// <summary>Announce mode voice. Non-spatialized audio broadcast to all clients.</summary>
        public const byte AnnounceVoiceChannel = 4;
        /// <summary>Voice recipient list (byte count, ≤255 recipients)</summary>
        public const byte AudioRecipientsChannel = 5;
        /// <summary>Voice recipient list (ushort count, >255 recipients)</summary>
        public const byte AudioRecipientsLargeChannel = 39;
        /// <summary>Spatialized voice data (ushort playerID, for IDs >255)</summary>
        public const byte VoiceLargeChannel = 40;
        /// <summary>Voice excluded list (byte count, ≤255 excluded). Server sends to everyone EXCEPT listed IDs.</summary>
        public const byte AudioRecipientsInvertedChannel = 49;
        /// <summary>Voice excluded list (ushort count, >255 excluded). Server sends to everyone EXCEPT listed IDs.</summary>
        public const byte AudioRecipientsInvertedLargeChannel = 50;
        /// <summary>Voice recipients as a bitfield. Bit at position playerID = recipient.</summary>
        public const byte AudioRecipientsBitfieldChannel = 51;

        // ── Per-quality avatar channels ──────────────────────────────────────
        // Layout: PlayerAvatarVeryLowChannel + quality * 2 + hasAdditional
        //   6  = VeryLow               7  = VeryLow + Additional
        //   8  = Low                   9  = Low + Additional
        //   10 = Medium               11 = Medium + Additional
        //   12 = High                 13 = High + Additional
        public const byte PlayerAvatarVeryLowChannel = 6;
        public const byte PlayerAvatarVeryLowAdditionalChannel = 7;
        public const byte PlayerAvatarLowChannel = 8;
        public const byte PlayerAvatarLowAdditionalChannel = 9;
        public const byte PlayerAvatarMediumChannel = 10;
        public const byte PlayerAvatarMediumAdditionalChannel = 11;
        public const byte PlayerAvatarHighChannel = 12;
        public const byte PlayerAvatarHighAdditionalChannel = 13;

        // ── Avatar management ────────────────────────────────────────────────
        /// <summary>Swap to a different avatar</summary>
        public const byte AvatarChangeMessageChannel = 14;

        // AvatarChangeMessageChannel carries two message kinds, discriminated by a leading byte, because
        // all 64 LiteNetLib channels are assigned and a body-fit update has nowhere else to go that is
        // both server-stored and replayed to late joiners. Both kinds update the same saved record.
        /// <summary>Full avatar change: ClientAvatarChangeMessage / ServerAvatarChangeMessage.</summary>
        public const byte AvatarChangeKindFull = 0;
        /// <summary>Body-fit-only update: ClientBodyFitMessage / ServerBodyFitMessage. No avatar reload.</summary>
        public const byte AvatarChangeKindBodyFit = 1;
        /// <summary>Generic avatar script data</summary>
        public const byte AvatarChannel = 15;

        // ── Player management ────────────────────────────────────────────────
        /// <summary>Create a remote player entity</summary>
        public const byte CreateRemotePlayerChannel = 16;
        /// <summary>Create remote player entities for a newly joined peer</summary>
        public const byte CreateRemotePlayersForNewPeerChannel = 17;
        /// <summary>Chat text messages displayed above player nameplates</summary>
        public const byte ChatChannel = 18;

        // ── Ownership ────────────────────────────────────────────────────────
        /// <summary>Get the current owner of a network object</summary>
        public const byte GetCurrentOwnerRequestChannel = 19;
        /// <summary>Transfer ownership of a network object</summary>
        public const byte ChangeCurrentOwnerRequestChannel = 20;
        /// <summary>Remove current ownership</summary>
        public const byte RemoveCurrentOwnerRequestChannel = 21;

        // ── Net IDs ──────────────────────────────────────────────────────────
        /// <summary>Assign a net id (string to ushort)</summary>
        public const byte netIDAssignChannel = 22;
        /// <summary>Assign an array of net ids (string to ushort)</summary>
        public const byte NetIDAssignsChannel = 23;

        // ── Scene & resources ────────────────────────────────────────────────
        /// <summary>Scene script data</summary>
        public const byte SceneChannel = 24;
        /// <summary>Load a resource (scene, gameobject, script, asset)</summary>
        public const byte LoadResourceChannel = 25;
        /// <summary>Unload a resource</summary>
        public const byte UnloadResourceChannel = 26;
        /// <summary>Client tells server it has finished preloading a resource (ready or failed).</summary>
        public const byte PreloadReadyChannel = 27;
        /// <summary>Server tells all clients to spawn a previously preloaded resource.</summary>
        public const byte SpawnPreloadedChannel = 28;
        /// <summary>
        /// Modify an already-spawned resource's flags (e.g. Static). Client→server request;
        /// the server authorizes (item creator or moderator) then rebroadcasts to all clients.
        /// Id is 55 (not contiguous with the other resource channels) because 29-54 were
        /// already taken when this was added.
        /// </summary>
        public const byte ModifyResourceChannel = 55;

        // ── Content sharing (multiplexed: first payload byte = ContentShareSub_*) ──
        /// <summary>Content share operations. First payload byte selects a ContentShareSub_* sub-type.</summary>
        public const byte ContentShareChannel = 29;
        /// <summary>ContentShareChannel sub-type: drop a content sphere.</summary>
        public const byte ContentShareSub_Drop = 0;
        /// <summary>ContentShareChannel sub-type: remove/cleanup content spheres.</summary>
        public const byte ContentShareSub_Cleanup = 1;

        // ── Avatar delta (server → client only) ──────────────────────────────
        /// <summary>
        /// Server→client avatar delta frames (reclaimed from the former ContentShareCleanupChannel).
        /// Keyframes stay on the per-quality avatar channels (6-13 / 41-48) unchanged; this channel
        /// carries only deltas against each sender's last keyframe. Wire:
        ///   [header:1][playerId:1|2][interval:1][sequence:1][baseSeq:1][delta body]
        /// header bits: quality(2) | hasAdditional&lt;&lt;2 | largeId&lt;&lt;3. Unreliable, server-only.
        /// </summary>
        public const byte DeltaAvatarChannel = 30;

        // ── Server-bound ─────────────────────────────────────────────────────
        /// <summary>Developer hook — data only delivered to the server</summary>
        public const byte ServerBoundChannel = 31;

        // ── Admin ────────────────────────────────────────────────────────────
        // Channels 32 & 33 are free (held the removed server-side database).
        /// <summary>Admin messages from client</summary>
        public const byte AdminChannel = 34;

        // ── Stats, camera & events ───────────────────────────────────────────
        /// <summary>Server statistics</summary>
        public const byte ServerStatisticsChannel = 35;
        /// <summary>PIP camera created/destroyed state (reliable, per-player).</summary>
        public const byte CameraPIPStateChannel = 36;
        /// <summary>PIP camera position updates (sequenced, position only).</summary>
        public const byte CameraPIPPositionChannel = 37;
        /// <summary>
        /// Generic low-priority events channel. The first byte of the payload
        /// identifies the event type (see EventType constants below).
        /// </summary>
        public const byte EventsChannel = 38;

        // ── Event type sub-bytes for EventsChannel ──
        /// <summary>Camera shutter sound fired when a player takes a photo.</summary>
        public const byte EventType_CameraShutterSound = 0;
        /// <summary>Camera countdown started — remote clients replay the tick/shutter timing.</summary>
        public const byte EventType_CameraCountdown = 1;
        /// <summary>
        /// Session-scoped "temp block" notification. Sender tells a specific target peer
        /// that it has (or has not) blocked them locally, so the target can mirror the
        /// block and hide the sender's avatar/audio/nameplate on their client. Not persisted.
        /// </summary>
        public const byte EventType_PlayerTempBlock = 2;
        // Wire (client→server): [eventType:1][intervalMs:2]
        // Wire (server→client): [eventType:1][senderId:2][intervalMs:2]
        public const byte EventType_AvatarRateChange = 3;
        /// <summary>Per-player talk mode for nameplate coloring (Normal/Private/Direct/ThisPerson/Announce).</summary>
        // Wire (client→server): [eventType:1][modeByte:1]
        // Wire (server→client): [eventType:1][senderId:2][modeByte:1]
        public const byte EventType_TalkModeChanged = 4;
        /// <summary>Per-player self-mute state for nameplate coloring.</summary>
        // Wire (client→server): [eventType:1][muted:1]
        // Wire (server→client): [eventType:1][senderId:2][muted:1]
        public const byte EventType_MuteStateChanged = 5;
        /// <summary>Transient chat typing state for a remote player.</summary>
        public const byte EventType_PlayerChatTyping = 6;
        /// <summary>
        /// Client→server one-shot error/exception report (first sighting only). The server
        /// attaches identity from connect metadata and stores it to disk; never rebroadcast.
        /// Wire: [eventType:1][severity:1][lenPrefixed PermissionCompression blob of (system, message, stack)]
        /// </summary>
        public const byte EventType_ErrorReport = 7;
        /// <summary>
        /// Recorder→recordee request to record the recordee's voice. Forwarded to the
        /// target peer only, stamped with the requester's peer id.
        /// </summary>
        // Wire (client→server): [eventType:1][targetId:2]
        // Wire (server→target): [eventType:1][requesterId:2]
        public const byte EventType_VoiceRecordRequest = 8;
        /// <summary>
        /// Recordee→recorder consent decision for a voice-record request
        /// (0 = denied, 1 = granted, 2 = revoked). Forwarded to the target peer only,
        /// stamped with the responder's peer id.
        /// </summary>
        // Wire (client→server): [eventType:1][targetId:2][state:1]
        // Wire (server→target): [eventType:1][responderId:2][state:1]
        public const byte EventType_VoiceRecordConsent = 9;
        /// <summary>
        /// Jiggle grab lifecycle between two players' avatars. Start is re-sent periodically
        /// as a keepalive/late-join heal and is relevance-filtered by the server; Stop and
        /// Deny broadcast. Deny is sent by the grab TARGET to kill a grab everywhere.
        /// </summary>
        // Wire Start (client→server): [eventType:1][op:1][targetId:2][rigIndex:1][pointIndex:2][hand:1][boneNameHash:4][offset:3xhalf]
        // Wire Start (server→clients): [eventType:1][op:1][grabberId:2][targetId:2][rigIndex:1][pointIndex:2][hand:1][boneNameHash:4][offset:3xhalf]
        // Wire Stop (client→server): [eventType:1][op:1][targetId:2][rigIndex:1][pointIndex:2]
        // Wire Stop (server→clients): [eventType:1][op:1][grabberId:2][targetId:2][rigIndex:1][pointIndex:2]
        // Wire Deny (client→server): [eventType:1][op:1][grabberId:2]
        // Wire Deny (server→clients): [eventType:1][op:1][denierId:2][grabberId:2]
        public const byte EventType_JiggleGrab = 10;
        public const byte JiggleGrabOp_Start = 0;
        public const byte JiggleGrabOp_Stop = 1;
        public const byte JiggleGrabOp_Deny = 2;

        // ── Per-quality avatar channels (ushort playerID, for IDs >255) ──
        // Same layout as byte-ID channels: base + quality * 2 + hasAdditional
        //   41 = VeryLow              42 = VeryLow + Additional
        //   43 = Low                  44 = Low + Additional
        //   45 = Medium              46 = Medium + Additional
        //   47 = High                48 = High + Additional
        public const byte PlayerAvatarVeryLowLargeChannel = 41;
        public const byte PlayerAvatarVeryLowAdditionalLargeChannel = 42;
        public const byte PlayerAvatarLowLargeChannel = 43;
        public const byte PlayerAvatarLowAdditionalLargeChannel = 44;
        public const byte PlayerAvatarMediumLargeChannel = 45;
        public const byte PlayerAvatarMediumAdditionalLargeChannel = 46;
        public const byte PlayerAvatarHighLargeChannel = 47;
        public const byte PlayerAvatarHighAdditionalLargeChannel = 48;

        // ── Compressed avatar bundle (server → client only) ──────────────────
        /// <summary>
        /// Server-only outbound channel carrying multiple avatar quality messages
        /// to a single receiver, compressed into one UDP datagram that fits the peer MTU.
        /// Wire format (v53):
        ///   [flags:1][rawLen:2-LE][compressed( group* )]
        ///   group := [origChannel:1][n:1][msgLen:2-LE] x n [bodies]
        /// flags: bits 0-2 codec id, bits 3-7 dictionary generation. Byte 0 was a message count
        /// through v52 that every decoder documented as a hint and none read, so the hybrid codec
        /// costs no wire bytes. A v52 client would ignore the byte and LZ4-decode a Zstd bundle
        /// into garbage rather than rejecting it, which is why v53 is a hard version bump.
        /// Each [origChannel] is the byte-id or ushort-id avatar quality channel the message would
        /// have been sent on individually (channels 6-13 / 41-48), or DeltaAvatarChannel. One
        /// channel byte per RUN, not per entry — the server channel-sorts a receiver's pending
        /// messages first so the runs are long, but decoding does not depend on that.
        /// The DeltaAvatarChannel group's bodies are COLUMN-TRANSPOSED (byte j of every body, then
        /// byte j+1 of every body); no other group is. See BasisAvatarBundleCodec, which is the
        /// shared reader, and BundleCompressionExperiment for why.
        /// Compression is hybrid, chosen per bundle by traffic class: delta-only bundles use LZ4
        /// (LZ4Codec.Encode at LZ4Level.L00_FAST, K4os.Compression.LZ4 1.3.x); bundles carrying a
        /// keyframe/full quality channel use Zstd against a trained dictionary. Zstd measured
        /// 16.7-18.1% smaller on keyframes and 2.8-4.5% LARGER on deltas, which is why the split
        /// exists. See BasisAvatarBundleZstd.
        /// </summary>
        public const byte CompressedAvatarBundleChannel = 52;

        // ── Server-provided default library ──────────────────────────────────
        /// <summary>
        /// Server pushes a list of default library entries (avatars / props / worlds)
        /// to each client on connect. Items live in the client's library only while
        /// connected to that server and are cleared on disconnect.
        /// </summary>
        public const byte ServerLibraryChannel = 53;

        // ── Peer-to-peer direct connection ───────────────────────────────────
        // First byte of payload selects a P2PSub_* sub-type; remaining bytes are
        // the BasisP2PSignalMessage body. Reliable-ordered.
        public const byte P2PChannel = 54;

        public const byte P2PSub_Request = 0;
        public const byte P2PSub_Accept = 1;
        public const byte P2PSub_Decline = 2;
        public const byte P2PSub_Cancel = 3;
        public const byte P2PSub_LinkLost = 4;
        public const byte P2PSub_ServerArmed = 5;
        public const byte P2PSub_LinkUp = 6;
        // Server → both peers once both sides reported LinkUp and it began offloading the
        // pair. Positive confirmation the direct link is fully up; a client that stays
        // Connected without ever seeing this treats its link as partial (server fallback).
        public const byte P2PSub_Offloaded = 7;

        // ── Direct-connect custom data (P2P-first, server fallback) ──────────
        /// <summary>P2P world/prop direct custom data. Frame: [messageIndex:2][payload].</summary>
        public const byte DirectSceneChannel = 56;
        /// <summary>Server relay of a direct-origin scene message (recipients with no direct link).</summary>
        public const byte DirectSceneServerChannel = 57;
        /// <summary>P2P avatar direct custom data. Frame: [messageIndex:1][avatarLinkIndex:1][payload].</summary>
        public const byte DirectAvatarChannel = 58;
        /// <summary>Server relay of a direct-origin avatar message (recipients with no direct link).</summary>
        public const byte DirectAvatarServerChannel = 59;

        // ── Dynamic message registry (subscribe & supply) ────────────────────
        // Channels 60-63 carry the dynamic message layer negotiated at connect.
        // 60 negotiates the registry; 61-63 multiplex plugin payloads keyed by a
        // ushort message id, one channel per delivery semantic. Core channels
        // 0-59 keep their own dedicated channels and ordering streams.
        /// <summary>Registry handshake. First payload byte is a RegistrySub_* sub-type.</summary>
        public const byte RegistryControlChannel = 60;
        /// <summary>Reliable-ordered plugin payloads. Frame: [messageId:2][payload].</summary>
        public const byte PluginReliableChannel = 61;
        /// <summary>Sequenced plugin payloads. Frame: [messageId:2][payload].</summary>
        public const byte PluginSequencedChannel = 62;
        /// <summary>Unreliable plugin payloads. Frame: [messageId:2][payload].</summary>
        public const byte PluginUnreliableChannel = 63;

        /// <summary>RegistryControlChannel sub-type: server to client full descriptor manifest.</summary>
        public const byte RegistrySub_Supply = 0;
        /// <summary>RegistryControlChannel sub-type: client to server list of message ids it can handle.</summary>
        public const byte RegistrySub_Subscribe = 1;

        /// <summary>Maps a plugin DeliveryMethod to its multiplexed channel (61-63). Returns RegistryControlChannel for unmapped values.</summary>
        public static byte GetPluginChannelForDelivery(DeliveryMethod delivery)
        {
            switch (delivery)
            {
                case DeliveryMethod.ReliableOrdered:
                case DeliveryMethod.ReliableUnordered:
                case DeliveryMethod.ReliableSequenced:
                    return PluginReliableChannel;
                case DeliveryMethod.Sequenced:
                    return PluginSequencedChannel;
                case DeliveryMethod.Unreliable:
                    return PluginUnreliableChannel;
                default:
                    return RegistryControlChannel;
            }
        }

        /// <summary>Canonical DeliveryMethod for a multiplexed plugin channel (reverse of GetPluginChannelForDelivery).</summary>
        public static DeliveryMethod GetDeliveryForPluginChannel(byte channel)
        {
            switch (channel)
            {
                case PluginSequencedChannel:
                    return DeliveryMethod.Sequenced;
                case PluginUnreliableChannel:
                    return DeliveryMethod.Unreliable;
                default:
                    return DeliveryMethod.ReliableOrdered;
            }
        }

        /// <summary>True if the channel is one of the multiplexed plugin channels (61-63) carrying a [messageId:2] prefix.</summary>
        public static bool IsPluginChannel(byte channel)
        {
            return channel >= PluginReliableChannel && channel <= PluginUnreliableChannel;
        }

        // ── Server info unconnected query ────────────────────────────────────
        // Out-of-band UDP probe: a client can hit the server's port without
        // authenticating and get back a name/online/max/MOTD payload — same
        // shape as a Minecraft server-list-ping. Travels via LiteNetLib's
        // SendUnconnectedMessage so it never enters the channel/peer pipeline.
        /// <summary>Magic header for the unconnected info query packet from the client.</summary>
        public const uint ServerInfoQueryMagic = 0xBA515101u;
        /// <summary>Magic header for the unconnected info response packet from the server.</summary>
        public const uint ServerInfoResponseMagic = 0xBA515102u;
        /// <summary>Wire-format version for the info query payload. Bump when the layout changes.</summary>
        public const ushort ServerInfoProtocolVersion = 1;
        /// <summary>Hard cap on the server name length the client/server will read or write.</summary>
        public const int ServerInfoNameMaxLength = 64;
        /// <summary>Hard cap on the MOTD length the client/server will read or write.</summary>
        public const int ServerInfoMotdMaxLength = 256;
        /// <summary>
        /// Minimum total request size (in bytes) the server will accept on an
        /// unconnected info query. Clients pad their query up to this size with
        /// zeros so the response is never larger than the request — that removes
        /// the bandwidth-amplification factor that makes UDP discovery protocols
        /// attractive as DDoS reflectors. Worst-case response is ~340 bytes
        /// (full-length name + MOTD), so 384 keeps the amp ratio &lt; 1.
        /// </summary>
        public const int ServerInfoMinRequestBytes = 384;

        // ── Structured connection-reject payload ─────────────────────────────
        // Attached by the server to ConnectionRequest.Reject so the client can react specifically
        // (e.g. an "Update Required" screen, a "Server Full" notice) instead of printing a bare reason
        // string. A 4-byte magic distinguishes a structured reject from a plain reason string — older
        // servers send a bare string (no magic), which the client still surfaces via the legacy path.
        // Wire: [magic:uint][kind:byte][aux0:ushort][aux1:ushort][message:string]
        //   VersionMismatch → aux0 = server protocol version, aux1 = client protocol version.
        //   ServerFull      → aux0/aux1 unused (0); any counts are in the message.
        /// <summary>Marker for a structured reject payload. "BA51 5CE1" ≈ "Basis reject".</summary>
        public const uint RejectMagic = 0xBA515CE1u;
        /// <summary>RejectKind: the client's protocol version does not match the server's.</summary>
        public const byte RejectKind_VersionMismatch = 1;
        /// <summary>RejectKind: the server has reached its player limit.</summary>
        public const byte RejectKind_ServerFull = 2;

        /// <summary>
        /// Channels whose unreliable traffic must not be queued behind, or shed alongside, bulk
        /// avatar state. Indexed by channel number; handed to the transport, which keeps these in a
        /// separate per-peer queue drained ahead of everything else.
        ///
        /// The distinction is whether a newer packet supersedes an older one. An avatar update does:
        /// the position queued behind it is the same player a moment later, so discarding the stale
        /// one costs nothing anybody can see, and that is what makes the bulk queue's drop-oldest
        /// policy correct. Voice is the opposite — every packet is a distinct slice of audio, so a
        /// dropped one is a hole in what somebody said and a late one is no better than a lost one.
        /// Sharing a queue meant voice was shed at the bulk stream's drop rate, which at overload
        /// removed roughly every second voice packet on the instance.
        ///
        /// Only the voice DATA channels belong here. The recipient-list channels are control traffic:
        /// low-rate, and their newest message genuinely does supersede the last, so they are bulk.
        /// </summary>
        public static bool[] BuildPriorityUnreliableChannelMap()
        {
            bool[] map = new bool[TotalChannels];
            map[VoiceChannel] = true;
            map[AnnounceVoiceChannel] = true;
            map[VoiceLargeChannel] = true;
            return map;
        }

        /// <summary>
        /// Maps quality index (0‑3) + additional data presence → byte-ID channel.
        /// </summary>
        public static byte GetPlayerAvatarChannelForQuality(int qualityIndex, bool hasAdditionalData)
        {
            return (byte)(PlayerAvatarVeryLowChannel + qualityIndex * 2 + (hasAdditionalData ? 1 : 0));
        }

        /// <summary>
        /// Maps quality index (0‑3) + additional data presence → ushort-ID channel (for playerIDs >255).
        /// </summary>
        public static byte GetPlayerAvatarLargeChannelForQuality(int qualityIndex, bool hasAdditionalData)
        {
            return (byte)(PlayerAvatarVeryLowLargeChannel + qualityIndex * 2 + (hasAdditionalData ? 1 : 0));
        }

        /// <summary>
        /// Returns true if this channel uses ushort playerID (large variant).
        /// </summary>
        public static bool IsLargePlayerIdChannel(byte channel)
        {
            return channel == VoiceLargeChannel
                || (channel >= PlayerAvatarVeryLowLargeChannel && channel <= PlayerAvatarHighAdditionalLargeChannel);
        }

        /// <summary>
        /// Reverse mapping: channel → quality index (0‑3).
        /// Works for both byte-ID and ushort-ID avatar channels.
        /// </summary>
        public static byte GetQualityFromChannel(byte channel)
        {
            if (channel >= PlayerAvatarVeryLowLargeChannel)
                return (byte)((channel - PlayerAvatarVeryLowLargeChannel) / 2);
            return (byte)((channel - PlayerAvatarVeryLowChannel) / 2);
        }

        /// <summary>
        /// Reverse mapping: channel → has additional data.
        /// Odd offset channels carry additional data, even do not.
        /// Works for both byte-ID and ushort-ID avatar channels.
        /// </summary>
        public static bool ChannelHasAdditionalData(byte channel)
        {
            if (channel >= PlayerAvatarVeryLowLargeChannel)
                return ((channel - PlayerAvatarVeryLowLargeChannel) & 1) == 1;
            return ((channel - PlayerAvatarVeryLowChannel) & 1) == 1;
        }

        // ── DeltaAvatarChannel header helpers ────────────────────────────────
        // The delta channel is a single channel for all quality/id-width/additional combinations;
        // that metadata (which is channel-encoded for keyframes) lives in a 1-byte header instead.
        /// <summary>Packs quality(0-3) + additional + large-id into the DeltaAvatarChannel header byte.</summary>
        public static byte BuildDeltaHeader(int qualityIndex, bool hasAdditionalData, bool largeId)
        {
            return (byte)((qualityIndex & 0x3) | (hasAdditionalData ? 0x4 : 0) | (largeId ? 0x8 : 0));
        }
        /// <summary>Quality index (0-3) from a DeltaAvatarChannel header byte.</summary>
        public static byte DeltaHeaderQuality(byte header) => (byte)(header & 0x3);
        /// <summary>Additional-data flag from a DeltaAvatarChannel header byte.</summary>
        public static bool DeltaHeaderHasAdditionalData(byte header) => (header & 0x4) != 0;
        /// <summary>Large (ushort) player-id flag from a DeltaAvatarChannel header byte.</summary>
        public static bool DeltaHeaderLargeId(byte header) => (header & 0x8) != 0;

        // Control frames on DeltaAvatarChannel (v42): header bit 7 marks a non-delta control
        // message so keyframe recovery is request-driven instead of purely periodic.
        //   client→server KeyframeRequest: [hdr=0x80][senderId:ushort]  "my baseline for this
        //     sender is missing/stale — force my next frame from them to be a keyframe".
        //   server→client UplinkKeyframeRequest: [hdr=0xC0]  "your uplink baseline is missing —
        //     make your next avatar send a full keyframe".
        public const byte DeltaHeaderControlBit = 0x80;
        public const byte DeltaControlKeyframeRequest = 0x80;
        public const byte DeltaControlUplinkKeyframeRequest = 0xC0;
        /// <summary>True when a DeltaAvatarChannel first byte is a control frame, not a delta.</summary>
        public static bool IsDeltaControlHeader(byte header) => (header & DeltaHeaderControlBit) != 0;

        /// <summary>
        /// All 16 per-quality avatar channels (byte-ID + ushort-ID) for aggregate congestion checks.
        /// </summary>
        public static readonly byte[] PlayerAvatarQualityChannels = new byte[]
        {
            PlayerAvatarVeryLowChannel,
            PlayerAvatarVeryLowAdditionalChannel,
            PlayerAvatarLowChannel,
            PlayerAvatarLowAdditionalChannel,
            PlayerAvatarMediumChannel,
            PlayerAvatarMediumAdditionalChannel,
            PlayerAvatarHighChannel,
            PlayerAvatarHighAdditionalChannel,
            PlayerAvatarVeryLowLargeChannel,
            PlayerAvatarVeryLowAdditionalLargeChannel,
            PlayerAvatarLowLargeChannel,
            PlayerAvatarLowAdditionalLargeChannel,
            PlayerAvatarMediumLargeChannel,
            PlayerAvatarMediumAdditionalLargeChannel,
            PlayerAvatarHighLargeChannel,
            PlayerAvatarHighAdditionalLargeChannel,
        };
    }
}
