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

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using System;
using Oculus.VR.Editor;
using UnityEngine.Serialization;
using System.Linq;


[System.Serializable]
#if UNITY_EDITOR
[UnityEditor.InitializeOnLoad]
#endif
public class OVRProjectConfig : ScriptableObject, ISerializationCallbackReceiver
{
    // Consider targetDeviceTypes when modifying
    public enum DeviceType
    {
        //GearVrOrGo = 0, // DEPRECATED
        Quest = 1,
        Quest2 = 2,
        QuestPro = 3,
        Quest3 = 4,
        Quest3S = 5,
    }

    public enum HandTrackingSupport
    {
        ControllersOnly = 0,
        ControllersAndHands = 1,
        HandsOnly = 2
    }

    public enum HandTrackingFrequency
    {
        LOW = 0,
        HIGH = 1,
        MAX = 2
    }

    public enum HandTrackingVersion
    {
        Default = 0,
        V1 = 1,
        V2 = 2
    }

    public enum AnchorSupport
    {
        Disabled = 0,
        Enabled = 1,
    }

    public enum RenderModelSupport
    {
        Disabled = 0,
        Enabled = 1,
    }

    public enum TrackedKeyboardSupport
    {
        None = 0,
        Supported = 1,
        Required = 2
    }

    public enum ProcessorFavor
    {
        FavorEqually = 0,
        FavorCPU = -1,
        FavorGPU = 1
    }

    public enum FeatureSupport
    {
        None = 0,
        Supported = 1,
        Required = 2
    }

    public static readonly int minSdkVersion = 68;
    public static readonly int[] skippedSdkVersions = { 70, 73, 75 };
    public static int currentSdkVersion = OVRPlugin.wrapperVersion.Minor - 32;
    public static int[] horizonOsSdkVersions = Enumerable.Range(minSdkVersion, currentSdkVersion - minSdkVersion + 1)
    .Except(skippedSdkVersions)
    .ToArray();

    public List<DeviceType> targetDeviceTypes = new()
        { DeviceType.Quest, DeviceType.Quest2, DeviceType.QuestPro, DeviceType.Quest3, DeviceType.Quest3S };

    public bool allowOptional3DofHeadTracking = false;
    public HandTrackingSupport handTrackingSupport = HandTrackingSupport.ControllersOnly;
    public HandTrackingFrequency handTrackingFrequency = HandTrackingFrequency.LOW;
    public HandTrackingVersion handTrackingVersion = HandTrackingVersion.Default;

    [FormerlySerializedAs("spatialAnchorsSupport")]
    public AnchorSupport anchorSupport = AnchorSupport.Disabled;

    public FeatureSupport sharedAnchorSupport = FeatureSupport.None;
    public RenderModelSupport renderModelSupport = RenderModelSupport.Disabled;
    public TrackedKeyboardSupport trackedKeyboardSupport = TrackedKeyboardSupport.None;
    public FeatureSupport bodyTrackingSupport = FeatureSupport.None;
    public FeatureSupport faceTrackingSupport = FeatureSupport.None;
    public FeatureSupport eyeTrackingSupport = FeatureSupport.None;
    public FeatureSupport virtualKeyboardSupport = FeatureSupport.None;
    public FeatureSupport colocationSessionSupport = FeatureSupport.None;
    public FeatureSupport sceneSupport = FeatureSupport.None;
    public FeatureSupport boundaryVisibilitySupport = FeatureSupport.None;

    public bool disableBackups = true;
    public bool enableNSCConfig = true;
    public string securityXmlPath;
    public bool horizonOsSdkEnabled = false;
    public int minHorizonOsSdkVersion = minSdkVersion;
    public int targetHorizonOsSdkVersion = currentSdkVersion;

    public bool skipUnneededShaders = false;

    public bool enableIL2CPPLTO = false;

    public bool removeGradleManifest = true;

    [System.Obsolete("Focus awareness is now required. The option will be deprecated.", false)]
    public bool focusAware = true;

    public bool requiresSystemKeyboard = false;
    public bool experimentalFeaturesEnabled = false;

    [Obsolete("This value has no effect. Use " + nameof(insightPassthroughSupport) + " instead.")]
    public bool insightPassthroughEnabled;

    public FeatureSupport insightPassthroughSupport
    {
        get => _insightPassthroughSupport;
        set => _insightPassthroughSupport = value;
    }

    [SerializeField]
    internal FeatureSupport _insightPassthroughSupport = FeatureSupport.None;

    public ProcessorFavor processorFavor
    {
        get => _processorFavor;
        set => _processorFavor = value;
    }

