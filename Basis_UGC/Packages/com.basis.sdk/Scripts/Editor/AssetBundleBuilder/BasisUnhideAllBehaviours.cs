using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BasisUnhideAllBehaviours : EditorWindow
{
    [MenuItem("Basis/Tools/Unhide All Behaviours in Scene")]
   public static void UnhideAll()
    {
        int total = 0;

        // Loop through all root game objects in the active scene
        GameObject[] rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (GameObject root in rootObjects)
        {
            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null) continue;

                if (behaviour.hideFlags != HideFlags.None)
                {
                    behaviour.hideFlags = HideFlags.None;
                    EditorUtility.SetDirty(behaviour);
                    total++;
                }
            }

            // Also unhide the GameObjects themselves if needed
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in transforms)
            {
                if (t.gameObject.hideFlags != HideFlags.None)
                {
                    t.gameObject.hideFlags = HideFlags.None;
                    EditorUtility.SetDirty(t.gameObject);
                    total++;
                }
            }
        }

        Debug.Log($"Unhid {total} hidden behaviours/GameObjects.");
    }
}
