using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Basis.Editor.Localization;

[NoAutoStaticsCleanup]
public static class BasisDeprecatedComponentUpgrader
{
#pragma warning disable 618
    private static readonly (Type From, Type To)[] Map =
    {
        (typeof(BasisObjectSyncNetworking), typeof(BasisPickupSyncNetworking)),
    };
#pragma warning restore 618

    private static readonly Dictionary<Type, MonoScript> ScriptCache = new Dictionary<Type, MonoScript>();

    public sealed class Report
    {
        public int Swapped;
        public int Assets;
        public int Events;
        public readonly List<string> Warnings = new List<string>();
    }

    public static bool TryGetReplacement(Type from, out Type to)
    {
        for (int i = 0; i < Map.Length; i++)
        {
            if (Map[i].From != from) continue;
            to = Map[i].To;
            return true;
        }
        to = null;
        return false;
    }

    public static VisualElement Banner(UnityEngine.Object[] targets)
    {
        if (targets == null || targets.Length == 0 || targets[0] == null || !TryGetReplacement(targets[0].GetType(), out Type to)) return null;
        Type from = targets[0].GetType();

        var box = new VisualElement();
        box.style.marginBottom = 10;
        box.style.paddingTop = 8;
        box.style.paddingBottom = 8;
        box.style.paddingLeft = 10;
        box.style.paddingRight = 10;
        box.style.backgroundColor = BasisEditorUI.Light ? new Color(0.98f, 0.92f, 0.70f, 0.85f) : new Color(0.651f, 0.631f, 0.051f, 0.5f);
        box.style.borderBottomWidth = 3;
        box.style.borderBottomColor = BasisEditorUI.Warn;
        box.style.borderTopLeftRadius = 5;
        box.style.borderTopRightRadius = 5;
        box.style.borderBottomLeftRadius = 5;
        box.style.borderBottomRightRadius = 5;

        var title = new Label(BasisEditorLocalization.Get("editor.deprecatedUpgrader.bannerTitle", from.Name));
        title.style.fontSize = 13;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = BasisEditorUI.Value;

        var body = new Label(targets.Length > 1 ? BasisEditorLocalization.Get("editor.deprecatedUpgrader.bannerBodyMulti", to.Name, targets.Length) : BasisEditorLocalization.Get("editor.deprecatedUpgrader.bannerBody", to.Name));
        body.style.whiteSpace = WhiteSpace.Normal;
        body.style.fontSize = 11;
        body.style.marginTop = 2;
        body.style.color = BasisEditorUI.Value;

        var button = new Button(() => UpgradeTargets(targets)) { text = BasisEditorLocalization.Get("editor.deprecatedUpgrader.upgradeTo", to.Name) };
        button.style.marginTop = 6;
        button.style.height = 24;
        button.style.unityFontStyleAndWeight = FontStyle.Bold;

        box.Add(title);
        box.Add(body);
        box.Add(button);
        return box;
    }

    private static void UpgradeTargets(UnityEngine.Object[] targets)
    {
        Upgrade(targets.OfType<Component>().ToArray());
        EditorApplication.delayCall += () => ActiveEditorTracker.sharedTracker.ForceRebuild();
    }

    [MenuItem("Basis/Tools/Upgrade Deprecated Components/Selection", false, 523)]
    public static void UpgradeSelection()
    {
        Upgrade(Selection.gameObjects.SelectMany(go => go.GetComponentsInChildren<Component>(true)).ToArray());
    }

    [MenuItem("Basis/Tools/Upgrade Deprecated Components/Selection", true)]
    private static bool ValidateSelection() => Selection.gameObjects.Length > 0;

    [MenuItem("Basis/Tools/Upgrade Deprecated Components/Open Scenes", false, 524)]
    public static void UpgradeOpenScenes()
    {
        Upgrade(LoadedScenes().SelectMany(SceneComponents).ToArray());
    }

