using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BasisAndroidBuild
{
    public static void BuildQuest()
    {
        string buildPath = GetArgument("customBuildPath");
        if (string.IsNullOrWhiteSpace(buildPath)) throw new BuildFailedException("Required command line argument '-customBuildPath' was not provided.");
        string keystoreName = GetArgument("androidKeystoreName") ?? PlayerSettings.Android.keystoreName;
        string keyaliasName = GetArgument("androidKeyaliasName") ?? PlayerSettings.Android.keyaliasName;
        string keystorePass = Environment.GetEnvironmentVariable("BASIS_ANDROID_KEYSTORE_PASS");
        string keyaliasPass = Environment.GetEnvironmentVariable("BASIS_ANDROID_KEYALIAS_PASS") ?? keystorePass;
        if (string.IsNullOrEmpty(keystorePass)) throw new BuildFailedException("BASIS_ANDROID_KEYSTORE_PASS environment variable is not set.");
        if (!File.Exists(keystoreName)) throw new BuildFailedException($"Keystore not found: {keystoreName}");
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android)) throw new BuildFailedException("Android build target is not supported in this editor.");
        Debug.Log($"[BasisAndroidBuild] buildPath={buildPath} keystore={keystoreName} alias={keyaliasName} activeBuildTarget(before)={EditorUserBuildSettings.activeBuildTarget}");
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) Debug.Log($"[BasisAndroidBuild] SwitchActiveBuildTarget(Android) => {EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android)}");
        EditorUserBuildSettings.buildAppBundle = false;
        PlayerSettings.Android.useCustomKeystore = true;
        PlayerSettings.Android.keystoreName = keystoreName;
        PlayerSettings.Android.keyaliasName = keyaliasName;
        PlayerSettings.Android.keystorePass = keystorePass;
        PlayerSettings.Android.keyaliasPass = keyaliasPass;
        string directory = Path.GetDirectoryName(buildPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        AddressableAssetSettings addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
        AddressableAssetSettings.PlayerBuildOption originalOption = AddressableAssetSettings.PlayerBuildOption.PreferencesValue;
        if (addressableSettings != null)
        {
            originalOption = addressableSettings.BuildAddressablesWithPlayerBuild;
            bool buildAddressables = originalOption == AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer || (originalOption == AddressableAssetSettings.PlayerBuildOption.PreferencesValue && EditorPrefs.GetBool("Addressables.BuildAddressablesWithPlayerBuild", true));
            if (buildAddressables)
            {
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                Debug.Log($"[BasisAndroidBuild] Addressables error='{result.Error}' output='{result.OutputPath}'");
                if (!string.IsNullOrWhiteSpace(result.Error)) throw new BuildFailedException($"Addressables build failed: {result.Error}");
            }
            addressableSettings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
        }
        try
        {
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(), locationPathName = buildPath, target = BuildTarget.Android, targetGroup = BuildTargetGroup.Android, options = BuildOptions.None });
            Debug.Log($"[BasisAndroidBuild] Build result={report.summary.result} output={report.summary.outputPath} errors={report.summary.totalErrors} warnings={report.summary.totalWarnings} size={report.summary.totalSize} versionCode={PlayerSettings.Android.bundleVersionCode} bundleVersion={PlayerSettings.bundleVersion}");
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) throw new BuildFailedException($"Player build failed: {report.summary.result}");
        }
        finally
        {
            if (addressableSettings != null) addressableSettings.BuildAddressablesWithPlayerBuild = originalOption;
            PlayerSettings.Android.keystorePass = string.Empty;
            PlayerSettings.Android.keyaliasPass = string.Empty;
        }
        Console.WriteLine("Build succeeded!");
    }
    private static string GetArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 0; index < args.Length - 1; index++) if (args[index] == $"-{name}") return args[index + 1];
        return null;
    }
}
