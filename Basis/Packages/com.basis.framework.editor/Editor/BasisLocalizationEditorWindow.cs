#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Basis.BasisUI;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor window for authoring Basis language files without hand-editing JSON.
/// Also surfaces keys that are referenced in code (via BasisLocalization.Get)
/// but not yet defined in the active language file, and keys present in
/// English that are still untranslated in the selected language.
/// </summary>
public class BasisLocalizationEditorWindow : EditorWindow
{
    private const string LanguagesRelativePath = "StreamingAssets/Basis/Languages";

    [Serializable]
    private class LanguageTableDTO
    {
        public string code;
        public string nativeName;
        public List<LanguageEntryDTO> entries = new();
    }

    [Serializable]
    private class LanguageEntryDTO
    {
        public string key;
        public string value;
    }

    private readonly List<string> _languageFiles = new();
    private int _selectedLanguageIndex = -1;
    private LanguageTableDTO _loaded;
    private string _loadedPath;
    private bool _dirty;

    // Canonical English table used to compute "untranslated" diff for other languages.
    private Dictionary<string, string> _englishSnapshot = new();

    // Code-scan results cached between repaints.
    private HashSet<string> _referencedKeys = new();
    private DateTime _lastScanTime;

    // UI state.
    private Vector2 _entriesScroll;
    private Vector2 _missingScroll;
    private string _newKey = string.Empty;
    private string _filter = string.Empty;
    private bool _showMissingFromCode = true;
    private bool _showUntranslated = true;

    [MenuItem("Basis/Settings/Localization Editor")]
    public static void Open()
    {
        GetWindow<BasisLocalizationEditorWindow>("Basis Localization");
    }

    private static string LanguagesDirectory =>
        Path.Combine(Application.streamingAssetsPath, "Basis/Languages");

    private void OnEnable()
    {
        RefreshLanguageList();
        LoadEnglishSnapshot();
        if (_languageFiles.Count > 0)
        {
            _selectedLanguageIndex = 0;
            LoadSelectedLanguage();
        }
    }

    private void OnGUI()
    {
        DrawToolbar();
        EditorGUILayout.Space(4);

        if (_loaded == null)
        {
            EditorGUILayout.HelpBox(
                $"No language file loaded. Expected files under {LanguagesRelativePath}/. " +
                "Use the New button to create one.",
                MessageType.Info);
            return;
        }

        DrawHeader();
        EditorGUILayout.Space(4);
        DrawEntriesTable();
        EditorGUILayout.Space(8);
        DrawAddRow();
        EditorGUILayout.Space(8);
        DrawDiagnosticsPanel();
    }

