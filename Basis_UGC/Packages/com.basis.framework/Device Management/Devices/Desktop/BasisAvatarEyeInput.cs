using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;

namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    public class BasisAvatarEyeInput : BasisInput
    {
        public Camera Camera;
        public BasisLocalAvatarDriver AvatarDriver;
        public static BasisAvatarEyeInput Instance;
        public float rotationSpeed = 1f;
        public float rotationY;
        public float rotationX;
        public float minimumY = -89f;
        public float maximumY = 50f;
        public float InjectedX = 0;
        public float InjectedZ = 0;
        public bool HasEyeEvents = false;
        public float InjectedZRot = 0;

        public Vector2 LookRotationVector = Vector2.zero;

        private readonly BasisLocks.LockContext CrouchingLock = BasisLocks.GetContext(BasisLocks.Crouching);
        private readonly BasisLocks.LockContext LookRotationLock = BasisLocks.GetContext(BasisLocks.LookRotation);

        public void Initialize(string ID = "Desktop Eye", string subSystems = "BasisDesktopManagement")
        {
            BasisDebug.Log("Initializing Avatar Eye", BasisDebug.LogTag.Input);
            if (BasisLocalPlayer.Instance.LocalAvatarDriver != null)
            {
                BasisDebug.Log("Using Configured Height " + BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerHeight, BasisDebug.LogTag.Input);
                LocalRawPosition = new Vector3(InjectedX, BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerHeight, InjectedZ);
                LocalRawRotation = Quaternion.identity;
            }
            else
            {
                BasisDebug.Log("Using Fallback Height " + BasisLocalPlayer.FallbackSize, BasisDebug.LogTag.Input);
                LocalRawPosition = new Vector3(InjectedX, BasisLocalPlayer.FallbackSize, InjectedZ);
                LocalRawRotation = Quaternion.identity;
            }
            TransformFinalPosition = LocalRawPosition;
            TransformFinalRotation = LocalRawRotation;
            InitalizeTracking(ID, ID, subSystems, true, BasisBoneTrackedRole.CenterEye);
            if (BasisHelpers.CheckInstance(Instance))
            {
                Instance = this;
            }
            PlayerInitialized();
            BasisCursorManagement.OverrideAbleLock(nameof(BasisAvatarEyeInput));
            if (HasEyeEvents == false)
            {
                BasisLocalPlayer.Instance.OnLocalAvatarChanged += PlayerInitialized;
                BasisLocalPlayer.Instance.OnPlayersHeightChanged += OnPlayersHeightChanged;
                // BasisLocalPlayer.Instance.LocalAvatarDriver.CalibrationComplete += OnCalibration;
                OnPlayersHeightChanged();
                BasisCursorManagement.OnCursorStateChange += OnCursorStateChange;
                BasisPointRaycaster.UseWorldPosition = false;
                BasisVirtualSpine.Initialize();
                HasEyeEvents = true;
            }
            //  OnCalibration();
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
                BasisLocalPlayer.Instance.OnLocalAvatarChanged -= PlayerInitialized;
                BasisLocalPlayer.Instance.OnPlayersHeightChanged -= OnPlayersHeightChanged;
                BasisCursorManagement.OnCursorStateChange -= OnCursorStateChange;
                //  BasisLocalPlayer.Instance.LocalAvatarDriver.CalibrationComplete -= OnCalibration;
                HasEyeEvents = false;

                BasisVirtualSpine.DeInitialize();
            }
            base.OnDestroy();
        }
        private void OnPlayersHeightChanged()
        {
            BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight = BasisLocalPlayer.Instance.CurrentHeight.AvatarEyeHeight;
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
            BasisLocalPlayer.Instance.OnLocalAvatarChanged -= PlayerInitialized;
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
            rotationX += lookVector.x * rotationSpeed;
            rotationY -= lookVector.y * rotationSpeed;
        }
        public override void DoPollData()
        {
            if (hasRoleAssigned)
            {
                if (!LookRotationVector.Equals(Vector2.zero))
                    HandleLookRotation(LookRotationVector);
                if (BasisLocalInputActions.Instance != null)
                {
                    BasisLocalInputActions.Instance.InputState.CopyTo(CurrentInputState);
                }
                // InputState.CopyTo(characterInputActions.InputState);
                // Apply modulo operation to keep rotation within 0 to 360 range
                rotationX %= 360f;
                rotationY %= 360f;
                // Clamp rotationY to stay within the specified range
                rotationY = Mathf.Clamp(rotationY, minimumY, maximumY);
                LocalRawRotation = Quaternion.Euler(rotationY, rotationX, InjectedZRot);
                Vector3 adjustedHeadPosition = new Vector3(InjectedX, BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight, InjectedZ);
                if (!CrouchingLock)
                {
                    // adjustment is 0-1 interpolated between configurable normalized minimum and the max avatar height
                    // crouch minimum is a percent amount of the max height
                    var crouchMinimum = BasisLocalPlayer.Instance.LocalCharacterDriver.MinimumCrouchPercent;
                    float heightAdjustment = (1 - crouchMinimum) * BasisLocalPlayer.Instance.LocalCharacterDriver.CrouchBlend + crouchMinimum;
                    // crouch is calculated from the ground up, so invert to move it to the avatar height context
                    adjustedHeadPosition.y -= Control.TposeLocal.position.y * (1 - heightAdjustment);
                }
                LocalRawPosition = adjustedHeadPosition;
                Control.IncomingData.position = LocalRawPosition;
                Control.IncomingData.rotation = LocalRawRotation;
                TransformFinalPosition = LocalRawPosition;
                TransformFinalRotation = LocalRawRotation;
                UpdatePlayerControl();
            }
        }
        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null && LoadedDeviceRequest == null)
            {
                BasisDeviceMatchSettings Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
                if (Match.CanDisplayPhysicalTracker)
                {
                    var op = Addressables.LoadAssetAsync<GameObject>(Match.DeviceID);
                    GameObject go = op.WaitForCompletion();
                    GameObject gameObj = Instantiate(go, this.transform, true);
                    gameObj.name = CommonDeviceIdentifier;
                    if (gameObj.TryGetComponent(out BasisVisualTracker))
                    {
                        BasisVisualTracker.Initialization(this);
                    }
                }
                else
                {
                    if (UseFallbackModel())
                    {
                        UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> op = Addressables.LoadAssetAsync<GameObject>(FallbackDeviceID);
                        GameObject go = op.WaitForCompletion();
                        GameObject gameObj = Instantiate(go, this.transform, true);
                        gameObj.name = CommonDeviceIdentifier;
                        if (gameObj.TryGetComponent(out BasisVisualTracker))
                        {
                            BasisVisualTracker.Initialization(this);
                        }
                    }
                }
            }
        }

        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
            BasisDebug.LogError("Eye Does not Support haptics!");
        }

        public BasisVirtualSpineDriver BasisVirtualSpine = new BasisVirtualSpineDriver();
    }
}
