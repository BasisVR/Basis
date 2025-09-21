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

//#define BUILDSESSION

#if USING_XR_MANAGEMENT && (USING_XR_SDK_OCULUS || USING_XR_SDK_OPENXR)
#define USING_XR_SDK
#endif


using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Diagnostics;
using System.Threading;
using Oculus.VR.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif

#if USING_XR_SDK_OPENXR
using UnityEngine.XR.OpenXR;
using UnityEditor.XR.OpenXR.Features;
#endif

#if USING_XR_SDK_OCULUS
using Unity.XR.Oculus;
#endif

#if USING_XR_MANAGEMENT
using UnityEditor.XR.Management;
#endif

#if USING_URP
using UnityEngine.Rendering.Universal;
#endif

using Meta.XR.Editor.Utils;

[InitializeOnLoad]
public class OVRGradleGeneration
    : IPreprocessBuildWithReport, IPostprocessBuildWithReport
#if UNITY_ANDROID
        , IPostGenerateGradleAndroidProject
#endif
{
    public OVRADBTool adbTool;
    public Process adbProcess;

#if PRIORITIZE_OCULUS_XR_SETTINGS
    private int _callbackOrder = 3;
#else
    // to be executed after OculusManifest in Oculus XR Plugin, which has callbackOrder 10000
    private int _callbackOrder = 99999;
#endif

    public int callbackOrder
    {
        get { return _callbackOrder; }
    }

    static private System.DateTime buildStartTime;
    static private System.Guid buildGuid;

#if UNITY_ANDROID
    public const string prefName = "OVRAutoIncrementVersionCode_Enabled";
    private const string menuItemAutoIncVersion = "Meta/Tools/Auto Increment Version Code";
    static bool autoIncrementVersion = false;
#endif

#if UNITY_ANDROID && USING_XR_SDK_OCULUS
    static private bool symmetricWarningShown = false;
#endif

    static OVRGradleGeneration()
    {
        EditorApplication.delayCall += OnDelayCall;
    }

    static void OnDelayCall()
    {
#if UNITY_ANDROID
        autoIncrementVersion = PlayerPrefs.GetInt(prefName, 0) != 0;
        Menu.SetChecked(menuItemAutoIncVersion, autoIncrementVersion);
#endif
    }

#if UNITY_ANDROID
    [MenuItem(menuItemAutoIncVersion)]
    public static void ToggleUtilities()
    {
        autoIncrementVersion = !autoIncrementVersion;
        Menu.SetChecked(menuItemAutoIncVersion, autoIncrementVersion);

        int newValue = (autoIncrementVersion) ? 1 : 0;
        PlayerPrefs.SetInt(prefName, newValue);
        PlayerPrefs.Save();

        UnityEngine.Debug.Log("Auto Increment Version Code: " + autoIncrementVersion);
    }
#endif

    public void OnPreprocessBuild(BuildReport report)
    {
        bool useOpenXR = OVRPluginInfo.IsOVRPluginOpenXRActivated();

#if USING_XR_SDK_OPENXR
        // OpenXR Plugin will remove all native plugins if they are not under the Feature folder. Include OVRPlugin to the build if MetaXRFeature is enabled.
        var metaXRFeature =
            FeatureHelpers.GetFeatureWithIdForBuildTarget(report.summary.platformGroup, Meta.XR.MetaXRFeature.featureId);
        if (metaXRFeature != null && metaXRFeature.enabled && !useOpenXR)
        {
            throw new BuildFailedException("OpenXR backend for Oculus Plugin is disabled, which is required to support Unity OpenXR Plugin. Please enable OpenXR backend for Oculus Plugin through the 'Oculus -> Tools -> OpenXR' menu.");
        }

        string ovrRootPath = OVRPluginInfo.GetUtilitiesRootPath();
        var importers = PluginImporter.GetAllImporters();
        var utilitiesPackageInfo = OVRPluginInfo.GetUtilitiesPackageInfo();

        foreach (var importer in importers)
        {
            if (!importer.GetCompatibleWithPlatform(report.summary.platform))
                continue;

            string assetPath = "";
            bool isUtilitiesAsset = false;

            assetPath = importer.assetPath;
            var assetPackageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(importer.assetPath);
            if (utilitiesPackageInfo != null && assetPackageInfo != null)
            {
                isUtilitiesAsset = (assetPackageInfo.name.Equals(utilitiesPackageInfo.name));
            }
            else
            {
                if (assetPackageInfo == null)
                    assetPath = Path.Combine(Directory.GetCurrentDirectory(), importer.assetPath);
#if UNITY_EDITOR_WIN
                assetPath = assetPath.Replace("/", "\\");
#endif
                isUtilitiesAsset = assetPath.StartsWith(ovrRootPath);
            }

            // Use the libraries from OVRPlugin that come from the integration sdk or the upm version
            if (isUtilitiesAsset && assetPath.Contains("OVRPlugin"))
            {
                if (metaXRFeature != null && metaXRFeature.enabled)
                    UnityEngine.Debug.LogFormat("[Meta] Native plugin included in build because of enabled MetaXRFeature: {0}", importer.assetPath);
                else
                    UnityEngine.Debug.LogWarning("MetaXRFeature is not enabled in OpenXR Settings. Oculus Integration scripts will not be functional.");
                importer.SetIncludeInBuildDelegate(path => metaXRFeature != null && metaXRFeature.enabled);
            }

            // Only disable other OpenXR Loaders if the Meta XR feature is enabled
            if (metaXRFeature != null && metaXRFeature.enabled)
            {
                if (!isUtilitiesAsset && (assetPath.Contains("libopenxr_loader.so") || assetPath.Contains("openxr_loader.aar")))
                {
                    UnityEngine.Debug.LogFormat("[Meta] libopenxr_loader.so from other packages will be disabled because of enabled MetaXRFeature: {0}", importer.assetPath);
                    importer.SetIncludeInBuildDelegate(path => false);
                }
            }
        }
#endif

#if UNITY_ANDROID && !(USING_XR_SDK && UNITY_2019_3_OR_NEWER)
        // Generate error when Vulkan is selected as the perferred graphics API, which is not currently supported in Unity XR
        if (!PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android))
        {
            GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            if (apis.Length >= 1 && apis[0] == GraphicsDeviceType.Vulkan)
            {
                throw new BuildFailedException("The Vulkan Graphics API does not support XR in your configuration. To use Vulkan, you must use Unity 2019.3 or newer, and the XR Plugin Management.");
            }
        }
#endif

#if UNITY_ANDROID && USING_XR_SDK_OCULUS && OCULUS_XR_SYMMETRIC
        OculusSettings settings;
        if (EditorBuildSettings.TryGetConfigObject<OculusSettings>("Unity.XR.Oculus.Settings", out settings)
            && settings.SymmetricProjection && !symmetricWarningShown)
        {
            symmetricWarningShown = true;
            UnityEngine.Debug.LogWarning(
                "Symmetric Projection is enabled in the Oculus XR Settings. To ensure best GPU performance, make sure at least FFR 1 is being used.");
        }
#endif

#if UNITY_ANDROID
#if USING_XR_SDK
        if (useOpenXR)
        {
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                throw new BuildFailedException(
                    "Oculus Utilities Plugin with OpenXR only supports linear lighting. Please set 'Rendering/Color Space' to 'Linear' in Player Settings");
            }
        }
