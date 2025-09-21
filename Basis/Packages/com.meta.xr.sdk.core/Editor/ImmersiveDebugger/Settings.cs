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
using Meta.XR.Editor.Id;
using Meta.XR.Editor.ToolingSupport;
using UnityEditor;
using UnityEngine;
using Meta.XR.Editor.Settings;
using static Meta.XR.Editor.UserInterface.Styles;
using static Meta.XR.Editor.UserInterface.Utils;

namespace Meta.XR.ImmersiveDebugger.Editor
{
    internal static class Settings
    {
        private const string PanelLayerName = "Panel Layer";
        private const string MeshLayerName = "Mesh Layer";

        private static readonly GUIContent LayersDescription = new GUIContent(
            $"<b>{Utils.ToolDescriptor.Name}</b> requires two layers :" +
            $"\n- <b>{PanelLayerName}</b> : Used to handle and renders the canvas into a Render Target." +
            $"\n- <b>{MeshLayerName}</b> : Used to render the mesh used for the overlay, will be passed to the Overlay Renderer." +
            "\nAt runtime in headset, those layers are only used as temporary rendering layers before <b>OVROverlayCanvas</b> renders the interface. Both layers should be culled by the cameras, because the rendering will occur on an overlay pass." +
            "\nAt runtime in editor, both layers need to be rendered by the main camera since there won't be any overlay pass." +
            "\n\nWe recommend to assign two layers that are not used by the project itself, and set <i>Automatically Update Culling Mask</i> on");

        private static readonly Setting Enabled = new CustomBool()
        {
            Uid = nameof(Enabled),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ImmersiveDebuggerEnabled,
            Set = (val) =>
            {
                if (val)
                {
                    UsageSettings.UsesImmersiveDebugger.SetValue(true);
                }
                RuntimeSettings.Instance.ImmersiveDebuggerEnabled = val;
            },
            Label = "Enable",
            Tooltip = "Enable Immersive Debugger panels in runtime and allow collecting scripting attributes in Editor.",
            SendTelemetry = true
        };

        private static readonly Setting DisplayAtStartup = new CustomBool()
        {
            Uid = nameof(DisplayAtStartup),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ImmersiveDebuggerDisplayAtStartup,
            Set = (val) => RuntimeSettings.Instance.ImmersiveDebuggerDisplayAtStartup = val,
            Label = "Display at Start-up",
            Tooltip = "Display Immersive Debugger panel when the application starts.",
            SendTelemetry = true
        };

        private static readonly Setting EnableOnlyInDebugBuild = new CustomBool()
        {
            Uid = nameof(EnableOnlyInDebugBuild),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.EnableOnlyInDebugBuild,
            Set = (val) => RuntimeSettings.Instance.EnableOnlyInDebugBuild = val,
            Label = "Enable Only In Debug Build",
            Tooltip = "On top of enabling Immersive Debugger tool, this option allows to enable it only in " +
                      "Debug Build so the tool doesn't get shown in production build.",
            SendTelemetry = true
        };

        private static readonly Setting FollowOverride = new CustomBool()
        {
            Uid = nameof(FollowOverride),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.FollowOverride,
            Set = (val) => RuntimeSettings.Instance.FollowOverride = val,
            Label = "Follow Camera Rig Translation",
            Tooltip = "Whether or not the Immersive Debugger panel follows the player by default."
        };

        private static readonly Setting RotateOverride = new CustomBool()
        {
            Uid = nameof(RotateOverride),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.RotateOverride,
            Set = (val) => RuntimeSettings.Instance.RotateOverride = val,
            Label = "Follow Camera Rig Rotation",
            Tooltip = "Whether or not the Immersive Debugger panel rotates with the player by default."
        };

        private static readonly Setting ShowInspectors = new CustomBool()
        {
            Uid = nameof(ShowInspectors),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ShowInspectors,
            Set = (val) => RuntimeSettings.Instance.ShowInspectors = val,
            Label = "Show Inspectors",
            Tooltip = "Display Inspectors panel by default.",
            SendTelemetry = true
        };

        private static readonly Setting ShowConsole = new CustomBool()
        {
            Uid = nameof(ShowConsole),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ShowConsole,
            Set = (val) => RuntimeSettings.Instance.ShowConsole = val,
            Label = "Show Console",
            Tooltip = "Display Console by default.",
            SendTelemetry = true
        };

