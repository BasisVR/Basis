using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using Basis.Scripts.Networking.Sync;
using NUnit.Framework;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Guards the single-datagram budget that keeps an oversized sync packet from throwing
    /// TooBigPacketException out of the transport — a throw that unwinds through
    /// BasisSyncDriver.TransmitOwned into BasisEventDriver.LateUpdateBody and takes every later
    /// LateUpdate stage with it, including the interpolation-job join.
    ///
    /// The runtime escalation itself (BasisSyncedObject.TransmitIfDue swapping an over-budget
    /// packet onto ReliableUnordered) is not covered here for the same reason the send cadence
    /// isn't: driving TransmitIfDue through a send lands in BasisNetworkGenericMessages and needs a
    /// live transport. What IS covered is every pure input that decision is made from.
    /// </summary>
    [NoAutoStaticsCleanup]
    public class BasisSyncDatagramBudgetTests
    {
        [Test]
        public void CanFragment_OnlyReliableOrderedAndUnordered()
        {
            // Mirrors NetPeer.SendInternal: everything else throws instead of splitting.
            Assert.IsTrue(BasisNetworkCommons.CanFragment(DeliveryMethod.ReliableOrdered));
            Assert.IsTrue(BasisNetworkCommons.CanFragment(DeliveryMethod.ReliableUnordered));

            Assert.IsFalse(BasisNetworkCommons.CanFragment(DeliveryMethod.Unreliable));
            Assert.IsFalse(BasisNetworkCommons.CanFragment(DeliveryMethod.Sequenced));
            Assert.IsFalse(BasisNetworkCommons.CanFragment(DeliveryMethod.ReliableSequenced));
        }

        [Test]
        public void EscalationTarget_CanActuallyFragment()
        {
            // The whole point of the escalation is picking a method the transport will split.
            Assert.IsTrue(BasisNetworkCommons.CanFragment(DeliveryMethod.ReliableUnordered),
                "BasisSyncedObject escalates oversized packets to ReliableUnordered; if that stopped being " +
                "fragmentable the escalation would throw exactly like the case it exists to prevent.");
        }

        [Test]
        public void Budget_FitsInsideTheSmallestMtuWithHeaderRoom()
        {
            Assert.Less(BasisNetworkCommons.MaxUnfragmentedPayload, BasisNetworkCommons.MinimumPeerMtu,
                "budget must leave room for the transport header inside the smallest MTU a peer can be at");
            Assert.Greater(BasisNetworkCommons.MaxUnfragmentedPayload, 0);
            Assert.AreEqual(BasisNetworkCommons.MinimumPeerMtu - BasisNetworkCommons.MaxUnfragmentedPayload,
                BasisNetworkCommons.UnfragmentedHeadroom);
        }

        [Test]
        public void Budget_UsesTheInitialMtuNotADiscoveredOne()
        {
            // Sizing against a discovered MTU is the bug: a broadcast fans out to peers at different
            // points in discovery (and to P2P links that negotiated separately), so the budget has to
            // hold for a peer that has not probed upward yet.
            Assert.LessOrEqual(BasisNetworkCommons.MinimumPeerMtu, 1024,
                "a budget derived from anything above the initial MTU throws on a peer still probing");
        }

        [Test]
        public void SceneDataFraming_MatchesWhatSceneDataMessageWrites()
        {
            // SceneDataMessage.Serialize: messageIndex (u16) + recipientsSize (u16) + one u16 per recipient.
            Assert.AreEqual(4, BasisNetworkGenericMessages.SceneDataFramingBytes(null),
                "broadcast: messageIndex + recipientsSize, no ids");
            Assert.AreEqual(4, BasisNetworkGenericMessages.SceneDataFramingBytes(new ushort[0]));
            Assert.AreEqual(6, BasisNetworkGenericMessages.SceneDataFramingBytes(new ushort[] { 1 }));
            Assert.AreEqual(4 + 40 * 2, BasisNetworkGenericMessages.SceneDataFramingBytes(new ushort[40]),
                "recipient ids are part of the datagram and must count against the budget");
        }

        [Test]
        public void BatchCap_LeavesRoomForItsOwnFraming()
        {
            // Regression: the cap was a standalone 1100, chosen against a typical discovered MTU. Batches
            // flush Unreliable — unfragmentable — so at a peer's initial MTU that threw for the first
            // seconds of every session, before discovery raised it.
            Assert.LessOrEqual(
                BasisSyncBatchCollector.MaxBatchPayload + BasisNetworkGenericMessages.SceneDataFramingBytes(null),
                BasisNetworkCommons.MaxUnfragmentedPayload,
                "a full batch plus its scene-data framing must still fit one unfragmentable datagram");
            Assert.Greater(BasisSyncBatchCollector.MaxBatchPayload, 0);
        }

        [Test]
        public void BatchCap_HoldsAFullBatchWorthOfEntries()
        {
            // The cap is only useful if a batch can still carry a realistic number of objects.
            var buf = new byte[BasisSyncBatchCollector.MaxBatchPayload];
            var w = new BasisSyncBatchWriter(buf);
            var payload = new byte[12];

            int appended = 0;
            while (w.TryAppend((ushort)appended, payload, payload.Length)) appended++;

            Assert.Greater(appended, 20, "a 988 B budget should still coalesce dozens of small objects");
            Assert.LessOrEqual(w.Length, BasisSyncBatchCollector.MaxBatchPayload);
        }

        [Test]
        public void TypicalTransformSchema_StaysUnderBudget()
        {
            // A normal synced transform must never trip the escalation — if it did, the guard would be
            // quietly moving ordinary traffic onto a reliable channel.
            var schema = new BasisSyncSchema();
            schema.AddField(BasisSyncFieldType.Position);
            schema.AddRotation(true, 9);
            schema.AddField(BasisSyncFieldType.Scale);

            int worst = BasisSyncCodec.MaxSerializedSize(schema) + BasisNetworkGenericMessages.SceneDataFramingBytes(null);
            Assert.Less(worst, BasisNetworkCommons.MaxUnfragmentedPayload);
        }

        [Test]
        public void LargeSchema_ExceedsBudget_AndIsWhatTheGuardCatches()
        {
            // 168 raw Vector3 fields is ~2 KB — the shape of the packet that threw in the field. The
            // schema is legal (255 fields max), so nothing upstream stops it; the send-side check must.
            var schema = new BasisSyncSchema();
            for (int i = 0; i < 168; i++) schema.AddField(BasisSyncFieldType.Position);

            int worst = BasisSyncCodec.MaxSerializedSize(schema) + BasisNetworkGenericMessages.SceneDataFramingBytes(null);
            Assert.Greater(worst, BasisNetworkCommons.MaxUnfragmentedPayload,
                "this is the case the guard exists for; if it now fits, the budget moved and the test is stale");
            Assert.IsFalse(BasisNetworkCommons.CanFragment(DeliveryMethod.Unreliable),
                "and the default delta delivery cannot carry it");
        }

        // ── The guard must be invisible to normal traffic ──────────────────────────────────
        // The escalation only ever reads a length the codec already produced; it never adds a byte and
        // never touches the delivery method below the budget. These pin that, so a later change to the
        // budget or the framing can't quietly start rerouting ordinary objects onto a reliable channel.

        /// <summary>Exact serialized sizes for the stock BasisSyncedTransform configurations, as registered in its Awake.</summary>
        static readonly object[] StockConfigs =
        {
            //          name                              posAxes rotMode          scaleAxes  keyframe delta
            new object[] { "default (pos XYZ + smallest-three 9)", 3, "st9",  0, 22, 23 },
            new object[] { "pos + rot + scale XYZ",                3, "st9",  3, 34, 35 },
            new object[] { "position only",                        3, "none", 0, 18, 19 },
            new object[] { "rotation only (smallest-three 9)",     0, "st9",  0, 10, 11 },
            new object[] { "quaternion raw (4 floats)",            3, "qraw", 0, 34, 35 },
        };

        [TestCaseSource(nameof(StockConfigs))]
        public void StockTransformConfigs_HaveUnchangedWireSizes(string name, int posAxes, string rotMode, int scaleAxes, int expectKeyframe, int expectDelta)
        {
            var schema = new BasisSyncSchema();
            for (int i = 0; i < posAxes; i++) schema.AddField(BasisSyncFieldType.Float);
            switch (rotMode)
            {
                case "st9": schema.AddRotation(true, 9); break;
                case "qraw": for (int i = 0; i < 4; i++) schema.AddField(BasisSyncFieldType.Float); break;
            }
            for (int i = 0; i < scaleAxes; i++) schema.AddField(BasisSyncFieldType.Float);

            int bits = 0;
            for (int i = 0; i < schema.FieldCount; i++)
            {
                BasisSyncField f = schema.GetField(i);
                bits += f.Pool == BasisSyncPool.Rotation ? 2 + 3 * (1 + f.RotBits) : f.ContComponents * 32;
            }
            BasisSyncCodec.WireBytes(bits, schema.FieldCount, true, out _, out _, out int keyframeBytes, out int deltaBytes);

            Assert.AreEqual(expectKeyframe, keyframeBytes, $"{name}: keyframe bytes moved");
            Assert.AreEqual(expectDelta, deltaBytes, $"{name}: delta bytes moved");
        }

        [TestCaseSource(nameof(StockConfigs))]
        public void StockTransformConfigs_AreNeverEscalated(string name, int posAxes, string rotMode, int scaleAxes, int expectKeyframe, int expectDelta)
        {
            // Worst case for a stock object: the bigger of the two packet kinds, broadcast framing, and
            // the relevance-culling case where 64 recipient ids ride along.
            int worst = System.Math.Max(expectKeyframe, expectDelta);
            int broadcast = worst + BasisNetworkGenericMessages.SceneDataFramingBytes(null);
            int culled = worst + BasisNetworkGenericMessages.SceneDataFramingBytes(new ushort[64]);

            foreach (DeliveryMethod dm in new[] { DeliveryMethod.Unreliable, DeliveryMethod.Sequenced, DeliveryMethod.ReliableSequenced })
            {
                Assert.IsFalse(BasisSyncedObject.NeedsFragmentableDelivery(broadcast, dm), $"{name}: broadcast escalated on {dm}");
                Assert.IsFalse(BasisSyncedObject.NeedsFragmentableDelivery(culled, dm), $"{name}: 64-recipient send escalated on {dm}");
            }

            Assert.Less(broadcast, BasisNetworkCommons.MaxUnfragmentedPayload / 10,
                $"{name}: a stock object should sit an order of magnitude under the budget");
        }

        [Test]
        public void EveryPacketUpToTheBudget_KeepsItsRequestedDelivery()
        {
            // Exhaustive over the whole in-budget range, so "the guard is a no-op for normal traffic" is
            // checked rather than argued.
            var methods = new[]
            {
                DeliveryMethod.Unreliable, DeliveryMethod.Sequenced, DeliveryMethod.ReliableSequenced,
                DeliveryMethod.ReliableOrdered, DeliveryMethod.ReliableUnordered,
            };

            for (int size = 0; size <= BasisNetworkCommons.MaxUnfragmentedPayload; size++)
            {
                foreach (DeliveryMethod dm in methods)
                {
                    Assert.IsFalse(BasisSyncedObject.NeedsFragmentableDelivery(size, dm),
                        $"{size} B on {dm} must go out exactly as configured");
                }
            }

            // And one byte past it, only the unfragmentable methods move.
            int over = BasisNetworkCommons.MaxUnfragmentedPayload + 1;
            Assert.IsTrue(BasisSyncedObject.NeedsFragmentableDelivery(over, DeliveryMethod.Unreliable));
            Assert.IsTrue(BasisSyncedObject.NeedsFragmentableDelivery(over, DeliveryMethod.Sequenced));
            Assert.IsTrue(BasisSyncedObject.NeedsFragmentableDelivery(over, DeliveryMethod.ReliableSequenced));
            Assert.IsFalse(BasisSyncedObject.NeedsFragmentableDelivery(over, DeliveryMethod.ReliableOrdered),
                "already fragmentable — nothing to change");
            Assert.IsFalse(BasisSyncedObject.NeedsFragmentableDelivery(over, DeliveryMethod.ReliableUnordered));
        }

        [Test]
        public void SerializedLength_IsUnaffectedByTheGuard()
        {
            // End-to-end: the guard runs after Serialize and only reads its result, so the bytes handed to
            // the transport for a stock object are whatever the codec produced — 22 B keyframe here.
            var schema = new BasisSyncSchema();
            for (int i = 0; i < 3; i++) schema.AddField(BasisSyncFieldType.Float);
            schema.AddRotation(true, 9);

            var values = new BasisSyncValues();
            values.Allocate(schema);
            var scratch = new byte[BasisSyncCodec.MaxSerializedSize(schema)];
            var mask = new byte[schema.DirtyMaskBytes];

            int keyframeLen = BasisSyncCodec.Serialize(schema, values, true, mask, 1, 50, scratch, true);
            Assert.AreEqual(22, keyframeLen, "stock transform keyframe is 22 B on the wire");
            Assert.IsFalse(BasisSyncedObject.NeedsFragmentableDelivery(
                keyframeLen + BasisNetworkGenericMessages.SceneDataFramingBytes(null), DeliveryMethod.Unreliable));

            for (int i = 0; i < schema.DirtyMaskBytes; i++) mask[i] = 0xFF;
            int deltaLen = BasisSyncCodec.Serialize(schema, values, false, mask, 2, 50, scratch, true);
            Assert.AreEqual(23, deltaLen, "stock transform all-fields delta is 23 B on the wire");
        }

        [Test]
        public void QuantizingTheSameSchema_BringsItBackUnderBudget()
        {
            // The error message tells authors to quantize. This asserts that advice actually works —
            // same field count, same types, only the per-component width changes.
            const int Fields = 100;

            var raw = new BasisSyncSchema();
            for (int i = 0; i < Fields; i++) raw.AddField(BasisSyncFieldType.Position);

            var half = new BasisSyncSchema();
            var halfSpecs = new[] { BasisQuantSpec.Half, BasisQuantSpec.Half, BasisQuantSpec.Half };
            for (int i = 0; i < Fields; i++) half.AddField(BasisSyncFieldType.Position, true, halfSpecs);

            int framing = BasisNetworkGenericMessages.SceneDataFramingBytes(null);
            Assert.Greater(BasisSyncCodec.MaxSerializedSize(raw) + framing, BasisNetworkCommons.MaxUnfragmentedPayload,
                "100 raw Vector3 fields must be over budget, or this test proves nothing");
            Assert.Less(BasisSyncCodec.MaxSerializedSize(half) + framing, BasisNetworkCommons.MaxUnfragmentedPayload,
                "halving the bits per component must bring the same schema back under");
        }
    }
}
