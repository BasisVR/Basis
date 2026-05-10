using Basis.Scripts.Drivers;
using UnityEngine;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    public class BasisOpenVRInputEye : BasisInputEye
    {
        private SteamVR_Action_Pose _gazeAction;
        private SteamVR_Input_Sources _gazeSource = SteamVR_Input_Sources.Any;

        private TrackedDevicePose_t _hmdPose;
        private TrackedDevicePose_t _hmdGamePose;

        public override void Initalize()
        {
            _gazeAction = SteamVR_Input.GetAction<SteamVR_Action_Pose>("EyeGaze");
        }

        public override void Shutdown()
        {
            BasisEyeGazeGizmo.Shutdown();
            BasisLocalCameraDriver.HasEyeGaze = false;
            _gazeAction = null;
        }

        public override void Simulate()
        {
            LeftPosition = SteamVR.instance.eyes[0].pos;
            RightPosition = SteamVR.instance.eyes[1].pos;
            if (BasisLocalCameraDriver.HasInstance)
            {
                BasisLocalCameraDriver.LeftEye = LeftPosition;
                BasisLocalCameraDriver.RightEye = RightPosition;
            }

            UpdateGaze();
        }

        private void UpdateGaze()
        {
            if (!BasisLocalCameraDriver.HasInstance)
            {
                MarkUntracked();
                return;
            }

            if (_gazeAction == null
                || !_gazeAction.GetActive(_gazeSource)
                || !_gazeAction.GetPoseIsValid(_gazeSource))
            {
                MarkUntracked();
                return;
            }

            if (!SteamVR.active || SteamVR.instance == null || SteamVR.instance.compositor == null)
            {
                MarkUntracked();
                return;
            }

            var compositorResult = SteamVR.instance.compositor.GetLastPoseForTrackedDeviceIndex(
                Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd,
                ref _hmdPose,
                ref _hmdGamePose);
            if (compositorResult != EVRCompositorError.None || !_hmdPose.bPoseIsValid)
            {
                MarkUntracked();
                return;
            }

            Vector3 hmdTrackingPos = _hmdPose.mDeviceToAbsoluteTracking.GetPosition();
            Quaternion hmdTrackingRot = _hmdPose.mDeviceToAbsoluteTracking.GetRotation();

            Vector3 gazeTrackingPos = _gazeAction.GetLocalPosition(_gazeSource);
            Quaternion gazeTrackingRot = _gazeAction.GetLocalRotation(_gazeSource);

            Quaternion invHmdRot = Quaternion.Inverse(hmdTrackingRot);
            Vector3 gazeRelHmdPos = invHmdRot * (gazeTrackingPos - hmdTrackingPos);
            Quaternion gazeRelHmdRot = invHmdRot * gazeTrackingRot;

            Vector3 worldGazeOrigin = BasisLocalCameraDriver.Position + BasisLocalCameraDriver.Rotation * gazeRelHmdPos;
            Quaternion worldGazeRot = BasisLocalCameraDriver.Rotation * gazeRelHmdRot;
            Vector3 worldGazeDir = worldGazeRot * Vector3.forward;

            BasisLocalCameraDriver.GazeOrigin = worldGazeOrigin;
            BasisLocalCameraDriver.GazeDirection = worldGazeDir;
            BasisLocalCameraDriver.HasEyeGaze = true;

            bool gizmoVisible = SMModuleDebugOptions.UseGizmos && SMModuleDebugOptions.UseEyeGazeGizmo;
            BasisEyeGazeGizmo.Tick(gizmoVisible, worldGazeOrigin, worldGazeDir);
        }

        private static void MarkUntracked()
        {
            BasisLocalCameraDriver.HasEyeGaze = false;
            BasisEyeGazeGizmo.Tick(false, Vector3.zero, Vector3.forward);
        }
    }
}
