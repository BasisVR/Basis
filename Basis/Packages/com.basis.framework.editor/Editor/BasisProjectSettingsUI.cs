#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

// Section drawers shared by the Project Settings pages and the Basis Project Wizard, so a
// setting shown in both places has one implementation and one set of localization keys.
internal static class BasisProjectSettingsUI
{
    private static string Tr(string key, string english)
    {
        string value = BasisEditorLocalization.Get(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? english : value;
    }

    public static void DrawLocalHttpSection()
    {
        BasisNetworkingProjectSettings settings = BasisNetworkingProjectSettings.instance;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(
                Tr("projectSetup.networking.header", "Local Network HTTP"),
                EditorStyles.miniBoldLabel);

            EditorGUI.BeginChangeCheck();
            bool allow = EditorGUILayout.ToggleLeft(
                Tr("projectSetup.networking.allowToggle", "Allow HTTP on the local network"),
                settings.AllowLocalHttp);
            if (EditorGUI.EndChangeCheck())
            {
                settings.SetAllowLocalHttp(allow);
                settings.ApplyToPlayerSettings();
            }

            EditorGUILayout.HelpBox(
                Tr("projectSetup.networking.allowHelp",
                    "Basis picks the scheme per host: IP literals, localhost, single-label host names and "
                    + ".local/.lan/.internal/.home.arpa use http://, everything else uses https://. "
                    + "This grants the platform permission that choice needs — insecureHttpOption on "
                    + "Windows and Linux, usesCleartextTraffic on Android/Quest, and NSAllowsLocalNetworking "
                    + "on iOS and macOS (private and .local hosts only; public hosts stay TLS-only)."),
                MessageType.None);

            using (new EditorGUI.DisabledScope(!settings.AllowLocalHttp))
            {
                EditorGUI.BeginChangeCheck();
                string usage = EditorGUILayout.TextField(
                    new GUIContent(
                        Tr("projectSetup.networking.usageDescription", "iOS prompt text"),
                        "NSLocalNetworkUsageDescription — iOS 14+ shows this the first time the app reaches the local network."),
                    settings.LocalNetworkUsageDescription);
                if (EditorGUI.EndChangeCheck()) settings.SetLocalNetworkUsageDescription(usage);
            }

            InsecureHttpOption current = PlayerSettings.insecureHttpOption;
            InsecureHttpOption desired = settings.DesiredInsecureHttpOption;
            if (current == desired) return;

            EditorGUILayout.HelpBox(
                string.Format(
                    Tr("projectSetup.networking.mismatch",
                        "Player Settings insecureHttpOption is {0}, but this setting needs {1}. It is corrected automatically on build."),
                    current, desired),
                MessageType.Warning);
            if (GUILayout.Button(Tr("projectSetup.networking.fixNow", "Fix now")))
                settings.ApplyToPlayerSettings();
        }
    }

    public static void DrawScriptingBackendSection()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(
                Tr("projectSetup.buildModules.scriptingBackendHeader", "Build Scripting Backend (Windows / macOS)"),
                EditorStyles.miniBoldLabel);

            BasisBuildScriptingBackendPreference.Mode mode = BasisBuildScriptingBackendPreference.Current;
            BasisBuildScriptingBackendPreference.Mode original = mode;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Toggle(mode == BasisBuildScriptingBackendPreference.Mode.Ask,
                        Tr("projectSetup.buildModules.scriptingBackendAsk", "Ask each build"),
                        EditorStyles.radioButton))
                    mode = BasisBuildScriptingBackendPreference.Mode.Ask;

                if (GUILayout.Toggle(mode == BasisBuildScriptingBackendPreference.Mode.IL2CPP,
                        Tr("projectSetup.buildModules.scriptingBackendIl2cpp", "Always IL2CPP"),
                        EditorStyles.radioButton))
                    mode = BasisBuildScriptingBackendPreference.Mode.IL2CPP;

                if (GUILayout.Toggle(mode == BasisBuildScriptingBackendPreference.Mode.Mono,
                        Tr("projectSetup.buildModules.scriptingBackendMono", "Always Mono"),
                        EditorStyles.radioButton))
                    mode = BasisBuildScriptingBackendPreference.Mode.Mono;
            }

            if (mode != original)
                BasisBuildScriptingBackendPreference.Current = mode;

            EditorGUILayout.HelpBox(
                Tr("projectSetup.buildModules.scriptingBackendHelp",
                    "Remembers the IL2CPP/Mono answer for Windows and macOS builds so you’re not asked every time. " +
                    "Forced platforms are unaffected: Android and iOS always use IL2CPP, Linux always uses Mono. " +
                    "Choosing a backend in the build prompt also updates this setting."),
                MessageType.None);
        }
    }

    public static void DrawLeakDetectionSection()
    {
        bool enabled = BasisLeakDetectionDefault.Enabled;
        bool next = EditorGUILayout.ToggleLeft(
            Tr("projectSetup.playXR.leakToggle", "Force Job Leak Detection (with stack traces) on editor startup"),
            enabled);
        if (next != enabled) BasisLeakDetectionDefault.Enabled = next;

        EditorGUILayout.HelpBox(
            Tr("projectSetup.playXR.leakHelp",
                "Unity resets Jobs ▶ Leak Detection to a lower level every time the editor restarts. " +
                "While this is on, Basis re-applies “Enabled With Stack Trace” on each editor load so native/job " +
                "leaks keep reporting full stack traces. Turn it off to disable leak detection (no overhead)."),
            MessageType.None);
    }

    [SettingsProvider]
    public static SettingsProvider CreateNetworkingProvider()
    {
        return new SettingsProvider("Project/Basis/Networking", SettingsScope.Project)
        {
            label = "Networking",
            guiHandler = _ =>
            {
                EditorGUILayout.Space();
                DrawLocalHttpSection();
            },
            keywords = new HashSet<string>
            {
                "http", "https", "cleartext", "insecure", "local", "network",
                "lan", "ats", "transport", "security"
            }
        };
    }

    [SettingsProvider]
    public static SettingsProvider CreateProjectWizardProvider()
    {
        return new SettingsProvider("Project/Basis/Project Wizard", SettingsScope.Project)
        {
            label = "Project Wizard",
            guiHandler = _ =>
            {
                EditorGUILayout.Space();
                DrawScriptingBackendSection();

                EditorGUILayout.Space();
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField(
                        Tr("projectSetup.playXR.leakFoldout", "Diagnostics — Job Leak Detection"),
                        EditorStyles.miniBoldLabel);
                    DrawLeakDetectionSection();
                }

                EditorGUILayout.Space();
                if (GUILayout.Button(Tr("projectSetup.openWizard", "Open Basis Project Wizard")))
                    BasisProjectSetup.ShowWindow();
            },
            keywords = new HashSet<string>
            {
                "basis", "wizard", "setup", "il2cpp", "mono", "scripting", "backend",
                "leak", "detection", "diagnostics"
            }
        };
    }
}
#endif
