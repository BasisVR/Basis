using Basis.Scripts.Device_Management.EyeTracking;
using Basis.Scripts.Drivers;
using Unity.Mathematics;
using UnityEngine;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    public class BasisOpenVRInputEye : BasisInputEye, IBasisEyeTrackingProvider
    {
        private SteamVR_Action_Pose _gazeAction;
        private SteamVR_Input_Sources _gazeSource = SteamVR_Input_Sources.Any;

        private TrackedDevicePose_t _hmdPose;
        private TrackedDevicePose_t _hmdGamePose;

        private bool _hasGaze;
        private float3 _gazeOrigin;
        private float3 _gazeDirection;

        public BasisEyeSource Source => BasisEyeSource.Hmd;
        public bool IsActive => _hasGaze;

        public bool TryGetEyeData(ref BasisEyeTrackingData data)
        {
            if (!_hasGaze)
            {
                return false;
            }
            data.GazeOrigin = _gazeOrigin;
            data.GazeDirection = _gazeDirection;
            data.HasWorldRay = true;
            data.LeftEyePosition = LeftPosition;
            data.RightEyePosition = RightPosition;
            data.HasEyePositions = true;
            return true;
        }

        public override void Initialize()
        {
            _gazeAction = SteamVR_Input.GetAction<SteamVR_Action_Pose>("EyeGaze");
            BasisEyeTrackingManager.Register(this);
        }

        public override void Shutdown()
        {
            BasisEyeTrackingManager.Unregister(this);
            _hasGaze = false;
            BasisEyeGazeGizmo.Shutdown();
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

            BasisEyeMath.HmdRelativeGazeToWorld(
                hmdTrackingPos, hmdTrackingRot, gazeTrackingPos, gazeTrackingRot,
                BasisLocalCameraDriver.Position, BasisLocalCameraDriver.Rotation,
                out Vector3 worldGazeOrigin, out Vector3 worldGazeDir);

            _gazeOrigin = worldGazeOrigin;
            _gazeDirection = worldGazeDir;
            _hasGaze = true;

            bool gizmoVisible = SMModuleDebugOptions.UseGizmos && SMModuleDebugOptions.UseEyeGazeGizmo;
            BasisEyeGazeGizmo.Tick(gizmoVisible, worldGazeOrigin, worldGazeDir);
        }

        private void MarkUntracked()
        {
            _hasGaze = false;
            BasisEyeGazeGizmo.Tick(false, Vector3.zero, Vector3.forward);
        }
    }
}
