using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Play mode tool for judging the GI on its own: strips the baked lightmaps and baked light probes from
/// whatever world is loaded (worlds arrive from bundles at runtime, so this cannot be done on the asset),
/// optionally turns the world's baked lights into realtime ones so the room is not left black, and puts all
/// of it back on Restore or when play mode ends.
/// </summary>
public static class BasisBakedLightingToggle
{
    private const string MenuStrip = "Basis/Rendering/Baked Lighting/Strip Baked Lightmaps";
    private const string MenuStripRealtime = "Basis/Rendering/Baked Lighting/Strip Baked Lightmaps + Make Baked Lights Realtime";
    private const string MenuRestore = "Basis/Rendering/Baked Lighting/Restore";

    private struct RendererState { public Renderer renderer; public int index; public Vector4 scaleOffset; }
    private struct TerrainState { public Terrain terrain; public int index; public Vector4 scaleOffset; }
    private struct LightState { public Light light; public LightBakingOutput output; }

    private static LightmapData[] savedLightmaps;
    private static LightProbes savedProbes;
    private static readonly List<RendererState> savedRenderers = new List<RendererState>();
    private static readonly List<TerrainState> savedTerrains = new List<TerrainState>();
    private static readonly List<LightState> savedLights = new List<LightState>();
    private static bool stripped;

    [InitializeOnLoadMethod]
    private static void Hook()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode && stripped) { Restore(); }
        if (change == PlayModeStateChange.EnteredEditMode) { Clear(); }
    }

    [MenuItem(MenuStrip, true)] [MenuItem(MenuStripRealtime, true)]
    private static bool CanStrip() { return EditorApplication.isPlaying && !stripped; }

    [MenuItem(MenuRestore, true)]
    private static bool CanRestore() { return EditorApplication.isPlaying && stripped; }

    [MenuItem(MenuStrip, false, 200)]
    private static void StripOnly() { Strip(false); }

    [MenuItem(MenuStripRealtime, false, 201)]
    private static void StripRealtime() { Strip(true); }

    public static void Strip(bool bakedLightsToRealtime)
    {
        if (stripped) { return; }
        Clear();
        savedLightmaps = LightmapSettings.lightmaps;
        savedProbes = LightmapSettings.lightProbes;

        int renderers = 0, terrains = 0, lights = 0;
        foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (renderer.lightmapIndex < 0 || renderer.lightmapIndex >= 0xFFFE) { continue; }
            savedRenderers.Add(new RendererState { renderer = renderer, index = renderer.lightmapIndex, scaleOffset = renderer.lightmapScaleOffset });
            renderer.lightmapIndex = -1;
            renderers++;
        }
        foreach (Terrain terrain in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (terrain.lightmapIndex < 0 || terrain.lightmapIndex >= 0xFFFE) { continue; }
            savedTerrains.Add(new TerrainState { terrain = terrain, index = terrain.lightmapIndex, scaleOffset = terrain.lightmapScaleOffset });
            terrain.lightmapIndex = -1;
            terrains++;
        }
        if (bakedLightsToRealtime)
        {
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                LightBakingOutput output = light.bakingOutput;
                if (!output.isBaked && output.lightmapBakeType == LightmapBakeType.Realtime) { continue; }
                savedLights.Add(new LightState { light = light, output = output });
                light.bakingOutput = new LightBakingOutput { isBaked = false, lightmapBakeType = LightmapBakeType.Realtime, mixedLightingMode = MixedLightingMode.IndirectOnly, probeOcclusionLightIndex = -1, occlusionMaskChannel = -1 };
                lights++;
            }
        }

        LightmapSettings.lightmaps = new LightmapData[0];
        LightmapSettings.lightProbes = null;
        stripped = true;
        Debug.Log($"[BasisBakedLighting] stripped {savedLightmaps.Length} lightmaps from {renderers} renderers and {terrains} terrains, removed baked light probes{(bakedLightsToRealtime ? $", made {lights} baked lights realtime" : string.Empty)}.");
    }

    [MenuItem(MenuRestore, false, 220)]
    public static void Restore()
    {
        if (!stripped) { return; }
        LightmapSettings.lightmaps = savedLightmaps;
        LightmapSettings.lightProbes = savedProbes;
        foreach (RendererState state in savedRenderers)
        {
            if (state.renderer == null) { continue; }
            state.renderer.lightmapIndex = state.index;
            state.renderer.lightmapScaleOffset = state.scaleOffset;
        }
        foreach (TerrainState state in savedTerrains)
        {
            if (state.terrain == null) { continue; }
            state.terrain.lightmapIndex = state.index;
            state.terrain.lightmapScaleOffset = state.scaleOffset;
        }
        foreach (LightState state in savedLights)
        {
            if (state.light != null) { state.light.bakingOutput = state.output; }
        }
        Debug.Log($"[BasisBakedLighting] restored {savedLightmaps?.Length ?? 0} lightmaps, {savedRenderers.Count} renderers, {savedTerrains.Count} terrains, {savedLights.Count} lights.");
        Clear();
    }

    private static void Clear()
    {
        savedRenderers.Clear();
        savedTerrains.Clear();
        savedLights.Clear();
        savedLightmaps = null;
        savedProbes = null;
        stripped = false;
    }
}
