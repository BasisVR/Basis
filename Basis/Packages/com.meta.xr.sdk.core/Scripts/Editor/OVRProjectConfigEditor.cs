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
using Oculus.VR.Editor;
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(OVRProjectConfig))]
public class OVRProjectConfigEditor : Editor
{
    override public void OnInspectorGUI()
    {
        OVRProjectConfig projectConfig = (OVRProjectConfig)target;
        DrawTargetDeviceInspector(projectConfig);
        EditorGUILayout.Space();
        DrawProjectConfigInspector(projectConfig);
    }

    public static void DrawTargetDeviceInspector(OVRProjectConfig projectConfig)
    {
        // Target Devices
        EditorGUILayout.LabelField("Target Devices", EditorStyles.boldLabel);
        bool useOculusXRSettings = false;

#if PRIORITIZE_OCULUS_XR_SETTINGS
        EditorGUILayout.LabelField("Configure Target Devices in Oculus XR Plugin Settings:", GUILayout.Width(320));
        if (GUILayout.Button("Open Settings"))
            SettingsService.OpenProjectSettings("Project/XR Plug-in Management/Oculus");
#if !USING_QUEST_PRO_COMPATIBLE_OCULUS_XR_PLUGIN_VERSION
        ++EditorGUI.indentLevel;
        EditorGUILayout.LabelField("Note: The currently installed Oculus XR Plugin version does not support "
               + "configuring Quest Pro as a target device. Please install a compatible version:"
               + "\n2.2.0-preview.1 or newer\n3.2.1 or newer", EditorStyles.wordWrappedMiniLabel);
        --EditorGUI.indentLevel;
#endif
        useOculusXRSettings = true;
#endif

        if (!useOculusXRSettings)
        {
            bool hasModified = false;

            foreach (OVRProjectConfig.DeviceType deviceType in System.Enum.GetValues(
                         typeof(OVRProjectConfig.DeviceType)))
            {
                bool oldSupportsDevice = projectConfig.targetDeviceTypes.Contains(deviceType);
                bool newSupportsDevice = oldSupportsDevice;
                if (deviceType == OVRProjectConfig.DeviceType.Quest)
                {
                    continue;
                }

                OVREditorUtil.SetupBoolField(projectConfig, ObjectNames.NicifyVariableName(deviceType.ToString()),
                    ref newSupportsDevice, ref hasModified);

                if (newSupportsDevice && !oldSupportsDevice)
                {
                    projectConfig.targetDeviceTypes.Add(deviceType);
                }
                else if (oldSupportsDevice && !newSupportsDevice)
                {
                    projectConfig.targetDeviceTypes.Remove(deviceType);
                }
            }

            if (hasModified)
            {
                OVRProjectConfig.CommitProjectConfig(projectConfig);
            }
        }
    }

    internal enum eProjectConfigTab
    {
        General = 0,
        BuildSettings,
        Security,
        Experimental,
    }

    internal static eProjectConfigTab selectedTab = 0;
    static string[] projectConfigTabStrs = null;

    public static void DrawProjectConfigInspector(OVRProjectConfig projectConfig)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Quest Features", EditorStyles.boldLabel);

        if (EditorUserBuildSettings.activeBuildTarget != UnityEditor.BuildTarget.Android)
        {
            EditorGUILayout.LabelField(
                $"Your current platform is \"{EditorUserBuildSettings.activeBuildTarget}\". These settings only apply if your active platform is \"Android\".",
                EditorStyles.wordWrappedMiniLabel);
        }

        if (projectConfigTabStrs == null)
        {
            projectConfigTabStrs = Enum.GetNames(typeof(eProjectConfigTab));
            for (int i = 0; i < projectConfigTabStrs.Length; ++i)
                projectConfigTabStrs[i] = ObjectNames.NicifyVariableName(projectConfigTabStrs[i]);
        }

        selectedTab =
            (eProjectConfigTab)GUILayout.SelectionGrid((int)selectedTab, projectConfigTabStrs, 3, GUI.skin.button);
        EditorGUILayout.Space(5);
        bool hasModified = false;

        switch (selectedTab)
        {
            case eProjectConfigTab.General:

                // Show overlay support option
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Toggle(new GUIContent("Focus Aware (Required)",
                            "If checked, the new overlay will be displayed when the user presses the home button. The game will not be paused, but will now receive InputFocusLost and InputFocusAcquired events."),
                        true);
                }

                // Hand Tracking Support
                OVREditorUtil.SetupEnumField(projectConfig, "Hand Tracking Support",
                    ref projectConfig.handTrackingSupport, ref hasModified);

