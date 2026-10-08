using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Scripts.Rendering
{
    [AutoStaticsCleanup]
    public static partial class BasisLightingSolutions
    {
        public const string SolutionBasis = "Basis";
        public const string SolutionUnity = "Unity";
        [NoAutoStaticsCleanup] public static readonly string[] SolutionOptions = { SolutionBasis, SolutionUnity };
        [NoAutoStaticsCleanup] public static readonly string[] SolutionLabelKeys = { "settings.graphics.solution.basis", "settings.graphics.solution.unity" };
        [NoAutoStaticsCleanup] private static readonly Dictionary<ScriptableRendererFeature, bool> authored = new Dictionary<ScriptableRendererFeature, bool>();
        [NoAutoStaticsCleanup] private static readonly List<ScriptableRendererFeature> features = new List<ScriptableRendererFeature>();
        [NoAutoStaticsCleanup] private static readonly List<ScriptableRendererFeature> lookup = new List<ScriptableRendererFeature>();
        [NoAutoStaticsCleanup] private static readonly Dictionary<ScreenSpaceReflectionRendererFeature, bool> authoredAfterOpaque = new Dictionary<ScreenSpaceReflectionRendererFeature, bool>();
        private const string AuthoredActiveKey = "BasisLightingSolutions.authored.active.";
        private const string AuthoredAfterOpaqueKey = "BasisLightingSolutions.authored.afterOpaque.";
        private static bool installed, ambientOcclusionRunning, globalIlluminationRunning, reflectionsRunning;

        public static bool Installed => installed;
        public static bool UnityAmbientOcclusionRunning => ambientOcclusionRunning;
        public static bool UnityGlobalIlluminationRunning => globalIlluminationRunning;
        public static bool UnityReflectionsRunning => reflectionsRunning;

        public static bool IsUnity(string solution) => string.Equals(solution?.Trim(), SolutionUnity, StringComparison.OrdinalIgnoreCase);
        public static bool PicksUnityAmbientOcclusion(string solution) => IsUnity(solution) && HasUnityAmbientOcclusion;
        public static bool PicksUnityGlobalIllumination(string solution) => IsUnity(solution) && HasUnityGlobalIllumination;
        public static bool PicksUnityReflections(string solution) => IsUnity(solution) && HasUnityReflections;
        public static bool UnityAmbientOcclusion => BasisSettingsDefaults.UseRayTracedAmbientOcclusion.RawValue && PicksUnityAmbientOcclusion(BasisSettingsDefaults.AmbientOcclusionSolution.RawValue);
        public static bool BasisAmbientOcclusion => BasisSettingsDefaults.UseRayTracedAmbientOcclusion.RawValue && !PicksUnityAmbientOcclusion(BasisSettingsDefaults.AmbientOcclusionSolution.RawValue);
        public static bool UnityGlobalIllumination => BasisSettingsDefaults.UseGlobalIllumination.RawValue && PicksUnityGlobalIllumination(BasisSettingsDefaults.GlobalIlluminationSolution.RawValue);
        public static bool BasisGlobalIllumination => BasisSettingsDefaults.UseGlobalIllumination.RawValue && !PicksUnityGlobalIllumination(BasisSettingsDefaults.GlobalIlluminationSolution.RawValue);
        public static bool UnityReflections => BasisSettingsDefaults.GlobalIlluminationSpecular.RawValue && PicksUnityReflections(BasisSettingsDefaults.ReflectionsSolution.RawValue);
        public static bool BasisReflections => BasisSettingsDefaults.GlobalIlluminationSpecular.RawValue && !PicksUnityReflections(BasisSettingsDefaults.ReflectionsSolution.RawValue);

        public static bool IsSolutionKey(string key)
        {
            if (string.IsNullOrEmpty(key)) { return false; }
            string lowered = key.ToLowerInvariant();
            return lowered == BasisSettingsDefaults.UseRayTracedAmbientOcclusion.BindingKey
                || lowered == BasisSettingsDefaults.AmbientOcclusionSolution.BindingKey
                || lowered == BasisSettingsDefaults.UseGlobalIllumination.BindingKey
                || lowered == BasisSettingsDefaults.GlobalIlluminationSolution.BindingKey
                || lowered == BasisSettingsDefaults.GlobalIlluminationSpecular.BindingKey
                || lowered == BasisSettingsDefaults.ReflectionsSolution.BindingKey;
        }

        public static bool IsUnityLightingFeature(ScriptableRendererFeature feature)
        {
            if (feature is ScreenSpaceAmbientOcclusion || feature is ScreenSpaceReflectionRendererFeature) { return true; }
#if SURFACE_CACHE_SUPPORTED || UNITY_EDITOR
            if (feature is SurfaceCacheGIRendererFeature) { return true; }
#endif
            return false;
        }

        public static bool HasUnityAmbientOcclusion => HasFeature<ScreenSpaceAmbientOcclusion>();
        public static bool HasUnityReflections => HasFeature<ScreenSpaceReflectionRendererFeature>();
#if SURFACE_CACHE_SUPPORTED || UNITY_EDITOR
        public static bool HasUnityGlobalIllumination => HasFeature<SurfaceCacheGIRendererFeature>();
#else
        public static bool HasUnityGlobalIllumination => false;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Install()
        {
            if (installed) { return; }
            installed = true;
            BasisSettingsSystem.OnSettingChanged += OnSettingChanged;
            BasisSettingsSystem.OnSettingsFinishedChanges += Apply;
            RenderPipelineManager.activeRenderPipelineCreated -= Apply;
            RenderPipelineManager.activeRenderPipelineCreated += Apply;
            Application.quitting -= Uninstall;
            Application.quitting += Uninstall;
            Apply();
        }

        public static void Uninstall()
        {
            if (!installed) { return; }
            installed = false;
            BasisSettingsSystem.OnSettingChanged -= OnSettingChanged;
            BasisSettingsSystem.OnSettingsFinishedChanges -= Apply;
            RenderPipelineManager.activeRenderPipelineCreated -= Apply;
            Application.quitting -= Uninstall;
            RestoreAuthored();
        }

        private static void OnSettingChanged(string key, string value)
        {
            if (IsSolutionKey(key) || string.Equals(key, BasisUnityLightingSettings.SsrAfterOpaque.BindingKey, StringComparison.OrdinalIgnoreCase)) { Apply(); }
        }

        public static void Apply()
        {
            bool ambientOcclusion = UnityAmbientOcclusion, globalIllumination = UnityGlobalIllumination, reflections = UnityReflections;
            ambientOcclusionRunning = globalIlluminationRunning = reflectionsRunning = false;
            Collect(features);
            for (int index = 0; index < features.Count; index++)
            {
                ScriptableRendererFeature feature = features[index];
                if (!authored.ContainsKey(feature)) { authored.Add(feature, Captured(feature, AuthoredActiveKey, feature.isActive)); }
                if (feature is ScreenSpaceAmbientOcclusion)
                {
                    Switch(feature, ambientOcclusion);
                    ambientOcclusionRunning |= ambientOcclusion;
                }
                else if (feature is ScreenSpaceReflectionRendererFeature reflectionFeature)
                {
                    if (!authoredAfterOpaque.ContainsKey(reflectionFeature)) { authoredAfterOpaque.Add(reflectionFeature, Captured(reflectionFeature, AuthoredAfterOpaqueKey, reflectionFeature.afterOpaque)); }
                    reflectionFeature.afterOpaque = BasisUnityLightingSettings.SsrAfterOpaque.RawValue;
                    Switch(feature, reflections);
                    reflectionsRunning |= reflections;
                }
                else
                {
                    Switch(feature, globalIllumination);
                    globalIlluminationRunning |= globalIllumination;
                }
            }
        }

        public static void RestoreAuthored()
        {
            foreach (KeyValuePair<ScriptableRendererFeature, bool> entry in authored)
            {
                if (entry.Key == null) { continue; }
                Switch(entry.Key, entry.Value);
                Forget(entry.Key, AuthoredActiveKey);
            }
            foreach (KeyValuePair<ScreenSpaceReflectionRendererFeature, bool> entry in authoredAfterOpaque)
            {
                if (entry.Key == null) { continue; }
                entry.Key.afterOpaque = entry.Value;
                Forget(entry.Key, AuthoredAfterOpaqueKey);
            }
            authored.Clear();
            authoredAfterOpaque.Clear();
            ambientOcclusionRunning = globalIlluminationRunning = reflectionsRunning = false;
        }

        private static bool Captured(ScriptableRendererFeature feature, string prefix, bool current)
        {
#if UNITY_EDITOR
            string key = prefix + feature.GetEntityId();
            int saved = UnityEditor.SessionState.GetInt(key, -1);
            if (saved >= 0) { return saved == 1; }
            UnityEditor.SessionState.SetInt(key, current ? 1 : 0);
#endif
            return current;
        }

        private static void Forget(ScriptableRendererFeature feature, string prefix)
        {
#if UNITY_EDITOR
            UnityEditor.SessionState.EraseInt(prefix + feature.GetEntityId());
#endif
        }

        private static void Switch(ScriptableRendererFeature feature, bool active)
        {
            if (feature.isActive == active) { return; }
            feature.SetActive(active);
#if SURFACE_CACHE_SUPPORTED || UNITY_EDITOR
            if (!active && feature is SurfaceCacheGIRendererFeature) { feature.Create(); }
#endif
        }

        private static bool HasFeature<T>() where T : ScriptableRendererFeature
        {
            Collect(lookup);
            for (int index = 0; index < lookup.Count; index++)
            {
                if (lookup[index] is T) { return true; }
            }
            return false;
        }

        private static void Collect(List<ScriptableRendererFeature> into)
        {
            into.Clear();
            Collect(QualitySettings.renderPipeline as UniversalRenderPipelineAsset, into);
            Collect(GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset, into);
            Collect(GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset, into);
        }

        private static void Collect(UniversalRenderPipelineAsset asset, List<ScriptableRendererFeature> into)
        {
            if (asset == null) { return; }
            ReadOnlySpan<ScriptableRendererData> renderers = asset.rendererDataList;
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                ScriptableRendererData data = renderers[rendererIndex];
                if (data == null) { continue; }
                List<ScriptableRendererFeature> list = data.rendererFeatures;
                for (int featureIndex = 0; featureIndex < list.Count; featureIndex++)
                {
                    ScriptableRendererFeature feature = list[featureIndex];
                    if (feature != null && IsUnityLightingFeature(feature) && !into.Contains(feature)) { into.Add(feature); }
                }
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorPlayModeRestore()
        {
            UnityEditor.EditorApplication.playModeStateChanged -= OnEditorPlayModeStateChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnEditorPlayModeStateChanged;
            if (Application.isPlaying) { Install(); }
        }

        private static void OnEditorPlayModeStateChanged(UnityEditor.PlayModeStateChange change)
        {
            if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode) { Uninstall(); }
        }

        [NoAutoStaticsCleanup] private static readonly Dictionary<ScriptableRendererFeature, bool> beforeBuild = new Dictionary<ScriptableRendererFeature, bool>();

        public static void ActivateForBuild()
        {
            List<ScriptableRendererFeature> buildFeatures = new List<ScriptableRendererFeature>();
            for (int level = 0; level < QualitySettings.count; level++)
            {
                Collect(QualitySettings.GetRenderPipelineAssetAt(level) as UniversalRenderPipelineAsset, buildFeatures);
            }
            Collect(GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset, buildFeatures);
            for (int index = 0; index < buildFeatures.Count; index++)
            {
                ScriptableRendererFeature feature = buildFeatures[index];
                if (!beforeBuild.ContainsKey(feature)) { beforeBuild.Add(feature, feature.isActive); }
                feature.SetActive(true);
            }
            UnityEditor.EditorApplication.update -= RestoreOnceTheBuildEnds;
            UnityEditor.EditorApplication.update += RestoreOnceTheBuildEnds;
        }

        public static void RestoreAfterBuild()
        {
            UnityEditor.EditorApplication.update -= RestoreOnceTheBuildEnds;
            foreach (KeyValuePair<ScriptableRendererFeature, bool> entry in beforeBuild)
            {
                if (entry.Key != null) { Switch(entry.Key, entry.Value); }
            }
            beforeBuild.Clear();
        }

        private static void RestoreOnceTheBuildEnds()
        {
            if (!UnityEditor.BuildPipeline.isBuildingPlayer) { RestoreAfterBuild(); }
        }
#endif
    }
}
