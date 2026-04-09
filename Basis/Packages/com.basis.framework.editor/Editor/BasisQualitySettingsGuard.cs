#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ensures the 4 expected quality levels (DESKTOP, QUEST, IOS, HEADLESS) always exist
/// in ProjectSettings/QualitySettings.asset. Exiting play mode can sometimes corrupt
/// or delete quality profiles — this script regenerates them from code whenever they're wrong.
/// Runs on domain reload and when exiting play mode.
/// </summary>
[InitializeOnLoad]
public static class BasisQualitySettingsGuard
{
    private static readonly string[] ExpectedNames = { "DESKTOP", "QUEST", "IOS", "HEADLESS" };

    // URP pipeline asset paths (the folder name typo "Settiings" is intentional — that's the actual folder)
    private const string DesktopPipelinePath  = "Assets/Basis/Settings/Quality Settiings/Modified - Desktop.asset";
    private const string QuestPipelinePath    = "Assets/Basis/Settings/Quality Settiings/Modified - Android.asset";
    private const string IOSPipelinePath      = "Assets/Basis/Settings/Quality Settiings/Modified - IOS.asset";
    private const string HeadlessPipelinePath = "Assets/Basis/Settings/Quality Settiings/Modified - Headless.asset";

    static BasisQualitySettingsGuard()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.delayCall += Validate;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode)
            EditorApplication.delayCall += Validate;
    }

    [MenuItem("Basis/Quality Settings/Validate Now")]
    public static void Validate()
    {
        if (NamesMatch(QualitySettings.names))
            return;

        string actual = QualitySettings.names.Length == 0
            ? "(empty)"
            : string.Join(", ", QualitySettings.names);

        Debug.LogWarning(
            $"[BasisQualitySettingsGuard] Quality levels are wrong.\n" +
            $"  Expected: [{string.Join(", ", ExpectedNames)}]\n" +
            $"  Actual:   [{actual}]\n" +
            $"  Regenerating...");

        if (Regenerate())
            Debug.Log("[BasisQualitySettingsGuard] Quality settings regenerated successfully.");
    }

    [MenuItem("Basis/Quality Settings/Force Regenerate")]
    public static void ForceRegenerate()
    {
        if (Regenerate())
            Debug.Log("[BasisQualitySettingsGuard] Quality settings force-regenerated successfully.");
    }

    private static bool NamesMatch(string[] names)
    {
        if (names.Length != ExpectedNames.Length)
            return false;
        for (int i = 0; i < ExpectedNames.Length; i++)
        {
            if (names[i] != ExpectedNames[i])
                return false;
        }
        return true;
    }

    private static bool Regenerate()
    {
        string desktopGuid  = AssetDatabase.AssetPathToGUID(DesktopPipelinePath);
        string questGuid    = AssetDatabase.AssetPathToGUID(QuestPipelinePath);
        string iosGuid      = AssetDatabase.AssetPathToGUID(IOSPipelinePath);
        string headlessGuid = AssetDatabase.AssetPathToGUID(HeadlessPipelinePath);

        if (string.IsNullOrEmpty(desktopGuid) || string.IsNullOrEmpty(questGuid) ||
            string.IsNullOrEmpty(iosGuid)     || string.IsNullOrEmpty(headlessGuid))
        {
            Debug.LogError(
                "[BasisQualitySettingsGuard] Cannot regenerate — URP pipeline asset(s) missing:\n" +
                $"  Desktop:  {(string.IsNullOrEmpty(desktopGuid)  ? "MISSING" : "OK")} ({DesktopPipelinePath})\n" +
                $"  Quest:    {(string.IsNullOrEmpty(questGuid)    ? "MISSING" : "OK")} ({QuestPipelinePath})\n" +
                $"  IOS:      {(string.IsNullOrEmpty(iosGuid)      ? "MISSING" : "OK")} ({IOSPipelinePath})\n" +
                $"  Headless: {(string.IsNullOrEmpty(headlessGuid) ? "MISSING" : "OK")} ({HeadlessPipelinePath})");
            return false;
        }

        string yaml = BuildYaml(desktopGuid, questGuid, iosGuid, headlessGuid);
        string path = Path.Combine(Application.dataPath, "..", "ProjectSettings", "QualitySettings.asset");
        File.WriteAllText(path, yaml, new UTF8Encoding(false));

        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        return true;
    }

    // ────────────────────────────── YAML generation ──────────────────────────────

    private static string BuildYaml(string desktopGuid, string questGuid, string iosGuid, string headlessGuid)
    {
        var sb = new StringBuilder(4096);
        L(sb, "%YAML 1.1");
        L(sb, "%TAG !u! tag:unity3d.com,2011:");
        L(sb, "--- !u!47 &1");
        L(sb, "QualitySettings:");
        L(sb, "  m_ObjectHideFlags: 0");
        L(sb, "  serializedVersion: 5");
        L(sb, "  m_CurrentQuality: 0");
        L(sb, "  m_QualitySettings:");

        // Index 0 — DESKTOP  (Standalone + Linux)
        AppendLevel(sb, "DESKTOP", antiAliasing: 2, lodCrossFade: 1, pipelineGuid: desktopGuid,
            excludeAndroid: true, excludeServer: true, excludeIPhone: true, excludeStandalone: false);

        // Index 1 — QUEST  (Android)
        AppendLevel(sb, "QUEST", antiAliasing: 2, lodCrossFade: 0, pipelineGuid: questGuid,
            excludeAndroid: false, excludeServer: true, excludeIPhone: true, excludeStandalone: true);

        // Index 2 — IOS  (iPhone)
        AppendLevel(sb, "IOS", antiAliasing: 0, lodCrossFade: 0, pipelineGuid: iosGuid,
            excludeAndroid: true, excludeServer: true, excludeIPhone: false, excludeStandalone: true);

        // Index 3 — HEADLESS  (Server)
        AppendLevel(sb, "HEADLESS", antiAliasing: 0, lodCrossFade: 0, pipelineGuid: headlessGuid,
            excludeAndroid: true, excludeServer: false, excludeIPhone: true, excludeStandalone: true);

        L(sb, "  m_TextureMipmapLimitGroupNames: []");
        L(sb, "  m_PerPlatformDefaultQuality:");
        L(sb, "    Android: 0");
        L(sb, "    Nintendo Switch 2: 0");
        L(sb, "    Server: 0");
        L(sb, "    Standalone: 0");
        L(sb, "    iPhone: 0");

        return sb.ToString();
    }

    private static void AppendLevel(
        StringBuilder sb, string name,
        int antiAliasing, int lodCrossFade, string pipelineGuid,
        bool excludeAndroid, bool excludeServer, bool excludeIPhone, bool excludeStandalone)
    {
        L(sb, "  - serializedVersion: 5");
        L(sb, $"    name: {name}");
        L(sb, "    pixelLightCount: 1");
        L(sb, "    shadows: 2");
        L(sb, "    shadowResolution: 2");
        L(sb, "    shadowProjection: 1");
        L(sb, "    shadowCascades: 4");
        L(sb, "    shadowDistance: 150");
        L(sb, "    shadowNearPlaneOffset: 3");
        L(sb, "    shadowCascade2Split: 0.33333334");
        L(sb, "    shadowCascade4Split: {x: 0.06666667, y: 0.2, z: 0.46666667}");
        L(sb, "    shadowmaskMode: 1");
        L(sb, "    skinWeights: 255");
        L(sb, "    globalTextureMipmapLimit: 0");
        L(sb, "    textureMipmapLimitSettings: []");
        L(sb, "    anisotropicTextures: 1");
        L(sb, $"    antiAliasing: {antiAliasing}");
        L(sb, "    softParticles: 1");
        L(sb, "    softVegetation: 1");
        L(sb, "    realtimeReflectionProbes: 1");
        L(sb, "    billboardsFaceCameraPosition: 1");
        L(sb, "    useLegacyDetailDistribution: 0");
        L(sb, "    adaptiveVsync: 0");
        L(sb, "    vSyncCount: 0");
        L(sb, "    realtimeGICPUUsage: 100");
        L(sb, "    adaptiveVsyncExtraA: 0");
        L(sb, "    adaptiveVsyncExtraB: 0");
        L(sb, "    lodBias: 2");
        L(sb, "    meshLodThreshold: 0.01");
        L(sb, "    maximumLODLevel: 0");
        L(sb, $"    enableLODCrossFade: {lodCrossFade}");
        L(sb, "    streamingMipmapsActive: 1");
        L(sb, "    streamingMipmapsAddAllCameras: 1");
        L(sb, "    streamingMipmapsMemoryBudget: 24138");
        L(sb, "    streamingMipmapsRenderersPerFrame: 512");
        L(sb, "    streamingMipmapsMaxLevelReduction: 4");
        L(sb, "    streamingMipmapsMaxFileIORequests: 512");
        L(sb, "    particleRaycastBudget: 4096");
        L(sb, "    asyncUploadTimeSlice: 2");
        L(sb, "    asyncUploadBufferSize: 16");
        L(sb, "    asyncUploadPersistentBuffer: 1");
        L(sb, "    resolutionScalingFixedDPIFactor: 1");
        L(sb, $"    customRenderPipeline: {{fileID: 11400000, guid: {pipelineGuid}, type: 2}}");
        L(sb, "    terrainQualityOverrides: 0");
        L(sb, "    terrainPixelError: 1");
        L(sb, "    terrainDetailDensityScale: 1");
        L(sb, "    terrainBasemapDistance: 1000");
        L(sb, "    terrainDetailDistance: 80");
        L(sb, "    terrainTreeDistance: 5000");
        L(sb, "    terrainBillboardStart: 50");
        L(sb, "    terrainFadeLength: 5");
        L(sb, "    terrainMaxTrees: 50");
        L(sb, "    excludedTargetPlatforms:");

        if (excludeAndroid)    L(sb, "    - Android");
        if (excludeServer)     L(sb, "    - Server");
        if (excludeIPhone)     L(sb, "    - iPhone");
        if (excludeStandalone) L(sb, "    - Standalone");
    }

    private static void L(StringBuilder sb, string line)
    {
        sb.Append(line);
        sb.Append('\n');
    }
}
#endif
