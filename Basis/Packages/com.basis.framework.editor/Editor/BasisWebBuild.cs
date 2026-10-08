using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Player;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class BasisWebBuild
{
    private const string ScriptCheckOutput = "Temp/BasisWebScriptCheck";
    private const int Wasm64MaximumMemoryMegabytes = 16384;
    private static readonly string[] WebDefines = { "SETTINGS_MANAGER_UNIVERSAL", "USE_INPUT_SYSTEM_POSE_CONTROL", "USE_STICK_CONTROL_THUMBSTICKS", "AUDIOLINK", "AUDIOLINK_V1", "BASIS_FRAMEWORK_EXISTS", "LITENETLIB_SPANS", "Basis_VOLUMETRIC_SUPPORTED", "BASIS_DISABLE_MICROPHONE" };

    [MenuItem("Basis/Build/Web Player")]
    private static void BuildFromMenu()
    {
        string folder = EditorUtility.SaveFolderPanel("Web player output folder", Directory.GetCurrentDirectory(), "WebBuild");
        if (string.IsNullOrEmpty(folder)) return;
        Build(folder);
    }

    [MenuItem("Basis/Build/Check Web Player Scripts")]
    private static void CheckFromMenu()
    {
        int compiled = CompileScripts();
        EditorUtility.DisplayDialog("Web player scripts", compiled > 0 ? $"{compiled} assemblies compiled for the web player." : "The web player scripts failed to compile. The errors are in the console.", "OK");
    }

    public static void BuildWeb()
    {
        string buildPath = GetArgument("customBuildPath");
        if (string.IsNullOrWhiteSpace(buildPath)) throw new BuildFailedException("Required command line argument '-customBuildPath' was not provided.");
        Build(buildPath, Array.IndexOf(Environment.GetCommandLineArgs(), "-webStackTraces") >= 0);
    }

    public static void CheckWebScripts()
    {
        if (CompileScripts() == 0) throw new BuildFailedException("The web player scripts failed to compile.");
    }

    public static int CompileScripts()
    {
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL)) throw new BuildFailedException("The Web build module is not installed for this editor.");
        Directory.CreateDirectory(ScriptCheckOutput);
        ScriptCompilationResult result = PlayerBuildInterface.CompilePlayerScripts(new ScriptCompilationSettings { target = BuildTarget.WebGL, group = BuildTargetGroup.WebGL, options = ScriptCompilationOptions.None }, ScriptCheckOutput);
        Debug.Log($"[BasisWebBuild] {result.assemblies.Count} assemblies compiled for the web player.");
        return result.assemblies.Count;
    }

    public static void ApplyPlayerSettings()
    {
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithoutStacktrace;
        PlayerSettings.WebGL.memoryGrowthMode = WebGLMemoryGrowthMode.Geometric;
        PlayerSettings.WebGL.maximumMemorySize = Wasm64MaximumMemoryMegabytes;
        PlayerSettings.WebGL.threadsSupport = false;
        PlayerSettings.WebGL.powerPreference = WebGLPowerPreference.HighPerformance;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.WebGPU });
        PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.WebGL, out string[] defines);
        string[] webDefines = defines.Union(WebDefines).ToArray();
        if (!webDefines.SequenceEqual(defines)) PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL, webDefines);
    }

    public static void Build(string buildPath, bool stackTraces = false)
    {
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL)) throw new BuildFailedException("The Web build module is not installed for this editor.");
        Debug.Log($"[BasisWebBuild] buildPath={buildPath} activeBuildTarget(before)={EditorUserBuildSettings.activeBuildTarget}");
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL) Debug.Log($"[BasisWebBuild] SwitchActiveBuildTarget(WebGL) => {EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL)}");
        ApplyPlayerSettings();
        if (stackTraces) PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;
        Directory.CreateDirectory(buildPath);
        AddressableAssetSettings addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
        AddressableAssetSettings.PlayerBuildOption originalOption = AddressableAssetSettings.PlayerBuildOption.PreferencesValue;
        if (addressableSettings != null)
        {
            originalOption = addressableSettings.BuildAddressablesWithPlayerBuild;
            bool buildAddressables = originalOption == AddressableAssetSettings.PlayerBuildOption.BuildWithPlayer || (originalOption == AddressableAssetSettings.PlayerBuildOption.PreferencesValue && EditorPrefs.GetBool("Addressables.BuildAddressablesWithPlayerBuild", true));
            if (buildAddressables)
            {
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                Debug.Log($"[BasisWebBuild] Addressables error='{result.Error}' output='{result.OutputPath}'");
                if (!string.IsNullOrWhiteSpace(result.Error)) throw new BuildFailedException($"Addressables build failed: {result.Error}");
            }
            addressableSettings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
        }
        try
        {
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(), locationPathName = buildPath, target = BuildTarget.WebGL, targetGroup = BuildTargetGroup.WebGL, options = BuildOptions.None });
            Debug.Log($"[BasisWebBuild] Build result={report.summary.result} output={report.summary.outputPath} errors={report.summary.totalErrors} warnings={report.summary.totalWarnings} size={report.summary.totalSize}");
            if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException($"Player build failed: {report.summary.result}");
        }
        finally
        {
            if (addressableSettings != null) addressableSettings.BuildAddressablesWithPlayerBuild = originalOption;
        }
    }

    private static string GetArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 0; index < args.Length - 1; index++) if (args[index] == $"-{name}") return args[index + 1];
        return null;
    }
}
