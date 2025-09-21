/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Linq;
using Meta.XR.Editor.Callbacks;
using Meta.XR.Editor.Utils;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
#if USING_URP
using UnityEngine.Rendering.Universal;
#endif
#if USING_XR_SDK_OPENXR
using UnityEngine.XR.OpenXR;
#endif


[InitializeOnLoad]
internal static class OVRProjectSetupRenderingTasks
{
#if USING_URP && UNITY_2022_2_OR_NEWER
    // Call action for all UniversalRendererData being used, return true if all the return value of action is true
    private static bool ForEachRendererData(Func<UniversalRendererData, bool> action)
    {
        var ret = true;
        var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
        QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);
        foreach (var pipelineAsset in pipelineAssets)
        {
            var urpPipelineAsset = pipelineAsset as UniversalRenderPipelineAsset;
            // If using URP pipeline
            if (urpPipelineAsset)
            {
                var path = AssetDatabase.GetAssetPath(urpPipelineAsset);
                var dependency = AssetDatabase.GetDependencies(path);
                for (int i = 0; i < dependency.Length; i++)
                {
                    // Try to read the dependency as UniversalRendererData
                    if (AssetDatabase.GetMainAssetTypeAtPath(dependency[i]) != typeof(UniversalRendererData))
                        continue;

                    UniversalRendererData renderData =
                        (UniversalRendererData)AssetDatabase.LoadAssetAtPath(dependency[i],
                            typeof(UniversalRendererData));
                    if (renderData)
                    {
                        ret = ret && action(renderData);
                    }

                    if (!ret)
                    {
                        break;
                    }
                }
            }
        }

        return ret;
    }
#endif

    internal static GraphicsDeviceType[] GetGraphicsAPIs(BuildTargetGroup buildTargetGroup)
    {
        var buildTarget = buildTargetGroup.GetBuildTarget();
        if (PlayerSettings.GetUseDefaultGraphicsAPIs(buildTarget))
        {
            return Array.Empty<GraphicsDeviceType>();
        }

        // Recommends OpenGL ES 3 or Vulkan
        return PlayerSettings.GetGraphicsAPIs(buildTarget);
    }

    static OVRProjectSetupRenderingTasks()
    {
        InitializeOnLoad.Register(AddTasks);
    }

    static void AddTasks()
    {
        const OVRProjectSetup.TaskGroup targetGroup = OVRProjectSetup.TaskGroup.Rendering;

        //[Required] Set the color space to linear
        OVRProjectSetup.AddTask(
            conditionalLevel: _ =>
                PackageList.IsPackageInstalled(OVRProjectSetupXRTasks.UnityXRPackage)
                    ? OVRProjectSetup.TaskLevel.Required
                    : OVRProjectSetup.TaskLevel.Recommended,
            group: targetGroup,
            isDone: _ => PlayerSettings.colorSpace == ColorSpace.Linear,
            message: "Color Space is required to be Linear",
            fix: _ => PlayerSettings.colorSpace = ColorSpace.Linear,
            fixMessage: "PlayerSettings.colorSpace = ColorSpace.Linear"
        );

#if UNITY_GRAPHICS_JOB_FIX
        //[Required] Enable Graphics Jobs
        OVRProjectSetup.AddTask(
                    level: OVRProjectSetup.TaskLevel.Recommended,
                    group: targetGroup,
                    isDone: _ => PlayerSettings.graphicsJobs,
                    message: "Enable Legacy Graphics Jobs. This can help performance if your application is main thread bound.",
                    fix: _ =>
                    {
                        PlayerSettings.graphicsJobs = true;
                        PlayerSettings.graphicsJobMode = GraphicsJobMode.Legacy;
                    },
                    fixMessage: "PlayerSettings.graphicsJobs = true, PlayerSettings.graphicsJobMode = GraphicsJobMode.Legacy"
                );
#endif

        //[Recommended] Set the Graphics API order for Android
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            platform: BuildTargetGroup.Android,
            group: targetGroup,
            isDone: buildTargetGroup =>
                GetGraphicsAPIs(buildTargetGroup).Any(item =>
                    item == GraphicsDeviceType.OpenGLES3 || item == GraphicsDeviceType.Vulkan),
            message: "Manual selection of Graphic API, favoring Vulkan (or OpenGLES3)",
            fix: buildTargetGroup =>
            {
                var buildTarget = buildTargetGroup.GetBuildTarget();
                PlayerSettings.SetUseDefaultGraphicsAPIs(buildTarget, false);
                PlayerSettings.SetGraphicsAPIs(buildTarget, new[] { GraphicsDeviceType.Vulkan });
            },
            fixMessage: "Set Graphics APIs for this build target to Vulkan"
        );