                OVREditorUtil.SetupEnumField(projectConfig, new GUIContent("Hand Tracking Frequency",
                        "Note that a higher tracking frequency will reserve some performance headroom from the application's budget."),
                    ref projectConfig.handTrackingFrequency, ref hasModified,
                    "https://developer.oculus.com/documentation/unity/unity-handtracking/#enable-hand-tracking");

                OVREditorUtil.SetupEnumField(projectConfig, "Hand Tracking Version",
                    ref projectConfig.handTrackingVersion, ref hasModified);

                // Enable Render Model Support
                bool renderModelSupportAvailable = OVRPluginInfo.IsOVRPluginOpenXRActivated();
                EditorGUI.BeginDisabledGroup(!renderModelSupportAvailable);
                if (!renderModelSupportAvailable)
                {
                    projectConfig.renderModelSupport = OVRProjectConfig.RenderModelSupport.Disabled;
                }

                OVREditorUtil.SetupEnumField(projectConfig, new GUIContent("Render Model Support",
                        "If enabled, the application will be able to load render models from the runtime."),
                    ref projectConfig.renderModelSupport, ref hasModified);
                if (hasModified && projectConfig.renderModelSupport == OVRProjectConfig.RenderModelSupport.Disabled)
                {
                    if (projectConfig.trackedKeyboardSupport != OVRProjectConfig.TrackedKeyboardSupport.None)
                    {
                        Debug.LogWarning("Tracked Keyboard support disabled. Requires Render Model Support");
                        projectConfig.trackedKeyboardSupport = OVRProjectConfig.TrackedKeyboardSupport.None;
                    }

                    if (projectConfig.virtualKeyboardSupport != OVRProjectConfig.FeatureSupport.None)
                    {
                        Debug.LogWarning("Virtual Keyboard support disabled. Requires Render Model Support");
                        projectConfig.virtualKeyboardSupport = OVRProjectConfig.FeatureSupport.None;
                    }
                }

                EditorGUI.EndDisabledGroup();

                // System Keyboard Support
                OVREditorUtil.SetupBoolField(projectConfig, new GUIContent("Requires System Keyboard",
                        "If checked, the Oculus System keyboard will be enabled for Unity input fields and any calls to open/close the Unity TouchScreenKeyboard."),
                    ref projectConfig.requiresSystemKeyboard, ref hasModified);

                // Tracked Keyboard Support
                bool trackedKeyboardSupportAvailable = OVRPluginInfo.IsOVRPluginOpenXRActivated();
                using (new EditorGUI.DisabledGroupScope(!trackedKeyboardSupportAvailable))
                {
                    if (!trackedKeyboardSupportAvailable)
                    {
                        projectConfig.trackedKeyboardSupport = OVRProjectConfig.TrackedKeyboardSupport.None;
                    }

                    OVREditorUtil.SetupEnumField(projectConfig, new GUIContent("Tracked Keyboard Support",
                            "Show user's physical keyboard in correct position in VR."),
                        ref projectConfig.trackedKeyboardSupport, ref hasModified);
                }

                // Virtual Keyboard Support
                bool virtualKeyboardSupportAvailable = OVRPluginInfo.IsOVRPluginOpenXRActivated();
                using (new EditorGUI.DisabledGroupScope(!virtualKeyboardSupportAvailable))
                {
                    if (!virtualKeyboardSupportAvailable)
                    {
                        projectConfig.virtualKeyboardSupport = OVRProjectConfig.FeatureSupport.None;
                    }

                    OVREditorUtil.SetupEnumField(projectConfig, new GUIContent("Virtual Keyboard Support",
                            "Provides a consistent typing experience across Meta Quest VR applications."),
                        ref projectConfig.virtualKeyboardSupport, ref hasModified);

                    if (projectConfig.requiresSystemKeyboard
                        && projectConfig.virtualKeyboardSupport != OVRProjectConfig.FeatureSupport.None)
                    {
                        EditorGUILayout.HelpBox(
                            "Using the System Keyboard with Virtual Keyboard is not recommended.",
                            MessageType.Warning);
                    }
                }

