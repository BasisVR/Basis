using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class BasisGlobalIlluminationSurfaceTags
{
    public const string OutputKeyword = "_BASIS_SURFACE_MRT";
    public const string OwnPassTag = "BasisSurface";
    public const int RenderersPerFrame = 512;
    public const float RescanSeconds = 0.5f;

    [NoAutoStaticsCleanup] private static readonly Dictionary<Shader, bool> capableShaders = new Dictionary<Shader, bool>();
    [NoAutoStaticsCleanup] private static readonly List<Material> materialScratch = new List<Material>();
    [NoAutoStaticsCleanup] private static readonly ShaderTagId lightModeTag = new ShaderTagId("LightMode");
    private static Renderer[] snapshot;
    private static int cursor;
    private static float snapshotTime = float.NegativeInfinity;
    [NoAutoStaticsCleanup] private static readonly int emissionColorId = Shader.PropertyToID("_EmissionColor");
    private static int overrideBit = -2, emissiveBit = -2;
    public static bool Enabled = true;

    public static uint Mask
    {
        get
        {
            if (!Enabled) { return 0u; }
            if (overrideBit == -2) { PickBits(); }
            return overrideBit < 0 ? 0u : 1u << overrideBit;
        }
    }

    public static uint EmissiveMask
    {
        get
        {
            if (!Enabled) { return 0u; }
            if (overrideBit == -2) { PickBits(); }
            return emissiveBit < 0 ? 0u : 1u << emissiveBit;
        }
    }

    private static void PickBits()
    {
        int defined = RenderingLayerMask.GetRenderingLayerCount();
        overrideBit = emissiveBit = -1;
        for (int candidate = 31; candidate >= 1; candidate--)
        {
            bool free = candidate >= defined || string.IsNullOrEmpty(RenderingLayerMask.RenderingLayerToName(candidate));
            if (!free) { continue; }
            if (overrideBit < 0) { overrideBit = candidate; continue; }
            emissiveBit = candidate;
            break;
        }
    }

    public static bool IsEmissive(Material material)
    {
        return material.IsKeywordEnabled("_EMISSION")
            && (material.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0
            && material.HasColor(emissionColorId)
            && material.GetColor(emissionColorId).maxColorComponent > 0.001f;
    }

    public static bool IsCapable(Shader shader)
    {
        if (shader == null) { return false; }
        if (capableShaders.TryGetValue(shader, out bool capable)) { return capable; }
        capable = shader.keywordSpace.FindKeyword(OutputKeyword).isValid && HasOwnPass(shader);
        capableShaders[shader] = capable;
        return capable;
    }

    private static bool HasOwnPass(Shader shader)
    {
        int passes = shader.passCount;
        for (int index = 0; index < passes; index++)
        {
            if (shader.FindPassTagValue(index, lightModeTag).name == OwnPassTag) { return true; }
        }
        return false;
    }

    public static bool IsCapable(Renderer renderer)
    {
        if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) { return false; }
        renderer.GetSharedMaterials(materialScratch);
        bool any = false;
        for (int index = 0; index < materialScratch.Count; index++)
        {
            Material material = materialScratch[index];
            if (material == null) { continue; }
            if (!IsCapable(material.shader)) { materialScratch.Clear(); return false; }
            any = true;
        }
        materialScratch.Clear();
        return any;
    }

    public static void Apply(Renderer renderer)
    {
        uint overrideMask = Mask, emissiveMask = EmissiveMask;
        if ((overrideMask | emissiveMask) == 0u || renderer == null) { return; }
        bool capable = renderer is MeshRenderer || renderer is SkinnedMeshRenderer;
        bool any = false, emissive = false;
        renderer.GetSharedMaterials(materialScratch);
        for (int index = 0; index < materialScratch.Count; index++)
        {
            Material material = materialScratch[index];
            if (material == null) { continue; }
            any = true;
            if (capable && !IsCapable(material.shader)) { capable = false; }
            if (!emissive && IsEmissive(material)) { emissive = true; }
        }
        materialScratch.Clear();
        capable &= any;
        uint current = renderer.renderingLayerMask;
        uint wanted = current;
        if (overrideMask != 0u) { wanted = capable ? wanted & ~overrideMask : wanted | overrideMask; }
        if (emissiveMask != 0u) { wanted = emissive ? wanted | emissiveMask : wanted & ~emissiveMask; }
        if (wanted != current) { renderer.renderingLayerMask = wanted; }
    }

    public static void Tick()
    {
        if ((Mask | EmissiveMask) == 0u) { return; }
        float now = Time.realtimeSinceStartup;
        if (snapshot == null || now < snapshotTime || (cursor >= snapshot.Length && now - snapshotTime >= RescanSeconds))
        {
            snapshot = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            snapshotTime = now;
            capableShaders.Clear();
            cursor = 0;
        }
        int end = Mathf.Min(cursor + RenderersPerFrame, snapshot.Length);
        for (; cursor < end; cursor++) { Apply(snapshot[cursor]); }
    }

    public static void TagAllNow()
    {
        snapshot = null;
        Tick();
        while (snapshot != null && cursor < snapshot.Length) { Tick(); }
    }

    public static void Forget()
    {
        capableShaders.Clear();
        snapshot = null;
        cursor = 0;
        snapshotTime = float.NegativeInfinity;
    }
}
