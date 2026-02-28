using Basis.Scripts.BasisSdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class BasisBulkBuildWindowEditor : EditorWindow
{
    private enum ContentTypeFilter
    {
        All,
        Avatars,
        Props,
    }

    [Serializable]
    private class Entry
    {
        public string assetPath;
        public string displayName;
        public ContentType type;
        public bool selected;
        public Texture2D preview;
    }

    private enum ContentType { Avatar, Prop }

    private DefaultAsset searchFolder;
    private ContentTypeFilter typeFilter = ContentTypeFilter.All;
    private Vector2 scroll;

    private readonly List<Entry> entries = new List<Entry>();
    private bool isBuilding;

    [MenuItem("Basis/Editor/Bulk Build Bundles")]
    public static void Open()
    {
        var w = GetWindow<BasisBulkBuildWindowEditor>();
        w.titleContent = new GUIContent("Basis Bulk Build");
        w.minSize = new Vector2(520, 420);
        w.Show();
    }

    private void OnGUI()
    {
        using (new EditorGUI.DisabledScope(isBuilding))
        {
            DrawHeader();
            EditorGUILayout.Space(6);
            DrawList();
            EditorGUILayout.Space(6);
            DrawFooter();
        }

        if (isBuilding)
        {
            EditorGUILayout.HelpBox("Building… check the progress bar and Console output.", MessageType.Info);
        }
    }

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("Scan Prefabs", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            searchFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Folder (optional)", "If set, only scans prefabs under this folder"),
                searchFolder,
                typeof(DefaultAsset),
                false);

            typeFilter = (ContentTypeFilter)EditorGUILayout.EnumPopup(new GUIContent("Type"), typeFilter);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan Project", GUILayout.Height(24)))
                Scan();

            if (GUILayout.Button("Select All", GUILayout.Height(24), GUILayout.Width(100)))
                SetAllSelected(true);

            if (GUILayout.Button("Select None", GUILayout.Height(24), GUILayout.Width(100)))
                SetAllSelected(false);
        }

        if (entries.Count > 0)
        {
            int sel = entries.Count(e => e.selected);
            EditorGUILayout.LabelField($"Found {entries.Count} item(s), {sel} selected.");
        }
        else
        {
            EditorGUILayout.LabelField("No items scanned yet.");
        }
    }

    private void DrawList()
    {
        EditorGUILayout.LabelField("Items", EditorStyles.boldLabel);

        using (var sv = new EditorGUILayout.ScrollViewScope(scroll))
        {
            scroll = sv.scrollPosition;

            if (entries.Count == 0)
            {
                EditorGUILayout.HelpBox("Click 'Scan Project' to find Basis prefabs.", MessageType.None);
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!PassesFilter(e)) continue;

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    e.selected = EditorGUILayout.Toggle(e.selected, GUILayout.Width(18));

                    // Preview
                    GUILayout.Box(e.preview, GUILayout.Width(44), GUILayout.Height(44));

                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(e.displayName, EditorStyles.boldLabel);
                        EditorGUILayout.LabelField($"{e.type} — {e.assetPath}", EditorStyles.miniLabel);
                    }

                    if (GUILayout.Button("Ping", GUILayout.Width(60), GUILayout.Height(24)))
                    {
                        var obj = AssetDatabase.LoadMainAssetAtPath(e.assetPath);
                        EditorGUIUtility.PingObject(obj);
                        Selection.activeObject = obj;
                    }
                }
            }
        }
    }

    private void DrawFooter()
    {
        EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);

        int selectedCount = entries.Count(e => e.selected && PassesFilter(e));
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(selectedCount == 0 || isBuilding))
            {
                if (GUILayout.Button($"Build Selected ({selectedCount})", GUILayout.Height(28)))
                {
                    _ = BuildSelectedAsync();
                }
            }

            if (GUILayout.Button("Clear Console", GUILayout.Height(28), GUILayout.Width(120)))
            {
                var logEntries = Type.GetType("UnityEditor.LogEntries, UnityEditor.dll");
                var clearMethod = logEntries?.GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                clearMethod?.Invoke(null, null);
            }
        }

        EditorGUILayout.HelpBox(
            "Uses BasisAssetBundleObject.selectedTargets and the same password settings as your inspectors.\n" +
            "Avatars/Props build via GameObjectBundleBuild. Scenes build via SceneBundleBuild.",
            MessageType.Info);
    }

    private bool PassesFilter(Entry e)
    {
        return typeFilter switch
        {
            ContentTypeFilter.All => true,
            ContentTypeFilter.Avatars => e.type == ContentType.Avatar,
            ContentTypeFilter.Props => e.type == ContentType.Prop,
            _ => true
        };
    }

    private void SetAllSelected(bool value)
    {
        foreach (var e in entries)
        {
            if (PassesFilter(e))
                e.selected = value;
        }
        Repaint();
    }

    private void Scan()
    {
        entries.Clear();

        string folderPath = null;
        if (searchFolder != null)
        {
            folderPath = AssetDatabase.GetAssetPath(searchFolder);
            if (!AssetDatabase.IsValidFolder(folderPath))
                folderPath = null;
        }

        string[] searchInFolders = folderPath != null ? new[] { folderPath } : null;
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", searchInFolders);

        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            // Single probe: is it any Basis content?
            BasisContentBase content = prefab.GetComponentInChildren<BasisContentBase>(true);
            if (content == null) continue;

            // Determine the specific type for display/build routing
            if (content is BasisAvatar)
                AddEntry(path, prefab.name, ContentType.Avatar, prefab);
            else if (content is BasisProp)
                AddEntry(path, prefab.name, ContentType.Prop, prefab);
            else
            {
                // Future-proofing: new subclasses won't break the window
                Debug.LogWarning($"Found BasisContentBase subclass not handled: {content.GetType().FullName} at {path}");
            }
        }

        entries.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));

        foreach (var e in entries)
        {
            var main = AssetDatabase.LoadMainAssetAtPath(e.assetPath);
            e.preview = AssetPreview.GetAssetPreview(main) ?? AssetPreview.GetMiniThumbnail(main);
        }

        Repaint();
    }

    private void AddEntry(string path, string prefabName, ContentType type, GameObject prefab)
    {
        entries.Add(new Entry
        {
            assetPath = path,
            displayName = prefabName,
            type = type,
            selected = true,
            preview = AssetPreview.GetAssetPreview(prefab) ?? AssetPreview.GetMiniThumbnail(prefab)
        });
    }

    private async Task BuildSelectedAsync()
    {
        if (isBuilding) return;

        BasisAssetBundleObject assetBundleObject =
            AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);

        if (assetBundleObject == null)
        {
            Debug.LogError("BasisAssetBundleObject not found. Check BasisAssetBundleObject.AssetBundleObject path.");
            return;
        }

        var targets = assetBundleObject.selectedTargets;
        if (targets == null || targets.Count == 0)
        {
            Debug.LogError("No build targets selected (BasisAssetBundleObject.selectedTargets is empty).");
            return;
        }

        var toBuild = entries.Where(e => e.selected && PassesFilter(e)).ToList();
        if (toBuild.Count == 0) return;

        isBuilding = true;
        try
        {
            for (int i = 0; i < toBuild.Count; i++)
            {
                var e = toBuild[i];

                EditorUtility.DisplayProgressBar(
                    "Basis Bulk Build",
                    $"{i + 1}/{toBuild.Count} — {e.type}: {e.displayName}",
                    (float)i / toBuild.Count);

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(e.assetPath);
                if (prefab == null)
                {
                    Debug.LogError($"Failed to load prefab at {e.assetPath}");
                    continue;
                }

                // Build from an instantiated clone so we never touch the authored prefab.
                var buildRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (buildRoot == null)
                {
                    Debug.LogError($"Failed to instantiate prefab: {e.assetPath}");
                    continue;
                }

                try
                {
                    // Preview PNG (same idea as your inspectors)
                    Texture2D img = AssetPreview.GetAssetPreview(prefab) ?? AssetPreview.GetAssetPreview(buildRoot);
                    string imageBytes = img != null ? BasisTextureCompression.ToPngBytes(img) : null;

                    switch (e.type)
                    {
                        case ContentType.Avatar:
                            {
                                var avatar = buildRoot.GetComponentInChildren<BasisAvatar>(true);
                                if (avatar == null) { Debug.LogError($"No BasisAvatar found in {e.assetPath}"); break; }

                                var (ok, msg) = await BasisBundleBuild.GameObjectBundleBuild(
                                    imageBytes, avatar, targets,
                                    assetBundleObject.UseCustomPassword,
                                    assetBundleObject.UserSelectedPassword);

                                LogResult(ok, msg, e);
                                break;
                            }

                        case ContentType.Prop:
                            {
                                var prop = buildRoot.GetComponentInChildren<BasisProp>(true);
                                if (prop == null) { Debug.LogError($"No BasisProp found in {e.assetPath}"); break; }

                                var (ok, msg) = await BasisBundleBuild.GameObjectBundleBuild(
                                    imageBytes, prop, targets,
                                    assetBundleObject.UseCustomPassword,
                                    assetBundleObject.UserSelectedPassword);

                                LogResult(ok, msg, e);
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
                finally
                {
                    // Always clean up the clone
                    if (buildRoot != null)
                        DestroyImmediate(buildRoot);
                }
            }
        }
        finally
        {
            isBuilding = false;
            EditorUtility.ClearProgressBar();
        }
    }

    private void LogResult(bool ok, string msg, Entry e)
    {
        if (ok)
            Debug.Log($"[Basis Bulk Build] SUCCESS — {e.type}: {e.displayName} ({e.assetPath})");
        else
            Debug.LogError($"[Basis Bulk Build] FAIL — {e.type}: {e.displayName} ({e.assetPath}) :: {msg}");
    }
}