        private static readonly Setting ShowInfoLog = new CustomBool()
        {
            Uid = nameof(ShowInfoLog),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ShowInfoLog,
            Set = (val) => RuntimeSettings.Instance.ShowInfoLog = val,
            Label = "Show Info Logs in Console",
            Tooltip = "Show Info Logs in Console by default."
        };

        private static readonly Setting ShowWarningLog = new CustomBool()
        {
            Uid = nameof(ShowWarningLog),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ShowWarningLog,
            Set = (val) => RuntimeSettings.Instance.ShowWarningLog = val,
            Label = "Show Error Logs in Console",
            Tooltip = "Show Warning Logs in Console by default."
        };

        private static readonly Setting ShowErrorLog = new CustomBool()
        {
            Uid = nameof(ShowErrorLog),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ShowErrorLog,
            Set = (val) => RuntimeSettings.Instance.ShowErrorLog = val,
            Label = "Show Warning Logs in Console",
            Tooltip = "Show Error Logs in Console by default."
        };

        private static readonly Setting CollapsedIdenticalLogEntries = new CustomBool()
        {
            Uid = nameof(CollapsedIdenticalLogEntries),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.CollapsedIdenticalLogEntries,
            Set = (val) => RuntimeSettings.Instance.CollapsedIdenticalLogEntries = val,
            Label = "Collapse Identical Log Entries"
        };

        private static readonly Setting AutomaticLayerCullingUpdate = new CustomBool()
        {
            Uid = nameof(AutomaticLayerCullingUpdate),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.AutomaticLayerCullingUpdate,
            Set = (val) => RuntimeSettings.Instance.AutomaticLayerCullingUpdate = val,
            Label = "Automatically Update Culling Mask",
            Tooltip = "The culling mask of the camera used by the Immersive Debugger is automatically updated to properly cull on/off the two layers."
        };

        private static readonly Setting CreateEventSystem = new CustomBool()
        {
            Uid = nameof(CreateEventSystem),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.CreateEventSystem,
            Set = (val) => RuntimeSettings.Instance.CreateEventSystem = val,
            Label = "Create Event System",
            Tooltip = "As the Immersive Debugger requires an EventSystem, it can be automatically created if none was found.",
            SendTelemetry = true
        };

        private static readonly Setting UseCustomIntegrationConfig = new CustomBool()
        {
            Uid = nameof(UseCustomIntegrationConfig),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.UseCustomIntegrationConfig,
            Set = (val) => RuntimeSettings.Instance.UseCustomIntegrationConfig = val,
            Label = "Use Custom Integration Config",
            Tooltip = "Supply an implementation of CustomIntegrationConfigBase to define how Immersive Debugger works with the project.",
            SendTelemetry = true
        };

        private static readonly Setting MaximumNumberOfLogEntries = new CustomInt()
        {
            Uid = nameof(MaximumNumberOfLogEntries),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.MaximumNumberOfLogEntries,
            Set = (val) => RuntimeSettings.Instance.MaximumNumberOfLogEntries = val,
            Label = "Maximum Number of Log Entries",
            Tooltip = "Limits the number of Log Entries on the console."
        };

        private static readonly Setting ClickButton = new CustomFlags<OVRInput.Button>()
        {
            Uid = nameof(ClickButton),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ClickButton,
            Set = (val) => RuntimeSettings.Instance.ClickButton = val,
            Label = "Click Button",
            Tooltip = "Customize the input button to interact with the Immersive Debugger panels in runtime.",
            SendTelemetry = true
        };

        private static readonly Setting ImmersiveDebuggerToggleDisplayButton = new CustomFlags<OVRInput.Button>()
        {
            Uid = nameof(ImmersiveDebuggerToggleDisplayButton),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ImmersiveDebuggerToggleDisplayButton,
            Set = (val) => RuntimeSettings.Instance.ImmersiveDebuggerToggleDisplayButton = val,
            Label = "Toggle Display Input Button",
            Tooltip = "Customize the input button to show/hide the Immersive Debugger panel in runtime.",
            SendTelemetry = true
        };

