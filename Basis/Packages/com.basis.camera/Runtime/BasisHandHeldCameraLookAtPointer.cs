using System.Collections.Generic;
using Basis.Cinematics;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
public partial class BasisHandHeldCamera
{
    private const float LookAtPointerRange = 250f, LookAtPointerTriggerThreshold = 0.9f;
    private const float LookAtPointerReticleSize = 0.09f, LookAtPointerSubjectPadding = 0.05f;
    private static readonly Color LookAtPointerReticleColor = new Color(1f, 0.78f, 0.2f, 1f);
    private readonly Dictionary<BasisInput, bool> lookAtTriggerPrev = new Dictionary<BasisInput, bool>();
    private int lookAtReticleId;
    private bool lookAtReticleCreated, lookAtClickPrev;
    public bool LookAtPointerArmed { get; private set; }
    public void SetLookAtPointerArmed(bool armed)
    {
        if (LookAtPointerArmed == armed) return;

        LookAtPointerArmed = armed;

        if (armed)
        {
            SeedLookAtPointerEdges();
        }
        else
        {
            lookAtTriggerPrev.Clear();
            HideLookAtReticle();
        }
    }
    public void ToggleLookAtPointer() => SetLookAtPointerArmed(!LookAtPointerArmed);
    private void SeedLookAtPointerEdges()
    {
        lookAtTriggerPrev.Clear();
        lookAtClickPrev = Mouse.current != null && Mouse.current.leftButton.isPressed;

        if (BasisDeviceManagement.Instance == null) return;
        var inputs = BasisDeviceManagement.Instance.AllInputDevices;
        for (int Index = 0; Index < inputs.Count; Index++)
        {
            BasisInput input = inputs[Index];
            if (input != null) lookAtTriggerPrev[input] = input.CurrentInputState.Trigger >= LookAtPointerTriggerThreshold;
        }
    }
    private void TickLookAtPointer()
    {
        if (!LookAtPointerArmed) return;

        if (captureCamera == null || IsCameraHidden)
        {
            SetLookAtPointerArmed(false);
            return;
        }

        bool found = BasisDeviceManagement.IsUserInDesktop() ? TickDesktopLookAtPointer(out Vector3 point, out bool commit) : TickVRLookAtPointer(out point, out commit);

        if (found) ShowLookAtReticle(point);
        else HideLookAtReticle();

        if (commit)
        {
            if (found) SetFixedPointTo(point);
            SetLookAtPointerArmed(false);
        }
    }
    private bool TickDesktopLookAtPointer(out Vector3 point, out bool commit)
    {
        point = default;
        commit = false;

        bool down = Mouse.current != null && Mouse.current.leftButton.isPressed, pressed = down && !lookAtClickPrev;
        lookAtClickPrev = down;

        if (!BasisLocalCameraDriver.HasInstance || BasisLocalCameraDriver.CameraInstance == null) return false;

        BasisLocalCameraDriver.CameraInstance.transform.GetPositionAndRotation(out Vector3 origin, out Quaternion rotation);
        bool found = TryResolveLookAtPoint(new Ray(origin, rotation * Vector3.forward), out point);

        commit = pressed && !PointerIsOverMenu();
        return found;
    }
    private bool TickVRLookAtPointer(out Vector3 point, out bool commit)
    {
        point = default;
        commit = false;
        bool found = false;
        float nearest = float.PositiveInfinity;

        if (BasisDeviceManagement.Instance == null) return false;

        var inputs = BasisDeviceManagement.Instance.AllInputDevices;
        for (int Index = 0; Index < inputs.Count; Index++)
        {
            BasisInput input = inputs[Index];
            if (input == null) continue;
            if (input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.CenterEye) continue;

            bool down = input.CurrentInputState.Trigger >= LookAtPointerTriggerThreshold;
            lookAtTriggerPrev.TryGetValue(input, out bool wasDown);
            lookAtTriggerPrev[input] = down;

            Ray ray = new Ray(input.RaycastCoord.position, input.RaycastCoord.rotation * Vector3.forward);
            bool onSomething = TryResolveLookAtPoint(ray, out Vector3 candidate);

            if (onSomething)
            {
                float distance = Vector3.Distance(ray.origin, candidate);
                if (distance < nearest)
                {
                    nearest = distance;
                    point = candidate;
                    found = true;
                }
            }

            bool overMenu = input.BasisUIRaycast != null && input.BasisUIRaycast.HadRaycastUITarget;
            if (down && !wasDown && !overMenu)
            {
                commit = true;
                if (onSomething) point = candidate;
                return onSomething;
            }
        }

        return found;
    }
    private static bool PointerIsOverMenu() => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    private bool TryResolveLookAtPoint(Ray ray, out Vector3 point)
    {
        point = default;
        if (ray.direction.sqrMagnitude < 1e-8f) return false;

        int layers = BasisDepthOfFieldInteractionHandler.VisibleFocusLayers(BasisDepthOfFieldInteractionHandler.DefaultRaycastLayers, WorldCullingMask);
        bool hasWorld = BasisCameraSubjectPicker.TryRaycastWorld(ray, LookAtPointerRange, layers, this, out RaycastHit worldHit, out float worldDistance);

        if (BasisCameraSubjectPicker.TryPickSubject(ray, LookAtPointerRange, hasWorld ? worldDistance : float.PositiveInfinity, LookAtPointerSubjectPadding, !IsDetachedFromHand, out BasisCameraSubjectHit subject))
        {
            point = subject.Point;
            return true;
        }

        if (!hasWorld) return false;

        point = worldHit.point;
        return true;
    }
    private void ShowLookAtReticle(Vector3 position)
    {
        float size = LookAtPointerReticleSize * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

        if (!lookAtReticleCreated)
        {
            if (!BasisGizmoManager.CreateSphereGizmo("CameraLookAtPointer", out lookAtReticleId, position, size, LookAtPointerReticleColor)) return;

            int overlayUi = BasisCameraCaptureLayers.Marker;
            if (overlayUi >= 0) BasisGizmoManager.SetGizmoLayer(lookAtReticleId, overlayUi);
            lookAtReticleCreated = true;
            return;
        }

        BasisGizmoManager.SetGizmoActive(lookAtReticleId, true);
        BasisGizmoManager.UpdateSphereGizmo(lookAtReticleId, position, Vector3.one * size);
    }
    private void HideLookAtReticle()
    {
        if (lookAtReticleCreated) BasisGizmoManager.SetGizmoActive(lookAtReticleId, false);
    }
    private void ShutdownLookAtPointer()
    {
        LookAtPointerArmed = false;
        lookAtTriggerPrev.Clear();

        if (!lookAtReticleCreated) return;
        BasisGizmoManager.DestroyGizmo(lookAtReticleId);
        lookAtReticleCreated = false;
    }
}
