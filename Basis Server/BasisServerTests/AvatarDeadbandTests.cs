using Basis.Network.Core.Compression;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Sender-side deadband primitives: raw-value comparisons that let the uplink drop frames whose
/// every field moved less than a sub-visibility threshold since the last frame actually sent.
/// </summary>
public class AvatarDeadbandTests
{
    private static float[] YawQuat(double degrees)
    {
        double half = degrees * Math.PI / 180.0 * 0.5;
        return new[] { 0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half) };
    }

    [Fact]
    public void QuatsWithin_PassesUnderThreshold_FailsOver()
    {
        double minDot = BasisAvatarDeadband.MinAbsDotForAngleDegrees(0.10f);
        float[] baseQ = YawQuat(0);
        Assert.True(BasisAvatarDeadband.QuatsWithin(baseQ, YawQuat(0.0), minDot), "identical");
        Assert.True(BasisAvatarDeadband.QuatsWithin(baseQ, YawQuat(0.05), minDot), "under threshold");
        Assert.True(BasisAvatarDeadband.QuatsWithin(baseQ, YawQuat(0.099), minDot), "just under");
        Assert.False(BasisAvatarDeadband.QuatsWithin(baseQ, YawQuat(0.12), minDot), "over threshold");
        Assert.False(BasisAvatarDeadband.QuatsWithin(baseQ, YawQuat(5.0), minDot), "far over");
    }

    [Fact]
    public void QuatsWithin_IsDoubleCoverInsensitive()
    {
        double minDot = BasisAvatarDeadband.MinAbsDotForAngleDegrees(0.10f);
        float[] q = YawQuat(30);
        float[] negated = { -q[0], -q[1], -q[2], -q[3] };
        Assert.True(BasisAvatarDeadband.QuatsWithin(q, negated, minDot), "-q represents the same rotation");
    }

    [Fact]
    public void QuatsWithin_ChecksEveryQuatInTheSpan()
    {
        double minDot = BasisAvatarDeadband.MinAbsDotForAngleDegrees(0.10f);
        float[] a = new float[8];
        float[] b = new float[8];
        YawQuat(0).CopyTo(a, 0); YawQuat(0).CopyTo(b, 0);
        YawQuat(0).CopyTo(a, 4); YawQuat(1.0).CopyTo(b, 4);
        Assert.False(BasisAvatarDeadband.QuatsWithin(a, b, minDot), "second quat differs");
        Assert.False(BasisAvatarDeadband.QuatsWithin(a.AsSpan(0, 7), b.AsSpan(0, 7), minDot), "non-multiple-of-4 rejected");
    }

    [Fact]
    public void TightRootThreshold_IsResolvableInDouble()
    {
        // 0.05° → cos(0.025°) sits within float-epsilon of 1.0; the double path must still
        // separate under from over.
        double minDot = BasisAvatarDeadband.MinAbsDotForAngleDegrees(BasisAvatarDeadband.RootAngleDegrees);
        Assert.True(BasisAvatarDeadband.QuatsWithin(YawQuat(0), YawQuat(0.03), minDot));
        Assert.False(BasisAvatarDeadband.QuatsWithin(YawQuat(0), YawQuat(0.08), minDot));
    }

    [Fact]
    public void ValuesWithin_BoundsAbsoluteDelta_AndRejectsNonFinite()
    {
        float[] a = { 1.0f, -2.0f, 0.5f };
        Assert.True(BasisAvatarDeadband.ValuesWithin(a, new[] { 1.0015f, -2.0015f, 0.5f }, 0.002f));
        Assert.False(BasisAvatarDeadband.ValuesWithin(a, new[] { 1.0f, -2.0f, 0.503f }, 0.002f));
        Assert.False(BasisAvatarDeadband.ValuesWithin(a, new[] { 1.0f, float.NaN, 0.5f }, 0.002f), "NaN must not wedge suppression");
        Assert.False(BasisAvatarDeadband.ValuesWithin(a, new[] { 1.0f, -2.0f }, 0.002f), "length mismatch");
    }

    [Fact]
    public void DriftAgainstFixedBaseline_EventuallyExceeds()
    {
        // Compare-vs-last-SENT semantics: tiny per-frame drift accumulates against the fixed
        // baseline until it crosses the threshold and forces a send. 0.03°/frame crosses 0.1°
        // on the 4th frame.
        double minDot = BasisAvatarDeadband.MinAbsDotForAngleDegrees(BasisAvatarDeadband.BoneAngleDegrees);
        float[] baseline = YawQuat(0);
        int sentAt = -1;
        for (int frame = 1; frame <= 6; frame++)
        {
            if (!BasisAvatarDeadband.QuatsWithin(baseline, YawQuat(0.03 * frame), minDot))
            {
                sentAt = frame;
                break;
            }
        }
        Assert.Equal(4, sentAt);
    }
}
