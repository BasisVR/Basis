using System;
using System.Runtime.CompilerServices;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;

namespace BasisServerTests.Compute
{
    /// <summary>
    /// The per-pair table is the server's dominant memory term: one record per ORDERED PAIR of players,
    /// so a byte added here is a byte times N squared. At the protocol's 65535 peer ceiling each byte of
    /// record width is four and a bit gigabytes. Nothing else in the codebase notices a field being
    /// widened back, which is what these tests are for.
    /// </summary>
    public class PeerTrackingFootprintTests
    {
        /// <summary>
        /// What one receiver stores about one sender, across both halves of the split table.
        /// </summary>
        private static int BytesPerPair =>
            Unsafe.SizeOf<PeerTrackingData>() + sizeof(uint); // + PlayerState.PeerLastSeenGeneration

        [Fact]
        public void ARecordIsTwelveBytes()
        {
            // uint + uint + four byte-wide fields. If this grows, say so out loud: at 4000 players the
            // table is a quarter of a gigabyte and every byte here is another 16 MB.
            Assert.Equal(12, Unsafe.SizeOf<PeerTrackingData>());
        }

        [Fact]
        public void APairCostsSixteenBytesAcrossBothArrays()
        {
            Assert.Equal(16, BytesPerPair);
        }

        [Fact]
        public void TheTableStillFitsFourThousandPlayersInUnderThreeHundredMegabytes()
        {
            // The measured working set at 4000 players was 4.9 GB with the table at 524 MB of it. This
            // pins the halving that took it to 262 MB, in the units the operator actually feels.
            const int players = 4000;
            long capacity = 4096; // arrays grow by doubling, so this is the real allocation at 4000
            long bytes = players * capacity * BytesPerPair;
            Assert.True(bytes < 300L * 1024 * 1024,
                $"per-pair table at {players} players is {bytes / (1024 * 1024)} MB");
        }

        [Fact]
        public void NoBaselineIsNotAGenerationTheSenderCanReach()
        {
            // The sentinel replaced a -1 that no longer fits. It has to be a value the truncated
            // keyframe generation cannot legitimately hold for a very long time, or a receiver holding
            // no keyframe would be told it holds the current one and be sent a delta against nothing.
            Assert.Equal(uint.MaxValue, PeerTrackingData.NoBaseline);

            PeerTrackingData fresh = default;
            Assert.NotEqual(PeerTrackingData.NoBaseline, fresh.BaselineKeyframeGen);
            Assert.False(fresh.HasDistanceCache);
        }

        [Fact]
        public void EveryIntervalByteDecodesToTheTickCountTheRecordUsedToStore()
        {
            // The record's fourth field was exactly this lookup. Removing it is only sound while the
            // table is total over the byte range, so walk all 256 of them.
            int[] table = BasisServerReductionSystemEvents.EnsureIntervalTickTable();
            Assert.Equal(256, table.Length);
            for (int b = 0; b < 256; b++)
            {
                Assert.True(table[b] > 0, $"interval byte {b} decoded to {table[b]} ticks");
            }
        }

        [Fact]
        public void TheIntervalTableIsDerivedFromTheBaseInterval()
        {
            // The table used to be built once inside the distance sweep, so a server that changed its
            // base interval kept pacing every pair off the one it booted with. The cached accessor now
            // rebuilds when the base moves; this pins the derivation it rebuilds from, without writing
            // the configuration static that every other pass in the system reads.
            int[] atFifty = BasisServerReductionSystemEvents.BuildIntervalTickTable(50);
            int[] atHundred = BasisServerReductionSystemEvents.BuildIntervalTickTable(100);

            Assert.True(atHundred[0] > atFifty[0],
                $"table did not follow the base interval: {atFifty[0]} then {atHundred[0]}");
        }
    }
}
