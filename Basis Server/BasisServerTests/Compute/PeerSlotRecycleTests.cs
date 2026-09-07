using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;

namespace BasisServerTests.Compute
{
    /// <summary>
    /// LiteNetLib hands out recycled peer ids, so the slot a departing player leaves behind in every
    /// other player's per-peer table is handed to whoever joins next. If the departing player's state is
    /// not cleared out of it, the new player inherits it.
    ///
    /// The failure is completely silent. A stale last-seen generation is HIGHER than anything the new
    /// player has published, so the send loop's new-data gate rejects every frame they send and they are
    /// simply invisible to everyone who was already in the instance - no exception, no log, no failed
    /// round trip. These tests exist because the clearing pass is the only thing standing between that
    /// and a live server, and because splitting the table in two is exactly the sort of change that
    /// leaves half of it behind. Verified against the un-split code: all three fail without it.
    ///
    /// Deliberately built on receivers the test owns rather than on the shared player table, so nothing
    /// here depends on what the tick thread or another fixture is doing to the statics.
    /// </summary>
    public class PeerSlotRecycleTests
    {
        private const int Leaver = 7;

        private static PlayerState NewTrackedPlayer(int capacity)
        {
            return new PlayerState
            {
                IsActive = true,
                PeerTracking = new PeerTrackingData[capacity],
                PeerLastSeenGeneration = new uint[capacity],
            };
        }

        [Fact]
        public void ClearingADepartedPeerClearsBothHalvesOfTheSlot()
        {
            PlayerState watcher = NewTrackedPlayer(16);
            watcher.PeerLastSeenGeneration[Leaver] = 900_000;
            watcher.PeerTracking[Leaver].BaselineKeyframeGen = 4242;
            watcher.PeerTracking[Leaver].BaselineQuality = 3;
            watcher.PeerTracking[Leaver].LastSentTime = 12345;
            watcher.PeerTracking[Leaver].HasDistanceCache = true;

            BasisServerReductionSystemEvents.ClearDepartedPeerSlots(watcher, stackalloc int[] { Leaver });

            // The generation is the one that matters: left behind, the next holder of this id is muted.
            Assert.Equal(0u, watcher.PeerLastSeenGeneration[Leaver]);
            Assert.Equal(default, watcher.PeerTracking[Leaver]);
        }

        [Fact]
        public void APlayerOnARecycledIdIsNotMutedByTheLastHoldersGeneration()
        {
            PlayerState watcher = NewTrackedPlayer(16);
            watcher.PeerLastSeenGeneration[Leaver] = 900_000;

            BasisServerReductionSystemEvents.ClearDepartedPeerSlots(watcher, stackalloc int[] { Leaver });

            // A fresh player takes the recycled id and publishes their very first frame. The send loop's
            // gate is `senderGen > lastSeen`, so this is the exact comparison that decides whether they
            // are ever seen by somebody who was already here.
            const uint firstFrameGeneration = 1;
            Assert.True(firstFrameGeneration > watcher.PeerLastSeenGeneration[Leaver],
                "a recycled id inherited the previous holder's generation and would never be sent");
        }

        [Fact]
        public void ABatchOfRemovalsClearsEveryDepartedSlot()
        {
            PlayerState watcher = NewTrackedPlayer(16);
            int[] leaving = { 3, 4, 5 };
            foreach (int id in leaving)
            {
                watcher.PeerLastSeenGeneration[id] = 555;
                watcher.PeerTracking[id].HasDistanceCache = true;
            }
            // A survivor either side of the batch, to catch a clear that is too wide.
            watcher.PeerLastSeenGeneration[2] = 111;
            watcher.PeerLastSeenGeneration[6] = 222;

            BasisServerReductionSystemEvents.ClearDepartedPeerSlots(watcher, leaving);

            Assert.All(leaving, id => Assert.Equal(0u, watcher.PeerLastSeenGeneration[id]));
            Assert.All(leaving, id => Assert.False(watcher.PeerTracking[id].HasDistanceCache));
            Assert.Equal(111u, watcher.PeerLastSeenGeneration[2]);
            Assert.Equal(222u, watcher.PeerLastSeenGeneration[6]);
        }

        [Fact]
        public void AnIdBeyondTheTablesEndIsIgnoredRatherThanThrowing()
        {
            // Capacity is per receiver and grows on demand, so a departing peer can legitimately have an
            // id this particular receiver never had a slot for.
            PlayerState watcher = NewTrackedPlayer(4);
            BasisServerReductionSystemEvents.ClearDepartedPeerSlots(watcher, stackalloc int[] { 99 });
            Assert.Equal(4, watcher.PeerTracking.Length);
        }

        [Fact]
        public void AReceiverWithNoCompanionArrayIsNotAnException()
        {
            // Several fixtures build a PlayerState with only the record array. The clear must cope, the
            // same way the send loop repairs it rather than skipping the receiver.
            PlayerState watcher = new PlayerState { PeerTracking = new PeerTrackingData[8] };
            watcher.PeerTracking[Leaver].HasDistanceCache = true;

            BasisServerReductionSystemEvents.ClearDepartedPeerSlots(watcher, stackalloc int[] { Leaver });

            Assert.False(watcher.PeerTracking[Leaver].HasDistanceCache);
        }
    }
}
