using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Setup.Modules
{
    public sealed class BasisQualitySetup : BasisSetupModuleBase
    {
        private const string Folder = "Assets/Basis/Settings/Quality Settiings";

        private const string DesktopPath = Folder + "/Modified - Desktop.asset";
        private const string AndroidPath = Folder + "/Modified - Android.asset";
        private const string IOSPath = Folder + "/Modified - IOS.asset";
        private const string HeadlessPath = Folder + "/Modified - Headless.asset";

        private const string DesktopGuid = "7b7fd9122c28c4d15b667c7040e3b3fd";
        private const string AndroidGuid = "45a5c8d499b4a4b409944db2ed30cdc5";
        private const string IOSGuid = "68d60043d42ed5c4f8f86c4449e87f2f";
        private const string HeadlessGuid = "3d69cec2d01a4784ea4ca3626648005d";

        private const string DesktopRendererGuid = "c40be3174f62c4acf8c1216858c64956";
        private const string DesktopCameraRendererGuid = "d72f5d4ed70e6af45910472d0b56540f";
        private const string QuestRendererGuid = "dc6efa2bbf91054468277b399108bb90";
        private const string HeadlessRendererGuid = "a998016ed30342e4aa3a3687e625caa6";

        private const string DefaultVolumeGuid = "fe38da480e5432d4096cdaf1f3e9fdc1";
        private const string DesktopVolumeGuid = "c472becb8ef443eeb97e31b87151cadc";

        private struct PipelineConfig
        {
            public bool SupportsHDR;
            public int HdrPrecision;
            public int Msaa;
            public float RenderScale;
            public bool RequireDepth;
            public bool RequireOpaque;
            public int OpaqueDownsampling;
            public int MainLightMode;
            public int MainLightShadowRes;
            public int AdditionalLightsMode;
            public int AdditionalLightsShadowRes;
            public int ShadowCascadeCount;
            public bool SoftShadows;
            public int SoftShadowQuality;
            public bool LodCrossFade;
            public int LightProbeSystem;
            public int ProbeVolumeBudget;
            public int ColorGradingMode;
            public bool LightCookies;
            public int GpuResidentDrawerMode;
            public int VolumeUpdateMode;
            public bool MixedLighting;
            public int CookieFormat;
        }

        public override string Key => "quality.pipelines";
        public override string DisplayName => "Quality Pipeline Assets (per platform)";
        public override string Category => "Quality";
        public override int Version => 1;
        public override int Order => 20;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(DesktopPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            BasisSetupIO.EnsureAssetFolder(Folder);

            bool changed = false;
            changed |= BuildPipeline(DesktopPath, DesktopGuid, DesktopRendererGuid, DesktopCameraRendererGuid,
                DesktopVolumeGuid, mode, DesktopConfig());
            changed |= BuildPipeline(AndroidPath, AndroidGuid, QuestRendererGuid, null,
                DefaultVolumeGuid, mode, AndroidConfig());
            changed |= BuildPipeline(IOSPath, IOSGuid, QuestRendererGuid, null,
                DefaultVolumeGuid, mode, IOSConfig());
            changed |= BuildPipeline(HeadlessPath, HeadlessGuid, HeadlessRendererGuid, null,
                DefaultVolumeGuid, mode, HeadlessConfig());

            AssignProjectDefaultPipeline();
            RegenerateQualityLevels();
            return changed;
        }

        private bool BuildPipeline(string path, string guid, string rendererGuid, string secondRendererGuid,
            string volumeGuid, BasisSetupMode mode, PipelineConfig config)
        {
            UniversalRenderPipelineAsset existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            bool created = existing == null;
            if (!created && mode == BasisSetupMode.EnsureExists)
            {
                return false;
            }

            UniversalRenderPipelineAsset pipeline;
            if (created)
            {
                UniversalRendererData first = LoadByGuid<UniversalRendererData>(rendererGuid);
                pipeline = UniversalRenderPipelineAsset.Create(first);
                BasisSetupIO.EnsureParentFolder(path);
                AssetDatabase.CreateAsset(pipeline, path);
            }
            else
            {
                BasisSetupIO.BackupIfExists(path);
                pipeline = existing;
            }

            SerializedObject so = new SerializedObject(pipeline);

            SerializedProperty list = so.FindProperty("m_RendererDataList");
            if (list != null)
            {
                list.arraySize = string.IsNullOrEmpty(secondRendererGuid) ? 1 : 2;
                list.GetArrayElementAtIndex(0).objectReferenceValue = LoadByGuid<UniversalRendererData>(rendererGuid);
                if (!string.IsNullOrEmpty(secondRendererGuid))
                {
                    list.GetArrayElementAtIndex(1).objectReferenceValue = LoadByGuid<UniversalRendererData>(secondRendererGuid);
                }
            }

            SetObject(so, "m_VolumeProfile", LoadByGuid<VolumeProfile>(volumeGuid));

            SetBool(so, "m_SupportsHDR", config.SupportsHDR);
            SetInt(so, "m_HDRColorBufferPrecision", config.HdrPrecision);
            SetInt(so, "m_MSAA", config.Msaa);
            SetFloat(so, "m_RenderScale", config.RenderScale);
            SetBool(so, "m_RequireDepthTexture", config.RequireDepth);
            SetBool(so, "m_RequireOpaqueTexture", config.RequireOpaque);
            SetInt(so, "m_OpaqueDownsampling", config.OpaqueDownsampling);
            SetInt(so, "m_MainLightRenderingMode", config.MainLightMode);
            SetInt(so, "m_MainLightShadowmapResolution", config.MainLightShadowRes);
            SetInt(so, "m_AdditionalLightsRenderingMode", config.AdditionalLightsMode);
            SetInt(so, "m_AdditionalLightsShadowmapResolution", config.AdditionalLightsShadowRes);
            SetInt(so, "m_ShadowCascadeCount", config.ShadowCascadeCount);
            SetBool(so, "m_SoftShadowsSupported", config.SoftShadows);
            SetInt(so, "m_SoftShadowQuality", config.SoftShadowQuality);
            SetBool(so, "m_EnableLODCrossFade", config.LodCrossFade);
            SetInt(so, "m_LightProbeSystem", config.LightProbeSystem);
            SetInt(so, "m_ProbeVolumeMemoryBudget", config.ProbeVolumeBudget);
            SetInt(so, "m_ColorGradingMode", config.ColorGradingMode);
            SetBool(so, "m_SupportsLightCookies", config.LightCookies);
            SetInt(so, "m_GPUResidentDrawerMode", config.GpuResidentDrawerMode);
            SetInt(so, "m_VolumeFrameworkUpdateMode", config.VolumeUpdateMode);
            SetBool(so, "m_MixedLightingSupported", config.MixedLighting);
            SetInt(so, "m_AdditionalLightsCookieFormat", config.CookieFormat);
            SetFloat(so, "m_ShadowDistance", 150f);
            SetBool(so, "m_UseSRPBatcher", true);

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
            BasisSetupIO.ForceGuid(path, guid);
            return true;
        }

        private static void AssignProjectDefaultPipeline()
        {
            UniversalRenderPipelineAsset desktop =
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(DesktopPath);
            if (desktop != null && GraphicsSettings.defaultRenderPipeline == null)
            {
                GraphicsSettings.defaultRenderPipeline = desktop;
            }
        }

        private static void RegenerateQualityLevels()
        {
            Type guard = FindType("BasisQualitySettingsGuard");
            if (guard == null)
            {
                Debug.Log("[BasisSetup] Pipeline assets created. Quality levels install via BasisQualitySettingsGuard on the next domain reload, or run Basis > Settings > Quality Settings > Force Regenerate.");
                return;
            }

            MethodInfo method = guard.GetMethod("Regenerate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? guard.GetMethod("ForceRegenerate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                method?.Invoke(null, null);
            }
            catch
            {
            }
        }

        private static PipelineConfig DesktopConfig() => new PipelineConfig
        {
            SupportsHDR = true, HdrPrecision = 1, Msaa = 2, RenderScale = 1.002662f,
            RequireDepth = true, RequireOpaque = true, OpaqueDownsampling = 1,
            MainLightMode = 1, MainLightShadowRes = 8192, AdditionalLightsMode = 1, AdditionalLightsShadowRes = 8192,
            ShadowCascadeCount = 4, SoftShadows = true, SoftShadowQuality = 3, LodCrossFade = true,
            LightProbeSystem = 1, ProbeVolumeBudget = 2048, ColorGradingMode = 1, LightCookies = true,
            GpuResidentDrawerMode = 1, VolumeUpdateMode = 0, MixedLighting = true, CookieFormat = 4,
        };

        private static PipelineConfig AndroidConfig() => new PipelineConfig
        {
            SupportsHDR = false, HdrPrecision = 0, Msaa = 2, RenderScale = 0.9973433f,
            RequireDepth = false, RequireOpaque = false, OpaqueDownsampling = 3,
            MainLightMode = 1, MainLightShadowRes = 8192, AdditionalLightsMode = 2, AdditionalLightsShadowRes = 8192,
            ShadowCascadeCount = 4, SoftShadows = false, SoftShadowQuality = 1, LodCrossFade = false,
            LightProbeSystem = 1, ProbeVolumeBudget = 1024, ColorGradingMode = 0, LightCookies = false,
            GpuResidentDrawerMode = 0, VolumeUpdateMode = 0, MixedLighting = true, CookieFormat = 2,
        };

        private static PipelineConfig IOSConfig() => new PipelineConfig
        {
            SupportsHDR = true, HdrPrecision = 1, Msaa = 1, RenderScale = 1f,
            RequireDepth = false, RequireOpaque = false, OpaqueDownsampling = 3,
            MainLightMode = 1, MainLightShadowRes = 256, AdditionalLightsMode = 2, AdditionalLightsShadowRes = 256,
            ShadowCascadeCount = 4, SoftShadows = false, SoftShadowQuality = 1, LodCrossFade = false,
            LightProbeSystem = 1, ProbeVolumeBudget = 1024, ColorGradingMode = 0, LightCookies = false,
            GpuResidentDrawerMode = 0, VolumeUpdateMode = 0, MixedLighting = true, CookieFormat = 2,
        };

        private static PipelineConfig HeadlessConfig() => new PipelineConfig
        {
            SupportsHDR = false, HdrPrecision = 0, Msaa = 1, RenderScale = 1f,
            RequireDepth = false, RequireOpaque = false, OpaqueDownsampling = 3,
            MainLightMode = 0, MainLightShadowRes = 256, AdditionalLightsMode = 0, AdditionalLightsShadowRes = 256,
            ShadowCascadeCount = 2, SoftShadows = false, SoftShadowQuality = 1, LodCrossFade = false,
            LightProbeSystem = 0, ProbeVolumeBudget = 1024, ColorGradingMode = 0, LightCookies = false,
            GpuResidentDrawerMode = 0, VolumeUpdateMode = 1, MixedLighting = false, CookieFormat = 2,
        };

        private static void SetInt(SerializedObject so, string property, int value)
        {
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.intValue = value;
            }
        }

        private static void SetFloat(SerializedObject so, string property, float value)
        {
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.floatValue = value;
            }
        }

        private static void SetBool(SerializedObject so, string property, bool value)
        {
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.boolValue = value;
            }
        }

        private static void SetObject(SerializedObject so, string property, UnityEngine.Object value)
        {
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
            }
        }

        private static T LoadByGuid<T>(string guid) where T : UnityEngine.Object
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }

        private static Type FindType(string simpleName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i] != null && types[i].Name == simpleName)
                    {
                        return types[i];
                    }
                }
            }

            return null;
        }
    }
}
