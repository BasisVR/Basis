using System;
using System.Numerics;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        internal static void TestOnly_PreSerializeFrame(PlayerState state, long publishGen, bool forceKeyframe)
            => PreSerializeFrame(state, publishGen, forceKeyframe);

        internal static int TestOnly_BuildRawForRange(PlayerState state, PendingAvatarSend[] pending, int start, int end)
            => BuildRawForRange(state, pending, start, end);

        internal static void TestOnly_SortPendingByChannel(PlayerState state, PendingAvatarSend[] pending, int count)
            => SortPendingByChannel(state, pending, count);

        /// <summary>
        /// Drives one widening verdict: pretends the send pool went <paramref name="from"/> to
        /// <paramref name="current"/> workers with the whole-pool rate moving as given, and returns
        /// the width left in force.
        /// </summary>
        internal static int TestOnly_ResolveWidenTrial(int from, int current, double rateBefore, double rateAfter, int playerCount)
        {
            _widenTrialFrom = from;
            _aggregateRateAtWiden = rateBefore;
            _aggregateRateEma = rateAfter;
            _passesSinceWiden = WidenTrialPasses;
            parallelOptions.MaxDegreeOfParallelism = current;
            return ResolveWidenTrial(current, playerCount, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        internal static int TestOnly_LearnedWidthCeiling => _learnedWidthCeiling;

        internal static int TestOnly_SendWorkers => parallelOptions.MaxDegreeOfParallelism;

        internal static void TestOnly_ExpireLearnedCeiling(int playerCount)
            => ExpireLearnedCeiling(System.Diagnostics.Stopwatch.GetTimestamp(), playerCount);

        /// <summary>Backdates the learned ceiling past its retry window.</summary>
        internal static void TestOnly_AgeLearnedCeiling()
            => _learnedCeilingTick -= LearnedCeilingRetryTicks + 1;

        internal static void TestOnly_ResetPoolTuning(int workers)
        {
            _widenTrialFrom = 0;
            _aggregateRateAtWiden = 0;
            _passesSinceWiden = 0;
            _aggregateRateEma = 0;
            _learnedWidthCeiling = 0;
            _learnedCeilingPlayers = 0;
            _learnedCeilingSendCap = 0;
            _learnedCeilingTick = 0;
            parallelOptions.MaxDegreeOfParallelism = workers;
        }

        internal static void TestOnly_RunDistanceSweep((int id, PlayerState state)[] roster)
        {
            SnapshotPositions(roster, roster.Length);
            RunDistanceSlice(roster, roster.Length, 0, roster.Length);
        }

        /// <summary>
        /// Drains the removal queue exactly as the tick does, so the stale-slot clearing it performs can
        /// be asserted rather than assumed. That clearing is invisible from every other angle: getting it
        /// wrong does not throw, does not log, and does not fail a round trip - it silently mutes the
        /// next player to be given a recycled id.
        /// </summary>
        internal static void TestOnly_ProcessPendingRemovals() => ProcessPendingRemovals();

#if NET10_0_OR_GREATER
        internal static void TestOnly_EncodeAvatarIntervals(int[] rawIntervals, int baseIntervalMs, int[] encoded, int[] actualMs)
        {
            int width = Vector<int>.Count;
            int[] lanes = new int[width];
            for (int i = 0; i < rawIntervals.Length; i += width)
            {
                int take = Math.Min(width, rawIntervals.Length - i);
                Array.Copy(rawIntervals, i, lanes, 0, take);
                EncodeAvatarIntervals(new Vector<int>(lanes), baseIntervalMs,
                    out Vector<int> encodedLanes, out Vector<int> actualMsLanes);
                for (int lane = 0; lane < take; lane++)
                {
                    encoded[i + lane] = encodedLanes[lane];
                    actualMs[i + lane] = actualMsLanes[lane];
                }
            }
        }
#endif
    }
}
