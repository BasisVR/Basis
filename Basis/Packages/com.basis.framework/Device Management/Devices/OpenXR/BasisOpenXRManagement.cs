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
                BasisFingerPose rightFinger = BasisLocalPlayer.Instance.LocalMuscleDriver.RightFinger;
                BasisDebug.LogError("rightHand");
                // Extract and map joint curls and splays
                UpdateHandFromXR(rightHand, ref rightFinger, isLeft: false);
            }

            // Process Left Hand
            if (subsystem.leftHand.isTracked)
            {
                XRHand leftHand = subsystem.leftHand;
                BasisFingerPose leftFinger = BasisLocalPlayer.Instance.LocalMuscleDriver.LeftFinger;
                BasisDebug.LogError("leftHand");
                // Extract and map joint curls and splays
                UpdateHandFromXR(leftHand, ref leftFinger, isLeft: true);
            }
        }

        private void UpdateHandFromXR(XRHand hand, ref BasisFingerPose fingerPose, bool isLeft)
        {
            fingerPose.jointPositions = new Vector3[XRHandJointID.EndMarker.ToIndex()];
            fingerPose.jointRotations = new Quaternion[XRHandJointID.EndMarker.ToIndex()];

            foreach (XRHandJointID jointId in Enum.GetValues(typeof(XRHandJointID)))
            {
                if (jointId == XRHandJointID.Invalid || jointId == XRHandJointID.BeginMarker || jointId == XRHandJointID.EndMarker)
                    continue;

                XRHandJoint joint = hand.GetJoint(jointId);
                if (joint.TryGetPose(out Pose pose))
                {
                    fingerPose.jointPositions[jointId.ToIndex()] = pose.position;
                    fingerPose.jointRotations[jointId.ToIndex()] = pose.rotation;
                }
            }
            /*
            // Example: Calculate curls based on joint distances
            float[] fingerCurls = new float[5];

            // Thumb (from Metacarpal to Tip)
            fingerCurls[0] = CalculateCurl(
                jointPositions[(int)XRHandJointID.ThumbMetacarpal],
                jointPositions[(int)XRHandJointID.ThumbProximal],
                jointPositions[(int)XRHandJointID.ThumbDistal],
                jointPositions[(int)XRHandJointID.ThumbTip]);

            // Index
            fingerCurls[1] = CalculateCurl(
                jointPositions[(int)XRHandJointID.IndexMetacarpal],
                jointPositions[(int)XRHandJointID.IndexProximal],
                jointPositions[(int)XRHandJointID.IndexIntermediate],
                jointPositions[(int)XRHandJointID.IndexTip]);

            // Middle
            fingerCurls[2] = CalculateCurl(
                jointPositions[(int)XRHandJointID.MiddleMetacarpal],
                jointPositions[(int)XRHandJointID.MiddleProximal],
                jointPositions[(int)XRHandJointID.MiddleIntermediate],
                jointPositions[(int)XRHandJointID.MiddleTip]);

            // Ring
            fingerCurls[3] = CalculateCurl(
                jointPositions[(int)XRHandJointID.RingMetacarpal],
                jointPositions[(int)XRHandJointID.RingProximal],
                jointPositions[(int)XRHandJointID.RingIntermediate],
                jointPositions[(int)XRHandJointID.RingTip]);

            // Little
            fingerCurls[4] = CalculateCurl(
                jointPositions[(int)XRHandJointID.LittleMetacarpal],
                jointPositions[(int)XRHandJointID.LittleProximal],
                jointPositions[(int)XRHandJointID.LittleIntermediate],
                jointPositions[(int)XRHandJointID.LittleTip]);

            // Map to your muscle driver system (example values)
            fingerPose.ThumbPercentage = new Vector2(MapCurl(fingerCurls[0]), 0);
            fingerPose.IndexPercentage = new Vector2(MapCurl(fingerCurls[1]), 0);
            fingerPose.MiddlePercentage = new Vector2(MapCurl(fingerCurls[2]), 0);
            fingerPose.RingPercentage = new Vector2(MapCurl(fingerCurls[3]), 0);
            fingerPose.LittlePercentage = new Vector2(MapCurl(fingerCurls[4]), 0);
            */
        }

        // Utility method to calculate curl value from joint angles
        private float CalculateCurl(Vector3 baseJoint, Vector3 middleJoint, Vector3 distalJoint, Vector3 tipJoint)
        {
            Vector3 vec1 = (middleJoint - baseJoint).normalized;
            Vector3 vec2 = (tipJoint - distalJoint).normalized;
            float angle = Vector3.Angle(vec1, vec2);
            return Mathf.InverseLerp(0, 90, angle); // 0 is fully extended, 1 is curled
        }

        // Example mapping function for muscle driver compatibility
        private float MapCurl(float rawCurl)
        {
            return BasisBaseMuscleDriver.MapValue(1 - rawCurl, 0, 1, -1f, 0.7f);
        }

        public override string Type()
        {
            return "OpenXRLoader";
        }
    }
}
