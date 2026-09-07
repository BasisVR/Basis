using System;
using System.Numerics;
using System.Threading.Tasks;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

using Basis.Network.Core;
using Basis.Network.Core.Compute;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        // Cached muscle+tail byte counts for the position-only fast path (skip repack).
        private static readonly int HighMuscleAndTailBytes = MuscleBytes(BitQuality.High) + TailBytes;

        // Generation snapshot: populated once per tick before the O(N²) send loop.
        // Eliminates Interlocked.Read per pair (N² memory fences → N).
        // Pre-allocated to InitialPlayerArrayCapacity to avoid reallocation on early player joins.
        private static long[] _generationSnapshot = new long[InitialPlayerArrayCapacity];

        // Position snapshots in roster order: contiguous arrays for cache-friendly reads in the inner
        // loop, avoiding pointer-chasing through scattered heap PlayerState objects per pair. Roster
        // order rather than peer id because an id-indexed array is full of holes, and a device
        // transfer would pay for every one of them.
        private static float[] _denseX = new float[InitialPlayerArrayCapacity];
        private static float[] _denseY = new float[InitialPlayerArrayCapacity];
        private static float[] _denseZ = new float[InitialPlayerArrayCapacity];
        private static int[] _densePlayerIds = new int[InitialPlayerArrayCapacity];

        private static byte[] _deviceIntervalByte = Array.Empty<byte>();
        private static byte[] _deviceQuality = Array.Empty<byte>();

        // The tick count for every interval byte. This used to be a convenience so the device never had
        // to send the ticks; it is now the only copy, because storing the decoded value beside the byte
        // that decodes to it cost four bytes on every ordered pair of players in the instance.
        private static int[] _intervalTickTable;
        // The base interval the table was built against, so a server reconfigured at runtime rebuilds it
        // rather than pacing every pair off the interval it booted with.
        private static int _intervalTickTableBase = -1;

        /// <summary>
        /// How far back a forced send dates its last-sent stamp. Comfortably longer than the longest
        /// interval the encoding can express, so the interval gate is open however the wrapping tick
        /// counter happens to stand.
        /// </summary>
        internal static readonly uint ForceSendBackdateTicks = (uint)Math.Min(int.MaxValue, (long)(600_000 * MsToTick));

        /// <summary>
        /// The interval-byte to ticks table, built on first use.
        ///
        /// Called once per send pass rather than per pair. It cannot be a field initializer: the base
        /// interval it is derived from is read out of configuration after type initialization, and it
        /// used to be built inside the distance sweep - which the send loop can and does run before.
        /// </summary>
        internal static int[] EnsureIntervalTickTable()
        {
            int[] table = _intervalTickTable;
            if (table != null && _intervalTickTableBase == BSRSMillisecondDefaultInterval)
            {
                return table;
            }

            table = BuildIntervalTickTable(BSRSMillisecondDefaultInterval);
            _intervalTickTableBase = BSRSMillisecondDefaultInterval;
            _intervalTickTable = table;
            return table;
        }

        /// <summary>
        /// The table for a given base interval, touching nothing. Separate from the cached accessor so a
        /// test can ask what a different base would produce without writing the configuration static that
        /// every other pass in the system reads.
        /// </summary>
        internal static int[] BuildIntervalTickTable(int baseIntervalMs)
        {
            int[] table = new int[256];
            for (int b = 0; b < 256; b++)
            {
                table[b] = (int)(BasisNetworkCommons.DecodeAvatarIntervalMs((byte)b, baseIntervalMs) * MsToTick);
            }
            return table;
        }

        /// <summary>
        /// The active roster, ordered by player id.
        ///
        /// The order matters for one reason and it is a big one. Both O(N squared) passes walk the roster
        /// as the inner loop and index each receiver's per-peer table by the sender's ID - so the order
        /// the roster happens to be in is the order those tables are touched in. Unordered, the distance
        /// sweep computes eight lanes of SIMD and then scatters them to eight unrelated places in an
        /// array that is megabytes wide at scale; every write is its own cache line and the vector work
        /// in front of it is wasted. Sorted, the same writes march forward through the table and the pass
        /// streams. The sort itself is O(N log N) and only runs when somebody joins or leaves, against
        /// O(N squared) of work per sweep that it makes sequential.
        ///
        /// Nothing downstream depends on any particular order - the send loop rotates its start point,
        /// the position snapshot is written in this same order, and the device path indexes both by the
        /// same roster position - so this is free to choose the order that the memory likes.
        /// </summary>
        private static (int id, PlayerState state)[] BuildActiveRoster()
        {
            var roster = _activePlayers.ToArray();
            Array.Sort(roster, static (a, b) => a.id.CompareTo(b.id));
            return roster;
        }

        private static IBasisDistanceSolver _distanceSolver;
        private static bool _distanceSolverTried;
        private static bool _distanceSolverVerified;

        public static bool EnableComputeOffload = true;

        /// <summary>Which device to use when the host has more than one. Empty picks the best.</summary>
        public static string ComputeDevice = "";

        /// <summary>
        /// Refresh period to use while a device is carrying the sweep. Read through
        /// <see cref="EffectiveDistanceIntervalTicks"/>, never directly, so that losing the backend
        /// mid-run puts the period back rather than leaving the CPU on a schedule it cannot meet.
        /// </summary>
        public static int ComputeDistanceUpdateIntervalTicks = 32;

        /// <summary>
        /// The period actually in force this tick.
        ///
        /// <para>A cheaper sweep is worth almost nothing as saved CPU and quite a lot as reduced
        /// staleness, so the device's whole return is taken here rather than in cores. It is keyed
        /// off the live solver rather than off configuration, which is what makes the fallback
        /// automatic: the moment the backend is refused, this reads the CPU period again.</para>
        /// </summary>
        private static int EffectiveDistanceIntervalTicks =>
            _distanceSolver != null ? ComputeDistanceUpdateIntervalTicks : DistanceUpdateIntervalTicks;

        /// <summary>Which backend the sweep is running on, for the boot log.</summary>
        public static string DistanceBackend { get; private set; } = "cpu";

        private static bool UpdateDistanceCacheSlice()
        {
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
            if (activeCopy.Length == 0)
            {
                _distanceSliceCursor = 0;
                _distanceSweepRoster = Array.Empty<(int, PlayerState)>();
                return false;
            }

            // Between sweeps: the refresh period is still DistanceUpdateIntervalTicks, we just do the
            // work in chunks rather than all on one tick.
            if (_distanceSliceCursor == 0 && ++_distanceTickCounter < EffectiveDistanceIntervalTicks)
            {
                return false;
            }

            // ⚠️ Pin the roster for the whole sweep instead of re-reading _activePlayersSnapshot each
            // slice. SnapshotPositions runs only on the first slice and writes the position arrays in
            // this array's order, so a roster re-read mid-sweep would leave every later slice pairing
            // receivers against positions that belong to somebody else. Pinning also puts the roster
            // on the same single frame the positions were already documented to use; a mid-sweep
            // joiner is picked up by the next sweep, which is exactly the case the send path's "not
            // yet in the distance cache" fallback interval already covers.
            if (_distanceSliceCursor == 0)
            {
                _distanceSweepRoster = activeCopy;
            }
            activeCopy = _distanceSweepRoster;
            int playerCount = activeCopy.Length;

            // ⚠️ Slice size is bounded BELOW for a reason. This work is a Parallel.For over receivers,
            // so a slice must carry enough receivers to be worth dispatching — sizing it as
            // playerCount/interval gives ~8 at 1000 players, and the parallel setup then costs far
            // more than the distance math it is scheduling. Measured: the naive split made the whole
            // phase ~30x more expensive than the periodic full sweep it replaced. Larger chunks mean
            // the sweep finishes early and simply idles until the next period, which is the point.
            int interval = EffectiveDistanceIntervalTicks;
            int perTick = Math.Max(MinDistanceSliceReceivers,
                (playerCount + interval - 1) / interval);

            if (_distanceSliceCursor >= playerCount)
            {
                _distanceSliceCursor = 0;
            }
            int sliceStart = _distanceSliceCursor;
            int sliceEnd = Math.Min(sliceStart + perTick, playerCount);
            if (sliceEnd >= playerCount)
            {
                // Sweep complete — restart the period counter.
                _distanceSliceCursor = 0;
                _distanceTickCounter = 0;
            }
            else
            {
                _distanceSliceCursor = sliceEnd;
            }

            // Positions are re-snapshotted only when starting a fresh sweep, so every receiver in
            // one sweep is measured against the same frame rather than a moving target.
            if (sliceStart == 0)
            {
                SnapshotPositions(activeCopy, playerCount);
                EnsureDistanceSolver();
            }

            if (!TryRunDistanceSliceOnDevice(activeCopy, playerCount, sliceStart, sliceEnd))
            {
                RunDistanceSlice(activeCopy, playerCount, sliceStart, sliceEnd);
            }
            return true;
        }

        private static void SnapshotPositions((int id, PlayerState state)[] activeCopy, int playerCount)
        {
            // Snapshot positions into contiguous arrays for cache-friendly distance math.
            if (_denseX.Length < playerCount)
            {
                int length = Math.Max(playerCount, _denseX.Length * 2);
                _denseX = new float[length];
                _denseY = new float[length];
                _denseZ = new float[length];
                _densePlayerIds = new int[length];
            }

            for (int i = 0; i < playerCount; i++)
            {
                var (id, state) = activeCopy[i];
                _densePlayerIds[i] = id;
                _denseX[i] = state.Position.x;
                _denseY[i] = state.Position.y;
                _denseZ[i] = state.Position.z;
            }
        }