#else
        if (useOpenXR)
        {
            throw new BuildFailedException("Oculus Utilities Plugin with OpenXR only supports XR Plug-in Managmenent with Oculus XR Plugin.");
        }
#endif
#endif

#if UNITY_ANDROID && USING_XR_SDK && !USING_COMPATIBLE_OCULUS_XR_PLUGIN_VERSION
        if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
            throw new BuildFailedException("Your project is using an Oculus XR Plugin version with known issues. Please navigate to the Package Manager and upgrade the Oculus XR Plugin to the latest verified version. When performing the upgrade" +
                ", you must first \"Remove\" the Oculus XR Plugin package, and then \"Install\" the package at the verified version. Be sure to remove, then install, not just upgrade.");
#endif

        buildStartTime = System.DateTime.Now;
        buildGuid = System.Guid.NewGuid();

#if BUILDSESSION
        StreamWriter writer = new StreamWriter("build_session", false);
        UnityEngine.Debug.LogFormat("Build Session: {0}", buildGuid.ToString());
        writer.WriteLine(buildGuid.ToString());
        writer.Close();
#endif

#if UNITY_ANDROID
        OVRProjectConfig projectConfig = OVRProjectConfig.CachedProjectConfig;
#if PRIORITIZE_OCULUS_XR_SETTINGS
#if !OCULUS_XR_PLUGIN_QUEST_ONE_REMOVED
        EditorBuildSettings.TryGetConfigObject("Unity.XR.Oculus.Settings", out OculusSettings deviceSettings);
        if (deviceSettings.TargetQuest)
        {
            UnityEngine.Debug.LogWarning("Quest 1 is no longer supported as a target device as of v51. Please uncheck Quest 1 as a target device, or downgrade to v50.");
        }
