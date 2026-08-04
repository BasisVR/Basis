using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using UnityEngine;

namespace Basis.BasisUI
{
    public class BasisMenuMover : MonoBehaviour
    {
        /// <summary>
        /// Which mode the panel group uses for placement.
        /// </summary>
        public enum PanelGroupRootMode
        {
            Floating,
            World,
            Eye,
            LeftHand,       // VR Only
            RightHand,      // VR Only

            /// <summary>
            /// VR-focused: menu spawns at eye pose, then sticks to playspace movement (no head bob),
            /// using a captured playspace-local anchor.
            /// </summary>
            PlaySpaceStable,
        }

        [Serializable]
        public struct RootModeOffset
        {
            public Vector3 Position;
            public Vector3 EulerRotation;
            public float Scale;
            public Quaternion Rotation => Quaternion.Euler(EulerRotation);
        }

        [Header("References")]
        public RectTransform GroupOffset;

        [Header("Settings")]
        public PanelGroupRootMode VRMode = PanelGroupRootMode.PlaySpaceStable;
        public PanelGroupRootMode DesktopRootMode = PanelGroupRootMode.Eye;
        public PanelGroupRootMode InUse = PanelGroupRootMode.Eye;

        [Tooltip("Base UI scale (menu sizing)")]
        public float RootScale = 0.0005f;

        [Header("Offsets are multiplied against the Player Eye Height.\nAssign your values assuming a height of 1 meter.")]
        public RootModeOffset WorldOffset;
        public RootModeOffset HeadOffset;
        public RootModeOffset LeftHandOffset;
        public RootModeOffset RightHandOffset;
        public RootModeOffset FloatingOffset;

        [Header("Floating")]
        public Vector3 VRRootOffset;

        private BasisLocalBoneControl leftHandControl;
        private BasisLocalBoneControl rightHandControl;

        private bool HasCallbackForLocalCreate;
        private bool _hasLocalMoveEvent;
        private bool _moveEventOnRender;

        private const float MIN_Z_SCALE = 0.01f;
        // Degenerate-value guard ONLY — deliberately far below any playable avatar scale. The old
        // 0.055 floor (empirical TMP block-glyph limit before the atlas was mipmapped —
        // BasisTMPAtlasMipmapBaker) rendered the menu 5.5x OVERSIZED and 5.5x TOO FAR at 0.01 avatar
        // scale (anchor distance scales by the floored root too), while the hand/camera/raycast were
        // true-scale: the ray hit the right targets but the pointer swept the panel at a 5.5x
        // mismatched rate ("moving left and right but scaled by something"). The menu must stay
        // proportional to the avatar; tiny-scale text legibility is the mipmapped atlas' job.
        public const float MIN_TMP_RENDER_SCALE = 0.005f;

        private bool _hasLastEyeWrite;
        private Vector3 _lastEyeWorldPos;
        private Quaternion _lastEyeWorldRot;
        private Vector3 _lastEyeGroupPos;
        private Quaternion _lastEyeGroupRot;
        private Vector3 _lastEyeGroupScale;
        private Vector3 _lastEyeRootScale;

        // --- PlaySpaceStable state (from v1) ---
        private bool _stableHasAnchor;
        private Vector3 _stableLocalPos;
        private Quaternion _stableLocalRot = Quaternion.identity;
        private bool _hasLastStableWrite;
        private Vector3 _lastStablePos;
        private Quaternion _lastStableRot;

        private void OnEnable()
        {
            // Local player init
            if (BasisLocalPlayer.PlayerReady)
            {
                OnLocalPlayerCreated();
            }
            else
            {
                BasisLocalPlayer.OnLocalPlayerInitialized += OnLocalPlayerCreated;
                HasCallbackForLocalCreate = true;
            }
            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnAvatarHeightChange;
        }

