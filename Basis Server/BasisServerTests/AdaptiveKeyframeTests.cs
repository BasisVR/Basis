using BasisNetworkServer.BasisNetworkingReductionSystem;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Adaptive keyframe stretch (v42): a streak of small High deltas doubles the periodic keyframe
/// interval up to AvatarDeltaKeyframeMaxIntervalMs; any large delta snaps it back to the base.
/// </summary>
[Collection("Basis reduction statics")]
public class AdaptiveKeyframeTests : IDisposable
{
    private readonly int _savedBase = BasisServerReductionSystemEvents.AvatarDeltaKeyframeIntervalMs;
    private readonly int _savedMax = BasisServerReductionSystemEvents.AvatarDeltaKeyframeMaxIntervalMs;

    public AdaptiveKeyframeTests()
    {
        BasisServerReductionSystemEvents.AvatarDeltaKeyframeIntervalMs = 500;
        BasisServerReductionSystemEvents.AvatarDeltaKeyframeMaxIntervalMs = 2000;
    }

    public void Dispose()
    {
        BasisServerReductionSystemEvents.AvatarDeltaKeyframeIntervalMs = _savedBase;
        BasisServerReductionSystemEvents.AvatarDeltaKeyframeMaxIntervalMs = _savedMax;
    }

    [Fact]
    public void EffectiveInterval_DoublesPerShift_AndClampsToMax()
    {
        Assert.Equal(500, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(0));
        Assert.Equal(1000, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(1));
        Assert.Equal(2000, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(2));
        Assert.Equal(2000, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(3));
        Assert.Equal(2000, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(60));
    }

    [Fact]
    public void EffectiveInterval_MaxAtOrBelowBase_DisablesStretch()
    {
        BasisServerReductionSystemEvents.AvatarDeltaKeyframeMaxIntervalMs = 0;
        Assert.Equal(500, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(5));
        BasisServerReductionSystemEvents.AvatarDeltaKeyframeMaxIntervalMs = 500;
        Assert.Equal(500, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(5));
    }

    [Fact]
    public void SmallDeltaStreak_StretchesAfterFour_BigDeltaResets()
    {
        var state = new PlayerState();

        // Three small deltas: not yet stretched.
        for (int i = 0; i < 3; i++)
            BasisServerReductionSystemEvents.UpdateKeyframeStretch(state, 20);
        Assert.Equal(0, state.KeyframeStretchShift);

        // Fourth completes the streak.
        BasisServerReductionSystemEvents.UpdateKeyframeStretch(state, 20);
        Assert.Equal(1, state.KeyframeStretchShift);

        // Four more: next stretch step.
        for (int i = 0; i < 4; i++)
            BasisServerReductionSystemEvents.UpdateKeyframeStretch(state, 20);
        Assert.Equal(2, state.KeyframeStretchShift);

        // At the cap (2000ms at shift 2), further small deltas stop accumulating.
        for (int i = 0; i < 16; i++)
            BasisServerReductionSystemEvents.UpdateKeyframeStretch(state, 20);
        Assert.Equal(2, state.KeyframeStretchShift);
        Assert.Equal(2000, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(state.KeyframeStretchShift));

        // A single big delta (motion) snaps everything back.
        BasisServerReductionSystemEvents.UpdateKeyframeStretch(state, 200);
        Assert.Equal(0, state.KeyframeStretchShift);
        Assert.Equal(0, state.SmallDeltaStreak);
        Assert.Equal(500, BasisServerReductionSystemEvents.EffectiveKeyframeIntervalMs(state.KeyframeStretchShift));
    }
}
