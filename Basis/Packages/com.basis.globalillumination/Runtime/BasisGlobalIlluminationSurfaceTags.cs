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
    private static int bit = -2;
    public static bool Enabled = true;

    public static uint Mask
    {
        get
        {
            if (!Enabled) { return 0u; }
            if (bit == -2) { bit = PickBit(); }
            return bit < 0 ? 0u : 1u << bit;
        }
    }

    private static int PickBit()
    {
        int defined = RenderingLayerMask.GetRenderingLayerCount();
        if (defined < 32) { return 31; }
        for (int candidate = 31; candidate >= 1; candidate--)
        {
            if (string.IsNullOrEmpty(RenderingLayerMask.RenderingLayerToName(candidate))) { return candidate; }
        }
        return -1;
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
        uint mask = Mask;
        if (mask == 0u || renderer == null) { return; }
        uint current = renderer.renderingLayerMask;
        uint wanted = IsCapable(renderer) ? current & ~mask : current | mask;
        if (wanted != current) { renderer.renderingLayerMask = wanted; }
    }

    public static void Tick()
    {
        if (Mask == 0u) { return; }
        float now = Time.realtimeSinceStartup;
        if (snapshot == null || (cursor >= snapshot.Length && now - snapshotTime >= RescanSeconds))
        {
            snapshot = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            snapshotTime = now;
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
