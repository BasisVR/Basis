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

using System.Linq;
using Meta.XR.Editor.Utils;
using UnityEditor;
using UnityEngine;

#if USING_XR_MANAGEMENT
using UnityEditor.XR.Management;
using UnityEngine.XR.Management;
#endif

[InitializeOnLoad]
internal static class OVRProjectSetupXRTasks
{
    private const string OculusXRPackageName = "com.unity.xr.oculus";
    internal const string XRPluginManagementPackageName = "com.unity.xr.management";
    internal const string XRSimulatorPackageName = "com.meta.xr.simulator";
    internal const string UnityXRPackage = "com.unity.xr.openxr";
    private const string UPMTitle = "Unity Package Manager";

    private const OVRProjectSetup.TaskGroup XRTaskGroup = OVRProjectSetup.TaskGroup.Packages;

    static OVRProjectSetupXRTasks()
    {
#if UNITY_EDITOR_OSX
        OVRProjectSetup.AddTask(
            conditionalValidity: _ => PackageList.PackageManagerListAvailable,
            level: OVRProjectSetup.TaskLevel.Required,
            group: XRTaskGroup,
            isDone: _ => PackageList.IsPackageInstalled(UnityXRPackage),
#if UNITY_6000_0_OR_NEWER
            message: $"It is recommended to use the OpenXR Plugin ({UnityXRPackage}) package installed through the {UPMTitle}."
#else
            message: $"It is recommended to use the OpenXR Plugin ({UnityXRPackage}) package installed through the {UPMTitle}. Please note that OpenXR Plugin support for Depth API is only available from Unity 6 and onwards."
#endif
        );
#else
        OVRProjectSetup.AddTask(
            conditionalValidity: _ => PackageList.PackageManagerListAvailable,
            level: OVRProjectSetup.TaskLevel.Required,
            group: XRTaskGroup,
            isDone: _ => PackageList.IsPackageInstalled(OculusXRPackageName) || PackageList.IsPackageInstalled(UnityXRPackage),
#if UNITY_6000_0_OR_NEWER
            message: $"Either the Oculus XR ({OculusXRPackageName}) or OpenXR Plugin ({UnityXRPackage}) package must be installed through the {UPMTitle}. It is recommended to use the OpenXR Plugin ({UnityXRPackage}) package."
#else
            message: $"Either the Oculus XR ({OculusXRPackageName}) or OpenXR Plugin ({UnityXRPackage}) package must be installed through the {UPMTitle}. Please note that OpenXR Plugin support for Depth API is only available from Unity 6 and onwards."
#endif
        );
#endif

#if UNITY_6000_0_OR_NEWER
        OVRProjectSetup.AddTask(
            conditionalValidity: _ => PackageList.PackageManagerListAvailable,
            level: OVRProjectSetup.TaskLevel.Recommended,
            group: XRTaskGroup,
            isDone: _ => !PackageList.IsPackageInstalled(OculusXRPackageName),
            message: $"Beginning with v74, it is recommended to use the OpenXR plugin ({UnityXRPackage}) instead of the OculusXR plugin ({OculusXRPackageName}).",
            fixMessage: $"Open Package Manager",
            fix: _ => { UnityEditor.PackageManager.UI.Window.Open(OculusXRPackageName); }
        );
#else
        OVRProjectSetup.AddTask(
            conditionalValidity: _ => PackageList.PackageManagerListAvailable,
            level: OVRProjectSetup.TaskLevel.Optional,
            group: XRTaskGroup,
            isDone: _ => !PackageList.IsPackageInstalled(UnityXRPackage),
            message: $"Please note that OpenXR Plugin ({UnityXRPackage}) support for Depth API is only available from Unity 6 and onwards."
        );
#endif
        OVRProjectSetup.AddTask(
            conditionalValidity: _ => PackageList.PackageManagerListAvailable,
            level: OVRProjectSetup.TaskLevel.Required,
            group: XRTaskGroup,
            isDone: _ => PackageList.IsPackageInstalled(XRPluginManagementPackageName),
            message: $"The XR Plug-in Management ({XRPluginManagementPackageName}) package must be installed through the {UPMTitle}."
        );

        OVRProjectSetup.AddTask(
            conditionalValidity: _ => PackageList.PackageManagerListAvailable,
            level: OVRProjectSetup.TaskLevel.Required,
            group: XRTaskGroup,
            isDone: _ => !(PackageList.IsPackageInstalled(OculusXRPackageName) && PackageList.IsPackageInstalled(UnityXRPackage)),
            fixAutomatic: false,
            message: $"It's not recommended to install Oculus XR Plugin and OpenXR Plugin at the same time, which may introduce unintentional conflicts.\nClick 'Edit' to open the Package Manager to uninstall one of the plugins. OpenXR Plugin is the recommended plugin to use.",
            fixMessage: $"Open Package Manager",
            fix: _ => { UnityEditor.PackageManager.UI.Window.Open(OculusXRPackageName); }
        );
    }

#if USING_XR_MANAGEMENT
    internal static bool IsActiveLoader<T>(BuildTargetGroup buildTargetGroup)
    {
        var settings = OVRProjectSetupXRTasks.GetXRGeneralSettingsForBuildTarget(buildTargetGroup, false);
        return settings != null && settings.Manager != null &&
               settings.Manager.activeLoaders.Any(loader => loader is T);
    }

