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
        private ulong _eyeTrackingAction;
        private VREyeTrackingData_t _eyeTrackingData;
        private static readonly uint EyeTrackingDataSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<VREyeTrackingData_t>();

        private TrackedDevicePose_t _hmdPose;
        private TrackedDevicePose_t _hmdGamePose;

        private bool _hasGaze;
        private float3 _gazeOrigin;
        private float3 _gazeDirection;

        public BasisEyeSource Source => BasisEyeSource.Hmd;
        public string DisplayName { get; private set; } = "SteamVR";
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
            _eyeTrackingAction = 0;
            if (Valve.VR.OpenVR.Input == null || Valve.VR.OpenVR.Input.GetActionHandle("/actions/default/in/EyeTracking", ref _eyeTrackingAction) != EVRInputError.None)
            {
                _eyeTrackingAction = 0;
            }
            CVRSystem system = Valve.VR.OpenVR.System;
            if (system != null)
            {
                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                System.Text.StringBuilder model = new System.Text.StringBuilder(64);
                system.GetStringTrackedDeviceProperty(Valve.VR.OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_ModelNumber_String, model, 64, ref error);
                if (error == ETrackedPropertyError.TrackedProp_Success && model.Length > 0)
                {
                    DisplayName = model.ToString();
                }
            }
            BasisEyeTrackingManager.Register(this);
        }

        public override void Shutdown()
        {
            BasisEyeTrackingManager.Unregister(this);
            _hasGaze = false;
            BasisEyeGazeGizmo.Shutdown();
            _gazeAction = null;
            _eyeTrackingAction = 0;
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

            bool useEyeTracking = TryReadEyeTracking(out Vector3 gazeTrackingPos, out Quaternion gazeTrackingRot);
            if (!useEyeTracking
                && (_gazeAction == null
                || !_gazeAction.GetActive(_gazeSource)
                || !_gazeAction.GetPoseIsValid(_gazeSource)))
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

            if (!useEyeTracking)
            {
                gazeTrackingPos = _gazeAction.GetLocalPosition(_gazeSource);
                gazeTrackingRot = _gazeAction.GetLocalRotation(_gazeSource);
            }

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

        private bool TryReadEyeTracking(out Vector3 origin, out Quaternion rotation)
        {
            origin = default;
            rotation = Quaternion.identity;
            CVRInput input = Valve.VR.OpenVR.Input;
            CVRCompositor compositor = Valve.VR.OpenVR.Compositor;
            if (_eyeTrackingAction == 0 || input == null || compositor == null)
            {
                return false;
            }
            if (input.GetEyeTrackingDataForNextFrame(_eyeTrackingAction, compositor.GetTrackingSpace(), ref _eyeTrackingData, EyeTrackingDataSize) != EVRInputError.None
                || !_eyeTrackingData.bActive || !_eyeTrackingData.bValid || !_eyeTrackingData.bTracked)
            {
                return false;
            }
            HmdVector3_t o = _eyeTrackingData.vGazeOrigin;
            HmdVector3_t t = _eyeTrackingData.vGazeTarget;
            origin = new Vector3(o.v0, o.v1, -o.v2);
            Vector3 direction = new Vector3(t.v0, t.v1, -t.v2) - origin;
            if (direction.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            rotation = Quaternion.LookRotation(direction);
            return true;
        }

        private void MarkUntracked()
        {
            _hasGaze = false;
            BasisEyeGazeGizmo.Tick(false, Vector3.zero, Vector3.forward);
        }
    }
}
