using System;
using System.IO;
using System.Linq;
using Basis.Scripts.Device_Management.Devices.WebXR;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.XR.Management;

public static class BasisWebXRBuild
{
    private const string LoaderPath = "Packages/com.basis.webxr/Runtime/WebXRLoader.asset";

    [MenuItem("Basis/Build/WebXR Player")]
    private static void BuildFromMenu()
    {
        string folder = EditorUtility.SaveFolderPanel("WebXR Player", Directory.GetCurrentDirectory(), "WebXRBuild");
        if (string.IsNullOrEmpty(folder)) return;
        Build(folder);
    }

    public static void BuildWebXR()
    {
        string buildPath = BasisWebBuild.GetArgument("customBuildPath");
        if (string.IsNullOrWhiteSpace(buildPath)) throw new BuildFailedException("Required command line argument '-customBuildPath' was not provided.");
        string version = BasisWebBuild.GetArgument("buildVersion");
        if (!string.IsNullOrWhiteSpace(version) && version != "none") PlayerSettings.bundleVersion = version;
        Build(buildPath, Array.IndexOf(Environment.GetCommandLineArgs(), "-webStackTraces") >= 0);
        Console.WriteLine("Build succeeded!");
    }

    public static void Build(string buildPath, bool stackTraces = false)
    {
        BasisWebXRLoader loader = AssetDatabase.LoadAssetAtPath<BasisWebXRLoader>(LoaderPath);
        if (loader == null) throw new BuildFailedException($"WebXR loader asset missing at {LoaderPath}");
        XRManagerSettings manager = WebManager();
        bool added = !manager.activeLoaders.Contains(loader);
        if (added && !manager.TryAddLoader(loader)) throw new BuildFailedException("The WebXR loader could not be added to the Web XR settings.");
        Debug.Log($"[BasisWebXRBuild] Web XR loaders: {string.Join(", ", manager.activeLoaders)}");
        try
        {
            BasisWebBuild.Build(buildPath, stackTraces);
        }
        finally
        {
            if (added)
            {
                manager.TryRemoveLoader(loader);
                EditorUtility.SetDirty(manager);
                AssetDatabase.SaveAssets();
            }
        }
    }

    private static XRManagerSettings WebManager()
    {
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null) throw new BuildFailedException("XR Plug-in Management settings were not found.");
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.WebGL)) perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.WebGL);
        XRGeneralSettings general = perTarget.SettingsForBuildTarget(BuildTargetGroup.WebGL);
        general.InitManagerOnStart = false;
        XRManagerSettings manager = general.Manager;
        manager.automaticLoading = false;
        manager.automaticRunning = false;
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(manager);
        AssetDatabase.SaveAssets();
        return manager;
    }
}