                // Anchor Support - linked to Shared Spatial Anchors and Scene
                var anchorSupportRequired = projectConfig.sharedAnchorSupport != OVRProjectConfig.FeatureSupport.None;
                var anchorSupportTooltip = "Anchor Support is required for Anchor And Space Sharing Support.";
                anchorSupportRequired = anchorSupportRequired ||
                                        projectConfig.sceneSupport != OVRProjectConfig.FeatureSupport.None;
                anchorSupportTooltip =
                    "Anchor Support is required for Anchor And Sharing Support and/or Scene Support.";
                using (new EditorGUI.DisabledScope(anchorSupportRequired))
                {
                    var tooltip = anchorSupportRequired ? anchorSupportTooltip : "";
                    OVREditorUtil.SetupEnumField(projectConfig, new GUIContent("Anchor Support", tooltip),
                        ref projectConfig.anchorSupport, ref hasModified);
                }

                OVREditorUtil.SetupEnumField(projectConfig,
                    new GUIContent("Shared Spatial Anchor Support",
                        "Enables support for sharing spatial anchors with other users. This requires Anchor Support to be enabled."),
                    ref projectConfig.sharedAnchorSupport, ref hasModified);

                if (projectConfig.sharedAnchorSupport != OVRProjectConfig.FeatureSupport.None &&
                    projectConfig.anchorSupport != OVRProjectConfig.AnchorSupport.Enabled)
                {
                    projectConfig.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
                    hasModified = true;
                }

                OVREditorUtil.SetupEnumField(projectConfig, "Colocation Session Support",
                    ref projectConfig.colocationSessionSupport, ref hasModified);


                // Scene Support
                var sceneTooltip =
                    "Enable support for scene understanding. This requires Anchor Support to be enabled.";
                OVREditorUtil.SetupEnumField(projectConfig, new GUIContent("Scene Support", sceneTooltip),
                    ref projectConfig.sceneSupport, ref hasModified);
                // enable anchor support if scene requires it
                if (projectConfig.sceneSupport != OVRProjectConfig.FeatureSupport.None &&
                    projectConfig.anchorSupport != OVRProjectConfig.AnchorSupport.Enabled)
                {
                    projectConfig.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
                    hasModified = true;
                }


                // Passthrough support
                OVREditorUtil.SetupEnumField(projectConfig, new GUIContent("Passthrough Support",
                        "Allows the application to use passthrough functionality. This option must be enabled at build time, otherwise initializing passthrough and creating passthrough layers in application scenes will fail."),
                    ref projectConfig._insightPassthroughSupport, ref hasModified);
                if (hasModified && projectConfig._insightPassthroughSupport != OVRProjectConfig.FeatureSupport.None)
                {
                    // Enable contextual passthrough for MR apps
                    projectConfig._systemLoadingScreenBackground =
                        OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
                }

                // Boundary Visibility Support
                var boundaryVisibilityTooltip =
                    "Enable support for modifying the Guardian boundary visibility. Requires Passthrough Support.";
                OVREditorUtil.SetupEnumField(projectConfig,
                    new GUIContent("Boundary Visibility Support", boundaryVisibilityTooltip),
                    ref projectConfig.boundaryVisibilitySupport, ref hasModified);

                // enable passthrough support if boundary visibility support is enabled
                if (projectConfig.boundaryVisibilitySupport != OVRProjectConfig.FeatureSupport.None &&
                    projectConfig._insightPassthroughSupport == OVRProjectConfig.FeatureSupport.None)
                {
                    projectConfig._insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
                    hasModified = true;
                }

                // Body Tracking Support
                OVREditorUtil.SetupEnumField(projectConfig, "Body Tracking Support",
                    ref projectConfig.bodyTrackingSupport, ref hasModified);

                // Face Tracking Support
                OVREditorUtil.SetupEnumField(projectConfig, "Face Tracking Support",
                    ref projectConfig.faceTrackingSupport, ref hasModified);

                // Eye Tracking Support
                OVREditorUtil.SetupEnumField(projectConfig, "Eye Tracking Support",
                    ref projectConfig.eyeTrackingSupport, ref hasModified);

                if (hasModified && projectConfig.trackedKeyboardSupport != OVRProjectConfig.TrackedKeyboardSupport.None)
                {
                    projectConfig.renderModelSupport = OVRProjectConfig.RenderModelSupport.Enabled;
                }

                if (hasModified && projectConfig.virtualKeyboardSupport != OVRProjectConfig.FeatureSupport.None)
                {
                    projectConfig.renderModelSupport = OVRProjectConfig.RenderModelSupport.Enabled;
                }

                if (!OVRPluginInfo.IsOVRPluginOpenXRActivated())
                {
                    EditorGUILayout.HelpBox(
                        "The OpenXR backend must be enabled in the Oculus menu to use the Render Model and Tracked Keyboard features.",
                        MessageType.Info);
                }

