using UnityEngine;

/// <summary>
/// Shared visualisation for the local user's eye-gaze ray. Eye-tracking producers
/// (OpenXR EyeGazeInteraction, OpenVR pose action, future vendor SDKs) call <see cref="Tick"/>
/// each frame; the gizmo manager handles the rest. Producers also gate on the
/// developer-tab toggle before passing <c>shouldShow=true</c>. Call <see cref="Shutdown"/>
/// when the producing device is torn down.
/// </summary>
public static class BasisEyeGazeGizmo
{
    private const float RayLength = 5f;
    private const float TargetSize = 0.04f;
    private const float RayWidth = 0.005f;

    private static int _rayId = -1;
    private static int _targetId = -1;
    private static bool _created;
    private static bool _visible;
    private static bool _registered;

    public static void Tick(bool shouldShow, Vector3 origin, Vector3 direction)
    {
        EnsureMasterToggleHook();

        if (!shouldShow)
        {
            SetVisible(false);
            return;
        }
        EnsureCreated();
        Vector3 endpoint = origin + direction * RayLength;
        BasisGizmoManager.UpdateLineGizmo(_rayId, origin, endpoint);
        BasisGizmoManager.UpdateSphereGizmo(_targetId, endpoint, Vector3.one * TargetSize);
        SetVisible(true);
    }

    public static void Shutdown()
    {
        if (!_created)
        {
            return;
        }
        BasisGizmoManager.DestroyGizmo(_rayId);
        BasisGizmoManager.DestroyGizmo(_targetId);
        ResetState();
    }

    private static void EnsureCreated()
    {
        if (_created)
        {
            return;
        }
        BasisGizmoManager.CreateLineGizmo("EyeGazeRay", out _rayId,
            Vector3.zero, Vector3.forward * RayLength, RayWidth, Color.cyan);
        BasisGizmoManager.CreateSphereGizmo("EyeGazeTarget", out _targetId,
            Vector3.zero, TargetSize, Color.cyan);
        _created = true;
        _visible = true;
    }

    private static void SetVisible(bool visible)
    {
        if (!_created || _visible == visible)
        {
            return;
        }
        BasisGizmoManager.SetGizmoActive(_rayId, visible);
        BasisGizmoManager.SetGizmoActive(_targetId, visible);
        _visible = visible;
    }

    private static void EnsureMasterToggleHook()
    {
        if (_registered)
        {
            return;
        }
        BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
        _registered = true;
    }

    /// <summary>
    /// Master toggle going off blows away the gizmo dictionaries in BasisGizmoManager,
    /// so our cached IDs become stale. Drop them; the next Tick will EnsureCreated.
    /// </summary>
    private static void OnMasterToggleChanged(bool state)
    {
        if (!state)
        {
            ResetState();
        }
    }

    private static void ResetState()
    {
        _rayId = -1;
        _targetId = -1;
        _created = false;
        _visible = false;
    }
}