#if NET10_0_OR_GREATER
        private const int AvatarIntervalExtendedStart = BasisNetworkCommons.AvatarIntervalExtendedStart;
        private const int AvatarIntervalExtendedStepMs = BasisNetworkCommons.AvatarIntervalExtendedStepMs;
        private const int AvatarIntervalMaxSteps = byte.MaxValue - AvatarIntervalExtendedStart;
        private const int AvatarIntervalNumeratorOffset = AvatarIntervalExtendedStart - (AvatarIntervalExtendedStepMs >> 1);
        private const int AvatarIntervalMaxRelativeMs =
            AvatarIntervalNumeratorOffset + ((AvatarIntervalMaxSteps + 1) * AvatarIntervalExtendedStepMs) - 1;
        private const int AvatarIntervalDivideMagic = 0xAAAB;
        private const int AvatarIntervalDivideShift = 19;

        /// <summary>
        /// <c>BasisNetworkCommons.EncodeAvatarIntervalByte</c> + <c>DecodeAvatarIntervalMs</c>, one
        /// vector at a time. A transcription rather than a call because the protocol pair branches
        /// and divides per value; clamping the relative interval up front lets the extended-range
        /// step come out as a multiply-shift with no lane-wise control flow.
        ///
        /// <para><c>DistanceSweepTests.VectorIntervalEncodingMatchesTheProtocol</c> checks this
        /// against the protocol encoder across its whole input domain, so a change to one that is
        /// not made to the other fails there rather than silently shipping two encodings of the same
        /// wire byte.</para>
        /// </summary>
        private static void EncodeAvatarIntervals(Vector<int> rawIntervals, int baseIntervalMs,
            out Vector<int> encodedIntervals, out Vector<int> actualIntervalsMs)
        {
            Vector<int> zero = Vector<int>.Zero;
            Vector<int> relative = Vector.Min(
                Vector.Max(rawIntervals - new Vector<int>(baseIntervalMs), zero),
                new Vector<int>(AvatarIntervalMaxRelativeMs));

            Vector<int> numerator = Vector.Max(relative - new Vector<int>(AvatarIntervalNumeratorOffset), zero);
            Vector<int> steps = Vector.ShiftRightArithmetic(
                numerator * new Vector<int>(AvatarIntervalDivideMagic), AvatarIntervalDivideShift);
            Vector<int> extended = Vector.GreaterThan(relative, new Vector<int>(AvatarIntervalExtendedStart - 1));

            encodedIntervals = Vector.ConditionalSelect(extended,
                new Vector<int>(AvatarIntervalExtendedStart) + steps,
                relative);
            actualIntervalsMs = Vector.ConditionalSelect(extended,
                new Vector<int>(baseIntervalMs + AvatarIntervalExtendedStart) + steps * new Vector<int>(AvatarIntervalExtendedStepMs),
                new Vector<int>(baseIntervalMs) + relative);
        }