#endif
#else
        if (projectConfig.targetDeviceTypes.Contains(OVRProjectConfig.DeviceType.Quest))
        {
            projectConfig.targetDeviceTypes.Remove(OVRProjectConfig.DeviceType.Quest);
            OVRProjectConfig.CommitProjectConfig(projectConfig);
            UnityEngine.Debug.Log("Quest 1 is no longer supported as a target device as of v51 and has been removed as a target device from this project.");
        };
#endif
        string gradlePath = Path.Combine(Application.dataPath, "..", "Library", "Bee", "Android", "Prj", "IL2CPP", "Gradle");
        if (projectConfig.removeGradleManifest && Directory.Exists(gradlePath))
        {
            string gradleManifest = Path.Combine(gradlePath, "unityLibrary", "src", "main", "AndroidManifest.xml");
            if (File.Exists(gradleManifest))
            {
                File.Delete(gradleManifest);
            }
        }
#endif

        SendOVRManagerSettingsTelemetry();
    }

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        UnityEngine.Debug.Log("OVRGradleGeneration triggered.");

        var targetOculusPlatform = new List<string>();
        if (OVRDeviceSelector.isTargetDeviceQuestFamily)
        {
            targetOculusPlatform.Add("quest");
        }

        UnityEngine.Debug.LogFormat("QuestFamily = {0}: Quest = {1}, Quest2 = {2}",
            OVRDeviceSelector.isTargetDeviceQuestFamily,
            OVRDeviceSelector.isTargetDeviceQuest,
            OVRDeviceSelector.isTargetDeviceQuest2);

        OVRProjectConfig projectConfig = OVRProjectConfig.CachedProjectConfig;

        // Toggle & generate system splash screen
        if (projectConfig != null && projectConfig.systemSplashScreen != null)
        {
            if (PlayerSettings.virtualRealitySplashScreen != null)
            {
                UnityEngine.Debug.LogWarning(
                    "Virtual Reality Splash Screen (in Player Settings) is active. It would be displayed after the system splash screen, before the first game frame be rendered.");
            }

            string splashScreenAssetPath = AssetDatabase.GetAssetPath(projectConfig.systemSplashScreen);
            if (Path.GetExtension(splashScreenAssetPath).ToLower() != ".png")
            {
                throw new BuildFailedException(
                    "Invalid file format of System Splash Screen. It has to be a PNG file to be used by the Quest OS. The asset path: " +
                    splashScreenAssetPath);
            }

            string sourcePath = splashScreenAssetPath;
            string targetFolder = Path.Combine(path, "src/main/assets");
            string targetPath = targetFolder + "/vr_splash.png";
            UnityEngine.Debug.LogFormat("Copy splash screen asset from {0} to {1}", sourcePath, targetPath);
            try
            {
                // In many common cases such as P4, files can be read-only so when they are copied from a previous build it can fail
                if (File.Exists(targetPath))
                {
                    File.SetAttributes(targetPath, File.GetAttributes(targetPath) & ~FileAttributes.ReadOnly);
                }
                File.Copy(sourcePath, targetPath, true);
            }
            catch (Exception e)
            {
                throw new BuildFailedException(e.Message);
            }
        }

        PatchAndroidManifest(path);
    }

    public void PatchAndroidManifest(string path)
    {
        string manifestFolder = Path.Combine(path, "src/main");
        string file = manifestFolder + "/AndroidManifest.xml";

        bool patchedSecurityConfig = false;
        // If Enable NSC Config, copy XML file into gradle project
        OVRProjectConfig projectConfig = OVRProjectConfig.CachedProjectConfig;
        if (projectConfig != null)
        {
            if (projectConfig.enableNSCConfig)
            {
                // If no custom xml security path is specified, look for the default location in the integrations package.
                string securityConfigFile = projectConfig.securityXmlPath;
                if (string.IsNullOrEmpty(securityConfigFile))
                {
                    securityConfigFile = GetOculusProjectNetworkSecConfigPath();
                }
                else
                {
                    Uri configUri = new Uri(Path.GetFullPath(securityConfigFile));
                    Uri projectUri = new Uri(Application.dataPath);
                    Uri relativeUri = projectUri.MakeRelativeUri(configUri);
                    securityConfigFile = relativeUri.ToString();
                }

                string xmlDirectory = Path.Combine(path, "src/main/res/xml");
                try
                {
                    if (!Directory.Exists(xmlDirectory))
                    {
                        Directory.CreateDirectory(xmlDirectory);
                    }

                    string targetPath = Path.Combine(xmlDirectory, "network_sec_config.xml");
                    // In many common cases such as P4, files can be read-only so when they are copied from a previous build it can fail
                    if (File.Exists(targetPath))
                    {
                        File.SetAttributes(targetPath, File.GetAttributes(targetPath) & ~FileAttributes.ReadOnly);
                    }
                    File.Copy(securityConfigFile, targetPath, true);
                    patchedSecurityConfig = true;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError(e.Message);
                }
            }
        }

        // Unity doesn't delete the entire gradle project anymore so we need to check manually if there's a manifest override in Assets/Plugins/Android
        OVRManifestPreprocessor.PatchAndroidManifest(file, enableSecurity: patchedSecurityConfig, skipExistingAttributes: CustomManifestExists());
    }

    private static string GetOculusProjectNetworkSecConfigPath()
    {
        var so = ScriptableObject.CreateInstance(typeof(OVRPluginInfo));
        var script = MonoScript.FromScriptableObject(so);
        string assetPath = AssetDatabase.GetAssetPath(script);
        string editorDir = Directory.GetParent(assetPath).FullName;
        string configAssetPath = Path.GetFullPath(Path.Combine(editorDir, "network_sec_config.xml"));
        Uri configUri = new Uri(configAssetPath);
        Uri projectUri = new Uri(Application.dataPath);
        Uri relativeUri = projectUri.MakeRelativeUri(configUri);

        return relativeUri.ToString();
    }

    private static bool CustomManifestExists()
    {
        string manifestPath = Path.Combine(Application.dataPath, "Plugins", "Android", "AndroidManifest.xml");
        return File.Exists(manifestPath);
    }

    public void OnPostprocessBuild(BuildReport report)
    {
#if UNITY_ANDROID
        SendXRPluginSettingsTelemetry();
        SendProjectSettingsTelemetry();

        if (autoIncrementVersion)
        {
            if ((report.summary.options & BuildOptions.Development) == 0)
            {
                PlayerSettings.Android.bundleVersionCode++;
                UnityEngine.Debug.Log("Incrementing version code to " + PlayerSettings.Android.bundleVersionCode);
            }
        }

        bool isExporting = true;
        foreach (var step in report.steps)
        {
            if (step.name.Contains("Compile scripts")
                || step.name.Contains("Building scenes")
                || step.name.Contains("Writing asset files")
                || step.name.Contains("Preparing APK resources")
                || step.name.Contains("Creating Android manifest")
                || step.name.Contains("Processing plugins")
                || step.name.Contains("Exporting project")
                || step.name.Contains("Building Gradle project"))
            {
#if BUILDSESSION
                UnityEngine.Debug.LogFormat("build_step_" + step.name.ToLower().Replace(' ', '_') + ": {0}", step.duration.TotalSeconds.ToString());
#endif
                if (step.name.Contains("Building Gradle project"))
                {
                    isExporting = false;
                }
            }
        }
#endif
        if (!report.summary.outputPath.Contains("OVRGradleTempExport"))
        {
#if BUILDSESSION
            UnityEngine.Debug.LogFormat("build_complete: {0}", (System.DateTime.Now - buildStartTime).TotalSeconds.ToString());
#endif
        }

#if UNITY_ANDROID
        if (!isExporting)
        {
            // Get the hosts path to Android SDK
            if (adbTool == null)
            {
                adbTool = new OVRADBTool(OVRConfig.Instance.GetAndroidSDKPath(false));
            }

            if (adbTool.isReady)
            {
                // Check to see if there are any ADB devices connected before continuing.
                List<string> devices = adbTool.GetDevices();
                if (devices.Count == 0)
                {
                    return;
                }

                // Clear current logs on device
                Process adbClearProcess;
                adbClearProcess = adbTool.RunCommandAsync(new string[] { "logcat --clear" }, null);

                // Add a timeout if we cannot get a response from adb logcat --clear in time.
                Stopwatch timeout = new Stopwatch();
                timeout.Start();
                while (!adbClearProcess.WaitForExit(100))
                {
                    if (timeout.ElapsedMilliseconds > 2000)
                    {
                        adbClearProcess.Kill();
                        return;
                    }
                }

                // Check if existing ADB process is still running, kill if needed
                if (adbProcess != null && !adbProcess.HasExited)
                {
                    adbProcess.Kill();
                }

                // Begin thread to time upload and install
                var thread = new Thread(delegate () { TimeDeploy(); });
                thread.Start();
            }
        }
#endif
    }

    public void SendOVRManagerSettingsTelemetry()
    {
        var ovrManager = OVRProjectSetupUtils.FindComponentInScene<OVRManager>();
        if (ovrManager != null)
        {
            OVRPlugin.SendEvent("ovr_manager_dynamic_resolution", OVRTelemetry.GetTelemetrySettingString(ovrManager.enableDynamicResolution));
            OVRPlugin.SendEvent("ovr_manager_min_quest2_resolution", ovrManager.quest2MinDynamicResolutionScale.ToString());
            OVRPlugin.SendEvent("ovr_manager_max_quest2_resolution", ovrManager.quest2MaxDynamicResolutionScale.ToString());
            OVRPlugin.SendEvent("ovr_manager_min_quest3_resolution", ovrManager.quest3MinDynamicResolutionScale.ToString());
            OVRPlugin.SendEvent("ovr_manager_max_quest3_resolution", ovrManager.quest3MaxDynamicResolutionScale.ToString());
        }
    }

    public void SendXRPluginSettingsTelemetry()
    {
#if USING_XR_MANAGEMENT
        OVRTelemetryConstants.ProjectSettings.XrPlugin xrplugin = OVRTelemetryConstants.ProjectSettings.XrPlugin.Unknown;
        var buildGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
        OVRTelemetryConstants.ProjectSettings.FoveatedRenderingMode foveatedRenderingMode = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingMode.Unknown;
        OVRTelemetryConstants.ProjectSettings.FoveatedRenderingAPI foveatedRenderingAPI = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingAPI.Unknown;
        OVRTelemetryConstants.ProjectSettings.DepthSubmissionMode depthSubmissionMode = OVRTelemetryConstants.ProjectSettings.DepthSubmissionMode.Unknown;
#if USING_XR_SDK_OCULUS
        var loader = GetActiveLoader<OculusLoader>(buildGroup);
        if (loader)
        {
            xrplugin = OVRTelemetryConstants.ProjectSettings.XrPlugin.Oculus;
            var oculusLoader = loader as OculusLoader;
            OculusSettings settings = oculusLoader.GetSettings();
            OVRPlugin.SendEvent("xr_optimize_buffer_discard", OVRTelemetry.GetTelemetrySettingString(settings.OptimizeBufferDiscards));
            OVRPlugin.SendEvent("xr_symmetric_projection", OVRTelemetry.GetTelemetrySettingString(settings.SymmetricProjection));
            OVRPlugin.SendEvent("xr_subsampled_layout", OVRTelemetry.GetTelemetrySettingString(settings.SubsampledLayout));

#if OCULUS_XR_PLUGIN_3_2_1_OR_NEWER
            depthSubmissionMode = settings.DepthSubmission ? OVRTelemetryConstants.ProjectSettings.DepthSubmissionMode.Depth24Bit : OVRTelemetryConstants.ProjectSettings.DepthSubmissionMode.None;

            switch (settings.FoveatedRenderingMethod)
            {
                case OculusSettings.FoveationMethod.FixedFoveatedRendering:
                    foveatedRenderingMode = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingMode.FixedFoveatedRendering;
                    foveatedRenderingAPI = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingAPI.Legacy;
                    break;
                case OculusSettings.FoveationMethod.EyeTrackedFoveatedRendering:
                    foveatedRenderingMode = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingMode.EyeTrackedFoveatedRendering;
                    foveatedRenderingAPI = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingAPI.Legacy;
                    break;
#if OCULUS_XR_PLUGIN_4_3_0_OR_NEWER && UNITY_2023_2_OR_NEWER && URP_16_OR_NEWER
                case OculusSettings.FoveationMethod.FixedFoveatedRenderingUsingUnityAPIForURP:
                    foveatedRenderingMode = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingMode.FixedFoveatedRendering;
                    foveatedRenderingAPI = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingAPI.SRP;
                    break;
                case OculusSettings.FoveationMethod.EyeTrackedFoveatedRenderingUsingUnityAPIForURP:
                    foveatedRenderingMode = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingMode.EyeTrackedFoveatedRendering;
                    foveatedRenderingAPI = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingAPI.SRP;
                    break;
#endif
            }
#endif
        }
#endif
#if USING_XR_SDK_OPENXR
        var xrloader = GetActiveLoader<OpenXRLoader>(buildGroup);
        if (xrloader)
        {
            xrplugin = OVRTelemetryConstants.ProjectSettings.XrPlugin.OpenXR;
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(buildGroup);

            if (settings != null)
            {
#if UNITY_OPENXR_PLUGIN_1_11_0_OR_NEWER
                switch (settings.foveatedRenderingApi)
                {
                    case OpenXRSettings.BackendFovationApi.Legacy:
                        foveatedRenderingAPI = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingAPI.Legacy;
                        break;
                    case OpenXRSettings.BackendFovationApi.SRPFoveation:
                        foveatedRenderingAPI = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingAPI.SRP;
                        break;
                }

                OVRPlugin.SendEvent("xr_optimize_buffer_discard", OVRTelemetry.GetTelemetrySettingString(settings.optimizeBufferDiscards));
                OVRPlugin.SendEvent("xr_symmetric_projection", OVRTelemetry.GetTelemetrySettingString(settings.symmetricProjection));
#endif

                switch (settings.depthSubmissionMode)
                {
                    case OpenXRSettings.DepthSubmissionMode.None:
                        depthSubmissionMode = OVRTelemetryConstants.ProjectSettings.DepthSubmissionMode.None;
                        break;
                    case OpenXRSettings.DepthSubmissionMode.Depth16Bit:
                        depthSubmissionMode = OVRTelemetryConstants.ProjectSettings.DepthSubmissionMode.Depth16Bit;
                        break;
                    case OpenXRSettings.DepthSubmissionMode.Depth24Bit:
                        depthSubmissionMode = OVRTelemetryConstants.ProjectSettings.DepthSubmissionMode.Depth24Bit;
                        break;
                }

                var metaXRETFRFeature = settings.GetFeature<Meta.XR.MetaXREyeTrackedFoveationFeature>();
                if (metaXRETFRFeature != null && metaXRETFRFeature.enabled)
                {
                    foveatedRenderingMode = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingMode.EyeTrackedFoveatedRendering;
                }
                else
                {
                    var metaXRFoveationFeature = settings.GetFeature<Meta.XR.MetaXRFoveationFeature>();
                    if (metaXRFoveationFeature != null && metaXRFoveationFeature.enabled)
                    {
                        foveatedRenderingMode = OVRTelemetryConstants.ProjectSettings.FoveatedRenderingMode.FixedFoveatedRendering;
                    }
                }


            }
        }
#endif
        OVRPlugin.SendEvent("xr_foveated_rendering_method", foveatedRenderingMode.ToString());
        OVRPlugin.SendEvent("xr_foveated_rendering_api", foveatedRenderingAPI.ToString());
        OVRPlugin.SendEvent("xr_depth_submission_mode", depthSubmissionMode.ToString());

        OVRPlugin.SendEvent("xr_plugin_type", xrplugin.ToString());
        OVRTelemetry.Start(OVRTelemetryConstants.ProjectSettings.MarkerId.XrPluginType)
            .AddAnnotation(OVRTelemetryConstants.ProjectSettings.AnnotationType.XrPluginType, xrplugin.ToString())
            .Send();
#endif
    }

    public void SendProjectSettingsTelemetry()
    {
        OVRTelemetryConstants.ProjectSettings.RenderThreadingMode mode = OVRTelemetryConstants.ProjectSettings.RenderThreadingMode.Unknown;
        if (PlayerSettings.graphicsJobs)
        {
            switch (PlayerSettings.graphicsJobMode)
            {
                case GraphicsJobMode.Legacy:
                    mode = OVRTelemetryConstants.ProjectSettings.RenderThreadingMode.LegacyGraphicsJobs;
                    break;
                case GraphicsJobMode.Native:
                    mode = OVRTelemetryConstants.ProjectSettings.RenderThreadingMode.NativeGraphicsJobs;
                    break;
            }
        }
        else if (PlayerSettings.MTRendering)
        {
            mode = OVRTelemetryConstants.ProjectSettings.RenderThreadingMode.Multithreaded;
        }
        OVRTelemetry.Start(OVRTelemetryConstants.ProjectSettings.MarkerId.RenderThreadingMode)
            .AddAnnotation(OVRTelemetryConstants.ProjectSettings.AnnotationType.RenderThreadingMode, mode.ToString())
            .Send();
        OVRPlugin.SendEvent("project_settings_graphics_jobs_mode", PlayerSettings.graphicsJobMode.ToString());
        OVRPlugin.SendEvent("project_settings_multithreaded_rendering", OVRTelemetry.GetTelemetrySettingString(PlayerSettings.MTRendering));

        var urpPackageInfo = PackageList.GetPackage("com.unity.render-pipelines.universal");
        if (urpPackageInfo != null)
        {
            OVRPlugin.SendEvent("urp_version", urpPackageInfo.version);
            OVRPlugin.SendEvent("urp_source", urpPackageInfo.source.ToString());
            if (urpPackageInfo.source == UnityEditor.PackageManager.PackageSource.Git)
            {
                // Log the git branch if using our fork.
                if (urpPackageInfo.packageId.Contains("https://github.com/Oculus-VR/Unity-Graphics.git"))
                {
                    OVRPlugin.SendEvent("urp_git_revision", urpPackageInfo.git.revision);
                }
            }
        }

        var openXRPackageInfo = PackageList.GetPackage("com.unity.xr.openxr");
        if (openXRPackageInfo != null)
        {
            OVRPlugin.SendEvent("openxrplugin_version", openXRPackageInfo.version);
        }

        var oculusXRPackageInfo = PackageList.GetPackage("com.unity.xr.oculus");
        if (oculusXRPackageInfo != null)
        {
            OVRPlugin.SendEvent("oculusxrplugin_version", oculusXRPackageInfo.version);
        }

#if URP_14_OR_NEWER
        var pipelineAssets = new System.Collections.Generic.List<RenderPipelineAsset>();
        QualitySettings.GetAllRenderPipelineAssetsForPlatform("Android", ref pipelineAssets);
        foreach (var pipelineAsset in pipelineAssets)
        {
            var urpPipelineAsset = pipelineAsset as UniversalRenderPipelineAsset;
            if (urpPipelineAsset != null)
            {
                OVRPlugin.SendEvent("urp_depth_texture", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.supportsCameraDepthTexture));
                OVRPlugin.SendEvent("urp_color_texture", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.supportsCameraOpaqueTexture));
                OVRPlugin.SendEvent("urp_srp_batcher", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.useSRPBatcher));
                OVRPlugin.SendEvent("urp_dynamic_batching", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.supportsDynamicBatching));
                OVRPlugin.SendEvent("urp_hdr", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.supportsHDR));
                OVRPlugin.SendEvent("urp_hdr_precision", urpPipelineAsset.hdrColorBufferPrecision.ToString());
                OVRPlugin.SendEvent("urp_msaa", urpPipelineAsset.msaaSampleCount.ToString());
                OVRPlugin.SendEvent("urp_render_scale", urpPipelineAsset.renderScale.ToString());
                OVRPlugin.SendEvent("urp_upscale_filtering", urpPipelineAsset.upscalingFilter.ToString());
                OVRPlugin.SendEvent("urp_main_light_rendering_mode", urpPipelineAsset.mainLightRenderingMode.ToString());
                OVRPlugin.SendEvent("urp_main_light_cast_shadows", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.supportsMainLightShadows));
                if (urpPipelineAsset.supportsMainLightShadows)
                {
                    OVRPlugin.SendEvent("urp_main_light_shadow_resolution", urpPipelineAsset.mainLightShadowmapResolution.ToString());
                }

                OVRPlugin.SendEvent("urp_additional_lights_rendering_mode", urpPipelineAsset.additionalLightsRenderingMode.ToString());
                OVRPlugin.SendEvent("urp_additional_lights_per_object_limit", urpPipelineAsset.maxAdditionalLightsCount.ToString());
                OVRPlugin.SendEvent("urp_additional_lights_cast_shadows", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.supportsAdditionalLightShadows));
                OVRPlugin.SendEvent("urp_additional_lights_shadow_atlas_resolution", urpPipelineAsset.additionalLightsShadowmapResolution.ToString());

