using System.Collections.Generic;
using UnityEditor;

public class BasisContentPoliceInclusionPostprocessor : AssetPostprocessor
{
    private const string Marker = "/Resources/" + BasisContentPoliceInclusions.ResourcesFolder + "/";

    [InitializeOnLoadMethod]
    private static void SyncOnLoad() => EditorApplication.delayCall += SyncSelectors;

    private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        if (!Touches(importedAssets) && !Touches(deletedAssets) && !Touches(movedAssets) && !Touches(movedFromAssetPaths)) return;
        BasisContentPoliceInclusions.Reload();
        EditorApplication.delayCall -= SyncSelectors;
        EditorApplication.delayCall += SyncSelectors;
    }

    public static void SyncSelectors()
    {
        string[] guids = AssetDatabase.FindAssets("t:" + nameof(ContentPoliceSelector));
        for (int index = 0; index < guids.Length; index++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[index]);
            ContentPoliceSelector selector = AssetDatabase.LoadAssetAtPath<ContentPoliceSelector>(path);
            if (selector == null || !selector.IncludePackageApprovals) continue;
            var present = new HashSet<string>(selector.selectedTypes, System.StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (string typeName in BasisContentPoliceInclusions.Get(selector.Kind).Keys)
            {
                if (present.Add(typeName)) missing.Add(typeName);
            }
            if (missing.Count == 0) continue;
            missing.Sort(System.StringComparer.Ordinal);
            Undo.RecordObject(selector, "Sync package content police inclusions");
            selector.selectedTypes.AddRange(missing);
            EditorUtility.SetDirty(selector);
            AssetDatabase.SaveAssetIfDirty(selector);
            BasisDebug.Log($"Content police: added {missing.Count} package approved types to {selector.name}: {string.Join(", ", missing)}", BasisDebug.LogTag.System);
        }
    }

    private static bool Touches(string[] paths)
    {
        for (int index = 0; index < paths.Length; index++)
        {
            if (paths[index].Contains(Marker)) return true;
        }
        return false;
    }
}