                if (projectConfig.trackedKeyboardSupport != OVRProjectConfig.TrackedKeyboardSupport.None &&
                    projectConfig.renderModelSupport == OVRProjectConfig.RenderModelSupport.Disabled)
                {
                    EditorGUILayout.HelpBox(
                        "Render model support is required to load keyboard models from the runtime.",
                        MessageType.Error);
                }

                if (projectConfig.virtualKeyboardSupport != OVRProjectConfig.FeatureSupport.None &&
                    projectConfig.renderModelSupport == OVRProjectConfig.RenderModelSupport.Disabled)
                {
                    EditorGUILayout.HelpBox(
                        "Render model support is required to load virtual keyboard models from the runtime.",
                        MessageType.Error);
                }

                // System Splash Screen
                bool splashScreenTextureModified = false;
                OVREditorUtil.SetupTexture2DField(projectConfig, new GUIContent("System Splash Screen",
                        "If set, the Splash Screen will be presented by the Operating System as a high quality composition layer at launch time."),
                    ref projectConfig.systemSplashScreen, ref splashScreenTextureModified,
                    "https://developer.oculus.com/documentation/unity/unity-splash-screen/");

                if (splashScreenTextureModified)
                {
                    Texture2D newSystemSplashScreen =
                        OVRSystemSplashScreenEditor.ProcessTexture(
                            projectConfig.systemSplashScreen);

                    if (newSystemSplashScreen != null &&
                        !projectConfig.systemSplashScreen.Equals(newSystemSplashScreen))
                    {
                        projectConfig.systemSplashScreen = newSystemSplashScreen;
                        hasModified = true;
                    }
                }

                // System Splash Screen: "Mono", "Stereo"
                OVREditorUtil.SetupEnumField(
                    projectConfig,
                    new GUIContent("System Splash Screen Type", "\"Mono\": Texture will be rendered to both eyes.\n\"Stereo\": Texture will be split and rendered to each eye."),
                    ref projectConfig.systemSplashScreenType,
                    ref hasModified
                );

                if (projectConfig.systemSplashScreenType == OVRProjectConfig.SystemSplashScreenType.Stereo)
                {
                    EditorGUILayout.HelpBox(
                        "For stereoscopic splash screen, the image needs to be double-wide with left-to-right texture pair.",
                        MessageType.Info);
                }

                // System Splash Screen Background: Black vs ContextualPassthrough
                bool systemSplashScreenBackgroundCustomizable =
                    projectConfig.insightPassthroughSupport == OVRProjectConfig.FeatureSupport.None;
                using (new EditorGUI.DisabledGroupScope(!systemSplashScreenBackgroundCustomizable))
                {
                    OVREditorUtil.SetupEnumField(
                        projectConfig,
                        new GUIContent(
                            text: "System Splash Screen Background",
                            tooltip: "Background shown by the Operating System during system splash screens:\n" +
                                     "  \"Black\" - always black.\n" +
                                     "  \"Passthrough (Contextual)\" - Passthrough, if the user has passthrough active in home. Black otherwise." +
                                     (!systemSplashScreenBackgroundCustomizable ? "\n\nReadonly for current Passthrough Support value." : "")),
                        ref projectConfig._systemLoadingScreenBackground,
                        ref hasModified
                    );
                }

                if (projectConfig.systemLoadingScreenBackground == OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough)
                {
                    if (PlayerSettings.virtualRealitySplashScreen != null)
                    {
                        EditorGUILayout.HelpBox(
                            "Virtual Reality Splash Screen (in Player Settings) is active, this will result in an inconsistent experience.",
                            MessageType.Info);
                    }
                    else if (PlayerSettings.SplashScreen.show)
                    {
                        EditorGUILayout.HelpBox(
                            "Show Splash Screen (in Player Settings) is on, this will result in an inconsistent experience.",
                            MessageType.Info);
                    }
                }

                // Allow optional 3-dof head-tracking
                OVREditorUtil.SetupBoolField(projectConfig, new GUIContent("Allow Optional 3DoF Head Tracking",
                        "If checked, application can work in both 6DoF and 3DoF modes. It's highly recommended to keep it unchecked unless your project strongly needs the 3DoF head tracking."),
                    ref projectConfig.allowOptional3DofHeadTracking, ref hasModified);

                // Processor favor (cpu/gpu level trading)
                OVREditorUtil.SetupEnumField(projectConfig, new GUIContent("Processor Favor",
                        "If selected, will increase the frequency of one processor at the expense of decreasing the frequency of the other on supported devices"),
                    ref projectConfig._processorFavor, ref hasModified);

