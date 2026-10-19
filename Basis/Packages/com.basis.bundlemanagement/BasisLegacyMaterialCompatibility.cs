using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime material migrations for content serialized by older Unity versions. AssetBundle
/// materials do not pass through URP's editor-only MaterialPostprocessor when loaded.
/// </summary>
public static class BasisLegacyMaterialCompatibility
{
    private const int DirectEmissionIntroducedUnityMajor = 6000;
    private const int DirectEmissionIntroducedUnityMinor = 7;
    private const string EmissionKeyword = "_EMISSION";
    private const MaterialGlobalIlluminationFlags AllEmissionFlags =
        MaterialGlobalIlluminationFlags.RealtimeDirectEmission |
        MaterialGlobalIlluminationFlags.RealtimeIndirectEmission |
        MaterialGlobalIlluminationFlags.BakedEmission;

    public static int UpgradeEmission(IList<Renderer> renderers, string builtWithUnityVersion)
    {
        if (renderers == null || !RequiresLegacyEmissionUpgrade(builtWithUnityVersion))
            return 0;

        HashSet<Material> visited = new HashSet<Material>();
        List<Material> materials = new List<Material>(8);
        int upgraded = 0;
        string firstUpgrade = null;
        for (int rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (renderer == null)
                continue;

            materials.Clear();
            renderer.GetSharedMaterials(materials);
            for (int materialIndex = 0; materialIndex < materials.Count; materialIndex++)
            {
                Material material = materials[materialIndex];
                if (material == null || !visited.Add(material))
                    continue;

                MaterialGlobalIlluminationFlags flags = material.globalIlluminationFlags;
                if ((flags & AllEmissionFlags) == AllEmissionFlags &&
                    (flags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0)
                    continue;

                bool emissiveIsBlack =
                    (flags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) != 0;
                bool hadLegacyEmissionIntent = material.IsKeywordEnabled(EmissionKeyword) ||
                    (!emissiveIsBlack && (flags & AllEmissionFlags) != 0);
                // EmissiveIsBlack is Unity's explicit "emission disabled" state, even when a
                // stale BakedEmission bit is also present. Only an existing _EMISSION keyword
                // is strong enough to override it.
                if (!hadLegacyEmissionIntent)
                    continue;

                material.globalIlluminationFlags = (flags & ~MaterialGlobalIlluminationFlags.EmissiveIsBlack) |
                                                   AllEmissionFlags;
                material.EnableKeyword(EmissionKeyword);
                MaterialGlobalIlluminationFlags readback = material.globalIlluminationFlags;
                if (firstUpgrade == null)
                {
                    firstUpgrade = $"{material.name} ({material.shader?.name ?? "missing shader"}) {flags} -> {readback}, keyword={material.IsKeywordEnabled(EmissionKeyword)}";
                }
                upgraded++;
            }
        }

        if (upgraded > 0)
        {
            BasisDebug.Log(
                $"AssetBundle emission compatibility scan (Unity {builtWithUnityVersion ?? "unknown"}): " +
                $"renderers={renderers.Count}, materials={visited.Count}, upgraded={upgraded}" +
                (firstUpgrade == null ? "." : $". First: {firstUpgrade}"),
                BasisDebug.LogTag.Event);
        }
        return upgraded;
    }

    public static bool RequiresLegacyEmissionUpgrade(string unityVersion)
    {
        // Bundles built before Basis started retaining the parsed header version are also legacy.
        if (string.IsNullOrWhiteSpace(unityVersion))
            return true;

        string[] parts = unityVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            return true;

        return major < DirectEmissionIntroducedUnityMajor ||
               (major == DirectEmissionIntroducedUnityMajor && minor < DirectEmissionIntroducedUnityMinor);
    }
}
