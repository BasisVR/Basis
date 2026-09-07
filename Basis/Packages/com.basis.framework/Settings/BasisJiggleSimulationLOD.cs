using Basis.BasisUI;
using Basis.Scripts.Networking;
using UnityEngine;

/// <summary>
/// Distance-based pause of remote avatars' jiggle simulation itself — distinct from
/// <see cref="BasisJiggleColliderLOD"/>, which only trims the colliders other jiggle chains bounce
/// off. A registered jiggle tree pays a Verlet integrate plus a transform read/write every physics
/// tick regardless of whether anyone can actually see it move. Past the cutoff distance, a remote's
/// <see cref="GatorDragonGames.JigglePhysics.JiggleRig"/> components are disabled, which unregisters
/// their trees from the global simulation via the package's own OnDisable -&gt; RemoveJiggleTreeSegment
/// path; re-enabling re-adds them, which reseeds the verlet state from the current animated bone pose
/// rather than resuming mid-motion. That reseed-on-return is the deliberate trade for paying zero
/// simulation cost on a far/occluded avatar's jiggle bones instead of a reduced-but-nonzero rate. The
/// local player is never touched.
/// </summary>
public static class BasisJiggleSimulationLOD
{
    public static bool Enabled;

    // internal: also read by BasisJiggleLodJob, which mirrors ShouldSimulate's math to run in
    // parallel with the transmit tick's distance/reduce/cap/dampen chain (see that job's summary).
    internal static float _cutoffSqr = 120f * 120f;

    // Matches the squared-hysteresis convention in BasisJiggleColliderLOD so a remote hovering on
    // the boundary doesn't flip its jiggle rigs on/off every distance-loop tick.
    internal const float HysteresisSqr = 1.1f * 1.1f;

    /// <summary>
    /// Pushes the current setting values into this module. Call once at startup and again on every
    /// settings change. On an enabled-&gt;disabled edge it restores every remote to simulating.
    /// </summary>
    public static void ApplyFromSettings()
    {
        bool wasEnabled = Enabled;
        Enabled = BasisSettingsDefaults.UseJiggleSimulationDistanceLod.RawValue;

        float cutoff = Mathf.Max(0f, BasisSettingsDefaults.JiggleSimulationLodDistance.RawValue);
        _cutoffSqr = cutoff * cutoff;

        if (wasEnabled && !Enabled)
        {
            RestoreAllRemotes();
        }
    }

    /// <summary>
    /// Whether a remote at this squared distance should be simulating, with hysteresis relative to
    /// whether it currently is. Always true when the feature is disabled.
    /// </summary>
    public static bool ShouldSimulate(float distanceSq, bool currentlySimulating)
    {
        if (!Enabled)
        {
            return true;
        }
        float threshold = currentlySimulating ? (_cutoffSqr * HysteresisSqr) : (_cutoffSqr / HysteresisSqr);
        return distanceSq < threshold;
    }

    private static void RestoreAllRemotes()
    {
        foreach (var kvp in BasisNetworkPlayers.RemotePlayers)
        {
            var driver = kvp.Value?.RemoteAvatarDriver;
            if (driver != null)
            {
                driver.SetJiggleSimulating(true);
            }
        }
    }
}
