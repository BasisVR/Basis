using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using UnityEngine;

/// <summary>
/// Distance-based reduction of how many bone influences are blended per vertex when skinning a
/// remote avatar. Remote SkinnedMeshRenderers are left on <see cref="SkinQuality.Auto"/>, which
/// resolves to the project quality level's skin weights (Unlimited), so every vertex of every
/// distant avatar is skinned against its full influence set. Past a few metres the difference
/// between four influences and one is not resolvable, so the extra weights are pure cost.
///
/// This rides the mesh LOD level that <see cref="BasisDistanceJobParallel"/> already computes, so
/// it costs no extra distance work — it only reacts when a remote crosses a LOD boundary.
/// </summary>
public static class BasisAvatarSkinLOD
{
    /// <summary>Master switch. When false every remote is restored to <see cref="SkinQuality.Auto"/>.</summary>
    public static bool Enabled;

    /// <summary>
    /// Skin quality per mesh LOD level (0 = closest, 3 = furthest). LOD 0 stays on Auto so the
    /// avatars the local player is actually close to keep whatever the quality level asks for.
    /// </summary>
    private static readonly SkinQuality[] QualityByLod =
    {
        SkinQuality.Auto,
        SkinQuality.Bone4,
        SkinQuality.Bone2,
        SkinQuality.Bone1,
    };

    /// <summary>Skin quality this LOD level should render at. Clamps out-of-range levels.</summary>
    public static SkinQuality QualityForLod(int lod) => QualityByLod[Mathf.Clamp(lod, 0, QualityByLod.Length - 1)];

    /// <summary>
    /// Pushes the current setting value into this module. Call once at startup and again on every
    /// settings change. On either enable edge it re-runs every remote, because the per-LOD apply is
    /// edge-triggered off LOD changes and a stationary crowd would otherwise never be revisited.
    /// </summary>
    public static void ApplyFromSettings()
    {
        bool wasEnabled = Enabled;
        Enabled = BasisSettingsDefaults.UseAvatarSkinLod.RawValue;

        if (wasEnabled != Enabled)
        {
            ReapplyAllRemotes();
        }
    }

    /// <summary>
    /// Applies the skin quality for <paramref name="lod"/> to a remote's skinned renderers.
    /// Takes the level explicitly rather than reading <see cref="BasisRemotePlayer.CurrentLodLevel"/>
    /// because the transmit tick applies the mesh LOD before it stores the new level.
    /// </summary>
    public static void Apply(BasisRemotePlayer remote, int lod)
    {
        if (remote == null)
        {
            return;
        }

        var driver = remote.RemoteAvatarDriver;
        if (driver != null)
        {
            Apply(driver.SkinnedMeshRenderer, driver.SkinnedMeshRendererLength, lod);
        }
    }

    /// <summary>
    /// Renderer-array overload, for callers that hold the renderers before the player's driver
    /// back-reference is established (avatar load).
    /// </summary>
    public static void Apply(SkinnedMeshRenderer[] renderers, int length, int lod)
    {
        if (renderers == null)
        {
            return;
        }

        SkinQuality quality = Enabled ? QualityForLod(lod) : SkinQuality.Auto;
        for (int index = 0; index < length; index++)
        {
            SkinnedMeshRenderer renderer = renderers[index];
            if (renderer != null && renderer.quality != quality)
            {
                renderer.quality = quality;
            }
        }
    }

    private static void ReapplyAllRemotes()
    {
        foreach (var kvp in BasisNetworkPlayers.RemotePlayers)
        {
            BasisRemotePlayer remote = kvp.Value;
            if (remote != null)
            {
                Apply(remote, remote.CurrentLodLevel);
            }
        }
    }
}
