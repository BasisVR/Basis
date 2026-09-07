using Basis.Network.Core;
using System;
using System.Diagnostics;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        // Tick slicing: only process a subset of receivers each tick to spread the O(NÂ²) work.
        // Adaptive: increases when ticks take too long, decreases when under budget.
        private static int _sliceCount = 1;
        private static int _sliceIndex = 0;

        private static int _senderRotation;

        private static double _tickDutyEma;

        private static long _lastSendPairs;

        private static long _lastRebalanceTick;
        private static long _lastPeersUpdatedTotal;
        private static long _lastPeerBusyMicros;

        private static void RebalanceCpuBudget(long nowTick)
        {
            if (nowTick - _lastRebalanceTick < RebalanceIntervalTicks) return;
            _lastRebalanceTick = nowTick;

            double peerPressure = 0;
            LiteNetLib.NetManager lnl = (NetworkServer.Server as LNLNetManager)?.manager;
            if (lnl != null)
            {
                peerPressure = lnl.PeerUpdatePressure;

                // Differentiate the transport's totals here rather than having it call into the
                // allocator — LiteNetLib is vendored and does not reference Basis.Network.Core, so
                // the counters cross the boundary as plain numbers.
                long peers = lnl.PeersUpdatedTotal;
                long busy = lnl.PeerUpdateBusyMicros;
                if (_lastPeersUpdatedTotal > 0 || _lastPeerBusyMicros > 0)
                {
                    BasisCpuBudget.PeerUpdateLease.AddWork(
                        peers - _lastPeersUpdatedTotal,
                        (busy - _lastPeerBusyMicros) / 1000.0);
                }
                _lastPeersUpdatedTotal = peers;
                _lastPeerBusyMicros = busy;
            }

            BasisCpuBudget.ReportPressure(_tickDutyEma, peerPressure);
            BasisCpuBudget.Rebalance();

            // Tell the transport how full the machine is, so its pool can tell being short of
            // workers apart from being short of cores.
            double util = BasisCpuBudget.SampleUtilization();
            if (lnl != null)
            {
                lnl.MachineUtilization = util;

                // Push the current grant, not just the one from construction. Without this the
                // transport keeps whatever share it was handed at startup and none of the
                // rebalancing above reaches it — the allocator would be moving a number nobody
                // reads. The transport still sizes itself inside this cap by population and by its
                // own probe; the cap is the ceiling that makes the two pools compose.
                lnl.PeerUpdateWorkerCap = BasisCpuBudget.PeerUpdateCap;
                // Send capacity is set by socket count, not core count — tell the budget how many
                // actually bound so the send pool is sized for the paths that exist.
                BasisCpuBudget.SetSendSocketCount(lnl.BoundSendSocketCount);
                MaybeGrowSendSockets(lnl, nowTick, util);
            }

            // Say which pool is hot, periodically. The split is tuned from measurements taken on
            // one machine; on hardware with a different core count or per-core speed this line is
            // what tells an operator whether the shipped default is wrong for them, and which of
            // BSRMaxDegreeOfParallelism / PeerUpdateParallelism to reach for.
            //
            // The delivery side rides the same line rather than a second one: undeliverable packets
            // used to be visible only on the health endpoint, so a server shedding a third of its
            // output looked identical in the log to one running clean. Drop pressure is per player
            // per control window; sustained values above the escalate figure are what drive the
            // shedding shown alongside. Worker counts are what is actually running, not just what
            // each pool is allowed — the gap between those two is what hid a server using a quarter
            // of a large host.
            if (WriteLoadLog && nowTick - _lastPoolLoadLogTick >= PoolLoadLogIntervalTicks)
            {
                _lastPoolLoadLogTick = nowTick;
                int peerWorkers = lnl?.PeerUpdateWorkers ?? 0;
                int pop = NetworkServer.Server?.ConnectedPeersCount ?? 0;
                BNL.Log(
                    $"[CPU/POP] {pop} peers | send {parallelOptions.MaxDegreeOfParallelism}/{BasisCpuBudget.ReductionSendCap} wkr " +
                    $"({_pairsPerWorkerMs:F0} pairs/wkr-ms, budget {_sendBudgetDutyEma:F2}), " +
                    $"peer-upd {peerWorkers}/{BasisCpuBudget.PeerUpdateCap} wkr " +
                    $"(pass {lnl?.PeerUpdatePassMs ?? 0:F1}/{LiteNetLib.NetManager.PeerPassTargetMs:F0} ms), " +
                    $"machine {BasisCpuBudget.Utilization * 100:F0}% of {BasisCpuBudget.TotalCores} cores | " +
                    $"drops {_dropsPerPlayerWindow:F2}/player (esc {DropEscalatePerPlayer:F2}), " +
                    $"slice {_sliceCount}/{MaxSliceCount()}, " +
                    $"tier {_loadShedTier} {LoadShedTierName(_loadShedTier)}, " +
                    $"unrel q {(lnl != null ? lnl.EffectiveUnreliableQueuePerPeer : 0)}/peer");
            }
        }

        private static readonly long RebalanceIntervalTicks =
            (long)(BasisCpuBudget.RebalanceIntervalMs * (Stopwatch.Frequency / 1000.0));

        public static int MaxSendSockets = 8;

        private static long _lastSocketGrowTick;
        private static int _sendPressureStreak;

        // Sustained pressure before another socket is added, in rebalance steps (~100ms each), and
        // the settle period after adding one.
        private const int SendPressureStreakToGrow = 20;   // ~2s of continuous pressure
        private const int SocketGrowSettleMs = 5000;

        private const int SocketProbeWindowMs = 30000;

        private const double SocketProbeMustImproveBy = 0.20;

        private static long _lastDropTotal = -1;
        private static double _dropRateEma;
        private static double _dropRateAtGrow;
        private static long _probeDeadlineTick;
        private static bool _probePending;

        private static bool _socketGrowthHelpless;
        private static double _dropRateAtGiveUp;

        private static void SampleDropRate()
        {
            long total = BasisNetworkUdpDropMonitor.TotalReceiveBufferDrops;
            if (_lastDropTotal < 0) { _lastDropTotal = total; return; }

            double perSecond = (total - _lastDropTotal) * (1000.0 / BasisCpuBudget.RebalanceIntervalMs);
            _lastDropTotal = total;

            // Slow, because the source counter advances in 10s steps: a per-sample rate is mostly
            // zeros with an occasional spike, and the question being asked is about the trend.
            const double Alpha = 0.0033;   // ~30s time constant at a 100ms cadence
            _dropRateEma += (perSecond - _dropRateEma) * Alpha;
        }

        private static void MaybeGrowSendSockets(LiteNetLib.NetManager lnl, long nowTick, double utilization)
        {
            SampleDropRate();

            if (MaxSendSockets <= 1) return;

            if (!lnl.CanAddSendSockets)
            {
                WarnSocketGrowthUnavailable(utilization);
                return;
            }

            // A probe is outstanding: the last socket is on trial, and nothing else gets added
            // until it has answered for itself.
            if (_probePending)
            {
                if (nowTick < _probeDeadlineTick) return;
                _probePending = false;

                double improvement = _dropRateAtGrow > 0
                    ? (_dropRateAtGrow - _dropRateEma) / _dropRateAtGrow
                    : 1.0;

                if (improvement < SocketProbeMustImproveBy)
                {
                    _socketGrowthHelpless = true;
                    _dropRateAtGiveUp = _dropRateEma;
                    BNL.LogWarning(
                        $"[CPU] Added a send socket ({lnl.BoundSendSocketCount} now) and the drop rate did not " +
                        $"improve ({_dropRateAtGrow:F0} -> {_dropRateEma:F0} drops/s). More receive threads are " +
                        $"not the fix -- raise sysctl net.core.rmem_max, or the link itself is saturated. " +
                        $"Socket growth paused.");
                }
                else
                {
                    BNL.Log($"[CPU] Send socket {lnl.BoundSendSocketCount} cut the drop rate " +
                            $"{_dropRateAtGrow:F0} -> {_dropRateEma:F0} drops/s.");
                }
            }

            // Giving up is not permanent — it was a verdict about one load level. If drops get
            // substantially worse than they were when growth was paused, the situation has changed
            // enough to be worth testing again.
            if (_socketGrowthHelpless)
            {
                if (_dropRateEma <= _dropRateAtGiveUp * 2.0 + 1.0) return;
                _socketGrowthHelpless = false;
                BNL.Log($"[CPU] Drop rate rose to {_dropRateEma:F0}/s since socket growth was paused; retrying.");
            }

            if (lnl.BoundSendSocketCount >= MaxSendSockets) return;

            if (!NetworkPathUnderPressure(utilization, out bool receiveDropping))
            {
                _sendPressureStreak = 0;
                return;
            }

            // Drops are already evidence of sustained trouble — the monitor samples over 10s — so
            // they do not have to wait out the streak that send-side pressure does.
            if (receiveDropping) _sendPressureStreak = SendPressureStreakToGrow;

            if (++_sendPressureStreak < SendPressureStreakToGrow) return;
            if (nowTick - _lastSocketGrowTick < SocketGrowSettleTicks) return;

            _sendPressureStreak = 0;
            _lastSocketGrowTick = nowTick;

            if (lnl.TryAddSendSocket())
            {
                BasisCpuBudget.SetSendSocketCount(lnl.BoundSendSocketCount);
                BNL.Log($"[CPU] Send path was the limit — added a socket, now {lnl.BoundSendSocketCount} " +
                        $"(send workers may rise to {BasisCpuBudget.ReductionSendCap}).");

                // Only drop-driven growth gets put on trial. Send-side pressure is judged by the
                // tick making its budget, which the next rebalance already re-reads; drops are the
                // case where the symptom can persist for reasons another thread cannot touch.
                if (receiveDropping)
                {
                    _dropRateAtGrow = _dropRateEma;
                    _probeDeadlineTick = nowTick + SocketProbeWindowTicks;
                    _probePending = true;
                }
            }
        }

        /// <summary>
        /// Whether the evidence for another socket is present. Shared by the grow path and the
        /// cannot-grow warning so the two can never disagree about what pressure looks like — a
        /// warning that fired on a different test than the one that grows sockets would send
        /// operators after the wrong setting.
        /// </summary>
        private static bool NetworkPathUnderPressure(double utilization, out bool receiveDropping)
        {
            // Receive-side saturation is the other reason to add a socket, and it is the one that
            // matters most on hosts with many weak cores: a single receive thread is one core's
            // worth of syscall throughput, and past that the kernel simply discards datagrams. That
            // never appears as high CPU — the thread is pinned either way — so it has to be read
            // from the drop counter. Each extra SO_REUSEPORT socket is another receive thread with
            // the kernel hashing flows across them.
            receiveDropping = _dropRateEma > 0;

            bool sendPoolPinned = parallelOptions.MaxDegreeOfParallelism >= BasisCpuBudget.ReductionSendCap;
            bool tickBehind = _tickOverrunRatio > OverrunEscalateRatio || _sliceCount > 1;
            bool machineHasRoom = utilization > 0 && utilization < 0.80;

            // Drops bypass the machine-has-room test on purpose. Losing inbound packets is worse
            // than being busy, and the fix is a thread that spends its life blocked in recvfrom.
            bool sendPathLimited = sendPoolPinned && tickBehind && machineHasRoom;

            return sendPathLimited || receiveDropping;
        }

        private static bool _growthUnavailableWarned;

        /// <summary>
        /// Says so, once, when the network path is the limit and nothing here can do anything
        /// about it.
        ///
        /// Whether sockets can be added is fixed at bind: SO_REUSEPORT has to be set before the
        /// primary socket binds, so it is only enabled when MultiSocketCount > 1 at Start(). The
        /// default is 1, which means that on a default config every rebalance runs this whole path
        /// and no-ops — silently, while the symptom it exists to fix is live. Nothing in the log
        /// distinguishes that from the pressure never having been there; the only tell is a send
        /// cap that never moves.
        /// </summary>
        private static void WarnSocketGrowthUnavailable(double utilization)
        {
            if (_growthUnavailableWarned) return;
            if (!NetworkPathUnderPressure(utilization, out _)) return;

            _growthUnavailableWarned = true;
            BNL.LogWarning(
                "[CPU] The network path is what limits this server, but no socket can be added: " +
                "SO_REUSEPORT is only enabled when MultiSocketCount > 1 at startup and it is 1, so " +
                "MaxSendSockets has nothing to grow. Set MultiSocketCount in litenetlib.xml (2-4 " +
                "around 1k players, 4-8 around 2k) and restart — it is read once, at bind. Raise " +
                "net.core.rmem_max / net.core.wmem_max too, which Linux clamps the socket buffers " +
                "to. On Windows and macOS there is no SO_REUSEPORT and this cannot be fixed.");
        }

        private static readonly long SocketGrowSettleTicks = (long)(SocketGrowSettleMs * (Stopwatch.Frequency / 1000.0));
        private static readonly long SocketProbeWindowTicks = (long)(SocketProbeWindowMs * (Stopwatch.Frequency / 1000.0));

        private static long _lastPoolLoadLogTick;
        private static readonly long PoolLoadLogIntervalTicks = (long)(15000 * (Stopwatch.Frequency / 1000.0));

        // Distance cache: recalculate quality/interval from distance every N ticks.
        // The fast send loop uses cached values instead of computing distance per pair per tick.
        // At 4ms tick interval, 125 ticks = ~500ms. Players at 6m/s cover 3m in that time,
        // which is within one quality threshold (3m/10m/20m) — acceptable staleness.
        // Cursor for the amortized distance-cache sweep: index of the next receiver to refresh.
        private static int _distanceSliceCursor = 0;
        private static int _distanceTickCounter = 0;
        // Roster the in-progress sweep is running against, pinned at its first slice. A sweep spans
        // DistanceUpdateIntervalTicks ticks and the position arrays are sized from this array's peer
        // ids exactly once, so it must not be re-read mid-sweep. See UpdateDistanceCacheSlice.
        private static (int id, PlayerState state)[] _distanceSweepRoster = Array.Empty<(int, PlayerState)>();
        // Minimum receivers per distance slice. Below roughly this the Parallel.For dispatch costs
        // more than the work it schedules; see UpdateDistanceCacheSlice.
        private const int MinDistanceSliceReceivers = 128;
        // Smoothed tick duration driving the load controller, and a rate limit for its log line.
        private static double _tickMsEma;
        private static long _lastSliceLogTick;
        private static bool _loadLegendWritten;

        // Overrun-ratio control signal. Evaluated once per window rather than per tick so the
        // controller cannot chatter, and so a single slow tick cannot move it.
        // ⚠️ WINDOW LENGTH IS LOAD-BEARING. It bounds how fast the controller can respond, and each
        // evaluation moves exactly one step. At 128 ticks the server needed thousands of ticks to
        // reach the slicing an overloaded instance requires — measured: it sat undegraded while the
        // tick climbed to 339 ms, because 4M pairs/tick piled up faster than it could react. Short
        // enough to respond within a second, long enough that a GC pause cannot flip it.
        private const int TickControlWindow = 16;
        private const double OverrunEscalateRatio = 0.25;   // a quarter of ticks missing = real load
        private const double OverrunRecoverRatio = 0.05;    // almost never missing = give capacity back
        // Above this, the server is not merely behind — it is collapsing, and one step per window is
        // too slow to catch up. Escalation takes several steps at once until it is back in control.
        private const double OverrunPanicRatio = 0.75;
        private const int PanicEscalationSteps = 4;
        // Cap on how far shedding may stretch an interval. 3 doublings = 8x, so a VeryLow pair on a
        // ~500 ms interval bottoms out around 4 s — slow and obviously distant, but still visibly
        // alive. Without a cap the interval would grow until it was indistinguishable from frozen,
        // which is the failure this whole mechanism exists to avoid.
        private const int MaxShedIntervalDoublings = 3;
        private static int _tickWindowCount;
        private static int _tickOverrunCount;
        private static double _tickOverrunRatio;
        private static bool _tickControlReady;

        // ── Second control signal: packets the TRANSPORT could not deliver ────────────────────
        // Tick time alone is not enough, and relying on it alone produced the worst failure this
        // controller has had. When a peer's unreliable queue is over budget the transport discards
        // the oldest packet, and discarding is far cheaper than sending — so the harder the server
        // overshot what it could deliver, the FASTER its ticks became. The controller read that as
        // spare capacity, unwound its shedding, and produced still more. Measured at 2000 players:
        // shed tier sat at "none" and slicing unwound to 4-6 while the transport was destroying
        // 5.2 million packets a second, about half of everything produced.
        //
        // Tick overrun answers "can I compute this?". This answers "can I deliver it?" — a
        // different limit with a different bottleneck, and on a small or slow box it is usually the
        // one that binds first. Having both is what lets one build adapt from a 4-core VPS to a
        // 32-thread host without a tuned constant per machine.
        private static long _lastUnreliableDropped;
        private static bool _dropBaselineReady;
        private static double _dropsPerPlayerWindow;

        // Escalate above one lost packet per player per control window (~0.32 s), recover below an
        // eighth of that. A wide hysteresis band on purpose: a handful of drops during a join burst
        // is normal and must not ratchet the whole instance down.
        private const double DropEscalatePerPlayer = 1.0;
        private const double DropRecoverPerPlayer = 0.125;

        private static int MaxSliceCount() =>
            BasisPopulationScale.SliceCap(
                NetworkServer.Configuration?.BSRMaxSliceCount ?? 0,
                NetworkServer.Server?.ConnectedPeersCount ?? 0);

        // Distance-ordered load shedding. 0 = send everything. 1 = drop VeryLow pairs (the furthest),
        // 2 = also drop Low, 3 = High only. Raised before slicing because slicing degrades everyone
        // uniformly, which costs nearby players the most (see the send loop for the full reasoning).
        private static int _loadShedTier;
        // Capped at 2 deliberately. Tier 3 would mean "High only", i.e. nobody past ~10 m updates at
        // all — measured at 2000 players the controller drove straight to it and the world outside
        // arm's reach froze. 2 still guarantees everyone inside the Medium band (~30 m) keeps
        // updating, which is the population a player can actually perceive moving.
        private const int MaxLoadShedTier = 2;
        public static bool LoadSheddingEnabled = true;

        private static string LoadShedTierName(int tier) => tier switch
        {
            0 => "none",
            1 => "dropping VeryLow (furthest)",
            2 => "dropping VeryLow+Low",
            _ => "High only (nearest)",
        };

        public static bool WriteLoadLog = true;

        public static double TickMsEma => Volatile.Read(ref _tickMsEma);
        public static double TickOverrunRatio => Volatile.Read(ref _tickOverrunRatio);
        public static int LoadShedTier => Volatile.Read(ref _loadShedTier);
        public static int SliceCount => Volatile.Read(ref _sliceCount);
        public static string LoadShedTierLabel => LoadShedTierName(Volatile.Read(ref _loadShedTier));
        public static int DistanceUpdateIntervalTicks = 125;
    }
}
