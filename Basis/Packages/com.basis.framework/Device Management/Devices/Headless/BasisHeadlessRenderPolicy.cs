using Unity.Scripting.LifecycleManagement;
#if UNITY_SERVER
using System;
using UnityEngine;

/// <summary>
/// Clamps the render-side quality knobs for headless builds.
///
/// The project ships a HEADLESS quality level, but ProjectSettings pins
/// <c>m_PerPlatformDefaultQuality.Server = 0</c>, so a server build actually boots on
/// DESKTOP — full-resolution textures, shadows and realtime reflection probes, none of
/// which are ever rasterized. This runs before the first scene loads so the mipmap limit
/// is already in place when the first avatar bundle is read: Unity drops the top mips at
/// load time, so the memory is never allocated rather than freed later.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisHeadlessRenderPolicy
{
    /// <summary>Name of the quality level to select when present.</summary>
    public const string HeadlessQualityLevelName = "HEADLESS";

    /// <summary>
    /// Mip levels discarded at load. 4 keeps a 1024x1024 source at 64x64 — enough that a
    /// texture still exists for any code that inspects it, at 1/256th the bytes.
    /// </summary>
    public static int TextureMipmapLimit = 4;

    /// <summary>Set false via config before startup to leave quality settings untouched.</summary>
    public static bool Enabled = true;

    private static bool applied;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ApplyOnBoot()
    {
        Apply();
    }

    public static void Apply()
    {
        if (!Enabled || applied)
        {
            return;
        }

        applied = true;

        try
        {
            SelectHeadlessQualityLevel();

            QualitySettings.globalTextureMipmapLimit = Mathf.Max(0, TextureMipmapLimit);

            // Mipmap streaming needs a camera to compute desired mip levels. Headless renders
            // nothing, so the system only adds bookkeeping — and the shipped budget is 24 GB,
            // which caps nothing at all.
            QualitySettings.streamingMipmapsActive = false;

            // URP owns shadows/AA through its pipeline asset, so these are belt-and-braces for a
            // built-in-pipeline fallback. The knobs above and below are engine-level and apply
            // regardless of the active render pipeline — those are the ones that hold memory.
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.shadowDistance = 0f;
            QualitySettings.realtimeReflectionProbes = false;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
            QualitySettings.antiAliasing = 0;
            QualitySettings.softParticles = false;
            QualitySettings.billboardsFaceCameraPosition = false;

            // One bone influence per vertex. Nothing is skinned for display, and the weight
            // count feeds mesh upload sizing.
            QualitySettings.skinWeights = SkinWeights.OneBone;

            // Always take the cheapest LOD that exists.
            QualitySettings.lodBias = 0.01f;

            QualitySettings.vSyncCount = 0;

            BasisDebug.Log(
                $"Headless render policy applied: quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}', " +
                $"mipmap limit {QualitySettings.globalTextureMipmapLimit}, shadows off, streaming off.",
                BasisDebug.LogTag.Device);
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"Headless render policy failed to apply: {ex.Message}", BasisDebug.LogTag.Device);
        }
    }

    private static void SelectHeadlessQualityLevel()
    {
        string[] names = QualitySettings.names;
        for (int index = 0; index < names.Length; index++)
        {
            if (!string.Equals(names[index], HeadlessQualityLevelName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (QualitySettings.GetQualityLevel() != index)
            {
                // applyExpensiveChanges: false — the expensive path recreates render targets,
                // which is pointless with no graphics device attached.
                QualitySettings.SetQualityLevel(index, false);
            }

            return;
        }

        // Expected on a server build: the HEADLESS level lists Standalone in
        // excludedTargetPlatforms, so Unity strips it from the player and ProjectSettings pins
        // Server to index 0 (DESKTOP) because HEADLESS is not selectable there. The overrides
        // above are what actually hold memory down, and they apply to whatever level is active,
        // so this is informational rather than a failure.
        BasisDebug.Log(
            $"Headless quality level '{HeadlessQualityLevelName}' not present in this build; applying overrides on top of '{names[QualitySettings.GetQualityLevel()]}'.",
            BasisDebug.LogTag.Device);
    }
}
#endif
