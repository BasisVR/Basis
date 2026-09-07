using Basis.Network.Core;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        private static void BackgroundTickLoop()
        {
            while (!cts.Token.IsCancellationRequested)
            {
                long startTick = Stopwatch.GetTimestamp();

                // One bad tick must not kill the thread. An unhandled throw here (e.g. an
                // edge case during mass connect/recycle) would otherwise stop every future
                // tick and silently freeze all avatar sync until server restart.
                try
                {
                    RunTick(startTick);
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[BSR Tick] Unhandled exception: {ex}");
                }

                // Empty server: park until work arrives instead of spinning at 250Hz.
                // _tickWake is signaled by the first inbound packet (and by Shutdown),
                // so this costs ~0% CPU when idle with no added connect latency.
                if (Volatile.Read(ref _activePlayerCount) == 0)
                {
                    _tickWake.WaitOne(IdleWaitMs);
                    continue;
                }

                // Load-adaptive wait. remainMs is the unused budget = a direct load signal:
                // large (light load) -> block on WaitOne (~0% CPU); small (heavy load, near
                // budget) -> spin the remainder to hit the rate precisely, since the scheduler
                // wakes a yielded thread late under load and the core is busy anyway. remainMs
                // <= 0 (saturated) falls through both branches: no wait, no spin.
                long targetTick = startTick + (long)(intervalMs * MsToTick);
                double remainMs = (targetTick - Stopwatch.GetTimestamp()) / MsToTick;
                if (remainMs > MaxSpinMs)
                {
                    _tickWake.WaitOne((int)Math.Round(remainMs));
                }
                else
                {
                    while (Stopwatch.GetTimestamp() < targetTick)
                    {
                        Thread.SpinWait(20);
                    }
                }
            }
        }

        private static void RunTick(long startTick)
        {
            bool profiling = BSRProfiler.Enabled;
            long phaseTick = profiling ? Stopwatch.GetTimestamp() : 0;

            // Phase 1: Drain
            // Take everything queued since the last tick, removing as we go.
            //
            // This used to double-buffer: clear the back dictionary, swap it in, then read the
            // old one. The swap was free but the clear was not — ConcurrentDictionary.Clear takes
            // every bucket lock and rebuilds the table, so the cost was set by shard count rather
            // than by how much was queued. Draining ~19 messages several hundred times a second
            // made that the most contended thing on the tick thread. Removing exactly the keys we
            // drain is proportional to the traffic and takes one bucket lock at a time; anything
            // written mid-drain lands on the next tick, exactly as it did under the swap.
            _messagesSnapshot.Clear();
            currentMessages.DrainInto(_messagesSnapshot);
            if (profiling) { BSRProfiler.drainTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp(); }

            // Phase 2: Process messages (static delegate avoids closure allocation per tick)
            // Range-partitioned rather than Parallel.ForEach over the List. The default enumerable
            // partitioner chunks with buffering and rebalances poorly at these counts — measured
            // ~300 messages/tick taking 0.95 ms against a ~0.06 ms ideal, i.e. ~15x off, because the
            // per-chunk overhead swamped ~6 us of actual work per message. An index range over the
            // backing list lets the scheduler split evenly with no per-item bookkeeping.
            int messageCount = _messagesSnapshot.Count;
            if (messageCount > 0)
            {
                Parallel.For(0, messageCount, parallelOptions, s_processMessageByIndexAction);
            }
            if (profiling) { BSRProfiler.processTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp(); }

            ProcessPendingRemovals();
            ProcessPendingKeyframeRequests();

            // Phase 2.5: Distance cache update, amortized across the interval instead of one big
            // tick. Doing the whole N^2 matrix on a single tick made that tick cost ~125x its
            // neighbours, which then fed the slice adaptation below and ratcheted it upward.
            long distStart = profiling ? Stopwatch.GetTimestamp() : 0;
            bool didDistanceWork = UpdateDistanceCacheSlice();
            if (profiling && didDistanceWork)
            {
                BSRProfiler.distanceTicks += Stopwatch.GetTimestamp() - distStart;
                phaseTick = Stopwatch.GetTimestamp();
            }

            //Phase 3: Send loop
            long now = Stopwatch.GetTimestamp();
            _lastSendPairs = 0;
            _lastSendWorkers = 0;
            UpdateCommunicationAndDistances(now);
            long sendPhaseTicks = Stopwatch.GetTimestamp() - now;
            if (profiling)
            {
                BSRProfiler.updateTicks += Stopwatch.GetTimestamp() - phaseTick; phaseTick = Stopwatch.GetTimestamp();
            }

            // Pairs served per millisecond this phase was busy — the signal the core allocator uses
            // to find the width past which more send workers stop helping. Timed unconditionally
            // rather than under `profiling`, because the allocator runs on every server and a
            // measurement that only exists when someone is profiling is not one it can steer on.
            double sendPhaseMs = sendPhaseTicks / MsToTick;
            NoteSendPassCost(_lastSendPairs, sendPhaseMs);
            if (_lastSendPairs > 0)
            {
                BasisCpuBudget.ReductionSendLease.AddWork(_lastSendPairs, sendPhaseMs);
            }

            //Phase 4: Network I/O
            BasisNetworkPIPCamera.UpdatePIPPositions(now);
            if (NetworkServer.Server is LNLNetManager lnlReductionServer && lnlReductionServer.manager != null)
            {
                lnlReductionServer.manager.TriggerUpdate();
            }
            if (profiling)
            {
                BSRProfiler.triggerTicks += Stopwatch.GetTimestamp() - phaseTick;
                BSRProfiler.tickCount++;
                BSRProfiler.messagesProcessed += _messagesSnapshot.Count;
            }

            //Tick bookkeeping
            long elapsedTicks = Stopwatch.GetTimestamp() - startTick;
            double elapsedMs = elapsedTicks / MsToTick;

            BSRProfiler.TryPrint();

            // Load controller. It has three levers, escalated in order of how much a player notices:
            //   1. tick PERIOD  — invisible while it stays under the shortest send interval
            //   2. shed tier    — distant players stop updating
            //   3. slicing      — LAST RESORT, cuts everyone's rate uniformly, so it hurts the
            //                     players standing next to you most (their intervals are the short
            //                     ones; a distant player is already on a long interval and unaffected)
            // Recovery unwinds in reverse, restoring visibility before rate.
            //
            // Kept for the log line only — the controller no longer steers on it, see below.
            _tickMsEma = _tickMsEma <= 0.0 ? elapsedMs : (_tickMsEma * 0.9 + elapsedMs * 0.1);

            // ── Control signal: how OFTEN we miss the period, not the average time ──
            // An EMA of tick duration is the wrong signal here. Tick time is heavy-tailed (GC, OS
            // scheduling, bursty inbound), so a handful of slow ticks drag the mean well above the
            // typical tick — measured 18 ms EMA against a 13.2 ms real average at 2000 players. The
            // controller then escalated against load that was not there and kept shedding players
            // while 42% of the box sat idle. Counting overruns is bounded and outlier-insensitive:
            // one 60 ms tick is one overrun, not a permanent shift in the average.
            _tickWindowCount++;
            if (elapsedMs > intervalMs) _tickOverrunCount++;

            // Duty cycle of this pool: work time against the period it is trying to hold. This is
            // the currency the core allocator balances in — see RebalanceCpuBudget.
            _tickDutyEma = _tickDutyEma <= 0.0
                ? elapsedMs / Math.Max(1.0, intervalMs)
                : _tickDutyEma * 0.9 + (elapsedMs / Math.Max(1.0, intervalMs)) * 0.1;
            RebalanceCpuBudget(startTick);

            if (_tickWindowCount >= TickControlWindow)
            {
                _tickOverrunRatio = _tickOverrunCount / (double)_tickWindowCount;
                _tickWindowCount = 0;
                _tickOverrunCount = 0;
                _tickControlReady = true;

                // Sample undeliverable packets over the same window. Normalised per player so one
                // threshold works from 50 players to 8000 — the raw count is meaningless without
                // knowing how many receivers produced it.
                var transport = NetworkServer.Server;
                long droppedNow = transport?.UnreliableDropped ?? 0;
                int population = transport?.ConnectedPeersCount ?? 0;

                if (!_dropBaselineReady)
                {
                    // First window has no previous sample to difference against; seeding it against
                    // 0 would read the whole join burst as one window of catastrophic loss.
                    _lastUnreliableDropped = droppedNow;
                    _dropBaselineReady = true;
                    _dropsPerPlayerWindow = 0;
                }
                else
                {
                    long delta = droppedNow - _lastUnreliableDropped;
                    _lastUnreliableDropped = droppedNow;
                    // The counter is monotonic, but a transport swap or restart resets it.
                    if (delta < 0) delta = 0;
                    _dropsPerPlayerWindow = population > 0 ? delta / (double)population : 0;
                }
            }

            if (!_tickControlReady)
            {
                return;
            }
            _tickControlReady = false;

            // Adapt the tick PERIOD before degrading anything the players can see. Stretching the
            // period costs nothing while it stays under the shortest send interval, and it directly
            // buys back parallel efficiency (bigger batches, fewer barriers). Only once the period is
            // maxed out do we start shedding distant pairs, and only after that do we slice.
            // Either limit being hit means overloaded; BOTH must be clear to give capacity back.
            // Asymmetric on purpose — a server that is delivering everything but missing its period
            // and a server that holds its period only by discarding half its output are both
            // overloaded, and only the first is visible in tick time.
            bool droppingHard = _dropsPerPlayerWindow > DropEscalatePerPlayer;
            bool deliveringCleanly = _dropsPerPlayerWindow < DropRecoverPerPlayer;

            bool overloaded = _tickOverrunRatio > OverrunEscalateRatio || droppingHard;
            bool comfortable = _tickOverrunRatio < OverrunRecoverRatio && deliveringCleanly;

            // Sustained loss is as much an emergency as a collapsing tick: at 8x the escalate
            // threshold the instance is shedding faster than one step per window can correct.
            bool dropPanic = _dropsPerPlayerWindow > DropEscalatePerPlayer * 8.0;
            int escalationSteps = _tickOverrunRatio > OverrunPanicRatio || dropPanic
                ? PanicEscalationSteps
                : 1;
            int previousSliceCount = _sliceCount;
            int previousShedTier = _loadShedTier;
            long previousInterval = intervalMs;

            // The seed value is independent of BSRSMillisecondDefaultInterval, so a configured send
            // interval above 40ms puts the floor above it and neither branch below can ever move it:
            // the speed-up requires intervalMs > floor, and the slow-down only fires under overload.
            // The period then sits under its own floor for the process lifetime, running ticks the
            // send intervals do not justify and paying fork/join on each. Snap up to the floor first.
            if (intervalMs < AdaptiveMinIntervalMs)
            {
                intervalMs = AdaptiveMinIntervalMs;
            }

            if (overloaded && intervalMs < MaxTickIntervalMs)
            {
                intervalMs = Math.Min(MaxTickIntervalMs, intervalMs + 2L * escalationSteps);
            }
            else if (comfortable && intervalMs > AdaptiveMinIntervalMs
                     && _sliceCount == 1 && _loadShedTier == 0)
            {
                // Only tighten the period once nothing is being degraded — otherwise the loop would
                // speed back up while still dropping players, which is the wrong order of recovery.
                //
                // Stops at the period the send intervals actually justify, not at the absolute
                // floor: past that point the extra ticks find nothing due and just pay barriers.
                intervalMs = Math.Max(AdaptiveMinIntervalMs, intervalMs - 1);
            }

            if (overloaded)
            {
                // Shed the furthest pairs FIRST, and only fall back to slicing once even a
                // High-quality-only workload cannot fit the budget. Slicing is the blunt instrument:
                // it cuts everyone's rate uniformly, which hurts nearby players most (their intervals
                // are the short ones), so it must be the last resort rather than the first.
                if (intervalMs < MaxTickIntervalMs)
                {
                    // Period is still stretching — give that a chance before degrading anything.
                }
                else if (LoadSheddingEnabled && _loadShedTier < MaxLoadShedTier)
                {
                    _loadShedTier = Math.Min(MaxLoadShedTier, _loadShedTier + escalationSteps);
                }
                else if (_sliceCount < MaxSliceCount())
                {
                    _sliceCount = Math.Min(MaxSliceCount(), _sliceCount + escalationSteps);
                }
            }
            else if (comfortable)
            {
                // Recover visibility BEFORE rate. Restoring a dropped player at a low update rate is
                // far better than leaving them frozen while everyone else speeds up — and unwinding
                // slicing first proved to be a trap: slicing oscillates under normal jitter, so the
                // tier never got a chance to come back down and the server sat permanently at maximum
                // shedding even once the tick was comfortably inside budget.
                if (_loadShedTier > 0)
                {
                    _loadShedTier--;
                }
                else if (_sliceCount > 1)
                {
                    _sliceCount--;
                }
            }

            // Rate-limited: this is a health signal, not a per-change trace.
            if (WriteLoadLog && (_sliceCount != previousSliceCount || _loadShedTier != previousShedTier || intervalMs != previousInterval))
            {
                long nowLog = Stopwatch.GetTimestamp();
                if (nowLog - _lastSliceLogTick > Stopwatch.Frequency * 5)
                {
                    _lastSliceLogTick = nowLog;
                    // How to read the line is worth saying once per run, not on every change: it was
                    // 118 characters of unchanging prose repeated all through a busy session, more
                    // than the numbers it was explaining.
                    if (!_loadLegendWritten)
                    {
                        _loadLegendWritten = true;
                        BNL.Log("[BSR] Load legend: period alone is harmless; tier > 0 means distant " +
                                "players stop updating; slicing > 1 means everyone's rate is reduced.");
                    }
                    BNL.Log($"[BSR] Load: {_tickOverrunRatio:P0} ticks over budget " +
                            $"(mean {_tickMsEma:F2} ms), period {intervalMs} ms " +
                            $"({1000 / Math.Max(1, intervalMs)} Hz), " +
                            $"tier {_loadShedTier} {LoadShedTierName(_loadShedTier)}, slicing {_sliceCount}");
                }
            }
        }
    }
}
