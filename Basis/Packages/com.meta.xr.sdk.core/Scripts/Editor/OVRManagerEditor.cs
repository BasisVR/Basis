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
using UnityEditor;
using System.IO;
using UnityEditor.SceneManagement;

[CustomEditor(typeof(OVRManager))]
public class OVRManagerEditor : Editor
{
    private SerializedProperty _requestBodyTrackingPermissionOnStartup;
    private SerializedProperty _requestFaceTrackingPermissionOnStartup;
    private SerializedProperty _requestEyeTrackingPermissionOnStartup;
    private SerializedProperty _requestScenePermissionOnStartup;
    private SerializedProperty _requestRecordAudioPermissionOnStartup;
    private bool _expandPermissionsRequest;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_ANDROID
    private bool _showFaceTrackingDataSources = false;
#endif

    void OnEnable()
    {
        _requestBodyTrackingPermissionOnStartup =
            serializedObject.FindProperty(nameof(OVRManager.requestBodyTrackingPermissionOnStartup));
        _requestFaceTrackingPermissionOnStartup =
            serializedObject.FindProperty(nameof(OVRManager.requestFaceTrackingPermissionOnStartup));
        _requestEyeTrackingPermissionOnStartup =
            serializedObject.FindProperty(nameof(OVRManager.requestEyeTrackingPermissionOnStartup));
        _requestScenePermissionOnStartup =
            serializedObject.FindProperty(nameof(OVRManager.requestScenePermissionOnStartup));
        _requestRecordAudioPermissionOnStartup =
            serializedObject.FindProperty(nameof(OVRManager.requestRecordAudioPermissionOnStartup));

        OVRManager manager = target as OVRManager;
        manager?.UpdateDynamicResolutionVersion();

        OVRRuntimeSettings runtimeSettings = OVRRuntimeSettings.GetRuntimeSettings();
        if (!runtimeSettings.QuestVisibilityMeshOverriden)
        {
            string occlusionMeshShaderPath = "Packages/com.unity.render-pipelines.universal/Shaders/XR/XROcclusionMesh.shader";
            if (File.Exists(occlusionMeshShaderPath))
            {
                string shaderText = File.ReadAllText(occlusionMeshShaderPath);
                if (shaderText.Contains("USE_XR_OCCLUSION_MESH_COMBINED_MULTIVIEW"))
                {
                    runtimeSettings.VisibilityMesh = true;
                    OVRRuntimeSettings.CommitRuntimeSettings(runtimeSettings);
                }
            }
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.ApplyModifiedProperties();
        OVRRuntimeSettings runtimeSettings = OVRRuntimeSettings.GetRuntimeSettings();
        OVRProjectConfig projectConfig = OVRProjectConfig.CachedProjectConfig;

#if UNITY_ANDROID
        OVRProjectConfigEditor.DrawTargetDeviceInspector(projectConfig);
        EditorGUILayout.Space();
#endif

        DrawDefaultInspector();

        bool modified = false;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_ANDROID
        OVRManager manager = (OVRManager)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Display", EditorStyles.boldLabel);

        OVRManager.ColorSpace colorGamut = runtimeSettings.colorSpace;
        OVREditorUtil.SetupEnumField(target, new GUIContent("Color Gamut",
                "The target color gamut when displayed on the HMD"), ref colorGamut, ref modified,
            "https://developer.oculus.com/documentation/unity/unity-color-space/");
        manager.colorGamut = colorGamut;

        bool visibilityMesh = runtimeSettings.VisibilityMesh;
#if UNITY_ANDROID
        OVREditorUtil.SetupBoolField(target, new GUIContent("Visibility Mesh", "Toggle Visibility Mesh for Quest. This feature is supported on OpenXR with URP 14.0.7 Meta Fork or URP 17.0.3."), ref visibilityMesh, ref modified);
#endif

        if (modified)
        {
            runtimeSettings.colorSpace = colorGamut;
            if (runtimeSettings.VisibilityMesh != visibilityMesh)
            {
                runtimeSettings.VisibilityMesh = visibilityMesh;
                runtimeSettings.QuestVisibilityMeshOverriden = true;
            }
            OVRRuntimeSettings.CommitRuntimeSettings(runtimeSettings);
        }
#endif

        EditorGUILayout.Space();
        OVRProjectConfigEditor.DrawProjectConfigInspector(projectConfig);

#if UNITY_ANDROID
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Mixed Reality Capture for Quest", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        OVREditorUtil.SetupEnumField(target, "ActivationMode", ref manager.mrcActivationMode, ref modified);
        EditorGUI.indentLevel--;
#endif

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        manager.expandMixedRealityCapturePropertySheet =
            EditorGUILayout.BeginFoldoutHeaderGroup(manager.expandMixedRealityCapturePropertySheet,
                "Mixed Reality Capture");
        OVREditorUtil.DisplayDocLink("https://developer.oculus.com/documentation/unity/unity-mrc/");
        EditorGUILayout.EndHorizontal();
        if (manager.expandMixedRealityCapturePropertySheet)
        {
            string[] layerMaskOptions = new string[32];
            for (int i = 0; i < 32; ++i)
            {
                layerMaskOptions[i] = LayerMask.LayerToName(i);
                if (layerMaskOptions[i].Length == 0)
                {
                    layerMaskOptions[i] = "<Layer " + i.ToString() + ">";
                }
            }

            EditorGUI.indentLevel++;

            OVREditorUtil.SetupBoolField(target, "Enable MixedRealityCapture", ref manager.enableMixedReality,
                ref modified);
            OVREditorUtil.SetupEnumField(target, "Composition Method", ref manager.compositionMethod, ref modified);
            OVREditorUtil.SetupLayerMaskField(target, "Extra Hidden Layers", ref manager.extraHiddenLayers,
                layerMaskOptions, ref modified);
            OVREditorUtil.SetupLayerMaskField(target, "Extra Visible Layers", ref manager.extraVisibleLayers,
                layerMaskOptions, ref modified);
            OVREditorUtil.SetupBoolField(target, "Dynamic Culling Mask", ref manager.dynamicCullingMask, ref modified);

            // CompositionMethod.External is the only composition method that is available.
            // All other deprecated composition methods should fallback to the path below.
            {
                // CompositionMethod.External
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("External Composition", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;

                OVREditorUtil.SetupColorField(target, "Backdrop Color (Target, Rift)",
                    ref manager.externalCompositionBackdropColorRift, ref modified);
                OVREditorUtil.SetupColorField(target, "Backdrop Color (Target, Quest)",
                    ref manager.externalCompositionBackdropColorQuest, ref modified);
                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndFoldoutHeaderGroup();
#endif

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_ANDROID
        // Multimodal hands and controllers section
#if UNITY_ANDROID
        bool launchSimultaneousHandsControllersOnStartup = manager.SimultaneousHandsAndControllersEnabled;
        EditorGUI.BeginDisabledGroup(!launchSimultaneousHandsControllersOnStartup);
        GUIContent enableSimultaneousHandsAndControllersOnStartup = new GUIContent("Launch simultaneous hands and controllers mode on startup",
            "Launches simultaneous hands and controllers on startup for the scene. Simultaneous Hands and Controllers Capability must be enabled in the project settings.");
#else
        GUIContent enableSimultaneousHandsAndControllersOnStartup = new GUIContent("Enable simultaneous hands and controllers mode on startup",
            "Launches simultaneous hands and controllers on startup for the scene.");
#endif
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Simultaneous hands and controllers", EditorStyles.boldLabel);
#if UNITY_ANDROID
        if (!launchSimultaneousHandsControllersOnStartup)
        {
            EditorGUILayout.LabelField(
                "Requires Simultaneous Hands and Controllers Capability to be enabled in the General section of the Quest features.",
                EditorStyles.wordWrappedLabel);
        }
#endif
        OVREditorUtil.SetupBoolField(target, enableSimultaneousHandsAndControllersOnStartup,
            ref manager.launchSimultaneousHandsControllersOnStartup,
            ref modified);
#if UNITY_ANDROID
        EditorGUI.EndDisabledGroup();
#endif
#endif

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_ANDROID
        // Insight Passthrough section
#if UNITY_ANDROID
        bool passthroughCapabilityEnabled =
            projectConfig.insightPassthroughSupport != OVRProjectConfig.FeatureSupport.None;
        EditorGUI.BeginDisabledGroup(!passthroughCapabilityEnabled);
        GUIContent enablePassthroughContent = new GUIContent("Enable Passthrough",
            "Enables passthrough functionality for the scene. Can be toggled at runtime. Passthrough Capability must be enabled in the project settings.");
#else
        GUIContent enablePassthroughContent = new GUIContent("Enable Passthrough",
            "Enables passthrough functionality for the scene. Can be toggled at runtime.");
#endif
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Insight Passthrough & Guardian Boundary", EditorStyles.boldLabel);
        EditorGUI.indentLevel++; // PT section
#if UNITY_ANDROID
        if (!passthroughCapabilityEnabled)
        {
            EditorGUILayout.LabelField(
                "Requires Passthrough Capability to be enabled in the General section of the Quest features.",
                EditorStyles.wordWrappedLabel);
        }
#endif
        OVREditorUtil.SetupBoolField(target, enablePassthroughContent, ref manager.isInsightPassthroughEnabled,
            ref modified);
#if UNITY_ANDROID
        EditorGUI.EndDisabledGroup();
#endif

        var boundaryCapabilityEnabled =
            projectConfig.boundaryVisibilitySupport != OVRProjectConfig.FeatureSupport.None;

        var canModifyBoundary = boundaryCapabilityEnabled && manager.isInsightPassthroughEnabled;
        if (!canModifyBoundary)
            manager.shouldBoundaryVisibilityBeSuppressed = false;

        using (new EditorGUI.DisabledScope(!canModifyBoundary))
        {
            var content = new GUIContent("Should Boundary Visibility Be Suppressed",
                "Request that the Guardian Boundary Visibility be suppressed. " +
                "Can only be suppressed when Passthrough is enabled, and can " +
                "therefore differ from the system boundary state. There can " +
                "be a delay when setting this due to the delay in Passthrough startup. " +
                "Boundary Visibility Capability must be enabled in the project settings.");
            OVREditorUtil.SetupBoolField(target, content, ref manager.shouldBoundaryVisibilityBeSuppressed, ref modified);
        }

        // actual boundary state (readonly)
        using (new EditorGUI.DisabledScope(true))
        {
            var isBoundaryVisibilitySuppressed = manager.isBoundaryVisibilitySuppressed;
            OVREditorUtil.SetupBoolField(target, new GUIContent("Is Boundary Visibility Suppressed",
                "The system state of the Guardian Boundary Visibility which may differ " +
                "from the requested state."),
                ref isBoundaryVisibilitySuppressed, ref modified);
        }
        EditorGUI.indentLevel--; // PT section

        // Common Hand Tracking section header
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Hand Tracking", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;

        // Hand Tracking Settings
        EditorGUI.BeginDisabledGroup(projectConfig.handTrackingSupport == OVRProjectConfig.HandTrackingSupport.ControllersOnly);
        OVRHandSkeletonVersion handSkeletonVersion = runtimeSettings.HandSkeletonVersion;
        OVREditorUtil.SetupEnumField(target, new GUIContent("Hand Skeleton Version",
                "The version of the hand skeleton"), ref handSkeletonVersion, ref modified);

        if (modified)
        {
            runtimeSettings.HandSkeletonVersion = handSkeletonVersion;
            OVRRuntimeSettings.CommitRuntimeSettings(runtimeSettings);
        }
        EditorGUI.EndDisabledGroup();

        // Common Hand Tracking section footer
        EditorGUI.indentLevel--;

        // Common Movement Tracking section header
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Movement Tracking", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;

        // Body Tracking settings
        EditorGUI.BeginDisabledGroup(projectConfig.bodyTrackingSupport == OVRProjectConfig.FeatureSupport.None);
        OVRPlugin.BodyTrackingFidelity2 bodyTrackingFidelity = runtimeSettings.BodyTrackingFidelity;
        OVREditorUtil.SetupEnumField(target, new GUIContent("Body Tracking Fidelity",
                "Body Tracking Fidelity defines quality of tracking"), ref bodyTrackingFidelity, ref modified,
            "https://developer.oculus.com/documentation/unity/move-body-tracking/");

        if (modified)
        {
            runtimeSettings.BodyTrackingFidelity = bodyTrackingFidelity;
            OVRRuntimeSettings.CommitRuntimeSettings(runtimeSettings);
        }

        OVRPlugin.BodyJointSet bodyTrackingJointSet = runtimeSettings.BodyTrackingJointSet;
        OVREditorUtil.SetupEnumField(target, new GUIContent("Body Tracking Joint Set",
                "Body Tracking Joint Set"), ref bodyTrackingJointSet, ref modified,
            "https://developer.oculus.com/documentation/unity/move-body-tracking/");
        if (modified)
        {
            runtimeSettings.BodyTrackingJointSet = bodyTrackingJointSet;
            OVRRuntimeSettings.CommitRuntimeSettings(runtimeSettings);
        }
        EditorGUI.EndDisabledGroup();

        // Face Tracking settings
        EditorGUI.BeginDisabledGroup(projectConfig.faceTrackingSupport == OVRProjectConfig.FeatureSupport.None);

        _showFaceTrackingDataSources = EditorGUILayout.Foldout(
            _showFaceTrackingDataSources,
            new GUIContent("Face Tracking Data Sources", "Specifies the face tracking data sources accepted by the application.\n\n" +
                "Requires Face Tracking Support to be \"Required\" or \"Supported\" in the General section of Quest Features."));

        bool visualFaceTracking = runtimeSettings.RequestsVisualFaceTracking;
        bool audioFaceTracking = runtimeSettings.RequestsAudioFaceTracking;
        bool dataSourceSelected = visualFaceTracking || audioFaceTracking;
        // Show the warning under the foldout, even if the foldout is collapsed.
        if (projectConfig.faceTrackingSupport != OVRProjectConfig.FeatureSupport.None && !dataSourceSelected)
        {
            EditorGUILayout.HelpBox("Please specify at least one face tracking data source. If you don't choose, all available data sources will be chosen.", MessageType.Warning, true);
        }

        // Warn about face tracking when simultaneous hands and controllers is enabled as an incompatibility exists
        // for Quest 2 headsets
        if (projectConfig.faceTrackingSupport != OVRProjectConfig.FeatureSupport.None && manager.SimultaneousHandsAndControllersEnabled == true) {
            EditorGUILayout.HelpBox("For Quest 2, simultaneous hands and controllers are not supported together with face tracking. Please select only one of these features if shipping on Quest 2", MessageType.Warning, true);
        }

        // Also warn about Body API when simultaneous hands and controllers is enabled
        bool bodyEnabled =
#pragma warning disable CS0618 // Type or member is obsolete
            (GameObject.FindObjectOfType<OVRBody>() != null || manager.wideMotionModeHandPosesEnabled == true);
#pragma warning restore CS0618 // Type or member is obsolete
        if (bodyEnabled && manager.SimultaneousHandsAndControllersEnabled)
        {
            EditorGUILayout.HelpBox("Simultaneous hands and controllers are not supported together with Body API. Please select only one of these features", MessageType.Warning, true);
        }

        if (_showFaceTrackingDataSources)
        {
            EditorGUI.indentLevel++;
            OVREditorUtil.SetupBoolField(
                target,
                new GUIContent("Visual", "Estimate face expressions with visual or audiovisual data."),
                ref visualFaceTracking,
                ref modified);
            OVREditorUtil.SetupBoolField(
                target,
                new GUIContent("Audio", "Estimate face expressions using audio data only."),
                ref audioFaceTracking,
                ref modified);
            EditorGUI.indentLevel--;
        }

        bool enableFaceTrackingVisemesOutput = runtimeSettings.EnableFaceTrackingVisemesOutput;
        OVREditorUtil.SetupBoolField(
            target,
            new GUIContent("Enable visemes", "Enabling visemes allows you to get face expressions in the form of Visemes in addition to blendshape weights. If it's not enabled, visemes will not be available."),
            ref enableFaceTrackingVisemesOutput,
            ref modified);

        if (modified)
        {
            runtimeSettings.RequestsVisualFaceTracking = visualFaceTracking;
            runtimeSettings.RequestsAudioFaceTracking = audioFaceTracking;
            runtimeSettings.EnableFaceTrackingVisemesOutput = enableFaceTrackingVisemesOutput;
            OVRRuntimeSettings.CommitRuntimeSettings(runtimeSettings);
        }
        EditorGUI.EndDisabledGroup();

        // Common Movement Tracking section footer
        EditorGUI.indentLevel--;
#endif

        #region DynamicResolution
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Dynamic Resolution", EditorStyles.boldLabel);
        bool enableDynamicResolution = manager.enableDynamicResolution;
        OVREditorUtil.SetupBoolField(target, "Enable Dynamic Resolution", ref enableDynamicResolution, ref modified, "https://developers.meta.com/horizon/documentation/unity/dynamic-resolution-unity");
        manager.enableDynamicResolution = enableDynamicResolution;
        if (manager.enableDynamicResolution)
        {
            OVREditorUtil.SetupRangeSlider(target, new GUIContent("Quest 2/Pro Range", "Quest2/Pro resolution scaling factor range when dynamic resolution is enabled."), ref manager.quest2MinDynamicResolutionScale, ref manager.quest2MaxDynamicResolutionScale, 0.7f, 2.0f, ref modified);
            OVREditorUtil.SetupRangeSlider(target, new GUIContent("Quest 3/3S Range", "Quest3/3S Resolution scaling factor range when dynamic resolution is enabled."), ref manager.quest3MinDynamicResolutionScale, ref manager.quest3MaxDynamicResolutionScale, 0.7f, 2.0f, ref modified);
        }
        #endregion

        #region PermissionRequests

        EditorGUILayout.Space();
        _expandPermissionsRequest =
            EditorGUILayout.BeginFoldoutHeaderGroup(_expandPermissionsRequest, "Permission Requests On Startup");
        if (_expandPermissionsRequest)
        {
            // helper function for all permission requests
            static void AddPermissionGroup(bool featureEnabled, string permissionName, string capabilityName, SerializedProperty property)
            {
                using (new EditorGUI.DisabledScope(!featureEnabled))
                {
                    if (!featureEnabled)
                    {
                        EditorGUILayout.LabelField(
                            $"Requires {capabilityName} Capability to be enabled in Quest Features.",
                            EditorStyles.wordWrappedLabel);

                        // disable if the Quest Features doesn't have support for it
                        if (property.boolValue == true)
                            property.boolValue = false;
                    }

                    var label = new GUIContent(permissionName,
                        $"Requests {permissionName} permission on start up. " +
                        $"{capabilityName} Capability must be enabled in Quest Features. " +
                        "It is recommended to manage runtime permissions yourself, " +
                        "and to only request them when needed."
                        );
                    EditorGUILayout.PropertyField(property, label);
                }
            }

            AddPermissionGroup(projectConfig.bodyTrackingSupport != OVRProjectConfig.FeatureSupport.None,
                "Body Tracking", "Body Tracking", _requestBodyTrackingPermissionOnStartup);
            AddPermissionGroup(projectConfig.eyeTrackingSupport != OVRProjectConfig.FeatureSupport.None,
                "Eye Tracking", "Eye Tracking", _requestEyeTrackingPermissionOnStartup);
            AddPermissionGroup(projectConfig.faceTrackingSupport != OVRProjectConfig.FeatureSupport.None,
                "Face Tracking", "Face Tracking", _requestFaceTrackingPermissionOnStartup);
            AddPermissionGroup(projectConfig.faceTrackingSupport != OVRProjectConfig.FeatureSupport.None,
                "Record Audio for audio based Face Tracking", "Face Tracking", _requestRecordAudioPermissionOnStartup);
            AddPermissionGroup(projectConfig.sceneSupport != OVRProjectConfig.FeatureSupport.None,
                "Scene", "Scene", _requestScenePermissionOnStartup);
        }

        EditorGUILayout.EndFoldoutHeaderGroup();

        #endregion


        if (modified)
        {
            EditorUtility.SetDirty(target);
        }

        serializedObject.ApplyModifiedProperties();

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_ANDROID
#if !USING_XR_SDK_OPENXR && (!OCULUS_XR_3_3_0_OR_NEWER || !UNITY_2021_1_OR_NEWER)
        if (manager.enableDynamicResolution && !PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android))
        {
            UnityEngine.Rendering.GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            if (apis.Length >= 1 && apis[0] == UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
            {
                Debug.LogError("Vulkan Dynamic Resolution is not supported on your current build version. Ensure you are on Unity 2021+ with Oculus XR plugin v3.3.0+");
                manager.enableDynamicResolution = false;
            }
        }
#endif
#endif
    }
}
