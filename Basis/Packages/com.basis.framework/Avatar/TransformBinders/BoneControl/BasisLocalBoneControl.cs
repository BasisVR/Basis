using Basis.Scripts.Common;
using System;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Scripts.TransformBinders.BoneControl
{
    [System.Serializable]
    [BurstCompile]
    public class BasisLocalBoneControl
    {
        public static readonly float AngleBeforeSpeedup = 25f;
        public static readonly float trackersmooth = 25;
        public static readonly float QuaternionLerp = 14;
        public static readonly float QuaternionLerpFastMovement = 56;
        public static float PositionLerpAmount = 40;
        public static bool HasEvents { get; internal set; }

        [SerializeField]
        public string name;
        [NonSerialized]
        public BasisLocalBoneControl Target;
        public bool HasLineDraw;
        public int LineDrawIndex;
        public bool HasTarget = false;
        public float3 Offset;
        public float3 ScaledOffset;
        public int GizmoReference = -1;
        public bool HasGizmo = false;
        public int TposeGizmoReference = -1;
        public bool TposeHasGizmo = false;
        public bool HasVirtualOverride;

        public bool UseInverseOffset;
        [SerializeField]
        public Color Color = Color.blue;
        // Events for property changes
        public System.Action<BasisHasTracked> OnHasTrackerDriverChanged;
        // Backing fields for the properties
        [SerializeField]
        private BasisHasTracked hasTrackerDriver = BasisHasTracked.HasNoTracker;
        // Properties with get/set accessors
        public BasisHasTracked HasTracked
        {
            get => hasTrackerDriver;
            set
            {
                if (hasTrackerDriver != value)
                {
                    // BasisDebug.Log("Setting Tracker To has Tracker Position Driver " + value);
                    hasTrackerDriver = value;
                    OnHasTrackerDriverChanged?.Invoke(value);
                }
            }
        }
        // Events for property changes
        public Action OnHasRigChanged;
        // Backing fields for the properties
        [SerializeField]
        private BasisHasRigLayer hasRigLayer = BasisHasRigLayer.HasNoRigLayer;
        // Properties with get/set accessors
        public BasisHasRigLayer HasRigLayer
        {
            get => hasRigLayer;
            set
            {
                if (hasRigLayer != value)
                {
                    hasRigLayer = value;
                    OnHasRigChanged?.Invoke();
                }
            }
        }

        [SerializeField]
        public BasisCalibratedCoords IncomingData = new BasisCalibratedCoords();
        [SerializeField]
        public BasisCalibratedCoords OutGoingData = new BasisCalibratedCoords();
        [SerializeField]
        public BasisCalibratedCoords OutgoingWorldData = new BasisCalibratedCoords();

        [SerializeField]
        public BasisCalibratedCoords LastRunData = new BasisCalibratedCoords();
        [SerializeField]
        public BasisCalibratedCoords InverseOffsetFromBone = new BasisCalibratedCoords();

        [SerializeField]
        public BasisCalibratedCoords TposeLocal = new BasisCalibratedCoords();
        //the scaled tpose is tpose * the avatar height change
        [SerializeField]
        public BasisCalibratedCoords TposeLocalScaled = new BasisCalibratedCoords();

        public void ComputeMovementLocal(Matrix4x4 parentMatrix, Quaternion Rotation, float DeltaTime)
        {
            if (hasTrackerDriver == BasisHasTracked.HasTracker)
            {
                // This needs to be refactored to understand each part of the body and a generic mode.
                // Start off with a distance limiter for the hips.
                // Could also be a step at the end for every targeted type
                if (UseInverseOffset)
                {
                    Vector3 DestinationPosition = IncomingData.position + IncomingData.rotation * InverseOffsetFromBone.position;
                    Quaternion DestinationRotation = IncomingData.rotation * InverseOffsetFromBone.rotation;
                    // Update the position of the secondary transform to maintain the initial offset
                    OutGoingData.position = Vector3.Lerp(LastRunData.position, DestinationPosition, trackersmooth);

                    // Update the rotation of the secondary transform to maintain the initial offset
                    OutGoingData.rotation = Quaternion.Slerp(LastRunData.rotation, DestinationRotation, trackersmooth);
                }
                else
                {
                    // This is going to the generic always accurate fake skeleton
                    OutGoingData.rotation = IncomingData.rotation;
                    OutGoingData.position = IncomingData.position;
                }
                ApplyWorldAndLast(parentMatrix, Rotation);
            }
            else
            {
                if (!HasVirtualOverride && HasTarget)
                {
                    OutGoingData.rotation = ApplyLerpToQuaternion(DeltaTime, LastRunData.rotation, Target.OutGoingData.rotation);
                    // Apply the rotation offset using *
                    Vector3 customDirection = Target.OutGoingData.rotation * ScaledOffset;

                    // Calculate the target outgoing position with the rotated offset
                    Vector3 targetPosition = Target.OutGoingData.position + customDirection;

                    float lerpFactor = ClampInterpolationFactor(PositionLerpAmount, DeltaTime);

                    // Interpolate between the last position and the target position
                    OutGoingData.position = Vector3.Lerp(LastRunData.position, targetPosition, lerpFactor);
                    ApplyWorldAndLast(parentMatrix, Rotation);
                }
            }
        }
        public Quaternion ApplyLerpToQuaternion(float DeltaTime, Quaternion CurrentRotation, Quaternion FutureRotation)
        {
            // Calculate the dot product once to check similarity between rotations
            float dotProduct = math.dot(CurrentRotation, FutureRotation);

            // If quaternions are nearly identical, skip interpolation
            if (dotProduct > 0.999999f)
            {
                return FutureRotation;
            }

            // Calculate angle difference, avoid acos for very small differences
            float angleDifference = math.acos(math.clamp(dotProduct, -1f, 1f));

            // If the angle difference is very small, skip interpolation
            if (angleDifference < math.EPSILON)
            {
                return FutureRotation;
            }

            // Cached LerpAmount values for normal and fast movement
            float lerpAmountNormal = QuaternionLerp;
            // Timing factor for speed-up
            float timing = math.min(angleDifference / AngleBeforeSpeedup, 1f);

            // Interpolate between normal and fast movement rates based on angle
            float lerpAmount = lerpAmountNormal + (QuaternionLerpFastMovement - lerpAmountNormal) * timing;

            // Apply frame-rate-independent lerp factor
            float lerpFactor = ClampInterpolationFactor(lerpAmount, DeltaTime); math.clamp(lerpAmount * DeltaTime, 0f, 1f);

            // Perform spherical interpolation (slerp) with the optimized factor
            return math.slerp(CurrentRotation, FutureRotation, lerpFactor);
        }
        private float ClampInterpolationFactor(float lerpAmount, float DeltaTime)
        {
            // Clamp the interpolation factor to ensure it stays between 0 and 1
            return math.clamp(lerpAmount * DeltaTime, 0f, 1f);
        }
        public void ApplyWorldAndLast(Matrix4x4 parentMatrix, Quaternion Rotation)
        {
            LastRunData.position = OutGoingData.position;
            LastRunData.rotation = OutGoingData.rotation;

            OutgoingWorldData.position = parentMatrix.MultiplyPoint3x4(OutGoingData.position);

            // Transform rotation via quaternion multiplication
            OutgoingWorldData.rotation = Rotation * OutGoingData.rotation;
        }
    }
}
