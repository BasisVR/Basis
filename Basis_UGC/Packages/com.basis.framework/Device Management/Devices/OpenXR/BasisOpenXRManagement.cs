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
            switch (updateType)
            {
                case XRHandSubsystem.UpdateType.BeforeRender:
                    if (subsystem.rightHand.isTracked)
                    {
                        foreach(XRHandJointID Join in Enum.GetValues(typeof(XRHandJointID)))
                        {
                            switch (Join)
                            {
                                case XRHandJointID.Invalid:
                                    break;
                                case XRHandJointID.BeginMarker:
                                    break;
                                case XRHandJointID.Palm:
                                    var Joint = subsystem.rightHand.GetJoint(Join);
                                    if (Joint.TryGetPose(out Pose pose))
                                    {
                                     //   pose.position;
                                    }
                                    break;
                                case XRHandJointID.ThumbMetacarpal:
                                    break;
                                case XRHandJointID.ThumbProximal:
                                    break;
                                case XRHandJointID.ThumbDistal:
                                    break;
                                case XRHandJointID.ThumbTip:
                                    break;
                                case XRHandJointID.IndexMetacarpal:
                                    break;
                                case XRHandJointID.IndexProximal:
                                    break;
                                case XRHandJointID.IndexIntermediate:
                                    break;
                                case XRHandJointID.IndexDistal:
                                    break;
                                case XRHandJointID.IndexTip:
                                    break;
                                case XRHandJointID.MiddleMetacarpal:
                                    break;
                                case XRHandJointID.MiddleProximal:
                                    break;
                                case XRHandJointID.MiddleIntermediate:
                                    break;
                                case XRHandJointID.MiddleDistal:
                                    break;
                                case XRHandJointID.MiddleTip:
                                    break;
                                case XRHandJointID.RingMetacarpal:
                                    break;
                                case XRHandJointID.RingProximal:
                                    break;
                                case XRHandJointID.RingIntermediate:
                                    break;
                                case XRHandJointID.RingDistal:
                                    break;
                                case XRHandJointID.RingTip:
                                    break;
                                case XRHandJointID.LittleMetacarpal:
                                    break;
                                case XRHandJointID.LittleProximal:
                                    break;
                                case XRHandJointID.LittleIntermediate:
                                    break;
                                case XRHandJointID.LittleDistal:
                                    break;
                                case XRHandJointID.LittleTip:
                                    break;
                                case XRHandJointID.EndMarker:
                                    break;
                            }
                        }
                    }
                    if (subsystem.leftHand.isTracked)
                    {

                    }
                    break;
            }
        }

        public override string Type()
        {
            return "OpenXRLoader";
        }
    }
}
