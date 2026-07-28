using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

/// <summary>
/// Direct coverage of <see cref="AvatarQualityRepacker.BuildAllLowerFromHighInto"/>: the High
/// source keeps float32 position at offset 0..12, lower tiers store int24-mm at 0..9, so every
/// downstream region (rotation bitstream, tail) shifts by 3 bytes. A wrong base offset here
/// corrupts every reduced avatar frame, so the layout is asserted bit-exactly.
/// </summary>
public class AvatarRepackerTests
{
    private static readonly BitQuality[] LowerQualities = { BitQuality.Medium, BitQuality.Low, BitQuality.VeryLow };

    private static uint Rescale(uint qSrc, int bSrc, int bDst)
    {
        if (bSrc == bDst) return qSrc;
        ulong maxSrc = ((ulong)1 << bSrc) - 1UL;
        ulong maxDst = ((ulong)1 << bDst) - 1UL;
        return (uint)(((ulong)qSrc * maxDst + (maxSrc >> 1)) / maxSrc);
    }

    [Fact]
    public void RepackedLowerTiers_MatchExpectedLayoutBitExactly()
    {
        var rng = new Random(777);
        for (int iter = 0; iter < 50; iter++)
        {
            byte[] highArr = S.MakeRealisticPayload(BitQuality.High, rng);
            // MakeRealisticPayload fills position with random bytes; overwrite with plausible
            // world coordinates so the int24-mm transcode is exercised inside its range.
            for (int axis = 0; axis < 3; axis++)
                BitConverter.GetBytes((float)(rng.NextDouble() * 2000.0 - 1000.0)).CopyTo(highArr, axis * 4);
            var high = new SerializableBasis.LocalAvatarSyncMessage { array = highArr, DataQualityLevel = (byte)BitQuality.High };
            var med = new SerializableBasis.LocalAvatarSyncMessage();
            var low = new SerializableBasis.LocalAvatarSyncMessage();
            var vlow = new SerializableBasis.LocalAvatarSyncMessage();
            AvatarQualityRepacker.BuildAllLowerFromHighInto(high, ref med, ref low, ref vlow);

            var tiers = new[] { (BitQuality.Medium, med), (BitQuality.Low, low), (BitQuality.VeryLow, vlow) };
            byte[] highBpc = BasisBoneRotationCompression.BPC_HIGH;
            var highOffs = S.BoneBitOffsets(BitQuality.High);

            foreach (var (q, msg) in tiers)
            {
                Assert.Equal((byte)q, msg.DataQualityLevel);
                Assert.True(msg.array.Length >= BasisAvatarBitPacking.ConvertToSize(q));

                int posBytes = BasisAvatarBitPacking.PositionBytes(q);
                Assert.Equal(BasisAvatarBitPacking.WritePositionQuantized, posBytes);

                // Position survives the float32 → int24-mm transcode within half a millimetre.
                for (int axis = 0; axis < 3; axis++)
                {
                    float expected = BitConverter.ToSingle(highArr, axis * 4);
                    float actual = BasisAvatarBitPacking.DecodeAxisMm(msg.array, axis * 3);
                    Assert.True(Math.Abs(expected - actual) <= 0.0006f,
                        $"{q} axis {axis}: {expected} vs {actual}");
                }

                // Every bone is the High bone rescaled to the tier's BPC, written at the 9-byte base.
                byte[] bpc = BasisBoneRotationCompression.GetBpcTable(q);
                var offs = S.BoneBitOffsets(q);
                for (int slot = 0; slot < S.BoneCount; slot++)
                {
                    int srcPos = 12 * 8 + highOffs[slot];
                    ulong rawHigh = BasisBoneRotationCompression.ReadBits(highArr, ref srcPos, 2 + 3 * highBpc[slot]);
                    uint idx = (uint)(rawHigh & 3UL);
                    uint maskSrc = (uint)((1 << highBpc[slot]) - 1);
                    uint qa = (uint)((rawHigh >> 2) & maskSrc);
                    uint qb = (uint)((rawHigh >> (2 + highBpc[slot])) & maskSrc);
                    uint qc = (uint)((rawHigh >> (2 + 2 * highBpc[slot])) & maskSrc);

                    ulong expectedPacked = idx
                        | ((ulong)Rescale(qa, highBpc[slot], bpc[slot]) << 2)
                        | ((ulong)Rescale(qb, highBpc[slot], bpc[slot]) << (2 + bpc[slot]))
                        | ((ulong)Rescale(qc, highBpc[slot], bpc[slot]) << (2 + 2 * bpc[slot]));

                    int dstPos = posBytes * 8 + offs[slot];
                    ulong actualPacked = BasisBoneRotationCompression.ReadBits(msg.array, ref dstPos, 2 + 3 * bpc[slot]);
                    Assert.True(expectedPacked == actualPacked, $"{q} bone slot {slot} mismatch");
                }

                // Tail is copied verbatim from the High source.
                int srcTail = 12 + BasisBoneRotationCompression.RotationBytes(BitQuality.High);
                int dstTail = posBytes + BasisBoneRotationCompression.RotationBytes(q);
                for (int i = 0; i < BasisBoneRotationCompression.TailBytes; i++)
                    Assert.Equal(highArr[srcTail + i], msg.array[dstTail + i]);
            }
        }
    }

    [Fact]
    public void RepackedPayloads_RoundTripThroughDeltaCodec()
    {
        // The delta codec and the repacker must agree on the lower-tier layout: a delta built
        // from two repacked frames has to reconstruct the second exactly.
        var rng = new Random(778);
        byte[] a = S.MakeRealisticPayload(BitQuality.High, rng);
        byte[] b = (byte[])a.Clone();
        b[0] ^= 0xFF; // move position
        S.FlipBone(b, BitQuality.High, 4);

        var highA = new SerializableBasis.LocalAvatarSyncMessage { array = a, DataQualityLevel = (byte)BitQuality.High };
        var highB = new SerializableBasis.LocalAvatarSyncMessage { array = b, DataQualityLevel = (byte)BitQuality.High };
        var (medA, lowA, vlowA) = AvatarQualityRepacker.BuildAllLowerFromHigh(highA);
        var (medB, lowB, vlowB) = AvatarQualityRepacker.BuildAllLowerFromHigh(highB);

        S.AssertRoundTrip(medA.array, medB.array, BitQuality.Medium);
        S.AssertRoundTrip(lowA.array, lowB.array, BitQuality.Low);
        S.AssertRoundTrip(vlowA.array, vlowB.array, BitQuality.VeryLow);
    }
}