    [SerializeField]
    internal ProcessorFavor _processorFavor = ProcessorFavor.FavorEqually;

    public enum SystemSplashScreenType
    {
        Mono = 0,
        Stereo = 1,
    }

    public enum SystemLoadingScreenBackground
    {
        Black = 0,
        [InspectorName("Passthrough (Contextual)")]
        ContextualPassthrough = 1
    }

    public Texture2D systemSplashScreen;
    public SystemSplashScreenType systemSplashScreenType;

    public SystemLoadingScreenBackground systemLoadingScreenBackground
    {
        get => _systemLoadingScreenBackground;
        set => _systemLoadingScreenBackground = value;
    }

    [SerializeField]
    internal SystemLoadingScreenBackground _systemLoadingScreenBackground = SystemLoadingScreenBackground.Black;


    //public const string OculusProjectConfigAssetPath = "Assets/Oculus/OculusProjectConfig.asset";

    private static OVRProjectConfig _cachedProjectConfig;
    public static OVRProjectConfig CachedProjectConfig
    {
        get
        {
            if (_cachedProjectConfig == null)
            {
                _cachedProjectConfig = GetOrCreateProjectConfig();
            }

            return _cachedProjectConfig;
        }
    }

    static OVRProjectConfig()
    {
        // BuildPipeline.isBuildingPlayer cannot be called in a static constructor
        // Run Update once to call GetProjectConfig then remove delegate
        EditorApplication.update += Update;
    }

    static void Update()
    {
        // Initialize the asset if it doesn't exist
        GetOrCreateProjectConfig();
        // Stop running Update
        EditorApplication.update -= Update;
    }

    internal static string ComputeOculusProjectAssetPath(string assetName)
    {
        string oculusDir;
        if (OVRPluginInfo.IsInsidePackageDistribution())
        {
            oculusDir = Path.GetFullPath(Path.Combine(Application.dataPath, "Oculus"));
            if (!Directory.Exists(oculusDir))
            {
                Directory.CreateDirectory(oculusDir);
            }
        }
        else
        {
            var so = ScriptableObject.CreateInstance(typeof(OVRPluginInfo));
            var script = MonoScript.FromScriptableObject(so);
            string assetPath = AssetDatabase.GetAssetPath(script);
            string editorDir = Directory.GetParent(assetPath).FullName;
            string ovrDir = Directory.GetParent(editorDir).FullName;
            oculusDir = Directory.GetParent(ovrDir).FullName;
        }

        string configAssetPath = Path.GetFullPath(Path.Combine(oculusDir, assetName));
        Uri configUri = new Uri(configAssetPath);
        Uri projectUri = new Uri(Application.dataPath);
        Uri relativeUri = projectUri.MakeRelativeUri(configUri);

        return relativeUri.ToString();
    }

    private static string ComputeOculusProjectConfigAssetPath()
    {
        return ComputeOculusProjectAssetPath("OculusProjectConfig.asset");
    }

