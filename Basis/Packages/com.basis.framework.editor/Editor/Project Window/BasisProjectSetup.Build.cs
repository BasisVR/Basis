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
            !_hasMac.HasValue || !_hasIOS.HasValue ||
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

            case PlatformChoice.Mac:
                return _hasMac == true && _hasIl2cppStandalone == true;

            case PlatformChoice.IOS:
                return _hasIOS == true;

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

            case PlatformChoice.Mac:
                group = BuildTargetGroup.Standalone;
                target = BuildTarget.StandaloneOSX;
                desiredQuality = QUALITY_DESKTOP;
                break;

            case PlatformChoice.IOS:
                group = BuildTargetGroup.iOS;
                target = BuildTarget.iOS;
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
                    Tr("projectSetup.platformQuality.il2cppNotAvailableTitle", "IL2CPP Not Available"),
                    string.Format(Tr("projectSetup.platformQuality.il2cppNotAvailableBody",
                        "IL2CPP scripting backend is not available for {0}. " +
                        "Install the appropriate *Build Support (IL2CPP)* module via Unity Hub, some platforms won't have Il2cpp support."), group),
                    Tr("projectSetup.dialog.ok", "OK"));
                return;
            }

            try
            {
                SetScriptingBackendSafe(group, ScriptingImplementation.IL2CPP);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    Tr("projectSetup.platformQuality.failedIl2cppTitle", "Failed to Set IL2CPP"),
                    string.Format(Tr("projectSetup.platformQuality.failedIl2cppBody", "Tried to set IL2CPP but Unity reported an error:\n{0}"), ex.Message),
                    Tr("projectSetup.dialog.ok", "OK"));
                return;
            }
        }

        if (EditorUserBuildSettings.activeBuildTarget != target)
            EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);

        SetQualitySafe(desiredQuality);

        var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(group));
        EditorUtility.DisplayDialog(
            Tr("projectSetup.platformQuality.platformAppliedTitle", "Platform Applied"),
            string.Format(Tr("projectSetup.platformQuality.platformAppliedBody",
                "Switched to: {0}/{1}\nQuality: {2}\nScripting Backend: {3}"), group, target, desiredQuality, backend),
            Tr("projectSetup.platformQuality.nice", "Nice"));
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
        _hasMac = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX);
        _hasIOS = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS);

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
        EditorGUILayout.LabelField(Tr("projectSetup.buildModules.installedModules", "Installed Build Modules:"), EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawBadge(Tr("projectSetup.platformQuality.windows", "Windows"), _hasWin == true);
        DrawBadge(Tr("projectSetup.platformQuality.linux", "Linux"), _hasLinux == true);
        DrawBadge(Tr("projectSetup.status.androidPlain", "Android"), _hasAndroid == true);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(Tr("projectSetup.buildModules.recheck", "Re-check"))) RecheckBuildModulesAndBackends();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        DrawBadge(Tr("projectSetup.platformQuality.mac", "macOS"), _hasMac == true);
        DrawBadge(Tr("projectSetup.platformQuality.ios", "iOS"), _hasIOS == true);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField(Tr("projectSetup.buildModules.il2cppAvailability", "IL2CPP Availability:"), EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawBadge(Tr("projectSetup.buildModules.il2cppStandalone", "Standalone (Win/Linux/macOS)"), _hasIl2cppStandalone == true);
        DrawBadge(Tr("projectSetup.status.androidPlain", "Android"), _hasIl2cppAndroid == true);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        bool needsAllThree = (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World);
        if (needsAllThree)
        {
            if (!(_hasWin == true && _hasLinux == true && _hasAndroid == true))
            {
                EditorGUILayout.HelpBox(
                    Tr("projectSetup.buildModules.warnAvatarWorldNeedsAll",
                        "Avatar/World setup: install Windows, Linux, and Android Build Support via Unity Hub."),
                    MessageType.Warning);
            }
            if (!(_hasIl2cppStandalone == true && _hasIl2cppAndroid == true))
            {
                EditorGUILayout.HelpBox(
                    Tr("projectSetup.buildModules.warnAvatarWorldNeedsIl2cpp",
                        "Avatar/World setup: IL2CPP must be available for Standalone and Android. " +
                        "Install *Build Support (IL2CPP)* modules in Unity Hub."),
                    MessageType.Warning);
            }
        }
        else
        {
            if (_choice == PlatformChoice.Android && _hasAndroid != true)
                EditorGUILayout.HelpBox(Tr("projectSetup.buildModules.errAndroidMissing", "Android Build Support is missing. Install it in Unity Hub to build for Quest."), MessageType.Error);

            if (_choice == PlatformChoice.Android && _hasIl2cppAndroid != true && _enforceIl2cpp)
                EditorGUILayout.HelpBox(Tr("projectSetup.buildModules.errAndroidIl2cppMissing", "Android IL2CPP is not available. Install Android Build Support (includes IL2CPP) in Unity Hub."), MessageType.Error);
        }

        if (Application.platform == RuntimePlatform.LinuxEditor)
        {
            EditorGUILayout.HelpBox(
                Tr("projectSetup.buildModules.linuxEditorNotice",
                    "Running in Linux Editor. Some platform toolchains may not be available on this OS; " +
                    "the badges reflect what’s actually installed here."),
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

    private void DrawBuildScriptingBackendPreference()
    {
        BasisProjectSettingsUI.DrawScriptingBackendSection();
    }
}
#endif