    // ----------------- Toolbar -----------------

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                RefreshLanguageList();
                LoadEnglishSnapshot();
                LoadSelectedLanguage();
            }

            string[] labels = _languageFiles
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .ToArray();

            int newIndex = EditorGUILayout.Popup(
                _selectedLanguageIndex,
                labels,
                EditorStyles.toolbarPopup,
                GUILayout.Width(160));

            if (newIndex != _selectedLanguageIndex)
            {
                if (ConfirmDiscardDirty())
                {
                    _selectedLanguageIndex = newIndex;
                    LoadSelectedLanguage();
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!_dirty))
            {
                if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    SaveSelectedLanguage();
                }
            }

            if (GUILayout.Button("New…", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                CreateNewLanguageDialog();
            }

            if (GUILayout.Button("Scan Code", EditorStyles.toolbarButton, GUILayout.Width(90)))
            {
                ScanReferencedKeys();
            }
        }
    }

    // ----------------- Header metadata -----------------

    private void DrawHeader()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            string fileName = Path.GetFileName(_loadedPath);
            EditorGUILayout.LabelField("File", fileName);

            string newCode = EditorGUILayout.TextField("Code", _loaded.code ?? string.Empty);
            if (newCode != _loaded.code)
            {
                _loaded.code = newCode;
                _dirty = true;
            }

            string newName = EditorGUILayout.TextField("Native Name", _loaded.nativeName ?? string.Empty);
            if (newName != _loaded.nativeName)
            {
                _loaded.nativeName = newName;
                _dirty = true;
            }

            EditorGUILayout.LabelField("Entries", _loaded.entries.Count.ToString());
        }
    }

    // ----------------- Entries table -----------------

    private void DrawEntriesTable()
    {
        EditorGUILayout.LabelField("Entries", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Filter", GUILayout.Width(40));
            _filter = EditorGUILayout.TextField(_filter);
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                _filter = string.Empty;
                GUI.FocusControl(null);
            }
        }

        _entriesScroll = EditorGUILayout.BeginScrollView(_entriesScroll, GUILayout.MinHeight(180));

        bool hasFilter = !string.IsNullOrEmpty(_filter);
        string filterLower = hasFilter ? _filter.ToLowerInvariant() : null;

        int removeIndex = -1;
        for (int i = 0; i < _loaded.entries.Count; i++)
        {
            LanguageEntryDTO entry = _loaded.entries[i];
            if (entry == null) continue;

            if (hasFilter)
            {
                bool matchesKey = !string.IsNullOrEmpty(entry.key) && entry.key.ToLowerInvariant().Contains(filterLower);
                bool matchesValue = !string.IsNullOrEmpty(entry.value) && entry.value.ToLowerInvariant().Contains(filterLower);
                if (!matchesKey && !matchesValue) continue;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                string newKey = EditorGUILayout.TextField(entry.key ?? string.Empty, GUILayout.Width(260));
                if (newKey != entry.key)
                {
                    entry.key = newKey;
                    _dirty = true;
                }

                string newValue = EditorGUILayout.TextField(entry.value ?? string.Empty);
                if (newValue != entry.value)
                {
                    entry.value = newValue;
                    _dirty = true;
                }

                if (GUILayout.Button("×", GUILayout.Width(24)))
                {
                    removeIndex = i;
                }
            }
        }

        EditorGUILayout.EndScrollView();

        if (removeIndex >= 0)
        {
            _loaded.entries.RemoveAt(removeIndex);
            _dirty = true;
        }
    }

    private void DrawAddRow()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Add key", GUILayout.Width(60));
            _newKey = EditorGUILayout.TextField(_newKey);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_newKey)))
            {
                if (GUILayout.Button("Add", GUILayout.Width(60)))
                {
                    AddKey(_newKey);
                    _newKey = string.Empty;
                    GUI.FocusControl(null);
                }
            }
        }
    }

    // ----------------- Diagnostics -----------------

    private void DrawDiagnosticsPanel()
    {
        EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            _showMissingFromCode = GUILayout.Toggle(_showMissingFromCode, "Referenced in code but missing", EditorStyles.miniButton);
            _showUntranslated = GUILayout.Toggle(_showUntranslated, "Missing translations vs. English", EditorStyles.miniButton);
        }

        if (_lastScanTime == default)
        {
            EditorGUILayout.HelpBox("Click \"Scan Code\" to search *.cs files for BasisLocalization.Get(\"...\") calls.", MessageType.None);
        }
        else
        {
            EditorGUILayout.LabelField($"Last scan: {_lastScanTime:HH:mm:ss} — {_referencedKeys.Count} keys referenced in code.", EditorStyles.miniLabel);
        }

        var loadedKeys = new HashSet<string>(_loaded.entries
            .Where(e => e != null && !string.IsNullOrEmpty(e.key))
            .Select(e => e.key));

        _missingScroll = EditorGUILayout.BeginScrollView(_missingScroll, GUILayout.MinHeight(140));

        if (_showMissingFromCode && _referencedKeys.Count > 0)
        {
            var missingFromCode = _referencedKeys
                .Where(k => !loadedKeys.Contains(k))
                .OrderBy(k => k)
                .ToList();

            EditorGUILayout.LabelField($"Referenced in code but missing here ({missingFromCode.Count})", EditorStyles.boldLabel);
            if (missingFromCode.Count == 0)
            {
                EditorGUILayout.LabelField("  (none)", EditorStyles.miniLabel);
            }
            else
            {
                DrawMissingKeyList(missingFromCode);
            }
            EditorGUILayout.Space(4);
        }

        if (_showUntranslated && !IsEditingEnglish())
        {
            var missingFromTranslation = _englishSnapshot.Keys
                .Where(k => !loadedKeys.Contains(k))
                .OrderBy(k => k)
                .ToList();

            EditorGUILayout.LabelField($"In English but not in {_loaded.code} ({missingFromTranslation.Count})", EditorStyles.boldLabel);
            if (missingFromTranslation.Count == 0)
            {
                EditorGUILayout.LabelField("  (none)", EditorStyles.miniLabel);
            }
            else
            {
                DrawMissingKeyList(missingFromTranslation, showEnglishSource: true);
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawMissingKeyList(List<string> keys, bool showEnglishSource = false)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(key, GUILayout.Width(260));
                if (showEnglishSource && _englishSnapshot.TryGetValue(key, out string englishValue))
                {
                    EditorGUILayout.LabelField(englishValue, EditorStyles.miniLabel);
                }
                else
                {
                    GUILayout.FlexibleSpace();
                }

                if (GUILayout.Button("Add", GUILayout.Width(50)))
                {
                    string seed = showEnglishSource && _englishSnapshot.TryGetValue(key, out string seedValue)
                        ? seedValue
                        : string.Empty;
                    AddKey(key, seed);
                }
            }
        }
    }

    // ----------------- Language file I/O -----------------

    private void RefreshLanguageList()
    {
        _languageFiles.Clear();
        string dir = LanguagesDirectory;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        foreach (string path in Directory.GetFiles(dir, "*.json"))
        {
            _languageFiles.Add(path);
        }

        _languageFiles.Sort(StringComparer.OrdinalIgnoreCase);

        if (_selectedLanguageIndex >= _languageFiles.Count)
        {
            _selectedLanguageIndex = _languageFiles.Count - 1;
        }
    }

    private void LoadSelectedLanguage()
    {
        if (_selectedLanguageIndex < 0 || _selectedLanguageIndex >= _languageFiles.Count)
        {
            _loaded = null;
            _loadedPath = null;
            _dirty = false;
            return;
        }

        _loadedPath = _languageFiles[_selectedLanguageIndex];
        try
        {
            string json = File.ReadAllText(_loadedPath);
            _loaded = JsonUtility.FromJson<LanguageTableDTO>(json) ?? new LanguageTableDTO();
            _loaded.entries ??= new List<LanguageEntryDTO>();
        }
        catch (Exception e)
        {
            Debug.LogError($"[BasisLocalizationEditor] Failed to load {_loadedPath}: {e}");
            _loaded = new LanguageTableDTO
            {
                code = Path.GetFileNameWithoutExtension(_loadedPath),
                nativeName = Path.GetFileNameWithoutExtension(_loadedPath),
                entries = new List<LanguageEntryDTO>()
            };
        }

        _dirty = false;
    }

    private void SaveSelectedLanguage()
    {
        if (_loaded == null || string.IsNullOrEmpty(_loadedPath)) return;

        try
        {
            // Strip empty rows so the on-disk file stays clean.
            _loaded.entries = _loaded.entries
                .Where(e => e != null && !string.IsNullOrEmpty(e.key))
                .ToList();

            string json = JsonUtility.ToJson(_loaded, true);
            File.WriteAllText(_loadedPath, json);
            AssetDatabase.Refresh();
            _dirty = false;

            if (Path.GetFileNameWithoutExtension(_loadedPath).Equals(BasisLocalization.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            {
                LoadEnglishSnapshot();
            }

            Debug.Log($"[BasisLocalizationEditor] Saved {Path.GetFileName(_loadedPath)} ({_loaded.entries.Count} entries).");
        }
        catch (Exception e)
        {
            Debug.LogError($"[BasisLocalizationEditor] Failed to save {_loadedPath}: {e}");
        }
    }

    private void LoadEnglishSnapshot()
    {
        _englishSnapshot.Clear();
        string path = Path.Combine(LanguagesDirectory, BasisLocalization.DefaultLanguage + ".json");
        if (!File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            LanguageTableDTO table = JsonUtility.FromJson<LanguageTableDTO>(json);
            if (table?.entries == null) return;

            foreach (LanguageEntryDTO entry in table.entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.key)) continue;
                _englishSnapshot[entry.key] = entry.value ?? string.Empty;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[BasisLocalizationEditor] Failed to read English snapshot: {e}");
        }
    }

    private bool IsEditingEnglish()
    {
        return _loaded != null &&
            string.Equals(_loaded.code, BasisLocalization.DefaultLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private void CreateNewLanguageDialog()
    {
        string code = EditorInputDialog.Show("New Language", "Language code (e.g. fr, de, zh-Hans):", string.Empty);
        if (string.IsNullOrEmpty(code)) return;

        string path = Path.Combine(LanguagesDirectory, code + ".json");
        if (File.Exists(path))
        {
            EditorUtility.DisplayDialog("Already exists", $"{code}.json already exists.", "OK");
            return;
        }

        LanguageTableDTO table = new()
        {
            code = code,
            nativeName = code,
            entries = new List<LanguageEntryDTO>()
        };

        File.WriteAllText(path, JsonUtility.ToJson(table, true));
        AssetDatabase.Refresh();

        RefreshLanguageList();
        _selectedLanguageIndex = _languageFiles.FindIndex(p => p == path);
        LoadSelectedLanguage();
    }

    private void AddKey(string key, string seedValue = "")
    {
        if (_loaded == null) return;
        if (_loaded.entries.Any(e => e != null && e.key == key)) return;

        _loaded.entries.Add(new LanguageEntryDTO { key = key, value = seedValue });
        _dirty = true;
        Repaint();
    }

    private bool ConfirmDiscardDirty()
    {
        if (!_dirty) return true;
        return EditorUtility.DisplayDialog(
            "Unsaved changes",
            "You have unsaved changes in this language. Discard them?",
            "Discard",
            "Cancel");
    }

    // ----------------- Code scanner -----------------

    private static readonly Regex GetCallRegex = new(
        @"BasisLocalization\.Get\s*\(\s*""([^""\\]*(?:\\.[^""\\]*)*)""",
        RegexOptions.Compiled);

    private void ScanReferencedKeys()
    {
        _referencedKeys = ScanProjectForReferencedKeys();
        _lastScanTime = DateTime.Now;
        Repaint();
    }

    /// <summary>
    /// Walks Assets/ and Packages/com.basis.* source trees for any
    /// BasisLocalization.Get("literal") call and returns the set of
    /// string literals it finds. Interpolated or variable arguments
    /// are intentionally not matched — only static literals can be
    /// verified at edit time.
    /// </summary>
    public static HashSet<string> ScanProjectForReferencedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in EnumerateScanRoots())
        {
            if (!Directory.Exists(root)) continue;

            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch
                {
                    continue;
                }

                if (text.IndexOf("BasisLocalization", StringComparison.Ordinal) < 0) continue;

                foreach (Match match in GetCallRegex.Matches(text))
                {
                    if (match.Groups.Count >= 2)
                    {
                        keys.Add(match.Groups[1].Value);
                    }
                }
            }
        }
        return keys;
    }

    private static IEnumerable<string> EnumerateScanRoots()
    {
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        yield return Application.dataPath;
        if (string.IsNullOrEmpty(projectRoot)) yield break;

        string packagesRoot = Path.Combine(projectRoot, "Packages");
        if (!Directory.Exists(packagesRoot)) yield break;

        foreach (string dir in Directory.GetDirectories(packagesRoot, "com.basis.*", SearchOption.TopDirectoryOnly))
        {
            yield return dir;
        }
    }
}

