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
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Meta.XR.ImmersiveDebugger
{
    /// <summary>
    /// A serializable structure holding the debug data used by Immersive Debugger. Indexed by each assembly.
    /// It represents the debug types with the metadata of assembly name and class names within the same assembly.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    [Serializable]
    public class DebugData
    {
        /// <summary>
        /// String of the assembly, should be full name so it's identifiable
        /// </summary>
        [SerializeField]
        public string AssemblyName;
        /// <summary>
        /// A list of strings of the class names, each should be full name containing namespaces
        /// </summary>
        [SerializeField]
        public List<string> DebugTypes;
        /// <summary>
        /// Constructor of the DebugData.
        /// </summary>
        /// <param name="assemblyName">String of the assembly, should be full name so it's identifiable</param>
        /// <param name="types">A list of strings of the class names, each should be full name containing namespaces</param>
        public DebugData(string assemblyName, List<string> types)
        {
            AssemblyName = assemblyName;
            DebugTypes = types;
        }
    }

    /// <summary>
    /// A ScriptableObject named ImmersiveDebuggerSettings.asset to store:
    /// 1. various runtime settings
    /// 2. cache for Immersive Debugger debug types to understand which assemblies and types are interested to monitor for debugging
    /// This is linked to the Editor settings which can be found under Meta > Immersive Debugger.
    /// This file will be placed under "Assets/Resources" folder.
    /// For more info about Immersive Debugger, check out the [official doc](https://developer.oculus.com/documentation/unity/immersivedebugger-overview)
    /// </summary>
    public class RuntimeSettings : OVRRuntimeAssetsBase, ISerializationCallbackReceiver
    {
        /// <summary>
        /// Options of the panel's distance in relation to the player,
        /// applicable for all the in-headset panels of Immersive Debugger.
        /// </summary>
        public enum DistanceOption
        {
            Close, Default, Far
        }

        internal static string InstanceAssetName = "ImmersiveDebuggerSettings";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() // reset static fields in case of domain reload disabled
        {
            _instance = null;
        }
        private static RuntimeSettings _instance;
        internal static RuntimeSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    LoadAsset(out RuntimeSettings debugTypes, InstanceAssetName);
                    _instance = debugTypes;
                }

                return _instance;
            }
        }

        [SerializeField]
        private List<DebugData> debugTypes;
        // the actual underlying data structure to perform update etc.
        internal Dictionary<string, List<string>> debugTypesDict;

        [SerializeField] private bool immersiveDebuggerEnabled = false;
        internal static event Action OnImmersiveDebuggerEnabledChanged;
        internal bool ImmersiveDebuggerEnabled
        {
            get => immersiveDebuggerEnabled;
            set
            {
                if (immersiveDebuggerEnabled != value)
                {
                    immersiveDebuggerEnabled = value;
                    OnImmersiveDebuggerEnabledChanged?.Invoke();
                }
            }
        }

        [SerializeField] private bool immersiveDebuggerDisplayAtStartup = false;
        internal bool ImmersiveDebuggerDisplayAtStartup
        {
            get => immersiveDebuggerDisplayAtStartup;
            set => immersiveDebuggerDisplayAtStartup = value;
        }

        [SerializeField] private bool enableOnlyInDebugBuild = false;
        internal bool EnableOnlyInDebugBuild
        {
            get => enableOnlyInDebugBuild;
            set => enableOnlyInDebugBuild = value;
        }

        [SerializeField] private bool showInspectors = false;
        internal bool ShowInspectors
        {
            get => showInspectors;
            set => showInspectors = value;
        }

        [SerializeField] private bool showConsole = false;
        internal bool ShowConsole
        {
            get => showConsole;
            set => showConsole = value;
        }

        [SerializeField] private bool followOverride = true;
        internal bool FollowOverride
        {
            get => followOverride;
            set => followOverride = value;
        }

        [SerializeField] private bool rotateOverride = false;
        internal bool RotateOverride
        {
            get => rotateOverride;
            set => rotateOverride = value;
        }

        [SerializeField] private bool showInfoLog = false;
        internal bool ShowInfoLog
        {
            get => showInfoLog;
            set => showInfoLog = value;
        }

        [SerializeField] private bool showWarningLog = true;
        internal bool ShowWarningLog
        {
            get => showWarningLog;
            set => showWarningLog = value;
        }

        [SerializeField] private bool showErrorLog = true;
        internal bool ShowErrorLog
        {
            get => showErrorLog;
            set => showErrorLog = value;
        }

        [SerializeField] private bool collapsedIdenticalLogEntries = false;
        internal bool CollapsedIdenticalLogEntries
        {
            get => collapsedIdenticalLogEntries;
            set => collapsedIdenticalLogEntries = value;
        }

        [SerializeField] private int maximumNumberOfLogEntries = 1000;
        internal int MaximumNumberOfLogEntries
        {
            get => maximumNumberOfLogEntries;
            set => maximumNumberOfLogEntries = value;
        }

        [SerializeField] private DistanceOption panelDistance = DistanceOption.Default;
        internal DistanceOption PanelDistance
        {
            get => panelDistance;
            set => panelDistance = value;
        }

        [SerializeField] private bool createEventSystem = true;
        internal bool CreateEventSystem
        {
            get => createEventSystem;
            set => createEventSystem = value;
        }

        [SerializeField] private bool automaticLayerCullingUpdate = true;
        internal bool AutomaticLayerCullingUpdate
        {
            get => automaticLayerCullingUpdate;
            set => automaticLayerCullingUpdate = value;
        }

        [SerializeField] private int panelLayer = 20;
        internal int PanelLayer
        {
            get => panelLayer;
            set => panelLayer = value;
        }

        [SerializeField] private int meshRendererLayer = 21;
        internal int MeshRendererLayer
        {
            get => meshRendererLayer;
            set => meshRendererLayer = value;
        }

        [SerializeField] private int overlayDepth = 10;
        internal int OverlayDepth
        {
            get => overlayDepth;
            set => overlayDepth = value;
        }

        [SerializeField] private bool useOverlay = true;
        internal bool UseOverlay
        {
            get => useOverlay;
            set => useOverlay = value;
        }

        /// <summary>
        /// Proxy for <see cref="UseOverlay"/> that additionally returns false if in editor
        /// </summary>
        internal bool ShouldUseOverlay =>