#if !UNITY_EDITOR_OSX && !UNITY_EDITOR_LINUX
        //[Required] Set the Graphics API order for Windows
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Required,
            platform: BuildTargetGroup.Standalone,
            group: targetGroup,
            isDone: buildTargetGroup =>
                GetGraphicsAPIs(buildTargetGroup).Any(item =>
                    item == GraphicsDeviceType.Direct3D11),
            message: "Manual selection of Graphic API, favoring Direct3D11",
            fix: buildTargetGroup =>
            {
                var buildTarget = buildTargetGroup.GetBuildTarget();
                PlayerSettings.SetUseDefaultGraphicsAPIs(buildTarget, false);
                PlayerSettings.SetGraphicsAPIs(buildTarget, new[] { GraphicsDeviceType.Direct3D11 });
            },
            fixMessage: "Set Graphics APIs for this build target to Direct3D11"
        );
#endif

        //[Recommended] Enable Multithreaded Rendering
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: targetGroup,
            isDone: buildTargetGroup => PlayerSettings.MTRendering &&
                                        (buildTargetGroup != BuildTargetGroup.Android
#pragma warning disable CS0618 // Type or member is obsolete
                                         || PlayerSettings.GetMobileMTRendering(buildTargetGroup)),
#pragma warning restore CS0618 // Type or member is obsolete
            message: "Enable Multithreaded Rendering",
            fix: buildTargetGroup =>
            {
                PlayerSettings.MTRendering = true;
                if (buildTargetGroup == BuildTargetGroup.Android)
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    PlayerSettings.SetMobileMTRendering(buildTargetGroup, true);
#pragma warning restore CS0618 // Type or member is obsolete
                }
            },
            conditionalFixMessage: buildTargetGroup =>
                buildTargetGroup == BuildTargetGroup.Android
                    ? "PlayerSettings.MTRendering = true and PlayerSettings.SetMobileMTRendering(buildTargetGroup, true)"
                    : "PlayerSettings.MTRendering = true"
        );

        //[Recommended] Set the Display Buffer Format to 32 bit
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: targetGroup,
            isDone: _ =>
                PlayerSettings.use32BitDisplayBuffer,
            message: "Use 32Bit Display Buffer",
            fix: _ => PlayerSettings.use32BitDisplayBuffer = true,
            fixMessage: "PlayerSettings.use32BitDisplayBuffer = true"
        );

        //[Recommended] Set the Rendering Path to Forward
        // TODO : Support Scripted Rendering Pipeline?
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: targetGroup,
            isDone: buildTargetGroup =>
                EditorGraphicsSettings.GetTierSettings(buildTargetGroup, Graphics.activeTier).renderingPath ==
                RenderingPath.Forward,
            message: "Use Forward Rendering Path",
            fix: buildTargetGroup =>
            {
                var renderingTier = EditorGraphicsSettings.GetTierSettings(buildTargetGroup, Graphics.activeTier);
                renderingTier.renderingPath =
                    RenderingPath.Forward;
                EditorGraphicsSettings.SetTierSettings(buildTargetGroup, Graphics.activeTier, renderingTier);
            },
            fixMessage: "renderingTier.renderingPath = RenderingPath.Forward"
        );

        // [Recommended] Set the Stereo Rendering to Instancing
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: targetGroup,
            isDone: _ => PlayerSettings.stereoRenderingPath == StereoRenderingPath.Instancing,
            message: "Use Stereo Rendering Instancing",
            fix: _ => PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing,
            fixMessage: "PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing"
        );

#if USING_URP && UNITY_2022_2_OR_NEWER
        //[Recommended] When using URP, set Intermediate Texture to "Auto"
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: targetGroup,
            isDone: _ =>
                ForEachRendererData(rd => { return rd.intermediateTextureMode == IntermediateTextureMode.Auto; }),
            message: "Setting the Intermediate Texture Mode to \"Always\" might have a performance impact, it is recommended to use \"Auto\"",
            fix: _ =>
                ForEachRendererData(rd => { rd.intermediateTextureMode = IntermediateTextureMode.Auto; return true; }),
            fixMessage: "Set Intermediate Texture Mode to \"Auto\""
        );

        //[Recommended] When using URP, disable SSAO
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: targetGroup,
            isDone: _ =>
                ForEachRendererData(rd =>
                {
                    return rd.rendererFeatures.Count == 0
                        || !rd.rendererFeatures.Any(
                            feature => feature != null && (feature.isActive && feature.GetType().Name == "ScreenSpaceAmbientOcclusion"));
                }),
            message: "SSAO will have some performace impact, it is recommended to disable SSAO",
            fix: _ =>
                ForEachRendererData(rd =>
                {
                    rd.rendererFeatures.ForEach(feature =>
                    {
                        if (feature != null && feature.GetType().Name == "ScreenSpaceAmbientOcclusion")
                            feature.SetActive(false);
                    }
                    );
                    return true;
                }),
            fixMessage: "Disable SSAO"
        );
