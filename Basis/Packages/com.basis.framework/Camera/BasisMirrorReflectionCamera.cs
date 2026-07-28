using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marks a camera as a mirror reflection source. BasisSDKMirror adds this to the portal
/// cameras it creates; third-party mirror implementations should add it to theirs.
/// Per-camera logic (mirror updates, screen effects) can then skip reflection cameras via
/// <see cref="IsReflectionCamera"/> instead of relying on object-name prefixes.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class BasisMirrorReflectionCamera : MonoBehaviour
{
    private static readonly HashSet<Camera> ReflectionCameras = new HashSet<Camera>();

    private Camera cachedCamera;

    private void Awake()
    {
        if (TryGetComponent(out cachedCamera))
        {
            ReflectionCameras.Add(cachedCamera);
        }
    }

    private void OnDestroy()
    {
        ReflectionCameras.Remove(cachedCamera);
    }

    /// <summary>True when the camera renders a mirror reflection and must not drive mirror updates or other per-view effects.</summary>
    public static bool IsReflectionCamera(Camera camera)
    {
        return camera != null && ReflectionCameras.Contains(camera);
    }
}
