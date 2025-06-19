using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
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
        }
        public override string Type()
        {
            return "OpenXRLoader";
        }
    }
}