                break;

            case eProjectConfigTab.BuildSettings:

                var usingSRP = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null;
                if (usingSRP && projectConfig.skipUnneededShaders)
                {
                    projectConfig.skipUnneededShaders = false;
                }

                using (new EditorGUI.DisabledScope(usingSRP))
                {
                    OVREditorUtil.SetupBoolField(projectConfig, new GUIContent("Skip Unneeded Shaders",
                        "If checked, prevent building shaders (BiRP only) that are not used by default to reduce time spent when building."),
                        ref projectConfig.skipUnneededShaders, ref hasModified,
                        "https://developer.oculus.com/documentation/unity/unity-strip-shaders/");
                }

                OVREditorUtil.SetupBoolField(projectConfig, new GUIContent("IL2CPP Link Time Optimization",
                        "If checked, IL2CPP will compile code with link time optimization in release build."),
                    ref projectConfig.enableIL2CPPLTO, ref hasModified,
                    "https://clang.llvm.org/docs/ThinLTO.html");

                EditorGUILayout.LabelField("Android Manifest Settings", EditorStyles.boldLabel);
                OVREditorUtil.SetupBoolField(projectConfig, new GUIContent("Remove Existing Gradle AndroidManifest per Build",
                    "If checked, this will always delete the AndroidManifest file in the gradle project before a build which is known to prevent the AndroidManfiest from updating properly."),
                    ref projectConfig.removeGradleManifest, ref hasModified);
                OVREditorUtil.SetupBoolField(projectConfig, new GUIContent("Horizon OS SDK Tag", "If checked, the application can specify the minimum required Horizon OS SDK version needed to run and the target version the application was created for."),
                    ref projectConfig.horizonOsSdkEnabled, ref hasModified);
                if (projectConfig.horizonOsSdkEnabled)
                {
                    var sdkLabels = new GUIContent[OVRProjectConfig.horizonOsSdkVersions.Length];
                    for (var i = 0; i < OVRProjectConfig.horizonOsSdkVersions.Length; i++)
                        sdkLabels[i] = new GUIContent(OVRProjectConfig.horizonOsSdkVersions[i].ToString());
                    var minSdkIndex = Array.IndexOf(OVRProjectConfig.horizonOsSdkVersions, projectConfig.minHorizonOsSdkVersion);
                    var targetSdkIndex = Array.IndexOf(OVRProjectConfig.horizonOsSdkVersions, projectConfig.targetHorizonOsSdkVersion);

                    OVREditorUtil.SetupPopupField(projectConfig, "Minimum SDK Version", ref minSdkIndex, sdkLabels, ref hasModified);
                    OVREditorUtil.SetupPopupField(projectConfig, "Target SDK Version", ref targetSdkIndex, sdkLabels, ref hasModified);

                    projectConfig.minHorizonOsSdkVersion = OVRProjectConfig.horizonOsSdkVersions[minSdkIndex];
                    projectConfig.targetHorizonOsSdkVersion = OVRProjectConfig.horizonOsSdkVersions[targetSdkIndex];
                }
                break;

            case eProjectConfigTab.Security:

                OVREditorUtil.SetupBoolField(projectConfig, "Disable Backups", ref projectConfig.disableBackups,
                    ref hasModified,
                    "https://developer.android.com/guide/topics/data/autobackup#EnablingAutoBackup");
                OVREditorUtil.SetupBoolField(projectConfig, "Enable NSC Configuration",
                    ref projectConfig.enableNSCConfig, ref hasModified,
                    "https://developer.android.com/training/articles/security-config");
                EditorGUI.BeginDisabledGroup(!projectConfig.enableNSCConfig);
                ++EditorGUI.indentLevel;
                OVREditorUtil.SetupInputField(projectConfig, "Custom Security XML Path",
                    ref projectConfig.securityXmlPath, ref hasModified);
                --EditorGUI.indentLevel;
                EditorGUI.EndDisabledGroup();

                break;

            case eProjectConfigTab.Experimental:

                // Experimental Features Enabled
                OVREditorUtil.SetupBoolField(projectConfig, new GUIContent("Experimental Features Enabled",
                        "If checked, this application can use experimental features. Note that such features are for developer use only. This option must be disabled when submitting to the Oculus Store."),
                    ref projectConfig.experimentalFeaturesEnabled, ref hasModified);

                break;
        }

        EditorGUILayout.EndVertical();

        // apply any pending changes to project config
        if (hasModified)
        {
            OVRProjectConfig.CommitProjectConfig(projectConfig);
        }
    }
}