    internal static void AddLoader<T>(BuildTargetGroup buildTargetGroup)
        where T : XRLoaderHelper
    {
        var settings = GetXRGeneralSettingsForBuildTarget(buildTargetGroup, true);
        if (settings == null)
        {
            throw new OVRConfigurationTaskException("Could not find XR Plugin Manager settings");
        }

        var loader = GetLoader<T>(buildTargetGroup);

        if(!settings.Manager.TryAddLoader(loader))
        {
            Debug.LogError("Failed to add loader, try doing it manually.");
        }
        EditorUtility.SetDirty(settings);
    }

    internal static void RemoveLoader<T>(BuildTargetGroup buildTargetGroup)
        where T : XRLoaderHelper
    {
        var settings = GetXRGeneralSettingsForBuildTarget(buildTargetGroup, true);
        if (settings == null)
        {
            throw new OVRConfigurationTaskException("Could not find XR Plugin Manager settings");
        }

        var loader = GetLoader<T>(buildTargetGroup);

        if (!settings.Manager.TryRemoveLoader(loader))
        {
            Debug.LogError("Failed to remove loader, try doing it manually.");
        }
        EditorUtility.SetDirty(settings);
    }

    private static XRLoaderHelper GetLoader<T>(BuildTargetGroup buildTargetGroup)
        where T : XRLoaderHelper
    {
        var expectedType = typeof(T);

        var loadersList = AssetDatabase.FindAssets($"t: {expectedType.Name}")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<T>).ToList();

        T loader;
        if (loadersList.Count > 0)
        {
            loader = loadersList[0];
        }
        else
        {
            loader = ScriptableObject.CreateInstance<T>();
            EnsureIsValidFolder("Assets/XR/Loaders");
            AssetDatabase.CreateAsset(loader, $"Assets/XR/Loaders/{expectedType.Name}.asset");
        }

        return loader;
    }

    private static void EnsureIsValidFolder(string path)
    {
        var folders = path.Split('/');
        string fullPath = null;
        foreach (var folder in folders)
        {
            var newPath = string.IsNullOrEmpty(fullPath) ? folder : fullPath + "/" + folder;
            if (!AssetDatabase.IsValidFolder(newPath))
                AssetDatabase.CreateFolder(fullPath, folder);
            fullPath = newPath;
        }
    }

    private static UnityEngine.XR.Management.XRGeneralSettings GetXRGeneralSettingsForBuildTarget(
        BuildTargetGroup buildTargetGroup, bool create)
    {
        var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(buildTargetGroup);
        if (!create || settings != null)
        {
            return settings;
        }

        // we have to create these settings ourselves as
        // long as Unity doesn't expose the internal function
        // XRGeneralSettingsPerBuildTarget.GetOrCreate()
        var settingsKey = UnityEngine.XR.Management.XRGeneralSettings.k_SettingsKey;
        EditorBuildSettings.TryGetConfigObject<XRGeneralSettingsPerBuildTarget>(
            settingsKey, out var settingsPerBuildTarget);

        if (settingsPerBuildTarget == null)
        {
            settingsPerBuildTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            EnsureIsValidFolder("Assets/XR");
            const string assetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
            AssetDatabase.CreateAsset(settingsPerBuildTarget, assetPath);
            AssetDatabase.SaveAssets();

            EditorBuildSettings.AddConfigObject(settingsKey, settingsPerBuildTarget, true);
        }

        if (!settingsPerBuildTarget.HasManagerSettingsForBuildTarget(buildTargetGroup))
        {
            settingsPerBuildTarget.CreateDefaultManagerSettingsForBuildTarget(buildTargetGroup);
        }

        return XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(buildTargetGroup);
    }
#endif
}
