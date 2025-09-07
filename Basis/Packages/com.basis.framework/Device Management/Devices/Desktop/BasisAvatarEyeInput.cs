using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    public class BasisAvatarEyeInput : BasisInput
    {
        public Camera Camera;
        public BasisLocalAvatarDriver AvatarDriver;
        public static BasisAvatarEyeInput Instance;

        [Header("Rotation")]
        public float rotationSpeed = 1f;
        public float rotationY; // pitch (X euler)
        public float rotationX; // yaw   (Y euler)
        public float minimumY = -89f;
        public float maximumY = 80;

        [Header("Mouse/Look")]
        public Vector2 LookRotationVector = Vector2.zero;

        private readonly BasisLocks.LockContext CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);
        private readonly BasisLocks.LockContext LookRotationLock = BasisLocks.GetContext(BasisLocks.LookRotation);

        public BasisLocalVirtualSpineDriver BasisVirtualSpine = new BasisLocalVirtualSpineDriver();

        public bool HasEyeEvents = false;
        public float X;
        public float Z;
        public void Initialize(string ID = "Desktop Eye", string subSystems = "BasisDesktopManagement")
        {
            BasisDebug.Log("Initializing Avatar Eye", BasisDebug.LogTag.Input);

            if (BasisLocalPlayer.Instance.LocalAvatarDriver != null)
            {
                BasisDebug.Log("Using Configured Height " + BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerHeight, BasisDebug.LogTag.Input);
                ScaledDeviceCoord.position = new Vector3(X, BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerHeight, Z);
            }
            else
            {
                BasisDebug.Log("Using Fallback Height " + BasisLocalPlayer.FallbackSize, BasisDebug.LogTag.Input);
                ScaledDeviceCoord.position = new Vector3(X, BasisLocalPlayer.FallbackSize, Z);
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
                BasisPointRaycaster.UseWorldPosition = false;
                BasisVirtualSpine.Initialize();
                HasEyeEvents = true;
            }
        }

        private void OnCursorStateChange(CursorLockMode cursor, bool newCursorVisible)
        {
            BasisDebug.Log("cursor changed to : " + cursor + " | Cursor Visible : " + newCursorVisible, BasisDebug.LogTag.Input);
            if (cursor == CursorLockMode.Locked)
            {
                LookRotationLock.Remove(nameof(BasisCursorManagement));
            }
            else
            {
                LookRotationLock.Add(nameof(BasisCursorManagement));
            }
        }

        public new void OnDestroy()
        {
            if (HasEyeEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
                BasisCursorManagement.OnCursorStateChange -= OnCursorStateChange;
                HasEyeEvents = false;
                BasisVirtualSpine.DeInitialize();
            }
            base.OnDestroy();
        }

        public void PlayerInitialized()
        {
            BasisLocalInputActions.Instance.AvatarEyeInput = this;
            AvatarDriver = BasisLocalPlayer.Instance.LocalAvatarDriver;
            Camera = BasisLocalCameraDriver.Instance.Camera;

            BasisDeviceManagement Device = BasisDeviceManagement.Instance;
            int count = Device.BasisLockToInputs.Count;
            for (int Index = 0; Index < count; Index++)
            {
                Device.BasisLockToInputs[Index].FindRole();
            }
        }

        public new void OnDisable()
        {
            BasisLocalPlayer.OnLocalAvatarChanged -= PlayerInitialized;
            base.OnDisable();
        }

        public void SetLookRotationVector(Vector2 delta)
        {
            LookRotationVector = delta;
        }

        public void HandleLookRotation(Vector2 lookVector)
        {
            BasisPointRaycaster.ScreenPoint = Mouse.current.position.value;

            if (!isActiveAndEnabled || LookRotationLock)
            {
                return;
            }

            rotationX += lookVector.x * rotationSpeed; // yaw
            rotationY -= lookVector.y * rotationSpeed; // pitch (invert Y)
        }

        public override void DoPollData()
        {
            if (!hasRoleAssigned) return;

            if (!LookRotationVector.Equals(Vector2.zero))
            {
                HandleLookRotation(LookRotationVector);
            }

            if (BasisLocalInputActions.Instance != null)
            {
                BasisLocalInputActions.Instance.InputState.CopyTo(CurrentInputState);
            }
            // World eye (SDK helper already returns world)
            Vector3 tposeEyeWorld = BasisLocalBoneDriver.EyeControl.TposeLocalScaled.position;

            // Use HEAD as pivot (prevents big backward arc when looking up)
            Vector3 tposeHeadWorld = BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position;

            // Eye relative to HEAD (short lever)
            Vector3 neutralEyeFromHead = tposeEyeWorld - tposeHeadWorld;

            // yaw wrap
            rotationX = Mathf.Repeat(rotationX, 360f);
            // pitch clamp
            rotationY = Mathf.Clamp(rotationY, minimumY, maximumY);

            Quaternion targetRot = Quaternion.Euler(rotationY, rotationX, 0);

            // Apply crouch at pivot (use head local Y scale)
            if (!CrouchingLock)
            {
                BasisLocalPlayer Player = BasisLocalPlayer.Instance;

                var crouchMinimum = Player.LocalCharacterDriver.MinimumCrouchPercent;
                float heightAdj = (1 - crouchMinimum) * Player.LocalCharacterDriver.CrouchBlend + crouchMinimum;
                float headLocalY = BasisLocalBoneDriver.HeadControl.TposeLocalScaled.position.y;
                float crouchDelta = headLocalY * (1 - heightAdj);
                tposeHeadWorld.y -= crouchDelta;
            }

            // Rotate small head->eye vector
            Vector3 rotatedEyeOffset = targetRot * neutralEyeFromHead;
            Vector3 eyeWorld = tposeHeadWorld + rotatedEyeOffset;

            // Output
            UnscaledDeviceCoord.position = eyeWorld;
            UnscaledDeviceCoord.rotation = targetRot;

            ScaledDeviceCoord.position = UnscaledDeviceCoord.position;
            ScaledDeviceCoord.rotation = UnscaledDeviceCoord.rotation;

            ControlOnlyAsDevice();
            UpdatePlayerControl();
        }

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
