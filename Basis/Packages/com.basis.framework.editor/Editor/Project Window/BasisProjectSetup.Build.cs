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
            !_hasCoreClrWin.HasValue || !_hasCoreClrLinux.HasValue || !_hasCoreClrMac.HasValue || !_hasIl2cppAndroid.HasValue)
            return false;

        if (_firstRunKind == FirstRunKind.Avatar || _firstRunKind == FirstRunKind.World)
        {
            return _hasWin == true && _hasLinux == true && _hasAndroid == true
                && _hasCoreClrWin == true && _hasCoreClrLinux == true && _hasIl2cppAndroid == true;
        }

        return PlatformModuleInstalled(_choice) == true && PlatformBackendInstalled(_choice) == true;
    }

    // Apply platform + quality (+ optional scripting backend)
    private void ApplyPlatformAndQuality(PlatformChoice choice, bool applyBackend)
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

        if (applyBackend)
        {
            ScriptingImplementation desiredBackend = BasisSDKConstants.ScriptingBackendFor(target);
            if (!BasisValidationAssetCache.ScriptingBackendInstalled(target))
            {
                EditorUtility.DisplayDialog(
                    string.Format(Tr("projectSetup.platformQuality.backendNotAvailableTitle", "{0} Not Available"), desiredBackend),
                    string.Format(Tr("projectSetup.platformQuality.backendNotAvailableBody",
                        "The {0} scripting backend is not installed for {1}. " +
                        "Install *{2}* via Unity Hub, or untick the scripting backend option to switch platforms without changing it."), desiredBackend, PlatformDisplayName(choice), BuildSupportModuleName(choice)),
                    Tr("projectSetup.dialog.ok", "OK"));
                return;
            }

            try
            {
                SetScriptingBackendSafe(group, desiredBackend);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    string.Format(Tr("projectSetup.platformQuality.failedBackendTitle", "Failed to Set {0}"), desiredBackend),
                    string.Format(Tr("projectSetup.platformQuality.failedBackendBody", "Tried to set {0} but Unity reported an error:\n{1}"), desiredBackend, ex.Message),
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

    // Build module + scripting backend checks
    private void RecheckBuildModulesAndBackends()
    {
        _hasWin = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
        _hasLinux = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
        _hasAndroid = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);
        _hasMac = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX);
        _hasIOS = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS);

        _hasCoreClrWin = BasisValidationAssetCache.ScriptingBackendInstalled(BuildTarget.StandaloneWindows64);
        _hasCoreClrLinux = BasisValidationAssetCache.ScriptingBackendInstalled(BuildTarget.StandaloneLinux64);
        _hasCoreClrMac = BasisValidationAssetCache.ScriptingBackendInstalled(BuildTarget.StandaloneOSX);
        _hasIl2cppAndroid = BasisValidationAssetCache.ScriptingBackendInstalled(BuildTarget.Android);
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
        EditorGUILayout.LabelField(Tr("projectSetup.buildModules.backendAvailability", "Scripting Backends:"), EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        DrawBadge(Tr("projectSetup.status.coreClrWindows", "CoreCLR Windows"), _hasCoreClrWin == true);
        DrawBadge(Tr("projectSetup.status.coreClrLinux", "CoreCLR Linux"), _hasCoreClrLinux == true);
        DrawBadge(Tr("projectSetup.status.il2cppAndroid", "IL2CPP Android"), _hasIl2cppAndroid == true);
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
            if (!(_hasCoreClrWin == true && _hasCoreClrLinux == true && _hasIl2cppAndroid == true))
            {
                EditorGUILayout.HelpBox(
                    Tr("projectSetup.buildModules.warnAvatarWorldNeedsBackends",
                        "Avatar/World setup: Windows and Linux build with CoreCLR, Android with IL2CPP. " +
                        "Install *Windows Build Support (CoreCLR)*, *Linux Build Support (CoreCLR)* and *Android Build Support* in Unity Hub."),
                    MessageType.Warning);
            }
        }
        else
        {
            if (_choice == PlatformChoice.Android && _hasAndroid != true)
                BasisEditorUI.Help(Tr("projectSetup.buildModules.errAndroidMissing", "Android Build Support is missing. Install it in Unity Hub to build for Quest."), MessageType.Error);

            if (_choice == PlatformChoice.Android && _hasIl2cppAndroid != true && _applyBackend)
                BasisEditorUI.Help(Tr("projectSetup.buildModules.errAndroidIl2cppMissing", "Android IL2CPP is not available. Install Android Build Support (includes IL2CPP) in Unity Hub."), MessageType.Error);

            if ((_choice == PlatformChoice.Windows || _choice == PlatformChoice.Linux || _choice == PlatformChoice.Mac) && PlatformModuleInstalled(_choice) == true && PlatformBackendInstalled(_choice) != true && _applyBackend)
                BasisEditorUI.Help(string.Format(Tr("projectSetup.buildModules.errCoreClrMissing", "{0} CoreCLR is not available. Install {1} in Unity Hub."), PlatformDisplayName(_choice), BuildSupportModuleName(_choice)), MessageType.Error);
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

    // Scripting backend helpers
    private static void SetScriptingBackendSafe(BuildTargetGroup group, ScriptingImplementation impl)
    {
        NamedBuildTarget namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(group);
        if (PlayerSettings.GetScriptingBackend(namedBuildTarget) == impl) return;
        PlayerSettings.SetScriptingBackend(namedBuildTarget, impl);
    }

    private void DrawBuildScriptingBackendPreference()
    {
        BasisProjectSettingsUI.DrawScriptingBackendSection();
    }
}
#endif
