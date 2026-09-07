using System.Collections.Generic;
using Basis.Cinematics;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Pointing at the world to say what the shot is about.
///
/// <para>Every other way of choosing a subject asks the operator to name one — a player off a
/// roster, a group, a point typed as three numbers. None of those reach a thing: the corner of a
/// building, the doorway somebody is about to walk through, the middle of a stage. Once the camera
/// is out on a dolly the operator cannot aim it by hand either, so the shot has to be told what to
/// hold on to before the move starts.</para>
///
/// <para>This is the third way, and the only one that works while the camera is somewhere else:
/// arm it, point at the thing, pull the trigger. Where you pointed becomes the fixed point and the
/// rotation slot is put on it, so the move you already laid out runs with the shot holding that
/// spot the whole way down the track.</para>
/// </summary>
public partial class BasisHandHeldCamera
{
    /// <summary>How far the pointing ray reaches. Past this there is nothing worth framing.</summary>
    private const float LookAtPointerRange = 250f;

    /// <summary>Trigger pull that counts as a click, matching the camera's other VR pick.</summary>
    private const float LookAtPointerTriggerThreshold = 0.9f;

    /// <summary>Reticle diameter in metres at default avatar scale.</summary>
    private const float LookAtPointerReticleSize = 0.09f;

    /// <summary>Slop on the bone capsules, so pointing just off a thin limb still finds the person.</summary>
    private const float LookAtPointerSubjectPadding = 0.05f;

    private static readonly Color LookAtPointerReticleColor = new Color(1f, 0.78f, 0.2f, 1f);

    /// <summary>True while the camera is waiting for the operator to point somewhere.</summary>
    public bool LookAtPointerArmed { get; private set; }

    private int lookAtReticleId;
    private bool lookAtReticleCreated;

    /// <summary>
    /// Per-input trigger state from last frame, so a pick fires on the press edge.
    ///
    /// <para>Seeded from the live state on arming rather than cleared: in VR the button that armed
    /// this was pressed with a hand ray, so that hand's trigger is already down. A cleared table
    /// would read the press still in progress as a new one and drop the point on whatever the
    /// panel happens to be standing in front of.</para>
    /// </summary>
    private readonly Dictionary<BasisInput, bool> lookAtTriggerPrev = new Dictionary<BasisInput, bool>();

    /// <summary>The same edge, for the desktop mouse.</summary>
    private bool lookAtClickPrev;

    /// <summary>Arms or disarms pointing. Idempotent, so a panel may drive it from a stale label.</summary>
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

    /// <summary>
    /// Takes every button that is down right now as already handled, so the click that armed this
    /// has to be released before the next one counts.
    /// </summary>
    private void SeedLookAtPointerEdges()
    {
        lookAtTriggerPrev.Clear();
        lookAtClickPrev = Mouse.current != null && Mouse.current.leftButton.isPressed;

        if (BasisDeviceManagement.Instance == null) return;
        var inputs = BasisDeviceManagement.Instance.AllInputDevices;
        for (int Index = 0; Index < inputs.Count; Index++)
        {
            BasisInput input = inputs[Index];
            if (input == null) continue;
            lookAtTriggerPrev[input] = input.CurrentInputState.Trigger >= LookAtPointerTriggerThreshold;
        }
    }

    /// <summary>
    /// One frame of pointing: show where the ray lands, and take the point if a trigger came down
    /// somewhere that was not the menu.
    /// </summary>
    private void TickLookAtPointer()
    {
        if (!LookAtPointerArmed)
        {
            return;
        }

        // The camera being put away is not a decision to place a point, and the reticle would be
        // the only thing left of a camera that is no longer there.
        if (captureCamera == null || IsCameraHidden)
        {
            SetLookAtPointerArmed(false);
            return;
        }

        bool found = BasisDeviceManagement.IsUserInDesktop()
            ? TickDesktopLookAtPointer(out Vector3 point, out bool commit)
            : TickVRLookAtPointer(out point, out commit);

        if (found)
        {
            ShowLookAtReticle(point);
        }
        else
        {
            HideLookAtReticle();
        }

        // A click on nothing disarms rather than doing nothing: the operator asked a question and
        // "there is nothing out there" is an answer, and leaving it armed makes the next unrelated
        // trigger pull place a point they had stopped thinking about.
        if (commit)
        {
            if (found) SetFixedPointTo(point);
            SetLookAtPointerArmed(false);
        }
    }

