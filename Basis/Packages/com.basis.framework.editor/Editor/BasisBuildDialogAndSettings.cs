using LinkerGenerator;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class BasisBuildDialogAndSettings : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    // ====== Version bump config ======
    private const bool AutoIncrementBundleVersion = true;   // PlayerSettings.bundleVersion (also Android versionName)
    private const bool AutoIncrementAndroidVersionCode = true; // PlayerSettings.Android.bundleVersionCode

    // If true, forces bundleVersion into X.Y.Z format (best practice).
    private static bool ForceSemanticVersionFormat = true;

    // Platforms that effectively require IL2CPP (commonly true in modern Unity).
    private static readonly HashSet<BuildTarget> Il2CppOnlyTargets = new HashSet<BuildTarget>
    {
        BuildTarget.Android,
        BuildTarget.iOS,
        BuildTarget.tvOS,
        BuildTarget.WebGL,
#if UNITY_2019_1_OR_NEWER
        BuildTarget.PS4,
        BuildTarget.XboxOne,
        BuildTarget.Switch,
#endif
    };

    // Platforms you want to force CoreCLR (example: your Linux choice).
    private static readonly HashSet<BuildTarget> CoreClrOnlyTargets = new HashSet<BuildTarget>
    {
        BuildTarget.StandaloneLinux64,
        BuildTarget.LinuxHeadlessSimulation,
    };

    internal static PropertyInfo LinuxArchitectureProperty => Type.GetType("UnityEditor.LinuxStandalone.UserBuildSettings, UnityEditor.LinuxStandalone.Extensions")?.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static);

    private static bool IsLinuxArm64(BuildTarget target) => target == BuildTarget.StandaloneLinux64 && LinuxArchitectureProperty?.GetValue(null) is OSArchitecture architecture && architecture == OSArchitecture.ARM64;

    public void OnPreprocessBuild(BuildReport report)
    {
        // 0) Generate link.xml
        BasisLinkGenerator.GenerateLinkXml();

        // 0.5) Versioning
        BumpVersionsIfNeeded(report.summary.platform);

        var namedBuildTarget =
            report.summary.platformGroup == BuildTargetGroup.Standalone && report.summary.GetSubtarget<StandaloneBuildSubtarget>() == StandaloneBuildSubtarget.Server
                ? UnityEditor.Build.NamedBuildTarget.Server
                : UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);

        var currentBackend = PlayerSettings.GetScriptingBackend(namedBuildTarget);
        var target = report.summary.platform;

        // 1) Force IL2CPP-only targets
        if (Il2CppOnlyTargets.Contains(target))
        {
            SetBackendIfNeeded(namedBuildTarget, currentBackend, ScriptingImplementation.IL2CPP);
            return;
        }

        // 2) Force CoreCLR-only targets
        if (CoreClrOnlyTargets.Contains(target))
        {
            SetBackendIfNeeded(namedBuildTarget, currentBackend, IsLinuxArm64(target) ? ScriptingImplementation.IL2CPP : ScriptingImplementation.CoreCLR);
            return;
        }

        // 3) Use the remembered backend; prompt only when set to Ask
        ScriptingImplementation desired;
        var backendPref = BasisBuildScriptingBackendPreference.Current;
        if (backendPref == BasisBuildScriptingBackendPreference.Mode.IL2CPP)
        {
            desired = ScriptingImplementation.IL2CPP;
        }
        else if (backendPref == BasisBuildScriptingBackendPreference.Mode.Mono)
        {
            desired = ScriptingImplementation.Mono2x;
        }
        else if (backendPref == BasisBuildScriptingBackendPreference.Mode.CoreCLR)
        {
            desired = ScriptingImplementation.CoreCLR;
        }
        else if (Application.isBatchMode)
        {
            // Safe default for CI: CoreCLR
            desired = ScriptingImplementation.CoreCLR;
        }
        else
        {
            int choice = EditorUtility.DisplayDialogComplex(
                "Scripting Backend",
                $"Build target: {target}\n\nWhich scripting backend should this build use?\n\nYour choice is remembered for next time. Change it under Basis ▸ Project Setup ▸ Build & Modules.",
                "IL2CPP",
                "Mono",
                "CoreCLR"
            );

            if (choice == 0)
            {
                desired = ScriptingImplementation.IL2CPP;
                BasisBuildScriptingBackendPreference.Current = BasisBuildScriptingBackendPreference.Mode.IL2CPP;
            }
            else if (choice == 2)
            {
                desired = ScriptingImplementation.CoreCLR;
                BasisBuildScriptingBackendPreference.Current = BasisBuildScriptingBackendPreference.Mode.CoreCLR;
            }
            else
            {
                desired = ScriptingImplementation.Mono2x;
                BasisBuildScriptingBackendPreference.Current = BasisBuildScriptingBackendPreference.Mode.Mono;
            }
        }

        SetBackendIfNeeded(namedBuildTarget, currentBackend, desired);
    }

    private static void BumpVersionsIfNeeded(BuildTarget target)
    {
        if (AutoIncrementBundleVersion)
        {
            var before = PlayerSettings.bundleVersion;
            if (IncrementBundleVersion(before, out var after))
            {
                if (after != before)
                {
                    PlayerSettings.bundleVersion = after;
                    BasisDebug.Log($"[Build] bundleVersion: {before} -> {after}");
                }
            }
        }

        // Only bump Android versionCode when building Android (usually what you want).
        // If you want it bumped on *any* build, remove the target check.
        if (AutoIncrementAndroidVersionCode && target == BuildTarget.Android)
        {
            int before = PlayerSettings.Android.bundleVersionCode;
            int after = Mathf.Max(1, before + 1);
            PlayerSettings.Android.bundleVersionCode = after;
            BasisDebug.Log($"[Build] Android versionCode: {before} -> {after}");

            // Android versionName comes from PlayerSettings.bundleVersion by default.
            // If you want it explicitly logged:
            BasisDebug.Log($"[Build] Android versionName: {PlayerSettings.bundleVersion}");
        }

        // If you want the changes to definitely persist to ProjectSettings on disk:
        // AssetDatabase.SaveAssets();
    }

    private static bool IncrementBundleVersion(string version,out string ComputedVersion)
    {
        // Match "major.minor.patch" with optional extra junk ignored
        var m = Regex.Match(version ?? "", @"^\s*(\d+)\.(\d+)\.(\d+)\s*$");
        if (m.Success)
        {
            int major = int.Parse(m.Groups[1].Value);
            int minor = int.Parse(m.Groups[2].Value);
            int patch = int.Parse(m.Groups[3].Value) + 1;
            ComputedVersion = $"{major}.{minor}.{patch}";
        }

        // If it isn't semver, coerce it into semver and start at .0.1
        // Examples:
        // "1" -> "1.0.1"
        // "1.2" -> "1.2.1"
        // "v1.2" -> "1.2.1" (extracts digits)
        var nums = Regex.Matches(version ?? "", @"\d+");
        int majorC = nums.Count > 0 ? int.Parse(nums[0].Value) : 0;
        int minorC = nums.Count > 1 ? int.Parse(nums[1].Value) : 0;
        int patchC = 1;
        ComputedVersion = $"{majorC}.{minorC}.{patchC}";
        return ForceSemanticVersionFormat;
    }

    private static void SetBackendIfNeeded(
        UnityEditor.Build.NamedBuildTarget namedBuildTarget,
        ScriptingImplementation current,
        ScriptingImplementation desired)
    {
        if (current == desired) return;
        PlayerSettings.SetScriptingBackend(namedBuildTarget, desired);
    }
}