#endif

        /// <summary>
        /// Grows a receiver's per-peer arrays to cover <paramref name="jId"/>.
        ///
        /// The two arrays are one logical table split for cache reasons, so they are grown together and
        /// under one lock: a reader that found PeerTracking long enough must never then index a
        /// PeerLastSeenGeneration that is still short. Rare - only when a peer id exceeds the current
        /// capacity - so the lock costs nothing on the hot path.
        /// </summary>
        internal static PeerTrackingData[] GrowPeerTracking(PlayerState state, int jId)
        {
            lock (state)
            {
                if (jId >= state.PeerTracking.Length)
                {
                    int newLen = Math.Max(state.PeerTracking.Length * 2, jId + 1);
                    Array.Resize(ref state.PeerTracking, newLen);
                }
                // Null as well as short: a PlayerState built without the companion array - which
                // several test fixtures do, and which any future construction site could - would
                // otherwise make the send loop skip that receiver entirely and silently. Repairing it
                // here means the two arrays cannot be observed out of step no matter who allocated them.
                if (state.PeerLastSeenGeneration == null)
                {
                    state.PeerLastSeenGeneration = new uint[state.PeerTracking.Length];
                }
                else if (state.PeerLastSeenGeneration.Length < state.PeerTracking.Length)
                {
                    Array.Resize(ref state.PeerLastSeenGeneration, state.PeerTracking.Length);
                }
                return state.PeerTracking;
            }
        }

        private static void RunDistanceSlice((int id, PlayerState state)[] activeCopy, int playerCount, int sliceStart, int sliceEnd)
        {
            int baseIntervalMs = BSRSMillisecondDefaultInterval;
            float baseMultiplier = BSRBaseMultiplier;
            float increaseRate = BSRSIncreaseRate;
            float highDistanceSq = HighDistanceSq;
            float mediumDistanceSq = MediumDistanceSq;
            float lowDistanceSq = LowDistanceSq;
            double msToTick = MsToTick;

            Parallel.For(sliceStart, sliceEnd, parallelOptions, i =>
            {
                int id = _densePlayerIds[i];
                PlayerState state = activeCopy[i].state;
                var tracking = state.PeerTracking;
                if (tracking == null) return;

                float iX = _denseX[i];
                float iY = _denseY[i];
                float iZ = _denseZ[i];

                int index = 0;

#if NET10_0_OR_GREATER
                if (Vector.IsHardwareAccelerated && playerCount >= Vector<float>.Count)
                {
                    int width = Vector<float>.Count;
                    Span<int> encodedIntervals = stackalloc int[width];
                    Span<int> actualIntervalsMs = stackalloc int[width];
                    Span<int> qualityIndices = stackalloc int[width];

                    Vector<float> iXVector = new Vector<float>(iX);
                    Vector<float> iYVector = new Vector<float>(iY);
                    Vector<float> iZVector = new Vector<float>(iZ);
                    Vector<float> baseIntervalVector = new Vector<float>(baseIntervalMs);
                    Vector<float> baseMultiplierVector = new Vector<float>(baseMultiplier);
                    Vector<float> increaseRateVector = new Vector<float>(increaseRate);
                    Vector<float> highDistanceVector = new Vector<float>(highDistanceSq);
                    Vector<float> mediumDistanceVector = new Vector<float>(mediumDistanceSq);
                    Vector<float> lowDistanceVector = new Vector<float>(lowDistanceSq);
                    Vector<int> one = new Vector<int>(1);
                    Vector<int> two = new Vector<int>(2);
                    Vector<int> three = new Vector<int>(3);

                    int vectorEnd = playerCount - width + 1;
                    for (; index < vectorEnd; index += width)
                    {
                        Vector<float> dx = iXVector - new Vector<float>(_denseX, index);
                        Vector<float> dy = iYVector - new Vector<float>(_denseY, index);
                        Vector<float> dz = iZVector - new Vector<float>(_denseZ, index);
                        Vector<float> distancesSq = dx * dx + dy * dy + dz * dz;

                        Vector<int> rawIntervals = Vector.ConvertToInt32(
                            baseIntervalVector * (baseMultiplierVector + distancesSq * increaseRateVector));
                        EncodeAvatarIntervals(rawIntervals, baseIntervalMs,
                            out Vector<int> encoded, out Vector<int> actualMs);
                        encoded.CopyTo(encodedIntervals);
                        actualMs.CopyTo(actualIntervalsMs);

                        Vector.ConditionalSelect(Vector.LessThanOrEqual(distancesSq, highDistanceVector), three,
                            Vector.ConditionalSelect(Vector.LessThanOrEqual(distancesSq, mediumDistanceVector), two,
                                Vector.ConditionalSelect(Vector.LessThanOrEqual(distancesSq, lowDistanceVector), one,
                                    Vector<int>.Zero))).CopyTo(qualityIndices);

                        for (int lane = 0; lane < width; lane++)
                        {
                            int jId = _densePlayerIds[index + lane];
                            if (id == jId) continue;

                            if (jId >= tracking.Length) tracking = GrowPeerTracking(state, jId);

                            tracking[jId].CachedQualityIndex = (byte)qualityIndices[lane];
                            tracking[jId].CachedIntervalByte = (byte)encodedIntervals[lane];
                            tracking[jId].HasDistanceCache = true;
                        }
                    }
                }
#endif

                for (; index < playerCount; index++)
                {
                    int jId = _densePlayerIds[index];
                    if (id == jId) continue;

                    if (jId >= tracking.Length) tracking = GrowPeerTracking(state, jId);

                    float dx = iX - _denseX[index];
                    float dy = iY - _denseY[index];
                    float dz = iZ - _denseZ[index];
                    float distSq = dx * dx + dy * dy + dz * dz;

                    CalculateIntervalFromDistanceSq(distSq, out byte intervalByte, out int actualInterval);

                    tracking[jId].CachedQualityIndex = (byte)GetQualityIndex(distSq);
                    tracking[jId].CachedIntervalByte = intervalByte;
                    tracking[jId].HasDistanceCache = true;
                }
            });
        }

        /// <summary>
        /// Resolves the compute backend once, on the first sweep that has a roster to measure.
        /// Deferred to first use because loading it is the expensive part, and a server that never
        /// reaches a population worth sweeping should never pay it.
        /// </summary>
        private static void EnsureDistanceSolver()
        {
            if (_distanceSolverTried) return;
            _distanceSolverTried = true;

            EnsureIntervalTickTable();

            if (!EnableComputeOffload)
            {
                DistanceBackend = "cpu (offload disabled)";
                return;
            }

            _distanceSolver = BasisComputeBackend.TryLoadDistanceSolver(BSRSMillisecondDefaultInterval, ComputeDevice);
            if (_distanceSolver == null)
            {
                DistanceBackend = "cpu";
                BNL.Log("[BSR] Distance sweep on the CPU - " + BasisComputeBackend.Status + ".");
                return;
            }

            DistanceBackend = _distanceSolver.Backend;
            BNL.Log("[BSR] Distance sweep offloaded to " + BasisComputeBackend.Status +
                    ". Refresh period " + DistanceUpdateIntervalTicks + " -> " +
                    ComputeDistanceUpdateIntervalTicks + " ticks while it holds. It is checked against " +
                    "the CPU on its first slice and dropped if it disagrees.");

            string devices = BasisComputeBackend.DescribeDevices();
            if (devices != null && devices.IndexOf("[1]", StringComparison.Ordinal) >= 0)
            {
                BNL.Log("[BSR] This host has more than one compute device. Set ComputeDevice in config.xml "
                        + "to an index or a name to choose:\n" + devices.TrimEnd());
            }
        }

        private static BasisDistanceSolveParameters CurrentSolveParameters()
        {
            return new BasisDistanceSolveParameters
            {
                HighDistanceSq = HighDistanceSq,
                MediumDistanceSq = MediumDistanceSq,
                LowDistanceSq = LowDistanceSq,
                BaseMultiplier = BSRBaseMultiplier,
                IncreaseRate = BSRSIncreaseRate,
                BaseIntervalMs = BSRSMillisecondDefaultInterval,
            };
        }

        /// <summary>
        /// Runs one slice on the device and writes the result into the same cache the CPU sweep
        /// writes. Returns false when the device could not be used, so the caller runs the CPU
        /// sweep and no slice is ever left half-updated.
        /// </summary>
        private static bool TryRunDistanceSliceOnDevice((int id, PlayerState state)[] activeCopy, int playerCount, int sliceStart, int sliceEnd)
        {
            IBasisDistanceSolver solver = _distanceSolver;
            if (solver == null) return false;

            int sliceLength = sliceEnd - sliceStart;
            if (sliceLength <= 0) return false;

            long resultLength = (long)sliceLength * playerCount;
            if (resultLength > int.MaxValue) return false;

            if (_deviceIntervalByte.Length < resultLength)
            {
                _deviceIntervalByte = new byte[resultLength];
                _deviceQuality = new byte[resultLength];
            }

            BasisDistanceSolveRequest request = new BasisDistanceSolveRequest
            {
                PosX = _denseX,
                PosY = _denseY,
                PosZ = _denseZ,
                PlayerCount = playerCount,
                SliceStart = sliceStart,
                SliceEnd = sliceEnd,
                Parameters = CurrentSolveParameters(),
            };

            try
            {
                solver.Solve(ref request, _deviceIntervalByte, _deviceQuality);
            }
            catch (Exception ex)
            {
                BNL.LogWarning("[BSR] The compute backend failed mid-sweep (" + ex.GetType().Name + ": " + ex.Message +
                               "). Dropping back to the CPU sweep for the rest of this process.");
                DisableDistanceSolver();
                return false;
            }

            if (!_distanceSolverVerified && !VerifyDeviceAgainstCpu(playerCount, sliceStart, sliceEnd))
            {
                return false;
            }

            ScatterDeviceResults(activeCopy, playerCount, sliceStart, sliceEnd);
            return true;
        }

        /// <summary>
        /// Checks the device against the CPU on the first slice it produces, and refuses it if they
        /// disagree on a quality tier.
        ///
        /// <para>The offload is on by default, so something has to establish that this driver on
        /// this card agrees with the arithmetic the rest of the server assumes. A tier disagreement
        /// means a receiver is sent the wrong avatar detail, which is a visible defect. The interval
        /// byte is allowed to differ by one step: a device that contracts the three squared terms
        /// into fused multiply-adds rounds once where the CPU rounds three times, and what that
        /// changes is a cache entry the next sweep overwrites.</para>
        /// </summary>
        private static bool VerifyDeviceAgainstCpu(int playerCount, int sliceStart, int sliceEnd)
        {
            int checkedPairs = 0;
            int intervalDrift = 0;
            int receiverStep = Math.Max(1, (sliceEnd - sliceStart) / 32);
            int senderStep = Math.Max(1, playerCount / 64);

            for (int s = sliceStart; s < sliceEnd; s += receiverStep)
            {
                int local = s - sliceStart;
                float iX = _denseX[s], iY = _denseY[s], iZ = _denseZ[s];
                long baseOffset = (long)local * playerCount;

                for (int j = 0; j < playerCount; j += senderStep)
                {
                    if (s == j) continue;

                    float dx = iX - _denseX[j];
                    float dy = iY - _denseY[j];
                    float dz = iZ - _denseZ[j];
                    float distSq = dx * dx + dy * dy + dz * dz;

                    CalculateIntervalFromDistanceSq(distSq, out byte expectedByte, out _);
                    byte expectedQuality = (byte)GetQualityIndex(distSq);

                    checkedPairs++;
                    byte deviceQuality = _deviceQuality[baseOffset + j];
                    if (deviceQuality != expectedQuality)
                    {
                        BNL.LogWarning("[BSR] The compute backend disagrees with the CPU on a quality tier (device " +
                                       deviceQuality + ", cpu " + expectedQuality + "). Refusing the offload; the " +
                                       "sweep stays on the CPU.");
                        DisableDistanceSolver();
                        return false;
                    }

                    int difference = _deviceIntervalByte[baseOffset + j] - expectedByte;
                    if (difference < -1 || difference > 1) intervalDrift++;
                }
            }

            if (intervalDrift > 0)
            {
                BNL.LogWarning("[BSR] The compute backend's interval byte differs by more than one step on " +
                               intervalDrift + " of " + checkedPairs + " sampled pairs. Refusing the offload.");
                DisableDistanceSolver();
                return false;
            }

            _distanceSolverVerified = true;
            BNL.Log("[BSR] Compute backend agrees with the CPU over " + checkedPairs + " sampled pairs.");
            return true;
        }

        private static void DisableDistanceSolver()
        {
            try
            {
                if (_distanceSolver != null) _distanceSolver.Dispose();
            }
            catch (Exception)
            {
                // A backend that is already broken enough to be refused is allowed to fail on the
                // way out too; there is nothing left to salvage and the CPU path does not care.
            }
            _distanceSolver = null;
            DistanceBackend = "cpu (backend refused)";
            BNL.Log("[BSR] Refresh period back to " + DistanceUpdateIntervalTicks +
                    " ticks now the sweep is on the CPU again.");
        }

        private static void ScatterDeviceResults((int id, PlayerState state)[] activeCopy, int playerCount, int sliceStart, int sliceEnd)
        {
            int[] tickTable = _intervalTickTable;

            Parallel.For(sliceStart, sliceEnd, parallelOptions, i =>
            {
                var (id, state) = activeCopy[i];
                var tracking = state.PeerTracking;
                if (tracking == null) return;

                long baseOffset = (long)(i - sliceStart) * playerCount;

                for (int index = 0; index < playerCount; index++)
                {
                    int jId = activeCopy[index].id;
                    if (id == jId) continue;

                    if (jId >= tracking.Length)
                    {
                        lock (state)
                        {
                            if (jId >= state.PeerTracking.Length)
                            {
                                int newLen = Math.Max(state.PeerTracking.Length * 2, jId + 1);
                                Array.Resize(ref state.PeerTracking, newLen);
                            }
                            tracking = state.PeerTracking;
                        }
                    }

                    byte encoded = _deviceIntervalByte[baseOffset + index];
                    tracking[jId].CachedQualityIndex = _deviceQuality[baseOffset + index];
                    tracking[jId].CachedIntervalByte = encoded;
                    tracking[jId].HasDistanceCache = true;
                }
            });
        }

    }
}