#endif

        //[Optional] Use Non-Directional Lightmaps
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Optional,
            group: targetGroup,
            isDone: _ =>
            {
                return LightmapSettings.lightmaps.Length == 0 ||
                       LightmapSettings.lightmapsMode == LightmapsMode.NonDirectional;
            },
            message: "Use Non-Directional lightmaps",
            fix: _ => LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional,
            fixMessage: "LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional"
        );

        //[Optional] Disable Realtime GI
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Optional,
            group: targetGroup,
            isDone: _ => !Lightmapping.realtimeGI,
            message: "Disable Realtime Global Illumination",
            fix: _ => Lightmapping.realtimeGI = false,
            fixMessage: "Lightmapping.realtimeGI = false"
        );

        //[Optional] GPU Skinning
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Optional,
            platform: BuildTargetGroup.Android,
            group: targetGroup,
            isDone: _ => PlayerSettings.gpuSkinning,
            message: "Consider using GPU Skinning if your application is CPU bound",
            fix: _ => PlayerSettings.gpuSkinning = true,
            fixMessage: "PlayerSettings.gpuSkinning = true"
        );

        //[Recommended] Dynamic Resolution
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            conditionalValidity: _ =>
            {
                var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                return ovrManager != null;
            },
            platform: BuildTargetGroup.Android,
            group: targetGroup,
            isDone: _ =>
            {
                var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                return ovrManager == null || ovrManager.enableDynamicResolution;
            },
            message: "Using Dynamic Resolution can help improve quality when GPU Utilization is low, and improve framerate in GPU heavy scenes. It also unlocks GPU Level 5 on Meta Quest 2, Pro and 3. Consider disabling it when profiling and optimizing your application.",
            fix: _ =>
            {
                var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                if (ovrManager)
                {
                    ovrManager.enableDynamicResolution = true;
                    if (ovrManager.quest2MinDynamicResolutionScale == 1.0f && ovrManager.quest2MaxDynamicResolutionScale == 1.0f)
                    {
                        ovrManager.quest2MinDynamicResolutionScale = 0.7f;
                        ovrManager.quest2MaxDynamicResolutionScale = 1.3f;
                    }

                    if (ovrManager.quest3MinDynamicResolutionScale == 1.0f && ovrManager.quest3MaxDynamicResolutionScale == 1.0f)
                    {
                        ovrManager.quest3MinDynamicResolutionScale = 0.7f;
                        ovrManager.quest3MaxDynamicResolutionScale = 1.6f;
                    }
                }
            },
            fixMessage: "OVRManager.enableDynamicResolution = true, OVRManager.quest2MinDynamicResolutionScale = 0.7f, OVRManager.quest2MaxDynamicResolutionScale = 1.3f, OVRManager.quest3MinDynamicResolutionScale = 0.7f, OVRManager.quest3MaxDynamicResolutionScale = 1.6f"
        );

#if USING_URP && UNITY_2022_2_OR_NEWER
        // [Recommended] Disable Depth Texture
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            platform: BuildTargetGroup.Android,
            group: targetGroup,
            isDone: _ =>
            {
                var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
                QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);

                return !pipelineAssets.OfType<UniversalRenderPipelineAsset>().Any(asset => asset.supportsCameraDepthTexture);
            },
            fix: _ =>
            {
                var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
                QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);

                foreach (var urpAsset in pipelineAssets.OfType<UniversalRenderPipelineAsset>())
                {
                    urpAsset.supportsCameraDepthTexture = false;
                }
            },
            message: "Enabling Depth Texture may significantly impact performance. It is recommended to disable it when it isn't required in a shader.");

        // [Recommended] Disable Opaque Texture
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            platform: BuildTargetGroup.Android,
            group: targetGroup,
            isDone: _ =>
            {
                var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
                QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);

                return !pipelineAssets.OfType<UniversalRenderPipelineAsset>().Any(asset => asset.supportsCameraOpaqueTexture);
            },
            fix: _ =>
            {
                var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
                QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);

                foreach (var urpAsset in pipelineAssets.OfType<UniversalRenderPipelineAsset>())
                {
                    urpAsset.supportsCameraOpaqueTexture = false;
                }
            },
            message: "Enabling Opaque Texture may significantly impact performance. It is recommended to disable it when it isn't required in a shader.");

        // [Recommended] Disable HDR
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            platform: BuildTargetGroup.Android,
            group: targetGroup,
            isDone: _ =>
            {
                var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
                QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);

                return !pipelineAssets
                    .OfType<UniversalRenderPipelineAsset>()
                    .Any(asset => asset.supportsHDR);
            },
            fix: _ =>
            {
                var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
                QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);

                foreach (var urpAsset in pipelineAssets.OfType<UniversalRenderPipelineAsset>())
                {
                    urpAsset.supportsHDR = false;
                }
            },
            message: "Using HDR may significantly impact performance. It is recommended to disable HDR.");

        // [Recommended] Disable FSR
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            platform: BuildTargetGroup.Android,
            group: targetGroup,
            isDone: _ =>
            {
                var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
                QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);

                return !pipelineAssets
                    .OfType<UniversalRenderPipelineAsset>()
                    .Any(asset => asset.upscalingFilter == UpscalingFilterSelection.FSR);
            },
            fix: _ =>
            {
                var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
                QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);

                foreach (var urpAsset in pipelineAssets.OfType<UniversalRenderPipelineAsset>())
                {
                    urpAsset.upscalingFilter = UpscalingFilterSelection.Auto;
                }
            },
            message: "Using FSR may significantly impact performance. It is recommended to switch Upscaling Filter to Auto.");

        // [Recommended] Disable Camera Stack
        OVRProjectSetup.AddTask(
            level: OVRProjectSetup.TaskLevel.Recommended,
            platform: BuildTargetGroup.Android,
            group: targetGroup,
            conditionalValidity: _ =>
            {
                    // We must check if the current render pipeline asset is a URP asset,
                    // or else cameraData.cameraStack will internally derefence a null value.
                    return GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset;
            },
            isDone: _ =>
            {
                    // Any camera in the scene is using a camera stack
                    return !OVRProjectSetupUtils
                    .FindComponentsInScene<Camera>()
                    ?.Select(camera => camera.GetUniversalAdditionalCameraData())
                    ?.Any(cameraData => cameraData.cameraStack?.Any() ?? false) ?? false;
            },
            message: "Using the camera stack may significantly impact performance. It is not recommended to use the camera stack feature."
        );
