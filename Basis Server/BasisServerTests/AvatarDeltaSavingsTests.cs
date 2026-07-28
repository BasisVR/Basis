using Basis.Network.Core.Compression;
using Xunit;
using Xunit.Abstractions;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

/// <summary>
/// Bandwidth-savings characterization at different levels of data similarity. Wire sizes model the
/// common byte-id case:
///   keyframe wire = 1(id) + 1(interval) + 1(seq) + payload
///   delta wire    = 1(header) + 1(id) + 1(interval) + 1(seq) + 1(baseSeq) + deltaBody
/// deltaBody = 7(dirty mask) + changed byte-fields + ceil(sum of changed bone widths / 8).
/// </summary>
public class AvatarDeltaSavingsTests
{
    private readonly ITestOutputHelper _out;
    public AvatarDeltaSavingsTests(ITestOutputHelper output) => _out = output;

    private static int KeyframeWire(BitQuality q) => 3 + S.PayloadSize(q);
    private static int DeltaWire(int body) => 5 + body;
    private static double Savings(BitQuality q, int body) => 1.0 - (double)DeltaWire(body) / KeyframeWire(q);

    private static int BodyFor(byte[] kf, byte[] cur, BitQuality q)
    {
        var dst = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(q)];
        return BasisAvatarDeltaCompression.BuildDelta(kf, cur, q, dst, 0);
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void Idle_SavesOver88Percent(BitQuality q)
    {
        var rng = new Random((int)q);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        int body = BodyFor(kf, (byte[])kf.Clone(), q);
        Assert.Equal(8, body);
        Assert.True(Savings(q, body) >= 0.88, $"idle savings {Savings(q, body):P1} < 88% at {q}");
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void RootPositionOnly_SavesOver78Percent(BitQuality q)
    {
        var rng = new Random((int)q + 10);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        cur[0] ^= 0xFF;
        int body = BodyFor(kf, cur, q);
        Assert.Equal(8 + S.PosBytes(q), body);
        Assert.True(Savings(q, body) >= 0.78, $"position-only savings {Savings(q, body):P1} < 78% at {q}");
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void AllBones_StillSavesSomething(BitQuality q)
    {
        var rng = new Random((int)q + 20);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        for (int s = 0; s < S.BoneCount; s++) S.FlipBone(cur, q, s);
        int body = BodyFor(kf, cur, q);
        Assert.Equal(8 + S.RotBytes(q), body);
        Assert.True(Savings(q, body) > 0.10, $"all-bones savings {Savings(q, body):P1} not positive enough at {q}");
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void EverythingChanged_DeltaNotSmallerThanKeyframe_TriggersPromotion(BitQuality q)
    {
        var rng = new Random((int)q + 30);
        byte[] kf = S.MakeRealisticPayload(q, rng);
        byte[] cur = (byte[])kf.Clone();
        cur[0] ^= 0xFF;
        cur[S.ScaleOffset(q)] ^= 0xFF;
        cur[S.BodyRotOffset(q)] ^= 0xFF;
        cur[S.HipsDeltaOffset(q)] ^= 0xFF;
        cur[S.HipsRotOffset(q)] ^= 0xFF;
        for (int s = 0; s < S.BoneCount; s++) S.FlipBone(cur, q, s);
        if (S.EndEffectorBytes(q) > 0) S.FlipEndEffector(cur, q);
        int body = BodyFor(kf, cur, q);
        // The server promotes to a keyframe when body >= payload — verify that guard fires here.
        Assert.True(body >= S.PayloadSize(q), $"expected promotion at {q}: body {body} < payload {S.PayloadSize(q)}");
    }

    [Fact]
    public void PrintSavingsTable()
    {
        int[] boneCounts = { 0, 1, 2, 3, 5, 8, 12, 18, 25, 35, 51 };
        var rng = new Random(2024);
        const int trials = 400;

        _out.WriteLine("Avatar delta bandwidth savings vs full keyframe (byte-id wire, averaged over realistic poses)");
        _out.WriteLine("");
        foreach (var q in S.AllQualities)
        {
            _out.WriteLine($"== {q}  (keyframe wire = {KeyframeWire(q)} B, payload = {S.PayloadSize(q)} B) ==");
            _out.WriteLine("  changed(pos+bones) | avg delta body | delta wire | savings");
            // Position always counts as changed for a moving avatar; K bones on top.
            foreach (int k in boneCounts)
            {
                long bodySum = 0;
                for (int t = 0; t < trials; t++)
                {
                    byte[] kf = S.MakeRealisticPayload(q, rng);
                    byte[] cur = (byte[])kf.Clone();
                    if (k > 0 || true) cur[0] ^= 0xFF; // root position moved
                    var slots = new HashSet<int>();
                    while (slots.Count < k) slots.Add(rng.Next(S.BoneCount));
                    foreach (int s in slots) S.FlipBone(cur, q, s);
                    bodySum += BodyFor(kf, cur, q);
                }
                double avgBody = (double)bodySum / trials;
                double wire = 5 + avgBody;
                double sav = 1.0 - wire / KeyframeWire(q);
                _out.WriteLine($"  pos + {k,2} bones        | {avgBody,10:F1}   | {wire,8:F1}  | {sav,6:P1}");
            }
            // Idle (no position change either) for reference.
            _out.WriteLine($"  idle (nothing)       |        7.0   |    12.0  | {Savings(q, 7),6:P1}");
            _out.WriteLine("");
        }
    }
}
