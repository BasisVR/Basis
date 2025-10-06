using System;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.VowganUIOld
{
    public class PanelGroupMover : MonoBehaviour
    {

        /// <summary>
        /// Which mode the panel group uses for placement.
        /// </summary>
        public enum PanelGroupRootMode
        {
            World,
            Head,
            Playspace,
            LeftHand,
            RightHand,
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
        public Transform GroupOffset;
        public Transform GroupMovementRoot;
        public Transform GroupStaticRoot;

        [Header("Settings")]
        public PanelGroupRootMode RootMode = PanelGroupRootMode.Playspace;
        public PanelGroupRootMode DesktopRootMode = PanelGroupRootMode.Head;
        public float RootScale = 0.001f;

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


        /*
        [ContextMenu("VR/SetRootMode World")]
        public void SetRootModeWorld() => SetRootMode(PanelGroupRootMode.World);

        [ContextMenu("VR/SetRootMode Playspace")]
        public void SetRootModePlayspace() => SetRootMode(PanelGroupRootMode.Playspace);

        [ContextMenu("VR/SetRootMode Head")]
        public void SetRootModeHead() => SetRootMode(PanelGroupRootMode.Head);

        [ContextMenu("VR/SetRootMode LeftHand")]
        public void SetRootModeLeftHand() => SetRootMode(PanelGroupRootMode.LeftHand);

        [ContextMenu("VR/SetRootMode RightHand")]
        public void SetRootModeRightHand() => SetRootMode(PanelGroupRootMode.RightHand);

        [ContextMenu("Desktop/SetDesktopRootMode World")]
        public void SetDesktopRootModeWorld() => SetDesktopRootMode(PanelGroupRootMode.World);

        [ContextMenu("Desktop/SetDesktopRootMode Playspace")]
        public void SetDesktopRootModePlayspace() => SetDesktopRootMode(PanelGroupRootMode.Playspace);

        [ContextMenu("Desktop/SetDesktopRootMode Head")]
        public void SetDesktopRootModeHead() => SetDesktopRootMode(PanelGroupRootMode.Head);

        [ContextMenu("Desktop/SetDesktopRootMode LeftHand")]
        public void SetDesktopRootModeLeftHand() => SetDesktopRootMode(PanelGroupRootMode.LeftHand);

        [ContextMenu("Desktop/SetDesktopRootMode RightHand")]
        public void SetDesktopRootModeRightHand() => SetDesktopRootMode(PanelGroupRootMode.RightHand);
        */

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
            GroupOffset.localScale = Vector3.one * RootScale;
            GroupMovementRoot.localScale = Vector3.one * offset.Scale;
            GroupStaticRoot.localScale = Vector3.one * offset.Scale;
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
