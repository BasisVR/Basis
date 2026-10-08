using Unity.Scripting.LifecycleManagement;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using Basis.IK;
namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    [AutoStaticsCleanup]
    public partial class BasisDesktopEye : BasisInput
    {
        public Camera Camera;

        public static BasisDesktopEye Instance;

        [Header("Rotation")]

        public float rotationPitch;

        public float rotationYaw;

        public float minimumPitch = -89f;

        public float maximumPitch = 80;

        [Header("Mouse/Look")]
        public Vector2 LookRotationVector = Vector2.zero;
        private readonly BasisLocks.LockContext LookRotationLock = BasisLocks.GetContext(BasisLocks.LookRotation);

        public bool HasEyeEvents = false;

        public float X;

        public float Z;

        [Header("Reticle")]
        public BasisDesktopReticle Reticle = new BasisDesktopReticle();

        public void Initialize(string ID = "Desktop Eye", string subSystems = "BasisDesktopManagement")
        {
            BasisDebug.Log("Initializing Avatar Eye", BasisDebug.LogTag.Input);

            if (BasisLocalPlayer.Instance.LocalAvatarDriver != null)
            {
                BasisDebug.Log($"Using Configured Height {BasisHeightDriver.SelectedScaledPlayerHeight}", BasisDebug.LogTag.Input);
                ScaledDeviceCoord.position = new Vector3(X, BasisHeightDriver.SelectedScaledPlayerHeight, Z);
            }
            else
            {
                BasisDebug.Log($"Using Fallback Height {BasisHeightDriver.FallbackHeightInMeters}", BasisDebug.LogTag.Input);
                ScaledDeviceCoord.position = new Vector3(X, BasisHeightDriver.FallbackHeightInMeters, Z);
            }

            ScaledDeviceCoord.rotation = Quaternion.identity;

            TrackingHardware = BasisTrackingHardware.Simulated;
            InitializeTracking(ID, ID, subSystems, true, BasisBoneTrackedRole.CenterEye);

            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }

            PlayerInitialized();

            if (HasEyeEvents == false)
            {
                BasisLocalPlayer.OnLocalAvatarChanged += PlayerInitialized;
                BasisCursorManagement.OnCursorStateChange += OnCursorStateChange;
                if (HasRaycaster)
                {
                    BasisPointRaycaster.UseWorldPosition = false;
                }
                BasisLocalPlayer.AfterSimulateOnRender.AddAction(100, DoRenderRaycast);
                HasEyeEvents = true;
            }
            LockEye();

            // SetEnabled lazy-inits
            Reticle?.SetEnabled(BasisSettingsDefaults.DesktopReticle.RawValue);
        }
        public void LockEye()
        {
            LookRotationLock.Clear();
            BasisCursorManagement.LockCursor(nameof(BasisDesktopEye));
            // If cursor didn't actually lock (e.g. stale unlock requests), block rotation
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                LookRotationLock.Add(nameof(BasisCursorManagement));
            }
        }

        private void OnCursorStateChange(CursorLockMode cursor, bool newCursorVisible)
        {
            BasisDebug.Log("cursor changed to : " + cursor + " | Cursor Visible : " + newCursorVisible, BasisDebug.LogTag.Input);
            switch (cursor)
            {
                case CursorLockMode.Locked:
                    LookRotationLock.Remove(nameof(BasisCursorManagement));
                    break;
                case CursorLockMode.Confined:
                    LookRotationLock.Add(nameof(BasisCursorManagement));
                    break;
                case CursorLockMode.None:
                    LookRotationLock.Add(nameof(BasisCursorManagement));
                    break;
                default:
                    LookRotationLock.Add(nameof(BasisCursorManagement));
                    break;
            }

            Reticle?.SetFocused(cursor == CursorLockMode.Locked);
        }

        public new void OnDestroy()
        {
            if (HasEyeEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
                BasisCursorManagement.OnCursorStateChange -= OnCursorStateChange;
                BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(100, DoRenderRaycast);
                HasEyeEvents = false;
                LookRotationLock.Remove(nameof(BasisCursorManagement));
            }
            // Reticle quad is parented under the camera (which outlives this GO),
            // so we have to tear it down explicitly here.
            Reticle?.Destroy();
            base.OnDestroy();
        }

        public void PlayerInitialized()
        {
            BasisLocalInputActions.DesktopEyeInput = this;
            Camera = BasisLocalCameraDriver.Instance.Camera;

            BasisDeviceManagement Device = BasisDeviceManagement.Instance;
            int count = Device.BasisLockToInputs.Count;
            for (int Index = 0; Index < count; Index++)
            {
                Device.BasisLockToInputs[Index].FindRole();
            }
        }

        public void OnDisable()
        {
            BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
        }

        public void SetLookRotationVector(Vector2 delta)
        {
            LookRotationVector = delta;
        }

        public void HandleLookRotation(Vector2 lookVector)
        {
            if (!isActiveAndEnabled || LookRotationLock)
            {
                return;
            }

            rotationYaw += lookVector.x * SMModuleControllerSettings.MouseSensitivty; // yaw
            rotationPitch -= lookVector.y * SMModuleControllerSettings.MouseSensitivty; // pitch (invert Y)
        }

        public override void LateDoPollData()
        {
            if (!hasRoleAssigned)
            {
                return;
            }

            if (HasRaycaster)
            {
                BasisPointRaycaster.ScreenPoint = BasisLocalInputActions.Pointer;
            }

            if (!LookRotationVector.Equals(Vector2.zero))
            {
                HandleLookRotation(LookRotationVector);
            }

            if (BasisLocalInputActions.InputState != null)
            {
                BasisLocalInputActions.InputState.CopyTo(CurrentInputState);
            }

            // Eye relative position. Read TposeLocal (unscaled) rather than
            // TposeLocalScaled here: UnscaledDeviceCoord is the playspace
            // pre-scale field the constellation classifier and CalculatePlayerEyeHeight
            // read from, and the calibration system is supposed to operate in
            // real-world / human scale only. Writing the avatar-scaled eye height
            // into UnscaledDeviceCoord makes CapturePlayerHeight adopt it as
            // PlayerEyeHeight, which feeds back into ScaledToMatchValue, which
            // re-scales the avatar even larger next frame — divergent loop that
            // ends with player heights of 12 m and tracker ratios above 2.
            // DeviceScale projects the unscaled pose onto the avatar below.
            Vector3 tposeEyeWorld = BasisLocalBoneDriver.EyeControl.TposeLocal.position;
            Vector3 tposeHeadWorld = BasisLocalBoneDriver.HeadControl.TposeLocal.position;
            Vector3 neutralEyeFromHead = tposeEyeWorld - tposeHeadWorld;

            // Apply yaw/pitch with clamping
            rotationYaw = Mathf.Repeat(rotationYaw, 360f);
            rotationPitch = Mathf.Clamp(rotationPitch, minimumPitch, maximumPitch);
            Quaternion targetRot = Quaternion.Euler(rotationPitch, rotationYaw, 0);

            // Handle crouching/prone adjustment — always apply the current stance visual
            // offset. The CrouchingLock only prevents *input* from changing CrouchBlend;
            // skipping this block when locked caused the camera to snap to standing height
            // while the player was still crouched (see issue #637).
            {
                BasisLocalPlayer Player = BasisLocalPlayer.Instance;
                float heightAdj = Player.LocalCharacterDriver.GetStanceHeightPercent();
                // Match the space of tposeHeadWorld above (TposeLocal, pre-scale)
                // so the subtraction is dimensionally consistent.
                float headLocalY = BasisLocalBoneDriver.HeadControl.TposeLocal.position.y;
                float crouchDelta = headLocalY * (1 - heightAdj);
                tposeHeadWorld.y -= crouchDelta;
            }

            // Rotate and compute final eye position
            Vector3 rotatedEyeOffset = targetRot * neutralEyeFromHead;
            Vector3 eyeWorld = tposeHeadWorld + rotatedEyeOffset;

            // Pin the eye over the hips. This is what stops the eye's static forward offset ORBITING the neck
            // every time you turn — yaw would otherwise sweep the camera sideways through an arc. The pin is
            // right; what it also threw away was the pitch swing, which is put back immediately below.
            eyeWorld.x = X;
            eyeWorld.z = Z;

            // The head pitches about the BASE OF THE NECK, not about itself, so the eye rides a lever arm and is
            // carried FORWARD as the gaze tips down — look at your own feet and your eyes travel out over them.
            // VR gets that for free (the HMD is a physical object on the same lever arm; the spine bend even has
            // to SUPPRESS the resulting hunch when a chest tracker is present). Desktop had none of it: the gaze
            // tipped, BasisCervicalSolveCore hunched the chest to follow, and the head stayed pinned over the
            // hips — so the neck absorbed a translation the head should have made itself.
            //
            // Horizontal only, and that is not a shortcut: eye HEIGHT feeds CapturePlayerHeight through
            // UnscaledDeviceCoord.y (see the note above), and a pitch-varying eye height would drive it straight
            // into the avatar-rescale loop. The forward carry is the piece that was missing and the piece that is
            // safe to add.
            if (Basis.BasisUI.BasisSettingsDefaults.DesktopHeadSwingEnabled.RawValue &&
                BasisLocalBoneDriver.NeckControl != null)
            {
                BasisHeadPitchSwingCore.Solve(rotationPitch, rotationYaw,
                    tposeEyeWorld - BasisLocalBoneDriver.NeckControl.TposeLocal.position,
                    Basis.BasisUI.BasisSettingsDefaults.DesktopHeadSwingStrength.RawValue,
                    Basis.BasisUI.BasisSettingsDefaults.DesktopHeadSwingBackward.RawValue,
                    out Vector3 swingOffset, out _);
                eyeWorld += swingOffset;
            }

            // Output transforms
            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, eyeWorld);
            UnscaledDeviceCoord.rotation = targetRot;

            float deviceScale = BasisHeightDriver.AppliedUpScale;
            ScaledDeviceCoord.rotation = OffsetCoords.rotation * UnscaledDeviceCoord.rotation;
            ScaledDeviceCoord.position = OffsetCoords.position + (OffsetCoords.rotation * (UnscaledDeviceCoord.position * deviceScale));

            ControlOnlyAsDevice();
            if (IsComputingRaycast)
            {
                ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);
                UpdateInputEvents(HasPlayerControlSupport: true, hasPlayerRaycastSupport: false); // control raycast
            }
        }
        private void DoRenderRaycast()
        {
            if (!hasRoleAssigned || !IsComputingRaycast)
            {
                return;
            }

            UpdateInputEvents(HasPlayerControlSupport: false, hasPlayerRaycastSupport: true); // ui raycast
        }
        public bool IsComputingRaycast = true;
        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null)
            {
                DeviceSupportInformation Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
                if (Match.CanDisplayPhysicalTracker)
                {
                    LoadModelWithKey(Match.DeviceID);
                }
                else
                {
                    if (UseFallbackModel())
                    {
                        LoadModelWithKey(FallbackDeviceID);
                    }
                }
            }
        }

        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
        }

        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }

    }
}
