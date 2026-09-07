using Basis.Network.Core;
using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace BasisNetworkServer
{
    /// <summary>
    /// Hands memory back after the crowd leaves.
    ///
    /// <para>An emptied server allocates nothing — the reduction tick parks on its wake event and
    /// the transport has no peers to service — and collection is demand-driven, so with no
    /// allocation nothing is ever collected and nothing is decommitted. Everything the session
    /// touched simply stays resident. Measured with 1000 players leaving at once: 227 MB managed
    /// heap and 311 MB working set with nobody connected, against a 50 MB empty baseline, flat for
    /// as long as it was watched; one gen2 took the heap to 7.6 MB, so essentially all of it was
    /// garbage that nothing had a reason to ask for.</para>
    ///
    /// <para>The population drop is therefore the trigger. An empty server gets the aggressive,
    /// compacting collection, since a pause costs nobody anything; a server that merely shed most
    /// of its crowd gets a background gen2, which reclaims the same garbage without stopping the
    /// players who stayed.</para>
    /// </summary>
    public static class BasisServerMemoryReclaim
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(60);
        private const int DropDivisor = 4;
        private const double MinimumSecondsBetweenPasses = 120;

#if NET7_0_OR_GREATER
        private const GCCollectionMode FullCollectionMode = GCCollectionMode.Aggressive;
#else
        private const GCCollectionMode FullCollectionMode = GCCollectionMode.Forced;
#endif

        private sealed class RunState { public volatile bool Running = true; }

        private static Thread _worker;
        private static RunState _state;

        private static int _peakSincePass;
        private static DateTime _eligibleSinceUtc = DateTime.MinValue;
        private static DateTime _lastPassUtc = DateTime.MinValue;

        private static long _passes;
        private static long _reclaimedBytes;

        /// <summary>Collections this process has run because the population dropped.</summary>
        public static long Passes => Interlocked.Read(ref _passes);

        /// <summary>Managed bytes those collections freed, summed across passes.</summary>
        public static long ReclaimedBytes => Interlocked.Read(ref _reclaimedBytes);

        public static void Start()
        {
            if (_worker != null) return;
            _peakSincePass = 0;
            _eligibleSinceUtc = DateTime.MinValue;
            _lastPassUtc = DateTime.MinValue;
            RunState state = new RunState();
            _state = state;
            _worker = new Thread(() => Run(state))
            {
                Name = "MemoryReclaim",
                IsBackground = true
            };
            _worker.Start();
        }

        public static void Stop()
        {
            RunState state = _state;
            if (state != null) state.Running = false;
            _state = null;
            _worker = null;
        }

        private static void Run(RunState state)
        {
            while (state.Running)
            {
                try { Sample(); }
                catch (Exception ex) { BNL.LogError($"[MemoryReclaim] {ex.Message}"); }
                Thread.Sleep(SampleInterval);
            }
        }

        private static void Sample()
        {
            Configuration configuration = NetworkServer.Configuration;
            if (configuration == null || !configuration.IdleMemoryReclaimEnabled)
            {
                _eligibleSinceUtc = DateTime.MinValue;
                return;
            }

            int players = CurrentPlayers();
            if (players > _peakSincePass) _peakSincePass = players;

            int minimumPeak = Math.Max(1, configuration.IdleMemoryReclaimMinimumPeak);
            if (_peakSincePass < minimumPeak || players * DropDivisor > _peakSincePass)
            {
                _eligibleSinceUtc = DateTime.MinValue;
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (_eligibleSinceUtc == DateTime.MinValue)
            {
                _eligibleSinceUtc = now;
                return;
            }
            if ((now - _eligibleSinceUtc).TotalSeconds < Math.Max(1, configuration.IdleMemoryReclaimSettleSeconds)) return;
            if (_lastPassUtc != DateTime.MinValue && (now - _lastPassUtc).TotalSeconds < MinimumSecondsBetweenPasses) return;

            Collect(_peakSincePass, players);

            _peakSincePass = NextPeakAfterPass(_peakSincePass, players);
            _eligibleSinceUtc = DateTime.MinValue;
            _lastPassUtc = now;
        }

        /// <summary>
        /// The peak carried into the next eligibility check. Only a pass that actually emptied the
        /// server may lower it — a pass that merely thinned the crowd must not rebase the bar down
        /// to the reduced population, or a population that recedes in stages rather than hitting
        /// zero in one shot can permanently duck under 1/4 of a peak that no longer reflects
        /// reality. That is what let one production spike (2379 -> 24 players, 14.2 GB working set)
        /// sit unreclaimed for the rest of that run.
        /// </summary>
        internal static int NextPeakAfterPass(int peakSincePass, int playersAfterPass) =>
            playersAfterPass == 0 ? 0 : peakSincePass;

        private static void Collect(int peak, int players)
        {
            long heapBefore = GC.GetTotalMemory(false);
            long workingSetBefore = WorkingSetBytes();
            Stopwatch elapsed = Stopwatch.StartNew();

            if (players > 0)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
                elapsed.Stop();
                Interlocked.Increment(ref _passes);
                BNL.Log($"[MemoryReclaim] {peak} -> {players} players: background gen2 requested at " +
                        $"{Megabytes(heapBefore)} MB heap, {Megabytes(workingSetBefore)} MB working set.");
                return;
            }

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, FullCollectionMode, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, FullCollectionMode, blocking: true, compacting: true);
            elapsed.Stop();

            long heapAfter = GC.GetTotalMemory(false);
            long workingSetAfter = WorkingSetBytes();

            Interlocked.Increment(ref _passes);
            if (heapBefore > heapAfter) Interlocked.Add(ref _reclaimedBytes, heapBefore - heapAfter);

            BNL.Log($"[MemoryReclaim] {peak} -> 0 players: heap {Megabytes(heapBefore)} -> {Megabytes(heapAfter)} MB, " +
                    $"working set {Megabytes(workingSetBefore)} -> {Megabytes(workingSetAfter)} MB " +
                    $"in {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
        }

        private static int CurrentPlayers()
        {
            NetManager server = NetworkServer.Server;
            if (server != null) return server.ConnectedPeersCount;
            return NetworkServer.AuthenticatedPeers.Count;
        }

        private static long WorkingSetBytes()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return process.WorkingSet64;
                }
            }
            catch
            {
                return 0;
            }
        }

        private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("F1");
    }
}
