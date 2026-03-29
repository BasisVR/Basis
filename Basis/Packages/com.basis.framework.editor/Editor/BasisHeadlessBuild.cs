using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public static class BasisHeadlessBuild
{
    private const string AddressablesBuildWithPlayerPreferenceKey = "Addressables.BuildAddressablesWithPlayerBuild";

    public static void BuildLinuxServer()
    {
        BuildServer(BuildTarget.StandaloneLinux64);
    }

    public static void BuildWindowsServer()
    {
        BuildServer(BuildTarget.StandaloneWindows64);
    }

    private static void BuildServer(BuildTarget target)
    {
        string buildPath = RequireArgument("customBuildPath");
        string buildName = GetArgument("customBuildName") ?? Path.GetFileNameWithoutExtension(buildPath);
        string projectPath = GetArgument("projectPath") ?? Directory.GetCurrentDirectory();
        string standaloneSubtargetArg = GetArgument("standaloneBuildSubtarget") ?? "Server";
        string linuxArchitectureArg = GetArgument("linuxArchitecture");

        Debug.Log($"[BasisHeadlessBuild] Starting {target} build");
        Debug.Log($"[BasisHeadlessBuild] projectPath={projectPath}");
        Debug.Log($"[BasisHeadlessBuild] buildName={buildName}");
        Debug.Log($"[BasisHeadlessBuild] buildPath={buildPath}");
        Debug.Log($"[BasisHeadlessBuild] activeBuildTarget(before)={EditorUserBuildSettings.activeBuildTarget}");
        Debug.Log($"[BasisHeadlessBuild] activeBuildTargetGroup(before)={BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget)}");
        Debug.Log($"[BasisHeadlessBuild] standaloneBuildSubtarget(arg)={standaloneSubtargetArg}");
        Debug.Log($"[BasisHeadlessBuild] linuxArchitecture(arg)={linuxArchitectureArg ?? "<default>"}");

        BuildTargetGroup targetGroup = BuildPipeline.GetBuildTargetGroup(target);
        if (!BuildPipeline.IsBuildTargetSupported(targetGroup, target))
        {
            throw new BuildFailedException($"Build target {target} is not supported in this editor.");
        }

        if (EditorUserBuildSettings.activeBuildTarget != target)
        {
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, target);
            Debug.Log($"[BasisHeadlessBuild] SwitchActiveBuildTarget({target}) => {switched}");
        }

        StandaloneBuildSubtarget standaloneSubtarget = ParseStandaloneSubtarget(standaloneSubtargetArg);
        EditorUserBuildSettings.standaloneBuildSubtarget = standaloneSubtarget;
        if (target == BuildTarget.StandaloneLinux64)
        {
            int linuxArchitecture = ParseLinuxArchitecture(linuxArchitectureArg);
            PlayerSettings.SetArchitecture(targetGroup, linuxArchitecture);
            Debug.Log($"[BasisHeadlessBuild] Linux architecture(set)={linuxArchitecture}");
        }
        Debug.Log($"[BasisHeadlessBuild] activeBuildTarget(after)={EditorUserBuildSettings.activeBuildTarget}");
        Debug.Log($"[BasisHeadlessBuild] standaloneBuildSubtarget(set)={EditorUserBuildSettings.standaloneBuildSubtarget}");

        EnsureBuildDirectory(buildPath);
        LogEnabledScenes();

        AddressableAssetSettings addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
        bool restoreBuildAddressablesWithPlayerBuild = false;
        AddressableAssetSettings.PlayerBuildOption originalBuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.PreferencesValue;
        if (addressableSettings != null)
        {
            originalBuildAddressablesWithPlayerBuild = addressableSettings.BuildAddressablesWithPlayerBuild;
            restoreBuildAddressablesWithPlayerBuild = true;
            if (ShouldBuildAddressablesWithPlayerBuild(originalBuildAddressablesWithPlayerBuild))
            {
                BuildAddressables(target, addressableSettings);
            }
            addressableSettings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            Debug.Log($"[BasisHeadlessBuild] Overriding BuildAddressablesWithPlayerBuild: {originalBuildAddressablesWithPlayerBuild} -> {addressableSettings.BuildAddressablesWithPlayerBuild}");
        }
        else
        {
            Debug.LogWarning("[BasisHeadlessBuild] Addressables settings not found; continuing without Addressables override.");
        }

        try
        {
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
                locationPathName = buildPath,
                target = target,
                targetGroup = targetGroup,
                subtarget = (int)standaloneSubtarget,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[BasisHeadlessBuild] Build result={report.summary.result}");
            Debug.Log($"[BasisHeadlessBuild] Build output path={report.summary.outputPath}");
            Debug.Log($"[BasisHeadlessBuild] Build totalErrors={report.summary.totalErrors}");
            Debug.Log($"[BasisHeadlessBuild] Build totalWarnings={report.summary.totalWarnings}");

            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new BuildFailedException($"Player build failed: {report.summary.result}");
            }
        }
        finally
        {
            if (restoreBuildAddressablesWithPlayerBuild)
            {
                addressableSettings.BuildAddressablesWithPlayerBuild = originalBuildAddressablesWithPlayerBuild;
                Debug.Log($"[BasisHeadlessBuild] Restored BuildAddressablesWithPlayerBuild={addressableSettings.BuildAddressablesWithPlayerBuild}");
            }
        }
    }

    private static bool ShouldBuildAddressablesWithPlayerBuild(AddressableAssetSettings.PlayerBuildOption option)
    {
        switch (option)
        {
            case AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer:
                return true;
            case AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer:
                return false;
            case AddressableAssetSettings.PlayerBuildOption.PreferencesValue:
                return EditorPrefs.GetBool(AddressablesBuildWithPlayerPreferenceKey, true);
            default:
                return false;
        }
    }

    private static void BuildAddressables(BuildTarget target, AddressableAssetSettings settings)
    {
        Debug.Log($"[BasisHeadlessBuild] Building Addressables explicitly for {target}. Active profile={settings.activeProfileId}, builderIndex={settings.ActivePlayerDataBuilderIndex}");
        AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
        Debug.Log($"[BasisHeadlessBuild] Addressables result.Error='{result.Error}'");
        Debug.Log($"[BasisHeadlessBuild] Addressables outputPath='{result.OutputPath}'");

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            throw new BuildFailedException($"Addressables build failed: {result.Error}");
        }
    }

    private static void LogEnabledScenes()
    {
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            Debug.Log($"[BasisHeadlessBuild] Scene enabled={scene.enabled} path={scene.path}");
        }
    }

    private static void EnsureBuildDirectory(string buildPath)
    {
        string directory = Path.GetDirectoryName(buildPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static StandaloneBuildSubtarget ParseStandaloneSubtarget(string value)
    {
        if (Enum.TryParse(value, true, out StandaloneBuildSubtarget parsed))
        {
            return parsed;
        }

        return StandaloneBuildSubtarget.Server;
    }

    private static int ParseLinuxArchitecture(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        switch (value.Trim().ToUpperInvariant())
        {
            case "ARM64":
                return 1;
            case "UNIVERSAL":
                return 2;
            case "X64":
            default:
                return 0;
        }
    }

    private static string RequireArgument(string name)
    {
        string value = GetArgument(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BuildFailedException($"Required command line argument '-{name}' was not provided.");
        }

        return value;
    }

    private static string GetArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == $"-{name}")
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
