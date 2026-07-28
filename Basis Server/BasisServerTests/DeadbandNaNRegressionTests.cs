using Basis.Network.Core.Compression;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Regression: <see cref="BasisAvatarDeadband.QuatsWithin"/> must reject a NaN dot (force a send),
/// matching <see cref="BasisAvatarDeadband.ValuesWithin"/>. Before the fix a NaN rotation component
/// with finite positions passed the deadband, so a glitched frame could be suppressed and the
/// receiver would hold a stale pose until the next heartbeat.
/// </summary>
public class DeadbandNaNRegressionTests
{
    static readonly double Dot = BasisAvatarDeadband.MinAbsDotForAngleDegrees(BasisAvatarDeadband.BoneAngleDegrees);

    [Fact]
    public void QuatsWithin_NaNComponent_ForcesSend()
    {
        float[] cur = { 0f, 0f, 0f, float.NaN };
        float[] last = { 0f, 0f, 0f, 1f };
        Assert.False(BasisAvatarDeadband.QuatsWithin(cur, last, Dot));
    }

    [Fact]
    public void QuatsWithin_NaNInSecondQuatOfPair_ForcesSend()
    {
        float[] cur = { 0f, 0f, 0f, 1f, 0f, 0f, 0f, float.NaN };
        float[] last = { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        Assert.False(BasisAvatarDeadband.QuatsWithin(cur, last, Dot));
    }

    [Fact]
    public void QuatsWithin_IdenticalQuat_Within()
    {
        float[] q = { 0f, 0f, 0f, 1f };
        Assert.True(BasisAvatarDeadband.QuatsWithin(q, (float[])q.Clone(), Dot));
    }

    [Fact]
    public void QuatsWithin_LargeAngle_NotWithin()
    {
        // 90° about Z: (0,0,sin45,cos45); |dot| with identity ≈ 0.707, well below the sub-degree threshold.
        const float s = 0.70710678f;
        float[] cur = { 0f, 0f, s, s };
        float[] last = { 0f, 0f, 0f, 1f };
        Assert.False(BasisAvatarDeadband.QuatsWithin(cur, last, Dot));
    }

    [Fact]
    public void BothPredicates_RejectNaN()
    {
        Assert.False(BasisAvatarDeadband.ValuesWithin(new[] { float.NaN }, new[] { 0f }, 0.01f));
        Assert.False(BasisAvatarDeadband.QuatsWithin(new[] { float.NaN, 0f, 0f, 1f }, new[] { 0f, 0f, 0f, 1f }, Dot));
    }
}
