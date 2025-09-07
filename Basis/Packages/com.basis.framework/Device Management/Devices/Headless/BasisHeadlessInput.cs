using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Headless
{
    public class BasisHeadlessInput : BasisInput
    {
        public Camera Camera;
        public BasisLocalAvatarDriver AvatarDriver;
        public static BasisHeadlessInput Instance;
        public BasisLocalVirtualSpineDriver BasisVirtualSpine = new BasisLocalVirtualSpineDriver();
        public bool HasEyeEvents = false;

        // Movement and look rotation
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
        public float respawnInterval = 300;
        public void Initialize(string ID = "Desktop Eye", string subSystems = "BasisDesktopManagement")
        {
            BasisDebug.Log("Initializing Avatar Eye", BasisDebug.LogTag.Input);

            float height = BasisLocalPlayer.Instance?.CurrentHeight.SelectedPlayerHeight ?? BasisLocalPlayer.FallbackSize;
            ScaledDeviceCoord.position = new Vector3(0, height, 0);
            ScaledDeviceCoord.rotation = Quaternion.identity;

            InitalizeTracking(ID, ID, subSystems, true, BasisBoneTrackedRole.CenterEye);

            if (BasisHelpers.CheckInstance(Instance))
                Instance = this;

            PlayerInitialized();
            if (!HasEyeEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged += PlayerInitialized;
                BasisPointRaycaster.UseWorldPosition = false;
                BasisVirtualSpine.Initialize();
                HasEyeEvents = true;
                StartCoroutine(RespawnRoutine());
            }
            
        }

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
        public void PlayerInitialized()
        {
            AvatarDriver = BasisLocalPlayer.Instance.LocalAvatarDriver;
            Camera = BasisLocalCameraDriver.Instance.Camera;

            foreach (var input in BasisDeviceManagement.Instance.BasisLockToInputs)
                input.FindRole();
        }

        public new void OnDisable()
        {
            BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
            base.OnDisable();
        }

        public override void DoPollData()
        {
            if (!hasRoleAssigned) return;


            float TimeUnscaled = Time.unscaledDeltaTime;

            timeSinceLastInputChange += TimeUnscaled;

            if (timeSinceLastInputChange >= inputChangeInterval)
            {
                RandomizeTargetInput();
                timeSinceLastInputChange = 0f;
            }

            // Smoothly lerp to target values
            currentMoveVector = Vector2.Lerp(currentMoveVector, targetMoveVector, TimeUnscaled * inputLerpSpeed);
            currentPrimary2DAxis = Vector2.Lerp(currentPrimary2DAxis, targetPrimary2DAxis, TimeUnscaled * inputLerpSpeed);
            currentSecondary2DAxis = Vector2.Lerp(currentSecondary2DAxis, targetSecondary2DAxis, TimeUnscaled * inputLerpSpeed);
            currentRotation = Quaternion.Slerp(currentRotation, targetRotation, TimeUnscaled * rotationLerpSpeed);

            // Apply to character movement
            BasisLocalPlayer.Instance.LocalCharacterDriver.SetMovementVector(currentMoveVector);
            BasisLocalPlayer.Instance.LocalCharacterDriver.UpdateMovementSpeed(false);

            // Update input state
            CurrentInputState.Trigger = Random.value > 0.5f ? 1f : 0f;
            CurrentInputState.SecondaryTrigger = Random.value > 0.5f ? 1f : 0f;
          //  CurrentInputState.PrimaryButtonGetState = Random.value > 0.5f;
          //  CurrentInputState.Secondary2DAxisClick = Random.value > 0.5f;

            CurrentInputState.Primary2DAxis = currentPrimary2DAxis;
            CurrentInputState.Secondary2DAxis = currentSecondary2DAxis;

            if(Random.value > 0.9f)
            {
                BasisLocalPlayer.Instance.LocalCharacterDriver.HandleJump();
            }

            // Rotation and position
            UnscaledDeviceCoord.rotation = currentRotation;
            float baseHeight = BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerHeight;
            Vector3 pos = new Vector3(0, baseHeight, 0);

            if (!BasisLocks.GetContext(BasisLocks.Crouching)) // No crouch lock
            {
                float crouchMin = BasisLocalPlayer.Instance.LocalCharacterDriver.MinimumCrouchPercent;
                float crouchBlend = BasisLocalPlayer.Instance.LocalCharacterDriver.CrouchBlend;
                float heightAdjust = (1f - crouchMin) * crouchBlend + crouchMin;
                pos.y -= Control.TposeLocalScaled.position.y * (1f - heightAdjust);
            }

            UnscaledDeviceCoord.position = pos;
            ScaledDeviceCoord.position = pos;
            ScaledDeviceCoord.rotation = currentRotation;

            ControlOnlyAsDevice();
            UpdatePlayerControl();
        }

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

        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F) { }

        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }
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