#endif

#if USING_URP
            const string urpFixLink = "https://developers.meta.com/horizon/documentation/unity/dynamic-resolution-unity/#temprt-reallocation-leading-to-oom-fixed-in-unity-600001f1-and-2022315f1";
            const string minValidUrpPackageVersionString = "14.0.9";
            const string urpPackageName = "com.unity.render-pipelines.universal";
            const string urpGit = "https://github.com/Oculus-VR/Unity-Graphics.git";
            var minValidPackageVersion = new Version(minValidUrpPackageVersionString);
            OVRProjectSetup.AddTask(
                level: OVRProjectSetup.TaskLevel.Required,
                platform: BuildTargetGroup.Android,
                group: targetGroup,
                conditionalValidity: _ =>
                {
                    if (!PackageList.PackageManagerListAvailable)
                    {
                        return false;
                    }

                    var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
                    return ovrManager != null && ovrManager.enableDynamicResolution;
                },
            isDone: _ =>
            {
                var urpPackage = PackageList.GetPackage(urpPackageName);
                if (urpPackage == null)
                {
                    return true;
                }

                if (urpPackage.packageId.Contains(urpGit) && urpPackage.version is "14.0.7" or "14.0.8")
                {
                    // We've fixed the bug in our own URP fork
                    return true;
                }

                if (!Version.TryParse(urpPackage.version, out var version))
                {
                    // failed to parse the package version, don't trigger the setup rule
                    return true;
                }

                return version >= minValidPackageVersion;
            },
            message: $"Using Dynamic Resolution and a URP version older than {minValidUrpPackageVersionString} will impact performance. It's recommended that you update your URP package.",
            url: urpFixLink
        );
#endif
        // [Recommended] Use recommended MSAA level from OVRPlugin
        OVRProjectSetup.AddTask(
           level: OVRProjectSetup.TaskLevel.Recommended,
           platform: BuildTargetGroup.Android,
           group: targetGroup,
           isDone: buildTargetGroup =>
           {
#if USING_URP
               var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
               QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);
               foreach (var urpAsset in pipelineAssets.OfType<UniversalRenderPipelineAsset>())
               {
                   if (urpAsset.msaaSampleCount != OVRPlugin.recommendedMSAALevel)
                   {
                       return false;
                   }
               }
               return true;
#else
               return QualitySettings.antiAliasing == OVRPlugin.recommendedMSAALevel;
#endif
           },
           message: "Use recommended MSAA level for Android",
           fix: buildTargetGroup =>
           {
#if USING_URP
               var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
               QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);
               foreach (var urpAsset in pipelineAssets.OfType<UniversalRenderPipelineAsset>())
               {
                   urpAsset.msaaSampleCount = OVRPlugin.recommendedMSAALevel;
               }
#else
               QualitySettings.antiAliasing = OVRPlugin.recommendedMSAALevel;
#endif
           },
           fixMessage: "Set MSAA for all URP Asset to " + OVRPlugin.recommendedMSAALevel.ToString()
        );
    }
}