        private void OnDisable()
        {
            if (BasisLocalPlayer.PlayerReady)
            {
                BasisLocalPlayer.Instance.OnAvatarSwitched -= OnAvatarHeightChange;
            }
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnAvatarHeightChange;

            if (HasCallbackForLocalCreate)
            {
                BasisLocalPlayer.OnLocalPlayerInitialized -= OnLocalPlayerCreated;
                HasCallbackForLocalCreate = false;
            }

            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
            if (HasCallbackForLocalCreate)
            {
                BasisLocalPlayer.OnLocalPlayerInitialized -= OnLocalPlayerCreated;
            }

            SetMovementCallback(false);
        }
        private void OnLocalPlayerCreated()
        {
            // Avatar swap + height changes
            BasisLocalPlayer.Instance.OnAvatarSwitched += OnAvatarHeightChange;
            var localbonedriver = BasisLocalPlayer.Instance.LocalBoneDriver;
            // Bone refs
            localbonedriver.FindBone(out leftHandControl, BasisBoneTrackedRole.LeftHand);
            localbonedriver.FindBone(out rightHandControl, BasisBoneTrackedRole.RightHand);
            // Apply current mode
            SetRootMode(GetFindCurrentMode());
        }

        private void OnBootModeChanged(string mode)
        {
            if (!BasisLocalPlayer.PlayerReady)
            {
                return;
            }
            BasisDebug.Log("OnBootModeChanged Menu Updating", BasisDebug.LogTag.Core);
            SetRootMode(GetFindCurrentMode());
        }

        public void OnAvatarHeightChange()
        {
            BasisDebug.Log("OnAvatarHeightChange Menu Updating", BasisDebug.LogTag.Core);
            SetRootMode(GetFindCurrentMode());
        }

        public void OnAvatarHeightChange(BasisHeightDriver.HeightModeChange change)
        {
            if (change == BasisHeightDriver.HeightModeChange.OnTpose)
            {
                return;
            }

            if (change == BasisHeightDriver.HeightModeChange.OnSitStandChanged)
            {
                // Sit/stand teleports the eye vertically: the play-space-stable anchor is now at the
                // wrong height. Do NOT re-anchor synchronously here — this callback drains early in
                // the frame, BEFORE the device poll that applies the new vertical offset, so capturing
                // now anchors at the PRE-lift camera and the menu never moves on Y. Drop the anchor
                // instead: the per-frame UpdateUILocation (AfterSimulateOnLate, post-poll) recaptures
                // at the post-lift camera the same frame; a closed menu recaptures on open.
                _stableHasAnchor = false;
                ApplyScaleOnly();
                return;
            }

            if (InUse == PanelGroupRootMode.PlaySpaceStable && _stableHasAnchor)
            {
                ApplyScaleOnly();
                return;
            }

            SetRootMode(GetFindCurrentMode());
        }

        public PanelGroupRootMode GetFindCurrentMode()
        {
            if (BasisDeviceManagement.IsUserInDesktop())
            {
                return DesktopRootMode;
            }

            if (BasisDeviceManagement.IsCurrentModeVR())
            {
                return VRMode;
            }

            return DesktopRootMode;
        }