    /// <summary>
    /// Desktop: the operator points with their head, which is the only aim they have — the mouse
    /// is driving the cursor over the panel that armed this. A click anywhere on UI is that panel
    /// being used, not an answer.
    /// </summary>
    private bool TickDesktopLookAtPointer(out Vector3 point, out bool commit)
    {
        point = default;
        commit = false;

        bool down = Mouse.current != null && Mouse.current.leftButton.isPressed;
        bool pressed = down && !lookAtClickPrev;
        lookAtClickPrev = down;

        if (!BasisLocalCameraDriver.HasInstance || BasisLocalCameraDriver.CameraInstance == null)
        {
            return false;
        }

        Transform eye = BasisLocalCameraDriver.CameraInstance.transform;
        eye.GetPositionAndRotation(out Vector3 origin, out Quaternion rotation);
        bool found = TryResolveLookAtPoint(new Ray(origin, rotation * Vector3.forward), out point);

        commit = pressed && !PointerIsOverMenu();
        return found;
    }

    /// <summary>
    /// VR: every hand is a pointer. The reticle follows whichever one is on something nearest, and
    /// the pick belongs to whichever one's trigger came down — a hand whose ray is on the menu is
    /// pressing a button, so it is left to do that.
    /// </summary>
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
                // The hand that clicked decides, even if the other one was on something nearer.
                if (onSomething)
                {
                    point = candidate;
                    found = true;
                }
                else
                {
                    found = false;
                }
                return found;
            }
        }

        return found;
    }

    /// <summary>Whether a pointer is on menu geometry, which is the panel being used rather than aimed past.</summary>
    private static bool PointerIsOverMenu()
        => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    /// <summary>
    /// Where a ray lands, as a thing to film rather than as a surface.
    ///
    /// <para>People carry no colliders, so a plain cast goes straight through them into the wall
    /// behind — which is why the focus pick hit-tests their live skeleton, and why this one does
    /// too. A person under the ray is what the operator meant, so they win over the world hit
    /// whenever they stand in front of it, and the point lands on the body rather than on the
    /// skin facing the lens.</para>
    ///
    /// <para>The layers are narrowed to what the capture camera renders. The panel, the player's
    /// own menu and the camera's markers all carry colliders and are all culled from every shot,
    /// so without that the nearest hit for most of the room is a piece of interface — and a shot
    /// cannot be about something it does not draw.</para>
    /// </summary>
    private bool TryResolveLookAtPoint(Ray ray, out Vector3 point)
    {
        point = default;
        if (ray.direction.sqrMagnitude < 1e-8f) return false;

        int layers = BasisDepthOfFieldInteractionHandler.VisibleFocusLayers(
            BasisDepthOfFieldInteractionHandler.DefaultRaycastLayers, WorldCullingMask);

        bool hasWorld = BasisCameraSubjectPicker.TryRaycastWorld(
            ray, LookAtPointerRange, layers, this, out RaycastHit worldHit, out float worldDistance);

        if (BasisCameraSubjectPicker.TryPickSubject(ray, LookAtPointerRange,
            hasWorld ? worldDistance : float.PositiveInfinity, LookAtPointerSubjectPadding,
            !IsDetachedFromHand, out BasisCameraSubjectHit subject))
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
            if (!BasisGizmoManager.CreateSphereGizmo("CameraLookAtPointer", out lookAtReticleId,
                position, size, LookAtPointerReticleColor))
            {
                return;
            }

            // The same layer both detached markers use: the capture camera culls it, so the thing
            // the operator is aiming with can never end up in the shot it is aiming.
            int overlayUi = MarkerLayer;
            if (overlayUi >= 0) BasisGizmoManager.SetGizmoLayer(lookAtReticleId, overlayUi);
            lookAtReticleCreated = true;
            return;
        }

        BasisGizmoManager.SetGizmoActive(lookAtReticleId, true);
        BasisGizmoManager.UpdateSphereGizmo(lookAtReticleId, position, Vector3.one * size);
    }

    private void HideLookAtReticle()
    {
        if (!lookAtReticleCreated) return;
        BasisGizmoManager.SetGizmoActive(lookAtReticleId, false);
    }

    /// <summary>Drops the reticle outright. Called from teardown.</summary>
    private void ShutdownLookAtPointer()
    {
        LookAtPointerArmed = false;
        lookAtTriggerPrev.Clear();

        if (!lookAtReticleCreated) return;
        BasisGizmoManager.DestroyGizmo(lookAtReticleId);
        lookAtReticleCreated = false;
    }

}
