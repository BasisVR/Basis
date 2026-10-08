using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisCullingCameraRegistry
{
    private static readonly List<Camera> Cameras = new List<Camera>(8);

    public static void Register(Camera camera)
    {
        if (camera == null || Cameras.Contains(camera))
        {
            return;
        }
        Cameras.Add(camera);
    }

    public static void Unregister(Camera camera)
    {
        if (camera == null)
        {
            return;
        }
        Cameras.Remove(camera);
    }

    public static void CollectInto(List<Camera> destination)
    {
        for (int i = Cameras.Count - 1; i >= 0; i--)
        {
            var camera = Cameras[i];
            if (camera == null)
            {
                Cameras.RemoveAt(i);
                continue;
            }
            if (camera.gameObject.activeInHierarchy)
            {
                destination.Add(camera);
            }
        }
    }
}