#if URP_17_OR_NEWER
                OVRPlugin.SendEvent("urp_light_probe_system", urpPipelineAsset.lightProbeSystem.ToString());
#endif
                OVRPlugin.SendEvent("urp_reflection_probe_blending", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.reflectionProbeBlending));
                OVRPlugin.SendEvent("urp_reflection_probe_box_projection", OVRTelemetry.GetTelemetrySettingString(urpPipelineAsset.reflectionProbeBoxProjection));

#if (URP_17_OR_NEWER)
                foreach (var rendererData in urpPipelineAsset.rendererDataList)
                {
                    UniversalRendererData urpRendererData = rendererData as UniversalRendererData;

#else
                UniversalRendererData urpRendererData = null;
                var path = AssetDatabase.GetAssetPath(urpPipelineAsset);
                var dependency = AssetDatabase.GetDependencies(path);
                for (int i = 0; i < dependency.Length; i++)
                {
                    if (AssetDatabase.GetMainAssetTypeAtPath(dependency[i]) != typeof(UniversalRendererData))
                        continue;

                    urpRendererData = (UniversalRendererData)AssetDatabase.LoadAssetAtPath(dependency[i], typeof(UniversalRendererData));
#endif

                    if (urpRendererData != null)
                    {
                        OVRTelemetryConstants.ProjectSettings.RenderingPath renderingPath = OVRTelemetryConstants.ProjectSettings.RenderingPath.Unknown;
                        switch (urpRendererData.renderingMode)
                        {
                            case RenderingMode.Forward:
                                renderingPath = OVRTelemetryConstants.ProjectSettings.RenderingPath.Forward;
                                break;
                            case RenderingMode.ForwardPlus:
                                renderingPath = OVRTelemetryConstants.ProjectSettings.RenderingPath.ForwardPlus;
                                break;
                            case RenderingMode.Deferred:
                                renderingPath = OVRTelemetryConstants.ProjectSettings.RenderingPath.Deferred;
                                break;
                        }
                        OVRTelemetry.Start(OVRTelemetryConstants.ProjectSettings.MarkerId.RenderingPath)
                            .AddAnnotation(OVRTelemetryConstants.ProjectSettings.AnnotationType.RenderingPath, renderingPath.ToString())
                            .Send();

                        OVRPlugin.SendEvent("urp_rendering_path", urpRendererData.renderingMode.ToString());
                        OVRPlugin.SendEvent("urp_depth_priming_mode", urpRendererData.depthPrimingMode.ToString());
                        OVRPlugin.SendEvent("urp_copy_depth_mode", urpRendererData.copyDepthMode.ToString());
                        OVRPlugin.SendEvent("urp_post_processing", OVRTelemetry.GetTelemetrySettingString(urpRendererData.postProcessData != null));
                        foreach (var feature in urpRendererData.rendererFeatures)
                        {
                            OVRPlugin.SendEvent("urp_renderer_feature", feature.name);
                        }
                    }
                }
            }
        }
