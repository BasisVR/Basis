using System;
using Basis.Scripts.Networking.Sync;
using NUnit.Framework;
using UnityEngine;
using static Basis.Tests.Sync.BasisSyncTestSupport;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Stress for the word-wise BitReader/BitWriter: every bit phase a field can start at, buffer
    /// reuse carrying no stale bits, truncation bounds, checksum integrity over the packed bytes,
    /// and codec purity under parallel decode (the driver fans AdvanceReceiver across workers).
    /// </summary>
    public class BasisSyncBitIoTests
    {
        [Test]
        public void Codec_RawFloat_BitExact_AtEveryBitPhase([NUnit.Framework.Range(0, 8)] int leadingBools)
        {
            var s = new BasisSyncSchema();
            for (int i = 0; i < leadingBools; i++) s.AddField(BasisSyncFieldType.Bool, true, false);
            s.AddField(BasisSyncFieldType.Float, true, false);
            s.AddField(BasisSyncFieldType.Int, true, false);
            s.Lock();

            var src = Values(s);
            for (int i = 0; i < leadingBools; i++) src.Disc[i] = i & 1;
            src.Cont[0] = 123.4567f;
            src.Disc[leadingBools] = -987654321;

            BasisSyncValues dst = RoundTripKeyframe(s, src);
            for (int i = 0; i < leadingBools; i++) Assert.AreEqual(src.Disc[i], dst.Disc[i], $"bool {i}");
            Assert.AreEqual(BitConverter.SingleToInt32Bits(src.Cont[0]), BitConverter.SingleToInt32Bits(dst.Cont[0]), "raw float must be bit-exact");
            Assert.AreEqual(src.Disc[leadingBools], dst.Disc[leadingBools], "trailing int");
        }

        [Test]
        public void Codec_BufferReuse_CarriesNoStaleBits()
        {
            var (s, a) = Single(BasisSyncFieldType.Position);
            a.Cont[0] = 999.9f; a.Cont[1] = -999.9f; a.Cont[2] = 555.5f;
            var buf = new byte[BasisSyncCodec.MaxSerializedSize(s)];
            var mask = new byte[Math.Max(1, s.DirtyMaskBytes)];
            BasisSyncCodec.Serialize(s, a, true, mask, 1, 50, buf);

            var b = Values(s);
            b.Cont[0] = 0.001f; b.Cont[1] = 0.002f; b.Cont[2] = 0.003f;
            int len = BasisSyncCodec.Serialize(s, b, true, mask, 2, 50, buf);

            var dst = Values(s);
            Assert.IsTrue(BasisSyncCodec.Deserialize(s, buf, len, dst, out _, out _, out _));
            for (int c = 0; c < 3; c++)
            {
                Assert.AreEqual(BitConverter.SingleToInt32Bits(b.Cont[c]), BitConverter.SingleToInt32Bits(dst.Cont[c]), $"component {c}");
            }
        }

        [Test]
        public void Codec_MixedWidths_RoundTrip_WithChecksum()
        {
            var s = new BasisSyncSchema();
            s.AddField(BasisSyncFieldType.Bool, true, false);
            s.AddField(BasisSyncFieldType.Position, true, true);
            s.AddField(BasisSyncFieldType.Byte, true, false);
            s.AddField(BasisSyncFieldType.Rotation, true, false);
            s.AddField(BasisSyncFieldType.Float, true, true);
            s.AddField(BasisSyncFieldType.UInt, true, false);
            s.AddField(BasisSyncFieldType.UShort, true, false);
            s.Lock();

            var src = Values(s);
            src.Disc[0] = 1;
            src.Cont[0] = 1.25f; src.Cont[1] = -2.5f; src.Cont[2] = 3.75f;
            src.Disc[1] = 200;
            src.Rot[0] = Quat(33, 77, 124);
            src.Cont[3] = 0.5f;
            src.Disc[2] = 305419896;
            src.Disc[3] = 54321;

            byte[] buf = SerializeKeyframe(s, src, 7, out int len, 50, true);
            Assert.IsTrue(BasisSyncCodec.VerifyChecksum(buf, len), "checksum must verify over the packed payload");

            var dst = Values(s);
            Assert.IsTrue(BasisSyncCodec.Deserialize(s, buf, len, dst, out _, out bool kf, out _));
            Assert.IsTrue(kf);
            Assert.AreEqual(1, dst.Disc[0]);
            Assert.AreEqual(1.25f, dst.Cont[0], 0.06f);
            Assert.AreEqual(-2.5f, dst.Cont[1], 0.06f);
            Assert.AreEqual(3.75f, dst.Cont[2], 0.06f);
            Assert.AreEqual(200, dst.Disc[1]);
            Assert.LessOrEqual(QuatAngle(src.Rot[0], dst.Rot[0]), 1.0f);
            Assert.AreEqual(0.5f, dst.Cont[3], 0.02f);
            Assert.AreEqual(305419896, dst.Disc[2]);
            Assert.AreEqual(54321, dst.Disc[3]);
        }

        [Test]
        public void Codec_TruncatedPacket_IsRejected_NotMisread()
        {
            var (s, src) = Single(BasisSyncFieldType.Position);
            src.Cont[0] = 1f; src.Cont[1] = 2f; src.Cont[2] = 3f;
            byte[] buf = SerializeKeyframe(s, src, 1, out int len);

            for (int cut = 1; cut <= len - BasisSyncCodec.HeaderSize; cut++)
            {
                var dst = Values(s);
                Assert.IsFalse(BasisSyncCodec.Deserialize(s, buf, len - cut, dst, out _, out _, out _), $"cut of {cut} bytes must be rejected");
            }
        }

        [Test]
        public void Codec_DeltaMask_TouchesOnlyMaskedFields()
        {
            var s = Schema(BasisSyncFieldType.Float, BasisSyncFieldType.Bool, BasisSyncFieldType.Float, BasisSyncFieldType.Int);
            var src = Values(s);
            src.Cont[0] = 11f; src.Disc[0] = 1; src.Cont[1] = 22f; src.Disc[1] = 42;

            byte[] buf = SerializeDelta(s, src, 3, new[] { 1, 2 }, out int len);
            var dst = Values(s);
            dst.Cont[0] = -1f; dst.Cont[1] = -1f; dst.Disc[1] = -1;
            Assert.IsTrue(BasisSyncCodec.Deserialize(s, buf, len, dst, out _, out bool kf, out _));
            Assert.IsFalse(kf);
            Assert.AreEqual(-1f, dst.Cont[0], "unmasked field must not move");
            Assert.AreEqual(1, dst.Disc[0]);
            Assert.AreEqual(22f, dst.Cont[1], 1e-4f);
            Assert.AreEqual(-1, dst.Disc[1], "unmasked field must not move");
        }

        [Test]
        public void Codec_ParallelDecode_MatchesSerial()
        {
            var s = Schema(BasisSyncFieldType.Position, BasisSyncFieldType.Rotation, BasisSyncFieldType.Int);
            const int n = 64;
            var bufs = new byte[n][];
            var lens = new int[n];
            var serial = new BasisSyncValues[n];
            for (int i = 0; i < n; i++)
            {
                var v = Values(s);
                v.Cont[0] = i * 1.5f; v.Cont[1] = -i; v.Cont[2] = i * 0.25f;
                v.Rot[0] = Quat(i * 3f, i * 5f, i * 7f);
                v.Disc[0] = i * 1000 - 500;
                bufs[i] = SerializeKeyframe(s, v, (byte)i, out lens[i]);
                serial[i] = Values(s);
                Assert.IsTrue(BasisSyncCodec.Deserialize(s, bufs[i], lens[i], serial[i], out _, out _, out _));
            }

            var parallel = new BasisSyncValues[n];
            for (int i = 0; i < n; i++) parallel[i] = Values(s);
            System.Threading.Tasks.Parallel.For(0, n, i =>
            {
                if (!BasisSyncCodec.Deserialize(s, bufs[i], lens[i], parallel[i], out _, out _, out _))
                {
                    throw new InvalidOperationException($"parallel deserialize failed at {i}");
                }
            });

            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < 3; c++) Assert.AreEqual(serial[i].Cont[c], parallel[i].Cont[c], $"packet {i} cont {c}");
                Assert.AreEqual(serial[i].Disc[0], parallel[i].Disc[0], $"packet {i} disc");
                Assert.LessOrEqual(QuatAngle(serial[i].Rot[0], parallel[i].Rot[0]), 1e-3f, $"packet {i} rot");
            }
        }
    }
}
