using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Headless
{
    /// <summary>
    /// Headless input driver that simulates a CenterEye device for server/headless builds.
    /// Generates gentle movement/look input and periodically respawns the player.
    /// </summary>
    public class BasisHeadlessInput : BasisInput
    {
        /// <summary>Reference to the local player camera (if present in headless).</summary>
        public Camera Camera;

        /// <summary>Local avatar driver for pose/scale info.</summary>
        public BasisLocalAvatarDriver AvatarDriver;

        /// <summary>Singleton-like access to the active headless input.</summary>
        public static BasisHeadlessInput Instance;

        /// <summary>Virtual spine helper for headless posture.</summary>
        public BasisLocalVirtualSpineDriver BasisVirtualSpine = new BasisLocalVirtualSpineDriver();

        /// <summary>Whether events for eye/local avatar have been wired.</summary>
        public bool HasEyeEvents = false;

        // --- Simulated inputs (smoothed towards randomized targets) ---

        private Vector2 currentMoveVector = Vector2.zero;
        private Vector2 targetMoveVector = Vector2.zero;

        private Vector2 currentPrimary2DAxis = Vector2.zero;
        private Vector2 targetPrimary2DAxis = Vector2.zero;

        private Vector2 currentSecondary2DAxis = Vector2.zero;
        private Vector2 targetSecondary2DAxis = Vector2.zero;

        private Quaternion currentRotation = Quaternion.identity;
        private Quaternion targetRotation = Quaternion.identity;

        private float rotationLerpSpeed = 1.5f;
        private float inputLerpSpeed = 3f;
        private float timeSinceLastInputChange = 0f;
        private float inputChangeInterval = 2f;

        /// <summary>Seconds between automatic player respawns.</summary>
        public float respawnInterval = 300;

        /// <summary>
        /// Initializes a headless, CenterEye-style input and hooks events.
        /// </summary>
        /// <param name="ID">Logical device ID shown in logs.</param>
        /// <param name="subSystems">Subsystem identifier string.</param>
        public void Initialize(string ID = "Desktop Eye", string subSystems = "BasisDesktopManagement")
        {
            BasisDebug.Log("Initializing Avatar Eye", BasisDebug.LogTag.Input);

            float height = BasisLocalPlayer.Instance?.CurrentHeight.SelectedPlayerHeight ?? BasisLocalPlayer.FallbackSize;

            // Start at eye height, forward facing
            ScaledDeviceCoord.position = new Vector3(0, height, 0);
            ScaledDeviceCoord.rotation = Quaternion.identity;

            InitalizeTracking(ID, ID, subSystems, true, BasisBoneTrackedRole.CenterEye);

            if (BasisHelpers.CheckInstance(Instance))
                Instance = this;

            PlayerInitialized();

            if (!HasEyeEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged += PlayerInitialized;
                // In headless, ray origin is device-space (not world head)
                BasisPointRaycaster.UseWorldPosition = false;

                BasisVirtualSpine.Initialize();
                HasEyeEvents = true;

                // Auto-respawn loop
                StartCoroutine(RespawnRoutine());
            }
        }

        /// <summary>
        /// Unhooks event handlers and deinitializes virtual spine.
        /// </summary>
        public new void OnDestroy()
        {
            if (HasEyeEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
                HasEyeEvents = false;
                BasisVirtualSpine.DeInitialize();
            }
            base.OnDestroy();
        }

        /// <summary>
        /// Refreshes cached references after avatar changes.
        /// </summary>
        public void PlayerInitialized()
        {
            AvatarDriver = BasisLocalPlayer.Instance.LocalAvatarDriver;
            Camera = BasisLocalCameraDriver.Instance.Camera;

            // Re-run lock-to-input find (for camera mount points etc.)
            foreach (var input in BasisDeviceManagement.Instance.BasisLockToInputs)
                input.FindRole();
        }

        /// <summary>
        /// Ensures cleanup on disable.
        /// </summary>
        public new void OnDisable()
        {
            BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
            base.OnDisable();
        }

        /// <summary>
        /// Main per-frame simulation: smooth random inputs, update movement/pose,
        /// and write into <see cref="BasisInputState"/> + bone control.
        /// </summary>
        public override void DoPollData()
        {
            if (!hasRoleAssigned) return;

            float dt = Time.unscaledDeltaTime;
            timeSinceLastInputChange += dt;

            // Occasionally pick a new target input/rotation
            if (timeSinceLastInputChange >= inputChangeInterval)
            {
                RandomizeTargetInput();
                timeSinceLastInputChange = 0f;
            }

            // Smoothly lerp to targets
            currentMoveVector = Vector2.Lerp(currentMoveVector, targetMoveVector, dt * inputLerpSpeed);
            currentPrimary2DAxis = Vector2.Lerp(currentPrimary2DAxis, targetPrimary2DAxis, dt * inputLerpSpeed);
            currentSecondary2DAxis = Vector2.Lerp(currentSecondary2DAxis, targetSecondary2DAxis, dt * inputLerpSpeed);
            currentRotation = Quaternion.Slerp(currentRotation, targetRotation, dt * rotationLerpSpeed);

            // Apply to character movement (walk, never sprint)
            BasisLocalPlayer.Instance.LocalCharacterDriver.SetMovementVector(currentMoveVector);
            BasisLocalPlayer.Instance.LocalCharacterDriver.UpdateMovementSpeed(false);

            // Update input state with light random triggers and axes
            CurrentInputState.Trigger = Random.value > 0.5f ? 1f : 0f;
            CurrentInputState.SecondaryTrigger = Random.value > 0.5f ? 1f : 0f;
            // If needed in the future:
            // CurrentInputState.PrimaryButtonGetState = ...
            // CurrentInputState.Secondary2DAxisClick  = ...

            CurrentInputState.Primary2DAxis = currentPrimary2DAxis;
            CurrentInputState.Secondary2DAxis = currentSecondary2DAxis;

            // Rarely trigger a jump to keep animation states active
            if (Random.value > 0.9f)
            {
                BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJump();
            }

            // Position/rotation at eye height with crouch compensation
            UnscaledDeviceCoord.rotation = currentRotation;
            float baseHeight = BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerHeight;
            Vector3 pos = new Vector3(0, baseHeight, 0);

            if (!BasisLocks.GetContext(BasisLocks.Crouching)) // no crouch lock
            {
                float crouchMin = BasisLocalPlayer.Instance.LocalCharacterDriver.MinimumCrouchPercent;
                float crouchBlend = BasisLocalPlayer.Instance.LocalCharacterDriver.CrouchBlend;
                float heightAdjust = (1f - crouchMin) * crouchBlend + crouchMin;

                // Lower eye by the scaled head height amount based on crouch blend
                pos.y -= Control.TposeLocalScaled.position.y * (1f - heightAdjust);
            }

            UnscaledDeviceCoord.position = pos;
            ScaledDeviceCoord.position = pos;
            ScaledDeviceCoord.rotation = currentRotation;

            // Drive our CenterEye bone
            ControlOnlyAsDevice();
            UpdatePlayerControl();
        }

        /// <summary>
        /// Picks a new random movement/axis set and orientation to lerp toward.
        /// </summary>
        private void RandomizeTargetInput()
        {
            targetMoveVector = new Vector2(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            ).normalized;

            targetPrimary2DAxis = new Vector2(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            );

            targetSecondary2DAxis = new Vector2(
                Random.Range(-1f, 1f),
                Random.Range(-1f, 1f)
            );

            targetRotation = Quaternion.Euler(
                Random.Range(-30f, 30f),   // Pitch
                Random.Range(0f, 360f),    // Yaw
                Random.Range(-10f, 10f)    // Roll
            );
        }

        /// <summary>
        /// Shows either a matched device model or a simple fallback visualization.
        /// </summary>
        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker != null) return;

            DeviceSupportInformation match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
            if (match.CanDisplayPhysicalTracker)
            {
                LoadModelWithKey(match.DeviceID);
            }
            else if (UseFallbackModel())
            {
                LoadModelWithKey(FallbackDeviceID);
            }
        }

        /// <summary>No haptics in headless.</summary>
        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F) { }

        /// <summary>Routes UI sounds to common default implementation.</summary>
        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }

        /// <summary>
        /// Periodically respawns the player to reset state in long-running headless sessions.
        /// </summary>
        private IEnumerator RespawnRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(respawnInterval);
                BasisSceneFactory.SpawnPlayer(BasisLocalPlayer.Instance);
            }
        }
    }
}
