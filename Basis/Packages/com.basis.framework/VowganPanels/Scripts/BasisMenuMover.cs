using System;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.VowganUI
{
    public class BasisMenuMover : MonoBehaviour
    {

        /// <summary>
        /// Which mode the panel group uses for placement.
        /// </summary>
        public enum PanelGroupRootMode
        {
            World,
            Head,
            Playspace, // VR Only
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

        public PanelGroupRootMode CurrentRootMode => BasisDeviceManagement.IsUserInDesktop() ? DesktopRootMode : RootMode;


        [Header("References")]
        public RectTransform GroupOffset;

        [Header("Settings")]
        public PanelGroupRootMode RootMode = PanelGroupRootMode.Playspace;
        public PanelGroupRootMode DesktopRootMode = PanelGroupRootMode.Head;
        public float RootScale = 0.0005f;

        [Header("Offsets are multiplied against the Player Eye Height.\nAssign your values assuming a height of 1 meter.")]
        public RootModeOffset WorldOffset;
        public RootModeOffset PlayspaceOffset;
        public RootModeOffset HeadOffset;
        public RootModeOffset LeftHandOffset;
        public RootModeOffset RightHandOffset;

        [Header("Readout")]
        public Vector3 Position;
        public Quaternion Rotation;

        private BasisLocalBoneControl _leftHandControl;
        private BasisLocalBoneControl _rightHandControl;
        private bool _hasLocalCreationEvent;
        private bool _hasLocalMoveEvent;

        private void Start()
        {
            if (BasisLocalPlayer.Instance)
            {
                OnLocalPlayerCreated();
            }
            else
            {
                BasisLocalPlayer.OnLocalPlayerCreated += OnLocalPlayerCreated;
                _hasLocalCreationEvent = true;
            }

            ApplyOffset();
        }

        private void OnDestroy()
        {
            BasisLocalPlayer.Instance.OnAvatarSwitched -= ApplyOffset;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= ApplyOffset;

            if (_hasLocalCreationEvent)
                BasisLocalPlayer.OnLocalPlayerCreated -= OnLocalPlayerCreated;

            if (_hasLocalMoveEvent)
                BasisLocalPlayer.AfterFinalMove.RemoveAction(120, UpdateUILocation);
        }

        private void OnLocalPlayerCreated()
        {
            BasisLocalPlayer.Instance.OnAvatarSwitched += ApplyOffset;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += ApplyOffset;
            SetRootMode(BasisDeviceManagement.IsUserInDesktop() ? DesktopRootMode : RootMode);

            BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out _leftHandControl, BasisBoneTrackedRole.LeftHand);
            BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out _rightHandControl, BasisBoneTrackedRole.RightHand);
        }

        public void SetRootMode(PanelGroupRootMode mode)
        {
            RootMode = mode;
            ApplyOffset();
        }

        public void SetDesktopRootMode(PanelGroupRootMode mode)
        {
            DesktopRootMode = mode;
            ApplyOffset();
        }

        /// <summary>
        /// Apply the offset for the Current Root Mode.
        /// This also subscribes to the player's movement callback if needed.
        /// </summary>
        private void ApplyOffset()
        {
            switch (CurrentRootMode)
            {
                case PanelGroupRootMode.World:
                    SetMovementCallback(false);
                    SetRootOffset(WorldOffset);
                    break;
                case PanelGroupRootMode.Playspace:
                    SetMovementCallback(true);
                    SetRootOffset(PlayspaceOffset);
                    break;
                case PanelGroupRootMode.Head:
                    SetMovementCallback(true);
                    SetRootOffset(HeadOffset);
                    break;
                case PanelGroupRootMode.LeftHand:
                    SetMovementCallback(true);
                    SetRootOffset(LeftHandOffset);
                    break;
                case PanelGroupRootMode.RightHand:
                    SetMovementCallback(true);
                    SetRootOffset(RightHandOffset);
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
                    BasisLocalPlayer.AfterFinalMove.AddAction(120, UpdateUILocation);
                }
                else
                {
                    BasisLocalPlayer.AfterFinalMove.RemoveAction(120, UpdateUILocation);
                }

                _hasLocalMoveEvent = value;
            }
        }

        private void SetRootOffset(RootModeOffset offset)
        {
            float playerHeight = BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight;
            GroupOffset.SetLocalPositionAndRotation(offset.Position, offset.Rotation);
            GroupOffset.localScale = Vector3.one * (offset.Scale * RootScale);
            transform.localScale = Vector3.one * playerHeight;
        }

        private void UpdateUILocation()
        {
            PanelGroupRootMode mode = BasisDeviceManagement.IsUserInDesktop() ? DesktopRootMode : RootMode;

            switch (mode)
            {
                case PanelGroupRootMode.World:
                    break;
                case PanelGroupRootMode.Playspace:
                    Position = BasisLocalPlayer.Instance.AvatarTransform.position;
                    Rotation = BasisLocalPlayer.Instance.AvatarTransform.rotation;
                    break;
                case PanelGroupRootMode.Head:
                    BasisLocalCameraDriver.GetPositionAndRotation(out Position, out Rotation);
                    break;
                case PanelGroupRootMode.LeftHand:
                    BasisCalibratedCoords leftData = _leftHandControl.OutgoingWorldData;
                    Position = leftData.position;
                    Rotation = leftData.rotation;
                    break;
                case PanelGroupRootMode.RightHand:
                    BasisCalibratedCoords rightData = _rightHandControl.OutgoingWorldData;
                    Position = rightData.position;
                    Rotation = rightData.rotation;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            transform.SetPositionAndRotation(Position, Rotation);
        }

    }
}
