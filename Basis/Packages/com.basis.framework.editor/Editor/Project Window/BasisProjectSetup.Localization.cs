#if UNITY_EDITOR
using System;
using System.IO;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

public partial class BasisProjectSetup : EditorWindow
{
    private void HookLanguageChanged()
    {
        BasisEditorLocalization.Initialize();
        // Tables read via AssetDatabase.LoadAssetAtPath, which can serve a stale
        // TextAsset until Unity reimports the JSON. Force-reimport our language
        // files so external edits show up the first time the window opens.
        ForceReimportLanguageFiles();
        BasisEditorLocalization.ReloadTables();
        BasisEditorLocalization.OnLanguageChanged += OnEditorLanguageChanged;
    }

    private static void ForceReimportLanguageFiles()
    {
        string folder = BasisEditorLocalization.LanguagesFolder;
        if (!Directory.Exists(folder)) return;
        foreach (string path in Directory.GetFiles(folder, "*.json"))
        {
            AssetDatabase.ImportAsset(path.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
        }
    }

    private void UnhookLanguageChanged()
    {
        BasisEditorLocalization.OnLanguageChanged -= OnEditorLanguageChanged;
    }

    private void OnEditorLanguageChanged()
    {
        titleContent = new GUIContent(Tr("projectSetup.window.title", "Basis Project Setup"));
        Repaint();
    }

    // Lookup with a literal English fallback. If the key isn't yet present in
    // any language file, we render the literal instead of the raw key so the
    // UI never shows "projectSetup.tab.welcome" to a user.
    private static string Tr(string key, string english)
    {
        string value = BasisEditorLocalization.Get(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? english : value;
    }

    private void DrawLanguageSelector()
    {
        var available = BasisEditorLocalization.Available;
        if (available == null || available.Count <= 1) return;

        string currentCode = BasisEditorLocalization.CurrentLanguage;
        string[] codes = new string[available.Count];
        string[] names = new string[available.Count];
        int selected = 0;
        for (int i = 0; i < available.Count; i++)
        {
            codes[i] = available[i].Code;
            names[i] = $"{available[i].NativeName} ({available[i].Code})";
            if (string.Equals(available[i].Code, currentCode, StringComparison.OrdinalIgnoreCase))
                selected = i;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(Tr("projectSetup.language.label", "Language"), EditorStyles.miniLabel, GUILayout.Width(70));
            int next = EditorGUILayout.Popup(selected, names, GUILayout.Width(200));
            if (next != selected)
            {
                BasisEditorLocalization.SetLanguage(codes[next]);
            }
            if (GUILayout.Button("↻", EditorStyles.miniButton, GUILayout.Width(24)))
            {
                ForceReimportLanguageFiles();
                BasisEditorLocalization.ReloadTables();
                Repaint();
            }
        }
    }
}
#endif
