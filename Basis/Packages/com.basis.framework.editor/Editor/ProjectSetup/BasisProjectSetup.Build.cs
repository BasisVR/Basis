#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public partial class BasisProjectSetup : EditorWindow
{
    // Validation helper: are required modules present for the current selection?
    private bool AreRequiredModulesOkForCurrentSelection()
    {
        if (!_hasWin.HasValue || !_hasLinux.HasValue || !_hasAndroid.HasValue ||
            !_hasIl2cppStandalone.HasValue || !_hasIl2cppAndroid.HasValue)
            return false;

        if (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World)
        {
            return _hasWin == true && _hasLinux == true && _hasAndroid == true
                && _hasIl2cppStandalone == true && _hasIl2cppAndroid == true;
        }

        switch (_choice)
        {
            case PlatformChoice.Windows:
            case PlatformChoice.Linux:
                return (_hasWin == true || _hasLinux == true)
                    ? _hasIl2cppStandalone == true
                    : false;

            case PlatformChoice.Android:
                return _hasAndroid == true && _hasIl2cppAndroid == true;

            default:
                return false;
        }
    }

    // Apply platform + quality (+ optional IL2CPP enforce)
    private void ApplyPlatformAndQuality(PlatformChoice choice, bool enforceIl2cpp)
    {
        BuildTargetGroup group;
        BuildTarget target;
        int desiredQuality;

        switch (choice)
        {
            case PlatformChoice.Android:
                group = BuildTargetGroup.Android;
                target = BuildTarget.Android;
                desiredQuality = QUALITY_ANDROID;
                break;

            case PlatformChoice.Linux:
                group = BuildTargetGroup.Standalone;
                target = BuildTarget.StandaloneLinux64;
                desiredQuality = QUALITY_DESKTOP;
                break;

            case PlatformChoice.Windows:
            default:
                group = BuildTargetGroup.Standalone;
                target = BuildTarget.StandaloneWindows64;
                desiredQuality = QUALITY_DESKTOP;
                break;
        }

#if UNITY_2021_2_OR_NEWER
        if (group == BuildTargetGroup.Standalone)
        {
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
        }
#endif

        if (enforceIl2cpp)
        {
            if (!SupportsIl2cpp(group))
            {
                EditorUtility.DisplayDialog(
                    "IL2CPP Not Available",
                    $"IL2CPP scripting backend is not available for {group}. " +
                    $"Install the appropriate *Build Support (IL2CPP)* module via Unity Hub, some platforms wont have Il2cpp support.",
                    "OK");
                return;
            }

            try
            {
                SetScriptingBackendSafe(group, ScriptingImplementation.IL2CPP);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "Failed to Set IL2CPP",
                    "Tried to set IL2CPP but Unity reported an error:\n" + ex.Message,
                    "OK");
                return;
            }
        }

        if (EditorUserBuildSettings.activeBuildTarget != target)
            EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);

        SetQualitySafe(desiredQuality);

        var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(group));
        EditorUtility.DisplayDialog(
            "Platform Applied",
            $"Switched to: {group}/{target}\n" +
            $"Quality: {desiredQuality}\n" +
            $"Scripting Backend: {backend}",
            "Nice");
    }

    private static void SetQualitySafe(int index)
    {
        index = Mathf.Clamp(index, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
        QualitySettings.SetQualityLevel(index, true);
    }

    // Build module + IL2CPP checks
    private void RecheckBuildModulesAndBackends()
    {
        _hasWin = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
        _hasLinux = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
        _hasAndroid = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);

        _hasIl2cppStandalone = SupportsIl2cpp(BuildTargetGroup.Standalone);
        _hasIl2cppAndroid = SupportsIl2cpp(BuildTargetGroup.Android);
    }

    private void RecheckBuildModulesAndBackendsRow()
    {
        RecheckBuildModulesAndBackends();
        DrawModuleAndBackendStatusRow();
    }

    private void DrawModuleAndBackendStatusRow()
    {
        EditorGUILayout.LabelField("Installed Build Modules:", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawBadge("Windows", _hasWin == true);
        DrawBadge("Linux", _hasLinux == true);
        DrawBadge("Android", _hasAndroid == true);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Re-check")) RecheckBuildModulesAndBackends();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("IL2CPP Availability:", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawBadge("Standalone (Win/Linux)", _hasIl2cppStandalone == true);
        DrawBadge("Android", _hasIl2cppAndroid == true);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        bool needsAllThree = (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World);
        if (needsAllThree)
        {
            if (!(_hasWin == true && _hasLinux == true && _hasAndroid == true))
            {
                EditorGUILayout.HelpBox(
                    "Avatar/World setup: install Windows, Linux, and Android Build Support via Unity Hub.",
                    MessageType.Warning);
            }
            if (!(_hasIl2cppStandalone == true && _hasIl2cppAndroid == true))
            {
                EditorGUILayout.HelpBox(
                    "Avatar/World setup: IL2CPP must be available for Standalone and Android. " +
                    "Install *Build Support (IL2CPP)* modules in Unity Hub.",
                    MessageType.Warning);
            }
        }
        else
        {
            if (_choice == PlatformChoice.Android && _hasAndroid != true)
                EditorGUILayout.HelpBox("Android Build Support is missing. Install it in Unity Hub to build for Quest.", MessageType.Error);

            if (_choice == PlatformChoice.Android && _hasIl2cppAndroid != true && _enforceIl2cpp)
                EditorGUILayout.HelpBox("Android IL2CPP is not available. Install Android Build Support (includes IL2CPP) in Unity Hub.", MessageType.Error);
        }

        if (Application.platform == RuntimePlatform.LinuxEditor)
        {
            EditorGUILayout.HelpBox(
                "Running in Linux Editor. Some platform toolchains may not be available on this OS; " +
                "the badges reflect what’s actually installed here.",
                MessageType.None);
        }
    }

    private void DrawBadge(string label, bool ok)
    {
        var prev = GUI.color;
        GUI.color = ok ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.6f, 0.6f);
        GUILayout.Label(ok ? $"✓ {label}" : $"✕ {label}", EditorStyles.helpBox, GUILayout.MinWidth(140));
        GUI.color = prev;
    }

    // IL2CPP helpers
    private static bool SupportsIl2cpp(BuildTargetGroup group)
    {
        try
        {
            var backends = GetAvailableScriptingBackendsSafe(group);
            foreach (var b in backends)
            {
                if (b == ScriptingImplementation.IL2CPP)
                    return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static ScriptingImplementation[] GetAvailableScriptingBackendsSafe(BuildTargetGroup group)
    {
        var direct = typeof(PlayerSettings).GetMethod(
            "GetAvailableScriptingBackends",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (direct != null)
        {
            return (ScriptingImplementation[])direct.Invoke(null, new object[] { group });
        }

        var any = typeof(PlayerSettings).GetMethod(
            "GetAvailableScriptingBackends",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (any != null)
        {
            return (ScriptingImplementation[])any.Invoke(null, new object[] { group });
        }

        if (group == BuildTargetGroup.Android)
            return new[] { ScriptingImplementation.Mono2x, ScriptingImplementation.IL2CPP };

        return new[] {PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(group))
    };
    }

    private static void SetScriptingBackendSafe(BuildTargetGroup group, ScriptingImplementation impl)
    {
        if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(group)) == impl) return;

        var backends = GetAvailableScriptingBackendsSafe(group);
        bool supported = Array.Exists(backends, b => b == impl);
        if (!supported)
            throw new InvalidOperationException($"IL2CPP not supported for {group} on this Editor install.");

        PlayerSettings.SetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(group), impl);
    }
}
#endif