        /// <summary>
        /// Apply the offset for the Current Root Mode.
        /// This also subscribes to the player's movement callback if needed.
        /// </summary>
        public void SetRootMode(PanelGroupRootMode mode)
        {
            InUse = mode;

            // Reset playspace-stable anchor when switching into/out of it
            if (InUse != PanelGroupRootMode.PlaySpaceStable)
            {
                _stableHasAnchor = false;
            }

            switch (InUse)
            {
                case PanelGroupRootMode.World:
                    SetMovementCallback(false);
                    SetRootOffset(WorldOffset);
                    break;

                case PanelGroupRootMode.Eye:
                    SetMovementCallback(true);
                    UpdateUILocation(PanelGroupRootMode.Eye, false);
                    break;

                case PanelGroupRootMode.LeftHand:
                    SetMovementCallback(true);
                    SetRootOffset(LeftHandOffset);
                    break;

                case PanelGroupRootMode.RightHand:
                    SetMovementCallback(true);
                    SetRootOffset(RightHandOffset);
                    break;

                case PanelGroupRootMode.Floating:
                    SetMovementCallback(false);
                    SetRootOffset(FloatingOffset);
                    UpdateUILocation(PanelGroupRootMode.Floating, false);
                    break;

                case PanelGroupRootMode.PlaySpaceStable:
                    SetMovementCallback(true);
                    SetRootOffsetForPlaySpaceStable();
                    UpdateUILocation(PanelGroupRootMode.PlaySpaceStable, true);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void SetMovementCallback(bool value)
        {
            bool onRender = InUse == PanelGroupRootMode.Eye;
            if (value == _hasLocalMoveEvent && (!value || onRender == _moveEventOnRender))
            {
                return;
            }

            if (_hasLocalMoveEvent)
            {
                if (_moveEventOnRender)
                {
                    BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(99, UpdateUILocation);
                }
                else
                {
                    BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(120, UpdateUILocation);
                }
            }

            if (value)
            {
                if (onRender)
                {
                    BasisLocalPlayer.AfterSimulateOnRender.AddAction(99, UpdateUILocation);
                }
                else
                {
                    BasisLocalPlayer.AfterSimulateOnLate.AddAction(120, UpdateUILocation);
                }
                _moveEventOnRender = onRender;
            }

            _hasLocalMoveEvent = value;
        }

        private void SetRootOffset(RootModeOffset offset)
        {
            _hasLastEyeWrite = false;

            float playerHeight = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

            GroupOffset.SetLocalPositionAndRotation(offset.Position, offset.Rotation);

            Vector3 offsetScale = Vector3.one * (offset.Scale * RootScale);
            offsetScale.z = Mathf.Max(MIN_Z_SCALE, offsetScale.z);
            GroupOffset.localScale = offsetScale;

            transform.localScale = Vector3.one * GetRenderSafeMenuScale(playerHeight);
        }

        /// <summary>
        /// Writes the head-locked menu pose, skipping the write entirely when it would land on the
        /// values already there. Returns whether anything moved, which is what decides if the
        /// menu's colliders owe PhysX a flush.
        /// </summary>
        private bool SetEyePose(Vector3 worldPosition, Quaternion worldRotation, float scaleFactor)
        {
            float playerHeight = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;

            Vector3 scaledOffset = Vector3.Scale(HeadOffset.Position, new Vector3(scaleFactor, scaleFactor, 1f));
            Quaternion offsetRotation = HeadOffset.Rotation;

            Vector3 offsetScale = Vector3.one * (HeadOffset.Scale * RootScale * scaleFactor);
            offsetScale.z = Mathf.Max(MIN_Z_SCALE, offsetScale.z);

            Vector3 rootScale = Vector3.one * GetRenderSafeMenuScale(playerHeight);

            if (_hasLastEyeWrite &&
                _lastEyeWorldPos == worldPosition &&
                _lastEyeWorldRot == worldRotation &&
                _lastEyeGroupPos == scaledOffset &&
                _lastEyeGroupRot == offsetRotation &&
                _lastEyeGroupScale == offsetScale &&
                _lastEyeRootScale == rootScale)
            {
                return false;
            }

            transform.SetPositionAndRotation(worldPosition, worldRotation);
            GroupOffset.SetLocalPositionAndRotation(scaledOffset, offsetRotation);
            GroupOffset.localScale = offsetScale;
            transform.localScale = rootScale;

            _lastEyeWorldPos = worldPosition;
            _lastEyeWorldRot = worldRotation;
            _lastEyeGroupPos = scaledOffset;
            _lastEyeGroupRot = offsetRotation;
            _lastEyeGroupScale = offsetScale;
            _lastEyeRootScale = rootScale;
            _hasLastEyeWrite = true;
            return true;
        }

        /// <summary>
        /// PlaySpaceStable distance is controlled ONLY by GroupOffset (like v1).
        /// We keep the "VR distance" default here: 0.6m forward in local space when VR.
        /// </summary>
        private void SetRootOffsetForPlaySpaceStable()
        {
            GroupOffset.SetLocalPositionAndRotation(new Vector3(0f, 0f, 0.6f), Quaternion.identity);

            ApplyScaleOnly();
        }

        private void ApplyScaleOnly()
        {
            _hasLastEyeWrite = false;

            // 1) UI group scale (menu sizing)
            Vector3 offsetScale = Vector3.one * RootScale;
            offsetScale.z = Mathf.Max(MIN_Z_SCALE, offsetScale.z);
            GroupOffset.localScale = offsetScale;

            transform.localScale = Vector3.one * GetRenderSafeMenuScale(BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale);
        }

        // Menu was designed at 80 FOV
        public const float EYE_DESIGN_FOV = 80f;
        public const float EYE_WIDTH_FIT_ASPECT = 1.2f;

        public static float GetEyeModeScaleFactor(float fieldOfView, float aspect)
        {
            float tanFOV = Mathf.Tan((Mathf.Deg2Rad * fieldOfView) / 2f);
            float tanFOVBase = Mathf.Tan((Mathf.Deg2Rad * EYE_DESIGN_FOV) / 2f);
            float scaleFactor = tanFOV / tanFOVBase;

            if (aspect > 0f && aspect < EYE_WIDTH_FIT_ASPECT)
            {
                scaleFactor *= aspect / EYE_WIDTH_FIT_ASPECT;
            }

            return scaleFactor;
        }

        public static float GetRenderSafeMenuScale(float avatarRelativeScale)
        {
            if (float.IsNaN(avatarRelativeScale) || float.IsInfinity(avatarRelativeScale) || avatarRelativeScale <= 0f)
            {
                return MIN_TMP_RENDER_SCALE;
            }

            return Mathf.Max(avatarRelativeScale, MIN_TMP_RENDER_SCALE);
        }

        private void UpdateUILocation()
        {
            UpdateUILocation(InUse, false);
        }

        private void UpdateUILocation(PanelGroupRootMode mode, bool OverrideAnchor)
        {
            if (OverrideAnchor)
            {
                _stableHasAnchor = false;
            }
            if (mode != PanelGroupRootMode.Eye)
            {
                _hasLastEyeWrite = false;
            }
            if (mode != PanelGroupRootMode.PlaySpaceStable)
            {
                _hasLastStableWrite = false;
            }
            switch (mode)
            {
                case PanelGroupRootMode.World:
                    // Static in world space; GroupOffset handles position relative to root.
                    break;

                case PanelGroupRootMode.Eye:
                    if (!BasisLocalCameraDriver.HasInstance)
                    {
                        break;
                    }
                    // Drive the screen-space-constant compensation off the LIVE camera FOV.
                    // In third-person the FOV ramps with the zoom (50–75°); the matching
                    // scaleFactor keeps the menu the same on-screen size as the user scrolls,
                    // which is the existing 1p invariant.
                    float aspect = BasisDeviceManagement.IsCurrentModeVR() ? 0f : BasisLocalCameraDriver.CameraInstance.aspect;
                    float scaleFactor = GetEyeModeScaleFactor(BasisLocalCameraDriver.CameraInstance.fieldOfView, aspect);

                    BasisLocalCameraDriver.GetPositionAndRotation(out Vector3 Position, out Quaternion Rotation);

                    if (SetEyePose(Position, Rotation, scaleFactor))
                    {
                        // Physics.autoSyncTransforms is off project-wide, so the menu's colliders would
                        // keep answering the pointer ray from where the menu used to be.
                        BasisPhysicsSyncGate.MarkColliderMoved();
                    }
                    break;

                case PanelGroupRootMode.LeftHand:
                    if (leftHandControl == null)
                    {
                        break;
                    }
                    BasisCalibratedCoords leftData = leftHandControl.OutgoingWorldData;
                    transform.SetPositionAndRotation(leftData.position, leftData.rotation);
                    break;

                case PanelGroupRootMode.RightHand:
                    if (rightHandControl == null)
                    {
                        break;
                    }
                    BasisCalibratedCoords rightData = rightHandControl.OutgoingWorldData;
                    transform.SetPositionAndRotation(rightData.position, rightData.rotation);
                    break;

                case PanelGroupRootMode.Floating:
                    if (!BasisLocalCameraDriver.HasInstance)
                    {
                        break;
                    }
                    BasisLocalCameraDriver.GetPositionAndRotation(out Vector3 CameraPosition, out Quaternion CameraRotation);
                    Quaternion floatingRotation = Quaternion.LookRotation(CameraRotation * Vector3.forward, Vector3.up);
                    transform.SetPositionAndRotation(CameraPosition + VRRootOffset, floatingRotation);
                    break;

                case PanelGroupRootMode.PlaySpaceStable:
                    UpdateUILocationPlaySpace();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        private void UpdateUILocationPlaySpace()
        {
            CaptureStableAnchorIfNeeded();

            if (!_stableHasAnchor)
            {
                return;
            }

            BasisLocalPlayer.Instance.PlayerSelf.GetPositionAndRotation(out Vector3 playPosWS, out Quaternion playRotWS);

            // Apply playspace transform to captured playspace-local anchor
            Vector3 targetPos = playPosWS + (playRotWS * _stableLocalPos);
            Quaternion targetRot = playRotWS * _stableLocalRot;

            if (_hasLastStableWrite && _lastStablePos == targetPos && _lastStableRot == targetRot)
            {
                return;
            }

            transform.SetPositionAndRotation(targetPos, targetRot);
            _lastStablePos = targetPos;
            _lastStableRot = targetRot;
            _hasLastStableWrite = true;
        }

        private static float ExtractPitchDegreesNoRoll(Quaternion localRot)
        {
            // Pitch from forward.y (roll-proof).
            Vector3 fwd = localRot * Vector3.forward;
            float pitchRad = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f));
            return pitchRad * Mathf.Rad2Deg;
        }

        // Add near the other constants / fields:
        private const float STABLE_RECENTER_TOLERANCE_MULT = 2.25f;   // how far beyond intended distance before we recenter
        private const float STABLE_RECENTER_MIN_WORLD_DIST = 0.75f;   // absolute minimum threshold (meters), avoids tiny-scale jitter
        private const float STABLE_MAX_DOWNWARD_PITCH_DEG = 30f;      // cap captured downward pitch so spawning while looking down keeps the menu off the torso

        /// <summary>
        /// Expected menu distance from the head in world meters, accounting for avatar-to-default scaling.
        /// In PlaySpaceStable, distance is controlled by GroupOffset local Z (default 0.5).
        /// Root (transform) is scaled by BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale.
        /// </summary>
        private float GetExpectedStableMenuDistanceWorld()
        {
            // GroupOffset.localPosition.z is in root-local units
            float localZ = Mathf.Abs(GroupOffset.localPosition.z);

            // Root scale converts local units -> world units.
            // In your setup, root scale is avatar-compensation (AvatarToDefaultRatioScaledWithAvatarScale).
            // Use lossyScale.z to capture actual world scaling even if hierarchy changes.
            float rootWorldScaleZ = Mathf.Abs(transform.lossyScale.z);

            // Expected world distance along forward axis
            float expected = localZ * rootWorldScaleZ;

            // Safety clamp in case scale is weird / tiny.
            return Mathf.Max(expected, 0.01f);
        }

        private void CaptureStableAnchorIfNeeded()
        {
            // If already anchored, verify we didn't drift too far away.
            if (_stableHasAnchor)
            {
                if (!BasisLocalCameraDriver.HasInstance || GroupOffset == null)
                {
                    // Can't validate; keep anchor.
                    return;
                }

                BasisLocalCameraDriver.GetPositionAndRotation(out Vector3 camPosWS, out _);

                // Measure distance from head to the actual UI group (not the root).
                float currentDist = Vector3.Distance(camPosWS, GroupOffset.position);

                // Height-aware "intended" distance.
                float expectedDist = GetExpectedStableMenuDistanceWorld();

                // Allow some tolerance; also enforce a minimum meter threshold so small avatars don't cause constant recaptures.
                float maxAllowed = Mathf.Max(expectedDist * STABLE_RECENTER_TOLERANCE_MULT, STABLE_RECENTER_MIN_WORLD_DIST);

                if (currentDist > maxAllowed)
                {
                    // Force a recapture this frame.
                    _stableHasAnchor = false;
                }
                else
                {
                    return; // anchor is valid, keep it
                }
            }

            if (!BasisLocalCameraDriver.HasInstance)
            {
                return;
            }

            BasisLocalPlayer.Instance.PlayerSelf.GetPositionAndRotation(out Vector3 playPosWS, out Quaternion playRotWS);

            // Camera pose (head/eye)
            BasisLocalCameraDriver.GetPositionAndRotation(out Vector3 camPosWS2, out Quaternion camRotWS);

            // Head rotation in playspace-local space
            Quaternion headLocal = Quaternion.Inverse(playRotWS) * camRotWS;

            float pitch = -ExtractPitchDegreesNoRoll(headLocal);
            pitch = Mathf.Min(pitch, STABLE_MAX_DOWNWARD_PITCH_DEG);

            // yaw then pitch (pitch around local X)
            Quaternion spawnLocalRotNoRoll =
                Quaternion.Euler(0f, headLocal.eulerAngles.y, 0f) *
                Quaternion.Euler(pitch, 0f, 0f);

            Quaternion spawnRotWS = playRotWS * spawnLocalRotNoRoll;

            // Place the root at the spawn pose once (then we follow playspace)
            transform.SetPositionAndRotation(camPosWS2, spawnRotWS);

            // Cache playspace-local anchor
            _stableLocalPos = Quaternion.Inverse(playRotWS) * (camPosWS2 - playPosWS);
            _stableLocalRot = spawnLocalRotNoRoll;

            _stableHasAnchor = true;
        }
    }
}