    private static OVRProjectConfig GetOrCreateProjectConfig()
    {
        OVRProjectConfig projectConfig = null;
        string oculusProjectConfigAssetPath = ComputeOculusProjectConfigAssetPath();
        try
        {
            projectConfig =
                AssetDatabase.LoadAssetAtPath(oculusProjectConfigAssetPath, typeof(OVRProjectConfig)) as
                    OVRProjectConfig;
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("Unable to load ProjectConfig from {0}, error {1}", oculusProjectConfigAssetPath,
                e.Message);
        }

        // Initialize the asset only if a build is not currently running.
        if (projectConfig == null && !BuildPipeline.isBuildingPlayer)
        {
            if (File.Exists(Path.GetFullPath(oculusProjectConfigAssetPath)))
            {
                Debug.LogError("OVRProjectConfig exists but could not be loaded. Config values may not be available until restart.");
            }
            else
            {
                Debug.LogFormat("Creating ProjectConfig at path {0}", oculusProjectConfigAssetPath);
                projectConfig = ScriptableObject.CreateInstance<OVRProjectConfig>();
                projectConfig.targetDeviceTypes = new List<DeviceType>();
                projectConfig.targetDeviceTypes.Add(DeviceType.Quest);
                projectConfig.targetDeviceTypes.Add(DeviceType.Quest2);
                projectConfig.targetDeviceTypes.Add(DeviceType.QuestPro);
                projectConfig.targetDeviceTypes.Add(DeviceType.Quest3);
                projectConfig.targetDeviceTypes.Add(DeviceType.Quest3S);
                projectConfig.allowOptional3DofHeadTracking = false;
                projectConfig.handTrackingSupport = HandTrackingSupport.ControllersOnly;
                projectConfig.handTrackingFrequency = HandTrackingFrequency.LOW;
                projectConfig.handTrackingVersion = HandTrackingVersion.Default;
                projectConfig.anchorSupport = AnchorSupport.Disabled;
                projectConfig.sharedAnchorSupport = FeatureSupport.None;
                projectConfig.trackedKeyboardSupport = TrackedKeyboardSupport.None;
                projectConfig.renderModelSupport = RenderModelSupport.Disabled;
                projectConfig.bodyTrackingSupport = FeatureSupport.None;
                projectConfig.faceTrackingSupport = FeatureSupport.None;
                projectConfig.eyeTrackingSupport = FeatureSupport.None;
                projectConfig.virtualKeyboardSupport = FeatureSupport.None;
                projectConfig.colocationSessionSupport = FeatureSupport.None;
                projectConfig.sceneSupport = FeatureSupport.None;
                projectConfig.boundaryVisibilitySupport = FeatureSupport.None;
                projectConfig.disableBackups = true;
                projectConfig.enableNSCConfig = true;
                projectConfig.skipUnneededShaders = false;
                projectConfig.requiresSystemKeyboard = false;
                projectConfig.experimentalFeaturesEnabled = false;
                projectConfig.insightPassthroughSupport = FeatureSupport.None;
                projectConfig.horizonOsSdkEnabled = false;
                projectConfig.minHorizonOsSdkVersion = horizonOsSdkVersions[0];
                projectConfig.targetHorizonOsSdkVersion = horizonOsSdkVersions[horizonOsSdkVersions.Length - 1];
                AssetDatabase.CreateAsset(projectConfig, oculusProjectConfigAssetPath);
            }
        }

        if (projectConfig == null)
        {
            return null;
        }


        // Force migration to Quest device if still on legacy GearVR/Go device type
        if (projectConfig.targetDeviceTypes.Contains((DeviceType)0)) // deprecated GearVR/Go device
        {
            projectConfig.targetDeviceTypes.Remove((DeviceType)0); // deprecated GearVR/Go device
            if (!projectConfig.targetDeviceTypes.Contains(DeviceType.Quest))
            {
                projectConfig.targetDeviceTypes.Add(DeviceType.Quest);
            }

            if (!projectConfig.targetDeviceTypes.Contains(DeviceType.Quest2))
            {
                projectConfig.targetDeviceTypes.Add(DeviceType.Quest2);
            }

            if (!projectConfig.targetDeviceTypes.Contains(DeviceType.QuestPro))
            {
                projectConfig.targetDeviceTypes.Add(DeviceType.QuestPro);
            }

            if (!projectConfig.targetDeviceTypes.Contains(DeviceType.Quest3))
            {
                projectConfig.targetDeviceTypes.Add(DeviceType.Quest3);
            }

            if (!projectConfig.targetDeviceTypes.Contains(DeviceType.Quest3S))
            {
                projectConfig.targetDeviceTypes.Add(DeviceType.Quest3S);
            }
        }

        return projectConfig;
    }

    public static void CommitProjectConfig(OVRProjectConfig projectConfig)
    {
        string oculusProjectConfigAssetPath = ComputeOculusProjectConfigAssetPath();
        if (AssetDatabase.GetAssetPath(projectConfig) != oculusProjectConfigAssetPath)
        {
            Debug.LogWarningFormat("The asset path of ProjectConfig is wrong. Expect {0}, get {1}",
                oculusProjectConfigAssetPath, AssetDatabase.GetAssetPath(projectConfig));
        }

        EditorUtility.SetDirty(projectConfig);
    }

    void ISerializationCallbackReceiver.OnBeforeSerialize()
    {
    }

    void ISerializationCallbackReceiver.OnAfterDeserialize()
    {
#pragma warning disable CS0618
        // If it was previously enabled, map that to FeatureSupport.Required
        if (insightPassthroughEnabled)
        {
            if (_insightPassthroughSupport == FeatureSupport.None)
            {
                _insightPassthroughSupport = FeatureSupport.Required;
            }

            insightPassthroughEnabled = false;
        }
#pragma warning restore CS0618
    }
}

internal static class OVRProjectConfigExtensions
{
    public static string ToRequiredAttributeValue(this OVRProjectConfig.FeatureSupport value)
        => value == OVRProjectConfig.FeatureSupport.Required ? "true" : "false";

    public static string ToManifestTag(this OVRProjectConfig.SystemSplashScreenType type)
    {
        return type switch
        {
            OVRProjectConfig.SystemSplashScreenType.Mono => "mono",
            OVRProjectConfig.SystemSplashScreenType.Stereo => "stereo",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }
}
