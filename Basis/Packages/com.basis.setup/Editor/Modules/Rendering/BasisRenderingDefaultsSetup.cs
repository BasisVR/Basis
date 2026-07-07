using System.IO;
using Basis.Scripts.BasisSdk.Highlight;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Basis.Setup.Modules
{
    public sealed class BasisRenderingDefaultsSetup : BasisSetupModuleBase
    {
        private const string Folder = "Assets/Basis/Settings/Unity Rendering Defaults";

        private const string DesktopRendererPath = Folder + "/DesktopRenderer.asset";
        private const string DesktopCameraRendererPath = Folder + "/DesktopRendererCamera.asset";
        private const string QuestRendererPath = Folder + "/QuestRenderer.asset";
        private const string HeadlessRendererPath = Folder + "/HeadlessRenderer.asset";

        private const string DefaultProfilePath = Folder + "/DefaultVolumeProfile.asset";
        private const string DesktopProfilePath = Folder + "/DesktopVolumeProfile.asset";
        private const string CameraProfilePath = Folder + "/CameraVolumeProfile.asset";
        private const string SceneProfilePath = Folder + "/SceneProfile.asset";
        private const string LightingPath = Folder + "/inital.lighting";

        private const string DesktopRendererGuid = "c40be3174f62c4acf8c1216858c64956";
        private const string DesktopCameraRendererGuid = "d72f5d4ed70e6af45910472d0b56540f";
        private const string QuestRendererGuid = "dc6efa2bbf91054468277b399108bb90";
        private const string HeadlessRendererGuid = "a998016ed30342e4aa3a3687e625caa6";
        private const string DefaultProfileGuid = "fe38da480e5432d4096cdaf1f3e9fdc1";
        private const string DesktopProfileGuid = "c472becb8ef443eeb97e31b87151cadc";
        private const string CameraProfileGuid = "49fa3e3e4ae0a5a43985fc094a849f4a";
        private const string SceneProfileGuid = "a6560a915ef98420e9faacc1c7438823";
        private const string LightingGuid = "53f3dab963dc0c749a296ebbd47894b9";

        private const string PostProcessDataGuid = "41439944d30ece34e96484bdb6645b55";
        private const string DesktopHighlightGuid = "c301ac59f1dce524f8e770032f059e8f";
        private const string AndroidHighlightGuid = "3102546134695bd41b2d54c183638e5b";

        public override string Key => "rendering.defaults";
        public override string DisplayName => "URP Renderers, Volume Profiles & Lighting";
        public override string Category => "Rendering";
        public override int Version => 1;
        public override int Order => 10;

        protected override bool Exists()
        {
            return File.Exists(BasisSetupIO.ToFullPath(DesktopRendererPath));
        }

        protected override bool Build(BasisSetupMode mode)
        {
            BasisSetupIO.EnsureAssetFolder(Folder);
            PostProcessData postProcess = LoadByGuid<PostProcessData>(PostProcessDataGuid);
            BasisHighlightSettings desktopHighlight = LoadByGuid<BasisHighlightSettings>(DesktopHighlightGuid);
            BasisHighlightSettings androidHighlight = LoadByGuid<BasisHighlightSettings>(AndroidHighlightGuid);

            bool changed = false;

            changed |= BuildRenderer(DesktopRendererPath, DesktopRendererGuid, mode, rd =>
            {
                rd.renderingMode = RenderingMode.ForwardPlus;
                rd.depthPrimingMode = DepthPrimingMode.Forced;
                rd.copyDepthMode = CopyDepthMode.AfterTransparents;
                rd.shadowTransparentReceive = true;
                rd.postProcessData = postProcess;
                AddFog(rd);
                AddHighlight(rd, desktopHighlight);
            });

            changed |= BuildRenderer(DesktopCameraRendererPath, DesktopCameraRendererGuid, mode, rd =>
            {
                rd.renderingMode = RenderingMode.ForwardPlus;
                rd.depthPrimingMode = DepthPrimingMode.Disabled;
                rd.copyDepthMode = CopyDepthMode.AfterTransparents;
                rd.shadowTransparentReceive = true;
                rd.postProcessData = postProcess;
                SetBool(rd, "m_UseNativeRenderPass", true);
                AddFog(rd);
            });

            changed |= BuildRenderer(QuestRendererPath, QuestRendererGuid, mode, rd =>
            {
                rd.renderingMode = RenderingMode.Forward;
                rd.depthPrimingMode = DepthPrimingMode.Disabled;
                rd.copyDepthMode = CopyDepthMode.AfterOpaques;
                rd.shadowTransparentReceive = false;
                SetBool(rd, "m_TileOnlyMode", true);
                AddHighlight(rd, androidHighlight);
            });

            changed |= BuildRenderer(HeadlessRendererPath, HeadlessRendererGuid, mode, rd =>
            {
                rd.renderingMode = RenderingMode.Forward;
                rd.depthPrimingMode = DepthPrimingMode.Disabled;
                rd.copyDepthMode = CopyDepthMode.AfterOpaques;
                rd.shadowTransparentReceive = false;
                SetBool(rd, "m_UseNativeRenderPass", true);
                SetInt(rd, "m_DepthAttachmentFormat", 90);
                SetInt(rd, "m_DepthTextureFormat", 90);
            });

            changed |= BuildProfile(DefaultProfilePath, DefaultProfileGuid, mode, p =>
            {
                Tonemapping tone = p.Add<Tonemapping>();
                tone.mode.Override(TonemappingMode.ACES);
                Bloom bloom = p.Add<Bloom>();
                bloom.intensity.Override(0.1f);
                bloom.threshold.Override(0.9f);
                bloom.highQualityFiltering.Override(true);
                ProbeVolumesOptions apv = p.Add<ProbeVolumesOptions>();
                apv.normalBias.Override(0.33f);
                VolumetricFogVolumeComponent fog = p.Add<VolumetricFogVolumeComponent>();
                fog.distance.Override(64f);
                fog.density.Override(0.2f);
                fog.maxSteps.Override(128);
                fog.enabled.Override(false);
            });

            changed |= BuildProfile(DesktopProfilePath, DesktopProfileGuid, mode, p =>
            {
                VolumetricFogVolumeComponent fog = p.Add<VolumetricFogVolumeComponent>();
                fog.active = false;
                fog.distance.Override(64f);
                fog.density.Override(0.2f);
                fog.maxSteps.Override(128);
                fog.blurIterations.Override(2);
                fog.enableAPVContribution.Override(true);
                fog.enabled.Override(true);
            });

            changed |= BuildProfile(CameraProfilePath, CameraProfileGuid, mode, p =>
            {
                DepthOfField dof = p.Add<DepthOfField>();
                dof.active = false;
                dof.mode.Override(DepthOfFieldMode.Bokeh);
                dof.focalLength.Override(125.4f);
                Tonemapping tone = p.Add<Tonemapping>();
                tone.mode.Override(TonemappingMode.Neutral);
                p.Add<ColorAdjustments>();
                p.Add<Bloom>();
                VolumetricFogVolumeComponent fog = p.Add<VolumetricFogVolumeComponent>();
                fog.distance.Override(15f);
                fog.baseHeight.Override(-50f);
                fog.density.Override(0.0775f);
                fog.enableMainLightContribution.Override(true);
                fog.anisotropy.Override(0.34f);
                fog.scattering.Override(0.95f);
                fog.tint.Override(new Color(1f, 0.178f, 0f, 1f));
                fog.maxSteps.Override(188);
                fog.blurIterations.Override(3);
                fog.enabled.Override(true);
            });

            changed |= BuildProfile(SceneProfilePath, SceneProfileGuid, mode, p =>
            {
                Tonemapping tone = p.Add<Tonemapping>();
                tone.mode.Override(TonemappingMode.ACES);
                p.Add<Bloom>();
                ProbeVolumesOptions apv = p.Add<ProbeVolumesOptions>();
                apv.active = false;
            });

            changed |= BuildLighting(mode);
            AssignDefaultVolumeProfile();

            return changed;
        }

        private bool BuildRenderer(string path, string guid, BasisSetupMode mode, System.Action<UniversalRendererData> configure)
        {
            UniversalRendererData data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
            bool created = data == null;
            if (!created && mode == BasisSetupMode.EnsureExists)
            {
                return false;
            }

            if (created)
            {
                BasisSetupIO.EnsureParentFolder(path);
                data = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(data, path);
            }
            else
            {
                BasisSetupIO.BackupIfExists(path);
                for (int i = data.rendererFeatures.Count - 1; i >= 0; i--)
                {
                    if (data.rendererFeatures[i] != null)
                    {
                        Object.DestroyImmediate(data.rendererFeatures[i], true);
                    }
                }
            }

            data.rendererFeatures.Clear();
            configure(data);
            EditorUtility.SetDirty(data);
            BasisSetupIO.ForceGuid(path, guid);
            return true;
        }

        private static void AddFog(UniversalRendererData data)
        {
            VolumetricFogRendererFeature feature = ScriptableObject.CreateInstance<VolumetricFogRendererFeature>();
            feature.name = "VolumetricFogRendererFeature";
            data.rendererFeatures.Add(feature);
            AssetDatabase.AddObjectToAsset(feature, data);
        }

        private static void AddHighlight(UniversalRendererData data, BasisHighlightSettings settings)
        {
            BasisHighlightFeature feature = ScriptableObject.CreateInstance<BasisHighlightFeature>();
            feature.name = "BasisHighlightFeature";
            data.rendererFeatures.Add(feature);
            AssetDatabase.AddObjectToAsset(feature, data);

            SerializedObject so = new SerializedObject(feature);
            SerializedProperty prop = so.FindProperty("settings");
            if (prop != null)
            {
                prop.objectReferenceValue = settings;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private bool BuildProfile(string path, string guid, BasisSetupMode mode, System.Action<VolumeProfile> configure)
        {
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            bool created = profile == null;
            if (!created && mode == BasisSetupMode.EnsureExists)
            {
                return false;
            }

            if (created)
            {
                BasisSetupIO.EnsureParentFolder(path);
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            else
            {
                BasisSetupIO.BackupIfExists(path);
                for (int i = profile.components.Count - 1; i >= 0; i--)
                {
                    if (profile.components[i] != null)
                    {
                        Object.DestroyImmediate(profile.components[i], true);
                    }
                }
            }

            profile.components.Clear();
            configure(profile);
            for (int i = 0; i < profile.components.Count; i++)
            {
                if (profile.components[i] != null && !AssetDatabase.Contains(profile.components[i]))
                {
                    AssetDatabase.AddObjectToAsset(profile.components[i], profile);
                }
            }

            EditorUtility.SetDirty(profile);
            BasisSetupIO.ForceGuid(path, guid);
            return true;
        }

        private bool BuildLighting(BasisSetupMode mode)
        {
            LightingSettings lighting = AssetDatabase.LoadAssetAtPath<LightingSettings>(LightingPath);
            bool created = lighting == null;
            if (!created && mode == BasisSetupMode.EnsureExists)
            {
                return false;
            }

            if (created)
            {
                lighting = new LightingSettings();
            }
            else
            {
                BasisSetupIO.BackupIfExists(LightingPath);
            }

            lighting.name = "inital";
            lighting.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            lighting.bakedGI = false;
            lighting.realtimeGI = false;
            lighting.lightmapMaxSize = 1024;
            lighting.bakeResolution = 40f;
            lighting.padding = 2;
            lighting.mixedBakeMode = MixedLightingMode.Shadowmask;
            lighting.directSampleCount = 32;
            lighting.indirectSampleCount = 512;
            lighting.environmentSampleCount = 256;
            lighting.lightProbeSampleCountMultiplier = 4f;
            lighting.maxBounces = 2;
            lighting.minBounces = 2;

            if (created)
            {
                AssetDatabase.CreateAsset(lighting, LightingPath);
            }
            else
            {
                EditorUtility.SetDirty(lighting);
            }

            BasisSetupIO.ForceGuid(LightingPath, LightingGuid);
            return true;
        }

        private void AssignDefaultVolumeProfile()
        {
            VolumeProfile profile = LoadByGuid<VolumeProfile>(DefaultProfileGuid);
            if (profile == null)
            {
                return;
            }

            try
            {
                VolumeProfile current = VolumeManager.instance.globalDefaultProfile;
                if (current == null)
                {
                    VolumeManager.instance.SetGlobalDefaultProfile(profile);
                }
            }
            catch
            {
            }
        }

        private static void SetBool(UniversalRendererData data, string property, bool value)
        {
            SerializedObject so = new SerializedObject(data);
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.boolValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void SetInt(UniversalRendererData data, string property, int value)
        {
            SerializedObject so = new SerializedObject(data);
            SerializedProperty prop = so.FindProperty(property);
            if (prop != null)
            {
                prop.intValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static T LoadByGuid<T>(string guid) where T : Object
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }
    }
}