#if UNITY_EDITOR
            false;
#else
            UseOverlay;
#endif

        [SerializeField] private List<bool> inspectedDataEnabled = new();
        internal List<bool> InspectedDataEnabled
        {
            get => inspectedDataEnabled;
            set => inspectedDataEnabled = value;
        }

        [SerializeField] private List<InspectedData> inspectedDataAssets = new();
        internal List<InspectedData> InspectedDataAssets
        {
            get => inspectedDataAssets;
            set => inspectedDataAssets = value;
        }

        [SerializeField] private bool useCustomIntegrationConfig = false;
        internal bool UseCustomIntegrationConfig
        {
            get => useCustomIntegrationConfig;
            set => useCustomIntegrationConfig = value;
        }

        [SerializeField] private string customIntegrationConfigClassName = null;

        internal string CustomIntegrationConfigClassName
        {
            get => customIntegrationConfigClassName;
            set => customIntegrationConfigClassName = value;
        }

        [SerializeField] private bool hierarchyViewShowsPrivateMembers = false;
        internal bool HierarchyViewShowsPrivateMembers
        {
            get => hierarchyViewShowsPrivateMembers;
            set => hierarchyViewShowsPrivateMembers = value;
        }

        [SerializeField] private OVRInput.Button clickButton = OVRInput.Button.PrimaryIndexTrigger | OVRInput.Button.One;
        internal OVRInput.Button ClickButton
        {
            get => clickButton;
            set => clickButton = value;
        }

        [SerializeField] private OVRInput.Button toggleFollowTranslationButton = OVRInput.Button.None;
        internal OVRInput.Button ToggleFollowTranslationButton
        {
            get => toggleFollowTranslationButton;
            set => toggleFollowTranslationButton = value;
        }

        [SerializeField] private OVRInput.Button toggleFollowRotationButton = OVRInput.Button.None;
        internal OVRInput.Button ToggleFollowRotationButton
        {
            get => toggleFollowRotationButton;
            set => toggleFollowRotationButton = value;
        }

        [SerializeField] private OVRInput.Button immersiveDebuggerToggleDisplayButton = OVRInput.Button.Two;
        internal OVRInput.Button ImmersiveDebuggerToggleDisplayButton
        {
            get => immersiveDebuggerToggleDisplayButton;
            set => immersiveDebuggerToggleDisplayButton = value;
        }

#if UNITY_EDITOR
        internal static void UpdateAllDebugTypesForInstance(List<Type> types)
        {
            UpdateAllDebugTypes(types, Instance);
        }

        internal static void UpdateAllDebugTypes(List<Type> types, RuntimeSettings instance = null)
        {
            var targetInstance = instance ? instance : Instance;
            targetInstance.debugTypesDict.Clear();
            foreach (var type in types)
            {
                var assemblyName = type.Assembly.GetName().Name;
                if (!targetInstance.debugTypesDict.ContainsKey(assemblyName))
                {
                    targetInstance.debugTypesDict[assemblyName] = new List<string>();
                }
                targetInstance.debugTypesDict[assemblyName].Add(type.FullName);
            }
            CommitDebugTypes(targetInstance);
        }

        internal static void UpdateTypes(string assemblyName, List<string> types, RuntimeSettings instance = null)
        {
            var targetInstance = instance ? instance : Instance;
            var existingDebugTypesDict = targetInstance.debugTypesDict;
            if (types.Count == 0)
            {
                if (existingDebugTypesDict.ContainsKey(assemblyName))
                {
                    existingDebugTypesDict.Remove(assemblyName);
                    CommitDebugTypes(targetInstance);
                }
            }
            else
            {
                existingDebugTypesDict[assemblyName] = types;
                CommitDebugTypes(targetInstance);
            }
        }

        private static void CommitDebugTypes(RuntimeSettings runtimeSettings)
        {
            EditorUtility.SetDirty(runtimeSettings);
        }

        internal static string GetDebugTypesInstanceAssetPath()
        {
            return GetAssetPath(InstanceAssetName);
        }
#endif

        internal RuntimeSettings()
        {
            debugTypes = new List<DebugData>();
            debugTypesDict = new Dictionary<string, List<string>>();
        }

        /// <summary>
        /// Implementation of OnBeforeSerialize from <see cref="ISerializationCallbackReceiver"/>.
        /// Reset the <see cref="debugTypes"/> and re-add everything from the cached <see cref="debugTypesDict"/> so it's serialized properly.
        /// </summary>
        public void OnBeforeSerialize()
        {
            debugTypes.Clear();
            foreach (var (assemblyName, typeList) in debugTypesDict)
            {
                debugTypes.Add(new DebugData(assemblyName, typeList));
            }
        }

        /// <summary>
        /// Implementation of OnAfterDeserialize from <see cref="ISerializationCallbackReceiver"/>.
        /// Load every debug data pair from the <see cref="debugTypes"/> so the cached data is up to date.
        /// </summary>
        public void OnAfterDeserialize()
        {
            foreach (var debugDataPair in debugTypes)
            {
                debugTypesDict[debugDataPair.AssemblyName] = debugDataPair.DebugTypes;
            }
        }

    }
}
