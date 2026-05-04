using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    /// <summary>
    /// Provides simulated eye-tracking input for desktop mode.
    /// Handles look rotation via mouse input, device initialization, and integration with avatar drivers.
    /// </summary>
    public class BasisDesktopEye : BasisInput
    {
        /// <summary>
        /// Reference to the active Unity <see cref="Camera"/> used for eye input simulation.
        /// </summary>
        public Camera Camera;

        /// <summary>
        /// Singleton instance for global access to the desktop eye input.
        /// </summary>
        public static BasisDesktopEye Instance;

        [Header("Rotation")]

        /// <summary>
        /// Current pitch rotation (X axis).
        /// </summary>
        public float rotationPitch;

        /// <summary>
        /// Current yaw rotation (Y axis).
        /// </summary>
        public float rotationYaw;

        /// <summary>
        /// Minimum clamped pitch angle.
        /// </summary>
        public float minimumPitch = -89f;

        /// <summary>
        /// Maximum clamped pitch angle.
        /// </summary>
        public float maximumPitch = 80;

        [Header("Mouse/Look")]
        /// <summary>
        /// Stores look input delta from the mouse or input system.
        /// </summary>
        public Vector2 LookRotationVector = Vector2.zero;
        private readonly BasisLocks.LockContext LookRotationLock = BasisLocks.GetContext(BasisLocks.LookRotation);

        /// <summary>
        /// Local virtual spine driver for applying pose adjustments in desktop mode.
        /// </summary>
        public BasisLocalVirtualSpineDriver BasisVirtualSpine = new BasisLocalVirtualSpineDriver();

        /// <summary>
        /// Tracks whether eye-related event subscriptions are active.
        /// </summary>
        public bool HasEyeEvents = false;

        /// <summary>
        /// Local X position offset for the simulated eye.
        /// </summary>
        public float X;

        /// <summary>
        /// Local Z position offset for the simulated eye.
        /// </summary>
        public float Z;

        /// <summary>
        /// Screen-center aim reticle. The quad lives under the local camera so it
        /// tracks aim for free; lifecycle is driven by <see cref="Initialize"/> and
        /// <see cref="OnDestroy"/>. Toggle via <c>Reticle.SetEnabled(bool)</c>.
        /// </summary>
        [Header("Reticle")]
        public BasisDesktopReticle Reticle = new BasisDesktopReticle();

        /// <summary>
        /// Initializes the eye input system for desktop usage.
        /// Sets device coordinates, hooks into player events, and prepares tracking roles.
        /// </summary>
        /// <param name="ID">Identifier for this input device (default "Desktop Eye").</param>
        /// <param name="subSystems">Name of the subsystem responsible for initialization (default "BasisDesktopManagement").</param>
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

            InitalizeTracking(ID, ID, subSystems, true, BasisBoneTrackedRole.CenterEye);

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
                BasisVirtualSpine.Initialize();
                HasEyeEvents = true;
            }
            LockEye();

            Reticle?.Initialize();
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

        /// <summary>
        /// Handles updates when the cursor state changes (locked or free).
        /// Adjusts <see cref="LookRotationLock"/> accordingly.
        /// </summary>
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
        }

        /// <summary>
        /// Cleans up event subscriptions and deinitializes the virtual spine when destroyed.
        /// </summary>
        public new void OnDestroy()
        {
            if (HasEyeEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
                BasisCursorManagement.OnCursorStateChange -= OnCursorStateChange;
                HasEyeEvents = false;
                BasisVirtualSpine.DeInitialize();
            }
            // Reticle quad is parented under the camera (which outlives this GO),
            // so we have to tear it down explicitly here.
            Reticle?.Destroy();
            base.OnDestroy();
        }

        /// <summary>
        /// Re-initializes player-specific references (camera, avatar driver, and input bindings).
        /// Called on local avatar change.
        /// </summary>
        public void PlayerInitialized()
        {
            BasisLocalInputActions.Instance.DesktopEyeInput = this;
            Camera = BasisLocalCameraDriver.Instance.Camera;

            BasisDeviceManagement Device = BasisDeviceManagement.Instance;
            int count = Device.BasisLockToInputs.Count;
            for (int Index = 0; Index < count; Index++)
            {
                Device.BasisLockToInputs[Index].FindRole();
            }
        }

        /// <summary>
        /// Removes avatar change subscriptions when disabled.
        /// </summary>
        public void OnDisable()
        {
            BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
        }

        /// <summary>
        /// Updates the look rotation input vector.
        /// Called externally by input actions.
        /// </summary>
        /// <param name="delta">Mouse or input delta vector.</param>
        public void SetLookRotationVector(Vector2 delta)
        {
            LookRotationVector = delta;
        }

        /// <summary>
        /// Applies yaw/pitch rotation based on the given input vector.
        /// Handles mouse-look simulation for the eye.
        /// Note: This is relative to the player's non-head rotation. The final camera rotation is that, combined with this eye rotation.
        /// </summary>
        /// <param name="lookVector">Delta vsector from input system.</param>
        public void HandleLookRotation(Vector2 lookVector)
        {
            if (!isActiveAndEnabled || LookRotationLock)
            {
                return;
            }

            rotationYaw += lookVector.x * SMModuleControllerSettings.MouseSensitivty; // yaw
            rotationPitch -= lookVector.y * SMModuleControllerSettings.MouseSensitivty; // pitch (invert Y)
        }

        /// <summary>
        /// Main polling loop for updating eye input state.
        /// Calculates eye position/rotation based on avatar head, crouching, and inputs deltas.
        /// </summary>
        public override void LateDoPollData()
        {
            if (!hasRoleAssigned)
            {
                return;
            }

            if (HasRaycaster)
            {
                BasisPointRaycaster.ScreenPoint = BasisLocalInputActions.Instance.Pointer;
            }

            if (!LookRotationVector.Equals(Vector2.zero))
            {
                HandleLookRotation(LookRotationVector);
            }

            if (BasisLocalInputActions.Instance != null)
            {
                BasisLocalInputActions.Instance.InputState.CopyTo(CurrentInputState);
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

            // Handle crouching adjustment — always apply the current crouch visual
            // offset. The CrouchingLock only prevents *input* from changing CrouchBlend;
            // skipping this block when locked caused the camera to snap to standing height
            // while the player was still crouched (see issue #637).
            {
                BasisLocalPlayer Player = BasisLocalPlayer.Instance;
                var crouchMinimum = Player.LocalCharacterDriver.MinimumCrouchPercent;
                float heightAdj = (1 - crouchMinimum) * Player.LocalCharacterDriver.CrouchBlend + crouchMinimum;
                // Match the space of tposeHeadWorld above (TposeLocal, pre-scale)
                // so the subtraction is dimensionally consistent.
                float headLocalY = BasisLocalBoneDriver.HeadControl.TposeLocal.position.y;
                float crouchDelta = headLocalY * (1 - heightAdj);
                tposeHeadWorld.y -= crouchDelta;
            }

            // Rotate and compute final eye position
            Vector3 rotatedEyeOffset = targetRot * neutralEyeFromHead;
            Vector3 eyeWorld = tposeHeadWorld + rotatedEyeOffset;

            eyeWorld.x = X;
            eyeWorld.z = Z;
            // Output transforms
            ComputeUnscaledDeviceCoord(ref UnscaledDeviceCoord, eyeWorld);
            UnscaledDeviceCoord.rotation = targetRot;

            // Apply DeviceScale when projecting unscaled → scaled so the camera
            // lands at the avatar's actual world head position (avatar may be
            // scaled relative to the player). This matches BasisInput's base
            // ConvertToScaledDeviceCoord. Skipping DeviceScale would force the
            // previous version to write avatar-scaled positions directly into
            // UnscaledDeviceCoord, restarting the calibration feedback loop.
            float deviceScale = BasisHeightDriver.DeviceScale;
            ScaledDeviceCoord.rotation = OffsetCoords.rotation * UnscaledDeviceCoord.rotation;
            ScaledDeviceCoord.position = OffsetCoords.position + (OffsetCoords.rotation * (UnscaledDeviceCoord.position * deviceScale));

            ControlOnlyAsDevice();
            if (IsComputingRaycast)
            {
                ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);
                UpdateInputEvents();
            }
        }
        public bool IsComputingRaycast = true;
        /// <summary>
        /// Displays a visual tracker for the device if supported by the matched device definition.
        /// Falls back to a generic model if needed.
        /// </summary>
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

        /// <summary>
        /// Plays a haptic effect.
        /// Not implemented for desktop eye input.
        /// </summary>
        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
        }

        /// <summary>
        /// Plays a sound effect for the input device.
        /// Uses the default implementation.
        /// </summary>
        /// <param name="SoundEffectName">The sound effect key or name.</param>
        /// <param name="Volume">Volume level for playback.</param>
        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }

    }
}
