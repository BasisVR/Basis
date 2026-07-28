using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Distance-based suppression of shadow casting on remote avatars. Nothing in Basis has ever
/// written <see cref="Renderer.shadowCastingMode"/> on a remote renderer, so every avatar in the
/// instance casts into the shadow map at any distance — a full extra skinned draw per shadow
/// cascade, per avatar. Past a dozen metres that shadow is a handful of pixels.
///
/// Rides the mesh LOD level <see cref="BasisDistanceJobParallel"/> already computes, so it adds no
/// distance work of its own and only reacts when a remote crosses a LOD boundary.
/// </summary>
public static class BasisAvatarShadowLOD
{
    /// <summary>Master switch. When false every remote is restored to its authored modes.</summary>
    public static bool Enabled;

    /// <summary>
    /// Whether a remote at this mesh LOD level still casts shadows (0 = closest, 3 = furthest).
    /// Shadows survive the two near levels, which on the Windows defaults is roughly the first 14 m.
    /// </summary>
    private static readonly bool[] CastsByLod = { true, true, false, false };

    /// <summary>Whether this LOD level should cast shadows. Clamps out-of-range levels.</summary>
    public static bool CastsAtLod(int lod) => CastsByLod[Mathf.Clamp(lod, 0, CastsByLod.Length - 1)];

    /// <summary>
    /// Snapshots the authored <see cref="Renderer.shadowCastingMode"/> of every renderer on the
    /// avatar. Must be called at avatar load, before any reduction is applied, and must be the only
    /// source of restore values: authors deliberately use <see cref="ShadowCastingMode.Off"/> and
    /// <see cref="ShadowCastingMode.ShadowsOnly"/>, so blanket-restoring to
    /// <see cref="ShadowCastingMode.On"/> would light up shadow-proxy meshes that are supposed to be
    /// invisible.
    /// </summary>
    public static void Capture(BasisRemotePlayer remote)
    {
        var renders = remote == null || remote.BasisAvatar == null ? null : remote.BasisAvatar.Renders;
        if (renders == null)
        {
            if (remote != null)
            {
                remote.AuthoredShadowCasting = null;
            }
            return;
        }

        int length = renders.Length;
        var authored = new ShadowCastingMode[length];
        for (int index = 0; index < length; index++)
        {
            Renderer renderer = renders[index];
            authored[index] = renderer != null ? renderer.shadowCastingMode : ShadowCastingMode.Off;
        }
        remote.AuthoredShadowCasting = authored;
    }

    /// <summary>
    /// Pushes the current setting value into this module. Call once at startup and again on every
    /// settings change. On either enable edge it re-runs every remote, because the per-LOD apply is
    /// edge-triggered off LOD changes and a stationary crowd would otherwise never be revisited.
    /// </summary>
    public static void ApplyFromSettings()
    {
        bool wasEnabled = Enabled;
        Enabled = BasisSettingsDefaults.UseAvatarShadowLod.RawValue;

        if (wasEnabled != Enabled)
        {
            ReapplyAllRemotes();
        }
    }

    /// <summary>
    /// Applies the shadow-casting decision for <paramref name="lod"/>. Takes the level explicitly
    /// rather than reading <see cref="BasisRemotePlayer.CurrentLodLevel"/> because the transmit tick
    /// applies the mesh LOD before it stores the new level.
    /// </summary>
    public static void Apply(BasisRemotePlayer remote, int lod)
    {
        if (remote == null || remote.BasisAvatar == null)
        {
            return;
        }

        var renders = remote.BasisAvatar.Renders;
        var authored = remote.AuthoredShadowCasting;

        // A length mismatch means the snapshot is stale relative to the renderer set. Leaving the
        // shadows alone is recoverable; indexing a stale snapshot would assign the wrong authored
        // mode to the wrong renderer and stick that way until the avatar reloads.
        if (renders == null || authored == null || authored.Length != renders.Length)
        {
            return;
        }

        bool casts = !Enabled || CastsAtLod(lod);
        int length = renders.Length;
        for (int index = 0; index < length; index++)
        {
            Renderer renderer = renders[index];
            if (renderer == null)
            {
                continue;
            }

            ShadowCastingMode target = casts ? authored[index] : ShadowCastingMode.Off;
            if (renderer.shadowCastingMode != target)
            {
                renderer.shadowCastingMode = target;
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
