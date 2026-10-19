using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.SceneManagement;

[AutoStaticsCleanup]
public static partial class BasisContent
{
    static readonly Dictionary<Scene, BasisScene> SceneContent = new Dictionary<Scene, BasisScene>();
    public static bool SpawnedByLocalPlayer(GameObject gameObject) => TryResolve(gameObject, out BasisContentBase content) && content.SpawnedByLocalPlayer;
    public static bool SpawnedByLocalPlayer(Component component) => component != null && SpawnedByLocalPlayer(component.gameObject);
    public static string CreatorUUID(GameObject gameObject) => TryResolve(gameObject, out BasisContentBase content) ? content.CreatorUUID : string.Empty;
    public static string CreatorUUID(Component component) => component != null ? CreatorUUID(component.gameObject) : string.Empty;
    public static bool TryResolve(GameObject gameObject, out BasisContentBase content)
    {
        content = null;
        if (gameObject == null) return false;
        content = gameObject.GetComponentInParent<BasisContentBase>(true);
        if (content == null) content = ResolveScene(gameObject.scene);
        return content != null;
    }
    public static bool TryResolve(Component component, out BasisContentBase content)
    {
        content = null;
        return component != null && TryResolve(component.gameObject, out content);
    }
    static BasisScene ResolveScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return null;
        if (SceneContent.TryGetValue(scene, out BasisScene cached) && cached != null) return cached;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            BasisScene found = root.GetComponentInChildren<BasisScene>(true);
            if (found != null)
            {
                SceneContent[scene] = found;
                return found;
            }
        }
        return null;
    }
}
