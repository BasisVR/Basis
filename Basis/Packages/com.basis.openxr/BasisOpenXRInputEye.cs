using Basis.Scripts.Drivers;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace Basis.Scripts.Device_Management.Devices.OpenXR
{
    public class BasisOpenXRInputEye : BasisInputEye
    {
        private XRNodeState leftEyeState;
        private XRNodeState rightEyeState;
        private XRNodeState centerEyeState;
        private bool hasCenterEyeState;
        private readonly List<XRNodeState> _nodeStates = new List<XRNodeState>();

        private InputAction _gazePoseAction;

        public override void Initalize()
        {
            RefreshNodeStates();

            _gazePoseAction = new InputAction("EyeGazePose", InputActionType.Value, "<EyeGaze>/pose", expectedControlType: "Pose");
            _gazePoseAction.Enable();
        }

        public override void Shutdown()
        {
            if (_gazePoseAction != null)
            {
                _gazePoseAction.Disable();
                _gazePoseAction.Dispose();
                _gazePoseAction = null;
            }
            BasisEyeGazeGizmo.Shutdown();
            BasisLocalCameraDriver.HasEyeGaze = false;
        }

        public override void Simulate()
        {
            // XRNodeState is a snapshot struct — its position/rotation reflect the
            // moment GetNodeStates filled the list, not live tracking. Refresh so
            // hmdTrackingPos in UpdateGaze stays anchored to the actual HMD pose.
            RefreshNodeStates();

            if (leftEyeState.TryGetPosition(out LeftPosition))
            {
            }
            if (rightEyeState.TryGetPosition(out RightPosition))
            {
            }
            if (BasisLocalCameraDriver.HasInstance)
            {
                BasisLocalCameraDriver.LeftEye = LeftPosition;
                BasisLocalCameraDriver.RightEye = RightPosition;
            }

            UpdateGaze();
        }

        private void UpdateGaze()
        {
            if (_gazePoseAction == null || !BasisLocalCameraDriver.HasInstance)
            {
                MarkUntracked();
                return;
            }

            PoseState gazeTracking = _gazePoseAction.ReadValue<PoseState>();
            if (!gazeTracking.isTracked)
            {
                MarkUntracked();
                return;
            }

            Vector3 hmdTrackingPos = Vector3.zero;
            Quaternion hmdTrackingRot = Quaternion.identity;
            if (hasCenterEyeState)
            {
                centerEyeState.TryGetPosition(out hmdTrackingPos);
                centerEyeState.TryGetRotation(out hmdTrackingRot);
            }
            else if (leftEyeState.TryGetPosition(out Vector3 lp) && rightEyeState.TryGetPosition(out Vector3 rp))
            {
                hmdTrackingPos = (lp + rp) * 0.5f;
                if (!leftEyeState.TryGetRotation(out hmdTrackingRot))
                {
                    hmdTrackingRot = Quaternion.identity;
                }
            }

            Quaternion invHmdRot = Quaternion.Inverse(hmdTrackingRot);
            Vector3 gazeRelHmdPos = invHmdRot * (gazeTracking.position - hmdTrackingPos);
            Quaternion gazeRelHmdRot = invHmdRot * gazeTracking.rotation;

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

        private void RefreshNodeStates()
        {
            InputTracking.GetNodeStates(_nodeStates);
            hasCenterEyeState = false;
            foreach (XRNodeState nodeState in _nodeStates)
            {
                if (nodeState.nodeType == XRNode.LeftEye)
                {
                    leftEyeState = nodeState;
                }
                else if (nodeState.nodeType == XRNode.RightEye)
                {
                    rightEyeState = nodeState;
                }
                else if (nodeState.nodeType == XRNode.CenterEye)
                {
                    centerEyeState = nodeState;
                    hasCenterEyeState = true;
                }
            }
        }
    }
}
