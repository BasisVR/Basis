using System;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.VowganUI
{
    public class PanelGroupMover : MonoBehaviour
    {

        public enum PanelRootMode
        {
            World,
            Playspace,
            Head,
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

        public PanelRootMode CurrentRootMode => BasisDeviceManagement.IsUserInDesktop() ? DesktopRootMode : RootMode;


        [Header("References")]
        public Transform OffsetRootTransform;

        [Header("Settings")]
        public PanelRootMode RootMode = PanelRootMode.Playspace;
        public PanelRootMode DesktopRootMode = PanelRootMode.Head;

        [Header("Offsets are multiplied against the Player Eye Height.\nAssign your values assuming a height of 1 meter.")]
        public RootModeOffset WorldOffset;
        public RootModeOffset PlayspaceOffset;
        public RootModeOffset HeadOffset;
        public RootModeOffset LeftHandOffset;
        public RootModeOffset RightHandOffset;

        [Header("Readout")]
        public Vector3 Position;
        public Quaternion Rotation;

        private bool _hasLocalPlayerCreationEvent;

        private void Start()
        {
            if (BasisLocalPlayer.Instance)
            {
                OnLocalPlayerCreated();
            }
            else
            {
                BasisLocalPlayer.OnLocalPlayerCreated += OnLocalPlayerCreated;
                _hasLocalPlayerCreationEvent = true;
            }
        }

        private void OnDestroy()
        {
            // No, I don't know why 120 is used. It was used in the BasisUIMovementDriver.
            BasisLocalPlayer.AfterFinalMove.RemoveAction(120, UpdateUILocation);
            BasisLocalPlayer.Instance.OnAvatarSwitched -= ApplyOffset;

            if (_hasLocalPlayerCreationEvent)
                BasisLocalPlayer.OnLocalPlayerCreated -= OnLocalPlayerCreated;
        }

        private void OnLocalPlayerCreated()
        {
            // No, I don't know why 120 is used. It was used in the BasisUIMovementDriver.
            BasisLocalPlayer.AfterFinalMove.AddAction(120, UpdateUILocation);
            BasisLocalPlayer.Instance.OnAvatarSwitched += ApplyOffset;

            SetRootMode(BasisDeviceManagement.IsUserInDesktop() ? DesktopRootMode : RootMode);
        }


        [ContextMenu("VR/SetRootMode World")]
        public void SetRootModeWorld() => SetRootMode(PanelRootMode.World);
        [ContextMenu("VR/SetRootMode Playspace")]
        public void SetRootModePlayspace() => SetRootMode(PanelRootMode.Playspace);
        [ContextMenu("VR/SetRootMode Head")]
        public void SetRootModeHead() => SetRootMode(PanelRootMode.Head);
        [ContextMenu("VR/SetRootMode LeftHand")]
        public void SetRootModeLeftHand() => SetRootMode(PanelRootMode.LeftHand);
        [ContextMenu("VR/SetRootMode RightHand")]
        public void SetRootModeRightHand() => SetRootMode(PanelRootMode.RightHand);

        [ContextMenu("Desktop/SetDesktopRootMode World")]
        public void SetDesktopRootModeWorld() => SetDesktopRootMode(PanelRootMode.World);
        [ContextMenu("Desktop/SetDesktopRootMode Playspace")]
        public void SetDesktopRootModePlayspace() => SetDesktopRootMode(PanelRootMode.Playspace);
        [ContextMenu("Desktop/SetDesktopRootMode Head")]
        public void SetDesktopRootModeHead() => SetDesktopRootMode(PanelRootMode.Head);
        [ContextMenu("Desktop/SetDesktopRootMode LeftHand")]
        public void SetDesktopRootModeLeftHand() => SetDesktopRootMode(PanelRootMode.LeftHand);
        [ContextMenu("Desktop/SetDesktopRootMode RightHand")]
        public void SetDesktopRootModeRightHand() => SetDesktopRootMode(PanelRootMode.RightHand);

        public void SetRootMode(PanelRootMode mode)
        {
            RootMode = mode;
            ApplyOffset();
        }

        public void SetDesktopRootMode(PanelRootMode mode)
        {
            DesktopRootMode = mode;
            ApplyOffset();
        }

        private void ApplyOffset()
        {
            switch (CurrentRootMode)
            {
                case PanelRootMode.World:
                    SetRootOffset(WorldOffset);
                    break;
                case PanelRootMode.Playspace:
                    SetRootOffset(PlayspaceOffset);
                    break;
                case PanelRootMode.Head:
                    SetRootOffset(HeadOffset);
                    break;
                case PanelRootMode.LeftHand:
                    SetRootOffset(LeftHandOffset);
                    break;
                case PanelRootMode.RightHand:
                    SetRootOffset(RightHandOffset);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void SetRootOffset(RootModeOffset offset)
        {
            float playerHeight = BasisLocalPlayer.Instance.CurrentHeight.PlayerEyeHeight;
            OffsetRootTransform.SetLocalPositionAndRotation(offset.Position, offset.Rotation);
            OffsetRootTransform.localScale = Vector3.one * offset.Scale;
            transform.localScale = Vector3.one * playerHeight;
        }

        private void UpdateUILocation()
        {
            PanelRootMode mode = BasisDeviceManagement.IsUserInDesktop() ? DesktopRootMode : RootMode;

            switch (mode)
            {
                case PanelRootMode.World:
                    break;
                case PanelRootMode.Playspace:
                    Position = BasisLocalPlayer.Instance.AvatarTransform.position;
                    break;
                case PanelRootMode.Head:
                    BasisLocalCameraDriver.GetPositionAndRotation(out Position, out Rotation);
                    break;
                case PanelRootMode.LeftHand:
                    BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(
                        out BasisLocalBoneControl leftControl, BasisBoneTrackedRole.LeftHand);

                    BasisCalibratedCoords leftData  = leftControl.OutgoingWorldData;
                    Position = leftData.position;
                    Rotation = leftData.rotation;
                    break;
                case PanelRootMode.RightHand:
                    BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(
                        out BasisLocalBoneControl rightControl, BasisBoneTrackedRole.RightHand);

                    BasisCalibratedCoords rightData  = rightControl.OutgoingWorldData;
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
