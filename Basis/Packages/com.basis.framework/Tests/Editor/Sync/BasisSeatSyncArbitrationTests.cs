using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Receive-side arbitration tests for <see cref="BasisSeatSync"/>: simultaneous claims must converge
    /// on the same occupant regardless of delivery order, stale claims are dropped, and releases only
    /// apply when they come from the recorded occupant with a newer generation.
    /// </summary>
    public class BasisSeatSyncArbitrationTests
    {
        private GameObject _go;
        private BasisSeatSync _sync;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject(nameof(BasisSeatSyncArbitrationTests));
            var seat = _go.AddComponent<BasisSeat>();
            _sync = _go.AddComponent<BasisSeatSync>();
            _sync.Seat = seat;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        private static byte[] Packet(bool occupied, uint gen)
        {
            return new byte[]
            {
                occupied ? (byte)1 : (byte)0,
                (byte)gen,
                (byte)(gen >> 8),
                (byte)(gen >> 16),
                (byte)(gen >> 24)
            };
        }

        private void Receive(ushort sender, bool occupied, uint gen)
        {
            _sync.OnNetworkMessage(sender, Packet(occupied, gen), DeliveryMethod.ReliableOrdered);
        }

        private ushort Occupant()
        {
            Assert.IsTrue(_sync.HasUser(out ushort id), "expected an occupant");
            return id;
        }

        [Test]
        public void Claim_OnEmptySeat_SetsOccupant()
        {
            Receive(3, true, 1);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void SimultaneousClaims_LowerIdWins_WhenArrivingSecond()
        {
            Receive(5, true, 1);
            Receive(3, true, 1);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void SimultaneousClaims_LowerIdWins_WhenArrivingFirst()
        {
            Receive(3, true, 1);
            Receive(5, true, 1);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void Claim_WithOlderGeneration_IsDropped()
        {
            Receive(3, true, 5);
            Receive(2, true, 4);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void Claim_WithNewerGeneration_EvictsOccupant()
        {
            Receive(3, true, 1);
            Receive(5, true, 2);
            Assert.AreEqual(5, Occupant());
        }

        [Test]
        public void Claim_Reassert_SameOccupantKept()
        {
            Receive(3, true, 1);
            Receive(3, true, 1);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void Release_FromOccupant_ClearsSeat()
        {
            Receive(3, true, 1);
            Receive(3, false, 2);
            Assert.IsFalse(_sync.HasUser(out _));
        }

        [Test]
        public void Release_FromNonOccupant_IsIgnored()
        {
            Receive(3, true, 1);
            Receive(5, false, 2);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void Release_WithStaleGeneration_IsIgnored()
        {
            Receive(3, true, 2);
            Receive(3, false, 2);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void Release_OnEmptySeat_IsIgnored()
        {
            Receive(3, false, 1);
            Assert.IsFalse(_sync.HasUser(out _));
        }

        [Test]
        public void HandoffDeliveredOutOfOrder_ConvergesOnFinalOccupant()
        {
            Receive(5, true, 3);
            Receive(3, false, 2);
            Receive(3, true, 1);
            Assert.AreEqual(5, Occupant());
        }

        [Test]
        public void LegacyOneBytePacket_ClaimAccepted()
        {
            _sync.OnNetworkMessage(3, new byte[] { 1 }, DeliveryMethod.ReliableOrdered);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void LegacyOneBytePacket_ReleaseFromOccupant_Clears()
        {
            Receive(3, true, 1);
            _sync.OnNetworkMessage(3, new byte[] { 0 }, DeliveryMethod.ReliableOrdered);
            Assert.IsFalse(_sync.HasUser(out _));
        }

        [Test]
        public void LegacyOneBytePacket_ReleaseFromNonOccupant_Ignored()
        {
            Receive(3, true, 1);
            _sync.OnNetworkMessage(5, new byte[] { 0 }, DeliveryMethod.ReliableOrdered);
            Assert.AreEqual(3, Occupant());
        }

        [Test]
        public void MalformedPacket_IsIgnored()
        {
            Receive(3, true, 1);
            _sync.OnNetworkMessage(5, null, DeliveryMethod.ReliableOrdered);
            _sync.OnNetworkMessage(5, new byte[0], DeliveryMethod.ReliableOrdered);
            Assert.AreEqual(3, Occupant());
        }
    }
}