    [MenuItem("Basis/Tools/Upgrade Deprecated Components/Whole Project", false, 525)]
    public static void UpgradeProject()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("Exit Play Mode before upgrading deprecated components.");
            return;
        }
        string[] guids = Map.Select(m => FindScript(m.From)).Where(s => s != null).Select(s => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out string guid, out long _) ? guid : null).Where(g => g != null).ToArray();
        if (guids.Length == 0)
        {
            Debug.LogError("Could not resolve the MonoScript of any deprecated component; nothing was scanned.");
            return;
        }
        List<string> prefabs = Candidates("t:Prefab", ".prefab", guids);
        List<string> scenes = Candidates("t:Scene", ".unity", guids);
        if (prefabs.Count == 0 && scenes.Count == 0)
        {
            Debug.Log("No deprecated components found in any prefab or scene.");
            return;
        }
        if (!EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.deprecatedUpgrader.title"), BasisEditorLocalization.Get("editor.deprecatedUpgrader.projectConfirm", prefabs.Count, scenes.Count, string.Join("\n", prefabs.Concat(scenes))), BasisEditorLocalization.Get("editor.deprecatedUpgrader.upgrade"), BasisEditorLocalization.Get("editor.common.cancel"))) return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup().Where(s => !string.IsNullOrEmpty(s.path)).ToArray();
        var report = new Report();
        int total = prefabs.Count + scenes.Count;
        try
        {
            for (int i = 0; i < prefabs.Count; i++)
            {
                EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.deprecatedUpgrader.title"), prefabs[i], (float)i / total);
                UpgradeAssets(new[] { prefabs[i] }, report);
            }
            for (int i = 0; i < scenes.Count; i++)
            {
                EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("editor.deprecatedUpgrader.title"), scenes[i], (float)(prefabs.Count + i) / total);
                Scene scene = EditorSceneManager.OpenScene(scenes[i], OpenSceneMode.Single);
                int before = report.Swapped + report.Events;
                Run(SceneComponents(scene).ToArray(), report);
                if (report.Swapped + report.Events > before) EditorSceneManager.SaveScene(scene);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup);
            else EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        }
        Log(report);
    }

    public static Report Upgrade(IReadOnlyList<Component> components)
    {
        var report = new Report();
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            report.Warnings.Add("Exit Play Mode before upgrading deprecated components.");
            Log(report);
            return report;
        }
        var assets = new List<string>();
        bool any = false;
        foreach (Component c in components)
        {
            if (c == null || !TryGetReplacement(c.GetType(), out _)) continue;
            any = true;
            string path = RoutedAssetPath(c);
            if (path != null && !assets.Contains(path)) assets.Add(path);
        }
        if (!any)
        {
            Debug.Log("No deprecated components to upgrade.");
            return report;
        }
        if (assets.Count > 0 && !EditorUtility.DisplayDialog(BasisEditorLocalization.Get("editor.deprecatedUpgrader.title"), BasisEditorLocalization.Get("editor.deprecatedUpgrader.assetsConfirm", assets.Count, string.Join("\n", assets)), BasisEditorLocalization.Get("editor.deprecatedUpgrader.upgrade"), BasisEditorLocalization.Get("editor.common.cancel"))) return report;
        Run(components, report);
        Log(report);
        return report;
    }

    private static void Run(IReadOnlyList<Component> components, Report report)
    {
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Upgrade Deprecated Components");
        var assets = new List<string>();
        var dirtyScenes = new HashSet<Scene>();
        foreach (Component c in components)
        {
            if (c == null || !TryGetReplacement(c.GetType(), out Type to)) continue;
            string path = RoutedAssetPath(c);
            if (path != null)
            {
                if (!assets.Contains(path)) assets.Add(path);
                continue;
            }
            Scene scene = c.gameObject.scene;
            if (!Swap(c, to, true)) continue;
            report.Swapped++;
            if (scene.IsValid()) dirtyScenes.Add(scene);
        }
        UpgradeAssets(assets, report);
        foreach (Scene scene in LoadedScenes()) report.Events += Restitch(scene.GetRootGameObjects(), report.Warnings, true);
        foreach (Scene scene in dirtyScenes) EditorSceneManager.MarkSceneDirty(scene);
    }

    private static string RoutedAssetPath(Component c)
    {
        if (PrefabUtility.IsPartOfPrefabInstance(c) && !PrefabUtility.IsAddedComponentOverride(c))
        {
            Component source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(c);
            if (source != null) return AssetDatabase.GetAssetPath(source);
        }
        return EditorUtility.IsPersistent(c) ? AssetDatabase.GetAssetPath(c) : null;
    }

    private static void UpgradeAssets(IEnumerable<string> paths, Report report)
    {
        var pending = new Queue<string>(paths);
        var visited = new HashSet<string>();
        while (pending.Count > 0) UpgradePrefabAsset(pending.Dequeue(), visited, pending, report);
    }

    private static void UpgradePrefabAsset(string path, HashSet<string> visited, Queue<string> pending, Report report)
    {
        if (string.IsNullOrEmpty(path) || !visited.Add(path)) return;
        if (!Editable(path))
        {
            report.Warnings.Add($"{path} is in a read-only package and was skipped.");
            return;
        }
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            int swapped = 0;
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || !TryGetReplacement(c.GetType(), out Type to)) continue;
                if (PrefabUtility.IsPartOfPrefabInstance(c) && !PrefabUtility.IsAddedComponentOverride(c))
                {
                    Component source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(c);
                    string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
                    if (!string.IsNullOrEmpty(sourcePath) && sourcePath != path)
                    {
                        pending.Enqueue(sourcePath);
                        continue;
                    }
                }
                if (Swap(c, to, false)) swapped++;
            }
            int events = Restitch(new[] { root }, report.Warnings, false);
            if (swapped == 0 && events == 0) return;
            PrefabUtility.SaveAsPrefabAsset(root, path);
            report.Swapped += swapped;
            report.Events += events;
            report.Assets++;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static bool Swap(Component component, Type to, bool undo)
    {
        MonoScript script = FindScript(to);
        if (script == null)
        {
            Debug.LogError($"No MonoScript found for {to.FullName}; cannot upgrade {component.GetType().Name} on {component.name}.", component);
            return false;
        }
        var so = new SerializedObject(component);
        so.FindProperty("m_Script").objectReferenceValue = script;
        if (undo) so.ApplyModifiedProperties();
        else so.ApplyModifiedPropertiesWithoutUndo();
        return true;
    }

    private static int Restitch(IEnumerable<GameObject> roots, List<string> warnings, bool undo)
    {
        int events = 0;
        foreach (GameObject root in roots)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var so = new SerializedObject(component);
                SerializedProperty it = so.GetIterator();
                bool enter = true, dirty = false;
                while (it.Next(enter))
                {
                    enter = EnterChildren(it);
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    UnityEngine.Object value = it.objectReferenceValue;
                    if (value == null) continue;
                    for (int i = 0; i < Map.Length; i++)
                    {
                        (Type from, Type to) = Map[i];
                        if (!to.IsInstanceOfType(value) || from.IsInstanceOfType(value)) continue;
                        if (it.type == "PPtr<$" + from.Name + ">") warnings.Add($"{HierarchyPath(component)}.{it.propertyPath} is declared as {from.Name} but now points at a {value.GetType().Name}; change the field type to {to.Name} or the reference is dropped on the next reload.");
                        if (it.name != "m_Target") continue;
                        SerializedProperty typeName = so.FindProperty(ParentPath(it.propertyPath) + ".m_TargetAssemblyTypeName");
                        if (typeName == null || typeName.propertyType != SerializedPropertyType.String || !typeName.stringValue.StartsWith(from.FullName + ",", StringComparison.Ordinal)) continue;
                        typeName.stringValue = AssemblyTypeName(value.GetType());
                        dirty = true;
                        events++;
                    }
                }
                if (!dirty) continue;
                if (undo) so.ApplyModifiedProperties();
                else so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        return events;
    }

    private static bool EnterChildren(SerializedProperty p)
    {
        if (p.propertyType == SerializedPropertyType.ManagedReference) return true;
        if (p.propertyType != SerializedPropertyType.Generic) return false;
        if (!p.isArray || p.arraySize == 0) return true;
        SerializedPropertyType element = p.GetArrayElementAtIndex(0).propertyType;
        return element == SerializedPropertyType.Generic || element == SerializedPropertyType.ObjectReference || element == SerializedPropertyType.ManagedReference;
    }

    private static string ParentPath(string path)
    {
        int i = path.LastIndexOf('.');
        return i < 0 ? "" : path.Substring(0, i);
    }

    private static string AssemblyTypeName(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;

    private static string HierarchyPath(Component c)
    {
        string path = c.name;
        for (Transform t = c.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
        return path + " <" + c.GetType().Name + ">";
    }

    private static MonoScript FindScript(Type type)
    {
        if (ScriptCache.TryGetValue(type, out MonoScript cached) && cached != null) return cached;
        MonoScript found = FindScript(type, "t:MonoScript " + type.Name) ?? FindScript(type, "t:MonoScript");
        if (found != null) ScriptCache[type] = found;
        return found;
    }

    private static MonoScript FindScript(Type type, string filter)
    {
        foreach (string guid in AssetDatabase.FindAssets(filter))
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
            if (script != null && script.GetClass() == type) return script;
        }
        return null;
    }

    private static bool Editable(string path)
    {
        if (!path.StartsWith("Packages/", StringComparison.Ordinal)) return path.StartsWith("Assets/", StringComparison.Ordinal);
        UnityEditor.PackageManager.PackageInfo info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
        return info != null && (info.source == UnityEditor.PackageManager.PackageSource.Embedded || info.source == UnityEditor.PackageManager.PackageSource.Local);
    }

    private static List<string> Candidates(string filter, string extension, string[] guids)
    {
        var result = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets(filter))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || !Editable(path) || !Mentions(path, guids)) continue;
            result.Add(path);
        }
        return result;
    }

    private static bool Mentions(string path, string[] guids)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch { return false; }
        if (!text.StartsWith("%YAML", StringComparison.Ordinal)) return true;
        for (int i = 0; i < guids.Length; i++) if (text.Contains(guids[i])) return true;
        return false;
    }

    private static IEnumerable<Scene> LoadedScenes()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded) yield return scene;
        }
        PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && stage.scene.IsValid()) yield return stage.scene;
    }

    private static IEnumerable<Component> SceneComponents(Scene scene) => scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<Component>(true));

    private static void Log(Report report)
    {
        Debug.Log($"Deprecated component upgrade: {report.Swapped} component(s) swapped, {report.Assets} prefab asset(s) rewritten, {report.Events} UnityEvent target(s) restitched.");
        foreach (string warning in report.Warnings) Debug.LogWarning(warning);
    }
}
