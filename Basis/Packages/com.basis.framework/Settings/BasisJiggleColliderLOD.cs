using Basis.BasisUI;
using Basis.Scripts.Networking;
using UnityEngine;


/// <summary>
/// Distance-based reduction of the per-avatar jiggle colliders other players contribute to the
/// global jiggle simulation. The arm/finger/foot colliders of remote avatars are the dominant
/// per-frame cost in crowded instances; cutting them back by distance reclaims most of it while
/// staying invisible to the local player (finger-level jiggle collisions can't be seen past a few
/// metres). The thresholds are user-tunable in Settings &gt; Performance Limits.
/// </summary>
public static class BasisJiggleColliderLOD
{
    /// <summary>Master switch. When false, every avatar keeps its full collider set.</summary>
    public static bool Enabled;

    // internal: also read by BasisJiggleLodJob, which mirrors ComputeTier's math to run in
    // parallel with the transmit tick's distance/reduce/cap/dampen chain (see that job's summary).
    internal static float _nearSqr = 25f * 25f;
    internal static float _midSqr = 50f * 50f;
    internal static float _farSqr = 100f * 100f;

    // 10% distance hysteresis (matches the squared convention used by the distance-reduction jobs)
    // so an avatar hovering on a boundary doesn't churn add/remove every tick.
    internal const float HysteresisSqr = 1.1f * 1.1f;

    // [feet, arms, hands, fingers] active per tier. Static so tier lookups never allocate.
    private static readonly bool[] FullCats = { true, true, true, true };
    private static readonly bool[] NoFingersCats = { true, true, true, false };
    private static readonly bool[] HandsOnlyCats = { false, false, true, false };
    private static readonly bool[] NoneCats = { false, false, false, false };

    /// <summary>Which collider categories are active for a tier: [feet, arms, hands, fingers].</summary>
    public static bool[] ActiveCategories(BasisJiggleColliderTier tier) => tier switch
    {
        BasisJiggleColliderTier.Full => FullCats,
        BasisJiggleColliderTier.NoFingers => NoFingersCats,
        BasisJiggleColliderTier.HandsOnly => HandsOnlyCats,
        _ => NoneCats,
    };

    /// <summary>
    /// Pushes the current setting values into this module. Call once at startup and again on every
    /// settings change. On an enabled-&gt;disabled edge it restores every remote to its full set.
    /// </summary>
    public static void ApplyFromSettings()
    {
        bool wasEnabled = Enabled;
        Enabled = BasisSettingsDefaults.UseJiggleColliderDistanceLod.RawValue;

        float near = Mathf.Max(0f, BasisSettingsDefaults.JiggleColliderLodNearDistance.RawValue);
        float mid = Mathf.Max(0f, BasisSettingsDefaults.JiggleColliderLodMidDistance.RawValue);
        float far = Mathf.Max(0f, BasisSettingsDefaults.JiggleColliderLodFarDistance.RawValue);

        // Keep the thresholds ascending so the tiers stay meaningful even if the sliders cross over.
        if (mid < near) mid = near;
        if (far < mid) far = mid;

        _nearSqr = near * near;
        _midSqr = mid * mid;
        _farSqr = far * far;

        if (wasEnabled && !Enabled)
        {
            RestoreAllRemotesToFull();
        }
    }

    /// <summary>
    /// Maps a squared distance to a tier, applying hysteresis relative to the tier the avatar is
    /// already in so boundary crossings don't flap. Returns <see cref="BasisJiggleColliderTier.Full"/>
    /// when the feature is disabled.
    /// </summary>
    public static BasisJiggleColliderTier ComputeTier(float distanceSq, BasisJiggleColliderTier current)
    {
        if (!Enabled)
        {
            return BasisJiggleColliderTier.Full;
        }

        int cur = (int)current;
        BasisJiggleColliderTier tier = BasisJiggleColliderTier.Full;
        if (PastBoundary(distanceSq, _nearSqr, cur > 0))
        {
            tier = BasisJiggleColliderTier.NoFingers;
            if (PastBoundary(distanceSq, _midSqr, cur > 1))
            {
                tier = BasisJiggleColliderTier.HandsOnly;
                if (PastBoundary(distanceSq, _farSqr, cur > 2))
                {
                    tier = BasisJiggleColliderTier.None;
                }
            }
        }
        return tier;
    }

    // alreadyPast: true when the avatar is currently on the far side of this boundary. Coming back
    // requires dropping below boundary/Hyst; crossing out requires exceeding boundary*Hyst.
    private static bool PastBoundary(float distanceSq, float boundarySq, bool alreadyPast)
    {
        float threshold = alreadyPast ? boundarySq / HysteresisSqr : boundarySq * HysteresisSqr;
        return distanceSq > threshold;
    }

    private static void RestoreAllRemotesToFull()
    {
        foreach (var kvp in BasisNetworkPlayers.RemotePlayers)
        {
            var driver = kvp.Value?.RemoteAvatarDriver;
            if (driver != null && driver.HasJiggleColliders)
            {
                driver.ApplyColliderLOD(BasisJiggleColliderTier.Full);
            }
        }
    }
}
