using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cameras that view the world and should drive per-view mirror reflections (for example
/// handheld capture cameras). The local player camera is always the primary mirror viewer
/// and does not register here. Mirror reflection cameras must never register — see
/// <see cref="BasisMirrorReflectionCamera"/>.
/// </summary>
public static class BasisMirrorViewerRegistry
{
    private static readonly List<Camera> Cameras = new List<Camera>(4);

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

    /// <summary>Appends live, active and enabled viewer cameras; prunes destroyed entries.</summary>
    public static void CollectInto(List<Camera> destination)
    {
        for (int i = Cameras.Count - 1; i >= 0; i--)
        {
            Camera camera = Cameras[i];
            if (camera == null)
            {
                Cameras.RemoveAt(i);
                continue;
            }
            if (camera.enabled && camera.gameObject.activeInHierarchy)
            {
                destination.Add(camera);
            }
        }
    }
}