/// <summary>
/// Minimal modal input dialog used by the localization window for
/// the "New Language" action. Not a Basis-wide utility — keep it
/// scoped to this file.
/// </summary>
internal class EditorInputDialog : EditorWindow
{
    private string _value = string.Empty;
    private string _prompt;
    private bool _submitted;
    private bool _cancelled;

    public static string Show(string title, string prompt, string defaultValue)
    {
        EditorInputDialog window = CreateInstance<EditorInputDialog>();
        window.titleContent = new GUIContent(title);
        window._prompt = prompt;
        window._value = defaultValue ?? string.Empty;
        window.minSize = new Vector2(320, 90);
        window.maxSize = new Vector2(500, 120);
        window.position = new Rect(Screen.currentResolution.width / 2 - 160, Screen.currentResolution.height / 2 - 45, 320, 90);
        window.ShowModal();
        if (window._cancelled || !window._submitted) return string.Empty;
        return window._value;
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(_prompt);
        GUI.SetNextControlName("input");
        _value = EditorGUILayout.TextField(_value);
        EditorGUI.FocusTextInControl("input");

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                _cancelled = true;
                Close();
            }
            if (GUILayout.Button("OK", GUILayout.Width(80)) ||
                (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
            {
                _submitted = true;
                Close();
            }
        }
    }
}
#endif