        private static readonly Setting ToggleFollowTranslationButton = new CustomFlags<OVRInput.Button>()
        {
            Uid = nameof(ToggleFollowTranslationButton),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ToggleFollowTranslationButton,
            Set = (val) => RuntimeSettings.Instance.ToggleFollowTranslationButton = val,
            Label = "Toggle Follow Translation Button",
            Tooltip = "Customize the input button to automatically follow the Camera Rig translation in runtime.",
            SendTelemetry = true
        };

        private static readonly Setting ToggleFollowRotationButton = new CustomFlags<OVRInput.Button>()
        {
            Uid = nameof(ToggleFollowRotationButton),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.ToggleFollowRotationButton,
            Set = (val) => RuntimeSettings.Instance.ToggleFollowRotationButton = val,
            Label = "Toggle Follow Rotation Button",
            Tooltip = "Customize the input button to automatically follow the Camera Rig rotation in runtime.",
            SendTelemetry = true
        };

        private static readonly Setting PanelDistance = new CustomEnum<RuntimeSettings.DistanceOption>()
        {
            Uid = nameof(PanelDistance),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.PanelDistance,
            Set = (val) => RuntimeSettings.Instance.PanelDistance = val,
            Label = "Panel Distance",
            Tooltip = "Set default panel distance from VR camera on startup.",
            SendTelemetry = true
        };

        private static readonly Setting OverlayDepth = new CustomInt()
        {
            Uid = nameof(OverlayDepth),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.OverlayDepth,
            Set = (val) => RuntimeSettings.Instance.OverlayDepth = val,
            Label = "Overlay Depth",
            Tooltip = "Set a depth for the overlay canvas where Immersive Debugger panel is drawn in runtime.",
            SendTelemetry = false,
        };

        private static readonly Setting UseOverlay = new CustomBool()
        {
            Uid = nameof(UseOverlay),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.UseOverlay,
            Set = (val) => RuntimeSettings.Instance.UseOverlay = val,
            Label = "Use Overlay",
            Tooltip = "OVROverlay helps improves the overall visual quality of the User Interface by using a higher resolution render texture and projecting it onto a curved plane.",
            SendTelemetry = true,
        };

        private static readonly Setting PanelLayer = new CustomLayer()
        {
            Uid = nameof(PanelLayer),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.PanelLayer,
            Set = (val) => RuntimeSettings.Instance.PanelLayer = val,
            Label = PanelLayerName,
            Tooltip = "Set a layer for Immersive Debugger panels / objects in runtime.",
            SendTelemetry = false
        };

        private static readonly Setting MeshRendererLayer = new CustomLayer()
        {
            Uid = nameof(MeshRendererLayer),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.MeshRendererLayer,
            Set = (val) => RuntimeSettings.Instance.MeshRendererLayer = val,
            Label = MeshLayerName,
            Tooltip = "Set a different layer for Immersive Debugger mesh renderer in runtime.",
            SendTelemetry = false
        };

        private static readonly Setting CustomIntegrationConfigClassName = new CustomObject<MonoScript>()
        {
            Uid = nameof(CustomIntegrationConfigClassName),
            Owner = Utils.ToolDescriptor,
            Get = () => FindMonoScriptByTypeName(RuntimeSettings.Instance.CustomIntegrationConfigClassName),
            Set = (val) => RuntimeSettings.Instance.CustomIntegrationConfigClassName =
                (val as MonoScript)?.GetClass()?.AssemblyQualifiedName,
            Label = "Custom Integration Config",
            SendTelemetry = false
        };

        private static readonly Setting HierarchyViewShowsPrivateMembers = new CustomBool()
        {
            Uid = nameof(HierarchyViewShowsPrivateMembers),
            Owner = Utils.ToolDescriptor,
            Get = () => RuntimeSettings.Instance.HierarchyViewShowsPrivateMembers,
            Set = (val) => RuntimeSettings.Instance.HierarchyViewShowsPrivateMembers = val,
            Label = "Inspect Private Members",
            Tooltip = "Whether or not the private members will be filtered out when using the hierarchy view",
            SendTelemetry = true
        };

        private static void Draw(this Setting setting, Origins origin)
        {
            setting.DrawForGUI(origin, Utils.ToolDescriptor, SetDirty);
        }

        private static void SetDirty()
        {
            EditorUtility.SetDirty(RuntimeSettings.Instance);
        }

