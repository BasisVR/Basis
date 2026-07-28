using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Lets a VR user grab and drag (and optionally rotate/scale) their play space by holding a
    /// configurable controller input, the way naelstrof's VRPlayspaceMover does. Gated behind the
    /// Body Tracking "Playspace Mover" toggle and driven each frame after character movement.
    ///
    /// Horizontal (X/Z) drag is applied incrementally through the character controller, so it composes
    /// with normal locomotion instead of blocking it and still resolves walls/floors. One held hand
    /// drags (translation). Two hands holding the main input scale (drives the custom avatar scale, not
    /// the play space); two hands holding the rotation input rotate (yaw). The net horizontal drag is
    /// tracked so <see cref="ResetOffset"/> can undo it while leaving normal locomotion intact.
    ///
    /// Vertical (Y) drag works like OVR Advanced Settings' "Space Drag": you pull yourself up or down
    /// and stay where you let go. It can't ride the character controller (gravity, applied earlier in
    /// the frame, would claw it straight back), so instead it accumulates into <see cref="VerticalOffset"/>
    /// — a tracking-space height offset injected into every device in
    /// <see cref="Device_Management.Devices.BasisInput.ComputeUnscaledDeviceCoord"/>, the same hook seated
    /// mode uses for its height delta. That shifts the whole tracking space (view, hands, avatar) without
    /// moving the capsule or touching gravity, so it persists for free. Gated by the "Vertical" toggle.
    /// </summary>
    public static class BasisLocalPlayspaceMover
    {
        public const string InputGrip = "Grip";
        public const string InputTrigger = "Trigger";
        public const string InputPrimary = "Primary";
        public const string InputSecondary = "Secondary";

        public const string HandBoth = "Both";
        public const string HandLeft = "Left";
        public const string HandRight = "Right";

        // Play-space flip axes (settings-driven, not controller-bound).
        public const string AxisRoll = "Roll";   // about forward — sideways / upside down
        public const string AxisPitch = "Pitch"; // about right — front/back flip
        public const string AxisYaw = "Yaw";     // about up — spin the view horizontally

        private const float MinHeight = 0.1f;
        private const float MaxHeight = 5f;
        private const float TriggerThreshold = 0.5f;

        private static bool _grabbing;
        private static bool _capLeft;
        private static bool _capRight;
        private static Vector3 _prevLeftLocal;
        private static Vector3 _prevRightLocal;

        private static bool _scaling;
        private static Vector3 _grabLeftUnscaled;
        private static Vector3 _grabRightUnscaled;
        private static float _grabBaseHeight;

        private static bool _scaleDirty;
        private static float _pendingScaleHeight;

        private static bool _scriptScaleDriving;
        private static float _scriptScaleRestore;
        private static bool _scriptScaleRestoreCustom;
        private static float _scriptScaleApplied = float.NaN;

        // Net horizontal translation the mover has applied through the character controller, so it can
        // be undone on demand. Vertical is tracked separately in VerticalOffset.
        private static Vector3 _offsetPos;

        /// <summary>
        /// Vertical play-space offset (OVRAS Space-Drag style), in unscaled (real-world) metres. Injected
        /// into every device's Y in <see cref="Device_Management.Devices.BasisInput.ComputeUnscaledDeviceCoord"/>
        /// — the same hook seated mode uses — so the whole tracking space (view, hands, avatar) shifts up/
        /// down without moving the capsule or fighting gravity. Persists until dragged back or reset.
        /// </summary>
        public static float VerticalOffset;

        // Previous frame's offset-free (raw) hand heights. The vertical drag is measured from these so
        // the injected VerticalOffset (which is added back into the device Y we read) can't feed back
        // into the measurement and run the offset away.
        private static float _prevLeftRawY;
        private static float _prevRightRawY;

        /// <summary>
        /// Active play-space flip rotation (OVRAS-style, but settings-driven not grabbed). Applied about
        /// head height to the rig's final local->world pose — the avatar via <see cref="ApplyFlipToMatrix"/>
        /// (localToWorldMatrix) and the view/controllers/trackers via
        /// <see cref="Device_Management.Devices.BasisInput.ApplyFinalMovement"/> — so the world appears
        /// rotated / upside down without rotating the character controller capsule. Identity when the flip
        /// toggle is off or the angle is ~0/360.
        /// </summary>
        public static Quaternion FlipRotation = Quaternion.identity;
        /// <summary>True while <see cref="FlipRotation"/> is a non-identity rotation worth applying.</summary>
        public static bool HasFlip;
        // Local-space pivot height (eye height) the flip rotates about, so your view stays put as the world tips.
        private static float _flipPivotY;

        private static BasisLocks.LockContext _movementLock;
        private static bool _hasMovementLock;

        /// <summary>Total horizontal play-space drag currently applied (world units).</summary>
        public static Vector3 CurrentOffset => _offsetPos;

        public static void Simulate(BasisLocalPlayer player, float deltaTime)
        {
            // Ticked ahead of every gate below so the restore still runs on the frame a script stops
            // driving (or the mover bails), instead of stranding the player at a scripted size.
            TickScriptedScale();

            // The opt-in toggle and the VR requirement gate the hand-grab GESTURE. A sandboxed script
            // supplying synthetic hands is not a gesture, so it drives the mover on its own terms
            // (desktop included) while every safety gate below still applies to both paths.
            bool deviceDriven = BasisSettingsDefaults.EnablePlayspaceMover.RawValue
                && BasisDeviceManagement.IsCurrentModeVR();
            bool scriptDriven = BasisScriptedPlayerInput.MoverActive;

            if (player == null || BasisLocalPlayer.PlayerReady == false
                || (deviceDriven == false && scriptDriven == false)
                || player.LocalSeatDriver.IsSeated)
            {
                // Feature off / not VR / seated / not ready: drop any vertical offset + flip so re-enabling
                // starts clean and you aren't left floating or tipped.
                VerticalOffset = 0f;
                HasFlip = false;
                FlipRotation = Quaternion.identity;
                Stop();
                return;
            }

            // Admin-controlled lockout: non-admins can't use the playspace mover. Clear any vertical
            // offset / flip first so a locked player can't stay floating or tipped, then bail. Admins
            // (basis.moderation.globallock) are exempt.
            if (BasisNetworkModeration.GlobalPlayspaceMoverLocked
                && BasisNetworkModeration.LocalPlayerHasGlobalLockBypass() == false)
            {
                VerticalOffset = 0f;
                HasFlip = false;
                FlipRotation = Quaternion.identity;
                Stop();
                return;
            }

            if (_hasMovementLock == false)
            {
                _movementLock = BasisLocks.GetContext(BasisLocks.Movement);
                _hasMovementLock = true;
            }

            // Publish the flip from its settings every active frame (it's independent of grabbing and
            // persists through movement lock + idle, like the vertical offset).
            UpdateFlip();

            if (_movementLock)
            {
                // Movement externally locked: stop dragging but keep the vertical offset (it's a static
                // tracking shift, so you simply stay put — opening a menu mid-air won't drop you).
                Stop();
                return;
            }

            // A hand aiming at UI means the user is driving the menu, not the play space — disable the
            // whole mover (no drag/rotate/scale/vertical) while either hand is raycasting onto a UI
            // target. Like the movement lock, keep the vertical offset + flip so opening a menu mid-air
            // doesn't drop or un-tip you (issue #874).
            if (AnyHandPointingAtUI())
            {
                Stop();
                return;
            }

            // Vertical drag turned off while lifted: settle back to the floor rather than leaving the
            // player stuck at a previous offset with no way to lower it short of Reset.
            if (BasisSettingsDefaults.PlayspaceMoverVertical.RawValue == false
                && BasisScriptedPlayerInput.VerticalActive == false)
            {
                VerticalOffset = 0f;
            }

            // Applied before the no-hands bail below so a script can drive vertical/horizontal on its
            // own without also having to synthesize a grabbing hand.
            BasisScriptedPlayerInput.ConsumeVertical(ref VerticalOffset);

            if (BasisScriptedPlayerInput.TryConsumeHorizontal(out BasisScriptedInputBlend horizontalBlend, out Vector3 horizontal))
            {
                // Additive nudges the play space; Override drives the NET drag to an absolute offset,
                // mirroring the vertical channel. Routed through Apply so it goes via the character
                // controller and still resolves walls and floors like a hand drag does.
                Vector3 drag = horizontalBlend == BasisScriptedInputBlend.Override
                    ? horizontal - _offsetPos
                    : horizontal;
                drag.y = 0f;
                if (drag.sqrMagnitude > 1e-10f)
                {
                    player.transform.GetPositionAndRotation(out Vector3 hpos, out Quaternion hrot);
                    Apply(player, hpos, hpos + drag, hrot);
                }
            }

            string handMode = BasisSettingsDefaults.PlayspaceMoverHand.RawValue;
            string mainInput = BasisSettingsDefaults.PlayspaceMoverInput.RawValue;
            string rotateInput = BasisSettingsDefaults.PlayspaceMoverRotateInput.RawValue;
            bool allowLeft = handMode != HandRight;
            bool allowRight = handMode != HandLeft;
            bool allowRotate = BasisSettingsDefaults.PlayspaceMoverRotate.RawValue;
            bool allowScale = BasisSettingsDefaults.PlayspaceMoverScale.RawValue;

            GatherHand(BasisBoneTrackedRole.LeftHand, mainInput, rotateInput, deviceDriven, out bool leftPresent, out bool leftMain, out bool leftRotate, out Vector3 leftLocal, out Vector3 leftUnscaled);
            GatherHand(BasisBoneTrackedRole.RightHand, mainInput, rotateInput, deviceDriven, out bool rightPresent, out bool rightMain, out bool rightRotate, out Vector3 rightLocal, out Vector3 rightUnscaled);

            // The rotate input is a two-handed-only gesture (yaw); translation (one hand) and scale (two
            // hands) are driven by the MAIN input. A single hand on the rotate input must not engage, or a
            // lone trigger pull (the default rotate input, and also the UI click input) drags the play
            // space. See issue #874.
            bool bothRotate = allowRotate && allowLeft && allowRight && leftPresent && rightPresent && leftRotate && rightRotate;
            bool left = (leftPresent && leftMain && allowLeft) || bothRotate;
            bool right = (rightPresent && rightMain && allowRight) || bothRotate;
            int count = (left ? 1 : 0) + (right ? 1 : 0);

            if (count == 0)
            {
                // No hand grabbing this frame. Release the grab/scale state; the vertical offset stays
                // put on its own (OVRAS-style "stay where you let go").
                Stop();
                return;
            }

            // Offset-free hand heights for vertical drag: subtract the injected VerticalOffset that the
            // device pipeline already added back into UnscaledDeviceCoord, so the measurement can't feed
            // back into itself and run the offset away.
            float leftRawY = leftUnscaled.y - VerticalOffset;
            float rightRawY = rightUnscaled.y - VerticalOffset;

            if (_grabbing == false || left != _capLeft || right != _capRight)
            {
                Capture(left, right, leftLocal, rightLocal);
                _prevLeftRawY = leftRawY;
                _prevRightRawY = rightRawY;
            }

            player.transform.GetPositionAndRotation(out Vector3 pcur, out Quaternion qcur);

            Vector3 newPos;
            Quaternion newRot;

            if (count == 1)
            {
                _scaling = false;
                CommitScaleIfPending();
                Vector3 handNow = left ? leftLocal : rightLocal;
                Vector3 handPrev = left ? _prevLeftLocal : _prevRightLocal;
                newPos = pcur + (qcur * (handPrev - handNow));
                newRot = qcur;
            }
            else
            {
                bool doRotate = allowRotate && leftRotate && rightRotate;
                bool doScale = allowScale && leftMain && rightMain && doRotate == false;

                Vector3 lNow = pcur + (qcur * leftLocal);
                Vector3 rNow = pcur + (qcur * rightLocal);
                Vector3 lPrev = pcur + (qcur * _prevLeftLocal);
                Vector3 rPrev = pcur + (qcur * _prevRightLocal);
                Vector3 midNow = (lNow + rNow) * 0.5f;
                Vector3 midPrev = (lPrev + rPrev) * 0.5f;

                Quaternion yawM = Quaternion.identity;
                if (doRotate)
                {
                    Vector3 aFlat = new Vector3(lNow.x - rNow.x, 0f, lNow.z - rNow.z);
                    Vector3 bFlat = new Vector3(lPrev.x - rPrev.x, 0f, lPrev.z - rPrev.z);
                    if (aFlat.sqrMagnitude > 1e-6f && bFlat.sqrMagnitude > 1e-6f)
                    {
                        yawM = Quaternion.FromToRotation(aFlat.normalized, bFlat.normalized);
                    }
                }

                newPos = (yawM * (pcur - midNow)) + midPrev;
                newRot = yawM * qcur;

                if (doScale)
                {
                    if (_scaling == false)
                    {
                        CaptureScaleBaseline(leftUnscaled, rightUnscaled);
                        _scaling = true;
                    }
                    ApplyScaleGesture(leftUnscaled, rightUnscaled);
                }
                else
                {
                    _scaling = false;
                    CommitScaleIfPending();
                }
            }

            // Vertical play-space drag: pulling the hand(s) down (raw Y decreasing) lifts the play space.
            // Two-handed drag uses the hand midpoint so both hands moving together raise/lower you.
            if (BasisSettingsDefaults.PlayspaceMoverVertical.RawValue)
            {
                float curRawY = count == 1 ? (left ? leftRawY : rightRawY) : (leftRawY + rightRawY) * 0.5f;
                float prevRawY = count == 1 ? (left ? _prevLeftRawY : _prevRightRawY) : (_prevLeftRawY + _prevRightRawY) * 0.5f;
                VerticalOffset += prevRawY - curRawY;
            }

            Apply(player, pcur, newPos, newRot);
            _prevLeftLocal = leftLocal;
            _prevRightLocal = rightLocal;
            _prevLeftRawY = leftRawY;
            _prevRightRawY = rightRawY;
        }

        /// <summary>
        /// Undoes the net play-space drag (returns to where the user was before dragging),
        /// leaving custom scale and normal locomotion untouched.
        /// </summary>
        public static void ResetOffset()
        {
            var player = BasisLocalPlayer.Instance;
            if (player != null && _offsetPos.sqrMagnitude > 1e-8f)
            {
                player.transform.GetPositionAndRotation(out Vector3 p, out Quaternion r);
                player.Teleport(p - _offsetPos, r);
            }
            _offsetPos = Vector3.zero;
            VerticalOffset = 0f;
            // Also clear any active flip and turn its toggle off so Reset fully returns you to normal.
            BasisSettingsDefaults.PlayspaceMoverFlip.SetValue(false);
            HasFlip = false;
            FlipRotation = Quaternion.identity;
        }

        /// <summary>
        /// Recomputes <see cref="FlipRotation"/>/<see cref="HasFlip"/> from the flip settings (toggle,
        /// angle 0-360, axis). The character controller is never rotated — only the rig's render/tracking
        /// pose, applied via <see cref="ApplyFlipToMatrix"/> and
        /// <see cref="Device_Management.Devices.BasisInput.ApplyFinalMovement"/>.
        /// </summary>
        private static void UpdateFlip()
        {
            if (BasisSettingsDefaults.PlayspaceMoverFlip.RawValue == false)
            {
                HasFlip = false;
                FlipRotation = Quaternion.identity;
                return;
            }

            float angle = BasisSettingsDefaults.PlayspaceMoverFlipAngle.RawValue;
            // ~0 or ~360 is no rotation; skip the work and the per-device/matrix transforms.
            HasFlip = Mathf.Abs(Mathf.DeltaAngle(angle, 0f)) > 0.05f;
            if (HasFlip == false)
            {
                FlipRotation = Quaternion.identity;
                return;
            }

            FlipRotation = Quaternion.AngleAxis(angle, FlipAxisVector(BasisSettingsDefaults.PlayspaceMoverFlipAxis.RawValue));
            _flipPivotY = BasisHeightDriver.SelectedScaledPlayerHeight;
        }

        private static Vector3 FlipAxisVector(string axis)
        {
            switch (axis)
            {
                case AxisPitch: return Vector3.right;   // front/back flip
                case AxisYaw: return Vector3.up;        // spin the view horizontally
                default: return Vector3.forward;        // roll / barrel (upside down)
            }
        }

        /// <summary>
        /// Bakes the active flip into a local->world matrix (used for the avatar bones). Rotates about the
        /// eye-height pivot in the player's local space so the body tips around the head, not the floor.
        /// No-op unless a flip is active.
        /// </summary>
        public static Matrix4x4 ApplyFlipToMatrix(Matrix4x4 localToWorld)
        {
            if (HasFlip == false) return localToWorld;
            Vector3 pivot = new Vector3(0f, _flipPivotY, 0f);
            Matrix4x4 flip = Matrix4x4.Translate(pivot) * Matrix4x4.Rotate(FlipRotation) * Matrix4x4.Translate(-pivot);
            return localToWorld * flip;
        }

        /// <summary>
        /// Applies the active flip to a device's final local pose (camera, controllers, trackers) so they
        /// tip in lockstep with the avatar. No-op unless a flip is active.
        /// </summary>
        public static void ApplyFlipToLocalPose(ref Vector3 localPosition, ref Quaternion localRotation)
        {
            if (HasFlip == false) return;
            Vector3 pivot = new Vector3(0f, _flipPivotY, 0f);
            localPosition = pivot + (FlipRotation * (localPosition - pivot));
            localRotation = FlipRotation * localRotation;
        }

        /// <summary>
        /// Drives the player's height from a sandboxed script. Unlike the two-hand scale gesture this is
        /// deliberately TRANSIENT: it applies live but is never written back to the CustomScale /
        /// SelectedScale settings, and the user's configured size is restored the moment the script stops
        /// driving — so an avatar can resize you without permanently editing your profile. The gesture
        /// wins while it is active.
        /// </summary>
        private static void TickScriptedScale()
        {
            bool gestureActive = _scaling;
            if (gestureActive == false && BasisScriptedPlayerInput.TryGetScale(out BasisScriptedInputBlend blend, out float height))
            {
                if (_scriptScaleDriving == false)
                {
                    _scriptScaleDriving = true;
                    _scriptScaleRestoreCustom = BasisSettingsDefaults.CustomScale.RawValue;
                    _scriptScaleRestore = _scriptScaleRestoreCustom
                        ? BasisSettingsDefaults.SelectedScale.RawValue
                        : BasisHeightDriver.SelectedUnScaledAvatarHeight;
                    if (_scriptScaleRestore < 1e-3f) _scriptScaleRestore = BasisHeightDriver.FallbackHeightInMeters;
                }

                float target = blend == BasisScriptedInputBlend.Override ? height : _scriptScaleRestore + height;
                ApplyScaleLive(true, Mathf.Clamp(target, MinHeight, MaxHeight));
                return;
            }

            if (_scriptScaleDriving)
            {
                _scriptScaleDriving = false;
                ApplyScaleLive(_scriptScaleRestoreCustom, _scriptScaleRestore);
            }
        }

        // ApplyScaleAndHeight re-runs the whole height/scale pipeline, so skip it while a script holds a
        // steady size instead of paying for it every frame.
        private static void ApplyScaleLive(bool custom, float height)
        {
            if (_scriptScaleApplied == height && SMModuleCalibration.ApplyCustomScale == custom)
            {
                return;
            }

            _scriptScaleApplied = height;
            SMModuleCalibration.ApplyCustomScale = custom;
            SMModuleCalibration.SelectedScale = height;
            BasisHeightDriver.ApplyScaleAndHeight();
        }

        private static void ApplyScaleGesture(Vector3 leftUnscaled, Vector3 rightUnscaled)
        {
            // Measure from the unscaled (pre-player-scaling) device poses so changing the scale
            // live does not feed back into the hand distance and run the scale away.
            float grabDist = (_grabLeftUnscaled - _grabRightUnscaled).magnitude;
            float curDist = (leftUnscaled - rightUnscaled).magnitude;
            if (grabDist < 1e-4f || curDist < 1e-4f) return;

            float target = Mathf.Clamp(_grabBaseHeight * (curDist / grabDist), MinHeight, MaxHeight);

            // Drive the full height/scale recompute live (same path the avatar-scale slider uses) so
            // the avatar, camera eye height, and scaled controller positions all update immediately.
            SMModuleCalibration.ApplyCustomScale = true;
            SMModuleCalibration.SelectedScale = target;
            BasisHeightDriver.ApplyScaleAndHeight();

            _pendingScaleHeight = target;
            _scaleDirty = true;
        }

        private static void CommitScaleIfPending()
        {
            if (_scaleDirty == false) return;
            _scaleDirty = false;
            BasisSettingsDefaults.CustomScale.SetValue(true);
            BasisSettingsDefaults.SelectedScale.SetValue(_pendingScaleHeight);
        }

        private static void Stop()
        {
            _scaling = false;
            CommitScaleIfPending();
            _grabbing = false;
        }

        private static void Capture(bool left, bool right, Vector3 leftLocal, Vector3 rightLocal)
        {
            _grabbing = true;
            _capLeft = left;
            _capRight = right;
            _prevLeftLocal = leftLocal;
            _prevRightLocal = rightLocal;
        }

        private static void CaptureScaleBaseline(Vector3 leftUnscaled, Vector3 rightUnscaled)
        {
            _grabLeftUnscaled = leftUnscaled;
            _grabRightUnscaled = rightUnscaled;
            _grabBaseHeight = BasisSettingsDefaults.CustomScale.RawValue
                ? BasisSettingsDefaults.SelectedScale.RawValue
                : BasisHeightDriver.SelectedUnScaledAvatarHeight;
            if (_grabBaseHeight < 1e-3f) _grabBaseHeight = BasisHeightDriver.FallbackHeightInMeters;
        }

        private static void Apply(BasisLocalPlayer player, Vector3 pcur, Vector3 newPos, Quaternion newRot)
        {
            Transform t = player.transform;
            var driver = player.LocalCharacterDriver;
            Vector3 delta = newPos - pcur;

            // The play-space drag handled here is horizontal; vertical is applied separately as a
            // tracking offset (see VerticalOffset) so it never fights gravity/grounding. The character
            // controller keeps resolving floor height, steps, and falls while dragging.
            delta.y = 0f;

            // Move through the character controller so the drag composes with normal locomotion
            // (both call Move this frame) and the controller keeps its internal position in sync.
            if (driver.characterController != null && driver.characterController.enabled)
            {
                // This runs AFTER locomotion's gravity Move, so it is the frame's LAST Move -- and
                // CharacterController.isGrounded only ever reflects the most recent Move. A purely
                // horizontal drag delta (or the ~zero delta of a stationary two-hand scale) never
                // reports "below" contact, so on its own this Move clears isGrounded; next frame's
                // GroundCheck then reads not-grounded and drops the avatar into the falling animation
                // for the entire time you drag or scale. When we're already grounded (locomotion's
                // gravity Move established it earlier this same frame) re-assert the floor contact with
                // a tiny downward stick -- the same thing gravity does every frame during normal
                // locomotion -- so grounding, and the animation state, survive the drag. It's absorbed
                // by the floor collision, so it doesn't actually lower the player. Left at zero when
                // genuinely airborne so a real fall or an in-progress jump isn't disturbed.
                if (driver.characterController.isGrounded)
                {
                    delta.y = -Mathf.Max(driver.characterController.skinWidth * 4f, 0.02f);
                }

                driver.characterController.Move(delta);
                t.rotation = newRot;
            }
            else
            {
                t.SetPositionAndRotation(newPos, newRot);
            }

            t.GetPositionAndRotation(out Vector3 finalPos, out Quaternion finalRot);
            BasisLocalPlayer.localToWorldMatrix = Matrix4x4.TRS(finalPos, finalRot, t.lossyScale);
            driver.CurrentPosition = finalPos;
            driver.CurrentRotation = finalRot;

            _offsetPos += finalPos - pcur;
        }

        private static void GatherHand(BasisBoneTrackedRole role, string mainInput, string rotateInput, bool deviceDriven, out bool present, out bool mainHeld, out bool rotateHeld, out Vector3 local, out Vector3 unscaled)
        {
            present = false;
            mainHeld = false;
            rotateHeld = false;
            local = Vector3.zero;
            unscaled = Vector3.zero;

            if (deviceDriven && BasisDeviceManagement.Instance != null)
            {
                var devices = BasisDeviceManagement.Instance.AllInputDevices;
                if (devices != null)
                {
                    foreach (BasisInput device in devices)
                    {
                        if (device == null || device.HasControl == false) continue;
                        if (device.TryGetRole(out BasisBoneTrackedRole r) == false || r != role) continue;

                        present = true;
                        // Grip is also the pickup input — while this hand is holding an interactable,
                        // don't let it drive the mover until the object is released.
                        if (IsHandHoldingObject(device))
                        {
                            mainHeld = false;
                            rotateHeld = false;
                        }
                        else
                        {
                            mainHeld = IsHeld(device.CurrentInputState, mainInput);
                            rotateHeld = IsHeld(device.CurrentInputState, rotateInput);
                        }
                        local = device.Control.OutGoingData.position;
                        unscaled = device.UnscaledDeviceCoord.position;
                        break;
                    }
                }
            }

            // Override replaces the real hand outright; Additive only fills in a hand that isn't
            // already grabbing, so the player's own grip always wins over the script's.
            if (BasisScriptedPlayerInput.TryGetHand(role == BasisBoneTrackedRole.LeftHand, out BasisScriptedInputBlend scriptBlend, out Vector3 scriptLocal, out Vector3 scriptUnscaled, out bool scriptGrab, out bool scriptRotate))
            {
                bool replace = scriptBlend == BasisScriptedInputBlend.Override
                    || (mainHeld == false && rotateHeld == false);
                if (replace)
                {
                    present = true;
                    local = scriptLocal;
                    unscaled = scriptUnscaled;
                    mainHeld = scriptGrab;
                    rotateHeld = scriptRotate;
                }
            }
        }

        private static bool IsHandHoldingObject(BasisInput device)
        {
            var interact = BasisPlayerInteract.Instance;
            if (interact == null) return false;
            var inputs = interact.InteractInputs;
            if (inputs == null) return false;

            for (int i = 0; i < inputs.Length; i++)
            {
                BasisInteractInput ii = inputs[i];
                if (ii.input == null || ii.lastTarget == null) continue;
                if (ii.IsInput(device) && ii.lastTarget.IsInteractingWith(device))
                {
                    return true;
                }
            }
            return false;
        }

        // True while either hand controller's pointer is over a UI element this frame. Mirrors the
        // HadRaycastUITarget gate the pickup/interaction system uses so the menu, not the play space, gets
        // the input. Checked for both hands regardless of hand mode — aiming at the menu with any hand
        // means you're in the menu, so the whole mover is suppressed.
        private static bool AnyHandPointingAtUI()
        {
            if (BasisDeviceManagement.Instance == null) return false;
            var devices = BasisDeviceManagement.Instance.AllInputDevices;
            if (devices == null) return false;

            foreach (BasisInput device in devices)
            {
                if (device == null || device.HasControl == false) continue;
                if (device.HasRaycaster == false || device.BasisUIRaycast == null) continue;
                if (device.BasisUIRaycast.HadRaycastUITarget == false) continue;
                if (device.TryGetRole(out BasisBoneTrackedRole r) == false) continue;
                if (r != BasisBoneTrackedRole.LeftHand && r != BasisBoneTrackedRole.RightHand) continue;
                return true;
            }
            return false;
        }

        private static bool IsHeld(BasisInputState state, string inputMode)
        {
            switch (inputMode)
            {
                case InputTrigger: return state.Trigger >= TriggerThreshold;
                case InputPrimary: return state.PrimaryButtonGetState;
                case InputSecondary: return state.SecondaryButtonGetState;
                default: return state.GripButton;
            }
        }
    }
}
