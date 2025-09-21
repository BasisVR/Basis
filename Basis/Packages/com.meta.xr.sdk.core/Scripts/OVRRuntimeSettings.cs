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

using UnityEngine;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// This class serializes the settings used in the Meta Core SDK and are read at runtime. When read, a single instance is created and can be accessed globally through
/// OVRRuntimeSettings.Instance <see cref="OVRRuntimeSettings.Instance"/>.
/// </summary>
public class OVRRuntimeSettings : OVRRuntimeAssetsBase
{
    private const string _assetName = "OculusRuntimeSettings";
    private static OVRRuntimeSettings _instance;

    private static readonly OVRHandSkeletonVersion NewProjectDefaultSkeletonVersion = OVRHandSkeletonVersion.OpenXR;

    [SerializeField]
    private OVRHandSkeletonVersion handSkeletonVersion = NewProjectDefaultSkeletonVersion;

    /// <summary>
    /// Sets the version of hand skeleton that will be used in hand tracking. You can also use it to check which hand skeleton version is currently being used.
    /// </summary>
    public OVRHandSkeletonVersion HandSkeletonVersion
    {
        get => handSkeletonVersion;
        set => handSkeletonVersion = value;
    }

    /// <summary>
    /// Access to the singleton instance of OVRRuntimeSettings.  This is cached when the settings are loaded, and is recommended over <see cref="OVRRuntimeSettings.GetRuntimeSettings()"/>.
    /// </summary>
    public static OVRRuntimeSettings Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = GetRuntimeSettings();
            }

            return _instance;
        }
    }

    /// <summary>
    /// The color space that is used for the app and defaults to P3 color space. For more information on color spaces on Quest devices,
    /// please refer to the documentation on [color spaces](https://developer.oculus.com/documentation/unity/unity-color-space/).
    /// </summary>
    public OVRManager.ColorSpace colorSpace = OVRManager.ColorSpace.P3;

    [SerializeField] private bool requestsVisualFaceTracking = true;

    /// <summary>
    /// Sets if the app uses visuals as a data source for face tracking. Also used to check if the app is requesting use of visual face tracking.
    /// </summary>
    public bool RequestsVisualFaceTracking
    {
        get => requestsVisualFaceTracking;
        set => requestsVisualFaceTracking = value;
    }

    [SerializeField] private bool requestsAudioFaceTracking = true;

    /// <summary>
    /// Sets if the app uses audio as a data source for face tracking. Also used to check if the app is requesting use of audio face tracking.
    /// </summary>
    public bool RequestsAudioFaceTracking
    {
        get => requestsAudioFaceTracking;
        set => requestsAudioFaceTracking = value;
    }

    [SerializeField] private bool enableFaceTrackingVisemesOutput = false;
    public bool EnableFaceTrackingVisemesOutput
    {
        get => enableFaceTrackingVisemesOutput;
        set
        {
            enableFaceTrackingVisemesOutput = value;
            OVRPlugin.SetFaceTrackingVisemesEnabled(enableFaceTrackingVisemesOutput);
        }
    }

    [SerializeField] private string telemetryProjectGuid;
    internal string TelemetryProjectGuid
    {
        get
        {
            if (string.IsNullOrEmpty(telemetryProjectGuid))
            {
                telemetryProjectGuid = Guid.NewGuid().ToString();
#if UNITY_EDITOR
                CommitRuntimeSettings(this);
#endif
            }
            return telemetryProjectGuid;
        }
    }


    [SerializeField] private OVRPlugin.BodyTrackingFidelity2 bodyTrackingFidelity = OVRPlugin.BodyTrackingFidelity2.Low;

    /// <summary>
    /// Sets the body tracking fidelity to either High or Low. Can also be used to check which body tracking fidelity is currently being used.
    /// </summary>
    public OVRPlugin.BodyTrackingFidelity2 BodyTrackingFidelity
    {
        get => bodyTrackingFidelity;
        set => bodyTrackingFidelity = value;
    }

    [SerializeField] private OVRPlugin.BodyJointSet bodyTrackingJointSet = OVRPlugin.BodyJointSet.UpperBody;

    /// <summary>
    /// Sets which kind of body joint set to use for body tracking. Can also be used to check which body joint set is currently being used.
    /// </summary>
    public OVRPlugin.BodyJointSet BodyTrackingJointSet
    {
        get => bodyTrackingJointSet;
        set => bodyTrackingJointSet = value;
    }

    [SerializeField] private bool allowVisibilityMesh = false;

    public bool VisibilityMesh
    {
        get => allowVisibilityMesh;
        set => allowVisibilityMesh = value;
    }

    public bool QuestVisibilityMeshOverriden = false;


#if UNITY_EDITOR
    /// <summary>
    /// Returns the path to the OVRRuntimeSettings asset in the project as a string.
    /// </summary>
    public static string GetOculusRuntimeSettingsAssetPath()
    {
        return GetAssetPath(_assetName);
    }

    /// <summary>
    /// Saves any changes made to OVRRuntimeSettings to the asset. This should only be used by editor scripts that modify OVRRuntimeSettings.
    /// </summary>
    public static void CommitRuntimeSettings(OVRRuntimeSettings runtimeSettings)
    {
        string runtimeSettingsAssetPath = GetOculusRuntimeSettingsAssetPath();
        if (AssetDatabase.GetAssetPath(runtimeSettings) != runtimeSettingsAssetPath)
        {
            Debug.LogWarningFormat("The asset path of RuntimeSettings is wrong. Expect {0}, get {1}",
                runtimeSettingsAssetPath, AssetDatabase.GetAssetPath(runtimeSettings));
        }

        EditorUtility.SetDirty(runtimeSettings);
    }
#endif

    /// <summary>
    /// Returns the OVRRuntimeSettings instance that contains the current settings that will be loaded at runtime.
    /// We recommend using OVRRuntimeSettings.Instance <see cref="OVRRuntimeSettings.Instance"/> to get the cached value.
    /// </summary>
    /// <returns>Loaded OVRRuntimeSettings object.</returns>
    public static OVRRuntimeSettings GetRuntimeSettings()
    {
        LoadAsset(out OVRRuntimeSettings settings, _assetName, HandleSettingsCreated);
#if !UNITY_EDITOR
        if (settings == null)
        {
            Debug.LogWarning("Failed to load runtime settings. Using default runtime settings instead.");
            settings = ScriptableObject.CreateInstance<OVRRuntimeSettings>();
            HandleSettingsCreated(settings);
        }
#endif
        return settings;
    }

    private static void HandleSettingsCreated(OVRRuntimeSettings settings)
    {
    }
}
