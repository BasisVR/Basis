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
            LeftHand, // VR Only
            RightHand, // VR Only
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
        public PanelGroupRootMode VRMode = PanelGroupRootMode.Floating;
        public PanelGroupRootMode DesktopRootMode = PanelGroupRootMode.Eye;
        public PanelGroupRootMode InUse = PanelGroupRootMode.Eye;
        public float RootScale = 0.0005f;

        [Header("Offsets are multiplied against the Player Eye Height.\nAssign your values assuming a height of 1 meter.")]
        public RootModeOffset WorldOffset;
        public RootModeOffset HeadOffset;
        public RootModeOffset LeftHandOffset;
        public RootModeOffset RightHandOffset;
        public RootModeOffset FloatingOffset;

        [Header("Readout")]
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 VRRootOffset;

        private BasisLocalBoneControl _leftHandControl;
        private BasisLocalBoneControl _rightHandControl;
        private bool _hasLocalCreationEvent;
        private bool _hasLocalMoveEvent;

        private const float MIN_Z_SCALE = 0.01f;


        private void Start()
        {
            if (BasisLocalPlayer.Instance)
            {
                OnLocalPlayerCreated();
            }
            else
            {
                BasisLocalPlayer.OnLocalPlayerInitalized += OnLocalPlayerCreated;
                _hasLocalCreationEvent = true;
            }

            BasisDeviceManagement.OnBootModeChanged += OnBootModeChanged;
        }
        public void OnEnable()
        {
            OnAvatarHeightChange();
        }

        private void OnDestroy()
        {
            BasisLocalPlayer.Instance.OnAvatarSwitched -= OnAvatarHeightChange;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnAvatarHeightChange;

            if (_hasLocalCreationEvent)
            {
                BasisLocalPlayer.OnLocalPlayerInitalized -= OnLocalPlayerCreated;
            }

            if (_hasLocalMoveEvent)
            {
                BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(120, UpdateUILocation);
            }

            BasisDeviceManagement.OnBootModeChanged -= OnBootModeChanged;
        }

        private void OnLocalPlayerCreated()
        {
            BasisLocalPlayer.Instance.OnAvatarSwitched += OnAvatarHeightChange;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnAvatarHeightChange;
            SetRootMode(GetFindCurrentMode());

            BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out _leftHandControl, BasisBoneTrackedRole.LeftHand);
            BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out _rightHandControl, BasisBoneTrackedRole.RightHand);
        }

        private void OnBootModeChanged(string obj)
        {
            SetRootMode(GetFindCurrentMode());
        }
        public void OnAvatarHeightChange()
        {
            SetRootMode(GetFindCurrentMode());
        }

        public void OnAvatarHeightChange(BasisHeightDriver.HeightModeChange HeightModeChange)
        {
            SetRootMode(GetFindCurrentMode());
        }

        public PanelGroupRootMode GetFindCurrentMode()
        {
            if (BasisDeviceManagement.IsUserInDesktop())
            {
                return DesktopRootMode;
            }
            else
            {
                if (BasisDeviceManagement.IsCurrentModeVR())
                {
                    return VRMode;
                }
                else
                {
                    return DesktopRootMode;
                }
            }
        }

        /// <summary>
        /// Apply the offset for the Current Root Mode.
        /// This also subscribes to the player's movement callback if needed.
        /// </summary>
        public void SetRootMode(PanelGroupRootMode mode)
        {
            InUse = mode;
            switch (InUse)
            {
                case PanelGroupRootMode.World:
                    SetMovementCallback(false);
                    SetRootOffset(WorldOffset);
                    break;
                case PanelGroupRootMode.Eye:
                    SetMovementCallback(true);
                    // SetRootOffset(HeadOffset);
                    UpdateUILocation(PanelGroupRootMode.Eye);
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
                    UpdateUILocation();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void SetMovementCallback(bool value)
        {
            if (value != _hasLocalMoveEvent)
            {
                if (value)
                {
                    BasisLocalPlayer.AfterSimulateOnLate.AddAction(120, UpdateUILocation);
                }
                else
                {
                    BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(120, UpdateUILocation);
                }

                _hasLocalMoveEvent = value;
            }
        }

        private void SetRootOffset(RootModeOffset offset)
        {
            float playerHeight = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            GroupOffset.SetLocalPositionAndRotation(offset.Position, offset.Rotation);

            Vector3 offsetScale =  Vector3.one * (offset.Scale * RootScale);
            offsetScale.z = Mathf.Max(MIN_Z_SCALE, offsetScale.z);
            GroupOffset.localScale = offsetScale;

            transform.localScale = Vector3.one * playerHeight;
        }

        private void SetEyeOffset(float scaleFactor)
        {
            float playerHeight = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            Vector3 scaledOffset = Vector3.Scale(HeadOffset.Position, new Vector3(scaleFactor, scaleFactor, 1));
            GroupOffset.SetLocalPositionAndRotation(scaledOffset, HeadOffset.Rotation);

            Vector3 offsetScale =  Vector3.one * (HeadOffset.Scale * RootScale * scaleFactor);
            offsetScale.z = Mathf.Max(MIN_Z_SCALE, offsetScale.z);
            GroupOffset.localScale = offsetScale;

            transform.localScale = Vector3.one * playerHeight;
        }

        private void UpdateUILocation()
        {
            UpdateUILocation(InUse);
        }

        private void UpdateUILocation(PanelGroupRootMode mode)
        {
            switch (mode)
            {
                case PanelGroupRootMode.World:
                    break;
                case PanelGroupRootMode.Eye:
                    if (BasisLocalCameraDriver.HasInstance)
                    {
                        // 11-30-2025: This value did not report the active value, as this setting was not applied to the application immediately.
                        // float fieldOfView = BasisSettingsSystem.LoadFloat(BasisSettingsDefaults.FieldOfView.BindingKey);
                        float fieldOfView = BasisLocalCameraDriver.CameraInstance.fieldOfView;
                        float tanFOV = Mathf.Tan((Mathf.Deg2Rad * fieldOfView) / 2);

                        // 80 was the FOV the Menu was designed at.
                        const float designerMenuScale = 80;
                        float tanFOVBase = Mathf.Tan((Mathf.Deg2Rad * designerMenuScale) / 2);
                        float scaleFactor = tanFOV / tanFOVBase;

                        BasisLocalCameraDriver.GetPositionAndRotation(out Position, out Rotation);
                        transform.SetPositionAndRotation(Position, Rotation);

                        SetEyeOffset(scaleFactor);
                    }
                    break;
                case PanelGroupRootMode.LeftHand:
                    BasisCalibratedCoords leftData = _leftHandControl.OutgoingWorldData;
                    Position = leftData.position;
                    Rotation = leftData.rotation;
                    transform.SetPositionAndRotation(Position, Rotation);
                    break;
                case PanelGroupRootMode.RightHand:
                    BasisCalibratedCoords rightData = _rightHandControl.OutgoingWorldData;
                    Position = rightData.position;
                    Rotation = rightData.rotation;
                    transform.SetPositionAndRotation(Position, Rotation);
                    break;
                case PanelGroupRootMode.Floating:
                    BasisLocalCameraDriver.GetPositionAndRotation(out Position, out Rotation);
                    Rotation = Quaternion.LookRotation(Rotation * Vector3.forward, Vector3.up);
                    transform.SetPositionAndRotation(Position + VRRootOffset, Rotation);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