#endif
    }

#if USING_XR_MANAGEMENT
    public UnityEngine.XR.Management.XRLoader GetActiveLoader<T>(BuildTargetGroup group)
    {
        var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
        foreach (var activeLoader in settings.Manager.activeLoaders)
        {
            if (activeLoader is T)
            {
                return activeLoader;
            }
        }
        return null;
    }
#endif

#if UNITY_ANDROID
    public bool WaitForProcess;
    public bool TransferStarted;
    public DateTime UploadStart;
    public DateTime UploadEnd;
    public DateTime InstallEnd;

    public void TimeDeploy()
    {
        if (adbTool != null)
        {
            TransferStarted = false;
            DataReceivedEventHandler outputRecieved = new DataReceivedEventHandler(
                (s, e) =>
                {
                    if (e.Data != null && e.Data.Length != 0 && !e.Data.Contains("\u001b"))
                    {
                        if (e.Data.Contains("free_cache"))
                        {
                            // Device recieved install command and is starting upload
                            UploadStart = System.DateTime.Now;
                            TransferStarted = true;
                        }
                        else if (e.Data.Contains("Running dexopt"))
                        {
                            // Upload has finished and Package Manager is starting install
                            UploadEnd = System.DateTime.Now;
                        }
                        else if (e.Data.Contains("dex2oat took"))
                        {
                            // Package Manager finished install
                            InstallEnd = System.DateTime.Now;
                            WaitForProcess = false;
                        }
                        else if (e.Data.Contains("W PackageManager"))
                        {
                            // Warning from Package Manager is a failure in the install process
                            WaitForProcess = false;
                        }
                    }
                }
            );

            WaitForProcess = true;
            adbProcess = adbTool.RunCommandAsync(new string[] { "logcat" }, outputRecieved);

            Stopwatch transferTimeout = new Stopwatch();
            transferTimeout.Start();
            while (adbProcess != null && !adbProcess.WaitForExit(100))
            {
                if (!WaitForProcess)
                {
                    adbProcess.Kill();
                }

                if (!TransferStarted && transferTimeout.ElapsedMilliseconds > 5000)
                {
                    adbProcess.Kill();
                }
            }
        }
    }
#endif
}
