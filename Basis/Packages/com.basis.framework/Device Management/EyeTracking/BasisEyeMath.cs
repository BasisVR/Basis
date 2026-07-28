using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.Device_Management.EyeTracking
{
    /// <summary>
    /// Shared conversions between a combined world-space gaze ray and per-eye
    /// canonical head-local yaw/pitch angles. Canonical frame: +Z forward, +Y up,
    /// +X right (matches BasisLocalEyeDriver.WorldPointToCanonicalYawPitch).
    /// </summary>
    public static class BasisEyeMath
    {
        public static void HmdRelativeGazeToWorld(
            Vector3 hmdTrackingPos, Quaternion hmdTrackingRot,
            Vector3 gazeTrackingPos, Quaternion gazeTrackingRot,
            Vector3 cameraWorldPosition, Quaternion cameraWorldRotation,
            out Vector3 worldGazeOrigin, out Vector3 worldGazeDirection)
        {
            Quaternion invHmdRot = Quaternion.Inverse(hmdTrackingRot);
            Vector3 gazeRelHmdPos = invHmdRot * (gazeTrackingPos - hmdTrackingPos);
            Quaternion gazeRelHmdRot = invHmdRot * gazeTrackingRot;

            worldGazeOrigin = cameraWorldPosition + cameraWorldRotation * gazeRelHmdPos;
            Quaternion worldGazeRot = cameraWorldRotation * gazeRelHmdRot;
            worldGazeDirection = worldGazeRot * Vector3.forward;
        }

        public static float2 WorldPointToCanonicalYawPitch(float3 target, float3 eyeCenter, quaternion invHeadRot)
        {
            float3 dir = math.normalizesafe(target - eyeCenter);
            float3 dirHead = math.mul(invHeadRot, dir);
            return new float2(
                math.atan2(dirHead.x, dirHead.z),
                math.asin(math.clamp(dirHead.y, -1f, 1f)));
        }

        public static void WorldRayToPerEyeAngles(
            float3 gazeOrigin, float3 gazeDir, float convergenceDistance,
            float3 leftEyeCenter, float3 rightEyeCenter, quaternion headRot,
            out float2 leftAngles, out float2 rightAngles)
        {
            float3 target = gazeOrigin + math.normalizesafe(gazeDir) * convergenceDistance;
            quaternion invHeadRot = math.inverse(headRot);
            leftAngles = WorldPointToCanonicalYawPitch(target, leftEyeCenter, invHeadRot);
            rightAngles = WorldPointToCanonicalYawPitch(target, rightEyeCenter, invHeadRot);
        }

        public static float3 CanonicalAnglesToHeadLocalDir(float2 yawPitch)
        {
            float cp = math.cos(yawPitch.y);
            return new float3(
                math.sin(yawPitch.x) * cp,
                math.sin(yawPitch.y),
                math.cos(yawPitch.x) * cp);
        }

        public static void PerEyeAnglesToWorldRay(
            float2 leftAngles, float2 rightAngles,
            float3 headPosition, quaternion headRot,
            out float3 origin, out float3 dir)
        {
            float3 leftDir = math.mul(headRot, CanonicalAnglesToHeadLocalDir(leftAngles));
            float3 rightDir = math.mul(headRot, CanonicalAnglesToHeadLocalDir(rightAngles));
            origin = headPosition;
            dir = math.normalizesafe(leftDir + rightDir);
        }

        /// <summary>
        /// Convert canonical per-eye yaw/pitch into the rig-local rotation offset the
        /// eye-apply job expects. Reproduces the legacy HVR ComputeRigOffset exactly.
        /// </summary>
        public static quaternion CanonicalAnglesToEyeOffset(float2 yawPitch, BasisEyeCalibration cal)
        {
            quaternion yaw = quaternion.AxisAngle(new float3(0, 1, 0), yawPitch.x);
            quaternion pitch = quaternion.AxisAngle(new float3(1, 0, 0), -yawPitch.y);
            quaternion canonical = math.mul(yaw, pitch);
            return math.mul(math.mul(cal.basis, canonical), cal.invBasis);
        }
    }
}
