using System.Collections.Generic;
using System.Linq;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEngine;

public static class RemoveMissingScriptsEditor
{
    [MenuItem("GameObject/Editor Extensions/Remove Missing Scripts")]
    private static void FindAndRemoveMissingInSelected()
    {
        GameObject[] allObjects = GetAllChildren(Selection.gameObjects);
        int count = RemoveMissingScriptsFrom(allObjects);
        if (count == 0) return;
        EditorUtility.DisplayDialog(
            BasisEditorLocalization.Get("sdk.removeMissingScripts.title"),
            BasisEditorLocalization.Get("sdk.removeMissingScripts.body", count),
            BasisEditorLocalization.Get("sdk.common.dialog.ok"));
    }

    [MenuItem("Assets/Editor Extensions/Remove Missing Scripts")]
    private static void FindAndRemoveMissingInSelectedAssets()
    {
        FindAndRemoveMissingInSelected();
    }

    [MenuItem("Assets/Editor Extensions/Remove Missing Scripts", true)]
    private static bool FindAndRemoveMissingInSelectedAssetsValidate()
    {
        return Selection.objects.OfType<GameObject>().Any();
    }

    [MenuItem("Basis/Tools/Remove Missing Scripts From Prefabs")]
    private static void RemoveFromPrefabs()
    {
        string[] allPrefabGuids = AssetDatabase.FindAssets("t:Prefab");
        IEnumerable<string> allPrefabsPath = allPrefabGuids.Select(AssetDatabase.GUIDToAssetPath);
        IEnumerable<GameObject> allPrefabsObjects = allPrefabsPath.Select(AssetDatabase.LoadAssetAtPath<GameObject>);
        RemoveMissingScriptsFrom(allPrefabsObjects.ToArray());
        Debug.Log($"Removed all missing scripts from Prefabs");
    }

    private static int RemoveMissingScriptsFrom(params GameObject[] objects)
    {
        List<GameObject> forceSave = new();
        int removedCounter = 0;
        foreach (GameObject current in objects)
        {
            if (current == null) continue;

            int missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(current);
            if (missingCount == 0) continue;

            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(current);
            EditorUtility.SetDirty(current);

            if (EditorUtility.IsPersistent(current) && PrefabUtility.IsAnyPrefabInstanceRoot(current)) forceSave.Add(current);

            Debug.Log($"Removed {missingCount} missing script(s) from {current.gameObject.name}", current);
            removedCounter += missingCount;
        }

        foreach (GameObject o in forceSave) PrefabUtility.SavePrefabAsset(o);

        return removedCounter;
    }

    private static GameObject[] GetAllChildren(GameObject[] selection)
    {
        List<Transform> t = new();

        foreach (GameObject o in selection)
        {
            t.AddRange(o.GetComponentsInChildren<Transform>(true));
        }

        return t.Distinct().Select(x => x.gameObject).ToArray();
    }
}