        public static void OnGUI(Origins origin, string searchContext)
        {
            Enabled.Draw(origin);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Behavior", GUIStyles.BoldLabel);
            DisplayAtStartup.Draw(origin);
            FollowOverride.Draw(origin);
            RotateOverride.Draw(origin);
            PanelDistance.Draw(origin);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Default Toggles", GUIStyles.BoldLabel);
            ShowInspectors.Draw(origin);
            ShowConsole.Draw(origin);
            ShowInfoLog.Draw(origin);
            ShowWarningLog.Draw(origin);
            ShowErrorLog.Draw(origin);
            CollapsedIdenticalLogEntries.Draw(origin);
            MaximumNumberOfLogEntries.Draw(origin);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Input Bindings", GUIStyles.BoldLabel);
            ClickButton.Draw(origin);
            ImmersiveDebuggerToggleDisplayButton.Draw(origin);
            ToggleFollowTranslationButton.Draw(origin);
            ToggleFollowRotationButton.Draw(origin);

            var dataAssets = RuntimeSettings.Instance.InspectedDataAssets;
            if (dataAssets.Count != 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(
                    new GUIContent("Inspected Data Sets", "A list of prebuilt dataset of inspected members of InspectedData scriptable object"),
                    GUIStyles.BoldLabel);
                var dataToggle = RuntimeSettings.Instance.InspectedDataEnabled;
                for (var i = 0; i < dataAssets.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();

                    var index = i;
                    new CustomBool()
                    {
                        Uid = dataAssets[i].DisplayName,
                        Owner = Utils.ToolDescriptor,
                        Get = () => dataToggle[index],
                        Set = val => dataToggle[index] = val,
                        Label = dataAssets[i].DisplayName
                    }.DrawForGUI(origin, Utils.ToolDescriptor);

                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.ObjectField(dataAssets[i], typeof(InspectedData), false, GUILayout.ExpandWidth(true));
                    EditorGUI.EndDisabledGroup();
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Advanced", GUIStyles.BoldLabel);
            if (Foldout("Hierarchy View", "Hierarchy View"))
            {
                using (new IndentScope(EditorGUI.indentLevel + 1))
                {
                    HierarchyViewShowsPrivateMembers.Draw(origin);
                }
            }

            if (Foldout("Integration", "Integration"))
            {
                using (new IndentScope(EditorGUI.indentLevel + 1))
                {
                    EnableOnlyInDebugBuild.Draw(origin);
                    CreateEventSystem.Draw(origin);
                    UseCustomIntegrationConfig.Draw(origin);

                    EditorGUI.BeginDisabledGroup(!RuntimeSettings.Instance.UseCustomIntegrationConfig);
                    CustomIntegrationConfigClassName.Draw(origin);
                    EditorGUI.EndDisabledGroup();
                }
            }

            if (Foldout("Rendering", "Rendering"))
            {
                using (new IndentScope(EditorGUI.indentLevel + 1))
                {
                    var dialogRect = EditorGUILayout.BeginHorizontal(GUIStyles.DialogBox);
                    using (var color = new XR.Editor.UserInterface.Utils.ColorScope(XR.Editor.UserInterface.Utils.ColorScope.Scope.Content, Colors.Meta))
                    {
                        EditorGUILayout.LabelField(Meta.XR.Editor.UserInterface.Styles.Contents.InstructionsIcon, GUIStyles.DialogIconStyle, GUILayout.Width(GUIStyles.DialogIconStyle.fixedWidth));
                    }

                    EditorGUILayout.BeginVertical();
                    EditorGUILayout.LabelField(LayersDescription, GUIStyles.DialogTextStyle);
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.EndHorizontal();
                    UseOverlay.Draw(origin);
                    PanelLayer.Draw(origin);
                    MeshRendererLayer.Draw(origin);
                    AutomaticLayerCullingUpdate.Draw(origin);
                    OverlayDepth.Draw(origin);
                }
            }
        }

        private static MonoScript FindMonoScriptByTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var possibleFileName = Type.GetType(typeName)?.Name; // file name most likely the same to class name
            var guid = AssetDatabase.FindAssets(possibleFileName).FirstOrDefault();
            if (guid == null) // heavier if file not found by class name - iterate through MonoScript for the type
            {
                guid = AssetDatabase.FindAssets("t:MonoScript")
                    .FirstOrDefault(script => script.GetType().AssemblyQualifiedName == typeName);
            }
            return !string.IsNullOrEmpty(guid)
                ? AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid))
                : null;
        }
    }
}
