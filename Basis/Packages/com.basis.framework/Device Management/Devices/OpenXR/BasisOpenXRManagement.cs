using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;
namespace Basis.Scripts.Device_Management.Devices.UnityInputSystem
{
    [Serializable]
    public class BasisOpenXRManagement : BasisBaseTypeManagement
    {
        List<BasisInput> controls = new List<BasisInput>();
        private void CreatePhysicalHandTracker(string device, string uniqueID, BasisBoneTrackedRole Role)
        {
            var gameObject = new GameObject(uniqueID)
            {
                transform =
                {
                    parent = BasisLocalPlayer.Instance.transform
                }
            };
            BasisOpenXRHandInput basisXRInput = gameObject.AddComponent<BasisOpenXRHandInput>();
            basisXRInput.ClassName = nameof(BasisOpenXRHandInput);
            basisXRInput.Initialize(uniqueID, device, nameof(BasisOpenXRManagement), true, Role);
            BasisDeviceManagement.Instance.TryAdd(basisXRInput);
            controls.Add(basisXRInput);
        }
        private void CreatePhysicalHeadTracker(string device, string uniqueID)
        {
            var gameObject = new GameObject(uniqueID)
            {
                transform =
                {
                    parent = BasisLocalPlayer.Instance.transform
                }
            };
            BasisOpenXRHeadInput basisXRInput = gameObject.AddComponent<BasisOpenXRHeadInput>();
            basisXRInput.ClassName = nameof(BasisOpenXRHeadInput);
            basisXRInput.Initialize(uniqueID, device, nameof(BasisOpenXRManagement), true);
            BasisDeviceManagement.Instance.TryAdd(basisXRInput);
            controls.Add(basisXRInput);
        }
        public void DestroyPhysicalTrackedDevice(string id)
        {
            BasisDeviceManagement.Instance.RemoveDevicesFrom(nameof(BasisOpenXRManagement), id);
        }

        public override void StopSDK()
        {
            BasisDebug.Log("Stopping " + nameof(BasisOpenXRManagement));
            foreach (var device in controls)
            {
                DestroyPhysicalTrackedDevice(device.UniqueDeviceIdentifier);
            }
            controls.Clear();
        }

        public override void BeginLoadSDK()
        {
        }

        public override void StartSDK()
        {
            BasisDeviceManagement.Instance.SetCameraRenderState(true);
            BasisDebug.Log("Starting " + nameof(BasisOpenXRManagement));
            CreatePhysicalHeadTracker("Head OPENXR", "Head OPENXR");
            CreatePhysicalHandTracker("Left Hand OPENXR", "Left Hand OPENXR", BasisBoneTrackedRole.LeftHand);
            CreatePhysicalHandTracker("Right Hand OPENXR", "Right Hand OPENXR", BasisBoneTrackedRole.RightHand);

            XRHandSubsystem m_Subsystem =
      XRGeneralSettings.Instance?
          .Manager?
          .activeLoader?
          .GetLoadedSubsystem<XRHandSubsystem>();

            if (m_Subsystem != null)
            {
                m_Subsystem.updatedHands += OnHandUpdate;
            }
        }

        private void OnHandUpdate(XRHandSubsystem subsystem, XRHandSubsystem.UpdateSuccessFlags flags, XRHandSubsystem.UpdateType updateType)
        {
            if (updateType != XRHandSubsystem.UpdateType.BeforeRender)
            {
                return;
            }

            // Process Right Hand
            if (subsystem.rightHand.isTracked)
            {
                XRHand rightHand = subsystem.rightHand;
                // Extract and map joint curls and splays
                UpdateHandFromXR(rightHand,  BasisLocalPlayer.Instance.LocalMuscleDriver.RightHandPoses, isLeft: false);
            }

            // Process Left Hand
            if (subsystem.leftHand.isTracked)
            {
                XRHand leftHand = subsystem.leftHand;
                // Extract and map joint curls and splays
                UpdateHandFromXR(leftHand,  BasisLocalPlayer.Instance.LocalMuscleDriver.LeftHandPoses, isLeft: true);
            }
        }

        private void UpdateHandFromXR(XRHand hand, BasisFingerPose fingerPose, bool isLeft)
        {
            // Palm and Wrist
            UpdateJointPose(hand, XRHandJointID.Palm, out fingerPose.PalmPos, out fingerPose.PalmRot);
            UpdateJointPose(hand, XRHandJointID.Wrist, out fingerPose.WristPos, out fingerPose.WristRot); // Fixed: Use Wrist joint here

            // Thumb
            UpdateJointPose(hand, XRHandJointID.ThumbProximal, out fingerPose.thumbPositions[0], out fingerPose.thumbRotations[0]);
            UpdateJointPose(hand, XRHandJointID.ThumbDistal, out fingerPose.thumbPositions[1], out fingerPose.thumbRotations[1]);
            UpdateJointPose(hand, XRHandJointID.ThumbTip, out fingerPose.thumbPositions[2], out fingerPose.thumbRotations[2]);

            // Index
            UpdateJointPose(hand, XRHandJointID.IndexProximal, out fingerPose.indexPositions[0], out fingerPose.indexRotations[0]);
            UpdateJointPose(hand, XRHandJointID.IndexIntermediate, out fingerPose.indexPositions[1], out fingerPose.indexRotations[1]);
            UpdateJointPose(hand, XRHandJointID.IndexTip, out fingerPose.indexPositions[2], out fingerPose.indexRotations[2]);

            // Middle
            UpdateJointPose(hand, XRHandJointID.MiddleProximal, out fingerPose.middlePositions[0], out fingerPose.middleRotations[0]);
            UpdateJointPose(hand, XRHandJointID.MiddleIntermediate, out fingerPose.middlePositions[1], out fingerPose.middleRotations[1]);
            UpdateJointPose(hand, XRHandJointID.MiddleTip, out fingerPose.middlePositions[2], out fingerPose.middleRotations[2]);

            // Ring
            UpdateJointPose(hand, XRHandJointID.RingProximal, out fingerPose.ringPositions[0], out fingerPose.ringRotations[0]);
            UpdateJointPose(hand, XRHandJointID.RingIntermediate, out fingerPose.ringPositions[1], out fingerPose.ringRotations[1]);
            UpdateJointPose(hand, XRHandJointID.RingTip, out fingerPose.ringPositions[2], out fingerPose.ringRotations[2]);

            // Little
            UpdateJointPose(hand, XRHandJointID.LittleProximal, out fingerPose.littlePositions[0], out fingerPose.littleRotations[0]);
            UpdateJointPose(hand, XRHandJointID.LittleIntermediate, out fingerPose.littlePositions[1], out fingerPose.littleRotations[1]);
            UpdateJointPose(hand, XRHandJointID.LittleTip, out fingerPose.littlePositions[2], out fingerPose.littleRotations[2]);
        }
        private void UpdateJointPose(XRHand hand, XRHandJointID jointId, out Vector3 Position, out Quaternion Rotation)
        {
            XRHandJoint joint = hand.GetJoint(jointId);
            if (joint.TryGetPose(out Pose pose))
            {
                Position = pose.position;
                Rotation = pose.rotation;
            }
            else
            {
                Position = Vector3.zero;
                Rotation = Quaternion.identity;
            }
        }

        public override string Type()
        {
            return "OpenXRLoader";
        }
    }
}
